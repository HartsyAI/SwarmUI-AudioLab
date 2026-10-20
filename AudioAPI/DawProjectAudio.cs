using System.IO;
using System.Text.RegularExpressions;

namespace Hartsy.Extensions.AudioLab.AudioAPI;

/// <summary>A DAW project's audio, one file per clip beside the project's JSON instead of base64 inside it, so a project
/// is not bounded by the size of one request and a save uploads only the audio the server does not have yet.</summary>
public static class DawProjectAudio
{
    /// <summary>Largest single audio file accepted, decoded (it travels as base64, about 1.33x, under core's request limit).</summary>
    public const long MaxFileBytes = 140L * 1024 * 1024;

    /// <summary>Largest total audio one project may hold.</summary>
    public const long MaxProjectBytes = 4L * 1024 * 1024 * 1024;

    /// <summary>Audio keys are the DAW's blob keys: plain ASCII, safe as file names.</summary>
    private static readonly Regex KeyPattern = new("^[A-Za-z0-9_-]{1,128}$", RegexOptions.Compiled);

    /// <summary>The key when it is safe to use as a file name, else null.</summary>
    public static string SafeKey(string key)
    {
        return key is not null && KeyPattern.IsMatch(key) ? key : null;
    }

    /// <summary>A project's audio folder, beside its JSON file.</summary>
    public static string AudioDir(string projectDir, string stem)
    {
        return Path.Combine(projectDir, $"{stem}.audio");
    }

    /// <summary>Path of one audio file (the key must already be safe).</summary>
    public static string AudioPath(string audioDir, string key)
    {
        return Path.Combine(audioDir, $"{key}.bin");
    }

    /// <summary>Key to size of every audio file the project holds.</summary>
    public static Dictionary<string, long> List(string audioDir)
    {
        Dictionary<string, long> files = [];
        if (!Directory.Exists(audioDir))
        {
            return files;
        }
        foreach (string path in Directory.EnumerateFiles(audioDir, "*.bin"))
        {
            files[Path.GetFileNameWithoutExtension(path)] = new FileInfo(path).Length;
        }
        return files;
    }

    /// <summary>Writes one audio file via temp-then-rename, so a crash never leaves a half-written file under its key.</summary>
    public static async Task Write(string audioDir, string key, byte[] data)
    {
        Directory.CreateDirectory(audioDir);
        string path = AudioPath(audioDir, key);
        string tmp = $"{path}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllBytesAsync(tmp, data);
        File.Move(tmp, path, true);
    }

    /// <summary>Reads one audio file, or null when the project has none under that key.</summary>
    public static async Task<byte[]> Read(string audioDir, string key)
    {
        string path = AudioPath(audioDir, key);
        return File.Exists(path) ? await File.ReadAllBytesAsync(path) : null;
    }

    /// <summary>Deletes every audio file whose key is not in keep, plus temp files left by a write that crashed (an hour
    /// old, so another tab's upload in progress is never touched). Returns how many went.</summary>
    public static int Prune(string audioDir, ISet<string> keep)
    {
        if (!Directory.Exists(audioDir))
        {
            return 0;
        }
        int removed = 0;
        foreach (string path in Directory.EnumerateFiles(audioDir))
        {
            if (path.EndsWith(".bin", StringComparison.Ordinal))
            {
                if (keep.Contains(Path.GetFileNameWithoutExtension(path)))
                {
                    continue;
                }
            }
            else if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TimeSpan.FromHours(1))
            {
                continue;
            }
            File.Delete(path);
            removed++;
        }
        return removed;
    }

    /// <summary>Removes a project's audio folder.</summary>
    public static void DeleteAll(string audioDir)
    {
        if (Directory.Exists(audioDir))
        {
            Directory.Delete(audioDir, true);
        }
    }
}
