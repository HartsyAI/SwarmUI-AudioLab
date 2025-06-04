"""
Voice Assistant Backend Server
SwarmUI VoiceAssistant Extension - Main FastAPI HTTP Server

This module provides the main HTTP server and service orchestration for the voice assistant
backend. It handles STT, TTS, wake word detection, and WebSocket communication with the
C# extension layer.
"""

import asyncio
import json
import logging
import os
import signal
import sys
import traceback
from contextlib import asynccontextmanager
from pathlib import Path
from typing import Dict, List, Optional

import uvicorn
from fastapi import FastAPI, WebSocket, WebSocketDisconnect, HTTPException, Depends
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import JSONResponse
from loguru import logger
from pydantic import BaseModel, ValidationError

# Add the current directory to Python path for relative imports
sys.path.insert(0, str(Path(__file__).parent))

# Import local services and utilities
from services.stt_service import STTService
from services.tts_service import TTSService
from services.wake_word_service import WakeWordService
from services.conversation_manager import ConversationManager
from api.routes import router as api_router
from api.models import (
    HealthCheckResponse,
    STTRequest,
    STTResponse,
    TTSRequest,
    TTSResponse,
    WakeWordRequest,
    WakeWordResponse,
    ErrorResponse
)
from utils.config import VoiceConfig
from utils.audio_utils import AudioProcessor


class VoiceAssistantServer:
    """Main voice assistant server class that orchestrates all services"""
    
    def __init__(self):
        self.config = VoiceConfig()
        self.stt_service: Optional[STTService] = None
        self.tts_service: Optional[TTSService] = None
        self.wake_word_service: Optional[WakeWordService] = None
        self.conversation_manager: Optional[ConversationManager] = None
        self.audio_processor: Optional[AudioProcessor] = None
        self.websocket_connections: Dict[str, WebSocket] = {}
        self.is_shutting_down = False
        
        # Configure logging
        self._setup_logging()
        
    def _setup_logging(self):
        """Configure logging for the voice assistant server"""
        # Remove default loguru handler
        logger.remove()
        
        # Add custom handler with formatting
        logger.add(
            sys.stderr,
            format="<green>{time:YYYY-MM-DD HH:mm:ss}</green> | "
                   "<level>{level: <8}</level> | "
                   "<cyan>VoiceAssistant</cyan> | "
                   "<level>{message}</level>",
            level=self.config.log_level.upper(),
            colorize=True
        )
        
        # Add file logging if specified
        if self.config.log_file:
            logger.add(
                self.config.log_file,
                format="{time:YYYY-MM-DD HH:mm:ss} | {level: <8} | VoiceAssistant | {message}",
                level=self.config.log_level.upper(),
                rotation="10 MB",
                retention="7 days"
            )
            
        logger.info("Voice Assistant Server logging configured")
    
    async def initialize_services(self) -> bool:
        """Initialize all voice services"""
        try:
            logger.info("Initializing voice assistant services...")
            
            # Initialize audio processor first
            self.audio_processor = AudioProcessor(self.config.audio)
            if not await self.audio_processor.initialize():
                logger.error("Failed to initialize audio processor")
                return False
            
            # Initialize STT service
            self.stt_service = STTService(self.config.stt)
            if not await self.stt_service.initialize():
                logger.error("Failed to initialize STT service")
                return False
            
            # Initialize TTS service
            self.tts_service = TTSService(self.config.tts)
            if not await self.tts_service.initialize():
                logger.error("Failed to initialize TTS service")
                return False
            
            # Initialize wake word service if enabled
            if self.config.wake_word.enabled:
                self.wake_word_service = WakeWordService(self.config.wake_word)
                if not await self.wake_word_service.initialize():
                    logger.warning("Failed to initialize wake word service, continuing without it")
                    self.wake_word_service = None
            
            # Initialize conversation manager
            self.conversation_manager = ConversationManager()
            
            logger.info("All voice assistant services initialized successfully")
            return True
            
        except Exception as e:
            logger.error(f"Failed to initialize services: {e}")
            logger.error(traceback.format_exc())
            return False
    
    async def cleanup_services(self):
        """Cleanup all services"""
        try:
            logger.info("Cleaning up voice assistant services...")
            self.is_shutting_down = True
            
            # Close all WebSocket connections
            for session_id, websocket in list(self.websocket_connections.items()):
                try:
                    await websocket.close()
                except Exception as e:
                    logger.warning(f"Error closing WebSocket {session_id}: {e}")
            self.websocket_connections.clear()
            
            # Cleanup services
            if self.stt_service:
                await self.stt_service.cleanup()
            
            if self.tts_service:
                await self.tts_service.cleanup()
            
            if self.wake_word_service:
                await self.wake_word_service.cleanup()
            
            if self.audio_processor:
                await self.audio_processor.cleanup()
            
            logger.info("Voice assistant services cleaned up")
            
        except Exception as e:
            logger.error(f"Error during cleanup: {e}")


# Global server instance
voice_server = VoiceAssistantServer()


@asynccontextmanager
async def lifespan(app: FastAPI):
    """FastAPI lifespan context manager for startup and shutdown"""
    # Startup
    logger.info("Starting Voice Assistant Backend Server")
    
    if not await voice_server.initialize_services():
        logger.error("Failed to initialize services, shutting down")
        sys.exit(1)
    
    logger.info("Voice Assistant Backend Server started successfully")
    yield
    
    # Shutdown
    logger.info("Shutting down Voice Assistant Backend Server")
    await voice_server.cleanup_services()
    logger.info("Voice Assistant Backend Server shutdown complete")


# Create FastAPI application
app = FastAPI(
    title="SwarmUI Voice Assistant Backend",
    description="Backend services for SwarmUI Voice Assistant Extension",
    version="1.0.0",
    lifespan=lifespan
)

# Configure CORS
app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],  # Configure appropriately for production
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)

# Include API routes
app.include_router(api_router, prefix="/api")


# Health check endpoint
@app.get("/health", response_model=HealthCheckResponse)
async def health_check():
    """Health check endpoint"""
    try:
        services_status = {
            "stt_service": voice_server.stt_service is not None,
            "tts_service": voice_server.tts_service is not None,
            "wake_word_service": voice_server.wake_word_service is not None,
            "conversation_manager": voice_server.conversation_manager is not None,
            "audio_processor": voice_server.audio_processor is not None
        }
        
        all_healthy = all(services_status.values())
        
        return HealthCheckResponse(
            status="healthy" if all_healthy else "degraded",
            services=services_status,
            timestamp=asyncio.get_event_loop().time(),
            uptime=asyncio.get_event_loop().time() - voice_server.config.start_time
        )
        
    except Exception as e:
        logger.error(f"Health check failed: {e}")
        raise HTTPException(status_code=500, detail="Health check failed")


# Speech-to-Text endpoints
@app.post("/api/stt/transcribe", response_model=STTResponse)
async def transcribe_audio(request: STTRequest):
    """Transcribe audio data to text"""
    try:
        if not voice_server.stt_service:
            raise HTTPException(status_code=503, detail="STT service not available")
        
        result = await voice_server.stt_service.transcribe_audio(
            audio_data=request.audio_data,
            language=request.language,
            confidence_threshold=request.confidence_threshold
        )
        
        return STTResponse(
            transcription=result.get("transcription", ""),
            confidence=result.get("confidence", 0.0),
            language=result.get("language", request.language),
            processing_time=result.get("processing_time", 0.0),
            is_final=True
        )
        
    except Exception as e:
        logger.error(f"STT transcription failed: {e}")
        raise HTTPException(status_code=500, detail=f"Transcription failed: {str(e)}")


@app.post("/api/stt/realtime", response_model=STTResponse)
async def realtime_transcribe(request: STTRequest):
    """Real-time audio transcription"""
    try:
        if not voice_server.stt_service:
            raise HTTPException(status_code=503, detail="STT service not available")
        
        result = await voice_server.stt_service.process_realtime_audio(
            audio_data=request.audio_data,
            session_id=request.session_id or "default",
            language=request.language
        )
        
        return STTResponse(
            transcription=result.get("transcription", ""),
            confidence=result.get("confidence", 0.0),
            language=result.get("language", request.language),
            processing_time=result.get("processing_time", 0.0),
            is_final=result.get("is_final", False)
        )
        
    except Exception as e:
        logger.error(f"Real-time STT failed: {e}")
        raise HTTPException(status_code=500, detail=f"Real-time transcription failed: {str(e)}")


# Text-to-Speech endpoints
@app.post("/api/tts/synthesize", response_model=TTSResponse)
async def synthesize_speech(request: TTSRequest):
    """Synthesize speech from text"""
    try:
        if not voice_server.tts_service:
            raise HTTPException(status_code=503, detail="TTS service not available")
        
        result = await voice_server.tts_service.synthesize_speech(
            text=request.text,
            voice=request.voice,
            speed=request.speed,
            volume=request.volume,
            language=request.language
        )
        
        return TTSResponse(
            audio_data=result.get("audio_data", ""),
            duration=result.get("duration", 0.0),
            sample_rate=result.get("sample_rate", 22050),
            channels=result.get("channels", 1),
            voice_used=result.get("voice", request.voice)
        )
        
    except Exception as e:
        logger.error(f"TTS synthesis failed: {e}")
        raise HTTPException(status_code=500, detail=f"Speech synthesis failed: {str(e)}")


@app.get("/api/tts/voices")
async def list_voices():
    """List available TTS voices"""
    try:
        if not voice_server.tts_service:
            raise HTTPException(status_code=503, detail="TTS service not available")
        
        voices = voice_server.tts_service.list_available_voices()
        return {"voices": voices}
        
    except Exception as e:
        logger.error(f"Failed to list voices: {e}")
        raise HTTPException(status_code=500, detail=f"Failed to list voices: {str(e)}")


# Wake word detection endpoints
@app.post("/api/wake_word/detect", response_model=WakeWordResponse)
async def detect_wake_word(request: WakeWordRequest):
    """Detect wake words in audio"""
    try:
        if not voice_server.wake_word_service:
            raise HTTPException(status_code=503, detail="Wake word service not available")
        
        result = await voice_server.wake_word_service.detect_wake_word(
            audio_data=request.audio_data,
            wake_words=request.wake_words,
            sensitivity=request.sensitivity
        )
        
        return WakeWordResponse(
            wake_word_detected=result.get("detected", False),
            wake_word=result.get("wake_word", ""),
            confidence=result.get("confidence", 0.0),
            timestamp=result.get("timestamp", 0.0)
        )
        
    except Exception as e:
        logger.error(f"Wake word detection failed: {e}")
        raise HTTPException(status_code=500, detail=f"Wake word detection failed: {str(e)}")


# WebSocket endpoint for real-time communication
@app.websocket("/ws")
async def websocket_endpoint(websocket: WebSocket):
    """Main WebSocket endpoint for real-time voice processing"""
    session_id = None
    try:
        await websocket.accept()
        
        # Get session ID from query parameters or generate one
        session_id = websocket.query_params.get("session_id", f"ws_{id(websocket)}")
        voice_server.websocket_connections[session_id] = websocket
        
        logger.info(f"WebSocket connection established: {session_id}")
        
        # Send welcome message
        await websocket.send_json({
            "type": "connection_established",
            "session_id": session_id,
            "services_available": {
                "stt": voice_server.stt_service is not None,
                "tts": voice_server.tts_service is not None,
                "wake_word": voice_server.wake_word_service is not None,
            }
        })
        
        # Message processing loop
        while not voice_server.is_shutting_down:
            try:
                data = await websocket.receive()
                
                if data["type"] == "websocket.receive":
                    if "text" in data:
                        # Handle JSON messages
                        message = json.loads(data["text"])
                        await handle_websocket_message(websocket, session_id, message)
                    elif "bytes" in data:
                        # Handle binary audio data
                        await handle_websocket_audio(websocket, session_id, data["bytes"])
                        
            except WebSocketDisconnect:
                logger.info(f"WebSocket disconnected: {session_id}")
                break
            except json.JSONDecodeError as e:
                logger.error(f"Invalid JSON received from {session_id}: {e}")
                await websocket.send_json({
                    "type": "error",
                    "error": "Invalid JSON format",
                    "session_id": session_id
                })
            except Exception as e:
                logger.error(f"WebSocket message processing error for {session_id}: {e}")
                await websocket.send_json({
                    "type": "error", 
                    "error": str(e),
                    "session_id": session_id
                })
                
    except Exception as e:
        logger.error(f"WebSocket connection error: {e}")
    finally:
        if session_id and session_id in voice_server.websocket_connections:
            del voice_server.websocket_connections[session_id]
        logger.info(f"WebSocket connection closed: {session_id}")


async def handle_websocket_message(websocket: WebSocket, session_id: str, message: dict):
    """Handle incoming WebSocket JSON messages"""
    try:
        command = message.get("command")
        
        if command == "start_stt":
            await handle_start_stt(websocket, session_id, message)
        elif command == "stop_stt":
            await handle_stop_stt(websocket, session_id, message)
        elif command == "start_wake_word":
            await handle_start_wake_word(websocket, session_id, message)
        elif command == "stop_wake_word":
            await handle_stop_wake_word(websocket, session_id, message)
        elif command == "tts_speak":
            await handle_tts_speak(websocket, session_id, message)
        elif command == "ping":
            await websocket.send_json({
                "type": "pong",
                "session_id": session_id,
                "timestamp": asyncio.get_event_loop().time()
            })
        else:
            await websocket.send_json({
                "type": "error",
                "error": f"Unknown command: {command}",
                "session_id": session_id
            })
            
    except Exception as e:
        logger.error(f"Error handling WebSocket message: {e}")
        await websocket.send_json({
            "type": "error",
            "error": str(e),
            "session_id": session_id
        })


async def handle_websocket_audio(websocket: WebSocket, session_id: str, audio_data: bytes):
    """Handle incoming WebSocket binary audio data"""
    try:
        # Process audio through real-time STT if active
        if voice_server.stt_service:
            result = await voice_server.stt_service.process_realtime_audio_binary(
                audio_data, session_id
            )
            
            if result and result.get("transcription"):
                await websocket.send_json({
                    "type": "stt_result",
                    "session_id": session_id,
                    "transcription": result["transcription"],
                    "confidence": result.get("confidence", 0.0),
                    "is_final": result.get("is_final", False)
                })
                
    except Exception as e:
        logger.error(f"Error handling WebSocket audio: {e}")
        await websocket.send_json({
            "type": "error",
            "error": f"Audio processing error: {str(e)}",
            "session_id": session_id
        })


async def handle_start_stt(websocket: WebSocket, session_id: str, message: dict):
    """Handle start STT command"""
    if not voice_server.stt_service:
        await websocket.send_json({
            "type": "error",
            "error": "STT service not available",
            "session_id": session_id
        })
        return
    
    language = message.get("language", "en-US")
    success = await voice_server.stt_service.start_listening(session_id, language)
    
    await websocket.send_json({
        "type": "stt_started" if success else "stt_start_failed",
        "session_id": session_id,
        "language": language
    })


async def handle_stop_stt(websocket: WebSocket, session_id: str, message: dict):
    """Handle stop STT command"""
    if not voice_server.stt_service:
        await websocket.send_json({
            "type": "error",
            "error": "STT service not available",
            "session_id": session_id
        })
        return
    
    result = await voice_server.stt_service.stop_listening(session_id)
    
    await websocket.send_json({
        "type": "stt_stopped",
        "session_id": session_id,
        "final_transcription": result.get("transcription", ""),
        "confidence": result.get("confidence", 0.0)
    })


async def handle_start_wake_word(websocket: WebSocket, session_id: str, message: dict):
    """Handle start wake word detection command"""
    if not voice_server.wake_word_service:
        await websocket.send_json({
            "type": "error",
            "error": "Wake word service not available",
            "session_id": session_id
        })
        return
    
    wake_words = message.get("wake_words", ["hey swarm"])
    sensitivity = message.get("sensitivity", 0.5)
    
    success = await voice_server.wake_word_service.start_detection(wake_words, sensitivity)
    
    await websocket.send_json({
        "type": "wake_word_started" if success else "wake_word_start_failed",
        "session_id": session_id,
        "wake_words": wake_words,
        "sensitivity": sensitivity
    })


async def handle_stop_wake_word(websocket: WebSocket, session_id: str, message: dict):
    """Handle stop wake word detection command"""
    if not voice_server.wake_word_service:
        await websocket.send_json({
            "type": "error",
            "error": "Wake word service not available",
            "session_id": session_id
        })
        return
    
    success = await voice_server.wake_word_service.stop_detection()
    
    await websocket.send_json({
        "type": "wake_word_stopped",
        "session_id": session_id,
        "stopped": success
    })


async def handle_tts_speak(websocket: WebSocket, session_id: str, message: dict):
    """Handle TTS speak command"""
    if not voice_server.tts_service:
        await websocket.send_json({
            "type": "error",
            "error": "TTS service not available",
            "session_id": session_id
        })
        return
    
    text = message.get("text", "")
    if not text:
        await websocket.send_json({
            "type": "error",
            "error": "Text is required for TTS",
            "session_id": session_id
        })
        return
    
    result = await voice_server.tts_service.synthesize_speech(
        text=text,
        voice=message.get("voice"),
        speed=message.get("speed", 1.0),
        volume=message.get("volume", 0.8),
        language=message.get("language", "en-US")
    )
    
    await websocket.send_json({
        "type": "tts_result",
        "session_id": session_id,
        "audio_data": result.get("audio_data", ""),
        "duration": result.get("duration", 0.0),
        "text": text
    })


# Shutdown endpoint
@app.post("/api/shutdown")
async def shutdown_server():
    """Shutdown the voice assistant server"""
    try:
        logger.info("Shutdown requested via API")
        asyncio.create_task(delayed_shutdown())
        return {"status": "shutdown_initiated"}
    except Exception as e:
        logger.error(f"Shutdown request failed: {e}")
        raise HTTPException(status_code=500, detail="Shutdown failed")


async def delayed_shutdown():
    """Delayed shutdown to allow response to be sent"""
    await asyncio.sleep(1)
    os.kill(os.getpid(), signal.SIGTERM)


# Global exception handler
@app.exception_handler(Exception)
async def global_exception_handler(request, exc):
    """Global exception handler for unhandled errors"""
    logger.error(f"Unhandled exception: {exc}")
    logger.error(traceback.format_exc())
    
    return JSONResponse(
        status_code=500,
        content={
            "error": "Internal server error",
            "detail": str(exc) if voice_server.config.debug else "An unexpected error occurred"
        }
    )


def main():
    """Main entry point for the voice assistant server"""
    import argparse
    
    parser = argparse.ArgumentParser(description="SwarmUI Voice Assistant Backend Server")
    parser.add_argument("--host", default="localhost", help="Host to bind to")
    parser.add_argument("--port", type=int, default=7830, help="Port to bind to")
    parser.add_argument("--log-level", default="INFO", help="Log level")
    parser.add_argument("--reload", action="store_true", help="Enable auto-reload")
    parser.add_argument("--workers", type=int, default=1, help="Number of worker processes")
    
    args = parser.parse_args()
    
    # Update config with command line arguments
    voice_server.config.host = args.host
    voice_server.config.port = args.port
    voice_server.config.log_level = args.log_level
    voice_server.config.start_time = asyncio.get_event_loop().time()
    
    logger.info(f"Starting Voice Assistant Backend Server on {args.host}:{args.port}")
    
    # Run the server
    uvicorn.run(
        "voice_server:app",
        host=args.host,
        port=args.port,
        log_level=args.log_level.lower(),
        reload=args.reload,
        workers=args.workers if not args.reload else 1,
        access_log=True
    )


if __name__ == "__main__":
    main()
