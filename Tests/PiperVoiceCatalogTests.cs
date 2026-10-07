using System.Text.RegularExpressions;
using Hartsy.Extensions.AudioLab;
using Xunit;

namespace Hartsy.Extensions.AudioLab.Tests;

/// <summary>Unit tests for <see cref="AudioLabParams.PiperVoices"/> -- the "any listed voice must resolve to
/// the real rhasspy/piper-voices file layout" half of the Piper fix. The Engine's own path builder
/// (<c>PiperPipeline.VoiceRepoPath</c>, not reachable from here -- it's `private` in a different package) is:
///
/// <code>
/// // "en_US-lessac-medium" -&gt; "en/en_US/lessac/medium/en_US-lessac-medium"
/// string[] dash = voiceId.Split('-');
/// string locale = dash[0];            // en_US
/// string lang = locale.Split('_')[0]; // en
/// string name = dash[1];              // lessac
/// string quality = dash[2];           // medium
/// return $"{lang}/{locale}/{name}/{quality}/{voiceId}";
/// </code>
///
/// which only produces a real path when the id has exactly 3 dash-separated parts and the first is a
/// "ll_RR" locale. These tests mirror that formula against every voice AudioLab's UI offers, so a typo or a
/// future addition that breaks the shape fails here instead of 404ing against HuggingFace on a live
/// install.</summary>
public class PiperVoiceCatalogTests
{
    private static readonly Regex LocalePattern = new(@"^[a-z]{2}_[A-Z]{2}$");

    /// <summary>Mirrors the Engine's <c>PiperPipeline.VoiceRepoPath</c> exactly (see class doc) so these tests
    /// assert the real contract, not a paraphrase of it.</summary>
    private static string EngineVoiceRepoPath(string voiceId)
    {
        string[] dash = voiceId.Split('-');
        Assert.Equal(3, dash.Length); // the formula below is only meaningful once this holds
        string locale = dash[0];
        string lang = locale.Split('_')[0];
        string name = dash[1];
        string quality = dash[2];
        return $"{lang}/{locale}/{name}/{quality}/{voiceId}";
    }

    [Fact]
    public void PiperVoices_HasAllThirty_SevenDocumentedVoices()
    {
        // The param's own description says "All 37 English voices from the official VOICES.md are listed" --
        // this is the number that claim promises.
        Assert.Equal(37, AudioLabParams.PiperVoices.Length);
    }

    [Fact]
    public void PiperVoices_NoDuplicateIds()
    {
        string[] ids = [.. AudioLabParams.PiperVoices.Select(v => v.Id)];
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [MemberData(nameof(AllVoiceIds))]
    public void PiperVoices_EachId_HasExactlyThreeDashParts(string voiceId)
    {
        Assert.Equal(3, voiceId.Split('-').Length);
    }

    [Theory]
    [MemberData(nameof(AllVoiceIds))]
    public void PiperVoices_EachId_HasAnLlUnderscoreRrLocale(string voiceId)
    {
        string locale = voiceId.Split('-')[0];
        Assert.Matches(LocalePattern, locale);
    }

    [Theory]
    [MemberData(nameof(AllVoiceIds))]
    public void PiperVoices_EachId_ResolvesToTheRealRepoLayout(string voiceId)
    {
        string path = EngineVoiceRepoPath(voiceId);
        Assert.Matches(@"^[a-z]{2}/[a-z]{2}_[A-Z]{2}/[^/]+/[^/]+/[a-z]{2}_[A-Z]{2}-[^/]+-[^/]+$", path);
        Assert.EndsWith(voiceId, path, StringComparison.Ordinal);
    }

    [Fact]
    public void PiperVoices_KnownVoice_MatchesTheExactExpectedPath()
    {
        // One concrete example pinned byte for byte, so a change to the formula above (not just the data)
        // is caught even if every id still happens to be well-formed.
        Assert.Equal("en/en_US/amy/low/en_US-amy-low", EngineVoiceRepoPath("en_US-amy-low"));
        Assert.Equal("en/en_GB/northern_english_male/medium/en_GB-northern_english_male-medium",
            EngineVoiceRepoPath("en_GB-northern_english_male-medium"));
    }

    [Fact]
    public void PiperVoices_EngineDefaultVoice_IsInTheListedCatalog()
    {
        // AudioConfiguration.DefaultPiperVoice ("en_US-lessac-medium") is what an unparameterised request now
        // gets; it must be a real, listed voice, not just a plausible-looking string.
        Assert.Contains("en_US-lessac-medium", AudioLabParams.PiperVoices.Select(v => v.Id));
    }

    public static IEnumerable<object[]> AllVoiceIds() =>
        AudioLabParams.PiperVoices.Select(v => new object[] { v.Id });
}
