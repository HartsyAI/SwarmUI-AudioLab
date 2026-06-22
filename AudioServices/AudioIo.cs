using System.Diagnostics;
using System.IO;
using Newtonsoft.Json.Linq;
using SwarmUI.Utils;
using HartsyInference.Audio.Io;

namespace Hartsy.Extensions.AudioLab.AudioServices;

/// <summary>Audio I/O helpers for the handlers: base64 ↔ mono <c>float[]</c> (decode via ffmpeg, encode WAV
/// in C#) and the success/error JObjects AudioLab parses.</summary>
public static class AudioIo
{
    /// <summary>Decodes base64-encoded audio bytes (any ffmpeg-readable format) to a mono <c>float[]</c> in
    /// <c>[-1, 1]</c> at <paramref name="targetSampleRate"/>. Returns an empty array for empty input.</summary>
    public static float[] DecodeBase64ToMono(string base64, int targetSampleRate, CancellationToken cancel)
    {
        if (string.IsNullOrEmpty(base64))
        {
            return [];
        }
        byte[] bytes = Convert.FromBase64String(base64);
        string ffmpeg = Utilities.FfmegLocation.Value;
        if (string.IsNullOrWhiteSpace(ffmpeg))
        {
            throw new SwarmUserErrorException(
                "AudioLab needs ffmpeg to decode the audio input, but none was found. "
                + "Install ffmpeg on your system PATH (or install the ComfyUI self-start backend, whose bundled copy Swarm reuses).");
        }
        // Extension is irrelevant — ffmpeg sniffs the container from content.
        string tmpFile = Path.Combine(Path.GetTempPath(), $"audiolab_in_{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(tmpFile, bytes);
        string args = $"-v error -i \"{tmpFile}\" -ac 1 -ar {targetSampleRate} -f f32le -";
        ProcessStartInfo psi = new()
        {
            FileName = ffmpeg,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        Logs.Verbose($"[AudioLab] Decoding audio input via ffmpeg {args}");
        Process proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to launch ffmpeg for audio decode.");
        try
        {
            Task<string> stderrTask = proc.StandardError.ReadToEndAsync(cancel);
            using MemoryStream raw = new();
            proc.StandardOutput.BaseStream.CopyTo(raw);
            string stderr = stderrTask.GetAwaiter().GetResult();
            proc.WaitForExit();
            cancel.ThrowIfCancellationRequested();
            if (proc.ExitCode != 0)
            {
                throw new SwarmUserErrorException($"AudioLab: ffmpeg failed to decode the audio input (exit {proc.ExitCode}): {stderr}");
            }
            byte[] outBytes = raw.ToArray();
            float[] samples = new float[outBytes.Length / 4];
            Buffer.BlockCopy(outBytes, 0, samples, 0, samples.Length * 4);
            Logs.Verbose($"[AudioLab] Decoded {samples.Length} mono samples @ {targetSampleRate} Hz ({samples.Length / (double)targetSampleRate:0.0}s).");
            return samples;
        }
        finally
        {
            if (!proc.HasExited)
            {
                try { proc.Kill(); } catch (Exception ex) { Logs.Error($"[AudioLab] Failed to kill ffmpeg: {ex.Message}"); }
            }
            proc.Dispose();
            try { if (File.Exists(tmpFile)) File.Delete(tmpFile); }
            catch (Exception ex) { Logs.Warning($"[AudioLab] Failed to delete temp audio file '{tmpFile}': {ex.Message}"); }
        }
    }

    /// <summary>Encodes a mono <c>float[]</c> waveform to a base64 16-bit PCM WAV string (pure C#, no ffmpeg).
    /// WAV is AudioLab's default output format and needs no external encoder.</summary>
    public static string EncodeMonoWavBase64(float[] samples, int sampleRate)
    {
        using MemoryStream ms = new();
        WavFile.WriteMono16(ms, samples, sampleRate);
        return Convert.ToBase64String(ms.ToArray());
    }

    /// <summary>Encodes a mono (<paramref name="right"/> null) or stereo waveform to a base64 16-bit PCM WAV.</summary>
    public static string EncodeWavBase64(float[] left, float[] right, int sampleRate)
    {
        if (right is null)
        {
            return EncodeMonoWavBase64(left, sampleRate);
        }
        int frames = Math.Min(left.Length, right.Length);
        int dataBytes = frames * 2 * sizeof(short);
        using MemoryStream ms = new();
        using BinaryWriter w = new(ms, System.Text.Encoding.ASCII, leaveOpen: true);
        w.Write("RIFF"u8.ToArray());
        w.Write(36 + dataBytes);
        w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray());
        w.Write(16);                         // fmt chunk size
        w.Write((short)1);                   // PCM
        w.Write((short)2);                   // channels
        w.Write(sampleRate);
        w.Write(sampleRate * 2 * sizeof(short)); // byte rate
        w.Write((short)(2 * sizeof(short)));     // block align
        w.Write((short)16);                  // bits per sample
        w.Write("data"u8.ToArray());
        w.Write(dataBytes);
        for (int i = 0; i < frames; i++)
        {
            w.Write(ToPcm16(left[i]));
            w.Write(ToPcm16(right[i]));
        }
        w.Flush();
        return Convert.ToBase64String(ms.ToArray());
    }

    private static short ToPcm16(float v) => (short)Math.Clamp((int)MathF.Round(v * 32767f), short.MinValue, short.MaxValue);

    /// <summary>Success result for an audio-producing request (TTS / voice-conv / FX / music).</summary>
    public static JObject AudioResult(string audioBase64, string outputFormat, double durationSeconds) => new()
    {
        ["success"] = true,
        ["audio_data"] = audioBase64,
        ["output_format"] = outputFormat,
        ["duration"] = durationSeconds,
    };

    /// <summary>Success result for a transcription (STT) request.</summary>
    public static JObject TranscriptionResult(string text, string language) => new()
    {
        ["success"] = true,
        ["text"] = text ?? "",
        ["language"] = language,
    };

    /// <summary>Failure result. AudioLab surfaces <c>error</c> to the user.</summary>
    public static JObject Error(string message) => new()
    {
        ["success"] = false,
        ["error"] = message,
    };

    /// <summary>Cancellation result — AudioLab treats this as a clean stop, not a failure.</summary>
    public static JObject Cancelled() => new()
    {
        ["success"] = false,
        ["cancelled"] = true,
    };

    /// <summary>Reads a string arg (the boxed values AudioLab passes are strings/doubles/ints).</summary>
    public static string Str(IReadOnlyDictionary<string, object> args, string key, string fallback = "")
        => args.TryGetValue(key, out object v) && v is not null ? v.ToString() : fallback;
}
