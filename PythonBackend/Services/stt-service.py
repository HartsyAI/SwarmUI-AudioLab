"""
STT Service - Speech-to-Text Processing
SwarmUI VoiceAssistant Extension - RealtimeSTT Wrapper

This module provides speech-to-text functionality using RealtimeSTT library
with support for real-time transcription, multiple models, and language detection.
"""

import asyncio
import base64
import io
import time
import traceback
from typing import Dict, List, Optional, Any, Callable
from concurrent.futures import ThreadPoolExecutor
from dataclasses import dataclass
from pathlib import Path

import numpy as np
import torch
import torchaudio
from loguru import logger
from RealtimeSTT import AudioToTextRecorder
import speech_recognition as sr
from faster_whisper import WhisperModel
import wave
import tempfile


@dataclass
class STTConfig:
    """Configuration for STT service"""
    model: str = "openai/whisper-base"
    language: str = "en-US"
    confidence_threshold: float = 0.7
    enable_realtime: bool = True
    sample_rate: int = 16000
    channels: int = 1
    chunk_duration: float = 0.5
    buffer_size: int = 1024
    device: str = "auto"  # auto, cpu, cuda
    compute_type: str = "float16"  # float16, int8, float32


class STTService:
    """Speech-to-text service using RealtimeSTT and Whisper models"""
    
    def __init__(self, config: STTConfig):
        self.config = config
        self.recorder: Optional[AudioToTextRecorder] = None
        self.whisper_model: Optional[WhisperModel] = None
        self.sr_recognizer: Optional[sr.Recognizer] = None
        self.executor = ThreadPoolExecutor(max_workers=4)
        self.active_sessions: Dict[str, Dict[str, Any]] = {}
        self.realtime_sessions: Dict[str, Dict[str, Any]] = {}
        self.is_initialized = False
        
        # Callbacks
        self.transcription_callbacks: List[Callable] = []
        self.realtime_callbacks: List[Callable] = []
        
        logger.info("STT Service initialized")
    
    async def initialize(self) -> bool:
        """Initialize the STT service with models and dependencies"""
        try:
            logger.info("Initializing STT service...")
            
            # Determine device
            device = self._get_device()
            logger.info(f"Using device: {device}")
            
            # Initialize Whisper model for file transcription
            await self._initialize_whisper_model()
            
            # Initialize RealtimeSTT recorder
            await self._initialize_realtime_stt()
            
            # Initialize speech_recognition as fallback
            self._initialize_sr_fallback()
            
            self.is_initialized = True
            logger.info("STT service initialized successfully")
            return True
            
        except Exception as e:
            logger.error(f"Failed to initialize STT service: {e}")
            logger.error(traceback.format_exc())
            return False
    
    async def _initialize_whisper_model(self):
        """Initialize Whisper model for transcription"""
        try:
            device = self._get_device()
            model_name = self._normalize_model_name(self.config.model)
            
            logger.info(f"Loading Whisper model: {model_name}")
            
            # Load model in executor to avoid blocking
            self.whisper_model = await asyncio.get_event_loop().run_in_executor(
                self.executor,
                lambda: WhisperModel(
                    model_name,
                    device=device,
                    compute_type=self.config.compute_type
                )
            )
            
            logger.info("Whisper model loaded successfully")
            
        except Exception as e:
            logger.error(f"Failed to load Whisper model: {e}")
            raise
    
    async def _initialize_realtime_stt(self):
        """Initialize RealtimeSTT recorder"""
        try:
            logger.info("Initializing RealtimeSTT recorder...")
            
            # Configuration for RealtimeSTT
            recorder_config = {
                'model': self._normalize_model_name(self.config.model),
                'language': self._normalize_language(self.config.language),
                'silero_sensitivity': 0.4,
                'webrtc_sensitivity': 3,
                'post_speech_silence_duration': 0.7,
                'min_length_of_recording': 0.8,
                'min_gap_between_recordings': 0.1,
                'enable_realtime_transcription': True,
                'realtime_processing_pause': 0.2,
                'realtime_model_type': 'tiny.en' if 'en' in self.config.language else 'tiny',
            }
            
            # Initialize in executor to avoid blocking
            self.recorder = await asyncio.get_event_loop().run_in_executor(
                self.executor,
                lambda: AudioToTextRecorder(**recorder_config)
            )
            
            # Set up callbacks
            self.recorder.on_transcription_finished = self._on_transcription_finished
            self.recorder.on_realtime_transcription_update = self._on_realtime_transcription_update
            
            logger.info("RealtimeSTT recorder initialized successfully")
            
        except Exception as e:
            logger.error(f"Failed to initialize RealtimeSTT: {e}")
            # Continue without real-time STT
            self.recorder = None
    
    def _initialize_sr_fallback(self):
        """Initialize speech_recognition as fallback"""
        try:
            self.sr_recognizer = sr.Recognizer()
            self.sr_recognizer.energy_threshold = 300
            self.sr_recognizer.dynamic_energy_threshold = True
            self.sr_recognizer.pause_threshold = 0.8
            self.sr_recognizer.operation_timeout = 1
            
            logger.info("Speech recognition fallback initialized")
            
        except Exception as e:
            logger.warning(f"Failed to initialize speech recognition fallback: {e}")
    
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
    
    def _normalize_model_name(self, model: str) -> str:
        """Normalize model name for different backends"""
        model_mapping = {
            "openai/whisper-tiny": "tiny",
            "openai/whisper-base": "base", 
            "openai/whisper-small": "small",
            "openai/whisper-medium": "medium",
            "openai/whisper-large": "large",
            "openai/whisper-large-v2": "large-v2",
            "openai/whisper-large-v3": "large-v3"
        }
        return model_mapping.get(model, model.split("/")[-1])
    
    def _normalize_language(self, language: str) -> str:
        """Normalize language code"""
        language_mapping = {
            "en-US": "en",
            "en-GB": "en", 
            "es-ES": "es",
            "fr-FR": "fr",
            "de-DE": "de",
            "it-IT": "it",
            "pt-BR": "pt",
            "ru-RU": "ru",
            "ja-JP": "ja",
            "ko-KR": "ko",
            "zh-CN": "zh"
        }
        return language_mapping.get(language, language.split("-")[0])
    
    async def transcribe_audio(self, audio_data: str, language: str = None, 
                              confidence_threshold: float = None) -> Dict[str, Any]:
        """Transcribe audio data to text"""
        try:
            start_time = time.time()
            
            # Decode base64 audio data
            audio_bytes = base64.b64decode(audio_data)
            
            # Convert to audio format for processing
            audio_array = await self._convert_audio_to_array(audio_bytes)
            
            # Use language from parameter or config
            lang = self._normalize_language(language or self.config.language)
            threshold = confidence_threshold or self.config.confidence_threshold
            
            # Transcribe using Whisper model
            result = await self._transcribe_with_whisper(audio_array, lang)
            
            processing_time = time.time() - start_time
            
            # Check confidence threshold
            confidence = result.get("confidence", 0.0)
            if confidence < threshold:
                logger.warning(f"Transcription confidence {confidence:.2f} below threshold {threshold:.2f}")
            
            result.update({
                "processing_time": processing_time,
                "language": lang,
                "model_used": self.config.model
            })
            
            logger.debug(f"Transcribed audio in {processing_time:.2f}s: '{result.get('transcription', '')[:50]}...'")
            
            return result
            
        except Exception as e:
            logger.error(f"Audio transcription failed: {e}")
            logger.error(traceback.format_exc())
            return {
                "transcription": "",
                "confidence": 0.0,
                "error": str(e),
                "processing_time": time.time() - start_time
            }
    
    async def _transcribe_with_whisper(self, audio_array: np.ndarray, language: str) -> Dict[str, Any]:
        """Transcribe audio using Whisper model"""
        try:
            if not self.whisper_model:
                raise Exception("Whisper model not initialized")
            
            # Ensure audio is in correct format
            if audio_array.dtype != np.float32:
                audio_array = audio_array.astype(np.float32)
            
            # Normalize audio
            if audio_array.max() > 1.0:
                audio_array = audio_array / np.max(np.abs(audio_array))
            
            # Transcribe in executor
            segments, info = await asyncio.get_event_loop().run_in_executor(
                self.executor,
                lambda: self.whisper_model.transcribe(
                    audio_array,
                    language=language,
                    word_timestamps=True,
                    vad_filter=True,
                    vad_parameters=dict(min_silence_duration_ms=500)
                )
            )
            
            # Combine segments
            transcription = ""
            total_confidence = 0.0
            segment_count = 0
            
            for segment in segments:
                transcription += segment.text
                total_confidence += getattr(segment, 'avg_logprob', 0.0)
                segment_count += 1
            
            # Calculate average confidence (convert log probability to confidence)
            if segment_count > 0:
                avg_logprob = total_confidence / segment_count
                confidence = min(1.0, max(0.0, (avg_logprob + 1.0)))  # Normalize to 0-1
            else:
                confidence = 0.0
            
            return {
                "transcription": transcription.strip(),
                "confidence": confidence,
                "language_detected": info.language,
                "language_probability": info.language_probability,
                "segments": len(list(segments))
            }
            
        except Exception as e:
            logger.error(f"Whisper transcription failed: {e}")
            raise
    
    async def _convert_audio_to_array(self, audio_bytes: bytes) -> np.ndarray:
        """Convert audio bytes to numpy array"""
        try:
            # Try to load as wave file first
            try:
                with io.BytesIO(audio_bytes) as audio_io:
                    with wave.open(audio_io, 'rb') as wav_file:
                        frames = wav_file.readframes(-1)
                        sample_rate = wav_file.getframerate()
                        channels = wav_file.getnchannels()
                        sample_width = wav_file.getsampwidth()
                        
                        # Convert to numpy array
                        if sample_width == 1:
                            dtype = np.uint8
                        elif sample_width == 2:
                            dtype = np.int16
                        elif sample_width == 4:
                            dtype = np.int32
                        else:
                            dtype = np.float32
                        
                        audio_array = np.frombuffer(frames, dtype=dtype)
                        
                        # Convert to float32 and normalize
                        if dtype != np.float32:
                            audio_array = audio_array.astype(np.float32)
                            if dtype == np.int16:
                                audio_array /= 32768.0
                            elif dtype == np.int32:
                                audio_array /= 2147483648.0
                            elif dtype == np.uint8:
                                audio_array = (audio_array - 128) / 128.0
                        
                        # Handle stereo to mono conversion
                        if channels > 1:
                            audio_array = audio_array.reshape(-1, channels)
                            audio_array = np.mean(audio_array, axis=1)
                        
                        # Resample to target sample rate if needed
                        if sample_rate != self.config.sample_rate:
                            audio_array = await self._resample_audio(
                                audio_array, sample_rate, self.config.sample_rate
                            )
                        
                        return audio_array
                        
            except wave.Error:
                # Not a wave file, try with torchaudio
                pass
            
            # Try with torchaudio for other formats
            with tempfile.NamedTemporaryFile(suffix='.audio') as tmp_file:
                tmp_file.write(audio_bytes)
                tmp_file.flush()
                
                waveform, sample_rate = await asyncio.get_event_loop().run_in_executor(
                    self.executor,
                    lambda: torchaudio.load(tmp_file.name)
                )
                
                # Convert to numpy and handle channels
                audio_array = waveform.numpy()
                if audio_array.shape[0] > 1:  # Multi-channel
                    audio_array = np.mean(audio_array, axis=0)
                else:
                    audio_array = audio_array[0]
                
                # Resample if needed
                if sample_rate != self.config.sample_rate:
                    audio_array = await self._resample_audio(
                        audio_array, sample_rate, self.config.sample_rate
                    )
                
                return audio_array.astype(np.float32)
                
        except Exception as e:
            logger.error(f"Failed to convert audio: {e}")
            # Return empty array as fallback
            return np.array([], dtype=np.float32)
    
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
    
    async def start_listening(self, session_id: str, language: str = None) -> bool:
        """Start real-time listening for a session"""
        try:
            if not self.recorder:
                logger.warning("RealtimeSTT recorder not available")
                return False
            
            if session_id in self.active_sessions:
                logger.warning(f"Session {session_id} already active")
                return True
            
            lang = self._normalize_language(language or self.config.language)
            
            # Store session info
            self.active_sessions[session_id] = {
                "language": lang,
                "start_time": time.time(),
                "transcriptions": [],
                "is_active": True
            }
            
            logger.info(f"Started listening for session {session_id} (language: {lang})")
            return True
            
        except Exception as e:
            logger.error(f"Failed to start listening for session {session_id}: {e}")
            return False
    
    async def stop_listening(self, session_id: str) -> Dict[str, Any]:
        """Stop real-time listening for a session"""
        try:
            if session_id not in self.active_sessions:
                logger.warning(f"Session {session_id} not found")
                return {"transcription": "", "confidence": 0.0}
            
            session = self.active_sessions[session_id]
            session["is_active"] = False
            
            # Get final transcription
            transcriptions = session.get("transcriptions", [])
            final_transcription = " ".join([t.get("text", "") for t in transcriptions])
            
            # Calculate average confidence
            if transcriptions:
                avg_confidence = sum(t.get("confidence", 0.0) for t in transcriptions) / len(transcriptions)
            else:
                avg_confidence = 0.0
            
            # Clean up session
            del self.active_sessions[session_id]
            
            logger.info(f"Stopped listening for session {session_id}")
            
            return {
                "transcription": final_transcription.strip(),
                "confidence": avg_confidence,
                "duration": time.time() - session["start_time"],
                "segments": len(transcriptions)
            }
            
        except Exception as e:
            logger.error(f"Failed to stop listening for session {session_id}: {e}")
            return {"transcription": "", "confidence": 0.0, "error": str(e)}
    
    async def process_realtime_audio(self, audio_data: str, session_id: str, language: str = None) -> Dict[str, Any]:
        """Process real-time audio data"""
        try:
            if session_id not in self.realtime_sessions:
                self.realtime_sessions[session_id] = {
                    "buffer": [],
                    "last_update": time.time(),
                    "partial_transcription": "",
                    "language": self._normalize_language(language or self.config.language)
                }
            
            session = self.realtime_sessions[session_id]
            
            # Decode and add audio to buffer
            audio_bytes = base64.b64decode(audio_data)
            audio_array = await self._convert_audio_to_array(audio_bytes)
            
            session["buffer"].extend(audio_array)
            session["last_update"] = time.time()
            
            # Process if buffer is large enough
            buffer_duration = len(session["buffer"]) / self.config.sample_rate
            if buffer_duration >= self.config.chunk_duration:
                
                # Get audio chunk
                chunk_samples = int(self.config.chunk_duration * self.config.sample_rate)
                audio_chunk = np.array(session["buffer"][:chunk_samples])
                session["buffer"] = session["buffer"][chunk_samples:]
                
                # Transcribe chunk
                result = await self._transcribe_with_whisper(audio_chunk, session["language"])
                
                # Update partial transcription
                if result.get("transcription"):
                    session["partial_transcription"] = result["transcription"]
                    
                    return {
                        "transcription": result["transcription"],
                        "confidence": result.get("confidence", 0.0),
                        "is_final": False,
                        "session_id": session_id,
                        "processing_time": result.get("processing_time", 0.0)
                    }
            
            return {
                "transcription": session.get("partial_transcription", ""),
                "confidence": 0.0,
                "is_final": False,
                "session_id": session_id,
                "processing_time": 0.0
            }
            
        except Exception as e:
            logger.error(f"Real-time audio processing failed for session {session_id}: {e}")
            return {
                "transcription": "",
                "confidence": 0.0,
                "is_final": False,
                "error": str(e),
                "session_id": session_id
            }
    
    async def process_realtime_audio_binary(self, audio_data: bytes, session_id: str) -> Dict[str, Any]:
        """Process real-time binary audio data"""
        try:
            # Convert binary data to base64 for processing
            audio_base64 = base64.b64encode(audio_data).decode('utf-8')
            return await self.process_realtime_audio(audio_base64, session_id)
            
        except Exception as e:
            logger.error(f"Binary audio processing failed for session {session_id}: {e}")
            return {
                "transcription": "",
                "confidence": 0.0,
                "is_final": False,
                "error": str(e),
                "session_id": session_id
            }
    
    def _on_transcription_finished(self, text: str):
        """Callback for finished transcription"""
        try:
            logger.debug(f"Transcription finished: {text}")
            
            # Notify all active sessions
            for session_id, session in self.active_sessions.items():
                if session.get("is_active", False):
                    session["transcriptions"].append({
                        "text": text,
                        "confidence": 0.8,  # Default confidence for RealtimeSTT
                        "timestamp": time.time()
                    })
            
            # Call registered callbacks
            for callback in self.transcription_callbacks:
                try:
                    callback(text)
                except Exception as e:
                    logger.error(f"Transcription callback failed: {e}")
                    
        except Exception as e:
            logger.error(f"Error in transcription finished callback: {e}")
    
    def _on_realtime_transcription_update(self, text: str):
        """Callback for real-time transcription updates"""
        try:
            logger.debug(f"Real-time update: {text}")
            
            # Update all real-time sessions
            for session_id, session in self.realtime_sessions.items():
                session["partial_transcription"] = text
                session["last_update"] = time.time()
            
            # Call registered callbacks
            for callback in self.realtime_callbacks:
                try:
                    callback(text)
                except Exception as e:
                    logger.error(f"Real-time callback failed: {e}")
                    
        except Exception as e:
            logger.error(f"Error in real-time transcription callback: {e}")
    
    def register_transcription_callback(self, callback: Callable[[str], None]):
        """Register callback for transcription events"""
        self.transcription_callbacks.append(callback)
    
    def register_realtime_callback(self, callback: Callable[[str], None]):
        """Register callback for real-time transcription events"""
        self.realtime_callbacks.append(callback)
    
    def get_active_sessions(self) -> List[str]:
        """Get list of active session IDs"""
        return list(self.active_sessions.keys())
    
    def get_realtime_sessions(self) -> List[str]:
        """Get list of real-time session IDs"""
        return list(self.realtime_sessions.keys())
    
    async def cleanup(self):
        """Cleanup STT service resources"""
        try:
            logger.info("Cleaning up STT service...")
            
            # Stop all active sessions
            for session_id in list(self.active_sessions.keys()):
                await self.stop_listening(session_id)
            
            # Clear real-time sessions
            self.realtime_sessions.clear()
            
            # Cleanup recorder
            if self.recorder:
                try:
                    self.recorder.shutdown()
                except Exception as e:
                    logger.warning(f"Error shutting down recorder: {e}")
            
            # Shutdown executor
            self.executor.shutdown(wait=True)
            
            self.is_initialized = False
            logger.info("STT service cleanup completed")
            
        except Exception as e:
            logger.error(f"Error during STT service cleanup: {e}")
