#!/usr/bin/env python3
"""Distil-Whisper STT engine — fast, accurate speech-to-text."""

import base64
import logging
import os

from .base_engine import BaseAudioEngine

logger = logging.getLogger("STT.DistilWhisper")


class DistilWhisperEngine(BaseAudioEngine):
    """Distil-Whisper STT engine using HuggingFace transformers pipeline."""

    name = "distilwhisper"
    category = "stt"

    def __init__(self):
        self.pipe = None
        self.current_model_id = None

    def initialize(self) -> bool:
        try:
            from transformers import AutoModelForSpeechSeq2Seq  # noqa: F401

            self._ensure_ffmpeg()
            logger.info("Distil-Whisper ready (model loaded on first request)")
            return True
        except Exception as e:
            logger.error("Distil-Whisper init failed: %s", e)
            return False

    @staticmethod
    def _ensure_ffmpeg():
        """Ensure ffmpeg is available on PATH. Tries imageio_ffmpeg as fallback."""
        import shutil

        if shutil.which("ffmpeg"):
            return
        try:
            import imageio_ffmpeg
            ffmpeg_exe = imageio_ffmpeg.get_ffmpeg_exe()
            if ffmpeg_exe and os.path.isfile(ffmpeg_exe):
                ffmpeg_dir = os.path.dirname(ffmpeg_exe)
                if os.name == "nt":
                    target = os.path.join(ffmpeg_dir, "ffmpeg.exe")
                else:
                    target = os.path.join(ffmpeg_dir, "ffmpeg")
                if not os.path.exists(target):
                    import shutil as sh
                    sh.copy2(ffmpeg_exe, target)
                    logger.info("Created ffmpeg copy: %s -> %s", ffmpeg_exe, target)
                os.environ["PATH"] = ffmpeg_dir + os.pathsep + os.environ.get("PATH", "")
                logger.info("Added ffmpeg to PATH: %s", ffmpeg_dir)
                return
        except (ImportError, Exception) as e:
            logger.debug("imageio_ffmpeg fallback failed: %s", e)
        logger.warning(
            "ffmpeg not found on PATH. Distil-Whisper requires ffmpeg for audio loading. "
            "Install ffmpeg (https://ffmpeg.org/download.html) and add it to your system PATH, "
            "or install imageio-ffmpeg: pip install imageio-ffmpeg"
        )

    def _ensure_loaded(self, model_id: str = "distil-whisper/distil-large-v3"):
        if self.pipe is not None and self.current_model_id == model_id:
            return

        import torch
        from transformers import (
            AutoModelForSpeechSeq2Seq,
            AutoProcessor,
            pipeline,
        )

        device = "cuda:0" if self.has_cuda() else "cpu"
        torch_dtype = torch.float16 if self.has_cuda() else torch.float32

        local_path = self.ensure_model_local(model_id, "stt")
        model = AutoModelForSpeechSeq2Seq.from_pretrained(
            local_path,
            torch_dtype=torch_dtype,
            low_cpu_mem_usage=True,
            use_safetensors=True,
        )
        model.to(device)

        processor = AutoProcessor.from_pretrained(local_path)

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

        self.current_model_id = model_id
        logger.info("Distil-Whisper model loaded: %s on %s", model_id, device)

    def process(self, **kwargs) -> dict:
        audio_data = kwargs.get("audio_data", "")
        language = kwargs.get("language", "en-US")
        model_name = kwargs.get("model_name", "distil-whisper/distil-large-v3")

        if not audio_data:
            return {"success": False, "error": "No audio data provided"}

        try:
            self._ensure_loaded(model_name)

            import io
            import soundfile as sf

            audio_bytes = base64.b64decode(audio_data)
            audio_array, sample_rate = sf.read(io.BytesIO(audio_bytes), dtype="float32")

            # Ensure mono
            if audio_array.ndim > 1:
                audio_array = audio_array.mean(axis=1)

            lang_code = language.split("-")[0] if "-" in language else language

            # Pass numpy array + sampling_rate dict to bypass ffmpeg/torchcodec
            result = self.pipe(
                {"array": audio_array, "sampling_rate": sample_rate},
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
                    "model": model_name,
                    "language": lang_code,
                },
            }
        except Exception as e:
            logger.error("Distil-Whisper process failed: %s", e)
            return {"success": False, "error": str(e)}

    def cleanup(self):
        self.pipe = None
        self.current_model_id = None
