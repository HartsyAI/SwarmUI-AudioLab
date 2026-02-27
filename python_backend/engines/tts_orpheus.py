#!/usr/bin/env python3
"""Orpheus TTS engine — expressive, vLLM-backed text-to-speech."""

import logging
import wave
import io
import numpy as np

from .base_engine import BaseAudioEngine

logger = logging.getLogger("TTS.Orpheus")


class OrpheusEngine(BaseAudioEngine):
    """Orpheus TTS engine using orpheus-speech + vLLM."""

    name = "orpheus"
    category = "tts"

    def __init__(self):
        self.model = None
        self.sample_rate = 24000

    def initialize(self) -> bool:
        try:
            from orpheus_tts import OrpheusModel  # noqa: F401

            logger.info("Orpheus ready (model loaded on first request)")
            return True
        except Exception as e:
            logger.error("Orpheus init failed: %s", e)
            return False

    def _ensure_loaded(self, model_name: str = "canopylabs/orpheus-tts-0.1-finetune-prod"):
        """Load the Orpheus model on first use."""
        if self.model is not None:
            return

        from orpheus_tts import OrpheusModel

        self.model = OrpheusModel(model_name=model_name, max_model_len=2048)
        logger.info("Orpheus model loaded: %s", model_name)

    def process(self, **kwargs) -> dict:
        text = kwargs.get("text", "")
        voice = kwargs.get("voice", "tara")
        volume = float(kwargs.get("volume", 0.8))
        model_name = kwargs.get("model_name",
                                "canopylabs/orpheus-tts-0.1-finetune-prod")

        if not text.strip():
            return {"success": False, "error": "No text provided"}

        try:
            self._ensure_loaded(model_name)

            syn_tokens = self.model.generate_speech(
                prompt=text,
                voice=voice,
                temperature=1.0,
                top_p=0.9,
                repetition_penalty=1.1,
            )

            # Collect all audio chunks into a WAV buffer
            wav_buf = io.BytesIO()
            with wave.open(wav_buf, "wb") as wf:
                wf.setnchannels(1)
                wf.setsampwidth(2)
                wf.setframerate(self.sample_rate)
                for chunk in syn_tokens:
                    wf.writeframes(chunk)

            # Read back as float32
            wav_buf.seek(0)
            with wave.open(wav_buf, "rb") as wf:
                frames = wf.readframes(wf.getnframes())

            audio_int16 = np.frombuffer(frames, dtype=np.int16)
            audio_float = audio_int16.astype(np.float32) / 32767.0
            audio_float = audio_float * volume

            audio_b64 = self.audio_to_base64(audio_float, self.sample_rate)
            duration = len(audio_float) / self.sample_rate

            return {
                "success": True,
                "audio_data": audio_b64,
                "duration": duration,
                "metadata": {
                    "engine": "orpheus",
                    "sample_rate": self.sample_rate,
                    "voice": voice,
                },
            }
        except Exception as e:
            logger.error("Orpheus process failed: %s", e)
            return {"success": False, "error": str(e)}

    def cleanup(self):
        self.model = None
