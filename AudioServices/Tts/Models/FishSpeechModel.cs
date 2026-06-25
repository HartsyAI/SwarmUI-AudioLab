using System.IO;
using SwarmUI.Utils;
using HartsyInference.Audio.Cache;
using HartsyInference.Audio.Models.FishSpeech;
using HartsyInference.Audio.Pipelines;
using HartsyInference.ModelHandler.PyTorch;

namespace Hartsy.Extensions.AudioLab.AudioServices.Tts;

/// <summary>Fish-Speech 1.5 TTS (fishaudio/fish-speech-1.5) — a DualAR text2semantic model (Llama-style "slow"
/// backbone + "fast"/depth transformer over 8 audio codebooks) decoded by the firefly-gan-vq codec to 44.1 kHz
/// mono. Provider id <c>fishspeech_tts</c>.
///
/// <para>The HF repo ships <b>two</b> separate PyTorch pickles plus the tokenizer asset:
/// <list type="bullet">
///   <item><c>model.pth</c> — the DualAR weights (<c>embeddings.weight</c>, <c>model.*</c>, <c>fast_model.*</c>,
///   <c>output.weight</c>, …), consumed by <see cref="FishSpeechDualAr"/>.</item>
///   <item><c>firefly-gan-vq-fsq-8x1024-21hz-generator.pth</c> — the firefly codec (<c>quantizer.*</c> +
///   <c>head.*</c>), consumed by <see cref="FireflyDecoder"/>.</item>
///   <item><c>tokenizer.json</c> — the byte-level BPE vocab (102048 entries), loaded by
///   <see cref="FishSpeechTokenizer"/>.</item>
/// </list>
/// These are kept as two dicts because the pipeline's <c>LoadWeights(model, codec)</c> takes the DualAR and the
/// codec weight maps separately (the codec keys are not prefixed inside the model checkpoint).</para>
///
/// <para><b>Runtime-pending uncertainties:</b>
/// <list type="bullet">
///   <item><b>Codec filename:</b> the firefly generator is named
///   <c>firefly-gan-vq-fsq-8x1024-21hz-generator.pth</c> in the repo per <c>FireflyConfig</c> doc-comments; if the
///   repo layout differs this download will 404 and the filename must be adjusted.</item>
///   <item><b>Prompt format:</b> the upstream DualAR is conditioned on a chat-style prompt wrapped in
///   <c>&lt;|im_start|&gt;…&lt;|im_end|&gt;</c> with an <c>&lt;|audio_start|&gt;</c> marker before generation begins.
///   <see cref="FishSpeechTokenizer.Encode"/> peels those specials off the text, so we pass the wrapped form below.
///   If unconditioned (raw-text) prompting is preferred, drop the wrapper. Either way the Synthesize stop token is
///   the tokenizer's <see cref="FishSpeechTokenizer.ImEndId"/>.</item>
/// </list></para></summary>
public static class FishSpeechModel
{
    private const string Repo = "fishaudio/fish-speech-1.5";
    private const string ModelFile = "model.pth";
    private const string CodecFile = "firefly-gan-vq-fsq-8x1024-21hz-generator.pth";
    private const string TokenizerFile = "tokenizer.tiktoken";
    private const string SpecialTokensFile = "special_tokens.json";

    public static readonly TtsModelDescriptor Descriptor = new()
    {
        ResolveRepo = modelId => (modelId ?? "").Contains('/') ? modelId : Repo,
        LoadAsync = async (_, ct) =>
        {
            // Two separate pickles: DualAR weights + firefly codec weights. Both auto-download on first use.
            string modelPath = await AudioModelCache.GetAsync(Repo, ModelFile, ct: ct).ConfigureAwait(false);
            string codecPath = await AudioModelCache.GetAsync(Repo, CodecFile, ct: ct).ConfigureAwait(false);
            // The repo ships a tiktoken vocab + a special_tokens.json sibling; FishSpeechTokenizer.Load auto-finds
            // the sibling (same cache dir), so fetch both.
            string tokenizerPath = await AudioModelCache.GetAsync(Repo, TokenizerFile, ct: ct).ConfigureAwait(false);
            await AudioModelCache.GetAsync(Repo, SpecialTokensFile, ct: ct).ConfigureAwait(false);

            PytorchPickleLoader modelLoader = new();
            modelLoader.Load(modelPath);
            PytorchPickleLoader codecLoader = new();
            codecLoader.Load(codecPath);

            FishSpeechTokenizer tokenizer = new();
            tokenizer.Load(tokenizerPath);
            if (tokenizer.ImEndId < 0)
            {
                throw new InvalidOperationException(
                    $"Fish-Speech tokenizer at '{tokenizerPath}' is missing the '<|im_end|>' stop token — the "
                    + "DualAR pipeline cannot determine when to stop. Verify the tokenizer asset.");
            }

            FishSpeechPipeline pipeline = new(FishSpeechConfig.V1_5);
            pipeline.LoadWeights(modelLoader.GetAllTensors(), codecLoader.GetAllTensors());
            Logs.Info("[AudioLab][FishSpeech] Loaded fishaudio/fish-speech-1.5 (DualAR + firefly-gan-vq, 44.1 kHz).");

            // Chat-style conditioning wrapper (see class remarks); the tokenizer strips the specials to ids.
            // FishSpeechTokenizer is not IDisposable, so it is captured by the closure rather than passed as a
            // disposable. The pickle loaders are kept alive because the loaded F32 tensors reference their buffers.
            return new TtsRunner(pipeline.SampleRate, (backend, req) =>
            {
                string prompt = $"{FishSpeechTokenizer.ImStart}user\n{req.Text}{FishSpeechTokenizer.ImEnd}"
                    + $"{FishSpeechTokenizer.ImStart}assistant\n{FishSpeechTokenizer.AudioStart}";
                int[] tokens = tokenizer.Encode(prompt);
                return pipeline.Synthesize(backend, tokens, endToken: tokenizer.ImEndId, seed: req.Seed);
            }, pipeline, modelLoader, codecLoader);
        },
    };
}
