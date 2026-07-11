using System.IO;
using SwarmUI.Utils;
using Hartsy.Extensions.AudioLab.AudioProviders;
using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using HartsyInference.Core.Backends;
using HartsyInference.Core.Tensors;
using HartsyInference.Audio.Cache;
using HartsyInference.Audio.Dsp;
using HartsyInference.Audio.Models.Hubert;
using HartsyInference.Audio.Models.Rvc;
using HartsyInference.Audio.Pipelines;
using HartsyInference.ModelHandler.PyTorch;
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

    /// <summary>Sample rate the source/target audio is decoded to before conversion (RVC 16 kHz content input;
    /// OpenVoice 22.05 kHz).</summary>
    public int InputSampleRate { get; init; } = 16_000;
}

/// <summary>Voice-conversion model registry. RVC re-voices a source clip with a trained voice model, using
/// the engine's HuBERT/ContentVec content encoder + the new <see cref="F0Estimator"/> (YIN pitch).</summary>
public static class VcModels
{
    private const string ContentVecFile = "contentvec.safetensors";
    // ContentVec content encoder. There is no upstream "contentvec.safetensors"; the canonical artifact is the
    // HF-transformers HubertModel at lengyue233/content-vec-best (pytorch_model.bin, MIT) — its keys match the
    // engine's Hubert layout exactly (verified), so the conversion is a pickle→safetensors passthrough.
    private const string ContentVecRepo = "lengyue233/content-vec-best";
    private const string ContentVecSourceFile = "pytorch_model.bin";

    /// <summary>RVC v2 (40 kHz) — user-placed voice model + a shared ContentVec encoder. Re-voices the source
    /// audio in the model's voice (speaker id 0).</summary>
    public static readonly VcModelDescriptor Rvc = new()
    {
        ManagesOwnWeights = false,
        InputSampleRate = 16_000, // RVC's HuBERT content encoder + YIN F0 both run on 16 kHz source.
        CacheKey = (providerId, modelId) => ResolveRvcModel(providerId, modelId),
        LoadAsync = (providerId, modelId, ct) => LoadRvcAsync(ResolveRvcModel(providerId, modelId), ct),
    };

    // RVC voice models ship as native PyTorch `.pth` (the training output); we load that directly (no manual
    // conversion), and also accept a `.safetensors` a power user dropped in. Search order per variant name.
    private static readonly string[] RvcExtensions = ["", ".safetensors", ".pth", ".pt"];

    private static string ResolveRvcModel(string providerId, string modelId)
    {
        AudioProviderDefinition provider = AudioProviderRegistry.GetById(providerId)
            ?? throw new InvalidOperationException($"Unknown audio provider '{providerId}'.");
        string dir = AudioWeights.WeightsDirectory(provider);
        string variant = (modelId ?? "").Trim();
        foreach (string ext in RvcExtensions)
        {
            string p = Path.Combine(dir, variant + ext);
            if (File.Exists(p))
            {
                return p;
            }
        }
        return Path.Combine(dir, variant); // not-found path, used in the message below
    }

    /// <summary>Loads a checkpoint by extension: <c>.safetensors</c> (mmap) or a native PyTorch <c>.pth</c>/<c>.pt</c>
    /// pickle. Weights keep their stored dtype (fp16 stays fp16 — the model loaders upcast only the host-touched
    /// weights). Returns the tensor map + the owning loader (kept alive for the tensors' lifetime).</summary>
    private static (IReadOnlyDictionary<string, Tensor> Tensors, IDisposable Loader) LoadCheckpoint(string path)
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

    private static async Task<IVcRunner> LoadRvcAsync(string rvcModelPath, CancellationToken ct)
    {
        if (!File.Exists(rvcModelPath))
        {
            throw new FileNotFoundException(
                $"RVC voice model not found: '{rvcModelPath}'. Drop the RVC voice model (.pth or .safetensors) there.", rvcModelPath);
        }
        string contentVec = Path.Combine(Path.GetFullPath(AudioConfiguration.ModelRoot), ContentVecFile);
        await EnsureContentVecAsync(contentVec, ct).ConfigureAwait(false);

        Hubert hubert = new(HubertConfig.ChineseHubertBase);
        SafeTensorsLoader hubLoader = new();
        hubLoader.Load(contentVec);
        hubert.LoadWeights(hubLoader.GetAllTensors());

        (IReadOnlyDictionary<string, Tensor> rvcTensors, IDisposable rvcLoader) = LoadCheckpoint(rvcModelPath);
        RvcConfig cfg = DetectRvcConfig(rvcTensors);
        RvcPipeline rvc = new(cfg);
        rvc.LoadWeights(rvcTensors);

        Logs.Info($"[AudioLab][RVC] Loaded voice '{Path.GetFileName(rvcModelPath)}' ({rvc.SampleRate} Hz; ContentVec + YIN F0).");
        // Loaders kept alive for the runner's lifetime (the model tensors reference them); freed on Unload.
        // RVC carries the target voice in its trained weights — the target argument is unused.
        return new VcRunner(rvc.SampleRate, (backend, src, _, req) => ConvertRvc(backend, hubert, rvc, src, req.PitchShift), hubLoader, rvcLoader, rvc);
    }

    /// <summary>Ensures the shared ContentVec encoder exists as <c>contentvec.safetensors</c>. There is no upstream
    /// file by that name, so on first use we fetch the HF-transformers ContentVec (<see cref="ContentVecRepo"/>,
    /// MIT) and re-save its pickle state dict as safetensors — its keys already match the engine's Hubert layout,
    /// so it's a straight passthrough (no remapping).</summary>
    private static async Task EnsureContentVecAsync(string contentVecPath, CancellationToken ct)
    {
        if (File.Exists(contentVecPath))
        {
            return;
        }
        Logs.Info($"[AudioLab][RVC] ContentVec encoder missing — fetching {ContentVecRepo} and converting to {ContentVecFile}...");
        string binPath = await AudioModelCache.GetAsync(ContentVecRepo, ContentVecSourceFile, ct: ct).ConfigureAwait(false);
        PytorchPickleLoader loader = new();
        try
        {
            loader.Load(binPath);
            Directory.CreateDirectory(Path.GetDirectoryName(contentVecPath));
            string tmp = contentVecPath + ".tmp";
            SafeTensorsWriter.Save(tmp, loader.GetAllTensors());
            File.Move(tmp, contentVecPath, overwrite: true);
        }
        finally
        {
            loader.Dispose();
        }
        Logs.Info($"[AudioLab][RVC] {ContentVecFile} ready.");
    }

    /// <summary>Picks the RVC config from the model's first upsample-kernel width: the ConvTranspose1d kernel is 24
    /// for 48 kHz (<c>[12,10,2,2]</c>) and 16 for 40 kHz (<c>[10,10,2,2]</c>). weight-norm models store <c>weight_v</c>;
    /// pre-fused models store <c>weight</c>. Defaults to 40 kHz when the key is absent.</summary>
    private static RvcConfig DetectRvcConfig(IReadOnlyDictionary<string, Tensor> w)
    {
        Tensor? ups0 = w.TryGetValue("dec.ups.0.weight_v", out Tensor? v) ? v
            : w.TryGetValue("dec.ups.0.weight", out Tensor? ww) ? ww : null;
        int kernel = ups0 is not null ? (int)ups0.Shape[ups0.Shape.Rank - 1] : 16;
        return kernel >= 20 ? RvcConfig.V2_48k : RvcConfig.V2_40k;
    }

    private static float[] ConvertRvc(IBackend backend, Hubert hubert, RvcPipeline rvc, float[] source16k, double pitchSemitones)
    {
        int tPcm = source16k.Length;
        Tensor pcm = new(new TensorShape(1, 1, tPcm), DType.F32);
        source16k.AsSpan().CopyTo(pcm.AsSpan<float>());
        Tensor content = hubert.Forward(backend, pcm, tPcm); // [1, 768, T] at 50 Hz (HuBERT frame rate)
        pcm.Dispose();
        // RVC upsamples the HuBERT features 2× (F.interpolate scale_factor=2, nearest) → 100 Hz, so the NSF
        // decoder's ∏upsample_rates (400 @ 40k / 480 @ 48k) maps back to the true output rate. Skipping this
        // halved the output duration (2×-fast, garbled speech). F0 is then sampled at the matching 100 Hz grid.
        Tensor content2x = Interpolate2xNearest(content);
        content.Dispose();
        try
        {
            int contentT = (int)content2x.Shape[2];
            float[] f0 = F0Estimator.EstimateYin(source16k, 16_000, hopSize: 160); // 100 Hz, matches the 2× content grid
            if (pitchSemitones != 0d)
            {
                f0 = RvcPitch.Shift(f0, (float)pitchSemitones);
            }
            return rvc.Convert(backend, content2x, AlignF0(f0, contentT), sid: 0);
        }
        finally
        {
            content2x.Dispose();
        }
    }

    /// <summary>Nearest-neighbour 2× upsample of the content along time: <c>[1, C, T] → [1, C, 2T]</c> (each frame
    /// duplicated), matching RVC's <c>F.interpolate(scale_factor=2)</c> before the synthesizer.</summary>
    private static Tensor Interpolate2xNearest(Tensor content)
    {
        int c = (int)content.Shape[1], t = (int)content.Shape[2];
        ReadOnlySpan<float> src = content.AsSpan<float>();
        Tensor outT = new(new TensorShape(1, c, 2 * t), DType.F32);
        Span<float> dst = outT.AsSpan<float>();
        for (int ch = 0; ch < c; ch++)
        {
            int sBase = ch * t, dBase = ch * 2 * t;
            for (int i = 0; i < t; i++)
            {
                float v = src[sBase + i];
                dst[dBase + 2 * i] = v;
                dst[dBase + 2 * i + 1] = v;
            }
        }
        return outT;
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
