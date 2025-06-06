using SwarmUI.Core;
using SwarmUI.WebAPI;
using SwarmUI.Utils;
using SwarmUI.Accounts;
using SwarmUI.Text2Image;
using System.Diagnostics;
using System.Net.Http;
using System.Threading.Tasks;
using Hartsy.Extensions.VoiceAssistant.WebAPI;

namespace Hartsy.Extensions.VoiceAssistant;

/// <summary>
/// SwarmUI Voice Assistant Extension - Production Ready MVP
/// Provides speech-to-text, text-to-speech, and voice command processing
/// </summary>
public class VoiceAssistant : Extension
{
    #region Static Fields
    
    private static Process? PythonBackend;
    private static readonly HttpClient HttpClient = new();
    private static bool IsBackendRunning = false;
    private static readonly object BackendLock = new();
    private static readonly string BackendUrl = "http://localhost:7831";
    private static CancellationTokenSource? BackendCancellation;
    private static readonly HttpClient HttpClient = NetworkBackendUtils.MakeHttpClient();
    
    // SwarmUI Parameters
    public static T2IRegisteredParam<bool>? EnableVoiceAssistant;
    public static T2IRegisteredParam<string>? VoiceLanguage;
    public static T2IRegisteredParam<string>? TTSVoice;
    public static T2IRegisteredParam<float>? VoiceVolume;
    public static T2IParamGroup? VoiceGroup;

    #endregion

    #region Extension Lifecycle

    public override void OnPreInit()
    {
        Logs.Info("[VoiceAssistant] Initializing Voice Assistant Extension v1.0");
        
        // Register JavaScript files
        ScriptFiles.Add("Assets/voice-assistant.js");
        
        // Register CSS files
        StyleSheetFiles.Add("Assets/voice-assistant.css");
        
        // Configure HTTP client
        HttpClient.Timeout = TimeSpan.FromSeconds(30);
        HttpClient.DefaultRequestHeaders.Add("User-Agent", "SwarmUI-VoiceAssistant/1.0");
    }

    public override void OnInit()
    {
        try
        {
            // Register parameter group
            VoiceGroup = new T2IParamGroup("Voice Assistant", 
                Toggles: true, 
                Open: false, 
                IsAdvanced: false,
                OrderPriority: 5.0);
            
            // Register voice parameters
            EnableVoiceAssistant = T2IParamTypes.Register<bool>(new(
                "enable_voice_assistant", 
                "Enable Voice Assistant",
                "Activate voice commands for image generation and UI control", 
                false,
                Group: VoiceGroup, 
                Toggleable: true,
                OrderPriority: 1.0
            ));

            VoiceLanguage = T2IParamTypes.Register<string>(new(
                "voice_language",
                "Voice Language", 
                "Language for speech recognition and synthesis",
                "en-US",
                Group: VoiceGroup,
                Examples: new[] { "en-US", "en-GB", "es-ES", "fr-FR", "de-DE", "it-IT", "pt-BR", "ru-RU", "ja-JP", "ko-KR", "zh-CN" },
                OrderPriority: 2.0
            ));

            TTSVoice = T2IParamTypes.Register<string>(new(
                "tts_voice",
                "TTS Voice",
                "Voice model for text-to-speech synthesis", 
                "default",
                Group: VoiceGroup,
                Examples: new[] { "default", "male", "female", "neural" },
                OrderPriority: 3.0
            ));

            VoiceVolume = T2IParamTypes.Register<float>(new(
                "voice_volume",
                "Voice Volume",
                "Volume level for voice responses (0.0 to 1.0)",
                0.8f,
                Min: 0.0, Max: 1.0, Step: 0.1,
                Group: VoiceGroup,
                OrderPriority: 4.0
            ));

            // Register API endpoints
            VoiceAssistantAPI.Register();
            
            // Register frontend assets
            ScriptFiles.Add("Assets/voice-assistant.js");
            StyleSheetFiles.Add("Assets/voice-assistant.css");
            
            Logs.Info("[VoiceAssistant] Extension initialized successfully with API endpoints registered");
            
            // Auto-start backend if voice assistant is enabled
            if (EnableVoiceAssistant?.Value == true)
            {
                _ = Task.Run(async () => 
                {
                    await Task.Delay(2000); // Wait for SwarmUI to fully initialize
                    await StartPythonBackendSafe();
                });
            }
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] Failed to initialize extension: {ex.Message}");
            Logs.Debug($"[VoiceAssistant] Stack trace: {ex}");
        }
    }

    public override void OnShutdown()
    {
        try
        {
            Logs.Info("[VoiceAssistant] Shutting down Voice Assistant Extension");
            
            BackendCancellation?.Cancel();
            
            // Stop the backend synchronously with a timeout
            try
            {
                StopPythonBackendSafe().Wait(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                Logs.Error($"[VoiceAssistant] Error stopping backend: {ex.Message}");
            }
            
            // Clean up Python process if still running
            lock (BackendLock)
            {
                try
                {
                    if (PythonBackend != null && !PythonBackend.HasExited)
                    {
                        PythonBackend.Kill(true);
                        PythonBackend.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    Logs.Debug($"[VoiceAssistant] Error during process cleanup: {ex.Message}");
                }
            }
            
            HttpClient?.Dispose();
            Logs.Info("[VoiceAssistant] Extension shutdown complete");
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] Error during shutdown: {ex.Message}");
        }
    }

    #endregion

