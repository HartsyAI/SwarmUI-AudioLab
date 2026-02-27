#!/usr/bin/env python3
"""ACE-Step engine — full-song music generation with lyrics support."""

import logging
import os
import tempfile
import numpy as np

from .base_engine import BaseAudioEngine

logger = logging.getLogger("Music.ACEStep")


class AceStepEngine(BaseAudioEngine):
    """ACE-Step music generation engine."""

    name = "acestep"
    category = "musicgen"

    def __init__(self):
        self.handler = None
        self.sample_rate = 48000

    def initialize(self) -> bool:
        try:
            from acestep.pipeline import ACEStepPipeline  # noqa: F401

            logger.info("ACE-Step ready (model loaded on first request)")
            return True
        except Exception as e:
            logger.error("ACE-Step init failed: %s", e)
            return False

    def _ensure_loaded(self):
        """Load the ACE-Step pipeline if not already loaded."""
        if self.handler is not None:
            return

        from acestep.pipeline import ACEStepPipeline

        device = "cuda" if self.has_cuda() else "cpu"
        self.handler = ACEStepPipeline(device=device)
        logger.info("ACE-Step pipeline loaded on %s", device)

    def process(self, **kwargs) -> dict:
        prompt = kwargs.get("prompt", "")
        lyrics = kwargs.get("lyrics", "[Instrumental]")
        duration = float(kwargs.get("duration", 30))

        if not prompt.strip():
            return {"success": False, "error": "No prompt provided"}

        try:
            self._ensure_loaded()

            result = self.handler.generate(
                prompt=prompt,
                lyrics=lyrics,
                duration=duration,
                seed=-1,
            )

            if result is None:
                return {"success": False, "error": "ACE-Step returned no output"}

            # Result may be a dict with 'audio' tensor or similar
            if isinstance(result, dict):
                audio_tensor = result.get("audio", result.get("tensor"))
                sr = result.get("sample_rate", self.sample_rate)
            else:
                audio_tensor = result
                sr = self.sample_rate

            if audio_tensor is None:
                return {"success": False, "error": "No audio in ACE-Step result"}

            audio_numpy = audio_tensor.cpu().numpy().astype(np.float32)

            # If stereo [channels, samples], convert to mono
            if len(audio_numpy.shape) > 1:
                audio_numpy = np.mean(audio_numpy, axis=0)

            self.sample_rate = sr
            audio_b64 = self.audio_to_base64(audio_numpy, self.sample_rate)
            actual_duration = len(audio_numpy) / self.sample_rate

            return {
                "success": True,
                "audio_data": audio_b64,
                "duration": actual_duration,
                "metadata": {
                    "engine": "acestep",
                    "sample_rate": self.sample_rate,
                    "prompt": prompt,
                    "has_lyrics": lyrics != "[Instrumental]",
                },
            }
        except Exception as e:
            logger.error("ACE-Step process failed: %s", e)
            return {"success": False, "error": str(e)}

    def cleanup(self):
        self.handler = None
