"""
API Routes - HTTP API Route Definitions
SwarmUI VoiceAssistant Extension - FastAPI Route Handlers

This module defines all HTTP API routes for the voice assistant backend,
including STT, TTS, wake word detection, and conversation management endpoints.
"""

import asyncio
import time
from typing import List, Dict, Any

from fastapi import APIRouter, HTTPException, Depends, BackgroundTasks, File, UploadFile
from fastapi.responses import JSONResponse
from loguru import logger

from .models import (
    # Health and Status
    HealthCheckResponse,
    
    # STT Models
    STTRequest, STTResponse,
    RealtimeSTTRequest, RealtimeSTTResponse,
    BatchSTTRequest, BatchSTTResponse,
    
    # TTS Models
    TTSRequest, TTSResponse,
    VoiceCloneRequest, VoiceCloneResponse,
    VoiceListResponse,
    
    # Wake Word Models
    WakeWordRequest, WakeWordResponse,
    WakeWordConfigRequest,
    
    # Conversation Models
    ConversationRequest, ConversationResponse,
    ConversationHistoryRequest, ConversationHistoryResponse,
    
    # Configuration Models
    ServiceConfig, ConfigUpdateRequest, ConfigUpdateResponse,
    
    # Statistics Models
    StatsResponse,
    
    # Error Models
    ErrorResponse, ValidationErrorResponse,
    
    # File Upload Models
    FileUploadRequest, FileUploadResponse
)

# Create API router
router = APIRouter()

# Global references to services (these will be injected by the main server)
stt_service = None
tts_service = None
wake_word_service = None
conversation_manager = None
audio_processor = None


def get_stt_service():
    """Dependency to get STT service"""
    if stt_service is None:
        raise HTTPException(status_code=503, detail="STT service not available")
    return stt_service


def get_tts_service():
    """Dependency to get TTS service"""
    if tts_service is None:
        raise HTTPException(status_code=503, detail="TTS service not available")
    return tts_service


def get_wake_word_service():
    """Dependency to get wake word service"""
    if wake_word_service is None:
        raise HTTPException(status_code=503, detail="Wake word service not available")
    return wake_word_service


def get_conversation_manager():
    """Dependency to get conversation manager"""
    if conversation_manager is None:
        raise HTTPException(status_code=503, detail="Conversation manager not available")
    return conversation_manager


def get_audio_processor():
    """Dependency to get audio processor"""
    if audio_processor is None:
        raise HTTPException(status_code=503, detail="Audio processor not available")
    return audio_processor


# Health and Status Endpoints
@router.get("/health", response_model=HealthCheckResponse, tags=["Health"])
async def health_check():
    """Health check endpoint"""
    try:
        services_status = {
            "stt_service": stt_service is not None and getattr(stt_service, 'is_initialized', False),
            "tts_service": tts_service is not None and getattr(tts_service, 'is_initialized', False),
            "wake_word_service": wake_word_service is not None,
            "conversation_manager": conversation_manager is not None,
            "audio_processor": audio_processor is not None and getattr(audio_processor, 'is_initialized', False)
        }
        
        all_healthy = all(services_status.values())
        status = "healthy" if all_healthy else "degraded"
        
        return HealthCheckResponse(
            status=status,
            services=services_status,
            uptime=time.time(),  # Simplified uptime
            processing_time=0.001
        )
        
    except Exception as e:
        logger.error(f"Health check failed: {e}")
        raise HTTPException(status_code=500, detail="Health check failed")


@router.get("/status", response_model=Dict[str, Any], tags=["Health"])
async def get_detailed_status():
    """Get detailed service status"""
    try:
        status = {
            "timestamp": time.time(),
            "services": {
                "stt": {
                    "available": stt_service is not None,
                    "initialized": getattr(stt_service, 'is_initialized', False),
                    "active_sessions": len(getattr(stt_service, 'active_sessions', {})),
                    "backend": getattr(stt_service, 'backend', 'unknown')
                },
                "tts": {
                    "available": tts_service is not None,
                    "initialized": getattr(tts_service, 'is_initialized', False),
                    "available_voices": len(getattr(tts_service, 'available_voices', [])),
                    "backend": getattr(tts_service, 'backend', 'unknown')
                },
                "wake_word": {
                    "available": wake_word_service is not None,
                    "listening": getattr(wake_word_service, 'is_listening', False),
                    "available_words": len(getattr(wake_word_service, 'available_wake_words', []))
                },
                "conversation": {
                    "available": conversation_manager is not None,
                    "active_sessions": getattr(conversation_manager, 'get_active_sessions_count', lambda: 0)()
                }
            }
        }
        
        return status
        
    except Exception as e:
        logger.error(f"Status check failed: {e}")
        raise HTTPException(status_code=500, detail="Status check failed")


# STT Endpoints
@router.post("/stt/transcribe", response_model=STTResponse, tags=["Speech-to-Text"])
async def transcribe_audio(request: STTRequest, stt_svc=Depends(get_stt_service)):
    """Transcribe audio to text"""
    try:
        start_time = time.time()
        
        result = await stt_svc.transcribe_audio(
            audio_data=request.audio_data,
            language=request.language,
            confidence_threshold=request.confidence_threshold
        )
        
        processing_time = time.time() - start_time
        
        return STTResponse(
            transcription=result.get("transcription", ""),
            confidence=result.get("confidence", 0.0),
            language=result.get("language", request.language),
            is_final=True,
            processing_time=processing_time,
            backend_used=result.get("model_used", "unknown")
        )
        
    except Exception as e:
        logger.error(f"STT transcription failed: {e}")
        raise HTTPException(status_code=500, detail=f"Transcription failed: {str(e)}")


@router.post("/stt/realtime", response_model=RealtimeSTTResponse, tags=["Speech-to-Text"])
async def realtime_transcribe(request: RealtimeSTTRequest, stt_svc=Depends(get_stt_service)):
    """Process real-time audio chunk"""
    try:
        start_time = time.time()
        
        result = await stt_svc.process_realtime_audio(
            audio_data=request.audio_chunk,
            session_id=request.session_id or "default"
        )
        
        processing_time = time.time() - start_time
        
        return RealtimeSTTResponse(
            partial_transcription=result.get("transcription", ""),
            is_final=result.get("is_final", False),
            confidence=result.get("confidence", 0.0),
            processing_time=processing_time,
            words_added=len(result.get("transcription", "").split()) if result.get("transcription") else 0
        )
        
    except Exception as e:
        logger.error(f"Real-time STT failed: {e}")
        raise HTTPException(status_code=500, detail=f"Real-time transcription failed: {str(e)}")


@router.post("/stt/batch", response_model=BatchSTTResponse, tags=["Speech-to-Text"])
async def batch_transcribe(request: BatchSTTRequest, 
                          background_tasks: BackgroundTasks,
                          stt_svc=Depends(get_stt_service)):
    """Process multiple audio files in batch"""
    try:
        start_time = time.time()
        results = []
        successful_count = 0
        failed_count = 0
        
        # Process each audio file
        for i, audio_data in enumerate(request.audio_files):
            try:
                language = request.languages[i] if i < len(request.languages) else "en-US"
                
                result = await stt_svc.transcribe_audio(
                    audio_data=audio_data,
                    language=language,
                    confidence_threshold=request.confidence_threshold
                )
                
                response = STTResponse(
                    transcription=result.get("transcription", ""),
                    confidence=result.get("confidence", 0.0),
                    language=result.get("language", language),
                    is_final=True,
                    backend_used=result.get("model_used", "unknown")
                )
                
                results.append(response)
                successful_count += 1
                
            except Exception as e:
                logger.error(f"Failed to process audio file {i}: {e}")
                error_response = STTResponse(
                    success=False,
                    transcription="",
                    confidence=0.0,
                    language="unknown",
                    error=str(e)
                )
                results.append(error_response)
                failed_count += 1
        
        total_processing_time = time.time() - start_time
        
        return BatchSTTResponse(
            results=results,
            total_files=len(request.audio_files),
            successful_files=successful_count,
            failed_files=failed_count,
            total_processing_time=total_processing_time,
            processing_time=total_processing_time
        )
        
    except Exception as e:
        logger.error(f"Batch STT failed: {e}")
        raise HTTPException(status_code=500, detail=f"Batch processing failed: {str(e)}")


@router.post("/stt/start/{session_id}", tags=["Speech-to-Text"])
async def start_listening(session_id: str, language: str = "en-US", 
                         stt_svc=Depends(get_stt_service)):
    """Start real-time listening for a session"""
    try:
        success = await stt_svc.start_listening(session_id, language)
        if success:
            return {"success": True, "session_id": session_id, "language": language}
        else:
            raise HTTPException(status_code=500, detail="Failed to start listening")
            
    except Exception as e:
        logger.error(f"Failed to start listening: {e}")
        raise HTTPException(status_code=500, detail=str(e))


@router.post("/stt/stop/{session_id}", tags=["Speech-to-Text"])
async def stop_listening(session_id: str, stt_svc=Depends(get_stt_service)):
    """Stop real-time listening for a session"""
    try:
        result = await stt_svc.stop_listening(session_id)
        return {
            "success": True,
            "session_id": session_id,
            "final_transcription": result.get("transcription", ""),
            "confidence": result.get("confidence", 0.0),
            "duration": result.get("duration", 0.0)
        }
        
    except Exception as e:
        logger.error(f"Failed to stop listening: {e}")
        raise HTTPException(status_code=500, detail=str(e))


# TTS Endpoints
@router.post("/tts/synthesize", response_model=TTSResponse, tags=["Text-to-Speech"])
async def synthesize_speech(request: TTSRequest, tts_svc=Depends(get_tts_service)):
    """Synthesize speech from text"""
    try:
        start_time = time.time()
        
        result = await tts_svc.synthesize_speech(
            text=request.text,
            voice=request.voice,
            speed=request.speed,
            volume=request.volume,
            language=request.language,
            emotion=request.emotion,
            pacing=request.pacing
        )
        
        processing_time = time.time() - start_time
        
        return TTSResponse(
            audio_data=result.get("audio_data", ""),
            duration=result.get("duration", 0.0),
            sample_rate=result.get("sample_rate", 22050),
            channels=result.get("channels", 1),
            voice_used=result.get("voice", request.voice),
            backend_used=result.get("backend", "unknown"),
            audio_format="wav",
            processing_time=processing_time
        )
        
    except Exception as e:
        logger.error(f"TTS synthesis failed: {e}")
        raise HTTPException(status_code=500, detail=f"Speech synthesis failed: {str(e)}")


@router.get("/tts/voices", response_model=VoiceListResponse, tags=["Text-to-Speech"])
async def list_voices(tts_svc=Depends(get_tts_service)):
    """List available TTS voices"""
    try:
        voices = tts_svc.list_available_voices()
        
        # Convert to detailed voice info
        voice_details = []
        builtin_count = 0
        custom_count = 0
        
        for voice in voices:
            voice_info = {
                "name": voice,
                "type": "builtin" if voice in ["default", "male", "female"] else "custom",
                "language": "en-US",  # Would be determined from voice metadata
                "available": True
            }
            voice_details.append(voice_info)
            
            if voice_info["type"] == "builtin":
                builtin_count += 1
            else:
                custom_count += 1
        
        return VoiceListResponse(
            voices=voice_details,
            total_count=len(voices),
            builtin_count=builtin_count,
            custom_count=custom_count
        )
        
    except Exception as e:
        logger.error(f"Failed to list voices: {e}")
        raise HTTPException(status_code=500, detail=f"Failed to list voices: {str(e)}")


@router.post("/tts/clone", response_model=VoiceCloneResponse, tags=["Text-to-Speech"])
async def clone_voice(request: VoiceCloneRequest, tts_svc=Depends(get_tts_service)):
    """Clone a voice from reference audio"""
    try:
        start_time = time.time()
        
        voice_path = await tts_svc.clone_voice(
            reference_audio=request.reference_audio,
            voice_name=request.voice_name
        )
        
        # Get voice sample info
        voice_info = await tts_svc.get_voice_sample_info(voice_path)
        
        processing_time = time.time() - start_time
        
        return VoiceCloneResponse(
            voice_name=request.voice_name,
            voice_path=voice_path,
            quality_score=min(1.0, voice_info.get("rms_level", 0.0) * 10),  # Simple quality metric
            duration=voice_info.get("duration", 0.0),
            processing_time=processing_time
        )
        
    except Exception as e:
        logger.error(f"Voice cloning failed: {e}")
        raise HTTPException(status_code=500, detail=f"Voice cloning failed: {str(e)}")


@router.delete("/tts/voices/{voice_name}", tags=["Text-to-Speech"])
async def delete_voice(voice_name: str, tts_svc=Depends(get_tts_service)):
    """Delete a custom voice"""
    try:
        success = tts_svc.delete_voice_sample(voice_name)
        if success:
            return {"success": True, "voice_name": voice_name, "message": "Voice deleted successfully"}
        else:
            raise HTTPException(status_code=404, detail="Voice not found")
            
    except Exception as e:
        logger.error(f"Failed to delete voice: {e}")
        raise HTTPException(status_code=500, detail=str(e))


# Wake Word Endpoints
@router.post("/wake_word/detect", response_model=WakeWordResponse, tags=["Wake Word"])
async def detect_wake_word(request: WakeWordRequest, 
                          wake_word_svc=Depends(get_wake_word_service)):
    """Detect wake words in audio"""
    try:
        start_time = time.time()
        
        result = await wake_word_svc.detect_wake_word(
            audio_data=request.audio_data,
            wake_words=request.wake_words,
            sensitivity=request.sensitivity
        )
        
        processing_time = time.time() - start_time
        
        return WakeWordResponse(
            wake_word_detected=result.get("detected", False),
            wake_word=result.get("wake_word", ""),
            confidence=result.get("confidence", 0.0),
            detection_time=result.get("timestamp", 0.0),
            processing_time=processing_time,
            method_used=result.get("method", "unknown")
        )
        
    except Exception as e:
        logger.error(f"Wake word detection failed: {e}")
        raise HTTPException(status_code=500, detail=f"Wake word detection failed: {str(e)}")


@router.post("/wake_word/start", tags=["Wake Word"])
async def start_wake_word_detection(request: WakeWordConfigRequest,
                                   wake_word_svc=Depends(get_wake_word_service)):
    """Start wake word detection"""
    try:
        success = await wake_word_svc.start_detection(
            wake_words=request.wake_words,
            sensitivity=request.sensitivity
        )
        
        if success:
            return {
                "success": True,
                "message": "Wake word detection started",
                "wake_words": request.wake_words,
                "sensitivity": request.sensitivity
            }
        else:
            raise HTTPException(status_code=500, detail="Failed to start wake word detection")
            
    except Exception as e:
        logger.error(f"Failed to start wake word detection: {e}")
        raise HTTPException(status_code=500, detail=str(e))


@router.post("/wake_word/stop", tags=["Wake Word"])
async def stop_wake_word_detection(wake_word_svc=Depends(get_wake_word_service)):
    """Stop wake word detection"""
    try:
        success = await wake_word_svc.stop_detection()
        
        if success:
            return {"success": True, "message": "Wake word detection stopped"}
        else:
            raise HTTPException(status_code=500, detail="Failed to stop wake word detection")
            
    except Exception as e:
        logger.error(f"Failed to stop wake word detection: {e}")
        raise HTTPException(status_code=500, detail=str(e))


@router.get("/wake_word/stats", tags=["Wake Word"])
async def get_wake_word_stats(wake_word_svc=Depends(get_wake_word_service)):
    """Get wake word detection statistics"""
    try:
        stats = wake_word_svc.get_detection_stats()
        return stats
        
    except Exception as e:
        logger.error(f"Failed to get wake word stats: {e}")
        raise HTTPException(status_code=500, detail=str(e))


# Conversation Endpoints
@router.post("/conversation/message", response_model=ConversationResponse, tags=["Conversation"])
async def send_message(request: ConversationRequest,
                      conv_mgr=Depends(get_conversation_manager)):
    """Send a message in a conversation"""
    try:
        start_time = time.time()
        
        result = await conv_mgr.process_text_input(
            session_id=request.session_id or request.conversation_id,
            text=request.message,
            user_id=request.user_id
        )
        
        processing_time = time.time() - start_time
        
        return ConversationResponse(
            message=request.message,
            response=result.get("assistant_response", ""),
            conversation_id=result.get("session_id", ""),
            turn_id=result.get("turn_id", ""),
            context=result.get("context", {}),
            processing_time=processing_time
        )
        
    except Exception as e:
        logger.error(f"Conversation message failed: {e}")
        raise HTTPException(status_code=500, detail=f"Message processing failed: {str(e)}")


@router.get("/conversation/{conversation_id}/history", 
           response_model=ConversationHistoryResponse, tags=["Conversation"])
async def get_conversation_history(conversation_id: str, limit: int = 50, offset: int = 0,
                                  conv_mgr=Depends(get_conversation_manager)):
    """Get conversation history"""
    try:
        history = conv_mgr.get_conversation_history(conversation_id, limit + offset)
        
        # Apply offset and limit
        paginated_history = history[offset:offset + limit]
        has_more = len(history) > offset + limit
        
        return ConversationHistoryResponse(
            conversation_id=conversation_id,
            turns=paginated_history,
            total_turns=len(history),
            has_more=has_more
        )
        
    except Exception as e:
        logger.error(f"Failed to get conversation history: {e}")
        raise HTTPException(status_code=500, detail=str(e))


@router.get("/conversation/{conversation_id}/stats", tags=["Conversation"])
async def get_conversation_stats(conversation_id: str,
                                conv_mgr=Depends(get_conversation_manager)):
    """Get conversation statistics"""
    try:
        stats = conv_mgr.get_session_stats(conversation_id)
        return stats
        
    except Exception as e:
        logger.error(f"Failed to get conversation stats: {e}")
        raise HTTPException(status_code=500, detail=str(e))


@router.delete("/conversation/{conversation_id}", tags=["Conversation"])
async def end_conversation(conversation_id: str,
                          conv_mgr=Depends(get_conversation_manager)):
    """End a conversation"""
    try:
        conv_mgr.end_session(conversation_id)
        return {"success": True, "conversation_id": conversation_id, "message": "Conversation ended"}
        
    except Exception as e:
        logger.error(f"Failed to end conversation: {e}")
        raise HTTPException(status_code=500, detail=str(e))


# Configuration Endpoints
@router.get("/config", response_model=ServiceConfig, tags=["Configuration"])
async def get_configuration():
    """Get current service configuration"""
    try:
        # This would return actual configuration from services
        config = ServiceConfig(
            stt={"model": "whisper-base", "language": "en-US"},
            tts={"voice": "default", "speed": 1.0},
            wake_word={"enabled": True, "sensitivity": 0.5},
            conversation={"max_turns": 100},
            audio={"sample_rate": 16000, "channels": 1}
        )
        
        return config
        
    except Exception as e:
        logger.error(f"Failed to get configuration: {e}")
        raise HTTPException(status_code=500, detail=str(e))


@router.post("/config", response_model=ConfigUpdateResponse, tags=["Configuration"])
async def update_configuration(request: ConfigUpdateRequest):
    """Update service configuration"""
    try:
        # TODO: Update the actual configs
        updated_sections = []
        
        if request.config.stt:
            updated_sections.append("stt")
        if request.config.tts:
            updated_sections.append("tts")
        if request.config.wake_word:
            updated_sections.append("wake_word")
        if request.config.conversation:
            updated_sections.append("conversation")
        if request.config.audio:
            updated_sections.append("audio")
        
        return ConfigUpdateResponse(
            updated_sections=updated_sections,
            restart_required=request.restart_services,
            validation_warnings=[]
        )
        
    except Exception as e:
        logger.error(f"Failed to update configuration: {e}")
        raise HTTPException(status_code=500, detail=str(e))


# Statistics Endpoints
@router.get("/stats", response_model=StatsResponse, tags=["Statistics"])
async def get_statistics():
    """Get service statistics"""
    try:
        # TODO: collect actual stats
        from .models import ServiceStats
        
        overall_stats = ServiceStats(
            requests_total=1000,
            requests_successful=950,
            requests_failed=50,
            average_processing_time=0.5,
            uptime_seconds=3600,
            memory_usage_mb=256,
            cpu_usage_percent=25.0
        )
        
        return StatsResponse(
            overall=overall_stats,
            stt=overall_stats,
            tts=overall_stats,
            wake_word=overall_stats,
            conversation=overall_stats
        )
        
    except Exception as e:
        logger.error(f"Failed to get statistics: {e}")
        raise HTTPException(status_code=500, detail=str(e))


# File Upload Endpoints
@router.post("/upload/audio", response_model=FileUploadResponse, tags=["File Upload"])
async def upload_audio_file(file: UploadFile = File(...)):
    """Upload audio file for processing"""
    try:
        # Validate file type
        if not file.content_type.startswith('audio/'):
            raise HTTPException(status_code=400, detail="File must be an audio file")
        
        # Read file content
        file_content = await file.read()
        
        # Generate file ID and storage path
        import uuid
        file_id = str(uuid.uuid4())
        storage_path = f"uploads/{file_id}_{file.filename}"
        
        # TODO: Save the file to storage?
        
        return FileUploadResponse(
            file_id=file_id,
            filename=file.filename,
            file_size=len(file_content),
            content_type=file.content_type,
            storage_path=storage_path,
            processing_status="uploaded"
        )
        
    except Exception as e:
        logger.error(f"File upload failed: {e}")
        raise HTTPException(status_code=500, detail=f"File upload failed: {str(e)}")


# Utility function to set service references
def set_service_references(stt, tts, wake_word, conversation, audio):
    """Set global service references for dependency injection"""
    global stt_service, tts_service, wake_word_service, conversation_manager, audio_processor
    stt_service = stt
    tts_service = tts
    wake_word_service = wake_word
    conversation_manager = conversation
    audio_processor = audio
