#!/usr/bin/env python3
"""ACE-Step 1.5 engine — full-song music generation with lyrics alignment.

Supports 6 DiT model variants, 6 task types (text2music, cover, repaint,
extract, lego, complete), and optional LM planner (stubbed for future
integration with SwarmUI's AbstractLLMBackend).

Requires: ace-step >= 1.5 (pip install ace-step)
"""

import base64
import logging
import os
import shutil
import tempfile

import numpy as np

from .base_engine import BaseAudioEngine

logger = logging.getLogger("Music.ACEStep")


class AceStepEngine(BaseAudioEngine):
    """ACE-Step 1.5 music generation engine with lazy model loading."""

    name = "acestep"
    category = "musicgen"

    def __init__(self):
        self.handler = None
        self.current_dit_model = None
        self.sample_rate = 48000

    def initialize(self) -> bool:
        try:
            from acestep.handler import AceStepHandler  # noqa: F401
            logger.info("ACE-Step 1.5 ready (model loaded on first request)")
            return True
        except ImportError:
            logger.error(
                "ace-step package not found. Install with: pip install ace-step"
            )
            return False

    def _ensure_loaded(self, dit_model: str):
        """Load or swap the DiT model if needed."""
        if self.handler is not None and self.current_dit_model == dit_model:
            return

        # Unload previous model if switching
        if self.handler is not None:
            logger.info("Switching DiT model: %s -> %s", self.current_dit_model, dit_model)
            self.cleanup()

        import torch
        from acestep.handler import AceStepHandler

        device = "cuda" if torch.cuda.is_available() else "cpu"
        self.handler = AceStepHandler(dit_model=dit_model, device=device)
        self.current_dit_model = dit_model
        logger.info("ACE-Step 1.5 loaded: %s on %s", dit_model, device)

    def _decode_audio_to_tempfile(self, audio_b64: str, prefix: str = "ace_") -> str:
        """Decode base64 audio data and write to a temporary WAV file.
        Returns the path to the temp file."""
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

        # Core DiT params
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

        # Task params
        task_type = kwargs.get("task_type", "text2music")

        # LM params (stubbed — wired through but not processed)
        # TODO: Integrate with SwarmUI's AbstractLLMBackend when LLMAPI.cs is complete.
        # The LM planner (Qwen3-based, 0.6B/1.7B/4B) generates structured music metadata
        # (tags, caption, lyrics) from a text prompt. Currently these params are accepted
        # but llm_handler=None is passed to generate_music().
        lm_model = kwargs.get("lm_model", "none")
        # lm_temperature = float(kwargs.get("lm_temperature", 0.85))
        # lm_cfg_scale = float(kwargs.get("lm_cfg_scale", 2.0))
        # lm_top_k = int(kwargs.get("lm_top_k", 0))
        # lm_top_p = float(kwargs.get("lm_top_p", 0.9))
        # thinking = kwargs.get("thinking", "true") == "true"
        # lm_negative_prompt = kwargs.get("lm_negative_prompt", "")
        # use_cot_metas = kwargs.get("use_cot_metas", "true") == "true"
        # use_cot_caption = kwargs.get("use_cot_caption", "true") == "true"
        # use_cot_language = kwargs.get("use_cot_language", "true") == "true"

        if not prompt.strip():
            return {"success": False, "error": "No prompt provided"}

        # Validate task type
        valid_tasks = {"text2music", "cover", "repaint", "extract", "lego", "complete"}
        if task_type not in valid_tasks:
            return {"success": False, "error": f"Invalid task_type '{task_type}'. Must be one of: {', '.join(sorted(valid_tasks))}"}

        temp_files = []
        save_dir = None
        try:
            import torch
            import torchaudio

            self._ensure_loaded(dit_model)

            # Build generation kwargs
            gen_kwargs = {
                "prompt": prompt,
                "lyrics": lyrics,
                "task_type": task_type,
                "audio_duration": duration,
                "infer_step": infer_step,
                "guidance_scale": guidance_scale,
                "instrumental": instrumental,
                "bpm": bpm,
                "time_signature": time_signature,
                "vocal_language": vocal_language,
                "shift": shift,
                "infer_method": infer_method,
                "use_adg": use_adg,
                "cfg_interval_start": cfg_interval_start,
                "cfg_interval_end": cfg_interval_end,
                "enable_normalization": enable_normalization,
                "normalization_db": normalization_db,
            }

            # Only pass key_scale if explicitly set
            if key_scale:
                gen_kwargs["key_scale"] = key_scale

            # Seed handling
            if seed >= 0:
                gen_kwargs["manual_seeds"] = [seed]

            # Handle source audio for cover/repaint/extract/lego/complete tasks
            src_audio_b64 = kwargs.get("src_audio", "")
            if src_audio_b64:
                src_path = self._decode_audio_to_tempfile(src_audio_b64, "ace_src_")
                temp_files.append(src_path)
                gen_kwargs["src_audio_path"] = src_path

            # Handle reference audio (style/timbre reference)
            ref_audio_b64 = kwargs.get("reference_audio", "")
            if ref_audio_b64:
                ref_path = self._decode_audio_to_tempfile(ref_audio_b64, "ace_ref_")
                temp_files.append(ref_path)
                gen_kwargs["reference_audio_path"] = ref_path

            # Task-specific params
            if task_type in ("repaint",):
                gen_kwargs["repaint_start"] = float(kwargs.get("repaint_start", 0.0))
                repaint_end = float(kwargs.get("repaint_end", -1.0))
                if repaint_end >= 0:
                    gen_kwargs["repaint_end"] = repaint_end

            if task_type in ("cover",):
                gen_kwargs["cover_strength"] = float(kwargs.get("cover_strength", 1.0))
                gen_kwargs["cover_noise_strength"] = float(kwargs.get("cover_noise_strength", 0.0))

            # TODO: When SwarmUI LLM integration is complete, pass llm_handler here
            # instead of None. The LM planner would pre-process the prompt to generate
            # structured metadata (tags, caption, lyrics) before DiT generation.
            if lm_model != "none":
                logger.warning(
                    "LM model '%s' selected but LM integration is not yet available. "
                    "Proceeding without LM planner. See TODO in AceStepEngine.",
                    lm_model,
                )

            # Create temp dir for output files
            save_dir = tempfile.mkdtemp(prefix="acestep_")
            gen_kwargs["save_path"] = save_dir

            # Generate music via the handler
            result = self.handler.generate_music(**gen_kwargs)

            # Extract audio from result
            # v1.5 API returns a result object — try multiple access patterns
            audio_tensor = None
            audio_path = None

            if result is None:
                return {"success": False, "error": "ACE-Step returned no output"}

            # Pattern 1: Result is a list [output_path, ..., params_dict] (v1 compat)
            if isinstance(result, (list, tuple)) and len(result) >= 1:
                first = result[0]
                if isinstance(first, str) and os.path.exists(first):
                    audio_path = first
                elif hasattr(first, "cpu"):  # torch.Tensor
                    audio_tensor = first

            # Pattern 2: Result has .audios attribute (v1.5 GenerationResult)
            elif hasattr(result, "audios") and result.audios:
                audio_item = result.audios[0]
                if isinstance(audio_item, dict):
                    if "tensor" in audio_item:
                        audio_tensor = audio_item["tensor"]
                    elif "path" in audio_item and os.path.exists(audio_item["path"]):
                        audio_path = audio_item["path"]
                elif hasattr(audio_item, "cpu"):  # torch.Tensor directly
                    audio_tensor = audio_item

            # Pattern 3: Result is a dict with audio_data or path
            elif isinstance(result, dict):
                if "audio" in result and hasattr(result["audio"], "cpu"):
                    audio_tensor = result["audio"]
                elif "path" in result and os.path.exists(result["path"]):
                    audio_path = result["path"]

            # Pattern 4: Check save_dir for output files
            if audio_tensor is None and audio_path is None:
                for f in sorted(os.listdir(save_dir)):
                    if f.endswith((".wav", ".mp3", ".flac")):
                        audio_path = os.path.join(save_dir, f)
                        break

            if audio_tensor is None and audio_path is None:
                return {"success": False, "error": "Could not extract audio from ACE-Step result"}

            # Load audio to numpy
            if audio_tensor is not None:
                if hasattr(audio_tensor, "cpu"):
                    audio_numpy = audio_tensor.cpu().numpy().astype(np.float32)
                else:
                    audio_numpy = np.array(audio_tensor, dtype=np.float32)
                sr = self.sample_rate
            else:
                waveform, sr = torchaudio.load(audio_path)
                audio_numpy = waveform.cpu().numpy().astype(np.float32)
                self.sample_rate = sr

            # Mix to mono if stereo [channels, samples]
            if len(audio_numpy.shape) > 1:
                audio_numpy = np.mean(audio_numpy, axis=0)

            audio_b64 = self.audio_to_base64(audio_numpy, self.sample_rate)
            actual_duration = len(audio_numpy) / self.sample_rate

            return {
                "success": True,
                "audio_data": audio_b64,
                "duration": actual_duration,
                "metadata": {
                    "engine": "acestep",
                    "version": "1.5",
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
            # Cleanup temp files
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
        if self.handler is not None:
            # Release GPU memory
            try:
                import torch
                del self.handler
                self.handler = None
                self.current_dit_model = None
                if torch.cuda.is_available():
                    torch.cuda.empty_cache()
            except Exception:
                self.handler = None
                self.current_dit_model = None
