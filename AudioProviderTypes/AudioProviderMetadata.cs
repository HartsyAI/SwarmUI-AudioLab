namespace Hartsy.Extensions.AudioLab.AudioProviderTypes;

/// <summary>Runtime state for an initialized audio provider within the backend.</summary>
public class AudioProviderMetadata
{
    /// <summary>The provider definition this metadata is for.</summary>
    public required AudioProviderDefinition Definition { get; init; }

    /// <summary>Whether this provider is currently installed and active.</summary>
    public bool IsEnabled { get; set; }

    /// <summary>Whether all dependencies for this provider are installed.</summary>
    public bool DependenciesInstalled { get; set; }

    /// <summary>Whether the Python engine has been initialized and is ready to process.</summary>
    public bool IsInitialized { get; set; }

    /// <summary>Last error message if initialization or processing failed.</summary>
    public string LastError { get; set; } = "";

    /// <summary>When this provider was last successfully used.</summary>
    public DateTime? LastUsed { get; set; }
}
