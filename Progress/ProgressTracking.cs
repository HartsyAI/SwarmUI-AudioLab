using SwarmUI.Utils;
using System.Collections.Concurrent;
using Hartsy.Extensions.VoiceAssistant.Models;

namespace Hartsy.Extensions.VoiceAssistant.Progress;

/// <summary>
/// Enhanced progress tracking system for various operations in the Voice Assistant.
/// Thread-safe and supports multiple concurrent operations.
/// </summary>
public static class ProgressTracking
{
    private static readonly ConcurrentDictionary<string, IProgressTracker> _trackers = new();

    /// <summary>
    /// Gets or creates a progress tracker for installation operations.
    /// </summary>
    public static InstallationProgressTracker Installation =>
        (InstallationProgressTracker)_trackers.GetOrAdd("installation", _ => new InstallationProgressTracker());

    /// <summary>
    /// Gets or creates a progress tracker for API operations.
    /// </summary>
    public static ApiProgressTracker GetApiTracker(string operationId) =>
        (ApiProgressTracker)_trackers.GetOrAdd($"api_{operationId}", _ => new ApiProgressTracker(operationId));

    /// <summary>
    /// Removes a completed tracker to free memory.
    /// </summary>
    public static void RemoveTracker(string trackerId)
    {
        _trackers.TryRemove(trackerId, out _);
    }

    /// <summary>
    /// Gets all active trackers (for debugging/monitoring).
    /// </summary>
    public static IEnumerable<IProgressTracker> GetActiveTrackers()
    {
        return _trackers.Values.ToList();
    }
}

/// <summary>
/// Base interface for progress tracking operations.
/// </summary>
public interface IProgressTracker
{
    string Id { get; }
    int Progress { get; }
    string CurrentStep { get; }
    string StatusMessage { get; }
    bool IsComplete { get; }
    bool HasError { get; }
    string ErrorMessage { get; }
    DateTime StartTime { get; }
    DateTime? EndTime { get; }

    void UpdateProgress(int progress, string step, string message = "");
    void SetError(string errorMessage);
    void SetComplete();
    void Reset();
}

/// <summary>
/// Progress tracker for installation operations with detailed package tracking.
/// </summary>
public class InstallationProgressTracker : IProgressTracker
{
    private readonly object _lock = new();

    public string Id { get; } = "installation";
    public int Progress { get; private set; } = 0;
    public string CurrentStep { get; private set; } = "";
    public string CurrentPackage { get; private set; } = "";
    public int DownloadProgress { get; private set; } = 0;
    public string StatusMessage { get; private set; } = "";
    public bool IsComplete { get; private set; } = false;
    public bool HasError { get; private set; } = false;
    public string ErrorMessage { get; private set; } = "";
    public DateTime StartTime { get; private set; } = DateTime.UtcNow;
    public DateTime? EndTime { get; private set; }

    public List<string> CompletedPackages { get; private set; } = new();
    public List<string> FailedPackages { get; private set; } = new();
    public Dictionary<string, string> PackageVersions { get; private set; } = new();

    public void UpdateProgress(int progress, string step, string message = "")
    {
        lock (_lock)
        {
            Progress = Math.Clamp(progress, 0, 100);
            CurrentStep = step ?? "";
            StatusMessage = message ?? "";

            Logs.Info($"[VoiceAssistant] Installation Progress: {Progress}% - {step} - {message}");
        }
    }

    public void UpdateProgress(int progress, string step, string package, int downloadProgress, string message)
    {
        lock (_lock)
        {
            Progress = Math.Clamp(progress, 0, 100);
            CurrentStep = step ?? "";
            CurrentPackage = package ?? "";
            DownloadProgress = Math.Clamp(downloadProgress, 0, 100);
            StatusMessage = message ?? "";

            Logs.Info($"[VoiceAssistant] Installation Progress: {Progress}% - {step} - {package} ({downloadProgress}%) - {message}");
        }
    }

    public void SetError(string errorMessage)
    {
        lock (_lock)
        {
            HasError = true;
            ErrorMessage = errorMessage ?? "";
            EndTime = DateTime.UtcNow;

            if (!string.IsNullOrEmpty(CurrentPackage))
            {
                FailedPackages.Add(CurrentPackage);
            }

            Logs.Error($"[VoiceAssistant] Installation error: {errorMessage}");
        }
    }

    public void SetComplete()
    {
        lock (_lock)
        {
            IsComplete = true;
            Progress = 100;
            StatusMessage = "Installation completed successfully!";
            EndTime = DateTime.UtcNow;

            var duration = EndTime.Value - StartTime;
            Logs.Info($"[VoiceAssistant] Installation completed successfully in {duration.TotalSeconds:F1} seconds");
        }
    }

    public void AddCompletedPackage(string package, string version = "")
    {
        lock (_lock)
        {
            if (!CompletedPackages.Contains(package))
            {
                CompletedPackages.Add(package);
                if (!string.IsNullOrEmpty(version))
                {
                    PackageVersions[package] = version;
                }
            }
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            Progress = 0;
            CurrentStep = "";
            CurrentPackage = "";
            DownloadProgress = 0;
            StatusMessage = "";
            IsComplete = false;
            HasError = false;
            ErrorMessage = "";
            StartTime = DateTime.UtcNow;
            EndTime = null;
            CompletedPackages.Clear();
            FailedPackages.Clear();
            PackageVersions.Clear();
        }
    }

    public InstallationProgressResponse ToResponse()
    {
        lock (_lock)
        {
            return new InstallationProgressResponse
            {
                Success = !HasError,
                Progress = Progress,
                CurrentStep = CurrentStep,
                CurrentPackage = CurrentPackage,
                DownloadProgress = DownloadProgress,
                StatusMessage = StatusMessage,
                IsComplete = IsComplete,
                HasError = HasError,
                ErrorMessage = ErrorMessage
            };
        }
    }
}

/// <summary>
/// Progress tracker for API operations like STT/TTS calls.
/// </summary>
public class ApiProgressTracker : IProgressTracker
{
    private readonly object _lock = new();

    public string Id { get; }
    public int Progress { get; private set; } = 0;
    public string CurrentStep { get; private set; } = "";
    public string StatusMessage { get; private set; } = "";
    public bool IsComplete { get; private set; } = false;
    public bool HasError { get; private set; } = false;
    public string ErrorMessage { get; private set; } = "";
    public DateTime StartTime { get; private set; } = DateTime.UtcNow;
    public DateTime? EndTime { get; private set; }

    public string Operation { get; private set; } = "";
    public double ProcessingTime => ((EndTime ?? DateTime.UtcNow) - StartTime).TotalSeconds;

    public ApiProgressTracker(string operationId)
    {
        Id = operationId;
    }

    public void UpdateProgress(int progress, string step, string message = "")
    {
        lock (_lock)
        {
            Progress = Math.Clamp(progress, 0, 100);
            CurrentStep = step ?? "";
            StatusMessage = message ?? "";

            Logs.Debug($"[VoiceAssistant] API Progress [{Id}]: {Progress}% - {step} - {message}");
        }
    }

    public void SetOperation(string operation)
    {
        lock (_lock)
        {
            Operation = operation ?? "";
        }
    }

    public void SetError(string errorMessage)
    {
        lock (_lock)
        {
            HasError = true;
            ErrorMessage = errorMessage ?? "";
            EndTime = DateTime.UtcNow;

            Logs.Error($"[VoiceAssistant] API error [{Id}]: {errorMessage}");
        }
    }

    public void SetComplete()
    {
        lock (_lock)
        {
            IsComplete = true;
            Progress = 100;
            EndTime = DateTime.UtcNow;

            if (string.IsNullOrEmpty(StatusMessage))
            {
                StatusMessage = $"{Operation} completed successfully";
            }

            Logs.Debug($"[VoiceAssistant] API operation [{Id}] completed in {ProcessingTime:F3} seconds");
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            Progress = 0;
            CurrentStep = "";
            StatusMessage = "";
            IsComplete = false;
            HasError = false;
            ErrorMessage = "";
            StartTime = DateTime.UtcNow;
            EndTime = null;
            Operation = "";
        }
    }
}

/// <summary>
/// Progress tracker for backend health monitoring.
/// </summary>
public class HealthCheckProgressTracker : IProgressTracker
{
    private readonly object _lock = new();

    public string Id { get; } = "health_check";
    public int Progress { get; private set; } = 0;
    public string CurrentStep { get; private set; } = "";
    public string StatusMessage { get; private set; } = "";
    public bool IsComplete { get; private set; } = false;
    public bool HasError { get; private set; } = false;
    public string ErrorMessage { get; private set; } = "";
    public DateTime StartTime { get; private set; } = DateTime.UtcNow;
    public DateTime? EndTime { get; private set; }

    public int AttemptCount { get; private set; } = 0;
    public int MaxAttempts { get; set; } = 30;
    public bool IsHealthy { get; private set; } = false;

    public void UpdateProgress(int progress, string step, string message = "")
    {
        lock (_lock)
        {
            Progress = Math.Clamp(progress, 0, 100);
            CurrentStep = step ?? "";
            StatusMessage = message ?? "";
        }
    }

    public void IncrementAttempt()
    {
        lock (_lock)
        {
            AttemptCount++;
            Progress = (int)((double)AttemptCount / MaxAttempts * 100);
            StatusMessage = $"Health check attempt {AttemptCount}/{MaxAttempts}";
        }
    }

    public void SetHealthy()
    {
        lock (_lock)
        {
            IsHealthy = true;
            SetComplete();
        }
    }

    public void SetError(string errorMessage)
    {
        lock (_lock)
        {
            HasError = true;
            ErrorMessage = errorMessage ?? "";
            EndTime = DateTime.UtcNow;
            IsHealthy = false;
        }
    }

    public void SetComplete()
    {
        lock (_lock)
        {
            IsComplete = true;
            Progress = 100;
            EndTime = DateTime.UtcNow;

            if (string.IsNullOrEmpty(StatusMessage))
            {
                StatusMessage = IsHealthy ? "Backend is healthy" : "Backend health check failed";
            }
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            Progress = 0;
            CurrentStep = "";
            StatusMessage = "";
            IsComplete = false;
            HasError = false;
            ErrorMessage = "";
            StartTime = DateTime.UtcNow;
            EndTime = null;
            AttemptCount = 0;
            IsHealthy = false;
        }
    }
}
