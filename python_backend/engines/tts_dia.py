#!/usr/bin/env python3
"""Dia TTS engine — dialogue-capable speech generation from Nari Labs.

Uses the dia package API:
  - from dia.model import Dia
  - Dia.from_pretrained("nari-labs/Dia-1.6B")
  - model.generate(text) -> numpy array at 44100 Hz
  - Supports [S1]/[S2] speaker tags and nonverbal tokens like (laughs), (sighs)
"""

import logging

import numpy as np

from .base_engine import BaseAudioEngine

logger = logging.getLogger("TTS.Dia")


class DiaEngine(BaseAudioEngine):
    """Dia 1.6B dialogue TTS engine from Nari Labs."""

    name = "dia"
    category = "tts"

    def __init__(self):
        self.model = None
        self.sample_rate = 44100
        self.device = None

    def initialize(self) -> bool:
        try:
            from dia.model import Dia  # noqa: F401

            self.device = "cuda" if self.has_cuda() else "cpu"
            logger.info("Dia ready on %s (model loaded on first request)", self.device)
            return True
        except Exception as e:
            logger.error("Dia init failed: %s", e)
            return False

    def _ensure_loaded(self, model_name: str = "nari-labs/Dia-1.6B"):
        if self.model is not None:
            return

        from dia.model import Dia

        self.model = Dia.from_pretrained(model_name, compute_dtype="float16")
        logger.info("Dia model loaded: %s", model_name)

    def process(self, **kwargs) -> dict:
        text = kwargs.get("text", "")
        volume = float(kwargs.get("volume", 0.8))
        model_name = kwargs.get("model_name", "nari-labs/Dia-1.6B")

        if not text.strip():
            return {"success": False, "error": "No text provided"}

        try:
            self._ensure_loaded(model_name)

            # Dia uses [S1]/[S2] tags for multi-speaker dialogue
            # If no tags present, wrap in [S1]
            if "[S1]" not in text and "[S2]" not in text:
                text = f"[S1] {text}"

            # Generate speech — returns numpy array at 44100 Hz
            audio_data = self.model.generate(
                text,
                use_torch_compile=False,
                verbose=False,
            )

            if audio_data is None or len(audio_data) == 0:
                return {"success": False, "error": "No audio output generated"}

            audio_data = np.array(audio_data, dtype=np.float32)
            if audio_data.ndim > 1:
                audio_data = audio_data.squeeze()

            sr = self.sample_rate
            audio_data = audio_data * volume

            audio_b64 = self.audio_to_base64(audio_data, sr)
            duration = len(audio_data) / sr

            return {
                "success": True,
                "audio_data": audio_b64,
                "duration": duration,
                "metadata": {
                    "engine": "dia",
                    "sample_rate": sr,
                    "model": model_name,
                },
            }
        except Exception as e:
            logger.error("Dia process failed: %s", e)
            return {"success": False, "error": str(e)}

    def cleanup(self):
        self.model = None
