#!/usr/bin/env python3
"""
Simple TTS Engine Implementations
No abstract base classes, no factories - just working engines.
"""

import base64
import logging
import io
import struct
import sys
import numpy as np

# Debug log function that writes to stderr to avoid polluting stdout JSON
def log_debug(message):
    print(f"[DEBUG] {message}", file=sys.stderr, flush=True)

logger = logging.getLogger("TTS")

class ChatterboxEngine:
    """Chatterbox TTS engine implementation."""
    
    def __init__(self):
        self.name = "chatterbox"
        self.model = None
        self.sample_rate = 22050
        
    def initialize(self):
        """Initialize Chatterbox TTS."""
        try:
            from chatterbox import ChatterboxTTS
            
            # Initialize with auto device detection
            device = "cuda" if self._has_cuda() else "cpu"
            self.model = ChatterboxTTS.from_pretrained(device=device)
            self.sample_rate = getattr(self.model, 'sr', 22050)
            
            logger.info(f"Chatterbox initialized on {device}")
            return True
        except Exception as e:
            logger.error(f"Chatterbox init failed: {e}")
            return False
    
    def _has_cuda(self):
        """Check if CUDA is available."""
        try:
            import torch
            return torch.cuda.is_available()
        except ImportError:
            return False
    
    def synthesize(self, text: str, voice: str = "default", language: str = "en-US", 
                   volume: float = 0.8, **kwargs):
        """Synthesize speech using Chatterbox."""
        try:
            # Map voice to Chatterbox parameters
            voice_params = self._get_voice_params(voice)
            
            # Generate audio
            audio_tensor = self.model.generate(
                text,
                exaggeration=voice_params["exaggeration"],
                cfg_weight=voice_params["cfg_weight"]
            )
            
            # Convert to WAV bytes
            audio_numpy = audio_tensor.cpu().numpy()
            if len(audio_numpy.shape) > 1:
                audio_numpy = np.mean(audio_numpy, axis=0)  # Convert to mono
            
            # Apply volume
            audio_numpy = audio_numpy * volume
            
            # Convert to WAV
            wav_bytes = self._numpy_to_wav(audio_numpy)
            audio_base64 = base64.b64encode(wav_bytes).decode('utf-8')
            
            duration = len(audio_numpy) / self.sample_rate
            
            return {
                "audio_data": audio_base64,
                "duration": duration,
                "metadata": {"engine": "chatterbox", "sample_rate": self.sample_rate}
            }
            
        except Exception as e:
            logger.error(f"Chatterbox synthesis failed: {e}")
            raise
    
    def _get_voice_params(self, voice: str):
        """Map voice names to Chatterbox parameters."""
        voice_map = {
            "default": {"exaggeration": 0.5, "cfg_weight": 0.5},
            "expressive": {"exaggeration": 0.7, "cfg_weight": 0.3},
            "calm": {"exaggeration": 0.3, "cfg_weight": 0.7},
            "dramatic": {"exaggeration": 0.8, "cfg_weight": 0.2}
        }
        return voice_map.get(voice, voice_map["default"])
    
    def _numpy_to_wav(self, audio_numpy: np.ndarray):
        """Convert numpy array to WAV bytes."""
        # Normalize and convert to 16-bit PCM
        max_amplitude = np.max(np.abs(audio_numpy))
        if max_amplitude > 0.8:
            audio_numpy = audio_numpy * 0.8 / max_amplitude
        
        audio_int16 = (audio_numpy * 32767).astype(np.int16)
        
        # Create WAV file in memory
        wav_io = io.BytesIO()
        
        # WAV header
        num_channels = 1
        bits_per_sample = 16
        byte_rate = self.sample_rate * num_channels * bits_per_sample // 8
        block_align = num_channels * bits_per_sample // 8
        data_size = len(audio_int16) * bits_per_sample // 8
        file_size = 36 + data_size
        
        wav_io.write(b'RIFF')
        wav_io.write(struct.pack('<I', file_size))
        wav_io.write(b'WAVE')
        wav_io.write(b'fmt ')
        wav_io.write(struct.pack('<I', 16))  # fmt chunk size
        wav_io.write(struct.pack('<H', 1))   # PCM format
        wav_io.write(struct.pack('<H', num_channels))
        wav_io.write(struct.pack('<I', self.sample_rate))
        wav_io.write(struct.pack('<I', byte_rate))
        wav_io.write(struct.pack('<H', block_align))
        wav_io.write(struct.pack('<H', bits_per_sample))
        wav_io.write(b'data')
        wav_io.write(struct.pack('<I', data_size))
        wav_io.write(audio_int16.tobytes())
        
        return wav_io.getvalue()

class BarkEngine:
    """Bark TTS engine implementation."""
    
    def __init__(self):
        self.name = "bark"
        self.model = None
        self.sample_rate = 24000
        
    def initialize(self):
        """Initialize Bark TTS."""
        try:
            from bark import SAMPLE_RATE, generate_audio, preload_models
            
            # Preload models
            preload_models()
            self.sample_rate = SAMPLE_RATE
            
            logger.info("Bark initialized")
            return True
        except Exception as e:
            logger.error(f"Bark init failed: {e}")
            return False
    
    def synthesize(self, text: str, voice: str = "default", language: str = "en-US", 
                   volume: float = 0.8, **kwargs):
        """Synthesize speech using Bark."""
        try:
            from bark import generate_audio
            
            # Generate audio
            audio_array = generate_audio(text)
            
            # Apply volume
            audio_array = audio_array * volume
            
            # Convert to WAV
            wav_bytes = self._numpy_to_wav(audio_array)
            audio_base64 = base64.b64encode(wav_bytes).decode('utf-8')
            
            duration = len(audio_array) / self.sample_rate
            
            return {
                "audio_data": audio_base64,
                "duration": duration,
                "metadata": {"engine": "bark", "sample_rate": self.sample_rate}
            }
            
        except Exception as e:
            logger.error(f"Bark synthesis failed: {e}")
            raise
    
    def _numpy_to_wav(self, audio_numpy: np.ndarray):
        """Convert numpy array to WAV bytes."""
        # Same implementation as Chatterbox
        audio_int16 = (audio_numpy * 32767).astype(np.int16)
        
        wav_io = io.BytesIO()
        
        # WAV header (simplified)
        data_size = len(audio_int16) * 2
        file_size = 36 + data_size
        
        wav_io.write(b'RIFF')
        wav_io.write(struct.pack('<I', file_size))
        wav_io.write(b'WAVE')
        wav_io.write(b'fmt ')
        wav_io.write(struct.pack('<I', 16))
        wav_io.write(struct.pack('<H', 1))   # PCM
        wav_io.write(struct.pack('<H', 1))   # Mono
        wav_io.write(struct.pack('<I', self.sample_rate))
        wav_io.write(struct.pack('<I', self.sample_rate * 2))
        wav_io.write(struct.pack('<H', 2))   # Block align
        wav_io.write(struct.pack('<H', 16))  # Bits per sample
        wav_io.write(b'data')
        wav_io.write(struct.pack('<I', data_size))
        wav_io.write(audio_int16.tobytes())
        
        return wav_io.getvalue()

class FallbackTTSEngine:
    """Fallback TTS engine for when nothing else works."""
    
    def __init__(self):
        self.name = "fallback"
        self.sample_rate = 22050
        
    def initialize(self):
        """Always succeeds."""
        logger.warning("Using fallback TTS engine")
        return True
    
    def synthesize(self, text: str, voice: str = "default", language: str = "en-US", 
                   volume: float = 0.8, **kwargs):
        """Generate silence as placeholder."""
        try:
            # Generate 1 second of silence
            duration = 1.0
            num_samples = int(self.sample_rate * duration)
            silence = np.zeros(num_samples, dtype=np.int16)
            
            # Create simple WAV
            wav_io = io.BytesIO()
            
            # Minimal WAV header + silence
            data_size = len(silence) * 2
            file_size = 36 + data_size
            
            wav_io.write(b'RIFF')
            wav_io.write(struct.pack('<I', file_size))
            wav_io.write(b'WAVE')
            wav_io.write(b'fmt ')
            wav_io.write(struct.pack('<I', 16))
            wav_io.write(struct.pack('<H', 1))   # PCM
            wav_io.write(struct.pack('<H', 1))   # Mono
            wav_io.write(struct.pack('<I', self.sample_rate))
            wav_io.write(struct.pack('<I', self.sample_rate * 2))
            wav_io.write(struct.pack('<H', 2))
            wav_io.write(struct.pack('<H', 16))
            wav_io.write(b'data')
            wav_io.write(struct.pack('<I', data_size))
            wav_io.write(silence.tobytes())
            
            wav_bytes = wav_io.getvalue()
            audio_base64 = base64.b64encode(wav_bytes).decode('utf-8')
            
            return {
                "audio_data": audio_base64,
                "duration": duration,
                "metadata": {
                    "engine": "fallback", 
                    "warning": "Install Chatterbox or Bark for real speech synthesis",
                    "sample_rate": self.sample_rate
                }
            }
            
        except Exception as e:
            logger.error(f"Fallback TTS failed: {e}")
            raise

# Engine discovery and creation functions
def get_available_tts_engines():
    """Get list of available TTS engines."""
    engines = []
    
    # Test Chatterbox
    log_debug("Trying to import chatterbox...")
    try:
        import chatterbox
        log_debug("Chatterbox import succeeded")
        engines.append("chatterbox")
    except ImportError as e:
        log_debug(f"Chatterbox import failed: {e}")
    
    # Test Bark
    log_debug("Trying to import bark...")
    try:
        import bark
        log_debug("Bark import succeeded")
        engines.append("bark")
    except ImportError as e:
        log_debug(f"Bark import failed: {e}")
    
    # Fallback always available
    log_debug("Adding fallback TTS engine")
    engines.append("fallback")
    
    return engines

def get_tts_engine(engine_name: str):
    """Create and initialize a TTS engine."""
    engines = {
        "chatterbox": ChatterboxEngine,
        "bark": BarkEngine,
        "fallback": FallbackTTSEngine
    }
    
    if engine_name not in engines:
        raise ValueError(f"Unknown TTS engine: {engine_name}")
    
    engine = engines[engine_name]()
    
    if not engine.initialize():
        raise Exception(f"Failed to initialize {engine_name} engine")
    
    return engine
