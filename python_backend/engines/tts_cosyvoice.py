#!/usr/bin/env python3
"""CosyVoice TTS engine — Alibaba's multilingual speech synthesis.

Uses the cosyvoice package API (requires git clone of FunAudioLLM/CosyVoice):
  - from cosyvoice.cli.cosyvoice import AutoModel
  - AutoModel(model_dir=...) — auto-detects CosyVoice v1/v2/v3
  - inference_sft(text, speaker) — pre-trained speakers (v1 only)
  - inference_zero_shot(text, prompt_text, prompt_wav) — voice cloning (v2)
  - inference_cross_lingual(text, prompt_wav) — cross-lingual (v2)
  - All inference methods are generators yielding {'tts_speech': Tensor}
"""

import logging
import os
import tempfile

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
            from cosyvoice.cli.cosyvoice import AutoModel  # noqa: F401

            self.device = "cuda" if self.has_cuda() else "cpu"
            logger.info("CosyVoice ready on %s", self.device)
            return True
        except Exception as e:
            logger.error("CosyVoice init failed: %s", e)
            return False

    def _ensure_loaded(self, model_name: str = "FunAudioLLM/CosyVoice2-0.5B"):
        if self.model is not None:
            return

        from cosyvoice.cli.cosyvoice import AutoModel

        # AutoModel auto-detects CosyVoice v1/v2/v3 from the model dir
        self.model = AutoModel(model_dir=model_name)
        self.sample_rate = getattr(self.model, "sample_rate", 22050)
        logger.info("CosyVoice model loaded: %s (sr=%d)", model_name, self.sample_rate)

    def process(self, **kwargs) -> dict:
        text = kwargs.get("text", "")
        volume = float(kwargs.get("volume", 0.8))
        voice = kwargs.get("voice", "中文女")
        model_name = kwargs.get("model_name", "FunAudioLLM/CosyVoice2-0.5B")
        reference_audio = kwargs.get("reference_audio", "")
        reference_text = kwargs.get("reference_text", "")

        if not text.strip():
            return {"success": False, "error": "No text provided"}

        try:
            import torch

            self._ensure_loaded(model_name)

            chunks = []

            # Strategy 1: If reference audio provided, use zero-shot voice cloning
            if reference_audio:
                prompt_wav_path = self._decode_reference_audio(reference_audio)
                try:
                    if reference_text:
                        # Zero-shot: clone voice from reference
                        for chunk in self.model.inference_zero_shot(
                            text, reference_text, prompt_wav_path
                        ):
                            chunks.append(chunk["tts_speech"])
                    else:
                        # Cross-lingual: clone voice without transcript
                        for chunk in self.model.inference_cross_lingual(
                            text, prompt_wav_path
                        ):
                            chunks.append(chunk["tts_speech"])
                finally:
                    if os.path.exists(prompt_wav_path):
                        os.unlink(prompt_wav_path)

            # Strategy 2: Try SFT mode with pre-trained speakers (CosyVoice v1)
            elif hasattr(self.model, "inference_sft"):
                try:
                    for chunk in self.model.inference_sft(text, voice):
                        chunks.append(chunk["tts_speech"])
                except Exception as sft_err:
                    logger.warning("SFT inference failed: %s", sft_err)
                    return {
                        "success": False,
                        "error": (
                            "CosyVoice2 requires reference audio for voice cloning. "
                            "Provide 'reference_audio' (base64 WAV) and optionally "
                            "'reference_text' (transcript of the reference audio)."
                        ),
                    }
            else:
                return {
                    "success": False,
                    "error": (
                        "This CosyVoice model requires reference audio. "
                        "Provide 'reference_audio' (base64 WAV) and "
                        "'reference_text' (transcript of the reference)."
                    ),
                }

            if not chunks:
                return {"success": False, "error": "No audio chunks generated"}

            # Concatenate all chunks
            full_audio = torch.cat(chunks, dim=1)
            audio_numpy = full_audio.cpu().float().numpy().squeeze(0)
            audio_numpy = audio_numpy * volume
            sr = self.sample_rate

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

    def _decode_reference_audio(self, audio_b64: str) -> str:
        """Decode base64 audio to a temp WAV file and return the path."""
        import base64

        audio_bytes = base64.b64decode(audio_b64)
        tmp = tempfile.NamedTemporaryFile(suffix=".wav", delete=False)
        tmp.write(audio_bytes)
        tmp.close()
        return tmp.name

    def cleanup(self):
        self.model = None
