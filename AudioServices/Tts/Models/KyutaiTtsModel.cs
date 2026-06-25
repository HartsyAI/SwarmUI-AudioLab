using System;
using System.Collections.Generic;
using SwarmUI.Utils;
using HartsyInference.Core.Tensors;
using HartsyInference.Audio.Models.Kyutai;
using HartsyInference.Audio.Pipelines;

namespace Hartsy.Extensions.AudioLab.AudioServices.Tts;

/// <summary>Kyutai TTS (kyutai/tts-1.6b-en_fr) — Helium backbone + Mimi codec, delayed-streams @ 12.5 Hz →
/// 24 kHz. Provider id <c>kyutai_tts</c>. Weights load like the already-wired Kyutai STT (split the Mimi codec
/// off the <c>codec_model.</c> prefix), but synthesis needs the per-frame text stream the talker consumes —
/// a SentencePiece tokenizer + the PAD/EPAD/WORD frame state machine, plus the engine's delayed-coordinate /
/// speaker-conditioning path which is validation-pending. Load is wired; synth is gated (see engine-work list).</summary>
public static class KyutaiTtsModel
{
    private const string Repo = "kyutai/tts-1.6b-en_fr";

    public static readonly TtsModelDescriptor Descriptor = new()
    {
        ResolveRepo = modelId => (modelId ?? "").Contains('/') ? modelId : Repo,
        LoadAsync = async (modelId, ct) =>
        {
            string repo = (modelId ?? "").Contains('/') ? modelId : Repo;
            (IReadOnlyDictionary<string, Tensor> dict, IDisposable[] loaders) = await TtsModels.LoadCheckpointAsync(repo, ct).ConfigureAwait(false);

            // The checkpoint namespaces the Mimi codec under "codec_model."; strip it so the codec keys match.
            Dictionary<string, Tensor> mimi = new();
            Dictionary<string, Tensor> backbone = new();
            foreach (KeyValuePair<string, Tensor> kv in dict)
            {
                if (kv.Key.StartsWith("codec_model.", StringComparison.Ordinal)) { mimi[kv.Key["codec_model.".Length..]] = kv.Value; }
                else { backbone[kv.Key] = kv.Value; }
            }

            KyutaiTtsPipeline pipeline = new(KyutaiTtsConfig.Tts1_6B);
            pipeline.LoadWeights(backbone, mimi);
            Logs.Info($"[AudioLab][Kyutai-TTS] Loaded {repo} (Helium + Mimi, 24 kHz). Synth gated until the per-frame text front-end lands.");

            IDisposable[] keep = [pipeline, .. loaders];
            return new TtsRunner(pipeline.SampleRate, (_, _) => throw new NotSupportedException(
                "[AudioLab][Kyutai-TTS] Synthesis needs the per-frame text stream (SentencePiece 8k + PAD/EPAD/WORD frame "
                + "state machine) and the engine's delayed-coordinate/speaker-conditioning path (validation-pending). "
                + "See engine-work list."), keep);
        },
    };
}
