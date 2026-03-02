#!/usr/bin/env python3
"""ACE-Step engine — full-song music generation with lyrics support."""

import logging
import os
import shutil
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
            from acestep.pipeline_ace_step import ACEStepPipeline  # noqa: F401

            logger.info("ACE-Step ready (model loaded on first request)")
            return True
        except Exception as e:
            logger.error("ACE-Step init failed: %s", e)
            return False

    def _ensure_loaded(self):
        """Load the ACE-Step pipeline if not already loaded."""
        if self.handler is not None:
            return

        import torch
        from acestep.pipeline_ace_step import ACEStepPipeline

        device_id = 0 if torch.cuda.is_available() else -1
        self.handler = ACEStepPipeline(device_id=device_id)
        logger.info("ACE-Step pipeline loaded on device %d", device_id)

    def process(self, **kwargs) -> dict:
        prompt = kwargs.get("prompt", "")
        lyrics = kwargs.get("lyrics", "[Instrumental]")
        duration = float(kwargs.get("duration", 30))
        seed = int(kwargs.get("seed", -1))
        infer_step = int(kwargs.get("infer_step", 60))
        guidance_scale = float(kwargs.get("guidance_scale", 15.0))
        scheduler_type = kwargs.get("scheduler_type", "euler")
        cfg_type = kwargs.get("cfg_type", "apg")

        if not prompt.strip():
            return {"success": False, "error": "No prompt provided"}

        save_dir = None
        try:
            import torchaudio

            self._ensure_loaded()

            # ACE-Step uses manual_seeds as a list
            manual_seeds = [seed] if seed >= 0 else None

            # Create temp dir for output files
            save_dir = tempfile.mkdtemp(prefix="acestep_")

            # ACE-Step pipeline is callable (__call__), not .generate()
            result = self.handler(
                prompt=prompt,
                lyrics=lyrics,
                audio_duration=duration,
                manual_seeds=manual_seeds,
                infer_step=infer_step,
                guidance_scale=guidance_scale,
                scheduler_type=scheduler_type,
                cfg_type=cfg_type,
                save_path=save_dir,
            )

            if result is None or len(result) < 2:
                return {"success": False, "error": "ACE-Step returned no output"}

            # Result is [output_path_0, ..., params_dict]
            audio_path = result[0]

            if not os.path.exists(audio_path):
                return {"success": False, "error": f"Output file not found: {audio_path}"}

            # Read the generated audio file
            waveform, sr = torchaudio.load(audio_path)
            audio_numpy = waveform.cpu().numpy().astype(np.float32)

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
        finally:
            if save_dir and os.path.exists(save_dir):
                try:
                    shutil.rmtree(save_dir)
                except OSError:
                    pass

    def cleanup(self):
        self.handler = None
