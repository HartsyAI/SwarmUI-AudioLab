#!/usr/bin/env python3
"""VibeVoice TTS engine — Microsoft's multi-speaker speech synthesis.

Uses the vibevoice package API:
  - VibeVoiceProcessor.from_pretrained(model_name)  (non-streaming processor)
  - VibeVoiceStreamingForConditionalGenerationInference.from_pretrained(model_name)
  - Prefill: forward_lm + forward_tts_lm (4 streams: pos/neg × LM/TTS)
  - model.generate(all_prefilled_outputs=..., tts_text_ids=...) -> VibeVoiceGenerationOutput
"""

import logging

import numpy as np

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

    def initialize(self) -> bool:
        try:
            from vibevoice import (  # noqa: F401
                VibeVoiceStreamingForConditionalGenerationInference,
            )
            from vibevoice.processor.vibevoice_processor import (  # noqa: F401
                VibeVoiceProcessor,
            )

            self.device = "cuda" if self.has_cuda() else "cpu"
            logger.info("VibeVoice ready on %s", self.device)
            return True
        except Exception as e:
            logger.error("VibeVoice init failed: %s", e)
            return False

    def _ensure_loaded(self, model_name: str = "microsoft/VibeVoice-Realtime-0.5B"):
        if self.model is not None:
            return

        import warnings

        import torch
        from vibevoice import (
            VibeVoiceStreamingForConditionalGenerationInference,
        )
        from vibevoice.processor.vibevoice_processor import VibeVoiceProcessor

        # Suppress harmless warnings during model loading (uninitialized weights,
        # unused checkpoint keys, tokenizer special tokens — all normal for inference)
        with warnings.catch_warnings():
            warnings.filterwarnings("ignore", message=".*not used.*initializing.*")
            warnings.filterwarnings("ignore", message=".*not initialized.*checkpoint.*")
            warnings.filterwarnings("ignore", message=".*Special tokens.*")
            self.processor = VibeVoiceProcessor.from_pretrained(model_name)
            self.model = (
                VibeVoiceStreamingForConditionalGenerationInference.from_pretrained(
                    model_name,
                    dtype=torch.float16 if self.device == "cuda" else torch.float32,
                )
            )
        if self.device == "cuda":
            self.model = self.model.to("cuda")
        self.model.eval()
        logger.info("VibeVoice model loaded: %s", model_name)

    def _prefill(self, inputs_device):
        """Run 4-stream prefill (pos/neg × LM/TTS LM) to create KV caches for generate."""
        import torch

        input_ids = inputs_device["input_ids"]
        attention_mask = inputs_device["attention_mask"]
        seq_len = input_ids.shape[1]

        # Positive LM prefill
        lm_out = self.model.forward_lm(
            input_ids=input_ids,
            attention_mask=attention_mask,
            use_cache=True,
        )

        # Positive TTS LM prefill — all positions are text during prompt prefill
        tts_text_masks = torch.ones(1, seq_len, device=input_ids.device, dtype=torch.bool)
        tts_lm_out = self.model.forward_tts_lm(
            input_ids=input_ids,
            attention_mask=attention_mask,
            lm_last_hidden_state=lm_out.last_hidden_state,
            tts_text_masks=tts_text_masks,
            use_cache=True,
        )

        # Negative (unconditional) prefill — single <|image_pad|> token for CFG
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

    def process(self, **kwargs) -> dict:
        text = kwargs.get("text", "")
        volume = float(kwargs.get("volume", 0.8))
        model_name = kwargs.get("model_name", "microsoft/VibeVoice-Realtime-0.5B")
        cfg_scale = float(kwargs.get("cfg_scale", 1.3))

        if not text.strip():
            return {"success": False, "error": "No text provided"}

        try:
            import torch

            self._ensure_loaded(model_name)

            # Format text in VibeVoice's required speaker script format
            formatted_text = f"Speaker 0: {text}"

            # Use silence as default voice prompt (model generates its own voice)
            silence = [np.zeros(self.sample_rate, dtype=np.float32)]

            # Process inputs via non-streaming processor
            inputs = self.processor(
                text=[formatted_text],
                voice_samples=[silence],
                padding=True,
                return_tensors="pt",
                return_attention_mask=True,
            )

            # Move tensors to device
            inputs_device = {}
            for k, v in inputs.items():
                if hasattr(v, "to"):
                    inputs_device[k] = v.to(self.device)
                else:
                    inputs_device[k] = v

            # Prefill: 4-stream forward passes to create KV caches
            with torch.no_grad():
                all_prefilled = self._prefill(inputs_device)

                # Tokenize just the text portion for windowed TTS generation
                tts_text_ids = self.processor.tokenizer(
                    f" Speaker 0: {text}\n",
                    return_tensors="pt",
                    add_special_tokens=False,
                ).input_ids.to(self.device)

                # Generate speech
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
                )

            # Extract audio from generation output
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
                    "model": model_name,
                },
            }
        except Exception as e:
            logger.error("VibeVoice process failed: %s", e)
            return {"success": False, "error": str(e)}

    def cleanup(self):
        self.model = None
        self.processor = None
