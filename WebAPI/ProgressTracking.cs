using SwarmUI.Utils;
using System.Collections.Concurrent;
using Hartsy.Extensions.VoiceAssistant.WebAPI.Models;

namespace Hartsy.Extensions.VoiceAssistant.Progress;

/// <summary>Central progress tracking system with modern DRY implementation.</summary>
public static class ProgressTracking
{
    private static readonly ConcurrentDictionary<string, ProgressTracker> _trackers = new();
    private static readonly Timer _cleanupTimer;

    static ProgressTracking()
    {
        // Auto-cleanup completed trackers every 5 minutes
        _cleanupTimer = new Timer(CleanupCompletedTrackers, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    /// <summary>Gets or creates a progress tracker for installation operations.</summary>
    public static ProgressTracker Installation => GetOrCreateTracker("installation", ProgressTracker.TrackerType.Installation);

    /// <summary>Gets or creates a progress tracker for API operations.</summary>
    public static ProgressTracker GetApiTracker(string operationId) => GetOrCreateTracker($"api_{operationId}", ProgressTracker.TrackerType.Api);

    /// <summary>Gets or creates a progress tracker for health check operations.</summary>
    public static ProgressTracker HealthCheck => GetOrCreateTracker("health_check", ProgressTracker.TrackerType.HealthCheck);

    /// <summary>Creates a new health check tracker with custom id.</summary>
    public static ProgressTracker CreateHealthCheckTracker(string id) => GetOrCreateTracker(id, ProgressTracker.TrackerType.HealthCheck);

    /// <summary>Gets or creates a tracker with the specified type.</summary>
    private static ProgressTracker GetOrCreateTracker(string id, ProgressTracker.TrackerType type)
    {
        return _trackers.GetOrAdd(id, _ => new ProgressTracker(id, type));
    }

    /// <summary>Removes a specific tracker.</summary>
    public static bool RemoveTracker(string trackerId) => _trackers.TryRemove(trackerId, out _);

    /// <summary>Gets all active trackers.</summary>
    public static IReadOnlyCollection<ProgressTracker> GetActiveTrackers() => _trackers.Values.ToList().AsReadOnly();

    /// <summary>Cleanup completed trackers automatically.</summary>
    private static void CleanupCompletedTrackers(object state)
    {
        List<string> toRemove = [];
        foreach (KeyValuePair<string, ProgressTracker> kvp in _trackers)
        {
            ProgressTracker tracker = kvp.Value;
            // Remove trackers completed more than 10 minutes ago
            if (tracker.IsComplete && tracker.EndTime.HasValue &&
                DateTime.UtcNow - tracker.EndTime.Value > TimeSpan.FromMinutes(10))
            {
                toRemove.Add(kvp.Key);
            }
        }
        foreach (string id in toRemove)
        {
            _trackers.TryRemove(id, out _);
        }
        if (toRemove.Count > 0)
        {
            Logs.Debug($"[VoiceAssistant] Cleaned up {toRemove.Count} completed progress trackers");
        }
    }
}

/// <summary>Modern unified progress tracker with specialized data based on type.</summary>
public class ProgressTracker
{
    public enum TrackerType { Installation, Api, HealthCheck }

    private readonly object _lock = new();
    private volatile int _progress = 0;
    private volatile bool _isComplete = false;
    private volatile bool _hasError = false;

    public string Id { get; }
    public TrackerType Type { get; }
    public int Progress => _progress;
    public string CurrentStep { get; private set; } = "";
    public string StatusMessage { get; private set; } = "";
    public bool IsComplete => _isComplete;
    public bool HasError => _hasError;
    public string ErrorMessage { get; private set; } = "";
    public DateTime StartTime { get; } = DateTime.UtcNow;
    public DateTime? EndTime { get; private set; }
    public TimeSpan Duration => (EndTime ?? DateTime.UtcNow) - StartTime;

    // Specialized data - only populated for relevant tracker types
    public InstallationData Installation { get; }
    public ApiData Api { get; }
    public HealthCheckData HealthCheck { get; }

    // Convenience properties for backward compatibility
    public int DownloadProgress => Installation?.DownloadProgress ?? 0;
    public int MaxAttempts => HealthCheck?.MaxAttempts ?? 30;

    public ProgressTracker(string id, TrackerType type)
    {
        Id = id;
        Type = type;
        Installation = type == TrackerType.Installation ? new InstallationData() : null;
        Api = type == TrackerType.Api ? new ApiData() : null;
        HealthCheck = type == TrackerType.HealthCheck ? new HealthCheckData() : null;
    }

    /// <summary>Updates progress with flexible parameters for all tracker types.</summary>
    public void UpdateProgress(int progress, string step, string message = "", string package = "", int downloadProgress = 0)
    {
        lock (_lock)
        {
            _progress = Math.Clamp(progress, 0, 100);
            CurrentStep = step ?? "";
            StatusMessage = message ?? "";

            // Update type-specific data
            if (Installation != null && !string.IsNullOrEmpty(package))
            {
                Installation.CurrentPackage = package;
                Installation.DownloadProgress = Math.Clamp(downloadProgress, 0, 100);
            }

            LogProgress();
        }
    }

    /// <summary>Sets error state with automatic completion.</summary>
    public void SetError(string errorMessage)
    {
        lock (_lock)
        {
            _hasError = true;
            ErrorMessage = errorMessage ?? "";
            EndTime = DateTime.UtcNow;

            // Handle type-specific error logic
            if (Installation != null && !string.IsNullOrEmpty(Installation.CurrentPackage))
            {
                Installation.FailedPackages.Add(Installation.CurrentPackage);
            }

            Logs.Error($"[VoiceAssistant] {Type} error [{Id}]: {errorMessage}");
        }
    }

    /// <summary>Marks tracker as complete.</summary>
    public void SetComplete(string finalMessage = "")
    {
        lock (_lock)
        {
            _isComplete = true;
            _progress = 100;
            EndTime = DateTime.UtcNow;

            if (!string.IsNullOrEmpty(finalMessage))
            {
                StatusMessage = finalMessage;
            }
            else if (string.IsNullOrEmpty(StatusMessage))
            {
                StatusMessage = $"{Type} completed successfully";
            }

            Logs.Info($"[VoiceAssistant] {Type} [{Id}] completed in {Duration.TotalSeconds:F1} seconds");
        }
    }

    /// <summary>Resets tracker to initial state.</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _progress = 0;
            _isComplete = false;
            _hasError = false;
            CurrentStep = "";
            StatusMessage = "";
            ErrorMessage = "";
            EndTime = null;

            // Reset type-specific data
            Installation?.Reset();
            Api?.Reset();
            HealthCheck?.Reset();
        }
    }

    /// <summary>Adds a completed package (Installation trackers only).</summary>
    public void AddCompletedPackage(string package, string version = "")
    {
        if (Installation == null) return;
        lock (_lock)
        {
            if (!Installation.CompletedPackages.Contains(package))
            {
                Installation.CompletedPackages.Add(package);
                if (!string.IsNullOrEmpty(version))
                {
                    Installation.PackageVersions[package] = version;
                }
            }
        }
    }

    /// <summary>Sets operation name (API trackers only).</summary>
    public void SetOperation(string operation)
    {
        if (Api == null) return;
        lock (_lock)
        {
            Api.Operation = operation ?? "";
        }
    }

    /// <summary>Increments health check attempt (HealthCheck trackers only).</summary>
    public void IncrementHealthAttempt(int maxAttempts = 30)
    {
        if (HealthCheck == null) return;
        lock (_lock)
        {
            HealthCheck.AttemptCount++;
            HealthCheck.MaxAttempts = maxAttempts;
            _progress = (int)((double)HealthCheck.AttemptCount / maxAttempts * 100);
            StatusMessage = $"Health check attempt {HealthCheck.AttemptCount}/{maxAttempts}";
        }
    }

    /// <summary>Marks health check as healthy and complete.</summary>
    public void SetHealthy()
    {
        if (HealthCheck == null) return;
        lock (_lock)
        {
            HealthCheck.IsHealthy = true;
            SetComplete("Backend is healthy");
        }
    }

    /// <summary>Converts to API response format for Installation trackers.</summary>
    public InstallationProgressResponse ToInstallationResponse()
    {
        if (Installation == null) throw new InvalidOperationException("Not an installation tracker");
        lock (_lock)
        {
            return new InstallationProgressResponse
            {
                Success = !HasError,
                Progress = Progress,
                CurrentStep = CurrentStep,
                CurrentPackage = Installation.CurrentPackage,
                DownloadProgress = Installation.DownloadProgress,
                StatusMessage = StatusMessage,
                IsComplete = IsComplete,
                HasError = HasError,
                ErrorMessage = ErrorMessage
            };
        }
    }

    /// <summary>Backward compatibility alias for ToInstallationResponse.</summary>
    public InstallationProgressResponse ToResponse() => ToInstallationResponse();

    /// <summary>Backward compatibility alias for IncrementHealthAttempt.</summary>
    public void IncrementAttempt(int maxAttempts = 30) => IncrementHealthAttempt(maxAttempts);

    private void LogProgress()
    {
        string logLevel = Type == TrackerType.Api ? "Debug" : "Info";
        string message = Type switch
        {
            TrackerType.Installation when Installation != null =>
                $"Installation Progress: {Progress}% - {CurrentStep} - {Installation.CurrentPackage} ({Installation.DownloadProgress}%) - {StatusMessage}",
            TrackerType.Api =>
                $"API Progress [{Id}]: {Progress}% - {CurrentStep} - {StatusMessage}",
            TrackerType.HealthCheck =>
                $"Health Check Progress: {Progress}% - {CurrentStep} - {StatusMessage}",
            _ =>
                $"{Type} Progress [{Id}]: {Progress}% - {CurrentStep} - {StatusMessage}"
        };

        if (logLevel == "Debug")
        {
            Logs.Debug($"[VoiceAssistant] {message}");
        }
        else
        {
            Logs.Info($"[VoiceAssistant] {message}");
        }
    }
}

/// <summary>Installation-specific tracking data.</summary>
public class InstallationData
{
    public string CurrentPackage { get; set; } = "";
    public int DownloadProgress { get; set; } = 0;
    public List<string> CompletedPackages { get; } = [];
    public List<string> FailedPackages { get; } = [];
    public Dictionary<string, string> PackageVersions { get; } = new();

    public void Reset()
    {
        CurrentPackage = "";
        DownloadProgress = 0;
        CompletedPackages.Clear();
        FailedPackages.Clear();
        PackageVersions.Clear();
    }
}

/// <summary>API-specific tracking data.</summary>
public class ApiData
{
    public string Operation { get; set; } = "";

    public void Reset()
    {
        Operation = "";
    }
}

/// <summary>Health check-specific tracking data.</summary>
public class HealthCheckData
{
    public int AttemptCount { get; set; } = 0;
    public int MaxAttempts { get; set; } = 30;
    public bool IsHealthy { get; set; } = false;

    public void Reset()
    {
        AttemptCount = 0;
        MaxAttempts = 30;
        IsHealthy = false;
    }
}
