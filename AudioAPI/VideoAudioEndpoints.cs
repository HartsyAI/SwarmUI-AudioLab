using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Core;
using SwarmUI.Utils;
using SwarmUI.WebAPI;
using System.Diagnostics;
using System.IO;

namespace Hartsy.Extensions.AudioLab.AudioAPI;

/// <summary>API endpoints for combining video and audio using ffmpeg.</summary>
[API.APIClass("AudioLab Video+Audio combining endpoints")]
public static class VideoAudioEndpoints
{
    /// <summary>Maximum allowed video file size in megabytes.</summary>
    private const long MaxVideoSizeMB = 200;

    /// <summary>Maximum allowed audio file size in megabytes.</summary>
    private const long MaxAudioSizeMB = 50;

    /// <summary>Registers all video+audio API endpoints.</summary>
    public static void Register()
    {
        API.RegisterAPICall(CombineVideoAudio, false, AudioLabPermissions.PermProcessAudio);
        API.RegisterAPICall(ExtractAudioFromVideo, false, AudioLabPermissions.PermProcessAudio);
    }

    /// <summary>Combine a video file with an audio track using ffmpeg.
    /// Input: video_data (base64), audio_data (base64), mode ("replace" or "mix").
    /// Returns: combined video as base64.</summary>
    public static async Task<JObject> CombineVideoAudio(Session session, JObject input)
    {
        string ffmpeg = Utilities.FfmegLocation.Value;
        if (string.IsNullOrEmpty(ffmpeg))
        {
            return AudioLab.CreateErrorResponse("ffmpeg not found. Install ffmpeg and ensure it is in your PATH.", "ffmpeg_not_found");
        }

        try
        {
            string videoData = input["video_data"]?.ToString();
            string audioData = input["audio_data"]?.ToString();
            string mode = input["mode"]?.ToString() ?? "replace";

            if (string.IsNullOrEmpty(videoData))
                return AudioLab.CreateErrorResponse("video_data is required", "missing_video");
            if (string.IsNullOrEmpty(audioData))
                return AudioLab.CreateErrorResponse("audio_data is required", "missing_audio");

            // Validate sizes
            long videoBytes = (videoData.Length * 3) / 4;
            long audioBytes = (audioData.Length * 3) / 4;
            if (videoBytes > MaxVideoSizeMB * 1024 * 1024)
                return AudioLab.CreateErrorResponse($"Video too large (max {MaxVideoSizeMB}MB)", "video_too_large");
            if (audioBytes > MaxAudioSizeMB * 1024 * 1024)
                return AudioLab.CreateErrorResponse($"Audio too large (max {MaxAudioSizeMB}MB)", "audio_too_large");

            // Write temp files
            string tempDir = Path.Combine(Path.GetTempPath(), "audiolab_video");
            Directory.CreateDirectory(tempDir);
            string videoPath = Path.Combine(tempDir, $"video_{Guid.NewGuid():N}.mp4");
            string audioPath = Path.Combine(tempDir, $"audio_{Guid.NewGuid():N}.wav");
            string outputPath = Path.Combine(tempDir, $"output_{Guid.NewGuid():N}.mp4");

            try
            {
                await File.WriteAllBytesAsync(videoPath, Convert.FromBase64String(videoData));
                await File.WriteAllBytesAsync(audioPath, Convert.FromBase64String(audioData));

                string[] args = mode == "mix"
                    ? ["-i", videoPath, "-i", audioPath, "-filter_complex", "[0:a][1:a]amix=inputs=2:duration=first[a]", "-map", "0:v", "-map", "[a]", "-c:v", "copy", "-c:a", "aac", "-y", outputPath]
                    : ["-i", videoPath, "-i", audioPath, "-map", "0:v", "-map", "1:a", "-c:v", "copy", "-c:a", "aac", "-shortest", "-y", outputPath];

                string result = await RunFfmpeg(ffmpeg, args);
                Logs.Debug($"[AudioLab] ffmpeg combine result: {result}");

                if (!File.Exists(outputPath))
                {
                    return AudioLab.CreateErrorResponse($"ffmpeg failed to produce output: {result}", "ffmpeg_error");
                }

                byte[] outputBytes = await File.ReadAllBytesAsync(outputPath);
                string outputBase64 = Convert.ToBase64String(outputBytes);

                return new JObject
                {
                    ["success"] = true,
                    ["video_data"] = outputBase64,
                    ["size_bytes"] = outputBytes.Length,
                    ["mode"] = mode
                };
            }
            finally
            {
                TryDelete(videoPath);
                TryDelete(audioPath);
                TryDelete(outputPath);
            }
        }
        catch (Exception ex)
        {
            return AudioLab.CreateErrorResponse("Video+audio combining failed", "combine_error", ex);
        }
    }

    /// <summary>Extract the audio track from a video file.
    /// Input: video_data (base64). Returns: audio as base64 WAV.</summary>
    public static async Task<JObject> ExtractAudioFromVideo(Session session, JObject input)
    {
        string ffmpeg = Utilities.FfmegLocation.Value;
        if (string.IsNullOrEmpty(ffmpeg))
        {
            return AudioLab.CreateErrorResponse("ffmpeg not found. Install ffmpeg and ensure it is in your PATH.", "ffmpeg_not_found");
        }

        try
        {
            string videoData = input["video_data"]?.ToString();
            if (string.IsNullOrEmpty(videoData))
                return AudioLab.CreateErrorResponse("video_data is required", "missing_video");

            long videoBytes = (videoData.Length * 3) / 4;
            if (videoBytes > MaxVideoSizeMB * 1024 * 1024)
                return AudioLab.CreateErrorResponse($"Video too large (max {MaxVideoSizeMB}MB)", "video_too_large");

            string tempDir = Path.Combine(Path.GetTempPath(), "audiolab_video");
            Directory.CreateDirectory(tempDir);
            string videoPath = Path.Combine(tempDir, $"video_{Guid.NewGuid():N}.mp4");
            string audioPath = Path.Combine(tempDir, $"extracted_{Guid.NewGuid():N}.wav");

            try
            {
                await File.WriteAllBytesAsync(videoPath, Convert.FromBase64String(videoData));

                string[] args = ["-i", videoPath, "-vn", "-acodec", "pcm_s16le", "-ar", "44100", "-ac", "2", "-y", audioPath];
                string result = await RunFfmpeg(ffmpeg, args);
                Logs.Debug($"[AudioLab] ffmpeg extract result: {result}");

                if (!File.Exists(audioPath))
                {
                    return AudioLab.CreateErrorResponse($"ffmpeg failed to extract audio: {result}", "ffmpeg_error");
                }

                byte[] audioBytes = await File.ReadAllBytesAsync(audioPath);
                string audioBase64 = Convert.ToBase64String(audioBytes);

                return new JObject
                {
                    ["success"] = true,
                    ["audio_data"] = audioBase64,
                    ["size_bytes"] = audioBytes.Length
                };
            }
            finally
            {
                TryDelete(videoPath);
                TryDelete(audioPath);
            }
        }
        catch (Exception ex)
        {
            return AudioLab.CreateErrorResponse("Audio extraction failed", "extract_error", ex);
        }
    }

    private static async Task<string> RunFfmpeg(string ffmpegPath, string[] args)
    {
        ProcessStartInfo start = new(ffmpegPath, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using Process p = Process.Start(start);
        Task<string> stdOut = p.StandardOutput.ReadToEndAsync();
        Task<string> stdErr = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync(Program.GlobalProgramCancel);
        string output = await stdOut;
        string error = await stdErr;
        return string.IsNullOrWhiteSpace(error) ? output : $"{output}\n{error}";
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort cleanup */ }
    }
}
