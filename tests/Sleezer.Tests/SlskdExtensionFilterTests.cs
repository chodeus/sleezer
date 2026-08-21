using NzbDrone.Plugin.Sleezer.Indexers.Soulseek;
using Xunit;

namespace Sleezer.Tests;

// IncludeFileExtensions is stored dotless, but peers report ".log" with the dot
// and the filename fallback always yields ".cue" — both used to miss the
// whitelist and get dropped silently, so cue/log could never be downloaded.
public class SlskdExtensionFilterTests
{
    private static SlskdFileData F(string filename, string? extension) => new(
        Filename: filename, BitRate: null, BitDepth: null, Size: 1000, Length: null,
        Extension: extension, SampleRate: null, Code: 1, IsLocked: false);

    private static List<string> Filter(List<SlskdFileData> files, bool onlyAudio, params string[] whitelist) =>
        SlskdFileData.GetFilteredFiles(files, onlyAudio, whitelist).Select(f => f.Filename!).ToList();

    [Fact]
    public void A_dotless_extension_attribute_matches_the_dotless_whitelist()
    {
        Assert.Single(Filter([F(@"a\b\rip.cue", "cue")], true, "cue"));
    }

    // Locks the dot-normalization fix: without it Path.GetExtension's ".cue"
    // never matches the stored "cue".
    [Fact]
    public void A_missing_extension_attribute_falls_back_to_the_dotted_filename_extension()
    {
        Assert.Single(Filter([F(@"a\b\rip.cue", null)], true, "cue"));
    }

    // Locks the dot-normalization fix: some clients report the extension dotted.
    [Fact]
    public void A_dotted_extension_attribute_still_matches_the_dotless_whitelist()
    {
        Assert.Single(Filter([F(@"a\b\rip.log", ".log")], true, "log"));
    }

    [Fact]
    public void A_non_whitelisted_extra_is_still_excluded()
    {
        Assert.Empty(Filter([F(@"a\b\folder.nfo", null)], true, "cue", "log"));
    }

    [Fact]
    public void Audio_is_always_included_with_an_empty_whitelist()
    {
        Assert.Single(Filter([F(@"a\b\01 - Track.flac", null)], true));
    }

    [Fact]
    public void Nothing_is_filtered_when_audio_only_is_off()
    {
        List<SlskdFileData> files = [F(@"a\b\01.flac", "flac"), F(@"a\b\rip.cue", null), F(@"a\b\folder.nfo", null)];

        Assert.Equal(3, Filter(files, false).Count);
    }
}
