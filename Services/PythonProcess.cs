using SwarmUI.Utils;
using System.Diagnostics;
using Hartsy.Extensions.VoiceAssistant.Configuration;
using Hartsy.Extensions.VoiceAssistant.Models;
using System.IO;

namespace Hartsy.Extensions.VoiceAssistant.Services;

/// <summary>
/// Manages the Python backend process lifecycle.
/// Handles process creation, monitoring, and termination with proper resource cleanup.
/// </summary>
public class PythonProcess : IDisposable
{
    private readonly object _processLock = new();
    private Process _process;
    private bool _disposed = false;

    public bool IsRunning
    {
        get
        {
            lock (_processLock)
            {
                return _process != null && !_process.HasExited;
            }
        }
    }

    public int ProcessId
    {
        get
        {
            lock (_processLock)
            {
                return _process?.Id ?? 0;
            }
        }
    }

    public bool HasExited
    {
        get
        {
            lock (_processLock)
            {
                return _process?.HasExited ?? true;
            }
        }
    }

    public event EventHandler<string> OutputReceived;
    public event EventHandler<string> ErrorReceived;
    public event EventHandler ProcessExited;

    /// <summary>
    /// Starts the Python backend process.
    /// </summary>
    /// <param name="pythonPath">Path to the Python executable</param>
    /// <returns>True if process started successfully</returns>
    public async Task<bool> StartAsync(string pythonPath)
    {
        if (string.IsNullOrEmpty(pythonPath))
        {
            throw new ArgumentException("Python path cannot be null or empty", nameof(pythonPath));
        }

        if (!File.Exists(pythonPath))
        {
            throw new FileNotFoundException($"Python executable not found: {pythonPath}");
        }

        if (!File.Exists(ServiceConfiguration.PythonBackendScript))
        {
            throw new FileNotFoundException($"Backend script not found: {ServiceConfiguration.PythonBackendScript}");
        }

        lock (_processLock)
        {
            try
            {
                // Clean up any existing process
                StopInternal();

                Logs.Info("[VoiceAssistant] Starting Python backend process");

                // Create a custom ProcessStartInfo to ensure proper working directory
                string backendDir = Path.GetDirectoryName(ServiceConfiguration.PythonBackendScript);
                ProcessStartInfo startInfo = new()
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = backendDir // Set working directory to python_backend folder
                };
                
                // Get just the filename without the path for the script
                string scriptFileName = Path.GetFileName(ServiceConfiguration.PythonBackendScript);
                
                // Configure Python environment and launch the process
                if (File.Exists("./dlbackend/comfy/python_embeded/python.exe"))
                {
                    startInfo.FileName = "./dlbackend/comfy/python_embeded/python.exe";
                    startInfo.Environment["PATH"] = PythonLaunchHelper.ReworkPythonPaths(Path.GetFullPath("./dlbackend/comfy/python_embeded"));
                    PythonLaunchHelper.CleanEnvironmentOfPythonMess(startInfo, "[VoiceAssistant] ");
                }
                else if (File.Exists("./dlbackend/ComfyUI/venv/bin/python"))
                {
                    startInfo.FileName = "./dlbackend/ComfyUI/venv/bin/python";
                    PythonLaunchHelper.CleanEnvironmentOfPythonMess(startInfo, "[VoiceAssistant] ");
                    startInfo.Environment["PATH"] = PythonLaunchHelper.ReworkPythonPaths(Path.GetFullPath("./dlbackend/ComfyUI/venv/bin"));
                }
                else
                {
                    // Fall back to specified Python path
                    startInfo.FileName = pythonPath;
                    PythonLaunchHelper.CleanEnvironmentOfPythonMess(startInfo, "[VoiceAssistant] ");
                }
                
                // Add script and arguments - using only the filename since we're already in the right directory
                startInfo.ArgumentList.Add("-s");
                startInfo.ArgumentList.Add(scriptFileName);
                startInfo.ArgumentList.Add("--port");
                startInfo.ArgumentList.Add(ServiceConfiguration.BackendPort.ToString());
                startInfo.ArgumentList.Add("--host");
                startInfo.ArgumentList.Add(ServiceConfiguration.BackendHost);
                
                // Start the process
                _process = new Process { StartInfo = startInfo };
                _process.Start();

                if (_process == null)
                {
                    Logs.Error("[VoiceAssistant] Failed to create Python process");
                    return false;
                }

                // Configure process event handling
                _process.EnableRaisingEvents = true;
                _process.Exited += OnProcessExited;

                // Set up output logging
                _process.OutputDataReceived += OnOutputDataReceived;
                _process.ErrorDataReceived += OnErrorDataReceived;

                // Start reading output streams
                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();

                Logs.Info($"[VoiceAssistant] Backend process started with PID: {_process.Id}");
                return true;
            }
            catch (Exception ex)
            {
                Logs.Error($"[VoiceAssistant] Failed to start backend process: {ex.Message}");
                Logs.Debug($"[VoiceAssistant] Process startup error: {ex}");

                // Clean up on failure
                StopInternal();
                return false;
            }
        }
    }

    /// <summary>
    /// Stops the Python backend process gracefully.
    /// </summary>
    /// <param name="timeout">Maximum time to wait for graceful shutdown</param>
    /// <returns>True if process stopped successfully</returns>
    public async Task<bool> StopAsync(TimeSpan? timeout = null)
    {
        var actualTimeout = timeout ?? ServiceConfiguration.ProcessShutdownTimeout;

        lock (_processLock)
        {
            if (_process == null || _process.HasExited)
            {
                Logs.Debug("[VoiceAssistant] No process to stop");
                return true;
            }

            Logs.Info("[VoiceAssistant] Stopping Python backend process");

            try
            {
                // First try graceful shutdown via HTTP (handled by caller)
                // If that fails, we'll terminate the process

                // Wait for graceful exit
                bool exited = _process.WaitForExit((int)actualTimeout.TotalMilliseconds);

                if (!exited)
                {
                    Logs.Warning("[VoiceAssistant] Process did not exit gracefully, force terminating");

                    try
                    {
                        _process.Kill(true); // Kill process tree
                        _process.WaitForExit(3000); // Wait up to 3 seconds for kill to complete
                    }
                    catch (Exception killEx)
                    {
                        Logs.Error($"[VoiceAssistant] Error during force termination: {killEx.Message}");
                        return false;
                    }
                }

                Logs.Info("[VoiceAssistant] Backend process stopped successfully");
                return true;
            }
            catch (Exception ex)
            {
                Logs.Error($"[VoiceAssistant] Error stopping process: {ex.Message}");
                return false;
            }
            finally
            {
                CleanupProcess();
            }
        }
    }

    /// <summary>
    /// Forces immediate termination of the process.
    /// </summary>
    public void ForceStop()
    {
        lock (_processLock)
        {
            try
            {
                if (_process != null && !_process.HasExited)
                {
                    Logs.Warning("[VoiceAssistant] Force killing backend process");
                    _process.Kill(true);
                    _process.WaitForExit(3000);
                }
            }
            catch (Exception ex)
            {
                Logs.Error($"[VoiceAssistant] Error during force stop: {ex.Message}");
            }
            finally
            {
                CleanupProcess();
            }
        }
    }

    /// <summary>
    /// Gets current process information.
    /// </summary>
    /// <returns>Process information or null if not running</returns>
    public ProcessInfo GetProcessInfo()
    {
        lock (_processLock)
        {
            if (_process == null)
                return null;

            try
            {
                return new ProcessInfo
                {
                    ProcessId = _process.Id,
                    HasExited = _process.HasExited,
                    StartTime = _process.StartTime,
                    ProcessName = _process.ProcessName,
                    WorkingSet = _process.WorkingSet64,
                    VirtualMemory = _process.VirtualMemorySize64
                };
            }
            catch (Exception ex)
            {
                Logs.Debug($"[VoiceAssistant] Error getting process info: {ex.Message}");
                return null;
            }
        }
    }

    /// <summary>
    /// Internal stop method without timeout handling.
    /// </summary>
    private void StopInternal()
    {
        try
        {
            if (_process != null)
            {
                if (!_process.HasExited)
                {
                    _process.Kill(true);
                    _process.WaitForExit(3000);
                }
                CleanupProcess();
            }
        }
        catch (Exception ex)
        {
            Logs.Debug($"[VoiceAssistant] Error in internal stop: {ex.Message}");
        }
    }

    /// <summary>
    /// Cleans up process resources.
    /// </summary>
    private void CleanupProcess()
    {
        try
        {
            if (_process != null)
            {
                _process.OutputDataReceived -= OnOutputDataReceived;
                _process.ErrorDataReceived -= OnErrorDataReceived;
                _process.Exited -= OnProcessExited;
                _process.Dispose();
                _process = null;
            }
        }
        catch (Exception ex)
        {
            Logs.Debug($"[VoiceAssistant] Error cleaning up process: {ex.Message}");
        }
    }

    /// <summary>
    /// Handles process exit events.
    /// </summary>
    private void OnProcessExited(object sender, EventArgs e)
    {
        Logs.Info("[VoiceAssistant] Backend process exited");
        ProcessExited?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Handles process output data.
    /// </summary>
    private void OnOutputDataReceived(object sender, DataReceivedEventArgs e)
    {
        if (!string.IsNullOrEmpty(e?.Data))
        {
            Logs.Info($"[VoiceBackend] {e.Data}");
            OutputReceived?.Invoke(this, e.Data);
        }
    }

    /// <summary>
    /// Handles process error data.
    /// Only logs as Error if the message appears to be an actual error.
    /// Many Python libraries output regular information to stderr.
    /// </summary>
    private void OnErrorDataReceived(object sender, DataReceivedEventArgs e)
    {
        if (!string.IsNullOrEmpty(e?.Data))
        {
            string data = e.Data;
            
            // Check if this is actually an error message or just informational
            bool isActualError = data.Contains("Error") ||
                                data.Contains("ERROR") ||
                                data.Contains("Exception") ||
                                data.Contains("exception") ||
                                data.Contains("CRITICAL") ||
                                data.Contains("failed") ||
                                data.Contains("Failed") ||
                                data.Contains("FAILED") ||
                                data.Contains("warning") ||
                                data.Contains("WARNING");
                                
            // UVICORN and FastAPI often output regular logs to stderr
            if (isActualError)
            {
                Logs.Error($"[VoiceBackend] {data}");
            }
            else 
            {
                // Log as Info for clarity - avoids duplicate messages
                Logs.Info($"[VoiceBackend] {data}");
            }
            
            ErrorReceived?.Invoke(this, data);
        }
    }

    /// <summary>
    /// Disposes of process resources.
    /// </summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            try
            {
                lock (_processLock)
                {
                    StopInternal();
                }
                Logs.Debug("[VoiceAssistant] Python process disposed");
            }
            catch (Exception ex)
            {
                Logs.Error($"[VoiceAssistant] Error disposing Python process: {ex.Message}");
            }
            finally
            {
                _disposed = true;
            }
        }
    }
}

/// <summary>
/// Information about a running process.
/// </summary>
public class ProcessInfo
{
    public int ProcessId { get; set; }
    public bool HasExited { get; set; }
    public DateTime StartTime { get; set; }
    public string ProcessName { get; set; } = string.Empty;
    public long WorkingSet { get; set; }
    public long VirtualMemory { get; set; }
}
