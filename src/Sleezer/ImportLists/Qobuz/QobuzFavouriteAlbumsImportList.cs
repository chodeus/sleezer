using System;
using System.Collections.Generic;
using FluentValidation.Results;
using NLog;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Sleezer.Qobuz;

namespace NzbDrone.Core.ImportLists.Qobuz
{
    public class QobuzFavouriteAlbumsImportList(
        IImportListStatusService importListStatusService,
        IConfigService configService,
        IParsingService parsingService,
        Logger logger)
        : QobuzImportListBase<QobuzFavouritesSettings>(importListStatusService, configService, parsingService, logger)
    {
        public override string Name => "Qobuz Favourite Albums";

        public override IList<ImportListItemInfo> Fetch()
        {
            List<ImportListItemInfo> items = [];

            QobuzAPI api = QobuzAPI.Instance
                ?? throw new InvalidOperationException("Not signed in to Qobuz — add and save the Qobuz indexer first.");
            if (api.Login == null)
                throw new InvalidOperationException("Not signed in to Qobuz — add and save the Qobuz indexer first.");

            try
            {
                PageThrough("favourite albums", offset =>
                {
                    var favourites = QobuzAPI.Instance?.Client?.GetUserFavorites(null, type: "albums", limit: PageSize, offset: offset);
                    var page = favourites?.Albums?.Items;
                    if (page == null)
                        return (0, 0);

                    foreach (var album in page)
                    {
                        string? artistName = album.Artist?.Name;
                        if (string.IsNullOrWhiteSpace(artistName))
                            continue;

                        // Album as well as artist: this is an album list, and without the
                        // title Lidarr would monitor the whole artist instead.
                        items.Add(new ImportListItemInfo
                        {
                            Artist = artistName,
                            Album = album.CompleteTitle,
                            ReleaseDate = album.ReleaseDateOriginal?.DateTime ?? default,
                        });
                    }

                    return (page.Count, favourites?.Albums?.Total ?? 0);
                });
            }
            catch (Exception ex)
            {
                // Never return a partial list: Lidarr takes the result as the current,
                // authoritative set, so a transient outage would read as the user
                // having removed everything that had not been paged yet.
                _logger.Warn(ex, "Failed to fetch Qobuz favourite albums; failing the list rather than reporting a short one");
                throw;
            }

            return CleanupListItems(items);
        }

        protected override void Test(List<ValidationFailure> failures)
        {
            if (!RequireSession(failures))
                return;

            try
            {
                if (QobuzAPI.Instance?.Client?.GetUserFavorites(null, type: "albums", limit: 1) == null)
                    failures.Add(new ValidationFailure(string.Empty, "Qobuz returned no response for favourite albums."));
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Qobuz favourite-albums test failed");
                failures.Add(new ValidationFailure(string.Empty, $"Failed to fetch Qobuz favourite albums: {ex.Message}"));
            }
        }
    }
}
