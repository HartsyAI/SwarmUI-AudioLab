using System.Collections.Concurrent;
using Newtonsoft.Json.Linq;
using SwarmUI.Utils;
using HartsyInference.Core.Backends;

namespace Hartsy.Extensions.AudioLab.AudioServices.Stt;

/// <summary>
/// Generic speech-to-text handler. Every STT model is uniform — load a pipeline from a HuggingFace repo,
/// decode the input audio to mono 16 kHz, transcribe, return text — so a single handler driven by a
/// per-model <see cref="SttModelDescriptor"/> covers Whisper, Moonshine, distil-whisper, etc. Adding an STT
/// model is a ~5-line descriptor in <see cref="SttModels"/>, not a new handler class.
///
/// <para>Weights are HuggingFace-hosted and auto-downloaded into the engine cache on first use, so this
/// handler <see cref="ManagesOwnWeights"/>. Runners are kept resident per resolved repo and reused.</para>
/// </summary>
public sealed class SttHandler(SttModelDescriptor descriptor) : IAudioHandler
{
    private readonly ConcurrentDictionary<string, ISttRunner> _cache = new(StringComparer.Ordinal);
    private readonly object _loadLock = new();

    public string Category => "stt";

    public bool ManagesOwnWeights => true;

    public async Task EnsureWeightsAsync(string modelId, Action<string> onProgress, CancellationToken cancel)
    {
        string repo = descriptor.ResolveRepo(modelId);
        onProgress($"Fetching speech-to-text weights ({repo})...");
        await GetOrLoadAsync(repo, cancel).ConfigureAwait(false);
        onProgress($"Ready ({repo}).");
    }

    public async Task<JObject> ProcessAsync(IBackend backend, IReadOnlyDictionary<string, object> args, CancellationToken cancel)
    {
        string audioB64 = AudioIo.Str(args, "audio_data");
        if (string.IsNullOrEmpty(audioB64))
        {
            return AudioIo.Error("No audio supplied to transcribe (the STT audio input is empty).");
        }
        string language = AudioIo.Str(args, "language", "en");
        string repo = descriptor.ResolveRepo(AudioIo.Str(args, "__model_id"));

        // Decode to the 16 kHz mono the pipelines want (they would resample anyway; hand them 16k directly).
        float[] audio = AudioIo.DecodeBase64ToMono(audioB64, 16_000, cancel);
        if (audio.Length == 0)
        {
            return AudioIo.Error("The STT audio input decoded to no samples.");
        }
        cancel.ThrowIfCancellationRequested();

        ISttRunner runner = await GetOrLoadAsync(repo, cancel).ConfigureAwait(false);
        long start = Environment.TickCount64;
        string text = runner.Transcribe(backend, audio);
        Logs.Verbose($"[AudioLab][STT] Transcribed {audio.Length / 16000.0:0.0}s via {repo} in {Environment.TickCount64 - start}ms.");
        return AudioIo.TranscriptionResult(text?.Trim() ?? "", language);
    }

    public void Unload(string modelId)
    {
        string repo = descriptor.ResolveRepo(modelId);
        if (_cache.TryRemove(repo, out ISttRunner runner))
        {
            runner.Dispose();
        }
    }

    /// <summary>Loads (downloading on first use) and caches the runner for a repo. Thread-safe; the
    /// double-check keeps two concurrent callers from loading the same repo twice.</summary>
    private async Task<ISttRunner> GetOrLoadAsync(string repo, CancellationToken cancel)
    {
        if (_cache.TryGetValue(repo, out ISttRunner existing))
        {
            return existing;
        }
        ISttRunner loaded = await descriptor.LoadAsync(repo, cancel).ConfigureAwait(false);
        lock (_loadLock)
        {
            if (_cache.TryGetValue(repo, out ISttRunner raced))
            {
                loaded.Dispose();
                return raced;
            }
            _cache[repo] = loaded;
            return loaded;
        }
    }
}
