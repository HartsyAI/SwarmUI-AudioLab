#!/usr/bin/env python3
"""CSM engine — Sesame Conversational Speech Model."""

import logging
import numpy as np

from .base_engine import BaseAudioEngine

logger = logging.getLogger("TTS.CSM")


class CSMEngine(BaseAudioEngine):
    """CSM (Conversational Speech Model) TTS engine from Sesame."""

    name = "csm"
    category = "tts"

    def __init__(self):
        self.model = None
        self.processor = None
        self.sample_rate = 24000
        self.device = None

    def initialize(self) -> bool:
        try:
            from transformers import CsmForConditionalGeneration  # noqa: F401
            from transformers import AutoProcessor  # noqa: F401

            self.device = "cuda" if self.has_cuda() else "cpu"
            logger.info("CSM ready on %s (model loaded on first request)",
                        self.device)
            return True
        except Exception as e:
            logger.error("CSM init failed: %s", e)
            return False

    def _ensure_loaded(self, model_name: str = "sesame/csm-1b"):
        if self.model is not None:
            return

        from transformers import CsmForConditionalGeneration, AutoProcessor

        local_path = self.ensure_model_local(model_name, "tts")
        self.processor = AutoProcessor.from_pretrained(local_path)
        self.model = CsmForConditionalGeneration.from_pretrained(
            local_path, device_map=self.device
        )
        logger.info("CSM model loaded: %s", model_name)

    def process(self, **kwargs) -> dict:
        text = kwargs.get("text", "")
        volume = float(kwargs.get("volume", 0.8))
        speaker_id = kwargs.get("speaker_id", "0")
        model_name = kwargs.get("model_name", "sesame/csm-1b")
        temperature = float(kwargs.get("temperature", 0.9))
        top_k = int(kwargs.get("top_k", 50))

        if not text.strip():
            return {"success": False, "error": "No text provided"}

        try:
            self._ensure_loaded(model_name)

            # Prepare text with speaker ID prefix
            prompt_text = f"[{speaker_id}]{text}"
            inputs = self.processor(
                prompt_text, add_special_tokens=True
            ).to(self.device)

            gen_kwargs = {"output_audio": True, "do_sample": True}
            if temperature != 1.0:
                gen_kwargs["temperature"] = temperature
            if top_k > 0:
                gen_kwargs["top_k"] = top_k

            audio = self.model.generate(**inputs, **gen_kwargs)

            # Save to temp file and read back
            import tempfile
            import os
            import soundfile as sf

            tmp_path = tempfile.mktemp(suffix=".wav")
            try:
                self.processor.save_audio(audio, tmp_path)
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
                    "engine": "csm",
                    "sample_rate": sr,
                    "speaker_id": speaker_id,
                },
            }
        except Exception as e:
            logger.error("CSM process failed: %s", e)
            return {"success": False, "error": str(e)}

    def cleanup(self):
        self.model = None
        self.processor = None
