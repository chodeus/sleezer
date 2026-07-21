using NzbDrone.Core.Download.History;
using NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek;
using NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek.Models;
using NzbDrone.Plugin.Sleezer.Indexers.Soulseek;
using Xunit;

namespace Sleezer.Tests;

// Covers the follow-up fixes from the 2026-07-21 live verification of v1.8.0:
// remix qualifiers falsely matching their originals, and failed re-grabs
// reusing a downloadId that Lidarr's tracked-download cache has already
// poisoned (completed retries were never imported).
public class RemixSignatureTests
{
    [Theory]
    [InlineData("Never Say Never", null)]
    [InlineData("Never Say Never (Colyn Remix)", "colyn")]
    [InlineData("Never Say Never - Colyn Remix", "colyn")]
    [InlineData("Lethal Industry (Richard Durand Remixes)", "richard durand")]
    [InlineData("Lets Get Lost Remixes", "")]                        // generic: keyword outside brackets
    [InlineData("OK Computer (Deluxe Edition)", null)]               // \bedit\b must not match "Edition"
    [InlineData("Nevermind (Remastered)", null)]
    [InlineData("Love Who You Love (Radio Edit)", "radio")]
    [InlineData("Album (VIP)", "")]
    [InlineData("Album (2014) [FLAC]", null)]
    public void ExtractRemixSignature_identifies_remix_qualifiers(string title, string? expected)
    {
        Assert.Equal(expected, SlskdTextProcessor.ExtractRemixSignature(title));
    }

    [Theory]
    [InlineData("Never Say Never", @"Armin van Buuren - Never Say Never (Colyn Remix)", true)]
    [InlineData("Never Say Never (Colyn Remix)", @"Armin van Buuren - Never Say Never", true)]
    [InlineData("Never Say Never (Colyn Remix)", @"Never Say Never (Colyn Remix)", false)]
    [InlineData("Never Say Never (Colyn Remix)", @"Never Say Never (KIKI Remix)", true)]
    [InlineData("Never Say Never", @"Armin van Buuren - Never Say Never", false)]
    [InlineData("Lets Get Lost Remixes", @"G-Eazy - Lets Get Lost Remixes", false)]
    [InlineData("Lets Get Lost Remixes", @"G-Eazy - Lets Get Lost (Remixes)", false)]
    [InlineData("Minutes to Midnight", @"Linkin Park - Minutes To Midnight (Explicit)", false)]
    [InlineData("When Its Dark Out", @"When It's Dark Out (deluxe edition)", false)]
    [InlineData("Me, Myself & I", "G‐Eazy - Single - 2016 - Me Myself and I Marc Stout and Scott Svejda remix", true)]
    public void RemixSignaturesConflict_separates_remixes_from_originals(string searchAlbum, string folder, bool expected)
    {
        Assert.Equal(expected, SlskdTextProcessor.RemixSignaturesConflict(searchAlbum, folder));
    }
}

public class RetryDownloadIdTests
{
    [Fact]
    public void ResolveRetryId_keeps_base_id_without_failed_history()
    {
        string id = SlskdDownloadItem.ResolveRetryId("abc123", _ => null);
        Assert.Equal("abc123", id);
    }

    [Fact]
    public void ResolveRetryId_keeps_base_id_when_latest_event_is_a_grab()
    {
        string id = SlskdDownloadItem.ResolveRetryId("abc123", _ => DownloadHistoryEventType.DownloadGrabbed);
        Assert.Equal("abc123", id);
    }

    [Fact]
    public void ResolveRetryId_suffixes_after_a_failure()
    {
        string id = SlskdDownloadItem.ResolveRetryId(
            "abc123",
            queried => queried == "abc123" ? DownloadHistoryEventType.DownloadFailed : null);
        Assert.Equal("abc123-r2", id);
    }

    [Fact]
    public void ResolveRetryId_walks_past_failed_retries()
    {
        string id = SlskdDownloadItem.ResolveRetryId(
            "abc123",
            queried => queried is "abc123" or "abc123-r2" ? DownloadHistoryEventType.DownloadFailed : null);
        Assert.Equal("abc123-r3", id);
    }

    [Fact]
    public void ResolveRetryId_caps_the_suffix()
    {
        string id = SlskdDownloadItem.ResolveRetryId("abc123", _ => DownloadHistoryEventType.DownloadFailed);
        Assert.Equal("abc123-r9", id);
    }

    [Theory]
    [InlineData("abc123-r2", "abc123")]
    [InlineData("abc123-r9", "abc123")]
    [InlineData("abc123", "abc123")]
    [InlineData("abc-rock", "abc-rock")]      // non-numeric tail is not a retry suffix
    [InlineData("abc123-r", "abc123-r")]
    public void StripRetrySuffix_recovers_the_content_hash(string input, string expected)
    {
        Assert.Equal(expected, SlskdDownloadItem.StripRetrySuffix(input));
    }
}
