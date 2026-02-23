#!/usr/bin/env python3
"""Fallback STT engine — zero-dependency placeholder."""

import logging

from .base_engine import BaseAudioEngine

logger = logging.getLogger("STT.Fallback")


class FallbackSTTEngine(BaseAudioEngine):
    """Fallback STT engine for when no real STT engine is available."""

    name = "fallback"
    category = "stt"

    def initialize(self) -> bool:
        logger.warning("Using fallback STT engine")
        return True

    def process(self, **kwargs) -> dict:
        return {
            "success": True,
            "text": "[Placeholder transcription - no STT engine available]",
            "confidence": 0.0,
            "metadata": {
                "engine": "fallback",
                "warning": "Install RealtimeSTT or Whisper for real transcription",
            },
        }
