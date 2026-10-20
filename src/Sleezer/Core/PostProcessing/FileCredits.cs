using NzbDrone.Plugin.Sleezer.Core.Utilities;

namespace NzbDrone.Plugin.Sleezer.Core.PostProcessing;

/// <summary>Named: a credit names the artist. Foreign: credits name only others. Unjudged: nothing to judge.</summary>
public enum CreditVerdict
{
    Unjudged,
    Named,
    Foreign,
}

/// <summary>Judges a file's album-artist and artist credits against the wanted artist.</summary>
public static class FileCredits
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

    /// <summary>Guest credits are stripped first, as for identification; a Various Artists credit judges only a Various Artists target.</summary>
    public static CreditVerdict Judge(IEnumerable<string?> credits, ArtistCredit artist)
    {
        List<string> judged = [.. credits
            .Select(c => FeaturedArtistStripper.Strip(FeaturedArtistStripper.StripGuestCredits(c, artist.Name)))
            .Where(c => StoreReleaseVerifier.Normalize(c).Length > 0 && (artist.IsVariousArtists || !StoreReleaseVerifier.IsVariousArtists(c)))];

        if (judged.Count == 0)
            return CreditVerdict.Unjudged;

        return judged.Any(artist.IsNamedBy) ? CreditVerdict.Named : CreditVerdict.Foreign;
    }
}
