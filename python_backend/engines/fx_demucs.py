#!/usr/bin/env python3
"""Demucs engine — audio source separation into stems."""

import base64
import logging
import os
import tempfile
import numpy as np

from .base_engine import BaseAudioEngine

logger = logging.getLogger("FX.Demucs")


class DemucsEngine(BaseAudioEngine):
    """Demucs audio source separation engine."""

    name = "demucs"
    category = "audiofx"

    def __init__(self):
        self.model = None
        self.device = None
        self.sample_rate = 44100
        self.current_model_name = None

    def initialize(self) -> bool:
        try:
            from demucs.pretrained import get_model  # noqa: F401
            from demucs.apply import apply_model  # noqa: F401

            self.device = "cuda" if self.has_cuda() else "cpu"
            logger.info("Demucs ready on %s", self.device)
            return True
        except Exception as e:
            logger.error("Demucs init failed: %s", e)
            return False

    def _load_model(self, model_name: str = "htdemucs"):
        """Load a specific Demucs model."""
        if self.model is not None and self.current_model_name == model_name:
            return

        from demucs.pretrained import get_model
        import torch

        if self.model is not None:
            del self.model
            if torch.cuda.is_available():
                torch.cuda.empty_cache()

        self.model = get_model(model_name)
        self.model.to(self.device)
        self.model.eval()
        self.current_model_name = model_name
        logger.info("Loaded Demucs model: %s", model_name)

    def process(self, **kwargs) -> dict:
        audio_data = kwargs.get("audio_data", "")
        model_name = kwargs.get("model_name", "htdemucs")
        overlap = float(kwargs.get("overlap", 0.25))
        shifts = int(kwargs.get("shifts", 1))

        if not audio_data:
            return {"success": False, "error": "No audio data provided"}

        tmp_path = None
        try:
            import torch
            import torchaudio
            from demucs.apply import apply_model

            self._load_model(model_name)

            # Write input audio to temp file
            audio_bytes = base64.b64decode(audio_data)
            with tempfile.NamedTemporaryFile(
                suffix=".wav", delete=False
            ) as tmp:
                tmp.write(audio_bytes)
                tmp_path = tmp.name

            waveform, sr = torchaudio.load(tmp_path)
            waveform = waveform.to(self.device)

            # Resample to model's sample rate if needed
            if sr != self.sample_rate:
                resampler = torchaudio.transforms.Resample(sr, self.sample_rate).to(self.device)
                waveform = resampler(waveform)

            # Normalize
            ref = waveform.mean(0)
            waveform_norm = (waveform - ref.mean()) / (ref.std() + 1e-8)

            # Add batch dimension and separate
            with torch.no_grad():
                sources = apply_model(
                    self.model, waveform_norm[None], device=self.device,
                    overlap=overlap, shifts=shifts,
                )[0]

            # Denormalize
            sources = sources * (ref.std() + 1e-8) + ref.mean()

            # Build stems dict
            stems = {}
            source_names = self.model.sources
            for i, name in enumerate(source_names):
                stem_audio = sources[i].cpu().numpy().astype(np.float32)
                # Convert stereo to mono for consistency
                if len(stem_audio.shape) > 1:
                    stem_mono = np.mean(stem_audio, axis=0)
                else:
                    stem_mono = stem_audio
                stems[name] = self.audio_to_base64(stem_mono, self.sample_rate)

            return {
                "success": True,
                "stems": stems,
                "metadata": {
                    "engine": "demucs",
                    "model": model_name,
                    "sample_rate": self.sample_rate,
                    "stem_names": list(source_names),
                },
            }
        except Exception as e:
            logger.error("Demucs process failed: %s", e)
            return {"success": False, "error": str(e)}
        finally:
            if tmp_path and os.path.exists(tmp_path):
                os.unlink(tmp_path)

    def cleanup(self):
        self.model = None
        self.current_model_name = None
