using NzbDrone.Plugin.Sleezer.Core.PostProcessing;
using NzbDrone.Plugin.Sleezer.Core.Utilities;
using Xunit;

namespace Sleezer.Tests;

// MusicBrainz writes "re‐recorded" (U+2010) and "Moss´s" where files and store copies write ASCII.
public class VariantPunctuationTests
{
    private static string Describe(string title)
    {
        VariantQualifiers.VariantProfile p = VariantQualifiers.ExtractVariantProfile(title);
        return $"{p.Live} {p.Demo} {p.Extended} [{string.Join(",", p.Arrangements.Order())}] {p.MonoStereo} {p.RemixSignature ?? "-"} {VariantQualifiers.IsVariantTrack(title)}";
    }

    [Theory]
    [InlineData("Lantern Song (re-recorded)", "Lantern Song (re‐recorded)")]
    [InlineData("Lantern Song (1981 re-recording)", "Lantern Song (1981 re‑recording)")]
    [InlineData("Lantern Song (Moss Re-Work)", "Lantern Song (Moss Re‐Work)")]
    [InlineData("Lantern Song (sped-up version)", "Lantern Song (sped‐up version)")]
    [InlineData("Lantern Lo-Fi", "Lantern Lo‐Fi")]
    [InlineData("Lantern Song (Moss's Night Mix)", "Lantern Song (Moss´s Night Mix)")]
    [InlineData("Lantern Song (Dirty South Re-Work) (Clean)", "Lantern Song (Dirty South Re‐Work) (Clean)")]
    public void A_typographic_spelling_reads_as_its_ascii_one(string ascii, string typographic)
    {
        Assert.Equal(Describe(ascii), Describe(typographic));
        Assert.False(VariantQualifiers.RemixSignaturesConflict(ascii, typographic));
        Assert.False(VariantQualifiers.RemixSignaturesConflict(typographic, ascii));
        Assert.True(VariantQualifiers.RemixSignaturesConflict("Lantern Song", typographic));
    }

    [Fact]
    public void A_typographic_remix_names_its_remixer()
    {
        Assert.Equal("moss", VariantQualifiers.ExtractRemixSignature("Lantern Song (Moss Re‐Work)"));
    }

    [Fact]
    public void A_typographic_disambiguation_names_the_variant_its_tracks_carry()
    {
        string? title = VariantQualifiers.VariantTitleOf("Lantern Song", "Moss re‐work", ["Lantern Song (Moss re-work)", "Glass Harbour (Moss re-work)"]);

        Assert.Equal("Lantern Song (Moss re‐work)", title);
    }

    // A spaced dash may separate a date range or a subtitle; read as " - ", the words before it would pass for an artist.
    [Theory]
    [InlineData("Lantern Remixes 2009 – 2015")]
    [InlineData("Lantern Remixed – The Extended Mixes")]
    public void A_spaced_dash_is_left_as_it_is(string title)
    {
        Assert.Equal(string.Empty, VariantQualifiers.ExtractRemixSignature(title));
    }

    [Theory]
    [InlineData("re‐recorded", "re-recorded")]
    [InlineData("Moss´s ‘Night’", "Moss's 'Night'")]
    [InlineData("1989–1991", "1989-1991")]
    [InlineData("Lantern – Moss", "Lantern – Moss")]
    [InlineData("“Lantern”…", "“Lantern”…")]
    public void Word_punctuation_maps_apostrophes_and_joining_dashes_only(string input, string expected)
    {
        Assert.Equal(expected, ComparableText.MapWordPunctuation(input));
    }

    [Fact]
    public void A_file_written_in_ascii_matches_the_typographic_track_and_a_plain_one_does_not()
    {
        Assert.Equal(0, Assert.Single(TrackTitleMatcher.Match(["Lantern Song (re-recorded)"], ["Lantern Song (re‐recorded)"])).Value);
        Assert.Empty(TrackTitleMatcher.Match(["Lantern Song"], ["Lantern Song (re‐recorded)"]));
    }
}
