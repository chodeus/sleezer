using NzbDrone.Core.Download.History;
using NzbDrone.Plugin.Sleezer.Core.PostProcessing;
using NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek;
using NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek.Models;
using NzbDrone.Plugin.Sleezer.Indexers.Soulseek;
using Xunit;
using NzbDrone.Plugin.Sleezer.Core.Utilities;

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
    [InlineData("Album (Instrumental)", "")]
    [InlineData("Album (2014) [FLAC]", null)]
    public void ExtractRemixSignature_identifies_remix_qualifiers(string title, string? expected)
    {
        Assert.Equal(expected, VariantQualifiers.ExtractRemixSignature(title));
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
    [InlineData("Undaground Legend", @"Lil Flip - Undaground Legend (2002) [FLAC]", false)]     // keyword in ARTIST name is not a qualifier
    [InlineData("Me, Myself & I", "Me, Myself & I (Sped Up)", true)]
    [InlineData("The Documentary", "The Game - The Documentary (Instrumentals)", true)]
    [InlineData("Song", "Song (A Cappella)", true)]
    public void RemixSignaturesConflict_separates_remixes_from_originals(string searchAlbum, string folder, bool expected)
    {
        Assert.Equal(expected, VariantQualifiers.RemixSignaturesConflict(searchAlbum, folder));
    }

    [Theory]
    [InlineData("Apple Music Live: Fred again..", "Fred again.. - Apple Music Live", new[] { "Live" }, false)]  // metadata forgives the hidden qualifier
    [InlineData("Alive 2007", "Daft Punk - Alive 2007 (Live)", new[] { "Live" }, false)]
    [InlineData("Nevermind", "Nevermind (Live)", new string[0], true)]                                          // no metadata: studio target stays strict
    [InlineData("Definitely Maybe (Live)", "Definitely Maybe", new[] { "Live" }, true)]                         // explicit live target still refuses studio
    [InlineData("Bass, Beats & Melody Reloaded!", "Brooklyn Bounce - Bass, Beats & Melody Reloaded", new[] { "Remix" }, false)]  // live FP 2026-07-31: Remix type + plain folder must not conflict
    [InlineData("Canda! (The Darkside Returns)", "Brooklyn Bounce - Canda! {The Darkside Returns} [4040217013451]", new[] { "Remix" }, false)]
    [InlineData("Some Album", "Some Album (Remixes)", new[] { "Remix" }, false)]                                // Remix type still forgives remix text
    [InlineData("Some Album", "Some Album (Marc Stout Remix)", new[] { "Remix" }, false)]                       // named remixer forgiven too
    [InlineData("Some Album", "Some Album (VIP)", new[] { "Remix" }, false)]                                    // VIP is remix-family
    [InlineData("Some Album", "Some Album (Instrumental)", new[] { "Remix" }, true)]                            // Remix type must not admit other variants
    [InlineData("Some Album", "Some Album (Radio Edit)", new[] { "Remix" }, true)]
    [InlineData("Some Album", "Some Album (Sped Up)", new[] { "Remix" }, true)]
    [InlineData("Some Album", "Some Album (Remix Radio Edit)", new[] { "Remix" }, true)]                        // mixed qualifiers stay conflicting
    [InlineData("Some Album", "Some Album (VIP Instrumental)", new[] { "Remix" }, true)]
    [InlineData("Some Album", "Some Album (Remixes)", new string[0], true)]                                     // no metadata: remix candidate stays refused
    public void RemixSignaturesConflict_uses_secondary_types_asymmetrically(string searchAlbum, string folder, string[] types, bool expected)
    {
        Assert.Equal(expected, VariantQualifiers.RemixSignaturesConflict(searchAlbum, folder, types));
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
        Assert.Equal("abc123-r99", id);
    }

    [Theory]
    [InlineData(DownloadHistoryEventType.DownloadImported)]
    [InlineData(DownloadHistoryEventType.DownloadIgnored)]
    [InlineData(DownloadHistoryEventType.DownloadImportIncomplete)]
    public void ResolveRetryId_salts_past_any_terminal_event(DownloadHistoryEventType terminal)
    {
        string id = SlskdDownloadItem.ResolveRetryId(
            "abc123",
            queried => queried == "abc123" ? terminal : null);
        Assert.Equal("abc123-r2", id);
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

public class TrackTitleMatcherTests
{
    [Fact]
    public void Match_maps_a_single_pulled_from_an_album_share()
    {
        Dictionary<int, int> mapping = TrackTitleMatcher.Match(["I Luv U"], ["I Luv U"]);
        Assert.Equal(0, Assert.Single(mapping).Value);
    }

    [Fact]
    public void Match_excludes_remix_files_but_keeps_the_original()
    {
        Dictionary<int, int> mapping = TrackTitleMatcher.Match(["solo", "solo (KETTAMA remix)"], ["solo"]);
        Assert.Single(mapping);
        Assert.Equal(0, mapping[0]);
    }

    [Fact]
    public void Match_rejects_instrumental_variants_of_the_wanted_track()
    {
        Assert.Empty(TrackTitleMatcher.Match(["Me Myself and I Instrumental"], ["Me, Myself & I"]));
    }

    [Fact]
    public void Match_rejects_unrelated_titles()
    {
        Assert.Empty(TrackTitleMatcher.Match(["Lethal Industry"], ["I Luv U"]));
    }

    [Fact]
    public void Match_assigns_each_wanted_track_once()
    {
        Dictionary<int, int> mapping = TrackTitleMatcher.Match(["scared", "scared"], ["scared"]);
        Assert.Single(mapping);
    }

    [Fact]
    public void Match_prefers_the_best_score_regardless_of_file_order()
    {
        Dictionary<int, int> mapping = TrackTitleMatcher.Match(["scarred", "scared"], ["scared"]);
        Assert.Single(mapping);
        Assert.Equal(0, mapping[1]);
    }

    [Fact]
    public void Match_prefers_exact_over_subset_at_saturated_scores()
    {
        Dictionary<int, int> mapping = TrackTitleMatcher.Match(
            ["Love", "Love Is a Losing Game"],
            ["Love Is a Losing Game", "Love"]);
        Assert.Equal(1, mapping[0]);
        Assert.Equal(0, mapping[1]);
    }

    [Fact]
    public void Match_routes_a_lone_subset_title_to_its_exact_slot()
    {
        Dictionary<int, int> mapping = TrackTitleMatcher.Match(["Forever"], ["Forever Young", "Forever"]);
        Assert.Equal(1, Assert.Single(mapping).Value);
    }

    [Fact]
    public void Match_rejects_differing_digit_tokens()
    {
        Assert.Empty(TrackTitleMatcher.Match(["Part 1"], ["Part 12"]));
        Assert.Empty(TrackTitleMatcher.Match(["Chapter 1"], ["Chapter 11"]));
    }

    [Fact]
    public void Match_allows_one_sided_junk_digits()
    {
        Assert.Single(TrackTitleMatcher.Match(["Victory Lap 2024"], ["Victory Lap"]));
    }

    [Fact]
    public void Match_strips_featured_artists_before_scoring()
    {
        Assert.Single(TrackTitleMatcher.Match(["Solo (feat. Demi Lovato)"], ["Solo"]));
    }

    [Fact]
    public void Match_fails_closed_on_unknown_multi_token_qualifiers()
    {
        Assert.Empty(TrackTitleMatcher.Match(["Song (Slowed + Reverb Version)"], ["Song"]));
    }

    [Fact]
    public void TitleFromFilename_keeps_numeric_only_titles()
    {
        Assert.Equal("1999", TrackTitleMatcher.TitleFromFilename("1999"));
    }

    [Theory]
    [InlineData("03. I Luv U", "I Luv U")]
    [InlineData("0101 - G‐Eazy - Me Myself and I", "Me Myself and I")]
    [InlineData("04 solo", "solo")]
    [InlineData("01 - scared", "scared")]
    [InlineData("Fred again.. - Victory Lap", "Victory Lap")]
    [InlineData("", "")]
    public void TitleFromFilename_strips_numbering_and_artist_prefixes(string input, string expected)
    {
        Assert.Equal(expected, TrackTitleMatcher.TitleFromFilename(input));
    }
}

public class VariantProfileTests
{
    [Theory]
    [InlineData("Definitely Maybe", "Oasis - Definitely Maybe (Live)", true)]
    [InlineData("Nevermind", "Nirvana - Nevermind Live at the Paramount", true)]
    [InlineData("Live Forever", "Oasis - Live Forever", false)]                    // 'live' as a title word, not a qualifier
    [InlineData("Definitely Maybe (Live)", "Definitely Maybe (Live)", false)]
    [InlineData("Album", "Album (Acoustic)", true)]
    [InlineData("Album (Demos)", "Album", true)]
    [InlineData("Song", "Song (Extended Mix)", true)]
    [InlineData("Song (Radio Edit)", "Song (Extended Mix)", true)]
    [InlineData("Album", "Album (Extended Edition)", false)]                       // edition, not a different recording
    [InlineData("Album (Mono)", "Album (Stereo)", true)]
    [InlineData("Album (Stereo)", "Album", false)]                                 // one-sided master tag stays lenient
    [InlineData("Album", "Album (Remastered)", false)]
    [InlineData("Live", "AC-DC - Live", false)]                                       // album literally titled Live
    [InlineData("One More Light Live", "Linkin Park - One More Light Live [FLAC]", false)]  // trailing qualifier survives suffixes
    [InlineData("Song", "Song (Piano Version)", true)]
    [InlineData("Song (Piano Version)", "Song", true)]
    [InlineData("Song", "Song - Piano Version", true)]
    [InlineData("Song", "Song - Stripped", true)]
    [InlineData("Pull the Strings", "Pull the Strings - 2011 Remaster", false)]     // a noun ending the title is a title word
    [InlineData("Song (Piano Version)", "Song (Piano Ver.)", false)]
    [InlineData("Song (Acoustic)", "Song (Piano Version)", true)]                   // two different cuts
    [InlineData("Song", "Song (Unplugged)", true)]
    [InlineData("Song Unplugged", "Song - Unplugged", false)]
    [InlineData("Piano Man", "Piano Man (Remastered)", false)]                      // 'piano' as a title word
    [InlineData("Song", "Song (Cover)", true)]
    [InlineData("Song", "Song - Piano Cover", true)]
    [InlineData("Song (Piano Cover)", "Song - Piano Cover", false)]
    [InlineData("Song (Piano Cover)", "Song (Piano Version)", true)]                // a cover is its own cut
    [InlineData("Song", "Song - Acoustic Version", true)]
    [InlineData("Song", "Song (Stripped)", true)]
    [InlineData("Song (Stripped)", "Song (Orchestral Version)", true)]              // two different arrangements
    [InlineData("Song", "Song (String Version)", true)]
    [InlineData("Song (String Version)", "Song (Strings)", false)]
    [InlineData("Song (Orchestra Version)", "Song (Orchestral Version)", false)]
    [InlineData("Album", "Album (City Symphony Orchestra)", false)]                 // an ensemble, not an arrangement
    [InlineData("Quartets", "Quartets (Northside String Quartet)", false)]
    [InlineData("Sonatas", "Sonatas (cello: A. Player, piano: B. Player)", false)]  // performer credits
    [InlineData("Album (Covers)", "Album (Cover Version)", false)]
    [InlineData("Song", "Song (Symphonic Version)", true)]
    [InlineData("Song", "Song (Lullaby Version)", true)]
    [InlineData("Song (Lo-Fi Version)", "Song (Lofi)", false)]
    [InlineData("Song", "Song (Chill Version)", true)]
    [InlineData("Song", "Song (Guitar Version)", true)]
    [InlineData("Song", "Song (Re-Recorded)", true)]
    [InlineData("Song (Re-recording)", "Song (Rerecorded)", false)]
    [InlineData("Song", "Song (Night Bus Dub)", true)]
    [InlineData("Song", "Song (Album Version)", false)]                             // the plain recording
    [InlineData("Song", "Song (Clean)", false)]                                     // an edit of the same recording
    public void RemixSignaturesConflict_covers_variant_dimensions(string searchAlbum, string folder, bool expected)
    {
        Assert.Equal(expected, VariantQualifiers.RemixSignaturesConflict(searchAlbum, folder));
    }

    // The Remix type forgives remix text, not the target's own piano or unplugged cut.
    [Fact]
    public void A_remix_typed_piano_target_rejects_another_remix()
    {
        Assert.True(VariantQualifiers.RemixSignaturesConflict("Harbor Lights (piano unplugged)", "Harbor Lights (Night Bus Remix)", ["Remix"]));
    }

    // A dub is a remix: a Remix-typed target forgives it like any other.
    [Fact]
    public void A_remix_typed_target_forgives_a_dub()
    {
        Assert.False(VariantQualifiers.RemixSignaturesConflict("Harbor Lights", "Harbor Lights (Night Bus Dub)", ["Remix"]));
    }

    [Fact]
    public void A_piano_qualifier_on_the_parent_folder_counts()
    {
        Assert.True(VariantQualifiers.RemixSignaturesConflict("Album", ["Album", "Album (Piano Versions)"], null));
    }

    [Fact]
    public void Piano_and_unplugged_cuts_count_as_variant_tracks()
    {
        Assert.True(VariantQualifiers.IsVariantTrack("Harbor Lights (piano version)"));
        Assert.True(VariantQualifiers.HasVariantQualifier("Harbor Lights - Unplugged"));
        Assert.False(VariantQualifiers.IsVariantTrack("Piano Man"));
        Assert.False(VariantQualifiers.IsVariantTrack("Night Lullaby"));
    }

    [Fact]
    public void ExtractVariantProfile_reads_qualifier_zones_only()
    {
        VariantQualifiers.VariantProfile profile = VariantQualifiers.ExtractVariantProfile("Live Forever");
        Assert.False(profile.Live);

        profile = VariantQualifiers.ExtractVariantProfile("Definitely Maybe (Live at Knebworth) [FLAC]");
        Assert.True(profile.Live);
    }
}

public class RetryBackoffTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 6)]
    [InlineData(3, 24)]
    [InlineData(7, 24)]
    public void RetryBackoffWindow_escalates_with_failure_count(int failures, int expectedHours)
    {
        Assert.Equal(TimeSpan.FromHours(expectedHours), SlskdDownloadItem.RetryBackoffWindow(failures));
    }
}

public class UserFailurePenaltyTests
{
    private static SlskdFolderData Folder(int recentFailures) => new(
        Path: @"@@x\Artist\Album",
        Artist: "Artist",
        Album: "Album",
        Year: "2020",
        Username: "user",
        HasFreeUploadSlot: true,
        UploadSpeed: 2_000_000,
        LockedFileCount: 0,
        LockedFiles: [],
        QueueLength: 0,
        Token: 1,
        FileCount: 10,
        Files: [],
        RecentUserFailures: recentFailures);

    [Fact]
    public void CalculatePriority_downranks_users_with_recent_failures_without_zeroing()
    {
        int clean = Folder(0).CalculatePriority();
        int flaky = Folder(2).CalculatePriority();
        Assert.True(flaky < clean);
        Assert.True(flaky > 0);
    }
}
