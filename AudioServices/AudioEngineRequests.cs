using System.Globalization;
using HartsyInference.Engine.Requests;

namespace Hartsy.Extensions.AudioLab.AudioServices;

/// <summary>Maps AudioLab's untyped request args (built by <c>DynamicAudioBackend.BuildEngineArgs</c> and by the
/// <c>AudioAPI</c> endpoints) onto the Engine's typed audio requests. This is the ONLY translation layer between
/// AudioLab and HartsyInference — everything past it is Engine-owned.
///
/// <para>The args dict stays as the interchange because the cloud API handlers (<see cref="IApiEngineHandler"/>)
/// consume the exact same dict; only the local-engine branch converts to typed requests.</para></summary>
public static class AudioEngineRequests
{
    /// <summary>Builds the text-to-speech request. Params AudioLab collects but the Engine's
    /// <see cref="SpeechRequest"/> has no field for (volume, sampling knobs, per-model extras like
    /// <c>speaker_id</c>/<c>qwen3_*</c>/<c>emotion</c>) are dropped here exactly as the old handler dropped
    /// them — the pipelines never read them.</summary>
    public static SpeechRequest Speech(IReadOnlyDictionary<string, object> args)
    {
        string text = AudioIo.Str(args, "text");
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("No text supplied to synthesize.");
        }
        string voice = AudioIo.Str(args, "voice");
        return new SpeechRequest
        {
            Text = text,
            RefText = AudioIo.Str(args, "ref_text"),
            Reference = Clip(args, "reference_audio"),
            // "default" is the API layer's placeholder (ProcessTTS's voice default), not a real voice name.
            Voice = string.IsNullOrEmpty(voice) || voice.Equals("default", StringComparison.OrdinalIgnoreCase) ? null : voice,
            Speed = Number(args, "speed"),
            Exaggeration = Number(args, "exaggeration"),
            NfeStep = Integer(args, "nfe_step"),
            CfgScale = Number(args, "cfg_scale"),
            Seed = Seed(args),
        };
    }

    /// <summary>Builds the transcription request. <c>task</c> comes from the Whisper provider (transcribe |
    /// translate); other STT providers don't set it. AudioLab exposes no word-timestamp or diarization param,
    /// so those Engine features stay off.</summary>
    public static AudioRequest Transcribe(IReadOnlyDictionary<string, object> args)
    {
        AudioClip audio = Clip(args, "audio_data")
            ?? throw new ArgumentException("No audio supplied to transcribe (the STT audio input is empty).");
        return new AudioRequest
        {
            Audio = audio,
            Language = AudioIo.Str(args, "language", "en"),
            Translate = AudioIo.Str(args, "task", "transcribe").Equals("translate", StringComparison.OrdinalIgnoreCase),
        };
    }

    /// <summary>Builds the music request, including the ACE-Step editing modes selected by <c>task_type</c>.
    /// <c>extract</c> and <c>lego</c> are refused by name: the Engine's <see cref="MusicRequest"/> has no
    /// counterpart, and silently generating plain text-to-music would be worse than an error.</summary>
    public static MusicRequest Music(IReadOnlyDictionary<string, object> args)
    {
        string prompt = AudioIo.Str(args, "prompt");
        string genre = AudioIo.Str(args, "genre");
        // ACE-Step puts the style in genre and (optional) lyrics in prompt, so either alone is enough.
        if (string.IsNullOrWhiteSpace(prompt) && string.IsNullOrWhiteSpace(genre))
        {
            throw new ArgumentException("No prompt supplied to generate music.");
        }
        string task = AudioIo.Str(args, "task_type", "text2music");
        AudioClip source = Clip(args, "src_audio");
        double shift = Double(args, "shift", 0d);
        double steps = Double(args, "infer_step", 0d);
        return new MusicRequest
        {
            Prompt = prompt,
            Genre = genre,
            Duration = Double(args, "duration", 10d),
            Seed = Seed(args),
            // Sentinel 0 = "model default" for shift/steps (the per-variant defaults live in the Engine's loaders).
            Shift = shift > 0 ? shift : null,
            InferSteps = steps > 0 ? (int)steps : null,
            // HeartMuLa sends "cfg_scale"; ACE-Step sends "guidance_scale" — one CFG knob per provider.
            CfgScale = args.ContainsKey("cfg_scale") ? Double(args, "cfg_scale", 1.5)
                : args.ContainsKey("guidance_scale") ? Double(args, "guidance_scale", 7.0) : null,
            Temperature = args.ContainsKey("temperature") ? Double(args, "temperature", 1.0) : null,
            TopK = args.ContainsKey("topk") ? (int)Double(args, "topk", 50) : null,
            // ACE-Step prompt-template metas (upstream SFT_GEN_PROMPT "# Metas" block + lyric language header).
            Bpm = args.ContainsKey("bpm") ? (int)Double(args, "bpm", 120) : null,
            KeyScale = AudioIo.Str(args, "key_scale"),
            TimeSignature = AudioIo.Str(args, "time_signature"),
            VocalLanguage = AudioIo.Str(args, "vocal_language"),
            // ACE-Step base/sft CFG controls.
            InferMethod = AudioIo.Str(args, "infer_method"),
            UseAdg = Flag(args, "use_adg", false),
            CfgIntervalStart = Double(args, "cfg_interval_start", 0d),
            CfgIntervalEnd = Double(args, "cfg_interval_end", 1d),
            // ACE-Step 5 Hz LM planner controls.
            LmModel = AudioIo.Str(args, "lm_model"),
            Thinking = Flag(args, "thinking", true),
            LmTemperature = Double(args, "lm_temperature", 0.85),
            LmCfgScale = Double(args, "lm_cfg_scale", 2.0),
            LmTopK = (int)Double(args, "lm_top_k", 0d),
            LmTopP = Double(args, "lm_top_p", 0.9),
            LmNegativePrompt = AudioIo.Str(args, "lm_negative_prompt"),
            // Audio-conditioned editing modes. All three are mutually exclusive; the Engine re-validates.
            Continuation = task.Equals("complete", StringComparison.OrdinalIgnoreCase) ? RequireSource(source, task) : null,
            Repaint = task.Equals("repaint", StringComparison.OrdinalIgnoreCase) ? RequireSource(source, task) : null,
            RepaintStart = Double(args, "repaint_start", 0d),
            RepaintEnd = Double(args, "repaint_end", 0d),
            Cover = task.Equals("cover", StringComparison.OrdinalIgnoreCase) ? RequireSource(source, task) : null,
            CoverStrength = Double(args, "cover_strength", 0.5),
        };
    }

    /// <summary>Builds the voice-conversion request. RVC's retrieval knobs (<c>f0method</c>, <c>index_rate</c>,
    /// <c>rms_mix_rate</c>, <c>protect</c>) have no Engine counterpart and are dropped, as they were before.</summary>
    public static VoiceConversionRequest VoiceConversion(IReadOnlyDictionary<string, object> args)
    {
        AudioClip source = Clip(args, "source_audio")
            ?? throw new ArgumentException("No source audio supplied to re-voice.");
        return new VoiceConversionRequest
        {
            Source = source,
            Target = Clip(args, "target_voice"),
            PitchShift = Double(args, "pitch_shift", 0d),
        };
    }

    /// <summary>Builds the stem-separation request. <c>model_name</c> comes from the model's EngineConfig
    /// (htdemucs / htdemucs_6s / …). <c>overlap</c> and <c>shifts</c> have no Engine counterpart.</summary>
    public static FxSeparateRequest Separate(IReadOnlyDictionary<string, object> args)
    {
        AudioClip audio = Clip(args, "audio_data") ?? throw new ArgumentException("No audio supplied to separate.");
        string model = AudioIo.Str(args, "model_name");
        return new FxSeparateRequest
        {
            Audio = audio,
            Model = string.IsNullOrWhiteSpace(model) ? null : model,
        };
    }

    /// <summary>Builds the speech-enhancement request. <c>nfe</c> and <c>solver</c> have no Engine counterpart
    /// (the LCFM step count / solver are not on <see cref="FxEnhanceRequest"/>) and are dropped.</summary>
    public static FxEnhanceRequest Enhance(IReadOnlyDictionary<string, object> args)
    {
        AudioClip audio = Clip(args, "audio_data") ?? throw new ArgumentException("No audio supplied to enhance.");
        return new FxEnhanceRequest
        {
            Audio = audio,
            Lambd = Number(args, "lambd"),
            Tau = Number(args, "tau"),
            Seed = Seed(args),
        };
    }

    /// <summary>The source clip an editing task requires, or a precise error naming what is missing.</summary>
    private static AudioClip RequireSource(AudioClip source, string task)
        => source ?? throw new ArgumentException(
            $"The ACE-Step '{task}' task needs a source clip — set 'ACE Source Audio' (or switch Task Type back to text2music).");

    /// <summary>Decodes a base64 arg into an Engine clip; null when the arg is absent or empty. The Engine
    /// decodes the container itself, so no ffmpeg pass happens here any more.</summary>
    private static AudioClip Clip(IReadOnlyDictionary<string, object> args, string key)
    {
        string base64 = AudioIo.Str(args, key);
        if (string.IsNullOrEmpty(base64))
        {
            return null;
        }
        return new AudioClip { Data = Convert.FromBase64String(base64) };
    }

    /// <summary>Reads an optional numeric arg (boxed double/int/long/float/string); null when absent.</summary>
    private static double? Number(IReadOnlyDictionary<string, object> args, string key)
    {
        if (!args.TryGetValue(key, out object value) || value is null)
        {
            return null;
        }
        return value switch
        {
            double d => d,
            int i => i,
            long l => l,
            float f => f,
            _ => double.TryParse(value.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double parsed) ? parsed : null,
        };
    }

    /// <summary>Reads a numeric arg with a fallback.</summary>
    private static double Double(IReadOnlyDictionary<string, object> args, string key, double fallback)
        => Number(args, key) ?? fallback;

    /// <summary>Reads an optional integer arg; null when absent or unparseable.</summary>
    private static int? Integer(IReadOnlyDictionary<string, object> args, string key)
    {
        double? value = Number(args, key);
        return value.HasValue ? (int)Math.Round(value.Value) : null;
    }

    /// <summary>Reads a boolean arg. AudioLab sends these as the strings "true"/"false".</summary>
    private static bool Flag(IReadOnlyDictionary<string, object> args, string key, bool fallback)
    {
        if (!args.TryGetValue(key, out object value) || value is null)
        {
            return fallback;
        }
        return value is bool boxed ? boxed
            : bool.TryParse(value.ToString(), out bool parsed) ? parsed : fallback;
    }

    /// <summary>Reads SwarmUI's seed (a long; -1 = "random") as the int the Engine takes, where 0 means
    /// "leave unset" — so a -1 from Swarm becomes 0 rather than a negative seed.</summary>
    private static int Seed(IReadOnlyDictionary<string, object> args)
    {
        double? raw = Number(args, "seed");
        if (!raw.HasValue || raw.Value < 0)
        {
            return 0;
        }
        return unchecked((int)(long)raw.Value);
    }
}
