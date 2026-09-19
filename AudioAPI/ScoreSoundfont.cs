using System.Net.Http;
using Hartsy.Extensions.AudioLab.AudioServices;
using SwarmUI.Utils;
using System.IO;

namespace Hartsy.Extensions.AudioLab.AudioAPI;

/// <summary>The piano samples the Score tab's audition plays.
///
/// <para>abcjs asks for one mp3 per sounding pitch at <c>soundFontUrl + instrument + "-mp3/" + note + ".mp3"</c>
/// and defaults that base to a GitHub Pages host, so auditioning a plan needs the internet on every machine
/// that has not cached them yet. Fetching the 88 files of the one instrument our scores use (both YuE2 voices
/// and the chord comping are program 0) costs about 7 MB once and makes the tab work offline.</para>
///
/// <para>They are not committed: they are redistributable samples with their own upstream licence, and this is
/// a code repository. The download folder is gitignored and every note path is registered as an extension
/// asset at startup, so the files are served the moment they land — the lazy getters read from disk on
/// request, which is why no restart is needed after a fetch.</para></summary>
public static class ScoreSoundfont
{
    /// <summary>The abcjs soundfont set, which is what its own default URL points at.</summary>
    public const string RemoteBase = "https://paulrosen.github.io/midi-js-soundfonts/abcjs/";

    /// <summary>Program 0. Every other instrument is a further 7 MB for something no YuE2 score asks for.</summary>
    public const string Instrument = "acoustic_grand_piano";

    /// <summary>Path under the extension folder, which is also the URL path after /ExtensionFile/SwarmUI-AudioLab/.</summary>
    public const string AssetPrefix = "Assets/soundfont/" + Instrument + "-mp3/";

    private static readonly string[] PitchClasses = ["C", "Db", "D", "Eb", "E", "F", "Gb", "G", "Ab", "A", "Bb", "B"];

    /// <summary>A0 to C8 — the 88 keys of a piano, spelled the way abcjs names its sample files.</summary>
    public static readonly string[] NoteNames = [.. Enumerable.Range(21, 88).Select(m => $"{PitchClasses[m % 12]}{m / 12 - 1}")];

    /// <summary>Asset paths for every note, for <c>OtherAssets</c> registration.</summary>
    public static IEnumerable<string> AssetPaths => NoteNames.Select(n => $"{AssetPrefix}{n}.mp3");

    private static readonly SemaphoreSlim FetchLock = new(1, 1);

    public static string Directory => Path.Combine(AudioConfiguration.ExtensionDirectory, "Assets", "soundfont", $"{Instrument}-mp3");

    /// <summary>How many notes are already on disk. A truncated file is not a note.</summary>
    public static int InstalledCount()
    {
        string dir = Directory;
        if (!System.IO.Directory.Exists(dir))
        {
            return 0;
        }
        int found = 0;
        foreach (string note in NoteNames)
        {
            FileInfo info = new(Path.Combine(dir, $"{note}.mp3"));
            if (info.Exists && info.Length > 1024)
            {
                found++;
            }
        }
        return found;
    }

    /// <summary>Downloads whatever is missing. Returns (fetched, failed note names).</summary>
    public static async Task<(int Fetched, List<string> Failed)> FetchAsync()
    {
        await FetchLock.WaitAsync();
        try
        {
            string dir = Directory;
            System.IO.Directory.CreateDirectory(dir);
            List<string> failed = [];
            int fetched = 0;
            SemaphoreSlim gate = new(8, 8);
            async Task One(string note)
            {
                string target = Path.Combine(dir, $"{note}.mp3");
                FileInfo have = new(target);
                if (have.Exists && have.Length > 1024)
                {
                    return;
                }
                await gate.WaitAsync();
                try
                {
                    using HttpResponseMessage response = await Utilities.UtilWebClient.GetAsync($"{RemoteBase}{Instrument}-mp3/{note}.mp3");
                    response.EnsureSuccessStatusCode();
                    byte[] data = await response.Content.ReadAsByteArrayAsync();
                    if (data.Length < 1024)
                    {
                        throw new IOException($"{data.Length} bytes is not a sample");
                    }
                    // Written aside and moved, so a half-written file can never be counted as an installed note.
                    string temp = $"{target}.tmp";
                    await File.WriteAllBytesAsync(temp, data);
                    File.Move(temp, target, true);
                    Interlocked.Increment(ref fetched);
                }
                catch (Exception ex)
                {
                    Logs.Warning($"[AudioLab] Soundfont note {note} failed: {ex.Message}");
                    lock (failed)
                    {
                        failed.Add(note);
                    }
                }
                finally
                {
                    gate.Release();
                }
            }
            await Task.WhenAll(NoteNames.Select(One));
            return (fetched, failed);
        }
        finally
        {
            FetchLock.Release();
        }
    }
}
