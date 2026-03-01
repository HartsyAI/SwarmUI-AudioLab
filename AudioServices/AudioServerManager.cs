using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Newtonsoft.Json.Linq;
using SwarmUI.Backends;
using SwarmUI.Utils;

namespace Hartsy.Extensions.AudioLab.AudioServices;

/// <summary>Manages the persistent Python audio server process.
/// Uses SwarmUI's embedded Python environment with manual lifecycle management
/// (port allocation, log monitoring, health checks).
/// Replaces the subprocess-per-request model of PythonAudioProcessor.</summary>
public class AudioServerManager : IDisposable
{
    private static readonly Lazy<AudioServerManager> InstanceLazy = new(() => new AudioServerManager());
    public static AudioServerManager Instance => InstanceLazy.Value;

    private readonly HttpClient _httpClient = NetworkBackendUtils.MakeHttpClient(timeoutMinutes: 10);
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private Process _serverProcess;
    private int _port;
    private bool _isRunning;
    private int _dockerPort = 18716;
    private bool _dockerRunning;

    private AudioServerManager()
    {
        Logs.Debug("[AudioLab] AudioServerManager instance created");
    }

    /// <summary>Whether the persistent server is currently running and healthy.</summary>
    public bool IsRunning => _isRunning && _serverProcess is not null && !_serverProcess.HasExited;

    /// <summary>The port the server is listening on.</summary>
    public int Port => _port;

    /// <summary>Starts the persistent audio server with backend status management.
    /// Uses SwarmUI's embedded Python rather than DoSelfStart (which can't find Python
    /// outside the script's own directory tree).</summary>
    public async Task StartAsync(AbstractT2IBackend backend)
    {
        if (IsRunning)
        {
            Logs.Debug("[AudioLab] Server already running, skipping start");
            backend.Status = BackendStatus.RUNNING;
            return;
        }

        backend.Status = BackendStatus.LOADING;
        bool started = await EnsureRunningAsync();
        backend.Status = started ? BackendStatus.RUNNING : BackendStatus.ERRORED;
        if (!started)
        {
            backend.AddLoadStatus("Audio server failed to start. Check Python environment.");
        }
    }

    /// <summary>Ensures the audio server is running. Starts it standalone if needed (no backend required).
    /// Called automatically by ProcessAsync when the server isn't running.</summary>
    public async Task<bool> EnsureRunningAsync()
    {
        if (IsRunning) return true;

        await _startLock.WaitAsync();
        try
        {
            // Double-check after acquiring lock
            if (IsRunning) return true;

            string scriptPath = Path.Combine(AudioConfiguration.PythonBackendDirectory, "audio_server.py");
            if (!File.Exists(scriptPath))
            {
                Logs.Error($"[AudioLab] audio_server.py not found at {scriptPath}");
                return false;
            }

            string pythonPath = GetPythonPath();
            if (pythonPath == null)
            {
                Logs.Error("[AudioLab] Could not find Python. Ensure SwarmUI's Python environment is set up.");
                return false;
            }

            string modelRoot = Path.GetFullPath(AudioConfiguration.ModelRoot);
            string hfCache = Path.GetFullPath(AudioConfiguration.GetHuggingFaceCachePath());
            EnsureModelDirectories(modelRoot);

            _port = NetworkBackendUtils.GetNextPort();
            bool isEmbedded = pythonPath.Contains("python_embeded");

            ProcessStartInfo psi = new()
            {
                FileName = pythonPath,
                Arguments = $"-s \"{scriptPath}\" --port {_port} --model-root \"{modelRoot}\" --hf-cache \"{hfCache}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(scriptPath)
            };
            PythonLaunchHelper.CleanEnvironmentOfPythonMess(psi, "(AudioLab launch) ");
            if (isEmbedded)
            {
                psi.Environment["PATH"] = PythonLaunchHelper.ReworkPythonPaths(Path.GetDirectoryName(pythonPath));
            }

            Logs.Info($"[AudioLab] Starting audio server: {psi.FileName} {psi.Arguments}");

            _serverProcess = new Process { StartInfo = psi };
            _serverProcess.Start();

            // Monitor stderr for logs
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!_serverProcess.HasExited)
                    {
                        string line = await _serverProcess.StandardError.ReadLineAsync();
                        if (line != null) Logs.Debug($"[AudioLab/STDERR] {line}");
                    }
                }
                catch { /* Process exited */ }
            });

            // Poll health check
            for (int i = 0; i < 60; i++)
            {
                await Task.Delay(1000);
                if (_serverProcess.HasExited)
                {
                    Logs.Error($"[AudioLab] Server process exited during startup (exit code: {_serverProcess.ExitCode})");
                    return false;
                }
                try
                {
                    HttpResponseMessage resp = await _httpClient.GetAsync($"http://127.0.0.1:{_port}/health");
                    if (resp.IsSuccessStatusCode)
                    {
                        _isRunning = true;
                        Logs.Info($"[AudioLab] Audio server ready on port {_port} (PID: {_serverProcess.Id})");
                        return true;
                    }
                }
                catch { /* Server still starting */ }
            }

            Logs.Error("[AudioLab] Audio server failed to become healthy within 60 seconds");
            return false;
        }
        catch (Exception ex)
        {
            Logs.Error($"[AudioLab] Failed to start audio server: {ex.Message}");
            return false;
        }
        finally
        {
            _startLock.Release();
        }
    }

    /// <summary>Routes a processing request to the appropriate server (local or Docker) via HTTP.</summary>
    public async Task<JObject> ProcessAsync(AudioProviderDefinition provider, Dictionary<string, object> args)
    {
        // Route Docker-required providers to the Docker container if enabled
        if (provider.RequiresDocker && AudioConfiguration.UseDocker)
        {
            return await ProcessViaDockerAsync(provider, args);
        }

        // Auto-start the server if not running
        if (!IsRunning)
        {
            bool started = await EnsureRunningAsync();
            if (!started)
            {
                return CreateErrorResponse("Failed to start audio server. Check logs for details.");
            }
        }

        JObject payload = new()
        {
            ["module"] = provider.PythonModule,
            ["engine_class"] = provider.PythonEngineClass,
            ["kwargs"] = JObject.FromObject(args)
        };

        int timeoutMs = GetTimeoutMs(provider.Category);

        try
        {
            using CancellationTokenSource cts = new(timeoutMs);
            StringContent content = new(payload.ToString(), Encoding.UTF8, "application/json");
            HttpResponseMessage response = await _httpClient.PostAsync(
                $"http://127.0.0.1:{_port}/process", content, cts.Token);
            string body = await response.Content.ReadAsStringAsync();

            if (string.IsNullOrEmpty(body) || !body.StartsWith("{"))
            {
                return CreateErrorResponse($"Server returned invalid response: {body}");
            }

            return JObject.Parse(body);
        }
        catch (TaskCanceledException)
        {
            return CreateErrorResponse($"Request to {provider.Name} timed out after {timeoutMs / 1000}s");
        }
        catch (HttpRequestException ex)
        {
            _isRunning = false;
            Logs.Warning($"[AudioLab] Server connection lost: {ex.Message}");
            return CreateErrorResponse($"Server connection failed: {ex.Message}");
        }
        catch (Exception ex)
        {
            Logs.Error($"[AudioLab] ProcessAsync error: {ex.Message}");
            return CreateErrorResponse($"Processing error: {ex.Message}");
        }
    }

    /// <summary>Unloads a specific engine from GPU memory.</summary>
    public async Task<bool> UnloadEngineAsync(string module, string engineClass)
    {
        if (!IsRunning) return false;

        try
        {
            JObject payload = new() { ["module"] = module, ["engine_class"] = engineClass };
            StringContent content = new(payload.ToString(), Encoding.UTF8, "application/json");
            await _httpClient.PostAsync($"http://127.0.0.1:{_port}/unload", content);
            Logs.Info($"[AudioLab] Unloaded engine {module}:{engineClass}");
            return true;
        }
        catch (Exception ex)
        {
            Logs.Warning($"[AudioLab] Failed to unload engine: {ex.Message}");
            return false;
        }
    }

    /// <summary>Shuts down the persistent server gracefully.</summary>
    public async Task ShutdownAsync()
    {
        if (!IsRunning)
        {
            Logs.Debug("[AudioLab] Server not running, nothing to shut down");
            return;
        }

        try
        {
            Logs.Info("[AudioLab] Shutting down audio server...");
            using CancellationTokenSource cts = new(5000);
            await _httpClient.PostAsync($"http://127.0.0.1:{_port}/shutdown",
                new StringContent("{}", Encoding.UTF8, "application/json"), cts.Token);
        }
        catch (Exception ex)
        {
            Logs.Debug($"[AudioLab] Shutdown request: {ex.Message}");
        }

        _isRunning = false;

        // Wait for process to exit, kill if needed
        if (_serverProcess is not null && !_serverProcess.HasExited)
        {
            try
            {
                if (!_serverProcess.WaitForExit(5000))
                {
                    Logs.Warning("[AudioLab] Server did not exit gracefully, killing process");
                    _serverProcess.Kill();
                }
            }
            catch (Exception ex)
            {
                Logs.Debug($"[AudioLab] Process cleanup: {ex.Message}");
            }
        }

        Logs.Info("[AudioLab] Audio server stopped");
    }

    /// <summary>Gets diagnostic status of the server.</summary>
    public JObject GetStatus()
    {
        return new JObject
        {
            ["running"] = IsRunning,
            ["port"] = _port,
            ["pid"] = _serverProcess?.Id ?? 0,
            ["process_alive"] = _serverProcess is not null && !_serverProcess.HasExited
        };
    }

    /// <summary>Starts the Docker container for Linux-only engines.</summary>
    public async Task StartDockerAsync()
    {
        if (_dockerRunning) return;
        if (!AudioConfiguration.UseDocker)
        {
            Logs.Debug("[AudioLab] Docker support disabled, skipping container start");
            return;
        }

        string composePath = Path.Combine(AudioConfiguration.ExtensionDirectory, "python_backend", "docker", "docker-compose.yml");
        if (!File.Exists(composePath))
        {
            Logs.Warning($"[AudioLab] docker-compose.yml not found at {composePath}");
            return;
        }

        try
        {
            Logs.Info("[AudioLab] Starting Docker container for Linux-only engines...");
            ProcessStartInfo psi = new("docker", $"compose -f \"{composePath}\" up -d")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            Process proc = Process.Start(psi);
            await proc.WaitForExitAsync();

            if (proc.ExitCode == 0)
            {
                // Wait for health check
                for (int i = 0; i < 30; i++)
                {
                    await Task.Delay(2000);
                    try
                    {
                        HttpResponseMessage resp = await _httpClient.GetAsync($"http://127.0.0.1:{_dockerPort}/health");
                        if (resp.IsSuccessStatusCode)
                        {
                            _dockerRunning = true;
                            Logs.Info($"[AudioLab] Docker container ready on port {_dockerPort}");
                            return;
                        }
                    }
                    catch { /* Container still starting */ }
                }
                Logs.Warning("[AudioLab] Docker container started but health check timed out");
            }
            else
            {
                string stderr = await proc.StandardError.ReadToEndAsync();
                Logs.Error($"[AudioLab] Docker compose failed (exit {proc.ExitCode}): {stderr}");
            }
        }
        catch (Exception ex)
        {
            Logs.Error($"[AudioLab] Failed to start Docker container: {ex.Message}");
        }
    }

    /// <summary>Stops the Docker container for Linux-only engines.</summary>
    public async Task StopDockerAsync()
    {
        if (!_dockerRunning) return;

        string composePath = Path.Combine(AudioConfiguration.ExtensionDirectory, "python_backend", "docker", "docker-compose.yml");
        try
        {
            ProcessStartInfo psi = new("docker", $"compose -f \"{composePath}\" down")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            Process proc = Process.Start(psi);
            await proc.WaitForExitAsync();
            _dockerRunning = false;
            Logs.Info("[AudioLab] Docker container stopped");
        }
        catch (Exception ex)
        {
            Logs.Warning($"[AudioLab] Error stopping Docker container: {ex.Message}");
        }
    }

    /// <summary>Routes a request to the Docker container for Linux-only engines.</summary>
    private async Task<JObject> ProcessViaDockerAsync(AudioProviderDefinition provider, Dictionary<string, object> args)
    {
        if (!_dockerRunning)
        {
            await StartDockerAsync();
            if (!_dockerRunning)
            {
                return CreateErrorResponse($"{provider.Name} requires Docker (Linux-only engine). Enable Docker in backend settings and ensure Docker is installed.");
            }
        }

        JObject payload = new()
        {
            ["module"] = provider.PythonModule,
            ["engine_class"] = provider.PythonEngineClass,
            ["kwargs"] = JObject.FromObject(args)
        };

        int timeoutMs = GetTimeoutMs(provider.Category);

        try
        {
            using CancellationTokenSource cts = new(timeoutMs);
            StringContent content = new(payload.ToString(), Encoding.UTF8, "application/json");
            HttpResponseMessage response = await _httpClient.PostAsync(
                $"http://127.0.0.1:{_dockerPort}/process", content, cts.Token);
            string body = await response.Content.ReadAsStringAsync();

            if (string.IsNullOrEmpty(body) || !body.StartsWith("{"))
            {
                return CreateErrorResponse($"Docker server returned invalid response: {body}");
            }

            return JObject.Parse(body);
        }
        catch (TaskCanceledException)
        {
            return CreateErrorResponse($"Docker request to {provider.Name} timed out after {timeoutMs / 1000}s");
        }
        catch (HttpRequestException ex)
        {
            _dockerRunning = false;
            return CreateErrorResponse($"Docker server connection failed: {ex.Message}");
        }
        catch (Exception ex)
        {
            return CreateErrorResponse($"Docker processing error: {ex.Message}");
        }
    }

    public void Dispose()
    {
        ShutdownAsync().Wait(5000);
        StopDockerAsync().Wait(5000);
        _httpClient.Dispose();
    }

    // -- Helpers ---------------------------------------------------------------

    private static int GetTimeoutMs(AudioCategory _) => AudioConfiguration.TimeoutSeconds * 1000;

    /// <summary>Finds Python using the same logic as AudioDependencyInstaller.
    /// Checks SwarmUI's embedded Python first, then venv, then system Python.</summary>
    private static string GetPythonPath()
    {
        // SwarmUI embedded Python (Windows)
        if (File.Exists("./dlbackend/comfy/python_embeded/python.exe"))
            return Path.GetFullPath("./dlbackend/comfy/python_embeded/python.exe");
        // SwarmUI ComfyUI venv (Linux/Mac)
        if (File.Exists("./dlbackend/ComfyUI/venv/bin/python"))
            return Path.GetFullPath("./dlbackend/ComfyUI/venv/bin/python");
        // AudioLab local venv (if created manually)
        string localVenv = Path.Combine(AudioConfiguration.PythonBackendDirectory, "venv",
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Scripts/python.exe" : "bin/python");
        if (File.Exists(localVenv))
            return Path.GetFullPath(localVenv);
        Logs.Warning("[AudioLab] No known Python environment found. Falling back to system 'python'.");
        return null;
    }

    private static void EnsureModelDirectories(string root)
    {
        foreach (string sub in new[] { "tts", "stt", "music", "clone", "fx", ".cache" })
        {
            Directory.CreateDirectory(Path.Combine(root, sub));
        }
    }

    private static JObject CreateErrorResponse(string message)
    {
        return new JObject
        {
            ["success"] = false,
            ["error"] = message,
            ["timestamp"] = DateTime.UtcNow.ToString("O")
        };
    }
}
