"""
Services Package - Voice Assistant Services
SwarmUI VoiceAssistant Extension - Service Layer Initialization

This package contains all the core voice processing services including
STT, TTS, wake word detection, and conversation management.
"""

from .stt_service import STTService, STTConfig
from .tts_service import TTSService, TTSConfig
from .wake_word_service import WakeWordService, WakeWordConfig
from .conversation_manager import ConversationManager, ConversationConfig

__all__ = [
    # STT Service
    "STTService",
    "STTConfig", 
    
    # TTS Service
    "TTSService",
    "TTSConfig",
    
    # Wake Word Service
    "WakeWordService", 
    "WakeWordConfig",
    
    # Conversation Manager
    "ConversationManager",
    "ConversationConfig"
]

__version__ = "1.0.0"
__author__ = "SwarmUI VoiceAssistant Extension"
__description__ = "Voice processing services for SwarmUI voice assistant"
