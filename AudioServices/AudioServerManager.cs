using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Backends;
using SwarmUI.Core;
using SwarmUI.Utils;

namespace Hartsy.Extensions.AudioLab.AudioServices;

/// <summary>Manages per-compatibility-group Python audio server processes.
/// Each group (main, chatterbox, audiocraft, etc.) gets its own venv and server process
/// on a separate port. Servers are started lazily on first use via EnsureGroupRunningAsync.</summary>
public class AudioServerManager : IDisposable
{
    #region Fields

    private static readonly Lazy<AudioServerManager> InstanceLazy = new(() => new AudioServerManager());
    public static AudioServerManager Instance => InstanceLazy.Value;

    private readonly HttpClient _httpClient = NetworkBackendUtils.MakeHttpClient(timeoutMinutes: 10);
    private readonly ConcurrentDictionary<string, GroupServerState> _groupServers = new();
    private readonly ConcurrentDictionary<string, DateTime> _groupStartFailures = new();
    private static readonly TimeSpan StartFailureCooldown = TimeSpan.FromSeconds(60);
    private int _dockerPort = 18716;
    private bool _dockerRunning;

    /// <summary>State for a single compatibility group's server process.</summary>
    private class GroupServerState
    {
        /// <summary>Compatibility group name (e.g. "main", "chatterbox", "audiocraft").</summary>
        public string Group { get; init; }
        /// <summary>The running Python server process, or null if not started.</summary>
        public Process ServerProcess { get; set; }
        /// <summary>The HTTP port this group's server listens on.</summary>
        public int Port { get; set; }
        /// <summary>Whether the server has passed its health check.</summary>
        public bool IsRunning { get; set; }
        /// <summary>Prevents concurrent start attempts for this group.</summary>
        public SemaphoreSlim StartLock { get; } = new(1, 1);

        /// <summary>True if the server is running and the process hasn't exited.</summary>
        public bool IsAlive => IsRunning && ServerProcess is not null && !ServerProcess.HasExited;
    }

    private AudioServerManager()
    {
        Logs.Debug("[AudioLab] AudioServerManager instance created");
    }

    #endregion

    #region Properties

    /// <summary>Whether any group server is currently running and healthy.</summary>
    public bool IsRunning => _groupServers.Values.Any(g => g.IsAlive);

    /// <summary>Whether a specific group's server is running.</summary>
    public bool IsGroupRunning(string group) =>
        _groupServers.TryGetValue(group, out GroupServerState state) && state.IsAlive;

    /// <summary>The port for a specific group's server, or 0 if not running.</summary>
    public int GetGroupPort(string group) =>
        _groupServers.TryGetValue(group, out GroupServerState state) ? state.Port : 0;

    #endregion

    #region Server Lifecycle

    /// <summary>Starts the default group server with backend status management.
    /// Called by DynamicAudioBackend during initialization.</summary>
    public async Task StartAsync(AbstractT2IBackend backend)
    {
        if (IsRunning)
        {
            Logs.Debug("[AudioLab] Server already running, skipping start");
            backend.Status = BackendStatus.RUNNING;
            return;
        }

        backend.Status = BackendStatus.LOADING;
        bool started = await EnsureGroupRunningAsync("main");
        backend.Status = started ? BackendStatus.RUNNING : BackendStatus.ERRORED;
        if (!started)
        {
            backend.AddLoadStatus("Audio server failed to start. Check Python environment.");
        }
    }

    /// <summary>Backward-compatible wrapper: ensures the "main" group server is running.</summary>
    public Task<bool> EnsureRunningAsync() => EnsureGroupRunningAsync("main");

    /// <summary>Ensures a specific group's server is running. Creates its venv and starts the process if needed.
    /// Thread-safe: uses per-group locking to prevent concurrent starts for the same group.</summary>
    public async Task<bool> EnsureGroupRunningAsync(string group)
    {
        GroupServerState state = _groupServers.GetOrAdd(group, g => new GroupServerState { Group = g });

        if (state.IsAlive) return true;

        // Prevents retry storms during streaming when a group fails to start
        if (_groupStartFailures.TryGetValue(group, out DateTime failedAt)
            && DateTime.UtcNow - failedAt < StartFailureCooldown)
        {
            return false;
        }

        await state.StartLock.WaitAsync();
        try
        {
            if (state.IsAlive) return true;

            string scriptPath = Path.Combine(AudioConfiguration.PythonBackendDirectory, "audio_server.py");
            if (!File.Exists(scriptPath))
            {
                Logs.Error($"[AudioLab] audio_server.py not found at {scriptPath}");
                return false;
            }

            string pythonPath = await VenvManager.Instance.EnsureVenvAsync(group);
            if (pythonPath == null)
            {
                Logs.Error($"[AudioLab] Could not create Python venv for group '{group}'. AudioLab requires Python 3.10+ on your system PATH. Download from https://www.python.org/downloads/ (check 'Add python.exe to PATH'), then restart SwarmUI.");
                _groupStartFailures[group] = DateTime.UtcNow;
                return false;
            }

            if (!await InstallBootstrapDependenciesAsync(pythonPath, group))
            {
                _groupStartFailures[group] = DateTime.UtcNow;
                return false;
            }

            string modelRoot = Path.GetFullPath(AudioConfiguration.ModelRoot);
            string hfCache = Path.GetFullPath(AudioConfiguration.GetHuggingFaceCachePath());
            EnsureModelDirectories(modelRoot);

            state.Port = NetworkBackendUtils.GetNextPort();

            ProcessStartInfo psi = new()
            {
                FileName = pythonPath,
                Arguments = $"-s \"{scriptPath}\" --port {state.Port} --model-root \"{modelRoot}\" --hf-cache \"{hfCache}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(scriptPath)
            };
            PythonLaunchHelper.CleanEnvironmentOfPythonMess(psi, $"(AudioLab/{group}) ");
            psi.Environment["HF_HUB_DISABLE_SYMLINKS_WARNING"] = "1";
            psi.Environment["TOKENIZERS_PARALLELISM"] = "false";
            psi.Environment["PYTHONWARNINGS"] = "ignore::FutureWarning,ignore::UserWarning";
            string pythonDir = Path.GetDirectoryName(Path.GetFullPath(pythonPath));
            psi.Environment["PATH"] = PythonLaunchHelper.ReworkPythonPaths(pythonDir);
            try
            {
                if (Program.Sessions == null)
                {
                    Logs.Debug("[AudioLab] Program.Sessions is null — server starting before session init?");
                }
                else
                {
                    User user = Program.Sessions.GetUser(SessionHandler.LocalUserID);
                    if (user == null)
                    {
                        Logs.Debug($"[AudioLab] GetUser('{SessionHandler.LocalUserID}') returned null");
                    }
                    else
                    {
                        string hfToken = user.GetGenericData("huggingface_api", "key");
                        if (!string.IsNullOrEmpty(hfToken))
                        {
                            psi.Environment["HF_TOKEN"] = hfToken;
                            Logs.Info($"[AudioLab] HuggingFace token set for group '{group}'");
                        }
                        else
                        {
                            Logs.Debug("[AudioLab] No HuggingFace token found in user settings. Set one in SwarmUI Server > User Settings > API Keys to access gated models.");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logs.Warning($"[AudioLab] Could not retrieve HF token: {ex.Message}");
            }

            Logs.Info($"[AudioLab] Starting audio server for group '{group}': {psi.FileName} {psi.Arguments}");

            state.ServerProcess = new Process { StartInfo = psi };
            state.ServerProcess.Start();

            StringBuilder startupStderr = new();
            Process proc = state.ServerProcess;
            string logPrefix = group;
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!proc.HasExited)
                    {
                        string line = await proc.StandardError.ReadLineAsync();
                        if (line != null)
                        {
                            startupStderr.AppendLine(line);
                            Logs.Debug($"[AudioLab/{logPrefix}/STDERR] {line}");
                        }
                    }
                }
                catch { /* Process exited */ }
            });

            for (int i = 0; i < 60; i++)
            {
                await Task.Delay(1000);
                if (state.ServerProcess.HasExited)
                {
                    string stderr = startupStderr.ToString();
                    string hint = stderr.Contains("ModuleNotFoundError")
                        ? " Missing Python packages — install provider dependencies via the AudioLab UI."
                        : "";
                    Logs.Error($"[AudioLab] Server process for group '{group}' exited during startup (exit code: {state.ServerProcess.ExitCode}).{hint}");
                    _groupStartFailures[group] = DateTime.UtcNow;
                    return false;
                }
                try
                {
                    HttpResponseMessage resp = await _httpClient.GetAsync($"http://127.0.0.1:{state.Port}/health");
                    if (resp.IsSuccessStatusCode)
                    {
                        state.IsRunning = true;
                        _groupStartFailures.TryRemove(group, out _);
                        Logs.Info($"[AudioLab] Audio server for group '{group}' ready on port {state.Port} (PID: {state.ServerProcess.Id})");
                        return true;
                    }
                }
                catch { /* Server still starting */ }
            }

            Logs.Error($"[AudioLab] Audio server for group '{group}' failed to become healthy within 60 seconds");
            _groupStartFailures[group] = DateTime.UtcNow;
            return false;
        }
        catch (Exception ex)
        {
            Logs.Error($"[AudioLab] Failed to start audio server for group '{group}': {ex.Message}");
            _groupStartFailures[group] = DateTime.UtcNow;
            return false;
        }
        finally
        {
            state.StartLock.Release();
        }
    }

    #endregion

    #region Request Processing

    /// <summary>Routes a processing request to the appropriate group server (or Docker) via HTTP.</summary>
    public async Task<JObject> ProcessAsync(AudioProviderDefinition provider, Dictionary<string, object> args)
    {
        if (provider.RequiresDocker && AudioConfiguration.UseDocker)
        {
            return await ProcessViaDockerAsync(provider, args);
        }

        string group = provider.EngineGroup;
        if (!IsGroupRunning(group))
        {
            bool started = await EnsureGroupRunningAsync(group);
            if (!started)
            {
                return CreateErrorResponse($"Audio server for group '{group}' failed to start. Ensure provider dependencies are installed via the AudioLab UI, and that Python 3.10+ is on your PATH.");
            }
        }

        int port = GetGroupPort(group);

        JObject payload = new()
        {
            ["module"] = provider.PythonModule,
            ["engine_class"] = provider.PythonEngineClass,
            ["kwargs"] = JObject.FromObject(args),
            ["hf_token"] = GetHuggingFaceToken()
        };

        int timeoutMs = GetTimeoutMs(provider.Category);

        try
        {
            using CancellationTokenSource cts = new(timeoutMs);
            StringContent content = new(payload.ToString(), Encoding.UTF8, "application/json");
            HttpResponseMessage response = await _httpClient.PostAsync(
                $"http://127.0.0.1:{port}/process", content, cts.Token);
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
            if (_groupServers.TryGetValue(group, out GroupServerState state))
            {
                state.IsRunning = false;
            }
            Logs.Warning($"[AudioLab] Server connection lost for group '{group}': {ex.Message}");
            return CreateErrorResponse($"Server connection failed: {ex.Message}");
        }
        catch (Exception ex)
        {
            Logs.Error($"[AudioLab] ProcessAsync error: {ex.Message}");
            return CreateErrorResponse($"Processing error: {ex.Message}");
        }
    }

    /// <summary>Unloads a specific engine from GPU memory on the appropriate group server.</summary>
    public async Task<bool> UnloadEngineAsync(string module, string engineClass, string group = "main")
    {
        if (!IsGroupRunning(group)) return false;

        int port = GetGroupPort(group);
        try
        {
            JObject payload = new() { ["module"] = module, ["engine_class"] = engineClass };
            StringContent content = new(payload.ToString(), Encoding.UTF8, "application/json");
            await _httpClient.PostAsync($"http://127.0.0.1:{port}/unload", content);
            Logs.Info($"[AudioLab] Unloaded engine {module}:{engineClass} from group '{group}'");
            return true;
        }
        catch (Exception ex)
        {
            Logs.Warning($"[AudioLab] Failed to unload engine: {ex.Message}");
            return false;
        }
    }

    /// <summary>Shuts down all group servers gracefully.</summary>
    public async Task ShutdownAsync()
    {
        List<string> groups = _groupServers.Keys.ToList();
        if (groups.Count == 0)
        {
            Logs.Debug("[AudioLab] No servers running, nothing to shut down");
            return;
        }

        Logs.Info($"[AudioLab] Shutting down {groups.Count} audio server(s)...");
        await Task.WhenAll(groups.Select(ShutdownGroupAsync));
        Logs.Info("[AudioLab] All audio servers stopped");
    }

    /// <summary>Shuts down a specific group's server.</summary>
    public async Task ShutdownGroupAsync(string group)
    {
        if (!_groupServers.TryGetValue(group, out GroupServerState state)) return;

        if (!state.IsAlive)
        {
            _groupServers.TryRemove(group, out _);
            return;
        }

        try
        {
            Logs.Info($"[AudioLab] Shutting down audio server for group '{group}'...");
            using CancellationTokenSource cts = new(5000);
            await _httpClient.PostAsync($"http://127.0.0.1:{state.Port}/shutdown",
                new StringContent("{}", Encoding.UTF8, "application/json"), cts.Token);
        }
        catch (Exception ex)
        {
            Logs.Debug($"[AudioLab] Shutdown request for group '{group}': {ex.Message}");
        }

        state.IsRunning = false;

        if (state.ServerProcess is not null && !state.ServerProcess.HasExited)
        {
            try
            {
                if (!state.ServerProcess.WaitForExit(5000))
                {
                    Logs.Warning($"[AudioLab] Server for group '{group}' did not exit gracefully, killing process");
                    state.ServerProcess.Kill();
                }
            }
            catch (Exception ex)
            {
                Logs.Debug($"[AudioLab] Process cleanup for group '{group}': {ex.Message}");
            }
        }

        _groupServers.TryRemove(group, out _);
        Logs.Info($"[AudioLab] Audio server for group '{group}' stopped");
    }

    #endregion

    #region Diagnostics

    /// <summary>Gets diagnostic status of all group servers.</summary>
    public JObject GetStatus()
    {
        JObject status = new()
        {
            ["any_running"] = IsRunning,
            ["docker_running"] = _dockerRunning,
            ["docker_port"] = _dockerPort
        };

        JObject groups = new();
        foreach (KeyValuePair<string, GroupServerState> kvp in _groupServers)
        {
            groups[kvp.Key] = new JObject
            {
                ["running"] = kvp.Value.IsAlive,
                ["port"] = kvp.Value.Port,
                ["pid"] = kvp.Value.ServerProcess?.Id ?? 0,
                ["process_alive"] = kvp.Value.ServerProcess is not null && !kvp.Value.ServerProcess.HasExited
            };
        }
        status["groups"] = groups;

        return status;
    }

    #endregion

    #region Docker Support

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
            ["kwargs"] = JObject.FromObject(args),
            ["hf_token"] = GetHuggingFaceToken()
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

    /// <summary>Disposes HTTP client and shuts down all servers.</summary>
    public void Dispose()
    {
        ShutdownAsync().Wait(5000);
        StopDockerAsync().Wait(5000);
        _httpClient.Dispose();
    }

    #endregion

    #region Private Helpers

    /// <summary>Installs the minimum packages needed for audio_server.py to start.
    /// Currently just numpy (imported at top level by base_engine.py).
    /// Uses pip install which is idempotent — skips quickly if already installed.</summary>
    private async Task<bool> InstallBootstrapDependenciesAsync(string pythonPath, string group)
    {
        Logs.Info($"[AudioLab] Ensuring bootstrap dependencies for group '{group}'...");
        try
        {
            ProcessStartInfo psi = new()
            {
                FileName = pythonPath,
                Arguments = "-m pip install numpy soundfile --no-warn-script-location --prefer-binary -q",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AudioConfiguration.PythonBackendDirectory
            };
            PythonLaunchHelper.CleanEnvironmentOfPythonMess(psi, $"[AudioLab/{group}/bootstrap] ");
            string pythonDir = Path.GetDirectoryName(Path.GetFullPath(pythonPath));
            psi.Environment["PATH"] = PythonLaunchHelper.ReworkPythonPaths(pythonDir);

            // Use a short temp path to avoid Windows 260-char path limit during pip extraction.
            // Must override TMPDIR too — SwarmUI sets it globally (Program.cs) and Python's
            // tempfile module checks TMPDIR before TMP/TEMP on all platforms.
            string shortTmp = Path.Combine(Path.GetPathRoot(Environment.CurrentDirectory) ?? "C:\\", "tmp", "audiolab");
            Directory.CreateDirectory(shortTmp);
            psi.Environment["TMP"] = shortTmp;
            psi.Environment["TEMP"] = shortTmp;
            psi.Environment["TMPDIR"] = shortTmp;

            using Process proc = new() { StartInfo = psi };
            StringBuilder stderr = new();
            proc.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

            proc.Start();
            proc.BeginErrorReadLine();

            bool exited = await Task.Run(() => proc.WaitForExit(120_000));
            if (!exited)
            {
                try { proc.Kill(); } catch { }
                Logs.Error($"[AudioLab] Bootstrap dependency install timed out for group '{group}'");
                return false;
            }

            if (proc.ExitCode != 0)
            {
                Logs.Error($"[AudioLab] Bootstrap dependency install failed for group '{group}' (exit {proc.ExitCode}): {stderr}");
                return false;
            }

            Logs.Info($"[AudioLab] Bootstrap dependencies ready for group '{group}'");
            return true;
        }
        catch (Exception ex)
        {
            Logs.Error($"[AudioLab] Failed to install bootstrap dependencies for group '{group}': {ex.Message}");
            return false;
        }
    }

    /// <summary>Gets the request timeout in milliseconds from configuration.</summary>
    private static int GetTimeoutMs(AudioCategory _) => AudioConfiguration.TimeoutSeconds * 1000;

    /// <summary>Retrieves the current HuggingFace API token from user settings.
    /// Called per-request so token changes take effect without restarting the server.</summary>
    private static string GetHuggingFaceToken()
    {
        try
        {
            User user = Program.Sessions?.GetUser(SessionHandler.LocalUserID);
            return user?.GetGenericData("huggingface_api", "key") ?? "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>Downloads a model via the Python server's /download endpoint.
    /// Uses the 30-minute InstallationTimeout instead of the short request timeout.
    /// Called during engine installation so models are ready before registration.</summary>
    public async Task<JObject> DownloadModelAsync(string group, string modelName, string category)
    {
        bool started = await EnsureGroupRunningAsync(group);
        if (!started)
        {
            return CreateErrorResponse($"Audio server for group '{group}' failed to start.");
        }

        int port = GetGroupPort(group);

        JObject payload = new()
        {
            ["model_name"] = modelName,
            ["category"] = category,
            ["hf_token"] = GetHuggingFaceToken()
        };

        try
        {
            int timeoutMs = (int)AudioConfiguration.InstallationTimeout.TotalMilliseconds;
            using CancellationTokenSource cts = new(timeoutMs);
            StringContent content = new(payload.ToString(), Encoding.UTF8, "application/json");
            HttpResponseMessage response = await _httpClient.PostAsync(
                $"http://127.0.0.1:{port}/download", content, cts.Token);
            string body = await response.Content.ReadAsStringAsync();

            if (string.IsNullOrEmpty(body) || !body.StartsWith("{"))
            {
                return CreateErrorResponse($"Server returned invalid response: {body}");
            }

            return JObject.Parse(body);
        }
        catch (TaskCanceledException)
        {
            return CreateErrorResponse($"Model download timed out after {AudioConfiguration.InstallationTimeout.TotalMinutes:F0} minutes. The model may be too large or the connection too slow.");
        }
        catch (Exception ex)
        {
            return CreateErrorResponse($"Model download failed: {ex.Message}");
        }
    }

    /// <summary>Creates the standard model subdirectories (tts, stt, music, etc.) under the model root.</summary>
    private static void EnsureModelDirectories(string root)
    {
        foreach (string sub in new[] { "tts", "stt", "music", "clone", "fx", ".cache" })
        {
            Directory.CreateDirectory(Path.Combine(root, sub));
        }
    }

    /// <summary>Creates a standardized JSON error response.</summary>
    private static JObject CreateErrorResponse(string message)
    {
        return new JObject
        {
            ["success"] = false,
            ["error"] = message,
            ["timestamp"] = DateTime.UtcNow.ToString("O")
        };
    }

    #endregion
}
