using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using SwarmUI.Utils;

namespace Hartsy.Extensions.AudioLab.AudioServices;

/// <summary>Manages per-compatibility-group Python virtual environments.
/// Creates venvs from a base Python, validates them, and resolves
/// the correct python executable path for each compatibility group.
/// Venvs live under dlbackend/audiolab/{group}/.
/// Uses SwarmUI's bundled Python (ComfyUI embedded/venv) when available,
/// falling back to system Python. Supports 'virtualenv' fallback for
/// embedded Python distributions that lack the 'venv' module.</summary>
public class VenvManager
{
    #region Fields

    private static readonly Lazy<VenvManager> InstanceLazy = new(() => new VenvManager());
    public static VenvManager Instance => InstanceLazy.Value;

    /// <summary>Per-group locks to prevent concurrent venv creation for the same group.</summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _groupLocks = new();

    /// <summary>Cache of validated venv python paths (group -> python executable path).</summary>
    private readonly ConcurrentDictionary<string, string> _venvPythonPaths = new();

    #endregion

    #region Path Resolution

    /// <summary>Root directory for all venvs, inside dlbackend/ to keep everything
    /// within the SwarmUI folder. Nothing should ever be created outside the main folder.</summary>
    public static string VenvRoot => Path.GetFullPath("dlbackend/audiolab");

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

    #endregion

    #region Venv Lifecycle

    /// <summary>Ensures a venv exists for the given group. Creates it if needed.
    /// Returns the python executable path inside the venv, or null on failure.</summary>
    public async Task<string> EnsureVenvAsync(string group)
    {
        if (_venvPythonPaths.TryGetValue(group, out string cachedPath) && File.Exists(cachedPath))
            return cachedPath;

        SemaphoreSlim groupLock = _groupLocks.GetOrAdd(group, _ => new SemaphoreSlim(1, 1));
        await groupLock.WaitAsync();
        try
        {
            string pythonPath = GetVenvPythonPath(group);
            if (File.Exists(pythonPath))
            {
                _venvPythonPaths[group] = pythonPath;
                return pythonPath;
            }

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
            Logs.Error("[AudioLab] No Python found! Install a SwarmUI backend with ComfyUI, or install Python 3.10+ from https://www.python.org/downloads/ (check 'Add python.exe to PATH'), then restart SwarmUI.");
            return false;
        }

        string venvDir = GetVenvDirectory(group);
        Logs.Info($"[AudioLab] Creating venv for group '{group}' at {venvDir} using {basePython}");

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
        PythonLaunchHelper.CleanEnvironmentOfPythonMess(psi, $"[AudioLab] ");

        using Process proc = new() { StartInfo = psi };
        StringBuilder stderr = new();
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

        proc.Start();
        proc.BeginErrorReadLine();

        bool exited = await Task.Run(() => proc.WaitForExit(120_000)); // 2 min timeout
        if (!exited)
        {
            try { proc.Kill(); } catch { }
            Logs.Error($"[AudioLab] Venv creation timed out for group '{group}'");
            return false;
        }

        if (proc.ExitCode != 0)
        {
            // 'python -m venv' failed — common with ComfyUI's embedded Python (no venv module).
            // Fall back to 'virtualenv' package which works with any Python, including embedded.
            Logs.Debug($"[AudioLab] 'python -m venv' failed (exit {proc.ExitCode}), trying virtualenv fallback...");
            bool fallback = await TryCreateWithVirtualenvAsync(basePython, venvDir, group);
            if (!fallback)
            {
                Logs.Error($"[AudioLab] Venv creation failed for group '{group}'. "
                    + $"Neither 'python -m venv' nor 'virtualenv' succeeded. stderr: {stderr}");
                return false;
            }
        }

        string venvPython = GetVenvPythonPath(group);
        if (!File.Exists(venvPython))
        {
            Logs.Error($"[AudioLab] Venv created but python not found at {venvPython}");
            return false;
        }

        // Write pip config so ALL pip operations in this venv resolve torch from the CUDA index.
        // Without this, transitive torch dependencies (e.g. "bark" requires "torch") pull in
        // CPU-only torch from PyPI, overriding our explicitly installed CUDA version.
        WritePipConfig(venvDir);

        Logs.Info($"[AudioLab] Venv for group '{group}' created successfully");
        return true;
    }

    /// <summary>Fallback venv creation using 'virtualenv' package. Installs virtualenv via pip
    /// if needed, then creates the venv. Works with embedded Python that lacks the 'venv' module.</summary>
    private async Task<bool> TryCreateWithVirtualenvAsync(string basePython, string venvDir, string group)
    {
        // Install virtualenv into the base Python
        Logs.Info($"[AudioLab] Installing virtualenv for embedded Python fallback...");
        int installExit = await RunProcessAsync(basePython, "-m pip install virtualenv", group);
        if (installExit != 0)
        {
            Logs.Warning($"[AudioLab] Failed to install virtualenv (exit {installExit})");
            return false;
        }

        // Create the venv using virtualenv
        int createExit = await RunProcessAsync(basePython, $"-m virtualenv \"{venvDir}\"", group);
        if (createExit != 0)
        {
            Logs.Warning($"[AudioLab] virtualenv creation failed (exit {createExit})");
            return false;
        }

        Logs.Info($"[AudioLab] Venv created via virtualenv fallback for group '{group}'");
        return true;
    }

    /// <summary>Runs a Python command and returns the exit code. Helper for venv creation.</summary>
    private async Task<int> RunProcessAsync(string python, string arguments, string group)
    {
        ProcessStartInfo psi = new()
        {
            FileName = python,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = AudioConfiguration.PythonBackendDirectory
        };
        PythonLaunchHelper.CleanEnvironmentOfPythonMess(psi, $"[AudioLab] ");

        using Process proc = new() { StartInfo = psi };
        proc.Start();
        await proc.StandardOutput.ReadToEndAsync();
        await proc.StandardError.ReadToEndAsync();
        bool exited = await Task.Run(() => proc.WaitForExit(120_000));
        if (!exited)
        {
            try { proc.Kill(); } catch { }
            return -1;
        }
        return proc.ExitCode;
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
            Logs.Info($"[AudioLab] Deleted venv for group '{group}'");
        }
    }

    #endregion

    #region Base Python Detection

    /// <summary>Finds the base Python used to create venvs.
    /// Checks SwarmUI's bundled Python first (ComfyUI embedded/venv), then system Python.
    /// If the found Python lacks the 'venv' module (e.g. embedded Python on Windows),
    /// CreateVenvAsync will automatically fall back to 'virtualenv'.</summary>
    public static string GetBasePythonPath()
    {
        // 1. Try SwarmUI's bundled Python (follows PythonLaunchHelper.LaunchGeneric pattern)
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            string embedded = Path.GetFullPath("dlbackend/comfy/python_embeded/python.exe");
            if (File.Exists(embedded))
            {
                Logs.Debug($"[AudioLab] Using ComfyUI embedded Python: {embedded}");
                return embedded;
            }
        }
        else
        {
            string venvPy = Path.GetFullPath("dlbackend/ComfyUI/venv/bin/python");
            if (File.Exists(venvPy))
            {
                Logs.Debug($"[AudioLab] Using ComfyUI venv Python: {venvPy}");
                return venvPy;
            }
        }

        // 2. Fall back to system Python
        string systemPython = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "python" : "python3";
        if (TryFindSystemPython(systemPython, out string systemPath))
            return systemPath;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && TryFindSystemPython("python3", out string py3Path))
            return py3Path;

        Logs.Error("[AudioLab] No Python found! AudioLab checks for Python in this order: "
            + "(1) SwarmUI's bundled ComfyUI Python, (2) system Python on PATH. "
            + "Either install a SwarmUI backend with ComfyUI, or install Python 3.10+ from "
            + "https://www.python.org/downloads/ (check 'Add python.exe to PATH' during install), "
            + "then restart SwarmUI.");
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
            Logs.Debug($"[AudioLab] Wrote {configName} with PyTorch CUDA index");
        }
        catch (Exception ex)
        {
            Logs.Warning($"[AudioLab] Failed to write pip config: {ex.Message}");
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
                Logs.Debug($"[AudioLab] Found system Python: {command} ({output.Trim()})");
                return true;
            }
        }
        catch { /* Not found on PATH */ }
        return false;
    }

    #endregion
}
