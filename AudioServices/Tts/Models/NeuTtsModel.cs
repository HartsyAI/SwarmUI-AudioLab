using System;
using System.Threading;
using SwarmUI.Utils;
using HartsyInference.Audio.Models.Codecs.NeuCodec;
using HartsyInference.Audio.Models.NeuTts;
using HartsyInference.Audio.Pipelines;
using HartsyInference.Tokenizers;

namespace Hartsy.Extensions.AudioLab.AudioServices.Tts;

/// <summary>NeuTTS Air (neuphonic/neutts-air) — a Qwen2.5-0.5B LM that emits a single NeuCodec FSQ stream,
/// decoded to 24 kHz. Provider id <c>neutts_tts</c>. Backbone + NeuCodec auto-download.
///
/// <para><b>Voice cloning:</b> when the user supplies a voice reference, the reference clip is decoded to 16 kHz
/// and encoded by <see cref="NeuCodecEncoder"/> into FSQ <c>refCodes</c> that prime generation; the reference
/// transcript (if provided) is prepended to the prompt. With no reference, the model's default voice is used.</para>
///
/// <para><b>Runtime-pending:</b> the exact NeuTTS Air prompt template (chat wrapping / TextPromptStart-End
/// specials, and the reference-text join) needs verification against upstream — the form below is a reasonable
/// approximation, not yet weight-verified.</para></summary>
public static class NeuTtsModel
{
    private const string BackboneRepo = "neuphonic/neutts-air";
    private const string CodecRepo = "neuphonic/neucodec";

    public static readonly TtsModelDescriptor Descriptor = new()
    {
        ResolveRepo = modelId => (modelId ?? "").Contains('/') ? modelId : BackboneRepo,
        LoadAsync = async (_, ct) =>
        {
            (System.Collections.Generic.IReadOnlyDictionary<string, HartsyInference.Core.Tensors.Tensor> backbone, IDisposable[] bbLoaders)
                = await TtsModels.LoadCheckpointAsync(BackboneRepo, ct).ConfigureAwait(false);
            (System.Collections.Generic.IReadOnlyDictionary<string, HartsyInference.Core.Tensors.Tensor> codec, IDisposable[] codecLoaders)
                = await TtsModels.LoadCheckpointAsync(CodecRepo, ct).ConfigureAwait(false);

            NeuTtsPipeline pipeline = new(NeuTtsConfig.Air);
            pipeline.LoadWeights(backbone, codec);
            // The NeuCodec checkpoint carries both decoder (loaded above) and encoder (used for cloning).
            NeuCodecEncoder encoder = new(NeuCodecEncoderConfig.Default);
            encoder.LoadWeights(codec);
            Qwen2Tokenizer tokenizer = new();
            Logs.Info("[AudioLab][NeuTTS] Loaded neuphonic/neutts-air (Qwen2.5-0.5B + NeuCodec, 24 kHz; cloning when a reference is supplied).");

            IDisposable[] keep = [pipeline, encoder, .. bbLoaders, .. codecLoaders];
            return new TtsRunner(pipeline.SampleRate, (backend, req) =>
            {
                int[] refCodes = Array.Empty<int>();
                string prefixText = $"Convert the text to speech: {req.Text}";
                if (!string.IsNullOrEmpty(req.ReferenceB64))
                {
                    float[] ref16k = AudioIo.DecodeBase64ToMono(req.ReferenceB64, 16_000, CancellationToken.None);
                    if (ref16k.Length > 0)
                    {
                        refCodes = encoder.Encode(backend, ref16k);
                    }
                    if (!string.IsNullOrWhiteSpace(req.RefText))
                    {
                        prefixText = $"Convert the text to speech: {req.RefText} {req.Text}";
                    }
                }
                int[] promptPrefix = [.. tokenizer.EncodeRaw(prefixText)];
                return pipeline.Synthesize(backend, promptPrefix, refCodes, seed: req.Seed);
            }, keep);
        },
    };
}
