using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Sleezer.Core.PostProcessing;
using Xunit;

namespace Sleezer.Tests;

public class FeaturedArtistStripperTests
{
    [Theory]
    [InlineData("Song (feat. Other Artist)", "Song")]
    [InlineData("Song [Featuring Other Artist]", "Song")]
    [InlineData("Song {ft. Other Artist}", "Song")]
    [InlineData("Song (FEAT. Other Artist)", "Song")]
    [InlineData("Song (featuring Other)", "Song")]
    [InlineData("Song (ft Other)", "Song")]
    [InlineData("Song [feat Other]", "Song")]
    public void Strip_removes_bracketed_feat_suffixes(string input, string expected)
    {
        Assert.Equal(expected, FeaturedArtistStripper.Strip(input));
    }

    [Theory]
    [InlineData("My Featurette")]
    [InlineData("Feature Film")]
    [InlineData("Song feat. Other Artist")] // bare-text form intentionally not stripped
    [InlineData("Song featuring Other")]
    [InlineData("Song ft Other")]
    public void Strip_leaves_non_bracketed_feat_text_alone(string input)
    {
        Assert.Equal(input, FeaturedArtistStripper.Strip(input));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "   ")]
    public void Strip_handles_null_and_empty(string? input, string expected)
    {
        Assert.Equal(expected, FeaturedArtistStripper.Strip(input));
    }

    [Fact]
    public void Strip_preserves_inner_content_after_removing_feat()
    {
        Assert.Equal("Song Name", FeaturedArtistStripper.Strip("Song Name (feat. Someone Else)"));
    }

    [Fact]
    public void Strip_handles_multiple_feat_suffixes()
    {
        // If a title somehow has two bracketed feat-clauses, both should go.
        Assert.Equal("Song", FeaturedArtistStripper.Strip("Song (feat. A) [ft. B]"));
    }

    // Live 2026-07-31: comma-joined credits dragged the artist's own single
    // below Lidarr's import cutoff ("T & Sugah, Grace Barton" → 77.9% vs 80%).
    [Theory]
    [InlineData("T & Sugah, Grace Barton", "T & Sugah", "T & Sugah")]
    [InlineData("A$AP Rocky, Brent Faiyaz", "A$AP Rocky", "A$AP Rocky")]
    [InlineData("t & sugah, Grace Barton", "T & Sugah", "t & sugah")]              // case-insensitive anchor, input casing kept
    [InlineData("T & Sugah; Grace Barton", "T & Sugah", "T & Sugah")]
    [InlineData("T & Sugah feat. Grace Barton", "T & Sugah", "T & Sugah")]         // bare feat is safe once anchored
    [InlineData("T & Sugah ft Grace Barton", "T & Sugah", "T & Sugah")]
    public void StripGuestCredits_strips_when_anchored_on_primary_artist(string input, string artist, string expected)
    {
        Assert.Equal(expected, FeaturedArtistStripper.StripGuestCredits(input, artist));
    }

    [Theory]
    [InlineData("Grace Barton, T & Sugah", "T & Sugah")]       // artist not the prefix
    [InlineData("T & Sugarman, Grace", "T & Sugah")]           // prefix must be the full artist name
    [InlineData("T & Sugah & Grace Barton", "T & Sugah")]      // '&' join is ambiguous with duo names — left alone
    [InlineData("T & Sugah", "T & Sugah")]                     // exact match, nothing to strip
    [InlineData("Various Artists", "T & Sugah")]
    public void StripGuestCredits_leaves_unanchored_values_alone(string input, string artist)
    {
        Assert.Equal(input, FeaturedArtistStripper.StripGuestCredits(input, artist));
    }

    [Theory]
    [InlineData(null, "T & Sugah", null)]
    [InlineData("T & Sugah, Grace", null, "T & Sugah, Grace")]
    [InlineData("", "", "")]
    public void StripGuestCredits_passes_null_and_empty_through(string? input, string? artist, string? expected)
    {
        Assert.Equal(expected, FeaturedArtistStripper.StripGuestCredits(input, artist));
    }

    // Live 2026-09-29: store singles tagged "Song (feat. Guest) [Remixer Remix]" missed the pre-import cutoff on the album title alone.
    [Fact]
    public void ForIdentification_strips_feat_from_the_album_title()
    {
        ParsedTrackInfo info = new() { AlbumTitle = "Song Title (feat. Guest Artist) [Remixer Remix]" };

        FeaturedArtistStripper.ForIdentification(info, "Artist Name");

        Assert.Equal("Song Title [Remixer Remix]", info.AlbumTitle);
    }

    [Fact]
    public void ForIdentification_strips_feat_from_the_track_titles_and_artist()
    {
        ParsedTrackInfo info = new()
        {
            Title = "Song Title (feat. Guest Artist)",
            CleanTitle = "Song Title (ft. Guest Artist)",
            ArtistTitle = "Artist Name, Guest Artist"
        };

        FeaturedArtistStripper.ForIdentification(info, "Artist Name");

        Assert.Equal("Song Title", info.Title);
        Assert.Equal("Song Title", info.CleanTitle);
        Assert.Equal("Artist Name", info.ArtistTitle);
    }

    // Live 2026-09-30: Lidarr's CleanTitle turned "Song (feat. Guest) (Remixer Remix)" into "Song", so every remix track missed its match.
    [Theory]
    [InlineData("Song Title (feat. Guest Artist) (Remixer Remix)", "Song Title", "Song Title (Remixer Remix)")]
    [InlineData("Song Title (Christmas Version)", "Song Title", "Song Title (Christmas Version)")]
    public void ForIdentification_matches_on_the_title_with_only_the_feat_removed(string title, string lidarrCleanTitle, string expected)
    {
        ParsedTrackInfo info = new() { Title = title, CleanTitle = lidarrCleanTitle };

        FeaturedArtistStripper.ForIdentification(info, "Artist Name");

        Assert.Equal(expected, info.CleanTitle);
    }

    [Fact]
    public void ForIdentification_strips_a_bracketed_feat_from_the_artist()
    {
        ParsedTrackInfo info = new() { ArtistTitle = "Artist Name (feat. Guest Artist)" };

        FeaturedArtistStripper.ForIdentification(info, "Other Artist");

        Assert.Equal("Artist Name", info.ArtistTitle);
    }

    [Fact]
    public void ForIdentification_leaves_a_title_without_feat_alone()
    {
        ParsedTrackInfo info = new() { AlbumTitle = "Album Title (Remixer Remix)", Title = "Song Title" };

        FeaturedArtistStripper.ForIdentification(info, "Artist Name");

        Assert.Equal("Album Title (Remixer Remix)", info.AlbumTitle);
        Assert.Equal("Song Title", info.Title);
    }

    // Live 2026-10-01: store singles reached Lidarr with "(feat. X)" / "(with X)" still in title and album.
    [Theory]
    [InlineData("Track Title (feat. Guest Artist) (Remixer Name Remix)", "Track Title (Remixer Name Remix)")]
    [InlineData("Track Title (with Guest Artist) [Other Language Version]", "Track Title [Other Language Version]")]
    [InlineData("Track Title (with Second Guest) (Remixer Name Remix)", "Track Title (Remixer Name Remix)")]
    [InlineData("Track Title (with Guest Artist & Second Guest)", "Track Title")]
    [InlineData("Track Title (With Myself)", "Track Title (With Myself)")]
    [InlineData("Track Title (with Someone Else)", "Track Title (with Someone Else)")]
    public void StripCredits_drops_feat_and_a_with_credit_naming_credited_artists(string input, string expected)
    {
        string[] credited = ["Artist Name", "Guest Artist", "Second Guest"];

        Assert.Equal(expected, FeaturedArtistStripper.StripCredits(input, credited));
    }

    [Fact]
    public void StripCredits_reads_credited_artists_joined_in_one_tag()
    {
        Assert.Equal("Track Title", FeaturedArtistStripper.StripCredits("Track Title (with Guest Artist)", ["Artist Name, Guest Artist"]));
    }
}
