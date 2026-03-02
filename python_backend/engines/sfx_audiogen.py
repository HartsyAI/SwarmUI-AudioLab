#!/usr/bin/env python3
"""AudioGen engine — Meta's text-to-sound-effects generation via audiocraft."""

import logging
import numpy as np

from .base_engine import BaseAudioEngine

logger = logging.getLogger("SFX.AudioGen")


class AudioGenEngine(BaseAudioEngine):
    """AudioGen engine using Meta's audiocraft library."""

    name = "audiogen"
    category = "soundfx"

    def __init__(self):
        self.model = None
        self.sample_rate = 16000

    def initialize(self) -> bool:
        try:
            from audiocraft.models import AudioGen  # noqa: F401

            logger.info("AudioGen ready (model loaded on first request)")
            return True
        except Exception as e:
            logger.error("AudioGen init failed: %s", e)
            return False

    def _load_model(self, model_name: str = "facebook/audiogen-medium"):
        """Load the AudioGen model if not already loaded."""
        if self.model is not None:
            return

        from audiocraft.models import AudioGen

        local_path = self.ensure_model_local(model_name, "music")
        self.model = AudioGen.get_pretrained(local_path)
        self.sample_rate = self.model.sample_rate
        logger.info("Loaded AudioGen model: %s (sr=%d)", model_name,
                     self.sample_rate)

    def process(self, **kwargs) -> dict:
        prompt = kwargs.get("prompt", "")
        duration = float(kwargs.get("duration", 10))
        model_name = kwargs.get("model_name", "facebook/audiogen-medium")
        cfg_coef = float(kwargs.get("cfg_coef", 3.0))
        temperature = float(kwargs.get("temperature", 1.0))
        top_k = int(kwargs.get("top_k", 250))
        top_p = float(kwargs.get("top_p", 0.0))

        if not prompt.strip():
            return {"success": False, "error": "No prompt provided"}

        try:
            self._load_model(model_name)
            self.model.set_generation_params(
                use_sampling=True,
                duration=duration,
                cfg_coef=cfg_coef,
                temperature=temperature,
                top_k=top_k,
                top_p=top_p,
            )

            wav = self.model.generate([prompt])

            # wav shape: [batch, channels, samples]
            audio_numpy = wav[0, 0].cpu().numpy().astype(np.float32)

            audio_b64 = self.audio_to_base64(audio_numpy, self.sample_rate)
            actual_duration = len(audio_numpy) / self.sample_rate

            return {
                "success": True,
                "audio_data": audio_b64,
                "duration": actual_duration,
                "metadata": {
                    "engine": "audiogen",
                    "model": model_name,
                    "sample_rate": self.sample_rate,
                    "prompt": prompt,
                },
            }
        except Exception as e:
            logger.error("AudioGen process failed: %s", e)
            return {"success": False, "error": str(e)}

    def cleanup(self):
        self.model = None
