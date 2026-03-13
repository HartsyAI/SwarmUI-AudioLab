#!/usr/bin/env python3
"""Qwen3-TTS engine -- multilingual TTS with voice cloning, custom voices, and voice design.

Supports 5 model variants via the 'mode' EngineConfig key:
  - voice_clone:  Base models (1.7B/0.6B) -- clone voice from reference audio
  - custom_voice: CustomVoice models (1.7B/0.6B) -- 9 premium speakers + instruction
  - voice_design: VoiceDesign model (1.7B) -- describe voice in natural language

Output: 24kHz mono float32 numpy array.
"""

import base64
import logging
import os
import sys
import tempfile
import types

import numpy as np

from .base_engine import BaseAudioEngine

logger = logging.getLogger("TTS.Qwen3")


class Qwen3TTSEngine(BaseAudioEngine):
    """Qwen3-TTS engine using qwen_tts package."""

    name = "qwen3tts"
    category = "tts"

    def __init__(self):
        self.model = None
        self.sample_rate = 24000
        self.device = None
        self.current_model_name = None

    def initialize(self) -> bool:
        try:
            # Stub sox module — qwen_tts lists it as a dependency but core
            # inference doesn't need it.  The SoX binary is unavailable on
            # Windows, so prevent an ImportError at import time.
            if "sox" not in sys.modules:
                sys.modules["sox"] = types.ModuleType("sox")

            from qwen_tts import Qwen3TTSModel  # noqa: F401

            self.device = "cuda" if self.has_cuda() else "cpu"
            logger.info("Qwen3-TTS ready on %s (model loaded on first request)", self.device)
            return True
        except Exception as e:
            logger.error("Qwen3-TTS init failed: %s", e)
            return False

    def _ensure_loaded(self, model_name: str = "Qwen/Qwen3-TTS-12Hz-1.7B-CustomVoice"):
        """Load or switch the Qwen3-TTS model."""
        if self.model is not None and self.current_model_name == model_name:
            return

        if self.model is not None:
            self.cleanup()

        import torch
        from qwen_tts import Qwen3TTSModel

        local_path = self.ensure_model_local(model_name, "tts")
        dtype = torch.bfloat16 if self.device == "cuda" else torch.float32
        device_map = f"{self.device}:0" if self.device == "cuda" else self.device

        logger.info("Loading Qwen3-TTS model: %s (dtype=%s)", model_name, dtype)
        self.model = Qwen3TTSModel.from_pretrained(
            local_path,
            device_map=device_map,
            dtype=dtype,
        )
        self.current_model_name = model_name
        logger.info("Qwen3-TTS loaded: %s (device=%s)", model_name, self.device)

    def process(self, **kwargs) -> dict:
        text = kwargs.get("text", "")
        volume = float(kwargs.get("volume", 0.8))
        model_name = kwargs.get("model_name", "Qwen/Qwen3-TTS-12Hz-1.7B-CustomVoice")
        mode = kwargs.get("mode", "custom_voice")
        language = kwargs.get("qwen3_language", "Auto")
        speaker = kwargs.get("qwen3_speaker", "Ryan")
        instruct = kwargs.get("qwen3_instruct", "")
        reference_audio = kwargs.get("reference_audio", "")
        ref_text = kwargs.get("ref_text", "")

        if not text.strip():
            return {"success": False, "error": "No text provided"}

        try:
            self._ensure_loaded(model_name)

            if self.is_cancelled():
                return self.cancelled_response()
            if mode == "voice_clone":
                wavs, sr = self._generate_voice_clone(text, language, reference_audio, ref_text)
            elif mode == "custom_voice":
                wavs, sr = self._generate_custom_voice(text, language, speaker, instruct)
            elif mode == "voice_design":
                wavs, sr = self._generate_voice_design(text, language, instruct)
            else:
                return {"success": False, "error": f"Unknown mode: {mode}"}

            if wavs is None or len(wavs) == 0:
                return {"success": False, "error": "No audio generated"}

            self.sample_rate = sr
            audio_numpy = np.array(wavs[0], dtype=np.float32)
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
                    "engine": "qwen3tts",
                    "sample_rate": self.sample_rate,
                    "model": model_name,
                    "mode": mode,
                    "language": language,
                },
            }
        except Exception as e:
            logger.error("Qwen3-TTS process failed: %s", e)
            return {"success": False, "error": str(e)}

    def _generate_voice_clone(self, text, language, reference_audio_b64, ref_text):
        """Voice cloning via Base models."""
        if not reference_audio_b64:
            raise ValueError(
                "Voice clone mode requires reference audio. "
                "Upload a reference audio clip, or use a CustomVoice/VoiceDesign model instead."
            )

        ref_audio_path = None
        try:
            ref_bytes = base64.b64decode(reference_audio_b64)
            fd, ref_audio_path = tempfile.mkstemp(suffix=".wav")
            os.close(fd)
            with open(ref_audio_path, "wb") as f:
                f.write(ref_bytes)

            wavs, sr = self.model.generate_voice_clone(
                text=text,
                language=language,
                ref_audio=ref_audio_path,
                ref_text=ref_text or "",
            )
            return wavs, sr
        finally:
            if ref_audio_path and os.path.exists(ref_audio_path):
                try:
                    os.unlink(ref_audio_path)
                except OSError:
                    pass

    def _generate_custom_voice(self, text, language, speaker, instruct):
        """Custom voice via built-in speakers."""
        kwargs = {
            "text": text,
            "language": language,
            "speaker": speaker,
        }
        if instruct and instruct.strip():
            kwargs["instruct"] = instruct
        return self.model.generate_custom_voice(**kwargs)

    def _generate_voice_design(self, text, language, instruct):
        """Voice design via natural language description."""
        return self.model.generate_voice_design(
            text=text,
            language=language,
            instruct=instruct or "A natural and clear voice.",
        )

    def cleanup(self):
        if self.model is not None:
            del self.model
            self.model = None
            self.current_model_name = None
            try:
                import torch
                if torch.cuda.is_available():
                    torch.cuda.empty_cache()
            except Exception:
                pass
