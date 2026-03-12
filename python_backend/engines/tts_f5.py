#!/usr/bin/env python3
"""F5-TTS engine — zero-shot voice cloning via flow matching.

Uses the F5TTS high-level API which handles model downloading, vocoder
loading, and chunked inference internally.  Output is 24kHz mono WAV.
"""

import base64
import logging
import os
import tempfile

import numpy as np

from .base_engine import BaseAudioEngine

logger = logging.getLogger("TTS.F5")


class F5TTSEngine(BaseAudioEngine):
    """F5-TTS zero-shot voice cloning engine."""

    name = "f5"
    category = "tts"

    # EngineConfig sends HF repo IDs; F5TTS API expects model identifiers
    _MODEL_MAP = {
        "SWivid/F5-TTS": "F5TTS_v1_Base",
    }

    def __init__(self):
        self.tts = None
        self.sample_rate = 24000
        self.device = None
        self._current_model = None

    def initialize(self) -> bool:
        try:
            from f5_tts.api import F5TTS  # noqa: F401

            self.device = "cuda" if self.has_cuda() else "cpu"
            logger.info("F5-TTS ready on %s", self.device)
            return True
        except Exception as e:
            logger.error("F5-TTS init failed: %s", e)
            return False

    def _ensure_loaded(self, model_name: str = "F5TTS_v1_Base"):
        # Resolve HF repo ID to F5TTS model identifier
        model_name = self._MODEL_MAP.get(model_name, model_name)

        if self.tts is not None and self._current_model == model_name:
            return

        from f5_tts.api import F5TTS

        # F5TTS handles its own HF downloads via cached_path
        self.tts = F5TTS(
            model=model_name,
            device=self.device,
        )
        self._current_model = model_name
        logger.info("F5-TTS model '%s' loaded on %s", model_name, self.device)

    def process(self, **kwargs) -> dict:
        text = kwargs.get("text", "")
        reference_audio = kwargs.get("reference_audio", "")
        ref_text = kwargs.get("ref_text", "")
        volume = float(kwargs.get("volume", 0.8))
        model_name = kwargs.get("model_name", "F5TTS_v1_Base")
        cfg_strength = float(kwargs.get("cfg_scale", 2.0))
        nfe_step = int(kwargs.get("nfe_step", 32))
        speed = float(kwargs.get("speed", 1.0))
        sway_sampling_coef = float(kwargs.get("sway_sampling_coef", -1.0))
        cross_fade_duration = float(kwargs.get("cross_fade_duration", 0.15))
        target_rms = float(kwargs.get("target_rms", 0.1))
        remove_silence = bool(kwargs.get("remove_silence", False))
        seed = kwargs.get("seed", None)
        if seed is not None:
            seed = int(seed)
            if seed < 0:
                seed = None

        if not text.strip():
            return {"success": False, "error": "No text provided"}
        if not reference_audio:
            return {"success": False,
                    "error": "F5-TTS requires reference audio for voice cloning"}

        tmp_files = []
        try:
            self._ensure_loaded(model_name)

            # Write reference audio to temp file (F5-TTS needs a file path)
            ref_bytes = base64.b64decode(reference_audio)
            ref_fd, ref_path = tempfile.mkstemp(suffix=".wav")
            os.close(ref_fd)
            tmp_files.append(ref_path)
            with open(ref_path, "wb") as f:
                f.write(ref_bytes)

            # Auto-transcribe if no ref_text provided
            if not ref_text.strip():
                logger.info("No ref_text provided, auto-transcribing reference audio")
                ref_text = self.tts.transcribe(ref_path)
                logger.info("Transcribed: %s", ref_text[:80])

            wav, sr, _spec = self.tts.infer(
                ref_file=ref_path,
                ref_text=ref_text,
                gen_text=text,
                seed=seed,
                cfg_strength=cfg_strength,
                nfe_step=nfe_step,
                speed=speed,
                sway_sampling_coef=sway_sampling_coef,
                cross_fade_duration=cross_fade_duration,
                target_rms=target_rms,
                remove_silence=remove_silence,
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
                    "model": self._current_model,
                    "device": self.device,
                    "voice_cloned": True,
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
        self._current_model = None
