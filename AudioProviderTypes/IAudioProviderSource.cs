namespace Hartsy.Extensions.AudioLab.AudioProviderTypes;

/// <summary>Interface for provider sources that supply audio provider definitions.</summary>
public interface IAudioProviderSource
{
    /// <summary>Gets the provider definition for this source.</summary>
    AudioProviderDefinition GetProvider();
}

/// <summary>Registry that aggregates all audio provider sources.</summary>
public static class AudioProviderRegistry
{
    private static readonly List<IAudioProviderSource> _sources = [];

    /// <summary>Registers an audio provider source.</summary>
    public static void Register(IAudioProviderSource source) => _sources.Add(source);

    /// <summary>Gets all registered provider definitions.</summary>
    public static IReadOnlyList<AudioProviderDefinition> All => _sources.ConvertAll(s => s.GetProvider());

    /// <summary>Gets all providers matching a specific category.</summary>
    public static IReadOnlyList<AudioProviderDefinition> GetByCategory(AudioCategory category)
        => All.Where(p => p.Category == category).ToList().AsReadOnly();

    /// <summary>Gets a provider by its unique ID, or null if not found.</summary>
    public static AudioProviderDefinition GetById(string id)
        => All.FirstOrDefault(p => p.Id == id);

    /// <summary>Clears all registered sources (for testing).</summary>
    public static void Clear() => _sources.Clear();
}
