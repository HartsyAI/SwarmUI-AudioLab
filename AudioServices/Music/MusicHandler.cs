using System.Collections.Concurrent;
using System.Globalization;
using Newtonsoft.Json.Linq;
using SwarmUI.Utils;
using HartsyInference.Core.Backends;

namespace Hartsy.Extensions.AudioLab.AudioServices.Music;

/// <summary>Generic text-to-music handler (prompt → synth → base64 WAV), driven by a per-model
/// <see cref="MusicModelDescriptor"/>. Covers MusicGen, AudioGen, and YuE; runners cached per resolved model.</summary>
public sealed class MusicHandler(string providerId, MusicModelDescriptor descriptor) : IAudioHandler
{
    private readonly ConcurrentDictionary<string, IMusicRunner> _cache = new(StringComparer.Ordinal);
    private readonly object _loadLock = new();

    public string Category => "audiogen";

    public bool ManagesOwnWeights => descriptor.ManagesOwnWeights;

    public async Task EnsureWeightsAsync(string modelId, Action<string> onProgress, CancellationToken cancel)
    {
        onProgress("Loading music model...");
        await GetOrLoadAsync(modelId, cancel).ConfigureAwait(false);
        onProgress("Ready.");
    }

    public async Task<JObject> ProcessAsync(IBackend backend, IReadOnlyDictionary<string, object> args, CancellationToken cancel)
    {
        string prompt = AudioIo.Str(args, "prompt");
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return AudioIo.Error("No prompt supplied to generate music.");
        }
        MusicRequest request = new()
        {
            Prompt = prompt,
            Genre = AudioIo.Str(args, "genre"),
            Duration = ParseDouble(args, "duration", 10d),
            Seed = (int)ParseDouble(args, "seed", 0d),
        };
        string modelId = AudioIo.Str(args, "__model_id");

        IMusicRunner runner = await GetOrLoadAsync(modelId, cancel).ConfigureAwait(false);
        cancel.ThrowIfCancellationRequested();
        long start = Environment.TickCount64;
        float[] samples = runner.Synthesize(backend, request);
        if (samples is null || samples.Length == 0)
        {
            return AudioIo.Error("The music model produced no audio.");
        }
        double duration = samples.Length / (double)runner.SampleRate;
        Logs.Verbose($"[AudioLab][Music] Generated {duration:0.0}s @ {runner.SampleRate} Hz in {Environment.TickCount64 - start}ms.");
        return AudioIo.AudioResult(AudioIo.EncodeMonoWavBase64(samples, runner.SampleRate), "wav", duration);
    }

    public void Unload(string modelId)
    {
        string key = descriptor.CacheKey(providerId, modelId);
        if (_cache.TryRemove(key, out IMusicRunner runner))
        {
            runner.Dispose();
        }
    }

    private async Task<IMusicRunner> GetOrLoadAsync(string modelId, CancellationToken cancel)
    {
        string key = descriptor.CacheKey(providerId, modelId);
        if (_cache.TryGetValue(key, out IMusicRunner existing))
        {
            return existing;
        }
        IMusicRunner loaded = await descriptor.LoadAsync(providerId, modelId, cancel).ConfigureAwait(false);
        lock (_loadLock)
        {
            if (_cache.TryGetValue(key, out IMusicRunner raced))
            {
                loaded.Dispose();
                return raced;
            }
            _cache[key] = loaded;
            return loaded;
        }
    }

    private static double ParseDouble(IReadOnlyDictionary<string, object> args, string key, double fallback)
    {
        if (!args.TryGetValue(key, out object v) || v is null)
        {
            return fallback;
        }
        return v switch
        {
            double d => d,
            int i => i,
            long l => l,
            float f => f,
            _ => double.TryParse(v.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double parsed) ? parsed : fallback,
        };
    }
}
