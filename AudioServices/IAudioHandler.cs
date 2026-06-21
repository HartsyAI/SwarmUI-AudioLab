using Newtonsoft.Json.Linq;
using HartsyInference.Core.Backends;

namespace Hartsy.Extensions.AudioLab.AudioServices;

/// <summary>Bridges AudioLab's request args to a HartsyInference pipeline: maps the args dict to pipeline
/// inputs, runs inference on the shared device, and returns the JObject AudioLab parses. Registered in
/// <see cref="AudioEngine"/> keyed by provider id.</summary>
public interface IAudioHandler
{
    /// <summary>AudioLab category this handler serves: <c>tts</c>, <c>stt</c>, <c>audiogen</c>,
    /// <c>voiceconv</c>, or <c>audiofx</c>. Informational / output-shape sanity checks.</summary>
    string Category { get; }

    /// <summary>Whether the handler downloads and manages its own weights (HuggingFace auto-download into
    /// the engine cache). True means AudioLab need not resolve a local checkpoint before routing here.</summary>
    bool ManagesOwnWeights { get; }

    /// <summary>Ensures the weights for the given AudioLab model id are present on disk (drives the
    /// model-browser Install button). <paramref name="onProgress"/> receives human-readable status lines.</summary>
    Task EnsureWeightsAsync(string modelId, Action<string> onProgress, CancellationToken cancel);

    /// <summary>Runs one inference request on the shared compute <paramref name="backend"/> (already
    /// serialized by <see cref="AudioEngine"/> so this never runs concurrently with another audio job).</summary>
    Task<JObject> ProcessAsync(IBackend backend, IReadOnlyDictionary<string, object> args, CancellationToken cancel);

    /// <summary>Drops the resident pipeline for the given model id to free VRAM/RAM.</summary>
    void Unload(string modelId);
}
