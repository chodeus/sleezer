using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Sleezer.Core.Utilities;

namespace NzbDrone.Plugin.Sleezer.Core.Model
{
    /// <summary>
    /// Contains combined information about an album, search parameters, and search results.
    /// </summary>
    public class AlbumData(string name, string downloadProtocol)
    {
        public string? Guid { get; set; }
        public string IndexerName { get; } = name;

        // Mixed
        public string AlbumId { get; set; } = string.Empty;

        // Properties from AlbumInfo
        public string AlbumName { get; set; } = string.Empty;

        public string ArtistName { get; set; } = string.Empty;
        public string InfoUrl { get; set; } = string.Empty;

        // Identity, not display: slskd matches an interactive grab back to its search.
        public string CommentUrl { get; set; } = string.Empty;
        public string ReleaseDate { get; set; } = string.Empty;
        public DateTime ReleaseDateTime { get; set; }
        public string ReleaseDatePrecision { get; set; } = string.Empty;
        public int TotalTracks { get; set; }
        public bool ExplicitContent { get; set; }
        public string CustomString { get; set; } = string.Empty;
        public string CoverResolution { get; set; } = string.Empty;

        // Properties from YoutubeSearchResults
        public int Bitrate { get; set; }

        public int BitDepth { get; set; }
        public long Duration { get; set; }

        // Soulseek
        public long? Size { get; set; }

        public int Priotity { get; set; }
        public bool MatchedSearchCriteria { get; set; }

        // AlbumName is the searched MusicBrainz title, not a folder or store name: every bracket is kept.
        public bool AlbumIsSearchedTitle { get; set; }

        // Detected media source ("WEB", "CD", "Vinyl", "SACD") — parsed by
        // Lidarr's quality detection from the title suffix.
        public string SourceTag { get; set; } = "WEB";
        public List<string>? ExtraInfo { get; set; }

        public string DownloadProtocol { get; set; } = downloadProtocol;

        // Not used
        public AudioFormat Codec { get; set; } = AudioFormat.AAC;

        /// <summary>
        /// Converts AlbumData into a ReleaseInfo object.
        /// </summary>
        public ReleaseInfo ToReleaseInfo() => FillReleaseInfo(new StoreReleaseInfo());

        /// <summary>
        /// Slskd variant of <see cref="ToReleaseInfo"/>. ShareInfo derives from
        /// TorrentInfo so the share priority reaches
        /// SlskdIndexer.CleanupReleases as Seeders; a plain ReleaseInfo made
        /// that hook dead code and every release ranked on quality alone.
        /// </summary>
        public ShareInfo ToShareInfo()
        {
            ShareInfo release = (ShareInfo)FillReleaseInfo(new ShareInfo());
            release.Seeders = Priotity;
            release.MatchedSearchCriteria = MatchedSearchCriteria;
            // NB: InfoHash is deliberately NOT set here. Core prepends the
            // "{defId}_" prefix to Guid in IndexerBase.CleanupReleases AFTER
            // this runs, so mirroring Guid now would capture the un-prefixed
            // value while every stored/queried Guid is prefixed — a guaranteed
            // mismatch. SlskdIndexer.CleanupReleases sets InfoHash post-prefix.
            return release;
        }

        private ReleaseInfo FillReleaseInfo(ReleaseInfo release)
        {
            release.Guid = Guid ?? $"{IndexerName}-{AlbumId}-{Codec}-{Bitrate}-{BitDepth}";
            release.Artist = ArtistName;
            release.Album = AlbumName;
            release.DownloadUrl = AlbumId;
            release.InfoUrl = InfoUrl;
            release.CommentUrl = CommentUrl;
            // Only day-precision dates populate PublishDate; year/month are
            // synthesized and would trip EarlyReleaseSpecification — use discovery time.
            release.PublishDate = ReleaseDatePrecision == "day" && ReleaseDateTime != DateTime.MinValue
                ? ReleaseDateTime
                : DateTime.UtcNow;
            release.DownloadProtocol = DownloadProtocol;
            ReleaseTitleParts parts = TitleParts();
            if (release is StoreReleaseInfo store)
                ReleaseTitle.Render(store, parts);
            else
                release.Title = parts.Text;
            release.Codec = Codec.ToString();
            release.Resolution = CoverResolution;
            release.Source = CustomString;
            release.Container = Bitrate.ToString();
            release.Size = Size ?? (Duration > 0 ? Duration : TotalTracks * 300) * Bitrate * 1000 / 8;
            return release;
        }

        /// <summary>
        /// Parses the release date based on the precision.
        /// </summary>
        public void ParseReleaseDate() => ReleaseDateTime = ReleaseDatePrecision switch
        {
            "year" => new DateTime(int.Parse(ReleaseDate), 1, 1),
            "month" => DateTime.ParseExact(ReleaseDate, "yyyy-MM", System.Globalization.CultureInfo.InvariantCulture),
            "day" => DateTime.ParseExact(ReleaseDate, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            _ => throw new FormatException($"Unsupported release_date_precision: {ReleaseDatePrecision}"),
        };

        private ReleaseTitleParts TitleParts()
        {
            string album = AlbumIsSearchedTitle ? ReleaseTitle.SearchedAlbum(AlbumName) : ReleaseTitle.StoreAlbum(AlbumName);

            string title = string.Empty;

            if (ReleaseDateTime != DateTime.MinValue)
                title += $" ({ReleaseDateTime.Year})";

            if (ExplicitContent)
                title += " [Explicit]";

            int calculatedBitrate = Bitrate;
            if (calculatedBitrate <= 0 && Size.HasValue && Duration > 0)
                calculatedBitrate = (int)(Size.Value * 8 / (Duration * 1000));

            if (AudioFormatHelper.IsLossyFormat(Codec) && calculatedBitrate != 0)
                title += $" [{Codec} {calculatedBitrate}kbps]";
            else if (!AudioFormatHelper.IsLossyFormat(Codec) && BitDepth != 0)
                title += $" [{Codec} {BitDepth}bit]";
            else
                title += $" [{Codec}]";

            // An edition that repeats the source tag ("[CD] [CD]") reads as a bug;
            // SourceTag is appended below, so it wins.
            if (ExtraInfo?.Count > 0)
                title += string.Concat(ExtraInfo
                    .Where(info => !string.IsNullOrEmpty(info) && !string.Equals(info, SourceTag, StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(info => $" [{info}]"));

            title += $" [{SourceTag}]";
            return new ReleaseTitleParts(ArtistName, album, title);
        }
    }
}