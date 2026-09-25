namespace NzbDrone.Plugin.Sleezer.Core.Model
{
    /// <summary>Indexer settings that can switch the shared store-result verifier off.</summary>
    public interface IStoreMatchingSettings
    {
        bool StrictMatching { get; }
    }

    /// <summary>The Strict Matching help text, by what each indexer's results let the verifier check.</summary>
    public static class StrictMatchingHelp
    {
        private const string Rest = " against the MusicBrainz release, and reject remix, live, acoustic and extended variants unless the album itself is one. A result whose title and artist match exactly reaches Lidarr under the searched album's name. Failing results are not hidden: they reach Lidarr carrying the reason, so automatic search skips them while interactive search shows why and still lets you grab one. Various Artists compilations are dropped outright. Also covers the release-year check when a result carries a full release date.";

        public const string TracksAndLength = "Verify each result's artist, title, track count and length" + Rest;
        public const string Tracks = "Verify each result's artist, title and track count" + Rest;
        public const string TitleOnly = "Verify each result's artist and title" + Rest;
    }
}
