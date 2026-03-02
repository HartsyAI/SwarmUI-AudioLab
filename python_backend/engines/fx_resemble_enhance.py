#!/usr/bin/env python3
"""Resemble Enhance engine — audio denoising and enhancement."""

import base64
import logging
import os
import tempfile
import numpy as np

from .base_engine import BaseAudioEngine

logger = logging.getLogger("FX.ResembleEnhance")


class ResembleEnhanceEngine(BaseAudioEngine):
    """Resemble Enhance audio denoising/enhancement engine."""

    name = "resemble_enhance"
    category = "audiofx"

    def __init__(self):
        self.device = None
        self.sample_rate = 44100

    def initialize(self) -> bool:
        try:
            from resemble_enhance.enhancer.inference import denoise, enhance  # noqa: F401

            self.device = "cuda" if self.has_cuda() else "cpu"
            logger.info("Resemble Enhance ready on %s", self.device)
            return True
        except Exception as e:
            logger.error("Resemble Enhance init failed: %s", e)
            return False

    def process(self, **kwargs) -> dict:
        audio_data = kwargs.get("audio_data", "")
        mode = kwargs.get("mode", "enhance")
        nfe = int(kwargs.get("nfe", 64))
        solver = kwargs.get("solver", "midpoint")
        lambd = float(kwargs.get("lambd", 0.1))
        tau = float(kwargs.get("tau", 0.5))

        if not audio_data:
            return {"success": False, "error": "No audio data provided"}

        tmp_path = None
        try:
            import torch
            import torchaudio
            from resemble_enhance.enhancer.inference import denoise, enhance

            # Write input audio to temp file and load as tensor
            audio_bytes = base64.b64decode(audio_data)
            with tempfile.NamedTemporaryFile(
                suffix=".wav", delete=False
            ) as tmp:
                tmp.write(audio_bytes)
                tmp_path = tmp.name

            dwav, sr = torchaudio.load(tmp_path)
            dwav = dwav.to(self.device)

            # Convert to mono if stereo
            if dwav.shape[0] > 1:
                dwav = dwav.mean(dim=0)
            else:
                dwav = dwav[0]

            if mode == "denoise":
                out_wav, out_sr = denoise(dwav, sr, self.device)
            else:
                out_wav, out_sr = enhance(
                    dwav, sr, self.device,
                    nfe=nfe,
                    solver=solver,
                    lambd=lambd,
                    tau=tau,
                )

            self.sample_rate = out_sr

            # Convert output to numpy
            out_numpy = out_wav.cpu().numpy().astype(np.float32)
            if len(out_numpy.shape) > 1:
                out_numpy = np.mean(out_numpy, axis=0)

            audio_b64 = self.audio_to_base64(out_numpy, out_sr)
            duration = len(out_numpy) / out_sr

            return {
                "success": True,
                "audio_data": audio_b64,
                "duration": duration,
                "metadata": {
                    "engine": "resemble_enhance",
                    "mode": mode,
                    "sample_rate": out_sr,
                },
            }
        except Exception as e:
            logger.error("Resemble Enhance process failed: %s", e)
            return {"success": False, "error": str(e)}
        finally:
            if tmp_path and os.path.exists(tmp_path):
                os.unlink(tmp_path)

    def cleanup(self):
        pass
