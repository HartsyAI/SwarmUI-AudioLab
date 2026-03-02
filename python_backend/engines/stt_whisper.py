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
        self.current_model_name = None

    def initialize(self) -> bool:
        try:
            import whisper  # noqa: F401

            logger.info("Whisper ready (model loaded on first request)")
            return True
        except Exception as e:
            logger.error("Whisper init failed: %s", e)
            return False

    def _ensure_loaded(self, model_name: str = "base"):
        if self.model is not None and self.current_model_name == model_name:
            return

        import whisper

        model_dir = self.get_model_dir("stt") or None
        self.model = whisper.load_model(model_name, download_root=model_dir)
        self.current_model_name = model_name
        logger.info("Whisper model loaded: %s", model_name)

    def process(self, **kwargs) -> dict:
        audio_data = kwargs.get("audio_data", "")
        language = kwargs.get("language", "en-US")
        model_name = kwargs.get("model_name", "base")

        if not audio_data:
            return {"success": False, "error": "No audio data provided"}

        audio_bytes = base64.b64decode(audio_data)

        with tempfile.NamedTemporaryFile(suffix=".wav", delete=False) as tmp:
            tmp.write(audio_bytes)
            temp_path = tmp.name

        try:
            self._ensure_loaded(model_name)
            lang_code = language.split("-")[0] if "-" in language else language
            result = self.model.transcribe(temp_path, language=lang_code)
            return {
                "success": True,
                "text": result["text"],
                "confidence": 0.9,
                "metadata": {
                    "engine": "whisper",
                    "model": model_name,
                    "language": lang_code,
                },
            }
        except Exception as e:
            logger.error("Whisper process failed: %s", e)
            return {"success": False, "error": str(e)}
        finally:
            if os.path.exists(temp_path):
                os.unlink(temp_path)

    def cleanup(self):
        self.model = None
        self.current_model_name = None
