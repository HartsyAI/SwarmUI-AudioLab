using SwarmUI.Utils;
using System.Collections.Concurrent;
using Hartsy.Extensions.AudioLab.WebAPI.Models;
using Newtonsoft.Json.Linq;

namespace Hartsy.Extensions.AudioLab.Progress;

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

    /// <summary>Gets or creates a progress tracker for Job operations.</summary>
    public static ProgressTracker GetJobTracker(string operationId) => GetOrCreateTracker($"job_{operationId}", ProgressTracker.TrackerType.Job);

    /// <summary>Gets or creates a progress tracker for health check operations.</summary>
    public static ProgressTracker HealthCheck => GetOrCreateTracker("health_check", ProgressTracker.TrackerType.HealthCheck);

    /// <summary>Creates a new health check tracker with custom id and max attempts.</summary>
    public static ProgressTracker CreateHealthCheckTracker(string id, int maxAttempts)
    {
        ProgressTracker tracker = GetOrCreateTracker(id, ProgressTracker.TrackerType.HealthCheck);
        if (tracker.HealthCheck != null)
        {
            tracker.HealthCheck.MaxAttempts = maxAttempts;
        }
        return tracker;
    }

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
            Logs.Debug($"[AudioLab] Cleaned up {toRemove.Count} completed progress trackers");
        }
    }
}

/// <summary>Modern unified progress tracker with specialized data based on type.</summary>
public class ProgressTracker(string id, ProgressTracker.TrackerType type)
{
    public enum TrackerType { Installation, Job, HealthCheck }

    private readonly object _lock = new();
    private volatile int _progress = 0;
    private volatile bool _isComplete = false;
    private volatile bool _hasError = false;

    public string Id { get; } = id;
    public TrackerType Type { get; } = type;
    public int Progress => _progress;
    public int DownloadProgress => Installation?.DownloadProgress ?? 0;
    public string CurrentStep { get; private set; } = "";
    public string CurrentPackage => Installation?.CurrentPackage ?? "";
    public string StatusMessage { get; private set; } = "";
    public bool IsComplete => _isComplete;
    public List<string> CompletedPackages => [.. Installation.CompletedPackages];
    public bool HasError => _hasError;
    public string ErrorMessage { get; private set; } = "";
    public DateTime StartTime { get; } = DateTime.UtcNow;
    public DateTime? EndTime { get; private set; }
    public TimeSpan Duration => (EndTime ?? DateTime.UtcNow) - StartTime;

    // Specialized data - only populated for relevant tracker types
    public InstallationProgressResponse Installation { get; } = type == TrackerType.Installation ? new InstallationProgressResponse() : null;
    public JobData Job { get; } = type == TrackerType.Job ? new JobData() : null;
    public BackendHealthInfo HealthCheck { get; } = type == TrackerType.HealthCheck ? new BackendHealthInfo() : null;

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
                Installation.Progress = Math.Clamp(downloadProgress, 0, 100);
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
                Installation.FailedPackages += Installation.CurrentPackage;
            }

            Logs.Error($"[AudioLab] {Type} error [{Id}]: {errorMessage}");
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

            Logs.Info($"[AudioLab] {Type} [{Id}] completed in {Duration.TotalSeconds:F1} seconds");
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
            Job?.Reset();
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
                List<string> completedPackagesList = [.. Installation.CompletedPackages];
                completedPackagesList.Add(package);
                Installation.CompletedPackages = [.. completedPackagesList];
            }
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

    private void LogProgress()
    {
        string logLevel = Type == TrackerType.Job ? "Debug" : "Info";
        string message = Type switch
        {
            TrackerType.Installation when Installation != null =>
                $"Installation Progress: {Progress}% - {CurrentStep} - {Installation.CurrentPackage} ({Installation.DownloadProgress}%) - {StatusMessage}",
            TrackerType.Job =>
                $"Job Progress [{Id}]: {Progress}% - {CurrentStep} - {StatusMessage}",
            TrackerType.HealthCheck =>
                $"Health Check Progress: {Progress}% - {CurrentStep} - {StatusMessage}",
            _ =>
                $"{Type} Progress [{Id}]: {Progress}% - {CurrentStep} - {StatusMessage}"
        };

        if (logLevel == "Debug")
        {
            Logs.Debug($"[AudioLab] {message}");
        }
        else
        {
            Logs.Info($"[AudioLab] {message}");
        }
    }
}
