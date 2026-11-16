using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Sleezer.Core.Utilities;
using QobuzApiSharp.Models.Content;

namespace NzbDrone.Core.ImportLists.Qobuz
{
    internal static class QobuzPlaylistTrack
    {
        /// <summary>The album a playlist track belongs to, or null when it gives none to monitor.</summary>
        public static ImportListItemInfo? ToListItem(Track track)
        {
            string? artistName = track.Album?.Artist?.Name ?? track.Performer?.Name;
            string? albumTitle = track.Album?.CompleteTitle;

            // A compilation would add "Various Artists" to Lidarr. Not sent under the performer either:
            // Lidarr monitors an artist-only item's whole discography, even on a Specific Album list.
            if (string.IsNullOrWhiteSpace(artistName) || string.IsNullOrWhiteSpace(albumTitle) || StoreReleaseVerifier.IsVariousArtists(artistName))
                return null;

            return new ImportListItemInfo
            {
                Artist = artistName,
                Album = albumTitle,
            };
        }
    }
}
