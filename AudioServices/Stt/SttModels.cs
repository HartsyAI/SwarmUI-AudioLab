using System.IO;
using Hartsy.Extensions.AudioLab.AudioServices.Tts;
using HartsyInference.Audio.Cache;
using HartsyInference.Audio.Models.Kyutai;
using HartsyInference.Audio.Pipelines;
using HartsyInference.Tokenizers;

namespace Hartsy.Extensions.AudioLab.AudioServices.Stt;

/// <summary>Per-model specifics for the generic <see cref="SttHandler"/>: how to turn an AudioLab model id
/// into a HuggingFace repo, and how to load that repo into an <see cref="ISttRunner"/>.</summary>
public sealed class SttModelDescriptor
{
    /// <summary>Maps an AudioLab model id (the <c>__model_id</c> variant hint) to a HuggingFace repo id.</summary>
    public required Func<string, string> ResolveRepo { get; init; }

    /// <summary>Loads the repo (downloading on first use) into a uniform runner.</summary>
    public required Func<string, CancellationToken, Task<ISttRunner>> LoadAsync { get; init; }

    /// <summary>Sample rate the input audio is decoded to before transcription (Whisper/Moonshine 16 kHz; Kyutai 24 kHz).</summary>
    public int InputSampleRate { get; init; } = 16_000;
}

/// <summary>STT model registry — each entry wires a pipeline to the generic handler.</summary>
public static class SttModels
{
    /// <summary>Maps an <see cref="SttRequest"/> to the engine's Whisper decode options (language + translate task).</summary>
    private static WhisperOptions ToWhisperOptions(SttRequest req)
        => new() { Language = string.IsNullOrEmpty(req?.Language) ? null : req.Language, Translate = req?.Translate ?? false };

    /// <summary>OpenAI Whisper (<see cref="WhisperPipeline"/>). Honors language + translate task.</summary>
    public static readonly SttModelDescriptor Whisper = new()
    {
        ResolveRepo = ResolveWhisperRepo,
        LoadAsync = async (repo, ct) =>
        {
            WhisperPipeline p = await WhisperPipeline.LoadAsync(repo, ct: ct).ConfigureAwait(false);
            return new SttRunner((backend, audio, req) => p.TranscribeAudio(backend, audio, 16_000, ToWhisperOptions(req)), p);
        },
    };

    /// <summary>distil-whisper (same <see cref="WhisperPipeline"/>, but resolves to the distil-whisper/* repos —
    /// the model ids it receives ("large-v3" etc.) don't contain "distil", so it needs its own resolver).</summary>
    public static readonly SttModelDescriptor DistilWhisper = new()
    {
        ResolveRepo = ResolveDistilWhisperRepo,
        LoadAsync = async (repo, ct) =>
        {
            WhisperPipeline p = await WhisperPipeline.LoadAsync(repo, ct: ct).ConfigureAwait(false);
            return new SttRunner((backend, audio, req) => p.TranscribeAudio(backend, audio, 16_000, ToWhisperOptions(req)), p);
        },
    };

    /// <summary>Kyutai delayed-streams STT (kyutai/stt-2.6b-en). Helium LM + Mimi codec → text token ids,
    /// decoded by the SentencePiece <see cref="KyutaiSttTokenizer"/>. Input audio is 24 kHz.</summary>
    public static readonly SttModelDescriptor Kyutai = new()
    {
        InputSampleRate = 24_000,
        // The engine loader expects the HF Transformers ("-trfs") key layout (model.embed_tokens.embed_tokens.weight).
        ResolveRepo = modelId =>
        {
            string id = (modelId ?? "").Trim();
            if (id.Contains('/'))
            {
                return id;
            }
            return id.ToLowerInvariant().Contains("1b") ? "kyutai/stt-1b-en_fr-trfs" : "kyutai/stt-2.6b-en-trfs";
        },
        LoadAsync = async (repo, ct) =>
        {
            (System.Collections.Generic.IReadOnlyDictionary<string, HartsyInference.Core.Tensors.Tensor> dict, System.IDisposable[] loaders)
                = await TtsModels.LoadCheckpointAsync(repo, ct).ConfigureAwait(false);
            bool is1B = repo.ToLowerInvariant().Contains("1b");
            // The SentencePiece text model isn't shipped in the -trfs repos — fetch it from the original repo.
            (string spmRepo, string spmFile) = is1B
                ? ("kyutai/stt-1b-en_fr", "tokenizer_en_fr_audio_8000.model")
                : ("kyutai/stt-2.6b-en", "tokenizer_en_audio_4000.model");
            string spm = await AudioModelCache.GetAsync(spmRepo, spmFile, ct: ct).ConfigureAwait(false);
            // 1B (en+fr, vocab 8001, 16 layers) vs 2.6B (en, vocab 4001, 48 layers) — configs differ, pick by repo.
            KyutaiSttConfig cfg = is1B ? KyutaiSttConfig.Stt1B : KyutaiSttConfig.Stt2_6B;
            // The -trfs checkpoint namespaces the Mimi codec under "codec_model."; strip it so the engine's keys match.
            System.Collections.Generic.Dictionary<string, HartsyInference.Core.Tensors.Tensor> mimi = new();
            foreach (System.Collections.Generic.KeyValuePair<string, HartsyInference.Core.Tensors.Tensor> kv in dict)
            {
                if (kv.Key.StartsWith("codec_model.", System.StringComparison.Ordinal))
                {
                    mimi[kv.Key["codec_model.".Length..]] = kv.Value;
                }
            }
            KyutaiSttPipeline pipeline = new(cfg);
            pipeline.LoadWeights(dict, mimi);
            KyutaiSttTokenizer tokenizer = new(spm);
            System.IDisposable[] keep = [pipeline, .. loaders];
            // Kyutai STT has no language/task tokens — the request's language/translate are ignored.
            return new SttRunner((backend, audio, _) => tokenizer.Decode(pipeline.Transcribe(backend, audio)), keep);
        },
    };

    /// <summary>Useful Sensors Moonshine (tiny/base).</summary>
    public static readonly SttModelDescriptor Moonshine = new()
    {
        ResolveRepo = ResolveMoonshineRepo,
        LoadAsync = async (repo, ct) =>
        {
            MoonshinePipeline p = await MoonshinePipeline.LoadAsync(repo, ct: ct).ConfigureAwait(false);
            // Moonshine has no language/task tokens — the request's language/translate are ignored.
            return new SttRunner((backend, audio, _) => p.TranscribeAudio(backend, audio, 16_000), p);
        },
    };

    /// <summary>Whisper Streaming — the same Whisper weights driven through the engine's
    /// <see cref="WhisperStreamingPipeline"/> (LocalAgreement-2 hypothesis buffer). For AudioLab's request/response
    /// path we feed the whole clip and flush, so the result equals batch Whisper with the streaming stabilizer; the
    /// value is the live partial/confirmed API for future real-time callers. Provider id <c>whisperstreaming_stt</c>.</summary>
    public static readonly SttModelDescriptor WhisperStreaming = new()
    {
        ResolveRepo = ResolveWhisperRepo,
        LoadAsync = async (repo, ct) =>
        {
            WhisperPipeline p = await WhisperPipeline.LoadAsync(repo, ct: ct).ConfigureAwait(false);
            return new SttRunner((backend, audio, req) =>
            {
                using WhisperStreamingPipeline stream = new(p, backend, ToWhisperOptions(req));
                stream.PushAudio(audio, 16_000);
                return stream.Finish();
            }, p);
        },
    };

    /// <summary>Whisper / distil-whisper model id → HF repo. Full repo ids (with '/') pass through; otherwise
    /// a size/variant token is matched; otherwise a sensible per-family default.</summary>
    private static string ResolveWhisperRepo(string modelId)
    {
        string id = (modelId ?? "").Trim();
        if (id.Contains('/'))
        {
            return id;
        }
        string lower = id.ToLowerInvariant();
        if (lower.Contains("distil"))
        {
            return ResolveDistilWhisperRepo(id);
        }
        // "turbo" is the provider's id for large-v3-turbo and has no "large" substring — match it first.
        if (lower.Contains("turbo")) return "openai/whisper-large-v3-turbo";
        if (lower.Contains("large")) return lower.Contains("v2") ? "openai/whisper-large-v2" : "openai/whisper-large-v3";
        if (lower.Contains("medium")) return "openai/whisper-medium";
        if (lower.Contains("small")) return "openai/whisper-small";
        if (lower.Contains("tiny")) return "openai/whisper-tiny";
        if (lower.Contains("base")) return "openai/whisper-base";
        return "openai/whisper-base";
    }

    /// <summary>distil-whisper model id → HF repo. The distil provider's ids are bare ("large-v3", "large-v3.5"),
    /// so unlike <see cref="ResolveWhisperRepo"/> this always resolves to the distil-whisper/* family.</summary>
    private static string ResolveDistilWhisperRepo(string modelId)
    {
        string id = (modelId ?? "").Trim();
        if (id.Contains('/'))
        {
            return id;
        }
        string lower = id.ToLowerInvariant();
        if (lower.Contains("v3.5")) return "distil-whisper/distil-large-v3.5"; // before v3 (v3.5 contains "v3")
        if (lower.Contains("v2")) return "distil-whisper/distil-large-v2";
        if (lower.Contains("medium")) return "distil-whisper/distil-medium.en";
        if (lower.Contains("small")) return "distil-whisper/distil-small.en";
        if (lower.Contains("v3")) return "distil-whisper/distil-large-v3";
        return "distil-whisper/distil-large-v3.5";
    }

    /// <summary>Moonshine model id → HF repo (only tiny/base exist upstream).</summary>
    private static string ResolveMoonshineRepo(string modelId)
    {
        string id = (modelId ?? "").Trim();
        if (id.Contains('/'))
        {
            return id;
        }
        return id.ToLowerInvariant().Contains("tiny")
            ? "UsefulSensors/moonshine-tiny"
            : "UsefulSensors/moonshine-base";
    }
}
