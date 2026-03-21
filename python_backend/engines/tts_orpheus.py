#!/usr/bin/env python3
"""Orpheus TTS engine — expressive speech via Llama + SNAC codec.

Uses transformers (not vLLM) so it works on Windows.
Model: canopylabs/orpheus-3b-0.1-ft
Codec: hubertsiuzdak/snac_24khz (SNAC multi-scale neural audio codec)

Voices: tara, leah, jess, leo, dan, mia, zac, zoe
Emotion tags: <laugh>, <chuckle>, <sigh>, <cough>, <sniffle>, <groan>, <yawn>, <gasp>
"""

import logging
import numpy as np

from .base_engine import BaseAudioEngine

logger = logging.getLogger("TTS.Orpheus")

# Special token IDs for the Orpheus model
CODE_START = 128257
END_OF_SPEECH = 128258
START_OF_HUMAN = 128259
END_OF_HUMAN = 128260
END_OF_TEXT = 128009
AUDIO_CODE_BASE = 128266


class OrpheusEngine(BaseAudioEngine):
    """Orpheus TTS engine using transformers + SNAC codec."""

    name = "orpheus"
    category = "tts"

    def __init__(self):
        self.model = None
        self.tokenizer = None
        self.snac_model = None
        self.sample_rate = 24000
        self.device = None

    def initialize(self) -> bool:
        try:
            from transformers import AutoModelForCausalLM, AutoTokenizer  # noqa: F401
            from snac import SNAC  # noqa: F401

            self.device = "cuda" if self.has_cuda() else "cpu"
            logger.info("Orpheus ready on %s (model loaded on first request)", self.device)
            return True
        except Exception as e:
            logger.error("Orpheus init failed: %s", e)
            return False

    def _ensure_loaded(self, model_name: str = "canopylabs/orpheus-3b-0.1-ft"):
        if self.model is not None:
            return

        import torch
        from transformers import AutoModelForCausalLM, AutoTokenizer
        from snac import SNAC

        local_path = self.ensure_model_local(model_name, "tts")
        snac_path = self.ensure_model_local("hubertsiuzdak/snac_24khz", "tts")

        self.tokenizer = AutoTokenizer.from_pretrained(local_path)
        self.model = AutoModelForCausalLM.from_pretrained(
            local_path, torch_dtype=torch.bfloat16
        ).to(self.device)

        self.snac_model = SNAC.from_pretrained(snac_path)
        self.snac_model = self.snac_model.to(self.device)

        logger.info("Orpheus model loaded: %s", model_name)

    def _format_prompt(self, text: str, voice: str):
        """Format text with voice prefix and Orpheus control tokens."""
        import torch

        prompt = f"{voice}: {text}"
        input_ids = self.tokenizer(prompt, return_tensors="pt").input_ids

        start_token = torch.tensor([[START_OF_HUMAN]], dtype=torch.int64)
        end_tokens = torch.tensor([[END_OF_TEXT, END_OF_HUMAN]], dtype=torch.int64)
        modified = torch.cat([start_token, input_ids, end_tokens], dim=1)

        return modified.to(self.device)

    def _parse_audio_codes(self, generated_ids):
        """Extract SNAC audio codes from generated token IDs."""
        # Find last CODE_START token and crop everything before it
        indices = (generated_ids == CODE_START).nonzero(as_tuple=True)
        if len(indices[1]) > 0:
            last_idx = indices[1][-1].item()
            codes = generated_ids[:, last_idx + 1:]
        else:
            codes = generated_ids

        # Remove END_OF_SPEECH tokens, flatten
        row = codes[0]
        row = row[row != END_OF_SPEECH]

        # Trim to multiple of 7, subtract base offset
        n = (row.size(0) // 7) * 7
        row = row[:n]
        return [t.item() - AUDIO_CODE_BASE for t in row]

    def _decode_to_audio(self, code_list):
        """Redistribute flat codes into SNAC 3-layer structure and decode."""
        import torch

        layer_1, layer_2, layer_3 = [], [], []

        for i in range(len(code_list) // 7):
            base = 7 * i
            layer_1.append(code_list[base])
            layer_2.append(code_list[base + 1] - 4096)
            layer_3.append(code_list[base + 2] - 2 * 4096)
            layer_3.append(code_list[base + 3] - 3 * 4096)
            layer_2.append(code_list[base + 4] - 4 * 4096)
            layer_3.append(code_list[base + 5] - 5 * 4096)
            layer_3.append(code_list[base + 6] - 6 * 4096)

        dev = next(self.snac_model.parameters()).device
        codes = [
            torch.tensor(layer_1, device=dev).unsqueeze(0),
            torch.tensor(layer_2, device=dev).unsqueeze(0),
            torch.tensor(layer_3, device=dev).unsqueeze(0),
        ]

        audio = self.snac_model.decode(codes)
        return audio.detach().squeeze().cpu().numpy()

    def process(self, **kwargs) -> dict:
        text = kwargs.get("text", "")
        voice = kwargs.get("voice", "tara")
        volume = float(kwargs.get("volume", 0.8))
        model_name = kwargs.get("model_name", "canopylabs/orpheus-3b-0.1-ft")
        temperature = float(kwargs.get("temperature", 0.6))
        top_p = float(kwargs.get("top_p", 0.95))
        repetition_penalty = float(kwargs.get("repetition_penalty", 1.1))

        if not text.strip():
            return {"success": False, "error": "No text provided"}

        try:
            import torch

            self._ensure_loaded(model_name)
            input_ids = self._format_prompt(text, voice)

            with torch.no_grad():
                generated = self.model.generate(
                    input_ids=input_ids,
                    attention_mask=torch.ones_like(input_ids),
                    max_new_tokens=1200,
                    do_sample=True,
                    temperature=temperature,
                    top_p=top_p,
                    repetition_penalty=repetition_penalty,
                    eos_token_id=END_OF_SPEECH,
                )

            code_list = self._parse_audio_codes(generated)
            if not code_list:
                return {"success": False, "error": "No audio codes generated"}

            audio_numpy = self._decode_to_audio(code_list)
            audio_numpy = audio_numpy.astype(np.float32) * volume

            output_format = kwargs.get("output_format", "wav_16")
            output_quality = kwargs.get("output_quality", "high")
            audio_b64, fmt = self.encode_audio(audio_numpy, self.sample_rate, output_format=output_format, quality=output_quality)
            duration = len(audio_numpy) / self.sample_rate

            return {
                "success": True,
                "audio_data": audio_b64,
                "output_format": fmt,
                "duration": duration,
                "metadata": {
                    "engine": "orpheus",
                    "sample_rate": self.sample_rate,
                    "voice": voice,
                    "model": model_name,
                },
            }
        except Exception as e:
            logger.error("Orpheus process failed: %s", e)
            return {"success": False, "error": str(e)}

    def cleanup(self):
        self.model = None
        self.tokenizer = None
        self.snac_model = None
