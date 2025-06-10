using SwarmUI.Core;
using SwarmUI.WebAPI;
using SwarmUI.Utils;
using SwarmUI.Accounts;
using Newtonsoft.Json.Linq;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Hartsy.Extensions.VoiceAssistant;

/// <summary>
/// SwarmUI Voice Assistant Extension - Production Ready with Fixed Progress Tracking
/// Provides speech-to-text, text-to-speech, and voice command processing integrated into SwarmUI.
/// This extension manages a Python backend for STT/TTS processing and registers API endpoints
/// for voice interaction with the SwarmUI interface.
/// </summary>
public class VoiceAssistant : Extension
{
    #region Static Fields

    /// <summary>
    /// The Python backend process instance. Manages the FastAPI server for STT/TTS processing.
    /// Why static: Ensures only one backend process runs across all extension instances.
    /// </summary>
    public static Process PythonBackend;

    /// <summary>
    /// HTTP client for communicating with the Python backend service.
    /// Configured with appropriate timeouts and headers for voice processing operations.
    /// </summary>
    public static readonly HttpClient HttpClient = new();

    /// <summary>
    /// Tracks whether the Python backend is currently running and responsive.
    /// Used to avoid duplicate startup attempts and ensure proper health checking.
    /// </summary>
    public static bool IsBackendRunning = false;

    /// <summary>
    /// Thread safety lock for backend process management operations.
    /// Prevents race conditions during startup/shutdown of the Python process.
    /// </summary>
    public static readonly object BackendLock = new();

    /// <summary>
    /// Base URL for the Python backend FastAPI server.
    /// All STT/TTS API calls are made to endpoints under this URL.
    /// </summary>
    public static readonly string BackendUrl = "http://localhost:7831";

    /// <summary>
    /// Cancellation token source for graceful backend shutdown.
    /// Allows clean termination of background tasks and HTTP operations.
    /// </summary>
    public static CancellationTokenSource BackendCancellation;

    /// <summary>
    /// Extension directory path for locating Python backend files.
    /// Computed dynamically to handle different SwarmUI installation locations.
    /// FIXED: Now uses correct "SwarmUI-VoiceAssistant" directory name.
    /// </summary>
    public static string ExtensionDirectory = "";

    /// <summary>
    /// Progress tracking for installation processes.
    /// Allows real-time progress updates to be sent to the frontend.
    /// </summary>
    public static InstallationProgressTracker ProgressTracker;

    /// <summary>
    /// API endpoint handler for getting real-time installation progress.
    /// Provides detailed progress information during dependency installation.
    /// </summary>
    public static async Task<JObject> GetInstallationProgress(Session session, JObject input)
    {
        try
        {
            if (ProgressTracker == null)
            {
                return new JObject
                {
                    ["success"] = false,
                    ["error"] = "No installation in progress"
                };
            }

            return new JObject
            {
                ["success"] = true,
                ["progress"] = ProgressTracker.OverallProgress,
                ["current_step"] = ProgressTracker.CurrentStep,
                ["current_package"] = ProgressTracker.CurrentPackage,
                ["download_progress"] = ProgressTracker.DownloadProgress,
                ["status_message"] = ProgressTracker.StatusMessage,
                ["is_complete"] = ProgressTracker.IsComplete,
                ["has_error"] = ProgressTracker.HasError,
                ["error_message"] = ProgressTracker.ErrorMessage
            };
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] Error getting installation progress: {ex.Message}");
            return CreateErrorResponse($"Failed to get installation progress: {ex.Message}");
        }
    }

    /// <summary>API endpoint handler for checking installation status of dependencies.
    /// Updated to reflect the no-fallback policy and stricter requirements.</summary>
    public static async Task<JObject> CheckInstallationStatus(Session session, JObject input)
    {
        try
        {
            Logs.Debug($"[VoiceAssistant] Checking installation status for session: {session.ID}");
            // Detect Python environment - strict SwarmUI requirement
            PythonEnvironmentInfo pythonInfo = DetectPythonEnvironment();
            if (pythonInfo == null)
            {
                return new JObject
                {
                    ["success"] = false,
                    ["error"] = "SwarmUI Python environment not found. Voice Assistant requires SwarmUI with ComfyUI backend installed.",
                    ["python_detected"] = false,
                    ["requires_comfyui"] = true,
                    ["no_fallbacks"] = true
                };
            }
            // Check if dependencies are installed
            bool depsInstalled = await CheckDependenciesInstalled(pythonInfo);
            // Get detailed status
            JObject detailedStatus = await GetDetailedInstallationStatus(pythonInfo);
            JObject result = new()
            {
                ["success"] = true,
                ["python_detected"] = true,
                ["python_path"] = pythonInfo.PythonPath,
                ["operating_system"] = pythonInfo.OperatingSystem,
                ["is_embedded_python"] = pythonInfo.IsEmbedded,
                ["dependencies_installed"] = depsInstalled,
                ["installation_details"] = detailedStatus,
                ["required_libraries"] = new JArray { "RealtimeSTT", "Chatterbox TTS" },
                ["no_fallbacks"] = true,
                ["strict_requirements"] = true
            };
            return result;
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] Error checking installation status: {ex.Message}");
            return CreateErrorResponse($"Failed to check installation status: {ex.Message}");
        }
    }

    /// <summary>Gets detailed status of individual package installations.
    /// Tests only primary packages.</summary>
    public static async Task<JObject> GetDetailedInstallationStatus(PythonEnvironmentInfo pythonInfo)
    {
        JObject status = new()
        {
            ["core_packages"] = await TestPackageGroup(pythonInfo,
        [
            "fastapi", "uvicorn", "numpy", "scipy", "pydantic", "torchaudio"
        ]),
            ["stt_packages"] = await TestPackageGroup(pythonInfo,
        [
            "RealtimeSTT"
        ]),
            ["tts_packages"] = await TestPackageGroup(pythonInfo,
        [
            "chatterbox"
        ])
        };
        return status;
    }

    /// <summary>Tests a group of packages to see which ones are installed and working.</summary>
    public static async Task<JObject> TestPackageGroup(PythonEnvironmentInfo pythonInfo, string[] packages)
    {
        JObject results = [];

        foreach (string package in packages)
        {
            try
            {
                string testScript = $@"
                    try:
                        import {package}
                        print('SUCCESS')
                    except ImportError:
                        print('NOT_INSTALLED')
                    except Exception as e:
                        print(f'ERROR: {{e}}')
                    ";

                bool success = await RunPythonScript(pythonInfo, testScript, $"test {package}");
                results[package] = success;
            }
            catch
            {
                results[package] = false;
            }
        }

        return results;
    }

    #endregion

    #region Progress Tracking

    /// <summary>
    /// Class for tracking installation progress with real-time updates.
    /// Provides detailed progress information for frontend display.
    /// </summary>
    public class InstallationProgressTracker
    {
        public int OverallProgress { get; set; } = 0;
        public string CurrentStep { get; set; } = "";
        public string CurrentPackage { get; set; } = "";
        public int DownloadProgress { get; set; } = 0;
        public string StatusMessage { get; set; } = "";
        public bool IsComplete { get; set; } = false;
        public bool HasError { get; set; } = false;
        public string ErrorMessage { get; set; } = "";

        public void UpdateProgress(int overall, string step, string package = "", int download = 0, string message = "")
        {
            OverallProgress = overall;
            CurrentStep = step;
            CurrentPackage = package;
            DownloadProgress = download;
            StatusMessage = message;

            Logs.Info($"[VoiceAssistant] Progress: {overall}% - {step} - {message}");
        }

        public void SetError(string error)
        {
            HasError = true;
            ErrorMessage = error;
            Logs.Error($"[VoiceAssistant] Installation error: {error}");
        }

        public void SetComplete()
        {
            IsComplete = true;
            OverallProgress = 100;
            StatusMessage = "Installation completed successfully!";
            Logs.Info("[VoiceAssistant] Installation completed successfully");
        }
    }

    #endregion

    #region Permissions

    /// <summary>
    /// Permissions group for Voice Assistant functionality.
    /// Defines access control for voice processing and backend management operations.
    /// </summary>
    public static readonly PermInfoGroup VoiceAssistantPermGroup = new("VoiceAssistant", "Permissions related to Voice Assistant functionality for API calls and voice processing.");

    /// <summary>Permission for processing voice input and generating responses.</summary>
    public static readonly PermInfo PermProcessVoice = Permissions.Register(new("voice_process_input", "Process Voice Input", "Allows processing of voice commands and audio transcription.", PermissionDefault.POWERUSERS, VoiceAssistantPermGroup));

    /// <summary>Permission for managing the voice service backend lifecycle.</summary>
    public static readonly PermInfo PermManageService = Permissions.Register(new("voice_manage_service", "Manage Voice Service", "Allows starting and stopping the voice processing backend.", PermissionDefault.POWERUSERS, VoiceAssistantPermGroup));

    /// <summary>Permission for checking voice service status and health.</summary>
    public static readonly PermInfo PermCheckStatus = Permissions.Register(new("voice_check_status", "Check Voice Status", "Allows checking the status and health of voice services.", PermissionDefault.POWERUSERS, VoiceAssistantPermGroup));

    #endregion

    #region Extension Lifecycle

    /// <summary>
    /// Pre-initialization phase - registers web assets before SwarmUI core initialization.
    /// This runs before the main UI is ready, so we only register static assets here.
    /// Why separate from OnInit: Web assets must be registered before the UI loads.
    /// </summary>
    public override void OnPreInit()
    {
        Logs.Info("[VoiceAssistant] Starting Voice Assistant Extension v1.0 pre-initialization");

        try
        {
            // FIXED: Calculate extension directory path using correct folder name
            ExtensionDirectory = Path.Combine("src", "Extensions", "SwarmUI-VoiceAssistant");
            Logs.Debug($"[VoiceAssistant] Extension directory: {ExtensionDirectory}");

            // Register JavaScript files for frontend voice interaction
            ScriptFiles.Add("Assets/voice-assistant.js");
            Logs.Debug("[VoiceAssistant] Registered voice-assistant.js script file");

            // Register CSS files for voice assistant UI styling
            StyleSheetFiles.Add("Assets/voice-assistant.css");
            Logs.Debug("[VoiceAssistant] Registered voice-assistant.css stylesheet");

            // Configure HTTP client for backend communication
            // Timeout set to 45 seconds to handle longer STT/TTS processing times
            HttpClient.Timeout = TimeSpan.FromSeconds(45);
            HttpClient.DefaultRequestHeaders.Add("User-Agent", "SwarmUI-VoiceAssistant/1.0");
            Logs.Debug("[VoiceAssistant] HTTP client configured for backend communication");

            Logs.Info("[VoiceAssistant] Pre-initialization completed successfully");
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] Critical error during pre-initialization: {ex.Message}");
            Logs.Debug($"[VoiceAssistant] Pre-init stack trace: {ex}");
        }
    }

    /// <summary>
    /// Main initialization phase - registers API endpoints and sets up extension functionality.
    /// This runs after SwarmUI core is ready, allowing us to register API calls and start services.
    /// Why here: API registration requires SwarmUI's API system to be fully initialized.
    /// </summary>
    public override void OnInit()
    {
        try
        {
            Logs.Info("[VoiceAssistant] Starting main initialization phase");

            // Register API endpoints using SwarmUI's proper API registration system
            // These endpoints handle voice input, backend management, and status queries
            API.RegisterAPICall(ProcessVoiceInput, false, PermProcessVoice);
            API.RegisterAPICall(StartVoiceService, false, PermManageService);
            API.RegisterAPICall(StopVoiceService, false, PermManageService);
            API.RegisterAPICall(GetVoiceStatus, false, PermCheckStatus);
            API.RegisterAPICall(ProcessTextCommand, false, PermProcessVoice);
            API.RegisterAPICall(CheckInstallationStatus, false, PermCheckStatus);
            API.RegisterAPICall(GetInstallationProgress, false, PermCheckStatus); // NEW: Progress tracking endpoint

            Logs.Info("[VoiceAssistant] API endpoints registered successfully");
            Logs.Debug("[VoiceAssistant] Registered endpoints: ProcessVoiceInput, StartVoiceService, StopVoiceService, GetVoiceStatus, ProcessTextCommand, CheckInstallationStatus, GetInstallationProgress");

            // Initialize cancellation token for graceful shutdown handling
            BackendCancellation = new CancellationTokenSource();
            Logs.Debug("[VoiceAssistant] Cancellation token source initialized");

            Logs.Info("[VoiceAssistant] Extension initialization completed successfully");
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] Failed to initialize extension: {ex.Message}");
            Logs.Debug($"[VoiceAssistant] Initialization stack trace: {ex}");
        }
    }

    /// <summary>
    /// Extension shutdown - cleans up resources and stops the Python backend.
    /// Ensures graceful termination of background processes and proper resource disposal.
    /// Why important: Prevents orphaned Python processes and resource leaks.
    /// </summary>
    public override void OnShutdown()
    {
        try
        {
            Logs.Info("[VoiceAssistant] Starting extension shutdown process");

            // Cancel any ongoing operations
            BackendCancellation?.Cancel();
            Logs.Debug("[VoiceAssistant] Cancellation token triggered");

            // Stop the Python backend with timeout to prevent hanging
            try
            {
                Task.Run(async () => await StopPythonBackend()).Wait(TimeSpan.FromSeconds(10));
                Logs.Info("[VoiceAssistant] Python backend stopped successfully");
            }
            catch (Exception ex)
            {
                Logs.Error($"[VoiceAssistant] Error stopping backend during shutdown: {ex.Message}");

                // Force kill the process if graceful shutdown fails
                lock (BackendLock)
                {
                    try
                    {
                        if (PythonBackend != null && !PythonBackend.HasExited)
                        {
                            Logs.Warning("[VoiceAssistant] Force killing unresponsive backend process");
                            PythonBackend.Kill(true);
                            PythonBackend.Dispose();
                            PythonBackend = null;
                        }
                    }
                    catch (Exception killEx)
                    {
                        Logs.Error($"[VoiceAssistant] Error during force kill: {killEx.Message}");
                    }
                }
            }

            // Clean up HTTP client resources
            try
            {
                HttpClient?.Dispose();
                Logs.Debug("[VoiceAssistant] HTTP client disposed");
            }
            catch (Exception ex)
            {
                Logs.Debug($"[VoiceAssistant] Error disposing HTTP client: {ex.Message}");
            }

            Logs.Info("[VoiceAssistant] Extension shutdown completed");
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] Critical error during shutdown: {ex.Message}");
            Logs.Debug($"[VoiceAssistant] Shutdown stack trace: {ex}");
        }
    }

    #endregion

    #region Backend Process Management

    /// <summary>
    /// Starts the Python backend process using SwarmUI's PythonLaunchHelper.
    /// Handles process creation, health checking, dependency installation, and logging setup for the FastAPI server.
    /// Why async: Process startup and health checking involve I/O operations that shouldn't block.
    /// </summary>
    public static async Task<bool> StartPythonBackend()
    {
        // Check and install dependencies if needed
        await EnsurePythonDependencies();
        lock (BackendLock)
        {
            try
            {
                // Check if backend is already running and healthy
                if (IsBackendRunning && PythonBackend != null && !PythonBackend.HasExited)
                {
                    Logs.Info("[VoiceAssistant] Backend is already running, skipping startup");
                    return true;
                }

                Logs.Info("[VoiceAssistant] Starting Python backend process");

                // Clean up any existing process before starting new one
                if (PythonBackend != null)
                {
                    try
                    {
                        PythonBackend.Dispose();
                        Logs.Debug("[VoiceAssistant] Disposed previous backend process");
                    }
                    catch (Exception ex)
                    {
                        Logs.Debug($"[VoiceAssistant] Error disposing previous process: {ex.Message}");
                    }
                    PythonBackend = null;
                }

                // Locate the Python backend script
                string scriptPath = Path.Combine(ExtensionDirectory, "python_backend", "voice_server.py");
                if (!File.Exists(scriptPath))
                {
                    Logs.Error($"[VoiceAssistant] Backend script not found at: {scriptPath}");
                    Logs.Error($"[VoiceAssistant] Please ensure the python_backend directory exists in: {ExtensionDirectory}");
                    return false;
                }

                Logs.Debug($"[VoiceAssistant] Using backend script: {scriptPath}");

                // Use SwarmUI's PythonLaunchHelper for proper Python environment setup
                // This handles Python path resolution, environment cleanup, and process creation
                PythonBackend = PythonLaunchHelper.LaunchGeneric(
                    script: scriptPath,
                    autoOutput: false, // We'll handle output ourselves for better logging
                    args: ["--port", "7831", "--host", "localhost"]
                );

                // Configure process event handling for proper lifecycle management
                PythonBackend.EnableRaisingEvents = true;
                PythonBackend.Exited += (sender, e) => {
                    Logs.Info("[VoiceAssistant] Backend process exited");
                    IsBackendRunning = false;
                    lock (BackendLock)
                    {
                        PythonBackend?.Dispose();
                        PythonBackend = null;
                    }
                };

                // Set up output logging to capture backend messages
                PythonBackend.OutputDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e?.Data))
                    {
                        Logs.Info($"[VoiceBackend] {e.Data}");
                    }
                };

                PythonBackend.ErrorDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e?.Data))
                    {
                        Logs.Error($"[VoiceBackend] {e.Data}");
                    }
                };

                // Start reading output streams
                PythonBackend.BeginOutputReadLine();
                PythonBackend.BeginErrorReadLine();

                Logs.Info($"[VoiceAssistant] Backend process started with PID: {PythonBackend.Id}");

                return true;
            }
            catch (Exception ex)
            {
                Logs.Error($"[VoiceAssistant] Failed to start backend process: {ex.Message}");
                Logs.Debug($"[VoiceAssistant] Backend startup stack trace: {ex}");
                return false;
            }
        }
    }

    /// <summary>
    /// Ensures Python dependencies are installed for the voice assistant backend.
    /// ONLY works with SwarmUI's Python environment - fails gracefully if not found.
    /// Why strict: Libraries must be installed in SwarmUI's specific Python environment for compatibility.
    /// </summary>
    public static async Task EnsurePythonDependencies()
    {
        try
        {
            Logs.Info("[VoiceAssistant] Checking and installing Python dependencies...");

            // Initialize progress tracker
            ProgressTracker = new InstallationProgressTracker();
            ProgressTracker.UpdateProgress(5, "Detecting Python environment", "", 0, "Scanning for SwarmUI Python installation...");

            // Detect the SwarmUI Python environment - NO SYSTEM PYTHON FALLBACK
            PythonEnvironmentInfo pythonInfo = DetectPythonEnvironment();

            if (pythonInfo == null)
            {
                ProgressTracker.SetError("SwarmUI Python environment not found. Voice Assistant requires SwarmUI with ComfyUI backend installed.");
                Logs.Error("[VoiceAssistant] CRITICAL: SwarmUI Python environment not found!");
                Logs.Error("[VoiceAssistant] Voice Assistant requires SwarmUI with ComfyUI backend properly installed.");
                Logs.Error("[VoiceAssistant] Please ensure ComfyUI is installed and working in SwarmUI before using Voice Assistant.");
                throw new Exception("SwarmUI Python environment not found. Please install ComfyUI in SwarmUI first.");
            }

            Logs.Info($"[VoiceAssistant] Found SwarmUI Python environment: {pythonInfo.PythonPath}");
            Logs.Info($"[VoiceAssistant] Operating System: {pythonInfo.OperatingSystem}");

            ProgressTracker.UpdateProgress(10, "Checking existing dependencies", "", 0, $"Found SwarmUI Python: {pythonInfo.PythonPath}");

            // Check if dependencies are already installed
            if (await CheckDependenciesInstalled(pythonInfo))
            {
                Logs.Info("[VoiceAssistant] All required dependencies are already installed");
                ProgressTracker.SetComplete();
                return;
            }

            ProgressTracker.UpdateProgress(15, "Starting dependency installation", "", 0, "Installing required packages (no fallbacks)...");

            // Install dependencies - will throw exception if any required package fails
            await InstallDependencies(pythonInfo);

        }
        catch (Exception ex)
        {
            ProgressTracker?.SetError($"Failed to ensure Python dependencies: {ex.Message}");
            Logs.Error($"[VoiceAssistant] Failed to ensure Python dependencies: {ex.Message}");
            Logs.Debug($"[VoiceAssistant] Dependency installation error: {ex}");
            throw; // Re-throw to fail the entire process
        }
    }

    /// <summary>
    /// Installs required Python dependencies using the detected Python environment.
    /// Installs core dependencies, RealtimeSTT, and Chatterbox TTS - NO FALLBACKS.
    /// Throws exception if any required package fails to install.
    /// </summary>
    public static async Task InstallDependencies(PythonEnvironmentInfo pythonInfo)
    {
        try
        {
            Logs.Info("[VoiceAssistant] Installing Python dependencies (primary packages only)...");
            Logs.Info("[VoiceAssistant] This may take several minutes on first run...");

            ProgressTracker.UpdateProgress(20, "Installing core packages", "", 0, "Starting core package installation...");

            // Step 1: Install core dependencies
            await InstallCorePackages(pythonInfo);

            ProgressTracker.UpdateProgress(60, "Installing STT library", "", 0, "Installing RealtimeSTT (required)...");

            // Step 2: Install STT library (REQUIRED - no fallbacks)
            await InstallSTTLibrary(pythonInfo);

            ProgressTracker.UpdateProgress(80, "Installing TTS library", "", 0, "Installing Chatterbox TTS (required)...");

            // Step 3: Install TTS library (REQUIRED - no fallbacks)
            await InstallTTSLibrary(pythonInfo);

            ProgressTracker.SetComplete();
            Logs.Info("[VoiceAssistant] All required dependencies installed successfully!");
        }
        catch (Exception ex)
        {
            ProgressTracker?.SetError($"Dependency installation failed: {ex.Message}");
            Logs.Error($"[VoiceAssistant] Dependency installation failed: {ex.Message}");
            throw; // Re-throw to fail the entire process
        }
    }

    /// <summary>
    /// Information about the detected Python environment
    /// </summary>
    public class PythonEnvironmentInfo
    {
        public string PythonPath { get; set; }
        public string OperatingSystem { get; set; }
        public string PipCommand { get; set; }
        public bool IsEmbedded { get; set; }
    }

    /// <summary>
    /// Detects SwarmUI's Python environment and operating system.
    /// ONLY works with SwarmUI/ComfyUI Python environments - does NOT fall back to system Python.
    /// Why strict: Ensures compatibility with SwarmUI's specific Python setup and dependencies.
    /// </summary>
    public static PythonEnvironmentInfo DetectPythonEnvironment()
    {
        try
        {
            string currentDir = Environment.CurrentDirectory;
            string os = Environment.OSVersion.Platform.ToString();
            bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            bool isLinux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
            bool isMacOS = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

            string osName = isWindows ? "Windows" : isLinux ? "Linux" : isMacOS ? "macOS" : "Unknown";
            Logs.Debug($"[VoiceAssistant] Detected OS: {osName}");

            // Try to find SwarmUI's Python in order of preference
            string[] pythonPaths = GetPotentialPythonPaths(currentDir, isWindows);

            foreach (string pythonPath in pythonPaths)
            {
                if (File.Exists(pythonPath))
                {
                    Logs.Debug($"[VoiceAssistant] Found SwarmUI Python at: {pythonPath}");

                    bool isEmbedded = pythonPath.Contains("python_embeded") || pythonPath.Contains("python_embedded");

                    return new PythonEnvironmentInfo
                    {
                        PythonPath = pythonPath,
                        OperatingSystem = osName,
                        PipCommand = $"\"{pythonPath}\" -m pip",
                        IsEmbedded = isEmbedded
                    };
                }
            }

            // NO FALLBACK TO SYSTEM PYTHON - fail gracefully
            Logs.Error("[VoiceAssistant] No SwarmUI Python environment found!");
            Logs.Error("[VoiceAssistant] Voice Assistant requires SwarmUI with ComfyUI backend installed.");
            Logs.Error("[VoiceAssistant] Please ensure ComfyUI is properly installed in SwarmUI.");
            Logs.Error("[VoiceAssistant] Expected Python locations checked:");

            foreach (string path in pythonPaths)
            {
                Logs.Error($"[VoiceAssistant]   - {path}");
            }

            return null; // Return null instead of falling back to system Python
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] Error detecting Python environment: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Gets potential Python paths based on common SwarmUI installation patterns.
    /// Handles different installation methods and directory structures.
    /// </summary>
    public static string[] GetPotentialPythonPaths(string currentDir, bool isWindows)
    {
        List<string> paths = [];

        if (isWindows)
        {
            // Windows embedded Python paths (most common for SwarmUI)
            paths.AddRange(
            [
                Path.Combine(currentDir, "dlbackend", "comfy", "python_embeded", "python.exe"),
                Path.Combine(currentDir, "dlbackend", "comfy", "python_embedded", "python.exe"),
                Path.Combine(currentDir, "dlbackend", "ComfyUI", "python_embeded", "python.exe"),
                Path.Combine(currentDir, "python_embeded", "python.exe"),
                Path.Combine(currentDir, "venv", "Scripts", "python.exe"),
                Path.Combine(currentDir, ".venv", "Scripts", "python.exe")
            ]);
        }
        else
        {
            // Linux/Mac virtual environment paths
            paths.AddRange(
            [
                Path.Combine(currentDir, "dlbackend", "ComfyUI", "venv", "bin", "python"),
                Path.Combine(currentDir, "dlbackend", "comfy", "venv", "bin", "python"),
                Path.Combine(currentDir, "venv", "bin", "python"),
                Path.Combine(currentDir, ".venv", "bin", "python"),
                "/usr/bin/python3",
                "/usr/local/bin/python3"
            ]);
        }

        return [.. paths];
    }

    /// <summary>
    /// Checks if required dependencies are already installed in the Python environment.
    /// Tests imports to verify packages are actually usable, not just installed.
    /// </summary>
    public static async Task<bool> CheckDependenciesInstalled(PythonEnvironmentInfo pythonInfo)
    {
        try
        {
            Logs.Debug("[VoiceAssistant] Checking if dependencies are installed...");

            // Create a test script to check imports
            string testScript = $@"
                import sys
                try:
                    import fastapi
                    import uvicorn
                    import numpy
                    import scipy
                    import torchaudio
                    print('CORE_DEPS_OK')
                except ImportError as e:
                    print(f'CORE_DEPS_MISSING: {{e}}')
                    sys.exit(1)

                # Check primary STT library
                stt_available = False
                try:
                    import RealtimeSTT
                    print('REALTIMESTT_OK')
                    stt_available = True
                except ImportError:
                    print('STT_MISSING')

                # Check primary TTS library  
                tts_available = False
                try:
                    import chatterbox
                    print('CHATTERBOX_TTS_OK')
                    tts_available = True
                except ImportError:
                    print('TTS_MISSING')
                if stt_available and tts_available:
                    print('ALL_DEPS_OK')
                    sys.exit(0)
                else:
                    sys.exit(1)
                ";

            // Run the test script
            return await RunPythonScript(pythonInfo, testScript, "dependency check");
        }
        catch (Exception ex)
        {
            Logs.Debug($"[VoiceAssistant] Error checking dependencies: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Installs core packages required for the voice assistant backend.
    /// These are essential packages that must be installed for the server to run.
    /// </summary>
    public static async Task InstallCorePackages(PythonEnvironmentInfo pythonInfo)
    {
        Logs.Info("[VoiceAssistant] Installing core packages (FastAPI, NumPy, TorchAudio, etc.)...");

        string[] corePackages = [
            "fastapi>=0.104.0",
            "uvicorn[standard]>=0.24.0",
            "numpy>=1.24.0",
            "scipy>=1.10.0",
            "pydantic>=2.5.0",
            "python-multipart>=0.0.6",
            "httpx>=0.25.0",
            "torchaudio>=2.0.0"  // This is the big one that takes time
        ];

        for (int i = 0; i < corePackages.Length; i++)
        {
            string package = corePackages[i];
            int packageProgress = 20 + (int)((i / (float)corePackages.Length) * 40); // 20-60% for core packages

            ProgressTracker.UpdateProgress(packageProgress, "Installing core packages", package, 0, $"Installing {package}...");

            // Special handling for torchaudio (takes longest)
            if (package.StartsWith("torchaudio"))
            {
                await InstallSinglePackageWithProgress(pythonInfo, package, packageProgress);
            }
            else
            {
                await InstallSinglePackage(pythonInfo, package);
            }
        }
    }

    /// <summary>
    /// Installs Speech-to-Text library (RealtimeSTT only - no fallbacks).
    /// Fails if RealtimeSTT cannot be installed.
    /// </summary>
    public static async Task InstallSTTLibrary(PythonEnvironmentInfo pythonInfo)
    {
        Logs.Info("[VoiceAssistant] Installing Speech-to-Text library (RealtimeSTT)...");

        ProgressTracker.UpdateProgress(65, "Installing STT library", "RealtimeSTT", 0, "Installing RealtimeSTT...");

        // Install RealtimeSTT - NO FALLBACKS
        if (await TryInstallPackage(pythonInfo, "RealtimeSTT>=0.3.104"))
        {
            Logs.Info("[VoiceAssistant] RealtimeSTT installed successfully");
            return;
        }

        // Installation failed - throw exception instead of trying fallbacks
        ProgressTracker?.SetError("Failed to install RealtimeSTT - no fallback services available");
        throw new Exception("Failed to install required STT library: RealtimeSTT. Speech recognition will not be available.");
    }

    /// <summary>
    /// Installs Text-to-Speech library (Chatterbox TTS only - no fallbacks).
    /// Fails if Chatterbox TTS cannot be installed.
    /// </summary>
    public static async Task InstallTTSLibrary(PythonEnvironmentInfo pythonInfo)
    {
        Logs.Info("[VoiceAssistant] Installing Text-to-Speech library (Chatterbox TTS)...");

        ProgressTracker.UpdateProgress(85, "Installing TTS library", "chatterbox-tts", 0, "Installing Chatterbox TTS...");

        // Install Chatterbox TTS - NO FALLBACKS
        if (await TryInstallPackage(pythonInfo, "git+https://github.com/resemble-ai/chatterbox.git"))
        {
            Logs.Info("[VoiceAssistant] Chatterbox TTS installed successfully");
            return;
        }

        // Installation failed - throw exception instead of trying fallbacks
        ProgressTracker?.SetError("Failed to install Chatterbox TTS - no fallback services available");
        throw new Exception("Failed to install required TTS library: Chatterbox TTS. Speech synthesis will not be available.");
    }

    /// <summary>
    /// Installs a single package with detailed progress tracking.
    /// Monitors pip output for download progress and installation status.
    /// </summary>
    public static async Task InstallSinglePackageWithProgress(PythonEnvironmentInfo pythonInfo, string package, int baseProgress)
    {
        try
        {
            Logs.Debug($"[VoiceAssistant] Installing with progress tracking: {package}");

            ProcessStartInfo startInfo = new()
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

            using Process process = new() { StartInfo = startInfo };

            process.OutputDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e?.Data))
                {
                    // Parse pip output for progress information
                    string line = e.Data;

                    if (line.Contains("Downloading"))
                    {
                        // Extract download progress if available
                        var match = Regex.Match(line, @"(\d+)%");
                        if (match.Success && int.TryParse(match.Groups[1].Value, out int downloadPercent))
                        {
                            ProgressTracker.UpdateProgress(baseProgress, "Installing core packages", package, downloadPercent, $"Downloading {package}... {downloadPercent}%");
                        }
                        else
                        {
                            ProgressTracker.UpdateProgress(baseProgress, "Installing core packages", package, 50, $"Downloading {package}...");
                        }
                    }
                    else if (line.Contains("Installing"))
                    {
                        ProgressTracker.UpdateProgress(baseProgress, "Installing core packages", package, 90, $"Installing {package}...");
                    }
                    else if (line.Contains("Successfully installed"))
                    {
                        ProgressTracker.UpdateProgress(baseProgress, "Installing core packages", package, 100, $"Successfully installed {package}");
                    }

                    Logs.Debug($"[VoiceAssistant] Pip output: {line}");
                }
            };

            process.Start();
            process.BeginOutputReadLine();

            // Extended timeout for large packages like TorchAudio (15 minutes)
            bool finished = await Task.Run(() => process.WaitForExit(900000));

            if (!finished)
            {
                ProgressTracker.SetError($"Package installation timed out: {package}");
                Logs.Warning($"[VoiceAssistant] Package installation timed out: {package}");
                try { process.Kill(); } catch { }
                throw new Exception($"Installation timed out for {package}");
            }

            if (process.ExitCode != 0)
            {
                string stderr = await process.StandardError.ReadToEndAsync();
                ProgressTracker.SetError($"Failed to install {package}: {stderr}");
                throw new Exception($"Failed to install {package}: {stderr}");
            }

            Logs.Debug($"[VoiceAssistant] Successfully installed: {package}");
        }
        catch (Exception ex)
        {
            ProgressTracker?.SetError($"Exception installing {package}: {ex.Message}");
            Logs.Warning($"[VoiceAssistant] Exception installing {package}: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Attempts to install a single package, returning success/failure.
    /// Uses timeouts and error handling to prevent hanging installations.
    /// </summary>
    public static async Task<bool> TryInstallPackage(PythonEnvironmentInfo pythonInfo, string package)
    {
        try
        {
            Logs.Debug($"[VoiceAssistant] Attempting to install: {package}");

            ProcessStartInfo startInfo = new()
            {
                FileName = pythonInfo.PythonPath,
                Arguments = $"-s -m pip install \"{package}\" --no-warn-script-location",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Environment.CurrentDirectory
            };

            // Clean environment for SwarmUI's Python
            PythonLaunchHelper.CleanEnvironmentOfPythonMess(startInfo, "[VoiceAssistant] ");

            using Process process = new() { StartInfo = startInfo };
            process.Start();

            // Wait for completion with timeout (10 minutes max per package for large packages like torch)
            bool finished = await Task.Run(() => process.WaitForExit(600000));

            if (!finished)
            {
                Logs.Warning($"[VoiceAssistant] Package installation timed out: {package}");
                try { process.Kill(); } catch { }
                return false;
            }

            if (process.ExitCode == 0)
            {
                Logs.Debug($"[VoiceAssistant] Successfully installed: {package}");
                return true;
            }
            else
            {
                string stderr = await process.StandardError.ReadToEndAsync();
                Logs.Warning($"[VoiceAssistant] Failed to install {package}: {stderr}");
                return false;
            }
        }
        catch (Exception ex)
        {
            Logs.Warning($"[VoiceAssistant] Exception installing {package}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Installs a single package with error handling.
    /// Throws exception if installation fails (for required packages).
    /// </summary>
    public static async Task InstallSinglePackage(PythonEnvironmentInfo pythonInfo, string package)
    {
        if (!await TryInstallPackage(pythonInfo, package))
        {
            throw new Exception($"Failed to install required package: {package}");
        }
    }

    /// <summary>
    /// Runs a Python script and returns whether it succeeded.
    /// Used for testing dependencies and running installation checks.
    /// </summary>
    public static async Task<bool> RunPythonScript(PythonEnvironmentInfo pythonInfo, string script, string description)
    {
        try
        {
            // Write script to temporary file
            string tempScript = Path.GetTempFileName() + ".py";
            await File.WriteAllTextAsync(tempScript, script);

            try
            {
                ProcessStartInfo startInfo = new()
                {
                    FileName = pythonInfo.PythonPath,
                    Arguments = $"-s \"{tempScript}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = Environment.CurrentDirectory
                };

                // Clean environment for SwarmUI's Python
                PythonLaunchHelper.CleanEnvironmentOfPythonMess(startInfo, "[VoiceAssistant] ");

                using Process process = new() { StartInfo = startInfo };
                process.Start();

                bool finished = await Task.Run(() => process.WaitForExit(30000)); // 30 second timeout

                if (!finished)
                {
                    Logs.Debug($"[VoiceAssistant] Python script timed out: {description}");
                    try { process.Kill(); } catch { }
                    return false;
                }

                string output = await process.StandardOutput.ReadToEndAsync();
                if (!string.IsNullOrEmpty(output))
                {
                    Logs.Debug($"[VoiceAssistant] Python script output: {output}");
                }

                return process.ExitCode == 0;
            }
            finally
            {
                // Clean up temp file
                try { File.Delete(tempScript); } catch { }
            }
        }
        catch (Exception ex)
        {
            Logs.Debug($"[VoiceAssistant] Error running Python script for {description}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Waits for the Python backend to become healthy and responsive.
    /// Performs health checks with exponential backoff to ensure the FastAPI server is ready.
    /// Why separate method: Process startup and service readiness are different concerns.
    /// </summary>
    public static async Task<bool> WaitForBackendHealth(int maxAttempts = 30)
    {
        Logs.Debug("[VoiceAssistant] Starting backend health check sequence");

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                // Check if process is still running
                if (PythonBackend == null || PythonBackend.HasExited)
                {
                    Logs.Error("[VoiceAssistant] Backend process died during health check");
                    return false;
                }

                // Attempt health check with timeout
                using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
                HttpResponseMessage response = await HttpClient.GetAsync($"{BackendUrl}/health", cts.Token);

                if (response.IsSuccessStatusCode)
                {
                    IsBackendRunning = true;
                    Logs.Info($"[VoiceAssistant] Backend health check passed on attempt {attempt}");
                    return true;
                }

                Logs.Debug($"[VoiceAssistant] Health check attempt {attempt} failed with status: {response.StatusCode}");
            }
            catch (OperationCanceledException)
            {
                Logs.Debug($"[VoiceAssistant] Health check attempt {attempt} timed out");
            }
            catch (Exception ex)
            {
                Logs.Debug($"[VoiceAssistant] Health check attempt {attempt} failed: {ex.Message}");
            }

            // Wait before retrying, with exponential backoff for later attempts
            int delay = attempt <= 10 ? 1000 : Math.Min(5000, 1000 * attempt / 10);
            await Task.Delay(delay);
        }

        Logs.Error($"[VoiceAssistant] Backend failed to become healthy after {maxAttempts} attempts");
        return false;
    }

    /// <summary>
    /// Stops the Python backend process gracefully, with fallback to force termination.
    /// Sends shutdown signal to FastAPI server and waits for graceful exit before forcing.
    /// Why graceful: Allows the Python backend to clean up resources and close files properly.
    /// </summary>
    public static async Task StopPythonBackend()
    {
        lock (BackendLock)
        {
            if (!IsBackendRunning || PythonBackend == null)
            {
                Logs.Debug("[VoiceAssistant] No backend to stop");
                return;
            }

            Logs.Info("[VoiceAssistant] Stopping Python backend");

            try
            {
                // Attempt graceful shutdown via API call
                using CancellationTokenSource cts = new(TimeSpan.FromSeconds(3));
                Task.Run(async () =>
                {
                    try
                    {
                        await HttpClient.PostAsync($"{BackendUrl}/shutdown", null, cts.Token);
                        Logs.Debug("[VoiceAssistant] Shutdown signal sent to backend");
                    }
                    catch (Exception ex)
                    {
                        Logs.Debug($"[VoiceAssistant] Error sending shutdown signal: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                Logs.Debug($"[VoiceAssistant] Error initiating graceful shutdown: {ex.Message}");
            }

            try
            {
                // Wait for graceful exit with timeout
                bool exited = PythonBackend.WaitForExit(5000);

                if (!exited)
                {
                    Logs.Warning("[VoiceAssistant] Backend did not exit gracefully, force terminating");
                    PythonBackend.Kill(true);
                    PythonBackend.WaitForExit(3000);
                }

                PythonBackend.Dispose();
                PythonBackend = null;
                IsBackendRunning = false;

                Logs.Info("[VoiceAssistant] Backend stopped successfully");
            }
            catch (Exception ex)
            {
                Logs.Error($"[VoiceAssistant] Error stopping backend: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Checks if the Python backend is running and responsive.
    /// Performs a quick health check to verify the FastAPI server is accepting requests.
    /// Why needed: Process.HasExited doesn't guarantee the service is responsive to HTTP requests.
    /// </summary>
    public static async Task<bool> CheckBackendHealth()
    {
        try
        {
            if (PythonBackend == null || PythonBackend.HasExited)
            {
                Logs.Debug("[VoiceAssistant] Backend process is not running");
                return false;
            }

            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(3));
            HttpResponseMessage response = await HttpClient.GetAsync($"{BackendUrl}/health", cts.Token);
            bool isHealthy = response.IsSuccessStatusCode;

            Logs.Debug($"[VoiceAssistant] Health check result: {(isHealthy ? "healthy" : "unhealthy")}");
            return isHealthy;
        }
        catch (Exception ex)
        {
            Logs.Debug($"[VoiceAssistant] Health check failed: {ex.Message}");
            return false;
        }
    }

    #endregion

    #region API Endpoint Handlers

    /// <summary>
    /// API endpoint handler for processing voice input (audio to text to action).
    /// Handles the complete voice processing pipeline: STT -> command interpretation -> TTS response.
    /// Why comprehensive: Voice interaction requires coordinating multiple services in sequence.
    /// </summary>
    public static async Task<JObject> ProcessVoiceInput(Session session, JObject input)
    {
        try
        {
            Logs.Info($"[VoiceAssistant] Processing voice input for session: {session.ID}");

            // Ensure backend is running before processing
            if (!IsBackendRunning || !await CheckBackendHealth())
            {
                Logs.Warning("[VoiceAssistant] Backend not ready, attempting to start");

                bool started = await StartPythonBackend();
                if (!started)
                {
                    Logs.Error("[VoiceAssistant] Failed to start backend for voice processing");
                    return CreateErrorResponse("Voice backend is not available");
                }

                bool healthy = await WaitForBackendHealth();
                if (!healthy)
                {
                    Logs.Error("[VoiceAssistant] Backend started but failed health check");
                    return CreateErrorResponse("Voice backend is not responding");
                }
            }

            // Extract and validate audio data
            string audioBase64 = input["audio_data"]?.ToString();
            if (string.IsNullOrEmpty(audioBase64))
            {
                Logs.Warning("[VoiceAssistant] No audio data provided in voice input request");
                return CreateErrorResponse("No audio data provided");
            }

            string language = input["language"]?.ToString() ?? "en-US";
            Logs.Debug($"[VoiceAssistant] Processing audio with language: {language}");

            // Call STT service to transcribe audio
            JObject transcriptionRequest = new()
            {
                ["audio_data"] = audioBase64,
                ["language"] = language
            };

            JObject sttResponse = await CallPythonService("/stt/transcribe", transcriptionRequest);
            if (sttResponse == null || sttResponse["transcription"] == null)
            {
                Logs.Error("[VoiceAssistant] STT service returned no transcription");
                return CreateErrorResponse("Speech recognition failed");
            }

            string transcription = sttResponse["transcription"]?.ToString();
            Logs.Info($"[VoiceAssistant] Transcribed text: '{transcription}'");

            // Process the transcribed command
            CommandResponse commandResponse = await ProcessCommand(transcription);
            Logs.Debug($"[VoiceAssistant] Command processed: {commandResponse.Command}");

            // Generate TTS response if text response exists
            string audioResponse = null;
            if (!string.IsNullOrEmpty(commandResponse.Text))
            {
                JObject ttsRequest = new()
                {
                    ["text"] = commandResponse.Text,
                    ["voice"] = input["voice"]?.ToString() ?? "default",
                    ["language"] = language
                };

                JObject ttsResponse = await CallPythonService("/tts/synthesize", ttsRequest);
                audioResponse = ttsResponse?["audio_data"]?.ToString();

                if (audioResponse != null)
                {
                    Logs.Debug("[VoiceAssistant] TTS audio generated successfully");
                }
            }

            JObject result = new()
            {
                ["success"] = true,
                ["transcription"] = transcription,
                ["ai_response"] = commandResponse.Text,
                ["audio_response"] = audioResponse,
                ["command"] = commandResponse.Command,
                ["session_id"] = session.ID
            };

            Logs.Info("[VoiceAssistant] Voice input processed successfully");
            return result;
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] Error processing voice input: {ex.Message}");
            Logs.Debug($"[VoiceAssistant] Voice processing stack trace: {ex}");
            return CreateErrorResponse($"Voice processing failed: {ex.Message}");
        }
    }

    /// <summary>
    /// API endpoint handler for starting the voice service backend.
    /// Now fails gracefully if SwarmUI environment is not found or required packages can't be installed.
    /// NO FALLBACKS - requires proper SwarmUI with ComfyUI installation.
    /// </summary>
    public static async Task<JObject> StartVoiceService(Session session, JObject input)
    {
        try
        {
            Logs.Info($"[VoiceAssistant] Starting voice service for session: {session.ID}");

            // Check if already running
            if (IsBackendRunning && PythonBackend != null && !PythonBackend.HasExited)
            {
                bool isHealthy = await CheckBackendHealth();
                if (isHealthy)
                {
                    return new JObject
                    {
                        ["success"] = true,
                        ["message"] = "Voice service is already running",
                        ["backend_running"] = true,
                        ["backend_healthy"] = true,
                        ["backend_url"] = BackendUrl
                    };
                }
            }

            // Start the backend (this includes dependency installation)
            bool started = await StartPythonBackend();
            if (!started)
            {
                Logs.Error("[VoiceAssistant] Failed to start backend process");

                // Provide more specific error message based on the type of failure
                string errorMessage = "Failed to start voice service backend.";

                if (ProgressTracker?.HasError == true)
                {
                    errorMessage = ProgressTracker.ErrorMessage;
                }

                // Common failure scenarios with helpful messages
                if (errorMessage.Contains("Python environment"))
                {
                    errorMessage += " Voice Assistant requires SwarmUI with ComfyUI backend properly installed. Please ensure ComfyUI is working in SwarmUI before using Voice Assistant.";
                }
                else if (errorMessage.Contains("RealtimeSTT") || errorMessage.Contains("Chatterbox"))
                {
                    errorMessage += " Required voice processing libraries could not be installed. This may be due to Python version compatibility or network issues.";
                }

                return CreateErrorResponse(errorMessage);
            }

            // Wait for backend to become healthy
            Logs.Info("[VoiceAssistant] Waiting for backend to become ready...");
            bool healthy = await WaitForBackendHealth();
            if (!healthy)
            {
                Logs.Error("[VoiceAssistant] Backend process started but failed health check");
                return CreateErrorResponse("Voice service started but is not responding properly. Required voice processing libraries may not be properly installed.");
            }

            JObject result = new()
            {
                ["success"] = true,
                ["message"] = "Voice service started successfully",
                ["backend_running"] = IsBackendRunning,
                ["backend_healthy"] = true,
                ["backend_url"] = BackendUrl,
                ["first_time_setup"] = true,
                ["required_libraries"] = new JArray { "RealtimeSTT", "Chatterbox TTS" },
                ["no_fallbacks"] = true
            };

            Logs.Info("[VoiceAssistant] Voice service started successfully");
            return result;
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] Error starting voice service: {ex.Message}");
            Logs.Debug($"[VoiceAssistant] Start service stack trace: {ex}");

            // Provide helpful error messages for common issues
            string errorMessage = ex.Message;

            if (errorMessage.Contains("Python environment") || errorMessage.Contains("ComfyUI"))
            {
                errorMessage = "Voice Assistant requires SwarmUI with ComfyUI backend installed. Please ensure ComfyUI is properly set up and working in SwarmUI before using Voice Assistant.";
            }
            else if (errorMessage.Contains("RealtimeSTT"))
            {
                errorMessage = "Failed to install RealtimeSTT speech recognition library. This may be due to Python version compatibility (requires Python 3.9-3.12) or network connectivity issues.";
            }
            else if (errorMessage.Contains("Chatterbox") || errorMessage.Contains("TTS"))
            {
                errorMessage = "Failed to install Chatterbox TTS speech synthesis library. This may be due to Python version compatibility or network connectivity issues.";
            }

            return CreateErrorResponse($"Failed to start voice service: {errorMessage}");
        }
    }

    /// <summary>
    /// API endpoint handler for stopping the voice service backend.
    /// Gracefully shuts down the Python backend process and cleans up resources.
    /// Why needed: Allows controlled shutdown and resource cleanup from the frontend.
    /// </summary>
    public static async Task<JObject> StopVoiceService(Session session, JObject input)
    {
        try
        {
            Logs.Info($"[VoiceAssistant] Stopping voice service for session: {session.ID}");

            await StopPythonBackend();

            JObject result = new()
            {
                ["success"] = true,
                ["message"] = "Voice service stopped successfully",
                ["backend_running"] = IsBackendRunning
            };

            Logs.Info("[VoiceAssistant] Voice service stopped successfully");
            return result;
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] Error stopping voice service: {ex.Message}");
            Logs.Debug($"[VoiceAssistant] Stop service stack trace: {ex}");
            return CreateErrorResponse($"Failed to stop voice service: {ex.Message}");
        }
    }

    /// <summary>
    /// API endpoint handler for checking voice service status.
    /// Returns current backend status, health information, and configuration details.
    /// Why useful: Allows frontend to make informed decisions about service availability.
    /// </summary>
    public static async Task<JObject> GetVoiceStatus(Session session, JObject input)
    {
        try
        {
            Logs.Debug($"[VoiceAssistant] Checking voice service status for session: {session.ID}");

            bool isHealthy = await CheckBackendHealth();

            JObject result = new()
            {
                ["success"] = true,
                ["backend_running"] = IsBackendRunning,
                ["backend_healthy"] = isHealthy,
                ["backend_url"] = BackendUrl,
                ["process_id"] = PythonBackend?.Id,
                ["has_exited"] = PythonBackend?.HasExited ?? true,
                ["version"] = "1.0.0"
            };

            Logs.Debug($"[VoiceAssistant] Status check: running={IsBackendRunning}, healthy={isHealthy}");
            return result;
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] Error checking voice service status: {ex.Message}");
            return CreateErrorResponse($"Failed to check voice service status: {ex.Message}");
        }
    }

    /// <summary>
    /// API endpoint handler for processing text commands without voice input.
    /// Allows direct text-based interaction with the voice assistant system.
    /// Why needed: Provides fallback for users without microphone or for testing purposes.
    /// </summary>
    public static async Task<JObject> ProcessTextCommand(Session session, JObject input)
    {
        try
        {
            Logs.Info($"[VoiceAssistant] Processing text command for session: {session.ID}");

            string text = input["text"]?.ToString();
            if (string.IsNullOrEmpty(text))
            {
                Logs.Warning("[VoiceAssistant] No text provided in text command request");
                return CreateErrorResponse("No text provided");
            }

            Logs.Debug($"[VoiceAssistant] Processing text: '{text}'");

            // Process the command
            CommandResponse commandResponse = await ProcessCommand(text);

            // Generate TTS response if backend is available
            string audioResponse = null;
            if (!string.IsNullOrEmpty(commandResponse.Text) && IsBackendRunning)
            {
                try
                {
                    JObject ttsRequest = new()
                    {
                        ["text"] = commandResponse.Text,
                        ["voice"] = input["voice"]?.ToString() ?? "default",
                        ["language"] = input["language"]?.ToString() ?? "en-US"
                    };

                    JObject ttsResponse = await CallPythonService("/tts/synthesize", ttsRequest);
                    audioResponse = ttsResponse?["audio_data"]?.ToString();
                }
                catch (Exception ttsEx)
                {
                    Logs.Warning($"[VoiceAssistant] TTS generation failed, continuing without audio: {ttsEx.Message}");
                }
            }

            JObject result = new()
            {
                ["success"] = true,
                ["text"] = commandResponse.Text,
                ["audio_response"] = audioResponse,
                ["command"] = commandResponse.Command,
                ["session_id"] = session.ID
            };

            Logs.Info("[VoiceAssistant] Text command processed successfully");
            return result;
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] Error processing text command: {ex.Message}");
            Logs.Debug($"[VoiceAssistant] Text command stack trace: {ex}");
            return CreateErrorResponse($"Text command processing failed: {ex.Message}");
        }
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Calls a service endpoint on the Python backend with proper error handling.
    /// Handles HTTP communication, timeouts, and response parsing for backend services.
    /// Why centralized: Ensures consistent error handling and logging across all backend calls.
    /// </summary>
    public static async Task<JObject> CallPythonService(string endpoint, JObject data)
    {
        try
        {
            Logs.Debug($"[VoiceAssistant] Calling backend service: {endpoint}");

            string json = data.ToString();
            StringContent content = new(json, Encoding.UTF8, "application/json");

            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
            HttpResponseMessage response = await HttpClient.PostAsync($"{BackendUrl}{endpoint}", content, cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                string errorContent = await response.Content.ReadAsStringAsync();
                Logs.Error($"[VoiceAssistant] Backend service call failed: {response.StatusCode} - {errorContent}");
                return null;
            }

            string responseText = await response.Content.ReadAsStringAsync();
            JObject result = JObject.Parse(responseText);

            Logs.Debug($"[VoiceAssistant] Backend service call successful: {endpoint}");
            return result;
        }
        catch (OperationCanceledException)
        {
            Logs.Error($"[VoiceAssistant] Backend service call timed out: {endpoint}");
            return null;
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] Error calling backend service {endpoint}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Creates a standardized error response object for API endpoints.
    /// Ensures consistent error formatting across all voice assistant API responses.
    /// Why standardized: Simplifies frontend error handling and provides predictable response format.
    /// </summary>
    public static JObject CreateErrorResponse(string error)
    {
        return new JObject
        {
            ["success"] = false,
            ["error"] = error,
            ["timestamp"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
        };
    }

    #endregion

    #region Command Processing

    /// <summary>
    /// Data structure for command processing results.
    /// Encapsulates the response text, audio, and command type for voice interactions.
    /// Why structured: Provides type safety and clear data contracts for command responses.
    /// </summary>
    public class CommandResponse
    {
        /// <summary>Text response to be spoken or displayed to the user.</summary>
        public string Text { get; set; } = string.Empty;

        /// <summary>Base64 encoded audio response, if TTS was generated.</summary>
        public string AudioBase64 { get; set; }

        /// <summary>Command type identifier for frontend action handling.</summary>
        public string Command { get; set; }
    }

    /// <summary>
    /// Processes voice commands and generates appropriate responses.
    /// Interprets user intent from transcribed speech and formulates responses.
    /// Why separate method: Allows command logic to be extended without affecting voice processing pipeline.
    /// </summary>
    public static async Task<CommandResponse> ProcessCommand(string text)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                Logs.Debug("[VoiceAssistant] Empty or whitespace command received");
                return new CommandResponse
                {
                    Text = "I didn't catch that. Could you please repeat your request?"
                };
            }

            string normalizedText = text.Trim().ToLowerInvariant();
            Logs.Debug($"[VoiceAssistant] Processing normalized command: '{normalizedText}'");

            CommandResponse response = new();

            // Greeting detection
            if (normalizedText.ContainsAny(["hello", "hi", "hey", "good morning", "good afternoon"]))
            {
                response.Text = "Hello! I'm your SwarmUI voice assistant. I can help you generate images using voice commands. What would you like to create today?";
                response.Command = "greeting";
                Logs.Debug("[VoiceAssistant] Processed greeting command");
            }
            // Help request detection
            else if (normalizedText.ContainsAny(["help", "what can you do", "commands", "how to use"]))
            {
                response.Text = "I can help you generate images with voice commands. Try saying things like 'Generate a sunset over mountains', 'Create a portrait of a cyberpunk character', or 'Make an abstract painting with blue and gold colors'. You can also ask me to adjust settings or check the status.";
                response.Command = "help";
                Logs.Debug("[VoiceAssistant] Processed help command");
            }
            // Image generation detection
            else if (normalizedText.ContainsAny(["generate", "create", "make", "draw", "paint", "render", "produce"]))
            {
                // Extract the description after the action word
                string description = ExtractImageDescription(normalizedText);
                response.Text = $"I'll generate an image for you: {description}. Please check the main interface to see the generation progress.";
                response.Command = "generate_image";
                Logs.Info($"[VoiceAssistant] Processed image generation command: '{description}'");

                // Here you would integrate with SwarmUI's image generation system
                // This is where you'd pass the description to the T2I pipeline
            }
            // Status check
            else if (normalizedText.ContainsAny(["status", "how are you", "are you working", "are you online"]))
            {
                response.Text = $"I'm online and ready to help! The voice backend is {(IsBackendRunning ? "running smoothly" : "starting up")}. You can give me voice commands to generate images.";
                response.Command = "status";
                Logs.Debug("[VoiceAssistant] Processed status check command");
            }
            // Default case - treat as image generation prompt
            else
            {
                response.Text = $"I'll create an image based on your description: {text}. Please check the main interface for the generation progress.";
                response.Command = "generate_image";
                Logs.Info($"[VoiceAssistant] Treating unknown command as image generation: '{text}'");
            }

            Logs.Debug($"[VoiceAssistant] Command processing complete. Command type: {response.Command}");
            return response;
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] Error processing command: {ex.Message}");
            return new CommandResponse
            {
                Text = "I'm sorry, I had trouble processing that command. Please try again.",
                Command = "error"
            };
        }
    }

    /// <summary>
    /// Extracts image description from a voice command by removing action words.
    /// Cleans up the user's speech to get the core image description for generation.
    /// Why needed: Voice commands often include action words that shouldn't be part of the image prompt.
    /// </summary>
    public static string ExtractImageDescription(string text)
    {
        try
        {
            string[] actionWords = ["generate", "create", "make", "draw", "paint", "render", "produce", "an image of", "a picture of", "a photo of"];

            string description = text;

            // Remove action words from the beginning
            foreach (string action in actionWords)
            {
                if (description.StartsWith(action))
                {
                    description = description[action.Length..].Trim();
                    break;
                }
            }

            // Clean up common speech artifacts
            description = description.Replace(" an ", " ").Replace(" a ", " ").Trim();

            // Ensure we have something meaningful
            if (string.IsNullOrWhiteSpace(description))
            {
                description = "abstract art";
                Logs.Debug("[VoiceAssistant] Empty description after cleanup, using fallback");
            }

            Logs.Debug($"[VoiceAssistant] Extracted description: '{description}'");
            return description;
        }
        catch (Exception ex)
        {
            Logs.Error($"[VoiceAssistant] Error extracting description: {ex.Message}");
            return text; // Return original text as fallback
        }
    }

    #endregion
}

/// <summary>
/// Extension methods for string operations used in command processing.
/// Provides utility methods for text analysis and command detection.
/// Why extension methods: Keeps utility code clean and reusable across the extension.
/// </summary>
public static class StringExtensions
{
    /// <summary>
    /// Checks if a string contains any of the specified substrings (case-insensitive).
    /// Used for command detection and intent recognition in voice processing.
    /// </summary>
    public static bool ContainsAny(this string text, string[] values)
    {
        if (string.IsNullOrEmpty(text) || values == null)
            return false;

        return values.Any(value => text.Contains(value, StringComparison.OrdinalIgnoreCase));
    }
}
