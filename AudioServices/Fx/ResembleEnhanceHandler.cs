using System.Collections.Concurrent;
using Newtonsoft.Json.Linq;
using SwarmUI.Utils;
using Hartsy.Extensions.AudioLab.AudioProviders;
using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using HartsyInference.Core.Backends;
using HartsyInference.Core.Tensors;
using HartsyInference.Audio.Models.ResembleEnhance;
using HartsyInference.Audio.Pipelines;

namespace Hartsy.Extensions.AudioLab.AudioServices.Fx;

/// <summary>Resemble-Enhance speech enhancement (AudioProcessing): decodes the input to 44.1 kHz mono, runs the
/// engine's denoiser + LCFM enhancer + UnivNet vocoder (<see cref="ResembleEnhancePipeline"/>), and returns the
/// enhanced clip. Provider id <c>resembleenhance_fx</c>. The engine pipeline is complete; weights auto-download
/// from <c>ResembleAI/resemble-enhance</c> (adjust the repo/layout if resolution fails).</summary>
public sealed class ResembleEnhanceHandler : IAudioHandler
{
    private const string ProviderId = "resemble_enhance_fx";
    private const string Repo = "ResembleAI/resemble-enhance";
    private readonly ConcurrentDictionary<string, Loaded> _cache = new(StringComparer.Ordinal);
    private readonly object _loadLock = new();

    private sealed record Loaded(ResembleEnhancePipeline Pipeline, IDisposable[] Loaders);

    public string Category => "fx";

    public bool ManagesOwnWeights => true;

    public async Task EnsureWeightsAsync(string modelId, Action<string> onProgress, CancellationToken cancel)
    {
        onProgress("Fetching Resemble-Enhance weights...");
        _ = await Tts.TtsModels.LoadCheckpointAsync(Repo, cancel).ConfigureAwait(false);
        onProgress("Resemble-Enhance ready.");
    }

    public IReadOnlyList<string> GetWeightLocations(string modelId)
    {
        AudioProviderDefinition provider = AudioProviderRegistry.GetById(ProviderId);
        return provider is null ? [] : [AudioWeights.WeightsDirectory(provider)];
    }

    public async Task<JObject> ProcessAsync(IBackend backend, IReadOnlyDictionary<string, object> args, CancellationToken cancel)
    {
        string audioB64 = AudioIo.Str(args, "audio_data");
        if (string.IsNullOrEmpty(audioB64))
        {
            return AudioIo.Error("No audio supplied to enhance.");
        }
        float[] mono = AudioIo.DecodeBase64ToMono(audioB64, 44_100, cancel);
        if (mono.Length == 0)
        {
            return AudioIo.Error("The audio input decoded to no samples.");
        }
        cancel.ThrowIfCancellationRequested();

        Loaded loaded = await GetOrLoadAsync(cancel).ConfigureAwait(false);
        long start = Environment.TickCount64;
        // Honor the user's enhance params (previously hardcoded). `lambd` = denoise/enhance blend, `tau` = prior
        // temperature, `seed` = determinism. (`nfe`/`solver` are in args but the engine's Enhance doesn't take them
        // yet — LCFM step-count/solver would need an engine-side signature; tracked separately.)
        float lambd = (float)ParseDouble(args, "lambd", 0.5);
        float tau = (float)ParseDouble(args, "tau", 0.5);
        int seed = (int)ParseDouble(args, "seed", 0);
        float[] enhanced = loaded.Pipeline.Enhance(backend, mono, lambd: lambd, tau: tau, seed: seed < 0 ? 0 : seed);
        double duration = enhanced.Length / 44_100.0;
        Logs.Verbose($"[AudioLab][Resemble-Enhance] Enhanced {mono.Length / 44100.0:0.0}s in {Environment.TickCount64 - start}ms.");
        return AudioIo.AudioResult(AudioIo.EncodeWavBase64(enhanced, enhanced, 44_100), "wav", duration);
    }

    private async Task<Loaded> GetOrLoadAsync(CancellationToken cancel)
    {
        if (_cache.TryGetValue(Repo, out Loaded existing)) { return existing; }
        (System.Collections.Generic.IReadOnlyDictionary<string, Tensor> dict, IDisposable[] loaders)
            = await Tts.TtsModels.LoadCheckpointAsync(Repo, cancel).ConfigureAwait(false);
        lock (_loadLock)
        {
            if (_cache.TryGetValue(Repo, out Loaded found)) { return found; }
            ResembleEnhancePipeline pipeline = new(ResembleEnhanceConfig.Default, withDenoiserAndVocoder: true);
            pipeline.LoadWeights(dict);
            Loaded loaded = new(pipeline, loaders);
            _cache[Repo] = loaded;
            Logs.Info("[AudioLab][Resemble-Enhance] Loaded ResembleAI/resemble-enhance (denoiser + LCFM + UnivNet, 44.1 kHz).");
            return loaded;
        }
    }

    public void Unload(string modelId)
    {
        if (_cache.TryRemove(Repo, out Loaded loaded))
        {
            loaded.Pipeline.Dispose();
            foreach (IDisposable d in loaded.Loaders) { d?.Dispose(); }
        }
    }

    public void UnloadAll() => Unload(null);

    private static double ParseDouble(IReadOnlyDictionary<string, object> args, string key, double fallback)
    {
        if (!args.TryGetValue(key, out object v) || v is null) { return fallback; }
        return v switch
        {
            double d => d,
            int i => i,
            long l => l,
            float f => f,
            _ => double.TryParse(v.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double parsed) ? parsed : fallback,
        };
    }
}
