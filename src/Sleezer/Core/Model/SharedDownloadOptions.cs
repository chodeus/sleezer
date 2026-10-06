using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Plugin.Sleezer.Core.Utilities;
using NzbDrone.Plugin.Sleezer.Metadata.DownloadRules;
using NzbDrone.Plugin.Sleezer.Metadata.Lyrics;

namespace NzbDrone.Plugin.Sleezer.Core.Model
{
    /// <summary>The shared settings a store download reads, taken once per item.</summary>
    public sealed record SharedDownloadOptions(LyricsSettings Lyrics, bool WholeAlbumsOnly)
    {
        public static SharedDownloadOptions Default { get; } = new(new LyricsSettings(), true);

        public static SharedDownloadOptions Read(IMetadataFactory factory)
        {
            DownloadRulesSettings rules = SharedSettings.Read<DownloadRulesSettings>(factory)
                ?? throw new InvalidOperationException("No Sleezer Download Rules metadata entry found; not downloading without its rules.");
            return new(
                LyricsSettings.Effective(SharedSettings.Definition<LyricsSettings>(factory)),
                rules.WholeAlbumsOnly);
        }
    }
}
