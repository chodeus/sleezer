using System.Text.RegularExpressions;
using NzbDrone.Plugin.Sleezer.Core.Utilities;

namespace NzbDrone.Plugin.Sleezer.Indexers.Soulseek
{
    /// <summary>Whether a peer folder is the searched release, the files to take from it, and why.</summary>
    internal sealed record SlskdFolderVerdict(
        bool Matched,
        bool ArtistMatch,
        bool AlbumMatch,
        double TrackEvidence,
        List<SlskdFileData> FilesToDownload,
        int CoherencePriorityDelta,
        IReadOnlyList<string> Reasons);

    public partial class SlskdItemsParser
    {
        /// <summary>Judges one folder against the search; CreateAlbumData builds the release from the verdict.</summary>
        internal SlskdFolderVerdict JudgeFolder(IGrouping<string, SlskdFileData> directory, SlskdSearchData searchData, SlskdSettings? settings, int expectedTrackCount)
        {
            // The group key is already the merged directory (disc subfolders fold
            // into their parent), so every file in the group belongs to this release.
            FolderJudgement judgement = new(directory.ToList());

            // Wanted titles in the file names can prove the album when no path names the artist (server-blocked artists).
            (List<SlskdFileData> matchedTrackFiles, int coveredTrackCount, int wantedTrackTitleCount) = MatchWantedTrackFiles(directory, searchData.Tracks, searchData.TargetVariantTypes);
            TrackTitleEvidence evidence = new(matchedTrackFiles, coveredTrackCount, wantedTrackTitleCount);

            JudgeNames(judgement, directory.Key, searchData, settings?.MatchStrictness ?? 0, evidence);
            ApplyRemixGuard(judgement, directory.Key, searchData);
            JudgeSmallTarget(judgement, directory, searchData, settings, expectedTrackCount, evidence);
            ApplyReleaseTracks(judgement, directory.Key, searchData, settings, expectedTrackCount, evidence);

            _logger.Debug("Match results - Artist: {ArtistMatch}, Album: {AlbumMatch}, TrackEvidence: {Evidence:P0} — {Outcome}: {Reasons}",
                judgement.ArtistMatch, judgement.AlbumMatch, evidence.Share, judgement.Matched ? "matched" : "rejected", string.Join("; ", judgement.Reasons));

            return new SlskdFolderVerdict(judgement.Matched, judgement.ArtistMatch, judgement.AlbumMatch, evidence.Share, judgement.Files, judgement.CoherencePriorityDelta, judgement.Reasons);
        }

        // Artist and album named in the folder, the track-title evidence, and whether together they meet the search.
        private void JudgeNames(FolderJudgement judgement, string directoryKey, SlskdSearchData searchData, int strictnessOffset, TrackTitleEvidence evidence)
        {
            string dirNameNorm = NormalizeString(directoryKey);
            string searchArtistNorm = NormalizeString(searchData.Artist ?? "");
            string searchAlbumNorm = NormalizeString(searchData.Album ?? "");

            _logger.Trace("Creating album data - Dir: '{Dir}', Search artist: '{Artist}', Search album: '{Album}'", dirNameNorm, searchArtistNorm, searchAlbumNorm);

            // The volume route needs a keyword ("Vol. 3", "Part II"); a title that is a number ("1989", "IV") takes the normal route.
            bool isVolumeSearch = !string.IsNullOrEmpty(searchData.Album) && VolumeKeywordRegex().Match(searchData.Album).Success;

            (judgement.AlbumMatch, string albumSearched) = AlbumNamed(directoryKey, dirNameNorm, searchAlbumNorm, searchData.Album, isVolumeSearch, strictnessOffset);
            judgement.ArtistMatch = IsFuzzyArtistMatch(dirNameNorm, searchArtistNorm, strictnessOffset);

            AddNameReasons(judgement, albumSearched, isVolumeSearch);
            bool titlesCarryIt = ApplyTitleEvidence(judgement, evidence);
            judgement.Matched = NamesMeetSearch(judgement, searchArtistNorm, albumSearched, titlesCarryIt);
            JudgeCombinedName(judgement, dirNameNorm, searchData, strictnessOffset);
        }

        // Whether the folder names the album, and the form it was searched by: an album made only of marks ("÷")
        // normalizes to nothing, so the folder must carry the marks themselves.
        private (bool Named, string Searched) AlbumNamed(string directoryKey, string dirNameNorm, string searchAlbumNorm, string? searchAlbum, bool isVolumeSearch, int strictnessOffset)
        {
            string marks = ComparableText.MarksOnly(searchAlbum);
            if (marks.Length > 0)
                return (FolderCarriesMarks(directoryKey, marks), marks);

            bool named = isVolumeSearch
                ? CheckVolumeSeriesMatch(directoryKey, searchAlbum, strictnessOffset)
                : IsFuzzyAlbumNameMatch(dirNameNorm, searchAlbumNorm, strictnessOffset) || PathTailNamesAlbum(directoryKey, searchAlbumNorm, strictnessOffset);
            return (named, searchAlbumNorm);
        }

        private static void AddNameReasons(FolderJudgement judgement, string searchAlbumNorm, bool isVolumeSearch)
        {
            judgement.Reasons.Add(judgement.ArtistMatch ? "artist named" : "artist not named");
            if (!string.IsNullOrEmpty(searchAlbumNorm))
                judgement.Reasons.Add(AlbumReason(judgement.AlbumMatch, isVolumeSearch));
        }

        // Enough wanted titles in the file names make the folder the album, however it is named.
        private static bool ApplyTitleEvidence(FolderJudgement judgement, TrackTitleEvidence evidence)
        {
            if (evidence.Share < TrackEvidenceThreshold)
                return false;

            judgement.AlbumMatch = true;
            judgement.Reasons.Add($"track titles {(int)Math.Round(evidence.Share * 100, MidpointRounding.AwayFromZero)}%");
            return true;
        }

        private static bool NamesMeetSearch(FolderJudgement judgement, string searchArtistNorm, string searchAlbumNorm, bool titlesCarryIt) =>
            string.IsNullOrEmpty(searchAlbumNorm)
                ? judgement.ArtistMatch
                : judgement.AlbumMatch && (judgement.ArtistMatch || string.IsNullOrEmpty(searchArtistNorm) || titlesCarryIt);

        private static string AlbumReason(bool albumMatch, bool isVolumeSearch) => isVolumeSearch
            ? albumMatch ? "volume series matches" : "volume series differs"
            : albumMatch ? "album named" : "album not named";

        // The album's own folder only ("A+Band\Driftwood" names no "+" album), the marks standing alone ("A+B" names none), spaces between them allowed.
        private static bool FolderCarriesMarks(string directoryKey, string marks)
        {
            string[] components = SplitPathIntoComponents(directoryKey);
            string spaced = string.Join(@"\s*", marks.EnumerateRunes().Select(r => Regex.Escape(r.ToString())));
            return components.Length > 0 && Regex.IsMatch(components[^1], @"(?<![\p{L}\p{N}])" + spaced + @"(?![\p{L}\p{N}])");
        }

        // Path-tail retry: the album name often spans the parent+leaf components ("Artist - Album/CD extras" layouts).
        private static bool PathTailNamesAlbum(string directoryKey, string searchAlbumNorm, int strictnessOffset)
        {
            if (string.IsNullOrEmpty(searchAlbumNorm))
                return false;

            string[] components = SplitPathIntoComponents(directoryKey);
            return components.Length >= 2 && IsFuzzyAlbumNameMatch(NormalizeString($"{components[^2]} {components[^1]}"), searchAlbumNorm, strictnessOffset);
        }

        // Neither named alone: the artist and album read as one phrase.
        private static void JudgeCombinedName(FolderJudgement judgement, string dirNameNorm, SlskdSearchData searchData, int strictnessOffset)
        {
            if (judgement.ArtistMatch || judgement.AlbumMatch || string.IsNullOrEmpty(searchData.Artist) || string.IsNullOrEmpty(searchData.Album))
                return;

            string combinedSearch = NormalizeString($"{searchData.Artist} {searchData.Album}");
            (int combinedPartial, _) = GetCachedFuzzyScores(dirNameNorm, combinedSearch);
            judgement.AlbumMatch = combinedPartial > FuzzyCombinedThreshold + strictnessOffset;
            judgement.Matched = judgement.AlbumMatch;
            judgement.Reasons.Add(judgement.AlbumMatch ? "artist and album named together" : "artist and album not named together");
        }

        // After track evidence, which a remix's file names would otherwise satisfy; leaf and parent are one candidate
        // because "Album (Live)\FLAC" carries the qualifier a level up.
        private void ApplyRemixGuard(FolderJudgement judgement, string directoryKey, SlskdSearchData searchData)
        {
            if (!judgement.AlbumMatch || string.IsNullOrEmpty(searchData.Album))
                return;

            string[] pathComponents = SplitPathIntoComponents(directoryKey);
            string candidateLeaf = pathComponents.Length > 0 ? pathComponents[^1] : directoryKey;
            string? candidateParent = pathComponents.Length >= 2 ? pathComponents[^2] : null;
            if (!VariantQualifiers.RemixSignaturesConflict(searchData.Album, [candidateLeaf, candidateParent], searchData.TargetVariantTypes))
                return;

            _logger.Trace("Remix qualifier mismatch: search '{Album}' vs folder '{Folder}'", searchData.Album, candidateLeaf);
            judgement.AlbumMatch = false;
            judgement.Reject("remix or version qualifier differs");
        }

        // Single/EP: pluck, reject or rank by coverage of evidence.Wanted, not expectedTrackCount, so a two-letter
        // title ("Up") that the floor drops doesn't make a complete source look partial.
        private void JudgeSmallTarget(FolderJudgement judgement, IGrouping<string, SlskdFileData> directory, SlskdSearchData searchData, SlskdSettings? settings, int expectedTrackCount, TrackTitleEvidence evidence)
        {
            if (!judgement.Matched || evidence.Wanted == 0 || !IsSingleOrEpTarget(searchData.AlbumType, expectedTrackCount))
                return;

            if (evidence.Covered == 0)
            {
                judgement.Reject("holds none of the wanted tracks");
                _logger.Debug("Single/EP target '{Album}': source '{Dir}' holds none of the wanted tracks — rejecting", searchData.Album, directory.Key);
                return;
            }

            int audioFileCount = directory.Count(IsAudioFile);
            PluckFromBiggerSource(judgement, directory.Key, searchData, expectedTrackCount, evidence, audioFileCount);
            if (expectedTrackCount > 1)
                RankSourceCoherence(judgement, directory.Key, searchData, settings, evidence);

            // Prefer a source that IS the single over a full album we pluck from.
            if (audioFileCount > expectedTrackCount * 2)
                judgement.CoherencePriorityDelta -= AlbumExtractionPenalty;
        }

        // Only from a larger source, and only when every target track is matchable and matched, or a short title is lost.
        private void PluckFromBiggerSource(FolderJudgement judgement, string directoryKey, SlskdSearchData searchData, int expectedTrackCount, TrackTitleEvidence evidence, int audioFileCount)
        {
            if (audioFileCount <= expectedTrackCount || evidence.Wanted != expectedTrackCount || !evidence.CoversEveryTitle || evidence.Files.Count >= audioFileCount)
                return;

            _logger.Debug("Single/EP target '{Album}': plucking {Matched} track(s) from a {Total}-file source '{Dir}'", searchData.Album, evidence.Files.Count, audioFileCount, directoryKey);
            judgement.Files = evidence.Files;
            judgement.Reasons.Add($"took {evidence.Files.Count} of {audioFileCount} audio files");
        }

        // Source coherence for multi-track singles/EPs.
        private void RankSourceCoherence(FolderJudgement judgement, string directoryKey, SlskdSearchData searchData, SlskdSettings? settings, TrackTitleEvidence evidence)
        {
            if (evidence.CoversEveryTitle)
            {
                judgement.CoherencePriorityDelta += CoherentSourceBoost;
            }
            else if (settings?.RequireCoherentSingleSource == true)
            {
                judgement.Reject($"holds {evidence.Covered} of {evidence.Wanted} matchable tracks and Whole Albums Only is on");
                _logger.Debug("Single/EP target '{Album}': source '{Dir}' covers {Covered}/{Wanted} matchable tracks and Whole Albums Only is on — rejecting partial source", searchData.Album, directoryKey, evidence.Covered, evidence.Wanted);
            }
            else
            {
                judgement.CoherencePriorityDelta -= PartialSourcePenalty;
            }
        }

        private void ApplyReleaseTracks(FolderJudgement judgement, string directoryKey, SlskdSearchData searchData, SlskdSettings? settings, int expectedTrackCount, TrackTitleEvidence evidence)
        {
            if (settings?.MatchReleaseTracks != true || !judgement.Matched || string.IsNullOrEmpty(searchData.Album))
                return;

            if (SlskdReleaseTracks.Mismatch([.. judgement.Files.Where(IsAudioFile)], expectedTrackCount, evidence.Covered, evidence.Wanted, searchData.TrackDurations) is not { } mismatch)
                return;

            judgement.Reject($"Match Release Tracks: {mismatch}");
            _logger.Debug("Match Release Tracks: '{Dir}' is not the release of '{Album}' — {Reason}", directoryKey, searchData.Album, mismatch);
        }

        // The verdict while JudgeFolder builds it; each step narrows it.
        private sealed class FolderJudgement(List<SlskdFileData> files)
        {
            public bool Matched { get; set; }

            public bool ArtistMatch { get; set; }

            public bool AlbumMatch { get; set; }

            public List<SlskdFileData> Files { get; set; } = files;

            public int CoherencePriorityDelta { get; set; }

            public List<string> Reasons { get; } = [];

            public void Reject(string reason)
            {
                Matched = false;
                Reasons.Add(reason);
            }
        }

        // The wanted tracks found in the folder's file names (MatchWantedTrackFiles).
        private readonly record struct TrackTitleEvidence(List<SlskdFileData> Files, int Covered, int Wanted)
        {
            public double Share => Wanted > 0 ? Covered / (double)Wanted : 0;

            public bool CoversEveryTitle => Wanted > 0 && Covered >= Wanted;
        }
    }
}
