using Newtonsoft.Json.Linq;
using HartsyInference.Core.Backends;

namespace Hartsy.Extensions.AudioLab.AudioServices;

/// <summary>
/// One audio provider's bridge between AudioLab's request args and a HartsyInference pipeline.
/// Implementations are stateless dispatchers: they own a resident (lazily-loaded) pipeline cache,
/// translate the loosely-typed <c>args</c> dictionary AudioLab builds (see
/// <c>DynamicAudioBackend.BuildEngineArgs</c>) into the pipeline's strongly-typed inputs, run inference
/// on the shared compute device, and shape the result into the JObject AudioLab parses back
/// (<c>success</c> / <c>audio_data</c> / <c>text</c> / <c>output_format</c> / <c>duration</c> / <c>error</c>).
///
/// <para>Registered in <see cref="AudioEngine"/>'s dispatch table keyed by AudioLab provider id
/// (e.g. <c>"whisper_stt"</c>). A handler may serve several provider ids that share a pipeline.</para>
/// </summary>
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
