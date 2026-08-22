using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using NzbDrone.Plugin.Sleezer.Core.Tidal;

namespace NzbDrone.Core.Download.Clients.Deezer
{
    internal static class MetadataUtilities
    {
        public static string GetFilledTemplate(string template, string ext, JToken deezerPage, JToken deezerAlbumPage)
        {
            var songTitle = deezerPage["DATA"]!["SNG_TITLE"]!.ToString();
            var songVersion = deezerPage["DATA"]?["VERSION"]?.ToString() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(songVersion))
                songTitle = $"{songTitle} {songVersion}";

            var albumTitle = deezerPage["DATA"]!["ALB_TITLE"]!.ToString();
            var albumVersion = deezerAlbumPage["DATA"]?["VERSION"]?.ToString() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(albumVersion))
                albumTitle = $"{albumTitle} {albumVersion}";

            var releaseDate = DateTime.Parse(deezerPage["DATA"]!["PHYSICAL_RELEASE_DATE"]!.ToString(), CultureInfo.InvariantCulture);
            return GetFilledTemplate_Internal(template,
                songTitle,
                albumTitle,
                deezerAlbumPage["DATA"]!["ART_NAME"]!.ToString(),
                deezerPage["DATA"]!["ART_NAME"]!.ToString(),
                deezerAlbumPage["DATA"]!["ARTISTS"]!.Select(a => a["ART_NAME"]!.ToString()).ToArray(),
                deezerPage["DATA"]!["ARTISTS"]!.Select(a => a["ART_NAME"]!.ToString()).ToArray(),
                $"{(int)deezerPage["DATA"]!["TRACK_NUMBER"]!:00}",
                deezerAlbumPage["SONGS"]!["total"]!.ToString(),
                releaseDate.Year.ToString(CultureInfo.InvariantCulture),
                ext);
        }

        private static string GetFilledTemplate_Internal(string template, string title, string album, string albumArtist, string artist, string[] albumArtists, string[] artists, string track, string trackCount, string year, string ext)
        {
            StringBuilder t = new(template);
            ReplaceC("%title%", title);
            ReplaceC("%album%", album);
            ReplaceC("%albumartist%", albumArtist);
            ReplaceC("%artist%", artist);
            ReplaceC("%albumartists%", string.Join("; ", albumArtists));
            ReplaceC("%artists%", string.Join("; ", artists));
            ReplaceC("%track%", track);
            ReplaceC("%trackcount%", trackCount);
            ReplaceC("%ext%", ext);
            ReplaceC("%year%", year);

            return t.ToString();

            void ReplaceC(string o, string r)
            {
                t.Replace(o, CleanPath(r));
            }
        }

        // Delegated to the canonical sanitizer rather than Path.GetInvalidFileNameChars,
        // which on Linux returns only '/' and NUL — leaving ':', '\\', '*', '?', '<', '>',
        // '|', '"' and trailing dots in Deezer paths for Lidarr's parser to trip over.
        // Tidal's MetadataUtilities already delegates the same way.
        public static string CleanPath(string str) => TidalPathSanitizer.CleanPath(str);
    }
}
