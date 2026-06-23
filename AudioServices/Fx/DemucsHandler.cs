using System.Collections.Concurrent;
using System.IO;
using Newtonsoft.Json.Linq;
using SwarmUI.Utils;
using Hartsy.Extensions.AudioLab.AudioProviders;
using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using HartsyInference.Core.Backends;
using HartsyInference.Core.Tensors;
using HartsyInference.Audio.Models.Demucs;
using HartsyInference.Audio.Pipelines;
using HartsyInference.ModelHandler.PyTorch;
using HartsyInference.ModelHandler.SafeTensors;

namespace Hartsy.Extensions.AudioLab.AudioServices.Fx;

/// <summary>Demucs stem separation (AudioProcessing): decodes the input to stereo 44.1 kHz, runs the engine's
/// hybrid-transformer <see cref="DemucsPipeline"/>, and returns the stems (drums/bass/other/vocals) as the
/// <c>stems</c> map the DAW's stem-separation path consumes. Weights are user-placed in the fx model folder.</summary>
public sealed class DemucsHandler : IAudioHandler
{
    private const string ProviderId = "demucs_fx";
    private readonly ConcurrentDictionary<string, Loaded> _cache = new(StringComparer.Ordinal);
    private readonly object _loadLock = new();

    private sealed record Loaded(DemucsPipeline Pipeline, IDisposable Loader);

    public string Category => "fx";

    public bool ManagesOwnWeights => false;

    public Task EnsureWeightsAsync(string modelId, Action<string> onProgress, CancellationToken cancel)
    {
        onProgress("Demucs runs on the in-process C# engine — place the htdemucs checkpoint (.th/.safetensors) in the model folder.");
        return Task.CompletedTask;
    }

    public IReadOnlyList<string> GetWeightLocations(string modelId)
    {
        AudioProviderDefinition provider = AudioProviderRegistry.GetById(ProviderId);
        return provider is null ? [] : [AudioWeights.WeightsDirectory(provider)];
    }

    public Task<JObject> ProcessAsync(IBackend backend, IReadOnlyDictionary<string, object> args, CancellationToken cancel)
    {
        string audioB64 = AudioIo.Str(args, "audio_data");
        if (string.IsNullOrEmpty(audioB64))
        {
            return Task.FromResult(AudioIo.Error("No audio supplied to separate."));
        }
        string modelName = AudioIo.Str(args, "model_name", "htdemucs");
        (float[] left, float[] right) = AudioIo.DecodeBase64ToStereo(audioB64, 44_100, cancel);
        if (left.Length == 0)
        {
            return Task.FromResult(AudioIo.Error("The audio input decoded to no samples."));
        }
        cancel.ThrowIfCancellationRequested();

        Loaded loaded = GetOrLoad(modelName);
        long start = Environment.TickCount64;
        (float[] Left, float[] Right)[] stems = loaded.Pipeline.Separate(backend, left, right);
        IReadOnlyList<string> names = loaded.Pipeline.Sources;
        List<(string, string)> outStems = new(stems.Length);
        for (int i = 0; i < stems.Length; i++)
        {
            string name = i < names.Count ? names[i] : $"stem{i}";
            outStems.Add((name, AudioIo.EncodeWavBase64(stems[i].Left, stems[i].Right, loaded.Pipeline.SampleRate)));
        }
        Logs.Verbose($"[AudioLab][Demucs] Separated {left.Length / 44100.0:0.0}s into {outStems.Count} stems in {Environment.TickCount64 - start}ms.");
        return Task.FromResult(AudioIo.StemsResult(outStems));
    }

    public void Unload(string modelId)
    {
        if (_cache.TryRemove(ResolvePath(modelId), out Loaded loaded))
        {
            loaded.Pipeline.Dispose();
            loaded.Loader.Dispose();
        }
    }

    private Loaded GetOrLoad(string modelName)
    {
        string path = ResolvePath(modelName);
        if (_cache.TryGetValue(path, out Loaded existing))
        {
            return existing;
        }
        lock (_loadLock)
        {
            if (_cache.TryGetValue(path, out Loaded raced))
            {
                return raced;
            }
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    $"Demucs weights not found: '{path}'. Place the htdemucs checkpoint (.th or .safetensors) there.", path);
            }
            (IReadOnlyDictionary<string, Tensor> dict, IDisposable loader) = LoadDict(path);
            DemucsPipeline pipeline = new(new HtDemucsConfig());
            pipeline.LoadWeights(dict);
            Logs.Info($"[AudioLab][Demucs] Loaded '{Path.GetFileName(path)}' (4 stems, 44.1 kHz).");
            Loaded loaded = new(pipeline, loader);
            _cache[path] = loaded;
            return loaded;
        }
    }

    private static (IReadOnlyDictionary<string, Tensor>, IDisposable) LoadDict(string path)
    {
        if (path.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase))
        {
            SafeTensorsLoader st = new();
            st.Load(path);
            return (st.GetAllTensors(), st);
        }
        PytorchPickleLoader pt = new();
        pt.Load(path);
        return (pt.GetAllTensors(), pt);
    }

    private static string ResolvePath(string modelName)
    {
        AudioProviderDefinition provider = AudioProviderRegistry.GetById(ProviderId)
            ?? throw new InvalidOperationException($"Unknown audio provider '{ProviderId}'.");
        string dir = AudioWeights.WeightsDirectory(provider);
        string variant = string.IsNullOrWhiteSpace(modelName) ? "htdemucs" : modelName.Trim();
        string direct = Path.Combine(dir, variant);
        if (File.Exists(direct))
        {
            return direct;
        }
        string th = Path.Combine(dir, variant + ".th");
        if (File.Exists(th))
        {
            return th;
        }
        string safet = Path.Combine(dir, variant + ".safetensors");
        return File.Exists(safet) ? safet : th; // 'th' is used in the not-found message
    }
}
