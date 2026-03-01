#!/usr/bin/env python3
"""Piper TTS engine — fast, CPU-focused, multiple voices."""

import io
import logging
import os
import wave
import numpy as np

from .base_engine import BaseAudioEngine

logger = logging.getLogger("TTS.Piper")

# Voice model names mapped to HuggingFace paths
VOICE_MODELS = {
    "en_US-amy-medium": "en/en_US/amy/medium/en_US-amy-medium",
    "en_US-danny-low": "en/en_US/danny/low/en_US-danny-low",
    "en_GB-alba-medium": "en/en_GB/alba/medium/en_GB-alba-medium",
}

HF_BASE = "https://huggingface.co/rhasspy/piper-voices/resolve/main"


class PiperEngine(BaseAudioEngine):
    """Piper TTS engine using ONNX voice models."""

    name = "piper"
    category = "tts"

    def __init__(self):
        self.voices = {}
        self.sample_rate = 22050
        self.models_dir = None

    def initialize(self) -> bool:
        try:
            from piper.voice import PiperVoice

            self.models_dir = os.path.join(
                os.path.dirname(os.path.dirname(__file__)), "models", "piper"
            )
            os.makedirs(self.models_dir, exist_ok=True)

            logger.info("Piper initialized (models dir: %s)", self.models_dir)
            return True
        except Exception as e:
            logger.error("Piper init failed: %s", e)
            return False

    def _get_voice(self, voice_name: str):
        """Load a voice model, downloading if necessary."""
        if voice_name in self.voices:
            return self.voices[voice_name]

        from piper.voice import PiperVoice

        model_path = os.path.join(self.models_dir, f"{voice_name}.onnx")
        config_path = os.path.join(self.models_dir, f"{voice_name}.onnx.json")

        if not os.path.exists(model_path):
            self._download_voice(voice_name, model_path, config_path)

        voice = PiperVoice.load(model_path)
        self.sample_rate = voice.config.sample_rate
        self.voices[voice_name] = voice
        return voice

    def _download_voice(self, voice_name: str, model_path: str,
                        config_path: str):
        """Download voice ONNX model from HuggingFace."""
        import urllib.request

        hf_path = VOICE_MODELS.get(voice_name, f"en/en_US/amy/medium/{voice_name}")
        onnx_url = f"{HF_BASE}/{hf_path}.onnx"
        json_url = f"{HF_BASE}/{hf_path}.onnx.json"

        logger.info("Downloading Piper voice: %s", voice_name)
        urllib.request.urlretrieve(onnx_url, model_path)
        urllib.request.urlretrieve(json_url, config_path)
        logger.info("Downloaded Piper voice: %s", voice_name)

    def process(self, **kwargs) -> dict:
        text = kwargs.get("text", "")
        voice_name = kwargs.get("voice", "en_US-amy-medium")
        volume = float(kwargs.get("volume", 0.8))
        speed = float(kwargs.get("speed", 1.0))

        if not text.strip():
            return {"success": False, "error": "No text provided"}

        try:
            voice = self._get_voice(voice_name)

            # Collect raw PCM audio from synthesize() → AudioChunk objects
            from piper.config import SynthesisConfig

            sr = voice.config.sample_rate
            syn_config = SynthesisConfig(
                length_scale=1.0 / speed if speed > 0 else 1.0
            )
            audio_chunks = []
            for chunk in voice.synthesize(text, syn_config=syn_config):
                audio_chunks.append(chunk.audio_int16_bytes)

            if not audio_chunks:
                return {"success": False, "error": "No audio generated"}

            raw_bytes = b"".join(audio_chunks)
            audio_int16 = np.frombuffer(raw_bytes, dtype=np.int16)
            audio_float = audio_int16.astype(np.float32) / 32767.0
            audio_float = audio_float * volume
            self.sample_rate = sr

            audio_b64 = self.audio_to_base64(audio_float, self.sample_rate)
            duration = len(audio_float) / self.sample_rate

            return {
                "success": True,
                "audio_data": audio_b64,
                "duration": duration,
                "metadata": {
                    "engine": "piper",
                    "sample_rate": self.sample_rate,
                    "voice": voice_name,
                },
            }
        except Exception as e:
            logger.error("Piper process failed: %s", e)
            return {"success": False, "error": str(e)}

    def cleanup(self):
        self.voices.clear()
