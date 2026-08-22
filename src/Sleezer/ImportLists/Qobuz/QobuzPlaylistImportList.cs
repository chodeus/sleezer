using System;
using System.Collections.Generic;
using System.Linq;
using FluentValidation.Results;
using NLog;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Sleezer.Qobuz;
using QobuzApiSharp.Exceptions;

namespace NzbDrone.Core.ImportLists.Qobuz
{
    public class QobuzPlaylistImportList(
        IImportListStatusService importListStatusService,
        IConfigService configService,
        IParsingService parsingService,
        Logger logger)
        : QobuzImportListBase<QobuzPlaylistSettings>(importListStatusService, configService, parsingService, logger)
    {
        public override string Name => "Qobuz Playlist";

        public override IList<ImportListItemInfo> Fetch()
        {
            List<ImportListItemInfo> items = [];

            foreach (string playlistId in Settings.PlaylistIds ?? [])
            {
                try
                {
                    PageThrough($"tracks from playlist {playlistId}", offset =>
                    {
                        var playlist = QobuzAPI.Instance?.Client?.GetPlaylist(playlistId, withAuth: true, extra: "tracks", limit: PageSize, offset: offset);
                        var page = playlist?.Tracks?.Items;
                        if (page == null)
                        {
                            if (offset == 0)
                                _logger.Warn("Qobuz playlist {PlaylistId} returned no tracks", playlistId);
                            return (0, 0);
                        }

                        foreach (var track in page)
                        {
                            string? artistName = track.Album?.Artist?.Name ?? track.Performer?.Name;
                            if (string.IsNullOrWhiteSpace(artistName))
                                continue;

                            // Playlists are track-level, so the album a track belongs to is
                            // the most specific thing worth monitoring.
                            items.Add(new ImportListItemInfo
                            {
                                Artist = artistName,
                                Album = track.Album?.CompleteTitle,
                            });
                        }

                        return (page.Count, playlist?.Tracks?.Total ?? 0);
                    });
                }
                catch (ApiErrorResponseException ex)
                {
                    // One unavailable playlist (deleted, private) must not stop the others.
                    _logger.Warn(ex, "Skipping Qobuz playlist {PlaylistId}: {Status} ({Reason})", playlistId, ex.ResponseStatusCode, ex.ResponseReason);
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "Failed to fetch Qobuz playlist {PlaylistId}", playlistId);
                }
            }

            return CleanupListItems(items);
        }

        protected override void Test(List<ValidationFailure> failures)
        {
            if (!RequireSession(failures))
                return;

            string? first = Settings.PlaylistIds?.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(first))
            {
                failures.Add(new ValidationFailure(nameof(Settings.PlaylistIds), "Add at least one playlist ID."));
                return;
            }

            try
            {
                if (QobuzAPI.Instance?.Client?.GetPlaylist(first, withAuth: true, extra: "tracks", limit: 1) == null)
                    failures.Add(new ValidationFailure(nameof(Settings.PlaylistIds), $"Qobuz returned no response for playlist {first}."));
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Qobuz playlist test failed for {PlaylistId}", first);
                failures.Add(new ValidationFailure(nameof(Settings.PlaylistIds), $"Failed to fetch Qobuz playlist {first}: {ex.Message}"));
            }
        }
    }
}
