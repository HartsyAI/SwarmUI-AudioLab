"""
API Models - Pydantic Data Models
SwarmUI VoiceAssistant Extension - API Request/Response Models

This module defines all Pydantic data models for request validation,
response formatting, and data serialization in the voice assistant API.
"""

from datetime import datetime
from typing import Dict, List, Optional, Any, Union
from pydantic import BaseModel, Field, validator
from enum import Enum


class ServiceStatus(str, Enum):
    """Enumeration for service status values"""
    HEALTHY = "healthy"
    DEGRADED = "degraded"
    UNHEALTHY = "unhealthy"
    STARTING = "starting"
    STOPPING = "stopping"


class AudioFormat(str, Enum):
    """Enumeration for supported audio formats"""
    WAV = "wav"
    MP3 = "mp3"
    FLAC = "flac"
    OGG = "ogg"


class TTSBackend(str, Enum):
    """Enumeration for TTS backend options"""
    AUTO = "auto"
    CHATTERBOX = "chatterbox"
    COQUI = "coqui"
    GTTS = "gtts"
    FALLBACK = "fallback"


class STTBackend(str, Enum):
    """Enumeration for STT backend options"""
    AUTO = "auto"
    REALTIME_STT = "realtime_stt"
    WHISPER = "whisper"
    SPEECH_RECOGNITION = "speech_recognition"


# Base Models
class BaseRequest(BaseModel):
    """Base request model with common fields"""
    session_id: Optional[str] = Field(None, description="Session identifier")
    timestamp: Optional[datetime] = Field(default_factory=datetime.utcnow, description="Request timestamp")


class BaseResponse(BaseModel):
    """Base response model with common fields"""
    success: bool = Field(True, description="Whether the request was successful")
    timestamp: datetime = Field(default_factory=datetime.utcnow, description="Response timestamp")
    processing_time: Optional[float] = Field(None, description="Processing time in seconds")
    error: Optional[str] = Field(None, description="Error message if request failed")
    error_code: Optional[str] = Field(None, description="Error code for programmatic handling")


# Health Check Models
class HealthCheckResponse(BaseResponse):
    """Health check response model"""
    status: ServiceStatus = Field(description="Overall service status")
    services: Dict[str, bool] = Field(description="Status of individual services")
    uptime: float = Field(description="Service uptime in seconds")
    version: str = Field(default="1.0.0", description="Service version")
    memory_usage: Optional[float] = Field(None, description="Memory usage in MB")
    cpu_usage: Optional[float] = Field(None, description="CPU usage percentage")


# STT Models
class STTRequest(BaseRequest):
    """Speech-to-text request model"""
    audio_data: str = Field(description="Base64 encoded audio data")
    language: str = Field(default="en-US", description="Language code for recognition")
    confidence_threshold: float = Field(default=0.7, ge=0.0, le=1.0, description="Minimum confidence threshold")
    model: Optional[str] = Field(None, description="STT model to use")
    backend: STTBackend = Field(default=STTBackend.AUTO, description="STT backend to use")
    
    @validator('audio_data')
    def validate_audio_data(cls, v):
        if not v or len(v) < 100:  # Minimum reasonable audio data length
            raise ValueError("Audio data must be provided and non-empty")
        return v


class STTResponse(BaseResponse):
    """Speech-to-text response model"""
    transcription: str = Field(description="Transcribed text")
    confidence: float = Field(ge=0.0, le=1.0, description="Confidence score")
    language: str = Field(description="Detected or specified language")
    is_final: bool = Field(default=True, description="Whether this is a final transcription")
    alternatives: List[Dict[str, Any]] = Field(default_factory=list, description="Alternative transcriptions")
    word_timestamps: List[Dict[str, Any]] = Field(default_factory=list, description="Word-level timestamps")
    backend_used: Optional[str] = Field(None, description="Backend that processed the request")


class RealtimeSTTRequest(BaseRequest):
    """Real-time STT request model"""
    audio_chunk: str = Field(description="Base64 encoded audio chunk")
    is_final_chunk: bool = Field(default=False, description="Whether this is the final audio chunk")
    sequence_number: int = Field(default=0, description="Chunk sequence number")


class RealtimeSTTResponse(BaseResponse):
    """Real-time STT response model"""
    partial_transcription: str = Field(description="Partial transcription")
    is_final: bool = Field(description="Whether transcription is complete")
    confidence: float = Field(ge=0.0, le=1.0, description="Current confidence score")
    words_added: int = Field(default=0, description="Number of words added in this update")


# TTS Models
class TTSRequest(BaseRequest):
    """Text-to-speech request model"""
    text: str = Field(description="Text to synthesize", min_length=1, max_length=5000)
    voice: str = Field(default="default", description="Voice to use for synthesis")
    speed: float = Field(default=1.0, ge=0.5, le=2.0, description="Speech speed multiplier")
    volume: float = Field(default=0.8, ge=0.0, le=1.0, description="Audio volume level")
    language: str = Field(default="en-US", description="Language for synthesis")
    emotion: float = Field(default=0.5, ge=0.0, le=1.0, description="Emotion level")
    pacing: float = Field(default=0.5, ge=0.0, le=1.0, description="Speech pacing")
    backend: TTSBackend = Field(default=TTSBackend.AUTO, description="TTS backend to use")
    output_format: AudioFormat = Field(default=AudioFormat.WAV, description="Output audio format")
    
    @validator('text')
    def validate_text(cls, v):
        if not v.strip():
            raise ValueError("Text cannot be empty or only whitespace")
        return v.strip()


class TTSResponse(BaseResponse):
    """Text-to-speech response model"""
    audio_data: str = Field(description="Base64 encoded audio data")
    duration: float = Field(description="Audio duration in seconds")
    sample_rate: int = Field(description="Audio sample rate")
    channels: int = Field(description="Number of audio channels")
    voice_used: str = Field(description="Voice that was used for synthesis")
    backend_used: str = Field(description="Backend that processed the request")
    audio_format: AudioFormat = Field(description="Output audio format")
    file_size: Optional[int] = Field(None, description="Audio file size in bytes")


class VoiceCloneRequest(BaseRequest):
    """Voice cloning request model"""
    reference_audio: str = Field(description="Base64 encoded reference audio")
    voice_name: str = Field(description="Name for the cloned voice", min_length=1, max_length=50)
    description: Optional[str] = Field(None, description="Description of the voice")
    
    @validator('voice_name')
    def validate_voice_name(cls, v):
        # Ensure voice name is alphanumeric with underscores/hyphens
        import re
        if not re.match(r'^[a-zA-Z0-9_-]+$', v):
            raise ValueError("Voice name must contain only letters, numbers, underscores, and hyphens")
        return v


class VoiceCloneResponse(BaseResponse):
    """Voice cloning response model"""
    voice_name: str = Field(description="Name of the cloned voice")
    voice_path: str = Field(description="Path to the voice sample file")
    quality_score: float = Field(ge=0.0, le=1.0, description="Quality assessment of the voice sample")
    duration: float = Field(description="Duration of reference audio in seconds")


class VoiceListResponse(BaseResponse):
    """Voice list response model"""
    voices: List[Dict[str, Any]] = Field(description="List of available voices")
    total_count: int = Field(description="Total number of available voices")
    builtin_count: int = Field(description="Number of built-in voices")
    custom_count: int = Field(description="Number of custom/cloned voices")


# Wake Word Models
class WakeWordRequest(BaseRequest):
    """Wake word detection request model"""
    audio_data: str = Field(description="Base64 encoded audio data")
    wake_words: List[str] = Field(default_factory=lambda: ["hey swarm"], description="Wake words to detect")
    sensitivity: float = Field(default=0.5, ge=0.0, le=1.0, description="Detection sensitivity")
    
    @validator('wake_words')
    def validate_wake_words(cls, v):
        if not v:
            raise ValueError("At least one wake word must be specified")
        return [word.strip().lower() for word in v if word.strip()]


class WakeWordResponse(BaseResponse):
    """Wake word detection response model"""
    wake_word_detected: bool = Field(description="Whether a wake word was detected")
    wake_word: str = Field(default="", description="The detected wake word")
    confidence: float = Field(ge=0.0, le=1.0, description="Detection confidence")
    detection_time: float = Field(description="Time offset of detection in audio")
    method_used: Optional[str] = Field(None, description="Detection method used")


class WakeWordConfigRequest(BaseRequest):
    """Wake word configuration request model"""
    enabled: bool = Field(default=True, description="Enable wake word detection")
    wake_words: List[str] = Field(description="List of wake words to detect")
    sensitivity: float = Field(ge=0.0, le=1.0, description="Detection sensitivity")
    consecutive_detections: int = Field(default=2, ge=1, le=5, description="Required consecutive detections")
    cooldown_period: float = Field(default=2.0, ge=0.5, le=10.0, description="Cooldown period between detections")


# Conversation Models
class ConversationRequest(BaseRequest):
    """Conversation request model"""
    message: str = Field(description="User message", min_length=1, max_length=2000)
    user_id: Optional[str] = Field(None, description="User identifier")
    conversation_id: Optional[str] = Field(None, description="Conversation identifier")
    context: Dict[str, Any] = Field(default_factory=dict, description="Additional context")
    
    @validator('message')
    def validate_message(cls, v):
        if not v.strip():
            raise ValueError("Message cannot be empty or only whitespace")
        return v.strip()


class ConversationResponse(BaseResponse):
    """Conversation response model"""
    message: str = Field(description="User message")
    response: str = Field(description="Assistant response")
    conversation_id: str = Field(description="Conversation identifier")
    turn_id: str = Field(description="Turn identifier")
    context: Dict[str, Any] = Field(description="Conversation context")
    suggested_actions: List[str] = Field(default_factory=list, description="Suggested follow-up actions")


class ConversationHistoryRequest(BaseRequest):
    """Conversation history request model"""
    conversation_id: str = Field(description="Conversation identifier")
    limit: int = Field(default=50, ge=1, le=200, description="Maximum number of turns to return")
    offset: int = Field(default=0, ge=0, description="Number of turns to skip")


class ConversationHistoryResponse(BaseResponse):
    """Conversation history response model"""
    conversation_id: str = Field(description="Conversation identifier")
    turns: List[Dict[str, Any]] = Field(description="Conversation turns")
    total_turns: int = Field(description="Total number of turns in conversation")
    has_more: bool = Field(description="Whether there are more turns available")


# WebSocket Models
class WebSocketMessage(BaseModel):
    """WebSocket message model"""
    type: str = Field(description="Message type")
    session_id: Optional[str] = Field(None, description="Session identifier")
    data: Dict[str, Any] = Field(default_factory=dict, description="Message data")
    timestamp: datetime = Field(default_factory=datetime.utcnow, description="Message timestamp")


class WebSocketCommand(BaseModel):
    """WebSocket command model"""
    command: str = Field(description="Command to execute")
    parameters: Dict[str, Any] = Field(default_factory=dict, description="Command parameters")
    session_id: Optional[str] = Field(None, description="Session identifier")


class WebSocketResponse(BaseModel):
    """WebSocket response model"""
    type: str = Field(description="Response type")
    success: bool = Field(description="Whether command was successful")
    data: Dict[str, Any] = Field(default_factory=dict, description="Response data")
    error: Optional[str] = Field(None, description="Error message if failed")
    session_id: Optional[str] = Field(None, description="Session identifier")
    timestamp: datetime = Field(default_factory=datetime.utcnow, description="Response timestamp")


# Configuration Models
class ServiceConfig(BaseModel):
    """Service configuration model"""
    stt: Dict[str, Any] = Field(default_factory=dict, description="STT configuration")
    tts: Dict[str, Any] = Field(default_factory=dict, description="TTS configuration")
    wake_word: Dict[str, Any] = Field(default_factory=dict, description="Wake word configuration")
    conversation: Dict[str, Any] = Field(default_factory=dict, description="Conversation configuration")
    audio: Dict[str, Any] = Field(default_factory=dict, description="Audio configuration")


class ConfigUpdateRequest(BaseRequest):
    """Configuration update request model"""
    config: ServiceConfig = Field(description="Updated configuration")
    restart_services: bool = Field(default=False, description="Whether to restart services after update")


class ConfigUpdateResponse(BaseResponse):
    """Configuration update response model"""
    updated_sections: List[str] = Field(description="Configuration sections that were updated")
    restart_required: bool = Field(description="Whether a service restart is required")
    validation_warnings: List[str] = Field(default_factory=list, description="Configuration validation warnings")


# Statistics Models
class ServiceStats(BaseModel):
    """Service statistics model"""
    requests_total: int = Field(description="Total number of requests processed")
    requests_successful: int = Field(description="Number of successful requests")
    requests_failed: int = Field(description="Number of failed requests")
    average_processing_time: float = Field(description="Average processing time in seconds")
    uptime_seconds: float = Field(description="Service uptime in seconds")
    memory_usage_mb: float = Field(description="Memory usage in MB")
    cpu_usage_percent: float = Field(description="CPU usage percentage")


class StatsResponse(BaseResponse):
    """Statistics response model"""
    overall: ServiceStats = Field(description="Overall service statistics")
    stt: ServiceStats = Field(description="STT service statistics")
    tts: ServiceStats = Field(description="TTS service statistics")
    wake_word: ServiceStats = Field(description="Wake word service statistics")
    conversation: ServiceStats = Field(description="Conversation service statistics")


# Error Models
class ErrorResponse(BaseResponse):
    """Error response model"""
    success: bool = Field(default=False, description="Always false for error responses")
    error: str = Field(description="Error message")
    error_code: str = Field(description="Error code")
    details: Optional[Dict[str, Any]] = Field(None, description="Additional error details")
    suggestion: Optional[str] = Field(None, description="Suggested resolution")


class ValidationError(BaseModel):
    """Validation error model"""
    field: str = Field(description="Field that failed validation")
    message: str = Field(description="Validation error message")
    value: Optional[Any] = Field(None, description="Invalid value")


class ValidationErrorResponse(ErrorResponse):
    """Validation error response model"""
    error_code: str = Field(default="validation_error", description="Always validation_error")
    validation_errors: List[ValidationError] = Field(description="List of validation errors")


# File Upload Models
class FileUploadRequest(BaseRequest):
    """File upload request model"""
    filename: str = Field(description="Original filename")
    content_type: str = Field(description="File content type")
    file_data: str = Field(description="Base64 encoded file data")
    purpose: str = Field(description="Purpose of the file upload")
    
    @validator('content_type')
    def validate_content_type(cls, v):
        allowed_types = [
            'audio/wav', 'audio/mp3', 'audio/flac', 'audio/ogg',
            'audio/mpeg', 'audio/x-wav', 'audio/wave'
        ]
        if v not in allowed_types:
            raise ValueError(f"Content type {v} not supported. Allowed types: {allowed_types}")
        return v


class FileUploadResponse(BaseResponse):
    """File upload response model"""
    file_id: str = Field(description="Unique file identifier")
    filename: str = Field(description="Stored filename")
    file_size: int = Field(description="File size in bytes")
    content_type: str = Field(description="File content type")
    storage_path: str = Field(description="File storage path")
    processing_status: str = Field(description="File processing status")


# Batch Processing Models
class BatchSTTRequest(BaseRequest):
    """Batch STT processing request model"""
    audio_files: List[str] = Field(description="List of base64 encoded audio files")
    languages: List[str] = Field(default_factory=list, description="Languages for each file")
    confidence_threshold: float = Field(default=0.7, ge=0.0, le=1.0, description="Minimum confidence threshold")
    
    @validator('audio_files')
    def validate_audio_files(cls, v):
        if not v:
            raise ValueError("At least one audio file must be provided")
        if len(v) > 100:  # Reasonable batch size limit
            raise ValueError("Batch size cannot exceed 100 files")
        return v


class BatchSTTResponse(BaseResponse):
    """Batch STT processing response model"""
    results: List[STTResponse] = Field(description="STT results for each file")
    total_files: int = Field(description="Total number of files processed")
    successful_files: int = Field(description="Number of successfully processed files")
    failed_files: int = Field(description="Number of failed files")
    total_processing_time: float = Field(description="Total processing time for batch")


# Model configuration for JSON serialization
class Config:
    """Pydantic model configuration"""
    json_encoders = {
        datetime: lambda v: v.isoformat()
    }
    allow_population_by_field_name = True
    use_enum_values = True
