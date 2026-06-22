using System.IO;
using System.Linq;
using SwarmUI.Utils;
using Hartsy.Extensions.AudioLab.AudioProviders;
using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using HartsyInference.Core.Backends;
using HartsyInference.Core.Tensors;
using HartsyInference.Audio.Cache;
using HartsyInference.Audio.Models.Codecs.EnCodec;
using HartsyInference.Audio.Models.Codecs.XCodec;
using HartsyInference.Audio.Models.Music;
using HartsyInference.Audio.Pipelines;
using HartsyInference.Diffusion.Models.TextEncoders;
using HartsyInference.ModelHandler.CheckpointConverters;
using HartsyInference.Tokenizers;

namespace Hartsy.Extensions.AudioLab.AudioServices.Music;

/// <summary>Per-model specifics for the generic <see cref="MusicHandler"/>.</summary>
public sealed class MusicModelDescriptor
{
    /// <summary>True for HF-auto-downloaded models (MusicGen/AudioGen); false for user-placed checkpoints (YuE).</summary>
    public required bool ManagesOwnWeights { get; init; }

    /// <summary>Stable cache key for a model id (HF repo for MusicGen/AudioGen; folder path for YuE).</summary>
    public required Func<string, string, string> CacheKey { get; init; }

    /// <summary>Loads the model into a runner. Args: (providerId, modelId, cancel).</summary>
    public required Func<string, string, CancellationToken, Task<IMusicRunner>> LoadAsync { get; init; }
}

/// <summary>The music model registry. MusicGen/AudioGen reuse the engine's combined-checkpoint converters +
/// <see cref="MusicGenPipeline"/>; YuE reuses <see cref="YuePipeline"/> + <see cref="YueTokenizer"/>.</summary>
public static class MusicModels
{
    private const int T5MaxTokens = 256;

    #region MusicGen / AudioGen (HF combined safetensors)

    /// <summary>MusicGen (facebook/musicgen-{small,medium,large}) — 32 kHz; size inferred from the checkpoint.</summary>
    public static readonly MusicModelDescriptor MusicGen = new()
    {
        ManagesOwnWeights = true,
        CacheKey = (_, modelId) => ResolveMusicGenRepo(modelId),
        LoadAsync = (_, modelId, ct) => LoadMusicGenFamilyAsync(ResolveMusicGenRepo(modelId), audioGen: false, ct),
    };

    /// <summary>AudioGen (facebook/audiogen-medium) — sound effects at 16 kHz; fixed AudioGen preset.</summary>
    public static readonly MusicModelDescriptor AudioGen = new()
    {
        ManagesOwnWeights = true,
        CacheKey = (_, _) => "facebook/audiogen-medium",
        LoadAsync = (_, _, ct) => LoadMusicGenFamilyAsync("facebook/audiogen-medium", audioGen: true, ct),
    };

    private static string ResolveMusicGenRepo(string modelId)
    {
        string id = (modelId ?? "").Trim();
        if (id.Contains('/'))
        {
            return id;
        }
        string lower = id.ToLowerInvariant();
        if (lower.Contains("large")) return "facebook/musicgen-large";
        if (lower.Contains("medium")) return "facebook/musicgen-medium";
        return "facebook/musicgen-small";
    }

    /// <summary>Loads a MusicGen-family combined checkpoint (decoder + EnCodec + T5-base in one file).</summary>
    private static async Task<IMusicRunner> LoadMusicGenFamilyAsync(string repo, bool audioGen, CancellationToken ct)
    {
        string path = await AudioModelCache.GetAsync(repo, "model.safetensors", ct: ct).ConfigureAwait(false);

        (Dictionary<string, Tensor> decW, var decLoader) = MusicGenCheckpointConverter.LoadDecoder(path, castToF32: true);
        MusicGenConfig config = audioGen ? MusicGenConfig.AudioGen : InferMusicGenSize(decW, repo);
        MusicGenDecoder decoder = new(config);
        decoder.LoadWeights(decW, prefix: "model.decoder");

        (Dictionary<string, Tensor> codW, var codLoader) = MusicGenCheckpointConverter.LoadEnCodec(path, castToF32: true);
        EnCodec codec = new(audioGen ? EnCodecConfig.EnCodec16kHz : EnCodecConfig.EnCodec32kHz);
        codec.LoadWeights(codW);

        (Dictionary<string, Tensor> t5W, var t5Loader) = MusicGenCheckpointConverter.LoadTextEncoder(path, castToF32: true);
        T5TextEncoder t5 = new(T5TextEncoderConfig.T5Base);
        t5.LoadWeights(t5W);
        T5Tokenizer tokenizer = new(maxLength: T5MaxTokens);

        MusicGenPipeline pipeline = new(config, decoder, codec);
        Logs.Info($"[AudioLab][MusicGen] Loaded {repo} ({config.CodecSampleRate} Hz).");

        float[] Synth(IBackend backend, MusicRequest req)
        {
            int[] tokens = tokenizer.Encode(req.Prompt);
            Tensor t5States = t5.Encode(backend, [tokens], [T5Tokenizer.CreateAttentionMask(tokens)]);
            backend.Sync();
            backend.FreeWeights(t5.EnumerateWeights());
            try
            {
                // MusicGen/AudioGen are trained on ≤30 s windows.
                return pipeline.Synthesize(backend, t5States, seconds: (float)Math.Clamp(req.Duration, 1d, 30d), seed: req.Seed);
            }
            finally
            {
                t5States.Dispose();
            }
        }

        return new MusicRunner(config.CodecSampleRate, Synth, t5, tokenizer, decLoader, codLoader, t5Loader);
    }

    /// <summary>Infers Small/Medium/Large from the decoder's final layer-norm width (1024/1536/2048).</summary>
    private static MusicGenConfig InferMusicGenSize(Dictionary<string, Tensor> decoderWeights, string repo)
    {
        int hidden = decoderWeights.TryGetValue("model.decoder.layer_norm.weight", out Tensor finalNorm) ? (int)finalNorm.Shape[0] : 0;
        return hidden switch
        {
            1024 => MusicGenConfig.Small,
            1536 => MusicGenConfig.Medium,
            2048 => MusicGenConfig.Large,
            _ => throw new InvalidOperationException($"MusicGen checkpoint '{repo}' has unrecognized decoder width {hidden} (expected 1024/1536/2048)."),
        };
    }

    #endregion

    #region YuE (user-placed folder checkpoint)

    /// <summary>YuE Stage-1 (m-a-p/YuE-s1-7B-anneal-*) — folder checkpoint + sibling tokenizer.model + xcodec.safetensors.
    /// Vocal-cb0 only until the engine's Stage-2 residual upsampler ships.</summary>
    public static readonly MusicModelDescriptor Yue = new()
    {
        ManagesOwnWeights = false,
        CacheKey = (providerId, modelId) => ResolveYueFolder(providerId, modelId),
        LoadAsync = (providerId, modelId, ct) => LoadYueAsync(ResolveYueFolder(providerId, modelId), ct),
    };

    /// <summary>Resolves the YuE checkpoint folder under the AudioLab model root for the chosen variant.</summary>
    private static string ResolveYueFolder(string providerId, string modelId)
    {
        AudioProviderDefinition provider = AudioProviderRegistry.All.FirstOrDefault(p => p.Id == providerId)
            ?? throw new InvalidOperationException($"Unknown audio provider '{providerId}'.");
        string baseDir = AudioWeights.WeightsDirectory(provider);
        string variant = (modelId ?? "").Trim();
        return string.IsNullOrEmpty(variant) ? baseDir : Path.Combine(baseDir, variant);
    }

    private static Task<IMusicRunner> LoadYueAsync(string folder, CancellationToken ct)
    {
        if (!Directory.Exists(folder))
        {
            throw new DirectoryNotFoundException(
                $"YuE checkpoint folder not found: '{folder}'. Place the m-a-p/YuE-s1-7B-anneal-* folder there, "
                + "with sibling 'tokenizer.model' and 'xcodec.safetensors' (from m-a-p/xcodec_mini_infer).");
        }
        string tokenizerPath = FindSibling(folder, "tokenizer.model")
            ?? throw new InvalidOperationException($"YuE needs 'tokenizer.model' (mm_tokenizer_v0.2_hf) in or beside '{folder}'.");
        string xcodecPath = FindSibling(folder, "xcodec.safetensors")
            ?? throw new InvalidOperationException($"YuE needs 'xcodec.safetensors' (converted from m-a-p/xcodec_mini_infer) in or beside '{folder}'.");

        YueConfig config = YueConfig.V1;
        (Dictionary<string, Tensor> s1W, IDisposable s1Loader) = YueCheckpointConverter.LoadStage1(folder, castToF32: true);
        YueStage1Lm stage1 = new(config);
        stage1.LoadWeights(s1W, prefix: "model");

        (Dictionary<string, Tensor> cW, var cLoader) = YueCheckpointConverter.LoadXCodec(xcodecPath, castToF32: true);
        XCodec xcodec = new(XCodecConfig.XCodec16kHz);
        xcodec.LoadWeights(cW);

        YueTokenizer tokenizer = new(tokenizerPath);
        YuePipeline pipeline = new(config, stage1, xcodec);
        Logs.Info($"[AudioLab][YuE] Loaded Stage-1 from '{folder}' (16 kHz, vocal-cb0 only — Stage-2 pending).");

        float[] Synth(IBackend backend, MusicRequest req)
        {
            string genre = string.IsNullOrWhiteSpace(req.Genre) ? "pop" : req.Genre;
            int[] promptIds = tokenizer.EncodeStage1Prompt(genre, req.Prompt);
            int maxFrames = (int)(Math.Clamp(req.Duration, 5d, 300d) * config.FrameRateHz);
            return pipeline.Synthesize(backend, promptIds, maxFrames: maxFrames, seed: req.Seed);
        }

        // pipeline.Dispose() disposes the Stage-1 LM; also drop the loaders + tokenizer.
        return Task.FromResult<IMusicRunner>(new MusicRunner(config.SampleRate, Synth, pipeline, s1Loader, cLoader, tokenizer));
    }

    /// <summary>Finds a file inside the checkpoint folder, then one directory up (so variants can share one copy).</summary>
    private static string FindSibling(string folder, string fileName)
    {
        string inside = Path.Combine(folder, fileName);
        if (File.Exists(inside))
        {
            return inside;
        }
        string parent = Path.Combine(Directory.GetParent(folder)?.FullName ?? folder, fileName);
        return File.Exists(parent) ? parent : null;
    }

    #endregion
}
