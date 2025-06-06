from fastapi import FastAPI, HTTPException, Request
from fastapi.middleware.cors import CORSMiddleware
from pydantic import BaseModel
import uvicorn
import logging
import asyncio
from typing import Optional, Dict, Any
import os
import sys

# Configure logging
logging.basicConfig(
    level=logging.INFO,
    format='%(asctime)s - %(name)s - %(levelname)s - %(message)s'
)
logger = logging.getLogger("VoiceAssistant")

# Initialize FastAPI
app = FastAPI(
    title="Voice Assistant Backend",
    description="Backend service for SwarmUI Voice Assistant Extension",
    version="1.0.0"
)

# CORS middleware
app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)

# Models
class STTRequest(BaseModel):
    audio_data: str
    language: str = "en-US"

class TTSRequest(BaseModel):
    text: str
    voice: str = "default"
    language: Optional[str] = None
    volume: float = 0.8

# Health check endpoint
@app.get("/health")
async def health_check():
    return {"status": "healthy"}

# Shutdown endpoint for graceful shutdown
@app.post("/shutdown")
async def shutdown():
    logger.info("Shutdown requested via API")
    # This will be handled by the shutdown handler below
    return {"status": "shutting_down"}

# STT Endpoint
@app.post("/stt/transcribe")
async def transcribe(request: STTRequest):
    try:
        logger.info(f"Transcribing audio with language: {request.language}")
        # Simulate processing time
        await asyncio.sleep(0.5)
        
        # In a real implementation, this would call the actual STT service
        transcription = "This is a placeholder transcription"
        
        return {
            "transcription": transcription,
            "language": request.language,
            "confidence": 0.9
        }
    except Exception as e:
        logger.error(f"STT error: {str(e)}")
        raise HTTPException(status_code=500, detail=f"STT failed: {str(e)}")

# TTS Endpoint
@app.post("/tts/synthesize")
async def synthesize(request: TTSRequest):
    try:
        logger.info(f"Synthesizing speech: {request.text[:50]}...")
        # Simulate processing time
        await asyncio.sleep(0.5)
        
        # In a real implementation, this would call the actual TTS service
        audio_data = "base64_encoded_audio_placeholder"
        
        return {
            "audio_data": audio_data,
            "text": request.text,
            "voice": request.voice,
            "duration": 2.0
        }
    except Exception as e:
        logger.error(f"TTS error: {str(e)}")
        raise HTTPException(status_code=500, detail=f"TTS failed: {str(e)}")

# Error handlers
@app.exception_handler(Exception)
async def global_exception_handler(request: Request, exc: Exception):
    logger.error(f"Unhandled exception: {str(exc)}", exc_info=True)
    return JSONResponse(
        status_code=500,
        content={"detail": "Internal server error"},
    )

# Graceful shutdown handler
@app.on_event("shutdown")
def shutdown_event():
    logger.info("Shutting down voice server")
    # Cleanup code here if needed

if __name__ == "__main__":
    import argparse
    
    parser = argparse.ArgumentParser(description="Voice Assistant Backend")
    parser.add_argument("--host", type=str, default="localhost", help="Host to bind to")
    parser.add_argument("--port", type=int, default=7831, help="Port to listen on")
    parser.add_argument("--reload", action="store_true", help="Enable auto-reload")
    
    args = parser.parse_args()
    
    logger.info(f"Starting voice server on {args.host}:{args.port}")
    
    uvicorn.run(
        "voice_server:app",
        host=args.host,
        port=args.port,
        reload=args.reload,
        log_level="info"
    )
