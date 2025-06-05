"""
Conversation Manager - Conversation State and Context Management
SwarmUI VoiceAssistant Extension - Conversation Flow Management

This module manages conversation state, context, and multi-turn dialogue
for the voice assistant, including session management and conversation history.
"""

import asyncio
import json
import time
import uuid
from dataclasses import dataclass, field
from typing import Dict, List, Optional, Any, Union
from datetime import datetime, timedelta
from pathlib import Path

from loguru import logger


@dataclass
class ConversationTurn:
    """Represents a single turn in a conversation"""
    turn_id: str
    user_input: str
    assistant_response: str
    timestamp: datetime
    confidence: float = 0.0
    processing_time: float = 0.0
    context: Dict[str, Any] = field(default_factory=dict)
    metadata: Dict[str, Any] = field(default_factory=dict)


@dataclass
class ConversationSession:
    """Represents a conversation session"""
    session_id: str
    user_id: str
    created_at: datetime
    last_activity: datetime
    turns: List[ConversationTurn] = field(default_factory=list)
    context: Dict[str, Any] = field(default_factory=dict)
    preferences: Dict[str, Any] = field(default_factory=dict)
    is_active: bool = True
    language: str = "en-US"
    total_turns: int = 0
    
    def add_turn(self, turn: ConversationTurn):
        """Add a turn to the conversation"""
        self.turns.append(turn)
        self.last_activity = datetime.utcnow()
        self.total_turns += 1
    
    def get_recent_turns(self, count: int = 5) -> List[ConversationTurn]:
        """Get recent conversation turns"""
        return self.turns[-count:] if self.turns else []
    
    def get_context_window(self, max_tokens: int = 2000) -> List[ConversationTurn]:
        """Get conversation turns within token limit"""
        # Simple approximation: ~4 chars per token
        max_chars = max_tokens * 4
        total_chars = 0
        context_turns = []
        
        for turn in reversed(self.turns):
            turn_chars = len(turn.user_input) + len(turn.assistant_response)
            if total_chars + turn_chars > max_chars and context_turns:
                break
            context_turns.insert(0, turn)
            total_chars += turn_chars
        
        return context_turns


@dataclass
class ConversationConfig:
    """Configuration for conversation management"""
    max_session_duration: int = 3600  # 1 hour in seconds
    max_turns_per_session: int = 100
    context_window_size: int = 10
    session_cleanup_interval: int = 300  # 5 minutes
    persist_conversations: bool = True
    conversation_storage_path: str = "conversations"
    max_stored_sessions: int = 1000


class ConversationManager:
    """Manages conversation sessions, context, and state"""
    
    def __init__(self, config: ConversationConfig = None):
        self.config = config or ConversationConfig()
        self.active_sessions: Dict[str, ConversationSession] = {}
        self.user_sessions: Dict[str, List[str]] = {}  # user_id -> session_ids
        self.session_index: Dict[str, str] = {}  # session_id -> user_id
        
        # Storage
        self.storage_path = Path(self.config.conversation_storage_path)
        self.storage_path.mkdir(exist_ok=True)
        
        # Cleanup task
        self._cleanup_task: Optional[asyncio.Task] = None
        self._running = False
        
        logger.info("Conversation Manager initialized")
    
    async def initialize(self) -> bool:
        """Initialize conversation manager"""
        try:
            # Load persisted conversations if enabled
            if self.config.persist_conversations:
                await self._load_persisted_sessions()
            
            # Start cleanup task
            self._running = True
            self._cleanup_task = asyncio.create_task(self._cleanup_loop())
            
            logger.info("Conversation manager initialized successfully")
            return True
            
        except Exception as e:
            logger.error(f"Failed to initialize conversation manager: {e}")
            return False
    
    def create_session(self, user_id: str, language: str = "en-US") -> ConversationSession:
        """Create a new conversation session"""
        try:
            session_id = str(uuid.uuid4())
            session = ConversationSession(
                session_id=session_id,
                user_id=user_id,
                created_at=datetime.utcnow(),
                last_activity=datetime.utcnow(),
                language=language
            )
            
            # Store session
            self.active_sessions[session_id] = session
            self.session_index[session_id] = user_id
            
            # Update user sessions
            if user_id not in self.user_sessions:
                self.user_sessions[user_id] = []
            self.user_sessions[user_id].append(session_id)
            
            logger.info(f"Created conversation session {session_id} for user {user_id}")
            return session
            
        except Exception as e:
            logger.error(f"Failed to create session for user {user_id}: {e}")
            raise
    
    def get_session(self, session_id: str) -> Optional[ConversationSession]:
        """Get existing conversation session"""
        return self.active_sessions.get(session_id)
    
    def get_or_create_session(self, session_id: str = None, user_id: str = None, 
                             language: str = "en-US") -> ConversationSession:
        """Get existing session or create new one"""
        if session_id and session_id in self.active_sessions:
            session = self.active_sessions[session_id]
            session.last_activity = datetime.utcnow()
            return session
        
        if not user_id:
            user_id = session_id or str(uuid.uuid4())
        
        return self.create_session(user_id, language)
    
    def end_session(self, session_id: str):
        """End a conversation session"""
        try:
            if session_id in self.active_sessions:
                session = self.active_sessions[session_id]
                session.is_active = False
                
                # Persist session if enabled
                if self.config.persist_conversations:
                    asyncio.create_task(self._persist_session(session))
                
                # Remove from active sessions
                user_id = self.session_index.get(session_id)
                if user_id and user_id in self.user_sessions:
                    if session_id in self.user_sessions[user_id]:
                        self.user_sessions[user_id].remove(session_id)
                
                del self.active_sessions[session_id]
                if session_id in self.session_index:
                    del self.session_index[session_id]
                
                logger.info(f"Ended conversation session {session_id}")
                
        except Exception as e:
            logger.error(f"Failed to end session {session_id}: {e}")
    
    async def process_voice_input(self, session_id: str, audio_data: bytes, 
                                 user_id: str = None) -> Dict[str, Any]:
        """Process voice input and manage conversation flow"""
        try:
            start_time = time.time()
            
            # Get or create session
            session = self.get_or_create_session(session_id, user_id)
            
            # This would integrate with STT service in a real implementation
            # For now, return a placeholder response
            transcription = f"Voice input received at {datetime.now().isoformat()}"
            confidence = 0.8
            
            # Create conversation turn
            turn = ConversationTurn(
                turn_id=str(uuid.uuid4()),
                user_input=transcription,
                assistant_response="Processing voice input...",
                timestamp=datetime.utcnow(),
                confidence=confidence,
                processing_time=time.time() - start_time,
                context={"input_type": "voice", "audio_length": len(audio_data)}
            )
            
            session.add_turn(turn)
            
            return {
                "session_id": session_id,
                "turn_id": turn.turn_id,
                "transcription": transcription,
                "confidence": confidence,
                "processing_time": turn.processing_time,
                "context": session.context
            }
            
        except Exception as e:
            logger.error(f"Failed to process voice input for session {session_id}: {e}")
            return {
                "session_id": session_id,
                "error": str(e),
                "processing_time": time.time() - start_time
            }
    
    async def process_text_input(self, session_id: str, text: str, 
                                user_id: str = None) -> Dict[str, Any]:
        """Process text input and manage conversation flow"""
        try:
            start_time = time.time()
            
            # Get or create session
            session = self.get_or_create_session(session_id, user_id)
            
            # Generate assistant response (this would integrate with LLM)
            assistant_response = await self._generate_assistant_response(session, text)
            
            # Create conversation turn
            turn = ConversationTurn(
                turn_id=str(uuid.uuid4()),
                user_input=text,
                assistant_response=assistant_response,
                timestamp=datetime.utcnow(),
                confidence=1.0,  # Text input has full confidence
                processing_time=time.time() - start_time,
                context={"input_type": "text"}
            )
            
            session.add_turn(turn)
            
            return {
                "session_id": session_id,
                "turn_id": turn.turn_id,
                "user_input": text,
                "assistant_response": assistant_response,
                "processing_time": turn.processing_time,
                "context": session.context
            }
            
        except Exception as e:
            logger.error(f"Failed to process text input for session {session_id}: {e}")
            return {
                "session_id": session_id,
                "error": str(e),
                "processing_time": time.time() - start_time
            }
    
    async def _generate_assistant_response(self, session: ConversationSession, 
                                          user_input: str) -> str:
        """Generate assistant response (placeholder for LLM integration)"""
        try:
            # Get conversation context
            recent_turns = session.get_recent_turns(self.config.context_window_size)
            
            # Simple response generation (would be replaced with LLM calls)
            if not recent_turns:
                return "Hello! I'm your voice assistant. How can I help you today?"
            
            # Context-aware response based on recent conversation
            if "image" in user_input.lower() or "generate" in user_input.lower():
                return "I can help you generate images. What would you like me to create?"
            elif "help" in user_input.lower():
                return "I can help you with image generation, voice commands, and general assistance. What would you like to do?"
            elif "thank" in user_input.lower():
                return "You're welcome! Is there anything else I can help you with?"
            else:
                return f"I understand you said: '{user_input}'. How can I assist you with that?"
                
        except Exception as e:
            logger.error(f"Failed to generate assistant response: {e}")
            return "I'm sorry, I had trouble processing your request. Could you please try again?"
    
    def prepare_llm_request(self, session_id: str, user_input: str) -> Dict[str, Any]:
        """Prepare request data for LLM service"""
        try:
            session = self.get_session(session_id)
            if not session:
                return {
                    "messages": [{"role": "user", "content": user_input}],
                    "context": {},
                    "session_id": session_id
                }
            
            # Build message history for LLM
            messages = []
            
            # Add system prompt based on context
            system_prompt = self._build_system_prompt(session)
            if system_prompt:
                messages.append({"role": "system", "content": system_prompt})
            
            # Add conversation history
            context_turns = session.get_context_window()
            for turn in context_turns:
                messages.append({"role": "user", "content": turn.user_input})
                messages.append({"role": "assistant", "content": turn.assistant_response})
            
            # Add current user input
            messages.append({"role": "user", "content": user_input})
            
            return {
                "messages": messages,
                "context": session.context,
                "session_id": session_id,
                "user_id": session.user_id,
                "language": session.language,
                "turn_count": session.total_turns
            }
            
        except Exception as e:
            logger.error(f"Failed to prepare LLM request for session {session_id}: {e}")
            return {
                "messages": [{"role": "user", "content": user_input}],
                "context": {},
                "session_id": session_id,
                "error": str(e)
            }
    
    def _build_system_prompt(self, session: ConversationSession) -> str:
        """Build context-aware system prompt"""
        base_prompt = ("You are a helpful AI assistant integrated with SwarmUI for image generation. "
                      "You can help users create prompts, control workflows, and manage image generation tasks.")
        
        # Add context based on conversation history
        context_additions = []
        
        if session.total_turns > 0:
            context_additions.append(f"This is an ongoing conversation with {session.total_turns} previous turns.")
        
        if session.language != "en-US":
            context_additions.append(f"The user's preferred language is {session.language}.")
        
        # Add user preferences if available
        if session.preferences:
            pref_str = ", ".join([f"{k}: {v}" for k, v in session.preferences.items()])
            context_additions.append(f"User preferences: {pref_str}")
        
        if context_additions:
            return base_prompt + " " + " ".join(context_additions)
        
        return base_prompt
    
    def update_user_context(self, user_id: str, context: Dict[str, Any]):
        """Update user context across all sessions"""
        try:
            # Update context for all active sessions of this user
            if user_id in self.user_sessions:
                for session_id in self.user_sessions[user_id]:
                    if session_id in self.active_sessions:
                        session = self.active_sessions[session_id]
                        session.context.update(context)
                        session.last_activity = datetime.utcnow()
            
            logger.info(f"Updated context for user {user_id}")
            
        except Exception as e:
            logger.error(f"Failed to update user context for {user_id}: {e}")
    
    def get_conversation_history(self, session_id: str, limit: int = 50) -> List[Dict[str, Any]]:
        """Get conversation history for a session"""
        try:
            session = self.get_session(session_id)
            if not session:
                return []
            
            # Return recent turns as dictionaries
            recent_turns = session.turns[-limit:] if session.turns else []
            return [
                {
                    "turn_id": turn.turn_id,
                    "user_input": turn.user_input,
                    "assistant_response": turn.assistant_response,
                    "timestamp": turn.timestamp.isoformat(),
                    "confidence": turn.confidence,
                    "processing_time": turn.processing_time,
                    "context": turn.context,
                    "metadata": turn.metadata
                }
                for turn in recent_turns
            ]
            
        except Exception as e:
            logger.error(f"Failed to get conversation history for session {session_id}: {e}")
            return []
    
    def save_conversation_turn(self, session_id: str, turn: ConversationTurn):
        """Save a conversation turn to the session"""
        try:
            session = self.get_session(session_id)
            if session:
                session.add_turn(turn)
                logger.debug(f"Saved conversation turn {turn.turn_id} to session {session_id}")
            else:
                logger.warning(f"Session {session_id} not found for saving turn")
                
        except Exception as e:
            logger.error(f"Failed to save conversation turn: {e}")
    
    def get_session_stats(self, session_id: str) -> Dict[str, Any]:
        """Get statistics for a conversation session"""
        try:
            session = self.get_session(session_id)
            if not session:
                return {"error": "Session not found"}
            
            # Calculate session statistics
            duration = (datetime.utcnow() - session.created_at).total_seconds()
            avg_confidence = 0.0
            total_processing_time = 0.0
            
            if session.turns:
                confidences = [turn.confidence for turn in session.turns]
                avg_confidence = sum(confidences) / len(confidences)
                total_processing_time = sum(turn.processing_time for turn in session.turns)
            
            return {
                "session_id": session_id,
                "user_id": session.user_id,
                "created_at": session.created_at.isoformat(),
                "last_activity": session.last_activity.isoformat(),
                "duration_seconds": duration,
                "total_turns": session.total_turns,
                "average_confidence": avg_confidence,
                "total_processing_time": total_processing_time,
                "language": session.language,
                "is_active": session.is_active,
                "context_size": len(session.context),
                "preferences_count": len(session.preferences)
            }
            
        except Exception as e:
            logger.error(f"Failed to get session stats for {session_id}: {e}")
            return {"error": str(e)}
    
    def get_user_sessions(self, user_id: str) -> List[str]:
        """Get all session IDs for a user"""
        return self.user_sessions.get(user_id, []).copy()
    
    def get_active_sessions_count(self) -> int:
        """Get count of active sessions"""
        return len(self.active_sessions)
    
    async def _persist_session(self, session: ConversationSession):
        """Persist session to storage"""
        try:
            if not self.config.persist_conversations:
                return
            
            # Create session file path
            session_file = self.storage_path / f"{session.session_id}.json"
            
            # Convert session to dictionary
            session_data = {
                "session_id": session.session_id,
                "user_id": session.user_id,
                "created_at": session.created_at.isoformat(),
                "last_activity": session.last_activity.isoformat(),
                "language": session.language,
                "total_turns": session.total_turns,
                "is_active": session.is_active,
                "context": session.context,
                "preferences": session.preferences,
                "turns": [
                    {
                        "turn_id": turn.turn_id,
                        "user_input": turn.user_input,
                        "assistant_response": turn.assistant_response,
                        "timestamp": turn.timestamp.isoformat(),
                        "confidence": turn.confidence,
                        "processing_time": turn.processing_time,
                        "context": turn.context,
                        "metadata": turn.metadata
                    }
                    for turn in session.turns
                ]
            }
            
            # Write to file
            with open(session_file, 'w', encoding='utf-8') as f:
                json.dump(session_data, f, indent=2, ensure_ascii=False)
            
            logger.debug(f"Persisted session {session.session_id}")
            
        except Exception as e:
            logger.error(f"Failed to persist session {session.session_id}: {e}")
    
    async def _load_persisted_sessions(self):
        """Load persisted sessions from storage"""
        try:
            session_files = list(self.storage_path.glob("*.json"))
            loaded_count = 0
            
            for session_file in session_files:
                try:
                    with open(session_file, 'r', encoding='utf-8') as f:
                        session_data = json.load(f)
                    
                    # Reconstruct session object
                    session = ConversationSession(
                        session_id=session_data["session_id"],
                        user_id=session_data["user_id"],
                        created_at=datetime.fromisoformat(session_data["created_at"]),
                        last_activity=datetime.fromisoformat(session_data["last_activity"]),
                        language=session_data.get("language", "en-US"),
                        total_turns=session_data.get("total_turns", 0),
                        is_active=session_data.get("is_active", False),
                        context=session_data.get("context", {}),
                        preferences=session_data.get("preferences", {})
                    )
                    
                    # Reconstruct turns
                    for turn_data in session_data.get("turns", []):
                        turn = ConversationTurn(
                            turn_id=turn_data["turn_id"],
                            user_input=turn_data["user_input"],
                            assistant_response=turn_data["assistant_response"],
                            timestamp=datetime.fromisoformat(turn_data["timestamp"]),
                            confidence=turn_data.get("confidence", 0.0),
                            processing_time=turn_data.get("processing_time", 0.0),
                            context=turn_data.get("context", {}),
                            metadata=turn_data.get("metadata", {})
                        )
                        session.turns.append(turn)
                    
                    # Only load recent sessions
                    age = datetime.utcnow() - session.last_activity
                    if age.total_seconds() < self.config.max_session_duration:
                        self.active_sessions[session.session_id] = session
                        self.session_index[session.session_id] = session.user_id
                        
                        if session.user_id not in self.user_sessions:
                            self.user_sessions[session.user_id] = []
                        self.user_sessions[session.user_id].append(session.session_id)
                        
                        loaded_count += 1
                    
                except Exception as e:
                    logger.warning(f"Failed to load session from {session_file}: {e}")
            
            logger.info(f"Loaded {loaded_count} persisted sessions")
            
        except Exception as e:
            logger.error(f"Failed to load persisted sessions: {e}")
    
    async def _cleanup_loop(self):
        """Background task to cleanup expired sessions"""
        while self._running:
            try:
                await asyncio.sleep(self.config.session_cleanup_interval)
                await self._cleanup_expired_sessions()
                
            except asyncio.CancelledError:
                break
            except Exception as e:
                logger.error(f"Error in cleanup loop: {e}")
    
    async def _cleanup_expired_sessions(self):
        """Remove expired sessions"""
        try:
            current_time = datetime.utcnow()
            expired_sessions = []
            
            for session_id, session in self.active_sessions.items():
                # Check if session has expired
                age = current_time - session.last_activity
                if (age.total_seconds() > self.config.max_session_duration or
                    session.total_turns > self.config.max_turns_per_session):
                    expired_sessions.append(session_id)
            
            # Remove expired sessions
            for session_id in expired_sessions:
                self.end_session(session_id)
            
            if expired_sessions:
                logger.info(f"Cleaned up {len(expired_sessions)} expired sessions")
                
        except Exception as e:
            logger.error(f"Error during session cleanup: {e}")
    
    async def cleanup(self):
        """Cleanup conversation manager resources"""
        try:
            logger.info("Cleaning up conversation manager...")
            
            # Stop cleanup task
            self._running = False
            if self._cleanup_task:
                self._cleanup_task.cancel()
                try:
                    await self._cleanup_task
                except asyncio.CancelledError:
                    pass
            
            # Persist all active sessions
            if self.config.persist_conversations:
                persist_tasks = []
                for session in self.active_sessions.values():
                    persist_tasks.append(self._persist_session(session))
                
                if persist_tasks:
                    await asyncio.gather(*persist_tasks, return_exceptions=True)
            
            # Clear all sessions
            self.active_sessions.clear()
            self.user_sessions.clear()
            self.session_index.clear()
            
            logger.info("Conversation manager cleanup completed")
            
        except Exception as e:
            logger.error(f"Error during conversation manager cleanup: {e}")
