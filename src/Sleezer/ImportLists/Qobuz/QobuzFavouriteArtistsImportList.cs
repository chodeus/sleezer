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

            try
            {
                PageThrough("favourite artists", offset =>
                {
                    var favourites = QobuzAPI.Instance?.Client?.GetUserFavorites(null, type: "artists", limit: PageSize, offset: offset);
                    var page = favourites?.Artists?.Items;
                    if (page == null)
                        return (0, 0);

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
                _logger.Warn(ex, "Failed to fetch Qobuz favourite artists");
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
