using System;
using System.Collections.Generic;
using SwarmUI.Utils;
using HartsyInference.Core.Tensors;
using HartsyInference.Audio.Cache;
using HartsyInference.Audio.Models.Chatterbox;
using HartsyInference.Audio.Models.CosyVoice;
using HartsyInference.Audio.Pipelines;
using HartsyInference.ModelHandler.SafeTensors;
using HartsyInference.Tokenizers;

namespace Hartsy.Extensions.AudioLab.AudioServices.Tts;

/// <summary>Chatterbox (ResembleAI/chatterbox) — T3 LM → CosyVoice2 S3Gen flow → HiFTNet, 24 kHz. Provider id
/// <c>chatterbox_tts</c>. Load is the engine-verified recipe (merge <c>t3_cfg.safetensors</c> under <c>t3.</c> +
/// <c>s3gen.safetensors</c> under <c>s3gen.</c>; <c>ChatterboxConfig.Default</c> + <c>CosyVoiceConfig.V2_0_5B</c>),
/// and text uses the shipped <c>ChatterboxEnTokenizer</c>. The remaining gap is the voice conditioning: synth needs
/// the speaker embeddings (T3 [1,256] + flow [1,192]) — the default voice ships only as <c>conds.pt</c> (a torch
/// pickle, not safetensors), and reference-voice mode needs a PCM→mel extractor. Load is wired; synth is gated
/// until a conds.pt extractor or a mel-from-reference path lands (engine-work list).</summary>
public static class ChatterboxModel
{
    private const string Repo = "ResembleAI/chatterbox";

    public static readonly TtsModelDescriptor Descriptor = new()
    {
        ResolveRepo = _ => Repo,
        LoadAsync = async (_, ct) =>
        {
            string t3Path = await AudioModelCache.GetAsync(Repo, "t3_cfg.safetensors", ct: ct).ConfigureAwait(false);
            string s3Path = await AudioModelCache.GetAsync(Repo, "s3gen.safetensors", ct: ct).ConfigureAwait(false);
            string tokPath = await AudioModelCache.GetAsync(Repo, "tokenizer.json", ct: ct).ConfigureAwait(false);

            // Merge the two checkpoints under the prefixes ChatterboxPipeline.LoadWeights validates.
            Dictionary<string, Tensor> merged = new();
            SafeTensorsLoader t3Loader = new(); t3Loader.Load(t3Path);
            foreach (KeyValuePair<string, Tensor> kv in t3Loader.GetAllTensors()) { merged["t3." + kv.Key] = kv.Value; }
            SafeTensorsLoader s3Loader = new(); s3Loader.Load(s3Path);
            foreach (KeyValuePair<string, Tensor> kv in s3Loader.GetAllTensors()) { merged["s3gen." + kv.Key] = kv.Value; }

            ChatterboxConfig cfg = ChatterboxConfig.Default;
            CosyVoiceConfig cosy = CosyVoiceConfig.V2_0_5B;
            ChatterboxT3 t3 = new(cfg);
            CosyVoiceFlow flow = new(cosy);
            HiFTNetVocoder vocoder = new(cosy.Hift);
            CamPlusSpeakerEncoder spkEnc = new(cosy.Flow.SpeakerEmbedDim);
            ChatterboxPipeline pipeline = new(cfg, t3, flow, vocoder, spkEnc);
            pipeline.LoadWeights(merged); // key-validation gate: throws on any missing/renamed key.

            using System.IO.Stream tokStream = System.IO.File.OpenRead(tokPath);
            ChatterboxEnTokenizer tok = new(tokStream);
            _ = tok; // tokenizer ready; held for when voice conditioning is wired.
            Logs.Info("[AudioLab][Chatterbox] Loaded ResembleAI/chatterbox (T3 + S3Gen + HiFTNet, 24 kHz). Synth gated on voice conds.");

            IDisposable[] keep = [pipeline, t3, flow, vocoder, spkEnc, t3Loader, s3Loader];
            return new TtsRunner(cfg.SampleRate, (_, _) => throw new NotSupportedException(
                "[AudioLab][Chatterbox] Load is wired, but synth needs the speaker embeddings (T3 [1,256] + flow [1,192]): "
                + "the default voice ships only as conds.pt (torch pickle), and reference mode needs a PCM→mel extractor. "
                + "Add a conds.pt export or a mel-from-reference path. See engine-work list."), keep);
        },
    };
}
