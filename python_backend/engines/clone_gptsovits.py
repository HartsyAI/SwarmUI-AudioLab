#!/usr/bin/env python3
"""GPT-SoVITS engine — text-to-speech with voice cloning (text in → audio out).

Unlike RVC and OpenVoice which convert existing audio, GPT-SoVITS
generates NEW speech from text in a cloned voice.  Requires ~1 min
of reference audio to clone the voice.
"""

import base64
import logging
import os
import tempfile
import numpy as np

from .base_engine import BaseAudioEngine

logger = logging.getLogger("Clone.GPTSoVITS")


class GPTSoVITSEngine(BaseAudioEngine):
    """GPT-SoVITS TTS engine — generates new speech from text in a cloned voice."""

    name = "gptsovits"
    category = "voiceclone"

    def __init__(self):
        self.tts = None
        self.sample_rate = 32000

    def initialize(self) -> bool:
        try:
            # Try importing the library to verify availability
            import importlib
            gpt_sovits = importlib.import_module("GPT_SoVITS")  # noqa: F841
            logger.info("GPT-SoVITS ready")
            return True
        except Exception as e:
            logger.error("GPT-SoVITS init failed: %s", e)
            return False

    def _ensure_loaded(self):
        """Load the TTS pipeline on first use."""
        if self.tts is not None:
            return

        from GPT_SoVITS.TTS_infer_pack.TTS import TTS, TTS_Config

        base_dir = os.path.dirname(os.path.dirname(__file__))
        models_dir = os.path.join(base_dir, "models", "gptsovits")

        config_dict = {
            "device": "cuda" if self.has_cuda() else "cpu",
            "is_half": self.has_cuda(),
        }

        # Check for pretrained model files
        t2s_path = os.path.join(models_dir, "s1bert25hz-2kh-longer-epoch=68e-step=50232.ckpt")
        vits_path = os.path.join(models_dir, "s2G488k.pth")
        if os.path.exists(t2s_path):
            config_dict["t2s_weights_path"] = t2s_path
        if os.path.exists(vits_path):
            config_dict["vits_weights_path"] = vits_path

        config = TTS_Config(config_dict)
        self.tts = TTS(config)
        logger.info("GPT-SoVITS pipeline loaded")

    def process(self, **kwargs) -> dict:
        text = kwargs.get("text", "")
        source_audio = kwargs.get("source_audio", "")
        prompt_text = kwargs.get("prompt_text", "")
        language = kwargs.get("language", "en")

        if not text.strip():
            return {"success": False, "error": "No text provided"}
        if not source_audio:
            return {"success": False, "error": "No reference audio provided"}

        tmp_files = []
        try:
            self._ensure_loaded()

            # Write reference audio to temp file
            ref_path = self._write_temp(source_audio, ".wav")
            tmp_files.append(ref_path)

            params = {
                "text": text,
                "text_lang": language,
                "ref_audio_path": ref_path,
                "prompt_text": prompt_text,
                "prompt_lang": language,
            }

            generator = self.tts.run(params)
            sr, audio_data = next(generator)
            self.sample_rate = sr

            # Ensure float32
            if audio_data.dtype != np.float32:
                audio_data = audio_data.astype(np.float32)
                if np.max(np.abs(audio_data)) > 1.0:
                    audio_data = audio_data / 32768.0

            audio_b64 = self.audio_to_base64(audio_data, sr)
            duration = len(audio_data) / sr

            return {
                "success": True,
                "audio_data": audio_b64,
                "duration": duration,
                "metadata": {
                    "engine": "gptsovits",
                    "sample_rate": sr,
                    "language": language,
                },
            }
        except Exception as e:
            logger.error("GPT-SoVITS process failed: %s", e)
            return {"success": False, "error": str(e)}
        finally:
            for f in tmp_files:
                if f and os.path.exists(f):
                    try:
                        os.unlink(f)
                    except OSError:
                        pass

    @staticmethod
    def _write_temp(data_b64: str, suffix: str) -> str:
        data_bytes = base64.b64decode(data_b64)
        with tempfile.NamedTemporaryFile(suffix=suffix, delete=False) as tmp:
            tmp.write(data_bytes)
            return tmp.name

    def cleanup(self):
        self.tts = None
