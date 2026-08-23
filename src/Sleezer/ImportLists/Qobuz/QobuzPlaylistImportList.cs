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

            QobuzAPI api = QobuzAPI.Instance
                ?? throw new InvalidOperationException("Not signed in to Qobuz — add and save the Qobuz indexer first.");
            if (api.Login == null)
                throw new InvalidOperationException("Not signed in to Qobuz — add and save the Qobuz indexer first.");

            // An empty list here would be returned to Lidarr as the authoritative current
            // set, silently unmonitoring everything the lists had contributed.
            if (Settings.PlaylistIds?.Any() != true)
                throw new InvalidOperationException("No Qobuz playlist IDs are configured for this import list.");

            foreach (string playlistId in Settings.PlaylistIds)
            {
                try
                {
                    PageThrough($"tracks from playlist {playlistId}", offset =>
                    {
                        var playlist = api.Client.GetPlaylist(playlistId, withAuth: true, extra: "tracks", limit: PageSize, offset: offset);
                        var page = playlist?.Tracks?.Items
                            ?? throw new InvalidOperationException($"Qobuz returned no track page for playlist {playlistId} at offset {offset}.");

                        foreach (var track in page)
                        {
                            string? artistName = track.Album?.Artist?.Name ?? track.Performer?.Name;
                            string? albumTitle = track.Album?.CompleteTitle;
                            if (string.IsNullOrWhiteSpace(artistName) || string.IsNullOrWhiteSpace(albumTitle))
                                continue;

                            // Playlists are track-level, so the album a track belongs to is
                            // the most specific thing worth monitoring.
                            items.Add(new ImportListItemInfo
                            {
                                Artist = artistName,
                                Album = albumTitle,
                            });
                        }

                        return (page.Count, playlist?.Tracks?.Total ?? 0);
                    });
                }
                catch (ApiErrorResponseException ex) when (IsPlaylistGone(ex))
                {
                    // A deleted or private playlist is a real answer about that playlist,
                    // so skip it — the others are still valid.
                    _logger.Warn(ex, "Skipping Qobuz playlist {PlaylistId}: {Status} ({Reason})", playlistId, ex.ResponseStatusCode, ex.ResponseReason);
                }
                catch (Exception ex)
                {
                    // Anything else (auth, rate limit, 5xx, timeout) says nothing about
                    // the playlist's contents, so failing the list beats reporting a
                    // short one that Lidarr would treat as authoritative.
                    _logger.Warn(ex, "Failed to fetch Qobuz playlist {PlaylistId}; failing the list", playlistId);
                    throw;
                }
            }

            return CleanupListItems(items);
        }

        // ResponseStatusCode is a string on this exception type. Only 404 is a reliable
        // statement about the playlist; a 403 usually means the session is rejected, and
        // skipping it would hand Lidarr a short list it treats as authoritative.
        private static bool IsPlaylistGone(ApiErrorResponseException ex)
            => ex.ResponseStatusCode is "404";

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
