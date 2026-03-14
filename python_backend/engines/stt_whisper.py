#!/usr/bin/env python3
"""Whisper STT engine — extracted from stt_engines.py."""

import base64
import logging
import os
import tempfile

from .base_engine import BaseAudioEngine

logger = logging.getLogger("STT.Whisper")


class WhisperEngine(BaseAudioEngine):
    """OpenAI Whisper speech-to-text engine."""

    name = "whisper"
    category = "stt"

    def __init__(self):
        self.model = None
        self.current_model_name = None

    def initialize(self) -> bool:
        try:
            import whisper  # noqa: F401

            self._ensure_ffmpeg()
            logger.info("Whisper ready (model loaded on first request)")
            return True
        except Exception as e:
            logger.error("Whisper init failed: %s", e)
            return False

    @staticmethod
    def _ensure_ffmpeg():
        """Ensure ffmpeg is available on PATH. Tries imageio_ffmpeg as fallback."""
        import shutil

        if shutil.which("ffmpeg"):
            return
        # Try imageio_ffmpeg (bundled binary with non-standard name)
        try:
            import imageio_ffmpeg
            ffmpeg_exe = imageio_ffmpeg.get_ffmpeg_exe()
            if ffmpeg_exe and os.path.isfile(ffmpeg_exe):
                ffmpeg_dir = os.path.dirname(ffmpeg_exe)
                # imageio_ffmpeg names the binary like 'ffmpeg-win-x86_64-v7.1.exe'
                # Whisper calls 'ffmpeg' directly, so create a copy with the expected name
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
            "ffmpeg not found on PATH. Whisper requires ffmpeg for audio loading. "
            "Install ffmpeg (https://ffmpeg.org/download.html) and add it to your system PATH, "
            "or install imageio-ffmpeg: pip install imageio-ffmpeg"
        )

    def _ensure_loaded(self, model_name: str = "base"):
        if self.model is not None and self.current_model_name == model_name:
            return

        import whisper

        model_dir = self.get_model_dir("stt") or None
        self.model = whisper.load_model(model_name, download_root=model_dir)
        self.current_model_name = model_name
        logger.info("Whisper model loaded: %s", model_name)

    def process(self, **kwargs) -> dict:
        audio_data = kwargs.get("audio_data", "")
        language = kwargs.get("language", "en-US")
        model_name = kwargs.get("model_name", "base")
        task = kwargs.get("task", "transcribe")

        if not audio_data:
            return {"success": False, "error": "No audio data provided"}

        audio_bytes = base64.b64decode(audio_data)

        with tempfile.NamedTemporaryFile(suffix=".wav", delete=False) as tmp:
            tmp.write(audio_bytes)
            temp_path = tmp.name

        try:
            self._ensure_loaded(model_name)
            lang_code = language.split("-")[0] if "-" in language else language
            # Pass language=None for auto-detection
            transcribe_kwargs = {"task": task}
            if lang_code and lang_code != "auto":
                transcribe_kwargs["language"] = lang_code
            result = self.model.transcribe(temp_path, **transcribe_kwargs)
            # Compute confidence from segment log probabilities
            segments = result.get("segments", [])
            if segments:
                import math
                avg_logprob = sum(s.get("avg_logprob", -1.0) for s in segments) / len(segments)
                confidence = round(min(1.0, max(0.0, math.exp(avg_logprob))), 4)
            else:
                confidence = 0.0
            detected_lang = result.get("language", lang_code)
            return {
                "success": True,
                "text": result["text"],
                "confidence": confidence,
                "metadata": {
                    "engine": "whisper",
                    "model": model_name,
                    "language": detected_lang,
                    "task": task,
                },
            }
        except Exception as e:
            logger.error("Whisper process failed: %s", e)
            return {"success": False, "error": str(e)}
        finally:
            if os.path.exists(temp_path):
                os.unlink(temp_path)

    def cleanup(self):
        self.model = None
        self.current_model_name = None
