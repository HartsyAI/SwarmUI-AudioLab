using System.IO;
using SwarmUI.Utils;
using Hartsy.Extensions.AudioLab.AudioProviders;
using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.AudioServices.Tts;
using HartsyInference.Core.Backends;
using HartsyInference.Core.Tensors;
using HartsyInference.Audio.Cache;
using HartsyInference.Audio.Models.Codecs.EnCodec;
using HartsyInference.Audio.Models.Codecs.Oobleck;
using HartsyInference.Audio.Models.Codecs.XCodec;
using HartsyInference.Audio.Models.HeartMula;
using HartsyInference.Audio.Models.Music;
using HartsyInference.Audio.Pipelines;
using HartsyInference.Diffusion.Models.Denoisers;
using HartsyInference.Diffusion.Models.Music;
using HartsyInference.Diffusion.Models.TextEncoders;
using HartsyInference.Diffusion.Pipelines;
using HartsyInference.Diffusion.Utilities;
using HartsyInference.ModelHandler.CheckpointConverters;
using HartsyInference.ModelHandler.SafeTensors;
using HartsyInference.Tokenizers;

namespace Hartsy.Extensions.AudioLab.AudioServices.Music;

/// <summary>Per-model specifics for the generic <see cref="MusicHandler"/>.</summary>
public sealed class MusicModelDescriptor
{
    /// <summary>True for HF-auto-downloaded models (MusicGen/AudioGen); false for user-placed checkpoints (YuE, ACE-Step).</summary>
    public required bool ManagesOwnWeights { get; init; }

    /// <summary>Stable cache key for a model id (HF repo, or local checkpoint path).</summary>
    public required Func<string, string, string> CacheKey { get; init; }

    /// <summary>Loads the model into a runner. Args: (backend, providerId, modelId, cancel). The backend is
    /// needed by pipelines that bind to a device at construction (ACE-Step); others ignore it.</summary>
    public required Func<IBackend, string, string, CancellationToken, Task<IMusicRunner>> LoadAsync { get; init; }
}

/// <summary>The music model registry. MusicGen/AudioGen reuse the combined-checkpoint converters +
/// <see cref="MusicGenPipeline"/>; ACE-Step reuses <see cref="AceStepPipeline15"/>; YuE reuses
/// <see cref="YuePipeline"/> + YueTokenizer.</summary>
public static class MusicModels
{
    private const int T5MaxTokens = 256;

    #region MusicGen / AudioGen (HF combined safetensors)

    /// <summary>MusicGen (facebook/musicgen-{small,medium,large}) — 32 kHz; size inferred from the checkpoint.</summary>
    public static readonly MusicModelDescriptor MusicGen = new()
    {
        ManagesOwnWeights = true,
        CacheKey = (_, modelId) => ResolveMusicGenRepo(modelId),
        LoadAsync = (_, _, modelId, ct) => LoadMusicGenFamilyAsync(ResolveMusicGenRepo(modelId), audioGen: false, ct),
    };

    /// <summary>AudioGen (facebook/audiogen-medium) — sound effects at 16 kHz; fixed AudioGen preset.</summary>
    public static readonly MusicModelDescriptor AudioGen = new()
    {
        ManagesOwnWeights = true,
        CacheKey = (_, _) => "facebook/audiogen-medium",
        LoadAsync = (_, _, _, ct) => LoadMusicGenFamilyAsync("facebook/audiogen-medium", audioGen: true, ct),
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
        Dictionary<string, Tensor> decW, codW, t5W;
        IDisposable decLoader, codLoader, t5Loader;
        // AudioGen — and MusicGen *large*, whose combined HF file is sharded — load from the AudioCraft single-file
        // pickles instead: decoder = state_dict.bin, EnCodec = compression_state_dict.bin, T5 = standalone t5-base.
        bool audioCraft = audioGen || repo.Contains("large", StringComparison.OrdinalIgnoreCase);
        if (audioCraft)
        {
            string decPath = await AudioModelCache.GetAsync(repo, "state_dict.bin", ct: ct).ConfigureAwait(false);
            string codPath = await AudioModelCache.GetAsync(repo, "compression_state_dict.bin", ct: ct).ConfigureAwait(false);
            string t5Path = await AudioModelCache.GetAsync("google-t5/t5-base", "pytorch_model.bin", ct: ct).ConfigureAwait(false);
            (decW, decLoader) = MusicGenCheckpointConverter.LoadDecoderAny(decPath, castToF32: true);
            (codW, codLoader) = MusicGenCheckpointConverter.LoadEnCodecAny(codPath, castToF32: true);
            (t5W, t5Loader) = MusicGenCheckpointConverter.LoadTextEncoderAny(t5Path, castToF32: true);
        }
        else
        {
            // MusicGen small/medium ship one combined file: small = model.safetensors, medium = pytorch_model.bin.
            string path = await ResolveMusicGenCombinedAsync(repo, ct).ConfigureAwait(false);
            (decW, decLoader) = MusicGenCheckpointConverter.LoadDecoderAny(path, castToF32: true);
            (codW, codLoader) = MusicGenCheckpointConverter.LoadEnCodecAny(path, castToF32: true);
            (t5W, t5Loader) = MusicGenCheckpointConverter.LoadTextEncoderAny(path, castToF32: true);
        }

        MusicGenConfig config = audioGen ? MusicGenConfig.AudioGen : InferMusicGenSize(decW, repo);
        MusicGenDecoder decoder = new(config);
        decoder.LoadWeights(decW, prefix: "model.decoder");
        EnCodec codec = new(audioGen ? EnCodecConfig.EnCodec16kHz : EnCodecConfig.EnCodec32kHz);
        codec.LoadWeights(codW);
        T5TextEncoder t5 = new(T5TextEncoderConfig.T5Base);
        t5.LoadWeights(t5W);
        T5Tokenizer tokenizer = new(maxLength: T5MaxTokens);

        MusicGenPipeline pipeline = new(config, decoder, codec);
        Logs.Info($"[AudioLab][MusicGen] Loaded {repo} ({config.CodecSampleRate} Hz).");

        MusicAudio Synth(IBackend backend, MusicRequest req)
        {
            int[] tokens = tokenizer.Encode(req.Prompt);
            Tensor t5States = t5.Encode(backend, [tokens], [T5Tokenizer.CreateAttentionMask(tokens)]);
            backend.Sync();
            backend.FreeWeights(t5.EnumerateWeights());
            try
            {
                // MusicGen/AudioGen are trained on ≤30 s windows.
                float[] samples = pipeline.Synthesize(backend, t5States, seconds: (float)Math.Clamp(req.Duration, 1d, 30d), seed: req.Seed);
                return MusicAudio.Mono(samples);
            }
            finally
            {
                t5States.Dispose();
            }
        }

        return new MusicRunner(config.CodecSampleRate, Synth, t5, tokenizer, decLoader, codLoader, t5Loader);
    }

    /// <summary>The MusicGen combined checkpoint: small ships a single <c>model.safetensors</c>; medium ships the
    /// HF-transformers <c>pytorch_model.bin</c> instead. (Large is sharded — not handled here.)</summary>
    private static async Task<string> ResolveMusicGenCombinedAsync(string repo, CancellationToken ct)
    {
        try
        {
            return await AudioModelCache.GetAsync(repo, "model.safetensors", ct: ct).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return await AudioModelCache.GetAsync(repo, "pytorch_model.bin", ct: ct).ConfigureAwait(false);
        }
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

    #region ACE-Step 1.5 (user-placed DiT + auto-downloaded VAE & encoder)

    private const int QwenEosId = 151643; // Qwen3-Embedding <|endoftext|>
    private const string AceStep15Repo = "Comfy-Org/ace_step_1.5_ComfyUI_files";
    private const string AceStep15VaeFile = "split_files/vae/ace_1.5_vae.safetensors";
    private const string AceStep15TurboFile = "split_files/diffusion_models/acestep_v1.5_turbo.safetensors";
    private const string QwenEmbeddingRepo = "Qwen/Qwen3-Embedding-0.6B";
    private const string QwenEmbeddingFile = "model.safetensors";

    /// <summary>ACE-Step 1.5 (2B turbo flow-matching music DiT over 25 Hz Oobleck latents → 48 kHz stereo).
    /// The DiT, Oobleck VAE, and Qwen3-Embedding encoder all come from the Comfy-Org ACE-Step 1.5 distribution
    /// (auto-downloaded). The engine implements the turbo path (8-step Euler, no CFG); a user-placed checkpoint
    /// in the model folder overrides the downloaded DiT.</summary>
    public static readonly MusicModelDescriptor AceStep = new()
    {
        ManagesOwnWeights = false,
        CacheKey = (providerId, modelId) => ResolveLocalCheckpoint(providerId, modelId),
        LoadAsync = (backend, providerId, modelId, ct) =>
        {
            // SFT/Base need the 50-step CFG pipeline the engine's turbo-only path can't run — fail clearly
            // instead of silently downloading and running the turbo checkpoint in their place.
            string variant = (modelId ?? "").Trim().ToLowerInvariant();
            if (variant is "sft" or "base")
            {
                throw new NotSupportedException(
                    "[AudioLab][ACE-Step] The SFT/Base checkpoints require the 50-step CFG pipeline, which the "
                    + "engine's turbo-only path doesn't implement yet. Pick an ACE-Step 1.5 Turbo variant.");
            }
            return LoadAceStepAsync(backend, ResolveLocalCheckpoint(providerId, modelId), ct);
        },
    };

    private static async Task<IMusicRunner> LoadAceStepAsync(IBackend backend, string localPath, CancellationToken ct)
    {
        // A user-placed checkpoint in the model folder wins; otherwise pull the official Comfy-Org turbo DiT.
        string mainPath = File.Exists(localPath)
            ? localPath
            : await AudioModelCache.GetAsync(AceStep15Repo, AceStep15TurboFile, ct: ct).ConfigureAwait(false);
        string vaePath = await AudioModelCache.GetAsync(AceStep15Repo, AceStep15VaeFile, ct: ct).ConfigureAwait(false);
        string qwenPath = await AudioModelCache.GetAsync(QwenEmbeddingRepo, QwenEmbeddingFile, ct: ct).ConfigureAwait(false);

        AceStep15Config config = new();
        (Dictionary<string, Tensor> weights, SafeTensorsLoader mainLoader) = AceStepCheckpointConverter.LoadModel15(mainPath, castToF32: true);
        AceStep15Dit dit = new(config);
        dit.LoadWeights(weights);
        AceStep15ConditionEncoder conditionEncoder = new(config);
        conditionEncoder.LoadWeights(weights);

        SafeTensorsLoader vaeLoader = new();
        vaeLoader.Load(vaePath);
        OobleckVae vae = new(OobleckConfig.AceStep15);
        vae.LoadWeights(vaeLoader.GetAllTensors());

        SafeTensorsLoader qwenLoader = new();
        qwenLoader.Load(qwenPath);
        LlamaStyleEncoder qwen = new(LlamaStyleEncoderConfig.Qwen3_Embedding_0_6B);
        qwen.LoadWeights(qwenLoader.GetAllTensors());
        Qwen3Tokenizer tokenizer = new();

        AceStepPipeline15 pipeline = new(backend, dit, conditionEncoder, vae, config);
        Logs.Info("[AudioLab][ACE-Step] Loaded 1.5 (text/lyrics-to-music, 48 kHz stereo, turbo 8-step).");

        Tensor EncodeQwen(IBackend b, string text)
        {
            IReadOnlyList<int> raw = tokenizer.EncodeRaw(text ?? "");
            int[] tokens = new int[raw.Count + 1];
            for (int i = 0; i < raw.Count; i++)
            {
                tokens[i] = raw[i];
            }
            tokens[^1] = QwenEosId;
            Tensor batchT = qwen.Encode(b, [tokens]);
            Tensor sliced = CfgHelper.SliceBatchElement(batchT, 0, tokens.Length, config.TextHiddenDim);
            batchT.Dispose();
            return sliced;
        }

        MusicAudio Synth(IBackend b, MusicRequest req)
        {
            string style = string.IsNullOrWhiteSpace(req.Genre) ? "pop" : req.Genre;
            Tensor textHidden = EncodeQwen(b, style);
            Tensor lyricHidden = string.IsNullOrWhiteSpace(req.Prompt) ? null : EncodeQwen(b, req.Prompt);
            b.Sync();
            b.FreeWeights(qwen.EnumerateWeights());
            try
            {
                (float[] left, float[] right, int _, int _) = pipeline.Generate(
                    textHidden, lyricHidden, Math.Clamp(req.Duration, 1d, 600d),
                    shift: req.Shift.HasValue ? (float)req.Shift.Value : null, seed: req.Seed);
                return MusicAudio.Stereo(left, right);
            }
            finally
            {
                textHidden.Dispose();
                lyricHidden?.Dispose();
            }
        }

        return new MusicRunner(48_000, Synth, pipeline as IDisposable, qwen, tokenizer, mainLoader, vaeLoader, qwenLoader);
    }

    #endregion

    #region Local-checkpoint resolution (ACE-Step, YuE)

    /// <summary>Resolves a user-placed checkpoint under the AudioLab model root for the chosen variant.</summary>
    private static string ResolveLocalCheckpoint(string providerId, string modelId)
    {
        AudioProviderDefinition provider = AudioProviderRegistry.GetById(providerId)
            ?? throw new InvalidOperationException($"Unknown audio provider '{providerId}'.");
        string baseDir = AudioWeights.WeightsDirectory(provider);
        string variant = (modelId ?? "").Trim();
        // Single-file providers (ACE-Step) register the variant→filename map; map to the real .safetensors.
        // Folder-checkpoint providers (YuE) have no registry entry — fall back to the variant as a subpath.
        AudioWeightsRegistry.DownloadSpec spec = AudioWeightsRegistry.Resolve(providerId, variant);
        if (spec is not null)
        {
            return Path.Combine(baseDir, spec.FileName);
        }
        return string.IsNullOrEmpty(variant) ? baseDir : Path.Combine(baseDir, variant);
    }

    #endregion

    #region YuE (user-placed folder checkpoint)

    /// <summary>YuE Stage-1 (m-a-p/YuE-s1-7B-anneal-*) — folder checkpoint + sibling tokenizer.model + xcodec.safetensors.
    /// Vocal-cb0 only until the engine's Stage-2 residual upsampler ships.</summary>
    public static readonly MusicModelDescriptor Yue = new()
    {
        ManagesOwnWeights = false,
        CacheKey = (providerId, modelId) => ResolveLocalCheckpoint(providerId, modelId),
        LoadAsync = (_, providerId, modelId, ct) => LoadYueAsync(ResolveLocalCheckpoint(providerId, modelId), ct),
    };

    private static Task<IMusicRunner> LoadYueAsync(string folder, CancellationToken ct)
    {
        if (!Directory.Exists(folder))
        {
            throw new DirectoryNotFoundException(
                $"YuE checkpoint folder not found: '{folder}'. YuE is a user-placed folder checkpoint (no auto-download): "
                + "put the m-a-p/YuE-s1-7B-anneal-* folder there. Its 'tokenizer.model' ships inside that folder; also "
                + "place 'xcodec.safetensors' (converted from m-a-p/xcodec_mini_infer) in or beside it.");
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

        MusicAudio Synth(IBackend backend, MusicRequest req)
        {
            string genre = string.IsNullOrWhiteSpace(req.Genre) ? "pop" : req.Genre;
            int[] promptIds = tokenizer.EncodeStage1Prompt(genre, req.Prompt);
            int maxFrames = (int)(Math.Clamp(req.Duration, 5d, 300d) * config.FrameRateHz);
            return MusicAudio.Mono(pipeline.Synthesize(backend, promptIds, maxFrames: maxFrames, seed: req.Seed));
        }

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

    #region HeartMuLa (HeartMuLa-oss-3B — HF auto-download)

    private const string HeartMulaRepo = "HeartMuLa/HeartMuLa-oss-3B";
    private const string HeartMulaHnyRepo = "HeartMuLa/HeartMuLa-oss-3B-happy-new-year";
    // The dated RL repo is public; the undated "HeartMuLa-RL-oss-3B" is gated (401) — resolve to the dated one.
    private const string HeartMulaRlRepo = "HeartMuLa/HeartMuLa-RL-oss-3B-20260123";
    private const string HeartCodecRepo = "HeartMuLa/HeartCodec-oss-20260123";

    /// <summary>HeartMuLa-oss-3B (Apache 2.0) — a CSM-shaped music LM (Llama-3B global + Llama-300M depth over an
    /// 8-codebook grid) whose flow-matching HeartCodec decodes to 48 kHz. Lyrics/prompt → song. The LM
    /// (sharded ~15.75 GB) and the HeartCodec decoder both auto-download from HuggingFace.
    ///
    /// <para><b>Runtime-pending:</b> the engine's HeartCodec + MuQ conditioning are wired but not yet validated
    /// against the real checkpoints (the LM reuses the verified CSM stack). MuQ style conditioning is omitted
    /// (unconditional). Generates end-to-end; audible-quality parity is a follow-up. If the HeartCodec repo's
    /// weight filename differs from the safetensors/pickle names probed here, the codec download will 404 and the
    /// filename must be adjusted.</para></summary>
    public static readonly MusicModelDescriptor HeartMula = new()
    {
        ManagesOwnWeights = true,
        CacheKey = (_, modelId) => ResolveHeartMulaRepo(modelId),
        LoadAsync = (_, _, modelId, ct) => LoadHeartMulaAsync(ResolveHeartMulaRepo(modelId), ct),
    };

    private static string ResolveHeartMulaRepo(string modelId)
    {
        string id = (modelId ?? "").Trim();
        if (id.Contains('/'))
        {
            return id;
        }
        string lower = id.ToLowerInvariant();
        if (lower.Contains("rl")) return HeartMulaRlRepo;                      // 3b-rl
        if (lower.Contains("hny") || lower.Contains("happy")) return HeartMulaHnyRepo; // 3b-hny
        return HeartMulaRepo;                                                  // 3b-base
    }

    private static async Task<IMusicRunner> LoadHeartMulaAsync(string repo, CancellationToken ct)
    {
        // LM = sharded safetensors (no tokenizer files); codec = a separate flow-matching decoder checkpoint.
        (IReadOnlyDictionary<string, Tensor> lmW, IDisposable[] lmLoaders) = await TtsModels.LoadCheckpointAsync(repo, ct).ConfigureAwait(false);
        (IReadOnlyDictionary<string, Tensor> codecW, IDisposable[] codecLoaders) = await TtsModels.LoadCheckpointAsync(HeartCodecRepo, ct).ConfigureAwait(false);

        HeartMulaConfig config = HeartMulaConfig.Oss3B;
        HeartMulaPipeline pipeline = new(config);
        pipeline.LoadWeights(lmW);
        pipeline.LoadCodecWeights(codecW);

        // Lyrics use the Llama-3.2 128k vocab; Qwen2's tokenizer shares that base vocab (no special HeartMuLa asset).
        Qwen2Tokenizer tokenizer = new();
        Logs.Info($"[AudioLab][HeartMuLa] Loaded {repo} + {HeartCodecRepo} (CSM-shaped LM + HeartCodec, 48 kHz).");

        MusicAudio Synth(IBackend backend, MusicRequest req)
        {
            // Genre/style tag (if any) is folded ahead of the lyrics as a structural marker.
            string text = string.IsNullOrWhiteSpace(req.Genre) ? req.Prompt : $"[{req.Genre}]\n{req.Prompt}";
            int[] lyrics = [.. tokenizer.EncodeRaw(text ?? "")];
            int maxFrames = (int)(Math.Clamp(req.Duration, 1d, 300d) * 12.5); // HeartCodec frame rate = 12.5 Hz
            float[] samples = pipeline.Generate(backend, lyrics, maxFrames, seed: req.Seed);
            return MusicAudio.Mono(samples);
        }

        IDisposable[] keep = [pipeline, .. lmLoaders, .. codecLoaders];
        return new MusicRunner(pipeline.SampleRate, Synth, keep);
    }

    #endregion
}
