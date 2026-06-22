using System.IO;
using SwarmUI.Utils;
using Hartsy.Extensions.AudioLab.AudioProviders;
using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using HartsyInference.Core.Backends;
using HartsyInference.Core.Tensors;
using HartsyInference.Audio.Dsp;
using HartsyInference.Audio.Models.Hubert;
using HartsyInference.Audio.Models.Rvc;
using HartsyInference.Audio.Pipelines;
using HartsyInference.ModelHandler.SafeTensors;

namespace Hartsy.Extensions.AudioLab.AudioServices.Vc;

/// <summary>Per-model specifics for the generic <see cref="VcHandler"/>.</summary>
public sealed class VcModelDescriptor
{
    public required bool ManagesOwnWeights { get; init; }

    /// <summary>Stable cache key for (providerId, modelId).</summary>
    public required Func<string, string, string> CacheKey { get; init; }

    /// <summary>Loads the model into a runner. Loading is device-independent (no backend needed).</summary>
    public required Func<string, string, CancellationToken, Task<IVcRunner>> LoadAsync { get; init; }
}

/// <summary>Voice-conversion model registry. RVC re-voices a source clip with a trained voice model, using
/// the engine's HuBERT/ContentVec content encoder + the new <see cref="F0Estimator"/> (YIN pitch).</summary>
public static class VcModels
{
    private const string ContentVecFile = "contentvec.safetensors";

    /// <summary>RVC v2 (40 kHz) — user-placed voice model + a shared ContentVec encoder. Re-voices the source
    /// audio in the model's voice (speaker id 0).</summary>
    public static readonly VcModelDescriptor Rvc = new()
    {
        ManagesOwnWeights = false,
        CacheKey = (providerId, modelId) => ResolveRvcModel(providerId, modelId),
        LoadAsync = (providerId, modelId, ct) => LoadRvcAsync(ResolveRvcModel(providerId, modelId), ct),
    };

    private static string ResolveRvcModel(string providerId, string modelId)
    {
        AudioProviderDefinition provider = AudioProviderRegistry.GetById(providerId)
            ?? throw new InvalidOperationException($"Unknown audio provider '{providerId}'.");
        string dir = AudioWeights.WeightsDirectory(provider);
        string variant = (modelId ?? "").Trim();
        string direct = Path.Combine(dir, variant);
        if (File.Exists(direct))
        {
            return direct;
        }
        string withExt = Path.Combine(dir, variant + ".safetensors");
        return File.Exists(withExt) ? withExt : direct; // 'direct' is used in the not-found message
    }

    private static Task<IVcRunner> LoadRvcAsync(string rvcModelPath, CancellationToken ct)
    {
        if (!File.Exists(rvcModelPath))
        {
            throw new FileNotFoundException(
                $"RVC voice model not found: '{rvcModelPath}'. Place the RVC voice .safetensors (converted from the .pth) there.", rvcModelPath);
        }
        string contentVec = Path.Combine(Path.GetFullPath(AudioConfiguration.ModelRoot), ContentVecFile);
        if (!File.Exists(contentVec))
        {
            throw new FileNotFoundException(
                $"RVC needs the ContentVec/HuBERT content encoder — place '{ContentVecFile}' at '{contentVec}'.", contentVec);
        }

        Hubert hubert = new(HubertConfig.ChineseHubertBase);
        SafeTensorsLoader hubLoader = new();
        hubLoader.Load(contentVec);
        hubert.LoadWeights(hubLoader.GetAllTensors());

        RvcPipeline rvc = new(RvcConfig.V2_40k);
        SafeTensorsLoader rvcLoader = new();
        rvcLoader.Load(rvcModelPath);
        rvc.LoadWeights(rvcLoader.GetAllTensors());

        Logs.Info($"[AudioLab][RVC] Loaded voice '{Path.GetFileName(rvcModelPath)}' (40 kHz; ContentVec + YIN F0).");
        // Loaders kept alive for the runner's lifetime (the model tensors reference them); freed on Unload.
        return Task.FromResult<IVcRunner>(
            new VcRunner(rvc.SampleRate, (backend, src) => ConvertRvc(backend, hubert, rvc, src), hubLoader, rvcLoader, rvc));
    }

    private static float[] ConvertRvc(IBackend backend, Hubert hubert, RvcPipeline rvc, float[] source16k)
    {
        int tPcm = source16k.Length;
        Tensor pcm = new(new TensorShape(1, 1, tPcm), DType.F32);
        source16k.AsSpan().CopyTo(pcm.AsSpan<float>());
        Tensor content = hubert.Forward(backend, pcm, tPcm); // [1, 768, T]
        pcm.Dispose();
        try
        {
            int contentT = (int)content.Shape[2];
            float[] f0 = F0Estimator.EstimateYin(source16k, 16_000, hopSize: 320); // 50 Hz, same grid as the content frames
            return rvc.Convert(backend, content, AlignF0(f0, contentT), sid: 0);
        }
        finally
        {
            content.Dispose();
        }
    }

    /// <summary>RVC requires <c>f0.Length == content T</c>; trim or zero-pad (trailing = unvoiced).</summary>
    private static float[] AlignF0(float[] f0, int targetLen)
    {
        if (f0.Length == targetLen)
        {
            return f0;
        }
        float[] aligned = new float[targetLen];
        Array.Copy(f0, aligned, Math.Min(f0.Length, targetLen));
        return aligned;
    }
}
