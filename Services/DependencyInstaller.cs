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
    /// </summary>
    private async Task InstallSinglePackageWithProgressAsync(PythonEnvironmentInfo pythonInfo, string package, InstallationProgressTracker tracker)
    {
        try
        {
            Logs.Debug($"[VoiceAssistant] Installing package: {package}");

            var startInfo = new ProcessStartInfo
            {
                FileName = pythonInfo.PythonPath,
                Arguments = $"-m pip install \"{package}\" --no-warn-script-location --progress-bar=off -v",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Environment.CurrentDirectory
            };

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

            process.Start();
            process.BeginOutputReadLine();

            // Wait with timeout
            bool finished = await Task.Run(() => process.WaitForExit((int)ServiceConfiguration.PackageInstallTimeout.TotalMilliseconds));

            if (!finished)
            {
                try { process.Kill(); } catch { }
                throw new TimeoutException($"Package installation timed out: {package}");
            }

            if (process.ExitCode != 0)
            {
                var stderr = await process.StandardError.ReadToEndAsync();
                throw new InvalidOperationException($"Failed to install {package}: {stderr}");
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
