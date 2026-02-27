#!/usr/bin/env python3
"""Zonos TTS engine — Zyphra's expressive voice synthesis with cloning."""

import base64
import logging
import os
import tempfile
import numpy as np

from .base_engine import BaseAudioEngine

logger = logging.getLogger("TTS.Zonos")


class ZonosEngine(BaseAudioEngine):
    """Zonos TTS engine from Zyphra."""

    name = "zonos"
    category = "tts"

    def __init__(self):
        self.model = None
        self.sample_rate = 44100
        self.device = None

    def initialize(self) -> bool:
        try:
            from zonos.model import Zonos  # noqa: F401

            self.device = "cuda" if self.has_cuda() else "cpu"
            logger.info("Zonos ready on %s", self.device)
            return True
        except Exception as e:
            logger.error("Zonos init failed: %s", e)
            return False

    def _ensure_loaded(self, model_name: str = "Zyphra/Zonos-v0.1-transformer"):
        if self.model is not None:
            return

        from zonos.model import Zonos

        self.model = Zonos.from_pretrained(model_name, device=self.device)
        self.sample_rate = self.model.autoencoder.sampling_rate
        logger.info("Zonos model loaded: %s (sr=%d)", model_name,
                     self.sample_rate)

    def process(self, **kwargs) -> dict:
        text = kwargs.get("text", "")
        volume = float(kwargs.get("volume", 0.8))
        model_name = kwargs.get("model_name", "Zyphra/Zonos-v0.1-transformer")
        reference_audio = kwargs.get("reference_audio", "")
        language = kwargs.get("language", "en-us")

        if not text.strip():
            return {"success": False, "error": "No text provided"}

        try:
            import torch
            import torchaudio
            from zonos.conditioning import make_cond_dict

            self._ensure_loaded(model_name)

            # Create speaker embedding from reference if provided
            speaker = None
            tmp_path = None
            if reference_audio:
                ref_bytes = base64.b64decode(reference_audio)
                tmp_path = tempfile.mktemp(suffix=".wav")
                with open(tmp_path, "wb") as f:
                    f.write(ref_bytes)
                wav, sr = torchaudio.load(tmp_path)
                speaker = self.model.make_speaker_embedding(wav, sr)

            try:
                cond_dict = make_cond_dict(
                    text=text,
                    speaker=speaker,
                    language=language,
                )
                conditioning = self.model.prepare_conditioning(cond_dict)
                codes = self.model.generate(conditioning)
                wavs = self.model.autoencoder.decode(codes).cpu()

                audio_numpy = wavs[0].numpy().astype(np.float32)
                # If stereo, convert to mono
                if len(audio_numpy.shape) > 1:
                    audio_numpy = np.mean(audio_numpy, axis=0)
                audio_numpy = audio_numpy * volume

                audio_b64 = self.audio_to_base64(audio_numpy, self.sample_rate)
                duration = len(audio_numpy) / self.sample_rate

                return {
                    "success": True,
                    "audio_data": audio_b64,
                    "duration": duration,
                    "metadata": {
                        "engine": "zonos",
                        "sample_rate": self.sample_rate,
                        "model": model_name,
                        "language": language,
                    },
                }
            finally:
                if tmp_path and os.path.exists(tmp_path):
                    os.unlink(tmp_path)

        except Exception as e:
            logger.error("Zonos process failed: %s", e)
            return {"success": False, "error": str(e)}

    def cleanup(self):
        self.model = None
