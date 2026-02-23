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

    def initialize(self) -> bool:
        try:
            from RealtimeSTT import AudioToTextRecorder

            self.recorder = AudioToTextRecorder(
                model="base",
                language="en",
                use_microphone=False,
                spinner=False,
                level=logging.WARNING,
            )
            logger.info("RealtimeSTT initialized")
            return True
        except Exception as e:
            logger.error("RealtimeSTT init failed: %s", e)
            return False

    def process(self, **kwargs) -> dict:
        audio_data = kwargs.get("audio_data", "")
        audio_bytes = base64.b64decode(audio_data)

        self.recorder.feed_audio(audio_bytes)
        transcription = self.recorder.text()

        return {
            "success": True,
            "text": transcription,
            "confidence": 0.85,
            "metadata": {"engine": "realtimestt"},
        }

    def cleanup(self):
        if self.recorder:
            try:
                self.recorder.shutdown()
            except Exception:
                pass
        self.recorder = None
