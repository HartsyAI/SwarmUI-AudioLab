using System;
using SwarmUI.Utils;
using HartsyInference.Audio.Pipelines;

namespace Hartsy.Extensions.AudioLab.AudioServices.Tts;

/// <summary>Spark-TTS-0.5B (SparkAudio/Spark-TTS-0.5B) — a Qwen2.5-0.5B LM emits the unified global + semantic
/// BiCodec token stream, which BiCodec decodes to a 16 kHz waveform. Provider id <c>sparktts_tts</c>. Runs the
/// in-engine <b>controllable</b> mode (<c>SparkTtsPipeline.SynthesizeControllable</c>): text + a coarse style
/// (gender / pitch / speed). The engine is real-weight bit-exact vs upstream (LM logits corr 1.0, BiCodec wav
/// corr 1.0).
///
/// <para>Style: gender comes from the voice field (<c>male</c>/<c>female</c>, default female); the speaking-rate
/// param maps to Spark's coarse speed buckets; pitch defaults to moderate. Zero-shot voice cloning needs the
/// BiCodec <i>encoder</i> side (wav2vec2 + ECAPA), which isn't built, so an uploaded reference clip is ignored —
/// this is the controllable synthetic-voice path.</para></summary>
public static class SparkTtsModel
{
    private const string Repo = "SparkAudio/Spark-TTS-0.5B";

    public static readonly TtsModelDescriptor Descriptor = new()
    {
        ResolveRepo = _ => Repo,
        LoadAsync = async (_, ct) =>
        {
            SparkTtsPipeline pipeline = await SparkTtsPipeline.LoadAsync(Repo, ct: ct).ConfigureAwait(false);
            Logs.Info("[AudioLab][Spark-TTS] Loaded SparkAudio/Spark-TTS-0.5B (Qwen2.5-0.5B LM + BiCodec, 16 kHz, controllable mode).");
            return new TtsRunner(pipeline.SampleRate, (backend, req) =>
            {
                string gender = string.Equals(req.Voice, "male", StringComparison.OrdinalIgnoreCase) ? "male" : "female";
                return pipeline.SynthesizeControllable(backend, req.Text, gender, pitch: "moderate",
                    speed: SpeedLevel(req.Speed), seed: req.Seed);
            }, pipeline);
        },
    };

    /// <summary>Maps a speaking-rate multiplier (Swarm's Speed param, ~1.0 = normal) to Spark's five coarse speed
    /// buckets; null → moderate.</summary>
    private static string SpeedLevel(double? speed)
    {
        if (speed is null)
        {
            return "moderate";
        }
        double s = speed.Value;
        return s < 0.7 ? "very_low" : s < 0.9 ? "low" : s <= 1.15 ? "moderate" : s <= 1.4 ? "high" : "very_high";
    }
}
