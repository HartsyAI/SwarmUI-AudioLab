# SwarmUI Voice Assistant Extension

A production-ready voice assistant extension for SwarmUI that provides real-time speech-to-text and text-to-speech capabilities, integrated directly into the Text2Image tab interface with automated dependency installation and real-time progress tracking.

## Features

- 🎙️ **Voice-Controlled Image Generation** - Generate images using natural voice commands
- 🔄 **Bidirectional Communication** - Speak commands and hear AI responses
- 📊 **Real-time Installation Progress** - Live progress tracking during dependency installation
- 🎚️ **Customizable Settings** - Adjust voice style, language, and volume preferences
- 🚀 **Production-Ready FastAPI Backend** - High-performance Python backend with comprehensive error handling
- 🎨 **Responsive Modern UI** - Clean interface with real-time status indicators
- ⚡ **Automatic Dependency Management** - Handles complex library installation automatically
- 🔒 **SwarmUI Integration** - Built specifically for SwarmUI's Python environment (no system Python fallback)

## Requirements

**STRICT REQUIREMENTS:**
- SwarmUI with ComfyUI backend properly installed and working
- Python 3.9-3.12 (via SwarmUI's embedded Python environment)
- Microphone access (for speech input)
- Audio output (for voice responses)
- Modern web browser with Web Audio API support
- Stable internet connection (for downloading large ML packages)

**IMPORTANT:** This extension ONLY works with SwarmUI's Python environment and requires ComfyUI to be properly installed. It will NOT work with system Python installations.

## Installation

1. **Clone into SwarmUI Extensions Directory:**
   ```bash
   cd /path/to/SwarmUI/src/Extensions/
   git clone https://github.com/yourusername/SwarmUI-VoiceAssistant.git
   ```

2. **Restart SwarmUI:**
   - The extension will be automatically loaded
   - Dependencies will be installed automatically when you first start the voice service

3. **First-Time Setup:**
   - Navigate to the Text2Image tab in SwarmUI
   - Find the Voice Assistant panel
   - Click "Start Service" - this will automatically install all required dependencies
   - Installation includes large packages (~200MB) and may take 10-15 minutes

## Core Dependencies

The extension automatically installs these specific libraries:

**Speech Recognition:**
- **RealtimeSTT** - Primary STT engine (REQUIRED)

**Text-to-Speech:**
- **Chatterbox TTS** - Primary TTS engine (REQUIRED)

**Core Libraries:**
- FastAPI, Uvicorn - Web server framework
- NumPy, SciPy - Numerical computing
- TorchAudio - Audio processing
- PyDantic - Data validation

**NO FALLBACK LIBRARIES** - The extension requires these specific packages and will not use alternatives.

## Usage

### Initial Setup

1. **Navigate to Voice Assistant:**
   - Open SwarmUI
   - Go to Text2Image tab
   - Find the Voice Assistant panel

2. **Check Installation Status:**
   - Click "Check Installation" to see what's installed
   - View real-time dependency status

3. **Start Voice Service:**
   - Click "Start Service" 
   - Watch real-time progress as dependencies install
   - Wait for "Service Online" status

### Voice Commands

**Supported Commands:**
- **"Generate [description]"** - Creates images based on your description
- **"Create a [description]"** - Alternative generation command
- **"Make [description]"** - Another generation variant
- **"Hello" / "Hi"** - Greeting and help
- **"Help"** - List available commands
- **"Status"** - Check system status

**Voice Settings:**
- **Language:** English (US/UK), Spanish, French, German, Italian, Portuguese, Russian, Japanese, Korean, Chinese
- **Voice Style:** Default, Expressive, Calm, Dramatic, Male, Female, Neural
- **Volume:** Adjustable from 0.1 to 1.0

### Text Alternative

- Type commands directly in the text input field
- Useful for testing or when microphone is unavailable
- Supports all voice command types

### Real-Time Features

- **Live Transcript:** See your speech converted to text in real-time
- **Command History:** Track all voice and text interactions
- **Installation Progress:** Real-time progress during dependency installation
- **Service Status:** Live health monitoring of backend services

## Technical Architecture

### Project Structure

```
src/Extensions/SwarmUI-VoiceAssistant/
├── VoiceAssistant.cs              # Main C# extension class
├── python_backend/
│   ├── voice_server.py            # FastAPI backend server
│   ├── stt_service.py             # RealtimeSTT service
│   ├── tts_service.py             # Chatterbox TTS service
│   └── requirements.txt           # Python dependencies
├── Assets/
│   ├── voice-assistant.js         # Frontend JavaScript with progress tracking
│   ├── voice-assistant.css        # Modern UI styling
│   └── Tabs/Text2Image/
│       └── VoiceAssistant.html    # UI template
└── README.md                      # This file
```

### Architecture Components

1. **C# Extension Layer:**
   - Extension lifecycle management
   - API endpoint registration
   - Python backend process management
   - Real-time progress tracking for installations

2. **Python Backend (FastAPI):**
   - Speech-to-Text processing via RealtimeSTT
   - Text-to-Speech synthesis via Chatterbox TTS
   - Health monitoring and status reporting
   - Comprehensive error handling and logging

3. **Frontend (TypeScript/JavaScript):**
   - Voice recording and playback
   - Real-time UI updates and progress tracking
   - WebRTC audio processing
   - Command history and transcript management

### API Endpoints

- `ProcessVoiceInput` - Complete voice processing pipeline
- `StartVoiceService` - Start backend with dependency installation
- `StopVoiceService` - Graceful service shutdown
- `GetVoiceStatus` - Service health monitoring
- `ProcessTextCommand` - Text-based command processing
- `CheckInstallationStatus` - Dependency status checking
- `GetInstallationProgress` - Real-time installation progress

## Installation Process

### Automatic Dependency Installation

The extension handles complex dependency installation automatically:

1. **Environment Detection:**
   - Locates SwarmUI's Python environment
   - Validates Python version compatibility
   - Checks for ComfyUI backend

2. **Dependency Installation:**
   - Downloads and installs RealtimeSTT (~50MB)
   - Downloads and installs Chatterbox TTS (~150MB)
   - Installs supporting libraries (TorchAudio, etc.)
   - Real-time progress tracking with package-level detail

3. **Verification:**
   - Tests all installed packages
   - Validates service functionality
   - Reports detailed status

### Progress Tracking Features

- **Overall Progress:** 0-100% completion
- **Current Step:** "Installing core packages", "Installing STT library", etc.
- **Package Detail:** Real-time pip output and download progress
- **Status Messages:** Detailed information about current operations
- **Error Handling:** Clear error messages and recovery suggestions

## Troubleshooting

### Common Issues

**"SwarmUI Python environment not found":**
- Ensure SwarmUI is properly installed with ComfyUI backend
- Verify ComfyUI is working in SwarmUI before using Voice Assistant
- Check that SwarmUI's Python environment exists

**"Failed to install RealtimeSTT":**
- Requires Python 3.9-3.12 (not 3.13)
- Check internet connection for downloading large packages
- Ensure sufficient disk space (~500MB for all dependencies)

**"Microphone Access Denied":**
- Use HTTPS or localhost (required for microphone access)
- Check browser permissions for microphone
- Verify system audio settings

**"Service Not Responding":**
- Check SwarmUI logs for backend errors
- Try stopping and restarting the service
- Verify all dependencies installed correctly

### Installation Issues

**Python Version Problems:**
- Extension requires Python 3.9-3.12
- SwarmUI's embedded Python must be in this range
- Python 3.13 is not supported by required ML libraries

**Large Download Timeouts:**
- TorchAudio and ML models are large (~200MB total)
- Installation may take 10-15 minutes on first run
- Progress tracking shows real-time status

**Dependency Conflicts:**
- Extension only works with SwarmUI's Python environment
- System Python installations are not supported
- Requires clean SwarmUI installation with working ComfyUI

### Getting Help

1. **Check Installation Status:** Use the "Check Installation" button for detailed diagnostics
2. **Monitor Logs:** Check SwarmUI console output for detailed error messages
3. **Progress Tracking:** Watch real-time installation progress for stuck operations
4. **Service Status:** Use health check features to diagnose service issues

## Development

### Building and Testing

1. **Development Setup:**
   - Ensure SwarmUI development environment is ready
   - Install with ComfyUI backend for testing
   - Use development mode for faster iteration

2. **Testing Voice Features:**
   - Test in quiet environment for best STT accuracy
   - Use different microphones and audio setups
   - Test with various voice commands and languages

3. **Backend Development:**
   - Python backend runs on port 7831 by default
   - Use `--reload` flag for development
   - Monitor logs for debugging

### Code Quality

- **Production Ready:** Comprehensive error handling and logging
- **Type Safety:** Full type annotations in Python backend
- **Resource Management:** Proper cleanup and memory management
- **Progress Tracking:** Real-time feedback for all operations
- **Modern UI:** Responsive design with real-time updates

## License

MIT License - see [LICENSE](LICENSE) for details.

## Contributing

Contributions welcome! Please follow these steps:
1. Ensure you have a working SwarmUI + ComfyUI installation
2. Test voice functionality thoroughly
3. Follow existing code patterns and error handling
4. Update progress tracking for any new installation steps
5. Submit pull request with clear description of changes

## Acknowledgments

- **RealtimeSTT** - Primary speech recognition engine
- **Chatterbox TTS** - High-quality text-to-speech synthesis
- **SwarmUI** - Base platform and integration framework
- **FastAPI** - Backend web framework