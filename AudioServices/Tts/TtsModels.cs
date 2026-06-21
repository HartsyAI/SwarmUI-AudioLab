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

/// <summary>The TTS model registry. Each entry wires an engine pipeline to the generic handler — no
/// per-model handler/runner classes.
///
/// <para>Most token-based TTS models (Dia, Orpheus, CSM, …) join here once the engine's text front-ends
/// land (they need <c>AudioTextFrontend</c> from a newer engine build, and some need the Llama-3 / BERT
/// tokenizer assets). VibeVoice needs none of that — it has a built-in tokenizer and takes raw text +
/// a voice reference — so it runs against the current engine today.</para>
/// </summary>
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
}
