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

            // Track-title evidence: when enough of the wanted track titles appear
            // in the folder's filenames, the folder IS the album regardless of
            // how it is named. Essential for server-blocked artists whose name
            // never appears in the query or the path.
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

            // Volume-series matching only applies when the album has an explicit
            // volume KEYWORD ("Vol. 3", "Part II"). A bare trailing number is not
            // enough — titles that ARE numbers ("1989", "25", "IV") used to be
            // force-routed here and could never match their own directories.
            bool isVolumeSearch = !string.IsNullOrEmpty(searchData.Album) && VolumeKeywordRegex().Match(searchData.Album).Success;

            judgement.AlbumMatch = isVolumeSearch
                ? CheckVolumeSeriesMatch(directoryKey, searchData.Album, strictnessOffset)
                : IsFuzzyAlbumNameMatch(dirNameNorm, searchAlbumNorm, strictnessOffset) || PathTailNamesAlbum(directoryKey, searchAlbumNorm, strictnessOffset);
            judgement.ArtistMatch = IsFuzzyArtistMatch(dirNameNorm, searchArtistNorm, strictnessOffset);

            judgement.Reasons.Add(judgement.ArtistMatch ? "artist named" : "artist not named");
            if (!string.IsNullOrEmpty(searchAlbumNorm))
                judgement.Reasons.Add(AlbumReason(judgement.AlbumMatch, isVolumeSearch));

            bool titlesCarryIt = evidence.Share >= TrackEvidenceThreshold;
            if (titlesCarryIt)
            {
                judgement.AlbumMatch = true;
                judgement.Reasons.Add($"track titles {(int)Math.Round(evidence.Share * 100, MidpointRounding.AwayFromZero)}%");
            }

            judgement.Matched = string.IsNullOrEmpty(searchAlbumNorm)
                ? judgement.ArtistMatch
                : judgement.AlbumMatch && (judgement.ArtistMatch || string.IsNullOrEmpty(searchArtistNorm) || titlesCarryIt);

            if (!judgement.ArtistMatch && !judgement.AlbumMatch && !string.IsNullOrEmpty(searchData.Artist) && !string.IsNullOrEmpty(searchData.Album))
                JudgeCombinedName(judgement, dirNameNorm, searchData, strictnessOffset);
        }

        private static string AlbumReason(bool albumMatch, bool isVolumeSearch) => isVolumeSearch
            ? albumMatch ? "volume series matches" : "volume series differs"
            : albumMatch ? "album named" : "album not named";

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
            string combinedSearch = NormalizeString($"{searchData.Artist} {searchData.Album}");
            (int combinedPartial, _) = GetCachedFuzzyScores(dirNameNorm, combinedSearch);
            judgement.AlbumMatch = combinedPartial > FuzzyCombinedThreshold + strictnessOffset;
            judgement.Matched = judgement.AlbumMatch;
            judgement.Reasons.Add(judgement.AlbumMatch ? "artist and album named together" : "artist and album not named together");
        }

        // Runs after every album-match route including track evidence: a
        // remix single's filenames CONTAIN the base title, so evidence
        // would otherwise force the false match right back. Leaf AND parent
        // are judged as one candidate — "Album (Live)\FLAC" carries the
        // qualifier one level up.
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

        // Single/EP handling: pluck the wanted track(s) from a bigger album
        // instead of hauling it whole, reject a source with none of them, and
        // rank coherent sources over partial ones. Coverage is measured against
        // evidence.Wanted (titles that survived the three-letter floor), NOT
        // the raw expectedTrackCount — else a two-letter title ("Up")
        // makes a complete source look partial.
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

        // Pluck only from a genuinely larger source, and only when every
        // target track is matchable AND matched — else an unmatchable
        // (short/stop-word) title would be dropped and imported incomplete.
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
