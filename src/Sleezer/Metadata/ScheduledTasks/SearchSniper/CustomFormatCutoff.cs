using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Profiles.Qualities;

namespace NzbDrone.Plugin.Sleezer.Metadata.ScheduledTasks.SearchSniper
{
    /// <summary>The custom format half of Lidarr's cutoff, as CutoffSpecification applies it at grab.</summary>
    public static class CustomFormatCutoff
    {
        // Profiles where some file can score below the cutoff; the rest never need scoring.
        public static IEnumerable<QualityProfile> Profiles(IEnumerable<QualityProfile> qualityProfiles) =>
            qualityProfiles.Where(p => p.UpgradeAllowed && p.CutoffFormatScore > p.FormatItems.Where(f => f.Score < 0).Sum(f => f.Score));

        public static bool IsUnmet(QualityProfile profile, List<CustomFormat> formats) =>
            profile.CalculateCustomFormatScore(formats) < profile.CutoffFormatScore;
    }
}
