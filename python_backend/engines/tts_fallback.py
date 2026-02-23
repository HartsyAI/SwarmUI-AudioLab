#!/usr/bin/env python3
"""Fallback TTS engine — zero-dependency, generates silence."""

import base64
import io
import logging
import struct
import numpy as np

from .base_engine import BaseAudioEngine

logger = logging.getLogger("TTS.Fallback")


class FallbackTTSEngine(BaseAudioEngine):
    """Fallback TTS engine for when no real TTS engine is available."""

    name = "fallback"
    category = "tts"

    def __init__(self):
        self.sample_rate = 22050

    def initialize(self) -> bool:
        logger.warning("Using fallback TTS engine")
        return True

    def process(self, **kwargs) -> dict:
        duration = 1.0
        num_samples = int(self.sample_rate * duration)
        silence = np.zeros(num_samples, dtype=np.int16)

        wav_io = io.BytesIO()
        data_size = len(silence) * 2
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
        wav_io.write(struct.pack("<H", 2))
        wav_io.write(struct.pack("<H", 16))
        wav_io.write(b"data")
        wav_io.write(struct.pack("<I", data_size))
        wav_io.write(silence.tobytes())

        wav_bytes = wav_io.getvalue()
        audio_base64 = base64.b64encode(wav_bytes).decode("utf-8")

        return {
            "success": True,
            "audio_data": audio_base64,
            "duration": duration,
            "metadata": {
                "engine": "fallback",
                "warning": "Install Chatterbox or Bark for real speech synthesis",
                "sample_rate": self.sample_rate,
            },
        }
