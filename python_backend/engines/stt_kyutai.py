#!/usr/bin/env python3
"""Kyutai STT engine — speech-to-text using Kyutai's delayed-streams models.

Uses HuggingFace transformers API (>= 4.53.0):
  - KyutaiSpeechToTextProcessor
  - KyutaiSpeechToTextForConditionalGeneration
  - Models: kyutai/stt-1b-en_fr-trfs (English+French), kyutai/stt-2.6b-en-trfs (English)
  - Input audio must be resampled to 24kHz.
"""

import base64
import logging
import os
import tempfile

import numpy as np
import soundfile as sf

from .base_engine import BaseAudioEngine

logger = logging.getLogger("STT.KyutaiSTT")

TARGET_SR = 24000


def _resample(audio: np.ndarray, orig_sr: int, target_sr: int) -> np.ndarray:
    """Resample audio using linear interpolation."""
    if orig_sr == target_sr:
        return audio
    duration = len(audio) / orig_sr
    num_samples = int(duration * target_sr)
    x_old = np.linspace(0, duration, len(audio), endpoint=False)
    x_new = np.linspace(0, duration, num_samples, endpoint=False)
    return np.interp(x_new, x_old, audio).astype(np.float32)


class KyutaiSTTEngine(BaseAudioEngine):
    """Kyutai speech-to-text engine using HuggingFace transformers."""

    name = "kyutaistt"
    category = "stt"

    def __init__(self):
        self.processor = None
        self.model = None
        self.current_model_id = None

    def initialize(self) -> bool:
        try:
            from transformers import (  # noqa: F401
                KyutaiSpeechToTextProcessor,
                KyutaiSpeechToTextForConditionalGeneration,
            )

            logger.info("Kyutai STT ready")
            return True
        except Exception as e:
            logger.error("Kyutai STT init failed: %s", e)
            return False

    def _ensure_loaded(self, model_id: str):
        if self.model is not None and self.current_model_id == model_id:
            return

        import torch
        from transformers import (
            KyutaiSpeechToTextProcessor,
            KyutaiSpeechToTextForConditionalGeneration,
        )

        device = "cuda" if self.has_cuda() else "cpu"
        model_path = self.ensure_model_local(model_id, "stt")

        self.processor = KyutaiSpeechToTextProcessor.from_pretrained(model_path)
        self.model = KyutaiSpeechToTextForConditionalGeneration.from_pretrained(
            model_path,
            device_map=device,
            torch_dtype=torch.bfloat16 if device == "cuda" else torch.float32,
        )
        self.current_model_id = model_id
        logger.info("Kyutai STT model loaded: %s on %s", model_id, device)

    def process(self, **kwargs) -> dict:
        audio_data = kwargs.get("audio_data", "")
        language = kwargs.get("language", "en")
        model_name = kwargs.get("model_name", "kyutai/stt-2.6b-en-trfs")

        if not audio_data:
            return {"success": False, "error": "No audio data provided"}

        audio_bytes = base64.b64decode(audio_data)

        fd, temp_path = tempfile.mkstemp(suffix=".wav")
        os.close(fd)
        try:
            with open(temp_path, "wb") as f:
                f.write(audio_bytes)

            self._ensure_loaded(model_name)

            if self.is_cancelled():
                return self.cancelled_response()

            # Load and resample audio to 24kHz
            audio, sr = sf.read(temp_path, dtype="float32")
            if audio.ndim > 1:
                audio = audio.mean(axis=1)
            audio = _resample(audio, sr, TARGET_SR)

            if self.is_cancelled():
                return self.cancelled_response()

            # Process with transformers
            import torch

            inputs = self.processor(audio, return_tensors="pt")
            inputs = inputs.to(self.model.device)

            with torch.no_grad():
                output_tokens = self.model.generate(**inputs)

            text = self.processor.batch_decode(
                output_tokens, skip_special_tokens=True
            )[0]

            return {
                "success": True,
                "text": text.strip(),
                "confidence": 0.0,
                "metadata": {
                    "engine": "kyutaistt",
                    "model": model_name,
                    "language": language,
                },
            }
        except Exception as e:
            logger.error("Kyutai STT process failed: %s", e)
            return {"success": False, "error": str(e)}
        finally:
            if os.path.exists(temp_path):
                os.unlink(temp_path)

    def cleanup(self):
        self.processor = None
        self.model = None
        self.current_model_id = None
