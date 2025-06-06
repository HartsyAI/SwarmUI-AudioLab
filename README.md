# SwarmUI Voice Assistant Extension

A production-ready voice assistant extension for SwarmUI that provides seamless speech-to-text and text-to-speech capabilities, integrated directly into the Text2Image tab interface.

## Features

- 🎙️ **Voice-Controlled Image Generation** - Generate images using natural voice commands
- 🔄 **Bidirectional Communication** - Speak commands and hear responses
- 🎚️ **Customizable Settings** - Adjust voice, language, and volume preferences
- 🚀 **FastAPI Backend** - High-performance Python backend for speech processing
- 🎨 **Responsive UI** - Clean interface that matches SwarmUI's design language
- ⚡ **Real-time Feedback** - Visual indicators for listening and processing states

## Requirements

- SwarmUI with extension support
- Python 3.8+ with required dependencies
- Microphone access (for speech input)
- Audio output (for voice responses)
- Modern web browser with Web Audio API support

## Installation

1. Clone this repository into your SwarmUI extensions directory:
   ```bash
   git clone https://github.com/yourusername/SwarmUI-VoiceAssistant.git /path/to/SwarmUI/src/Extensions/
   ```

2. Install Python dependencies:
   ```bash
   cd /path/to/SwarmUI/src/Extensions/SwarmUI-VoiceAssistant/python_backend
   pip install -r requirements.txt
   ```

3. The extension will be automatically loaded when you restart SwarmUI

## Configuration

Access the Voice Assistant settings through the SwarmUI interface under the Text2Image tab. Available options include:

- **Enable/Disable** - Toggle the voice assistant functionality
- **Voice Language** - Set the language for speech recognition and synthesis
- **TTS Voice** - Choose from available text-to-speech voices
- **Voice Volume** - Adjust the volume of voice responses (0.1 to 1.0)

## Usage

### Accessing the Voice Assistant

1. Navigate to the Text2Image tab in SwarmUI
2. The Voice Assistant panel will be available alongside other generation options

### Voice Controls

1. **Start/Stop Recording**:
   - Click the microphone button to start recording
   - The button will turn red when actively listening
   - Click again to stop recording

2. **Voice Commands**:
   - "Generate an image of [description]" - Creates an image based on your description
   - "Change voice to [voice name]" - Switches between different TTS voices
   - "Set language to [language]" - Changes the recognition and synthesis language
   - "Help" - Lists available voice commands

3. **Visual Feedback**:
   - **Status Indicator**: Shows current state (Ready, Listening, Processing)
   - **Transcript**: Displays recognized speech in real-time
   - **Command History**: Keeps track of previous interactions

## Technical Details

### Architecture

The extension consists of several key components:

1. **Frontend (TypeScript/JavaScript)**
   - Voice recording and playback
   - UI components and state management
   - Communication with the backend API

2. **Backend (C#)**
   - Extension lifecycle management
   - API endpoint registration
   - Integration with SwarmUI core

3. **Python Service**
   - Speech-to-Text processing
   - Text-to-Speech synthesis
   - Command interpretation

### Project Structure

```
src/Extensions/SwarmUI-VoiceAssistant/
├── VoiceAssistant.cs           # Main extension class
├── VoiceAssistant.Backend.cs    # Backend process management
├── WebAPI/
│   └── VoiceAssistantAPI.cs    # API endpoint definitions
├── python_backend/
│   ├── voice_server.py         # FastAPI backend server
│   ├── stt_service.py          # Speech-to-Text service
│   ├── tts_service.py          # Text-to-Speech service
│   └── requirements.txt        # Python dependencies
├── Assets/
│   ├── voice-assistant.js      # Frontend JavaScript
│   ├── voice-assistant.css     # Frontend styles
│   └── Tabs/
│       └── Text2Image/
│           └── VoiceAssistant.html  # UI template
└── README.md                   # This file
```

## Troubleshooting

### Common Issues

- **Microphone Access**:
  - Ensure your browser has permission to access the microphone
  - Check system audio settings if the microphone isn't detected

- **Python Backend**:
  - Verify Python 3.8+ is installed and in PATH
  - Check SwarmUI logs for backend initialization errors
  - Ensure all Python dependencies are installed

- **Speech Recognition**:
  - Speak clearly and at a moderate pace
  - Reduce background noise
  - Check that your selected language matches your speech

## Development

### Building

1. Make your changes to the source files
2. Test the extension in development mode
3. Rebuild and restart SwarmUI to see changes

### Debugging

- Check the browser's developer console for frontend errors
- Monitor SwarmUI logs for backend and Python process output
- Use the browser's network tab to inspect API requests

## License

MIT License - see [LICENSE](LICENSE) for details.

## Contributing

Contributions are welcome! Please follow these steps:
1. Open an issue to discuss your proposed changes
2. Fork the repository and create a feature branch
3. Submit a pull request with a clear description of your changes
