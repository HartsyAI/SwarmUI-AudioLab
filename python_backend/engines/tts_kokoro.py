#!/usr/bin/env python3
"""Kokoro TTS engine — lightweight, high-quality, CPU-capable."""

import logging
import os
import numpy as np

from .base_engine import BaseAudioEngine

logger = logging.getLogger("TTS.Kokoro")


class KokoroEngine(BaseAudioEngine):
    """Kokoro TTS engine using the KPipeline API."""

    name = "kokoro"
    category = "tts"

    def __init__(self):
        self.pipeline = None
        self.sample_rate = 24000

    @staticmethod
    def _setup_espeak():
        """Ensure espeak-ng data path is set before Kokoro loads."""
        try:
            import espeakng_loader
            data_path = espeakng_loader.get_data_path()
            if os.path.isdir(data_path):
                os.environ["ESPEAK_DATA_PATH"] = os.path.dirname(data_path)
        except ImportError:
            pass

    def initialize(self) -> bool:
        try:
            self._setup_espeak()
            from kokoro import KPipeline

            device = "cuda" if self.has_cuda() else "cpu"
            self.pipeline = KPipeline(lang_code="a", device=device)
            logger.info("Kokoro initialized on %s", device)
            return True
        except Exception as e:
            logger.error("Kokoro init failed: %s", e)
            return False

    def process(self, **kwargs) -> dict:
        text = kwargs.get("text", "")
        voice = kwargs.get("voice", "af_heart")
        volume = float(kwargs.get("volume", 0.8))
        speed = float(kwargs.get("speed", 1.0))

        if not text.strip():
            return {"success": False, "error": "No text provided"}

        try:
            chunks = []
            for _graphemes, _phonemes, audio in self.pipeline(
                text, voice=voice, speed=speed
            ):
                if audio is not None:
                    chunks.append(audio)

            if not chunks:
                return {"success": False, "error": "No audio generated"}

            audio_numpy = np.concatenate(chunks).astype(np.float32)
            audio_numpy = audio_numpy * volume

            audio_b64 = self.audio_to_base64(audio_numpy, self.sample_rate)
            duration = len(audio_numpy) / self.sample_rate

            return {
                "success": True,
                "audio_data": audio_b64,
                "duration": duration,
                "metadata": {
                    "engine": "kokoro",
                    "sample_rate": self.sample_rate,
                    "voice": voice,
                },
            }
        except Exception as e:
            logger.error("Kokoro process failed: %s", e)
            return {"success": False, "error": str(e)}

    def cleanup(self):
        self.pipeline = None
