using System;
using System.Threading;
using SwarmUI.Utils;
using HartsyInference.Audio.Models.Codecs.NeuCodec;
using HartsyInference.Audio.Models.NeuTts;
using HartsyInference.Audio.Pipelines;
using HartsyInference.Phonemizer;
using HartsyInference.Phonemizer.Espeak;
using HartsyInference.Tokenizers;

namespace Hartsy.Extensions.AudioLab.AudioServices.Tts;

/// <summary>NeuTTS Air (neuphonic/neutts-air) — a Qwen2.5-0.5B LM that emits a single NeuCodec FSQ stream,
/// decoded to 24 kHz. Provider id <c>neutts_tts</c>. Backbone + NeuCodec auto-download.
///
/// <para><b>Prompt template (verified against upstream <c>neutts/neutts.py</c> <c>_apply_chat_template</c>):</b>
/// text is espeak-phonemized to IPA (en-us; upstream uses stress + preserved punctuation), then framed as
/// <c>"user: Convert the text to speech:" + TEXT_PROMPT_START + phones + TEXT_PROMPT_END + "\nassistant:"</c>
/// (the pipeline appends <c>SPEECH_GENERATION_START</c> + reference codes). The checkpoint carries dedicated IPA
/// added-tokens above the speech block, mapped by <see cref="NeuTtsPromptBuilder"/>; remaining spans use the
/// byte-level-exact Qwen2 BPE (<see cref="Qwen2Tokenizer.EncodeRawByteLevel"/>).</para>
///
/// <para><b>Voice cloning:</b> when the user supplies a voice reference, the reference clip is decoded to 16 kHz
/// and encoded by <see cref="NeuCodecEncoder"/> into FSQ <c>refCodes</c> that prime generation; the reference
/// transcript is phonemized and prepended (<c>phones(refText) + " " + phones(text)</c>, as upstream). Upstream
/// ALWAYS clones from a reference — the no-reference "default voice" path here is a best-effort extension, not
/// an upstream mode, so expect an arbitrary speaker without a reference.</para></summary>
public static class NeuTtsModel
{
    private const string BackboneRepo = "neuphonic/neutts-air";
    private const string CodecRepo = "neuphonic/neucodec";
    private const string EspeakLanguage = "en-us";   // upstream BACKBONE_LANGUAGE_MAP["neuphonic/neutts-air"]

    public static readonly TtsModelDescriptor Descriptor = new()
    {
        ResolveRepo = modelId => (modelId ?? "").Contains('/') ? modelId : BackboneRepo,
        LoadAsync = async (_, ct) =>
        {
            (System.Collections.Generic.IReadOnlyDictionary<string, HartsyInference.Core.Tensors.Tensor> backbone, IDisposable[] bbLoaders)
                = await TtsModels.LoadCheckpointAsync(BackboneRepo, ct).ConfigureAwait(false);
            (System.Collections.Generic.IReadOnlyDictionary<string, HartsyInference.Core.Tensors.Tensor> codec, IDisposable[] codecLoaders)
                = await TtsModels.LoadCheckpointAsync(CodecRepo, ct).ConfigureAwait(false);

            NeuTtsConfig cfg = NeuTtsConfig.Air;
            NeuTtsPipeline pipeline = new(cfg);
            pipeline.LoadWeights(backbone, codec);
            // The NeuCodec checkpoint's ENCODER is X-Codec2 layout (CodecEnc.* weight-norm convs + Snake), which
            // the engine's NeuCodecEncoder doesn't map yet — load it only if its expected keys exist, so the
            // default voice works today and cloning gates with a clear message. TODO(engine): port the
            // X-Codec2 encoder (CodecEnc.*, SemanticEncoder_module.*, fc_prior.*) to unlock reference cloning.
            NeuCodecEncoder encoder = null;
            if (codec.ContainsKey("encoder.stem.weight"))
            {
                encoder = new(NeuCodecEncoderConfig.Default);
                encoder.LoadWeights(codec);
            }
            // Upstream phonemizes with espeak-ng (phonemizer EspeakBackend, en-us, with_stress=True,
            // preserve_punctuation=True); raw text is out-of-distribution and produces garble.
            IPhonemizer phonemizer = EspeakPhonemizer.FromCache(EspeakLanguage);
            Qwen2Tokenizer tokenizer = new();
            Logs.Info($"[AudioLab][NeuTTS] Loaded neuphonic/neutts-air (Qwen2.5-0.5B + NeuCodec, 24 kHz; cloning {(encoder is null ? "unavailable — X-Codec2 encoder port pending" : "when a reference is supplied")}).");

            // Mirrors upstream _to_phones: phonemize, then whitespace-normalize.
            string Phones(string s) =>
                string.Join(' ', phonemizer.PhonemizeToIpa(s, EspeakLanguage).Split((char[])null, StringSplitOptions.RemoveEmptyEntries));

            IDisposable[] keep = encoder is null
                ? [pipeline, tokenizer, .. bbLoaders, .. codecLoaders]
                : [pipeline, encoder, tokenizer, .. bbLoaders, .. codecLoaders];
            return new TtsRunner(pipeline.SampleRate, (backend, req) =>
            {
                int[] refCodes = Array.Empty<int>();
                string phones = Phones(req.Text);
                if (!string.IsNullOrEmpty(req.ReferenceB64))
                {
                    if (encoder is null)
                    {
                        throw new NotSupportedException(
                            "[AudioLab][NeuTTS] Reference-voice cloning needs the X-Codec2 encoder (CodecEnc.*) which "
                            + "the engine doesn't map yet. Clear the voice reference to use the default voice.");
                    }
                    float[] ref16k = AudioIo.DecodeBase64ToMono(req.ReferenceB64, 16_000, CancellationToken.None);
                    if (ref16k.Length > 0)
                    {
                        refCodes = encoder.Encode(backend, ref16k);
                    }
                    if (!string.IsNullOrWhiteSpace(req.RefText))
                    {
                        phones = $"{Phones(req.RefText)} {phones}";   // upstream joins ref + target phones with " "
                    }
                }
                int[] promptPrefix = NeuTtsPromptBuilder.BuildPromptPrefix(cfg, tokenizer.EncodeRawByteLevel, phones);
                return pipeline.Synthesize(backend, promptPrefix, refCodes, seed: req.Seed);
            }, keep);
        },
    };
}
