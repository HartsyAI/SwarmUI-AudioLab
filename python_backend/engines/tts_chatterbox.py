#!/usr/bin/env python3
"""Chatterbox TTS engine — extracted from tts_engines.py."""

import base64
import io
import logging
import os
import struct
import tempfile
import numpy as np

from .base_engine import BaseAudioEngine

logger = logging.getLogger("TTS.Chatterbox")


class ChatterboxEngine(BaseAudioEngine):
    """Chatterbox TTS engine implementation."""

    name = "chatterbox"
    category = "tts"

    def __init__(self):
        self.model = None
        self.sample_rate = 22050

    def initialize(self) -> bool:
        try:
            from chatterbox import ChatterboxTTS

            device = "cuda" if self.has_cuda() else "cpu"
            self.model = ChatterboxTTS.from_pretrained(device=device)
            self.sample_rate = getattr(self.model, "sr", 22050)
            logger.info("Chatterbox initialized on %s", device)
            return True
        except Exception as e:
            logger.error("Chatterbox init failed: %s", e)
            return False

    def process(self, **kwargs) -> dict:
        text = kwargs.get("text", "")
        exaggeration = float(kwargs.get("exaggeration", 0.5))
        cfg_weight = float(kwargs.get("cfg_weight", 0.5))
        temperature = float(kwargs.get("temperature", 0.8))
        repetition_penalty = float(kwargs.get("repetition_penalty", 1.2))
        top_p = float(kwargs.get("top_p", 1.0))
        min_p = float(kwargs.get("min_p", 0.05))
        volume = float(kwargs.get("volume", 0.8))
        reference_audio = kwargs.get("reference_audio", "")

        # Decode reference audio to temp file if provided
        audio_prompt_path = None
        tmp_path = None
        if reference_audio:
            try:
                ref_bytes = base64.b64decode(reference_audio)
                with tempfile.NamedTemporaryFile(suffix=".wav", delete=False) as tmp:
                    tmp.write(ref_bytes)
                    tmp_path = tmp.name
                audio_prompt_path = tmp_path
                logger.info("Using reference audio for voice cloning (%d bytes)", len(ref_bytes))
            except Exception as e:
                logger.warning("Failed to decode reference audio, proceeding without: %s", e)

        try:
            audio_tensor = self.model.generate(
                text,
                exaggeration=exaggeration,
                cfg_weight=cfg_weight,
                temperature=temperature,
                repetition_penalty=repetition_penalty,
                top_p=top_p,
                min_p=min_p,
                audio_prompt_path=audio_prompt_path,
            )

            audio_numpy = audio_tensor.cpu().numpy()
            if len(audio_numpy.shape) > 1:
                audio_numpy = np.mean(audio_numpy, axis=0)

            audio_numpy = audio_numpy * volume
            wav_bytes = self._numpy_to_wav(audio_numpy)
            audio_base64 = base64.b64encode(wav_bytes).decode("utf-8")
            duration = len(audio_numpy) / self.sample_rate

            return {
                "success": True,
                "audio_data": audio_base64,
                "duration": duration,
                "metadata": {
                    "engine": "chatterbox",
                    "sample_rate": self.sample_rate,
                    "voice_cloned": audio_prompt_path is not None,
                },
            }
        finally:
            if tmp_path and os.path.exists(tmp_path):
                try:
                    os.unlink(tmp_path)
                except OSError:
                    pass

    def cleanup(self):
        self.model = None

    # -- private helpers --------------------------------------------------

    def _numpy_to_wav(self, audio_numpy: np.ndarray) -> bytes:
        max_amplitude = np.max(np.abs(audio_numpy))
        if max_amplitude > 0.8:
            audio_numpy = audio_numpy * 0.8 / max_amplitude

        audio_int16 = (audio_numpy * 32767).astype(np.int16)

        wav_io = io.BytesIO()
        num_channels = 1
        bits_per_sample = 16
        byte_rate = self.sample_rate * num_channels * bits_per_sample // 8
        block_align = num_channels * bits_per_sample // 8
        data_size = len(audio_int16) * bits_per_sample // 8
        file_size = 36 + data_size

        wav_io.write(b"RIFF")
        wav_io.write(struct.pack("<I", file_size))
        wav_io.write(b"WAVE")
        wav_io.write(b"fmt ")
        wav_io.write(struct.pack("<I", 16))
        wav_io.write(struct.pack("<H", 1))  # PCM
        wav_io.write(struct.pack("<H", num_channels))
        wav_io.write(struct.pack("<I", self.sample_rate))
        wav_io.write(struct.pack("<I", byte_rate))
        wav_io.write(struct.pack("<H", block_align))
        wav_io.write(struct.pack("<H", bits_per_sample))
        wav_io.write(b"data")
        wav_io.write(struct.pack("<I", data_size))
        wav_io.write(audio_int16.tobytes())

        return wav_io.getvalue()
