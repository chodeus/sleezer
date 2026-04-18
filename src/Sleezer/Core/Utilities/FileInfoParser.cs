using System.Text.RegularExpressions;

namespace NzbDrone.Plugin.Sleezer.Core.Utilities
{
    /// <summary>
    /// Was used for Slskd post-processing of file information, now no longer used but kept for potential future use.
    /// </summary>
    public partial class FileInfoParser
    {
        public string? Artist { get; private set; }
        public string? Title { get; private set; }
        public int TrackNumber { get; private set; }
        public int DiscNumber { get; private set; }
        public string? Tag { get; private set; }

        public FileInfoParser(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("File path cannot be null or empty.", nameof(filePath));
            string filename = Path.GetFileNameWithoutExtension(filePath);
            ParseFilename(filename);
        }

        private void ParseFilename(string filename)
        {
            // Try first set of patterns (space, underscore, dash separators with standard chars)
            foreach (Regex pattern in PatternsSet1)
            {
                Match match = pattern.Match(filename);
                if (match.Success)
                {
                    ExtractMatchResults(match);
                    return;
                }
            }

            // Try second set of patterns (space, dash separators with underscore in chars)
            foreach (Regex pattern in PatternsSet2)
            {
                Match match = pattern.Match(filename);
                if (match.Success)
                {
                    ExtractMatchResults(match);
                    return;
                }
            }
        }

        private void ExtractMatchResults(Match match)
        {
            Artist = match.Groups["artist"].Success ? match.Groups["artist"].Value.Trim() : string.Empty;
            Title = match.Groups["title"].Success ? match.Groups["title"].Value.Trim() : string.Empty;
            TrackNumber = match.Groups["track"].Success ? int.Parse(match.Groups["track"].Value) : 0;
            Tag = match.Groups["tag"].Success ? match.Groups["tag"].Value.Trim() : string.Empty;
            if (TrackNumber > 100)
            {
                DiscNumber = TrackNumber / 100;
                TrackNumber %= 100;
            }
        }

        // Pattern set 1: chars = a-z0-9,().&'' with space/underscore/dash separators
        private static readonly Regex[] PatternsSet1 =
        [
            TrackArtistTitleTagPattern1(),
            TrackArtistTagTitlePattern1(),
            TrackArtistTitlePattern1(),
            ArtistTagTrackTitlePattern1(),
            ArtistTrackTitleTagPattern1(),
            ArtistTrackTitlePattern1(),
            ArtistTitleTagPattern1(),
            ArtistTagTitlePattern1(),
            ArtistTitlePattern1(),
            TrackTitlePattern1(),
            TrackTagTitlePattern1(),
            TrackTitleTagPattern1(),
            TitleOnlyPattern()
        ];

        // Pattern set 2: chars = a-z0-9,().&'_ with space/dash separators
        private static readonly Regex[] PatternsSet2 =
        [
            TrackArtistTitleTagPattern2(),
            TrackArtistTagTitlePattern2(),
            TrackArtistTitlePattern2(),
            ArtistTagTrackTitlePattern2(),
            ArtistTrackTitleTagPattern2(),
            ArtistTrackTitlePattern2(),
            ArtistTitleTagPattern2(),
            ArtistTagTitlePattern2(),
            ArtistTitlePattern2(),
            TrackTitlePattern2(),
            TrackTagTitlePattern2(),
            TrackTitleTagPattern2(),
            TitleOnlyPattern()
        ];

        // Generated patterns for set 1 (chars include standard chars, sep = space/underscore/dash)
        [GeneratedRegex(@"^(?<track>\d+)(?<sep>[\s_-]+)(?<artist>[a-z0-9,\(\)\.&'']+)\k<sep>(?<title>[a-z0-9,\(\)\.&'']+)\k<sep>(?<tag>[a-z0-9,\(\)\.&'']+)$", RegexOptions.IgnoreCase)]
        private static partial Regex TrackArtistTitleTagPattern1();

        [GeneratedRegex(@"^(?<track>\d+)(?<sep>[\s_-]+)(?<artist>[a-z0-9,\(\)\.&'']+)\k<sep>(?<tag>[a-z0-9,\(\)\.&'']+)\k<sep>(?<title>[a-z0-9,\(\)\.&'']+)$", RegexOptions.IgnoreCase)]
        private static partial Regex TrackArtistTagTitlePattern1();

        [GeneratedRegex(@"^(?<track>\d+)(?<sep>[\s_-]+)(?<artist>[a-z0-9,\(\)\.&'']+)\k<sep>(?<title>[a-z0-9,\(\)\.&'']+)$", RegexOptions.IgnoreCase)]
        private static partial Regex TrackArtistTitlePattern1();

        [GeneratedRegex(@"^(?<artist>[a-z0-9,\(\)\.&'']+)(?<sep>[\s_-]+)(?<tag>[a-z0-9,\(\)\.&'']+)\k<sep>(?<track>\d+)\k<sep>(?<title>[a-z0-9,\(\)\.&'']+)$", RegexOptions.IgnoreCase)]
        private static partial Regex ArtistTagTrackTitlePattern1();

        [GeneratedRegex(@"^(?<artist>[a-z0-9,\(\)\.&'']+)(?<sep>[\s_-]+)(?<track>\d+)\k<sep>(?<title>[a-z0-9,\(\)\.&'']+)\k<sep>(?<tag>[a-z0-9,\(\)\.&'']+)$", RegexOptions.IgnoreCase)]
        private static partial Regex ArtistTrackTitleTagPattern1();

        [GeneratedRegex(@"^(?<artist>[a-z0-9,\(\)\.&'']+)(?<sep>[\s_-]+)(?<track>\d+)\k<sep>(?<title>[a-z0-9,\(\)\.&'']+)$", RegexOptions.IgnoreCase)]
        private static partial Regex ArtistTrackTitlePattern1();

        [GeneratedRegex(@"^(?<artist>[a-z0-9,\(\)\.&'']+)(?<sep>[\s_-]+)(?<title>[a-z0-9,\(\)\.&'']+)\k<sep>(?<tag>[a-z0-9,\(\)\.&'']+)$", RegexOptions.IgnoreCase)]
        private static partial Regex ArtistTitleTagPattern1();

        [GeneratedRegex(@"^(?<artist>[a-z0-9,\(\)\.&'']+)(?<sep>[\s_-]+)(?<tag>[a-z0-9,\(\)\.&'']+)\k<sep>(?<title>[a-z0-9,\(\)\.&'']+)$", RegexOptions.IgnoreCase)]
        private static partial Regex ArtistTagTitlePattern1();

        [GeneratedRegex(@"^(?<artist>[a-z0-9,\(\)\.&'']+)(?<sep>[\s_-]+)(?<title>[a-z0-9,\(\)\.&'']+)$", RegexOptions.IgnoreCase)]
        private static partial Regex ArtistTitlePattern1();

        [GeneratedRegex(@"^(?<track>\d+)(?<sep>[\s_-]+)(?<title>[a-z0-9,\(\)\.&'']+)$", RegexOptions.IgnoreCase)]
        private static partial Regex TrackTitlePattern1();

        [GeneratedRegex(@"^(?<track>\d+)(?<sep>[\s_-]+)(?<tag>[a-z0-9,\(\)\.&'']+)\k<sep>(?<title>[a-z0-9,\(\)\.&'']+)$", RegexOptions.IgnoreCase)]
        private static partial Regex TrackTagTitlePattern1();

        [GeneratedRegex(@"^(?<track>\d+)(?<sep>[\s_-]+)(?<title>[a-z0-9,\(\)\.&'']+)\k<sep>(?<tag>[a-z0-9,\(\)\.&'']+)$", RegexOptions.IgnoreCase)]
        private static partial Regex TrackTitleTagPattern1();

        // Generated patterns for set 2 (chars include underscore, sep = space/dash only)
        [GeneratedRegex(@"^(?<track>\d+)(?<sep>[\s-]+)(?<artist>[a-z0-9,\(\)\.\&''_]+)\k<sep>(?<title>[a-z0-9,\(\)\.\&''_]+)\k<sep>(?<tag>[a-z0-9,\(\)\.\&''_]+)$", RegexOptions.IgnoreCase)]
        private static partial Regex TrackArtistTitleTagPattern2();

        [GeneratedRegex(@"^(?<track>\d+)(?<sep>[\s-]+)(?<artist>[a-z0-9,\(\)\.\&''_]+)\k<sep>(?<tag>[a-z0-9,\(\)\.\&''_]+)\k<sep>(?<title>[a-z0-9,\(\)\.\&''_]+)$", RegexOptions.IgnoreCase)]
        private static partial Regex TrackArtistTagTitlePattern2();

        [GeneratedRegex(@"^(?<track>\d+)(?<sep>[\s-]+)(?<artist>[a-z0-9,\(\)\.\&''_]+)\k<sep>(?<title>[a-z0-9,\(\)\.\&''_]+)$", RegexOptions.IgnoreCase)]
        private static partial Regex TrackArtistTitlePattern2();

        [GeneratedRegex(@"^(?<artist>[a-z0-9,\(\)\.\&''_]+)(?<sep>[\s-]+)(?<tag>[a-z0-9,\(\)\.\&''_]+)\k<sep>(?<track>\d+)\k<sep>(?<title>[a-z0-9,\(\)\.\&''_]+)$", RegexOptions.IgnoreCase)]
        private static partial Regex ArtistTagTrackTitlePattern2();

        [GeneratedRegex(@"^(?<artist>[a-z0-9,\(\)\.\&''_]+)(?<sep>[\s-]+)(?<track>\d+)\k<sep>(?<title>[a-z0-9,\(\)\.\&''_]+)\k<sep>(?<tag>[a-z0-9,\(\)\.\&''_]+)$", RegexOptions.IgnoreCase)]
        private static partial Regex ArtistTrackTitleTagPattern2();

        [GeneratedRegex(@"^(?<artist>[a-z0-9,\(\)\.\&''_]+)(?<sep>[\s-]+)(?<track>\d+)\k<sep>(?<title>[a-z0-9,\(\)\.\&''_]+)$", RegexOptions.IgnoreCase)]
        private static partial Regex ArtistTrackTitlePattern2();

        [GeneratedRegex(@"^(?<artist>[a-z0-9,\(\)\.\&''_]+)(?<sep>[\s-]+)(?<title>[a-z0-9,\(\)\.\&''_]+)\k<sep>(?<tag>[a-z0-9,\(\)\.\&''_]+)$", RegexOptions.IgnoreCase)]
        private static partial Regex ArtistTitleTagPattern2();

        [GeneratedRegex(@"^(?<artist>[a-z0-9,\(\)\.\&''_]+)(?<sep>[\s-]+)(?<tag>[a-z0-9,\(\)\.\&''_]+)\k<sep>(?<title>[a-z0-9,\(\)\.\&''_]+)$", RegexOptions.IgnoreCase)]
        private static partial Regex ArtistTagTitlePattern2();

        [GeneratedRegex(@"^(?<artist>[a-z0-9,\(\)\.\&''_]+)(?<sep>[\s-]+)(?<title>[a-z0-9,\(\)\.\&''_]+)$", RegexOptions.IgnoreCase)]
        private static partial Regex ArtistTitlePattern2();

        [GeneratedRegex(@"^(?<track>\d+)(?<sep>[\s-]+)(?<title>[a-z0-9,\(\)\.\&''_]+)$", RegexOptions.IgnoreCase)]
        private static partial Regex TrackTitlePattern2();

        [GeneratedRegex(@"^(?<track>\d+)(?<sep>[\s-]+)(?<tag>[a-z0-9,\(\)\.\&''_]+)\k<sep>(?<title>[a-z0-9,\(\)\.\&''_]+)$", RegexOptions.IgnoreCase)]
        private static partial Regex TrackTagTitlePattern2();

        [GeneratedRegex(@"^(?<track>\d+)(?<sep>[\s-]+)(?<title>[a-z0-9,\(\)\.\&''_]+)\k<sep>(?<tag>[a-z0-9,\(\)\.\&''_]+)$", RegexOptions.IgnoreCase)]
        private static partial Regex TrackTitleTagPattern2();

        // Title only pattern (shared between sets)
        [GeneratedRegex(@"^(?<title>.+)$", RegexOptions.IgnoreCase)]
        private static partial Regex TitleOnlyPattern();
    }
}