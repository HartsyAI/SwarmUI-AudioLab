#!/usr/bin/env python3
"""Abstract base class for all audio engines."""

import base64
import io
import os
import struct
import threading

import numpy as np
from abc import ABC, abstractmethod
from typing import Any, Dict


class BaseAudioEngine(ABC):
    """Base class that all audio engines must extend.

    Subclasses implement ``initialize()`` to load models and ``process()``
    to handle requests.  The ``cleanup()`` method is called when the engine
    is being shut down and can be overridden to release GPU memory, close
    file handles, etc.
    """

    name: str = "base"
    category: str = "unknown"  # "tts", "stt", "musicgen", etc.

    # Set by audio_server.py before process() is called; cleared after.
    _cancel_event: threading.Event | None = None

    def is_cancelled(self) -> bool:
        """Check if the current request has been cancelled.

        Engines with iterative processing loops should call this periodically
        and return early when it returns ``True``.  Engines that don't check
        this will simply run to completion — the server layer handles the rest.
        """
        return self._cancel_event is not None and self._cancel_event.is_set()

    def cancelled_response(self) -> Dict[str, Any]:
        """Standard response dict for cancelled requests."""
        return {"success": False, "error": "cancelled", "cancelled": True}

    @abstractmethod
    def initialize(self) -> bool:
        """Load models and prepare the engine for processing.

        Returns ``True`` on success, ``False`` on failure.
        """
        ...

    @abstractmethod
    def process(self, **kwargs) -> Dict[str, Any]:
        """Run the engine's primary operation.

        For TTS engines, expected kwargs include ``text``, ``voice``,
        ``language``, ``volume``.  For STT engines, expected kwargs include
        ``audio_data`` (base64), ``language``.

        Returns a dict with at least ``"success": True/False``.
        """
        ...

    def cleanup(self):
        """Release resources held by this engine."""
        pass

    # Helpers shared across engines ----------------------------------------

    @staticmethod
    def get_model_dir(category: str = "") -> str:
        """Get centralized model directory from AUDIOLAB_MODEL_ROOT env var.

        Returns the path ``<root>/<category>/`` if set, creating it if needed.
        Returns empty string if the env var is not set.
        """
        root = os.environ.get("AUDIOLAB_MODEL_ROOT", "")
        if root and category:
            path = os.path.join(root, category)
            os.makedirs(path, exist_ok=True)
            return path
        return ""

    @staticmethod
    def ensure_model_local(model_name: str, category: str = "") -> str:
        """Download a HuggingFace model to a clean local directory.

        Converts ``"microsoft/VibeVoice-1.5B"`` to
        ``Models/audio/tts/VibeVoice-1.5B/`` and downloads via
        ``huggingface_hub.snapshot_download()``.  Returns the local path
        for use with ``from_pretrained()``.

        If the model directory already contains model files, the download
        is skipped.  Falls back to the raw *model_name* (HF cache) when
        ``AUDIOLAB_MODEL_ROOT`` is not set.
        """
        import logging
        logger = logging.getLogger("AudioLab.ModelDownload")

        model_dir = BaseAudioEngine.get_model_dir(category)
        if not model_dir:
            return model_name  # No model root configured — HF cache fallback

        local_name = model_name.split("/")[-1] if "/" in model_name else model_name
        local_path = os.path.join(model_dir, local_name)

        # Already downloaded?
        if os.path.isdir(local_path) and any(
            f.endswith((".bin", ".safetensors", ".json", ".model", ".onnx"))
            for f in os.listdir(local_path)
        ):
            logger.debug("Model already local: %s", local_path)
            return local_path

        logger.info("Downloading %s → %s ...", model_name, local_path)
        from huggingface_hub import snapshot_download
        try:
            snapshot_download(repo_id=model_name, local_dir=local_path)
        except Exception as e:
            err = str(e)
            if "gated repo" in err.lower() or "401" in err:
                has_token = bool(os.environ.get("HF_TOKEN"))
                if has_token:
                    raise RuntimeError(
                        f"Model '{model_name}' is gated. Your HuggingFace token was sent but access was denied. "
                        f"Go to https://huggingface.co/{model_name} and accept the model agreement, then try again."
                    ) from e
                raise RuntimeError(
                    f"Model '{model_name}' is gated and requires authentication. "
                    f"1) Go to https://huggingface.co/{model_name} and accept the model agreement. "
                    f"2) Set your HuggingFace token in SwarmUI: Server tab > User Settings > API Keys. "
                    f"Get a token at https://huggingface.co/settings/tokens (needs 'Read' permission)."
                ) from e
            raise
        logger.info("Download complete: %s", local_path)
        return local_path

    @staticmethod
    def has_cuda() -> bool:
        """Check if CUDA is available."""
        try:
            import torch
            return torch.cuda.is_available()
        except ImportError:
            return False

    @staticmethod
    def numpy_to_wav(audio_numpy: np.ndarray, sample_rate: int,
                     num_channels: int = 1) -> bytes:
        """Convert a float32 numpy array to WAV bytes (16-bit PCM).

        Normalises amplitude to avoid clipping, converts to int16, then
        builds a minimal RIFF/WAV header.
        """
        if len(audio_numpy.shape) > 1:
            audio_numpy = np.mean(audio_numpy, axis=0)
        max_amp = np.max(np.abs(audio_numpy))
        if max_amp > 0.8:
            audio_numpy = audio_numpy * 0.8 / max_amp
        audio_int16 = (audio_numpy * 32767).astype(np.int16)

        bits_per_sample = 16
        byte_rate = sample_rate * num_channels * bits_per_sample // 8
        block_align = num_channels * bits_per_sample // 8
        data_size = len(audio_int16) * bits_per_sample // 8
        file_size = 36 + data_size

        buf = io.BytesIO()
        buf.write(b"RIFF")
        buf.write(struct.pack("<I", file_size))
        buf.write(b"WAVE")
        buf.write(b"fmt ")
        buf.write(struct.pack("<I", 16))
        buf.write(struct.pack("<H", 1))           # PCM
        buf.write(struct.pack("<H", num_channels))
        buf.write(struct.pack("<I", sample_rate))
        buf.write(struct.pack("<I", byte_rate))
        buf.write(struct.pack("<H", block_align))
        buf.write(struct.pack("<H", bits_per_sample))
        buf.write(b"data")
        buf.write(struct.pack("<I", data_size))
        buf.write(audio_int16.tobytes())
        return buf.getvalue()

    @staticmethod
    def _numpy_to_wav_float32(audio_numpy: np.ndarray, sample_rate: int,
                               num_channels: int = 1) -> bytes:
        """Convert a float32 numpy array to WAV bytes (32-bit IEEE float)."""
        if len(audio_numpy.shape) > 1:
            audio_numpy = np.mean(audio_numpy, axis=0)
        max_amp = np.max(np.abs(audio_numpy))
        if max_amp > 0.8:
            audio_numpy = audio_numpy * 0.8 / max_amp
        audio_float32 = audio_numpy.astype(np.float32)

        bits_per_sample = 32
        byte_rate = sample_rate * num_channels * bits_per_sample // 8
        block_align = num_channels * bits_per_sample // 8
        data_size = len(audio_float32) * bits_per_sample // 8
        fmt_chunk_size = 18  # IEEE float needs extra 2 bytes (cbSize)
        file_size = 4 + (8 + fmt_chunk_size) + (8 + data_size)

        buf = io.BytesIO()
        buf.write(b"RIFF")
        buf.write(struct.pack("<I", file_size))
        buf.write(b"WAVE")
        buf.write(b"fmt ")
        buf.write(struct.pack("<I", fmt_chunk_size))
        buf.write(struct.pack("<H", 3))           # IEEE float
        buf.write(struct.pack("<H", num_channels))
        buf.write(struct.pack("<I", sample_rate))
        buf.write(struct.pack("<I", byte_rate))
        buf.write(struct.pack("<H", block_align))
        buf.write(struct.pack("<H", bits_per_sample))
        buf.write(struct.pack("<H", 0))           # cbSize
        buf.write(b"data")
        buf.write(struct.pack("<I", data_size))
        buf.write(audio_float32.tobytes())
        return buf.getvalue()

    @staticmethod
    def _encode_with_torchaudio(audio_numpy: np.ndarray, sample_rate: int,
                                 num_channels: int, fmt: str,
                                 quality: str) -> bytes:
        """Encode numpy audio to a compressed format via torchaudio.

        Supports flac, mp3, and ogg. Falls back to 16-bit WAV if the
        torchaudio backend cannot handle the requested format.
        """
        import logging
        import torch
        import torchaudio

        logger = logging.getLogger("AudioLab.Encode")

        # Normalize
        if len(audio_numpy.shape) > 1:
            audio_numpy = np.mean(audio_numpy, axis=0)
        max_amp = np.max(np.abs(audio_numpy))
        if max_amp > 0.8:
            audio_numpy = audio_numpy * 0.8 / max_amp

        # Reshape interleaved 1D → (channels, samples) for torchaudio
        if num_channels > 1:
            samples_per_ch = len(audio_numpy) // num_channels
            waveform = torch.tensor(audio_numpy, dtype=torch.float32) \
                            .reshape(samples_per_ch, num_channels).T
        else:
            waveform = torch.tensor(audio_numpy, dtype=torch.float32).unsqueeze(0)

        kwargs = {}
        if fmt == "mp3":
            bitrate_map = {"low": 128000, "medium": 192000, "high": 256000,
                           "max": 320000}
            kwargs["compression"] = bitrate_map.get(quality, 256000)
        elif fmt == "flac":
            level_map = {"low": 0, "medium": 5, "high": 8, "max": 8}
            kwargs["compression"] = level_map.get(quality, 5)
        elif fmt == "ogg":
            quality_map = {"low": 2, "medium": 5, "high": 8, "max": 10}
            kwargs["compression"] = quality_map.get(quality, 5)

        buf = io.BytesIO()
        try:
            torchaudio.save(buf, waveform, sample_rate, format=fmt, **kwargs)
            buf.seek(0)
            return buf.read()
        except Exception as e:
            logger.warning("torchaudio cannot encode '%s': %s — falling back to WAV", fmt, e)
            return BaseAudioEngine.numpy_to_wav(audio_numpy, sample_rate, num_channels)

    @staticmethod
    def encode_audio(audio_numpy: np.ndarray, sample_rate: int,
                     num_channels: int = 1, output_format: str = "wav_16",
                     quality: str = "high") -> tuple:
        """Encode numpy audio to the requested format.

        Args:
            audio_numpy: Float32 numpy array (1D interleaved or 2D).
            sample_rate: Sample rate in Hz.
            num_channels: Number of audio channels.
            output_format: One of wav_16, wav_32, flac, mp3, ogg.
            quality: One of low, medium, high, max (affects lossy formats).

        Returns:
            (base64_string, format_extension) tuple.
        """
        if output_format == "wav_32":
            audio_bytes = BaseAudioEngine._numpy_to_wav_float32(
                audio_numpy, sample_rate, num_channels)
            return base64.b64encode(audio_bytes).decode("utf-8"), "wav"
        elif output_format in ("flac", "mp3", "ogg"):
            audio_bytes = BaseAudioEngine._encode_with_torchaudio(
                audio_numpy, sample_rate, num_channels, output_format, quality)
            return base64.b64encode(audio_bytes).decode("utf-8"), output_format
        else:
            # Default: wav_16
            audio_bytes = BaseAudioEngine.numpy_to_wav(
                audio_numpy, sample_rate, num_channels)
            return base64.b64encode(audio_bytes).decode("utf-8"), "wav"

    @staticmethod
    def audio_to_base64(audio_numpy: np.ndarray, sample_rate: int,
                        num_channels: int = 1) -> str:
        """Convert numpy audio to a base64-encoded WAV string (16-bit PCM).

        Legacy helper kept for streaming TTS chunks which always use WAV.
        For final outputs, prefer :meth:`encode_audio`.
        """
        wav_bytes = BaseAudioEngine.numpy_to_wav(audio_numpy, sample_rate,
                                                  num_channels)
        return base64.b64encode(wav_bytes).decode("utf-8")

    @staticmethod
    def decode_audio_input(audio_data_b64: str) -> bytes:
        """Decode base64 audio data to raw bytes."""
        return base64.b64decode(audio_data_b64)
