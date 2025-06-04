"""
Audio Utilities - Audio Processing Utilities
SwarmUI VoiceAssistant Extension - Audio Processing and Management

This module provides audio processing utilities including format conversion,
filtering, resampling, and device management for the voice assistant backend.
"""

import asyncio
import base64
import io
import tempfile
import wave
from concurrent.futures import ThreadPoolExecutor
from dataclasses import dataclass
from pathlib import Path
from typing import Dict, List, Optional, Tuple, Union, Any
import numpy as np
from loguru import logger

# Audio processing imports
try:
    import librosa
    import soundfile as sf
    LIBROSA_AVAILABLE = True
except ImportError:
    logger.warning("librosa not available, using fallback audio processing")
    LIBROSA_AVAILABLE = False

try:
    from pydub import AudioSegment
    from pydub.utils import which
    PYDUB_AVAILABLE = True
except ImportError:
    logger.warning("pydub not available")
    PYDUB_AVAILABLE = False

try:
    import sounddevice as sd
    SOUNDDEVICE_AVAILABLE = True
except ImportError:
    logger.warning("sounddevice not available")
    SOUNDDEVICE_AVAILABLE = False

try:
    import pyaudio
    PYAUDIO_AVAILABLE = True
except ImportError:
    logger.warning("pyaudio not available")
    PYAUDIO_AVAILABLE = False

try:
    import torch
    import torchaudio
    TORCH_AVAILABLE = True
except ImportError:
    logger.warning("torch/torchaudio not available")
    TORCH_AVAILABLE = False

try:
    import scipy.signal
    import scipy.io.wavfile
    SCIPY_AVAILABLE = True
except ImportError:
    logger.warning("scipy not available")
    SCIPY_AVAILABLE = False


@dataclass
class AudioFormat:
    """Audio format specification"""
    sample_rate: int = 16000
    channels: int = 1
    bit_depth: int = 16
    format: str = "wav"  # wav, mp3, flac, ogg
    
    def __post_init__(self):
        """Validate audio format parameters"""
        if self.sample_rate not in [8000, 16000, 22050, 44100, 48000]:
            logger.warning(f"Unusual sample rate: {self.sample_rate}Hz")
        
        if self.channels not in [1, 2]:
            raise ValueError("Channels must be 1 (mono) or 2 (stereo)")
        
        if self.bit_depth not in [8, 16, 24, 32]:
            raise ValueError("Bit depth must be 8, 16, 24, or 32")


@dataclass
class AudioMetadata:
    """Audio file metadata"""
    duration: float
    sample_rate: int
    channels: int
    bit_depth: int
    format: str
    file_size: int
    rms_level: float
    peak_level: float
    dynamic_range: float
    spectral_centroid: Optional[float] = None
    zero_crossing_rate: Optional[float] = None


class AudioDevice:
    """Audio device information"""
    def __init__(self, device_id: int, name: str, channels: int, 
                 sample_rate: int, is_input: bool, is_output: bool):
        self.device_id = device_id
        self.name = name
        self.channels = channels
        self.sample_rate = sample_rate
        self.is_input = is_input
        self.is_output = is_output
    
    def __repr__(self):
        direction = []
        if self.is_input:
            direction.append("input")
        if self.is_output:
            direction.append("output")
        return f"AudioDevice(id={self.device_id}, name='{self.name}', {'/'.join(direction)})"


class AudioProcessor:
    """Audio processing and management utility class"""
    
    def __init__(self, config: Optional[Dict[str, Any]] = None):
        self.config = config or {}
        self.executor = ThreadPoolExecutor(max_workers=4)
        self.is_initialized = False
        
        # Audio format defaults
        self.default_format = AudioFormat(
            sample_rate=self.config.get('sample_rate', 16000),
            channels=self.config.get('channels', 1),
            bit_depth=self.config.get('bit_depth', 16)
        )
        
        # Audio devices
        self.input_devices: List[AudioDevice] = []
        self.output_devices: List[AudioDevice] = []
        self.default_input_device: Optional[AudioDevice] = None
        self.default_output_device: Optional[AudioDevice] = None
        
        logger.info("Audio processor initialized")
    
    async def initialize(self) -> bool:
        """Initialize audio processor and discover devices"""
        try:
            logger.info("Initializing audio processor...")
            
            # Discover audio devices
            await self._discover_audio_devices()
            
            # Test audio backends
            await self._test_audio_backends()
            
            self.is_initialized = True
            logger.info("Audio processor initialized successfully")
            return True
            
        except Exception as e:
            logger.error(f"Failed to initialize audio processor: {e}")
            return False
    
    async def _discover_audio_devices(self):
        """Discover available audio input/output devices"""
        try:
            self.input_devices.clear()
            self.output_devices.clear()
            
            if SOUNDDEVICE_AVAILABLE:
                await self._discover_sounddevice_devices()
            elif PYAUDIO_AVAILABLE:
                await self._discover_pyaudio_devices()
            else:
                logger.warning("No audio device discovery libraries available")
            
            logger.info(f"Discovered {len(self.input_devices)} input devices and {len(self.output_devices)} output devices")
            
        except Exception as e:
            logger.error(f"Error discovering audio devices: {e}")
    
    async def _discover_sounddevice_devices(self):
        """Discover devices using sounddevice"""
        try:
            devices = await asyncio.get_event_loop().run_in_executor(
                self.executor, sd.query_devices
            )
            
            for i, device in enumerate(devices):
                audio_device = AudioDevice(
                    device_id=i,
                    name=device['name'],
                    channels=device['max_input_channels'] if device['max_input_channels'] > 0 else device['max_output_channels'],
                    sample_rate=int(device['default_samplerate']),
                    is_input=device['max_input_channels'] > 0,
                    is_output=device['max_output_channels'] > 0
                )
                
                if audio_device.is_input:
                    self.input_devices.append(audio_device)
                if audio_device.is_output:
                    self.output_devices.append(audio_device)
            
            # Set default devices
            default_input = sd.default.device[0]
            default_output = sd.default.device[1]
            
            if default_input is not None and default_input < len(self.input_devices):
                self.default_input_device = self.input_devices[default_input]
            
            if default_output is not None and default_output < len(self.output_devices):
                self.default_output_device = self.output_devices[default_output]
                
        except Exception as e:
            logger.error(f"Error discovering sounddevice devices: {e}")
    
    async def _discover_pyaudio_devices(self):
        """Discover devices using pyaudio"""
        try:
            def _get_pyaudio_devices():
                p = pyaudio.PyAudio()
                devices = []
                
                try:
                    for i in range(p.get_device_count()):
                        device_info = p.get_device_info_by_index(i)
                        devices.append((i, device_info))
                finally:
                    p.terminate()
                
                return devices
            
            devices = await asyncio.get_event_loop().run_in_executor(
                self.executor, _get_pyaudio_devices
            )
            
            for device_id, device_info in devices:
                audio_device = AudioDevice(
                    device_id=device_id,
                    name=device_info['name'],
                    channels=max(device_info['maxInputChannels'], device_info['maxOutputChannels']),
                    sample_rate=int(device_info['defaultSampleRate']),
                    is_input=device_info['maxInputChannels'] > 0,
                    is_output=device_info['maxOutputChannels'] > 0
                )
                
                if audio_device.is_input:
                    self.input_devices.append(audio_device)
                if audio_device.is_output:
                    self.output_devices.append(audio_device)
                    
        except Exception as e:
            logger.error(f"Error discovering pyaudio devices: {e}")
    
    async def _test_audio_backends(self):
        """Test available audio processing backends"""
        backends = {
            'librosa': LIBROSA_AVAILABLE,
            'pydub': PYDUB_AVAILABLE,
            'sounddevice': SOUNDDEVICE_AVAILABLE,
            'pyaudio': PYAUDIO_AVAILABLE,
            'torch': TORCH_AVAILABLE,
            'scipy': SCIPY_AVAILABLE
        }
        
        available_backends = [name for name, available in backends.items() if available]
        logger.info(f"Available audio backends: {', '.join(available_backends)}")
    
    async def convert_audio_format(self, audio_data: bytes, input_format: str,
                                  output_format: AudioFormat) -> bytes:
        """Convert audio data between formats"""
        try:
            if PYDUB_AVAILABLE:
                return await self._convert_with_pydub(audio_data, input_format, output_format)
            elif LIBROSA_AVAILABLE:
                return await self._convert_with_librosa(audio_data, input_format, output_format)
            else:
                return await self._convert_basic(audio_data, input_format, output_format)
                
        except Exception as e:
            logger.error(f"Audio format conversion failed: {e}")
            raise
    
    async def _convert_with_pydub(self, audio_data: bytes, input_format: str,
                                 output_format: AudioFormat) -> bytes:
        """Convert audio using pydub"""
        def _convert():
            # Load audio with pydub
            audio_segment = AudioSegment.from_file(
                io.BytesIO(audio_data), format=input_format
            )
            
            # Convert sample rate
            if audio_segment.frame_rate != output_format.sample_rate:
                audio_segment = audio_segment.set_frame_rate(output_format.sample_rate)
            
            # Convert channels
            if output_format.channels == 1 and audio_segment.channels > 1:
                audio_segment = audio_segment.set_channels(1)
            elif output_format.channels == 2 and audio_segment.channels == 1:
                audio_segment = audio_segment.set_channels(2)
            
            # Convert bit depth
            if output_format.bit_depth == 16:
                audio_segment = audio_segment.set_sample_width(2)
            elif output_format.bit_depth == 8:
                audio_segment = audio_segment.set_sample_width(1)
            elif output_format.bit_depth == 32:
                audio_segment = audio_segment.set_sample_width(4)
            
            # Export to bytes
            output_buffer = io.BytesIO()
            audio_segment.export(output_buffer, format=output_format.format)
            return output_buffer.getvalue()
        
        return await asyncio.get_event_loop().run_in_executor(self.executor, _convert)
    
    async def _convert_with_librosa(self, audio_data: bytes, input_format: str,
                                   output_format: AudioFormat) -> bytes:
        """Convert audio using librosa"""
        def _convert():
            # Load audio with librosa
            with io.BytesIO(audio_data) as audio_io:
                audio_array, sample_rate = librosa.load(
                    audio_io, sr=output_format.sample_rate, mono=(output_format.channels == 1)
                )
            
            # Convert to target format
            if output_format.bit_depth == 16:
                audio_int16 = (audio_array * 32767).astype(np.int16)
                return self._array_to_wav_bytes(audio_int16, output_format.sample_rate, output_format.channels)
            elif output_format.bit_depth == 32:
                audio_int32 = (audio_array * 2147483647).astype(np.int32)
                return self._array_to_wav_bytes(audio_int32, output_format.sample_rate, output_format.channels)
            else:
                # Default to float32
                return self._array_to_wav_bytes(audio_array, output_format.sample_rate, output_format.channels)
        
        return await asyncio.get_event_loop().run_in_executor(self.executor, _convert)
    
    async def _convert_basic(self, audio_data: bytes, input_format: str,
                            output_format: AudioFormat) -> bytes:
        """Basic audio conversion fallback"""
        # Simple pass-through for WAV files
        if input_format.lower() == 'wav' and output_format.format.lower() == 'wav':
            return audio_data
        else:
            raise NotImplementedError("Advanced audio conversion requires librosa or pydub")
    
    def _array_to_wav_bytes(self, audio_array: np.ndarray, sample_rate: int, channels: int) -> bytes:
        """Convert numpy array to WAV bytes"""
        with io.BytesIO() as wav_buffer:
            with wave.open(wav_buffer, 'wb') as wav_file:
                wav_file.setnchannels(channels)
                wav_file.setframerate(sample_rate)
                
                if audio_array.dtype == np.int16:
                    wav_file.setsampwidth(2)
                elif audio_array.dtype == np.int32:
                    wav_file.setsampwidth(4)
                else:
                    # Convert float to int16
                    audio_array = (audio_array * 32767).astype(np.int16)
                    wav_file.setsampwidth(2)
                
                wav_file.writeframes(audio_array.tobytes())
            
            return wav_buffer.getvalue()
    
    async def resample_audio(self, audio_data: Union[np.ndarray, bytes], 
                           original_rate: int, target_rate: int) -> np.ndarray:
        """Resample audio to target sample rate"""
        try:
            if original_rate == target_rate:
                if isinstance(audio_data, bytes):
                    return await self.bytes_to_array(audio_data)
                return audio_data
            
            if LIBROSA_AVAILABLE:
                return await self._resample_with_librosa(audio_data, original_rate, target_rate)
            elif TORCH_AVAILABLE:
                return await self._resample_with_torch(audio_data, original_rate, target_rate)
            elif SCIPY_AVAILABLE:
                return await self._resample_with_scipy(audio_data, original_rate, target_rate)
            else:
                return await self._resample_basic(audio_data, original_rate, target_rate)
                
        except Exception as e:
            logger.error(f"Audio resampling failed: {e}")
            raise
    
    async def _resample_with_librosa(self, audio_data: Union[np.ndarray, bytes],
                                    original_rate: int, target_rate: int) -> np.ndarray:
        """Resample using librosa"""
        def _resample():
            if isinstance(audio_data, bytes):
                audio_array = self._bytes_to_array_sync(audio_data)
            else:
                audio_array = audio_data
            
            return librosa.resample(audio_array, orig_sr=original_rate, target_sr=target_rate)
        
        return await asyncio.get_event_loop().run_in_executor(self.executor, _resample)
    
    async def _resample_with_torch(self, audio_data: Union[np.ndarray, bytes],
                                  original_rate: int, target_rate: int) -> np.ndarray:
        """Resample using torchaudio"""
        def _resample():
            if isinstance(audio_data, bytes):
                audio_array = self._bytes_to_array_sync(audio_data)
            else:
                audio_array = audio_data
            
            audio_tensor = torch.from_numpy(audio_array).unsqueeze(0)
            resampler = torchaudio.transforms.Resample(original_rate, target_rate)
            resampled_tensor = resampler(audio_tensor)
            return resampled_tensor.squeeze(0).numpy()
        
        return await asyncio.get_event_loop().run_in_executor(self.executor, _resample)
    
    async def _resample_with_scipy(self, audio_data: Union[np.ndarray, bytes],
                                  original_rate: int, target_rate: int) -> np.ndarray:
        """Resample using scipy"""
        def _resample():
            if isinstance(audio_data, bytes):
                audio_array = self._bytes_to_array_sync(audio_data)
            else:
                audio_array = audio_data
            
            # Calculate number of samples for target rate
            num_samples = int(len(audio_array) * target_rate / original_rate)
            return scipy.signal.resample(audio_array, num_samples)
        
        return await asyncio.get_event_loop().run_in_executor(self.executor, _resample)
    
    async def _resample_basic(self, audio_data: Union[np.ndarray, bytes],
                             original_rate: int, target_rate: int) -> np.ndarray:
        """Basic resampling using linear interpolation"""
        def _resample():
            if isinstance(audio_data, bytes):
                audio_array = self._bytes_to_array_sync(audio_data)
            else:
                audio_array = audio_data
            
            # Simple linear interpolation
            ratio = target_rate / original_rate
            new_length = int(len(audio_array) * ratio)
            
            old_indices = np.linspace(0, len(audio_array) - 1, len(audio_array))
            new_indices = np.linspace(0, len(audio_array) - 1, new_length)
            
            return np.interp(new_indices, old_indices, audio_array)
        
        return await asyncio.get_event_loop().run_in_executor(self.executor, _resample)
    
    async def bytes_to_array(self, audio_bytes: bytes) -> np.ndarray:
        """Convert audio bytes to numpy array"""
        return await asyncio.get_event_loop().run_in_executor(
            self.executor, self._bytes_to_array_sync, audio_bytes
        )
    
    def _bytes_to_array_sync(self, audio_bytes: bytes) -> np.ndarray:
        """Synchronous version of bytes_to_array"""
        try:
            # Try to load as WAV file first
            try:
                with io.BytesIO(audio_bytes) as audio_io:
                    with wave.open(audio_io, 'rb') as wav_file:
                        frames = wav_file.readframes(-1)
                        sample_width = wav_file.getsampwidth()
                        channels = wav_file.getnchannels()
                        
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
                        
                        # Handle stereo to mono
                        if channels > 1:
                            audio_array = audio_array.reshape(-1, channels)
                            audio_array = np.mean(audio_array, axis=1)
                        
                        return audio_array
                        
            except wave.Error:
                pass
            
            # Try with librosa for other formats
            if LIBROSA_AVAILABLE:
                with io.BytesIO(audio_bytes) as audio_io:
                    audio_array, _ = librosa.load(audio_io, sr=None, mono=True)
                    return audio_array
            
            # Try with pydub as fallback
            if PYDUB_AVAILABLE:
                with tempfile.NamedTemporaryFile() as temp_file:
                    temp_file.write(audio_bytes)
                    temp_file.flush()
                    
                    audio_segment = AudioSegment.from_file(temp_file.name)
                    audio_array = np.array(audio_segment.get_array_of_samples(), dtype=np.float32)
                    
                    # Normalize
                    if audio_segment.sample_width == 2:
                        audio_array /= 32768.0
                    elif audio_segment.sample_width == 4:
                        audio_array /= 2147483648.0
                    
                    # Handle stereo
                    if audio_segment.channels == 2:
                        audio_array = audio_array.reshape(-1, 2)
                        audio_array = np.mean(audio_array, axis=1)
                    
                    return audio_array
            
            # Fallback: assume raw 16-bit PCM
            logger.warning("Assuming raw 16-bit PCM audio format")
            audio_array = np.frombuffer(audio_bytes, dtype=np.int16).astype(np.float32)
            return audio_array / 32768.0
            
        except Exception as e:
            logger.error(f"Failed to convert bytes to array: {e}")
            return np.array([], dtype=np.float32)
    
    async def array_to_bytes(self, audio_array: np.ndarray, sample_rate: int = None,
                           format: AudioFormat = None) -> bytes:
        """Convert numpy array to audio bytes"""
        format = format or self.default_format
        sample_rate = sample_rate or format.sample_rate
        
        return await asyncio.get_event_loop().run_in_executor(
            self.executor, self._array_to_bytes_sync, audio_array, sample_rate, format
        )
    
    def _array_to_bytes_sync(self, audio_array: np.ndarray, sample_rate: int,
                           format: AudioFormat) -> bytes:
        """Synchronous version of array_to_bytes"""
        try:
            # Ensure audio is in correct format
            if audio_array.dtype != np.float32:
                audio_array = audio_array.astype(np.float32)
            
            # Normalize to [-1, 1] range
            if np.max(np.abs(audio_array)) > 1.0:
                audio_array = audio_array / np.max(np.abs(audio_array))
            
            # Convert to target bit depth
            if format.bit_depth == 16:
                audio_int16 = (audio_array * 32767).astype(np.int16)
                return self._array_to_wav_bytes(audio_int16, sample_rate, format.channels)
            elif format.bit_depth == 8:
                audio_uint8 = ((audio_array + 1.0) * 127.5).astype(np.uint8)
                return self._array_to_wav_bytes(audio_uint8, sample_rate, format.channels)
            elif format.bit_depth == 32:
                audio_int32 = (audio_array * 2147483647).astype(np.int32)
                return self._array_to_wav_bytes(audio_int32, sample_rate, format.channels)
            else:
                # Default to float32
                return self._array_to_wav_bytes(audio_array, sample_rate, format.channels)
                
        except Exception as e:
            logger.error(f"Failed to convert array to bytes: {e}")
            raise
    
    async def analyze_audio(self, audio_data: Union[np.ndarray, bytes],
                           sample_rate: int = None) -> AudioMetadata:
        """Analyze audio data and extract metadata"""
        try:
            if isinstance(audio_data, bytes):
                audio_array = await self.bytes_to_array(audio_data)
                sample_rate = sample_rate or self.default_format.sample_rate
                file_size = len(audio_data)
            else:
                audio_array = audio_data
                sample_rate = sample_rate or self.default_format.sample_rate
                file_size = len(audio_array) * 4  # Assume float32
            
            return await asyncio.get_event_loop().run_in_executor(
                self.executor, self._analyze_audio_sync, audio_array, sample_rate, file_size
            )
            
        except Exception as e:
            logger.error(f"Audio analysis failed: {e}")
            raise
    
    def _analyze_audio_sync(self, audio_array: np.ndarray, sample_rate: int,
                           file_size: int) -> AudioMetadata:
        """Synchronous audio analysis"""
        try:
            # Basic measurements
            duration = len(audio_array) / sample_rate
            channels = 1 if audio_array.ndim == 1 else audio_array.shape[1]
            
            # Audio levels
            rms_level = float(np.sqrt(np.mean(audio_array**2)))
            peak_level = float(np.max(np.abs(audio_array)))
            
            # Dynamic range (simplified)
            sorted_samples = np.sort(np.abs(audio_array))
            p99 = sorted_samples[int(0.99 * len(sorted_samples))]
            p1 = sorted_samples[int(0.01 * len(sorted_samples))]
            dynamic_range = float(20 * np.log10(p99 / (p1 + 1e-10)))
            
            # Advanced features if librosa is available
            spectral_centroid = None
            zero_crossing_rate = None
            
            if LIBROSA_AVAILABLE and len(audio_array) > 512:
                try:
                    # Spectral centroid
                    centroids = librosa.feature.spectral_centroid(y=audio_array, sr=sample_rate)[0]
                    spectral_centroid = float(np.mean(centroids))
                    
                    # Zero crossing rate
                    zcr = librosa.feature.zero_crossing_rate(audio_array)[0]
                    zero_crossing_rate = float(np.mean(zcr))
                    
                except Exception as e:
                    logger.debug(f"Advanced audio analysis failed: {e}")
            
            return AudioMetadata(
                duration=duration,
                sample_rate=sample_rate,
                channels=channels,
                bit_depth=32,  # Assume float32
                format="wav",
                file_size=file_size,
                rms_level=rms_level,
                peak_level=peak_level,
                dynamic_range=dynamic_range,
                spectral_centroid=spectral_centroid,
                zero_crossing_rate=zero_crossing_rate
            )
            
        except Exception as e:
            logger.error(f"Audio analysis error: {e}")
            raise
    
    async def apply_noise_reduction(self, audio_array: np.ndarray,
                                   sample_rate: int = None) -> np.ndarray:
        """Apply noise reduction to audio"""
        try:
            if not SCIPY_AVAILABLE:
                logger.warning("Noise reduction requires scipy, returning original audio")
                return audio_array
            
            return await asyncio.get_event_loop().run_in_executor(
                self.executor, self._apply_noise_reduction_sync, audio_array, sample_rate
            )
            
        except Exception as e:
            logger.error(f"Noise reduction failed: {e}")
            return audio_array
    
    def _apply_noise_reduction_sync(self, audio_array: np.ndarray,
                                   sample_rate: int) -> np.ndarray:
        """Synchronous noise reduction"""
        try:
            # Simple spectral subtraction method
            # This is a basic implementation - more sophisticated methods exist
            
            # Apply window
            window_size = 1024
            hop_size = window_size // 4
            
            # Estimate noise from first 0.5 seconds
            noise_duration = min(0.5, len(audio_array) / sample_rate / 4)
            noise_samples = int(noise_duration * sample_rate)
            noise_spectrum = np.abs(np.fft.fft(audio_array[:noise_samples]))
            
            # Process audio in overlapping windows
            processed_audio = np.zeros_like(audio_array)
            
            for i in range(0, len(audio_array) - window_size, hop_size):
                window = audio_array[i:i + window_size]
                
                # Apply Hanning window
                windowed = window * np.hanning(len(window))
                
                # FFT
                spectrum = np.fft.fft(windowed)
                magnitude = np.abs(spectrum)
                phase = np.angle(spectrum)
                
                # Spectral subtraction
                clean_magnitude = magnitude - 0.5 * noise_spectrum[:len(magnitude)]
                clean_magnitude = np.maximum(clean_magnitude, 0.1 * magnitude)
                
                # Reconstruct signal
                clean_spectrum = clean_magnitude * np.exp(1j * phase)
                clean_window = np.real(np.fft.ifft(clean_spectrum))
                
                # Overlap-add
                processed_audio[i:i + window_size] += clean_window
            
            return processed_audio
            
        except Exception as e:
            logger.error(f"Noise reduction processing error: {e}")
            return audio_array
    
    async def normalize_volume(self, audio_array: np.ndarray, target_level: float = 0.7) -> np.ndarray:
        """Normalize audio volume to target level"""
        try:
            current_level = np.max(np.abs(audio_array))
            if current_level > 0:
                normalization_factor = target_level / current_level
                return audio_array * normalization_factor
            return audio_array
            
        except Exception as e:
            logger.error(f"Volume normalization failed: {e}")
            return audio_array
    
    def get_audio_devices(self) -> Dict[str, List[AudioDevice]]:
        """Get available audio devices"""
        return {
            'input_devices': self.input_devices,
            'output_devices': self.output_devices,
            'default_input': self.default_input_device,
            'default_output': self.default_output_device
        }
    
    def get_supported_formats(self) -> List[str]:
        """Get list of supported audio formats"""
        formats = ['wav']  # Always supported
        
        if PYDUB_AVAILABLE:
            formats.extend(['mp3', 'flac', 'ogg', 'm4a', 'aac'])
        
        if LIBROSA_AVAILABLE:
            formats.extend(['mp3', 'flac', 'ogg'])
        
        return list(set(formats))
    
    async def cleanup(self):
        """Cleanup audio processor resources"""
        try:
            logger.info("Cleaning up audio processor...")
            self.executor.shutdown(wait=True)
            self.is_initialized = False
            logger.info("Audio processor cleanup completed")
            
        except Exception as e:
            logger.error(f"Error during audio processor cleanup: {e}")


# Utility functions
async def convert_base64_to_array(audio_base64: str, sample_rate: int = 16000) -> np.ndarray:
    """Convert base64 audio to numpy array"""
    try:
        audio_bytes = base64.b64decode(audio_base64)
        processor = AudioProcessor()
        await processor.initialize()
        return await processor.bytes_to_array(audio_bytes)
        
    except Exception as e:
        logger.error(f"Base64 to array conversion failed: {e}")
        return np.array([], dtype=np.float32)


async def convert_array_to_base64(audio_array: np.ndarray, sample_rate: int = 16000) -> str:
    """Convert numpy array to base64 audio"""
    try:
        processor = AudioProcessor()
        await processor.initialize()
        audio_bytes = await processor.array_to_bytes(audio_array, sample_rate)
        return base64.b64encode(audio_bytes).decode('utf-8')
        
    except Exception as e:
        logger.error(f"Array to base64 conversion failed: {e}")
        return ""


def create_sine_wave(frequency: float, duration: float, sample_rate: int = 16000) -> np.ndarray:
    """Create a sine wave for testing"""
    t = np.linspace(0, duration, int(sample_rate * duration))
    return 0.5 * np.sin(2 * np.pi * frequency * t)


def create_white_noise(duration: float, amplitude: float = 0.1, sample_rate: int = 16000) -> np.ndarray:
    """Create white noise for testing"""
    samples = int(sample_rate * duration)
    return amplitude * np.random.normal(0, 1, samples)
