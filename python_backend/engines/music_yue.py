#!/usr/bin/env python3
"""YuE engine — full-song music generation with lyrics from genre tags.

Two-stage LLaMA2-based pipeline:
  Stage-1 (7B): genre tags + lyrics -> interleaved vocal/instrumental audio tokens
  Stage-2 (1B): 1-codebook tokens -> 8-codebook refined tokens
  xcodec: tokens -> 16kHz waveform

Requires: transformers, torch, torchaudio, sentencepiece, einops, omegaconf, scipy
Optional: flash-attn (falls back to SDPA), bitsandbytes (for quantization)

Repository: https://github.com/multimodal-art-projection/YuE
License: Apache 2.0
"""

import copy
import logging
import os
import re
import subprocess
import sys
import uuid
from collections import Counter

import numpy as np

from .base_engine import BaseAudioEngine

logger = logging.getLogger("Music.YuE")

# xcodec token configuration (from YuE's mm_tokenizer_v0.2)
_SPECIAL_TOKENS = {
    "SOA": 32001,
    "EOA": 32002,
    "STAGE_1": 32013,
    "XCODEC": 32016,
    "STAGE_2": 32017,
}

# xcodec codec configuration
_XCODEC_CFG = {
    "codebook_size": 1024,
    "num_codebooks": 12,
    "global_offset": 45334,
    "fps": 50,
}


class _CodecTool:
    """Lightweight codec token manipulator (replaces YuE's CodecManipulator).

    Handles conversion between raw codec indices (0-1023 per codebook)
    and global token IDs used by the LLM tokenizer.
    """

    def __init__(self, n_quantizer: int = 1):
        self.codebook_size = _XCODEC_CFG["codebook_size"]
        self.num_codebooks = _XCODEC_CFG["num_codebooks"]
        self.global_offset = _XCODEC_CFG["global_offset"]
        self.n_quantizer = n_quantizer
        self.sep_ids = [_SPECIAL_TOKENS["XCODEC"]]

    def npy2ids(self, npy: np.ndarray) -> list:
        """(K, T) codec values -> flat list of global token IDs."""
        data = npy.copy().astype(np.uint32)
        for k in range(self.n_quantizer):
            data[k] += self.global_offset + k * self.codebook_size
        data = data[: self.n_quantizer]
        from einops import rearrange
        return rearrange(data, "K T -> (T K)").tolist()

    def ids2npy(self, token_ids) -> np.ndarray:
        """Flat token ID list -> (K, T) codec values."""
        from einops import rearrange
        data = np.array(token_ids, dtype=np.uint32)
        data = rearrange(data, "(T K) -> K T", K=self.n_quantizer)
        for k in range(self.n_quantizer):
            data[k] -= self.global_offset + k * self.codebook_size
        return data


class _BlockTokenRangeProcessor:
    """Prevents generation of tokens in a specified ID range."""

    def __init__(self, start_id: int, end_id: int):
        self.blocked = list(range(start_id, end_id))

    def __call__(self, input_ids, scores):
        scores[:, self.blocked] = -float("inf")
        return scores


def _split_lyrics(lyrics: str) -> list[str]:
    """Parse lyrics into segments using [section] markers."""
    pattern = r"\[(\w+)\](.*?)(?=\[|\Z)"
    segments = re.findall(pattern, lyrics, re.DOTALL)
    if not segments:
        return [lyrics.strip()]
    return [f"[{seg[0]}]\n{seg[1].strip()}\n\n" for seg in segments]


def _find_yue_repo() -> str | None:
    """Locate the cloned YuE repository."""
    model_dir = BaseAudioEngine.get_model_dir("yue")
    if model_dir:
        repo_path = os.path.join(model_dir, "YuE-repo")
        if os.path.isdir(os.path.join(repo_path, "inference")):
            return repo_path

    # Check common alternate locations
    venv_base = os.path.dirname(os.path.dirname(sys.executable))
    for candidate in [
        os.path.join(venv_base, "src", "YuE"),
        os.path.join(venv_base, "YuE"),
    ]:
        if os.path.isdir(os.path.join(candidate, "inference")):
            return candidate

    return None


def _clone_yue_repo(target_dir: str) -> bool:
    """Clone the YuE repository for inference code and xcodec model definitions."""
    logger.info("Cloning YuE repository to %s ...", target_dir)
    try:
        subprocess.run(
            ["git", "clone", "--depth", "1",
             "https://github.com/multimodal-art-projection/YuE.git",
             target_dir],
            check=True, capture_output=True, text=True, timeout=300,
        )
        logger.info("YuE repo cloned successfully.")
        return True
    except FileNotFoundError:
        logger.error("git not found on PATH. Install git and retry.")
        return False
    except subprocess.CalledProcessError as e:
        logger.error("git clone failed: %s", e.stderr)
        return False
    except subprocess.TimeoutExpired:
        logger.error("git clone timed out after 300s")
        return False


def _setup_yue_sys_path(repo_path: str):
    """Add YuE inference directories to sys.path."""
    inference_dir = os.path.join(repo_path, "inference")
    xcodec_dir = os.path.join(inference_dir, "xcodec_mini_infer")
    dac_dir = os.path.join(xcodec_dir, "descriptaudiocodec")

    for p in [inference_dir, xcodec_dir, dac_dir]:
        if os.path.isdir(p) and p not in sys.path:
            sys.path.insert(0, p)


class YuEEngine(BaseAudioEngine):
    """YuE full-song music generation engine.

    Generates complete songs with vocals and instrumentals from genre tags
    and structured lyrics. Uses a two-stage autoregressive LLM pipeline
    with xcodec audio tokenization.
    """

    name = "yue"
    category = "audiogeneration"

    def __init__(self):
        self.stage1_model = None
        self.stage2_model = None
        self.codec_model = None
        self.mmtokenizer = None
        self.current_model_name = None
        self.current_quantization = None
        self.repo_path = None
        self.sample_rate = 16000
        self._codectool_s1 = _CodecTool(n_quantizer=1)
        self._codectool_s2 = _CodecTool(n_quantizer=8)

    def initialize(self) -> bool:
        try:
            import torch  # noqa: F401
            from transformers import AutoModelForCausalLM  # noqa: F401
            logger.info("YuE dependencies available (model loaded on first request)")
        except ImportError as e:
            logger.error("YuE init: missing dependency: %s", e)
            return False

        # Find or clone the YuE repository
        self.repo_path = _find_yue_repo()
        if self.repo_path is None:
            model_dir = self.get_model_dir("yue")
            if not model_dir:
                logger.error("AUDIOLAB_MODEL_ROOT not set, cannot clone YuE repo")
                return False
            target = os.path.join(model_dir, "YuE-repo")
            if not _clone_yue_repo(target):
                return False
            self.repo_path = target

        _setup_yue_sys_path(self.repo_path)

        # Verify critical imports from the repo
        try:
            from mmtokenizer import _MMSentencePieceTokenizer  # noqa: F401
            logger.info("YuE repo imports verified: mmtokenizer OK")
        except ImportError as e:
            logger.error("YuE repo import failed (repo may be incomplete): %s", e)
            return False

        return True

    def _get_attn_implementation(self) -> str:
        """Detect best available attention implementation."""
        try:
            import flash_attn  # noqa: F401
            logger.info("Using FlashAttention 2")
            return "flash_attention_2"
        except ImportError:
            logger.info("flash-attn not available, using SDPA fallback")
            return "sdpa"

    def _load_codec_model(self, device):
        """Load the xcodec audio codec model from the repo checkpoint."""
        if self.codec_model is not None:
            return

        import torch
        from omegaconf import OmegaConf
        from models.soundstream_hubert_new import SoundStream

        inference_dir = os.path.join(self.repo_path, "inference")
        config_path = os.path.join(inference_dir, "xcodec_mini_infer", "final_ckpt", "config.yaml")
        ckpt_path = os.path.join(inference_dir, "xcodec_mini_infer", "final_ckpt", "ckpt_00360000.pth")

        if not os.path.exists(ckpt_path):
            # Try downloading from HuggingFace
            logger.info("xcodec checkpoint not found locally, downloading from HuggingFace...")
            xcodec_dir = os.path.join(inference_dir, "xcodec_mini_infer")
            self.ensure_model_local("m-a-p/xcodec_mini_infer", "yue")
            # Copy downloaded files to expected location
            hf_dir = self.ensure_model_local("m-a-p/xcodec_mini_infer", "yue")
            if hf_dir != "m-a-p/xcodec_mini_infer" and os.path.isdir(hf_dir):
                # Link or use the HF-downloaded path
                config_path = os.path.join(hf_dir, "final_ckpt", "config.yaml")
                ckpt_path = os.path.join(hf_dir, "final_ckpt", "ckpt_00360000.pth")

        if not os.path.exists(config_path):
            raise FileNotFoundError(f"xcodec config not found: {config_path}")
        if not os.path.exists(ckpt_path):
            raise FileNotFoundError(
                f"xcodec checkpoint not found: {ckpt_path}. "
                "Ensure the YuE repo was cloned with git-lfs or download from HuggingFace."
            )

        model_config = OmegaConf.load(config_path)
        self.codec_model = SoundStream(**model_config.generator.config).to(device)
        params = torch.load(ckpt_path, map_location="cpu", weights_only=False)
        self.codec_model.load_state_dict(params["codec_model"])
        self.codec_model.eval()
        logger.info("xcodec model loaded")

    def _load_tokenizer(self):
        """Load the multimodal sentencepiece tokenizer."""
        if self.mmtokenizer is not None:
            return

        from mmtokenizer import _MMSentencePieceTokenizer

        # The tokenizer model is shipped with the HF model repos
        # Try loading from the repo first, then from HF-cached model
        inference_dir = os.path.join(self.repo_path, "inference")
        tokenizer_path = os.path.join(inference_dir, "mm_tokenizer_v0.2_hf", "tokenizer.model")

        if not os.path.exists(tokenizer_path):
            # Try to find it in a downloaded model directory
            model_dir = self.get_model_dir("yue")
            for candidate in ["mm_tokenizer_v0.2_hf", "tokenizer.model"]:
                p = os.path.join(model_dir, candidate) if model_dir else ""
                if os.path.exists(p):
                    tokenizer_path = p if p.endswith(".model") else os.path.join(p, "tokenizer.model")
                    break

        if not os.path.exists(tokenizer_path):
            raise FileNotFoundError(
                f"Tokenizer not found at {tokenizer_path}. "
                "Ensure the YuE repo was cloned completely."
            )

        self.mmtokenizer = _MMSentencePieceTokenizer(tokenizer_path)
        logger.info("Multimodal tokenizer loaded from %s", tokenizer_path)

    def _ensure_loaded(self, model_name: str, quantization: str = "fp16"):
        """Load or swap Stage-1 and Stage-2 models."""
        if (self.stage1_model is not None
                and self.current_model_name == model_name
                and self.current_quantization == quantization):
            return

        import torch
        from transformers import AutoModelForCausalLM

        device = torch.device("cuda:0" if torch.cuda.is_available() else "cpu")

        # Unload previous models if switching
        if self.stage1_model is not None:
            logger.info("Switching model: %s -> %s", self.current_model_name, model_name)
            self.cleanup()

        attn_impl = self._get_attn_implementation()

        # --- Stage-1 model (7B) ---
        logger.info("Loading Stage-1 model: %s (quantization: %s)", model_name, quantization)
        s1_local = self.ensure_model_local(model_name, "yue")

        load_kwargs = {
            "torch_dtype": torch.bfloat16,
            "attn_implementation": attn_impl,
            "device_map": "auto",
        }

        if quantization == "8bit":
            try:
                from transformers import BitsAndBytesConfig
                load_kwargs["quantization_config"] = BitsAndBytesConfig(load_in_8bit=True)
                del load_kwargs["torch_dtype"]
                logger.info("Using 8-bit quantization")
            except ImportError:
                logger.warning("bitsandbytes not available, falling back to fp16")
                load_kwargs["torch_dtype"] = torch.float16
        elif quantization == "4bit":
            try:
                from transformers import BitsAndBytesConfig
                load_kwargs["quantization_config"] = BitsAndBytesConfig(
                    load_in_4bit=True,
                    bnb_4bit_compute_dtype=torch.float16,
                )
                del load_kwargs["torch_dtype"]
                logger.info("Using 4-bit quantization")
            except ImportError:
                logger.warning("bitsandbytes not available, falling back to fp16")
                load_kwargs["torch_dtype"] = torch.float16

        self.stage1_model = AutoModelForCausalLM.from_pretrained(s1_local, **load_kwargs)
        self.stage1_model.eval()
        logger.info("Stage-1 model loaded")

        # --- Stage-2 model (1B) — always fp16, small enough ---
        stage2_name = "m-a-p/YuE-s2-1B-general"
        s2_local = self.ensure_model_local(stage2_name, "yue")
        logger.info("Loading Stage-2 model: %s", stage2_name)

        self.stage2_model = AutoModelForCausalLM.from_pretrained(
            s2_local,
            torch_dtype=torch.bfloat16,
            attn_implementation=attn_impl,
            device_map="auto",
        )
        self.stage2_model.eval()
        logger.info("Stage-2 model loaded")

        # --- xcodec + tokenizer ---
        self._load_codec_model(device)
        self._load_tokenizer()

        self.current_model_name = model_name
        self.current_quantization = quantization

    def _stage1_generate(self, genre_tags: str, lyrics_segments: list[str],
                         max_new_tokens: int, temperature: float, top_p: float,
                         repetition_penalty: float, run_n_segments: int,
                         seed: int) -> tuple[np.ndarray, np.ndarray]:
        """Run Stage-1: genre + lyrics -> interleaved vocal/instrumental codec tokens."""
        import torch
        from transformers import LogitsProcessorList

        device = next(self.stage1_model.parameters()).device

        if seed >= 0:
            torch.manual_seed(seed)
            np.random.seed(seed)

        codectool = self._codectool_s1
        full_lyrics = "\n".join(lyrics_segments)

        # Build the initial prompt
        prompt_header = f"Generate music from the given lyrics segment by segment.\n[Genre] {genre_tags}\n{full_lyrics}"

        # Tokenize special markers
        start_seg = self.mmtokenizer.tokenize("[start_of_segment]")
        end_seg = self.mmtokenizer.tokenize("[end_of_segment]")

        raw_output = None
        n_segments = min(run_n_segments, len(lyrics_segments))

        logits_processor = LogitsProcessorList([
            _BlockTokenRangeProcessor(0, 32002),
            _BlockTokenRangeProcessor(32016, 32016),
        ])

        for i in range(n_segments):
            if self.is_cancelled():
                logger.info("Stage-1 cancelled at segment %d/%d", i, n_segments)
                break

            section_text = lyrics_segments[i]
            guidance_scale = 1.5 if i == 0 else 1.2

            if i == 0:
                # First segment: include header
                head_ids = self.mmtokenizer.tokenize(prompt_header)
                prompt_ids = (head_ids + start_seg
                              + self.mmtokenizer.tokenize(section_text)
                              + [self.mmtokenizer.soa] + codectool.sep_ids)
            else:
                # Subsequent segments: continue from previous output
                prompt_ids = (end_seg + start_seg
                              + self.mmtokenizer.tokenize(section_text)
                              + [self.mmtokenizer.soa] + codectool.sep_ids)

            prompt_tensor = torch.as_tensor(prompt_ids).unsqueeze(0).to(device)

            if raw_output is not None:
                input_ids = torch.cat([raw_output, prompt_tensor], dim=1)
            else:
                input_ids = prompt_tensor

            # Enforce context window limit
            max_context = 16384 - max_new_tokens - 1
            if input_ids.shape[-1] > max_context:
                logger.warning("Segment %d: truncating input to last %d tokens", i, max_context)
                input_ids = input_ids[:, -max_context:]

            with torch.no_grad():
                output_seq = self.stage1_model.generate(
                    input_ids=input_ids,
                    max_new_tokens=max_new_tokens,
                    min_new_tokens=100,
                    do_sample=True,
                    top_p=top_p,
                    temperature=temperature,
                    repetition_penalty=repetition_penalty,
                    eos_token_id=self.mmtokenizer.eoa,
                    pad_token_id=self.mmtokenizer.eoa,
                    logits_processor=logits_processor,
                    guidance_scale=guidance_scale,
                )

                # Ensure output ends with eoa
                if output_seq[0, -1].item() != self.mmtokenizer.eoa:
                    eoa_t = torch.tensor([[self.mmtokenizer.eoa]], device=device)
                    output_seq = torch.cat([output_seq, eoa_t], dim=1)

            if raw_output is not None:
                raw_output = torch.cat([raw_output, prompt_tensor, output_seq[:, input_ids.shape[-1]:]], dim=1)
            else:
                raw_output = output_seq

            logger.info("Stage-1 segment %d/%d complete (%d tokens generated)",
                        i + 1, n_segments, output_seq.shape[1] - input_ids.shape[1])

        if raw_output is None:
            raise RuntimeError("Stage-1 produced no output")

        # Extract codec tokens from output
        ids = raw_output[0].cpu().numpy()
        soa_idx = np.where(ids == self.mmtokenizer.soa)[0].tolist()
        eoa_idx = np.where(ids == self.mmtokenizer.eoa)[0].tolist()

        if len(soa_idx) == 0 or len(eoa_idx) == 0:
            raise RuntimeError("Stage-1 output contains no audio segments (no SOA/EOA tokens found)")

        from einops import rearrange

        vocals_list = []
        instrumentals_list = []

        for j in range(min(len(soa_idx), len(eoa_idx))):
            codec_ids = ids[soa_idx[j] + 1: eoa_idx[j]]
            if len(codec_ids) == 0:
                continue
            if codec_ids[0] == _SPECIAL_TOKENS["XCODEC"]:
                codec_ids = codec_ids[1:]
            if len(codec_ids) < 2:
                continue
            # Ensure even length for 2-track interleaving
            codec_ids = codec_ids[: 2 * (len(codec_ids) // 2)]
            # De-interleave into vocal and instrumental
            reshaped = rearrange(codec_ids, "(n b) -> b n", b=2)
            vocals_list.append(codectool.ids2npy(reshaped[0]))
            instrumentals_list.append(codectool.ids2npy(reshaped[1]))

        if not vocals_list:
            raise RuntimeError("Stage-1 produced no valid audio segments")

        vocals = np.concatenate(vocals_list, axis=1)
        instrumentals = np.concatenate(instrumentals_list, axis=1)

        return vocals, instrumentals

    def _stage2_generate_batch(self, prompt: np.ndarray, batch_size: int) -> np.ndarray:
        """Run Stage-2 refinement: expand 1-codebook tokens to 8-codebook.

        Processes in chunks of 300 frames (6 seconds at 50fps).
        """
        import torch
        from transformers import LogitsProcessorList

        device = next(self.stage2_model.parameters()).device
        codectool = self._codectool_s2

        # Convert Stage-1 output to offset token IDs
        codec_ids = codectool._unflatten_s1(prompt)
        codec_ids = self._offset_for_stage2(codec_ids)

        output_duration = prompt.shape[-1] // 50 // 6 * 6  # Quantize to 6s chunks
        if output_duration == 0:
            output_duration = prompt.shape[-1] // 50
        num_batch = max(1, output_duration * 50 // 300)

        block_list = LogitsProcessorList([
            _BlockTokenRangeProcessor(0, 46358),
            _BlockTokenRangeProcessor(53526, self.mmtokenizer.vocab_size),
        ])

        all_segments = []
        total_frames = prompt.shape[-1]
        chunk_size = 300  # 6 seconds at 50fps

        for batch_start in range(0, total_frames, chunk_size * batch_size):
            if self.is_cancelled():
                logger.info("Stage-2 cancelled at frame %d/%d", batch_start, total_frames)
                break

            current_batch = []
            for b in range(batch_size):
                start = batch_start + b * chunk_size
                end = min(start + chunk_size, total_frames)
                if start >= total_frames:
                    break
                current_batch.append(codec_ids[:, start:end])

            if not current_batch:
                break

            actual_batch = len(current_batch)
            batch_codec = np.concatenate(current_batch, axis=0) if actual_batch > 1 else current_batch[0]

            # Build prompt: [soa][stage_1]{codec_frame}[stage_2]
            soa_s1 = np.tile(
                [self.mmtokenizer.soa, _SPECIAL_TOKENS["STAGE_1"]],
                (actual_batch, 1),
            )
            s2_marker = np.tile([_SPECIAL_TOKENS["STAGE_2"]], (actual_batch, 1))
            prompt_ids = np.concatenate([soa_s1, batch_codec, s2_marker], axis=1).astype(np.int32)

            prompt_tensor = torch.as_tensor(prompt_ids).to(device)
            cb0 = torch.as_tensor(batch_codec).to(device)
            len_prompt = prompt_tensor.shape[-1]

            # Generate 7 additional codebook levels per frame
            for frame_idx in range(cb0.shape[1]):
                frame_token = cb0[:, frame_idx: frame_idx + 1]
                prompt_tensor = torch.cat([prompt_tensor, frame_token], dim=1)

                with torch.no_grad():
                    output = self.stage2_model.generate(
                        input_ids=prompt_tensor,
                        min_new_tokens=7,
                        max_new_tokens=7,
                        eos_token_id=self.mmtokenizer.eoa,
                        pad_token_id=self.mmtokenizer.eoa,
                        logits_processor=block_list,
                    )
                prompt_tensor = output

            # Extract generated tokens
            result = prompt_tensor.cpu().numpy()[:, len_prompt:]
            if actual_batch > 1:
                result = np.concatenate([result[i] for i in range(actual_batch)], axis=0)
            else:
                result = result[0]

            all_segments.append(result)

        if not all_segments:
            raise RuntimeError("Stage-2 produced no output")

        output = np.concatenate(all_segments, axis=0)

        # Convert back to codec values
        refined = self._codectool_s2.ids2npy(output)

        # Fix outlier tokens
        fixed = copy.deepcopy(refined)
        for i, line in enumerate(refined):
            for j, val in enumerate(line):
                if val < 0 or val > 1023:
                    counter = Counter(line)
                    most_common = counter.most_common(1)[0][0]
                    fixed[i, j] = most_common

        return fixed

    def _offset_for_stage2(self, codec_ids: np.ndarray) -> np.ndarray:
        """Apply global offset for Stage-2 input tokens."""
        out = codec_ids.copy().astype(np.int32)
        for k in range(out.shape[0]):
            out[k] += _XCODEC_CFG["global_offset"] + k * _XCODEC_CFG["codebook_size"]
        return out

    def _decode_to_audio(self, codec_result: np.ndarray) -> np.ndarray:
        """Decode 8-codebook tokens to 16kHz waveform via xcodec."""
        import torch

        device = next(iter(self.codec_model.parameters())).device

        with torch.no_grad():
            tokens = torch.as_tensor(
                codec_result.astype(np.int16), dtype=torch.long
            ).unsqueeze(0).permute(1, 0, 2).to(device)
            waveform = self.codec_model.decode(tokens)

        return waveform.cpu().squeeze(0).numpy().astype(np.float32)

    def process(self, **kwargs) -> dict:
        prompt = kwargs.get("prompt", "")
        lyrics = kwargs.get("lyrics", "")
        model_name = kwargs.get("model_name", "m-a-p/YuE-s1-7B-anneal-en-cot")
        max_new_tokens = int(kwargs.get("max_new_tokens", 3000))
        quantization = kwargs.get("quantization", "fp16")
        seed = int(kwargs.get("seed", -1))
        stage2_batch_size = int(kwargs.get("stage2_batch_size", 4))
        temperature = float(kwargs.get("temperature", 0.9))
        top_p = float(kwargs.get("top_p", 0.93))
        repetition_penalty = float(kwargs.get("repetition_penalty", 1.2))
        run_n_segments = int(kwargs.get("run_n_segments", 2))

        if not prompt.strip():
            return {"success": False, "error": "No genre tags provided in the prompt field"}

        if not lyrics.strip():
            return {"success": False, "error": "No lyrics provided. Use [verse]/[chorus] section markers."}

        lyrics_segments = _split_lyrics(lyrics)
        if run_n_segments <= 0:
            run_n_segments = len(lyrics_segments)

        try:
            self._ensure_loaded(model_name, quantization)

            # --- Stage 1: Generate audio tokens ---
            logger.info(
                "Stage-1: generating %d segments, max_tokens=%d, model=%s",
                min(run_n_segments, len(lyrics_segments)), max_new_tokens, model_name,
            )
            vocals, instrumentals = self._stage1_generate(
                genre_tags=prompt,
                lyrics_segments=lyrics_segments,
                max_new_tokens=max_new_tokens,
                temperature=temperature,
                top_p=top_p,
                repetition_penalty=repetition_penalty,
                run_n_segments=run_n_segments,
                seed=seed,
            )

            if self.is_cancelled():
                return self.cancelled_response()

            # --- Offload Stage-1, keep Stage-2 ---
            import torch

            # --- Stage 2: Refine tokens ---
            logger.info("Stage-2: refining %d frames (vocal + instrumental), batch_size=%d",
                        vocals.shape[1], stage2_batch_size)

            vocals_refined = self._stage2_generate_batch(vocals, stage2_batch_size)
            if self.is_cancelled():
                return self.cancelled_response()

            instrumentals_refined = self._stage2_generate_batch(instrumentals, stage2_batch_size)
            if self.is_cancelled():
                return self.cancelled_response()

            # --- Decode to audio ---
            logger.info("Decoding audio via xcodec...")
            vocal_audio = self._decode_to_audio(vocals_refined)
            instrumental_audio = self._decode_to_audio(instrumentals_refined)

            # Mix vocal and instrumental
            min_len = min(vocal_audio.shape[-1], instrumental_audio.shape[-1])
            if len(vocal_audio.shape) > 1:
                vocal_audio = np.mean(vocal_audio, axis=0)
            if len(instrumental_audio.shape) > 1:
                instrumental_audio = np.mean(instrumental_audio, axis=0)

            vocal_audio = vocal_audio[:min_len]
            instrumental_audio = instrumental_audio[:min_len]
            mixed = (vocal_audio + instrumental_audio) / 2.0

            audio_b64 = self.audio_to_base64(mixed, self.sample_rate)
            actual_duration = len(mixed) / self.sample_rate

            logger.info("YuE generation complete: %.1fs of audio", actual_duration)

            return {
                "success": True,
                "audio_data": audio_b64,
                "duration": actual_duration,
                "metadata": {
                    "engine": "yue",
                    "model": model_name,
                    "sample_rate": self.sample_rate,
                    "prompt": prompt,
                    "has_lyrics": True,
                    "num_segments": min(run_n_segments, len(lyrics_segments)),
                    "quantization": quantization,
                    "seed": seed,
                },
            }

        except Exception as e:
            logger.error("YuE process failed: %s", e, exc_info=True)
            return {"success": False, "error": str(e)}

    def cleanup(self):
        """Release all GPU memory."""
        try:
            import torch

            for attr in ("stage1_model", "stage2_model", "codec_model"):
                obj = getattr(self, attr, None)
                if obj is not None:
                    del obj
                    setattr(self, attr, None)

            self.current_model_name = None
            self.current_quantization = None

            if torch.cuda.is_available():
                torch.cuda.empty_cache()

            logger.info("YuE models unloaded")
        except Exception:
            self.stage1_model = None
            self.stage2_model = None
            self.codec_model = None
            self.current_model_name = None
            self.current_quantization = None
