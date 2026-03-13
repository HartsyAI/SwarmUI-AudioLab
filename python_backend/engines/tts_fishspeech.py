#!/usr/bin/env python3
"""Fish Speech TTS engine — dual-autoregressive TTS with inline prosody control.

Supports:
  - fishaudio/s2-pro (5B params, 80+ languages, 15,000+ inline control tags)
  - fishaudio/s1-mini (lightweight variant for resource-constrained deployment)

Voice cloning via reference audio + optional transcript.
Inline control tags: [whisper], [emphasis], [laughing], [pause], [excited], etc.
"""

import base64
import logging
import os
import queue
import tempfile
import threading

import numpy as np

from .base_engine import BaseAudioEngine

logger = logging.getLogger("TTS.FishSpeech")


class FishSpeechEngine(BaseAudioEngine):
    """Fish Speech TTS engine using DualARTransformer + DAC codec."""

    name = "fishspeech"
    category = "tts"

    def __init__(self):
        self.engine = None
        self.decoder_model = None
        self.llama_queue = None
        self.sample_rate = 44100
        self.device = None
        self.current_model_name = None

    def initialize(self) -> bool:
        try:
            # fish_speech uses pyrootutils.setup_root() which expects a
            # .project-root marker file.  When installed via pip (--no-deps)
            # the marker is missing, so create it inside the package dir.
            import importlib, pathlib
            spec = importlib.util.find_spec("fish_speech")
            if spec and spec.submodule_search_locations:
                root_marker = pathlib.Path(spec.submodule_search_locations[0]) / ".project-root"
                if not root_marker.exists():
                    root_marker.touch()

            import fish_speech  # noqa: F401
            import torch  # noqa: F401
            import torchaudio  # noqa: F401

            self.device = "cuda" if self.has_cuda() else "cpu"
            logger.info("Fish Speech ready on %s (model loaded on first request)", self.device)
            return True
        except Exception as e:
            logger.error("Fish Speech init failed: %s", e)
            return False

    def _ensure_loaded(self, model_name: str = "fishaudio/s2-pro"):
        if self.engine is not None and self.current_model_name == model_name:
            return

        # Clean up previous model if switching
        if self.engine is not None:
            self.cleanup()

        import torch
        from fish_speech.inference_engine import TTSInferenceEngine
        from fish_speech.models.dac.inference import load_model as load_dac
        from fish_speech.models.text2semantic.inference import (
            launch_thread_safe_queue,
        )

        local_path = self.ensure_model_local(model_name, "tts")
        codec_path = os.path.join(local_path, "codec.pth")

        precision = torch.bfloat16 if self.device == "cuda" else torch.float32

        # Load DAC codec
        logger.info("Loading DAC codec from %s", codec_path)
        self.decoder_model = load_dac(
            config_name="modded_dac_vq",
            checkpoint_path=codec_path,
            device=self.device,
        )
        self.sample_rate = getattr(self.decoder_model, "sample_rate", 44100)

        # Launch LLM inference thread — returns the input queue
        logger.info("Loading DualARTransformer from %s", local_path)
        self.llama_queue = launch_thread_safe_queue(
            checkpoint_path=local_path,
            device=self.device,
            precision=precision,
            compile=False,
        )

        # Create inference engine
        self.engine = TTSInferenceEngine(
            llama_queue=self.llama_queue,
            decoder_model=self.decoder_model,
            precision=precision,
            compile=False,
        )

        self.current_model_name = model_name
        logger.info(
            "Fish Speech loaded: %s (sr=%d, device=%s)",
            model_name,
            self.sample_rate,
            self.device,
        )

    def process(self, **kwargs) -> dict:
        text = kwargs.get("text", "")
        volume = float(kwargs.get("volume", 0.8))
        model_name = kwargs.get("model_name", "fishaudio/s2-pro")
        reference_audio = kwargs.get("reference_audio", "")
        reference_text = kwargs.get("ref_text", "")
        temperature = float(kwargs.get("temperature", 0.8))
        top_p = float(kwargs.get("top_p", 0.8))
        repetition_penalty = float(kwargs.get("repetition_penalty", 1.1))
        max_new_tokens = int(kwargs.get("max_new_tokens", 1024))
        chunk_length = int(kwargs.get("chunk_length", 200))
        normalize = kwargs.get("normalize", True)

        if not text.strip():
            return {"success": False, "error": "No text provided"}

        try:
            from fish_speech.utils.schema import (
                ServeReferenceAudio,
                ServeTTSRequest,
            )

            self._ensure_loaded(model_name)

            # Build references list for voice cloning
            references = []
            ref_tmp_path = None
            if reference_audio:
                audio_bytes = base64.b64decode(reference_audio)
                tmp = tempfile.NamedTemporaryFile(suffix=".wav", delete=False)
                tmp.write(audio_bytes)
                tmp.close()
                ref_tmp_path = tmp.name
                references.append(
                    ServeReferenceAudio(
                        audio=audio_bytes,
                        text=reference_text or "",
                    )
                )

            try:
                # Build TTS request
                request = ServeTTSRequest(
                    text=text,
                    references=references,
                    max_new_tokens=max_new_tokens,
                    chunk_length=chunk_length,
                    top_p=top_p,
                    repetition_penalty=repetition_penalty,
                    temperature=temperature,
                    normalize=normalize if isinstance(normalize, bool) else str(normalize).lower() == "true",
                    format="wav",
                    streaming=False,
                )

                # Run inference and collect all audio segments
                audio_segments = []
                for result in self.engine.inference(request):
                    if result.code == "error":
                        return {
                            "success": False,
                            "error": f"Inference error: {result.error}",
                        }
                    if result.code == "final" and result.audio is not None:
                        sr, audio_data = result.audio
                        self.sample_rate = sr
                        audio_segments.append(audio_data)
                    elif result.code == "segment" and result.audio is not None:
                        sr, audio_data = result.audio
                        self.sample_rate = sr
                        audio_segments.append(audio_data)

                if not audio_segments:
                    return {"success": False, "error": "No audio generated"}

                # Concatenate all segments
                audio_numpy = np.concatenate(audio_segments)
                audio_numpy = audio_numpy.astype(np.float32) * volume

                audio_b64 = self.audio_to_base64(audio_numpy, self.sample_rate)
                duration = len(audio_numpy) / self.sample_rate

                return {
                    "success": True,
                    "audio_data": audio_b64,
                    "duration": duration,
                    "metadata": {
                        "engine": "fishspeech",
                        "sample_rate": self.sample_rate,
                        "model": model_name,
                    },
                }
            finally:
                if ref_tmp_path and os.path.exists(ref_tmp_path):
                    os.unlink(ref_tmp_path)

        except Exception as e:
            logger.error("Fish Speech process failed: %s", e)
            return {"success": False, "error": str(e)}

    def cleanup(self):
        self.engine = None
        self.decoder_model = None
        self.llama_queue = None
        self.current_model_name = None
