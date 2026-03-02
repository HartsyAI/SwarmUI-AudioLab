using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.WebAPI.Models;
using Hartsy.Extensions.AudioLab.Progress;
using Newtonsoft.Json.Linq;
using SwarmUI.Utils;
using System.IO;

namespace Hartsy.Extensions.AudioLab.AudioServices;

/// <summary>Provider-aware dependency installer. Replaces the old DependencyInstaller
/// that had hardcoded STT/TTS package arrays with provider-driven dependency resolution.</summary>
public class AudioDependencyInstaller
{
    private static readonly object _installLock = new();
    private bool _isInstalling;
    private Dictionary<string, string> _cachedInstalledPackages;
    private string _cachedInstalledPackagesKey;

    public bool IsInstalling => _isInstalling;

    /// <summary>Detects the Python environment for the "main" venv group.
    /// Falls back to base Python if the venv hasn't been created yet.</summary>
    public PythonEnvironmentInfo DetectPythonEnvironment()
    {
        try
        {
            // Check if "main" group venv exists
            string venvPython = VenvManager.GetVenvPythonPath("main");
            if (File.Exists(venvPython))
            {
                return new PythonEnvironmentInfo
                {
                    PythonPath = venvPython,
                    OperatingSystem = Environment.OSVersion.ToString(),
                    IsEmbedded = false,
                    Version = "detected",
                };
            }
            // Fall back to base python (for initial setup before venv exists)
            string basePython = VenvManager.GetBasePythonPath();
            if (basePython != null)
            {
                return new PythonEnvironmentInfo
                {
                    PythonPath = basePython,
                    OperatingSystem = Environment.OSVersion.ToString(),
                    IsEmbedded = basePython.Contains("python_embeded"),
                    Version = "detected",
                };
            }
            Logs.Error("[AudioLab] No Python environment found! Install Python 3.10+ or ensure python/python3 is on your system PATH.");
            return null;
        }
        catch (Exception ex)
        {
            Logs.Error($"[AudioLab] Error detecting Python environment: {ex.Message}");
            return null;
        }
    }

    /// <summary>Detects the Python environment for a specific compatibility group.
    /// Creates the group's venv if it doesn't exist yet.</summary>
    public async Task<PythonEnvironmentInfo> DetectPythonEnvironmentForGroupAsync(string group)
    {
        try
        {
            string pythonPath = await VenvManager.Instance.EnsureVenvAsync(group);
            if (pythonPath == null)
            {
                Logs.Error($"[AudioLab] Could not create Python venv for group '{group}'");
                return null;
            }
            return new PythonEnvironmentInfo
            {
                PythonPath = pythonPath,
                OperatingSystem = Environment.OSVersion.ToString(),
                IsEmbedded = false,
                Version = "detected",
            };
        }
        catch (Exception ex)
        {
            Logs.Error($"[AudioLab] Error detecting Python environment for group '{group}': {ex.Message}");
            return null;
        }
    }

    /// <summary>Checks if all dependencies for a specific provider are installed.</summary>
    public async Task<bool> CheckProviderDependenciesAsync(PythonEnvironmentInfo pythonInfo, AudioProviderDefinition provider, bool forceRefresh = false)
    {
        if (pythonInfo?.IsValid != true) return false;
        try
        {
            Dictionary<string, PackageStatus> statuses = await GetProviderPackageStatusAsync(pythonInfo, provider, forceRefresh);
            return statuses.Values.All(s => s.IsInstalled);
        }
        catch (Exception ex)
        {
            Logs.Error($"[AudioLab] Error checking {provider.Name} dependencies: {ex.Message}");
            return false;
        }
    }

    /// <summary>Installs all dependencies for a specific provider.</summary>
    public async Task<bool> InstallProviderDependenciesAsync(PythonEnvironmentInfo pythonInfo, AudioProviderDefinition provider)
    {
        if (pythonInfo?.IsValid != true)
            throw new InvalidOperationException("Invalid Python environment");

        lock (_installLock)
        {
            if (_isInstalling)
                throw new InvalidOperationException("Installation already in progress");
            _isInstalling = true;
        }

        try
        {
            Logs.Info($"[AudioLab] Installing dependencies for {provider.Name}");
            ProgressTracker tracker = ProgressTracking.Installation;
            tracker.Reset();
            tracker.UpdateProgress(5, $"Analyzing {provider.Name} dependencies", "Checking current installation status...");

            Dictionary<string, PackageStatus> statuses = await GetProviderPackageStatusAsync(pythonInfo, provider);
            List<PackageDefinition> toInstall = [.. provider.Dependencies.Where(pkg => !statuses[pkg.Name].IsInstalled)];

            if (toInstall.Count == 0)
            {
                tracker.SetComplete($"All {provider.Name} dependencies already installed!");
                return true;
            }

            Logs.Info($"[AudioLab] Need to install {toInstall.Count} packages for {provider.Name}");

            // Group by category for batched install
            Dictionary<string, List<PackageDefinition>> byCategory = toInstall
                .GroupBy(p => p.Category)
                .ToDictionary(g => g.Key, g => g.ToList());

            int progress = 10;
            int perCategory = 80 / Math.Max(byCategory.Count, 1);

            foreach (KeyValuePair<string, List<PackageDefinition>> group in byCategory)
            {
                tracker.UpdateProgress(progress, $"Installing {provider.Name} {group.Key} packages",
                    $"Installing {group.Value.Count} {group.Key} packages...");
                await InstallPackageCategoryAsync(pythonInfo, group.Value, tracker, progress, perCategory);
                progress += perCategory;
            }

            InvalidatePackageCache();
            tracker.SetComplete($"All {provider.Name} dependencies installed successfully!");
            return true;
        }
        catch (Exception ex)
        {
            ProgressTracking.Installation.SetError($"{provider.Name} installation failed: {ex.Message}");
            Logs.Error($"[AudioLab] {provider.Name} dependency installation failed: {ex.Message}");
            throw;
        }
        finally
        {
            _isInstalling = false;
        }
    }

    /// <summary>Installs dependencies for multiple providers, deduplicating shared packages.</summary>
    public async Task<bool> InstallMultipleProviderDependenciesAsync(PythonEnvironmentInfo pythonInfo, IEnumerable<AudioProviderDefinition> providers)
    {
        if (pythonInfo?.IsValid != true)
            throw new InvalidOperationException("Invalid Python environment");

        lock (_installLock)
        {
            if (_isInstalling)
                throw new InvalidOperationException("Installation already in progress");
            _isInstalling = true;
        }

        try
        {
            ProgressTracker tracker = ProgressTracking.Installation;
            tracker.Reset();

            // Deduplicate packages by ImportName
            Dictionary<string, PackageDefinition> uniquePackages = [];
            foreach (AudioProviderDefinition provider in providers)
            {
                foreach (PackageDefinition dep in provider.Dependencies)
                {
                    if (!uniquePackages.ContainsKey(dep.ImportName))
                    {
                        uniquePackages[dep.ImportName] = dep;
                    }
                }
            }

            Dictionary<string, string> installed = await GetInstalledPackagesAsync(pythonInfo);
            List<PackageDefinition> toInstall = [.. uniquePackages.Values.Where(p => !IsPackageInstalled(p.ImportName, installed))];

            if (toInstall.Count == 0)
            {
                tracker.SetComplete("All dependencies already installed!");
                return true;
            }

            Dictionary<string, List<PackageDefinition>> byCategory = toInstall
                .GroupBy(p => p.Category)
                .ToDictionary(g => g.Key, g => g.ToList());

            int progress = 5;
            int perCategory = 90 / Math.Max(byCategory.Count, 1);

            foreach (KeyValuePair<string, List<PackageDefinition>> group in byCategory)
            {
                tracker.UpdateProgress(progress, $"Installing {group.Key} packages",
                    $"Installing {group.Value.Count} {group.Key} packages...");
                await InstallPackageCategoryAsync(pythonInfo, group.Value, tracker, progress, perCategory);
                progress += perCategory;
            }

            InvalidatePackageCache();
            tracker.SetComplete("All dependencies installed successfully!");
            return true;
        }
        catch (Exception ex)
        {
            ProgressTracking.Installation.SetError($"Installation failed: {ex.Message}");
            throw;
        }
        finally
        {
            _isInstalling = false;
        }
    }

    /// <summary>Gets package status for a provider's dependencies.</summary>
    public async Task<Dictionary<string, PackageStatus>> GetProviderPackageStatusAsync(
        PythonEnvironmentInfo pythonInfo, AudioProviderDefinition provider, bool forceRefresh = false)
    {
        Dictionary<string, PackageStatus> results = [];
        if (pythonInfo?.IsValid != true) return results;

        try
        {
            Dictionary<string, string> installed = await GetInstalledPackagesAsync(pythonInfo, forceRefresh);

            foreach (PackageDefinition package in provider.Dependencies)
            {
                PackageStatus status = new()
                {
                    Name = package.Name,
                    Category = package.Category,
                    IsInstalled = false
                };

                if (IsPackageInstalled(package.ImportName, installed))
                {
                    status.IsInstalled = true;
                    status.DetectedVersion = installed.GetValueOrDefault(package.ImportName.ToLower(), "unknown");
                }
                else
                {
                    foreach (string alt in package.AlternativeNames)
                    {
                        if (IsPackageInstalled(alt, installed))
                        {
                            status.IsInstalled = true;
                            status.DetectedVersion = installed.GetValueOrDefault(alt.ToLower(), "unknown");
                            break;
                        }
                    }
                }

                if (!status.IsInstalled && package.IsGitPackage)
                {
                    status.IsInstalled = await CheckGitPackageAsync(pythonInfo, package);
                }

                results[package.Name] = status;
            }
        }
        catch (Exception ex)
        {
            Logs.Error($"[AudioLab] Error getting {provider.Name} package status: {ex.Message}");
        }
        return results;
    }

    // -- Existing install infrastructure (kept from DependencyInstaller) ----

    private void InvalidatePackageCache()
    {
        _cachedInstalledPackages = null;
        _cachedInstalledPackagesKey = null;
    }

    private async Task<Dictionary<string, string>> GetInstalledPackagesAsync(PythonEnvironmentInfo pythonInfo, bool forceRefresh = false)
    {
        if (_cachedInstalledPackages != null && !forceRefresh && _cachedInstalledPackagesKey == pythonInfo.PythonPath)
            return _cachedInstalledPackages;

        try
        {
            string script = @"import sys, json
try:
    import importlib.metadata
    distributions = list(importlib.metadata.distributions())
    package_data = {}
    for dist in distributions:
        try:
            package_data[dist.metadata['Name'].lower()] = dist.version
        except Exception:
            pass
    print('PACKAGE_LIST_START')
    print(json.dumps(package_data))
    print('PACKAGE_LIST_END')
except Exception as e:
    print(f'CRITICAL_ERROR: {e}')
";
            string result = await RunPythonScriptAsync(pythonInfo.PythonPath, script);
            Dictionary<string, string> packages = [];

            if (!string.IsNullOrEmpty(result) && result.Contains("PACKAGE_LIST_START") && result.Contains("PACKAGE_LIST_END"))
            {
                int start = result.IndexOf("PACKAGE_LIST_START") + "PACKAGE_LIST_START".Length;
                int end = result.IndexOf("PACKAGE_LIST_END");
                string json = result[start..end].Trim();
                packages = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, string>>(json) ?? [];
            }

            _cachedInstalledPackages = packages;
            _cachedInstalledPackagesKey = pythonInfo.PythonPath;
            return packages;
        }
        catch (Exception ex)
        {
            Logs.Error($"[AudioLab] Error getting installed packages: {ex.Message}");
            return [];
        }
    }

    private static bool IsPackageInstalled(string packageName, Dictionary<string, string> installed)
    {
        string norm = packageName.ToLower().Replace("-", "_").Replace("_", "-");
        string alt = packageName.ToLower().Replace("-", "_");
        return installed.ContainsKey(packageName.ToLower()) ||
               installed.ContainsKey(norm) ||
               installed.ContainsKey(alt) ||
               installed.Keys.Any(k => k.Replace("-", "_") == alt || k.Replace("_", "-") == norm);
    }

    private async Task<bool> CheckGitPackageAsync(PythonEnvironmentInfo pythonInfo, PackageDefinition package)
    {
        try
        {
            string script = $"import importlib.util; print('installed' if importlib.util.find_spec('{package.ImportName}') is not None else 'not_found')";
            string result = await RunPythonScriptAsync(pythonInfo.PythonPath, script);
            if (result?.Trim() == "installed") return true;

            foreach (string alt in package.AlternativeNames)
            {
                script = $"import importlib.util; print('installed' if importlib.util.find_spec('{alt}') is not None else 'not_found')";
                result = await RunPythonScriptAsync(pythonInfo.PythonPath, script);
                if (result?.Trim() == "installed") return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            Logs.Debug($"[AudioLab] Error checking git package {package.Name}: {ex.Message}");
            return false;
        }
    }

    private static async Task InstallPackageCategoryAsync(PythonEnvironmentInfo pythonInfo, List<PackageDefinition> packages, ProgressTracker tracker, int baseProgress, int progressRange)
    {
        List<PackageDefinition> batchable = [.. packages.Where(p => !p.IsGitPackage && string.IsNullOrEmpty(p.CustomInstallArgs))];
        List<PackageDefinition> individual = [.. packages.Except(batchable)];

        int progress = baseProgress;
        int perOp = progressRange / Math.Max(1 + individual.Count + (batchable.Count > 0 ? 1 : 0), 1);

        if (batchable.Count > 0)
        {
            tracker.UpdateProgress(progress, $"Batch installing {batchable.Count} packages", "");
            await InstallPackagesBatchAsync(pythonInfo, batchable, tracker);
            progress += perOp;
        }

        foreach (PackageDefinition pkg in individual)
        {
            tracker.UpdateProgress(progress, $"Installing {pkg.Name}", $"Installing {pkg.Name}...", pkg.Name);
            await InstallSinglePackageAsync(pythonInfo, pkg, tracker);
            progress += perOp;
        }
    }

    private static async Task InstallPackagesBatchAsync(PythonEnvironmentInfo pythonInfo, List<PackageDefinition> packages, ProgressTracker tracker)
    {
        string packageList = string.Join(" ", packages.Select(p => $"\"{p.InstallName}\""));
        string arguments = $"-m pip install {packageList} --no-warn-script-location --progress-bar=on -v --prefer-binary";
        await RunPipInstallAsync(pythonInfo, arguments, [.. packages.Select(p => p.Name)], tracker);
        foreach (PackageDefinition p in packages) tracker.AddCompletedPackage(p.Name);
    }

    private static async Task InstallSinglePackageAsync(PythonEnvironmentInfo pythonInfo, PackageDefinition package, ProgressTracker tracker, int retry = 0, int maxRetries = 3)
    {
        try
        {
            string arguments = $"-m pip install \"{package.InstallName}\" --no-warn-script-location --progress-bar=on -vvv --prefer-binary";
            if (!string.IsNullOrEmpty(package.CustomInstallArgs))
                arguments += $" {package.CustomInstallArgs}";
            await RunPipInstallAsync(pythonInfo, arguments, [package.Name], tracker, package.EstimatedInstallTimeMinutes);
            tracker.AddCompletedPackage(package.Name);
        }
        catch (Exception)
        {
            if (retry < maxRetries)
            {
                Logs.Warning($"[AudioLab] Retrying {package.Name} ({retry + 1}/{maxRetries})...");
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, retry)));
                await InstallSinglePackageAsync(pythonInfo, package, tracker, retry + 1, maxRetries);
                return;
            }
            throw;
        }
    }

    private static async Task RunPipInstallAsync(PythonEnvironmentInfo pythonInfo, string arguments, string[] packageNames, ProgressTracker tracker, int estMinutes = 5)
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

        // Derive PATH from the python executable's own directory
        string pythonDir = Path.GetDirectoryName(Path.GetFullPath(pythonInfo.PythonPath));
        startInfo.Environment["PATH"] = PythonLaunchHelper.ReworkPythonPaths(pythonDir);
        PythonLaunchHelper.CleanEnvironmentOfPythonMess(startInfo, "[AudioLab] ");

        // Use a short temp path to avoid Windows 260-char path limit during pip extraction.
        // Must override TMPDIR too — SwarmUI sets it globally (Program.cs) and Python's
        // tempfile module checks TMPDIR before TMP/TEMP on all platforms.
        string shortTmp = Path.Combine(Path.GetPathRoot(Environment.CurrentDirectory) ?? "C:\\", "tmp", "audiolab");
        Directory.CreateDirectory(shortTmp);
        startInfo.Environment["TMP"] = shortTmp;
        startInfo.Environment["TEMP"] = shortTmp;
        startInfo.Environment["TMPDIR"] = shortTmp;

        using Process process = new() { StartInfo = startInfo };
        DateTime lastUpdate = DateTime.Now;
        StringBuilder errorOutput = new();

        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e?.Data))
            {
                string line = e.Data;
                if (line.Contains("Downloading") || line.Contains("Installing") || line.Contains("Collecting") || line.Contains("Building wheel") || line.Contains("Successfully installed"))
                {
                    Match pct = Regex.Match(line, @"(\d+)%");
                    int dlPct = pct.Success ? int.Parse(pct.Groups[1].Value) : 50;
                    tracker.UpdateProgress(tracker.Progress, $"Installing {string.Join(", ", packageNames)}", line.Trim(), string.Join(", ", packageNames), dlPct);
                    lastUpdate = DateTime.Now;
                }
                Logs.Debug($"[AudioLab] Pip: {line}");
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e?.Data))
            {
                errorOutput.AppendLine(e.Data);
                lastUpdate = DateTime.Now;
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        bool finished = await Task.Run(() => process.WaitForExit((int)AudioConfiguration.PackageInstallTimeout.TotalMilliseconds));
        if (!finished)
        {
            try { process.Kill(); } catch { }
            throw new TimeoutException($"Package installation timed out: {string.Join(", ", packageNames)}");
        }
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Failed to install {string.Join(", ", packageNames)}: {errorOutput}");
        }
    }

    private async Task<string> RunPythonScriptAsync(string pythonPath, string script)
    {
        return await Task.Run(() =>
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

                    // Derive PATH from the python executable's own directory
                    string pythonDir = Path.GetDirectoryName(Path.GetFullPath(pythonPath));
                    startInfo.Environment["PATH"] = PythonLaunchHelper.ReworkPythonPaths(pythonDir);
                    PythonLaunchHelper.CleanEnvironmentOfPythonMess(startInfo, "[AudioLab] ");

                    using Process process = new() { StartInfo = startInfo };
                    StringBuilder output = new();
                    process.OutputDataReceived += (_, e) => { if (e.Data != null) output.AppendLine(e.Data); };
                    process.Start();
                    process.BeginOutputReadLine();

                    return process.WaitForExit(10000) ? output.ToString().Trim() : null;
                }
                finally
                {
                    try { File.Delete(tempScript); } catch { }
                }
            }
            catch (Exception ex)
            {
                Logs.Debug($"[AudioLab] Error running Python script: {ex.Message}");
                return null;
            }
        });
    }
}
