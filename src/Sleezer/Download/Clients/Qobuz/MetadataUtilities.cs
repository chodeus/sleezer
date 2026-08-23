using System.Globalization;
using System.Linq;
using System.Text;
using NzbDrone.Plugin.Sleezer.Core.Tidal;
using QobuzApiSharp.Models.Content;

namespace NzbDrone.Core.Download.Clients.Qobuz
{
    internal static class MetadataUtilities
    {
        public static string GetFilledTemplate(string template, string ext, Track qobuzTrack, Album qobuzAlbum)
        {
            var releaseDate = qobuzAlbum.ReleaseDateOriginal.GetValueOrDefault().DateTime;

            return GetFilledTemplate(
                template,
                qobuzTrack.CompleteTitle,
                qobuzTrack.Album?.CompleteTitle ?? qobuzAlbum.CompleteTitle,
                qobuzAlbum.Artist?.Name ?? string.Empty,
                qobuzTrack.Performer?.Name ?? string.Empty,
                qobuzAlbum.Artists?.Select(a => a.Name).ToArray() ?? [],

                // Qobuz exposes no multi-artist list on a track.
                [qobuzTrack.Performer?.Name ?? string.Empty],
                FormatNumber(qobuzTrack.TrackNumber),
                qobuzAlbum.TracksCount.GetValueOrDefault().ToString(CultureInfo.InvariantCulture),
                FormatNumber(qobuzTrack.MediaNumber),
                qobuzAlbum.MediaCount.GetValueOrDefault().ToString(CultureInfo.InvariantCulture),
                releaseDate.Year.ToString(CultureInfo.InvariantCulture),
                ext);
        }

        // Delegated to the canonical sanitizer rather than Path.GetInvalidFileNameChars,
        // which on Linux returns only '/' and NUL — leaving ':' and '\' in album titles
        // for Lidarr's path parser to trip over. Tidal's MetadataUtilities does the same.
        public static string CleanPath(string str) => TidalPathSanitizer.CleanPath(str);

        // Interpolating a null through "{x:00}" yields an empty field, which silently
        // produces filenames like " -  - Title.flac". "00" says "Qobuz did not tell us".
        private static string FormatNumber(int? value)
            => value.HasValue ? value.Value.ToString("00", CultureInfo.InvariantCulture) : "00";

        private static string GetFilledTemplate(string template, string title, string album, string albumArtist, string artist, string[] albumArtists, string[] artists, string track, string trackCount, string volume, string volumeCount, string year, string ext)
        {
            StringBuilder t = new(template);

            ReplaceCleaned("%title%", title);
            ReplaceCleaned("%album%", album);
            ReplaceCleaned("%albumartist%", albumArtist);
            ReplaceCleaned("%artist%", artist);
            ReplaceCleaned("%albumartists%", string.Join("; ", albumArtists));
            ReplaceCleaned("%artists%", string.Join("; ", artists));
            ReplaceCleaned("%track%", track);
            ReplaceCleaned("%trackcount%", trackCount);
            ReplaceCleaned("%volume%", volume);
            ReplaceCleaned("%volumecount%", volumeCount);
            ReplaceCleaned("%ext%", ext);
            ReplaceCleaned("%year%", year);

            return t.ToString();

            void ReplaceCleaned(string token, string value) => t.Replace(token, CleanPath(value));
        }
    }
}
