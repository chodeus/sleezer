using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace NzbDrone.Core.Download.Clients.Deezer
{
    /// <summary>Baseline tag write from GW page data — mirrors QobuzDownloader.ApplyMetadataToFile.</summary>
    public static class DeezerTagger
    {
        // PreImportTagger overwrites these with canonical Lidarr metadata after download;
        // this pass exists so identification (and any veto case) has solid tags to read.
        public static async Task ApplyAsync(string trackPath, JToken trackPage, JToken albumPage, byte[]? albumArt, string lyrics, CancellationToken token = default)
        {
            await Task.Run(() => Apply(trackPath, trackPage, albumPage, albumArt, lyrics), token);
        }

        internal static void Apply(string trackPath, JToken trackPage, JToken albumPage, byte[]? albumArt, string lyrics)
        {
            var data = trackPage["DATA"]!;
            var albumData = albumPage["DATA"];

            using TagLib.File file = TagLib.File.Create(trackPath);
            var tag = file.Tag;

            tag.Title = TitleWithVersion(data["SNG_TITLE"]?.ToString(), data["VERSION"]?.ToString());
            tag.Album = TitleWithVersion(albumData?["ALB_TITLE"]?.ToString() ?? data["ALB_TITLE"]?.ToString(), albumData?["VERSION"]?.ToString());

            var performers = Names(data["ARTISTS"]) ?? Names(data["ART_NAME"]?.ToString());
            if (performers != null)
                tag.Performers = performers;

            var albumArtists = Names(albumData?["ARTISTS"]) ?? Names(albumData?["ART_NAME"]?.ToString());
            if (albumArtists != null)
                tag.AlbumArtists = albumArtists;

            var year = ReleaseYear(data, albumData);
            if (year > 0)
                tag.Year = year;

            if (uint.TryParse(data["TRACK_NUMBER"]?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var trackNumber))
                tag.Track = trackNumber;
            if (uint.TryParse(albumPage["SONGS"]?["total"]?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var trackCount))
                tag.TrackCount = trackCount;

            if (uint.TryParse(data["DISK_NUMBER"]?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var disc))
                tag.Disc = disc;

            var discCount = (albumPage["SONGS"]?["data"] as JArray)?
                .Select(t => uint.TryParse(t["DISK_NUMBER"]?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) ? d : 0u)
                .DefaultIfEmpty(0u)
                .Max() ?? 0u;
            if (discCount > 0)
                tag.DiscCount = discCount;

            if (albumArt is { Length: > 0 })
            {
                tag.Pictures =
                [
                    new TagLib.Picture(new TagLib.ByteVector(albumArt))
                    {
                        Type = TagLib.PictureType.FrontCover,
                        Description = "Album Cover",
                    }
                ];
            }

            tag.Lyrics = lyrics;
            file.Save();
        }

        private static string? TitleWithVersion(string? title, string? version)
        {
            if (string.IsNullOrWhiteSpace(title))
                return title;
            return string.IsNullOrWhiteSpace(version) ? title : $"{title} {version.Trim()}";
        }

        private static string[]? Names(JToken? artists)
        {
            var names = (artists as JArray)?
                .Select(a => a["ART_NAME"]?.ToString())
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n!)
                .ToArray();
            return names is { Length: > 0 } ? names : null;
        }

        private static string[]? Names(string? single) =>
            string.IsNullOrWhiteSpace(single) ? null : [single];

        // DeezNET crashed here: culture-sensitive DateTime.Parse on a field Deezer sometimes omits.
        internal static uint ReleaseYear(JToken data, JToken? albumData)
        {
            foreach (var candidate in new[]
                     {
                         data["PHYSICAL_RELEASE_DATE"]?.ToString(),
                         albumData?["PHYSICAL_RELEASE_DATE"]?.ToString(),
                         albumData?["DIGITAL_RELEASE_DATE"]?.ToString(),
                         albumData?["ORIGINAL_RELEASE_DATE"]?.ToString(),
                     })
            {
                if (string.IsNullOrWhiteSpace(candidate))
                    continue;
                if (DateTime.TryParseExact(candidate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact))
                    return (uint)exact.Year;
                if (DateTime.TryParse(candidate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var loose))
                    return (uint)loose.Year;
            }

            return 0;
        }
    }
}
