using System;
using System.Collections.Generic;
using SwarmUI.Utils;
using HartsyInference.Core.Tensors;
using HartsyInference.Audio.Cache;
using HartsyInference.Audio.Models.CosyVoice;
using HartsyInference.Audio.Pipelines;
using HartsyInference.ModelHandler.PyTorch;
using HartsyInference.ModelHandler.SafeTensors;
using HartsyInference.Tokenizers;

namespace Hartsy.Extensions.AudioLab.AudioServices.Tts;

/// <summary>CosyVoice 2 (FunAudioLLM/CosyVoice2-0.5B) — zero-shot TTS: a Qwen2.5-0.5B LM emits S3 speech tokens
/// that an OT-CFM flow turns into a mel, vocoded by HiFTNet to 24 kHz. Speaker identity comes from a reference
/// clip via the CAM++ x-vector + S3 prompt tokens. Provider id <c>cosyvoice_tts</c>.
///
/// <para><b>Frozen-encoder weight source:</b> CosyVoice2 ships CAM++ and S3 ONLY as ONNX, whose export fuses
/// Conv+BN (CAM++ head — <c>head.bn*</c> vanish) and mangles S3 names, so neither loads into the clean-name
/// engine components. Both are the SAME frozen pretrained models ResembleAI/chatterbox ships as clean-named
/// safetensors (<c>s3gen.safetensors</c> → <c>speaker_encoder.*</c> / <c>tokenizer.*</c>), so we load them from
/// there. The LM/flow/vocoder are CosyVoice2's own <c>llm.pt</c>/<c>flow.pt</c>/<c>hift.pt</c> (their default engine
/// key maps are verified against the real checkpoints; e2e whisper is word-perfect).</para></summary>
public static class CosyVoiceModel
{
    private const string Repo = "FunAudioLLM/CosyVoice2-0.5B";
    private const string FrozenRepo = "ResembleAI/chatterbox";   // frozen S3 tokenizer + CAM++ speaker (clean names)

    public static readonly TtsModelDescriptor Descriptor = new()
    {
        ResolveRepo = modelId => (modelId ?? "").Contains('/') ? modelId : Repo,
        LoadAsync = async (_, ct) =>
        {
            string llmPath = await AudioModelCache.GetAsync(Repo, "llm.pt", ct: ct).ConfigureAwait(false);
            string flowPath = await AudioModelCache.GetAsync(Repo, "flow.pt", ct: ct).ConfigureAwait(false);
            string hiftPath = await AudioModelCache.GetAsync(Repo, "hift.pt", ct: ct).ConfigureAwait(false);
            string s3genPath = await AudioModelCache.GetAsync(FrozenRepo, "s3gen.safetensors", ct: ct).ConfigureAwait(false);

            PytorchPickleLoader llmL = new(); llmL.Load(llmPath);
            PytorchPickleLoader flowL = new(); flowL.Load(flowPath);
            PytorchPickleLoader hiftL = new(); hiftL.Load(hiftPath);
            SafeTensorsLoader s3genL = new(); s3genL.Load(s3genPath);
            Dictionary<string, Tensor> s3gen = s3genL.GetAllTensors();

            CosyVoiceConfig cfg = CosyVoiceConfig.V2_0_5B;
            CosyVoiceQwenLm lm = new(cfg); lm.LoadWeights(llmL.GetAllTensors());
            CosyVoiceFlow flow = new(cfg); flow.LoadWeights(flowL.GetAllTensors());
            HiFTNetVocoder vocoder = new(cfg.Hift); vocoder.LoadWeights(hiftL.GetAllTensors());
            CamPlusSpeakerEncoder speaker = new(cfg.Flow.SpeakerEmbedDim); speaker.LoadWeights(s3gen, "speaker_encoder");
            S3Tokenizer s3 = new(); s3.LoadWeights(s3gen, "tokenizer");
            CosyVoicePipeline pipeline = new(cfg, lm, flow, vocoder, speaker, s3);

            Qwen2Tokenizer tokenizer = new();
            Logs.Info("[AudioLab][CosyVoice] Loaded FunAudioLLM/CosyVoice2-0.5B (Qwen LM + OT-CFM flow + HiFTNet + chatterbox S3/CAM++, 24 kHz).");

            IDisposable[] keep = [pipeline, llmL, flowL, hiftL, s3genL];
            return new TtsRunner(cfg.SampleRate, (backend, req) =>
            {
                if (req.ReferenceMono24k is null || req.ReferenceMono24k.Length == 0)
                {
                    throw new InvalidOperationException(
                        "CosyVoice 2 is zero-shot — it needs a voice reference. Upload a short WAV clip in the voice reference field.");
                }
                int[] textTokenIds = [.. tokenizer.EncodeRawByteLevel(req.Text)];
                int[] refTextTokens = string.IsNullOrWhiteSpace(req.RefText) ? [] : [.. tokenizer.EncodeRawByteLevel(req.RefText)];
                // The pipeline derives the S3 mel (128-bin@100Hz), CAM++ fbank, and flow mel from the raw reference itself.
                return pipeline.Synthesize(backend, textTokenIds, referenceAudio: req.ReferenceMono24k,
                    referenceSampleRate: 24_000, referenceTextTokens: refTextTokens, seed: req.Seed);
            }, keep);
        },
    };
}
