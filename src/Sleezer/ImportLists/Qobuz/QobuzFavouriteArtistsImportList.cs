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
    public class QobuzFavouriteArtistsImportList(
        IImportListStatusService importListStatusService,
        IConfigService configService,
        IParsingService parsingService,
        Logger logger)
        : QobuzImportListBase<QobuzFavouritesSettings>(importListStatusService, configService, parsingService, logger)
    {
        public override string Name => "Qobuz Favourite Artists";

        public override IList<ImportListItemInfo> Fetch()
        {
            List<ImportListItemInfo> items = [];

            QobuzAPI api = QobuzAPI.Instance
                ?? throw new InvalidOperationException("Not signed in to Qobuz — add and save the Qobuz indexer first.");
            if (api.Login == null)
                throw new InvalidOperationException("Not signed in to Qobuz — add and save the Qobuz indexer first.");

            try
            {
                PageThrough("favourite artists", offset =>
                {
                    // Use the session already validated above, and treat a null response
                    // as a failure: returning (0, 0) would stop paging silently and hand
                    // Lidarr a short list it accepts as the current set.
                    var favourites = api.Client.GetUserFavorites(null, type: "artists", limit: PageSize, offset: offset);
                    var page = favourites?.Artists?.Items
                        ?? throw new InvalidOperationException($"Qobuz returned no favourite artists page at offset {offset}.");

                    foreach (var artist in page)
                    {
                        if (!string.IsNullOrWhiteSpace(artist.Name))
                            items.Add(new ImportListItemInfo { Artist = artist.Name });
                    }

                    return (page.Count, favourites?.Artists?.Total ?? 0);
                });
            }
            catch (Exception ex)
            {
                // Never return a partial list: Lidarr takes the result as the current,
                // authoritative set, so a transient outage would read as the user
                // having removed everything that had not been paged yet.
                _logger.Warn(ex, "Failed to fetch Qobuz favourite artists; failing the list rather than reporting a short one");
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
                if (QobuzAPI.Instance?.Client?.GetUserFavorites(null, type: "artists", limit: 1) == null)
                    failures.Add(new ValidationFailure(string.Empty, "Qobuz returned no response for favourite artists."));
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Qobuz favourite-artists test failed");
                failures.Add(new ValidationFailure(string.Empty, $"Failed to fetch Qobuz favourite artists: {ex.Message}"));
            }
        }
    }
}
