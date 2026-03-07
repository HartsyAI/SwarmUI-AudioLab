#!/usr/bin/env python3
"""RealtimeSTT engine — extracted from stt_engines.py."""

import base64
import logging

from .base_engine import BaseAudioEngine

logger = logging.getLogger("STT.RealtimeSTT")


class RealtimeSTTEngine(BaseAudioEngine):
    """RealtimeSTT streaming speech-to-text engine."""

    name = "realtimestt"
    category = "stt"

    def __init__(self):
        self.recorder = None
        self.current_model = None
        self.current_language = None

    def initialize(self) -> bool:
        try:
            from RealtimeSTT import AudioToTextRecorder  # noqa: F401

            logger.info("RealtimeSTT ready (recorder created on first request)")
            return True
        except Exception as e:
            logger.error("RealtimeSTT init failed: %s", e)
            return False

    def _ensure_recorder(self, model_name: str = "base", language: str = "en"):
        """Create or recreate the recorder if model/language changed."""
        if (self.recorder is not None
                and self.current_model == model_name
                and self.current_language == language):
            return

        from RealtimeSTT import AudioToTextRecorder

        if self.recorder is not None:
            try:
                self.recorder.shutdown()
            except Exception:
                pass

        self.recorder = AudioToTextRecorder(
            model=model_name,
            language=language,
            use_microphone=False,
            spinner=False,
            level=logging.WARNING,
        )
        self.current_model = model_name
        self.current_language = language
        logger.info("RealtimeSTT recorder created: model=%s, language=%s",
                     model_name, language)

    def process(self, **kwargs) -> dict:
        audio_data = kwargs.get("audio_data", "")
        model_name = kwargs.get("model_name", "base")
        language = kwargs.get("language", "en")
        lang_code = language.split("-")[0] if "-" in language else language

        try:
            self._ensure_recorder(model_name, lang_code)
            audio_bytes = base64.b64decode(audio_data)
            self.recorder.feed_audio(audio_bytes)
            transcription = self.recorder.text()

            return {
                "success": True,
                "text": transcription,
                "confidence": 0.85,
                "metadata": {
                    "engine": "realtimestt",
                    "model": model_name,
                    "language": lang_code,
                },
            }
        except Exception as e:
            logger.error("RealtimeSTT process failed: %s", e)
            return {"success": False, "error": str(e)}

    def cleanup(self):
        if self.recorder:
            try:
                self.recorder.shutdown()
            except Exception:
                pass
        self.recorder = None
