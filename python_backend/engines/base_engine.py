#!/usr/bin/env python3
"""Abstract base class for all audio engines."""

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
    def has_cuda() -> bool:
        """Check if CUDA is available."""
        try:
            import torch
            return torch.cuda.is_available()
        except ImportError:
            return False
