using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Indexers.Exceptions;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Sleezer.Qobuz;

namespace NzbDrone.Core.Indexers.Qobuz
{
    public class Qobuz : HttpIndexerBase<QobuzIndexerSettings>
    {
        public override string Name => "Qobuz";
        public override string Protocol => nameof(QobuzDownloadProtocol);
        public override bool SupportsRss => false;
        public override bool SupportsSearch => true;
        public override int PageSize => 100;
        public override TimeSpan RateLimit => TimeSpan.FromSeconds(1);

        public Qobuz(IHttpClient httpClient,
            IIndexerStatusService indexerStatusService,
            IConfigService configService,
            IParsingService parsingService,
            Logger logger)
            : base(httpClient, indexerStatusService, configService, parsingService, logger)
        {
        }

        public override IIndexerRequestGenerator GetRequestGenerator()
        {
            EnsureSignedIn();

            return new QobuzRequestGenerator
            {
                Settings = Settings,
                Logger = _logger
            };
        }

        public override IParseIndexerResponse GetParser() => new QobuzParser
        {
            Settings = Settings,
            Logger = _logger
        };

        // The parser expands one album into up to four quality variants, so the base
        // class's raw release count would read a partial page as a full one and fetch
        // another. Count distinct albums (one InfoUrl each) instead.
        protected override bool IsFullPage(IList<ReleaseInfo> page)
            => page.Select(r => r.InfoUrl).Distinct(StringComparer.OrdinalIgnoreCase).Count() >= PageSize;

        protected override async Task<ValidationFailure> TestConnection()
        {
            ValidationFailure? baseFailure = await base.TestConnection();
            if (baseFailure != null)
                return baseFailure;

            // Surface the storefront the account resolves to. Qobuz licenses per
            // territory, so "album not found" is usually this country, not a bug.
            string country = QobuzAPI.Instance?.CountryCode ?? string.Empty;
            if (string.IsNullOrEmpty(country))
                _logger.Warn("Qobuz signed in but reported no country code; regional availability cannot be determined");
            else
                _logger.Info("Qobuz account storefront is {Country} — search results and downloads are limited to what is licensed there", country);

            return null!;
        }

        // Qobuz's /album/search appends "Various Artists" compilations to many
        // specific-artist searches. They're the sole trigger for an interactive-search
        // 500: with two "Various Artists" entries in the library, ArtistRepository
        // .FindByName throws MultipleArtistsFoundException and aborts the whole search.
        public override async Task<IList<ReleaseInfo>> Fetch(AlbumSearchCriteria searchCriteria)
            => SkipVariousArtists(await base.Fetch(searchCriteria), searchCriteria.Artist?.Name);

        public override async Task<IList<ReleaseInfo>> Fetch(ArtistSearchCriteria searchCriteria)
            => SkipVariousArtists(await base.Fetch(searchCriteria), searchCriteria.Artist?.Name);

        private void EnsureSignedIn()
        {
            // Only the App ID/Secret decide whether the client itself has to be rebuilt;
            // everything else is a sign-in concern. Both are compared as configured,
            // never against the values QobuzApiService resolves from the web player —
            // a blank setting never equals a resolved one, which would rebuild and
            // re-authenticate on every single search.
            bool clientNeedsRebuild = QobuzAPI.Instance == null
                || QobuzAPI.Instance.ConfiguredAppId != (Settings.AppID ?? string.Empty)
                || QobuzAPI.Instance.ConfiguredAppSecret != (Settings.AppSecret ?? string.Empty);

            QobuzAPI.Initialize(Settings.AppID, Settings.AppSecret, _logger, clientNeedsRebuild);

            if (QobuzAPI.Instance!.Login != null
                && QobuzAPI.Instance.CredentialFingerprint == QobuzAPI.FingerprintOf(Settings))
                return;

            if (!QobuzAPI.Instance.SignIn(Settings))
                throw new ApiKeyException("Qobuz sign-in failed. Check the User ID and Auth Token in the indexer settings.");
        }

        private IList<ReleaseInfo> SkipVariousArtists(IList<ReleaseInfo> releases, string? searchedArtist)
        {
            if (releases.Count == 0 || IsVariousArtists(searchedArtist))
                return releases;

            List<ReleaseInfo> kept = [.. releases.Where(r => !IsVariousArtists(r.Artist))];
            if (kept.Count != releases.Count)
                _logger.Debug("Qobuz skipped {Count} 'Various Artists' result(s) for search '{Search}'", releases.Count - kept.Count, searchedArtist);

            return kept;
        }

        private static bool IsVariousArtists(string? artist)
            => !string.IsNullOrWhiteSpace(artist)
               && (artist.Trim().Equals("Various Artists", StringComparison.OrdinalIgnoreCase)
                   || artist.Trim().Equals("VA", StringComparison.OrdinalIgnoreCase));
    }
}
