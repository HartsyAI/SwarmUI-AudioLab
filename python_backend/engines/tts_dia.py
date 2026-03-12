#!/usr/bin/env python3
"""Dia TTS engine — dialogue-capable speech generation from Nari Labs.

Uses the dia package API:
  - from dia.model import Dia
  - Dia.from_local(config, checkpoint, device=...) or Dia.from_pretrained(...)
  - model.generate(text) -> numpy array at 44100 Hz
  - Supports [S1]/[S2] speaker tags and nonverbal tokens like (laughs), (sighs)
"""

import logging
import os

import numpy as np
import torch

from .base_engine import BaseAudioEngine

logger = logging.getLogger("TTS.Dia")


class DiaEngine(BaseAudioEngine):
    """Dia 1.6B dialogue TTS engine from Nari Labs."""

    name = "dia"
    category = "tts"

    def __init__(self):
        self.model = None
        self.sample_rate = 44100
        self.device = None

    def initialize(self) -> bool:
        try:
            from dia.model import Dia  # noqa: F401

            self.device = "cuda" if self.has_cuda() else "cpu"
            logger.info("Dia ready on %s (model loaded on first request)", self.device)
            return True
        except Exception as e:
            logger.error("Dia init failed: %s", e)
            return False

    def _ensure_loaded(self, model_name: str = "nari-labs/Dia-1.6B-0626"):
        if self.model is not None:
            return

        from dia.model import Dia

        local_path = self.ensure_model_local(model_name, "tts")
        config_path = os.path.join(local_path, "config.json")
        checkpoint_path = os.path.join(local_path, "dia-v1.pth")
        target_device = torch.device(self.device)
        # Use float16 on CUDA for ~4.4GB VRAM, float32 on CPU
        compute_dtype = "float16" if self.device == "cuda" else "float32"
        self.model = Dia.from_local(
            config_path,
            checkpoint_path,
            compute_dtype=compute_dtype,
            device=target_device,
        )
        logger.info("Dia model loaded on %s (dtype=%s): %s", self.device, compute_dtype, model_name)

    def process(self, **kwargs) -> dict:
        text = kwargs.get("text", "")
        volume = float(kwargs.get("volume", 0.8))
        model_name = kwargs.get("model_name", "nari-labs/Dia-1.6B-0626")
        temperature = float(kwargs.get("temperature", 1.2))
        top_p = float(kwargs.get("top_p", 0.95))
        cfg_scale = float(kwargs.get("cfg_scale", 3.0))
        cfg_filter_top_k = int(kwargs.get("cfg_filter_top_k", 45))
        max_tokens = int(kwargs.get("max_tokens", 3072))

        if not text.strip():
            return {"success": False, "error": "No text provided"}

        try:
            self._ensure_loaded(model_name)

            # Dia uses [S1]/[S2] tags for multi-speaker dialogue
            # If no tags present, wrap in [S1]
            if "[S1]" not in text and "[S2]" not in text:
                text = f"[S1] {text}"

            # Generate speech — returns numpy array at 44100 Hz
            audio_data = self.model.generate(
                text,
                max_tokens=max_tokens,
                temperature=temperature,
                top_p=top_p,
                cfg_scale=cfg_scale,
                cfg_filter_top_k=cfg_filter_top_k,
                use_torch_compile=False,
                verbose=False,
            )

            if audio_data is None or len(audio_data) == 0:
                return {"success": False, "error": "No audio output generated"}

            audio_data = np.array(audio_data, dtype=np.float32)
            if audio_data.ndim > 1:
                audio_data = audio_data.squeeze()

            sr = self.sample_rate
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
                    "device": self.device,
                },
            }
        except Exception as e:
            logger.error("Dia process failed: %s", e)
            return {"success": False, "error": str(e)}

    def cleanup(self):
        if self.model is not None:
            del self.model
            self.model = None
            if torch.cuda.is_available():
                torch.cuda.empty_cache()
