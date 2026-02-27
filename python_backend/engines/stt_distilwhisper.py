#!/usr/bin/env python3
"""Distil-Whisper STT engine — fast, accurate speech-to-text."""

import base64
import logging
import os
import tempfile

from .base_engine import BaseAudioEngine

logger = logging.getLogger("STT.DistilWhisper")


class DistilWhisperEngine(BaseAudioEngine):
    """Distil-Whisper STT engine using HuggingFace transformers pipeline."""

    name = "distilwhisper"
    category = "stt"

    def __init__(self):
        self.pipe = None
        self.model_id = "distil-whisper/distil-large-v3"

    def initialize(self) -> bool:
        try:
            import torch
            from transformers import (
                AutoModelForSpeechSeq2Seq,
                AutoProcessor,
                pipeline,
            )

            device = "cuda:0" if self.has_cuda() else "cpu"
            torch_dtype = torch.float16 if self.has_cuda() else torch.float32

            model = AutoModelForSpeechSeq2Seq.from_pretrained(
                self.model_id,
                torch_dtype=torch_dtype,
                low_cpu_mem_usage=True,
                use_safetensors=True,
            )
            model.to(device)

            processor = AutoProcessor.from_pretrained(self.model_id)

            self.pipe = pipeline(
                "automatic-speech-recognition",
                model=model,
                tokenizer=processor.tokenizer,
                feature_extractor=processor.feature_extractor,
                max_new_tokens=128,
                chunk_length_s=25,
                torch_dtype=torch_dtype,
                device=device,
            )

            logger.info("Distil-Whisper initialized on %s", device)
            return True
        except Exception as e:
            logger.error("Distil-Whisper init failed: %s", e)
            return False

    def process(self, **kwargs) -> dict:
        audio_data = kwargs.get("audio_data", "")
        language = kwargs.get("language", "en-US")

        if not audio_data:
            return {"success": False, "error": "No audio data provided"}

        temp_path = None
        try:
            audio_bytes = base64.b64decode(audio_data)

            with tempfile.NamedTemporaryFile(
                suffix=".wav", delete=False
            ) as tmp:
                tmp.write(audio_bytes)
                temp_path = tmp.name

            lang_code = language.split("-")[0] if "-" in language else language

            result = self.pipe(
                temp_path,
                generate_kwargs={"language": lang_code},
                return_timestamps="word",
            )

            text = result.get("text", "").strip()

            # Compute average confidence from word-level chunks
            chunks = result.get("chunks", [])
            if chunks:
                confidences = [
                    c.get("confidence", 0.9)
                    for c in chunks
                    if "confidence" in c
                ]
                avg_confidence = (
                    sum(confidences) / len(confidences) if confidences else 0.9
                )
            else:
                avg_confidence = 0.9

            return {
                "success": True,
                "text": text,
                "confidence": round(avg_confidence, 3),
                "metadata": {
                    "engine": "distilwhisper",
                    "model": self.model_id,
                    "language": lang_code,
                },
            }
        except Exception as e:
            logger.error("Distil-Whisper process failed: %s", e)
            return {"success": False, "error": str(e)}
        finally:
            if temp_path and os.path.exists(temp_path):
                os.unlink(temp_path)

    def cleanup(self):
        self.pipe = None
