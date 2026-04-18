namespace NzbDrone.Plugin.Sleezer.Core.Model;

public record PlaylistItem(
    string ArtistMusicBrainzId,
    string? AlbumMusicBrainzId,
    string ArtistName,
    string? AlbumTitle,
    string? TrackTitle = null,
    string? ForeignRecordingId = null);

public record PlaylistSnapshot(
    string ListName,
    List<PlaylistItem> Items,
    DateTime FetchedAt);

public interface IPlaylistTrackSource
{
    List<PlaylistItem> FetchTrackLevelItems();
}
