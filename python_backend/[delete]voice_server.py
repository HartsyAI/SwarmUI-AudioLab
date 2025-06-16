#!/usr/bin/env python3
"""
SwarmUI Voice Assistant Backend Server - Complete Production Version
Production-ready FastAPI server providing STT, TTS, and pipeline services.

This server handles:
- Pure Speech-to-Text transcription via /stt/transcribe
- Pure Text-to-Speech synthesis via /tts/synthesize
- Configurable pipeline processing via /pipeline/process
- Health monitoring and graceful shutdown
- CORS configuration for SwarmUI integration
- Server-side audio recording support
"""

import asyncio
import logging
import signal
import sys
import os
import time
import tempfile
import base64
import io
from contextlib import asynccontextmanager
from typing import Optional, Dict, Any, List

# Add current directory to Python path to ensure modules can be imported
current_dir = os.path.dirname(os.path.abspath(__file__))
if current_dir not in sys.path:
    sys.path.insert(0, current_dir)
    print(f"Added current directory to Python path: {current_dir}")

import uvicorn
from fastapi import FastAPI, HTTPException, Request, BackgroundTasks
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import JSONResponse
from pydantic import BaseModel, Field

# Configure comprehensive logging
logging.basicConfig(
    level=logging.INFO,
    format='%(asctime)s - %(name)s - %(levelname)s - %(message)s',
    handlers=[
        logging.StreamHandler(sys.stdout),
        logging.StreamHandler(sys.stderr)
    ]
)
logger = logging.getLogger("VoiceAssistant.Server")

# Global shutdown event for graceful termination
shutdown_event = asyncio.Event()

# Service availability flags
stt_service_available = False
tts_service_available = False
stt_service = None
tts_service = None

# Server-side audio recording support
audio_recording_active = False
audio_recording_data = []
audio_recording_start_time = None

def check_and_import_services():
    """
    Check for available STT/TTS services and import them if possible.
    This allows the server to run even if some dependencies are missing.
    """
    global stt_service_available, tts_service_available, stt_service, tts_service
    
    # Try to import STT service
    try:
        from stt_service import stt_service as imported_stt
        stt_service = imported_stt
        stt_service_available = True
        logger.info("STT service imported successfully")
    except ImportError as e:
        logger.warning(f"STT service not available: {e}")
        logger.warning("Voice transcription will not be available")
        stt_service_available = False
    except Exception as e:
        logger.error(f"Error importing STT service: {e}")
        stt_service_available = False
    
    # Try to import TTS service
    try:
        from tts_service import tts_service as imported_tts
        tts_service = imported_tts
        tts_service_available = True
        logger.info("TTS service imported successfully")
    except ImportError as e:
        logger.warning(f"TTS service not available: {e}")
        logger.warning("Voice synthesis will not be available")
        tts_service_available = False
    except Exception as e:
        logger.error(f"Error importing TTS service: {e}")
        tts_service_available = False

@asynccontextmanager
async def lifespan(app: FastAPI):
    """
    Application lifespan manager for startup and shutdown tasks.
    
    Handles:
    - Service initialization during startup
    - Graceful cleanup during shutdown
    - Resource management for STT/TTS services
    """
    global stt_service_available, tts_service_available
    
    logger.info("Starting Voice Assistant Backend Server with Generic Endpoints")
    
    try:
        # Check what services are available
        check_and_import_services()
        
        # Initialize available services
        if stt_service_available and stt_service:
            logger.info("Initializing STT service...")
            try:
                await stt_service.initialize()
                logger.info("STT service initialized successfully")
            except Exception as e:
                logger.error(f"STT service initialization failed: {e}")
                stt_service_available = False
        
        if tts_service_available and tts_service:
            logger.info("Initializing TTS service...")
            try:
                await tts_service.initialize()
                logger.info("TTS service initialized successfully")
            except Exception as e:
                logger.error(f"TTS service initialization failed: {e}")
                tts_service_available = False
        
        if not stt_service_available and not tts_service_available:
            logger.warning("No voice services available - server running in limited mode")
        else:
            logger.info("Voice Assistant Backend Server ready with generic endpoints")
        
        yield  # Server runs here
        
    except Exception as e:
        logger.error(f"Failed to initialize services: {e}")
        # Continue anyway in limited mode
        yield
    finally:
        # Cleanup during shutdown
        logger.info("Shutting down Voice Assistant Backend Server")
        # Stop any active audio recording
        stop_server_audio_recording()
        logger.info("Service cleanup completed")

# Initialize FastAPI with lifespan management
app = FastAPI(
    title="SwarmUI Voice Assistant Backend",
    description="Backend service providing STT, TTS, and pipeline capabilities with generic endpoints",
    version="2.0.0",
    docs_url="/docs",
    redoc_url="/redoc",
    lifespan=lifespan
)

# Configure CORS for SwarmUI integration
app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],  # In production, specify exact origins
    allow_credentials=True,
    allow_methods=["GET", "POST", "PUT", "DELETE", "OPTIONS"],
    allow_headers=["*"],
)

# Request/Response Models for Generic Endpoints
class STTRequest(BaseModel):
    """Pure Speech-to-Text request model."""
    audio_data: str = Field(..., description="Base64 encoded audio data")
    language: str = Field(default="en-US", description="Language code for transcription")
    options: Optional[Dict[str, Any]] = Field(default_factory=dict, description="STT processing options")
    
    class Config:
        schema_extra = {
            "example": {
                "audio_data": "UklGRjIAAABXQVZFZm10IBAAAAAB...",
                "language": "en-US",
                "options": {
                    "return_confidence": True,
                    "return_alternatives": False,
                    "model_preference": "accuracy"
                }
            }
        }

class TTSRequest(BaseModel):
    """Pure Text-to-Speech request model."""
    text: str = Field(..., description="Text to convert to speech")
    voice: str = Field(default="default", description="Voice model to use")
    language: Optional[str] = Field(default="en-US", description="Language code for synthesis")
    volume: float = Field(default=0.8, ge=0.0, le=1.0, description="Volume level (0.0 to 1.0)")
    options: Optional[Dict[str, Any]] = Field(default_factory=dict, description="TTS processing options")
    
    class Config:
        schema_extra = {
            "example": {
                "text": "Hello, this is a test message.",
                "voice": "default",
                "language": "en-US",
                "volume": 0.8,
                "options": {
                    "speed": 1.0,
                    "pitch": 1.0,
                    "format": "wav"
                }
            }
        }

class PipelineStep(BaseModel):
    """Pipeline step configuration."""
    type: str = Field(..., description="Step type (stt, tts, command_processing)")
    enabled: bool = Field(default=True, description="Whether this step is enabled")
    config: Dict[str, Any] = Field(default_factory=dict, description="Step-specific configuration")

class PipelineRequest(BaseModel):
    """Configurable pipeline request model."""
    input_type: str = Field(..., description="Type of input (audio or text)")
    input_data: str = Field(..., description="Input data (base64 audio or text)")
    pipeline_steps: List[PipelineStep] = Field(..., description="List of pipeline steps to execute")
    session_id: Optional[str] = Field(default=None, description="Optional session identifier")
    
    class Config:
        schema_extra = {
            "example": {
                "input_type": "audio",
                "input_data": "UklGRjIAAABXQVZFZm10IBAAAAAB...",
                "pipeline_steps": [
                    {
                        "type": "stt",
                        "enabled": True,
                        "config": {"language": "en-US"}
                    },
                    {
                        "type": "tts",
                        "enabled": True,
                        "config": {"voice": "default", "volume": 0.8}
                    }
                ],
                "session_id": "session-123"
            }
        }

class ServerAudioRequest(BaseModel):
    """Server-side audio recording request."""
    duration: Optional[float] = Field(default=5.0, description="Recording duration in seconds")
    sample_rate: Optional[int] = Field(default=16000, description="Audio sample rate")
    channels: Optional[int] = Field(default=1, description="Number of audio channels")

class HealthResponse(BaseModel):
    """Health check response model."""
    status: str = Field(..., description="Service status")
    version: str = Field(..., description="Server version")
    endpoint_version: str = Field(..., description="API endpoint version")
    services: Dict[str, bool] = Field(..., description="Individual service status")
    supported_endpoints: List[str] = Field(..., description="List of supported endpoints")
    missing_dependencies: Optional[Dict[str, str]] = Field(None, description="Missing dependencies info")

# Server-side audio recording functions
def start_server_audio_recording(duration: float = 5.0, sample_rate: int = 16000, channels: int = 1):
    """
    Start server-side audio recording using system microphone.
    This bypasses browser security restrictions.
    """
    global audio_recording_active, audio_recording_data, audio_recording_start_time
    
    try:
        # Try to import audio recording library
        try:
            import sounddevice as sd
        except ImportError:
            logger.warning("sounddevice not available for server-side recording")
            return False
        
        if audio_recording_active:
            logger.warning("Audio recording already active")
            return False
        
        logger.info(f"Starting server-side audio recording: {duration}s, {sample_rate}Hz, {channels} channels")
        
        # Reset recording data
        audio_recording_data = []
        audio_recording_start_time = time.time()
        audio_recording_active = True
        
        # Record audio
        def recording_callback(indata, frames, time, status):
            if status:
                logger.warning(f"Audio recording status: {status}")
            if audio_recording_active:
                audio_recording_data.append(indata.copy())
        
        # Start recording in background
        import threading
        def record_audio():
            global audio_recording_active
            try:
                with sd.InputStream(
                    callback=recording_callback,
                    duration=duration,
                    samplerate=sample_rate,
                    channels=channels,
                    dtype='float32'
                ):
                    sd.sleep(int(duration * 1000))
                audio_recording_active = False
                logger.info("Server-side audio recording completed")
            except Exception as e:
                logger.error(f"Error during server-side recording: {e}")
                audio_recording_active = False
        
        recording_thread = threading.Thread(target=record_audio)
        recording_thread.daemon = True
        recording_thread.start()
        
        return True
        
    except Exception as e:
        logger.error(f"Failed to start server-side audio recording: {e}")
        audio_recording_active = False
        return False

def stop_server_audio_recording():
    """Stop server-side audio recording."""
    global audio_recording_active
    if audio_recording_active:
        audio_recording_active = False
        logger.info("Server-side audio recording stopped")

def get_server_audio_data():
    """Get recorded audio data as base64."""
    global audio_recording_data
    
    if not audio_recording_data:
        return None
    
    try:
        import numpy as np
        import wave
        
        # Combine all audio chunks
        audio_array = np.concatenate(audio_recording_data, axis=0)
        
        # Convert to 16-bit PCM
        audio_int16 = (audio_array * 32767).astype(np.int16)
        
        # Create WAV file in memory
        wav_buffer = io.BytesIO()
        with wave.open(wav_buffer, 'wb') as wav_file:
            wav_file.setnchannels(1)
            wav_file.setsampwidth(2)
            wav_file.setframerate(16000)
            wav_file.writeframes(audio_int16.tobytes())
        
        # Convert to base64
        wav_data = wav_buffer.getvalue()
        base64_audio = base64.b64encode(wav_data).decode('utf-8')
        
        # Clear recording data
        audio_recording_data = []
        
        return base64_audio
        
    except Exception as e:
        logger.error(f"Error processing server audio data: {e}")
        return None

# Health check endpoint
@app.get("/health", response_model=HealthResponse)
async def health_check():
    """
    Health check endpoint for service monitoring.
    
    Returns the overall health status and individual service availability.
    Used by SwarmUI to verify backend readiness.
    """
    try:
        # Check service initialization status
        stt_ready = stt_service_available and (stt_service.initialized if stt_service else False)
        tts_ready = tts_service_available and (tts_service.initialized if tts_service else False)
        
        # Determine overall status
        if stt_ready and tts_ready:
            overall_status = "healthy"
        elif stt_ready or tts_ready:
            overall_status = "degraded"
        else:
            overall_status = "limited"
        
        # Collect missing dependency information
        missing_deps = {}
        if not stt_service_available:
            missing_deps["stt"] = "Install RealtimeSTT"
        if not tts_service_available:
            missing_deps["tts"] = "Install chatterbox-tts"
        
        response = HealthResponse(
            status=overall_status,
            version="2.0.0",
            endpoint_version="generic",
            services={
                "stt": stt_ready,
                "tts": tts_ready,
                "server_audio": True  # Server-side recording always available
            },
            supported_endpoints=[
                "/stt/transcribe",
                "/tts/synthesize", 
                "/pipeline/process",
                "/audio/start_recording",
                "/audio/stop_recording",
                "/audio/get_data",
                "/health",
                "/status",
                "/shutdown"
            ],
            missing_dependencies=missing_deps if missing_deps else None
        )
        
        logger.debug(f"Health check: {overall_status} (STT: {stt_ready}, TTS: {tts_ready})")
        return response
        
    except Exception as e:
        logger.error(f"Health check failed: {e}")
        raise HTTPException(status_code=500, detail="Health check failed")

# Server-side audio recording endpoints
@app.post("/audio/start_recording")
async def start_audio_recording(request: ServerAudioRequest):
    """Start server-side audio recording."""
    try:
        logger.info(f"Starting server-side audio recording: {request.duration}s")
        
        success = start_server_audio_recording(
            duration=request.duration,
            sample_rate=request.sample_rate,
            channels=request.channels
        )
        
        if success:
            return {
                "success": True,
                "message": "Server-side audio recording started",
                "duration": request.duration,
                "sample_rate": request.sample_rate,
                "channels": request.channels
            }
        else:
            raise HTTPException(status_code=500, detail="Failed to start server-side audio recording")
            
    except Exception as e:
        logger.error(f"Error starting server audio recording: {e}")
        raise HTTPException(status_code=500, detail=str(e))

@app.post("/audio/stop_recording")
async def stop_audio_recording():
    """Stop server-side audio recording."""
    try:
        stop_server_audio_recording()
        return {
            "success": True,
            "message": "Server-side audio recording stopped"
        }
    except Exception as e:
        logger.error(f"Error stopping server audio recording: {e}")
        raise HTTPException(status_code=500, detail=str(e))

@app.get("/audio/get_data")
async def get_audio_data():
    """Get recorded audio data."""
    try:
        audio_data = get_server_audio_data()
        
        if audio_data:
            return {
                "success": True,
                "audio_data": audio_data,
                "format": "wav",
                "sample_rate": 16000,
                "channels": 1
            }
        else:
            return {
                "success": False,
                "message": "No audio data available"
            }
            
    except Exception as e:
        logger.error(f"Error getting audio data: {e}")
        raise HTTPException(status_code=500, detail=str(e))

@app.get("/audio/status")
async def get_audio_status():
    """Get server-side audio recording status."""
    global audio_recording_active, audio_recording_start_time
    
    recording_duration = 0
    if audio_recording_active and audio_recording_start_time:
        recording_duration = time.time() - audio_recording_start_time
    
    return {
        "recording_active": audio_recording_active,
        "recording_duration": recording_duration,
        "has_data": len(audio_recording_data) > 0 if audio_recording_data else False
    }

# Pure STT endpoint
@app.post("/stt/transcribe")
async def transcribe_audio(request: STTRequest):
    """
    Pure Speech-to-Text transcription endpoint.
    
    Processes base64 encoded audio data and returns transcribed text.
    Supports multiple languages and provides confidence scores.
    """
    try:
        logger.info(f"Processing STT request for language: {request.language}")
        
        if not stt_service_available or not stt_service:
            raise HTTPException(
                status_code=503, 
                detail="STT service not available. Please install RealtimeSTT."
            )
        
        if not stt_service.initialized:
            logger.error("STT service not initialized")
            raise HTTPException(status_code=503, detail="STT service not initialized")
        
        # Validate audio data
        if not request.audio_data:
            raise HTTPException(status_code=400, detail="No audio data provided")
        
        # Process transcription with options
        options = request.options or {}
        result = await stt_service.transcribe(request.audio_data, request.language)
        
        # Build response with metadata
        response = {
            "success": True,
            "transcription": result.get("transcription", ""),
            "confidence": result.get("confidence", 0.0),
            "language": request.language,
            "processing_time": result.get("processing_time", 0.0),
            "metadata": {
                "model_used": result.get("method", "unknown"),
                "audio_duration": 0.0,  # TODO: Calculate from audio data
                "audio_format": "webm",  # Assume WebM from browser
                "sample_rate": 16000    # Default sample rate
            }
        }
        
        # Add alternatives if requested
        if options.get("return_alternatives", False):
            response["alternatives"] = result.get("alternatives", [])
        
        logger.info(f"STT completed successfully: '{result.get('transcription', '')[:50]}...'")
        return response
        
    except HTTPException:
        raise
    except Exception as e:
        logger.error(f"STT transcription failed: {e}")
        raise HTTPException(status_code=500, detail=f"Transcription failed: {str(e)}")

# Pure TTS endpoint
@app.post("/tts/synthesize")
async def synthesize_speech(request: TTSRequest):
    """
    Pure Text-to-Speech synthesis endpoint.
    
    Converts text to speech audio and returns base64 encoded audio data.
    Supports multiple voices, languages, and volume control.
    """
    try:
        logger.info(f"Processing TTS request: '{request.text[:50]}...' with voice: {request.voice}")
        
        if not tts_service_available or not tts_service:
            raise HTTPException(
                status_code=503, 
                detail="TTS service not available. Please install chatterbox-tts."
            )
        
        if not tts_service.initialized:
            logger.error("TTS service not initialized")
            raise HTTPException(status_code=503, detail="TTS service not initialized")
        
        # Validate text input
        if not request.text or not request.text.strip():
            raise HTTPException(status_code=400, detail="No text provided for synthesis")
        
        if len(request.text) > 1000:  # Reasonable limit for voice responses
            raise HTTPException(status_code=400, detail="Text too long (max 1000 characters)")
        
        # Process synthesis with options
        options = request.options or {}
        result = await tts_service.synthesize(
            text=request.text,
            voice=request.voice,
            language=request.language,
            volume=request.volume
        )
        
        # Build response with metadata
        response = {
            "success": True,
            "audio_data": result.get("audio_data", ""),
            "text": request.text,
            "voice": request.voice,
            "language": request.language,
            "volume": request.volume,
            "duration": result.get("duration", 0.0),
            "processing_time": result.get("processing_time", 0.0),
            "metadata": {
                "voice_used": result.get("method", request.voice),
                "sample_rate": 22050,  # Default for Chatterbox TTS
                "audio_format": options.get("format", "wav"),
                "audio_channels": 1
            }
        }
        
        logger.info("TTS synthesis completed successfully")
        return response
        
    except HTTPException:
        raise
    except Exception as e:
        logger.error(f"TTS synthesis failed: {e}")
        raise HTTPException(status_code=500, detail=f"Speech synthesis failed: {str(e)}")

# Pipeline processing endpoint
@app.post("/pipeline/process")
async def process_pipeline(request: PipelineRequest):
    """
    Configurable pipeline processing endpoint.
    
    Orchestrates multiple processing steps (STT, Commands, TTS) based on configuration.
    Allows for flexible workflows and custom processing pipelines.
    """
    try:
        logger.info(f"Processing pipeline with {len(request.pipeline_steps)} steps for input type: {request.input_type}")
        
        start_time = time.time()
        pipeline_results = {}
        executed_steps = []
        current_data = request.input_data
        
        # Filter enabled steps
        enabled_steps = [step for step in request.pipeline_steps if step.enabled]
        
        if not enabled_steps:
            raise HTTPException(status_code=400, detail="No enabled pipeline steps provided")
        
        # Validate pipeline logic
        if request.input_type == "audio" and enabled_steps[0].type != "stt":
            raise HTTPException(status_code=400, detail="Audio input requires STT as first step")
        
        if request.input_type == "text" and enabled_steps[0].type == "stt":
            raise HTTPException(status_code=400, detail="Text input cannot start with STT step")
        
        # Process each step
        for i, step in enumerate(enabled_steps):
            try:
                logger.debug(f"Processing pipeline step {i+1}/{len(enabled_steps)}: {step.type}")
                
                if step.type == "stt":
                    step_result = await process_stt_step(current_data, step.config)
                    current_data = step_result.get("transcription", "")
                    
                elif step.type == "tts":
                    step_result = await process_tts_step(current_data, step.config)
                    current_data = step_result.get("audio_data", "")
                    
                elif step.type == "command_processing":
                    step_result = await process_command_step(current_data, step.config)
                    current_data = step_result.get("response", current_data)
                    
                else:
                    raise HTTPException(status_code=400, detail=f"Unknown pipeline step type: {step.type}")
                
                pipeline_results[step.type] = step_result
                executed_steps.append(step.type)
                
            except Exception as step_error:
                logger.error(f"Pipeline step '{step.type}' failed: {step_error}")
                pipeline_results[step.type] = {
                    "success": False,
                    "error": str(step_error)
                }
                executed_steps.append(f"{step.type} (failed)")
                
                # For now, continue with other steps
                # TODO: Add configuration for fail-fast vs continue-on-error
        
        total_processing_time = time.time() - start_time
        
        response = {
            "success": True,
            "pipeline_results": pipeline_results,
            "executed_steps": executed_steps,
            "total_processing_time": total_processing_time,
            "session_id": request.session_id
        }
        
        logger.info(f"Pipeline processing completed with {len(executed_steps)} steps")
        return response
        
    except HTTPException:
        raise
    except Exception as e:
        logger.error(f"Pipeline processing failed: {e}")
        raise HTTPException(status_code=500, detail=f"Pipeline processing failed: {str(e)}")

# Pipeline step processors
async def process_stt_step(audio_data: str, config: Dict[str, Any]) -> Dict[str, Any]:
    """Process STT step in pipeline."""
    if not stt_service_available or not stt_service:
        raise HTTPException(status_code=503, detail="STT service not available for pipeline step")
    
    language = config.get("language", "en-US")
    options = config.get("options", {})
    
    result = await stt_service.transcribe(audio_data, language)
    return {
        "success": True,
        "transcription": result.get("transcription", ""),
        "confidence": result.get("confidence", 0.0),
        "processing_time": result.get("processing_time", 0.0)
    }

async def process_tts_step(text: str, config: Dict[str, Any]) -> Dict[str, Any]:
    """Process TTS step in pipeline."""
    if not tts_service_available or not tts_service:
        raise HTTPException(status_code=503, detail="TTS service not available for pipeline step")
    
    voice = config.get("voice", "default")
    language = config.get("language", "en-US")
    volume = config.get("volume", 0.8)
    
    result = await tts_service.synthesize(
        text=text,
        voice=voice,
        language=language,
        volume=volume
    )
    return {
        "success": True,
        "audio_data": result.get("audio_data", ""),
        "duration": result.get("duration", 0.0),
        "processing_time": result.get("processing_time", 0.0)
    }

async def process_command_step(text: str, config: Dict[str, Any]) -> Dict[str, Any]:
    """Process command step in pipeline."""
    # TODO: Implement actual command processing
    # For now, return a placeholder response
    processor = config.get("processor", "placeholder")
    
    if processor == "echo":
        response_text = f"Echo: {text}"
    else:
        response_text = "Command processing is not yet implemented. This will be added in a future version."
    
    return {
        "success": True,
        "command": "placeholder",
        "response": response_text,
        "confidence": 0.0,
        "processing_time": 0.001
    }

# Service status endpoint
@app.get("/status")
async def get_service_status():
    """
    Detailed service status endpoint.
    
    Returns detailed information about available services and their capabilities.
    """
    try:
        status = {
            "server_version": "2.0.0",
            "endpoint_version": "generic",
            "services": {
                "stt": {
                    "available": stt_service_available,
                    "initialized": stt_service.initialized if stt_service else False,
                    "status": stt_service.get_status() if stt_service else None
                },
                "tts": {
                    "available": tts_service_available,
                    "initialized": tts_service.initialized if tts_service else False,
                    "status": tts_service.get_status() if tts_service else None
                },
                "server_audio": {
                    "available": True,
                    "recording_active": audio_recording_active
                }
            },
            "capabilities": {
                "transcription": stt_service_available,
                "synthesis": tts_service_available,
                "server_side_recording": True,
                "pipeline_processing": True,
                "languages": getattr(stt_service, 'supported_languages', []) if stt_service else [],
                "voices": getattr(tts_service, 'available_voices', []) if tts_service else []
            },
            "supported_endpoints": [
                "/stt/transcribe",
                "/tts/synthesize", 
                "/pipeline/process",
                "/audio/start_recording",
                "/audio/stop_recording",
                "/audio/get_data",
                "/audio/status",
                "/health",
                "/status",
                "/shutdown"
            ],
            "pipeline_step_types": ["stt", "tts", "command_processing"]
        }
        
        return status
        
    except Exception as e:
        logger.error(f"Error getting service status: {e}")
        raise HTTPException(status_code=500, detail="Failed to get service status")

# Backend capabilities endpoint
@app.get("/capabilities")
async def get_backend_capabilities():
    """
    Returns information about backend capabilities and supported features.
    """
    return {
        "version": "2.0.0",
        "endpoint_version": "generic",
        "supported_endpoints": [
            "/stt/transcribe",
            "/tts/synthesize", 
            "/pipeline/process",
            "/audio/start_recording",
            "/audio/stop_recording",
            "/audio/get_data",
            "/audio/status",
            "/health",
            "/status",
            "/capabilities",
            "/shutdown"
        ],
        "pipeline_step_types": ["stt", "tts", "command_processing"],
        "supported_languages": [
            "en-US", "en-GB", "es-ES", "fr-FR", "de-DE", "it-IT", 
            "pt-BR", "ru-RU", "ja-JP", "ko-KR", "zh-CN"
        ],
        "supported_voices": [
            "default", "expressive", "calm", "dramatic", "male", "female", "neural"
        ],
        "features": {
            "pure_stt": stt_service_available,
            "pure_tts": tts_service_available,
            "server_side_recording": True,
            "pipeline_processing": True,
            "command_processing": False,  # TODO: Implement
            "multi_language": True,
            "voice_options": tts_service_available
        }
    }

# Installation help endpoint
@app.get("/install-help")
async def get_installation_help():
    """
    Provides installation help for missing dependencies.
    """
    help_info = {
        "message": "Voice Assistant Backend - Installation Help",
        "version": "2.0.0",
        "endpoint_version": "generic",
        "required_dependencies": {
            "core": ["fastapi", "uvicorn", "numpy", "scipy", "torchaudio"],
            "stt": {
                "primary": "RealtimeSTT",
                "install_command": "pip install RealtimeSTT"
            },
            "tts": {
                "primary": "chatterbox-tts",
                "install_command": "pip install git+https://github.com/resemble-ai/chatterbox.git"
            },
            "server_audio": {
                "optional": "sounddevice",
                "install_command": "pip install sounddevice",
                "note": "Required for server-side audio recording"
            }
        },
        "swarmui_installation": {
            "windows": "dlbackend\\comfy\\python_embeded\\python.exe -m pip install RealtimeSTT git+https://github.com/resemble-ai/chatterbox.git sounddevice",
            "linux_mac": "./dlbackend/ComfyUI/venv/bin/python -m pip install RealtimeSTT git+https://github.com/resemble-ai/chatterbox.git sounddevice"
        },
        "current_status": {
            "stt_available": stt_service_available,
            "tts_available": tts_service_available,
            "server_audio_available": True
        }
    }
    
    return help_info

# Graceful shutdown endpoint
@app.post("/shutdown")
async def initiate_shutdown():
    """
    Graceful shutdown endpoint.
    
    Allows SwarmUI to request a clean shutdown of the backend server.
    Triggers the shutdown event for proper resource cleanup.
    """
    try:
        logger.info("Shutdown requested via API endpoint")
        
        # Stop any active recording
        stop_server_audio_recording()
        
        # Set shutdown event to trigger graceful termination
        shutdown_event.set()
        
        # Schedule the actual shutdown to happen after we return the response
        asyncio.create_task(delayed_shutdown())
        
        return {"status": "shutdown_initiated", "message": "Server shutting down gracefully"}
        
    except Exception as e:
        logger.error(f"Error during shutdown initiation: {e}")
        raise HTTPException(status_code=500, detail="Failed to initiate shutdown")

async def delayed_shutdown():
    """
    Delayed shutdown task to allow the response to be sent before termination.
    
    Waits briefly to ensure the shutdown response is delivered, then stops the server.
    This prevents connection errors when SwarmUI calls the shutdown endpoint.
    """
    try:
        # Wait a moment to ensure the response is sent
        await asyncio.sleep(0.5)
        
        logger.info("Executing delayed shutdown")
        
        # Send SIGTERM to self to trigger graceful shutdown
        import os
        os.kill(os.getpid(), signal.SIGTERM)
        
    except Exception as e:
        logger.error(f"Error during delayed shutdown: {e}")

# Global exception handler
@app.exception_handler(Exception)
async def global_exception_handler(request: Request, exc: Exception):
    """
    Global exception handler for unhandled errors.
    
    Logs all unhandled exceptions and returns appropriate error responses.
    Ensures the server doesn't crash on unexpected errors.
    """
    logger.error(f"Unhandled exception in {request.method} {request.url}: {exc}", exc_info=True)
    
    return JSONResponse(
        status_code=500,
        content={
            "detail": "Internal server error",
            "error_type": type(exc).__name__,
            "path": str(request.url.path),
            "endpoint_version": "generic"
        }
    )

# Signal handlers for graceful shutdown
def setup_signal_handlers():
    """
    Set up signal handlers for graceful shutdown on SIGTERM and SIGINT.
    
    Ensures the server can be stopped cleanly by process managers or manual interruption.
    """
    def signal_handler(signum, frame):
        logger.info(f"Received signal {signum}, initiating graceful shutdown")
        shutdown_event.set()
    
    signal.signal(signal.SIGTERM, signal_handler)
    signal.signal(signal.SIGINT, signal_handler)

def main():
    """
    Main entry point for the voice assistant backend server.
    
    Handles command line arguments, server configuration, and startup.
    """
    import argparse
    
    parser = argparse.ArgumentParser(description="SwarmUI Voice Assistant Backend Server")
    parser.add_argument("--host", type=str, default="localhost", 
                       help="Host address to bind to (default: localhost)")
    parser.add_argument("--port", type=int, default=7831, 
                       help="Port number to listen on (default: 7831)")
    parser.add_argument("--reload", action="store_true", 
                       help="Enable auto-reload for development")
    parser.add_argument("--log-level", type=str, default="info",
                       choices=["debug", "info", "warning", "error"],
                       help="Logging level (default: info)")
    
    args = parser.parse_args()
    
    # Set logging level based on argument
    logging.getLogger().setLevel(getattr(logging, args.log_level.upper()))
    
    # Set up signal handlers
    setup_signal_handlers()
    
    logger.info(f"Starting SwarmUI Voice Assistant Backend with Generic Endpoints on {args.host}:{args.port}")
    logger.info(f"Log level: {args.log_level}")
    logger.info(f"Reload mode: {args.reload}")
    logger.info(f"Endpoint version: generic")
    logger.info(f"Server-side audio recording: Available")
    
    try:
        # Configure uvicorn server
        config = uvicorn.Config(
            app=app,
            host=args.host,
            port=args.port,
            reload=args.reload,
            log_level=args.log_level,
            access_log=True,
            use_colors=True
        )
        
        server = uvicorn.Server(config)
        
        # Run the server with shutdown monitoring
        async def run_with_shutdown():
            """Run server with shutdown event monitoring."""
            server_task = asyncio.create_task(server.serve())
            shutdown_task = asyncio.create_task(shutdown_event.wait())
            
            # Wait for either server completion or shutdown signal
            done, pending = await asyncio.wait(
                [server_task, shutdown_task],
                return_when=asyncio.FIRST_COMPLETED
            )
            
            # Cancel pending tasks
            for task in pending:
                task.cancel()
                try:
                    await task
                except asyncio.CancelledError:
                    pass
            
            # If shutdown was triggered, stop the server
            if shutdown_task in done:
                logger.info("Shutdown event triggered, stopping server")
                server.should_exit = True
                await server_task
        
        # Run the server
        asyncio.run(run_with_shutdown())
        
    except KeyboardInterrupt:
        logger.info("Server interrupted by user")
    except Exception as e:
        logger.error(f"Server error: {e}")
        sys.exit(1)
    finally:
        logger.info("Voice Assistant Backend Server stopped")

if __name__ == "__main__":
    main()
    