#!/usr/bin/env python3
"""
SwarmUI Voice Assistant Backend Server
Production-ready FastAPI server providing STT and TTS services for the SwarmUI Voice Assistant Extension.

This server handles:
- Speech-to-Text transcription via RealtimeSTT (with fallbacks)
- Text-to-Speech synthesis via Chatterbox TTS (with fallbacks)
- Health monitoring and graceful shutdown
- CORS configuration for SwarmUI integration
- Automatic dependency checking and fallback handling
"""

import asyncio
import logging
import signal
import sys
import os
from contextlib import asynccontextmanager
from typing import Optional, Dict, Any

import uvicorn
from fastapi import FastAPI, HTTPException, Request
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
    logger.info("Starting Voice Assistant Backend Server")
    
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
            logger.info("Voice Assistant Backend Server ready")
        
        yield  # Server runs here
        
    except Exception as e:
        logger.error(f"Failed to initialize services: {e}")
        # Continue anyway in limited mode
        yield
    finally:
        # Cleanup during shutdown
        logger.info("Shutting down Voice Assistant Backend Server")
        logger.info("Service cleanup completed")

# Initialize FastAPI with lifespan management
app = FastAPI(
    title="SwarmUI Voice Assistant Backend",
    description="Backend service providing STT and TTS capabilities for SwarmUI Voice Assistant Extension",
    version="1.0.0",
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

# Request/Response Models
class STTRequest(BaseModel):
    """Speech-to-Text request model."""
    audio_data: str = Field(..., description="Base64 encoded audio data")
    language: str = Field(default="en-US", description="Language code for transcription")
    
    class Config:
        schema_extra = {
            "example": {
                "audio_data": "UklGRjIAAABXQVZFZm10IBAAAAAB...",
                "language": "en-US"
            }
        }

class TTSRequest(BaseModel):
    """Text-to-Speech request model."""
    text: str = Field(..., description="Text to convert to speech")
    voice: str = Field(default="default", description="Voice model to use")
    language: Optional[str] = Field(default=None, description="Language code for synthesis")
    volume: float = Field(default=0.8, ge=0.0, le=1.0, description="Volume level (0.0 to 1.0)")
    
    class Config:
        schema_extra = {
            "example": {
                "text": "Hello, this is a test message.",
                "voice": "default",
                "language": "en-US",
                "volume": 0.8
            }
        }

class HealthResponse(BaseModel):
    """Health check response model."""
    status: str = Field(..., description="Service status")
    version: str = Field(..., description="Server version")
    services: Dict[str, bool] = Field(..., description="Individual service status")
    missing_dependencies: Optional[Dict[str, str]] = Field(None, description="Missing dependencies info")

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
            missing_deps["stt"] = "Install RealtimeSTT or speech-recognition"
        if not tts_service_available:
            missing_deps["tts"] = "Install chatterbox-tts or gTTS"
        
        response = HealthResponse(
            status=overall_status,
            version="1.0.0",
            services={
                "stt": stt_ready,
                "tts": tts_ready
            },
            missing_dependencies=missing_deps if missing_deps else None
        )
        
        logger.debug(f"Health check: {overall_status} (STT: {stt_ready}, TTS: {tts_ready})")
        return response
        
    except Exception as e:
        logger.error(f"Health check failed: {e}")
        raise HTTPException(status_code=500, detail="Health check failed")

# STT endpoint
@app.post("/stt/transcribe")
async def transcribe_audio(request: STTRequest):
    """
    Speech-to-Text transcription endpoint.
    
    Processes base64 encoded audio data and returns transcribed text.
    Supports multiple languages and provides confidence scores.
    """
    try:
        logger.info(f"Processing STT request for language: {request.language}")
        
        if not stt_service_available or not stt_service:
            raise HTTPException(
                status_code=503, 
                detail="STT service not available. Please install RealtimeSTT or speech-recognition."
            )
        
        if not stt_service.initialized:
            logger.error("STT service not initialized")
            raise HTTPException(status_code=503, detail="STT service not initialized")
        
        # Validate audio data
        if not request.audio_data:
            raise HTTPException(status_code=400, detail="No audio data provided")
        
        # Process transcription
        result = await stt_service.transcribe(request.audio_data, request.language)
        
        logger.info(f"STT completed successfully: '{result.get('transcription', '')[:50]}...'")
        return result
        
    except HTTPException:
        raise
    except Exception as e:
        logger.error(f"STT transcription failed: {e}")
        raise HTTPException(status_code=500, detail=f"Transcription failed: {str(e)}")

# TTS endpoint
@app.post("/tts/synthesize")
async def synthesize_speech(request: TTSRequest):
    """
    Text-to-Speech synthesis endpoint.
    
    Converts text to speech audio and returns base64 encoded audio data.
    Supports multiple voices, languages, and volume control.
    """
    try:
        logger.info(f"Processing TTS request: '{request.text[:50]}...' with voice: {request.voice}")
        
        if not tts_service_available or not tts_service:
            raise HTTPException(
                status_code=503, 
                detail="TTS service not available. Please install chatterbox-tts or gTTS."
            )
        
        if not tts_service.initialized:
            logger.error("TTS service not initialized")
            raise HTTPException(status_code=503, detail="TTS service not initialized")
        
        # Validate text input
        if not request.text or not request.text.strip():
            raise HTTPException(status_code=400, detail="No text provided for synthesis")
        
        if len(request.text) > 1000:  # Reasonable limit for voice responses
            raise HTTPException(status_code=400, detail="Text too long (max 1000 characters)")
        
        # Process synthesis
        result = await tts_service.synthesize(
            text=request.text,
            voice=request.voice,
            language=request.language,
            volume=request.volume
        )
        
        logger.info("TTS synthesis completed successfully")
        return result
        
    except HTTPException:
        raise
    except Exception as e:
        logger.error(f"TTS synthesis failed: {e}")
        raise HTTPException(status_code=500, detail=f"Speech synthesis failed: {str(e)}")

# Combined processing endpoint for voice input pipeline
@app.post("/process")
async def process_voice_input(request: STTRequest):
    """
    Combined voice processing endpoint.
    
    Processes audio input through STT and returns both transcription and
    any generated responses. This is a convenience endpoint for full voice processing.
    """
    try:
        logger.info("Processing combined voice input request")
        
        if not stt_service_available:
            raise HTTPException(
                status_code=503, 
                detail="STT service not available for voice processing"
            )
        
        # First transcribe the audio
        transcription_result = await transcribe_audio(request)
        
        # Extract transcribed text
        transcribed_text = transcription_result.get("transcription", "")
        
        if not transcribed_text:
            raise HTTPException(status_code=400, detail="No transcription produced from audio")
        
        # Return the transcription (command processing is handled by SwarmUI)
        result = {
            "text": transcribed_text,
            "language": request.language,
            "confidence": transcription_result.get("confidence", 0.0)
        }
        
        logger.info(f"Combined processing completed: '{transcribed_text}'")
        return result
        
    except HTTPException:
        raise
    except Exception as e:
        logger.error(f"Combined voice processing failed: {e}")
        raise HTTPException(status_code=500, detail=f"Voice processing failed: {str(e)}")

# Service status endpoint
@app.get("/status")
async def get_service_status():
    """
    Detailed service status endpoint.
    
    Returns detailed information about available services and their capabilities.
    """
    try:
        status = {
            "server_version": "1.0.0",
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
                }
            },
            "capabilities": {
                "transcription": stt_service_available,
                "synthesis": tts_service_available,
                "languages": getattr(stt_service, 'supported_languages', []) if stt_service else [],
                "voices": getattr(tts_service, 'available_voices', []) if tts_service else []
            }
        }
        
        return status
        
    except Exception as e:
        logger.error(f"Error getting service status: {e}")
        raise HTTPException(status_code=500, detail="Failed to get service status")

# Installation help endpoint
@app.get("/install-help")
async def get_installation_help():
    """
    Provides installation help for missing dependencies.
    """
    help_info = {
        "message": "Voice Assistant Backend - Installation Help",
        "required_dependencies": {
            "core": ["fastapi", "uvicorn", "numpy", "scipy", "torchaudio"],
            "stt": {
                "primary": "RealtimeSTT",
                "alternatives": ["speech-recognition", "openai-whisper"],
                "install_command": "pip install RealtimeSTT"
            },
            "tts": {
                "primary": "chatterbox-tts",
                "alternatives": ["gTTS", "pyttsx3"],
                "install_command": "pip install chatterbox-tts"
            }
        },
        "swarmui_installation": {
            "windows": "dlbackend\\comfy\\python_embeded\\python.exe -m pip install RealtimeSTT chatterbox-tts",
            "linux_mac": "./dlbackend/ComfyUI/venv/bin/python -m pip install RealtimeSTT chatterbox-tts"
        },
        "current_status": {
            "stt_available": stt_service_available,
            "tts_available": tts_service_available
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
            "path": str(request.url.path)
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
    
    logger.info(f"Starting SwarmUI Voice Assistant Backend on {args.host}:{args.port}")
    logger.info(f"Log level: {args.log_level}")
    logger.info(f"Reload mode: {args.reload}")
    
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
