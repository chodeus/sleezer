using System.Text.RegularExpressions;
using NzbDrone.Plugin.Sleezer.Core.Utilities;

namespace NzbDrone.Plugin.Sleezer.Core.PostProcessing;

/// <summary>Named: a credit lists the artist outright. Foreign: no credit names them, even loosely. Unjudged: neither, or nothing to judge.</summary>
public enum CreditVerdict
{
    Unjudged,
    Named,
    Foreign,
}

/// <summary>Judges a file's album-artist and artist credits against the wanted artist.</summary>
public static partial class FileCredits
{
    /// <summary>Every album-artist and artist credit as written, or null when the file can't be read; ParsedTrackInfo keeps only the first.</summary>
    public static IReadOnlyList<string?>? Read(string path)
    {
        try
        {
            using TagLib.File file = TagLib.File.Create(path);
            return [.. file.Tag.AlbumArtists, .. file.Tag.Performers];
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>A "feat.", "ft." or "featuring" guest, bracketed or not, never names the artist; a Various Artists credit judges only a Various Artists target.</summary>
    public static CreditVerdict Judge(IEnumerable<string?> credits, ArtistCredit artist)
    {
        List<string> judged = [.. credits
            .Select(c => BareGuestRegex().Replace(FeaturedArtistStripper.Strip(FeaturedArtistStripper.StripGuestCredits(c, artist.Name)), string.Empty))
            .Where(c => StoreReleaseVerifier.Normalize(c).Length > 0 && (artist.IsVariousArtists || !StoreReleaseVerifier.IsVariousArtists(c)))];

        if (judged.Count == 0 || !artist.IsJudgeable)
            return CreditVerdict.Unjudged;

        if (judged.Any(artist.IsListedIn))
            return CreditVerdict.Named;

        return judged.Any(artist.IsNamedBy) ? CreditVerdict.Unjudged : CreditVerdict.Foreign;
    }

    [GeneratedRegex(@"\s+(?:feat\.?|featuring|ft\.?)\s.*$", RegexOptions.IgnoreCase)]
    private static partial Regex BareGuestRegex();
}
