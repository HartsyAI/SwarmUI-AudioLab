using System.Diagnostics;
using System.Text;
using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Newtonsoft.Json.Linq;
using SwarmUI.Utils;
using System.IO;

namespace Hartsy.Extensions.AudioLab.AudioServices;

/// <summary>Provider-aware Python subprocess bridge.  Replaces PythonVoiceProcessor
/// with a generic interface that routes requests through the engine_registry.</summary>
public class PythonAudioProcessor
{
    private static readonly Lazy<PythonAudioProcessor> InstanceLazy = new(() => new PythonAudioProcessor());
    public static PythonAudioProcessor Instance => InstanceLazy.Value;

    private readonly object _lock = new();
    private string _pythonPath;
    private string _scriptPath;
    private bool _isInitialized;

    private PythonAudioProcessor()
    {
        Logs.Debug("[AudioLab] PythonAudioProcessor instance created");
    }

    /// <summary>Initializes the processor by detecting the Python environment.</summary>
    public async Task<bool> InitializeAsync()
    {
        lock (_lock)
        {
            if (_isInitialized) return true;
            try
            {
                _pythonPath = GetSwarmUIPythonPath();
                if (string.IsNullOrEmpty(_pythonPath))
                {
                    Logs.Error("[AudioLab] Python environment not detected");
                    return false;
                }
                _scriptPath = Path.Combine(AudioConfiguration.ExtensionDirectory, "python_backend", "voice_processor.py");
                if (!File.Exists(_scriptPath))
                {
                    Logs.Error($"[AudioLab] Voice processor script not found: {_scriptPath}");
                    return false;
                }
                _isInitialized = true;
                Logs.Info("[AudioLab] PythonAudioProcessor initialized successfully");
                return true;
            }
            catch (Exception ex)
            {
                Logs.Error($"[AudioLab] PythonAudioProcessor init failed: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>Processes a request through a specific audio provider's Python engine.
    /// Calls: python voice_processor.py process &lt;module&gt; &lt;class&gt; &lt;args_b64&gt;</summary>
    public async Task<JObject> ProcessAsync(AudioProviderDefinition provider, Dictionary<string, object> args)
    {
        if (!_isInitialized && !await InitializeAsync())
        {
            return CreateErrorResponse("PythonAudioProcessor not initialized");
        }

        string argsJson = Newtonsoft.Json.JsonConvert.SerializeObject(args);
        string argsB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(argsJson));

        string[] cmdArgs = ["process", provider.PythonModule, provider.PythonEngineClass, argsB64];

        // Model downloads + inference can take a long time on first run.
        // Music/SFX generation and voice cloning are especially slow.
        int timeoutMs = provider.Category switch
        {
            AudioCategory.TTS => 180000,         // 3 min — model download + inference
            AudioCategory.MusicGen => 300000,    // 5 min — large model download + generation
            AudioCategory.SoundFX => 300000,     // 5 min — audiocraft models
            AudioCategory.VoiceClone => 180000,  // 3 min — voice clone models
            AudioCategory.AudioFX => 180000,     // 3 min — demucs/enhancement
            _ => 120000                          // 2 min — default (STT, etc.)
        };
        string output = await RunPythonScriptAsync(cmdArgs, timeoutMs);

        return JObject.Parse(output);
    }

    /// <summary>Initializes legacy voice services (backward compatible).</summary>
    public async Task<JObject> InitializeVoiceServicesAsync(JObject config = null)
    {
        if (!_isInitialized && !await InitializeAsync())
        {
            return CreateErrorResponse("PythonAudioProcessor not initialized");
        }

        string configJson = config?.ToString() ?? "{}";
        string base64Config = Convert.ToBase64String(Encoding.UTF8.GetBytes(configJson));
        string output = await RunPythonScriptAsync(["init", "-b", base64Config], 300000);

        if (string.IsNullOrWhiteSpace(output) || !output.StartsWith("{"))
        {
            return CreateErrorResponse($"Python script returned non-JSON output: '{output}'");
        }
        return JObject.Parse(output);
    }

    /// <summary>Gets voice service status.</summary>
    public async Task<JObject> GetVoiceStatusAsync()
    {
        if (!_isInitialized)
        {
            return CreateErrorResponse("Python processor not initialized");
        }
        string result = await RunPythonScriptAsync(["status"]);
        return JObject.Parse(result);
    }

    /// <summary>Cleans up all engines.</summary>
    public async Task<JObject> CleanupAsync()
    {
        if (!_isInitialized)
        {
            return CreateErrorResponse("Not initialized");
        }
        string result = await RunPythonScriptAsync(["cleanup"]);
        return JObject.Parse(result);
    }

    /// <summary>Gets processor status for diagnostics.</summary>
    public JObject GetProcessorStatus()
    {
        return new JObject
        {
            ["initialized"] = _isInitialized,
            ["python_path"] = _pythonPath ?? "",
            ["script_path"] = _scriptPath ?? "",
            ["script_exists"] = !string.IsNullOrEmpty(_scriptPath) && File.Exists(_scriptPath)
        };
    }

    // -- internal helpers -------------------------------------------------

    /// <summary>Runs the voice_processor.py script with arguments and returns JSON output.</summary>
    private async Task<string> RunPythonScriptAsync(string[] args, int timeoutMs = 30000)
    {
        return await Task.Run(() =>
        {
            try
            {
                string arguments = $"\"{_scriptPath}\" " + string.Join(" ", args.Select(arg => $"\"{EscapeArgument(arg)}\""));

                ProcessStartInfo startInfo = new()
                {
                    FileName = _pythonPath,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = Path.GetDirectoryName(_scriptPath)
                };

                ConfigurePythonEnvironment(startInfo);

                using Process process = new() { StartInfo = startInfo };
                StringBuilder output = new();
                StringBuilder error = new();

                process.OutputDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                        output.AppendLine(e.Data);
                };
                process.ErrorDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                        error.AppendLine(e.Data);
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                bool exited = process.WaitForExit(timeoutMs);

                string stdoutOutput = output.ToString().Trim();
                string stderrOutput = error.ToString();

                bool hasValidJson = !string.IsNullOrEmpty(stdoutOutput) &&
                                    stdoutOutput.StartsWith("{") &&
                                    stdoutOutput.EndsWith("}") &&
                                    stdoutOutput.Contains("\"success\"");

                if (!exited)
                {
                    Logs.Error($"[AudioLab] Python script timed out after {timeoutMs}ms");
                    try { process.Kill(); } catch { }

                    if (hasValidJson)
                    {
                        Logs.Warning("[AudioLab] Process hung but returned valid JSON, treating as success.");
                        return stdoutOutput;
                    }
                    throw new TimeoutException($"Python script timed out after {timeoutMs}ms");
                }

                if (process.ExitCode != 0)
                {
                    Logs.Error($"[AudioLab] Python script failed (exit {process.ExitCode}): {stderrOutput}");
                    throw new Exception($"Python script failed: {stderrOutput}");
                }

                if (string.IsNullOrEmpty(stdoutOutput))
                {
                    throw new Exception("Python script returned empty output");
                }

                return stdoutOutput;
            }
            catch (Exception ex)
            {
                Logs.Error($"[AudioLab] Python script execution error: {ex.Message}");
                throw;
            }
        });
    }

    /// <summary>Configures the process environment to use SwarmUI's Python.</summary>
    private void ConfigurePythonEnvironment(ProcessStartInfo startInfo)
    {
        try
        {
            string scriptDirectory = Path.GetDirectoryName(_scriptPath);
            startInfo.WorkingDirectory = scriptDirectory;

            if (_pythonPath.Contains("python_embeded"))
            {
                string embedPath = Path.GetFullPath("./dlbackend/comfy/python_embeded");
                startInfo.Environment["PATH"] = PythonLaunchHelper.ReworkPythonPaths(embedPath);
            }
            else if (_pythonPath.Contains("venv"))
            {
                string venvPath = Path.GetFullPath("./dlbackend/ComfyUI/venv/bin");
                startInfo.Environment["PATH"] = PythonLaunchHelper.ReworkPythonPaths(venvPath);
            }

            PythonLaunchHelper.CleanEnvironmentOfPythonMess(startInfo, "[AudioLab] ");
            startInfo.Environment["PYTHONPATH"] = scriptDirectory;
        }
        catch (Exception ex)
        {
            Logs.Warning($"[AudioLab] Error configuring Python environment: {ex.Message}");
        }
    }

    private static string GetSwarmUIPythonPath()
    {
        if (File.Exists("./dlbackend/comfy/python_embeded/python.exe"))
            return Path.GetFullPath("./dlbackend/comfy/python_embeded/python.exe");
        if (File.Exists("./dlbackend/ComfyUI/venv/bin/python"))
            return Path.GetFullPath("./dlbackend/ComfyUI/venv/bin/python");
        return null;
    }

    private static string EscapeArgument(string argument)
    {
        if (string.IsNullOrEmpty(argument)) return "";
        return argument.Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
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
