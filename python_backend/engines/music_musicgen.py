#!/usr/bin/env python3
"""MusicGen engine — Meta's text-to-music generation via audiocraft."""

import base64
import logging
import tempfile
import os
import numpy as np

from .base_engine import BaseAudioEngine

logger = logging.getLogger("Music.MusicGen")


class MusicGenEngine(BaseAudioEngine):
    """MusicGen engine using Meta's audiocraft library."""

    name = "musicgen"
    category = "audiogeneration"

    def __init__(self):
        self.model = None
        self.sample_rate = 32000
        self.current_model_name = None

    def initialize(self) -> bool:
        try:
            from audiocraft.models import MusicGen  # noqa: F401

            logger.info("MusicGen ready (model loaded on first request)")
            return True
        except Exception as e:
            logger.error("MusicGen init failed: %s", e)
            return False

    def _load_model(self, model_name: str):
        """Load or switch to a specific MusicGen model."""
        if self.model is not None and self.current_model_name == model_name:
            return

        from audiocraft.models import MusicGen

        if self.model is not None:
            del self.model
            import torch
            if torch.cuda.is_available():
                torch.cuda.empty_cache()

        local_path = self.ensure_model_local(model_name, "music")
        self.model = MusicGen.get_pretrained(local_path)
        self.sample_rate = self.model.sample_rate
        self.current_model_name = model_name
        logger.info("Loaded MusicGen model: %s (sr=%d)", model_name,
                     self.sample_rate)

    def process(self, **kwargs) -> dict:
        prompt = kwargs.get("prompt", "")
        duration = float(kwargs.get("duration", 30))
        model_name = kwargs.get("model_name", "facebook/musicgen-small")
        reference_audio = kwargs.get("reference_audio", "")
        cfg_coef = float(kwargs.get("cfg_coef", 3.0))
        temperature = float(kwargs.get("temperature", 1.0))
        top_k = int(kwargs.get("top_k", 250))
        top_p = float(kwargs.get("top_p", 0.0))

        if not prompt.strip():
            return {"success": False, "error": "No prompt provided"}

        try:
            self._load_model(model_name)
            self.model.set_generation_params(
                use_sampling=True,
                duration=duration,
                cfg_coef=cfg_coef,
                temperature=temperature,
                top_k=top_k,
                top_p=top_p,
            )

            # Melody conditioning if reference audio and melody model
            if reference_audio and "melody" in model_name:
                wav = self._generate_with_melody(prompt, reference_audio)
            else:
                wav = self.model.generate([prompt])

            # wav shape: [batch, channels, samples]
            output_format = kwargs.get("output_format", "wav_16")
            output_quality = kwargs.get("output_quality", "high")
            num_channels = wav.shape[1]
            if num_channels >= 2:
                # Stereo: interleave L/R channels for WAV format
                left = wav[0, 0].cpu().numpy().astype(np.float32)
                right = wav[0, 1].cpu().numpy().astype(np.float32)
                audio_numpy = np.stack([left, right], axis=0)  # [2, samples]
                audio_b64, fmt = self.encode_audio(
                    audio_numpy.T.flatten(), self.sample_rate, num_channels=2,
                    output_format=output_format, quality=output_quality,
                )
                actual_duration = len(left) / self.sample_rate
            else:
                audio_numpy = wav[0, 0].cpu().numpy().astype(np.float32)
                audio_b64, fmt = self.encode_audio(audio_numpy, self.sample_rate, output_format=output_format, quality=output_quality)
                actual_duration = len(audio_numpy) / self.sample_rate

            return {
                "success": True,
                "audio_data": audio_b64,
                "output_format": fmt,
                "duration": actual_duration,
                "metadata": {
                    "engine": "musicgen",
                    "model": model_name,
                    "sample_rate": self.sample_rate,
                    "prompt": prompt,
                },
            }
        except Exception as e:
            logger.error("MusicGen process failed: %s", e)
            return {"success": False, "error": str(e)}

    def _generate_with_melody(self, prompt: str, reference_audio_b64: str):
        """Generate music conditioned on a reference melody."""
        import torch
        import torchaudio

        audio_bytes = base64.b64decode(reference_audio_b64)
        temp_path = None
        try:
            with tempfile.NamedTemporaryFile(
                suffix=".wav", delete=False
            ) as tmp:
                tmp.write(audio_bytes)
                temp_path = tmp.name

            melody, sr = torchaudio.load(temp_path)
            # Expand melody to match batch size of 1
            wav = self.model.generate_with_chroma(
                [prompt], melody[None], sr
            )
            return wav
        finally:
            if temp_path and os.path.exists(temp_path):
                os.unlink(temp_path)

    def cleanup(self):
        self.model = None
        self.current_model_name = None
