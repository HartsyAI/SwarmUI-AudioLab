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
SwarmUI Voice Assistant - Audio Processing Interface

Supports two modes:
  1. Legacy commands (init, stt, tts, status, cleanup) — backward compatible
  2. Generic "process" command — routes through engine_registry for any provider
     Usage: voice_processor.py process <module> <class> <args_b64>
"""

import json
import sys
import os
import traceback
import time
import base64
import contextlib
import tempfile
from io import StringIO
from typing import Dict, Any, Optional

# Add current directory to sys.path to ensure local modules can be imported
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

# Context manager to capture stdout temporarily and redirect to stderr
@contextlib.contextmanager
def redirect_stdout_to_stderr():
    """Redirect stdout to stderr temporarily."""
    old_stdout = sys.stdout
    try:
        sys.stdout = sys.stderr
        yield
    finally:
        sys.stdout = old_stdout

# ---------------------------------------------------------------------------
# Generic provider-based processing (new system)
# ---------------------------------------------------------------------------

def process_generic(module_name: str, class_name: str, args_b64: str) -> Dict[str, Any]:
    """Route a request through engine_registry to any provider engine.

    Args:
        module_name: Python module in engines/ (e.g. "tts_chatterbox")
        class_name: Engine class within the module (e.g. "ChatterboxEngine")
        args_b64: Base64-encoded JSON dict of kwargs for engine.process()

    Returns a dict with at least "success" key.
    """
    start_time = time.time()
    try:
        import engine_registry

        args_json = base64.b64decode(args_b64).decode("utf-8")
        kwargs = json.loads(args_json)

        log_debug(f"process_generic: module={module_name}, class={class_name}, args keys={list(kwargs.keys())}")

        with redirect_stdout_to_stderr():
            engine = engine_registry.get_engine(module_name, class_name)
            result = engine.process(**kwargs)

        result["processing_time"] = time.time() - start_time
        result["engine_module"] = module_name
        result["engine_class"] = class_name
        return result

    except Exception as e:
        return {
            "success": False,
            "error": str(e),
            "processing_time": time.time() - start_time,
            "traceback": traceback.format_exc(),
        }

# ---------------------------------------------------------------------------
# Legacy engine management (backward compatible)
# ---------------------------------------------------------------------------

# Lazy imports for legacy mode — only loaded when legacy commands are used
_legacy_loaded = False
_stt_engine = None
_tts_engine = None
_initialized = False
_voice_options = None
_state_file = os.path.join(tempfile.gettempdir(), "swarmui_voice_state.json")

def _ensure_legacy_imports():
    """Import legacy engine modules on demand."""
    global _legacy_loaded
    if _legacy_loaded:
        return
    # These will be removed in a future version when all callers migrate to "process"
    global get_stt_engine, get_available_stt_engines, get_tts_engine, get_available_tts_engines
    try:
        from stt_engines import get_stt_engine, get_available_stt_engines
        from tts_engines import get_tts_engine, get_available_tts_engines
    except ImportError:
        # Legacy engine files may have been deleted after migration
        def get_stt_engine(name): raise RuntimeError("Legacy stt_engines module removed. Use 'process' command.")
        def get_available_stt_engines(): return []
        def get_tts_engine(name): raise RuntimeError("Legacy tts_engines module removed. Use 'process' command.")
        def get_available_tts_engines(): return []
    _legacy_loaded = True

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
    """Load state metadata from file without reinitializing engines."""
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
    """Load a specific legacy engine by type and name."""
    _ensure_legacy_imports()
    if engine_type == "stt":
        try:
            engine = get_stt_engine(engine_name)
            log_debug(f"STT engine {engine_name} initialized successfully")
            return engine
        except Exception as err:
            log_debug(f"Failed to initialize STT engine {engine_name}: {err}")
    elif engine_type == "tts":
        try:
            engine = get_tts_engine(engine_name)
            log_debug(f"TTS engine {engine_name} initialized successfully")
            return engine
        except Exception as err:
            log_debug(f"Failed to initialize TTS engine {engine_name}: {err}")
    return None

def _load_state(load_engines=True):
    """Load state from file and optionally reinitialize engines."""
    global _initialized, _stt_engine, _tts_engine

    try:
        state = _load_state_metadata()
        if not state:
            return {}
        if not load_engines:
            return state

        stt_engine_name = state.get("stt_engine_name")
        tts_engine_name = state.get("tts_engine_name")

        if stt_engine_name and not _stt_engine:
            log_debug(f"Reinitializing STT engine: {stt_engine_name}")
            _stt_engine = _load_engine("stt", stt_engine_name)

        if tts_engine_name and not _tts_engine:
            log_debug(f"Reinitializing TTS engine: {tts_engine_name}")
            _tts_engine = _load_engine("tts", tts_engine_name)

        return state

    except Exception as e:
        log_debug(f"Failed to load state: {e}")
        return {}

def initialize_voice_services(config_json: str = None, is_base64: bool = False) -> Dict[str, Any]:
    """Initialize voice services with available engines (legacy)."""
    global _stt_engine, _tts_engine, _initialized
    _ensure_legacy_imports()

    try:
        log_debug("Starting initialize_voice_services")
        start_time = time.time()

        if is_base64 and config_json:
            config_json = base64.b64decode(config_json).decode('utf-8')

        config = json.loads(config_json) if config_json else {}

        stt_engines = get_available_stt_engines()
        tts_engines = get_available_tts_engines()

        with redirect_stdout_to_stderr():
            stt_engine_name = config.get("stt_engine") or (stt_engines[0] if stt_engines else None)
            if stt_engine_name:
                _stt_engine = get_stt_engine(stt_engine_name)

            tts_engine_name = config.get("tts_engine") or (tts_engines[0] if tts_engines else None)
            if tts_engine_name:
                _tts_engine = get_tts_engine(tts_engine_name)

        _initialized = True
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
        return {"success": False, "error": str(e), "traceback": traceback.format_exc()}

def process_stt(audio_data: str, language: str = "en-US", options: str = None) -> Dict[str, Any]:
    """Process speech-to-text (legacy)."""
    start_time = time.time()
    global _stt_engine, _initialized

    try:
        if not _stt_engine or not _initialized:
            _load_state(load_engines=True)
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
        return {"success": False, "error": str(e), "processing_time": time.time() - start_time, "traceback": traceback.format_exc()}

def process_tts(text: str, voice: str = "default", language: str = "en-US",
                volume: float = 0.8, options: str = None) -> Dict[str, Any]:
    """Process text-to-speech (legacy)."""
    start_time = time.time()
    global _tts_engine, _initialized

    try:
        if not _tts_engine or not _initialized:
            _load_state(load_engines=True)
        if not _tts_engine:
            return {"success": False, "error": "TTS engine not initialized"}

        opts = json.loads(options) if options else {}
        result = _tts_engine.synthesize(text, voice, language, volume, **opts)

        return {
            "success": True,
            "audio_data": result["audio_data"],
            "text": text, "voice": voice, "language": language, "volume": volume,
            "duration": result.get("duration", 0.0),
            "processing_time": time.time() - start_time,
            "engine": _tts_engine.name,
            "metadata": result.get("metadata", {})
        }
    except Exception as e:
        return {"success": False, "error": str(e), "processing_time": time.time() - start_time, "traceback": traceback.format_exc()}

def get_voice_status() -> Dict[str, Any]:
    """Get status of voice services (legacy)."""
    _ensure_legacy_imports()
    if _initialized and (_stt_engine is not None or _tts_engine is not None):
        return {
            "success": True, "initialized": _initialized,
            "stt_available": _stt_engine is not None,
            "tts_available": _tts_engine is not None,
            "stt_engine": _stt_engine.name if _stt_engine else None,
            "tts_engine": _tts_engine.name if _tts_engine else None,
            "stt_engines": get_available_stt_engines(),
            "tts_engines": get_available_tts_engines()
        }

    state = _load_state(load_engines=False)
    if state:
        return {
            "success": True,
            "initialized": state.get("initialized", False),
            "stt_available": state.get("has_stt", False),
            "tts_available": state.get("has_tts", False),
            "stt_engine": state.get("stt_engine_name"),
            "tts_engine": state.get("tts_engine_name"),
            "stt_engines": get_available_stt_engines(),
            "tts_engines": get_available_tts_engines()
        }

    return {"success": False, "initialized": False, "error": "Voice services not initialized", "stt_available": False, "tts_available": False}

def cleanup_voice_services() -> Dict[str, Any]:
    """Cleanup resources (legacy + registry)."""
    global _stt_engine, _tts_engine, _initialized

    try:
        if _stt_engine and hasattr(_stt_engine, 'cleanup'):
            _stt_engine.cleanup()
        if _tts_engine and hasattr(_tts_engine, 'cleanup'):
            _tts_engine.cleanup()
        _stt_engine = None
        _tts_engine = None
        _initialized = False

        # Also clean up registry engines
        try:
            import engine_registry
            engine_registry.cleanup_all()
        except ImportError:
            pass

        return {"success": True, "message": "Cleanup completed"}
    except Exception as e:
        return {"success": False, "error": str(e)}

# ---------------------------------------------------------------------------
# CLI entry point
# ---------------------------------------------------------------------------

def main():
    """Command line interface."""
    log_debug(f"main() started with args: {sys.argv}")

    if len(sys.argv) < 2:
        print(json.dumps({"success": False, "error": "No command provided"}), flush=True)
        sys.exit(1)

    command = sys.argv[1]
    log_debug(f"Command: {command}")

    try:
        # ---- Generic provider command (new system) ----
        if command == "process":
            if len(sys.argv) < 5:
                result = {"success": False, "error": "Usage: process <module> <class> <args_b64>"}
            else:
                module_name = sys.argv[2]
                class_name = sys.argv[3]
                args_b64 = sys.argv[4]
                result = process_generic(module_name, class_name, args_b64)

        # ---- Legacy commands (backward compatible) ----
        elif command == "init":
            is_base64 = False
            config = None
            if len(sys.argv) > 2:
                if sys.argv[2] == "-b" and len(sys.argv) > 3:
                    is_base64 = True
                    config = sys.argv[3]
                else:
                    config = sys.argv[2]
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
                with redirect_stdout_to_stderr():
                    result = process_tts(text, voice, language, volume, options)

        elif command == "status":
            result = get_voice_status()

        elif command == "cleanup":
            result = cleanup_voice_services()

        else:
            result = {"success": False, "error": f"Unknown command: {command}"}

        print(json.dumps(result, indent=2), flush=True)
        sys.exit(0)

    except Exception as e:
        error_result = {"success": False, "error": str(e), "traceback": traceback.format_exc()}
        log_debug(f"Error occurred: {str(e)}\n{traceback.format_exc()}")
        print(json.dumps(error_result, indent=2), flush=True)
        sys.exit(1)

if __name__ == "__main__":
    main()
