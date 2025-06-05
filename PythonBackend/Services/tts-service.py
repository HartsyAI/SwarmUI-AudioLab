"""
TTS Service - Text-to-Speech Processing
SwarmUI VoiceAssistant Extension - Chatterbox TTS Wrapper

This module provides text-to-speech functionality using Chatterbox TTS library
with support for voice cloning, multiple languages, and emotion control.
"""

import asyncio
import base64
import io
import os
import tempfile
import time
import traceback
import wave
from concurrent.futures import ThreadPoolExecutor
from dataclasses import dataclass
from pathlib import Path
from typing import Dict, List, Optional, Any, Union

import numpy as np
import torch
import torchaudio
from loguru import logger
from pydub import AudioSegment
import soundfile as sf

# Chatterbox TTS imports
try:
    from chatterbox_tts import ChatterboxTTS
    from chatterbox_tts.config import TTSConfig as ChatterboxConfig
    CHATTERBOX_AVAILABLE = True
except ImportError:
    logger.warning("Chatterbox TTS not available, using fallback TTS")
    CHATTERBOX_AVAILABLE = False

# TTS library imports
try:
    from TTS.api import TTS as CoquiTTS
    COQUI_AVAILABLE = True
except ImportError:
    logger.warning("Coqui TTS not available")
    COQUI_AVAILABLE = False

# GTTS fallback
try:
    from gtts import gTTS
    GTTS_AVAILABLE = True
except ImportError:
    logger.warning("gTTS not available")
    GTTS_AVAILABLE = False


@dataclass
class TTSConfig:
    """Configuration for TTS service"""
    voice: str = "default"
    speed: float = 1.0
    volume: float = 0.8
    language: str = "en-US"
    sample_rate: int = 22050
    channels: int = 1
    emotion: float = 0.5
    pacing: float = 0.5
    device: str = "auto"  # auto, cpu, cuda
    voice_clone_enabled: bool = True
    backend: str = "auto"  # auto, chatterbox, coqui, gtts


class TTSService:
    """Text-to-speech service supporting multiple TTS backends"""
    
    def __init__(self, config: TTSConfig):
        self.config = config
        self.chatterbox_tts: Optional[ChatterboxTTS] = None
        self.coqui_tts: Optional[CoquiTTS] = None
        self.executor = ThreadPoolExecutor(max_workers=2)
        self.voice_samples: Dict[str, Path] = {}
        self.available_voices: List[str] = []
        self.is_initialized = False
        self.backend = "fallback"
        
        logger.info("TTS Service initialized")
    
    async def initialize(self) -> bool:
        """Initialize the TTS service with available backends"""
        try:
            logger.info("Initializing TTS service...")
            
            # Determine device
            device = self._get_device()
            logger.info(f"Using device: {device}")
            
            # Initialize backend in order of preference
            backend_initialized = False
            
            if self.config.backend == "auto" or self.config.backend == "chatterbox":
                backend_initialized = await self._initialize_chatterbox()
                if backend_initialized:
                    self.backend = "chatterbox"
            
            if not backend_initialized and (self.config.backend == "auto" or self.config.backend == "coqui"):
                backend_initialized = await self._initialize_coqui()
                if backend_initialized:
                    self.backend = "coqui"
            
            if not backend_initialized and (self.config.backend == "auto" or self.config.backend == "gtts"):
                backend_initialized = await self._initialize_gtts()
                if backend_initialized:
                    self.backend = "gtts"
            
            if not backend_initialized:
                logger.warning("No TTS backend available, using simple fallback")
                self.backend = "fallback"
            
            # Load available voices
            await self._load_available_voices()
            
            # Set up voice samples directory
            self._setup_voice_samples_directory()
            
            self.is_initialized = True
            logger.info(f"TTS service initialized successfully with backend: {self.backend}")
            return True
            
        except Exception as e:
            logger.error(f"Failed to initialize TTS service: {e}")
            logger.error(traceback.format_exc())
            return False
    
    async def _initialize_chatterbox(self) -> bool:
        """Initialize Chatterbox TTS backend"""
        try:
            if not CHATTERBOX_AVAILABLE:
                return False
            
            logger.info("Initializing Chatterbox TTS...")
            
            # Configure Chatterbox TTS
            chatterbox_config = ChatterboxConfig(
                device=self._get_device(),
                sample_rate=self.config.sample_rate,
                enable_voice_cloning=self.config.voice_clone_enabled
            )
            
            # Initialize in executor
            self.chatterbox_tts = await asyncio.get_event_loop().run_in_executor(
                self.executor,
                lambda: ChatterboxTTS(config=chatterbox_config)
            )
            
            logger.info("Chatterbox TTS initialized successfully")
            return True
            
        except Exception as e:
            logger.warning(f"Failed to initialize Chatterbox TTS: {e}")
            return False
    
    async def _initialize_coqui(self) -> bool:
        """Initialize Coqui TTS backend"""
        try:
            if not COQUI_AVAILABLE:
                return False
            
            logger.info("Initializing Coqui TTS...")
            
            # Choose model based on language
            model_name = self._get_coqui_model_for_language(self.config.language)
            
            # Initialize in executor
            self.coqui_tts = await asyncio.get_event_loop().run_in_executor(
                self.executor,
                lambda: CoquiTTS(model_name=model_name, gpu=self._get_device() == "cuda")
            )
            
            logger.info("Coqui TTS initialized successfully")
            return True
            
        except Exception as e:
            logger.warning(f"Failed to initialize Coqui TTS: {e}")
            return False
    
    async def _initialize_gtts(self) -> bool:
        """Initialize gTTS backend"""
        try:
            if not GTTS_AVAILABLE:
                return False
            
            logger.info("gTTS backend available")
            return True
            
        except Exception as e:
            logger.warning(f"Failed to initialize gTTS: {e}")
            return False
    
    def _get_device(self) -> str:
        """Determine the best device for processing"""
        if self.config.device == "auto":
            if torch.cuda.is_available():
                return "cuda"
            elif hasattr(torch.backends, 'mps') and torch.backends.mps.is_available():
                return "mps"
            else:
                return "cpu"
        return self.config.device
    
    def _get_coqui_model_for_language(self, language: str) -> str:
        """Get appropriate Coqui TTS model for language"""
        language_models = {
            "en-US": "tts_models/en/ljspeech/tacotron2-DDC",
            "en-GB": "tts_models/en/ljspeech/tacotron2-DDC", 
            "es-ES": "tts_models/es/mai/tacotron2-DDC",
            "fr-FR": "tts_models/fr/mai/tacotron2-DDC",
            "de-DE": "tts_models/de/mai/tacotron2-DDC",
            "it-IT": "tts_models/it/mai/tacotron2-DDC",
            "pt-BR": "tts_models/pt/cv/vits",
            "ru-RU": "tts_models/ru/mai/tacotron2-DDC",
            "ja-JP": "tts_models/ja/kokoro/tacotron2-DDC",
            "ko-KR": "tts_models/ko/kss/tacotron2-DDC",
            "zh-CN": "tts_models/zh-CN/baker/tacotron2-DDC"
        }
        return language_models.get(language, "tts_models/en/ljspeech/tacotron2-DDC")
    
    async def _load_available_voices(self):
        """Load list of available voices"""
        try:
            self.available_voices = ["default"]
            
            if self.backend == "chatterbox" and self.chatterbox_tts:
                # Add Chatterbox voices
                chatterbox_voices = await asyncio.get_event_loop().run_in_executor(
                    self.executor,
                    lambda: self.chatterbox_tts.list_voices() if hasattr(self.chatterbox_tts, 'list_voices') else []
                )
                self.available_voices.extend(chatterbox_voices)
            
            elif self.backend == "coqui" and self.coqui_tts:
                # Add Coqui voices
                self.available_voices.extend(["male", "female"])
            
            elif self.backend == "gtts":
                # gTTS uses default voice
                pass
            
            # Add custom voice samples
            for voice_name in self.voice_samples.keys():
                if voice_name not in self.available_voices:
                    self.available_voices.append(voice_name)
            
            logger.info(f"Loaded {len(self.available_voices)} available voices")
            
        except Exception as e:
            logger.error(f"Failed to load available voices: {e}")
            self.available_voices = ["default"]
    
    def _setup_voice_samples_directory(self):
        """Set up directory for voice samples"""
        try:
            self.voice_samples_dir = Path.cwd() / "voice_samples"
            self.voice_samples_dir.mkdir(exist_ok=True)
            
            # Load existing voice samples
            for sample_file in self.voice_samples_dir.glob("*.wav"):
                voice_name = sample_file.stem
                self.voice_samples[voice_name] = sample_file
            
            logger.info(f"Voice samples directory: {self.voice_samples_dir}")
            
        except Exception as e:
            logger.error(f"Failed to setup voice samples directory: {e}")
    
    async def synthesize_speech(self, text: str, voice: str = None, speed: float = None,
                               volume: float = None, language: str = None, 
                               emotion: float = None, pacing: float = None) -> Dict[str, Any]:
        """Synthesize speech from text"""
        try:
            start_time = time.time()
            
            # Use parameters or config defaults
            voice = voice or self.config.voice
            speed = speed or self.config.speed
            volume = volume or self.config.volume
            language = language or self.config.language
            emotion = emotion or self.config.emotion
            pacing = pacing or self.config.pacing
            
            # Synthesize based on backend
            if self.backend == "chatterbox":
                result = await self._synthesize_with_chatterbox(
                    text, voice, speed, volume, language, emotion, pacing
                )
            elif self.backend == "coqui":
                result = await self._synthesize_with_coqui(
                    text, voice, speed, volume, language
                )
            elif self.backend == "gtts":
                result = await self._synthesize_with_gtts(
                    text, speed, volume, language
                )
            else:
                result = await self._synthesize_fallback(text)
            
            processing_time = time.time() - start_time
            result["processing_time"] = processing_time
            
            logger.debug(f"Synthesized speech in {processing_time:.2f}s: '{text[:50]}...'")
            
            return result
            
        except Exception as e:
            logger.error(f"Speech synthesis failed: {e}")
            logger.error(traceback.format_exc())
            return {
                "audio_data": "",
                "duration": 0.0,
                "sample_rate": self.config.sample_rate,
                "channels": self.config.channels,
                "error": str(e),
                "processing_time": time.time() - start_time
            }
    
    async def _synthesize_with_chatterbox(self, text: str, voice: str, speed: float,
                                         volume: float, language: str, emotion: float,
                                         pacing: float) -> Dict[str, Any]:
        """Synthesize speech using Chatterbox TTS"""
        try:
            if not self.chatterbox_tts:
                raise Exception("Chatterbox TTS not initialized")
            
            # Check if voice is a custom sample
            voice_sample_path = None
            if voice in self.voice_samples:
                voice_sample_path = str(self.voice_samples[voice])
            
            # Synthesize in executor
            audio_data = await asyncio.get_event_loop().run_in_executor(
                self.executor,
                lambda: self.chatterbox_tts.synthesize(
                    text=text,
                    voice_sample=voice_sample_path,
                    emotion=emotion,
                    pacing=pacing,
                    speed=speed,
                    language=language
                )
            )
            
            # Process audio data
            if isinstance(audio_data, np.ndarray):
                # Adjust volume
                audio_data = audio_data * volume
                
                # Convert to base64
                audio_base64 = await self._audio_array_to_base64(
                    audio_data, self.config.sample_rate
                )
                
                # Calculate duration
                duration = len(audio_data) / self.config.sample_rate
                
                return {
                    "audio_data": audio_base64,
                    "duration": duration,
                    "sample_rate": self.config.sample_rate,
                    "channels": self.config.channels,
                    "voice": voice,
                    "backend": "chatterbox"
                }
            else:
                raise Exception("Invalid audio data from Chatterbox TTS")
                
        except Exception as e:
            logger.error(f"Chatterbox TTS synthesis failed: {e}")
            raise
    
    async def _synthesize_with_coqui(self, text: str, voice: str, speed: float,
                                    volume: float, language: str) -> Dict[str, Any]:
        """Synthesize speech using Coqui TTS"""
        try:
            if not self.coqui_tts:
                raise Exception("Coqui TTS not initialized")
            
            # Generate audio in executor
            with tempfile.NamedTemporaryFile(suffix='.wav', delete=False) as tmp_file:
                output_path = tmp_file.name
            
            await asyncio.get_event_loop().run_in_executor(
                self.executor,
                lambda: self.coqui_tts.tts_to_file(
                    text=text,
                    file_path=output_path,
                    speaker=voice if voice != "default" else None
                )
            )
            
            # Load and process audio
            audio_data, sample_rate = await asyncio.get_event_loop().run_in_executor(
                self.executor,
                lambda: sf.read(output_path)
            )
            
            # Clean up temp file
            os.unlink(output_path)
            
            # Adjust speed and volume
            if speed != 1.0:
                audio_data = await self._adjust_speed(audio_data, speed)
            
            audio_data = audio_data * volume
            
            # Resample if needed
            if sample_rate != self.config.sample_rate:
                audio_data = await self._resample_audio(audio_data, sample_rate, self.config.sample_rate)
            
            # Convert to base64
            audio_base64 = await self._audio_array_to_base64(audio_data, self.config.sample_rate)
            
            duration = len(audio_data) / self.config.sample_rate
            
            return {
                "audio_data": audio_base64,
                "duration": duration,
                "sample_rate": self.config.sample_rate,
                "channels": self.config.channels,
                "voice": voice,
                "backend": "coqui"
            }
            
        except Exception as e:
            logger.error(f"Coqui TTS synthesis failed: {e}")
            raise
    
    async def _synthesize_with_gtts(self, text: str, speed: float, volume: float,
                                   language: str) -> Dict[str, Any]:
        """Synthesize speech using gTTS"""
        try:
            # Normalize language for gTTS
            gtts_lang = language.split("-")[0]  # en-US -> en
            
            # Generate TTS in executor
            with tempfile.NamedTemporaryFile(suffix='.mp3', delete=False) as tmp_file:
                output_path = tmp_file.name
            
            await asyncio.get_event_loop().run_in_executor(
                self.executor,
                lambda: gTTS(text=text, lang=gtts_lang, slow=False).save(output_path)
            )
            
            # Load audio using pydub
            audio_segment = await asyncio.get_event_loop().run_in_executor(
                self.executor,
                lambda: AudioSegment.from_mp3(output_path)
            )
            
            # Clean up temp file
            os.unlink(output_path)
            
            # Adjust speed and volume
            if speed != 1.0:
                # Adjust playback speed
                new_sample_rate = int(audio_segment.frame_rate * speed)
                audio_segment = audio_segment._spawn(audio_segment.raw_data, overrides={"frame_rate": new_sample_rate})
                audio_segment = audio_segment.set_frame_rate(self.config.sample_rate)
            
            # Adjust volume
            if volume != 1.0:
                volume_db = 20 * np.log10(volume) if volume > 0 else -60
                audio_segment = audio_segment + volume_db
            
            # Convert to numpy array
            audio_data = np.array(audio_segment.get_array_of_samples(), dtype=np.float32)
            audio_data = audio_data / (2**15)  # Normalize int16 to float32
            
            # Handle stereo to mono
            if audio_segment.channels == 2:
                audio_data = audio_data.reshape(-1, 2)
                audio_data = np.mean(audio_data, axis=1)
            
            # Convert to base64
            audio_base64 = await self._audio_array_to_base64(audio_data, self.config.sample_rate)
            
            duration = len(audio_data) / self.config.sample_rate
            
            return {
                "audio_data": audio_base64,
                "duration": duration,
                "sample_rate": self.config.sample_rate,
                "channels": self.config.channels,
                "voice": "default",
                "backend": "gtts"
            }
            
        except Exception as e:
            logger.error(f"gTTS synthesis failed: {e}")
            raise
    
    async def _synthesize_fallback(self, text: str) -> Dict[str, Any]:
        """Fallback synthesis when no TTS backend is available"""
        try:
            # Generate a simple beep or silence as fallback
            duration = len(text) * 0.1  # Rough estimation
            sample_count = int(duration * self.config.sample_rate)
            
            # Generate simple tone pattern based on text length
            t = np.linspace(0, duration, sample_count)
            frequency = 440  # A4 note
            audio_data = 0.1 * np.sin(2 * np.pi * frequency * t)
            
            # Add some variation based on text
            for i, char in enumerate(text[:10]):
                freq_offset = ord(char) * 2
                audio_data += 0.05 * np.sin(2 * np.pi * (frequency + freq_offset) * t)
            
            # Normalize
            audio_data = audio_data / np.max(np.abs(audio_data))
            audio_data = audio_data * 0.3  # Reduce volume
            
            # Convert to base64
            audio_base64 = await self._audio_array_to_base64(audio_data, self.config.sample_rate)
            
            return {
                "audio_data": audio_base64,
                "duration": duration,
                "sample_rate": self.config.sample_rate,
                "channels": self.config.channels,
                "voice": "fallback",
                "backend": "fallback"
            }
            
        except Exception as e:
            logger.error(f"Fallback synthesis failed: {e}")
            raise
    
    async def _audio_array_to_base64(self, audio_data: np.ndarray, sample_rate: int) -> str:
        """Convert audio array to base64 WAV format"""
        try:
            # Ensure audio is in correct format
            if audio_data.dtype != np.float32:
                audio_data = audio_data.astype(np.float32)
            
            # Convert to int16 for WAV
            audio_int16 = (audio_data * 32767).astype(np.int16)
            
            # Create WAV file in memory
            with io.BytesIO() as wav_buffer:
                with wave.open(wav_buffer, 'wb') as wav_file:
                    wav_file.setnchannels(self.config.channels)
                    wav_file.setsampwidth(2)  # 2 bytes for int16
                    wav_file.setframerate(sample_rate)
                    wav_file.writeframes(audio_int16.tobytes())
                
                wav_buffer.seek(0)
                wav_bytes = wav_buffer.read()
            
            # Convert to base64
            return base64.b64encode(wav_bytes).decode('utf-8')
            
        except Exception as e:
            logger.error(f"Failed to convert audio to base64: {e}")
            raise
    
    async def _adjust_speed(self, audio_data: np.ndarray, speed: float) -> np.ndarray:
        """Adjust audio playback speed"""
        try:
            if speed == 1.0:
                return audio_data
            
            # Use simple resampling for speed adjustment
            new_length = int(len(audio_data) / speed)
            indices = np.linspace(0, len(audio_data) - 1, new_length)
            return np.interp(indices, np.arange(len(audio_data)), audio_data)
            
        except Exception as e:
            logger.error(f"Speed adjustment failed: {e}")
            return audio_data
    
    async def _resample_audio(self, audio: np.ndarray, original_rate: int, target_rate: int) -> np.ndarray:
        """Resample audio to target sample rate"""
        try:
            if original_rate == target_rate:
                return audio
            
            # Use torchaudio for resampling
            audio_tensor = torch.from_numpy(audio).unsqueeze(0)  # Add channel dimension
            
            resampler = torchaudio.transforms.Resample(
                orig_freq=original_rate,
                new_freq=target_rate
            )
            
            resampled = await asyncio.get_event_loop().run_in_executor(
                self.executor,
                lambda: resampler(audio_tensor)
            )
            
            return resampled.squeeze(0).numpy()
            
        except Exception as e:
            logger.error(f"Audio resampling failed: {e}")
            return audio
    
    async def clone_voice(self, reference_audio: str, voice_name: str) -> str:
        """Clone a voice from reference audio"""
        try:
            if not self.config.voice_clone_enabled:
                raise Exception("Voice cloning is disabled")
            
            if self.backend != "chatterbox":
                raise Exception("Voice cloning only supported with Chatterbox TTS")
            
            # Decode reference audio
            audio_bytes = base64.b64decode(reference_audio)
            
            # Save reference audio
            reference_path = self.voice_samples_dir / f"{voice_name}.wav"
            with open(reference_path, 'wb') as f:
                f.write(audio_bytes)
            
            # Validate audio quality
            audio_info = await self.get_voice_sample_info(str(reference_path))
            if audio_info["duration"] < 3.0:
                logger.warning(f"Short reference audio for {voice_name}: {audio_info['duration']:.1f}s")
            
            # Store voice sample
            self.voice_samples[voice_name] = reference_path
            
            # Update available voices
            if voice_name not in self.available_voices:
                self.available_voices.append(voice_name)
            
            logger.info(f"Voice cloned successfully: {voice_name}")
            return str(reference_path)
            
        except Exception as e:
            logger.error(f"Voice cloning failed for {voice_name}: {e}")
            raise
    
    async def get_voice_sample_info(self, audio_path: str) -> Dict[str, Any]:
        """Analyze voice sample audio"""
        try:
            # Load audio file
            audio_data, sample_rate = await asyncio.get_event_loop().run_in_executor(
                self.executor,
                lambda: sf.read(audio_path)
            )
            
            duration = len(audio_data) / sample_rate
            
            # Basic audio analysis
            rms_level = np.sqrt(np.mean(audio_data**2))
            max_amplitude = np.max(np.abs(audio_data))
            
            return {
                "duration": duration,
                "sample_rate": sample_rate,
                "channels": audio_data.ndim,
                "samples": len(audio_data),
                "rms_level": float(rms_level),
                "max_amplitude": float(max_amplitude),
                "file_size": os.path.getsize(audio_path)
            }
            
        except Exception as e:
            logger.error(f"Failed to analyze voice sample: {e}")
            return {
                "duration": 0.0,
                "sample_rate": 0,
                "channels": 0,
                "error": str(e)
            }
    
    def list_available_voices(self) -> List[str]:
        """Get list of available voices"""
        return self.available_voices.copy()
    
    def save_voice_sample(self, audio_data: bytes, name: str) -> str:
        """Save voice sample for later use"""
        try:
            sample_path = self.voice_samples_dir / f"{name}.wav"
            with open(sample_path, 'wb') as f:
                f.write(audio_data)
            
            self.voice_samples[name] = sample_path
            
            if name not in self.available_voices:
                self.available_voices.append(name)
            
            logger.info(f"Voice sample saved: {name}")
            return str(sample_path)
            
        except Exception as e:
            logger.error(f"Failed to save voice sample {name}: {e}")
            raise
    
    def delete_voice_sample(self, name: str) -> bool:
        """Delete a voice sample"""
        try:
            if name in self.voice_samples:
                sample_path = self.voice_samples[name]
                if sample_path.exists():
                    sample_path.unlink()
                
                del self.voice_samples[name]
                
                if name in self.available_voices:
                    self.available_voices.remove(name)
                
                logger.info(f"Voice sample deleted: {name}")
                return True
            
            return False
            
        except Exception as e:
            logger.error(f"Failed to delete voice sample {name}: {e}")
            return False
    
    def adjust_audio_settings(self, sample_rate: int = None, channels: int = None):
        """Adjust audio output settings"""
        if sample_rate:
            self.config.sample_rate = sample_rate
        if channels:
            self.config.channels = channels
        
        logger.info(f"Audio settings updated: {self.config.sample_rate}Hz, {self.config.channels} channels")
    
    async def cleanup(self):
        """Cleanup TTS service resources"""
        try:
            logger.info("Cleaning up TTS service...")
            
            # Cleanup backends
            if self.chatterbox_tts:
                try:
                    if hasattr(self.chatterbox_tts, 'cleanup'):
                        await asyncio.get_event_loop().run_in_executor(
                            self.executor,
                            self.chatterbox_tts.cleanup
                        )
                except Exception as e:
                    logger.warning(f"Error cleaning up Chatterbox TTS: {e}")
            
            if self.coqui_tts:
                try:
                    del self.coqui_tts
                except Exception as e:
                    logger.warning(f"Error cleaning up Coqui TTS: {e}")
            
            # Shutdown executor
            self.executor.shutdown(wait=True)
            
            self.is_initialized = False
            logger.info("TTS service cleanup completed")
            
        except Exception as e:
            logger.error(f"Error during TTS service cleanup: {e}")
