using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Indexers.Exceptions;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Plugin.Sleezer.Qobuz;

namespace NzbDrone.Core.Indexers.Qobuz
{
    public class QobuzRequestGenerator : IIndexerRequestGenerator
    {
        private const int PageSize = 100;

        // Qobuz.IsFullPage stops paging as soon as a page returns fewer than PageSize
        // distinct albums, so this is only a worst-case ceiling.
        private const int MaxPages = 5;

        // Tier-2 cleaning. Qobuz's /album/search is token-AND, so a bracketed group or a
        // trailing edition qualifier that Qobuz doesn't carry in its own title drops the
        // result to zero. These strip that noise back to the core title.
        private static readonly Regex BracketedGroups = new(@"\s*[\(\[][^\)\]]*[\)\]]", RegexOptions.Compiled);

        // A colon/dash subtitle mentioning soundtrack words is dropped wholesale:
        // MusicBrainz's "The Hack: Original Television Soundtrack" is just "The Hack" on
        // Qobuz, and TrailingQualifier alone can't reach it ("Television" breaks its
        // original/motion-picture/soundtrack prefix chain).
        private static readonly Regex SubtitleQualifier = new(
            @"\s*[:\-–—]\s[^:]*\b(?:sound\s?tracks?|score|OST|music\s+from)\b.*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex TrailingQualifier = new(
            @"\s*[:\-–—]?\s*\b(?:" +
            @"(?:original\s+)?(?:motion\s+picture\s+)?(?:sound\s?tracks?|score)" +
            @"|OST" +
            @"|(?:\d+(?:st|nd|rd|th)?\s+)?anniversary\s+edition" +
            @"|special\s+edition" +
            @"|deluxe|expanded|remaster\w*|bonus\s+track\w*|EP|single" +
            @")\b.*$",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        private static readonly Regex CollapseSpaces = new(@"\s{2,}", RegexOptions.Compiled);

        public QobuzIndexerSettings Settings { get; set; } = null!;
        public Logger Logger { get; set; } = null!;

        public virtual IndexerPageableRequestChain GetRecentRequests()
        {
            // Qobuz has no new-release feed; this only exists so saving the indexer
            // settings has something to test against.
            var pageableRequests = new IndexerPageableRequestChain();
            pageableRequests.Add(GetRequests("never gonna give you up"));

            return pageableRequests;
        }

        public IndexerPageableRequestChain GetSearchRequests(AlbumSearchCriteria searchCriteria)
        {
            var chain = new IndexerPageableRequestChain();

            // Tier 1: the raw artist + album query, which already works for the vast
            // majority of searches. HttpIndexerBase only advances to the next tier when
            // the current one returns nothing.
            var tier1 = $"{searchCriteria.ArtistQuery} {searchCriteria.AlbumQuery}";
            chain.AddTier(GetRequests(tier1));

            if (string.IsNullOrWhiteSpace(searchCriteria.AlbumTitle) || string.IsNullOrWhiteSpace(searchCriteria.ArtistQuery))
                return chain;

            // Tier 2: strip punctuation and trailing edition/soundtrack qualifiers so the
            // core title survives token-AND matching (MB "Batman: Original Motion Picture
            // Score" -> "Batman", which Qobuz lists as "Batman (Original Motion Picture
            // Soundtrack)"). Added only when it differs, to avoid a redundant request.
            var artist = CleanForTokenSearch(searchCriteria.CleanArtistQuery);
            var album = CleanForTokenSearch(SearchCriteriaBase.GetQueryTitle(StripQualifiers(searchCriteria.AlbumTitle)));
            var tier2 = $"{artist} {album}";

            if (!string.Equals(tier2, tier1, StringComparison.OrdinalIgnoreCase))
                chain.AddTier(GetRequests(tier2));

            // Tier 3: MB split-release titles like "A / B" — Qobuz usually carries the
            // halves as separate releases, so search each. All halves share one tier so
            // their results come back together.
            if (!searchCriteria.AlbumTitle.Contains(" / ", StringComparison.Ordinal))
                return chain;

            List<string> partQueries =
            [
                .. searchCriteria.AlbumTitle
                    .Split(" / ", StringSplitOptions.RemoveEmptyEntries)
                    .Select(part => CleanForTokenSearch(SearchCriteriaBase.GetQueryTitle(StripQualifiers(part))))
                    .Where(part => !string.IsNullOrWhiteSpace(part))
                    .Select(part => $"{artist} {part}")
                    .Where(query => !string.Equals(query, tier1, StringComparison.OrdinalIgnoreCase)
                                    && !string.Equals(query, tier2, StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
            ];

            for (var i = 0; i < partQueries.Count; i++)
            {
                if (i == 0)
                    chain.AddTier(GetRequests(partQueries[i]));
                else
                    chain.Add(GetRequests(partQueries[i]));
            }

            return chain;
        }

        public IndexerPageableRequestChain GetSearchRequests(ArtistSearchCriteria searchCriteria)
        {
            var chain = new IndexerPageableRequestChain();
            chain.AddTier(GetRequests(searchCriteria.ArtistQuery));

            return chain;
        }

        // '+' back to spaces (GetAPIUrl would send %2B) and apostrophes to spaces —
        // Qobuz does not unify apostrophe variants attached to digits.
        private static string CleanForTokenSearch(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
                return query;

            var cleaned = query.Replace('+', ' ').Replace('\'', ' ');
            return CollapseSpaces.Replace(cleaned, " ").Trim();
        }

        // Reduces a MusicBrainz album title to its core. Returns the original if stripping
        // would leave nothing (an album literally named "Deluxe").
        private static string StripQualifiers(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
                return title;

            var stripped = BracketedGroups.Replace(title, string.Empty);
            stripped = SubtitleQualifier.Replace(stripped, string.Empty);
            stripped = TrailingQualifier.Replace(stripped, string.Empty);
            stripped = CollapseSpaces.Replace(stripped, " ").Trim(' ', ':', '-', '–', '—');

            return string.IsNullOrWhiteSpace(stripped) ? title : stripped;
        }

        private IEnumerable<IndexerRequest> GetRequests(string searchParameters)
        {
            QobuzAPI api = QobuzAPI.Instance
                ?? throw new ApiKeyException("Qobuz API is not initialised. Save the Qobuz indexer settings first.");

            // A scraped app secret can go stale between searches when Qobuz rotates it;
            // re-signing in re-reads bundle.js.
            if (!api.Client.IsAppSecretValid())
                api.SignIn(Settings);

            if (api.Login == null)
                throw new ApiKeyException("Qobuz login failed. Check your credentials in the indexer settings.");

            for (var page = 0; page < MaxPages; page++)
            {
                var data = new Dictionary<string, string>
                {
                    ["query"] = searchParameters,
                    ["limit"] = $"{PageSize}",
                    ["offset"] = $"{page * PageSize}",
                };

                var req = new IndexerRequest(api.GetAPIUrl("/album/search", data), HttpAccept.Json);
                req.HttpRequest.Method = System.Net.Http.HttpMethod.Get;
                req.HttpRequest.Headers.Add("X-App-ID", api.Client.AppId);
                req.HttpRequest.Headers.Add("X-User-Auth-Token", api.Login.AuthToken);
                yield return req;
            }
        }
    }
}
