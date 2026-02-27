#!/usr/bin/env python3
"""CosyVoice TTS engine — Alibaba's multilingual speech synthesis."""

import logging
import numpy as np

from .base_engine import BaseAudioEngine

logger = logging.getLogger("TTS.CosyVoice")


class CosyVoiceEngine(BaseAudioEngine):
    """CosyVoice 2 TTS engine from FunAudioLLM."""

    name = "cosyvoice"
    category = "tts"

    def __init__(self):
        self.model = None
        self.sample_rate = 22050
        self.device = None

    def initialize(self) -> bool:
        try:
            import torch
            from transformers import AutoModel  # noqa: F401

            self.device = "cuda" if self.has_cuda() else "cpu"
            logger.info("CosyVoice ready on %s", self.device)
            return True
        except Exception as e:
            logger.error("CosyVoice init failed: %s", e)
            return False

    def _ensure_loaded(self, model_name: str = "FunAudioLLM/CosyVoice2-0.5B"):
        if self.model is not None:
            return

        from transformers import AutoModel, AutoTokenizer

        self.model = AutoModel.from_pretrained(
            model_name, trust_remote_code=True
        ).to(self.device)
        logger.info("CosyVoice model loaded: %s", model_name)

    def process(self, **kwargs) -> dict:
        text = kwargs.get("text", "")
        volume = float(kwargs.get("volume", 0.8))
        voice = kwargs.get("voice", "default")
        model_name = kwargs.get("model_name", "FunAudioLLM/CosyVoice2-0.5B")

        if not text.strip():
            return {"success": False, "error": "No text provided"}

        try:
            self._ensure_loaded(model_name)

            # CosyVoice generates via model inference
            if hasattr(self.model, "inference_sft"):
                output = self.model.inference_sft(text, voice)
            elif hasattr(self.model, "generate"):
                output = self.model.generate(text)
            else:
                return {"success": False, "error": "CosyVoice API not recognized"}

            # Extract audio from output
            if isinstance(output, dict):
                audio_data = output.get("audio", output.get("tts_speech"))
                sr = output.get("sample_rate", self.sample_rate)
            else:
                audio_data = output
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
                    "engine": "cosyvoice",
                    "sample_rate": sr,
                    "model": model_name,
                },
            }
        except Exception as e:
            logger.error("CosyVoice process failed: %s", e)
            return {"success": False, "error": str(e)}

    def cleanup(self):
        self.model = None
