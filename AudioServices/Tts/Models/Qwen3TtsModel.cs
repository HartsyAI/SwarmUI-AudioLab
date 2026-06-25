using System;
using System.Collections.Generic;
using System.Threading;
using SwarmUI.Utils;
using HartsyInference.Core.Tensors;
using HartsyInference.Audio.Models.QwenTts;
using HartsyInference.Audio.Pipelines;
using HartsyInference.Tokenizers;

namespace Hartsy.Extensions.AudioLab.AudioServices.Tts;

/// <summary>Qwen3-TTS (Qwen/Qwen3-TTS-12Hz-*) — 12 Hz talker (semantic codebook-0) + MTP code-predictor
/// (codebooks 1..15) + Snake/ConvNeXt vocoder → 24 kHz. Provider id <c>qwen3_tts</c>. The model id encodes the
/// HF repo + mode (see <see cref="Qwen3TTSProvider"/> EngineConfig): <c>*-Base</c> → voice_clone,
/// <c>*-CustomVoice</c> → custom_voice (preset speakers), <c>*-VoiceDesign</c> → voice_design (instruct text).
///
/// <para><b>Verified modes:</b> custom_voice + voice_design (engine-confirmed). <b>Gated:</b> voice_clone is the
/// engine's "structural/ICL path pending real-weights validation" (<see cref="Qwen3TtsPipeline.SynthesizeVoiceClone"/>),
/// and only the 1.7B <see cref="Qwen3TtsConfig.Default_1_7B"/> preset exists today — the 0.6B config is engine-pending.
/// Text is Qwen BPE via the engine's <see cref="Qwen3Tokenizer"/>.</para></summary>
public static class Qwen3TtsModel
{
    private const string DefaultRepo = "Qwen/Qwen3-TTS-12Hz-1.7B-CustomVoice";

    /// <summary>English preset speaker id on the CustomVoice checkpoint (codec space). See pipeline docstring.</summary>
    private const int EnglishSpeakerToken = 3061;

    public static readonly TtsModelDescriptor Descriptor = new()
    {
        ResolveRepo = modelId => ResolveRepo(modelId),
        LoadAsync = async (modelId, ct) =>
        {
            string repo = ResolveRepo(modelId);
            string mode = ResolveMode(modelId);
            if (repo.Contains("0.6B", StringComparison.OrdinalIgnoreCase))
            {
                throw new NotSupportedException(
                    "[AudioLab][Qwen3-TTS] The 0.6B variant needs a Qwen3TtsConfig.Default_0_6B preset in the engine — "
                    + "only the 1.7B preset exists today. Pick a 1.7B model, or add the 0.6B config (see engine-work list).");
            }

            // Single combined checkpoint: each sub-model (talker/mtp/vocoder) slices its own prefix.
            // VERIFY against the published repo layout if loading fails on a missing key.
            (IReadOnlyDictionary<string, Tensor> dict, IDisposable[] loaders) = await TtsModels.LoadCheckpointAsync(repo, ct).ConfigureAwait(false);

            Qwen3TtsPipeline pipeline = new(Qwen3TtsConfig.Default_1_7B);
            pipeline.LoadWeights(dict, dict, dict);
            Qwen3Tokenizer tokenizer = new(maxLength: 512);
            Logs.Info($"[AudioLab][Qwen3-TTS] Loaded {repo} (mode={mode}, 12 Hz talker + MTP + vocoder, 24 kHz).");

            IDisposable[] keep = [pipeline, .. loaders];
            return new TtsRunner(pipeline.SampleRate, (backend, req) =>
            {
                int[] textTokens = tokenizer.Encode(req.Text, appendEos: false);
                return mode switch
                {
                    "custom_voice" => pipeline.SynthesizeCustomVoice(backend, textTokens, EnglishSpeakerToken, seed: req.Seed),
                    // Voice-design folds the instruct text into the token stream; here we pass the text as-is.
                    "voice_design" => pipeline.SynthesizeVoiceDesign(backend, textTokens, seed: req.Seed),
                    "voice_clone" => throw new NotSupportedException(
                        "[AudioLab][Qwen3-TTS] voice_clone is engine-pending (ICL/ECAPA path not yet weight-validated). "
                        + "Use a CustomVoice or VoiceDesign model until the engine confirms the clone path."),
                    _ => throw new InvalidOperationException($"[AudioLab][Qwen3-TTS] unknown mode '{mode}'."),
                };
            }, keep);
        },
    };

    /// <summary>The provider stores the HF repo in the model id (e.g. <c>1.7B-CustomVoice</c>); map to the repo.</summary>
    private static string ResolveRepo(string modelId)
    {
        string id = (modelId ?? "").Trim();
        if (id.Contains('/')) { return id; }
        string size = id.Contains("0.6B", StringComparison.OrdinalIgnoreCase) ? "0.6B" : "1.7B";
        string variant = id.Contains("CustomVoice", StringComparison.OrdinalIgnoreCase) ? "CustomVoice"
            : id.Contains("VoiceDesign", StringComparison.OrdinalIgnoreCase) ? "VoiceDesign"
            : "Base";
        return id.Length == 0 ? DefaultRepo : $"Qwen/Qwen3-TTS-12Hz-{size}-{variant}";
    }

    private static string ResolveMode(string modelId)
    {
        string id = (modelId ?? "").Trim();
        if (id.Contains("CustomVoice", StringComparison.OrdinalIgnoreCase)) { return "custom_voice"; }
        if (id.Contains("VoiceDesign", StringComparison.OrdinalIgnoreCase)) { return "voice_design"; }
        return "voice_clone";
    }
}
