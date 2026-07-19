using System.IO;
using SwarmUI.Utils;
using Hartsy.Extensions.AudioLab.AudioProviders;
using Hartsy.Extensions.AudioLab.AudioProviderTypes;
using Hartsy.Extensions.AudioLab.AudioServices.Tts;
using HartsyInference.Core.Backends;
using HartsyInference.Core.Tensors;
using HartsyInference.Audio.Cache;
using HartsyInference.Audio.Frontends;
using HartsyInference.Audio.Models.Codecs.EnCodec;
using HartsyInference.Audio.Models.Codecs.Oobleck;
using HartsyInference.Audio.Models.Codecs.Vocos;
using HartsyInference.Audio.Models.Codecs.XCodec;
using HartsyInference.Audio.Models.Csm;
using HartsyInference.Audio.Models.HeartMula;
using HartsyInference.Audio.Models.Music;
using HartsyInference.Audio.Pipelines;
using HartsyInference.Diffusion.Models.Denoisers;
using HartsyInference.Diffusion.Models.Music;
using HartsyInference.Diffusion.Models.TextEncoders;
using HartsyInference.Diffusion.Pipelines;
using HartsyInference.Diffusion.Utilities;
using HartsyInference.ModelHandler.CheckpointConverters;
using HartsyInference.ModelHandler.Gguf;
using HartsyInference.ModelHandler.PyTorch;
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
            // AudioGen conditions on t5-LARGE (enc_to_dec_proj input = 1024); MusicGen-large uses t5-base.
            string t5Repo = audioGen ? "google-t5/t5-large" : "google-t5/t5-base";
            string t5Path = await AudioModelCache.GetAsync(t5Repo, "pytorch_model.bin", ct: ct).ConfigureAwait(false);
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
        // AudioGen conditions on t5-large (dim 1024); MusicGen on t5-base. Built inline so this compiles against
        // the currently-pinned engine nuget (T5Large preset lands in the next engine release).
        T5TextEncoderConfig t5Cfg = audioGen
            ? new T5TextEncoderConfig { DModel = 1024, DFf = 4096, DKv = 64, NumHeads = 16, NumLayers = 24, VocabSize = 32128, GatedFeedForward = false, UseReluActivation = true, AttentionScale = 1.0f }
            : T5TextEncoderConfig.T5Base;
        T5TextEncoder t5 = new(t5Cfg);
        t5.LoadWeights(t5W);
        T5Tokenizer tokenizer = new(maxLength: T5MaxTokens);

        MusicGenPipeline pipeline = new(config, decoder, codec);
        Logs.Info($"[AudioLab][MusicGen] Loaded {repo} ({config.CodecSampleRate} Hz).");

        MusicAudio Synth(IBackend backend, MusicRequest req, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            // Real-length ids + </s>, NO padding: the MusicGen decoder cross-attention has no encoder mask, so padded
            // T5 rows would swamp the text conditioning. HF appends EOS and masks pad — real-length+EOS is equivalent.
            IReadOnlyList<int> rawIds = tokenizer.EncodeRaw(req.Prompt);
            int[] tokens = new int[rawIds.Count + 1];
            for (int i = 0; i < rawIds.Count; i++) tokens[i] = rawIds[i];
            tokens[rawIds.Count] = T5Tokenizer.EosTokenId;
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
        LoadAsync = (backend, providerId, modelId, ct) => LoadAceStepAsync(backend, providerId, modelId, ct),
    };

    private static async Task<IMusicRunner> LoadAceStepAsync(IBackend backend, string providerId, string modelId, CancellationToken ct)
    {
        // A user-placed/registry checkpoint in the model folder wins. If the variant's file is missing but
        // registered, self-heal by downloading THAT variant's file set (weights + config + silence latent);
        // only unregistered ids fall back to the legacy Comfy-Org turbo DiT.
        string localPath = ResolveLocalCheckpoint(providerId, modelId);
        // Migration: the legacy Comfy-Org filename is byte-identical to the official bundle turbo
        // (same upstream LFS sha256) — rename instead of re-downloading 4.8GB.
        if (!File.Exists(localPath) && (modelId ?? "").Trim().Equals("turbo", StringComparison.OrdinalIgnoreCase))
        {
            string legacy = Path.Combine(Path.GetDirectoryName(localPath)!, "acestep_v1.5_turbo.safetensors");
            if (File.Exists(legacy))
            {
                Logs.Info($"[AudioLab][ACE-Step] Migrating legacy turbo checkpoint filename → '{Path.GetFileName(localPath)}'.");
                File.Move(legacy, localPath);
            }
        }
        string mainPath;
        if (File.Exists(localPath))
        {
            mainPath = localPath;
        }
        else if (AudioWeightsRegistry.SpecsFor(providerId, (modelId ?? "").Trim()).Length > 0)
        {
            AudioProviderDefinition provider = AudioProviderRegistry.GetById(providerId);
            Logs.Info($"[AudioLab][ACE-Step] '{modelId}' weights missing — downloading this variant's file set.");
            await AudioWeights.EnsureProviderWeightsAsync(provider, msg => Logs.Info($"[AudioLab][ACE-Step] {msg}"), ct, (modelId ?? "").Trim()).ConfigureAwait(false);
            mainPath = localPath;
        }
        else
        {
            mainPath = await AudioModelCache.GetAsync(AceStep15Repo, AceStep15TurboFile, ct: ct).ConfigureAwait(false);
        }
        string vaePath = await AudioModelCache.GetAsync(AceStep15Repo, AceStep15VaeFile, ct: ct).ConfigureAwait(false);
        string qwenPath = await AudioModelCache.GetAsync(QwenEmbeddingRepo, QwenEmbeddingFile, ct: ct).ConfigureAwait(false);

        // Sidecar config.json (downloaded with the variant) drives dims + is_turbo; absent = 2B turbo defaults.
        string sidecarConfig = Path.ChangeExtension(mainPath, null) + ".config.json";
        AceStep15Config config = File.Exists(sidecarConfig)
            ? AceStep15Config.FromJson(await File.ReadAllTextAsync(sidecarConfig, ct).ConfigureAwait(false))
            : new();
        // Keep bf16 residency (upstream's inference dtype; ~half the host RAM + PCIe streaming — XL would
        // not fit as F32). Host-read tensors are EnsureF32'd selectively inside the models.
        (Dictionary<string, Tensor> weights, SafeTensorsLoader mainLoader) = AceStepCheckpointConverter.LoadModel15(mainPath, castToF32: false);
        AceStep15Dit dit = new(config);
        dit.LoadWeights(weights);
        // The condition side runs at encoder width (XL: 2048 under a 2560 decoder; identity for 2B).
        AceStep15ConditionEncoder conditionEncoder = new(config.EncoderVariant());
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
        LoadAceStepSilenceLatent(pipeline, Path.GetDirectoryName(mainPath));
        // 5 Hz code detokenizer (LM-planner hints → 25 Hz latents); weights ride in the same checkpoint.
        AceStep15AudioDetokenizer detokenizer = new(config);
        detokenizer.LoadWeights(weights);
        AceStepPlannerHolder plannerHolder = new();
        // The learned CFG uncond row ships in every checkpoint; base/sft need it (turbo keeps CFG off).
        if (weights.TryGetValue("null_condition_emb", out Tensor nullEmb))
        {
            Tensor copy = new(nullEmb.Shape, DType.F32);
            unsafe
            {
                Tensor f32 = nullEmb.DType == DType.F32 ? nullEmb : nullEmb.CastTo(DType.F32);
                Buffer.MemoryCopy((void*)f32.DataPointer, (void*)copy.DataPointer, f32.Shape.ElementCount * 4, f32.Shape.ElementCount * 4);
                if (!ReferenceEquals(f32, nullEmb)) f32.Dispose();
            }
            pipeline.SetNullConditionEmb(copy);
        }
        (int defaultSteps, float defaultShift) = AceStepVariantDefaults(modelId);
        Logs.Info($"[AudioLab][ACE-Step] Loaded 1.5 '{modelId}' ({(config.IsTurbo ? "turbo" : "CFG")}, 48 kHz stereo, default {defaultSteps} steps, shift {defaultShift}).");

        // <|endoftext|> is a special token: tokenize the surrounding text separately (via the HF-exact Qwen BPE)
        // and splice the id in. The Qwen3-Embedding tokenizer's post-processor then appends one more
        // <|endoftext|> to every sequence — replicate that terminal EOS too.
        int[] TokenizeWithEos(string body, string tail = "")
        {
            int[] raw = AudioTextFrontend.Qwen3Ids(body ?? "");
            int[] tailIds = tail.Length == 0 ? [] : AudioTextFrontend.Qwen3Ids(tail);
            int[] tokens = new int[raw.Length + 1 + tailIds.Length + 1];
            raw.CopyTo(tokens, 0);
            tokens[raw.Length] = QwenEosId;
            tailIds.CopyTo(tokens, raw.Length + 1);
            tokens[^1] = QwenEosId;
            return tokens;
        }

        // Text/caption branch: full Qwen3-Embedding forward → last_hidden_state (upstream infer_text_embeddings).
        Tensor EncodeQwenText(IBackend b, int[] tokens)
        {
            Tensor batchT = qwen.Encode(b, [tokens]);
            Tensor sliced = CfgHelper.SliceBatchElement(batchT, 0, tokens.Length, config.TextHiddenDim);
            batchT.Dispose();
            return sliced;
        }

        // Lyric branch: embedding-table lookup ONLY — upstream infer_lyric_embeddings uses
        // text_encoder.embed_tokens, NOT a transformer forward.
        Tensor EmbedQwenLyrics(int[] tokens)
        {
            Tensor batchT = qwen.LookupEmbeddings(tokens);
            Tensor sliced = CfgHelper.SliceBatchElement(batchT, 0, tokens.Length, config.TextHiddenDim);
            batchT.Dispose();
            return sliced;
        }

        MusicAudio Synth(IBackend b, MusicRequest req, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            double duration = Math.Clamp(req.Duration, 1d, 600d);
            // Upstream SFT_GEN_PROMPT: "# Instruction\n{instr}\n\n# Caption\n{caption}\n\n# Metas\n{metas}<|endoftext|>\n"
            // with the text2music instruction and a 4-line metas block. The DiT is trained on THIS format —
            // a bare style string is out-of-distribution.
            string caption = string.IsNullOrWhiteSpace(req.Genre) ? "pop" : req.Genre;
            string metas =
                $"- bpm: {(req.Bpm?.ToString() ?? "N/A")}\n" +
                $"- timesignature: {(string.IsNullOrWhiteSpace(req.TimeSignature) ? "N/A" : req.TimeSignature)}\n" +
                $"- keyscale: {(string.IsNullOrWhiteSpace(req.KeyScale) ? "N/A" : req.KeyScale)}\n" +
                $"- duration: {(int)duration} seconds\n";
            string textPrompt = $"# Instruction\nFill the audio semantic mask based on the given conditions:\n\n# Caption\n{caption}\n\n# Metas\n{metas}";
            Tensor textHidden = EncodeQwenText(b, TokenizeWithEos(textPrompt, tail: "\n"));
            // Upstream _format_lyrics: "# Languages\n{lang}\n\n# Lyric\n{lyrics}<|endoftext|>" ("[Instrumental]"
            // is the upstream convention for no vocals and goes through the same template).
            string language = string.IsNullOrWhiteSpace(req.VocalLanguage) ? "en" : req.VocalLanguage;
            string lyrics = string.IsNullOrWhiteSpace(req.Prompt) ? "[Instrumental]" : req.Prompt;
            Tensor lyricHidden = EmbedQwenLyrics(TokenizeWithEos($"# Languages\n{language}\n\n# Lyric\n{lyrics}"));
            b.Sync();
            b.FreeWeights(qwen.EnumerateWeights());
            try
            {
                // Optional 5 Hz LM planner: caption+lyrics → FSQ codes → detokenized 25 Hz lmHints.
                Tensor lmHints = null;
                bool lmThink = false;
                string lmKind = (req.LmModel ?? "").Trim().ToLowerInvariant();
                if (lmKind is not ("" or "none" or "disabled"))
                {
                    AceStepLmPlanner planner = plannerHolder.GetOrLoad(lmKind);
                    lmThink = req.Thinking;
                    (int[] codes, string _) = planner.Plan(b, caption, lyrics, duration, new AceStepPlannerOptions
                    {
                        Thinking = req.Thinking,
                        Temperature = (float)req.LmTemperature,
                        CfgScale = (float)req.LmCfgScale,
                        TopK = req.LmTopK,
                        TopP = (float)req.LmTopP,
                        NegativePrompt = req.LmNegativePrompt ?? "",
                        Seed = req.Seed,
                    }, ct);
                    b.Sync();
                    b.FreeWeights(planner.EnumerateWeights());
                    Tensor raw = detokenizer.Decode(b, codes);   // [1, codes·5, 64]
                    lmHints = FitHints(raw, config.FrameCount(duration), config.LatentChannels);
                }
                AceStep15GenerateOptions opts = new()
                {
                    Shift = req.Shift.HasValue ? (float)req.Shift.Value : defaultShift,
                    Seed = req.Seed,
                    InferSteps = req.InferSteps ?? defaultSteps,
                    // The pipeline clamps guidance to 1.0 on turbo configs itself (upstream behavior).
                    GuidanceScale = req.CfgScale.HasValue ? (float)req.CfgScale.Value : 7f,
                    UseAdg = req.UseAdg,
                    CfgIntervalStart = (float)req.CfgIntervalStart,
                    CfgIntervalEnd = (float)req.CfgIntervalEnd,
                    InferMethod = string.IsNullOrWhiteSpace(req.InferMethod) ? "ode" : req.InferMethod,
                    // Upstream DCW scalers flip with LM-think state (dcw_defaults.py).
                    DcwScaler = lmHints is not null && lmThink ? 0.02f : 0.05f,
                    DcwHighScaler = lmHints is not null && lmThink ? 0.06f : 0.02f,
                };
                try
                {
                    (float[] left, float[] right, int _, int _) = pipeline.Generate(textHidden, lyricHidden, duration, opts, lmHints: lmHints);
                    return MusicAudio.Stereo(left, right);
                }
                finally
                {
                    lmHints?.Dispose();
                }
            }
            finally
            {
                textHidden.Dispose();
                lyricHidden?.Dispose();
            }
        }

        return new MusicRunner(48_000, Synth, pipeline as IDisposable, detokenizer, plannerHolder, qwen, tokenizer, mainLoader, vaeLoader, qwenLoader);
    }

    /// <summary>Lazily loads/caches the 5 Hz LM planner ("0.6b"/"4b" — anything containing "4b" picks the big
    /// one) from the official HF repos via the shared model cache. Disposed with the runner.</summary>
    private sealed class AceStepPlannerHolder : IDisposable
    {
        private AceStepLmPlanner _planner;
        private string _kind = "";
        private readonly List<IDisposable> _loaders = [];

        public AceStepLmPlanner GetOrLoad(string kind)
        {
            string want = kind.Contains("4b") ? "4b" : "0.6b";
            if (_planner is not null && _kind == want)
            {
                return _planner;
            }
            _planner?.Dispose();
            string repo = want == "4b" ? "ACE-Step/acestep-5Hz-lm-4B" : "ACE-Step/acestep-5Hz-lm-0.6B";
            Logs.Info($"[AudioLab][ACE-Step] Loading 5Hz LM planner '{repo}'...");
            (IReadOnlyDictionary<string, Tensor> w, IDisposable[] loaders) =
                TtsModels.LoadCheckpointAsync(repo, CancellationToken.None).ConfigureAwait(false).GetAwaiter().GetResult();
            _loaders.AddRange(loaders);
            _planner = new AceStepLmPlanner(want == "4b" ? AceStepLmPlanner.Config4B : AceStepLmPlanner.Config0_6B, w);
            _kind = want;
            return _planner;
        }

        public void Dispose()
        {
            _planner?.Dispose();
            foreach (IDisposable l in _loaders) l.Dispose();
        }
    }

    /// <summary>Crops/pads detokenized hints <c>[1, T, 64]</c> to the pipeline's exact frame count
    /// (upstream crops to src length; short hints repeat the final frame).</summary>
    private static Tensor FitHints(Tensor raw, int frames, int latCh)
    {
        int t = (int)raw.Shape[1];
        if (t == frames)
        {
            return raw;
        }
        Tensor outT = new(new TensorShape(1, frames, latCh), DType.F32);
        unsafe
        {
            float* sp = (float*)raw.DataPointer;
            float* dp = (float*)outT.DataPointer;
            for (int i = 0; i < frames; i++)
            {
                long src = (long)Math.Min(i, t - 1) * latCh;
                Buffer.MemoryCopy(sp + src, dp + (long)i * latCh, latCh * 4, latCh * 4);
            }
        }
        raw.Dispose();
        return outT;
    }

    /// <summary>Per-variant inference defaults, mirroring upstream <c>get_ui_control_config</c>:
    /// turbo family 8 steps (shift 3 except the shift-1 checkpoint); sft 50 / base 32 steps at shift 1.</summary>
    private static (int Steps, float Shift) AceStepVariantDefaults(string modelId)
    {
        string v = (modelId ?? "").Trim().ToLowerInvariant();
        if (v.Contains("shift1")) return (8, 1f);
        if (v == "sft" || v.EndsWith("-sft")) return (50, 1f);
        if (v == "base" || v.EndsWith("-base")) return (32, 1f);
        return (8, 3f);   // turbo / turbo-shift3 / turbo-continuous / xl-turbo
    }

    /// <summary>Loads the shipped <c>silence_latent.pt</c> (fp32 [1, 64, 15000]) from the weights dir into the
    /// pipeline's src-latent slot — transposed to per-frame rows [T, 64], matching upstream's
    /// <c>torch.load(...).transpose(1, 2)</c>. Absent file = keep the VAE-recompute fallback.</summary>
    private static void LoadAceStepSilenceLatent(AceStepPipeline15 pipeline, string weightsDir)
    {
        try
        {
            string path = Path.Combine(weightsDir ?? "", "acestep-v15-silence_latent.pt");
            if (!File.Exists(path))
            {
                return;
            }
            using PytorchPickleLoader loader = new();
            loader.Load(path);
            Tensor raw = loader.GetAllTensors().Values.FirstOrDefault()
                ?? throw new InvalidDataException("silence_latent.pt contained no tensor.");
            Tensor f32 = raw.DType == DType.F32 ? raw : raw.CastTo(DType.F32);
            // [1, 64, T] → rows [T, 64].
            int ch = (int)f32.Shape[1];
            int t = (int)f32.Shape[2];
            Tensor rows = new(new TensorShape(t, ch), DType.F32);
            unsafe
            {
                float* sp = (float*)f32.DataPointer;
                float* dp = (float*)rows.DataPointer;
                for (int c = 0; c < ch; c++)
                    for (int i = 0; i < t; i++)
                        dp[(long)i * ch + c] = sp[(long)c * t + i];
            }
            if (!ReferenceEquals(f32, raw)) f32.Dispose();
            pipeline.SetSilenceLatent(rows);
            Logs.Info($"[AudioLab][ACE-Step] Loaded shipped silence latent ({t} frames).");
        }
        catch (Exception ex)
        {
            Logs.Warning($"[AudioLab][ACE-Step] Could not load silence_latent.pt ({ex.Message}) — using VAE recompute.");
        }
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
        // Folder-checkpoint providers (YuE) load a whole variant DIRECTORY (sharded weights + sidecars); the
        // registry's per-variant download set lands in "{variant}/…", so resolve to that folder.
        if (AudioWeightsRegistry.IsFolderCheckpoint(providerId))
        {
            return string.IsNullOrEmpty(variant) ? baseDir : Path.Combine(baseDir, variant);
        }
        // Single-file providers (ACE-Step) register the variant→filename map; map to the real .safetensors.
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
        string xcodecPath = EnsureYueXCodec(folder)
            ?? throw new InvalidOperationException($"YuE needs 'xcodec.safetensors' (or the X-Codec 'ckpt_00360000.pth' from m-a-p/xcodec_mini_infer, which the installer downloads and this loader auto-converts) in or beside '{folder}'.");

        YueConfig config = YueConfig.V1;
        YueTokenizer tokenizer = new(tokenizerPath);
        (Dictionary<string, Tensor> s1W, IDisposable s1Loader) = YueCheckpointConverter.LoadStage1(folder, castToF32: false);   // 7B: bf16 source
        YueCheckpointConverter.QuantizeLmWeights(s1W, DType.Q4_K);   // 14 GB bf16 → ~3.5 GB Q4_K: fits resident + fast dp4a GEMV decode
        YueStage1Lm stage1 = new(config);
        stage1.LoadWeights(s1W, prefix: "model");

        (Dictionary<string, Tensor> cW, var cLoader) = YueCheckpointConverter.LoadXCodec(xcodecPath, castToF32: true);
        XCodec xcodec = new(XCodecConfig.XCodec16kHz);
        xcodec.LoadWeights(cW);

        // Stage-2 residual upsampler (predicts codebooks 1-7 from Stage-1's cb0) — optional sibling 's2'/'stage2' folder.
        string? s2Folder = FindSiblingFolder(folder, "s2") ?? FindSiblingFolder(folder, "stage2");
        YueStage2Lm? stage2 = null;
        IDisposable? s2Loader = null;
        if (s2Folder is not null)
        {
            (Dictionary<string, Tensor> s2W, IDisposable ld) = YueCheckpointConverter.LoadStage2(s2Folder, castToF32: false);   // bf16 source
            YueCheckpointConverter.QuantizeLmWeights(s2W, DType.Q4_K);   // Q4_K: 1B fully resident + fast dp4a GEMV for the 8×/frame decode
            s2Loader = ld;
            stage2 = new YueStage2Lm(config, tokenizer.Soa, tokenizer.Stage1, tokenizer.Stage2);
            stage2.LoadWeights(s2W, prefix: "model");
        }
        // Per-stem Vocos vocoders — YuE's real 44.1 kHz output (converted decoders/decoder_131000.pth = vocal,
        // decoder_151000.pth = instrumental). When present, decode goes latent→Vocos→44.1 kHz instead of the 16 kHz
        // x-codec draft (which buries/roughens the vocals). Optional: falls back to the draft if not placed.
        string? vocalVocPath = FindSibling(folder, "vocal_vocoder.safetensors");
        string? instVocPath = FindSibling(folder, "inst_vocoder.safetensors");
        VocosDecoder? vocalVoc = null, instVoc = null;
        IDisposable? vVocLoader = null, iVocLoader = null;
        if (vocalVocPath is not null && instVocPath is not null)
        {
            (Dictionary<string, Tensor> vW, SafeTensorsLoader vL) = YueCheckpointConverter.LoadVocoder(vocalVocPath);
            vVocLoader = vL; vocalVoc = new VocosDecoder(); vocalVoc.LoadWeights(vW);
            (Dictionary<string, Tensor> iW, SafeTensorsLoader iL) = YueCheckpointConverter.LoadVocoder(instVocPath);
            iVocLoader = iL; instVoc = new VocosDecoder(); instVoc.LoadWeights(iW);
        }

        YuePipeline pipeline = new(config, stage1, xcodec, stage2, vocalVoc, instVoc);
        Logs.Info($"[AudioLab][YuE] Loaded Stage-1{(stage2 is not null ? " + Stage-2 (full 8-codebook)" : " (vocal-cb0 only — no s2 folder)")}"
            + $"{(vocalVoc is not null ? " + Vocos vocoders (44.1 kHz)" : " (16 kHz x-codec draft — no vocoders)")} from '{folder}'.");

        MusicAudio Synth(IBackend backend, MusicRequest req, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            string genre = string.IsNullOrWhiteSpace(req.Genre) ? "pop" : req.Genre;
            // Segment-by-segment Stage-1 (infer.py): head prompt + one prompt per [label] section, each injecting
            // its own lyrics. Fall back to a single unstructured segment when the lyrics carry no [label] markers.
            int[] headIds = tokenizer.EncodeStage1Head(genre, req.Prompt);
            IReadOnlyList<string> segments = tokenizer.Stage1Segments(req.Prompt);
            List<int[]> segmentPrompts = new(Math.Max(segments.Count, 1));
            for (int i = 0; i < segments.Count; i++)
                segmentPrompts.Add(tokenizer.EncodeSegmentPrompt(segments[i], isFirst: i == 0));
            if (segmentPrompts.Count == 0)
                segmentPrompts.Add(tokenizer.EncodeSegmentPrompt(req.Prompt ?? "", isFirst: true));
            int maxFrames = (int)(Math.Clamp(req.Duration, 5d, 300d) * config.FrameRateHz);
            int perSeg = Math.Max(config.FrameRateHz, maxFrames / segmentPrompts.Count);   // split the budget across segments
            return MusicAudio.Mono(pipeline.Synthesize(backend, headIds, segmentPrompts, maxFramesPerSegment: perSeg, seed: req.Seed));
        }

        List<IDisposable> disposables = [pipeline, s1Loader, cLoader, tokenizer];
        if (s2Loader is not null) disposables.Add(s2Loader);
        if (vVocLoader is not null) disposables.Add(vVocLoader);
        if (iVocLoader is not null) disposables.Add(iVocLoader);
        // pipeline.OutputSampleRate = 44.1 kHz with vocoders, else the 16 kHz x-codec draft.
        return Task.FromResult<IMusicRunner>(new MusicRunner(pipeline.OutputSampleRate, Synth, [.. disposables]));
    }

    /// <summary>Finds a file inside the checkpoint folder, then one directory up (so variants can share one copy).</summary>
    /// <summary>Ensures a loadable <c>xcodec.safetensors</c> exists for YuE, converting the downloaded X-Codec
    /// torch checkpoint (<c>m-a-p/xcodec_mini_infer/final_ckpt/ckpt_00360000.pth</c>) on first use. The .pth is a
    /// pickled <c>{codec_model: state_dict}</c>; we keep only the tensors the engine's X-Codec loader actually
    /// maps (its <see cref="YueCheckpointConverter.MapXCodecKey"/> drops the encoder/discriminator/semantic-train
    /// branches) and write them with their original keys, so the normal <c>LoadXCodec</c> path re-maps them
    /// identically. Returns the safetensors path, or null if neither the converted file nor the .pth is present.</summary>
    private static string EnsureYueXCodec(string folder)
    {
        string existing = FindSibling(folder, "xcodec.safetensors");
        if (existing is not null)
        {
            return existing;
        }
        string parent = Directory.GetParent(folder)?.FullName ?? folder;
        string[] candidates =
        [
            Path.Combine(parent, "xcodec", "ckpt_00360000.pth"),
            Path.Combine(folder, "xcodec", "ckpt_00360000.pth"),
            Path.Combine(parent, "ckpt_00360000.pth"),
            Path.Combine(folder, "ckpt_00360000.pth"),
        ];
        string pth = candidates.FirstOrDefault(File.Exists);
        if (pth is null)
        {
            return null;
        }
        string outPath = Path.Combine(parent, "xcodec.safetensors");
        Logs.Info($"[AudioLab][YuE] Converting X-Codec '{Path.GetFileName(pth)}' → xcodec.safetensors (one-time)...");
        using PytorchPickleLoader loader = new();
        loader.Load(pth, recursiveFlatten: true);
        Dictionary<string, Tensor> raw = loader.GetAllTensors();
        Dictionary<string, Tensor> keep = new(StringComparer.Ordinal);
        foreach ((string key, Tensor tensor) in raw)
        {
            if (YueCheckpointConverter.MapXCodecKey(key) is not null)
            {
                keep[key] = tensor;
            }
        }
        if (keep.Count == 0)
        {
            throw new InvalidDataException($"X-Codec checkpoint '{pth}' yielded no usable acoustic tensors — unexpected key layout.");
        }
        SafeTensorsWriter.Save(outPath, keep);
        Logs.Info($"[AudioLab][YuE] Wrote {keep.Count} X-Codec acoustic tensors to '{outPath}'.");
        return outPath;
    }

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

    /// <summary>Directory analog of <see cref="FindSibling"/>: a subfolder of the checkpoint, then one level up.</summary>
    private static string FindSiblingFolder(string folder, string name)
    {
        string inside = Path.Combine(folder, name);
        if (Directory.Exists(inside)) return inside;
        string parent = Path.Combine(Directory.GetParent(folder)?.FullName ?? folder, name);
        return Directory.Exists(parent) ? parent : null;
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
        // Cache key includes the quant so bf16/Q8/Q4 variants of the same repo cache as SEPARATE runners — otherwise
        // switching precision would return the stale cached runner.
        CacheKey = (_, modelId) => $"{ResolveHeartMulaRepo(modelId)}|{ResolveHeartMulaQuant(modelId) ?? "bf16"}",
        LoadAsync = (_, _, modelId, ct) => LoadHeartMulaAsync(ResolveHeartMulaRepo(modelId), ResolveHeartMulaQuant(modelId), ct),
    };

    private static string ResolveHeartMulaRepo(string modelId)
    {
        string id = (modelId ?? "").Trim();
        if (id.Contains('/'))
        {
            return id;
        }
        string lower = id.ToLowerInvariant();
        // The `-q8`/`-q4` precision suffix (if any) doesn't affect repo selection — these contains-checks ignore it.
        if (lower.Contains("rl")) return HeartMulaRlRepo;                      // 3b-rl[-q8|-q4]
        if (lower.Contains("hny") || lower.Contains("happy")) return HeartMulaHnyRepo; // 3b-hny[-q8|-q4]
        return HeartMulaRepo;                                                  // 3b-base[-q8|-q4]
    }

    /// <summary>Maps a HeartLib model id's precision suffix to a quant mode: <c>-q8</c> → <c>q8_0</c>,
    /// <c>-q4</c> → <c>q4_k</c>, otherwise null (full bf16). Q8/Q4 select a disk-cached quantized weight set that is
    /// ~1.4×/more faster + smaller; the conversion runs once on first generation (see <see cref="CsmWeightCache"/>).</summary>
    private static string ResolveHeartMulaQuant(string modelId)
    {
        string lower = (modelId ?? "").Trim().ToLowerInvariant();
        if (lower.EndsWith("-q8") || lower.EndsWith("-q8_0")) return "q8_0";
        if (lower.EndsWith("-q4") || lower.EndsWith("-q4_k")) return "q4_k";
        return null;   // bf16 (full precision)
    }

    private static async Task<IMusicRunner> LoadHeartMulaAsync(string repo, string? quant, CancellationToken ct)
    {
        // LM = sharded safetensors (no tokenizer files); codec = a separate flow-matching decoder checkpoint.
        (IReadOnlyDictionary<string, Tensor> lmW, IDisposable[] lmLoaders) = await TtsModels.LoadCheckpointAsync(repo, ct).ConfigureAwait(false);
        (IReadOnlyDictionary<string, Tensor> codecW, IDisposable[] codecLoaders) = await TtsModels.LoadCheckpointAsync(HeartCodecRepo, ct).ConfigureAwait(false);

        // The AR decode is memory-bandwidth-bound (per-token weight streaming), so the Q8/Q4 model variants
        // (`quant`, chosen at install via the `-q8`/`-q4` model id) are ~1.4×/more faster AND smaller. The pipeline
        // quantizes the REMAPPED weights ONCE to a disk GGUF cache (streaming, no OOM) and loads from there
        // thereafter; a null/unrecognized `quant` → the plain bf16 path.
        bool useQuant = CsmWeightCache.ResolveQuant(quant) is not null;

        HeartMulaConfig config = HeartMulaConfig.Oss3B;
        HeartMulaPipeline pipeline = new(config);
        if (useQuant)
        {
            string quantCache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".cache", "hartsyinference", "heartmula", $"{repo.Replace('/', '_')}-{quant!.ToLowerInvariant()}.gguf");
            pipeline.LoadWeights(lmW, quant, quantCache);   // raw → remap → quantize (post-remap), disk-cached
            // The GGUF cache owns the weights now — drop the source safetensors mmaps.
            foreach (IDisposable l in lmLoaders) l.Dispose();
            lmLoaders = System.Array.Empty<IDisposable>();
        }
        else
        {
            // Upstream inference dtypes: LM bf16 (halves the per-frame weight streaming too), codec f32.
            Dictionary<string, Tensor> lmBf = new(lmW.Count);
            foreach ((string k, Tensor t) in lmW) lmBf[k] = t.DType == DType.F32 ? t.CastTo(DType.BF16) : t;
            pipeline.LoadWeights(lmBf);
        }
        pipeline.LoadCodecWeights(codecW);

        Logs.Info($"[AudioLab][HeartMuLa] Loaded {repo} + {HeartCodecRepo} (CSM-shaped LM{(useQuant ? $", {quant} quantized" : " bf16")} + HeartCodec, 48 kHz).");

        MusicAudio Synth(IBackend backend, MusicRequest req, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            // Upstream prompt layout: [<tag>genre</tag>, MuQ row (pipeline-injected), lyrics] — Llama-3 BPE,
            // each text section lowercased + BOS/EOS-wrapped (AudioTextFrontend reproduces upstream preprocess).
            int[] tags = AudioTextFrontend.HeartMulaTags(req.Genre ?? "");
            int[] lyrics = AudioTextFrontend.HeartMulaLyrics(req.Prompt ?? "");
            int maxFrames = (int)(Math.Clamp(req.Duration, 1d, 300d) * 12.5); // HeartCodec frame rate = 12.5 Hz
            // Per-frame progress + cancellation: the AR decode is the long pole. Log the first few frames then every
            // 5, so the log visibly ticks (silence here reads as "stuck" even when it's just slow).
            void OnFrame(int done, int total)
            {
                if (done <= 3 || done % 5 == 0 || done == total)
                {
                    Logs.Info($"[AudioLab][HeartMuLa] Frame {done}/{total} ({done / 12.5:0.0}s of audio).");
                }
            }
            float[] samples = pipeline.Generate(backend, lyrics, maxFrames, seed: req.Seed,
                temperature: req.Temperature.HasValue ? (float)req.Temperature.Value : null,
                topK: req.TopK,
                cfgScale: req.CfgScale.HasValue ? (float)req.CfgScale.Value : null,
                cancel: ct, onFrame: OnFrame, tagsTokens: tags);
            return MusicAudio.Mono(samples);
        }

        IDisposable[] keep = [pipeline, .. lmLoaders, .. codecLoaders];
        return new MusicRunner(pipeline.SampleRate, Synth, keep);
    }

    #endregion

    #region Stable Audio Open Small (HF auto-download)

    private const string StableAudioRepo = "FastVideo/stable-audio-open-small-Diffusers";
    private const string T5BaseRepo = "google-t5/t5-base";

    /// <summary>Stable Audio Open Small (Stability AI Community License) — the 341M rectified-flow DiT over
    /// Oobleck-VAE latents, 8-step pingpong sampling, no CFG (ARC-distilled). Text prompt only (no lyrics/genre
    /// split — <c>req.Prompt</c> feeds T5-base directly); duration up to ~11.89 s (the model's fixed trained
    /// window — longer requests are clamped and logged by the pipeline). DiT/VAE/timing-conditioner are
    /// individually real-weight-verified bit-exact against independent Python references (cosine 1.0 each; see
    /// HartsyInference's PARITY_VERIFICATION.md).</summary>
    public static readonly MusicModelDescriptor StableAudio = new()
    {
        ManagesOwnWeights = true,
        CacheKey = (_, _) => StableAudioRepo,
        LoadAsync = (backend, _, _, ct) => LoadStableAudioAsync(backend, ct),
    };

    private static async Task<IMusicRunner> LoadStableAudioAsync(IBackend backend, CancellationToken ct)
    {
        string ditPath = await AudioModelCache.GetAsync(StableAudioRepo, "transformer/diffusion_pytorch_model.safetensors", ct: ct).ConfigureAwait(false);
        string vaePath = await AudioModelCache.GetAsync(StableAudioRepo, "vae/diffusion_pytorch_model.safetensors", ct: ct).ConfigureAwait(false);
        string condPath = await AudioModelCache.GetAsync(StableAudioRepo, "conditioner/diffusion_pytorch_model.safetensors", ct: ct).ConfigureAwait(false);
        string t5Path = await AudioModelCache.GetAsync(T5BaseRepo, "model.safetensors", ct: ct).ConfigureAwait(false);

        StableAudioDitConfig config = StableAudioDitConfig.OpenSmall;

        SafeTensorsLoader ditLoader = new();
        ditLoader.Load(ditPath);
        StableAudioDit dit = new(config);
        dit.LoadWeights(ditLoader.GetAllTensors());

        SafeTensorsLoader vaeLoader = new();
        vaeLoader.Load(vaePath);
        OobleckConfig vaeConfig = OobleckConfig.StableAudioOpen;
        // The real checkpoint uses diffusers' NAMED-submodule key layout, not the flat nn.Sequential layout
        // OobleckVae.LoadWeights expects (the ACE-Step 1.5 dialect) — remap first (pure key rename).
        Dictionary<string, Tensor> vaeWeights = OobleckKeyRemap.ToFlatSequentialLayout(vaeLoader.GetAllTensors(), vaeConfig);
        OobleckVae vae = new(vaeConfig);
        vae.LoadWeights(vaeWeights);

        SafeTensorsLoader condLoader = new();
        condLoader.Load(condPath);
        StableAudioNumberEmbedder timing = new(minVal: 0f, maxVal: (float)config.TimingMaxSeconds);
        timing.LoadWeights(condLoader.GetAllTensors(), "conditioners.seconds_total");

        SafeTensorsLoader t5Loader = new();
        t5Loader.Load(t5Path);
        T5TextEncoder t5 = new(T5TextEncoderConfig.T5Base);
        t5.LoadWeights(t5Loader.GetAllTensors());
        T5Tokenizer tokenizer = new(maxLength: 64);

        StableAudioPipeline pipeline = new(backend, dit, vae, timing, config);
        Logs.Info("[AudioLab][StableAudio] Loaded Stable Audio Open Small.");

        MusicAudio Synth(IBackend b, MusicRequest req, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            int[] tokens = tokenizer.Encode(req.Prompt);
            Tensor textEmbeds = t5.Encode(b, [tokens]);
            b.Sync();
            b.FreeWeights(t5.EnumerateWeights());
            try
            {
                (float[] left, float[] right, int rate, int seed) = pipeline.Generate(
                    textEmbeds, secondsTotal: req.Duration, steps: req.InferSteps, seed: req.Seed);
                return MusicAudio.Stereo(left, right);
            }
            finally
            {
                textEmbeds.Dispose();
            }
        }

        return new MusicRunner(config.SampleRate, Synth, pipeline, dit, ditLoader, vaeLoader, condLoader, t5, t5Loader);
    }

    #endregion
}
