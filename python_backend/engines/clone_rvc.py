#!/usr/bin/env python3
"""RVC engine — retrieval-based voice conversion."""

import base64
import logging
import os
import tempfile

from .base_engine import BaseAudioEngine

logger = logging.getLogger("Clone.RVC")


class RVCEngine(BaseAudioEngine):
    """RVC V2 voice conversion engine."""

    name = "rvc"
    category = "voiceclone"

    def __init__(self):
        self.rvc = None
        self.device = None

    def initialize(self) -> bool:
        try:
            from rvc_python.infer import RVCInference  # noqa: F401

            self.device = "cuda:0" if self.has_cuda() else "cpu"
            logger.info("RVC ready on %s", self.device)
            return True
        except Exception as e:
            logger.error("RVC init failed: %s", e)
            return False

    def process(self, **kwargs) -> dict:
        source_audio = kwargs.get("source_audio", "")
        target_voice = kwargs.get("target_voice", "")
        pitch_shift = int(kwargs.get("pitch_shift", 0))

        if not source_audio:
            return {"success": False, "error": "No source audio provided"}
        if not target_voice:
            return {"success": False, "error": "No target voice model provided"}

        tmp_files = []
        try:
            from rvc_python.infer import RVCInference

            rvc = RVCInference(device=self.device)

            # Target voice can be a base64-encoded model file or a model path
            model_path = self._write_temp(target_voice, ".pth")
            tmp_files.append(model_path)

            rvc.load_model(model_path)

            src_path = self._write_temp(source_audio, ".wav")
            tmp_files.append(src_path)
            out_path = tempfile.mktemp(suffix=".wav")
            tmp_files.append(out_path)

            rvc.infer_file(
                src_path,
                out_path,
                f0method="rmvpe",
                f0up_key=pitch_shift,
            )

            # Read output
            import soundfile as sf
            audio_data, sr = sf.read(out_path, dtype="float32")
            audio_b64 = self.audio_to_base64(audio_data, sr)
            duration = len(audio_data) / sr

            return {
                "success": True,
                "audio_data": audio_b64,
                "duration": duration,
                "metadata": {
                    "engine": "rvc",
                    "sample_rate": sr,
                    "pitch_shift": pitch_shift,
                },
            }
        except Exception as e:
            logger.error("RVC process failed: %s", e)
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
        self.rvc = None
