using System;
using SwarmUI.Utils;
using HartsyInference.Core.Tensors;
using HartsyInference.Audio.Cache;
using HartsyInference.Audio.Models.CosyVoice;
using HartsyInference.Audio.Pipelines;
using HartsyInference.Audio.Preprocessing;
using HartsyInference.ModelHandler.Onnx;
using HartsyInference.ModelHandler.PyTorch;
using HartsyInference.Tokenizers;

namespace Hartsy.Extensions.AudioLab.AudioServices.Tts;

/// <summary>CosyVoice 2 (FunAudioLLM/CosyVoice2-0.5B) — zero-shot TTS: a Qwen2.5-0.5B LM emits S3 speech tokens
/// that an OT-CFM flow turns into a mel, vocoded by HiFTNet to 24 kHz. Speaker identity comes from a reference
/// clip via the CAM++ x-vector (campplus.onnx) + S3 prompt tokens (speech_tokenizer_v2.onnx). Provider id
/// <c>cosyvoice_tts</c>. All five weight files auto-download.
///
/// <para><b>Runtime-pending (real-weights smoke test):</b> (1) the reference-mel parameters here are the
/// matcha/CosyVoice convention (24 kHz, n_fft 1920, hop 480, 80 mels, fmin 0, fmax 8000, natural-log, 1e-5 floor,
/// magnitude) — the engine has no CosyVoice mel preset yet, so confirm against the upstream feature extractor.
/// (2) The engine's `flow.pt`/`llm.pt`/`hift.pt` key maps are flagged in-source as needing reconciliation against
/// the real checkpoints; CAM++/S3 (ONNX) are validated. (3) Text uses the Qwen2 tokenizer — confirm vocab parity.</para></summary>
public static class CosyVoiceModel
{
    private const string Repo = "FunAudioLLM/CosyVoice2-0.5B";

    public static readonly TtsModelDescriptor Descriptor = new()
    {
        ResolveRepo = modelId => (modelId ?? "").Contains('/') ? modelId : Repo,
        LoadAsync = async (_, ct) =>
        {
            string llmPath = await AudioModelCache.GetAsync(Repo, "llm.pt", ct: ct).ConfigureAwait(false);
            string flowPath = await AudioModelCache.GetAsync(Repo, "flow.pt", ct: ct).ConfigureAwait(false);
            string hiftPath = await AudioModelCache.GetAsync(Repo, "hift.pt", ct: ct).ConfigureAwait(false);
            string campPath = await AudioModelCache.GetAsync(Repo, "campplus.onnx", ct: ct).ConfigureAwait(false);
            string s3Path = await AudioModelCache.GetAsync(Repo, "speech_tokenizer_v2.onnx", ct: ct).ConfigureAwait(false);

            PytorchPickleLoader llmL = new(); llmL.Load(llmPath);
            PytorchPickleLoader flowL = new(); flowL.Load(flowPath);
            PytorchPickleLoader hiftL = new(); hiftL.Load(hiftPath);
            OnnxWeightLoader campL = new(); campL.Load(campPath);
            OnnxWeightLoader s3L = new(); s3L.Load(s3Path);

            CosyVoiceConfig cfg = CosyVoiceConfig.V2_0_5B;
            CosyVoiceQwenLm lm = new(cfg); lm.LoadWeights(llmL.GetAllTensors());
            CosyVoiceFlow flow = new(cfg); flow.LoadWeights(flowL.GetAllTensors());
            HiFTNetVocoder vocoder = new(cfg.Hift); vocoder.LoadWeights(hiftL.GetAllTensors());
            CamPlusSpeakerEncoder speaker = new(192); speaker.LoadWeights(campL.GetAllTensors());
            S3Tokenizer s3 = new(); s3.LoadWeights(s3L.GetAllTensors());
            CosyVoicePipeline pipeline = new(cfg, lm, flow, vocoder, speaker, s3);

            Qwen2Tokenizer tokenizer = new();
            Logs.Info("[AudioLab][CosyVoice] Loaded FunAudioLLM/CosyVoice2-0.5B (Qwen LM + OT-CFM flow + HiFTNet + CAM++ + S3, 24 kHz).");

            IDisposable[] keep = [pipeline, llmL, flowL, hiftL, campL, s3L];
            return new TtsRunner(cfg.SampleRate, (backend, req) =>
            {
                if (req.ReferenceMono24k is null || req.ReferenceMono24k.Length == 0)
                {
                    throw new InvalidOperationException(
                        "CosyVoice 2 is zero-shot — it needs a voice reference. Upload a short WAV clip in the voice reference field.");
                }
                int[] textTokenIds = [.. tokenizer.EncodeRaw(req.Text)];
                int[] refTextTokens = string.IsNullOrWhiteSpace(req.RefText) ? [] : [.. tokenizer.EncodeRaw(req.RefText)];
                // The pipeline derives the S3 mel (128-bin@100Hz), CAM++ fbank, and flow mel from the raw reference itself.
                return pipeline.Synthesize(backend, textTokenIds, referenceAudio: req.ReferenceMono24k,
                    referenceSampleRate: 24_000, referenceTextTokens: refTextTokens, seed: req.Seed);
            }, keep);
        },
    };
}
