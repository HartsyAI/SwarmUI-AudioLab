#!/usr/bin/env python3
"""Bark TTS engine — extracted from tts_engines.py."""

import base64
import io
import logging
import struct
import numpy as np

from .base_engine import BaseAudioEngine

logger = logging.getLogger("TTS.Bark")


class BarkEngine(BaseAudioEngine):
    """Bark TTS engine implementation."""

    name = "bark"
    category = "tts"

    def __init__(self):
        self.model = None
        self.sample_rate = 24000

    @staticmethod
    def _patch_torch_safe_globals():
        """Allow numpy scalar unpickling needed by Bark checkpoints."""
        try:
            import torch
            import numpy.core.multiarray
            torch.serialization.add_safe_globals([numpy.core.multiarray.scalar])
        except (AttributeError, ImportError):
            pass

    def initialize(self) -> bool:
        try:
            self._patch_torch_safe_globals()
            from bark import SAMPLE_RATE  # noqa: F401

            self.sample_rate = SAMPLE_RATE
            logger.info("Bark ready (models loaded on first request)")
            return True
        except Exception as e:
            logger.error("Bark init failed: %s", e)
            return False

    def _ensure_loaded(self):
        if self.model is not None:
            return
        self._patch_torch_safe_globals()
        from bark import preload_models
        preload_models()
        self.model = True
        logger.info("Bark models loaded")

    def process(self, **kwargs) -> dict:
        text = kwargs.get("text", "")
        volume = float(kwargs.get("volume", 0.8))

        self._ensure_loaded()
        from bark import generate_audio

        audio_array = generate_audio(text)
        audio_array = audio_array * volume

        wav_bytes = self._numpy_to_wav(audio_array)
        audio_base64 = base64.b64encode(wav_bytes).decode("utf-8")
        duration = len(audio_array) / self.sample_rate

        return {
            "success": True,
            "audio_data": audio_base64,
            "duration": duration,
            "metadata": {"engine": "bark", "sample_rate": self.sample_rate},
        }

    def cleanup(self):
        self.model = None

    def _numpy_to_wav(self, audio_numpy: np.ndarray) -> bytes:
        audio_int16 = (audio_numpy * 32767).astype(np.int16)

        wav_io = io.BytesIO()
        data_size = len(audio_int16) * 2
        file_size = 36 + data_size

        wav_io.write(b"RIFF")
        wav_io.write(struct.pack("<I", file_size))
        wav_io.write(b"WAVE")
        wav_io.write(b"fmt ")
        wav_io.write(struct.pack("<I", 16))
        wav_io.write(struct.pack("<H", 1))  # PCM
        wav_io.write(struct.pack("<H", 1))  # Mono
        wav_io.write(struct.pack("<I", self.sample_rate))
        wav_io.write(struct.pack("<I", self.sample_rate * 2))
        wav_io.write(struct.pack("<H", 2))  # Block align
        wav_io.write(struct.pack("<H", 16))  # Bits per sample
        wav_io.write(b"data")
        wav_io.write(struct.pack("<I", data_size))
        wav_io.write(audio_int16.tobytes())

        return wav_io.getvalue()
