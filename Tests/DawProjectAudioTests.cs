using Hartsy.Extensions.AudioLab.AudioAPI;
using Xunit;

namespace Hartsy.Extensions.AudioLab.Tests;

/// <summary>The DAW project audio store: keys never escape the project folder, files round-trip, a save prunes what
/// the project no longer references, and a delete takes the folder with it.</summary>
public class DawProjectAudioTests : IDisposable
{
    private readonly string Root = Path.Combine(Path.GetTempPath(), "audiolab-daw-audio-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, true);
        }
    }

    [Theory]
    [InlineData("blob-1792491045-ab_c")]
    [InlineData("A")]
    public void SafeKeyAcceptsBlobKeys(string key)
    {
        Assert.Equal(key, DawProjectAudio.SafeKey(key));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a b")]
    [InlineData("key.bin")]
    public void SafeKeyRejectsPathsAndOtherCharacters(string key)
    {
        Assert.Null(DawProjectAudio.SafeKey(key));
    }

    [Fact]
    public void SafeKeyRejectsOverlongKeys()
    {
        Assert.Null(DawProjectAudio.SafeKey(new string('a', 129)));
        Assert.NotNull(DawProjectAudio.SafeKey(new string('a', 128)));
    }

    [Fact]
    public async Task WriteReadAndListRoundTrip()
    {
        string dir = DawProjectAudio.AudioDir(Root, "song");
        byte[] a = [1, 2, 3];
        byte[] b = new byte[1000];
        await DawProjectAudio.Write(dir, "a", a);
        await DawProjectAudio.Write(dir, "b", b);
        Assert.Equal(a, await DawProjectAudio.Read(dir, "a"));
        Dictionary<string, long> list = DawProjectAudio.List(dir);
        Assert.Equal(2, list.Count);
        Assert.Equal(3, list["a"]);
        Assert.Equal(1000, list["b"]);
        Assert.Null(await DawProjectAudio.Read(dir, "missing"));
    }

    [Fact]
    public async Task WritingAKeyAgainReplacesItsAudio()
    {
        string dir = DawProjectAudio.AudioDir(Root, "song");
        await DawProjectAudio.Write(dir, "a", [1]);
        await DawProjectAudio.Write(dir, "a", [2, 2]);
        Assert.Equal(new byte[] { 2, 2 }, await DawProjectAudio.Read(dir, "a"));
        Assert.Single(DawProjectAudio.List(dir));
    }

    [Fact]
    public async Task PruneKeepsOnlyReferencedAudio()
    {
        string dir = DawProjectAudio.AudioDir(Root, "song");
        foreach (string key in new[] { "a", "b", "c" })
        {
            await DawProjectAudio.Write(dir, key, [9]);
        }
        // Unreferenced audio is only pruned once it is an hour old
        File.SetLastWriteTimeUtc(DawProjectAudio.AudioPath(dir, "b"), DateTime.UtcNow.AddHours(-2));
        Assert.Equal(1, DawProjectAudio.Prune(dir, new HashSet<string> { "a", "c" }));
        Assert.Equal(new[] { "a", "c" }, DawProjectAudio.List(dir).Keys.Order().ToArray());
    }

    [Fact]
    public async Task PruneKeepsAFreshUploadThatNoSaveReferencesYet()
    {
        string dir = DawProjectAudio.AudioDir(Root, "song");
        await DawProjectAudio.Write(dir, "fresh", [1]);
        Assert.Equal(0, DawProjectAudio.Prune(dir, new HashSet<string>()));
        Assert.Single(DawProjectAudio.List(dir));
        File.SetLastWriteTimeUtc(DawProjectAudio.AudioPath(dir, "fresh"), DateTime.UtcNow.AddHours(-2));
        Assert.Equal(1, DawProjectAudio.Prune(dir, new HashSet<string>()));
        Assert.Empty(DawProjectAudio.List(dir));
    }

    [Fact]
    public async Task PruneLeavesAnUploadInProgressAlone()
    {
        string dir = DawProjectAudio.AudioDir(Root, "song");
        await DawProjectAudio.Write(dir, "a", [1]);
        string tmp = Path.Combine(dir, "b.bin.0123.tmp");
        await File.WriteAllBytesAsync(tmp, [1]);
        DawProjectAudio.Prune(dir, new HashSet<string> { "a" });
        Assert.True(File.Exists(tmp));
        // A temp file an hour old is a crashed write, and goes
        File.SetLastWriteTimeUtc(tmp, DateTime.UtcNow.AddHours(-2));
        DawProjectAudio.Prune(dir, new HashSet<string> { "a" });
        Assert.False(File.Exists(tmp));
        Assert.Single(DawProjectAudio.List(dir));
    }

    [Fact]
    public async Task DeleteAllRemovesTheFolder()
    {
        string dir = DawProjectAudio.AudioDir(Root, "song");
        await DawProjectAudio.Write(dir, "a", [1]);
        DawProjectAudio.DeleteAll(dir);
        Assert.False(Directory.Exists(dir));
        DawProjectAudio.DeleteAll(dir);
    }

    [Fact]
    public void AMissingFolderListsNothing()
    {
        Assert.Empty(DawProjectAudio.List(Path.Combine(Root, "nope.audio")));
        Assert.Equal(0, DawProjectAudio.Prune(Path.Combine(Root, "nope.audio"), new HashSet<string>()));
    }

    [Fact]
    public async Task MissingKeysNamesOnlyTheAudioTheFolderLacks()
    {
        string dir = DawProjectAudio.AudioDir(Root, "song");
        await DawProjectAudio.Write(dir, "a", [1]);
        Assert.Empty(DawProjectAudio.MissingKeys(dir, ["a"]));
        Assert.Equal(new[] { "b" }, DawProjectAudio.MissingKeys(dir, ["a", "b"]).ToArray());
    }

    [Fact]
    public void MissingKeysCountsEveryKeyWhenTheFolderIsAbsent()
    {
        Assert.Equal(new[] { "a", "b" }, DawProjectAudio.MissingKeys(Path.Combine(Root, "nope.audio"), ["a", "b"]).ToArray());
    }

    [Fact]
    public async Task MissingKeysTreatsAnUnsafeKeyAsMissing()
    {
        string dir = DawProjectAudio.AudioDir(Root, "song");
        await DawProjectAudio.Write(dir, "a", [1]);
        Assert.Equal(new[] { "../a" }, DawProjectAudio.MissingKeys(dir, ["../a"]).ToArray());
    }

    [Fact]
    public void TheAudioFolderSitsBesideTheProjectFile()
    {
        Assert.Equal(Path.Combine(Root, "song.audio"), DawProjectAudio.AudioDir(Root, "song"));
        Assert.Equal(Path.Combine(Root, "song.audio", "k.bin"), DawProjectAudio.AudioPath(Path.Combine(Root, "song.audio"), "k"));
    }
}
