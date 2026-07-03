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
        string genre = AudioIo.Str(args, "genre");
        // ACE-Step puts the style in genre and (optional) lyrics in prompt, so either alone is enough.
        if (string.IsNullOrWhiteSpace(prompt) && string.IsNullOrWhiteSpace(genre))
        {
            return AudioIo.Error("No prompt supplied to generate music.");
        }
        MusicRequest request = new()
        {
            Prompt = prompt,
            Genre = genre,
            Duration = ParseDouble(args, "duration", 10d),
            Seed = (int)ParseDouble(args, "seed", 0d),
            // Sentinel 0 = "model default" for shift/steps (per-variant defaults live in the model loaders).
            Shift = ParseDouble(args, "shift", 0d) > 0 ? ParseDouble(args, "shift", 0d) : null,
            InferSteps = ParseDouble(args, "infer_step", 0d) > 0 ? (int)ParseDouble(args, "infer_step", 0d) : null,
            // HeartMuLa sends "cfg_scale"; ACE-Step sends "guidance_scale" — one CFG knob per provider.
            CfgScale = args.ContainsKey("cfg_scale") ? ParseDouble(args, "cfg_scale", 1.5)
                : args.ContainsKey("guidance_scale") ? ParseDouble(args, "guidance_scale", 7.0) : null,
            // ACE-Step base/sft CFG controls.
            InferMethod = AudioIo.Str(args, "infer_method"),
            UseAdg = AudioIo.Str(args, "use_adg").Equals("true", StringComparison.OrdinalIgnoreCase),
            CfgIntervalStart = ParseDouble(args, "cfg_interval_start", 0d),
            CfgIntervalEnd = ParseDouble(args, "cfg_interval_end", 1d),
            // ACE-Step 5 Hz LM planner controls.
            LmModel = AudioIo.Str(args, "lm_model"),
            Thinking = !AudioIo.Str(args, "thinking").Equals("false", StringComparison.OrdinalIgnoreCase),
            LmTemperature = ParseDouble(args, "lm_temperature", 0.85),
            LmCfgScale = ParseDouble(args, "lm_cfg_scale", 2.0),
            LmTopK = (int)ParseDouble(args, "lm_top_k", 0d),
            LmTopP = ParseDouble(args, "lm_top_p", 0.9),
            LmNegativePrompt = AudioIo.Str(args, "lm_negative_prompt"),
            Temperature = args.ContainsKey("temperature") ? ParseDouble(args, "temperature", 1.0) : null,
            TopK = args.ContainsKey("topk") ? (int)ParseDouble(args, "topk", 50) : null,
            // ACE-Step prompt-template metas (upstream SFT_GEN_PROMPT "# Metas" block + lyric language header).
            Bpm = args.ContainsKey("bpm") ? (int)ParseDouble(args, "bpm", 120) : null,
            KeyScale = AudioIo.Str(args, "key_scale"),
            TimeSignature = AudioIo.Str(args, "time_signature"),
            VocalLanguage = AudioIo.Str(args, "vocal_language"),
        };
        string modelId = AudioIo.Str(args, "__model_id");

        IMusicRunner runner = await GetOrLoadAsync(backend, modelId, cancel).ConfigureAwait(false);
        cancel.ThrowIfCancellationRequested();
        long start = Environment.TickCount64;
        MusicAudio audio = runner.Synthesize(backend, request, cancel);
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

    public void UnloadAll()
    {
        foreach (string key in _cache.Keys)
        {
            if (_cache.TryRemove(key, out IMusicRunner runner))
            {
                runner.Dispose();
            }
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
