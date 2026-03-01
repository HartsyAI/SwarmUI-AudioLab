#!/usr/bin/env python3
"""Abstract base class for all audio engines."""

import base64
import io
import os
import struct

import numpy as np
from abc import ABC, abstractmethod
from typing import Any, Dict


class BaseAudioEngine(ABC):
    """Base class that all audio engines must extend.

    Subclasses implement ``initialize()`` to load models and ``process()``
    to handle requests.  The ``cleanup()`` method is called when the engine
    is being shut down and can be overridden to release GPU memory, close
    file handles, etc.
    """

    name: str = "base"
    category: str = "unknown"  # "tts", "stt", "musicgen", etc.

    @abstractmethod
    def initialize(self) -> bool:
        """Load models and prepare the engine for processing.

        Returns ``True`` on success, ``False`` on failure.
        """
        ...

    @abstractmethod
    def process(self, **kwargs) -> Dict[str, Any]:
        """Run the engine's primary operation.

        For TTS engines, expected kwargs include ``text``, ``voice``,
        ``language``, ``volume``.  For STT engines, expected kwargs include
        ``audio_data`` (base64), ``language``.

        Returns a dict with at least ``"success": True/False``.
        """
        ...

    def cleanup(self):
        """Release resources held by this engine."""
        pass

    # Helpers shared across engines ----------------------------------------

    @staticmethod
    def get_model_dir(category: str = "") -> str:
        """Get centralized model directory from AUDIOLAB_MODEL_ROOT env var.

        Returns the path ``<root>/<category>/`` if set, creating it if needed.
        Returns empty string if the env var is not set.
        """
        root = os.environ.get("AUDIOLAB_MODEL_ROOT", "")
        if root and category:
            path = os.path.join(root, category)
            os.makedirs(path, exist_ok=True)
            return path
        return ""

    @staticmethod
    def has_cuda() -> bool:
        """Check if CUDA is available."""
        try:
            import torch
            return torch.cuda.is_available()
        except ImportError:
            return False

    @staticmethod
    def numpy_to_wav(audio_numpy: np.ndarray, sample_rate: int,
                     num_channels: int = 1) -> bytes:
        """Convert a float32 numpy array to WAV bytes (16-bit PCM).

        Normalises amplitude to avoid clipping, converts to int16, then
        builds a minimal RIFF/WAV header.
        """
        if len(audio_numpy.shape) > 1:
            audio_numpy = np.mean(audio_numpy, axis=0)
        max_amp = np.max(np.abs(audio_numpy))
        if max_amp > 0.8:
            audio_numpy = audio_numpy * 0.8 / max_amp
        audio_int16 = (audio_numpy * 32767).astype(np.int16)

        bits_per_sample = 16
        byte_rate = sample_rate * num_channels * bits_per_sample // 8
        block_align = num_channels * bits_per_sample // 8
        data_size = len(audio_int16) * bits_per_sample // 8
        file_size = 36 + data_size

        buf = io.BytesIO()
        buf.write(b"RIFF")
        buf.write(struct.pack("<I", file_size))
        buf.write(b"WAVE")
        buf.write(b"fmt ")
        buf.write(struct.pack("<I", 16))
        buf.write(struct.pack("<H", 1))           # PCM
        buf.write(struct.pack("<H", num_channels))
        buf.write(struct.pack("<I", sample_rate))
        buf.write(struct.pack("<I", byte_rate))
        buf.write(struct.pack("<H", block_align))
        buf.write(struct.pack("<H", bits_per_sample))
        buf.write(b"data")
        buf.write(struct.pack("<I", data_size))
        buf.write(audio_int16.tobytes())
        return buf.getvalue()

    @staticmethod
    def audio_to_base64(audio_numpy: np.ndarray, sample_rate: int,
                        num_channels: int = 1) -> str:
        """Convert numpy audio to a base64-encoded WAV string."""
        wav_bytes = BaseAudioEngine.numpy_to_wav(audio_numpy, sample_rate,
                                                  num_channels)
        return base64.b64encode(wav_bytes).decode("utf-8")

    @staticmethod
    def decode_audio_input(audio_data_b64: str) -> bytes:
        """Decode base64 audio data to raw bytes."""
        return base64.b64decode(audio_data_b64)
