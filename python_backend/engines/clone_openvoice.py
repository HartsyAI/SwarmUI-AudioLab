#!/usr/bin/env python3
"""OpenVoice V2 engine — zero-shot voice tone cloning."""

import base64
import logging
import os
import tempfile
import numpy as np

from .base_engine import BaseAudioEngine

logger = logging.getLogger("Clone.OpenVoice")


class OpenVoiceEngine(BaseAudioEngine):
    """OpenVoice V2 voice cloning engine."""

    name = "openvoice"
    category = "voiceclone"

    def __init__(self):
        self.converter = None
        self.device = None
        self.ckpt_dir = None

    def initialize(self) -> bool:
        try:
            from openvoice.api import ToneColorConverter  # noqa: F401
            from openvoice import se_extractor  # noqa: F401

            self.device = "cuda:0" if self.has_cuda() else "cpu"

            # Checkpoint dir alongside the engines folder
            base_dir = os.path.dirname(os.path.dirname(__file__))
            self.ckpt_dir = os.path.join(base_dir, "models", "openvoice",
                                         "checkpoints_v2", "converter")

            if os.path.exists(os.path.join(self.ckpt_dir, "config.json")):
                self.converter = ToneColorConverter(
                    os.path.join(self.ckpt_dir, "config.json"),
                    device=self.device,
                )
                self.converter.load_ckpt(
                    os.path.join(self.ckpt_dir, "checkpoint.pth")
                )
                logger.info("OpenVoice converter loaded from %s", self.ckpt_dir)
            else:
                logger.info("OpenVoice ready (checkpoints will be loaded on use)")

            return True
        except Exception as e:
            logger.error("OpenVoice init failed: %s", e)
            return False

    def process(self, **kwargs) -> dict:
        source_audio = kwargs.get("source_audio", "")
        target_voice = kwargs.get("target_voice", "")

        if not source_audio:
            return {"success": False, "error": "No source audio provided"}
        if not target_voice:
            return {"success": False, "error": "No target voice reference provided"}

        tmp_files = []
        try:
            from openvoice.api import ToneColorConverter
            from openvoice import se_extractor

            if self.converter is None:
                return {"success": False,
                        "error": "OpenVoice checkpoints not found. "
                                 "Download checkpoints_v2 to models/openvoice/"}

            # Write source and target to temp files
            src_path = self._write_temp(source_audio, ".wav")
            tmp_files.append(src_path)
            ref_path = self._write_temp(target_voice, ".wav")
            tmp_files.append(ref_path)

            out_path = tempfile.mktemp(suffix=".wav")
            tmp_files.append(out_path)

            # Extract speaker embedding from target reference
            target_se, _ = se_extractor.get_se(
                ref_path, self.converter, vad=True
            )

            # Extract source speaker embedding
            source_se, _ = se_extractor.get_se(
                src_path, self.converter, vad=True
            )

            # Apply tone conversion
            self.converter.convert(
                audio_src_path=src_path,
                src_se=source_se,
                tgt_se=target_se,
                output_path=out_path,
            )

            # Read output and encode
            import soundfile as sf
            audio_data, sr = sf.read(out_path, dtype="float32")
            audio_b64 = self.audio_to_base64(audio_data, sr)
            duration = len(audio_data) / sr

            return {
                "success": True,
                "audio_data": audio_b64,
                "duration": duration,
                "metadata": {
                    "engine": "openvoice",
                    "sample_rate": sr,
                },
            }
        except Exception as e:
            logger.error("OpenVoice process failed: %s", e)
            return {"success": False, "error": str(e)}
        finally:
            for f in tmp_files:
                if f and os.path.exists(f):
                    try:
                        os.unlink(f)
                    except OSError:
                        pass

    @staticmethod
    def _write_temp(audio_b64: str, suffix: str) -> str:
        audio_bytes = base64.b64decode(audio_b64)
        with tempfile.NamedTemporaryFile(suffix=suffix, delete=False) as tmp:
            tmp.write(audio_bytes)
            return tmp.name

    def cleanup(self):
        self.converter = None
