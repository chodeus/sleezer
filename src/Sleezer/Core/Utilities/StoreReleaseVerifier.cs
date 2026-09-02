using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using FuzzySharp;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Sleezer.Core.Model;
using NzbDrone.Plugin.Sleezer.Indexers.Soulseek;

namespace NzbDrone.Plugin.Sleezer.Core.Utilities
{
    /// <summary>Drops store results that are not the searched album: wrong artist or title, a variant the album doesn't call for, or a different track count or length.</summary>
    public static class StoreReleaseVerifier
    {
        private const int ArtistFuzzyFloor = 90;
        private const int TitleFuzzyFloor = 85;
        private const int TrackCountSlack = 2;
        private const double TrackCountRatioSlack = 0.25;
        private const double DurationRatioSlack = 0.15;
        private const int DurationSecondsSlack = 20;
        private const string VariousArtistsCategory = "various artists";

        private static readonly Regex LeadingArticle = new(@"^(the|a|an)\s+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

        public static IList<ReleaseInfo> Apply(IList<ReleaseInfo> releases, AlbumSearchCriteria? criteria, string indexerName, Logger logger)
        {
            if (releases.Count == 0 || criteria?.Artist == null)
                return releases;

            var target = Target.From(criteria);
            List<ReleaseInfo> kept = [];
            List<(ReleaseInfo Release, string Category, string Detail)> dropped = [];

            foreach (var release in releases)
            {
                var verdict = Judge(release, target);
                if (verdict == null)
                    kept.Add(release);
                else
                    dropped.Add((release, verdict.Value.Category, verdict.Value.Detail));
            }

            if (dropped.Count == 0)
                return releases;

            var summary = string.Join(", ", dropped.GroupBy(d => d.Category).Select(g => $"{g.Count()} {g.Key}"));
            var verb = criteria.InteractiveSearch ? "flagged" : "dropped";
            foreach (var (release, _, detail) in dropped)
                logger.Debug("{Indexer} {Verb} '{Title}' — {Detail}", indexerName, verb, release.Title, detail);

            // Interactive search is the operator looking for themselves — show everything except
            // Various Artists hits, which crash ArtistRepository.FindByName when two VA entries exist.
            if (criteria.InteractiveSearch)
            {
                logger.Info("{Indexer}: {Count} of {Total} result(s) would be dropped by automatic search — {Summary}", indexerName, dropped.Count, releases.Count, summary);
                var variousArtists = dropped.Where(d => d.Category == VariousArtistsCategory).Select(d => d.Release).ToHashSet();
                return variousArtists.Count == 0 ? releases : [.. releases.Where(r => !variousArtists.Contains(r))];
            }

            logger.Info("{Indexer}: dropped {Count} of {Total} result(s) — {Summary}", indexerName, dropped.Count, releases.Count, summary);
            return kept;
        }

        private static (string Category, string Detail)? Judge(ReleaseInfo release, Target target)
        {
            var store = release as StoreReleaseInfo;
            var candidateTitle = store?.CandidateTitle ?? release.Album;

            // Missing data is unjudgeable, not wrong — each check only runs on what the store supplied.
            if (!string.IsNullOrWhiteSpace(release.Artist) && !target.IsVariousArtists && IsVariousArtists(release.Artist))
                return (VariousArtistsCategory, $"'{release.Artist}' compilation offered for '{target.ArtistName}'");

            if (!string.IsNullOrWhiteSpace(release.Artist) && !ArtistMatches(release.Artist, target))
                return ("artist", $"artist '{release.Artist}' is not '{target.ArtistName}'");

            if (!string.IsNullOrWhiteSpace(candidateTitle))
            {
                if (!TitleMatches(candidateTitle, target.Title))
                    return ("title", $"'{candidateTitle}' is not '{target.Title}'");

                if (VariantQualifiers.RemixSignaturesConflict(target.Title, candidateTitle, target.SecondaryTypes))
                    return ("variant", $"'{candidateTitle}' is a variant the album does not call for");
            }

            if (store == null || target.Releases.Count == 0)
                return null;

            if (store.TrackCount > 0 && !TrackCountMatches(store.TrackCount, target))
                return ("track count", $"{store.TrackCount} track(s) vs MusicBrainz {target.TrackCountSummary}");

            if (store.TotalDurationSeconds > 0 && DurationMismatch(store, target) is { } detail)
                return ("duration", detail);

            if (!VariantQualifiers.HasVariantQualifier(candidateTitle) && OnlyVariantEditionsFit(store, target) is { } noPlain)
                return ("variant", noPlain);

            return null;
        }

        // A plain candidate needs a plain edition to land on. When every release its track
        // count fits is an all-variant tracklist, Lidarr would attach the file to one of
        // those and label it as the variant — the live "Chase the Sun" case.
        private static string? OnlyVariantEditionsFit(StoreReleaseInfo store, Target target)
        {
            var fitting = target.Releases
                .Where(r => store.TrackCount <= 0 || TrackCountCompatible(store.TrackCount, r.TrackCount))
                .ToList();

            if (fitting.Count == 0 || !fitting.All(r => r.IsAllVariantTracklist()))
                return null;

            return $"MusicBrainz has no plain edition of that length — every {store.TrackCount}-track release is a variant";
        }

        private static bool ArtistMatches(string candidate, Target target)
        {
            if (target.IsVariousArtists)
                return true;

            var normalized = Normalize(candidate);
            if (normalized.Length == 0 || normalized == target.ArtistNormalized || target.AliasesNormalized.Contains(normalized))
                return true;

            // A collaboration credit ("Afrojack & David Guetta") still contains the searched artist whole.
            var candidateTokens = normalized.Split(' ').ToHashSet(StringComparer.Ordinal);
            if (target.ArtistTokens.Count > 0 && target.ArtistTokens.All(candidateTokens.Contains))
                return true;

            return Fuzz.TokenSortRatio(normalized, target.ArtistNormalized) >= ArtistFuzzyFloor;
        }

        private static bool TitleMatches(string candidate, string target)
        {
            var c = Normalize(StoreQueryCleaner.StripQualifiers(candidate));
            var t = Normalize(StoreQueryCleaner.StripQualifiers(target));
            if (c.Length == 0 || t.Length == 0 || c == t)
                return true;

            // Sort-ratio, not set-ratio: a superset title ("Baby Get Shaky") must not score as "Get Shaky".
            return Fuzz.TokenSortRatio(c, t) >= TitleFuzzyFloor;
        }

        private static bool TrackCountMatches(int count, Target target) =>
            target.Releases.Any(r => TrackCountCompatible(count, r.TrackCount));

        private static bool TrackCountCompatible(int count, int releaseCount) =>
            Math.Abs(count - releaseCount) <= TrackCountSlack
            && Math.Abs(count - releaseCount) <= Math.Max(releaseCount, 1) * TrackCountRatioSlack;

        // Judged against every release the track count is compatible with (all of them when the
        // store gave no count); one within tolerance is enough.
        private static string? DurationMismatch(StoreReleaseInfo store, Target target)
        {
            var candidates = target.Releases
                .Where(r => r.DurationSeconds > 0 && (store.TrackCount <= 0 || TrackCountCompatible(store.TrackCount, r.TrackCount)))
                .ToList();
            if (candidates.Count == 0)
                return null;

            if (candidates.Any(r => Math.Abs(store.TotalDurationSeconds - r.DurationSeconds) <= r.DurationSeconds * DurationRatioSlack + DurationSecondsSlack))
                return null;

            var nearest = candidates.MinBy(r => Math.Abs(store.TotalDurationSeconds - r.DurationSeconds));
            return $"runs {store.TotalDurationSeconds}s vs MusicBrainz {nearest.DurationSeconds}s";
        }

        private static bool IsVariousArtists(string? artist)
        {
            var normalized = Normalize(artist);
            return normalized is "various artists" or "va";
        }

        internal static string Normalize(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var text = value.Replace("&", " and ").RemoveAccent().ToLowerInvariant();
            text = SlskdTextProcessor.StripPunctuation(text);
            text = LeadingArticle.Replace(text, string.Empty);
            return Spaces.Replace(text, " ").Trim();
        }

        /// <summary>One MusicBrainz release of the searched album; Release backs the lazy tracklist read.</summary>
        private sealed record TargetRelease(int TrackCount, int DurationSeconds, AlbumRelease Release)
        {
            public bool IsAllVariantTracklist()
            {
                var titles = VariantQualifiers.TracklistOf(Release);
                return titles.Count > 0 && titles.All(VariantQualifiers.IsVariantTrack);
            }
        }

        private sealed record Target(
            string ArtistName,
            string ArtistNormalized,
            HashSet<string> ArtistTokens,
            HashSet<string> AliasesNormalized,
            bool IsVariousArtists,
            string Title,
            IReadOnlyCollection<string> SecondaryTypes,
            IReadOnlyList<TargetRelease> Releases)
        {
            public string TrackCountSummary => string.Join("/", Releases.Select(r => r.TrackCount).Distinct().OrderBy(c => c));

            public static Target From(AlbumSearchCriteria criteria)
            {
                var album = criteria.Albums?.FirstOrDefault();
                var releases = album?.AlbumReleases?.Value ?? [];
                var aliases = criteria.Artist.Metadata?.Value?.Aliases ?? [];
                var artistNormalized = Normalize(criteria.Artist.Name);

                return new Target(
                    criteria.Artist.Name,
                    artistNormalized,
                    [.. artistNormalized.Split(' ', StringSplitOptions.RemoveEmptyEntries)],
                    [.. aliases.Select(Normalize).Where(a => a.Length > 0)],
                    StoreReleaseVerifier.IsVariousArtists(criteria.Artist.Name),
                    album?.Title ?? criteria.AlbumTitle,
                    VariantQualifiers.ForgivenVariants(album),
                    [.. releases.Select(r => new TargetRelease(r.TrackCount, DurationSeconds(r), r))]);
            }

            private static int DurationSeconds(AlbumRelease release)
            {
                if (release.Duration > 0)
                    return release.Duration / 1000;

                var tracks = release.Tracks?.Value;
                return tracks == null ? 0 : tracks.Sum(t => t.Duration) / 1000;
            }
        }
    }
}
