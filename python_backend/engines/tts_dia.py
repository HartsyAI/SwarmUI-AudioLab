#!/usr/bin/env python3
"""Dia TTS engine — dialogue-capable speech generation."""

import logging
import numpy as np

from .base_engine import BaseAudioEngine

logger = logging.getLogger("TTS.Dia")


class DiaEngine(BaseAudioEngine):
    """Dia 1.6B dialogue TTS engine using HuggingFace transformers."""

    name = "dia"
    category = "tts"

    def __init__(self):
        self.model = None
        self.processor = None
        self.sample_rate = 44100
        self.device = None

    def initialize(self) -> bool:
        try:
            from transformers import AutoProcessor  # noqa: F401
            from transformers import DiaForConditionalGeneration  # noqa: F401

            self.device = "cuda" if self.has_cuda() else "cpu"
            logger.info("Dia ready on %s (model loaded on first request)",
                        self.device)
            return True
        except Exception as e:
            logger.error("Dia init failed: %s", e)
            return False

    def _ensure_loaded(self, model_name: str = "nari-labs/Dia-1.6B-0626"):
        if self.model is not None:
            return

        import torch
        from transformers import AutoProcessor, DiaForConditionalGeneration

        self.processor = AutoProcessor.from_pretrained(model_name)
        self.model = DiaForConditionalGeneration.from_pretrained(
            model_name
        ).to(self.device)
        logger.info("Dia model loaded: %s", model_name)

    def process(self, **kwargs) -> dict:
        text = kwargs.get("text", "")
        volume = float(kwargs.get("volume", 0.8))
        model_name = kwargs.get("model_name", "nari-labs/Dia-1.6B-0626")

        if not text.strip():
            return {"success": False, "error": "No text provided"}

        try:
            self._ensure_loaded(model_name)

            # Dia uses [S1]/[S2] tags for multi-speaker dialogue
            # If no tags present, wrap in [S1]
            if "[S1]" not in text and "[S2]" not in text:
                text = f"[S1] {text}"

            inputs = self.processor(
                text=[text], padding=True, return_tensors="pt"
            ).to(self.device)

            outputs = self.model.generate(
                **inputs,
                max_new_tokens=3072,
                guidance_scale=3.0,
                temperature=1.8,
                top_p=0.90,
                top_k=45,
            )

            decoded = self.processor.batch_decode(outputs)
            # decoded contains audio data — save and read back
            import tempfile
            import os
            import soundfile as sf

            tmp_path = tempfile.mktemp(suffix=".wav")
            try:
                self.processor.save_audio(decoded, tmp_path)
                audio_data, sr = sf.read(tmp_path, dtype="float32")
                self.sample_rate = sr
            finally:
                if os.path.exists(tmp_path):
                    os.unlink(tmp_path)

            audio_data = audio_data * volume

            audio_b64 = self.audio_to_base64(audio_data, sr)
            duration = len(audio_data) / sr

            return {
                "success": True,
                "audio_data": audio_b64,
                "duration": duration,
                "metadata": {
                    "engine": "dia",
                    "sample_rate": sr,
                    "model": model_name,
                },
            }
        except Exception as e:
            logger.error("Dia process failed: %s", e)
            return {"success": False, "error": str(e)}

    def cleanup(self):
        self.model = None
        self.processor = None
