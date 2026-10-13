using System.Reflection;
using NzbDrone.Core.Music;

namespace NzbDrone.Plugin.Sleezer.Metadata.Proxy
{
    // A Lidarr build with its own metadata sources (Settings > Metadata > Sources); looked up by name so standard Lidarr still loads this plugin.
    public static class MetadataSourcesBuild
    {
        public const string SourceInterfaceName = "NzbDrone.Core.MetadataSource.Sources.IMetadataSource";

        public const string StandDownMessage = "This Lidarr build manages metadata sources under Settings > Metadata > Sources, so Sleezer's metadata sources stay off here. Use the Sleezer Metadata Sources plugin to add Deezer, Discogs and Last.fm there.";

        private static readonly Lazy<bool> _present = new(() => HasType(typeof(Artist).Assembly, SourceInterfaceName));

        public static bool Present => _present.Value;

        public static bool HasType(Assembly lidarrCore, string typeName) => lidarrCore.GetType(typeName, throwOnError: false) != null;
    }
}
