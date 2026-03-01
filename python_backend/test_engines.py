#!/usr/bin/env python3
"""Test script for all audio engines.

Usage:
    python test_engines.py                  # Test all engines (import only)
    python test_engines.py --full           # Test all engines (import + inference)
    python test_engines.py --engine kokoro  # Test a specific engine
    python test_engines.py --engine csm --full  # Full test of a specific engine
"""

import argparse
import base64
import json
import os
import struct
import sys
import time
import traceback

# Add the engines directory to the path
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

# Engine registry: module_name -> (class_name, category, test_kwargs)
ENGINES = {
    "tts_kokoro": ("KokoroEngine", "tts", {"text": "Hello, this is a test of the Kokoro engine.", "volume": 0.8}),
    "tts_piper": ("PiperEngine", "tts", {"text": "Hello, this is a test of the Piper engine.", "volume": 0.8}),
    "tts_orpheus": ("OrpheusEngine", "tts", {"text": "Hello, this is a test of the Orpheus engine.", "volume": 0.8}),
    "tts_f5": ("F5TTSEngine", "tts", {"text": "Hello, this is a test of the F5 TTS engine.", "volume": 0.8}),
    "tts_zonos": ("ZonosEngine", "tts", {"text": "Hello, this is a test of the Zonos engine.", "volume": 0.8}),
    "tts_dia": ("DiaEngine", "tts", {"text": "[S1] Hello, this is a test of the Dia engine.", "volume": 0.8}),
    "tts_vibevoice": ("VibeVoiceEngine", "tts", {"text": "Hello, this is a test of VibeVoice.", "volume": 0.8}),
    "tts_csm": ("CSMEngine", "tts", {"text": "Hello, this is a test of the CSM engine.", "volume": 0.8}),
    "tts_cosyvoice": ("CosyVoiceEngine", "tts", {"text": "Hello, this is a test of CosyVoice.", "volume": 0.8}),
    "tts_neutts": ("NeuTTSEngine", "tts", {"text": "Hello, this is a test of NeuTTS.", "volume": 0.8}),
    "tts_chatterbox": ("ChatterboxEngine", "tts", {"text": "Hello, this is a test of Chatterbox.", "volume": 0.8}),
    "tts_bark": ("BarkEngine", "tts", {"text": "Hello, this is a test of Bark.", "volume": 0.8}),
    "music_musicgen": ("MusicGenEngine", "music", {"prompt": "upbeat electronic music", "duration": 5}),
    "music_acestep": ("AceStepEngine", "music", {"prompt": "calm piano music", "duration": 5}),
    "sfx_audiogen": ("AudioGenEngine", "sfx", {"prompt": "thunder and rain storm", "duration": 5}),
    "clone_openvoice": ("OpenVoiceEngine", "clone", None),  # Needs audio input
    "clone_rvc": ("RVCEngine", "clone", None),  # Needs audio input
    "clone_gptsovits": ("GPTSoVITSEngine", "clone", None),  # Needs audio input
    "fx_demucs": ("DemucsEngine", "fx", None),  # Needs audio input
    "fx_resemble_enhance": ("ResembleEnhanceEngine", "fx", None),  # Needs audio input
    "stt_distilwhisper": ("DistilWhisperEngine", "stt", None),  # Needs audio input
    "stt_moonshine": ("MoonshineEngine", "stt", None),  # Needs audio input
    "stt_whisper": ("WhisperEngine", "stt", None),  # Needs audio input
    "stt_realtimestt": ("RealtimeSTTEngine", "stt", None),  # Needs audio input
}


def generate_test_audio_b64(duration_s=2, sample_rate=16000, freq=440):
    """Generate a simple sine wave as base64 WAV for testing STT/FX engines."""
    import numpy as np
    t = np.linspace(0, duration_s, int(sample_rate * duration_s), dtype=np.float32)
    audio = 0.5 * np.sin(2 * np.pi * freq * t)
    # Convert to WAV bytes
    audio_int16 = (audio * 32767).astype(np.int16)
    import io
    buf = io.BytesIO()
    # WAV header
    data_size = len(audio_int16) * 2
    buf.write(b"RIFF")
    buf.write(struct.pack("<I", 36 + data_size))
    buf.write(b"WAVE")
    buf.write(b"fmt ")
    buf.write(struct.pack("<I", 16))
    buf.write(struct.pack("<H", 1))  # PCM
    buf.write(struct.pack("<H", 1))  # mono
    buf.write(struct.pack("<I", sample_rate))
    buf.write(struct.pack("<I", sample_rate * 2))
    buf.write(struct.pack("<H", 2))
    buf.write(struct.pack("<H", 16))
    buf.write(b"data")
    buf.write(struct.pack("<I", data_size))
    buf.write(audio_int16.tobytes())
    return base64.b64encode(buf.getvalue()).decode("utf-8")


def test_engine_import(module_name, class_name):
    """Test that the engine module can be imported and class instantiated."""
    try:
        module = __import__(f"engines.{module_name}", fromlist=[class_name])
        engine_class = getattr(module, class_name)
        engine = engine_class()
        return True, f"Import OK: {class_name}"
    except Exception as e:
        return False, f"Import FAILED: {e}"


def test_engine_init(module_name, class_name):
    """Test that the engine can initialize (check dependencies)."""
    try:
        module = __import__(f"engines.{module_name}", fromlist=[class_name])
        engine_class = getattr(module, class_name)
        engine = engine_class()
        result = engine.initialize()
        if result:
            return True, "Initialize OK"
        else:
            return False, "Initialize returned False (missing dependencies)"
    except Exception as e:
        return False, f"Initialize FAILED: {e}"


def test_engine_process(module_name, class_name, kwargs):
    """Test that the engine can process a request."""
    try:
        module = __import__(f"engines.{module_name}", fromlist=[class_name])
        engine_class = getattr(module, class_name)
        engine = engine_class()

        if not engine.initialize():
            return False, "Cannot test process: initialize() failed"

        start = time.time()
        result = engine.process(**kwargs)
        elapsed = time.time() - start

        if result.get("success"):
            # Validate output
            category = ENGINES[module_name][1]
            if category in ("tts", "music", "sfx", "clone", "fx"):
                audio_data = result.get("audio_data", "")
                if audio_data:
                    audio_bytes = base64.b64decode(audio_data)
                    duration = result.get("duration", 0)
                    return True, f"Process OK: {len(audio_bytes)} bytes, {duration:.2f}s audio, {elapsed:.1f}s elapsed"
                else:
                    return False, f"Process returned success=True but no audio_data"
            elif category == "stt":
                text = result.get("text", result.get("transcription", ""))
                return True, f"Process OK: transcribed '{text[:50]}', {elapsed:.1f}s elapsed"
            else:
                return True, f"Process OK: {elapsed:.1f}s elapsed"
        else:
            error = result.get("error", "Unknown error")
            return False, f"Process FAILED: {error}"
    except Exception as e:
        return False, f"Process EXCEPTION: {e}\n{traceback.format_exc()}"


def run_tests(engine_filter=None, full_test=False):
    """Run tests on all engines or a specific engine."""
    results = {}
    engines_to_test = ENGINES

    if engine_filter:
        # Match by module name or partial name
        filtered = {}
        for mod, info in ENGINES.items():
            if engine_filter in mod or engine_filter in info[0].lower():
                filtered[mod] = info
        if not filtered:
            print(f"No engine matching '{engine_filter}' found")
            return
        engines_to_test = filtered

    print(f"\n{'='*60}")
    print(f"  Audio Engine Test Suite")
    print(f"  Testing {len(engines_to_test)} engines")
    print(f"  Mode: {'FULL (import + init + process)' if full_test else 'IMPORT + INIT'}")
    print(f"{'='*60}\n")

    for module_name, (class_name, category, test_kwargs) in engines_to_test.items():
        print(f"\n--- {module_name} ({class_name}) [{category}] ---")

        # Test import
        ok, msg = test_engine_import(module_name, class_name)
        print(f"  Import: {'PASS' if ok else 'FAIL'} - {msg}")

        if not ok:
            results[module_name] = "IMPORT_FAIL"
            continue

        # Test init
        ok, msg = test_engine_init(module_name, class_name)
        print(f"  Init:   {'PASS' if ok else 'FAIL'} - {msg}")

        if not ok:
            results[module_name] = "INIT_FAIL"
            continue

        results[module_name] = "INIT_OK"

        # Full test - actually run inference
        if full_test and test_kwargs is not None:
            # For STT/FX engines that need audio input, generate test audio
            if category in ("stt", "fx") and test_kwargs is None:
                test_kwargs = {"audio_data": generate_test_audio_b64()}

            print(f"  Process: Running inference...")
            ok, msg = test_engine_process(module_name, class_name, test_kwargs)
            print(f"  Process: {'PASS' if ok else 'FAIL'} - {msg}")
            results[module_name] = "PROCESS_OK" if ok else "PROCESS_FAIL"
        elif full_test and test_kwargs is None:
            print(f"  Process: SKIPPED (needs audio input)")
            results[module_name] = "INIT_OK (process needs audio)"

    # Summary
    print(f"\n{'='*60}")
    print(f"  SUMMARY")
    print(f"{'='*60}")

    pass_count = sum(1 for v in results.values() if "OK" in v)
    fail_count = sum(1 for v in results.values() if "FAIL" in v)

    for mod, status in sorted(results.items()):
        icon = "OK" if "OK" in status else "FAIL"
        print(f"  [{icon:4s}] {mod}: {status}")

    print(f"\n  Total: {pass_count} passed, {fail_count} failed, {len(results)} tested")
    print(f"{'='*60}\n")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Test audio engines")
    parser.add_argument("--engine", "-e", help="Test a specific engine (partial name match)")
    parser.add_argument("--full", "-f", action="store_true", help="Run full inference test")
    args = parser.parse_args()

    run_tests(engine_filter=args.engine, full_test=args.full)
