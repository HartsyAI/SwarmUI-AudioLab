#!/usr/bin/env python3
"""F5-TTS engine — zero-shot voice cloning via flow matching."""

import base64
import logging
import os
import tempfile

from .base_engine import BaseAudioEngine

logger = logging.getLogger("TTS.F5")


class F5TTSEngine(BaseAudioEngine):
    """F5-TTS zero-shot voice cloning engine."""

    name = "f5"
    category = "tts"

    def __init__(self):
        self.tts = None
        self.sample_rate = 24000

    def initialize(self) -> bool:
        try:
            from f5_tts.api import F5TTS  # noqa: F401

            logger.info("F5-TTS ready (model loaded on first request)")
            return True
        except Exception as e:
            logger.error("F5-TTS init failed: %s", e)
            return False

    def _ensure_loaded(self):
        if self.tts is not None:
            return

        from f5_tts.api import F5TTS

        self.tts = F5TTS()
        logger.info("F5-TTS model loaded")

    def process(self, **kwargs) -> dict:
        text = kwargs.get("text", "")
        reference_audio = kwargs.get("reference_audio", "")
        ref_text = kwargs.get("ref_text", "")
        volume = float(kwargs.get("volume", 0.8))
        cfg_strength = float(kwargs.get("cfg_scale", 2.0))
        nfe_step = int(kwargs.get("nfe_step", 32))
        speed = float(kwargs.get("speed", 1.0))

        if not text.strip():
            return {"success": False, "error": "No text provided"}
        if not reference_audio:
            return {"success": False,
                    "error": "F5-TTS requires reference audio for voice cloning"}

        tmp_files = []
        try:
            import numpy as np
            self._ensure_loaded()

            # Write reference audio to temp file
            ref_bytes = base64.b64decode(reference_audio)
            ref_path = tempfile.mktemp(suffix=".wav")
            tmp_files.append(ref_path)
            with open(ref_path, "wb") as f:
                f.write(ref_bytes)

            wav, sr, _spec = self.tts.infer(
                ref_file=ref_path,
                ref_text=ref_text,
                gen_text=text,
                seed=-1,
                cfg_strength=cfg_strength,
                nfe_step=nfe_step,
                speed=speed,
            )

            self.sample_rate = sr
            audio_numpy = np.array(wav, dtype=np.float32) * volume

            audio_b64 = self.audio_to_base64(audio_numpy, sr)
            duration = len(audio_numpy) / sr

            return {
                "success": True,
                "audio_data": audio_b64,
                "duration": duration,
                "metadata": {
                    "engine": "f5",
                    "sample_rate": sr,
                },
            }
        except Exception as e:
            logger.error("F5-TTS process failed: %s", e)
            return {"success": False, "error": str(e)}
        finally:
            for f in tmp_files:
                if f and os.path.exists(f):
                    try:
                        os.unlink(f)
                    except OSError:
                        pass

    def cleanup(self):
        self.tts = None
