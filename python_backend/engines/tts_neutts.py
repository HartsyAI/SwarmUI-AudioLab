#!/usr/bin/env python3
"""NeuTTS engine — Neuphonic's on-device TTS with instant voice cloning.

Uses the neutts package API:
  - from neutts import NeuTTS
  - NeuTTS(backbone_repo=..., codec_repo="neuphonic/neucodec")
  - tts.encode_reference(wav_path) -> codec codes tensor
  - tts.infer(text, ref_codes, ref_text) -> numpy array at 24kHz
"""

import logging
import os
import tempfile

import numpy as np

from .base_engine import BaseAudioEngine

logger = logging.getLogger("TTS.NeuTTS")


class NeuTTSEngine(BaseAudioEngine):
    """NeuTTS Air on-device text-to-speech engine with voice cloning."""

    name = "neutts"
    category = "tts"

    def __init__(self):
        self.tts = None
        self.sample_rate = 24000
        self._default_ref_codes = None
        self._default_ref_text = None

    def initialize(self) -> bool:
        try:
            from neutts import NeuTTS  # noqa: F401

            logger.info("NeuTTS ready")
            return True
        except Exception as e:
            logger.error("NeuTTS init failed: %s", e)
            return False

    def _ensure_loaded(self, model_name: str = "neuphonic/neutts-air"):
        if self.tts is not None:
            return

        from neutts import NeuTTS

        device = "cuda" if self.has_cuda() else "cpu"
        self.tts = NeuTTS(
            backbone_repo=model_name,
            backbone_device=device,
            codec_repo="neuphonic/neucodec",
            codec_device=device,
        )
        logger.info("NeuTTS model loaded: %s on %s", model_name, device)

    def process(self, **kwargs) -> dict:
        text = kwargs.get("text", "")
        volume = float(kwargs.get("volume", 0.8))
        model_name = kwargs.get("model_name", "neuphonic/neutts-air")
        reference_audio = kwargs.get("reference_audio", "")
        reference_text = kwargs.get("reference_text", "")

        if not text.strip():
            return {"success": False, "error": "No text provided"}

        try:
            self._ensure_loaded(model_name)

            # Get or create reference codes
            ref_codes, ref_text = self._get_reference(reference_audio, reference_text)

            if ref_codes is None:
                return {
                    "success": False,
                    "error": (
                        "NeuTTS requires reference audio for voice cloning. "
                        "Provide 'reference_audio' (base64 WAV, 3-15 seconds) "
                        "and 'reference_text' (transcript of the reference audio)."
                    ),
                }

            # Generate speech
            audio_numpy = self.tts.infer(text, ref_codes, ref_text)

            if audio_numpy is None or len(audio_numpy) == 0:
                return {"success": False, "error": "No audio output generated"}

            audio_numpy = audio_numpy.astype(np.float32) * volume
            sr = self.sample_rate

            audio_b64 = self.audio_to_base64(audio_numpy, sr)
            duration = len(audio_numpy) / sr

            return {
                "success": True,
                "audio_data": audio_b64,
                "duration": duration,
                "metadata": {
                    "engine": "neutts",
                    "sample_rate": sr,
                    "model": model_name,
                },
            }
        except Exception as e:
            logger.error("NeuTTS process failed: %s", e)
            return {"success": False, "error": str(e)}

    def _get_reference(self, audio_b64: str, ref_text: str):
        """Get reference codes from provided audio or cached default."""
        import torch

        if audio_b64:
            # Decode provided reference audio
            tmp_path = self._decode_audio_to_file(audio_b64)
            try:
                ref_codes = self.tts.encode_reference(tmp_path)
                return ref_codes, ref_text or "Reference audio for voice cloning."
            finally:
                if os.path.exists(tmp_path):
                    os.unlink(tmp_path)

        # Use cached default if available
        if self._default_ref_codes is not None:
            return self._default_ref_codes, self._default_ref_text

        # No reference audio available
        return None, None

    def _decode_audio_to_file(self, audio_b64: str) -> str:
        """Decode base64 audio to a temp WAV file."""
        import base64

        audio_bytes = base64.b64decode(audio_b64)
        tmp = tempfile.NamedTemporaryFile(suffix=".wav", delete=False)
        tmp.write(audio_bytes)
        tmp.close()
        return tmp.name

    def cleanup(self):
        self.tts = None
        self._default_ref_codes = None
        self._default_ref_text = None
