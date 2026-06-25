using System.Collections.Concurrent;
using System.Globalization;
using Newtonsoft.Json.Linq;
using SwarmUI.Utils;
using HartsyInference.Core.Backends;
using HartsyInference.Audio.Cache;
using Hartsy.Extensions.AudioLab.AudioProviders;
using Hartsy.Extensions.AudioLab.AudioProviderTypes;

namespace Hartsy.Extensions.AudioLab.AudioServices.Music;

/// <summary>Generic text-to-music handler (prompt → synth → base64 WAV), driven by a per-model
/// <see cref="MusicModelDescriptor"/>. Covers MusicGen, AudioGen, ACE-Step; runners cached per resolved model.
/// Loading is lazy (needs the compute device), so it happens on first generation, not at install.</summary>
public sealed class MusicHandler(string providerId, MusicModelDescriptor descriptor) : IAudioHandler
{
    private readonly ConcurrentDictionary<string, IMusicRunner> _cache = new(StringComparer.Ordinal);
    private readonly object _loadLock = new();

    public string Category => "audiogen";

    public bool ManagesOwnWeights => descriptor.ManagesOwnWeights;

    public Task EnsureWeightsAsync(string modelId, Action<string> onProgress, CancellationToken cancel)
    {
        // The full load binds to a compute device, so it runs lazily on first generation.
        onProgress(descriptor.ManagesOwnWeights
            ? "Weights download on first generation."
            : "Place the model checkpoint; loads on first generation.");
        return Task.CompletedTask;
    }

    public IReadOnlyList<string> GetWeightLocations(string modelId)
    {
        // ManagesOwnWeights (MusicGen/AudioGen): CacheKey is the HF repo id → its private cache dir.
        // Checkpoint providers (ACE-Step/YuE): the provider's dedicated weights directory (where Install
        // downloads to). The shared ACE-Step VAE / Qwen-embedding caches are intentionally NOT included.
        if (descriptor.ManagesOwnWeights)
        {
            string repo = descriptor.CacheKey(providerId, modelId);
            return string.IsNullOrEmpty(repo) ? [] : [AudioModelCache.GetRepoDirectory(repo)];
        }
        AudioProviderDefinition provider = AudioProviderRegistry.GetById(providerId);
        return provider is null ? [] : [AudioWeights.WeightsDirectory(provider)];
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

        IMusicRunner runner = await GetOrLoadAsync(backend, modelId, cancel).ConfigureAwait(false);
        cancel.ThrowIfCancellationRequested();
        long start = Environment.TickCount64;
        MusicAudio audio = runner.Synthesize(backend, request);
        if (audio.Left is null || audio.Left.Length == 0)
        {
            return AudioIo.Error("The music model produced no audio.");
        }
        double duration = audio.Left.Length / (double)runner.SampleRate;
        Logs.Verbose($"[AudioLab][Music] Generated {duration:0.0}s @ {runner.SampleRate} Hz ({(audio.Right is null ? "mono" : "stereo")}) in {Environment.TickCount64 - start}ms.");
        return AudioIo.AudioResult(AudioIo.EncodeWavBase64(audio.Left, audio.Right, runner.SampleRate), "wav", duration);
    }

    public void Unload(string modelId)
    {
        string key = descriptor.CacheKey(providerId, modelId);
        if (_cache.TryRemove(key, out IMusicRunner runner))
        {
            runner.Dispose();
        }
    }

    private async Task<IMusicRunner> GetOrLoadAsync(IBackend backend, string modelId, CancellationToken cancel)
    {
        string key = descriptor.CacheKey(providerId, modelId);
        if (_cache.TryGetValue(key, out IMusicRunner existing))
        {
            return existing;
        }
        IMusicRunner loaded = await descriptor.LoadAsync(backend, providerId, modelId, cancel).ConfigureAwait(false);
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
