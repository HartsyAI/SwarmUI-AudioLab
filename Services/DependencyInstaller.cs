using SwarmUI.Utils;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Hartsy.Extensions.VoiceAssistant.WebAPI.Models;
using Hartsy.Extensions.VoiceAssistant.Progress;
using Newtonsoft.Json.Linq;
using System.IO;

namespace Hartsy.Extensions.VoiceAssistant.Services;

/// <summary>Service for installing and managing Python dependencies for the Voice Assistant.</summary>
public class DependencyInstaller
{
    private readonly object _installLock = new();
    private volatile bool _isInstalling = false;
    public bool IsInstalling => _isInstalling;

    /// <summary>Package definition for unified dependency management.</summary>
    public class PackageDefinition
    {
        public string Name { get; set; }
        public string InstallName { get; set; }
        public string ImportName { get; set; }
        public string Category { get; set; }
        public bool IsGitPackage { get; set; }
        public string[] AlternativeNames { get; set; } = [];
        public int EstimatedInstallTimeMinutes { get; set; } = 2;
        public string CustomInstallArgs { get; set; } = "";
    }

    /// <summary>Package installation status result.</summary>
    public class PackageStatus
    {
        public string Name { get; set; }
        public bool IsInstalled { get; set; }
        public string DetectedVersion { get; set; }
        public string Category { get; set; }
        public string Error { get; set; }
    }

    /// <summary>Complete dependency definitions for the Voice Assistant.</summary>
    private static readonly PackageDefinition[] PackageDefinitions =
    [
        // Core packages
        new() { Name = "websockets==15.0.1", InstallName = "websockets==15.0.1", ImportName = "websockets", Category = "core" },
        new() { Name = "scipy==1.15.2", InstallName = "scipy==1.15.2", ImportName = "scipy", Category = "core" },
        new() { Name = "soundfile==0.13.1", InstallName = "soundfile==0.13.1", ImportName = "soundfile", Category = "core" },
        new() { Name = "librosa==0.11.0", InstallName = "librosa==0.11.0", ImportName = "librosa", Category = "core" },
        new() { Name = "halo==0.0.31", InstallName = "halo==0.0.31", ImportName = "halo", Category = "core", EstimatedInstallTimeMinutes = 8 },
        new() { Name = "transformers==4.46.3", InstallName = "transformers==4.46.3", ImportName = "transformers", Category = "core" },
        new() { Name = "diffusers==0.29.0", InstallName = "diffusers==0.29.0", ImportName = "diffusers", Category = "core" },
        new() { Name = "conformer==0.3.2", InstallName = "conformer==0.3.2", ImportName = "conformer", Category = "core" },
        new() { Name = "safetensors==0.5.3", InstallName = "safetensors==0.5.3", ImportName = "safetensors", Category = "core" },
        // PyTorch packages
        new() { Name = "torch==2.6.0+cu126", InstallName = "torch==2.6.0+cu126", ImportName = "torch", Category = "pytorch", EstimatedInstallTimeMinutes = 12, CustomInstallArgs = "--extra-index-url https://download.pytorch.org/whl/cu126" },
        new() { Name = "torchvision==0.21.0+cu126", InstallName = "torchvision==0.21.0+cu126", ImportName = "torchvision", Category = "pytorch", EstimatedInstallTimeMinutes = 10, CustomInstallArgs = "--extra-index-url https://download.pytorch.org/whl/cu126" },
        new() { Name = "torchaudio==2.6.0+cu126", InstallName = "torchaudio==2.6.0+cu126", ImportName = "torchaudio", Category = "pytorch", EstimatedInstallTimeMinutes = 10, CustomInstallArgs = "--extra-index-url https://download.pytorch.org/whl/cu126" },
        // STT engine
        new() { Name = "RealtimeSTT", InstallName = "RealtimeSTT", ImportName = "RealtimeSTT", Category = "stt" },
        // TTS engine
        new() { Name = "chatterbox-tts", InstallName = "git+https://github.com/JarodMica/chatterbox.git", ImportName = "chatterbox", Category = "tts", IsGitPackage = true, EstimatedInstallTimeMinutes = 15, AlternativeNames = ["chatterbox", "resemble", "resemblevoice"] }
    ];

    /// <summary>Gets the SwarmUI Python environment using SwarmUI's established detection logic.</summary>
    /// <returns>Python environment information or null if not found</returns>
    public PythonEnvironmentInfo DetectPythonEnvironment()
    {
        try
        {
            string pythonPath = GetSwarmUIPythonPath();
            if (pythonPath == null)
            {
                Logs.Error("[VoiceAssistant] SwarmUI Python environment not found!");
                Logs.Error("[VoiceAssistant] Voice Assistant requires SwarmUI with ComfyUI backend properly installed.");
                return null;
            }
            return new PythonEnvironmentInfo
            {
                PythonPath = pythonPath,
                OperatingSystem = Environment.OSVersion.ToString(),
                IsEmbedded = pythonPath.Contains("python_embeded"),
                Version = "detected", // We know it works if SwarmUI is running
                IsValid = true
            };
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] Error detecting Python environment: {ex.Message}");
            return null;
        }
    }

    /// <summary>Gets comprehensive status of all packages with a single efficient check.</summary>
    /// <param name="pythonInfo">Python environment information</param>
    /// <returns>Dictionary of package statuses by name</returns>
    public async Task<Dictionary<string, PackageStatus>> GetAllPackageStatusAsync(PythonEnvironmentInfo pythonInfo)
    {
        Dictionary<string, PackageStatus> results = new();
        if (pythonInfo?.IsValid != true)
        {
            return results;
        }
        try
        {
            // Get all installed packages in one call for efficiency
            Dictionary<string, string> installedPackages = await GetInstalledPackagesAsync(pythonInfo);
            // Check each package definition
            foreach (PackageDefinition package in PackageDefinitions)
            {
                PackageStatus status = new()
                {
                    Name = package.Name,
                    Category = package.Category,
                    IsInstalled = false
                };
                // Check primary import name
                if (IsPackageInstalled(package.ImportName, installedPackages))
                {
                    status.IsInstalled = true;
                    status.DetectedVersion = installedPackages.GetValueOrDefault(package.ImportName.ToLower(), "unknown");
                }
                else
                {
                    // Check alternative names
                    foreach (string altName in package.AlternativeNames)
                    {
                        if (IsPackageInstalled(altName, installedPackages))
                        {
                            status.IsInstalled = true;
                            status.DetectedVersion = installedPackages.GetValueOrDefault(altName.ToLower(), "unknown");
                            break;
                        }
                    }
                }
                // Special handling for git packages that might not show up in pip list
                if (!status.IsInstalled && package.IsGitPackage)
                {
                    status.IsInstalled = await CheckGitPackageAsync(pythonInfo, package);
                }
                results[package.Name] = status;
            }
            return results;
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] Error getting package status: {ex.Message}");
            return results;
        }
    }

    /// <summary>Checks if all required dependencies are installed efficiently.</summary>
    /// <param name="pythonInfo">Python environment information</param>
    /// <returns>True if all dependencies are installed</returns>
    public async Task<bool> CheckDependenciesInstalledAsync(PythonEnvironmentInfo pythonInfo)
    {
        if (pythonInfo?.IsValid != true)
            return false;
        try
        {
            Dictionary<string, PackageStatus> statuses = await GetAllPackageStatusAsync(pythonInfo);
            return statuses.Values.All(status => status.IsInstalled);
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] Error checking dependencies: {ex.Message}");
            return false;
        }
    }

    /// <summary>Installs all required dependencies using modern batched approach with efficient progress tracking.</summary>
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
            Logs.Info("[VoiceAssistant] Starting dependency installation using modern batched approach");
            ProgressTracker tracker = ProgressTracking.Installation;
            tracker.Reset();
            tracker.UpdateProgress(5, "Analyzing dependencies", "Checking current installation status...");
            // Get current status
            Dictionary<string, PackageStatus> statuses = await GetAllPackageStatusAsync(pythonInfo);
            List<PackageDefinition> packagesToInstall = PackageDefinitions
                .Where(pkg => !statuses[pkg.Name].IsInstalled)
                .ToList();
            if (packagesToInstall.Count == 0)
            {
                tracker.SetComplete("All dependencies already installed!");
                Logs.Info("[VoiceAssistant] All dependencies already installed!");
                return true;
            }
            Logs.Info($"[VoiceAssistant] Need to install {packagesToInstall.Count} packages");
            // Group packages by category for efficient batched installation
            Dictionary<string, List<PackageDefinition>> packagesByCategory = packagesToInstall
                .GroupBy(p => p.Category)
                .ToDictionary(g => g.Key, g => g.ToList());
            int currentProgress = 10;
            int progressPerCategory = 80 / packagesByCategory.Count;
            foreach (KeyValuePair<string, List<PackageDefinition>> categoryGroup in packagesByCategory)
            {
                string category = categoryGroup.Key;
                List<PackageDefinition> packages = categoryGroup.Value;
                tracker.UpdateProgress(currentProgress, $"Installing {category} packages", $"Installing {packages.Count} {category} packages...");
                await InstallPackageCategoryAsync(pythonInfo, packages, tracker, currentProgress, progressPerCategory);
                currentProgress += progressPerCategory;
            }
            tracker.SetComplete("All dependencies installed successfully!");
            Logs.Info("[VoiceAssistant] All dependencies installed successfully using modern approach!");
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

    /// <summary>Gets detailed installation status for all dependencies efficiently.</summary>
    /// <param name="pythonInfo">Python environment information</param>
    /// <returns>Detailed installation status</returns>
    public async Task<JObject> GetDetailedInstallationStatusAsync(PythonEnvironmentInfo pythonInfo)
    {
        JObject result = new();
        try
        {
            if (pythonInfo?.IsValid != true)
            {
                return result;
            }
            Dictionary<string, PackageStatus> statuses = await GetAllPackageStatusAsync(pythonInfo);
            // Group by category for organized output
            Dictionary<string, JObject> categoryObjects = new();
            foreach (PackageStatus status in statuses.Values)
            {
                if (!categoryObjects.ContainsKey(status.Category))
                {
                    categoryObjects[status.Category] = new JObject();
                }
                JObject packageInfo = new()
                {
                    ["installed"] = status.IsInstalled,
                    ["version"] = status.DetectedVersion ?? "unknown"
                };
                if (!string.IsNullOrEmpty(status.Error))
                {
                    packageInfo["error"] = status.Error;
                }
                categoryObjects[status.Category][status.Name] = packageInfo;
            }
            foreach (KeyValuePair<string, JObject> category in categoryObjects)
            {
                result[$"{category.Key}_packages"] = category.Value;
            }
            return result;
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] Error getting detailed installation status: {ex.Message}");
            return result;
        }
    }

    #region Private Methods

    /// <summary>Gets all installed packages efficiently in a single call.</summary>
    private async Task<Dictionary<string, string>> GetInstalledPackagesAsync(PythonEnvironmentInfo pythonInfo)
    {
        try
        {
            string script = @"
import subprocess, json, sys
try:
    pip_cmd = [sys.executable, '-m', 'pip', 'list', '--format=json']
    pip_output = subprocess.check_output(pip_cmd, stderr=subprocess.STDOUT, universal_newlines=True)
    packages = json.loads(pip_output)
    for pkg in packages:
        print(f'{pkg[""name""].lower()}={pkg[""version""]}')
except Exception as e:
    print(f'ERROR: {e}')
";
            string result = await RunPythonScriptAsync(pythonInfo.PythonPath, script);
            Dictionary<string, string> installedPackages = new();
            if (!string.IsNullOrEmpty(result))
            {
                string[] lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                foreach (string line in lines)
                {
                    if (line.StartsWith("ERROR:")) continue;
                    string[] parts = line.Split('=', 2);
                    if (parts.Length == 2)
                    {
                        installedPackages[parts[0].Trim().ToLower()] = parts[1].Trim();
                    }
                }
            }
            return installedPackages;
        }
        catch (Exception ex)
        {
            Logs.Debug($"[VoiceAssistant] Error getting installed packages: {ex.Message}");
            return new Dictionary<string, string>();
        }
    }

    /// <summary>Checks if a package is installed using normalized name matching.</summary>
    private static bool IsPackageInstalled(string packageName, Dictionary<string, string> installedPackages)
    {
        string normalizedName = packageName.ToLower().Replace("-", "_").Replace("_", "-");
        string altNormalizedName = packageName.ToLower().Replace("-", "_");
        return installedPackages.ContainsKey(packageName.ToLower()) ||
               installedPackages.ContainsKey(normalizedName) ||
               installedPackages.ContainsKey(altNormalizedName) ||
               installedPackages.Keys.Any(key => key.Replace("-", "_") == altNormalizedName || key.Replace("_", "-") == normalizedName);
    }

    /// <summary>Checks git packages using import validation.</summary>
    private async Task<bool> CheckGitPackageAsync(PythonEnvironmentInfo pythonInfo, PackageDefinition package)
    {
        try
        {
            string script = $"import importlib.util; print('installed' if importlib.util.find_spec('{package.ImportName}') is not None else 'not_found')";
            string result = await RunPythonScriptAsync(pythonInfo.PythonPath, script);
            return result?.Trim() == "installed";
        }
        catch (Exception ex)
        {
            Logs.Debug($"[VoiceAssistant] Error checking git package {package.Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Installs packages by category using efficient batching where possible.</summary>
    private async Task InstallPackageCategoryAsync(PythonEnvironmentInfo pythonInfo, List<PackageDefinition> packages, ProgressTracker tracker, int baseProgress, int progressRange)
    {
        // Separate packages that can be batched vs those that need individual installation
        List<PackageDefinition> batchablePackages = packages.Where(p => !p.IsGitPackage && string.IsNullOrEmpty(p.CustomInstallArgs)).ToList();
        List<PackageDefinition> individualPackages = packages.Except(batchablePackages).ToList();
        int currentProgress = baseProgress;
        int progressPerOperation = progressRange / (1 + individualPackages.Count + (batchablePackages.Count > 0 ? 1 : 0));
        // Install batchable packages together for efficiency
        if (batchablePackages.Count > 0)
        {
            tracker.UpdateProgress(currentProgress, $"Installing {batchablePackages.Count} standard packages", "Batch installing standard packages...");
            await InstallPackagesBatchAsync(pythonInfo, batchablePackages, tracker);
            currentProgress += progressPerOperation;
        }
        // Install individual packages that require special handling
        foreach (PackageDefinition package in individualPackages)
        {
            tracker.UpdateProgress(currentProgress, $"Installing {package.Name}", $"Installing {package.Name} (estimated {package.EstimatedInstallTimeMinutes} min)...", package.Name);
            await InstallSinglePackageAsync(pythonInfo, package, tracker);
            currentProgress += progressPerOperation;
        }
    }

    /// <summary>Installs multiple packages in a single pip command for efficiency.</summary>
    private async Task InstallPackagesBatchAsync(PythonEnvironmentInfo pythonInfo, List<PackageDefinition> packages, ProgressTracker tracker)
    {
        try
        {
            string packageList = string.Join(" ", packages.Select(p => $"\"{p.InstallName}\""));
            string arguments = $"-m pip install {packageList} --no-warn-script-location --progress-bar=on -v --prefer-binary";
            await RunPipInstallAsync(pythonInfo, arguments, packages.Select(p => p.Name).ToArray(), tracker);
            foreach (PackageDefinition package in packages)
            {
                tracker.AddCompletedPackage(package.Name);
            }
            Logs.Info($"[VoiceAssistant] Successfully batch installed {packages.Count} packages");
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] Batch installation failed: {ex.Message}");
            throw;
        }
    }

    /// <summary>Installs a single package with custom handling for special cases.</summary>
    private async Task InstallSinglePackageAsync(PythonEnvironmentInfo pythonInfo, PackageDefinition package, ProgressTracker tracker, int retryCount = 0, int maxRetries = 3)
    {
        try
        {
            string arguments = $"-m pip install \"{package.InstallName}\" --no-warn-script-location --progress-bar=on -vvv --prefer-binary";
            if (!string.IsNullOrEmpty(package.CustomInstallArgs))
            {
                arguments += $" {package.CustomInstallArgs}";
            }
            await RunPipInstallAsync(pythonInfo, arguments, [package.Name], tracker, package.EstimatedInstallTimeMinutes);
            tracker.AddCompletedPackage(package.Name);
            Logs.Info($"[VoiceAssistant] Successfully installed: {package.Name}");
        }
        catch (Exception ex)
        {
            if (retryCount < maxRetries)
            {
                Logs.Warning($"[VoiceAssistant] Failed to install {package.Name}. Retrying ({retryCount + 1}/{maxRetries})...");
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, retryCount)));
                await InstallSinglePackageAsync(pythonInfo, package, tracker, retryCount + 1, maxRetries);
                return;
            }
            Logs.Error($"[VoiceAssistant] Failed to install {package.Name} after {retryCount + 1} attempts: {ex.Message}");
            throw;
        }
    }

    /// <summary>Runs pip install command with comprehensive progress tracking and error handling.</summary>
    private async Task RunPipInstallAsync(PythonEnvironmentInfo pythonInfo, string arguments, string[] packageNames, ProgressTracker tracker, int estimatedTimeMinutes = 5)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = pythonInfo.PythonPath,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Environment.CurrentDirectory
        };
        // Use SwarmUI's established environment setup
        if (pythonInfo.IsEmbedded)
        {
            startInfo.Environment["PATH"] = PythonLaunchHelper.ReworkPythonPaths(Path.GetFullPath("./dlbackend/comfy/python_embeded"));
            startInfo.WorkingDirectory = Path.GetFullPath("./dlbackend/comfy/");
        }
        else
        {
            startInfo.Environment["PATH"] = PythonLaunchHelper.ReworkPythonPaths(Path.GetFullPath("./dlbackend/ComfyUI/venv/bin"));
        }
        PythonLaunchHelper.CleanEnvironmentOfPythonMess(startInfo, "[VoiceAssistant] ");
        // Add pip-specific environment variables
        startInfo.EnvironmentVariables["GIT_CURL_VERBOSE"] = "1";
        startInfo.EnvironmentVariables["PIP_VERBOSE"] = "3";
        Logs.Info($"[VoiceAssistant] Installing packages: {string.Join(", ", packageNames)}");
        using Process process = new() { StartInfo = startInfo };
        DateTime lastUpdate = DateTime.Now;
        StringBuilder errorOutput = new();
        process.OutputDataReceived += (sender, e) =>
        {
            if (!string.IsNullOrEmpty(e?.Data))
            {
                string line = e.Data;
                if (line.Contains("Downloading") || line.Contains("Installing") || line.Contains("Collecting") || line.Contains("Building wheel") || line.Contains("Successfully installed"))
                {
                    Match percentMatch = Regex.Match(line, @"(\d+)%");
                    int downloadPercent = percentMatch.Success ? int.Parse(percentMatch.Groups[1].Value) : 50;
                    string stage = line.Contains("Downloading") ? "Downloading" : line.Contains("Building") ? "Building" : line.Contains("Installing") ? "Installing" : line.Contains("Successfully") ? "Completed" : "Processing";
                    tracker.UpdateProgress(tracker.Progress, $"Installing {string.Join(", ", packageNames)}", $"{stage}: {line.Trim()}", string.Join(", ", packageNames), downloadPercent);
                    lastUpdate = DateTime.Now;
                }
                Logs.Debug($"[VoiceAssistant] Pip: {line}");
            }
        };
        process.ErrorDataReceived += (sender, e) =>
        {
            if (!string.IsNullOrEmpty(e?.Data))
            {
                errorOutput.AppendLine(e.Data);
                Logs.Debug($"[VoiceAssistant] Pip Error: {e.Data}");
                if (e.Data.Contains("ERROR:") || e.Data.Contains("fatal:") || e.Data.Contains("Failed") || e.Data.Contains("Error:"))
                {
                    Logs.Warning($"[VoiceAssistant] Pip installation issue detected: {e.Data}");
                }
                lastUpdate = DateTime.Now;
            }
        };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        CancellationTokenSource tokenSource = new();
        Task heartbeatTask = StartHeartbeatAsync(packageNames, tracker, lastUpdate, tokenSource.Token, estimatedTimeMinutes);
        try
        {
            bool finished = await Task.Run(() => process.WaitForExit((int)ServiceConfiguration.PackageInstallTimeout.TotalMilliseconds));
            if (!finished)
            {
                try { process.Kill(); } catch { }
                throw new TimeoutException($"Package installation timed out: {string.Join(", ", packageNames)}");
            }
            if (process.ExitCode != 0)
            {
                string stderr = errorOutput.ToString();
                throw new InvalidOperationException($"Failed to install {string.Join(", ", packageNames)}: {stderr}");
            }
        }
        finally
        {
            tokenSource.Cancel();
            try { await Task.WhenAny(heartbeatTask, Task.Delay(1000)); } catch { }
            tokenSource.Dispose();
        }
    }

    /// <summary>Provides efficient heartbeat updates during long installations.</summary>
    private async Task StartHeartbeatAsync(string[] packageNames, ProgressTracker tracker, DateTime lastUpdateTime, CancellationToken cancellationToken, int estimatedTimeMinutes)
    {
        int heartbeatCount = 0;
        DateTime startTime = DateTime.Now;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(30000, cancellationToken); // Check every 30 seconds for efficiency
                TimeSpan timeSinceUpdate = DateTime.Now - lastUpdateTime;
                if (timeSinceUpdate.TotalSeconds > 30)
                {
                    heartbeatCount++;
                    double elapsedMinutes = (DateTime.Now - startTime).TotalMinutes;
                    if (heartbeatCount % 2 == 0) // Every minute
                    {
                        string message = $"Still installing - {elapsedMinutes:F1}/{estimatedTimeMinutes} min elapsed";
                        Logs.Info($"[VoiceAssistant] Installation progress: {message} - {string.Join(", ", packageNames)}");
                        tracker.UpdateProgress(tracker.Progress, tracker.CurrentStep, message, string.Join(", ", packageNames), tracker.DownloadProgress);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Logs.Debug($"[VoiceAssistant] Heartbeat error: {ex.Message}");
        }
    }

    /// <summary>Gets the SwarmUI Python path using the same logic as SwarmUI's PythonLaunchHelper.</summary>
    private static string GetSwarmUIPythonPath()
    {
        // Use the exact same logic as SwarmUI's PythonLaunchHelper.LaunchGeneric
        if (File.Exists("./dlbackend/comfy/python_embeded/python.exe"))
        {
            return Path.GetFullPath("./dlbackend/comfy/python_embeded/python.exe");
        }
        else if (File.Exists("./dlbackend/ComfyUI/venv/bin/python"))
        {
            return Path.GetFullPath("./dlbackend/ComfyUI/venv/bin/python");
        }
        // Don't fall back to system python for dependency installation - we need SwarmUI's environment
        return null;
    }

    /// <summary>Runs a Python script synchronously using SwarmUI's environment setup.</summary>
    private string RunPythonScriptSync(string pythonPath, string script)
    {
        try
        {
            string tempScript = Path.GetTempFileName() + ".py";
            File.WriteAllText(tempScript, script);
            try
            {
                ProcessStartInfo startInfo = new()
                {
                    FileName = pythonPath,
                    Arguments = $"-s \"{tempScript}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                // Use SwarmUI's environment setup
                bool isEmbedded = pythonPath.Contains("python_embeded");
                if (isEmbedded)
                {
                    startInfo.Environment["PATH"] = PythonLaunchHelper.ReworkPythonPaths(Path.GetFullPath("./dlbackend/comfy/python_embeded"));
                    startInfo.WorkingDirectory = Path.GetFullPath("./dlbackend/comfy/");
                }
                else
                {
                    startInfo.Environment["PATH"] = PythonLaunchHelper.ReworkPythonPaths(Path.GetFullPath("./dlbackend/ComfyUI/venv/bin"));
                }
                PythonLaunchHelper.CleanEnvironmentOfPythonMess(startInfo, "[VoiceAssistant] ");
                using Process process = new() { StartInfo = startInfo };
                process.Start();
                if (process.WaitForExit(10000))
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

    /// <summary>Runs a Python script asynchronously.</summary>
    private async Task<string> RunPythonScriptAsync(string pythonPath, string script)
    {
        return await Task.Run(() => RunPythonScriptSync(pythonPath, script));
    }

    #endregion
}
