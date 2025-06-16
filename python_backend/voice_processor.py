#!/usr/bin/env python3
"""
SwarmUI Voice Assistant - Simple Direct Function Interface
Just the essentials - no overengineering.
"""

import json
import sys
import traceback
import time
from typing import Dict, Any, Optional

# Import our simple engine modules
from stt_engines import get_stt_engine, get_available_stt_engines
from tts_engines import get_tts_engine, get_available_tts_engines

# Global state (simple and effective)
_stt_engine = None
_tts_engine = None
_initialized = False

def initialize_voice_services(config_json: str = None) -> Dict[str, Any]:
    """Initialize voice services with available engines."""
    global _stt_engine, _tts_engine, _initialized
    
    try:
        config = json.loads(config_json) if config_json else {}
        
        # Get available engines
        stt_engines = get_available_stt_engines()
        tts_engines = get_available_tts_engines()
        
        # Initialize STT
        stt_engine_name = config.get("stt_engine") or (stt_engines[0] if stt_engines else None)
        if stt_engine_name:
            _stt_engine = get_stt_engine(stt_engine_name)
        
        # Initialize TTS  
        tts_engine_name = config.get("tts_engine") or (tts_engines[0] if tts_engines else None)
        if tts_engine_name:
            _tts_engine = get_tts_engine(tts_engine_name)
        
        _initialized = True
        
        return {
            "success": True,
            "stt_engines": stt_engines,
            "tts_engines": tts_engines,
            "stt_current": stt_engine_name,
            "tts_current": tts_engine_name,
            "message": "Voice services initialized"
        }
        
    except Exception as e:
        return {
            "success": False,
            "error": str(e),
            "traceback": traceback.format_exc()
        }

def process_stt(audio_data: str, language: str = "en-US", options: str = None) -> Dict[str, Any]:
    """Process speech-to-text."""
    start_time = time.time()
    
    try:
        if not _stt_engine:
            return {"success": False, "error": "STT engine not initialized"}
        
        opts = json.loads(options) if options else {}
        result = _stt_engine.transcribe(audio_data, language, **opts)
        
        return {
            "success": True,
            "transcription": result["text"],
            "confidence": result.get("confidence", 0.0),
            "language": language,
            "processing_time": time.time() - start_time,
            "engine": _stt_engine.name,
            "alternatives": result.get("alternatives", []),
            "metadata": result.get("metadata", {})
        }
        
    except Exception as e:
        return {
            "success": False,
            "error": str(e),
            "processing_time": time.time() - start_time,
            "traceback": traceback.format_exc()
        }

def process_tts(text: str, voice: str = "default", language: str = "en-US", 
                volume: float = 0.8, options: str = None) -> Dict[str, Any]:
    """Process text-to-speech."""
    start_time = time.time()
    
    try:
        if not _tts_engine:
            return {"success": False, "error": "TTS engine not initialized"}
        
        opts = json.loads(options) if options else {}
        result = _tts_engine.synthesize(text, voice, language, volume, **opts)
        
        return {
            "success": True,
            "audio_data": result["audio_data"],
            "text": text,
            "voice": voice,
            "language": language,
            "volume": volume,
            "duration": result.get("duration", 0.0),
            "processing_time": time.time() - start_time,
            "engine": _tts_engine.name,
            "metadata": result.get("metadata", {})
        }
        
    except Exception as e:
        return {
            "success": False,
            "error": str(e),
            "processing_time": time.time() - start_time,
            "traceback": traceback.format_exc()
        }

def get_voice_status() -> Dict[str, Any]:
    """Get status of voice services."""
    return {
        "success": True,
        "initialized": _initialized,
        "stt_available": _stt_engine is not None,
        "tts_available": _tts_engine is not None,
        "stt_engine": _stt_engine.name if _stt_engine else None,
        "tts_engine": _tts_engine.name if _tts_engine else None,
        "stt_engines": get_available_stt_engines(),
        "tts_engines": get_available_tts_engines()
    }

def cleanup_voice_services() -> Dict[str, Any]:
    """Cleanup resources."""
    global _stt_engine, _tts_engine, _initialized
    
    try:
        if _stt_engine and hasattr(_stt_engine, 'cleanup'):
            _stt_engine.cleanup()
        if _tts_engine and hasattr(_tts_engine, 'cleanup'):
            _tts_engine.cleanup()
            
        _stt_engine = None
        _tts_engine = None
        _initialized = False
        
        return {"success": True, "message": "Cleanup completed"}
    except Exception as e:
        return {"success": False, "error": str(e)}

# Command line interface
def main():
    """Simple command line interface."""
    if len(sys.argv) < 2:
        print(json.dumps({"success": False, "error": "No command provided"}))
        sys.exit(1)
    
    command = sys.argv[1]
    
    try:
        if command == "init":
            config = sys.argv[2] if len(sys.argv) > 2 else None
            result = initialize_voice_services(config)
            
        elif command == "stt":
            if len(sys.argv) < 3:
                result = {"success": False, "error": "Audio data required"}
            else:
                audio_data = sys.argv[2]
                language = sys.argv[3] if len(sys.argv) > 3 else "en-US"
                options = sys.argv[4] if len(sys.argv) > 4 else None
                result = process_stt(audio_data, language, options)
                
        elif command == "tts":
            if len(sys.argv) < 3:
                result = {"success": False, "error": "Text required"}
            else:
                text = sys.argv[2]
                voice = sys.argv[3] if len(sys.argv) > 3 else "default"
                language = sys.argv[4] if len(sys.argv) > 4 else "en-US"
                volume = float(sys.argv[5]) if len(sys.argv) > 5 else 0.8
                options = sys.argv[6] if len(sys.argv) > 6 else None
                result = process_tts(text, voice, language, volume, options)
                
        elif command == "status":
            result = get_voice_status()
            
        elif command == "cleanup":
            result = cleanup_voice_services()
            
        else:
            result = {"success": False, "error": f"Unknown command: {command}"}
        
        print(json.dumps(result, indent=2))
        
    except Exception as e:
        print(json.dumps({
            "success": False,
            "error": str(e),
            "traceback": traceback.format_exc()
        }, indent=2))
        sys.exit(1)

if __name__ == "__main__":
    main()
