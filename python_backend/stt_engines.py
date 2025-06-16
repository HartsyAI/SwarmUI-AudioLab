#!/usr/bin/env python3
"""
Simple STT Engine Implementations
No abstract base classes, no factories - just working engines.
"""

import base64
import logging
import time

logger = logging.getLogger("STT")

class RealtimeSTTEngine:
    """RealtimeSTT engine implementation."""
    
    def __init__(self):
        self.name = "realtimestt"
        self.recorder = None
        
    def initialize(self):
        """Initialize RealtimeSTT."""
        try:
            from RealtimeSTT import AudioToTextRecorder
            
            self.recorder = AudioToTextRecorder(
                model="base",
                language="en",
                use_microphone=False,
                spinner=False,
                level=logging.WARNING
            )
            logger.info("RealtimeSTT initialized")
            return True
        except Exception as e:
            logger.error(f"RealtimeSTT init failed: {e}")
            return False
    
    def transcribe(self, audio_base64: str, language: str = "en-US", **kwargs):
        """Transcribe audio using RealtimeSTT."""
        try:
            # Decode audio
            audio_bytes = base64.b64decode(audio_base64)
            
            # Convert to format RealtimeSTT expects
            # (In real implementation, you'd handle audio format conversion here)
            
            # Feed audio and get transcription
            self.recorder.feed_audio(audio_bytes)
            transcription = self.recorder.text()
            
            return {
                "text": transcription,
                "confidence": 0.85,  # RealtimeSTT doesn't provide confidence
                "metadata": {"engine": "realtimestt"}
            }
            
        except Exception as e:
            logger.error(f"RealtimeSTT transcription failed: {e}")
            raise

class WhisperEngine:
    """OpenAI Whisper engine implementation."""
    
    def __init__(self):
        self.name = "whisper"
        self.model = None
        
    def initialize(self):
        """Initialize Whisper."""
        try:
            import whisper
            self.model = whisper.load_model("base")
            logger.info("Whisper initialized")
            return True
        except Exception as e:
            logger.error(f"Whisper init failed: {e}")
            return False
    
    def transcribe(self, audio_base64: str, language: str = "en-US", **kwargs):
        """Transcribe audio using Whisper."""
        try:
            import tempfile
            import os
            
            # Decode and save audio to temp file
            audio_bytes = base64.b64decode(audio_base64)
            
            with tempfile.NamedTemporaryFile(suffix='.wav', delete=False) as temp_file:
                temp_file.write(audio_bytes)
                temp_path = temp_file.name
            
            try:
                # Transcribe with Whisper
                result = self.model.transcribe(temp_path, language=language.split('-')[0])
                
                return {
                    "text": result["text"],
                    "confidence": 0.9,  # Whisper doesn't provide confidence either
                    "metadata": {"engine": "whisper"}
                }
            finally:
                os.unlink(temp_path)
                
        except Exception as e:
            logger.error(f"Whisper transcription failed: {e}")
            raise

class FallbackSTTEngine:
    """Fallback STT engine for when nothing else works."""
    
    def __init__(self):
        self.name = "fallback"
        
    def initialize(self):
        """Always succeeds."""
        logger.warning("Using fallback STT engine")
        return True
    
    def transcribe(self, audio_base64: str, language: str = "en-US", **kwargs):
        """Return placeholder transcription."""
        return {
            "text": "[Placeholder transcription - no STT engine available]",
            "confidence": 0.0,
            "metadata": {"engine": "fallback", "warning": "Install RealtimeSTT or Whisper for real transcription"}
        }

# Engine discovery and creation functions
def get_available_stt_engines():
    """Get list of available STT engines."""
    engines = []
    
    # Test RealtimeSTT
    try:
        import RealtimeSTT
        engines.append("realtimestt")
    except ImportError:
        pass
    
    # Test Whisper
    try:
        import whisper
        engines.append("whisper")
    except ImportError:
        pass
    
    # Fallback always available
    engines.append("fallback")
    
    return engines

def get_stt_engine(engine_name: str):
    """Create and initialize an STT engine."""
    engines = {
        "realtimestt": RealtimeSTTEngine,
        "whisper": WhisperEngine, 
        "fallback": FallbackSTTEngine
    }
    
    if engine_name not in engines:
        raise ValueError(f"Unknown STT engine: {engine_name}")
    
    engine = engines[engine_name]()
    
    if not engine.initialize():
        raise Exception(f"Failed to initialize {engine_name} engine")
    
    return engine
