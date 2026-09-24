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
    /// <summary>Flags store results that are not the searched album: wrong artist or title, a variant the album doesn't call for, or a different track count or length.</summary>
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
        // Volume and part numbers; a four-digit year is an edition detail, not a different album.
        private static readonly Regex Numeral = new(@"\b(?:\d{1,3}|[ivx]{1,4})\b", RegexOptions.Compiled);

        /// <summary>Annotates results that fail verification; only Various Artists hits are removed.</summary>
        public static IList<ReleaseInfo> Apply(IList<ReleaseInfo> releases, AlbumSearchCriteria? criteria, string indexerName, Logger logger)
        {
            if (releases.Count == 0 || criteria?.Artist == null)
                return releases;

            var target = Target.From(criteria);
            List<ReleaseInfo> kept = [];
            List<string> categories = [];

            foreach (var release in releases)
            {
                var verdict = Judge(release, target);
                if (verdict == null)
                {
                    kept.Add(release);
                    continue;
                }

                categories.Add(verdict.Value.Category);
                logger.Debug("{Indexer} flagged '{Title}' — {Detail}", indexerName, release.Title, verdict.Value.Detail);

                // A Various Artists hit is removed outright: Lidarr's ParsingService resolves the
                // credit through ArtistRepository.FindByName, which throws when the library holds
                // two Various Artists entries, so letting one through costs an error per search.
                if (verdict.Value.Category == VariousArtistsCategory)
                    continue;

                if (release is IVerifiableRelease verifiable)
                    verifiable.Rejection ??= verdict.Value.Detail;

                kept.Add(release);
            }

            if (categories.Count == 0)
                return releases;

            var summary = string.Join(", ", categories.GroupBy(c => c).Select(g => $"{g.Count()} {g.Key}"));
            logger.Info("{Indexer}: {Count} of {Total} result(s) failed verification — {Summary}", indexerName, categories.Count, releases.Count, summary);
            return kept;
        }

        /// <summary>Whether Judge, artist aside, would pass the copy under this title for another album.</summary>
        internal static bool PassesFor(StoreReleaseInfo release, string title, Album album, IReadOnlyList<AlbumRelease> releases) =>
            JudgeAlbum(release, title, AlbumTarget.Of(album, album.Title, releases)) == null;

        private static (string Category, string Detail)? Judge(ReleaseInfo release, Target target)
        {
            // Missing data is unjudgeable, not wrong — each check only runs on what the store supplied.
            if (!string.IsNullOrWhiteSpace(release.Artist) && !target.IsVariousArtists && IsVariousArtists(release.Artist))
                return (VariousArtistsCategory, $"'{release.Artist}' compilation offered for '{target.ArtistName}'");

            if (!string.IsNullOrWhiteSpace(release.Artist) && !ArtistMatches(release.Artist, target))
                return ("artist", $"artist '{release.Artist}' is not '{target.ArtistName}'");

            return JudgeAlbum(release, (release as StoreReleaseInfo)?.CandidateTitle ?? release.Album, target.Album);
        }

        private static (string Category, string Detail)? JudgeAlbum(ReleaseInfo release, string? candidateTitle, AlbumTarget album)
        {
            var store = release as StoreReleaseInfo;

            if (!string.IsNullOrWhiteSpace(candidateTitle))
            {
                if (!TitleMatches(candidateTitle, album.Title))
                    return ("title", $"'{candidateTitle}' is not '{album.Title}'");

                if (VariantQualifiers.RemixSignaturesConflict(album.Title, candidateTitle, album.SecondaryTypes))
                    return ("variant", $"'{candidateTitle}' is a variant the album does not call for");
            }

            if (store == null || album.Releases.Count == 0)
                return null;

            if (store.TrackCount > 0 && !TrackCountMatches(store.TrackCount, album))
                return ("track count", $"{store.TrackCount} track(s) vs MusicBrainz {album.TrackCountSummary}");

            if (store.TotalDurationSeconds > 0 && DurationMismatch(store, album) is { } detail)
                return ("duration", detail);

            if (store.TrackCount > 0 && !VariantQualifiers.HasVariantQualifier(candidateTitle) && OnlyVariantEditionsFit(store, album) is { } noPlain)
                return ("variant", noPlain);

            return null;
        }

        // Lidarr attaches a download to whichever release fits the file count, so with no
        // plain edition of that length a plain product lands on a variant and is named as one.
        private static string? OnlyVariantEditionsFit(StoreReleaseInfo store, AlbumTarget target)
        {
            var fitting = target.Releases
                .Where(r => TrackCountCompatible(store.TrackCount, r.TrackCount))
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

        internal static bool TitleMatches(string candidate, string target)
        {
            var c = Comparable(candidate);
            var t = Comparable(target);
            if (c.Length == 0 || t.Length == 0 || c == t)
                return true;

            // Sort-ratio, not set-ratio: a superset title ("Baby Get Shaky") must not score as "Get Shaky".
            return Fuzz.TokenSortRatio(c, t) >= TitleFuzzyFloor;
        }

        // TitleMatches passes an unjudgeable title; a caller that rejects on a match must skip it first.
        internal static bool TitleJudgeable(string? title) => Comparable(title).Length > 0;

        /// <summary>Whether a candidate names the target outright: identical apart from edition qualifiers, with the same numbers.</summary>
        // Stricter than TitleMatches on purpose: a result renamed to the target loses Lidarr's own title check.
        internal static bool SameAlbumTitle(string? candidate, string? target)
        {
            string c = Comparable(candidate);
            return c.Length > 0 && c == Comparable(target) && Numerals(candidate).SetEquals(Numerals(target));
        }

        /// <summary>Whether the store names the searched artist outright: the full credit, an alias, or a listed main artist.</summary>
        // Stricter than ArtistMatches on purpose: a result renamed to the searched artist loses Lidarr's own artist lookup,
        // and "Low" must not claim "Low Roar". Credits are never split: "Simon & Garfunkel" is not "Simon".
        internal static bool CreditsArtist(StoreReleaseInfo release, Artist searched)
        {
            HashSet<string> names = [.. new[] { searched.Name }.Concat(searched.Metadata?.Value?.Aliases ?? []).Select(Normalize).Where(n => n.Length > 0)];
            return names.Contains(Normalize(release.Artist)) || release.MainArtists.Any(a => names.Contains(Normalize(a)));
        }

        private static HashSet<string> Numerals(string? title) => [.. Numeral.Matches(Normalize(title)).Select(m => m.Value)];

        private static string Comparable(string? title) => Normalize(StoreQueryCleaner.StripQualifiers(title ?? string.Empty));

        private static bool TrackCountMatches(int count, AlbumTarget target) =>
            target.Releases.Any(r => TrackCountCompatible(count, r.TrackCount));

        internal static bool TrackCountCompatible(int count, int releaseCount) =>
            Math.Abs(count - releaseCount) <= TrackCountSlack
            && Math.Abs(count - releaseCount) <= Math.Max(releaseCount, 1) * TrackCountRatioSlack;

        // Judged against every release the track count is compatible with (all of them when the
        // store gave no count); one within tolerance is enough.
        private static string? DurationMismatch(StoreReleaseInfo store, AlbumTarget target)
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

        public static bool IsVariousArtists(string? artist)
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
        private readonly record struct TargetRelease(int TrackCount, int DurationSeconds, AlbumRelease Release)
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
            AlbumTarget Album)
        {
            public static Target From(AlbumSearchCriteria criteria)
            {
                var album = criteria.Albums?.FirstOrDefault();
                var aliases = criteria.Artist.Metadata?.Value?.Aliases ?? [];
                var artistNormalized = Normalize(criteria.Artist.Name);

                return new Target(
                    criteria.Artist.Name,
                    artistNormalized,
                    [.. artistNormalized.Split(' ', StringSplitOptions.RemoveEmptyEntries)],
                    [.. aliases.Select(Normalize).Where(a => a.Length > 0)],
                    StoreReleaseVerifier.IsVariousArtists(criteria.Artist.Name),
                    AlbumTarget.Of(album, album?.Title ?? criteria.AlbumTitle, album?.AlbumReleases?.Value ?? []));
            }
        }

        private sealed record AlbumTarget(string Title, IReadOnlyCollection<string> SecondaryTypes, IReadOnlyList<TargetRelease> Releases)
        {
            public string TrackCountSummary => string.Join("/", Releases.Select(r => r.TrackCount).Distinct().OrderBy(c => c));

            public static AlbumTarget Of(Album? album, string title, IEnumerable<AlbumRelease> releases) =>
                new(title, VariantQualifiers.ForgivenVariants(album), [.. releases.Select(r => new TargetRelease(r.TrackCount, DurationSeconds(r), r))]);

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
