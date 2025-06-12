using SwarmUI.Utils;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Hartsy.Extensions.VoiceAssistant.Configuration;
using Hartsy.Extensions.VoiceAssistant.Models;
using Hartsy.Extensions.VoiceAssistant.Progress;
using Newtonsoft.Json.Linq;
using System.IO;

namespace Hartsy.Extensions.VoiceAssistant.Services;

/// <summary>
/// Service for installing and managing Python dependencies for the Voice Assistant.
/// Handles dependency detection, installation with progress tracking, and validation.
/// </summary>
public class DependencyInstaller
{
    private readonly object _installLock = new();
    private volatile bool _isInstalling = false;

    public bool IsInstalling => _isInstalling;

    /// <summary>
    /// Detects the SwarmUI Python environment.
    /// STRICT REQUIREMENT: Only works with SwarmUI's Python environment.
    /// </summary>
    /// <returns>Python environment information or null if not found</returns>
    public PythonEnvironmentInfo DetectPythonEnvironment()
    {
        try
        {
            Logs.Debug("[VoiceAssistant] Detecting SwarmUI Python environment");

            // Try to detect SwarmUI's Python environment
            var pythonPaths = GetPotentialPythonPaths();

            foreach (var pythonPath in pythonPaths)
            {
                if (File.Exists(pythonPath))
                {
                    var info = ValidatePythonEnvironment(pythonPath);
                    if (info != null)
                    {
                        Logs.Info($"[VoiceAssistant] Found SwarmUI Python: {pythonPath}");
                        return info;
                    }
                }
            }

            Logs.Error("[VoiceAssistant] SwarmUI Python environment not found!");
            Logs.Error("[VoiceAssistant] Voice Assistant requires SwarmUI with ComfyUI backend properly installed.");
            return null;
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] Error detecting Python environment: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Checks if all required dependencies are installed.
    /// </summary>
    /// <param name="pythonInfo">Python environment information</param>
    /// <returns>True if all dependencies are installed</returns>
    public async Task<bool> CheckDependenciesInstalledAsync(PythonEnvironmentInfo pythonInfo)
    {
        if (pythonInfo?.IsValid != true)
            return false;

        try
        {
            Logs.Debug("[VoiceAssistant] Checking if dependencies are installed");

            // Check core packages
            var coreInstalled = await CheckPackagesInstalledAsync(pythonInfo, ServiceConfiguration.CorePackages);
            if (!coreInstalled)
            {
                Logs.Debug("[VoiceAssistant] Core packages not fully installed");
                return false;
            }

            // Check STT engine
            var sttInstalled = await CheckSinglePackageAsync(pythonInfo, ServiceConfiguration.PrimarySTTEngine);
            if (!sttInstalled)
            {
                Logs.Debug("[VoiceAssistant] STT engine not installed");
                return false;
            }

            // Check TTS engine (this is more complex as it's from git)
            var ttsInstalled = await CheckChatterboxTTSAsync(pythonInfo);
            if (!ttsInstalled)
            {
                Logs.Debug("[VoiceAssistant] TTS engine not installed");
                return false;
            }

            Logs.Info("[VoiceAssistant] All dependencies are installed");
            return true;
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] Error checking dependencies: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Installs all required dependencies with progress tracking.
    /// </summary>
    /// <param name="pythonInfo">Python environment information</param>
    /// <returns>True if installation succeeded</returns>
    public async Task<bool> InstallDependenciesAsync(PythonEnvironmentInfo pythonInfo)
    {
        if (pythonInfo?.IsValid != true)
        {
            throw new InvalidOperationException("Invalid Python environment");
        }

        lock (_installLock)
        {
            if (_isInstalling)
            {
                throw new InvalidOperationException("Installation already in progress");
            }
            _isInstalling = true;
        }

        try
        {
            Logs.Info("[VoiceAssistant] Starting dependency installation (no fallbacks)");
            var tracker = ProgressTracking.Installation;
            tracker.Reset();

            tracker.UpdateProgress(5, "Starting installation", "Preparing Python environment...");

            // Install core packages
            tracker.UpdateProgress(10, "Installing core packages", "Installing base dependencies...");
            await InstallCorePackagesAsync(pythonInfo, tracker);

            // Install STT engine
            tracker.UpdateProgress(60, "Installing STT engine", ServiceConfiguration.PrimarySTTEngine, 0, "Installing RealtimeSTT (required)...");
            await InstallSTTEngineAsync(pythonInfo, tracker);

            // Install TTS engine
            tracker.UpdateProgress(80, "Installing TTS engine", ServiceConfiguration.PrimaryTTSEngine, 0, "Installing Chatterbox TTS (required)...");
            await InstallTTSEngineAsync(pythonInfo, tracker);

            tracker.SetComplete();
            Logs.Info("[VoiceAssistant] All dependencies installed successfully!");
            return true;
        }
        catch (Exception ex)
        {
            ProgressTracking.Installation.SetError($"Installation failed: {ex.Message}");
            Logs.Error($"[VoiceAssistant] Dependency installation failed: {ex.Message}");
            throw;
        }
        finally
        {
            _isInstalling = false;
        }
    }

    /// <summary>
    /// Gets detailed installation status for all dependencies.
    /// </summary>
    /// <param name="pythonInfo">Python environment information</param>
    /// <returns>Detailed installation status</returns>
    public async Task<JObject> GetDetailedInstallationStatusAsync(PythonEnvironmentInfo pythonInfo)
    {
        var result = new JObject();

        try
        {
            if (pythonInfo?.IsValid != true)
            {
                return result;
            }

            // Check core packages
            var corePackages = new JObject();
            foreach (var package in ServiceConfiguration.CorePackages)
            {
                var packageName = package.Split(">=")[0]; // Remove version specifier
                var installed = await CheckSinglePackageAsync(pythonInfo, packageName);
                corePackages[packageName] = installed;
            }
            result["core_packages"] = corePackages;

            // Check STT packages
            var sttPackages = new JObject();
            var sttInstalled = await CheckSinglePackageAsync(pythonInfo, ServiceConfiguration.PrimarySTTEngine);
            sttPackages[ServiceConfiguration.PrimarySTTEngine] = sttInstalled;
            result["stt_packages"] = sttPackages;

            // Check TTS packages
            var ttsPackages = new JObject();
            var ttsInstalled = await CheckChatterboxTTSAsync(pythonInfo);
            ttsPackages["chatterbox"] = ttsInstalled;
            result["tts_packages"] = ttsPackages;

            return result;
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] Error getting detailed installation status: {ex.Message}");
            return result;
        }
    }

    #region Private Methods

    /// <summary>
    /// Gets potential Python executable paths for SwarmUI.
    /// </summary>
    private List<string> GetPotentialPythonPaths()
    {
        var paths = new List<string>();

        try
        {
            // Windows paths
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                paths.AddRange(new[]
                {
                    Path.Combine("dlbackend", "comfy", "python_embeded", "python.exe"),
                    Path.Combine("dlbackend", "ComfyUI", "venv", "Scripts", "python.exe"),
                    Path.Combine("src", "dlbackend", "comfy", "python_embeded", "python.exe"),
                    Path.Combine("src", "dlbackend", "ComfyUI", "venv", "Scripts", "python.exe")
                });
            }
            else
            {
                // Linux/Mac paths
                paths.AddRange(new[]
                {
                    Path.Combine("dlbackend", "ComfyUI", "venv", "bin", "python"),
                    Path.Combine("dlbackend", "ComfyUI", "venv", "bin", "python3"),
                    Path.Combine("src", "dlbackend", "ComfyUI", "venv", "bin", "python"),
                    Path.Combine("src", "dlbackend", "ComfyUI", "venv", "bin", "python3")
                });
            }

            return paths;
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] Error getting Python paths: {ex.Message}");
            return new List<string>();
        }
    }

    /// <summary>
    /// Validates a Python environment and returns information about it.
    /// </summary>
    private PythonEnvironmentInfo ValidatePythonEnvironment(string pythonPath)
    {
        try
        {
            var info = new PythonEnvironmentInfo
            {
                PythonPath = pythonPath,
                OperatingSystem = Environment.OSVersion.ToString(),
                IsEmbedded = pythonPath.Contains("python_embeded")
            };

            // Test if Python works
            var testScript = "import sys; print(f'{sys.version_info.major}.{sys.version_info.minor}.{sys.version_info.micro}')";
            var version = RunPythonScriptSync(pythonPath, testScript);

            if (!string.IsNullOrEmpty(version))
            {
                info.Version = version.Trim();
                return info;
            }

            return null;
        }
        catch (Exception ex)
        {
            Logs.Debug($"[VoiceAssistant] Python validation failed for {pythonPath}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Installs core packages with progress tracking.
    /// </summary>
    private async Task InstallCorePackagesAsync(PythonEnvironmentInfo pythonInfo, InstallationProgressTracker tracker)
    {
        int baseProgress = 10;
        int progressPerPackage = 50 / ServiceConfiguration.CorePackages.Length;

        for (int i = 0; i < ServiceConfiguration.CorePackages.Length; i++)
        {
            var package = ServiceConfiguration.CorePackages[i];
            var currentProgress = baseProgress + (i * progressPerPackage);

            tracker.UpdateProgress(currentProgress, "Installing core packages", package, 0, $"Installing {package}...");

            await InstallSinglePackageWithProgressAsync(pythonInfo, package, tracker);
            tracker.AddCompletedPackage(package);
        }

        tracker.UpdateProgress(60, "Core packages installed", "All core packages installed successfully");
    }

    /// <summary>
    /// Installs the STT engine.
    /// </summary>
    private async Task InstallSTTEngineAsync(PythonEnvironmentInfo pythonInfo, InstallationProgressTracker tracker)
    {
        await InstallSinglePackageWithProgressAsync(pythonInfo, ServiceConfiguration.PrimarySTTEngine, tracker);
        tracker.AddCompletedPackage(ServiceConfiguration.PrimarySTTEngine);
        tracker.UpdateProgress(80, "STT engine installed", "RealtimeSTT installed successfully");
    }

    /// <summary>
    /// Installs the TTS engine.
    /// </summary>
    private async Task InstallTTSEngineAsync(PythonEnvironmentInfo pythonInfo, InstallationProgressTracker tracker)
    {
        await InstallSinglePackageWithProgressAsync(pythonInfo, ServiceConfiguration.PrimaryTTSEngine, tracker);
        tracker.AddCompletedPackage("chatterbox-tts");
        tracker.UpdateProgress(100, "TTS engine installed", "Chatterbox TTS installed successfully");
    }

    /// <summary>
    /// Installs a single package with detailed progress tracking.
    /// Includes automatic retry for transient failures.
    /// </summary>
    private async Task InstallSinglePackageWithProgressAsync(PythonEnvironmentInfo pythonInfo, string package, InstallationProgressTracker tracker, int retryCount = 0, int maxRetries = 3)
    {
        try
        {
            Logs.Debug($"[VoiceAssistant] Installing package: {package}");

            // Set up environment variables to make git more verbose
            Dictionary<string, string> extraEnv = new Dictionary<string, string>
            {
                // Make git more verbose
                { "GIT_CURL_VERBOSE", "1" },
                { "GIT_TRACE", "1" },
                { "GIT_TRACE_PACKET", "1" },
                // Make pip and git clone show progress
                { "PIP_VERBOSE", "3" } // More verbose pip output
            };

            // Configure with binary packages preferred and max verbosity
            string arguments = $"-m pip install \"{package}\" --no-warn-script-location --progress-bar=on -vvv --prefer-binary";
            
            // For PyTorch packages, add the extra index URL for CUDA-enabled versions
            if (package.Contains("torch") && !package.StartsWith("git+"))
            {
                // Use the PyTorch index for CUDA packages
                arguments += " --extra-index-url https://download.pytorch.org/whl/cu126";
                Logs.Info($"[VoiceAssistant] Using PyTorch CUDA index for package: {package} with CUDA 12.6");
            }
            
            var startInfo = new ProcessStartInfo
            {
                FileName = pythonInfo.PythonPath,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Environment.CurrentDirectory
            };

            // Add our extra environment variables
            foreach (var kvp in extraEnv)
            {
                startInfo.EnvironmentVariables[kvp.Key] = kvp.Value;
            }
            
            // Track if this is a git-based install
            bool isGitInstall = package.StartsWith("git+", StringComparison.OrdinalIgnoreCase);
            if (isGitInstall)
            {
                Logs.Info($"[VoiceAssistant] Installing from Git: {package} (this may take longer and show limited progress)");
            }
            
            // Log command for debugging
            Logs.Info($"[VoiceAssistant] Running pip command: {startInfo.FileName} {startInfo.Arguments}");

            // Clean environment for SwarmUI's Python
            PythonLaunchHelper.CleanEnvironmentOfPythonMess(startInfo, "[VoiceAssistant] ");

            using var process = new Process { StartInfo = startInfo };
            var lastUpdate = DateTime.Now;

            process.OutputDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e?.Data))
                {
                    var line = e.Data;

                    // Parse pip output for meaningful updates
                    if (line.Contains("Downloading") || line.Contains("Installing") ||
                        line.Contains("Collecting") || line.Contains("Building wheel") ||
                        line.Contains("Successfully installed"))
                    {
                        var percentMatch = Regex.Match(line, @"(\d+)%");
                        int downloadPercent = percentMatch.Success ? int.Parse(percentMatch.Groups[1].Value) : 50;

                        string stage = line.Contains("Downloading") ? "Downloading" :
                                     line.Contains("Building") ? "Building" :
                                     line.Contains("Installing") ? "Installing" :
                                     line.Contains("Successfully") ? "Completed" : "Processing";

                        tracker.UpdateProgress(tracker.Progress, $"Installing {package.Split(">=")[0]}",
                                             package, downloadPercent, $"{stage}: {line.Trim()}");
                        lastUpdate = DateTime.Now;
                    }

                    Logs.Debug($"[VoiceAssistant] Pip: {line}");
                }
            };
            
            // Also handle standard error output to catch errors
            StringBuilder errorOutput = new StringBuilder();
            process.ErrorDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e?.Data))
                {
                    errorOutput.AppendLine(e.Data);
                    Logs.Debug($"[VoiceAssistant] Pip Error: {e.Data}");
                    
                    // Log critical errors at warning level so they're more visible
                    if (e.Data.Contains("ERROR:") || e.Data.Contains("fatal:") || 
                        e.Data.Contains("Failed") || e.Data.Contains("Error:"))
                    {
                        Logs.Warning($"[VoiceAssistant] Pip installation issue detected: {e.Data}");
                    }
                    
                    // Update last activity time
                    lastUpdate = DateTime.Now;
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine(); // Make sure we read stderr as well

            // Start a heartbeat task to provide periodic updates during long-running operations
            var tokenSource = new CancellationTokenSource();
            var heartbeatTask = StartHeartbeatUpdatesAsync(package, tracker, lastUpdate, tokenSource.Token);

            try
            {
                // Wait with timeout
                bool finished = await Task.Run(() => process.WaitForExit((int)ServiceConfiguration.PackageInstallTimeout.TotalMilliseconds));

                if (!finished)
                {
                    try { process.Kill(); } catch { }
                    
                    if (retryCount < maxRetries)
                    {
                        Logs.Warning($"[VoiceAssistant] Package installation timed out: {package}. Retrying ({retryCount+1}/{maxRetries})...");
                        // Exponential backoff between retries
                        await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, retryCount)));
                        await InstallSinglePackageWithProgressAsync(pythonInfo, package, tracker, retryCount + 1, maxRetries);
                        return;
                    }
                    
                    throw new TimeoutException($"Package installation timed out after {retryCount + 1} attempts: {package}");
                }

                if (process.ExitCode != 0)
                {
                    var stderr = errorOutput.ToString();
                    Logs.Error($"[VoiceAssistant] Package installation error: {package} - Exit code: {process.ExitCode}");
                    
                    if (retryCount < maxRetries)
                    {
                        Logs.Warning($"[VoiceAssistant] Failed to install {package}. Retrying ({retryCount+1}/{maxRetries})...");
                        // Exponential backoff between retries
                        await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, retryCount)));
                        await InstallSinglePackageWithProgressAsync(pythonInfo, package, tracker, retryCount + 1, maxRetries);
                        return;
                    }
                    
                    throw new InvalidOperationException($"Failed to install {package} after {retryCount + 1} attempts: {stderr}");
                }
            }
            finally
            {
                // Stop the heartbeat task
                tokenSource.Cancel();
                try { await Task.WhenAny(heartbeatTask, Task.Delay(1000)); } catch { }
                tokenSource.Dispose();
            }

            Logs.Debug($"[VoiceAssistant] Successfully installed: {package}");
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] Failed to install {package}: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Checks if multiple packages are installed.
    /// </summary>
    private async Task<bool> CheckPackagesInstalledAsync(PythonEnvironmentInfo pythonInfo, string[] packages)
    {
        try
        {
            foreach (var package in packages)
            {
                var packageName = package.Split(">=")[0]; // Remove version specifier
                if (!await CheckSinglePackageAsync(pythonInfo, packageName))
                {
                    return false;
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            Logs.Debug($"[VoiceAssistant] Error checking packages: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Checks if a single package is installed.
    /// </summary>
    private async Task<bool> CheckSinglePackageAsync(PythonEnvironmentInfo pythonInfo, string packageName)
    {
        try
        {
            var script = $"import importlib.util; print('installed' if importlib.util.find_spec('{packageName}') is not None else 'not_found')";
            var result = await RunPythonScriptAsync(pythonInfo.PythonPath, script);
            return result?.Trim() == "installed";
        }
        catch (Exception ex)
        {
            Logs.Debug($"[VoiceAssistant] Error checking package {packageName}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Provides periodic heartbeat updates during long-running package installations.
    /// Ensures users know the process is still running even when there's no visible pip output.
    /// </summary>
    private async Task StartHeartbeatUpdatesAsync(string package, InstallationProgressTracker tracker, DateTime lastUpdateTime, CancellationToken cancellationToken)
    {
        // Dictionary of known slow packages and operations with time estimates
        var slowOperations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "halo", "Building wheel for halo can take 5-10+ minutes" },
            { "torch", "PyTorch installation can take 10-15+ minutes depending on your network" },
            { "torchaudio", "TorchAudio installation can take 10+ minutes" },
            { "numpy", "NumPy compilation can take several minutes" },
            { "chatterbox", "Chatterbox TTS installation downloads models and can take 15+ minutes" }
        };

        // Get custom message if this is known to be a slow package
        string customTimeMessage = string.Empty;
        foreach (var slowOp in slowOperations)
        {
            if (package.Contains(slowOp.Key, StringComparison.OrdinalIgnoreCase))
            {
                customTimeMessage = slowOp.Value;
                break;
            }
        }

        // Show initial message for slow operations
        if (!string.IsNullOrEmpty(customTimeMessage))
        {
            Logs.Info($"[VoiceAssistant] Note: {customTimeMessage}");
            tracker.UpdateProgress(tracker.Progress, tracker.CurrentStep, package, tracker.DownloadProgress, 
                $"{tracker.StatusMessage} - {customTimeMessage}");
        }

        int heartbeatCount = 0;
        var startTime = DateTime.Now;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(15000, cancellationToken); // Check every 15 seconds
                
                // If more than 15 seconds passed since last update, show a heartbeat
                var timeSinceUpdate = DateTime.Now - lastUpdateTime;
                if (timeSinceUpdate.TotalSeconds > 15)
                {
                    heartbeatCount++;
                    var elapsedMinutes = (DateTime.Now - startTime).TotalMinutes;
                    
                    // Every 4th heartbeat (1 minute) show a more detailed update
                    if (heartbeatCount % 4 == 0)
                    {
                        string message = $"Still working - {elapsedMinutes:F1} minutes elapsed";
                        if (!string.IsNullOrEmpty(customTimeMessage))
                        {
                            message += $" - {customTimeMessage}";
                        }
                        
                        Logs.Info($"[VoiceAssistant] Installation heartbeat: {message}");
                        tracker.UpdateProgress(tracker.Progress, tracker.CurrentStep, package, tracker.DownloadProgress, 
                            $"{tracker.StatusMessage} - {message}");
                    }
                    // Simple heartbeat for other intervals
                    else
                    {
                        Logs.Debug($"[VoiceAssistant] Installation still in progress ({elapsedMinutes:F1} min elapsed) - {package}");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when cancellation is requested
        }
        catch (Exception ex)
        {
            // Just log any errors, don't let heartbeat issues affect the main installation
            Logs.Debug($"[VoiceAssistant] Heartbeat error: {ex.Message}");
        }
    }

    /// <summary>
    /// Checks if Chatterbox TTS is installed (special case for git packages).
    /// </summary>
    private async Task<bool> CheckChatterboxTTSAsync(PythonEnvironmentInfo pythonInfo)
    {
        try
        {
            var script = "import importlib.util; print('installed' if importlib.util.find_spec('chatterbox') is not None else 'not_found')";
            var result = await RunPythonScriptAsync(pythonInfo.PythonPath, script);
            return result?.Trim() == "installed";
        }
        catch (Exception ex)
        {
            Logs.Debug($"[VoiceAssistant] Error checking Chatterbox TTS: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Runs a Python script synchronously.
    /// </summary>
    private string RunPythonScriptSync(string pythonPath, string script)
    {
        try
        {
            var tempScript = Path.GetTempFileName() + ".py";
            File.WriteAllText(tempScript, script);

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = pythonPath,
                    Arguments = $"-s \"{tempScript}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                PythonLaunchHelper.CleanEnvironmentOfPythonMess(startInfo, "[VoiceAssistant] ");

                using var process = new Process { StartInfo = startInfo };
                process.Start();

                if (process.WaitForExit(10000)) // 10 second timeout
                {
                    return process.StandardOutput.ReadToEnd();
                }

                return null;
            }
            finally
            {
                try { File.Delete(tempScript); } catch { }
            }
        }
        catch (Exception ex)
        {
            Logs.Debug($"[VoiceAssistant] Error running Python script: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Runs a Python script asynchronously.
    /// </summary>
    private async Task<string> RunPythonScriptAsync(string pythonPath, string script)
    {
        return await Task.Run(() => RunPythonScriptSync(pythonPath, script));
    }

    #endregion
}
