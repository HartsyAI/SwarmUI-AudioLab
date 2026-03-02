using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using SwarmUI.Utils;

namespace Hartsy.Extensions.AudioLab.AudioServices;

/// <summary>Manages per-compatibility-group Python virtual environments.
/// Creates venvs from a base Python, validates them, and resolves
/// the correct python executable path for each compatibility group.
/// Venvs live under {ExtensionDirectory}/python_backend/venvs/{group}/.</summary>
public class VenvManager
{
    private static readonly Lazy<VenvManager> InstanceLazy = new(() => new VenvManager());
    public static VenvManager Instance => InstanceLazy.Value;

    /// <summary>Per-group locks to prevent concurrent venv creation for the same group.</summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _groupLocks = new();

    /// <summary>Cache of validated venv python paths (group -> python executable path).</summary>
    private readonly ConcurrentDictionary<string, string> _venvPythonPaths = new();

    /// <summary>Root directory for all venvs.
    /// On Windows, uses a short path (e.g. C:\audiolab-venvs) to avoid the 260-char path limit.
    /// Packages like onnx have deeply nested test directories that exceed 260 chars
    /// when combined with a long venv base path.</summary>
    public static string VenvRoot
    {
        get
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                string drive = Path.GetPathRoot(Environment.CurrentDirectory) ?? "C:\\";
                return Path.Combine(drive, "audiolab-venvs");
            }
            return Path.Combine(AudioConfiguration.PythonBackendDirectory, "venvs");
        }
    }

    /// <summary>Gets the venv directory for a specific group.</summary>
    public static string GetVenvDirectory(string group) => Path.Combine(VenvRoot, group);

    /// <summary>Gets the Python executable path inside a group's venv.</summary>
    public static string GetVenvPythonPath(string group)
    {
        string venvDir = GetVenvDirectory(group);
        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? Path.Combine(venvDir, "Scripts", "python.exe")
            : Path.Combine(venvDir, "bin", "python");
    }

    /// <summary>Ensures a venv exists for the given group. Creates it if needed.
    /// Returns the python executable path inside the venv, or null on failure.</summary>
    public async Task<string> EnsureVenvAsync(string group)
    {
        // Check cache first
        if (_venvPythonPaths.TryGetValue(group, out string cachedPath) && File.Exists(cachedPath))
            return cachedPath;

        SemaphoreSlim groupLock = _groupLocks.GetOrAdd(group, _ => new SemaphoreSlim(1, 1));
        await groupLock.WaitAsync();
        try
        {
            // Double-check after lock
            string pythonPath = GetVenvPythonPath(group);
            if (File.Exists(pythonPath))
            {
                _venvPythonPaths[group] = pythonPath;
                return pythonPath;
            }

            // Create the venv
            bool created = await CreateVenvAsync(group);
            if (!created) return null;

            _venvPythonPaths[group] = pythonPath;
            return pythonPath;
        }
        finally
        {
            groupLock.Release();
        }
    }

    /// <summary>Creates a new venv for the given group using the base Python.</summary>
    private async Task<bool> CreateVenvAsync(string group)
    {
        string basePython = GetBasePythonPath();
        if (basePython == null)
        {
            Logs.Error("[AudioLab/VenvManager] System Python not found! AudioLab requires Python 3.10+ on your PATH to create virtual environments. Download from https://www.python.org/downloads/ (check 'Add python.exe to PATH' during install), then restart SwarmUI.");
            return false;
        }

        string venvDir = GetVenvDirectory(group);
        Logs.Info($"[AudioLab/VenvManager] Creating venv for group '{group}' at {venvDir} using {basePython}");

        Directory.CreateDirectory(Path.GetDirectoryName(venvDir));

        ProcessStartInfo psi = new()
        {
            FileName = basePython,
            Arguments = $"-m venv --clear \"{venvDir}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = AudioConfiguration.PythonBackendDirectory
        };
        PythonLaunchHelper.CleanEnvironmentOfPythonMess(psi, $"[AudioLab/Venv/{group}] ");

        using Process proc = new() { StartInfo = psi };
        StringBuilder stderr = new();
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

        proc.Start();
        proc.BeginErrorReadLine();

        bool exited = await Task.Run(() => proc.WaitForExit(120_000)); // 2 min timeout
        if (!exited)
        {
            try { proc.Kill(); } catch { }
            Logs.Error($"[AudioLab/VenvManager] Venv creation timed out for group '{group}'");
            return false;
        }

        if (proc.ExitCode != 0)
        {
            Logs.Error($"[AudioLab/VenvManager] Venv creation failed (exit {proc.ExitCode}): {stderr}");
            return false;
        }

        // Verify the python executable was created
        string venvPython = GetVenvPythonPath(group);
        if (!File.Exists(venvPython))
        {
            Logs.Error($"[AudioLab/VenvManager] Venv created but python not found at {venvPython}");
            return false;
        }

        // Write pip config so ALL pip operations in this venv resolve torch from the CUDA index.
        // Without this, transitive torch dependencies (e.g. "bark" requires "torch") pull in
        // CPU-only torch from PyPI, overriding our explicitly installed CUDA version.
        WritePipConfig(venvDir);

        Logs.Info($"[AudioLab/VenvManager] Venv for group '{group}' created successfully");
        return true;
    }

    /// <summary>Validates that a venv is intact (python executable exists).</summary>
    public static bool ValidateVenv(string group) => File.Exists(GetVenvPythonPath(group));

    /// <summary>Deletes a venv for recreation (e.g., after corruption).</summary>
    public void DeleteVenv(string group)
    {
        string venvDir = GetVenvDirectory(group);
        if (Directory.Exists(venvDir))
        {
            Directory.Delete(venvDir, recursive: true);
            _venvPythonPaths.TryRemove(group, out _);
            Logs.Info($"[AudioLab/VenvManager] Deleted venv for group '{group}'");
        }
    }

    // TODO: Find a better solution for Python bootstrapping. Currently requires system Python
    // because ComfyUI's embedded Python doesn't include the 'venv' module. Options to explore:
    // - Use 'virtualenv' package (works with embedded Python, unlike 'venv')
    // - Download a standalone Python embeddable zip from python.org automatically
    // - Bundle a minimal Python with AudioLab

    /// <summary>Finds the base Python used to create venvs.
    /// Requires system Python 3.10+ on PATH. ComfyUI's embedded Python cannot create
    /// venvs (missing venv module), so it is not used.</summary>
    public static string GetBasePythonPath()
    {
        // System Python (required — ComfyUI's embedded Python lacks the venv module)
        string systemPython = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "python" : "python3";
        if (TryFindSystemPython(systemPython, out string systemPath))
            return systemPath;
        // On Windows also try python3
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && TryFindSystemPython("python3", out string py3Path))
            return py3Path;

        Logs.Error("[AudioLab/VenvManager] System Python not found! AudioLab requires Python 3.10+ installed on your system PATH to create isolated virtual environments. Download from https://www.python.org/downloads/ and ensure 'Add python.exe to PATH' is checked during installation. Then restart SwarmUI.");
        return null;
    }

    /// <summary>Writes a pip config file into the venv so the PyTorch CUDA index is always available.
    /// This prevents transitive dependencies from pulling in CPU-only torch from PyPI.</summary>
    private static void WritePipConfig(string venvDir)
    {
        try
        {
            string configName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "pip.ini" : "pip.conf";
            string configPath = Path.Combine(venvDir, configName);
            string content = "[global]\nextra-index-url = https://download.pytorch.org/whl/cu126\n";
            File.WriteAllText(configPath, content);
            Logs.Debug($"[AudioLab/VenvManager] Wrote {configName} with PyTorch CUDA index");
        }
        catch (Exception ex)
        {
            Logs.Warning($"[AudioLab/VenvManager] Failed to write pip config: {ex.Message}");
        }
    }

    /// <summary>Checks if a Python command is available on the system PATH.</summary>
    private static bool TryFindSystemPython(string command, out string resolvedPath)
    {
        resolvedPath = null;
        try
        {
            ProcessStartInfo psi = new()
            {
                FileName = command,
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using Process proc = new() { StartInfo = psi };
            proc.Start();
            string output = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
            bool exited = proc.WaitForExit(5000);
            if (exited && proc.ExitCode == 0 && output.Contains("Python"))
            {
                resolvedPath = command;
                Logs.Debug($"[AudioLab/VenvManager] Found system Python: {command} ({output.Trim()})");
                return true;
            }
        }
        catch { /* Not found on PATH */ }
        return false;
    }
}
