"""
Configuration Management - Python Configuration
SwarmUI VoiceAssistant Extension - Configuration Utilities

This module handles configuration loading, validation, and management
for the Python backend services of the voice assistant.
"""

import json
import os
import time
from dataclasses import dataclass, field, asdict
from pathlib import Path
from typing import Dict, List, Any, Optional, Union
from loguru import logger


@dataclass
class AudioConfig:
    """Audio processing configuration"""
    sample_rate: int = 16000
    channels: int = 1
    bit_depth: int = 16
    buffer_size: int = 1024
    input_device_id: Optional[int] = None
    output_device_id: Optional[int] = None
    latency: str = "low"  # low, medium, high
    noise_reduction: bool = True
    auto_gain_control: bool = True
    echo_cancellation: bool = True


@dataclass
class STTConfig:
    """Speech-to-text configuration"""
    model: str = "openai/whisper-base"
    language: str = "en-US"
    confidence_threshold: float = 0.7
    enable_realtime: bool = True
    chunk_duration: float = 0.5
    device: str = "auto"  # auto, cpu, cuda, mps
    compute_type: str = "float16"  # float16, int8, float32
    beam_size: int = 5
    best_of: int = 5
    temperature: float = 0.0
    compression_ratio_threshold: float = 2.4
    log_prob_threshold: float = -1.0
    no_captions_threshold: float = 0.6
    condition_on_previous_text: bool = True
    initial_prompt: Optional[str] = None
    word_timestamps: bool = False
    prepend_punctuations: str = "\"'"¿([{-"
    append_punctuations: str = "\"'.。,，!！?？:：")]}、"


@dataclass
class TTSConfig:
    """Text-to-speech configuration"""
    voice: str = "default"
    speed: float = 1.0
    volume: float = 0.8
    language: str = "en-US"
    sample_rate: int = 22050
    channels: int = 1
    emotion: float = 0.5
    pacing: float = 0.5
    device: str = "auto"  # auto, cpu, cuda, mps
    voice_clone_enabled: bool = True
    backend: str = "auto"  # auto, chatterbox, coqui, gtts
    max_text_length: int = 5000
    ssml_enabled: bool = False
    normalize_text: bool = True
    remove_silence: bool = True
    enhance_quality: bool = True


@dataclass
class WakeWordConfig:
    """Wake word detection configuration"""
    enabled: bool = True
    words: List[str] = field(default_factory=lambda: ["hey swarm"])
    sensitivity: float = 0.5
    sample_rate: int = 16000
    frame_length: int = 512
    detection_threshold: float = 0.7
    consecutive_detections: int = 2
    cooldown_period: float = 2.0
    vad_enabled: bool = True
    vad_aggressiveness: int = 2  # 0-3 (most aggressive)
    min_speech_duration: float = 0.5
    max_speech_gap: float = 1.0
    porcupine_access_key: Optional[str] = None
    custom_model_path: Optional[str] = None


@dataclass
class ConversationConfig:
    """Conversation management configuration"""
    max_session_duration: int = 3600  # 1 hour
    max_turns_per_session: int = 100
    context_window_size: int = 10
    session_cleanup_interval: int = 300  # 5 minutes
    persist_conversations: bool = True
    conversation_storage_path: str = "conversations"
    max_stored_sessions: int = 1000
    auto_save_interval: int = 30  # seconds
    compress_old_conversations: bool = True
    max_context_tokens: int = 2000
    enable_conversation_summary: bool = True


@dataclass
class NetworkConfig:
    """Network and API configuration"""
    host: str = "localhost"
    port: int = 7830
    workers: int = 1
    max_connections: int = 100
    timeout: int = 30
    keep_alive: int = 2
    cors_origins: List[str] = field(default_factory=lambda: ["*"])
    cors_methods: List[str] = field(default_factory=lambda: ["*"])
    cors_headers: List[str] = field(default_factory=lambda: ["*"])
    websocket_ping_interval: int = 20
    websocket_ping_timeout: int = 20


@dataclass
class SecurityConfig:
    """Security configuration"""
    enable_auth: bool = False
    auth_token: Optional[str] = None
    rate_limit_enabled: bool = True
    rate_limit_requests: int = 100
    rate_limit_window: int = 60  # seconds
    max_file_size: int = 50 * 1024 * 1024  # 50MB
    allowed_file_types: List[str] = field(default_factory=lambda: [
        "audio/wav", "audio/mp3", "audio/flac", "audio/ogg", "audio/mpeg"
    ])
    encryption_enabled: bool = False
    ssl_enabled: bool = False
    ssl_cert_path: Optional[str] = None
    ssl_key_path: Optional[str] = None


@dataclass
class LoggingConfig:
    """Logging configuration"""
    level: str = "INFO"
    format: str = "<green>{time:YYYY-MM-DD HH:mm:ss}</green> | <level>{level: <8}</level> | <cyan>VoiceAssistant</cyan> | <level>{message}</level>"
    file_enabled: bool = True
    file_path: str = "logs/voice_assistant.log"
    file_rotation: str = "10 MB"
    file_retention: str = "7 days"
    console_enabled: bool = True
    json_format: bool = False
    enable_trace: bool = False


@dataclass
class PerformanceConfig:
    """Performance optimization configuration"""
    enable_gpu: bool = True
    gpu_memory_fraction: float = 0.8
    enable_mixed_precision: bool = True
    enable_tensorrt: bool = False
    batch_size: int = 1
    num_threads: int = 4
    enable_profiling: bool = False
    cache_enabled: bool = True
    cache_size: int = 100
    preload_models: bool = True
    model_cache_dir: str = "models"


@dataclass
class VoiceConfig:
    """Main voice assistant configuration"""
    audio: AudioConfig = field(default_factory=AudioConfig)
    stt: STTConfig = field(default_factory=STTConfig)
    tts: TTSConfig = field(default_factory=TTSConfig)
    wake_word: WakeWordConfig = field(default_factory=WakeWordConfig)
    conversation: ConversationConfig = field(default_factory=ConversationConfig)
    network: NetworkConfig = field(default_factory=NetworkConfig)
    security: SecurityConfig = field(default_factory=SecurityConfig)
    logging: LoggingConfig = field(default_factory=LoggingConfig)
    performance: PerformanceConfig = field(default_factory=PerformanceConfig)
    
    # Runtime properties
    start_time: float = field(default_factory=time.time)
    config_version: str = "1.0.0"
    debug: bool = False
    
    def __post_init__(self):
        """Post-initialization validation and setup"""
        self._validate_config()
        self._setup_directories()
    
    def _validate_config(self):
        """Validate configuration values"""
        # Audio validation
        if not 8000 <= self.audio.sample_rate <= 48000:
            logger.warning(f"Unusual sample rate: {self.audio.sample_rate}Hz")
        
        if not 1 <= self.audio.channels <= 2:
            raise ValueError("Audio channels must be 1 (mono) or 2 (stereo)")
        
        # STT validation
        if not 0.0 <= self.stt.confidence_threshold <= 1.0:
            raise ValueError("STT confidence threshold must be between 0.0 and 1.0")
        
        if not 0.1 <= self.stt.chunk_duration <= 5.0:
            raise ValueError("STT chunk duration must be between 0.1 and 5.0 seconds")
        
        # TTS validation
        if not 0.5 <= self.tts.speed <= 2.0:
            raise ValueError("TTS speed must be between 0.5 and 2.0")
        
        if not 0.0 <= self.tts.volume <= 1.0:
            raise ValueError("TTS volume must be between 0.0 and 1.0")
        
        # Wake word validation
        if self.wake_word.enabled and not self.wake_word.words:
            raise ValueError("Wake words list cannot be empty when wake word detection is enabled")
        
        if not 0.0 <= self.wake_word.sensitivity <= 1.0:
            raise ValueError("Wake word sensitivity must be between 0.0 and 1.0")
        
        # Network validation
        if not 1024 <= self.network.port <= 65535:
            raise ValueError("Network port must be between 1024 and 65535")
        
        logger.debug("Configuration validation passed")
    
    def _setup_directories(self):
        """Create necessary directories"""
        directories = [
            self.conversation.conversation_storage_path,
            self.performance.model_cache_dir,
            Path(self.logging.file_path).parent,
            "uploads",
            "temp",
            "voice_samples",
            "wake_word_models"
        ]
        
        for directory in directories:
            Path(directory).mkdir(parents=True, exist_ok=True)
    
    @classmethod
    def from_file(cls, config_path: Union[str, Path]) -> 'VoiceConfig':
        """Load configuration from JSON file"""
        try:
            config_path = Path(config_path)
            if not config_path.exists():
                logger.warning(f"Config file not found: {config_path}, using defaults")
                return cls()
            
            with open(config_path, 'r', encoding='utf-8') as f:
                config_data = json.load(f)
            
            # Create config object from data
            config = cls()
            config._update_from_dict(config_data)
            
            logger.info(f"Configuration loaded from: {config_path}")
            return config
            
        except Exception as e:
            logger.error(f"Failed to load config from {config_path}: {e}")
            logger.info("Using default configuration")
            return cls()
    
    @classmethod
    def from_env(cls) -> 'VoiceConfig':
        """Load configuration from environment variables"""
        config = cls()
        
        # Audio config from environment
        if os.getenv('VOICE_SAMPLE_RATE'):
            config.audio.sample_rate = int(os.getenv('VOICE_SAMPLE_RATE'))
        if os.getenv('VOICE_CHANNELS'):
            config.audio.channels = int(os.getenv('VOICE_CHANNELS'))
        
        # STT config from environment
        if os.getenv('VOICE_STT_MODEL'):
            config.stt.model = os.getenv('VOICE_STT_MODEL')
        if os.getenv('VOICE_STT_LANGUAGE'):
            config.stt.language = os.getenv('VOICE_STT_LANGUAGE')
        if os.getenv('VOICE_STT_CONFIDENCE'):
            config.stt.confidence_threshold = float(os.getenv('VOICE_STT_CONFIDENCE'))
        
        # TTS config from environment
        if os.getenv('VOICE_TTS_VOICE'):
            config.tts.voice = os.getenv('VOICE_TTS_VOICE')
        if os.getenv('VOICE_TTS_SPEED'):
            config.tts.speed = float(os.getenv('VOICE_TTS_SPEED'))
        if os.getenv('VOICE_TTS_VOLUME'):
            config.tts.volume = float(os.getenv('VOICE_TTS_VOLUME'))
        
        # Wake word config from environment
        if os.getenv('VOICE_WAKE_ENABLED'):
            config.wake_word.enabled = os.getenv('VOICE_WAKE_ENABLED').lower() == 'true'
        if os.getenv('VOICE_WAKE_WORDS'):
            config.wake_word.words = os.getenv('VOICE_WAKE_WORDS').split(',')
        if os.getenv('VOICE_WAKE_SENSITIVITY'):
            config.wake_word.sensitivity = float(os.getenv('VOICE_WAKE_SENSITIVITY'))
        
        # Network config from environment
        if os.getenv('VOICE_HOST'):
            config.network.host = os.getenv('VOICE_HOST')
        if os.getenv('VOICE_PORT'):
            config.network.port = int(os.getenv('VOICE_PORT'))
        
        # Logging config from environment
        if os.getenv('VOICE_LOG_LEVEL'):
            config.logging.level = os.getenv('VOICE_LOG_LEVEL').upper()
        if os.getenv('VOICE_LOG_FILE'):
            config.logging.file_path = os.getenv('VOICE_LOG_FILE')
        
        # Debug mode
        if os.getenv('VOICE_DEBUG'):
            config.debug = os.getenv('VOICE_DEBUG').lower() == 'true'
        
        logger.info("Configuration loaded from environment variables")
        return config
    
    @classmethod 
    def from_swarmui_config(cls, swarmui_config: str) -> 'VoiceConfig':
        """Load configuration from SwarmUI config JSON string"""
        try:
            if not swarmui_config or swarmui_config == "{}":
                return cls()
            
            config_data = json.loads(swarmui_config)
            config = cls()
            config._update_from_dict(config_data)
            
            logger.info("Configuration loaded from SwarmUI")
            return config
            
        except Exception as e:
            logger.error(f"Failed to load SwarmUI config: {e}")
            return cls()
    
    def _update_from_dict(self, config_data: Dict[str, Any]):
        """Update configuration from dictionary"""
        try:
            # Update each configuration section
            if 'audio' in config_data:
                self._update_dataclass(self.audio, config_data['audio'])
            
            if 'stt' in config_data:
                self._update_dataclass(self.stt, config_data['stt'])
            
            if 'tts' in config_data:
                self._update_dataclass(self.tts, config_data['tts'])
            
            if 'wake_word' in config_data:
                self._update_dataclass(self.wake_word, config_data['wake_word'])
            
            if 'conversation' in config_data:
                self._update_dataclass(self.conversation, config_data['conversation'])
            
            if 'network' in config_data:
                self._update_dataclass(self.network, config_data['network'])
            
            if 'security' in config_data:
                self._update_dataclass(self.security, config_data['security'])
            
            if 'logging' in config_data:
                self._update_dataclass(self.logging, config_data['logging'])
            
            if 'performance' in config_data:
                self._update_dataclass(self.performance, config_data['performance'])
            
            # Update root level properties
            if 'debug' in config_data:
                self.debug = config_data['debug']
            
            self._validate_config()
            
        except Exception as e:
            logger.error(f"Error updating config from dict: {e}")
            raise
    
    def _update_dataclass(self, target_obj: Any, update_data: Dict[str, Any]):
        """Update dataclass fields from dictionary"""
        for key, value in update_data.items():
            if hasattr(target_obj, key):
                setattr(target_obj, key, value)
            else:
                logger.warning(f"Unknown config key: {key}")
    
    def to_dict(self) -> Dict[str, Any]:
        """Convert configuration to dictionary"""
        return {
            'audio': asdict(self.audio),
            'stt': asdict(self.stt),
            'tts': asdict(self.tts),
            'wake_word': asdict(self.wake_word),
            'conversation': asdict(self.conversation),
            'network': asdict(self.network),
            'security': asdict(self.security),
            'logging': asdict(self.logging),
            'performance': asdict(self.performance),
            'start_time': self.start_time,
            'config_version': self.config_version,
            'debug': self.debug
        }
    
    def to_file(self, config_path: Union[str, Path]):
        """Save configuration to JSON file"""
        try:
            config_path = Path(config_path)
            config_path.parent.mkdir(parents=True, exist_ok=True)
            
            with open(config_path, 'w', encoding='utf-8') as f:
                json.dump(self.to_dict(), f, indent=2, ensure_ascii=False)
            
            logger.info(f"Configuration saved to: {config_path}")
            
        except Exception as e:
            logger.error(f"Failed to save config to {config_path}: {e}")
    
    def get_summary(self) -> Dict[str, Any]:
        """Get a summary of key configuration settings"""
        return {
            'audio': {
                'sample_rate': self.audio.sample_rate,
                'channels': self.audio.channels,
                'buffer_size': self.audio.buffer_size
            },
            'stt': {
                'model': self.stt.model,
                'language': self.stt.language,
                'confidence_threshold': self.stt.confidence_threshold,
                'device': self.stt.device
            },
            'tts': {
                'voice': self.tts.voice,
                'backend': self.tts.backend,
                'sample_rate': self.tts.sample_rate,
                'device': self.tts.device
            },
            'wake_word': {
                'enabled': self.wake_word.enabled,
                'words': self.wake_word.words,
                'sensitivity': self.wake_word.sensitivity
            },
            'network': {
                'host': self.network.host,
                'port': self.network.port,
                'workers': self.network.workers
            },
            'performance': {
                'enable_gpu': self.performance.enable_gpu,
                'preload_models': self.performance.preload_models,
                'cache_enabled': self.performance.cache_enabled
            }
        }
    
    def validate_and_fix(self) -> List[str]:
        """Validate configuration and fix common issues"""
        fixes = []
        
        # Fix audio sample rate
        if self.audio.sample_rate not in [8000, 16000, 22050, 44100, 48000]:
            old_rate = self.audio.sample_rate
            self.audio.sample_rate = 16000
            fixes.append(f"Fixed audio sample rate: {old_rate} -> {self.audio.sample_rate}")
        
        # Fix wake word sensitivity
        if not 0.0 <= self.wake_word.sensitivity <= 1.0:
            old_sensitivity = self.wake_word.sensitivity
            self.wake_word.sensitivity = max(0.0, min(1.0, self.wake_word.sensitivity))
            fixes.append(f"Fixed wake word sensitivity: {old_sensitivity} -> {self.wake_word.sensitivity}")
        
        # Fix TTS speed
        if not 0.5 <= self.tts.speed <= 2.0:
            old_speed = self.tts.speed
            self.tts.speed = max(0.5, min(2.0, self.tts.speed))
            fixes.append(f"Fixed TTS speed: {old_speed} -> {self.tts.speed}")
        
        # Fix port range
        if not 1024 <= self.network.port <= 65535:
            old_port = self.network.port
            self.network.port = max(1024, min(65535, self.network.port))
            fixes.append(f"Fixed network port: {old_port} -> {self.network.port}")
        
        return fixes


def load_config(config_path: Optional[Union[str, Path]] = None, 
               swarmui_config: Optional[str] = None) -> VoiceConfig:
    """Load configuration from various sources"""
    
    # Priority order: file, SwarmUI config, environment, defaults
    if config_path and Path(config_path).exists():
        return VoiceConfig.from_file(config_path)
    elif swarmui_config:
        return VoiceConfig.from_swarmui_config(swarmui_config)
    elif os.getenv('VOICE_ASSISTANT_CONFIG'):
        return VoiceConfig.from_swarmui_config(os.getenv('VOICE_ASSISTANT_CONFIG'))
    else:
        # Load from environment variables with defaults
        return VoiceConfig.from_env()


def create_default_config_file(config_path: Union[str, Path] = "config.json"):
    """Create a default configuration file"""
    config = VoiceConfig()
    config.to_file(config_path)
    logger.info(f"Default configuration file created: {config_path}")


if __name__ == "__main__":
    # Create default config file if run as script
    create_default_config_file()
