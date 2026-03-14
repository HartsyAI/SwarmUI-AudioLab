#!/usr/bin/env python3
"""Persistent audio engine server for AudioLab.

Launched by SwarmUI via NetworkBackendUtils.DoSelfStart().
Keeps models loaded in GPU memory between requests.
Reuses engine_registry.py and all engines/*.py unchanged.

Uses Python's built-in http.server — no external dependencies needed.

Usage: python audio_server.py --port {PORT} [--model-root PATH] [--hf-cache PATH]
"""

import argparse
import json
import os
import signal
import sys
import threading
import time
import traceback
from http.server import HTTPServer, BaseHTTPRequestHandler
from socketserver import ThreadingMixIn

# Add current directory to path for engine imports
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import engine_registry


# ── Per-request cancellation registry ────────────────────────────────────────
# Maps request_id → threading.Event.  Set by /cancel endpoint, checked by
# engines via BaseAudioEngine.is_cancelled().

_cancel_events: dict[str, threading.Event] = {}
_cancel_lock = threading.Lock()


def register_cancel_event(request_id: str) -> threading.Event:
    """Create and register a cancellation Event for a request."""
    event = threading.Event()
    with _cancel_lock:
        _cancel_events[request_id] = event
    return event


def trigger_cancel(request_id: str) -> bool:
    """Set the cancellation event for a request.  Returns True if found."""
    with _cancel_lock:
        event = _cancel_events.get(request_id)
    if event:
        event.set()
        return True
    return False


def remove_cancel_event(request_id: str):
    """Remove a cancellation event after request completes."""
    with _cancel_lock:
        _cancel_events.pop(request_id, None)


class AudioRequestHandler(BaseHTTPRequestHandler):
    """HTTP request handler for audio engine processing."""

    def do_GET(self):
        """Handle GET requests (health check)."""
        if self.path == "/health":
            self._send_json({
                "status": "ok",
                "loaded_engines": engine_registry.list_loaded_engines(),
                "uptime": time.time() - self.server.start_time
            })
        else:
            self._send_json({"error": "Not found"}, 404)

    def do_POST(self):
        """Handle POST requests (process, cancel, download, unload, shutdown)."""
        if self.path == "/process":
            body = json.loads(self._read_body())
            result = self._process_request(body)
            self._send_json(result)
        elif self.path.startswith("/cancel/"):
            request_id = self.path.split("/cancel/", 1)[1]
            found = trigger_cancel(request_id)
            self._send_json({"success": found, "request_id": request_id})
        elif self.path == "/download":
            body = json.loads(self._read_body())
            result = self._download_model(body)
            self._send_json(result)
        elif self.path == "/unload":
            body = json.loads(self._read_body())
            module = body.get("module", "")
            engine_class = body.get("engine_class", "")
            engine_registry.remove_engine(module, engine_class)
            self._send_json({"success": True, "message": f"Unloaded {module}:{engine_class}"})
        elif self.path == "/shutdown":
            engine_registry.cleanup_all()
            self._send_json({"success": True, "message": "Shutting down"})
            threading.Timer(0.5, lambda: os.kill(os.getpid(), signal.SIGTERM)).start()
        else:
            self._send_json({"error": "Not found"}, 404)

    def _process_request(self, body):
        """Route a process request through engine_registry."""
        start = time.time()
        module = body.get("module", "")
        engine_class = body.get("engine_class", "")
        kwargs = body.get("kwargs", {})
        request_id = body.get("request_id", "")

        # Update HF_TOKEN per-request so token changes take effect without restart
        hf_token = body.get("hf_token", "")
        if hf_token:
            os.environ["HF_TOKEN"] = hf_token

        # Register cancellation event if request has an ID
        cancel_event = register_cancel_event(request_id) if request_id else None

        try:
            # Redirect stdout to stderr during processing to keep stdout clean
            old_stdout = sys.stdout
            sys.stdout = sys.stderr
            engine = None
            try:
                engine = engine_registry.get_engine(module, engine_class)
                # Inject cancel event so engine.is_cancelled() works
                engine._cancel_event = cancel_event
                result = engine.process(**kwargs)
            finally:
                sys.stdout = old_stdout
                if engine is not None:
                    engine._cancel_event = None

            # If cancelled during processing, override the result
            if cancel_event and cancel_event.is_set():
                return {
                    "success": False,
                    "error": "cancelled",
                    "cancelled": True,
                    "processing_time": time.time() - start,
                }

            result["processing_time"] = time.time() - start
            result["engine_module"] = module
            result["engine_class"] = engine_class
            return result

        except Exception as e:
            return {
                "success": False,
                "error": str(e),
                "traceback": traceback.format_exc(),
                "processing_time": time.time() - start,
            }
        finally:
            if request_id:
                remove_cancel_event(request_id)

    def _download_model(self, body):
        """Download a HuggingFace model to local storage during install."""
        model_name = body.get("model_name", "")
        category = body.get("category", "")
        hf_token = body.get("hf_token", "")

        if not model_name:
            return {"success": False, "error": "model_name is required"}

        if hf_token:
            os.environ["HF_TOKEN"] = hf_token

        try:
            from engines.base_engine import BaseAudioEngine

            # Redirect stdout to stderr to keep stdout clean for health checks
            old_stdout = sys.stdout
            sys.stdout = sys.stderr
            try:
                local_path = BaseAudioEngine.ensure_model_local(model_name, category)
            finally:
                sys.stdout = old_stdout

            return {"success": True, "local_path": local_path}
        except Exception as e:
            return {
                "success": False,
                "error": str(e),
                "traceback": traceback.format_exc(),
            }

    def _read_body(self):
        """Read the request body."""
        length = int(self.headers.get("Content-Length", 0))
        return self.rfile.read(length).decode("utf-8")

    def _send_json(self, data, code=200):
        """Send a JSON response."""
        body = json.dumps(data).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, fmt, *args):
        """Route HTTP logs to stderr so SwarmUI's ReportLogsFromProcess catches them."""
        print(f"[AudioServer] {fmt % args}", file=sys.stderr, flush=True)


class ThreadingHTTPServer(ThreadingMixIn, HTTPServer):
    """HTTPServer that handles each request in a new thread.

    Required so /cancel requests can arrive while /process is running.
    """
    daemon_threads = True


def main():
    parser = argparse.ArgumentParser(description="AudioLab persistent engine server")
    parser.add_argument("--port", type=int, required=True, help="Port to listen on")
    parser.add_argument("--model-root", type=str, default="", help="Centralized model storage path")
    parser.add_argument("--hf-cache", type=str, default="", help="HuggingFace cache directory")
    args = parser.parse_args()

    # Set model cache environment variables BEFORE any engine imports
    if args.hf_cache:
        os.environ["HF_HOME"] = args.hf_cache
        os.environ["TRANSFORMERS_CACHE"] = args.hf_cache
        os.environ["HUGGINGFACE_HUB_CACHE"] = args.hf_cache
        # Bark uses XDG_CACHE_HOME instead of HF_HOME for model storage
        os.environ["XDG_CACHE_HOME"] = args.hf_cache
        print(f"[AudioServer] HuggingFace cache: {args.hf_cache}", file=sys.stderr, flush=True)

    if args.model_root:
        os.environ["AUDIOLAB_MODEL_ROOT"] = args.model_root
        print(f"[AudioServer] Model root: {args.model_root}", file=sys.stderr, flush=True)

    # Handle SIGTERM for graceful shutdown
    def shutdown_handler(signum, frame):
        print("[AudioServer] Received shutdown signal, cleaning up...", file=sys.stderr, flush=True)
        engine_registry.cleanup_all()
        sys.exit(0)

    signal.signal(signal.SIGTERM, shutdown_handler)
    if hasattr(signal, "SIGINT"):
        signal.signal(signal.SIGINT, shutdown_handler)

    # Start server
    server = ThreadingHTTPServer(("127.0.0.1", args.port), AudioRequestHandler)
    server.start_time = time.time()

    print(f"[AudioServer] Listening on 127.0.0.1:{args.port}", file=sys.stderr, flush=True)
    print(f"[AudioServer] Ready", flush=True)  # stdout signal for DoSelfStart health check

    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        engine_registry.cleanup_all()
        server.server_close()
        print("[AudioServer] Server stopped", file=sys.stderr, flush=True)


if __name__ == "__main__":
    main()
