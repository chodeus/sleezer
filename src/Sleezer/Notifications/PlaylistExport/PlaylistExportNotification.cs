using FluentValidation.Results;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.ThingiProvider;

namespace NzbDrone.Plugin.Sleezer.Notifications.PlaylistExport;

public sealed class PlaylistExportNotification : NotificationBase<PlaylistExportSettings>
{
    private readonly IPlaylistExportService _service;

    public PlaylistExportNotification(IPlaylistExportService service) => _service = service;

    public override string Name => "Playlist Export";
    public override string Link => "https://github.com/TypNull/NzbDrone.Plugin.Sleezer";

    public override ProviderMessage Message =>
        new("Generates .m3u8 playlist files for selected import lists whenever a track is imported.",
            ProviderMessageType.Info);

    public override void OnReleaseImport(AlbumDownloadMessage message) =>
        _service.GeneratePlaylists(Settings);

    public override ValidationResult Test() => new();
}
