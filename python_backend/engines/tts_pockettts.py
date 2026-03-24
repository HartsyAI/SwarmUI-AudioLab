#!/usr/bin/env python3
"""Pocket TTS engine -- Kyutai's 100M parameter TTS with voice cloning.

Uses the pocket_tts package API:
  - from pocket_tts import TTSModel
  - TTSModel.load_model() -> model
  - model.get_state_for_audio_prompt("alba" | "/path/to/audio.wav") -> voice_state
  - model.generate_audio(voice_state, text) -> 1D torch tensor at model.sample_rate
"""

import base64
import hashlib
import logging
import os
import tempfile

import numpy as np

from .base_engine import BaseAudioEngine

logger = logging.getLogger("TTS.PocketTTS")

BUILTIN_VOICES = [
    "alba", "marius", "javert", "jean",
    "fantine", "cosette", "eponine", "azelma",
]


class PocketTTSEngine(BaseAudioEngine):
    """Pocket TTS — lightweight CPU-capable TTS with optional voice cloning."""

    name = "pockettts"
    category = "tts"

    def __init__(self):
        self.model = None
        self.sample_rate = None
        self._voice_state_cache = {}

    def initialize(self) -> bool:
        try:
            from pocket_tts import TTSModel  # noqa: F401

            logger.info("Pocket TTS ready")
            return True
        except Exception as e:
            logger.error("Pocket TTS init failed: %s", e)
            return False

    def _ensure_loaded(self):
        if self.model is not None:
            return
        from pocket_tts import TTSModel

        self.model = TTSModel.load_model()
        self.sample_rate = self.model.sample_rate
        logger.info("Pocket TTS model loaded (sample_rate=%d)", self.sample_rate)

    def _get_voice_state(self, voice_name: str, reference_audio_b64: str):
        """Get or create a cached voice state.

        If reference_audio_b64 is provided, decode it to a temp file and clone.
        Otherwise use the named built-in voice (defaults to 'alba').
        """
        if reference_audio_b64:
            cache_key = "ref:" + hashlib.md5(
                reference_audio_b64[:2000].encode()
            ).hexdigest()
            if cache_key not in self._voice_state_cache:
                audio_bytes = base64.b64decode(reference_audio_b64)
                fd, tmp_path = tempfile.mkstemp(suffix=".wav")
                os.close(fd)
                try:
                    with open(tmp_path, "wb") as f:
                        f.write(audio_bytes)
                    state = self.model.get_state_for_audio_prompt(tmp_path)
                    self._voice_state_cache[cache_key] = state
                    logger.info("Created voice state from reference audio")
                finally:
                    if os.path.exists(tmp_path):
                        os.unlink(tmp_path)
            return self._voice_state_cache[cache_key]

        voice = voice_name if voice_name in BUILTIN_VOICES else "alba"
        cache_key = "builtin:" + voice
        if cache_key not in self._voice_state_cache:
            state = self.model.get_state_for_audio_prompt(voice)
            self._voice_state_cache[cache_key] = state
            logger.info("Created voice state for built-in voice: %s", voice)
        return self._voice_state_cache[cache_key]

    def process(self, **kwargs) -> dict:
        text = kwargs.get("text", "")
        voice = kwargs.get("voice", "alba")
        volume = float(kwargs.get("volume", 0.8))
        reference_audio = kwargs.get("reference_audio", "")

        if not text.strip():
            return {"success": False, "error": "No text provided"}

        try:
            self._ensure_loaded()

            if self.is_cancelled():
                return self.cancelled_response()

            voice_state = self._get_voice_state(voice, reference_audio)

            if self.is_cancelled():
                return self.cancelled_response()

            audio_tensor = self.model.generate_audio(voice_state, text)
            audio_numpy = audio_tensor.cpu().numpy().astype(np.float32)

            if audio_numpy.ndim > 1:
                audio_numpy = audio_numpy.mean(axis=0)

            if audio_numpy is None or len(audio_numpy) == 0:
                return {"success": False, "error": "No audio output generated"}

            audio_numpy = audio_numpy * volume
            sr = self.sample_rate

            output_format = kwargs.get("output_format", "wav_16")
            output_quality = kwargs.get("output_quality", "high")
            audio_b64, fmt = self.encode_audio(
                audio_numpy, sr,
                output_format=output_format,
                quality=output_quality,
            )
            duration = len(audio_numpy) / sr

            return {
                "success": True,
                "audio_data": audio_b64,
                "output_format": fmt,
                "duration": duration,
                "metadata": {
                    "engine": "pockettts",
                    "sample_rate": sr,
                    "voice": voice if not reference_audio else "cloned",
                },
            }
        except Exception as e:
            logger.error("Pocket TTS process failed: %s", e)
            return {"success": False, "error": str(e)}

    def cleanup(self):
        self.model = None
        self._voice_state_cache.clear()
        self.sample_rate = None
