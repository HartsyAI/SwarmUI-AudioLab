#!/usr/bin/env python3
"""Moonshine STT engine — tiny, fast, edge-optimized speech-to-text."""

import base64
import logging
import os
import tempfile

from .base_engine import BaseAudioEngine

logger = logging.getLogger("STT.Moonshine")


class MoonshineEngine(BaseAudioEngine):
    """Moonshine STT engine from Useful Sensors."""

    name = "moonshine"
    category = "stt"

    def __init__(self):
        self.model_name = "moonshine/base"

    def initialize(self) -> bool:
        try:
            import moonshine  # noqa: F401

            logger.info("Moonshine initialized (model: %s)", self.model_name)
            return True
        except Exception as e:
            logger.error("Moonshine init failed: %s", e)
            return False

    def process(self, **kwargs) -> dict:
        audio_data = kwargs.get("audio_data", "")
        model_name = kwargs.get("model_name", self.model_name)

        if not audio_data:
            return {"success": False, "error": "No audio data provided"}

        temp_path = None
        try:
            import moonshine

            audio_bytes = base64.b64decode(audio_data)

            with tempfile.NamedTemporaryFile(
                suffix=".wav", delete=False
            ) as tmp:
                tmp.write(audio_bytes)
                temp_path = tmp.name

            result = moonshine.transcribe(temp_path, model_name)

            # Result is a list of transcription strings
            text = result[0] if isinstance(result, list) and result else str(result)

            return {
                "success": True,
                "text": text.strip(),
                "confidence": 0.85,
                "metadata": {
                    "engine": "moonshine",
                    "model": model_name,
                },
            }
        except Exception as e:
            logger.error("Moonshine process failed: %s", e)
            return {"success": False, "error": str(e)}
        finally:
            if temp_path and os.path.exists(temp_path):
                os.unlink(temp_path)

    def cleanup(self):
        pass
