#!/usr/bin/env python3

# Force unbuffered output for all print statements
import os
import sys
import signal
import threading
import atexit
os.environ['PYTHONUNBUFFERED'] = '1'

# Define logging function to write to stderr
def log_debug(message):
    print(f"[DEBUG] {message}", file=sys.stderr, flush=True)

log_debug("Python voice processor starting up...")
sys.stderr.flush()

# Register process exit handler to forcefully kill all threads on exit
def force_exit_handler():
    log_debug("Forcing exit with os._exit(0) to terminate all threads")
    os._exit(0)

# Register the exit handler
atexit.register(force_exit_handler)

"""
SwarmUI Voice Assistant - Simple Direct Function Interface
Just the essentials - no overengineering.
"""

import json
import sys
import os
import traceback
import time
import contextlib
import tempfile
from io import StringIO
from typing import Dict, Any, Optional

# Add current directory to sys.path to ensure local modules can be imported
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

# Import our simple engine modules
from stt_engines import get_stt_engine, get_available_stt_engines
from tts_engines import get_tts_engine, get_available_tts_engines

# Context manager to capture stdout temporarily and redirect to stderr
@contextlib.contextmanager
def redirect_stdout_to_stderr():
    """Redirect stdout to stderr temporarily."""
    # Keep a reference to the actual streams
    old_stdout = sys.stdout
    
    try:
        # Redirect stdout to stderr
        sys.stdout = sys.stderr
        yield
    finally:
        # Restore original stdout
        sys.stdout = old_stdout

# Global state (simple and effective)
_stt_engine = None
_tts_engine = None
_initialized = False
_voice_options = None

# File to persist state between command calls
_state_file = os.path.join(tempfile.gettempdir(), "swarmui_voice_state.json")

def _save_state():
    """Save the current state to a file."""
    state = {
        "initialized": _initialized,
        "stt_engine_name": _stt_engine.name if _stt_engine else None,
        "tts_engine_name": _tts_engine.name if _tts_engine else None,
        "has_stt": _stt_engine is not None,
        "has_tts": _tts_engine is not None
    }
    try:
        with open(_state_file, 'w') as f:
            json.dump(state, f)
    except Exception as e:
        log_debug(f"Failed to save state: {e}")

def _load_state_metadata():
    """Load state metadata from file without reinitializing engines.
    This is a lightweight operation suitable for status checks."""
    global _initialized
    try:
        if os.path.exists(_state_file):
            log_debug(f"Loading state metadata from {_state_file}")
            with open(_state_file, 'r') as f:
                state = json.load(f)
                _initialized = state.get("initialized", False)
                log_debug(f"State metadata loaded. Initialized: {_initialized}, " 
                          f"STT: {state.get('stt_engine_name')}, TTS: {state.get('tts_engine_name')}")
                return state
    except Exception as e:
        log_debug(f"Failed to load state metadata: {e}")
    return {}

def _load_engine(engine_type, engine_name):
    """Load a specific engine by type and name."""
    if engine_type == "stt":
        try:
            from stt_engines import get_stt_engine
            engine = get_stt_engine(engine_name)
            log_debug(f"STT engine {engine_name} initialized successfully")
            return engine
        except Exception as err:
            log_debug(f"Failed to initialize STT engine {engine_name}: {err}")
    elif engine_type == "tts":
        try:
            from tts_engines import get_tts_engine
            engine = get_tts_engine(engine_name)
            log_debug(f"TTS engine {engine_name} initialized successfully")
            return engine
        except Exception as err:
            log_debug(f"Failed to initialize TTS engine {engine_name}: {err}")
    return None
    
def _load_state(load_engines=True):
    """Load state from file and optionally reinitialize engines.
    
    Args:
        load_engines: If True, will attempt to load the actual engine objects.
                     If False, will only load metadata (faster, for status checks).
    """
    global _initialized, _stt_engine, _tts_engine
    
    try:
        state = _load_state_metadata()
        if not state:
            return {}
            
        # If we don't need to load engines, just return metadata
        if not load_engines:
            return state
            
        # Reinitialize engines if needed
        stt_engine_name = state.get("stt_engine_name")
        tts_engine_name = state.get("tts_engine_name")
        
        # Reinitialize STT engine if needed
        if stt_engine_name and not _stt_engine:
            log_debug(f"Reinitializing STT engine: {stt_engine_name}")
            _stt_engine = _load_engine("stt", stt_engine_name)
        
        # Reinitialize TTS engine if needed
        if tts_engine_name and not _tts_engine:
            log_debug(f"Reinitializing TTS engine: {tts_engine_name}")
            _tts_engine = _load_engine("tts", tts_engine_name)
            
        return state
                
    except Exception as e:
        log_debug(f"Failed to load state: {e}")
        return {}

def initialize_voice_services(config_json: str = None, is_base64: bool = False) -> Dict[str, Any]:
    """Initialize voice services with available engines."""
    global _stt_engine, _tts_engine, _initialized
    
    try:
        log_debug("Starting initialize_voice_services")
        start_time = time.time()
        
        # Decode Base64 string if specified
        if is_base64 and config_json:
            log_debug("Decoding Base64 string")
            import base64
            config_json = base64.b64decode(config_json).decode('utf-8')
            log_debug(f"Base64 decoded, length: {len(config_json)}")
        
        log_debug("Parsing JSON config")
        config = json.loads(config_json) if config_json else {}
        log_debug(f"JSON parsed successfully: {config}")
        
        # Get available engines
        log_debug("Getting available STT engines")
        stt_engines = get_available_stt_engines()
        log_debug(f"Found STT engines: {stt_engines} in {time.time() - start_time:.2f}s")
        
        log_debug("Getting available TTS engines")
        tts_engines = get_available_tts_engines()
        log_debug(f"Found TTS engines: {tts_engines} in {time.time() - start_time:.2f}s")
        
        # Use our context manager to redirect ALL stdout to stderr during initialization
        # This captures output from third-party libraries that we can't modify
        with redirect_stdout_to_stderr():
            # Initialize STT
            log_debug("Initializing STT engine")
            stt_engine_name = config.get("stt_engine") or (stt_engines[0] if stt_engines else None)
            if stt_engine_name:
                log_debug(f"Using STT engine: {stt_engine_name}")
                _stt_engine = get_stt_engine(stt_engine_name)
                log_debug(f"STT engine initialized in {time.time() - start_time:.2f}s")
            else:
                log_debug("No STT engine selected")
            
            # Initialize TTS  
            log_debug("Initializing TTS engine")
            tts_engine_name = config.get("tts_engine") or (tts_engines[0] if tts_engines else None)
            if tts_engine_name:
                log_debug(f"Using TTS engine: {tts_engine_name}")
                _tts_engine = get_tts_engine(tts_engine_name)
                log_debug(f"TTS engine initialized in {time.time() - start_time:.2f}s")
            else:
                log_debug("No TTS engine selected")
        
        _initialized = True
        
        # Save state to file
        _save_state()
        
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
    global _stt_engine, _initialized
    
    try:
        # Try to load state from file if not initialized - use full engine loading
        if not _stt_engine or not _initialized:
            log_debug("STT engine not initialized in memory, trying to load from state file with engines")
            _load_state(load_engines=True)
            log_debug(f"State loaded with engines. STT engine available: {_stt_engine is not None}")
            
        # Still no STT engine after loading state?
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
    global _tts_engine, _initialized
    
    try:
        # Try to load state from file if not initialized - use full engine loading
        if not _tts_engine or not _initialized:
            log_debug("TTS engine not initialized in memory, trying to load from state file with engines")
            _load_state(load_engines=True)
            log_debug(f"State loaded with engines. TTS engine available: {_tts_engine is not None}")
            
        # Still no TTS engine after loading state?
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
    # First try to get state from globals
    if _initialized and (_stt_engine is not None or _tts_engine is not None):
        log_debug("Getting voice status from active session")
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
    
    # If not initialized in this process, try to get from saved state
    # Use lightweight state loading (load_engines=False) to avoid timeout during status checks
    log_debug("Getting voice status from saved state (lightweight mode)")
    state = _load_state(load_engines=False)
    if state:
        stt_engines = get_available_stt_engines()
        tts_engines = get_available_tts_engines()
        return {
            "success": True,
            "initialized": state.get("initialized", False),
            "stt_available": state.get("has_stt", False),
            "tts_available": state.get("has_tts", False),
            "stt_engine": state.get("stt_engine_name"),
            "tts_engine": state.get("tts_engine_name"),
            "stt_engines": stt_engines,
            "tts_engines": tts_engines
        }
    
    # No state found
    return {
        "success": False,
        "initialized": False,
        "error": "Voice services not initialized",
        "stt_available": False,
        "tts_available": False
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
    log_debug(f"main() started with args: {sys.argv}")
    
    if len(sys.argv) < 2:
        print(json.dumps({"success": False, "error": "No command provided"}), flush=True)
        sys.exit(1)
    
    command = sys.argv[1]
    log_debug(f"Command: {command}")
    
    try:
        if command == "init":
            # Check for the -b flag indicating Base64 encoded config
            is_base64 = False
            config = None
            
            # Parse arguments
            log_debug(f"Parsing init arguments, arg count: {len(sys.argv)}")
            if len(sys.argv) > 2:
                if sys.argv[2] == "-b" and len(sys.argv) > 3:
                    is_base64 = True
                    config = sys.argv[3]
                    log_debug(f"Found base64 flag, config length: {len(config) if config else 0}")
                else:
                    config = sys.argv[2]
                    log_debug(f"Plain config, length: {len(config) if config else 0}")
            
            log_debug("About to call initialize_voice_services")
            result = initialize_voice_services(config, is_base64)
            
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
                # Use redirect_stdout_to_stderr to capture any unexpected output during TTS synthesis
                # This ensures libraries like chatterbox don't pollute stdout with their prints
                log_debug("Redirecting stdout during TTS processing to prevent JSON pollution")
                with redirect_stdout_to_stderr():
                    result = process_tts(text, voice, language, volume, options)
                
        elif command == "status":
            result = get_voice_status()
            
        elif command == "cleanup":
            result = cleanup_voice_services()
            
        else:
            result = {"success": False, "error": f"Unknown command: {command}"}
        
        # Only send the clean JSON output to stdout for C# parsing
        print(json.dumps(result, indent=2), flush=True)
        
        # Explicitly exit with success code
        sys.exit(0)
        
    except Exception as e:
        # Send error as JSON to stdout for C# parsing
        error_result = {
            "success": False,
            "error": str(e),
            "traceback": traceback.format_exc()
        }
        log_debug(f"Error occurred: {str(e)}\n{traceback.format_exc()}")
        print(json.dumps(error_result, indent=2), flush=True)
        sys.exit(1)

if __name__ == "__main__":
    main()
