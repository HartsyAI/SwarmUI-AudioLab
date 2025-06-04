"""
API Package - HTTP API for Voice Assistant
SwarmUI VoiceAssistant Extension - API Layer

This package provides the HTTP API layer for the voice assistant backend,
including route definitions, request/response models, and API utilities.
"""

from .routes import router, set_service_references
from .models import (
    # Health and Status
    HealthCheckResponse,
    ServiceStatus,
    
    # STT Models
    STTRequest, STTResponse,
    RealtimeSTTRequest, RealtimeSTTResponse,
    BatchSTTRequest, BatchSTTResponse,
    STTBackend,
    
    # TTS Models
    TTSRequest, TTSResponse,
    VoiceCloneRequest, VoiceCloneResponse,
    VoiceListResponse,
    TTSBackend,
    
    # Wake Word Models
    WakeWordRequest, WakeWordResponse,
    WakeWordConfigRequest,
    
    # Conversation Models
    ConversationRequest, ConversationResponse,
    ConversationHistoryRequest, ConversationHistoryResponse,
    
    # Configuration Models
    ServiceConfig,
    ConfigUpdateRequest, ConfigUpdateResponse,
    
    # WebSocket Models
    WebSocketMessage, WebSocketCommand, WebSocketResponse,
    
    # Statistics Models
    ServiceStats, StatsResponse,
    
    # Error Models
    ErrorResponse, ValidationErrorResponse,
    
    # File Upload Models
    FileUploadRequest, FileUploadResponse,
    
    # Enums
    AudioFormat,
    
    # Base Models
    BaseRequest, BaseResponse
)

__all__ = [
    # Router and utilities
    "router",
    "set_service_references",
    
    # Health and Status
    "HealthCheckResponse", 
    "ServiceStatus",
    
    # STT Models
    "STTRequest", "STTResponse",
    "RealtimeSTTRequest", "RealtimeSTTResponse",
    "BatchSTTRequest", "BatchSTTResponse",
    "STTBackend",
    
    # TTS Models
    "TTSRequest", "TTSResponse",
    "VoiceCloneRequest", "VoiceCloneResponse",
    "VoiceListResponse",
    "TTSBackend",
    
    # Wake Word Models
    "WakeWordRequest", "WakeWordResponse",
    "WakeWordConfigRequest",
    
    # Conversation Models
    "ConversationRequest", "ConversationResponse",
    "ConversationHistoryRequest", "ConversationHistoryResponse",
    
    # Configuration Models
    "ServiceConfig",
    "ConfigUpdateRequest", "ConfigUpdateResponse",
    
    # WebSocket Models
    "WebSocketMessage", "WebSocketCommand", "WebSocketResponse",
    
    # Statistics Models
    "ServiceStats", "StatsResponse",
    
    # Error Models
    "ErrorResponse", "ValidationErrorResponse",
    
    # File Upload Models
    "FileUploadRequest", "FileUploadResponse",
    
    # Enums
    "AudioFormat",
    
    # Base Models
    "BaseRequest", "BaseResponse"
]

__version__ = "1.0.0"
__author__ = "SwarmUI VoiceAssistant"
__description__ = "HTTP API layer for voice assistant backend services"
