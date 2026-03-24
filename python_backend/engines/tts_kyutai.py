#!/usr/bin/env python3
"""Kyutai TTS 1.6B engine — streaming text-to-speech using delayed-streams modeling.

Uses the moshi package API:
  - from moshi.models.loaders import CheckpointInfo
  - from moshi.models.tts import TTSModel
  - TTSModel.from_checkpoint_info(checkpoint_info, n_q=32, temp=0.6, device="cuda")
  - tts_model.prepare_script([text]) -> entries
  - tts_model.make_condition_attributes([voice_path], cfg_coef=2.0) -> conditions
  - tts_model.generate([entries], [conditions]) -> result with .frames
  - tts_model.mimi.decode(frame) -> PCM audio at tts_model.mimi.sample_rate
"""

import base64
import logging
import os
import tempfile

import numpy as np

from .base_engine import BaseAudioEngine

logger = logging.getLogger("TTS.KyutaiTTS")

# Default voice from the tts-voices repo
DEFAULT_VOICE = "expresso/ex03-ex01_happy_001_channel1_334s.wav"


class KyutaiTTSEngine(BaseAudioEngine):
    """Kyutai TTS 1.6B — streaming TTS with voice conditioning."""

    name = "kyutaitts"
    category = "tts"

    def __init__(self):
        self.tts_model = None
        self.sample_rate = None
        self._condition_cache = {}

    def initialize(self) -> bool:
        try:
            from moshi.models.tts import TTSModel  # noqa: F401

            logger.info("Kyutai TTS ready")
            return True
        except Exception as e:
            logger.error("Kyutai TTS init failed: %s", e)
            return False

    def _ensure_loaded(self):
        if self.tts_model is not None:
            return

        import torch
        from moshi.models.loaders import CheckpointInfo
        from moshi.models.tts import TTSModel

        device = "cuda" if self.has_cuda() else "cpu"
        if device == "cpu":
            logger.warning("Kyutai TTS on CPU will be very slow (model is 1.8B params)")

        checkpoint_info = CheckpointInfo.from_hf_repo("kyutai/tts-1.6b-en_fr")
        self.tts_model = TTSModel.from_checkpoint_info(
            checkpoint_info,
            n_q=32,
            temp=0.6,
            device=device,
        )
        self.sample_rate = self.tts_model.mimi.sample_rate
        logger.info(
            "Kyutai TTS model loaded on %s (sample_rate=%d)",
            device,
            self.sample_rate,
        )

    def _get_conditions(self, voice_name: str, reference_audio_b64: str):
        """Get voice conditioning attributes, with caching."""
        if reference_audio_b64:
            # Use reference audio for voice cloning
            audio_bytes = base64.b64decode(reference_audio_b64)
            fd, tmp_path = tempfile.mkstemp(suffix=".wav")
            os.close(fd)
            try:
                with open(tmp_path, "wb") as f:
                    f.write(audio_bytes)
                conditions = self.tts_model.make_condition_attributes(
                    [tmp_path], cfg_coef=2.0
                )
                logger.info("Created conditions from reference audio")
                return conditions
            finally:
                if os.path.exists(tmp_path):
                    os.unlink(tmp_path)

        # Built-in voice from tts-voices repo
        voice = voice_name if voice_name else DEFAULT_VOICE
        if voice not in self._condition_cache:
            voice_path = self.tts_model.get_voice_path(voice)
            conditions = self.tts_model.make_condition_attributes(
                [voice_path], cfg_coef=2.0
            )
            self._condition_cache[voice] = conditions
            logger.info("Created conditions for voice: %s", voice)
        return self._condition_cache[voice]

    def process(self, **kwargs) -> dict:
        import torch

        text = kwargs.get("text", "")
        voice = kwargs.get("voice", DEFAULT_VOICE)
        volume = float(kwargs.get("volume", 0.8))
        reference_audio = kwargs.get("reference_audio", "")

        if not text.strip():
            return {"success": False, "error": "No text provided"}

        try:
            self._ensure_loaded()

            if self.is_cancelled():
                return self.cancelled_response()

            conditions = self._get_conditions(voice, reference_audio)
            entries = self.tts_model.prepare_script([text], padding_between=1)

            if self.is_cancelled():
                return self.cancelled_response()

            # Generate audio frames
            result = self.tts_model.generate([entries], [conditions])

            if self.is_cancelled():
                return self.cancelled_response()

            # Decode audio from generated frames
            pcms = []
            with self.tts_model.mimi.streaming(1), torch.no_grad():
                for frame in result.frames[self.tts_model.delay_steps :]:
                    if self.is_cancelled():
                        return self.cancelled_response()
                    pcm = self.tts_model.mimi.decode(frame[:, 1:, :]).cpu().numpy()
                    pcms.append(np.clip(pcm[0, 0], -1, 1))

            if not pcms:
                return {"success": False, "error": "No audio output generated"}

            audio_numpy = np.concatenate(pcms, axis=-1).astype(np.float32)
            audio_numpy = audio_numpy * volume
            sr = self.sample_rate

            output_format = kwargs.get("output_format", "wav_16")
            output_quality = kwargs.get("output_quality", "high")
            audio_b64, fmt = self.encode_audio(
                audio_numpy,
                sr,
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
                    "engine": "kyutaitts",
                    "sample_rate": sr,
                    "voice": voice if not reference_audio else "cloned",
                },
            }
        except Exception as e:
            logger.error("Kyutai TTS process failed: %s", e)
            return {"success": False, "error": str(e)}

    def cleanup(self):
        self.tts_model = None
        self._condition_cache.clear()
        self.sample_rate = None
