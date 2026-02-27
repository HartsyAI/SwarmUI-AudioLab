#!/usr/bin/env python3
"""VibeVoice TTS engine — Microsoft's multi-speaker speech synthesis."""

import logging
import numpy as np

from .base_engine import BaseAudioEngine

logger = logging.getLogger("TTS.VibeVoice")


class VibeVoiceEngine(BaseAudioEngine):
    """VibeVoice TTS engine from Microsoft."""

    name = "vibevoice"
    category = "tts"

    def __init__(self):
        self.model = None
        self.processor = None
        self.sample_rate = 24000
        self.device = None

    def initialize(self) -> bool:
        try:
            from transformers import AutoProcessor  # noqa: F401

            self.device = "cuda" if self.has_cuda() else "cpu"
            logger.info("VibeVoice ready on %s", self.device)
            return True
        except Exception as e:
            logger.error("VibeVoice init failed: %s", e)
            return False

    def _ensure_loaded(self, model_name: str = "microsoft/VibeVoice-Realtime-0.5B"):
        if self.model is not None:
            return

        from transformers import AutoProcessor, AutoModelForCausalLM

        self.processor = AutoProcessor.from_pretrained(model_name,
                                                        trust_remote_code=True)
        self.model = AutoModelForCausalLM.from_pretrained(
            model_name, trust_remote_code=True
        ).to(self.device)
        logger.info("VibeVoice model loaded: %s", model_name)

    def process(self, **kwargs) -> dict:
        text = kwargs.get("text", "")
        volume = float(kwargs.get("volume", 0.8))
        model_name = kwargs.get("model_name",
                                "microsoft/VibeVoice-Realtime-0.5B")

        if not text.strip():
            return {"success": False, "error": "No text provided"}

        try:
            self._ensure_loaded(model_name)

            inputs = self.processor(text, return_tensors="pt").to(self.device)
            outputs = self.model.generate(**inputs)

            # Decode to audio
            import tempfile
            import os
            import soundfile as sf

            tmp_path = tempfile.mktemp(suffix=".wav")
            try:
                if hasattr(self.processor, "save_audio"):
                    decoded = self.processor.batch_decode(outputs)
                    self.processor.save_audio(decoded, tmp_path)
                else:
                    # Fallback: outputs might already be waveform
                    audio_np = outputs[0].cpu().numpy().astype(np.float32)
                    sf.write(tmp_path, audio_np, self.sample_rate)

                audio_data, sr = sf.read(tmp_path, dtype="float32")
                self.sample_rate = sr
            finally:
                if os.path.exists(tmp_path):
                    os.unlink(tmp_path)

            audio_data = audio_data * volume
            audio_b64 = self.audio_to_base64(audio_data, sr)
            duration = len(audio_data) / sr

            return {
                "success": True,
                "audio_data": audio_b64,
                "duration": duration,
                "metadata": {
                    "engine": "vibevoice",
                    "sample_rate": sr,
                    "model": model_name,
                },
            }
        except Exception as e:
            logger.error("VibeVoice process failed: %s", e)
            return {"success": False, "error": str(e)}

    def cleanup(self):
        self.model = None
        self.processor = None
