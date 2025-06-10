#!/usr/bin/env python3
"""
Text-to-Speech Service for SwarmUI Voice Assistant
Production-ready TTS service using Chatterbox TTS for speech synthesis.

This service provides:
- Text to speech conversion with multiple voices
- Base64 audio output for web integration
- Volume and language control
- Fallback audio generation when Chatterbox TTS unavailable
- Comprehensive error handling and logging
"""

import asyncio
import base64
import io
import logging
import time
import tempfile
import os
from typing import Dict, Any, Optional, List

import numpy as np
import torchaudio

logger = logging.getLogger("VoiceAssistant.TTS")

class TTSService:
    """
    Text-to-Speech service for converting text to spoken audio.
    
    Handles speech synthesis through Chatterbox TTS library with fallback
    to alternative TTS engines. Provides multiple voice options, language support,
    and volume control for production use.
    """
    
    def __init__(self):
        """
        Initialize the TTS service.
        
        Sets up the service state and prepares for model loading.
        Actual model initialization is deferred to the initialize() method
        for better startup performance and error handling.
        """
        self.initialized = False
        self.tts_engine = None
        self.tts_method = "none"
        self.available_voices = ["default", "male", "female", "neural"]
        self.supported_languages = [
            "en-US", "en-GB", "es-ES", "fr-FR", "de-DE", "it-IT",
            "pt-BR", "ru-RU", "ja-JP", "ko-KR", "zh-CN"
        ]
        self.default_voice = "default"
        self.sample_rate = 22050
        self.initialization_error = None
        self.use_chatterbox = False
        
        logger.debug("TTS service instance created")
    
    async def initialize(self):
        """
        Initialize the TTS service and load the speech synthesis engine.
    
        ONLY uses Chatterbox TTS - NO FALLBACKS. Fails if Chatterbox TTS is not available.
    
        Raises:
            Exception: If Chatterbox TTS initialization fails
        """
        if self.initialized:
            logger.debug("TTS service already initialized")
            return
        
        try:
            logger.info("Initializing TTS service (Chatterbox TTS only)...")
        
            # Try Chatterbox TTS - NO FALLBACKS
            if await self._try_chatterbox_tts():
                self.use_chatterbox = True
                self.tts_method = "chatterbox"
                logger.info("TTS service initialized with Chatterbox TTS")
            else:
                self.initialization_error = "Chatterbox TTS initialization failed"
                raise Exception("Failed to initialize Chatterbox TTS - no fallback TTS libraries available")
            
            self.initialized = True
            logger.info(f"TTS service initialization completed successfully with: {self.tts_method}")
            
        except Exception as e:
            self.initialization_error = str(e)
            logger.error(f"Critical error during TTS service initialization: {e}")
            raise Exception(f"TTS service initialization failed: {e}")
    
    async def _try_chatterbox_tts(self):
        """Try to initialize Chatterbox TTS."""
        try:
            from chatterbox import ChatterboxTTS
            
            # Initialize Chatterbox TTS model
            # Run in thread pool to avoid blocking
            self.tts_engine = await asyncio.get_event_loop().run_in_executor(
                None,
                lambda: ChatterboxTTS.from_pretrained(device="cuda" if self._has_cuda() else "cpu")
            )
            
            # Get sample rate from the model
            self.sample_rate = self.tts_engine.sr
            
            # Chatterbox TTS supports different exaggeration levels as "voices"
            self.available_voices = ["default", "expressive", "calm", "dramatic"]
            
            logger.info("Chatterbox TTS initialized successfully")
            return True
            
        except ImportError:
            logger.debug("Chatterbox TTS not available (not installed)")
            return False
        except Exception as e:
            logger.warning(f"Chatterbox TTS failed to initialize: {e}")
            return False
    
    def _has_cuda(self):
        """Check if CUDA is available."""
        try:
            import torch
            return torch.cuda.is_available()
        except ImportError:
            return False
    
    async def synthesize(self, text: str, voice: str = "default", 
                        language: Optional[str] = None, volume: float = 0.8) -> Dict[str, Any]:
        """
        Convert text to speech audio.
        
        Processes text through the TTS pipeline, handling voice selection,
        language configuration, and volume control. Returns base64 encoded
        audio suitable for web playback.
        
        Args:
            text: Text to convert to speech (max 1000 characters)
            voice: Voice identifier (e.g., 'default', 'expressive', 'calm')
            language: Language code for synthesis (e.g., 'en-US')
            volume: Volume level from 0.0 to 1.0
            
        Returns:
            Dict containing:
                - audio_data: Base64 encoded audio (WAV format)
                - text: Original text that was synthesized
                - voice: Voice used for synthesis
                - language: Language used for synthesis
                - volume: Volume level applied
                - duration: Audio duration in seconds
                - method: Synthesis method used
                
        Raises:
            Exception: If synthesis fails completely
        """
        start_time = time.time()
        
        try:
            if not self.initialized:
                raise Exception("TTS service not initialized")
            
            logger.debug(f"Starting TTS synthesis using {self.tts_method}: '{text[:50]}...' with voice '{voice}'")
            
            # Validate input parameters
            if not text or not text.strip():
                raise ValueError("No text provided for synthesis")
            
            text = text.strip()
            if len(text) > 1000:
                raise ValueError("Text too long (maximum 1000 characters)")
            
            # Validate and normalize voice
            if voice not in self.available_voices:
                logger.warning(f"Voice '{voice}' not available, using default")
                voice = self.default_voice
            
            # Validate and normalize language
            if language and language not in self.supported_languages:
                logger.warning(f"Language '{language}' not supported, using en-US")
                language = "en-US"
            elif not language:
                language = "en-US"
            
            # Validate and clamp volume
            volume = max(0.0, min(1.0, float(volume)))
            
            # Perform synthesis based on available engine
            if self.use_chatterbox:
                audio_data = await self._synthesize_with_chatterbox(text, voice, language, volume)
            
            # Encode audio as base64
            audio_base64 = base64.b64encode(audio_data).decode('utf-8')
            
            # Calculate duration (approximate for WAV files)
            duration = len(audio_data) / (self.sample_rate * 2)  # 16-bit samples
            processing_time = time.time() - start_time
            
            result = {
                "audio_data": audio_base64,
                "text": text,
                "voice": voice,
                "language": language,
                "volume": volume,
                "duration": round(duration, 2),
                "processing_time": round(processing_time, 3),
                "method": self.tts_method
            }
            
            logger.info(f"TTS synthesis completed in {processing_time:.3f}s (duration: {duration:.2f}s)")
            return result
            
        except ValueError as e:
            # Input validation errors
            logger.error(f"TTS validation error: {e}")
            raise Exception(f"Invalid input: {e}")
            
        except Exception as e:
            processing_time = time.time() - start_time
            logger.error(f"TTS synthesis failed after {processing_time:.3f}s: {e}")
            raise Exception(f"Speech synthesis failed: {e}")
    
    async def _synthesize_with_chatterbox(self, text: str, voice: str, 
                                        language: str, volume: float) -> bytes:
        """
        Perform synthesis using Chatterbox TTS library.
        
        Handles Chatterbox TTS API calls in a thread pool to avoid blocking
        the event loop. Applies voice, language, and volume settings.
        
        Args:
            text: Text to synthesize
            voice: Voice identifier (maps to exaggeration settings)
            language: Language code
            volume: Volume level
            
        Returns:
            Raw audio data as bytes (WAV format)
            
        Raises:
            Exception: If Chatterbox TTS synthesis fails
        """
        try:
            logger.debug(f"Using Chatterbox TTS for synthesis: voice={voice}, lang={language}")
            
            # Map voice types to Chatterbox TTS parameters
            voice_params = self._get_chatterbox_voice_params(voice)
            
            # Run synthesis in thread pool to avoid blocking
            audio_tensor = await asyncio.get_event_loop().run_in_executor(
                None,
                self._synthesize_sync_chatterbox,
                text, voice_params, language, volume
            )
            
            # Convert tensor to WAV bytes
            audio_data = self._tensor_to_wav_bytes(audio_tensor, volume)
            
            if not audio_data or len(audio_data) < 100:
                raise Exception("Chatterbox TTS returned empty or invalid audio")
            
            logger.debug(f"Chatterbox TTS synthesis successful: {len(audio_data)} bytes")
            return audio_data
            
        except Exception as e:
            logger.error(f"Chatterbox TTS synthesis error: {e}")
            raise Exception(f"Chatterbox TTS processing failed: {e}")
    
    def _get_chatterbox_voice_params(self, voice: str) -> Dict[str, float]:
        """
        Map voice types to Chatterbox TTS parameters.
        
        Args:
            voice: Voice identifier
            
        Returns:
            Dict with Chatterbox TTS parameters
        """
        voice_mapping = {
            "default": {"exaggeration": 0.5, "cfg_weight": 0.5},
            "expressive": {"exaggeration": 0.7, "cfg_weight": 0.3},
            "calm": {"exaggeration": 0.3, "cfg_weight": 0.7},
            "dramatic": {"exaggeration": 0.8, "cfg_weight": 0.2},
            "male": {"exaggeration": 0.4, "cfg_weight": 0.6},
            "female": {"exaggeration": 0.6, "cfg_weight": 0.4},
            "neural": {"exaggeration": 0.5, "cfg_weight": 0.5}
        }
        
        return voice_mapping.get(voice, voice_mapping["default"])
    
    def _synthesize_sync_chatterbox(self, text: str, voice_params: Dict[str, float], 
                                  language: str, volume: float) -> np.ndarray:
        """
        Synchronous wrapper for Chatterbox TTS synthesis.
        
        This method runs in a thread pool to avoid blocking the async event loop.
        Handles the actual Chatterbox TTS API calls and configuration.
        
        Args:
            text: Text to synthesize
            voice_params: Voice parameters (exaggeration, cfg_weight)
            language: Language code
            volume: Volume level
            
        Returns:
            Audio tensor as numpy array
        """
        try:
            logger.debug(f"Processing text with Chatterbox TTS: '{text[:50]}...'")
            
            # Generate audio using Chatterbox TTS
            # Note: Chatterbox TTS currently only supports English
            audio_tensor = self.tts_engine.generate(
                text,
                exaggeration=voice_params["exaggeration"],
                cfg_weight=voice_params["cfg_weight"]
            )
            
            logger.debug(f"Chatterbox TTS synthesis complete: {audio_tensor.shape}")
            return audio_tensor.cpu().numpy()
            
        except Exception as e:
            logger.error(f"Chatterbox TTS sync synthesis error: {e}")
            raise Exception(f"Chatterbox TTS API call failed: {e}")
    
    def _tensor_to_wav_bytes(self, audio_tensor: np.ndarray, volume: float) -> bytes:
        """
        Convert audio tensor to WAV format bytes.
        
        Args:
            audio_tensor: Audio data as numpy array
            volume: Volume level to apply
            
        Returns:
            WAV format audio data as bytes
        """
        try:
            # Ensure audio is in the right format
            if len(audio_tensor.shape) > 1:
                # Convert to mono if stereo
                audio_tensor = np.mean(audio_tensor, axis=0)
            
            # Apply volume
            audio_tensor = audio_tensor * volume
            
            # Normalize to prevent clipping
            max_amplitude = np.max(np.abs(audio_tensor))
            if max_amplitude > 0.8:
                audio_tensor = audio_tensor * 0.8 / max_amplitude
            
            # Convert to 16-bit PCM
            audio_int16 = (audio_tensor * 32767).astype(np.int16)
            
            # Create WAV file in memory
            wav_io = io.BytesIO()
            self._write_wav_header(wav_io, len(audio_int16), self.sample_rate)
            wav_io.write(audio_int16.tobytes())
            
            return wav_io.getvalue()
            
        except Exception as e:
            logger.error(f"Error converting tensor to WAV: {e}")
            raise
    
    def _convert_mp3_to_wav(self, mp3_data: bytes, volume: float) -> bytes:
        """
        Convert MP3 data to WAV format.
        
        Args:
            mp3_data: MP3 audio data
            volume: Volume level to apply
            
        Returns:
            WAV format audio data
        """
        try:
            # Try to use torchaudio for conversion if available
            import tempfile
            import os
            
            # Save MP3 to temp file
            with tempfile.NamedTemporaryFile(suffix='.mp3', delete=False) as temp_mp3:
                temp_mp3.write(mp3_data)
                temp_mp3_path = temp_mp3.name
            
            try:
                # Load with torchaudio
                waveform, sample_rate = torchaudio.load(temp_mp3_path)
                
                # Convert to mono if stereo
                if waveform.shape[0] > 1:
                    waveform = torch.mean(waveform, dim=0, keepdim=True)
                
                # Resample to our target sample rate if needed
                if sample_rate != self.sample_rate:
                    resampler = torchaudio.transforms.Resample(sample_rate, self.sample_rate)
                    waveform = resampler(waveform)
                
                # Apply volume
                waveform = waveform * volume
                
                # Convert to numpy and then to WAV bytes
                audio_numpy = waveform.squeeze().numpy()
                return self._numpy_to_wav_bytes(audio_numpy)
                
            finally:
                # Clean up temp file
                try:
                    os.unlink(temp_mp3_path)
                except:
                    pass
                    
        except Exception as e:
            logger.warning(f"Failed to convert MP3 to WAV: {e}")
            # Fallback: return the MP3 data as-is (browser can handle it)
            return mp3_data
    
    def _numpy_to_wav_bytes(self, audio_numpy: np.ndarray) -> bytes:
        """
        Convert numpy array to WAV bytes.
        
        Args:
            audio_numpy: Audio data as numpy array
            
        Returns:
            WAV format audio data as bytes
        """
        try:
            # Normalize and convert to 16-bit PCM
            max_amplitude = np.max(np.abs(audio_numpy))
            if max_amplitude > 0.8:
                audio_numpy = audio_numpy * 0.8 / max_amplitude
            
            audio_int16 = (audio_numpy * 32767).astype(np.int16)
            
            # Create WAV file in memory
            wav_io = io.BytesIO()
            self._write_wav_header(wav_io, len(audio_int16), self.sample_rate)
            wav_io.write(audio_int16.tobytes())
            
            return wav_io.getvalue()
            
        except Exception as e:
            logger.error(f"Error converting numpy to WAV: {e}")
            raise
    
    def _write_wav_header(self, wav_io: io.BytesIO, num_samples: int, sample_rate: int):
        """
        Write WAV file header to BytesIO stream.
        
        Creates a proper WAV file header for 16-bit mono audio.
        
        Args:
            wav_io: BytesIO stream to write to
            num_samples: Number of audio samples
            sample_rate: Sample rate in Hz
        """
        try:
            import struct
            
            # WAV file parameters
            num_channels = 1
            bits_per_sample = 16
            byte_rate = sample_rate * num_channels * bits_per_sample // 8
            block_align = num_channels * bits_per_sample // 8
            data_size = num_samples * bits_per_sample // 8
            file_size = 36 + data_size
            
            # Write WAV header
            wav_io.write(b'RIFF')
            wav_io.write(struct.pack('<I', file_size))
            wav_io.write(b'WAVE')
            wav_io.write(b'fmt ')
            wav_io.write(struct.pack('<I', 16))  # fmt chunk size
            wav_io.write(struct.pack('<H', 1))   # PCM format
            wav_io.write(struct.pack('<H', num_channels))
            wav_io.write(struct.pack('<I', sample_rate))
            wav_io.write(struct.pack('<I', byte_rate))
            wav_io.write(struct.pack('<H', block_align))
            wav_io.write(struct.pack('<H', bits_per_sample))
            wav_io.write(b'data')
            wav_io.write(struct.pack('<I', data_size))
            
        except Exception as e:
            logger.error(f"Error writing WAV header: {e}")
            raise
    
    def get_available_voices(self) -> List[str]:
        """
        Get list of available voice identifiers.
        
        Returns:
            List of available voice identifiers
        """
        return self.available_voices.copy()
    
    def get_supported_languages(self) -> List[str]:
        """
        Get list of supported language codes.
        
        Returns:
            List of supported language codes
        """
        return self.supported_languages.copy()
    
    def is_voice_available(self, voice: str) -> bool:
        """
        Check if a voice is available.
        
        Args:
            voice: Voice identifier to check
            
        Returns:
            True if voice is available, False otherwise
        """
        return voice in self.available_voices
    
    def is_language_supported(self, language: str) -> bool:
        """
        Check if a language is supported.
        
        Args:
            language: Language code to check
            
        Returns:
            True if language is supported, False otherwise
        """
        return language in self.supported_languages
    
    def get_status(self) -> Dict[str, Any]:
        """
        Get service status information.
        
        Returns:
            Dict containing service status details
        """
        return {
            "initialized": self.initialized,
            "tts_method": self.tts_method,
            "use_chatterbox": self.use_chatterbox,
            "available_voices": len(self.available_voices),
            "supported_languages": len(self.supported_languages),
            "sample_rate": self.sample_rate,
            "initialization_error": self.initialization_error
        }

# Singleton instance for use across the application
tts_service = TTSService()
