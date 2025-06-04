using SwarmUI.Utils;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceAssistant.Extensions;

/// <summary>Manages the Python backend process lifecycle for voice processing services
/// including RealtimeSTT, Chatterbox TTS, and wake word detection</summary>
public class VoiceBackendManager : IDisposable
{
    /// <summary>Python backend process</summary>
    public Process PythonProcess { get; set; }

    /// <summary>Backend service status</summary>
    public bool IsBackendRunning { get; set; } = false;

    /// <summary>Backend service URL</summary>
    public string BackendUrl { get; set; } = "http://localhost:7830";

    /// <summary>Last successful health check timestamp</summary>
    public DateTime LastHealthCheck { get; set; } = DateTime.MinValue;

    /// <summary>Last error message from backend</summary>
    public string LastError { get; set; } = string.Empty;

    /// <summary>HTTP client for backend communication</summary>
    public readonly HttpClient httpClient; // TODO: Use SwarmUI's HttpClientManager for better integration

    /// <summary>Process cancellation token source</summary>
    public CancellationTokenSource cancellationTokenSource;

    /// <summary>Backend startup timeout in milliseconds</summary>
    public const int StartupTimeoutMs = 30000;

    /// <summary>Health check timeout in milliseconds</summary>
    public const int HealthCheckTimeoutMs = 5000;

    /// <summary>Maximum restart attempts</summary>
    public const int MaxRestartAttempts = 3;

    /// <summary>Current restart attempt count</summary>
    public int restartAttempts = 0;

    /// <summary>Lock object for thread safety</summary>
    public readonly object processLock = new object();

    public VoiceBackendManager()
    {
        httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMilliseconds(HealthCheckTimeoutMs)
        };
        cancellationTokenSource = new CancellationTokenSource();
    }

    /// <summary>Start the Python backend service</summary>
    /// <returns>True if backend started successfully, false otherwise</returns>
    public async Task<bool> StartBackendAsync()
    {
        lock (processLock)
        {
            if (IsBackendRunning && PythonProcess != null && !PythonProcess.HasExited)
            {
                Logs.Debug("[VoiceAssistant] Backend already running");
                return true;
            }
        }
        try
        {
            Logs.Init("[VoiceAssistant] Starting Python backend service");
            // Find Python executable
            string pythonPath = GetPythonExecutablePath();
            if (string.IsNullOrEmpty(pythonPath))
            {
                LastError = "Python executable not found";
                Logs.Error($"[VoiceAssistant] {LastError}");
                return false;
            }
            // Get Python script path
            string scriptPath = GetPythonScriptPath();
            if (!File.Exists(scriptPath))
            {
                LastError = $"Python script not found at: {scriptPath}";
                Logs.Error($"[VoiceAssistant] {LastError}");
                return false;
            }
            // Create process start info
            ProcessStartInfo startInfo = CreateProcessStartInfo(pythonPath, scriptPath);
            // Start the process
            lock (processLock)
            {
                PythonProcess = new Process
                {
                    StartInfo = startInfo,
                    EnableRaisingEvents = true
                };
                PythonProcess.OutputDataReceived += OnProcessOutputReceived;
                PythonProcess.ErrorDataReceived += OnProcessErrorReceived;
                PythonProcess.Exited += OnProcessExited;
                if (!PythonProcess.Start())
                {
                    LastError = "Failed to start Python process";
                    Logs.Error($"[VoiceAssistant] {LastError}");
                    return false;
                }
                PythonProcess.BeginOutputReadLine();
                PythonProcess.BeginErrorReadLine();
            }
            // Wait for backend to be ready
            bool isReady = await WaitForStartupAsync(StartupTimeoutMs);
            if (isReady)
            {
                IsBackendRunning = true;
                LastHealthCheck = DateTime.UtcNow;
                restartAttempts = 0;
                Logs.Init($"[VoiceAssistant] Python backend started successfully (PID: {PythonProcess.Id})");
                return true;
            }
            else
            {
                LastError = "Backend startup timeout";
                Logs.Error($"[VoiceAssistant] {LastError}");
                await StopBackendAsync();
                return false;
            }
        }
        catch (Exception ex)
        {
            LastError = $"Exception starting backend: {ex.Message}";
            Logs.Error($"[VoiceAssistant] {LastError}");
            await StopBackendAsync();
            return false;
        }
    }

    /// <summary>Stop the Python backend service gracefully</summary>
    public async Task StopBackendAsync()
    {
        try
        {
            Logs.Init("[VoiceAssistant] Stopping Python backend service");
            lock (processLock)
            {
                if (PythonProcess == null)
                {
                    IsBackendRunning = false;
                    return;
                }
            }
            // Try graceful shutdown first
            try
            {
                await SendShutdownSignalAsync();
                await Task.Delay(2000); // Give process time to shutdown gracefully
            }
            catch (Exception ex)
            {
                Logs.Debug($"[VoiceAssistant] Graceful shutdown failed: {ex.Message}");
            }
            lock (processLock)
            {
                if (PythonProcess != null && !PythonProcess.HasExited)
                {
                    try
                    {
                        // Force kill if still running
                        PythonProcess.Kill();
                        if (!PythonProcess.WaitForExit(5000))
                        {
                            Logs.Error("[VoiceAssistant] Failed to terminate Python process within timeout");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logs.Error($"[VoiceAssistant] Error killing Python process: {ex}");
                    }
                }
                PythonProcess?.Dispose();
                PythonProcess = null;
                IsBackendRunning = false;
            }
            Logs.Init("[VoiceAssistant] Python backend stopped");
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] Error stopping backend: {ex}");
        }
    }

    /// <summary>Restart the Python backend service</summary>
    /// <returns>True if restart was successful, false otherwise</returns>
    public async Task<bool> RestartBackendAsync()
    {
        if (restartAttempts >= MaxRestartAttempts)
        {
            LastError = $"Maximum restart attempts ({MaxRestartAttempts}) exceeded";
            Logs.Error($"[VoiceAssistant] {LastError}");
            return false;
        }
        restartAttempts++;
        Logs.Init($"[VoiceAssistant] Restarting backend (attempt {restartAttempts}/{MaxRestartAttempts})");
        await StopBackendAsync();
        await Task.Delay(3000); // Wait before restart
        return await StartBackendAsync();
    }

    /// <summary>Check if the Python backend is healthy and responding</summary>
    /// <returns>True if backend is healthy, false otherwise</returns>
    public async Task<bool> CheckHealthAsync()
    {
        if (!IsBackendRunning)
        {
            return false;
        }
        try
        {
            JObject request = new JObject
            {
                ["command"] = "health_check",
                ["timestamp"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
            };
            JObject response = await SendRequestAsync<JObject>("health", request);
            if (response != null && response["status"]?.ToString() == "healthy")
            {
                LastHealthCheck = DateTime.UtcNow;
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            Logs.Debug($"[VoiceAssistant] Health check failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Send HTTP request to Python backend</summary>
    /// <typeparam name="T">Response type</typeparam>
    /// <param name="endpoint">API endpoint</param>
    /// <param name="data">Request data</param>
    /// <returns>Response object or null if failed</returns>
    public async Task<T> SendRequestAsync<T>(string endpoint, object data) where T : class
    {
        if (!IsBackendRunning)
        {
            throw new InvalidOperationException("Python backend is not running");
        }

        try
        {
            string url = $"{BackendUrl}/api/{endpoint.TrimStart('/')}";
            string jsonContent = data is string ? data.ToString() : JObject.FromObject(data).ToString();
            using StringContent content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await httpClient.PostAsync(url, content);
            if (response.IsSuccessStatusCode)
            {
                string responseContent = await response.Content.ReadAsStringAsync();
                if (typeof(T) == typeof(string))
                {
                    return responseContent as T;
                }
                return JObject.Parse(responseContent).ToObject<T>();
            }
            else
            {
                string errorContent = await response.Content.ReadAsStringAsync();
                Logs.Error($"[VoiceAssistant] Backend request failed: {response.StatusCode} - {errorContent}");
                return null;
            }
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] Backend request exception: {ex}");
            return null;
        }
    }

    /// <summary>Wait for the Python backend to start up and be ready</summary>
    /// <param name="timeoutMs">Timeout in milliseconds</param>
    /// <returns>True if backend is ready, false if timeout</returns>
    public async Task<bool> WaitForStartupAsync(int timeoutMs)
    {
        int elapsed = 0;
        int checkInterval = 1000; // Check every second
        while (elapsed < timeoutMs)
        {
            try
            {
                if (await CheckHealthAsync())
                {
                    return true;
                }
            }
            catch
            {
                // Ignore exceptions during startup checks
            }
            await Task.Delay(checkInterval);
            elapsed += checkInterval;
            // Check if process has exited
            lock (processLock)
            {
                if (PythonProcess?.HasExited == true)
                {
                    Logs.Error("[VoiceAssistant] Python process exited during startup");
                    return false;
                }
            }
        }
        return false;
    }

    /// <summary>Get the Python executable path for the backend</summary>
    /// <returns>Python executable path or null if not found</returns>
    public string GetPythonExecutablePath()
    {
        try
        {
            // Try SwarmUI's ComfyUI Python environment first
            string comfyPythonPath = Path.Combine("dlbackend", "comfy", "python_embeded", "python.exe");
            if (File.Exists(comfyPythonPath))
            {
                return Path.GetFullPath(comfyPythonPath);
            }
            // Try system Python
            string[] pythonCommands = { "python", "python3", "py" };
            foreach (string cmd in pythonCommands)
            {
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo
                    {
                        FileName = cmd,
                        Arguments = "--version",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    };
                    using Process process = Process.Start(psi);
                    if (process != null)
                    {
                        process.WaitForExit(3000);
                        if (process.ExitCode == 0)
                        {
                            return cmd;
                        }
                    }
                }
                catch
                {
                    // Continue to next command
                }
            }
            return null;
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] Error finding Python executable: {ex}");
            return null;
        }
    }

    /// <summary>Get the Python script path for the voice backend</summary>
    /// <returns>Absolute path to the Python script</returns>
    public string GetPythonScriptPath()
    {
        return Path.GetFullPath(Path.Combine("src", "Extensions", "VoiceAssistant", "python_backend", "voice_server.py"));
    }

    /// <summary>Create process start info for the Python backend</summary>
    /// <param name="pythonPath">Python executable path</param>
    /// <param name="scriptPath">Python script path</param>
    /// <returns>Configured ProcessStartInfo</returns>
    public ProcessStartInfo CreateProcessStartInfo(string pythonPath, string scriptPath)
    {
        ProcessStartInfo startInfo = new ProcessStartInfo
        {
            FileName = pythonPath,
            Arguments = $"\"{scriptPath}\" --port=7830 --host=localhost",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            WorkingDirectory = Environment.CurrentDirectory
        };
        // Set environment variables
        Dictionary<string, string> envVars = GetEnvironmentVariables();
        foreach (KeyValuePair<string, string> kvp in envVars)
        {
            startInfo.EnvironmentVariables[kvp.Key] = kvp.Value;
        }
        return startInfo;
    }

    /// <summary>Get environment variables for the Python process</summary>
    /// <returns>Dictionary of environment variables</returns>
    public Dictionary<string, string> GetEnvironmentVariables()
    {
        Dictionary<string, string> envVars = new Dictionary<string, string>();
        try
        {
            // Set CUDA paths if available
            string cudaPath = Environment.GetEnvironmentVariable("CUDA_PATH");
            if (!string.IsNullOrEmpty(cudaPath))
            {
                envVars["CUDA_PATH"] = cudaPath;
            }
            // Set Python path for voice assistant modules
            string pythonPath = Path.Combine(Environment.CurrentDirectory, "src", "Extensions", "VoiceAssistant", "python_backend");
            envVars["PYTHONPATH"] = pythonPath;
            // Disable Python buffering for real-time output
            envVars["PYTHONUNBUFFERED"] = "1";
            // Set voice assistant configuration
            envVars["VOICE_ASSISTANT_CONFIG"] = VoiceAssistant.ConfigManager?.GetPythonEnvironmentConfig() ?? "{}";
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] Error setting environment variables: {ex}");
        }
        return envVars;
    }

    /// <summary>Send shutdown signal to the Python backend</summary>
    public async Task SendShutdownSignalAsync()
    {
        try
        {
            JObject request = new JObject
            {
                ["command"] = "shutdown",
                ["timestamp"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
            };
            await SendRequestAsync<JObject>("shutdown", request);
        }
        catch (Exception ex)
        {
            Logs.Debug($"[VoiceAssistant] Error sending shutdown signal: {ex}");
        }
    }

    /// <summary>Handle Python process output</summary>
    public void OnProcessOutputReceived(object sender, DataReceivedEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.Data))
        {
            Logs.Debug($"[VoiceAssistant|Python] {e.Data}");
        }
    }

    /// <summary>Handle Python process error output</summary>
    public void OnProcessErrorReceived(object sender, DataReceivedEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.Data))
        {
            Logs.Error($"[VoiceAssistant|Python] ERROR: {e.Data}");
            LastError = e.Data;
        }
    }

    /// <summary>Handle Python process exit</summary>
    public void OnProcessExited(object sender, EventArgs e)
    {
        lock (processLock)
        {
            if (PythonProcess != null)
            {
                int exitCode = PythonProcess.ExitCode;
                Logs.Error($"[VoiceAssistant] Python process exited with code: {exitCode}");
                IsBackendRunning = false;
                // Try automatic restart if enabled and not too many attempts
                if (VoiceAssistant.EnableVoiceAssistant.Value && restartAttempts < MaxRestartAttempts)
                {
                    Task.Run(async () =>
                    {
                        await Task.Delay(5000); // Wait 5 seconds before restart
                        await RestartBackendAsync();
                    });
                }
            }
        }
    }

    /// <summary>Dispose of resources</summary>
    public void Dispose()
    {
        try
        {
            cancellationTokenSource?.Cancel();
            StopBackendAsync().Wait(5000);
            httpClient?.Dispose();
            cancellationTokenSource?.Dispose();
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] Error disposing VoiceBackendManager: {ex}");
        }
    }
}
