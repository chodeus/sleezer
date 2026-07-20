using NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek;
using NzbDrone.Plugin.Sleezer.Indexers.Soulseek;
using Xunit;

namespace Sleezer.Tests;

// Covers the TypNull/Tubifarry ae3ce28 + d857a0f ports: Soulseek-server
// blocked-term handling and slskd destination subdirectory resolution.
public class BlockedTermTests
{
    [Theory]
    [InlineData("Adele 25", true)]
    [InlineData("Kendrick Lamar DAMN", true)]
    [InlineData("Radiohead OK Computer", false)]
    public void ContainsBlockedTerms_detects_server_filtered_terms(string query, bool expected)
    {
        Assert.Equal(expected, SlskdTextProcessor.ContainsBlockedTerms(query));
    }

    [Fact]
    public void RemoveBlockedTerms_strips_multi_word_terms()
    {
        Assert.Equal("DAMN", SlskdTextProcessor.RemoveBlockedTerms("Kendrick Lamar DAMN"));
    }

    [Fact]
    public void RewriteRestrictedTerms_injects_accent_that_evades_the_filter()
    {
        string rewritten = SlskdTextProcessor.RewriteRestrictedTerms("Adele 25");

        Assert.NotEqual("Adele 25", rewritten);
        Assert.False(SlskdTextProcessor.ContainsBlockedTerms(rewritten));
        Assert.EndsWith(" 25", rewritten);
    }

    [Fact]
    public void GetBlockedTermEvidenceTracks_prefers_longest_clean_titles()
    {
        string[] tracks = ["Hello", "Someone Like You", "25"];
        List<string> evidence = SlskdTextProcessor.GetBlockedTermEvidenceTracks(tracks, "25").ToList();

        Assert.Equal("Someone Like You", evidence[0]);
        Assert.DoesNotContain("25", evidence);
    }
}

public class SlskdPathResolverTests
{
    private static readonly SlskdDestinationConfig DefaultConfig = new("/downloads", null);

    [Fact]
    public void Default_pattern_resolves_to_source_directory()
    {
        string? result = SlskdPathResolver.ResolveSubdirectory(DefaultConfig, "peer", @"@@abc12\Artist\Album\01.flac");
        Assert.Equal("Album", result);
    }

    [Fact]
    public void Custom_pattern_substitutes_tokens()
    {
        SlskdDestinationConfig config = new("/downloads", "${SOURCE_USERNAME}/${SOURCE_DIRECTORY}");
        string? result = SlskdPathResolver.ResolveSubdirectory(config, "peer", @"music\Artist\Album\01.flac");
        Assert.Equal("peer/Album", result);
    }

    [Fact]
    public void Traversal_segments_in_resolved_path_are_rejected()
    {
        SlskdDestinationConfig config = new("/downloads", "../${SOURCE_DIRECTORY}");
        Assert.Null(SlskdPathResolver.ResolveSubdirectory(config, "peer", @"music\Album\01.flac"));
    }

    [Theory]
    [InlineData("/downloads", "/downloads/Album", "Album")]
    [InlineData("/downloads", "/downloads/peer/Album", "peer/Album")]
    [InlineData("/downloads", "/elsewhere/Album", null)]
    [InlineData("/downloads", "/downloads", "")]
    public void MakeRelativeToDownloads_handles_containment(string root, string path, string? expected)
    {
        Assert.Equal(expected, SlskdPathResolver.MakeRelativeToDownloads(root, path));
    }
}
