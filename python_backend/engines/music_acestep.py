#!/usr/bin/env python3
"""ACE-Step engine — full-song music generation with lyrics alignment.

Uses ACEStepPipeline from ace-step v0.2.0+. Supports multiple DiT model
variants, task types (text2music, cover, repaint, edit, extend, retake),
and configurable generation parameters.

Requires: ace-step (git+https://github.com/ace-step/ACE-Step.git)
"""

import base64
import logging
import os
import shutil
import tempfile

import numpy as np

from .base_engine import BaseAudioEngine

logger = logging.getLogger("Music.ACEStep")

# Map SwarmUI model config names to HuggingFace repo IDs
_DIT_REPO_MAP = {
    "acestep-v15-turbo": "ACE-Step/ACE-Step-v1-3.5B",
    "acestep-v15-turbo-shift1": "ACE-Step/ACE-Step-v1-3.5B",
    "acestep-v15-turbo-shift3": "ACE-Step/ACE-Step-v1-3.5B",
    "acestep-v15-turbo-continuous": "ACE-Step/ACE-Step-v1-3.5B",
    "acestep-v15-sft": "ACE-Step/ACE-Step-v1-3.5B",
    "acestep-v15-base": "ACE-Step/ACE-Step-v1-3.5B",
}

# Map scheduler types to ACE-Step scheduler names
_SCHEDULER_MAP = {
    "ode": "euler",
    "euler": "euler",
    "heun": "heun",
    "pingpong": "pingpong",
}


class AceStepEngine(BaseAudioEngine):
    """ACE-Step music generation engine with lazy model loading."""

    name = "acestep"
    category = "audiogeneration"

    def __init__(self):
        self.pipeline = None
        self.current_dit_model = None
        self.sample_rate = 48000

    def initialize(self) -> bool:
        try:
            from acestep.pipeline_ace_step import ACEStepPipeline  # noqa: F401
            logger.info("ACE-Step ready (model loaded on first request)")
            return True
        except ImportError as e:
            logger.error("ace-step package not found: %s", e)
            return False

    def _ensure_loaded(self, dit_model: str):
        """Load or swap the pipeline if needed."""
        if self.pipeline is not None and self.current_dit_model == dit_model:
            return

        if self.pipeline is not None:
            logger.info("Switching DiT model: %s -> %s", self.current_dit_model, dit_model)
            self.cleanup()

        import torch
        from acestep.pipeline_ace_step import ACEStepPipeline

        device_id = 0 if torch.cuda.is_available() else -1
        self.pipeline = ACEStepPipeline(device_id=device_id)
        self.pipeline.load_checkpoint()
        self.current_dit_model = dit_model
        logger.info("ACE-Step loaded: %s on %s", dit_model,
                     "cuda" if torch.cuda.is_available() else "cpu")

    def _decode_audio_to_tempfile(self, audio_b64: str, prefix: str = "ace_") -> str:
        """Decode base64 audio data and write to a temporary WAV file."""
        audio_bytes = base64.b64decode(audio_b64)
        fd, path = tempfile.mkstemp(prefix=prefix, suffix=".wav")
        try:
            os.write(fd, audio_bytes)
        finally:
            os.close(fd)
        return path

    def process(self, **kwargs) -> dict:
        prompt = kwargs.get("prompt", "")
        lyrics = kwargs.get("lyrics", "[Instrumental]")
        duration = float(kwargs.get("duration", 30))
        seed = int(kwargs.get("seed", -1))
        dit_model = kwargs.get("dit_model", "acestep-v15-turbo")

        infer_step = int(kwargs.get("infer_step", 8))
        guidance_scale = float(kwargs.get("guidance_scale", 7.0))
        instrumental = kwargs.get("instrumental", "false") == "true"
        bpm = int(kwargs.get("bpm", 120))
        key_scale = kwargs.get("key_scale", "")
        time_signature = kwargs.get("time_signature", "4")
        vocal_language = kwargs.get("vocal_language", "en")
        shift = float(kwargs.get("shift", 3.0))
        infer_method = kwargs.get("infer_method", "ode")
        use_adg = kwargs.get("use_adg", "false") == "true"
        cfg_interval_start = float(kwargs.get("cfg_interval_start", 0.0))
        cfg_interval_end = float(kwargs.get("cfg_interval_end", 1.0))
        enable_normalization = kwargs.get("enable_normalization", "true") == "true"
        normalization_db = float(kwargs.get("normalization_db", -14.0))
        task_type = kwargs.get("task_type", "text2music")

        if not prompt.strip():
            return {"success": False, "error": "No prompt provided"}

        valid_tasks = {"text2music", "cover", "repaint", "edit", "extend", "retake"}
        if task_type not in valid_tasks:
            return {"success": False, "error": f"Invalid task_type '{task_type}'. Must be one of: {', '.join(sorted(valid_tasks))}"}

        temp_files = []
        save_dir = None
        try:
            import torchaudio

            self._ensure_loaded(dit_model)

            # Build lyrics with metadata tags
            if instrumental:
                lyrics = "[Instrumental]"

            full_prompt = prompt
            tags = []
            if bpm > 0:
                tags.append(f"bpm: {bpm}")
            if time_signature:
                tags.append(f"time_signature: {time_signature}/4")
            if key_scale:
                tags.append(f"key: {key_scale}")
            if tags:
                full_prompt = f"{prompt} [{', '.join(tags)}]"

            # Map scheduler
            scheduler_type = _SCHEDULER_MAP.get(infer_method, "euler")

            # Build __call__ kwargs
            gen_kwargs = {
                "prompt": full_prompt,
                "lyrics": lyrics,
                "task": task_type,
                "audio_duration": duration,
                "infer_step": infer_step,
                "guidance_scale": guidance_scale,
                "scheduler_type": scheduler_type,
                "cfg_type": "apg" if use_adg else "cfg",
                "guidance_interval": cfg_interval_end - cfg_interval_start,
                "batch_size": 1,
            }

            if seed >= 0:
                gen_kwargs["manual_seeds"] = [seed]

            # Source audio for cover/repaint/edit/extend tasks
            src_audio_b64 = kwargs.get("src_audio", "")
            if src_audio_b64:
                src_path = self._decode_audio_to_tempfile(src_audio_b64, "ace_src_")
                temp_files.append(src_path)
                gen_kwargs["src_audio_path"] = src_path

            # Reference audio for audio2audio
            ref_audio_b64 = kwargs.get("reference_audio", "")
            if ref_audio_b64:
                ref_path = self._decode_audio_to_tempfile(ref_audio_b64, "ace_ref_")
                temp_files.append(ref_path)
                gen_kwargs["ref_audio_input"] = ref_path
                gen_kwargs["audio2audio_enable"] = True
                gen_kwargs["ref_audio_strength"] = float(kwargs.get("cover_strength", 0.5))

            # Task-specific params
            if task_type == "repaint":
                gen_kwargs["repaint_start"] = int(float(kwargs.get("repaint_start", 0.0)))
                repaint_end = float(kwargs.get("repaint_end", -1.0))
                if repaint_end >= 0:
                    gen_kwargs["repaint_end"] = int(repaint_end)

            if task_type == "cover":
                gen_kwargs["ref_audio_strength"] = float(kwargs.get("cover_strength", 1.0))

            # Output directory
            save_dir = tempfile.mkdtemp(prefix="acestep_")
            gen_kwargs["save_path"] = save_dir

            # Generate
            result = self.pipeline(**gen_kwargs)

            # Extract audio — result is [output_path, ..., params_json_dict]
            audio_path = None
            if isinstance(result, (list, tuple)):
                for item in result:
                    if isinstance(item, str) and os.path.isfile(item):
                        audio_path = item
                        break

            # Fallback: scan save_dir
            if audio_path is None and save_dir:
                for f in sorted(os.listdir(save_dir)):
                    if f.endswith((".wav", ".mp3", ".flac")):
                        audio_path = os.path.join(save_dir, f)
                        break

            if audio_path is None:
                return {"success": False, "error": "ACE-Step produced no output audio"}

            waveform, sr = torchaudio.load(audio_path)
            audio_numpy = waveform.cpu().numpy().astype(np.float32)
            self.sample_rate = sr

            # Preserve stereo: interleave [channels, samples] → [samples * channels]
            num_channels = 1
            if len(audio_numpy.shape) > 1 and audio_numpy.shape[0] >= 2:
                num_channels = audio_numpy.shape[0]
                audio_numpy = audio_numpy.T.flatten()  # [C, N] → [N, C] → [N*C]
            elif len(audio_numpy.shape) > 1:
                audio_numpy = audio_numpy.squeeze(0)

            output_format = kwargs.get("output_format", "wav_16")
            output_quality = kwargs.get("output_quality", "high")
            audio_b64, fmt = self.encode_audio(audio_numpy, self.sample_rate, num_channels=num_channels, output_format=output_format, quality=output_quality)
            actual_duration = len(audio_numpy) / (self.sample_rate * num_channels)

            return {
                "success": True,
                "audio_data": audio_b64,
                "output_format": fmt,
                "duration": actual_duration,
                "metadata": {
                    "engine": "acestep",
                    "dit_model": dit_model,
                    "task_type": task_type,
                    "sample_rate": self.sample_rate,
                    "prompt": prompt,
                    "has_lyrics": not instrumental and lyrics != "[Instrumental]",
                    "seed": seed,
                },
            }
        except Exception as e:
            logger.error("ACE-Step process failed: %s", e, exc_info=True)
            return {"success": False, "error": str(e)}
        finally:
            for f in temp_files:
                try:
                    if os.path.exists(f):
                        os.remove(f)
                except OSError:
                    pass
            if save_dir and os.path.exists(save_dir):
                try:
                    shutil.rmtree(save_dir)
                except OSError:
                    pass

    def cleanup(self):
        if self.pipeline is not None:
            try:
                import torch
                self.pipeline.cleanup_memory()
                del self.pipeline
                self.pipeline = None
                self.current_dit_model = None
                if torch.cuda.is_available():
                    torch.cuda.empty_cache()
            except Exception:
                self.pipeline = None
                self.current_dit_model = None
