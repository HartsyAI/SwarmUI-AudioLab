using System;
using System.Collections.Generic;
using System.Threading;
using SwarmUI.Utils;
using HartsyInference.Audio.Cache;
using HartsyInference.Audio.Pipelines;

namespace Hartsy.Extensions.AudioLab.AudioServices.Tts;

/// <summary>Kyutai Pocket-TTS — a continuous-latent flow-LM (Helium-style backbone @ 12.5 Hz → Mimi → 24 kHz).
/// Provider id <c>pockettts_tts</c>. Engine path is real-weight parity-verified (all transformer layers + hidden
/// + latents corr 1.0 vs the <c>pocket_tts</c> reference). A voice is <b>required</b> — the model conditions on a
/// pre-primed speaker KV state and end-of-sequences into near-silence without one; predefined English voices
/// (default <c>alba</c>) ship as per-layer KV-cache safetensors.
///
/// <para><b>Weights:</b> the LANGUAGE-SPECIFIC checkpoint under the non-gated
/// <c>kyutai/pocket-tts-without-voice-cloning</c> — <c>languages/english/model.safetensors</c> + the SentencePiece
/// <c>tokenizer.model</c> + voices <c>languages/english/embeddings/&lt;name&gt;.safetensors</c>. (NOT the generic
/// <c>tts_*.safetensors</c> repack, which is a different checkpoint.) Arbitrary voice cloning is gated behind the
/// separate <c>kyutai/pocket-tts</c> weights and is not wired.</para></summary>
public static class PocketTtsModel
{
    private const string Repo = "kyutai/pocket-tts-without-voice-cloning";
    private const string Revision = "d29db7978e464fb90cb3359ee0c69a273b9142cc";
    private const string Language = "english";
    private const string WeightsFile = "languages/english/model.safetensors";
    private const string SpmFile = "tokenizer.model";
    private const string DefaultVoice = "alba";

    public static readonly TtsModelDescriptor Descriptor = new()
    {
        ResolveRepo = _ => Repo,
        LoadAsync = async (_, ct) =>
        {
            string weights = await AudioModelCache.GetAsync(Repo, WeightsFile, Revision, ct: ct).ConfigureAwait(false);
            string spm = await AudioModelCache.GetAsync(Repo, SpmFile, Revision, ct: ct).ConfigureAwait(false);
            PocketTtsPipeline pipeline = PocketTtsPipeline.LoadFromCheckpoint(weights, spm);
            await EnsureVoiceAsync(pipeline, DefaultVoice, ct).ConfigureAwait(false);
            Logs.Info("[AudioLab][Pocket-TTS] Loaded kyutai/pocket-tts (continuous-latent flow-LM, 24 kHz, English).");

            object voiceLock = new();
            return new TtsRunner(pipeline.SampleRate, (backend, req) =>
            {
                string voice = string.IsNullOrEmpty(req.Voice) ? DefaultVoice : req.Voice.ToLowerInvariant();
                if (!pipeline.HasVoice(voice))
                {
                    lock (voiceLock)
                    {
                        if (!pipeline.HasVoice(voice))
                        {
                            EnsureVoiceAsync(pipeline, voice, CancellationToken.None).GetAwaiter().GetResult();
                        }
                    }
                }
                return pipeline.Synthesize(backend, req.Text, voice, req.Seed);
            }, pipeline);
        },
    };

    /// <summary>Downloads a predefined English voice's KV-state safetensors and registers it with the pipeline.</summary>
    private static async Task EnsureVoiceAsync(PocketTtsPipeline pipeline, string voiceName, CancellationToken ct)
    {
        if (pipeline.HasVoice(voiceName))
        {
            return;
        }
        string path = await AudioModelCache.GetAsync(Repo, $"languages/{Language}/embeddings/{voiceName}.safetensors", Revision, ct: ct).ConfigureAwait(false);
        pipeline.RegisterVoiceFromFile(voiceName, path);
    }
}
