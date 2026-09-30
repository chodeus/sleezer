using NzbDrone.Plugin.Sleezer.Blocklisting;
using NzbDrone.Plugin.Sleezer.Core.Qobuz;
using Xunit;

namespace Sleezer.Tests;

public class QobuzFailFastTests
{
    [Fact]
    public void Only_tracks_the_payload_marks_unstreamable_are_counted() =>
        Assert.Equal(2, QobuzAlbumFailure.FlaggedUnstreamable([true, false, null, false, true]));

    [Fact]
    public void An_album_with_a_flagged_track_fails_album_wide_when_complete_albums_are_required() =>
        Assert.Equal(AlbumWideFailure.QobuzUnstreamable,
            QobuzAlbumFailure.Reason(QobuzAlbumFailure.FlaggedUnstreamable([true, false]), requireCompleteAlbum: true));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_album_without_flagged_tracks_does_not_fail_early(bool requireCompleteAlbum) =>
        Assert.Null(QobuzAlbumFailure.Reason(QobuzAlbumFailure.FlaggedUnstreamable([true, null, true]), requireCompleteAlbum));

    [Fact]
    public void Flagged_tracks_download_the_rest_when_complete_albums_are_not_required() =>
        Assert.Null(QobuzAlbumFailure.Reason(QobuzAlbumFailure.FlaggedUnstreamable([true, false]), requireCompleteAlbum: false));
}
