using System;
using SwarmUI.Utils;
using HartsyInference.Audio.Pipelines;

namespace Hartsy.Extensions.AudioLab.AudioServices.Tts;

/// <summary>Piper (VITS) — CPU TTS at 22.05 kHz. Provider id <c>piper_tts</c>. Self-contained: the engine
/// <see cref="PiperPipeline"/> bundles the pure-C# espeak-ng phonemizer (<c>EspeakPhonemizer.FromCache</c>) and
/// reads each voice's <c>phoneme_id_map</c> + espeak language straight from the Piper <c>.onnx.json</c>, so no
/// external front-end is needed. The engine VITS path is validated against onnxruntime (corr 0.9998). Not
/// zero-shot — no voice reference required.</summary>
public static class PiperModel
{
    /// <summary>Default English voice. The voice's <c>.onnx</c> + <c>.onnx.json</c> auto-download from
    /// <c>rhasspy/piper-voices</c> on first use.</summary>
    private const string DefaultVoice = "en_US-lessac-medium";

    public static readonly TtsModelDescriptor Descriptor = new()
    {
        ResolveRepo = _ => "rhasspy/piper-voices",
        LoadAsync = async (_, ct) =>
        {
            PiperPipeline pipeline = await PiperPipeline.LoadAsync(DefaultVoice, ct: ct).ConfigureAwait(false);
            Logs.Info($"[AudioLab][Piper] Loaded rhasspy/piper-voices {DefaultVoice} (VITS 22.05 kHz, bundled espeak-ng phonemizer).");
            return new TtsRunner(pipeline.SampleRate,
                (backend, req) => pipeline.SynthesizeText(backend, req.Text, seed: req.Seed),
                pipeline);
        },
    };
}
