using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.IO;
using System.Threading.Tasks;
using StableSwarmUI.Utils;
using StableSwarmUI.Core;
using FreneticUtilities.FreneticExtensions;

namespace Hartsy.Extensions.VoiceAssistant;

public partial class VoiceAssistant
{
    #region Backend Process Management

    private async Task<bool> StartBackend()
    {
        lock (BackendLock)
        {
            if (IsBackendRunning && PythonBackend != null && !PythonBackend.HasExited)
            {
                Logs.Info("[VoiceAssistant] Backend is already running");
                return true;
            }

            // Clean up any existing process
            if (PythonBackend != null)
            {
                try { PythonBackend.Dispose(); } catch { }
                PythonBackend = null;
            }

            try
            {
                // Get the path to the Python backend script
                string scriptPath = Path.Combine(ExtensionDirectory, "python_backend", "voice_server.py");
                if (!File.Exists(scriptPath))
                {
                    Logs.Error($"[VoiceAssistant] Backend script not found at: {scriptPath}");
                    return false;
                }

                // Use PythonLaunchHelper to start the process
                PythonBackend = PythonLaunchHelper.LaunchGeneric(
                    script: scriptPath,
                    autoOutput: true,
                    args: Array.Empty<string>()
                );
                PythonBackend.EnableRaisingEvents = true;
                PythonBackend.Exited += (sender, e) => {
                    Logs.Info("[VoiceAssistant] Backend process exited");
                    IsBackendRunning = false;
                    PythonBackend?.Dispose();
                    PythonBackend = null;
                };

                // Set up logging
                PythonBackend.OutputDataReceived += (sender, e) => 
                {
                    if (!string.IsNullOrEmpty(e?.Data))
                        Logs.Info($"[VoiceBackend] {e.Data}");
                };
                PythonBackend.ErrorDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e?.Data))
                        Logs.Error($"[VoiceBackend] {e.Data}");
                };

                // Give the backend some time to start
                await Task.Delay(2000);

                // Verify the backend is running
                bool isHealthy = await CheckBackendHealth();
                if (!isHealthy)
                {
                    Logs.Warning("[VoiceAssistant] Backend started but health check failed");
                    return false;
                }

                IsBackendRunning = true;
                Logs.Info("[VoiceAssistant] Backend started successfully");
                
                // Notify webhook if configured
                _ = WebhookManager.WaitUntilCanStartGenerating().ContinueWith(t => {
                    if (t.IsFaulted)
                        Logs.Error($"[VoiceAssistant] Error notifying webhook: {t.Exception}");
                });
                
                return true;
            }
            catch (Exception ex)
            {
                Logs.Error($"[VoiceAssistant] Failed to start backend: {ex}");
                return false;
            }
        }
    }

    private async Task StopBackend()
    {
        lock (BackendLock)
        {
            if (!IsBackendRunning || PythonBackend == null)
            {
                Logs.Info("[VoiceAssistant] Backend is not running");
                return;
            }

            try
            {
                // Send shutdown request to the backend
                using var client = NetworkBackendUtils.MakeHttpClient();
                client.Timeout = TimeSpan.FromSeconds(5);
                _ = client.PostAsync($"{BackendUrl}/shutdown", null).ContinueWith(t => 
                {
                    if (t.IsFaulted)
                    {
                        Logs.Debug($"[VoiceAssistant] Error sending shutdown: {t.Exception?.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                Logs.Debug($"[VoiceAssistant] Error sending shutdown to backend: {ex.Message}");
            }

            try
            {
                // Give it a moment to shut down gracefully
                if (!PythonBackend.WaitForExit(3000))
                {
                    try { PythonBackend.Kill(true); } 
                    catch (Exception ex) { Logs.Debug($"[VoiceAssistant] Error killing process: {ex.Message}"); }
                }
                PythonBackend.Dispose();
                PythonBackend = null;
                IsBackendRunning = false;
                Logs.Info("[VoiceAssistant] Backend stopped");
                
                // Notify webhook that generation is done
                _ = WebhookManager.TryMarkDoneGenerating().ContinueWith(t => {
                    if (t.IsFaulted)
                        Logs.Error($"[VoiceAssistant] Error notifying webhook: {t.Exception}");
                });
            }
            catch (Exception ex)
            {
                Logs.Error($"[VoiceAssistant] Error stopping backend: {ex}");
            }
        }
    }

    private async Task<bool> CheckBackendHealth()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var response = await HttpClient.GetAsync($"{BackendUrl}/health", cts.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Logs.Debug($"[VoiceAssistant] Health check failed: {ex.Message}");
            return false;
        }
    }

    private async Task StartPythonBackendSafe()
    {
        try
        {
            await StartBackend();
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] Error in StartPythonBackendSafe: {ex.Message}");
        }
    }

    private async Task StopPythonBackendSafe()
    {
        try
        {
            await StopBackend();
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] Error in StopPythonBackendSafe: {ex.Message}");
        }
    }

    #endregion

    #region API Endpoint Handlers

    private async Task<object> ProcessVoiceCommand(dynamic data)
    {
        // Notify that we're starting to process a command
        await WebhookManager.WaitUntilCanStartGenerating();
        
        try
        {
            if (!IsBackendRunning || !await CheckBackendHealth())
            {
                Logs.Warning("[VoiceAssistant] Backend not running, attempting to start...");
                if (!await StartBackend())
                {
                    await WebhookManager.TryMarkDoneGenerating();
                    return new { success = false, error = "Voice backend is not available" };
                }
            }

            string audioBase64 = data.audioData;
            if (string.IsNullOrEmpty(audioBase64))
            {
                await WebhookManager.TryMarkDoneGenerating();
                return new { success = false, error = "No audio data provided" };
            }

            // Send audio to backend for processing
            var request = new 
            {
                audio_data = audioBase64,
                language = VoiceLanguage?.Value ?? "en-US"
            };

            var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");
            var response = await HttpClient.PostAsync($"{BackendUrl}/process", content);
            
            if (!response.IsSuccessStatusCode)
            {
                return new { success = false, error = $"Backend error: {response.StatusCode}" };
            }

            var result = await response.Content.ReadAsStringAsync();
            var responseObj = JsonSerializer.Deserialize<Dictionary<string, object>>(result);
            
            // Process the command and generate response
            var commandResponse = await ProcessCommand(responseObj["text"]?.ToString() ?? "");
            
            return new { 
                success = true, 
                text = commandResponse.Text,
                audio = commandResponse.AudioBase64,
                command = commandResponse.Command
            };
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] Error processing voice command: {ex}");
            return new { success = false, error = ex.Message };
        }
        finally
        {
            // Ensure we mark as done even if an exception occurs
            await WebhookManager.TryMarkDoneGenerating();
        }
    }

    private async Task<object> ProcessTextCommand(dynamic data)
    {
        try
        {
            string text = data.text;
            if (string.IsNullOrEmpty(text))
            {
                return new { success = false, error = "No text provided" };
            }

            var commandResponse = await ProcessCommand(text);
            return new { 
                success = true, 
                text = commandResponse.Text,
                audio = commandResponse.AudioBase64,
                command = commandResponse.Command
            };
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] Error processing text command: {ex}");
            return new { success = false, error = ex.Message };
        }
    }

    private async Task<object> GetBackendStatus()
    {
        bool isHealthy = await CheckBackendHealth();
        return new 
        { 
            running = isHealthy,
            url = BackendUrl,
            version = "1.0.0"
        };
    }

    #endregion

    #region Command Processing

    private class CommandResponse
    {
        public string Text { get; set; } = string.Empty;
        public string? AudioBase64 { get; set; }
        public string? Command { get; set; }
    }

    private async Task<CommandResponse> ProcessCommand(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new CommandResponse { Text = "I didn't catch that. Could you please repeat?" };
        }

        text = text.Trim().ToLower();
        var response = new CommandResponse();

        // Simple command processing - can be expanded
        if (text.Contains("hello") || text.Contains("hi") || text.Contains("hey"))
        {
            response.Text = "Hello! How can I assist you with image generation today?";
        }
        else if (text.Contains("help") || text.Contains("what can you do"))
        {
            response.Text = "I can help you generate images. Try saying something like 'Generate a landscape with mountains and a lake' or 'Create a portrait of a cyberpunk character'.";
        }
        else if (text.Contains("generate") || text.Contains("create") || text.Contains("make") || text.Contains("draw"))
        {
            // This would be connected to your image generation logic
            response.Text = $"Generating image based on: {text}";
            response.Command = "generate_image";
            // You would add parameters to the command here
        }
        else
        {
            response.Text = $"I'll try to generate an image based on: {text}";
            response.Command = "generate_image";
            // You would add parameters to the command here
        }

        // Generate TTS for the response if backend is available
        if (IsBackendRunning && await CheckBackendHealth())
        {
            try
            {
                var ttsRequest = new 
                {
                    text = response.Text,
                    voice = TTSVoice?.Value ?? "default",
                    language = VoiceLanguage?.Value ?? "en-US"
                };

                var content = new StringContent(JsonSerializer.Serialize(ttsRequest), Encoding.UTF8, "application/json");
                var ttsResponse = await HttpClient.PostAsync($"{BackendUrl}/tts/synthesize", content);
                
                if (ttsResponse.IsSuccessStatusCode)
                {
                    var result = await ttsResponse.Content.ReadAsStringAsync();
                    var ttsResult = JsonSerializer.Deserialize<Dictionary<string, string>>(result);
                    if (ttsResult != null && ttsResult.ContainsKey("audio"))
                    {
                        response.AudioBase64 = ttsResult["audio"];
                    }
                }
            }
            catch (Exception ex)
            {
                Logs.Error($"[VoiceAssistant] Error generating TTS: {ex}");
            }
        }

        return response;
    }

    #endregion
}
