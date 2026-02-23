#!/usr/bin/env python3
"""Whisper STT engine — extracted from stt_engines.py."""

import base64
import logging
import os
import tempfile

from .base_engine import BaseAudioEngine

logger = logging.getLogger("STT.Whisper")


class WhisperEngine(BaseAudioEngine):
    """OpenAI Whisper speech-to-text engine."""

    name = "whisper"
    category = "stt"

    def __init__(self):
        self.model = None

    def initialize(self) -> bool:
        try:
            import whisper

            self.model = whisper.load_model("base")
            logger.info("Whisper initialized")
            return True
        except Exception as e:
            logger.error("Whisper init failed: %s", e)
            return False

    def process(self, **kwargs) -> dict:
        audio_data = kwargs.get("audio_data", "")
        language = kwargs.get("language", "en-US")

        audio_bytes = base64.b64decode(audio_data)

        with tempfile.NamedTemporaryFile(suffix=".wav", delete=False) as tmp:
            tmp.write(audio_bytes)
            temp_path = tmp.name

        try:
            result = self.model.transcribe(temp_path, language=language.split("-")[0])
            return {
                "success": True,
                "text": result["text"],
                "confidence": 0.9,
                "metadata": {"engine": "whisper"},
            }
        finally:
            os.unlink(temp_path)

    def cleanup(self):
        self.model = None
