using System.IO;
using HartsyInference.Audio.Frontends;
using HartsyInference.Audio.Pipelines;

namespace Hartsy.Extensions.AudioLab.AudioServices.Tts;

/// <summary>Per-model specifics for the generic <see cref="TtsHandler"/>: how to turn an AudioLab model id
/// into a repo (empty when the pipeline hardcodes it), and how to load it into an <see cref="ITtsRunner"/>.</summary>
public sealed class TtsModelDescriptor
{
    /// <summary>Maps an AudioLab model id (the <c>__model_id</c> variant hint) to a HuggingFace repo, or a
    /// constant/empty string when the pipeline resolves its own weights.</summary>
    public required Func<string, string> ResolveRepo { get; init; }

    /// <summary>Loads the model (downloading on first use) into a uniform runner.</summary>
    public required Func<string, CancellationToken, Task<ITtsRunner>> LoadAsync { get; init; }
}

/// <summary>TTS model registry. VibeVoice runs today; token-based TTS (Dia, Orpheus, CSM) join once the
/// engine's AudioTextFrontend + Llama/BERT tokenizer assets land.</summary>
public static class TtsModels
{
    /// <summary>VibeVoice 1.5B — long-form multi-speaker synthesis. Built-in tokenizer (raw text in);
    /// requires a 24 kHz voice reference. The pipeline hardcodes its HF repo, so the model id is ignored.</summary>
    public static readonly TtsModelDescriptor VibeVoice = new()
    {
        ResolveRepo = _ => "vibevoice/VibeVoice-1.5B",
        LoadAsync = async (_, ct) =>
        {
            VibeVoicePipeline p = await VibeVoicePipeline.LoadAsync(ct).ConfigureAwait(false);
            return new TtsRunner(p, sampleRate: 24_000, (backend, req) =>
            {
                if (req.ReferenceWavPath is null)
                {
                    throw new InvalidOperationException(
                        "VibeVoice needs a voice reference — upload a short WAV clip in the voice reference field.");
                }
                // Single line / single speaker. maxNewTokens caps the AR loop (it stops early at EOS);
                // generous so typical sentences/paragraphs aren't truncated.
                return p.Synthesize(backend, new[] { req.Text }, new[] { req.ReferenceWavPath }, maxNewTokens: 1024);
            });
        },
    };

    /// <summary>Kokoro-82M — fast CPU-capable TTS at 24 kHz. Auto-downloads its weights + voice packs; uses
    /// the engine's English <see cref="EnglishG2P"/> (text→IPA) front-end. Needs a CMU dictionary
    /// (<c>cmudict.dict</c>) placed in the audio model root. Built-in voice (default <c>af_heart</c>).</summary>
    public static readonly TtsModelDescriptor Kokoro = new()
    {
        ResolveRepo = _ => "hexgrad/Kokoro-82M",
        LoadAsync = async (_, ct) =>
        {
            string cmudict = Path.Combine(Path.GetFullPath(AudioConfiguration.ModelRoot), "cmudict.dict");
            if (!File.Exists(cmudict))
            {
                throw new FileNotFoundException(
                    $"Kokoro needs an English G2P dictionary — place the public-domain CMU Pronouncing Dictionary "
                    + $"('cmudict.dict') at '{cmudict}'.", cmudict);
            }
            EnglishG2P g2p = new(cmudict);
            KokoroPipeline p = await KokoroPipeline.LoadAsync(ct).ConfigureAwait(false);
            return new TtsRunner(p, sampleRate: 24_000, (backend, req) =>
                p.Synthesize(backend, g2p.ToIpa(req.Text), voiceName: "af_heart"));
        },
    };
}
