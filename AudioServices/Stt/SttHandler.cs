using System.Collections.Concurrent;
using Newtonsoft.Json.Linq;
using SwarmUI.Utils;
using HartsyInference.Core.Backends;
using HartsyInference.Audio.Cache;

namespace Hartsy.Extensions.AudioLab.AudioServices.Stt;

/// <summary>Generic speech-to-text handler (decode audio → load → transcribe → text), driven by a per-model
/// <see cref="SttModelDescriptor"/> so adding a model is a descriptor, not a class. Weights HF-auto-download
/// on first use; runners cached per resolved repo.</summary>
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

    public IReadOnlyList<string> GetWeightLocations(string modelId)
    {
        string repo = descriptor.ResolveRepo(modelId);
        return string.IsNullOrEmpty(repo) ? [] : [AudioModelCache.GetRepoDirectory(repo)];
    }

    public async Task<JObject> ProcessAsync(IBackend backend, IReadOnlyDictionary<string, object> args, CancellationToken cancel)
    {
        string audioB64 = AudioIo.Str(args, "audio_data");
        if (string.IsNullOrEmpty(audioB64))
        {
            return AudioIo.Error("No audio supplied to transcribe (the STT audio input is empty).");
        }
        string language = AudioIo.Str(args, "language", "en");
        // "task" comes from the Whisper provider (transcribe | translate); other STT providers don't set it.
        bool translate = string.Equals(AudioIo.Str(args, "task", "transcribe"), "translate", StringComparison.OrdinalIgnoreCase);
        SttRequest request = new() { Language = language, Translate = translate };
        string repo = descriptor.ResolveRepo(AudioIo.Str(args, "__model_id"));

        // Decode to the 16 kHz mono the pipelines want (they would resample anyway; hand them 16k directly).
        float[] audio = AudioIo.DecodeBase64ToMono(audioB64, descriptor.InputSampleRate, cancel);
        if (audio.Length == 0)
        {
            return AudioIo.Error("The STT audio input decoded to no samples.");
        }
        cancel.ThrowIfCancellationRequested();

        ISttRunner runner = await GetOrLoadAsync(repo, cancel).ConfigureAwait(false);
        long start = Environment.TickCount64;
        string text = runner.Transcribe(backend, audio, request);
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
