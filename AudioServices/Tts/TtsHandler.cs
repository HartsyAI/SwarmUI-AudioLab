using System.Collections.Concurrent;
using System.IO;
using Newtonsoft.Json.Linq;
using SwarmUI.Utils;
using HartsyInference.Core.Backends;
using HartsyInference.Audio.Io;

namespace Hartsy.Extensions.AudioLab.AudioServices.Tts;

/// <summary>Generic text-to-speech handler (text + optional voice reference → synth → base64 WAV), driven by
/// a per-model <see cref="TtsModelDescriptor"/> so adding a model is a descriptor, not a class. Weights
/// HF-auto-download on first use; runners cached per resolved model.</summary>
public sealed class TtsHandler(TtsModelDescriptor descriptor) : IAudioHandler
{
    private readonly ConcurrentDictionary<string, ITtsRunner> _cache = new(StringComparer.Ordinal);
    private readonly object _loadLock = new();

    /// <summary>Voice-reference clips are decoded/materialized at 24 kHz (the rate the cloning models want).</summary>
    private const int ReferenceSampleRate = 24_000;

    public string Category => "tts";

    public bool ManagesOwnWeights => true;

    public async Task EnsureWeightsAsync(string modelId, Action<string> onProgress, CancellationToken cancel)
    {
        string repo = descriptor.ResolveRepo(modelId);
        onProgress($"Fetching text-to-speech weights{(string.IsNullOrEmpty(repo) ? "" : $" ({repo})")}...");
        await GetOrLoadAsync(repo, cancel).ConfigureAwait(false);
        onProgress("Ready.");
    }

    public async Task<JObject> ProcessAsync(IBackend backend, IReadOnlyDictionary<string, object> args, CancellationToken cancel)
    {
        string text = AudioIo.Str(args, "text");
        if (string.IsNullOrWhiteSpace(text))
        {
            return AudioIo.Error("No text supplied to synthesize.");
        }
        string repo = descriptor.ResolveRepo(AudioIo.Str(args, "__model_id"));
        string refText = AudioIo.Str(args, "ref_text");

        // Materialize the optional voice reference once: decode to mono 24 kHz and write a temp WAV (for
        // models that take a file path). Cleaned up after synthesis.
        string refB64 = AudioIo.Str(args, "reference_audio");
        float[] refMono = null;
        string refWavPath = null;
        if (!string.IsNullOrEmpty(refB64))
        {
            refMono = AudioIo.DecodeBase64ToMono(refB64, ReferenceSampleRate, cancel);
            refWavPath = Path.Combine(Path.GetTempPath(), $"audiolab_voiceref_{Guid.NewGuid():N}.wav");
            using FileStream fs = new(refWavPath, FileMode.Create, FileAccess.Write);
            WavFile.WriteMono16(fs, refMono, ReferenceSampleRate);
        }
        try
        {
            cancel.ThrowIfCancellationRequested();
            ITtsRunner runner = await GetOrLoadAsync(repo, cancel).ConfigureAwait(false);
            TtsRequest request = new()
            {
                Text = text,
                RefText = refText,
                ReferenceMono24k = refMono,
                ReferenceWavPath = refWavPath,
            };
            long start = Environment.TickCount64;
            float[] samples = runner.Synthesize(backend, request);
            if (samples is null || samples.Length == 0)
            {
                return AudioIo.Error("The text-to-speech model produced no audio.");
            }
            double duration = samples.Length / (double)runner.SampleRate;
            Logs.Verbose($"[AudioLab][TTS] Synthesized {duration:0.0}s @ {runner.SampleRate} Hz in {Environment.TickCount64 - start}ms.");
            string wavB64 = AudioIo.EncodeMonoWavBase64(samples, runner.SampleRate);
            return AudioIo.AudioResult(wavB64, "wav", duration);
        }
        finally
        {
            if (refWavPath is not null)
            {
                try { if (File.Exists(refWavPath)) File.Delete(refWavPath); }
                catch (Exception ex) { Logs.Warning($"[AudioLab] Failed to delete temp voice-ref '{refWavPath}': {ex.Message}"); }
            }
        }
    }

    public void Unload(string modelId)
    {
        string repo = descriptor.ResolveRepo(modelId);
        if (_cache.TryRemove(repo, out ITtsRunner runner))
        {
            runner.Dispose();
        }
    }

    /// <summary>Loads (downloading on first use) and caches the runner. Thread-safe; the double-check keeps
    /// two concurrent callers from loading the same model twice.</summary>
    private async Task<ITtsRunner> GetOrLoadAsync(string repo, CancellationToken cancel)
    {
        if (_cache.TryGetValue(repo, out ITtsRunner existing))
        {
            return existing;
        }
        ITtsRunner loaded = await descriptor.LoadAsync(repo, cancel).ConfigureAwait(false);
        lock (_loadLock)
        {
            if (_cache.TryGetValue(repo, out ITtsRunner raced))
            {
                loaded.Dispose();
                return raced;
            }
            _cache[repo] = loaded;
            return loaded;
        }
    }
}
