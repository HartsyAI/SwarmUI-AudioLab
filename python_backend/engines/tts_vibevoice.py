#!/usr/bin/env python3
"""VibeVoice TTS engine — Microsoft's multi-speaker speech synthesis.

Two model families with different architectures:
  - Streaming (0.5B Realtime): VibeVoiceStreamingForConditionalGenerationInference
    Uses manual prefill (forward_lm + forward_tts_lm) for low-latency generation.
  - Standard (1.5B, 7B): VibeVoiceForConditionalGenerationInference
    Uses is_prefill=True in generate() — no manual prefill needed.

Both output 24kHz mono audio via a diffusion head + DAC codec.
"""

import base64
import io
import logging

import numpy as np
import soundfile as sf

from .base_engine import BaseAudioEngine

logger = logging.getLogger("TTS.VibeVoice")


class VibeVoiceEngine(BaseAudioEngine):
    """VibeVoice TTS engine from Microsoft."""

    name = "vibevoice"
    category = "tts"

    def __init__(self):
        self.model = None
        self.processor = None
        self.sample_rate = 24000
        self.device = None
        self._current_model_name = None
        self._is_streaming = False

    def initialize(self) -> bool:
        try:
            from vibevoice.processor.vibevoice_processor import (  # noqa: F401
                VibeVoiceProcessor,
            )

            self.device = "cuda" if self.has_cuda() else "cpu"
            logger.info("VibeVoice ready on %s", self.device)
            return True
        except Exception as e:
            logger.error("VibeVoice init failed: %s", e)
            return False

    def _ensure_loaded(self, model_name: str = "vibevoice/VibeVoice-Realtime-0.5B"):
        if self.model is not None and self._current_model_name == model_name:
            return

        import warnings

        import torch
        from vibevoice.processor.vibevoice_processor import VibeVoiceProcessor

        # Detect streaming vs standard model
        is_streaming = "realtime" in model_name.lower() or "streaming" in model_name.lower()

        if is_streaming:
            from vibevoice import (
                VibeVoiceStreamingForConditionalGenerationInference,
            )
            model_cls = VibeVoiceStreamingForConditionalGenerationInference
        else:
            from vibevoice.modular.modeling_vibevoice_inference import (
                VibeVoiceForConditionalGenerationInference,
            )
            model_cls = VibeVoiceForConditionalGenerationInference

        # Download to local directory if needed
        local_path = self.ensure_model_local(model_name, "tts")

        # Suppress harmless warnings during model loading
        import transformers.utils.logging as tf_logging
        tf_logging.set_verbosity_error()

        dtype = torch.bfloat16 if self.device == "cuda" else torch.float32
        device_map = "cuda" if self.device == "cuda" else "cpu"

        with warnings.catch_warnings():
            warnings.simplefilter("ignore")
            self.processor = VibeVoiceProcessor.from_pretrained(local_path)
            try:
                attn_impl = "flash_attention_2" if self.device == "cuda" else "sdpa"
                self.model = model_cls.from_pretrained(
                    local_path,
                    torch_dtype=dtype,
                    device_map=device_map,
                    attn_implementation=attn_impl,
                )
            except Exception:
                # Fallback to sdpa if flash_attention_2 is unavailable
                logger.info("flash_attention_2 unavailable, falling back to sdpa")
                self.model = model_cls.from_pretrained(
                    local_path,
                    torch_dtype=dtype,
                    device_map=device_map,
                    attn_implementation="sdpa",
                )

        tf_logging.set_verbosity_warning()
        self.model.eval()
        self.model.set_ddpm_inference_steps(num_steps=10)
        self._patch_compat()
        self._is_streaming = is_streaming
        self._current_model_name = model_name
        logger.info(
            "VibeVoice %s model loaded on %s: %s → %s",
            "streaming" if is_streaming else "standard",
            self.device, model_name, local_path,
        )

    def _patch_compat(self):
        """Patch vibevoice for transformers 4.50+ compatibility.

        1. VibeVoiceConfig.get_text_config returns self instead of decoder_config,
           so transformers can't find num_hidden_layers for cache setup.
        2. _prepare_cache_for_generation dropped the `device` parameter.
        3. DynamicCache replaced key_cache/value_cache lists with layers[].keys/values.
        """
        import inspect
        from vibevoice.modular.configuration_vibevoice import VibeVoiceConfig

        # Fix get_text_config to return decoder_config (matches VibeVoiceASRConfig)
        if not getattr(VibeVoiceConfig, "_patched_text_config", False):
            VibeVoiceConfig.get_text_config = lambda self, decoder=False: self.decoder_config
            VibeVoiceConfig._patched_text_config = True
            logger.info("Patched VibeVoiceConfig.get_text_config")

        # Fix _prepare_cache_for_generation (device param removed in transformers 4.50+)
        sig = inspect.signature(type(self.model)._prepare_cache_for_generation)
        if "device" not in sig.parameters:
            _orig = type(self.model)._prepare_cache_for_generation

            def _compat(self_model, generation_config, model_kwargs,
                        assistant_model, batch_size, max_cache_length, device=None):
                return _orig(self_model, generation_config, model_kwargs,
                             assistant_model, batch_size, max_cache_length)

            type(self.model)._prepare_cache_for_generation = _compat
            logger.info("Patched _prepare_cache_for_generation for transformers compat")

        # Fix DynamicCache: restore key_cache/value_cache properties removed in 4.57+
        from transformers import DynamicCache
        if not hasattr(DynamicCache, "key_cache"):
            DynamicCache.key_cache = property(lambda self: [l.keys for l in self.layers])
            DynamicCache.value_cache = property(lambda self: [l.values for l in self.layers])
            logger.info("Patched DynamicCache key_cache/value_cache for compat")

    def _decode_reference_audio(self, audio_b64: str) -> np.ndarray:
        """Decode base64 audio to numpy array for voice cloning."""
        audio_bytes = base64.b64decode(audio_b64)
        audio_data, sr = sf.read(io.BytesIO(audio_bytes), dtype="float32")
        if sr != self.sample_rate:
            import torch
            import torchaudio
            tensor = torch.from_numpy(audio_data).unsqueeze(0)
            if audio_data.ndim > 1:
                tensor = torch.from_numpy(audio_data.T)
            resampled = torchaudio.functional.resample(tensor, sr, self.sample_rate)
            audio_data = resampled.squeeze().numpy()
        if audio_data.ndim > 1:
            audio_data = audio_data.mean(axis=-1)
        return audio_data

    def process(self, **kwargs) -> dict:
        text = kwargs.get("text", "")
        volume = float(kwargs.get("volume", 0.8))
        model_name = kwargs.get("model_name", "vibevoice/VibeVoice-Realtime-0.5B")
        cfg_scale = float(kwargs.get("cfg_scale", 1.3))
        diffusion_steps = int(kwargs.get("diffusion_steps", 10))
        temperature = float(kwargs.get("temperature", 1.0))
        top_p = float(kwargs.get("top_p", 1.0))
        top_k = int(kwargs.get("top_k", 0))
        reference_audio = kwargs.get("reference_audio", "")
        max_new_tokens = int(kwargs.get("max_new_tokens", 2048))
        seed = int(kwargs.get("seed", 42))

        if not text.strip():
            return {"success": False, "error": "No text provided"}

        try:
            self._ensure_loaded(model_name)

            if self._is_streaming:
                return self._generate_streaming(
                    text, volume, cfg_scale, diffusion_steps, temperature,
                    top_p, top_k, reference_audio, max_new_tokens, seed,
                )
            else:
                return self._generate_standard(
                    text, volume, cfg_scale, diffusion_steps, temperature,
                    top_p, top_k, reference_audio, max_new_tokens, seed,
                )
        except Exception as e:
            logger.error("VibeVoice process failed: %s", e)
            return {"success": False, "error": str(e)}

    def _generate_standard(self, text, volume, cfg_scale, diffusion_steps,
                           temperature, top_p, top_k, reference_audio,
                           max_new_tokens, seed):
        """Generate with standard (non-streaming) models (1.5B, 7B).

        Uses VibeVoiceForConditionalGenerationInference which handles
        prefill internally via is_prefill parameter."""
        import torch

        torch.manual_seed(seed)
        if torch.cuda.is_available():
            torch.cuda.manual_seed(seed)

        self.model.set_ddpm_inference_steps(num_steps=diffusion_steps)

        # Format text in VibeVoice speaker script format
        formatted_text = f"Speaker 0: {text}"

        # Voice samples for processor
        if reference_audio:
            voice_data = self._decode_reference_audio(reference_audio)
            voice_samples = [[voice_data]]
            logger.info("Using reference audio for voice cloning (%d samples)", len(voice_data))
        else:
            voice_samples = [[np.zeros(self.sample_rate, dtype=np.float32)]]

        inputs = self.processor(
            text=[formatted_text],
            voice_samples=voice_samples,
            padding=True,
            return_tensors="pt",
            return_attention_mask=True,
        )

        # Move tensors to device
        for k, v in inputs.items():
            if torch.is_tensor(v):
                inputs[k] = v.to(self.device)

        # Build generation config
        do_sample = temperature < 1.0 or top_p < 1.0 or top_k > 0
        gen_config = {"do_sample": do_sample}
        if do_sample:
            gen_config["temperature"] = temperature
            gen_config["top_p"] = top_p
            if top_k > 0:
                gen_config["top_k"] = top_k

        with torch.no_grad():
            output = self.model.generate(
                **inputs,
                max_new_tokens=max_new_tokens if max_new_tokens > 0 else None,
                cfg_scale=cfg_scale,
                tokenizer=self.processor.tokenizer,
                generation_config=gen_config,
                verbose=False,
                is_prefill=bool(reference_audio),
            )

        if not output.speech_outputs or output.speech_outputs[0] is None:
            return {"success": False, "error": "No speech output generated"}

        audio_data = output.speech_outputs[0].cpu().float().numpy()
        if audio_data.ndim > 1:
            audio_data = audio_data.squeeze()

        sr = self.sample_rate
        audio_data = audio_data * volume
        audio_b64 = self.audio_to_base64(audio_data, sr)
        duration = len(audio_data) / sr

        return {
            "success": True,
            "audio_data": audio_b64,
            "duration": duration,
            "metadata": {
                "engine": "vibevoice",
                "sample_rate": sr,
                "model": self._current_model_name,
                "device": self.device,
                "mode": "standard",
                "voice_cloned": bool(reference_audio),
            },
        }

    def _generate_streaming(self, text, volume, cfg_scale, diffusion_steps,
                            temperature, top_p, top_k, reference_audio,
                            max_new_tokens, seed):
        """Generate with streaming model (0.5B Realtime).

        Uses VibeVoiceStreamingForConditionalGenerationInference with
        manual 4-stream prefill (forward_lm + forward_tts_lm)."""
        import torch

        torch.manual_seed(seed)
        if torch.cuda.is_available():
            torch.cuda.manual_seed(seed)

        self.model.set_ddpm_inference_steps(num_steps=diffusion_steps)

        formatted_text = f"Speaker 0: {text}"

        if reference_audio:
            voice_data = self._decode_reference_audio(reference_audio)
            voice_samples = [[voice_data]]
            logger.info("Using reference audio for voice cloning (%d samples)", len(voice_data))
        else:
            voice_samples = [[np.zeros(self.sample_rate, dtype=np.float32)]]

        inputs = self.processor(
            text=[formatted_text],
            voice_samples=voice_samples,
            padding=True,
            return_tensors="pt",
            return_attention_mask=True,
        )

        inputs_device = {}
        for k, v in inputs.items():
            if hasattr(v, "to"):
                inputs_device[k] = v.to(self.device)
            else:
                inputs_device[k] = v

        with torch.no_grad():
            all_prefilled = self._prefill_streaming(inputs_device)

            tts_text_ids = self.processor.tokenizer(
                f" Speaker 0: {text}\n",
                return_tensors="pt",
                add_special_tokens=False,
            ).input_ids.to(self.device)

            do_sample = temperature < 1.0 or top_p < 1.0
            output = self.model.generate(
                input_ids=inputs_device["input_ids"],
                attention_mask=inputs_device["attention_mask"],
                speech_tensors=inputs_device.get("speech_tensors"),
                speech_masks=inputs_device.get("speech_masks"),
                speech_input_mask=inputs_device.get("speech_input_mask"),
                tts_text_ids=tts_text_ids,
                tts_lm_input_ids=inputs_device["input_ids"],
                tts_lm_attention_mask=inputs_device["attention_mask"],
                all_prefilled_outputs=all_prefilled,
                tokenizer=self.processor.tokenizer,
                return_speech=True,
                cfg_scale=cfg_scale,
                max_new_tokens=max_new_tokens,
                do_sample=do_sample,
                temperature=temperature,
                top_p=top_p,
                top_k=top_k,
            )

        if not output.speech_outputs or output.speech_outputs[0] is None:
            return {"success": False, "error": "No speech output generated"}

        audio_data = output.speech_outputs[0].cpu().float().numpy()
        if audio_data.ndim > 1:
            audio_data = audio_data.squeeze()

        sr = self.sample_rate
        audio_data = audio_data * volume
        audio_b64 = self.audio_to_base64(audio_data, sr)
        duration = len(audio_data) / sr

        return {
            "success": True,
            "audio_data": audio_b64,
            "duration": duration,
            "metadata": {
                "engine": "vibevoice",
                "sample_rate": sr,
                "model": self._current_model_name,
                "device": self.device,
                "mode": "streaming",
                "voice_cloned": bool(reference_audio),
            },
        }

    def _prefill_streaming(self, inputs_device):
        """Run 4-stream prefill for the streaming model."""
        import torch

        input_ids = inputs_device["input_ids"]
        attention_mask = inputs_device["attention_mask"]
        seq_len = input_ids.shape[1]

        lm_out = self.model.forward_lm(
            input_ids=input_ids,
            attention_mask=attention_mask,
            use_cache=True,
        )

        tts_text_masks = torch.ones(1, seq_len, device=input_ids.device, dtype=torch.bool)
        tts_lm_out = self.model.forward_tts_lm(
            input_ids=input_ids,
            attention_mask=attention_mask,
            lm_last_hidden_state=lm_out.last_hidden_state,
            tts_text_masks=tts_text_masks,
            use_cache=True,
        )

        neg_tok = self.processor.tokenizer.convert_tokens_to_ids("<|image_pad|>")
        neg_ids = torch.tensor([[neg_tok]], device=input_ids.device, dtype=torch.long)
        neg_mask = torch.ones_like(neg_ids)
        neg_tts_masks = torch.ones(1, 1, device=input_ids.device, dtype=torch.bool)

        neg_lm = self.model.forward_lm(neg_ids, neg_mask, use_cache=True)
        neg_tts = self.model.forward_tts_lm(
            neg_ids, neg_mask,
            lm_last_hidden_state=neg_lm.last_hidden_state,
            tts_text_masks=neg_tts_masks,
            use_cache=True,
        )

        return {
            "lm": lm_out,
            "tts_lm": tts_lm_out,
            "neg_lm": neg_lm,
            "neg_tts_lm": neg_tts,
        }

    def cleanup(self):
        self.model = None
        self.processor = None
        self._current_model_name = None
        self._is_streaming = False
