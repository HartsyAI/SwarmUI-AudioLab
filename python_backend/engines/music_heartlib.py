#!/usr/bin/env python3
"""HeartLib engine — full-song music generation with vocals from style tags and lyrics.

HeartMuLa pipeline:
  HeartMuLa (4B): style tags + lyrics -> audio tokens (hierarchical transformer)
  HeartCodec (2B): audio tokens -> 48kHz waveform (flow-matching decoder)

Requires: heartlib (pip install from git), torch, torchaudio, transformers
Repository: https://github.com/HeartMuLa/heartlib
License: Apache 2.0
"""

import base64
import logging
import os
import tempfile
from typing import Any, Dict

import numpy as np

from .base_engine import BaseAudioEngine

logger = logging.getLogger("Music.HeartLib")


class HeartLibEngine(BaseAudioEngine):
    """Generates full songs (vocals + instruments) from style tags and lyrics."""

    name = "heartlib"
    category = "audiogeneration"

    def __init__(self):
        super().__init__()
        self._pipeline = None
        self._loaded_model = None

    def initialize(self) -> bool:
        """Verify heartlib is importable."""
        try:
            import heartlib  # noqa: F401
            logger.info("heartlib package found")
            return True
        except ImportError:
            logger.error("heartlib package not installed")
            return False

    def _get_dtype(self):
        """Auto-detect best dtype: bfloat16 on Ampere+, else float16."""
        import torch
        if torch.cuda.is_available():
            cap = torch.cuda.get_device_capability()
            if cap[0] >= 8:
                logger.info("GPU capability %d.%d — using bfloat16", cap[0], cap[1])
                return torch.bfloat16
        logger.info("Using float16 (GPU capability < 8.0 or no CUDA)")
        return torch.float16

    def _get_device(self):
        import torch
        return torch.device("cuda" if torch.cuda.is_available() else "cpu")

    def _ensure_model_dir(self, model_name: str) -> str:
        """Download and arrange the 3 HuggingFace repos into the expected layout.

        Layout:
          {root}/
          ├── HeartCodec-oss/          ← HeartMuLa/HeartCodec-oss-20260123
          ├── HeartMuLa-oss-3B/        ← selected model variant
          ├── gen_config.json           ← HeartMuLa/HeartMuLaGen
          └── tokenizer.json            ← HeartMuLa/HeartMuLaGen
        """
        from huggingface_hub import snapshot_download

        model_dir = self.get_model_dir("music")
        if not model_dir:
            raise RuntimeError("AUDIOLAB_MODEL_ROOT not set")

        root = os.path.join(model_dir, "HeartLib")
        os.makedirs(root, exist_ok=True)

        hf_token = os.environ.get("HF_TOKEN", None)

        # 1. Download config files (gen_config.json, tokenizer.json) from HeartMuLaGen
        config_marker = os.path.join(root, "gen_config.json")
        if not os.path.isfile(config_marker):
            logger.info("Downloading HeartMuLaGen config files...")
            snapshot_download(
                repo_id="HeartMuLa/HeartMuLaGen",
                local_dir=root,
                token=hf_token,
            )
            logger.info("Config files downloaded.")

        # 2. Download HeartCodec
        codec_dir = os.path.join(root, "HeartCodec-oss")
        if not os.path.isdir(codec_dir) or not any(
            f.endswith((".bin", ".safetensors", ".pt"))
            for f in os.listdir(codec_dir) if os.path.isfile(os.path.join(codec_dir, f))
        ):
            logger.info("Downloading HeartCodec-oss-20260123...")
            snapshot_download(
                repo_id="HeartMuLa/HeartCodec-oss-20260123",
                local_dir=codec_dir,
                token=hf_token,
            )
            logger.info("HeartCodec downloaded.")

        # 3. Download selected model variant into HeartMuLa-oss-3B/
        model_subdir = os.path.join(root, "HeartMuLa-oss-3B")
        if not os.path.isdir(model_subdir) or not any(
            f.endswith((".bin", ".safetensors", ".pt"))
            for f in os.listdir(model_subdir) if os.path.isfile(os.path.join(model_subdir, f))
        ):
            logger.info("Downloading model %s...", model_name)
            snapshot_download(
                repo_id=model_name,
                local_dir=model_subdir,
                token=hf_token,
            )
            logger.info("Model downloaded: %s", model_name)

        return root

    def _init_rope_caches(self):
        """Initialize RoPE position embedding caches on the mula model.

        torchtune's RotaryPositionalEmbeddings requires rope_init() to build
        the cos/sin cache before inference. The cache is built on CPU and must
        be moved to the model's device. This must be called after every model
        load/reload (including lazy_load reloads).
        """
        device = self._get_device()
        for module in self._pipeline.mula.modules():
            if hasattr(module, 'rope_init'):
                module.rope_init()
                if hasattr(module, 'cache') and module.cache is not None:
                    module.cache = module.cache.to(device)

    @staticmethod
    def _patch_codec_from_pretrained():
        """Patch HeartCodec.from_pretrained to pass ignore_mismatched_sizes=True.

        The HeartCodec checkpoint has a shape mismatch in
        flow_matching.vq_embed.layers.*._codebook.initted (Size([1]) vs Size([]))
        — functionally identical (scalar vs 1-element tensor) but newer
        transformers versions reject it. The pipeline's lazy codec property
        calls from_pretrained without the flag, and since _unload() deletes
        the codec between _forward and postprocess, we can't pre-load it.
        Patching the class method ensures all loads work correctly.
        """
        from heartlib.heartcodec.modeling_heartcodec import HeartCodec

        if getattr(HeartCodec, '_mismatched_sizes_patched', False):
            return

        _orig = HeartCodec.from_pretrained

        def _patched(*args, **kwargs):
            kwargs.setdefault('ignore_mismatched_sizes', True)
            return _orig(*args, **kwargs)

        HeartCodec.from_pretrained = _patched
        HeartCodec._mismatched_sizes_patched = True
        logger.info("Patched HeartCodec.from_pretrained for _codebook.initted shape mismatch")

    def _ensure_loaded(self, model_name: str):
        """Lazy-load the HeartMuLaGenPipeline."""
        if self._pipeline is not None and self._loaded_model == model_name:
            return

        import torch
        from heartlib import HeartMuLaGenPipeline

        # Unload previous if switching models
        if self._pipeline is not None:
            logger.info("Unloading previous model...")
            del self._pipeline
            self._pipeline = None
            torch.cuda.empty_cache()

        model_root = self._ensure_model_dir(model_name)
        mula_dtype = self._get_dtype()
        device = self._get_device()

        logger.info("Loading HeartMuLaGenPipeline from %s (dtype=%s, lazy_load=True)",
                     model_root, mula_dtype)
        self._pipeline = HeartMuLaGenPipeline.from_pretrained(
            model_root,
            device={"mula": device, "codec": device},
            dtype={"mula": mula_dtype, "codec": torch.float32},
            version="3B",
            lazy_load=True,
        )

        # Patch HeartCodec.from_pretrained so the pipeline's lazy codec
        # loader passes ignore_mismatched_sizes=True (needed after _unload
        # deletes the codec between _forward and postprocess)
        self._patch_codec_from_pretrained()

        # Initialize RoPE caches for initial load
        self._init_rope_caches()

        self._loaded_model = model_name
        logger.info("HeartMuLaGenPipeline loaded successfully")

    def process(self, **kwargs) -> Dict[str, Any]:
        """Generate a song from style tags and lyrics."""
        import torch

        prompt = kwargs.get("prompt", "")
        lyrics = kwargs.get("lyrics", "")
        model_name = kwargs.get("model_name", "HeartMuLa/HeartMuLa-oss-3B-happy-new-year")
        cfg_scale = float(kwargs.get("cfg_scale", 1.5))
        temperature = float(kwargs.get("temperature", 1.0))
        topk = int(kwargs.get("topk", 50))
        seed = int(kwargs.get("seed", -1))
        duration = float(kwargs.get("duration", 30.0))

        if not prompt and not lyrics:
            return {"success": False, "error": "Please provide style tags (Prompt) and/or lyrics."}

        # Set seed for reproducibility
        if seed >= 0:
            torch.manual_seed(seed)
            if torch.cuda.is_available():
                torch.cuda.manual_seed_all(seed)
            logger.info("Set seed: %d", seed)

        # Load model
        self._ensure_loaded(model_name)

        # Re-init RoPE caches before every generation.
        # With lazy_load=True the pipeline may have unloaded and reloaded the
        # mula model since _ensure_loaded last ran — accessing .mula here
        # triggers the reload if needed, then we rebuild the RoPE caches.
        self._init_rope_caches()

        max_audio_length_ms = int(duration * 1000)

        # Generate to temp file
        tmp_fd, tmp_path = tempfile.mkstemp(suffix=".wav")
        os.close(tmp_fd)

        try:
            logger.info("Generating: tags='%s...', lyrics=%d chars, duration=%ds, cfg=%.1f, temp=%.1f, topk=%d",
                         prompt[:50], len(lyrics), int(duration), cfg_scale, temperature, topk)

            with torch.no_grad():
                self._pipeline(
                    {"lyrics": lyrics, "tags": prompt},
                    max_audio_length_ms=max_audio_length_ms,
                    save_path=tmp_path,
                    topk=topk,
                    temperature=temperature,
                    cfg_scale=cfg_scale,
                )

            # Check for cancellation after generation
            if self.is_cancelled():
                return self.cancelled_response()

            # Read output file and convert to 16-bit PCM WAV (torchaudio saves 32-bit float)
            if not os.path.isfile(tmp_path) or os.path.getsize(tmp_path) == 0:
                return {"success": False, "error": "Generation produced no output audio."}

            import torchaudio
            waveform, sr = torchaudio.load(tmp_path)  # (channels, samples) float32
            audio_numpy = waveform.numpy()
            num_channels = audio_numpy.shape[0]
            # Interleave channels for WAV: (channels, samples) -> (samples,) interleaved
            if num_channels > 1:
                audio_numpy = audio_numpy.T.flatten()  # stereo interleave
            else:
                audio_numpy = audio_numpy.squeeze()
            output_format = kwargs.get("output_format", "wav_16")
            quality = kwargs.get("output_quality", "high")
            audio_b64, fmt = self.encode_audio(
                audio_numpy, sr, num_channels,
                output_format=output_format, quality=quality)
            estimated_duration = waveform.shape[1] / sr

            logger.info("Generation complete: ~%.1fs of audio (%s)", estimated_duration, fmt)

            return {
                "success": True,
                "audio_data": audio_b64,
                "output_format": fmt,
                "sample_rate": sr,
                "duration": round(estimated_duration, 2),
                "metadata": {
                    "engine": "heartlib",
                    "model": model_name,
                    "cfg_scale": cfg_scale,
                    "temperature": temperature,
                    "topk": topk,
                    "seed": seed,
                },
            }

        except Exception as e:
            logger.error("HeartLib generation failed: %s", e, exc_info=True)
            return {"success": False, "error": str(e)}

        finally:
            if os.path.isfile(tmp_path):
                try:
                    os.unlink(tmp_path)
                except OSError:
                    pass

    def cleanup(self):
        """Release GPU memory."""
        if self._pipeline is not None:
            del self._pipeline
            self._pipeline = None
            self._loaded_model = None
        try:
            import torch
            if torch.cuda.is_available():
                torch.cuda.empty_cache()
        except ImportError:
            pass
        logger.info("HeartLib engine cleaned up")
