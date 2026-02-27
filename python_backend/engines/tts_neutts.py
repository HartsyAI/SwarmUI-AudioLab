#!/usr/bin/env python3
"""NeuTTS engine — Neuphonic's lightweight TTS."""

import logging
import numpy as np

from .base_engine import BaseAudioEngine

logger = logging.getLogger("TTS.NeuTTS")


class NeuTTSEngine(BaseAudioEngine):
    """NeuTTS Air lightweight text-to-speech engine."""

    name = "neutts"
    category = "tts"

    def __init__(self):
        self.model = None
        self.sample_rate = 24000

    def initialize(self) -> bool:
        try:
            import neutts  # noqa: F401

            logger.info("NeuTTS ready")
            return True
        except Exception as e:
            logger.error("NeuTTS init failed: %s", e)
            return False

    def _ensure_loaded(self, model_name: str = "neuphonic/neutts-air"):
        if self.model is not None:
            return

        import neutts

        self.model = neutts.load_model(model_name)
        self.sample_rate = getattr(self.model, "sample_rate", 24000)
        logger.info("NeuTTS model loaded: %s", model_name)

    def process(self, **kwargs) -> dict:
        text = kwargs.get("text", "")
        volume = float(kwargs.get("volume", 0.8))
        voice = kwargs.get("voice", "default")
        model_name = kwargs.get("model_name", "neuphonic/neutts-air")

        if not text.strip():
            return {"success": False, "error": "No text provided"}

        try:
            self._ensure_loaded(model_name)

            # Generate speech
            if hasattr(self.model, "synthesize"):
                result = self.model.synthesize(text, voice=voice)
            elif hasattr(self.model, "generate"):
                result = self.model.generate(text)
            else:
                return {"success": False, "error": "NeuTTS API not recognized"}

            # Extract audio
            if isinstance(result, dict):
                audio_data = result.get("audio", result.get("waveform"))
                sr = result.get("sample_rate", self.sample_rate)
            else:
                audio_data = result
                sr = self.sample_rate

            if hasattr(audio_data, "cpu"):
                audio_numpy = audio_data.cpu().numpy().astype(np.float32)
            else:
                audio_numpy = np.array(audio_data, dtype=np.float32)

            if len(audio_numpy.shape) > 1:
                audio_numpy = np.mean(audio_numpy, axis=0)
            audio_numpy = audio_numpy * volume
            self.sample_rate = sr

            audio_b64 = self.audio_to_base64(audio_numpy, sr)
            duration = len(audio_numpy) / sr

            return {
                "success": True,
                "audio_data": audio_b64,
                "duration": duration,
                "metadata": {
                    "engine": "neutts",
                    "sample_rate": sr,
                },
            }
        except Exception as e:
            logger.error("NeuTTS process failed: %s", e)
            return {"success": False, "error": str(e)}

    def cleanup(self):
        self.model = None
