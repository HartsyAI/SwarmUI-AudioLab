#!/usr/bin/env python3
"""RVC engine — voice conversion (audio in → audio out).

Takes existing audio + a trained voice model and re-voices it.
Does NOT generate new speech from text — only converts the voice
in existing audio to a different voice.
"""

import base64
import logging
import os
import tempfile

from .base_engine import BaseAudioEngine

logger = logging.getLogger("Clone.RVC")


class RVCEngine(BaseAudioEngine):
    """RVC V2 voice conversion engine (audio in → audio out, no text generation)."""

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
        f0method = kwargs.get("f0method", "rmvpe")
        index_rate = float(kwargs.get("index_rate", 0.5))
        rms_mix_rate = float(kwargs.get("rms_mix_rate", 1.0))
        protect = float(kwargs.get("protect", 0.33))

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

            # Configure inference params via set_params() (not inline kwargs)
            rvc.set_params(
                f0method=f0method,
                f0up_key=pitch_shift,
                index_rate=index_rate,
                rms_mix_rate=rms_mix_rate,
                protect=protect,
            )

            src_path = self._write_temp(source_audio, ".wav")
            tmp_files.append(src_path)
            out_path = tempfile.mktemp(suffix=".wav")
            tmp_files.append(out_path)

            rvc.infer_file(src_path, out_path)

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
                    "f0method": f0method,
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
