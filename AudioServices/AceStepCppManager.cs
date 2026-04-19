using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using Newtonsoft.Json.Linq;
using SwarmUI.Backends;
using SwarmUI.Utils;

namespace Hartsy.Extensions.AudioLab.AudioServices;

/// <summary>Manages the acestep.cpp native binary: download, model management, server lifecycle, and job-based API.</summary>
public sealed class AceStepCppManager : IDisposable
{
    #region Constants

    public const string GITHUB_VST3_API_URL = "https://api.github.com/repos/ace-step/acestep.vst3/releases/latest";
    public const string GITHUB_CPP_API_URL = "https://api.github.com/repos/ace-step/acestep.cpp/releases/latest";
    public const string FALLBACK_RELEASE_TAG = "v0.1.0";
    public const string FALLBACK_WIN_URL = "https://github.com/ace-step/acestep.vst3/releases/download/v0.1.0/acestep-windows-x64.zip";
    public const string FALLBACK_LINUX_URL = "https://github.com/ace-step/acestep.vst3/releases/download/v0.1.0/acestep-linux-x64.tar.gz";
    public const string FALLBACK_MAC_URL = "https://github.com/ace-step/acestep.vst3/releases/download/v0.1.0/acestep-macos-arm64-metal.tar.gz";
    /// <summary>Community-hosted Windows build (updated more frequently than GitHub releases).</summary>
    public const string SERVEURPERSO_WIN_BASE_URL = "https://www.serveurperso.com/temp/acestep.cpp-win64/build/Release/";
    private static readonly string[] ServeurpersoWinFiles =
    [
        "ace-server.exe", "ace-lm.exe", "ace-synth.exe", "ace-understand.exe",
        "mp3-codec.exe", "neural-codec.exe", "quantize.exe",
        "ggml.dll", "ggml-base.dll", "ggml-cuda.dll", "ggml-vulkan.dll",
        "ggml-cpu-haswell.dll", "ggml-cpu-alderlake.dll", "ggml-cpu-cannonlake.dll",
        "ggml-cpu-cascadelake.dll", "ggml-cpu-icelake.dll", "ggml-cpu-sandybridge.dll",
        "ggml-cpu-skylakex.dll", "ggml-cpu-sse42.dll", "ggml-cpu-x64.dll"
    ];
    public const string HF_MODEL_BASE_URL = "https://huggingface.co/Serveurperso/ACE-Step-1.5-GGUF/resolve/main/";
    public const string VERSION_FILE = "acestep_version.json";
    private const int JOB_POLL_INTERVAL_MS = 500;
    private const int JOB_TIMEOUT_MS = 600_000; // 10 minutes max for generation
    private const int HEALTH_CHECK_TIMEOUT_MS = 30_000;
    private const int RETRY_503_MAX = 3;

    #endregion

    #region Singleton & State

    private static readonly Lazy<AceStepCppManager> InstanceLazy = new(() => new AceStepCppManager());
    public static AceStepCppManager Instance => InstanceLazy.Value;

    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private Process _serverProcess;
    private int _port;
    private bool _lmLoaded;
    /// <summary>Cached server props (available models, etc) from /props endpoint.</summary>
    private JObject _serverProps;

    /// <summary>Ring buffer of recent stderr lines for crash diagnostics.</summary>
    private readonly Queue<string> _recentStderr = new();
    private readonly object _stderrLock = new();
    private const int STDERR_BUFFER_SIZE = 50;
    /// <summary>Last critical error detected in stderr (GGML assertion, CUDA failure, etc).</summary>
    private volatile string _lastCriticalError;

    private AceStepCppManager()
    {
        _httpClient = NetworkBackendUtils.MakeHttpClient();
        _httpClient.Timeout = TimeSpan.FromMinutes(15);
    }

    #endregion

    #region Paths

    /// <summary>Root directory for the ace-server binary.</summary>
    public static string BinaryRoot => Path.GetFullPath(Path.Combine("dlbackend", "audiolab", "acestep"));

    /// <summary>Root directory for GGUF models.</summary>
    public static string ModelRoot => Path.GetFullPath(Path.Combine("Models", "audio", "music", "acestep-gguf"));

    /// <summary>Path to the ace-server executable.</summary>
    public static string ServerExecutable => Path.Combine(BinaryRoot,
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ace-server.exe" : "ace-server");

    /// <summary>Path to the version tracking file.</summary>
    public static string VersionFilePath => Path.Combine(BinaryRoot, VERSION_FILE);

    #endregion

    #region Binary Management

    /// <summary>Ensures the ace-server binary is downloaded and available. Also checks for updates.</summary>
    public async Task<string> EnsureBinaryAsync()
    {
        if (File.Exists(ServerExecutable))
        {
            // Binary exists — check for updates in the background (don't block startup)
            _ = Task.Run(async () =>
            {
                try { await CheckForUpdateAsync(); }
                catch (Exception ex) { Logs.Debug($"[AceStep] Update check failed: {ex.Message}"); }
            });
            return ServerExecutable;
        }
        Directory.CreateDirectory(BinaryRoot);
        Logs.Info("[AceStep] Downloading ace-server binary...");
        DownloadInfo info = await GetBinaryDownloadInfo();
        // info is null when files were downloaded directly (serveurperso fallback)
        if (info is null)
        {
            if (File.Exists(ServerExecutable))
            {
                return ServerExecutable;
            }
            Logs.Error("[AceStep] No pre-built binary available for this platform.");
            return null;
        }
        string executable = await DownloadAndExtractBinary(info);
        if (executable is not null)
        {
            SaveVersionInfo(new VersionInfo
            {
                TagName = info.TagName,
                ExecutablePath = executable,
                InstalledDate = DateTime.UtcNow,
                LastUpdateCheck = DateTime.UtcNow
            });
            Logs.Info($"[AceStep] Installed ace-server {info.TagName}");
        }
        return executable;
    }

    /// <summary>Gets download info from GitHub releases, checking both repos before falling back.
    /// Order: acestep.vst3 latest → acestep.cpp latest → serveurperso (Windows) → hardcoded v0.1.0.</summary>
    private async Task<DownloadInfo> GetBinaryDownloadInfo()
    {
        string pattern = GetAssetPattern();
        if (pattern is null)
        {
            return GetFallbackDownloadInfo();
        }
        // Try acestep.vst3 releases first, then acestep.cpp releases
        string[] repoUrls = [GITHUB_VST3_API_URL, GITHUB_CPP_API_URL];
        foreach (string repoUrl in repoUrls)
        {
            try
            {
                DownloadInfo info = await TryGetReleaseAssetAsync(repoUrl, pattern);
                if (info is not null)
                {
                    return info;
                }
            }
            catch (Exception ex)
            {
                Logs.Debug($"[AceStep] GitHub API check failed for {repoUrl}: {ex.Message}");
            }
        }
        // On Windows, try the community-hosted build before falling back to v0.1.0
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Logs.Info("[AceStep] No Windows binary in GitHub releases, trying community build...");
            bool downloaded = await DownloadServeurpersoFilesAsync();
            if (downloaded)
            {
                return null; // Signal that binary is already in place (no archive to extract)
            }
        }
        Logs.Info("[AceStep] Using v0.1.0 fallback");
        return GetFallbackDownloadInfo();
    }

    /// <summary>Checks a single GitHub releases/latest endpoint for a matching platform asset.</summary>
    private static async Task<DownloadInfo> TryGetReleaseAssetAsync(string apiUrl, string pattern)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, apiUrl);
        request.Headers.Add("User-Agent", "SwarmUI-AudioLab");
        using HttpResponseMessage response = await Utilities.UtilWebClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }
        JObject release = JObject.Parse(await response.Content.ReadAsStringAsync());
        string tag = release["tag_name"]?.ToString();
        if (release["assets"] is not JArray assets || assets.Count == 0)
        {
            return null;
        }
        foreach (JToken asset in assets)
        {
            string name = asset["name"]?.ToString();
            if (name is not null && name.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                Logs.Info($"[AceStep] Found binary {name} ({tag}) from {apiUrl}");
                return new DownloadInfo
                {
                    FileName = name,
                    DownloadUrl = asset["browser_download_url"]?.ToString(),
                    Size = asset["size"]?.ToObject<long>() ?? 0,
                    TagName = tag
                };
            }
        }
        return null;
    }

    /// <summary>Returns the asset filename pattern for the current OS.</summary>
    private static string GetAssetPattern()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "windows-x64";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return "linux-x64";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "macos-arm64";
        return null;
    }

    /// <summary>Returns hardcoded fallback download info for v0.1.0.</summary>
    private static DownloadInfo GetFallbackDownloadInfo()
    {
        string url;
        string fileName;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            url = FALLBACK_WIN_URL;
            fileName = "acestep-windows-x64.zip";
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            url = FALLBACK_LINUX_URL;
            fileName = "acestep-linux-x64.tar.gz";
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            url = FALLBACK_MAC_URL;
            fileName = "acestep-macos-arm64-metal.tar.gz";
        }
        else
        {
            Logs.Error("[AceStep] Unsupported platform for ace-server binary.");
            return null;
        }
        return new DownloadInfo { FileName = fileName, DownloadUrl = url, Size = 0, TagName = FALLBACK_RELEASE_TAG };
    }

    /// <summary>Downloads and extracts the binary archive.</summary>
    private async Task<string> DownloadAndExtractBinary(DownloadInfo info)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "SwarmUI-AceStep");
        Directory.CreateDirectory(tempDir);
        string archivePath = Path.Combine(tempDir, info.FileName);
        try
        {
            string sizeStr = info.Size > 0 ? $" ({info.Size / 1024.0 / 1024.0:F1} MB)" : "";
            Logs.Info($"[AceStep] Downloading {info.FileName}{sizeStr}...");
            await Utilities.DownloadFile(info.DownloadUrl, archivePath, (_, __, ___) => { });
            if (!File.Exists(archivePath) || new FileInfo(archivePath).Length == 0)
            {
                Logs.Error("[AceStep] Download failed — file is empty or missing.");
                return null;
            }
            Logs.Info("[AceStep] Extracting binary...");
            string extractDir = Path.Combine(tempDir, "extracted");
            if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
            if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                ZipFile.ExtractToDirectory(archivePath, extractDir);
            }
            else
            {
                // tar.gz for Linux/macOS
                Directory.CreateDirectory(extractDir);
                await Utilities.QuickRunProcess("tar", ["-xzf", archivePath, "-C", extractDir]);
            }
            CopyExtractedFiles(extractDir, BinaryRoot);
            if (!File.Exists(ServerExecutable))
            {
                Logs.Error($"[AceStep] Expected executable not found after extraction: {ServerExecutable}");
                return null;
            }
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try { Process.Start("chmod", $"+x \"{ServerExecutable}\"")?.WaitForExit(); } catch { }
            }
            return ServerExecutable;
        }
        finally
        {
            try { File.Delete(archivePath); } catch { }
            try { Directory.Delete(Path.Combine(tempDir, "extracted"), true); } catch { }
        }
    }

    /// <summary>Downloads individual binary files from the community-hosted build server.</summary>
    private async Task<bool> DownloadServeurpersoFilesAsync()
    {
        try
        {
            Directory.CreateDirectory(BinaryRoot);
            int downloaded = 0;
            foreach (string fileName in ServeurpersoWinFiles)
            {
                string localPath = Path.Combine(BinaryRoot, fileName);
                if (File.Exists(localPath))
                {
                    downloaded++;
                    continue;
                }
                string url = SERVEURPERSO_WIN_BASE_URL + fileName;
                Logs.Info($"[AceStep] Downloading {fileName}...");
                try
                {
                    await Utilities.DownloadFile(url, localPath, (_, __, ___) => { });
                    if (File.Exists(localPath) && new FileInfo(localPath).Length > 0)
                    {
                        downloaded++;
                    }
                    else
                    {
                        Logs.Warning($"[AceStep] Failed to download {fileName} (empty or missing)");
                    }
                }
                catch (Exception ex)
                {
                    Logs.Warning($"[AceStep] Failed to download {fileName}: {ex.Message}");
                }
            }
            if (File.Exists(ServerExecutable))
            {
                // Get Last-Modified from the server exe for future update checks
                string lastModified = null;
                try
                {
                    using HttpRequestMessage headReq = new(HttpMethod.Head, SERVEURPERSO_WIN_BASE_URL + "ace-server.exe");
                    headReq.Headers.Add("User-Agent", "SwarmUI-AudioLab");
                    using HttpResponseMessage headResp = await Utilities.UtilWebClient.SendAsync(headReq);
                    lastModified = headResp.Content.Headers.LastModified?.ToString("R");
                }
                catch { }
                SaveVersionInfo(new VersionInfo
                {
                    TagName = "serveurperso-latest",
                    Source = "serveurperso",
                    ExecutablePath = ServerExecutable,
                    InstalledDate = DateTime.UtcNow,
                    LastUpdateCheck = DateTime.UtcNow,
                    LastModified = lastModified
                });
                Logs.Info($"[AceStep] Installed ace-server from community build ({downloaded}/{ServeurpersoWinFiles.Length} files)");
                return true;
            }
            Logs.Warning("[AceStep] Community build download incomplete — ace-server.exe not found");
            return false;
        }
        catch (Exception ex)
        {
            Logs.Warning($"[AceStep] Community build download failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Recursively copies extracted files to the target directory.</summary>
    private static void CopyExtractedFiles(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (string file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
        }
        foreach (string dir in Directory.GetDirectories(source))
        {
            CopyExtractedFiles(dir, Path.Combine(target, Path.GetFileName(dir)));
        }
    }

    /// <summary>Loads saved version info from disk, or null if not available.</summary>
    private static VersionInfo LoadVersionInfo()
    {
        try
        {
            if (!File.Exists(VersionFilePath)) return null;
            string json = File.ReadAllText(VersionFilePath);
            return JObject.Parse(json).ToObject<VersionInfo>();
        }
        catch { return null; }
    }

    /// <summary>Checks for a newer ace-server binary and downloads it if the server is not running.
    /// Skips if checked within the last 24 hours.</summary>
    private async Task CheckForUpdateAsync()
    {
        VersionInfo current = LoadVersionInfo();
        if (current is not null && (DateTime.UtcNow - current.LastUpdateCheck).TotalHours < 24)
        {
            return; // Checked recently
        }
        Logs.Debug("[AceStep] Checking for ace-server updates...");
        bool updateAvailable = false;
        // Check serveurperso (Windows) — compare Last-Modified header
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            updateAvailable = await CheckServeurpersoUpdateAsync(current);
        }
        // Check GitHub releases if serveurperso didn't find an update
        if (!updateAvailable)
        {
            updateAvailable = await CheckGitHubUpdateAsync(current);
        }
        if (updateAvailable && !IsServerRunning())
        {
            Logs.Info("[AceStep] Newer binary available, downloading update...");
            await ApplyUpdateAsync();
        }
        else if (updateAvailable)
        {
            Logs.Info("[AceStep] Newer ace-server binary available. Will update on next startup.");
            // Mark that we checked but can't update right now
            if (current is not null)
            {
                current.LastUpdateCheck = DateTime.UtcNow;
                SaveVersionInfo(current);
            }
        }
        else
        {
            // No update — just record that we checked
            if (current is not null)
            {
                current.LastUpdateCheck = DateTime.UtcNow;
                SaveVersionInfo(current);
            }
        }
    }

    /// <summary>Checks serveurperso for a newer build by comparing Last-Modified header.</summary>
    private async Task<bool> CheckServeurpersoUpdateAsync(VersionInfo current)
    {
        try
        {
            using HttpRequestMessage req = new(HttpMethod.Head, SERVEURPERSO_WIN_BASE_URL + "ace-server.exe");
            req.Headers.Add("User-Agent", "SwarmUI-AudioLab");
            using HttpResponseMessage resp = await Utilities.UtilWebClient.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return false;
            string remoteLastModified = resp.Content.Headers.LastModified?.ToString("R");
            if (remoteLastModified is null) return false;
            if (current?.LastModified is null)
            {
                Logs.Debug("[AceStep] No stored Last-Modified, assuming update available.");
                return true;
            }
            if (remoteLastModified != current.LastModified)
            {
                Logs.Info($"[AceStep] Serveurperso binary changed: {current.LastModified} → {remoteLastModified}");
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            Logs.Debug($"[AceStep] Serveurperso update check failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Checks GitHub releases for a newer version tag.</summary>
    private static async Task<bool> CheckGitHubUpdateAsync(VersionInfo current)
    {
        try
        {
            string pattern = GetAssetPattern();
            if (pattern is null) return false;
            string[] repoUrls = [GITHUB_VST3_API_URL, GITHUB_CPP_API_URL];
            foreach (string repoUrl in repoUrls)
            {
                DownloadInfo info = await TryGetReleaseAssetAsync(repoUrl, pattern);
                if (info is not null && info.TagName != current?.TagName)
                {
                    Logs.Info($"[AceStep] GitHub release {info.TagName} available (current: {current?.TagName ?? "unknown"})");
                    return true;
                }
            }
            return false;
        }
        catch (Exception ex)
        {
            Logs.Debug($"[AceStep] GitHub update check failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Downloads the latest binary, replacing the current one.</summary>
    private async Task ApplyUpdateAsync()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Delete existing files so serveurperso download replaces them
                foreach (string fileName in ServeurpersoWinFiles)
                {
                    string path = Path.Combine(BinaryRoot, fileName);
                    try { if (File.Exists(path)) File.Delete(path); } catch { }
                }
                bool downloaded = await DownloadServeurpersoFilesAsync();
                if (downloaded)
                {
                    Logs.Info("[AceStep] ace-server updated successfully from community build.");
                    return;
                }
            }
            // Fallback: try GitHub release
            DownloadInfo info = await GetBinaryDownloadInfo();
            if (info is not null)
            {
                await DownloadAndExtractBinary(info);
                SaveVersionInfo(new VersionInfo
                {
                    TagName = info.TagName,
                    Source = "github",
                    ExecutablePath = ServerExecutable,
                    InstalledDate = DateTime.UtcNow,
                    LastUpdateCheck = DateTime.UtcNow
                });
                Logs.Info($"[AceStep] ace-server updated to {info.TagName}");
            }
        }
        catch (Exception ex)
        {
            Logs.Warning($"[AceStep] Failed to apply update: {ex.Message}");
        }
    }

    #endregion

    #region Model Management

    /// <summary>Ensures required GGUF models are downloaded for the given DiT variant.</summary>
    public async Task EnsureModelsAsync(string ditFileName, string lmModel = "none")
    {
        Directory.CreateDirectory(ModelRoot);
        // Always-required models
        await EnsureSingleModelAsync("vae-BF16.gguf");
        await EnsureSingleModelAsync("Qwen3-Embedding-0.6B-Q8_0.gguf");
        // DiT model
        await EnsureSingleModelAsync(ditFileName);
        // Optional LM model (filename provided directly from filesystem-scanned param)
        if (lmModel != "none" && !string.IsNullOrEmpty(lmModel))
        {
            string lmPath = Path.Combine(ModelRoot, lmModel);
            if (!File.Exists(lmPath))
            {
                Logs.Warning($"[AceStep] LM model not found: {lmModel}");
            }
        }
    }

    /// <summary>Downloads a single GGUF model file if not already present.</summary>
    private async Task EnsureSingleModelAsync(string fileName)
    {
        string localPath = Path.Combine(ModelRoot, fileName);
        if (File.Exists(localPath))
        {
            return;
        }
        string url = HF_MODEL_BASE_URL + fileName;
        Logs.Info($"[AceStep] Downloading model {fileName}...");
        await Utilities.DownloadFile(url, localPath, (current, total, bps) =>
        {
            if (total > 0)
            {
                double pct = current * 100.0 / total;
                double mbps = bps / 1024.0 / 1024.0;
                Logs.Verbose($"[AceStep] {fileName}: {pct:F1}% ({mbps:F1} MB/s)");
            }
        });
        Logs.Info($"[AceStep] Downloaded {fileName}");
    }

    /// <summary>Resolves a DiT model ID + quant level to a GGUF filename.</summary>
    public static string GetDitFileName(string ditModel, string quantLevel = "Q8_0")
    {
        return $"{ditModel}-{quantLevel}.gguf";
    }

    #endregion

    #region Server Lifecycle

    /// <summary>Ensures the ace-server is running. The new binary auto-discovers models in the --models directory
    /// and supports hot-swapping without restart, so we only need to ensure the process is alive.</summary>
    public async Task<bool> EnsureServerRunningAsync(string ditFileName, string lmModel = "none")
    {
        if (IsServerRunning()) return true;
        await _startLock.WaitAsync();
        try
        {
            if (IsServerRunning()) return true;
            string exe = await EnsureBinaryAsync();
            if (exe is null) return false;
            await EnsureModelsAsync(ditFileName, lmModel);
            return await StartServerProcessAsync();
        }
        finally
        {
            _startLock.Release();
        }
    }

    /// <summary>Starts the ace-server process with --models pointing to the GGUF directory.
    /// The new binary auto-discovers all models and supports hot-swapping without restart.</summary>
    private async Task<bool> StartServerProcessAsync()
    {
        _port = NetworkBackendUtils.GetNextPort();
        // Check if any LM model exists in the models directory
        _lmLoaded = Directory.GetFiles(ModelRoot, "acestep-5Hz-lm-*.gguf").Length > 0;
        StringBuilder args = new();
        args.Append($"--host 127.0.0.1 --port {_port}");
        args.Append($" --models \"{ModelRoot}\"");
        Logs.Info($"[AceStep] Starting ace-server on port {_port} with models dir: {ModelRoot}");
        // Clear stderr buffer and critical error from previous session
        lock (_stderrLock) { _recentStderr.Clear(); }
        ClearCriticalError();
        ProcessStartInfo psi = new(ServerExecutable, args.ToString())
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = BinaryRoot
        };
        try
        {
            _serverProcess = Process.Start(psi);
            if (_serverProcess is null || _serverProcess.HasExited)
            {
                Logs.Error("[AceStep] Failed to start ace-server process.");
                return false;
            }
            // Log stdout/stderr on background threads
            _ = Task.Run(() => LogStream(_serverProcess.StandardOutput, "stdout"));
            _ = Task.Run(() => LogStream(_serverProcess.StandardError, "stderr"));
            // Wait for health check
            bool healthy = await WaitForHealthAsync();
            if (!healthy)
            {
                Logs.Error("[AceStep] ace-server failed health check.");
                KillServer();
                return false;
            }
            // Query available models from the server
            await QueryPropsAsync();
            Logs.Info($"[AceStep] ace-server is ready on port {_port} (LM available: {_lmLoaded})");
            return true;
        }
        catch (Exception ex)
        {
            Logs.Error($"[AceStep] Failed to start ace-server: {ex.Message}");
            KillServer();
            return false;
        }
    }

    /// <summary>Queries the /props endpoint to discover available models and cache server configuration.</summary>
    private async Task QueryPropsAsync()
    {
        try
        {
            HttpResponseMessage resp = await _httpClient.GetAsync($"http://127.0.0.1:{_port}/props");
            if (resp.IsSuccessStatusCode)
            {
                string body = await resp.Content.ReadAsStringAsync();
                _serverProps = JObject.Parse(body);
                Logs.Debug($"[AceStep] Server props: {body}");
            }
        }
        catch (Exception ex)
        {
            Logs.Debug($"[AceStep] Failed to query /props: {ex.Message}");
            _serverProps = null;
        }
    }

    /// <summary>Logs output from the ace-server process, buffering stderr and detecting critical errors.</summary>
    private async Task LogStream(StreamReader reader, string label)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                Logs.Debug($"[AceStep:{label}] {line}");
                if (label != "stderr") continue;
                // Buffer recent stderr for crash diagnostics
                lock (_stderrLock)
                {
                    _recentStderr.Enqueue(line);
                    while (_recentStderr.Count > STDERR_BUFFER_SIZE)
                        _recentStderr.Dequeue();
                }
                // Detect critical errors and log at appropriate level
                if (line.Contains("GGML_ASSERT", StringComparison.Ordinal))
                {
                    _lastCriticalError = $"GGML assertion failed: {line}";
                    Logs.Error($"[AceStep] FATAL: {line}");
                }
                else if (line.Contains("failed to initialize CUDA", StringComparison.OrdinalIgnoreCase))
                {
                    _lastCriticalError = "CUDA failed to initialize — running on CPU only, which may cause errors with some models.";
                    Logs.Warning($"[AceStep] {_lastCriticalError}");
                }
                else if (line.Contains("out of memory", StringComparison.OrdinalIgnoreCase))
                {
                    _lastCriticalError = $"Out of memory: {line}";
                    Logs.Error($"[AceStep] {_lastCriticalError}");
                }
            }
        }
        catch { }
    }

    /// <summary>Gets the last N stderr lines for diagnostics. Returns empty string if none.</summary>
    private string GetRecentStderr(int maxLines = 10)
    {
        lock (_stderrLock)
        {
            if (_recentStderr.Count == 0) return "";
            return string.Join("\n", _recentStderr.TakeLast(maxLines));
        }
    }

    /// <summary>Clears the critical error flag (call before each new generation).</summary>
    private void ClearCriticalError() => _lastCriticalError = null;

    /// <summary>Waits for the ace-server /health endpoint to return ok.</summary>
    private async Task<bool> WaitForHealthAsync()
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(HEALTH_CHECK_TIMEOUT_MS);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                HttpResponseMessage resp = await _httpClient.GetAsync($"http://127.0.0.1:{_port}/health");
                if (resp.IsSuccessStatusCode)
                {
                    string body = await resp.Content.ReadAsStringAsync();
                    if (body.Contains("ok", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            catch { }
            await Task.Delay(500);
        }
        return false;
    }

    /// <summary>Checks if the server process is still running.</summary>
    public bool IsServerRunning()
    {
        return _serverProcess is not null && !_serverProcess.HasExited;
    }

    /// <summary>Gracefully shuts down the ace-server.</summary>
    public async Task ShutdownAsync()
    {
        if (_serverProcess is null) return;
        try
        {
            using CancellationTokenSource cts = new(5000);
            await _httpClient.PostAsync($"http://127.0.0.1:{_port}/shutdown",
                new StringContent("{}", Encoding.UTF8, "application/json"), cts.Token);
        }
        catch { }
        await Task.Delay(2000);
        KillServer();
    }

    /// <summary>Force-kills the server process if still running.</summary>
    private void KillServer()
    {
        try
        {
            if (_serverProcess is not null && !_serverProcess.HasExited)
            {
                Utilities.KillProcess(_serverProcess, 3);
            }
        }
        catch { }
        _serverProcess = null;
        _serverProps = null;
    }

    #endregion

    #region Request Processing

    /// <summary>Full processing pipeline: ensure server running, optionally run LM, then synth, return result.</summary>
    public async Task<JObject> ProcessAsync(Dictionary<string, object> args, CancellationToken cancelToken = default)
    {
        string ditFileName = args.TryGetValue("dit_file", out object ditObj) ? ditObj.ToString() : "acestep-v15-turbo-Q8_0.gguf";
        string lmModel = args.TryGetValue("lm_model", out object lmObj) ? lmObj.ToString() : "none";
        // Ensure models are downloaded before starting server
        bool started = await EnsureServerRunningAsync(ditFileName, lmModel);
        if (!started)
        {
            return CreateErrorResponse("Failed to start ace-server. Check logs for details.");
        }
        // If the server crashed on a previous request, clean up so we can restart
        if (_serverProcess is not null && _serverProcess.HasExited)
        {
            Logs.Warning("[AceStep] Server process is dead from a previous crash, will restart.");
            _serverProcess = null;
            _serverProps = null;
            started = await EnsureServerRunningAsync(ditFileName, lmModel);
            if (!started)
            {
                return CreateErrorResponse("Failed to restart ace-server after crash. Check logs for details.");
            }
        }
        ClearCriticalError();
        try
        {
            // Build AceRequest from args — includes dit_model for server model selection
            JObject aceRequest = BuildAceRequest(args);
            aceRequest["dit_model"] = ditFileName;
            // Phase 1: LM enrichment (optional — check if any LM model is available)
            if (_lmLoaded && lmModel != "none" && !string.IsNullOrEmpty(lmModel))
            {
                JObject enriched = await RunLmPhaseAsync(aceRequest, cancelToken);
                if (enriched is not null)
                {
                    aceRequest = enriched;
                }
            }
            // Phase 2: Synth
            byte[] audioBytes = await RunSynthPhaseAsync(aceRequest, args, cancelToken);
            if (audioBytes is null)
            {
                string synthError = BuildCrashDiagnostic("Synthesis returned no audio data.");
                return CreateErrorResponse(synthError);
            }
            string base64Audio = Convert.ToBase64String(audioBytes);
            // Detect format from file magic bytes: MP3 starts with 0xFF 0xFB or "ID3"
            string format = (audioBytes.Length >= 3 && (audioBytes[0] == 0xFF || (audioBytes[0] == 'I' && audioBytes[1] == 'D' && audioBytes[2] == '3')))
                ? "mp3" : "wav";
            return new JObject
            {
                ["success"] = true,
                ["audio_data"] = base64Audio,
                ["output_format"] = format,
                ["sample_rate"] = 48000,
                ["metadata"] = new JObject
                {
                    ["engine"] = "acestep.cpp",
                    ["dit_model"] = ditFileName,
                    ["lm_model"] = lmModel
                }
            };
        }
        catch (TaskCanceledException)
        {
            return CreateErrorResponse("Generation cancelled by user.");
        }
        catch (Exception ex)
        {
            string errorMsg = BuildCrashDiagnostic(ex.Message);
            Logs.Error($"[AceStep] Processing error: {errorMsg}");
            return CreateErrorResponse(errorMsg);
        }
    }

    /// <summary>Builds an AceRequest JSON from the args dictionary.</summary>
    private static JObject BuildAceRequest(Dictionary<string, object> args)
    {
        JObject req = new();
        void Set(string key, object defaultVal)
        {
            if (args.TryGetValue(key, out object val))
                req[key] = JToken.FromObject(val);
            else if (defaultVal is not null)
                req[key] = JToken.FromObject(defaultVal);
        }
        Set("caption", "");
        Set("lyrics", "");
        Set("bpm", 0);
        Set("duration", 0);
        Set("keyscale", "");
        Set("timesignature", "");
        Set("vocal_language", "");
        Set("seed", -1L);
        Set("inference_steps", 0);
        Set("guidance_scale", 0.0);
        Set("shift", 0.0);
        Set("audio_cover_strength", 0.5);
        Set("repainting_start", -1.0);
        Set("repainting_end", -1.0);
        Set("lego", "");
        // LM params
        Set("lm_temperature", 0.85);
        Set("lm_cfg_scale", 2.0);
        Set("lm_top_k", 0);
        Set("lm_top_p", 0.9);
        Set("lm_negative_prompt", "");
        Set("use_cot_caption", true);
        return req;
    }

    /// <summary>Runs the LM enrichment phase: POST to /lm, return enriched request.
    /// Handles both sync (direct JSON result) and async (job-based polling) server versions.</summary>
    private async Task<JObject> RunLmPhaseAsync(JObject aceRequest, CancellationToken cancelToken)
    {
        try
        {
            StringContent content = new(aceRequest.ToString(), Encoding.UTF8, "application/json");
            HttpResponseMessage resp = await _httpClient.PostAsync($"http://127.0.0.1:{_port}/lm", content, cancelToken);
            if (resp.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
            {
                Logs.Warning("[AceStep] LM phase skipped (GPU busy).");
                return null;
            }
            resp.EnsureSuccessStatusCode();
            string body = await resp.Content.ReadAsStringAsync(cancelToken);
            JToken parsed = JToken.Parse(body);
            // Async mode: {"id":"..."} — poll for result
            if (parsed is JObject idObj && idObj["id"] is not null && idObj.Count <= 2)
            {
                string jobId = idObj["id"].ToString();
                JToken result = await PollJobAsync(jobId, cancelToken);
                if (result is null) return null;
                parsed = result;
            }
            // Extract enriched request from result (array or object)
            if (parsed is JArray arr && arr.Count > 0)
            {
                return arr[0] as JObject;
            }
            if (parsed is JObject obj && obj["id"] is null)
            {
                return obj;
            }
            return null;
        }
        catch (Exception ex)
        {
            Logs.Warning($"[AceStep] LM phase failed (continuing without): {ex.Message}");
            return null;
        }
    }

    /// <summary>Runs the synth phase: POST to /synth, return raw audio bytes.
    /// Handles both sync (direct audio response) and async (job-based polling) server versions.</summary>
    private async Task<byte[]> RunSynthPhaseAsync(JObject aceRequest, Dictionary<string, object> args, CancellationToken cancelToken)
    {
        bool hasSourceAudio = args.TryGetValue("src_audio", out object srcAudioObj) && srcAudioObj is string srcAudio && !string.IsNullOrEmpty(srcAudio);
        for (int attempt = 0; attempt <= RETRY_503_MAX; attempt++)
        {
            HttpResponseMessage resp;
            if (hasSourceAudio)
            {
                using MultipartFormDataContent form = new();
                form.Add(new StringContent(aceRequest.ToString(), Encoding.UTF8, "application/json"), "request");
                byte[] audioBytes = Convert.FromBase64String((string)args["src_audio"]);
                form.Add(new ByteArrayContent(audioBytes), "audio", "source.wav");
                resp = await _httpClient.PostAsync($"http://127.0.0.1:{_port}/synth", form, cancelToken);
            }
            else
            {
                StringContent content = new(aceRequest.ToString(), Encoding.UTF8, "application/json");
                resp = await _httpClient.PostAsync($"http://127.0.0.1:{_port}/synth", content, cancelToken);
            }
            if (resp.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
            {
                int delayMs = 2000;
                Logs.Info($"[AceStep] GPU busy (503), retrying in {delayMs}ms...");
                await Task.Delay(delayMs, cancelToken);
                continue;
            }
            resp.EnsureSuccessStatusCode();
            string contentType = resp.Content.Headers.ContentType?.MediaType ?? "";
            // If response is JSON, it's an async job ID — poll for result
            if (contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
            {
                string body = await resp.Content.ReadAsStringAsync(cancelToken);
                JObject result = JObject.Parse(body);
                if (result["error"] is not null)
                {
                    Logs.Error($"[AceStep] Server error: {result["error"]}");
                    return null;
                }
                string jobId = result["id"]?.ToString();
                if (jobId is not null)
                {
                    return await PollJobForBytesAsync(jobId, cancelToken);
                }
                Logs.Error($"[AceStep] Unexpected JSON response: {body}");
                return null;
            }
            // Otherwise it's direct audio bytes (sync server)
            return await resp.Content.ReadAsByteArrayAsync(cancelToken);
        }
        Logs.Error("[AceStep] GPU busy after max retries.");
        return null;
    }

    /// <summary>Submits a JSON job to the specified endpoint. Returns the job ID.</summary>
    private async Task<string> SubmitJobAsync(string endpoint, JObject payload, CancellationToken cancelToken)
    {
        for (int attempt = 0; attempt <= RETRY_503_MAX; attempt++)
        {
            StringContent content = new(payload.ToString(), Encoding.UTF8, "application/json");
            HttpResponseMessage resp = await _httpClient.PostAsync($"http://127.0.0.1:{_port}{endpoint}", content, cancelToken);
            if (resp.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
            {
                // GPU busy — retry after delay
                string retryAfter = resp.Headers.RetryAfter?.Delta?.TotalMilliseconds.ToString() ?? "2000";
                int delayMs = int.TryParse(retryAfter, out int ra) ? Math.Max(ra, 500) : 2000;
                Logs.Info($"[AceStep] GPU busy (503), retrying in {delayMs}ms...");
                await Task.Delay(delayMs, cancelToken);
                continue;
            }
            resp.EnsureSuccessStatusCode();
            string body = await resp.Content.ReadAsStringAsync(cancelToken);
            JObject result = JObject.Parse(body);
            if (result["error"] is not null)
            {
                Logs.Error($"[AceStep] Server error: {result["error"]}");
                return null;
            }
            return result["id"]?.ToString();
        }
        Logs.Error("[AceStep] GPU busy after max retries.");
        return null;
    }

    /// <summary>Polls a job until completion and returns the result as a JToken (for LM phase JSON responses).</summary>
    private async Task<JToken> PollJobAsync(string jobId, CancellationToken cancelToken)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(JOB_TIMEOUT_MS);
        while (DateTime.UtcNow < deadline)
        {
            cancelToken.ThrowIfCancellationRequested();
            HttpResponseMessage statusResp = await _httpClient.GetAsync($"http://127.0.0.1:{_port}/job?id={jobId}", cancelToken);
            string statusBody = await statusResp.Content.ReadAsStringAsync(cancelToken);
            JObject statusObj = JObject.Parse(statusBody);
            string status = statusObj["status"]?.ToString();
            if (status == "done")
            {
                // Fetch result
                HttpResponseMessage resultResp = await _httpClient.GetAsync($"http://127.0.0.1:{_port}/job?id={jobId}&result=1", cancelToken);
                string resultBody = await resultResp.Content.ReadAsStringAsync(cancelToken);
                return JToken.Parse(resultBody);
            }
            if (status == "failed" || status == "cancelled")
            {
                string error = statusObj["error"]?.ToString() ?? status;
                Logs.Error($"[AceStep] Job {jobId} {status}: {error}");
                return null;
            }
            await Task.Delay(JOB_POLL_INTERVAL_MS, cancelToken);
        }
        Logs.Error($"[AceStep] Job {jobId} timed out.");
        return null;
    }

    /// <summary>Polls a job until completion and returns the result as raw bytes (for synth phase audio).</summary>
    private async Task<byte[]> PollJobForBytesAsync(string jobId, CancellationToken cancelToken)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(JOB_TIMEOUT_MS);
        while (DateTime.UtcNow < deadline)
        {
            cancelToken.ThrowIfCancellationRequested();
            HttpResponseMessage statusResp = await _httpClient.GetAsync($"http://127.0.0.1:{_port}/job?id={jobId}", cancelToken);
            string statusBody = await statusResp.Content.ReadAsStringAsync(cancelToken);
            JObject statusObj = JObject.Parse(statusBody);
            string status = statusObj["status"]?.ToString();
            if (status == "done")
            {
                // Fetch audio result as WAV
                HttpResponseMessage resultResp = await _httpClient.GetAsync($"http://127.0.0.1:{_port}/job?id={jobId}&result=1&wav=1", cancelToken);
                return await resultResp.Content.ReadAsByteArrayAsync(cancelToken);
            }
            if (status == "failed" || status == "cancelled")
            {
                string error = statusObj["error"]?.ToString() ?? status;
                Logs.Error($"[AceStep] Job {jobId} {status}: {error}");
                return null;
            }
            await Task.Delay(JOB_POLL_INTERVAL_MS, cancelToken);
        }
        Logs.Error($"[AceStep] Job {jobId} timed out.");
        return null;
    }

    #endregion

    #region Helpers

    private static JObject CreateErrorResponse(string message) => new()
    {
        ["success"] = false,
        ["error"] = message
    };

    /// <summary>Builds a diagnostic error message by checking if the server crashed and including stderr context.</summary>
    private string BuildCrashDiagnostic(string baseMessage)
    {
        bool crashed = _serverProcess is not null && _serverProcess.HasExited;
        string critical = _lastCriticalError;
        if (crashed && critical is not null)
        {
            // Server crashed with a known critical error — give a clear message
            Logs.Error($"[AceStep] Server process crashed. Last critical error: {critical}");
            string stderr = GetRecentStderr(5);
            return $"ace-server crashed: {critical}" + (string.IsNullOrEmpty(stderr) ? "" : $"\nRecent stderr:\n{stderr}");
        }
        if (crashed)
        {
            // Server crashed but we don't know why — include recent stderr
            Logs.Error("[AceStep] Server process crashed unexpectedly.");
            string stderr = GetRecentStderr(10);
            return "ace-server crashed unexpectedly." + (string.IsNullOrEmpty(stderr) ? "" : $"\nRecent stderr:\n{stderr}");
        }
        if (critical is not null)
        {
            // Server still running but a critical error was detected
            return $"{baseMessage} ({critical})";
        }
        return baseMessage;
    }

    private void SaveVersionInfo(VersionInfo info)
    {
        try { File.WriteAllText(VersionFilePath, JObject.FromObject(info).ToString(Newtonsoft.Json.Formatting.Indented)); }
        catch (Exception ex) { Logs.Warning($"[AceStep] Failed to save version info: {ex.Message}"); }
    }

    public void Dispose()
    {
        ShutdownAsync().Wait(5000);
        _httpClient?.Dispose();
        _startLock?.Dispose();
    }

    #endregion

    #region Data Classes

    public class VersionInfo
    {
        public string TagName { get; set; }
        public string Source { get; set; }
        public string ExecutablePath { get; set; }
        public DateTime InstalledDate { get; set; }
        public DateTime LastUpdateCheck { get; set; }
        /// <summary>Last-Modified header from the download source, used for update checking.</summary>
        public string LastModified { get; set; }
    }

    public class DownloadInfo
    {
        public string FileName { get; set; }
        public string DownloadUrl { get; set; }
        public long Size { get; set; }
        public string TagName { get; set; }
    }

    #endregion
}
