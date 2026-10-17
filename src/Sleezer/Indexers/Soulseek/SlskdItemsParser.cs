using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using FuzzySharp;
using Newtonsoft.Json;
using NLog;
using NzbDrone.Core.Indexers;
using NzbDrone.Plugin.Sleezer.Core.Model;
using NzbDrone.Plugin.Sleezer.Core.Utilities;
using NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek.Models;

namespace NzbDrone.Plugin.Sleezer.Indexers.Soulseek
{
    public partial class SlskdItemsParser : ISlskdItemsParser
    {
        private readonly Logger _logger;

        // Fuzzy matching threshold constants
        private const int FuzzyArtistPartialThreshold = 90;
        private const int FuzzyArtistTokenSortThreshold = 85;
        private const int FuzzyAlbumPartialThreshold = 85;
        private const int FuzzyAlbumTokenSortThreshold = 80;
        private const int FuzzyCombinedThreshold = 85;

        private static readonly ConcurrentDictionary<(string, string), (int Partial, int TokenSort)> FuzzyCache = new();
        private const int MaxCacheSize = 10000;

        private static readonly Dictionary<string, string> _textNumbers = new(StringComparer.OrdinalIgnoreCase)
        {
            { "one", "1" }, { "two", "2" }, { "three", "3" }, { "four", "4" },
            { "five", "5" }, { "six", "6" }, { "seven", "7" }, { "eight", "8" },
            { "nine", "9" }, { "ten", "10" }
        };

        private static readonly Dictionary<char, int> _romanNumerals = new()
        {
            { 'I', 1 }, { 'V', 5 }, { 'X', 10 }, { 'L', 50 },
            { 'C', 100 }, { 'D', 500 }, { 'M', 1000 }
        };

        private static readonly string[] _nonArtistFolders =
        [
            "music", "mp3", "flac", "audio", "compilations", "soundtracks",
            "pop", "rock", "jazz", "classical", "various", "downloads",
            "shared", "share", "shares", "soulseek", "soulseek shared folder",
            "albums", "album", "complete", "incomplete", "media", "lossless",
            "lossy", "singles", "eps", "discography", "collection", "itunes",
            "new music", "sorted", "unsorted"
        ];

        public SlskdItemsParser(Logger logger)
        {
            _logger = logger;
        }

        public SlskdFolderData ParseFolderName(string folderPath)
        {
            string[] pathComponents = SplitPathIntoComponents(folderPath);
            (string? artist, string? album, string? year) = ParseFromRegexPatterns(pathComponents);

            if (string.IsNullOrEmpty(artist) && pathComponents.Length >= 2)
                artist = GetArtistFromParentFolder(pathComponents);
            if (string.IsNullOrEmpty(album) && pathComponents.Length > 0)
                album = CleanComponent(pathComponents[^1]);
            if (string.IsNullOrEmpty(year))
                year = ExtractYearFromPath(folderPath);

            return new SlskdFolderData(
                Path: folderPath,
                Artist: artist ?? "Unknown Artist",
                Album: album ?? "Unknown Album",
                Year: year ?? string.Empty,
                Username: string.Empty,
                HasFreeUploadSlot: false,
                UploadSpeed: 0,
                LockedFileCount: 0,
                LockedFiles: [],
                QueueLength: 0,
                Token: 0,
                FileCount: 0,
                Files: []);
        }

        public AlbumData CreateAlbumData(IGrouping<string, SlskdFileData> directory, SlskdSearchData searchData, SlskdFolderData folderData, SlskdSettings? settings = null, int expectedTrackCount = 0)
        {
            SlskdFolderVerdict verdict = JudgeFolder(directory, searchData, settings, expectedTrackCount);
            List<SlskdFileData> filesToDownload = verdict.FilesToDownload;

            int actualTrackCount = filesToDownload.Count;

            // Determine final values for artist, album, year
            string finalArtist = DetermineFinalArtist(verdict.ArtistMatch, verdict.AlbumMatch, folderData, searchData);
            (string finalAlbum, bool albumIsSearchedTitle) = DetermineFinalAlbum(verdict.AlbumMatch, folderData, searchData);
            string finalYear = folderData.Year;

            // Whitelisted extras (cue/log) must not skew codec/bitrate detection.
            List<SlskdFileData> audioForQuality = filesToDownload.Where(IsAudioFile).ToList();
            (AudioFormat Codec, int? BitRate, int? BitDepth, int? SampleRate, long TotalSize, int TotalDuration)
                = AnalyzeAudioQuality(audioForQuality.Count > 0 ? audioForQuality : filesToDownload);

            _logger.Trace("Audio: {Codec}, BitRate: {BitRate}, BitDepth: {BitDepth}, SampleRate: {SampleRate}, Files: {TrackCount}", Codec, BitRate, BitDepth, SampleRate, actualTrackCount);

            // Points at the peer, not the search: Lidarr renders this as the title's
            // href, so hovering a result reveals which user it came from. Searches are
            // deleted after parsing, so a /searches/ link is usually dead by then.
            string infoUrl = SlskdUrls.Peer(settings, folderData.Username);
            string? edition = ExtractEdition(folderData.Path)?.ToUpper();
            bool labelSoulseek = settings?.LabelSoulseekResults == true;
            // A peer's folder is not a store download: the label drops a WEB claim, but CD/Vinyl/SACD still describe the rip.
            string? labelledMedium = labelSoulseek ? NamedRipMedium(folderData.Path) : null;

            int priority = folderData.CalculatePriority(expectedTrackCount);
            priority += (int)(verdict.TrackEvidence * 400);
            if (TrackLengthMatch(audioForQuality, searchData.TrackDurations) is double lengthMatch)
            {
                if (lengthMatch >= TrackLengthMatchedShare)
                    priority += (int)(lengthMatch * 400);
                else if (lengthMatch < TrackLengthMismatchedShare)
                    priority = (int)(priority * (0.5 + lengthMatch));
            }
            priority += verdict.CoherencePriorityDelta;
            // Gap check runs on the FULL directory, not the narrowed filesToDownload
            // — plucked album tracks keep their original (non-contiguous) numbers,
            // which would otherwise trigger a spurious grab-bag penalty.
            if (HasTrackNumberGaps(directory))
                priority /= 2;
            priority = Math.Clamp(priority, 0, 10000);

            // Surface the peer info (was previously bracketed onto the title
            // with emoji decorations). Lidarr indexes blocklisting by
            // DownloadUrl which already contains the username — this is for
            // operator log review only.
            _logger.Debug(
                "Slskd peer for {Artist} - {Album}: user={User} speed={Speed:F2}MB/s queue={Queue} freeSlot={FreeSlot}",
                finalArtist, finalAlbum, folderData.Username,
                folderData.UploadSpeed / 1024.0 / 1024.0,
                folderData.QueueLength, folderData.HasFreeUploadSlot);

            return new AlbumData("Slskd", nameof(SoulseekDownloadProtocol))
            {
                // username keeps peers distinct through DistinctBy(Guid) (album
                // failover); the file-set hash makes the blocklist per-release.
                Guid = $"Slskd-{folderData.Username}-{SlskdDownloadItem.GetStableMD5Id(filesToDownload.Select(f => f.Filename))}",
                AlbumId = $"/api/v0/transfers/downloads/{folderData.Username}",
                ArtistName = finalArtist,
                AlbumName = finalAlbum,
                AlbumIsSearchedTitle = albumIsSearchedTitle,
                MatchedSearchCriteria = verdict.Matched,
                SourceTag = labelSoulseek ? SoulseekSourceTag : DetectSourceTag(folderData.Path),
                ReleaseDate = finalYear,
                // Folder years are year-precision only — never a publish date
                // (see AlbumData.FillReleaseInfo). Keeps the "(YYYY)" title tag.
                ReleaseDatePrecision = "year",
                ReleaseDateTime = string.IsNullOrEmpty(finalYear) || !int.TryParse(finalYear, out int yearInt)
                    ? DateTime.MinValue
                    : new DateTime(yearInt, 1, 1),
                Codec = Codec,
                BitDepth = BitDepth ?? 0,
                SampleRate = SampleRate ?? 0,
                Bitrate = (Codec == AudioFormat.MP3
                          ? AudioFormatHelper.RoundToStandardBitrate(BitRate ?? 0)
                          : BitRate) ?? 0,
                Size = filesToDownload.Sum(f => f.Size),
                InfoUrl = infoUrl,
                ExplicitContent = ExtractExplicitTag(folderData.Path),
                Priotity = priority,
                CustomString = JsonConvert.SerializeObject(filesToDownload),
                // Peer info (user, speed, queue) used to live in ExtraInfo as
                // bracketed decorations on the release title — visually nice
                // but a maintenance risk: any future change to Lidarr's title
                // parser could choke on the unicode/punctuation. Blocklisting
                // already works by DownloadUrl (which encodes the username),
                // so we drop the decorations from the title and surface peer
                // metadata via a Debug log line below for traceability.
                ExtraInfo = [.. new[] { edition, labelledMedium }.OfType<string>().Where(info => info.Length > 0)],
                Duration = TotalDuration
            };
        }

        private static string[] SplitPathIntoComponents(string path) => path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);

        private static (string? artist, string? album, string? year) ParseFromRegexPatterns(string[] pathComponents)
        {
            if (pathComponents.Length == 0)
                return (null, null, null);

            string lastComponent = pathComponents[^1];

            // Year-led folders ("1969 - Artist - Album", "(2002) Album",
            // "1994 - Album") must be tried before the artist-album split, or the
            // year gets mistaken for the artist.
            if (LeadingYearProbeRegex().IsMatch(lastComponent))
            {
                Match? yearMatch = TryMatchRegex(lastComponent, YearArtistAlbumRegex());
                if (yearMatch != null)
                {
                    return (
                        yearMatch.Groups["artist"].Success ? yearMatch.Groups["artist"].Value.Trim() : null,
                        yearMatch.Groups["album"].Success ? yearMatch.Groups["album"].Value.Trim() : null,
                        yearMatch.Groups["year"].Success ? yearMatch.Groups["year"].Value.Trim() : null);
                }

                yearMatch = TryMatchRegex(lastComponent, LeadingYearAlbumRegex());
                if (yearMatch?.Groups["album"].Success == true)
                {
                    return (
                        GetArtistFromParentFolder(pathComponents),
                        yearMatch.Groups["album"].Value.Trim(),
                        yearMatch.Groups["year"].Success ? yearMatch.Groups["year"].Value.Trim() : null);
                }
            }

            // Try artist-year-album pattern ("Artist - (1994) Album", "Artist - 1994 - Album")
            Match? match = TryMatchRegex(lastComponent, ArtistYearAlbumRegex());
            if (match?.Groups["album"].Success == true)
            {
                return (
                    match.Groups["artist"].Value.Trim(),
                    CleanComponent(match.Groups["album"].Value),
                    match.Groups["year"].Value.Trim());
            }

            // Try artist-album-year pattern
            match = TryMatchRegex(lastComponent, ArtistAlbumYearRegex());
            if (match != null)
            {
                return (
                    match.Groups["artist"].Success ? match.Groups["artist"].Value.Trim() : null,
                    match.Groups["album"].Success ? match.Groups["album"].Value.Trim() : null,
                    match.Groups["year"].Success ? match.Groups["year"].Value.Trim() : null);
            }

            // Try album-year pattern
            match = TryMatchRegex(lastComponent, AlbumYearRegex());
            if (match?.Groups["album"].Success == true)
            {
                string? artist = null;
                if (pathComponents.Length >= 2)
                    artist = GetArtistFromParentFolder(pathComponents);

                return (artist,
                    match.Groups["album"].Value.Trim(),
                    match.Groups["year"].Success ? match.Groups["year"].Value.Trim() : null);
            }

            return (null, null, null);
        }

        private static string? GetArtistFromParentFolder(string[] pathComponents)
        {
            if (pathComponents.Length < 2) return null;
            string parentFolder = pathComponents[^2].Trim();

            // Share roots ("@@abc123"), drive letters and generic library folders
            // are not artist names; better to leave the artist unknown than to
            // fabricate one Lidarr can never match.
            if (parentFolder.StartsWith('@') ||
                parentFolder.Length <= 1 ||
                ShareRootRegex().IsMatch(parentFolder) ||
                _nonArtistFolders.Contains(parentFolder.ToLowerInvariant()))
                return null;

            return parentFolder;
        }

        private bool CheckVolumeSeriesMatch(string directoryPath, string? searchAlbum, int strictnessOffset = 0)
        {
            if (string.IsNullOrEmpty(searchAlbum))
                return false;

            bool isVolumeSeries = VolumeRegex().Match(searchAlbum).Success;
            if (!isVolumeSeries)
                return false;

            Match? searchMatch = VolumeRegex().Match(searchAlbum);
            Match dirMatch = VolumeRegex().Match(directoryPath);

            if (!dirMatch.Success)
                return false;

            string? normSearchVol = NormalizeVolume(searchMatch.Value);
            string? normDirVol = NormalizeVolume(dirMatch.Value);

            string? searchBaseAlbum = VolumeRegex().Replace(searchAlbum, "").Trim();
            string? dirBaseAlbum = VolumeRegex().Replace(directoryPath, "").Trim();

            bool baseAlbumMatch = Fuzz.PartialRatio(
                NormalizeString(dirBaseAlbum),
                NormalizeString(searchBaseAlbum)) > FuzzyAlbumPartialThreshold + strictnessOffset;

            return baseAlbumMatch && normSearchVol.Equals(normDirVol, StringComparison.OrdinalIgnoreCase);
        }

        public string NormalizeVolume(string volume)
        {
            _logger.Trace("Normalizing volume: '{Volume}'", volume);

            if (_textNumbers.TryGetValue(volume, out string? textNum))
                return textNum;
            if (int.TryParse(volume, out int num))
                return num.ToString();

            string normalizedRoman = NormalizeRomanNumeral(volume);
            if (RomanNumeralRegex().IsMatch(normalizedRoman))
            {
                int value = ConvertRomanToNumber(normalizedRoman);
                if (value > 0)
                    return value.ToString();
            }
            Match rangeMatch = VolumeRangeRegex().Match(volume);
            if (rangeMatch.Success && int.TryParse(rangeMatch.Groups[1].Value, out int firstNum))
                return firstNum.ToString();
            return volume.Trim().ToUpperInvariant();
        }

        private static string NormalizeRomanNumeral(string roman)
        {
            if (string.IsNullOrEmpty(roman))
                return string.Empty;

            return roman.Trim().ToUpperInvariant()
                .Replace("IIII", "IV")    // 4
                .Replace("VIIII", "IX")   // 9
                .Replace("XXXX", "XL")    // 40
                .Replace("LXXXX", "XC")   // 90
                .Replace("CCCC", "CD")    // 400
                .Replace("DCCCC", "CM");  // 900
        }

        private int ConvertRomanToNumber(string roman)
        {
            roman = roman.ToUpperInvariant();
            int total = 0;
            int prevValue = 0;

            for (int i = roman.Length - 1; i >= 0; i--)
            {
                if (!_romanNumerals.TryGetValue(roman[i], out int currentValue))
                    return 0;

                if (currentValue < prevValue)
                    total -= currentValue;
                else
                    total += currentValue;

                prevValue = currentValue;
            }

            if (total <= 0 || total > 5000)
                return 0;

            _logger.Trace("Roman numeral '{Roman}' converted to: {Total}", roman, total);
            return total;
        }

        public static string NormalizeString(string input)
        {
            if (string.IsNullOrEmpty(input))
                return string.Empty;

            string normalized = NormalizeWords(input);
            string withoutStopWords = RemoveWordsRegex().Replace(normalized, "");
            withoutStopWords = ReduceWhitespaceRegex().Replace(withoutStopWords, " ").Trim();

            // Names made entirely of stop words ("The The") must not normalize
            // to empty — an empty operand can never fuzzy-match anything.
            return withoutStopWords.Length > 0 ? withoutStopWords : normalized;
        }

        /// <summary>NormalizeString's words with the stop words kept.</summary>
        // Path separators split words like any other mark (ComparableText), so path components stay apart.
        internal static string NormalizeWords(string input) => ComparableText.Of(input);

        /// <summary>True for a word NormalizeString drops as a stop word.</summary>
        internal static bool IsStopWord(string word) => RemoveWordsRegex().Replace(word, string.Empty).Length == 0;

        private static string CleanComponent(string component)
        {
            if (string.IsNullOrEmpty(component))
                return string.Empty;
            component = CleanComponentRegex().Replace(component, "");
            return ReduceWhitespaceRegex().Replace(component.Trim(), " ");
        }

        private bool ExtractExplicitTag(string path)
        {
            Match match = ExplicitTagRegex().Match(path);
            if (match.Success)
            {
                if (match.Groups["negation"].Success && !string.IsNullOrWhiteSpace(match.Groups["negation"].Value))
                {
                    _logger.Trace("Found negated explicit tag in path, skipping: {Match}", match.Value);
                    return false;
                }

                _logger.Trace("Extracted explicit tag from path: {Path}", path);
                return true;
            }
            return false;
        }

        private static string? ExtractEdition(string path)
        {
            Match match = EditionRegex().Match(path);
            return match.Success ? match.Groups["edition"].Value.Trim() : null;
        }

        private static string? ExtractYearFromPath(string path)
        {
            Match yearMatch = YearExtractionRegex().Match(path);
            return yearMatch.Success ? yearMatch.Groups["year"].Value : null;
        }

        private static Match? TryMatchRegex(string input, Regex regex)
        {
            Match match = regex.Match(input);
            return match.Success ? match : null;
        }

        // Below this normalized length, PartialRatio saturates at ~100 against
        // almost any long path ("low" matches "follow", "19" matches "2019"), so
        // short names require an exact word-token hit instead of a fuzzy score.
        private const int ShortNameTokenLength = 4;

        // Whole words in order; fuzzy PartialRatio can score an exact substring as low as 50, so this runs first.
        private static bool ContainsWords(string dirNameNorm, string queryNorm) =>
            $" {dirNameNorm} ".Contains($" {queryNorm} ", StringComparison.Ordinal);

        private static bool IsFuzzyArtistMatch(string dirNameNorm, string searchArtistNorm, int strictnessOffset = 0)
        {
            if (string.IsNullOrEmpty(searchArtistNorm))
                return false;
            if (ContainsWords(dirNameNorm, searchArtistNorm))
                return true;
            if (searchArtistNorm.Length <= ShortNameTokenLength)
                return false;
            (int partial, int tokenSort) = GetCachedFuzzyScores(dirNameNorm, searchArtistNorm);
            return partial > FuzzyArtistPartialThreshold + strictnessOffset || tokenSort > FuzzyArtistTokenSortThreshold + strictnessOffset;
        }

        private static bool IsFuzzyAlbumNameMatch(string dirNameNorm, string searchAlbumNorm, int strictnessOffset = 0)
        {
            if (string.IsNullOrEmpty(searchAlbumNorm))
                return false;
            if (ContainsWords(dirNameNorm, searchAlbumNorm))
                return true;
            if (searchAlbumNorm.Length <= ShortNameTokenLength)
                return false;
            (int partial, int tokenSort) = GetCachedFuzzyScores(dirNameNorm, searchAlbumNorm);
            return partial > FuzzyAlbumPartialThreshold + strictnessOffset || tokenSort > FuzzyAlbumTokenSortThreshold + strictnessOffset;
        }

        /// <summary>
        /// Gets fuzzy matching scores with caching to avoid redundant calculations.
        /// </summary>
        private static (int Partial, int TokenSort) GetCachedFuzzyScores(string str1, string str2)
        {
            if (string.IsNullOrEmpty(str1) || string.IsNullOrEmpty(str2))
                return (0, 0);

            // Create a consistent cache key (alphabetically ordered to ensure cache hits)
            (string, string) cacheKey = string.Compare(str1, str2, StringComparison.Ordinal) < 0
                ? (str1, str2)
                : (str2, str1);

            return FuzzyCache.GetOrAdd(cacheKey, key =>
            {
                // Clear cache if it gets too large to prevent memory issues
                if (FuzzyCache.Count > MaxCacheSize)
                {
                    FuzzyCache.Clear();
                }

                return (
                    Fuzz.PartialRatio(key.Item1, key.Item2),
                    Fuzz.TokenSortRatio(key.Item1, key.Item2)
                );
            });
        }

        private static string DetermineFinalArtist(bool isArtistMatch, bool isAlbumMatch, SlskdFolderData folderData, SlskdSearchData searchData)
        {
            if (isArtistMatch && !string.IsNullOrEmpty(searchData.Artist))
                return searchData.Artist;

            // Album matched but the artist name isn't in the path (flat share
            // layouts, "@@share" roots): trust the album evidence and stamp the
            // searched artist — a folder-derived "@@abc123" artist would make
            // Lidarr reject an otherwise-correct release.
            if (isAlbumMatch && !string.IsNullOrEmpty(searchData.Artist))
                return searchData.Artist;

            if (!string.IsNullOrEmpty(folderData.Artist))
                return folderData.Artist;
            return searchData.Artist ?? "Unknown Artist";
        }

        // True when the album is the searched title, so its brackets must reach Lidarr intact.
        private static (string Album, bool IsSearchedTitle) DetermineFinalAlbum(bool isAlbumMatch, SlskdFolderData folderData, SlskdSearchData searchData)
        {
            if (isAlbumMatch && !string.IsNullOrEmpty(searchData.Album))
            {
                // Carry an explicit keyworded volume marker ("Vol. 3") over from
                // the folder; a bare trailing number ("...CD 1", "1989") must not
                // be appended to the searched title.
                Match folderVersion = VolumeKeywordRegex().Match(folderData.Album ?? "");
                Match searchVersion = VolumeKeywordRegex().Match(searchData.Album);
                return (folderVersion.Success && !searchVersion.Success ? $"{searchData.Album} {folderVersion.Value}" : searchData.Album, true);
            }
            if (!string.IsNullOrEmpty(folderData.Album))
                return (CleanFallbackAlbum(folderData.Album, searchData.Artist), false);
            return (searchData.Album ?? "Unknown Album", !string.IsNullOrEmpty(searchData.Album));
        }

        /// <summary>
        /// Cleans a folder-derived album name before it lands in the release
        /// title: strips share junk (usernames, dates, GUIDs) and an embedded
        /// artist name, so Lidarr sees "Album" rather than "@user Artist Album".
        /// </summary>
        private static string CleanFallbackAlbum(string album, string? artist)
        {
            string cleaned = JunkTokenRegex().Replace(album, " ");

            string normArtist = string.IsNullOrEmpty(artist) ? string.Empty : NormalizeString(artist);
            if (normArtist.Length > 2)
            {
                string[] words = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                string[] artistWords = normArtist.Split(' ');
                List<string> output = [];
                int i = 0;
                while (i < words.Length)
                {
                    if (i + artistWords.Length <= words.Length &&
                        NormalizeString(string.Join(' ', words[i..(i + artistWords.Length)])) == normArtist)
                    {
                        i += artistWords.Length;
                        continue;
                    }
                    output.Add(words[i]);
                    i++;
                }
                if (output.Count > 0)
                    cleaned = string.Join(' ', output);
            }

            cleaned = ReduceWhitespaceRegex().Replace(cleaned, " ").Trim(' ', '-', ':', '–');
            return cleaned.Length >= 2 ? cleaned : album;
        }

        private const double TrackEvidenceThreshold = 0.35;

        // Fallback ceiling for the single/EP gate when the search carries no
        // AlbumType (searches queued by an older build).
        private const int SmallTargetTrackCeiling = 6;

        /// <summary>
        /// Album-extraction/coherence handling applies only to single/EP targets.
        /// Prefer MusicBrainz's primary type; a real 6-track album should not get
        /// single handling just because it is short.
        /// </summary>
        private static bool IsSingleOrEpTarget(string? albumType, int expectedTrackCount)
        {
            if (expectedTrackCount <= 0)
                return false;

            // Only a NULL AlbumType means the search predates the field; an empty
            // one is a real value and must not re-enable the track-count guess.
            if (albumType is not null)
            {
                return albumType.Equals("Single", StringComparison.OrdinalIgnoreCase)
                    || albumType.Equals("EP", StringComparison.OrdinalIgnoreCase);
            }

            return expectedTrackCount <= SmallTargetTrackCeiling;
        }
        private const int CoherentSourceBoost = 1500;   // source holds every wanted track
        private const int PartialSourcePenalty = 1200;  // source holds only some (would stitch)
        private const int AlbumExtractionPenalty = 800; // source is a full album we pluck from

        private static bool IsAudioFile(SlskdFileData f)
        {
            // Fall back to the filename when Extension is null OR empty (?? only
            // catches null), matching GetMostCommonExtension — else an empty
            // Extension drops a valid track.flac.
            string? ext = string.IsNullOrEmpty(f.Extension) ? Path.GetExtension(f.Filename) : f.Extension;
            return AudioFormatHelper.GetAudioCodecFromExtension(ext ?? string.Empty) != AudioFormat.Unknown;
        }

        /// <summary>
        /// Maps wanted track titles onto the folder's files. Returns the files
        /// in whose (track-number-stripped) filename a wanted title stands alone, the
        /// number of DISTINCT wanted tracks covered, and the number of wanted
        /// titles long enough to match on (the track-evidence denominator).
        /// </summary>
        private static (List<SlskdFileData> MatchedFiles, int CoveredTracks, int WantedTitles) MatchWantedTrackFiles(
            IEnumerable<SlskdFileData> files, List<string>? expectedTracks, IReadOnlyCollection<string>? targetVariantTypes = null)
        {
            List<SlskdFileData> matched = [];
            if (expectedTracks == null || expectedTracks.Count == 0)
                return (matched, 0, 0);

            // Raw title kept alongside the normalized one: normalization strips the
            // brackets a variant qualifier lives in ("(Radio Edit)").
            List<(string Raw, string Norm, bool Qualified, (string[] Head, string[] Qualifier) Words)> titles = expectedTracks
                .Select(t => (Raw: t, Norm: NormalizeString(t), Qualified: VariantQualifiers.HasVariantQualifier(t), Words: SlskdTitleMatch.TitleWords(t)))
                // Three letters: "U.F.O." joins to "ufo".
                .Where(t => t.Norm.Length >= 3)
                .ToList();
            if (titles.Count == 0)
                return (matched, 0, 0);

            // Audio files only (a same-named .cue/.nfo must not outrank the track);
            // basename after normalizing backslashes (Path.* won't split them on Linux).
            List<(SlskdFileData File, List<string> Tokens, string Raw, bool Qualified)> named = files
                .Where(IsAudioFile)
                .Select(f =>
                {
                    string raw = TrackNumberPrefixRegex().Replace(Path.GetFileNameWithoutExtension((f.Filename ?? string.Empty).Replace('\\', '/')), string.Empty);
                    return (File: f, Tokens: SlskdTitleMatch.Tokens(raw), Raw: raw, Qualified: VariantQualifiers.HasVariantQualifier(raw));
                })
                .Where(x => x.Tokens.Count > 1)
                .ToList();
            if (named.Count == 0)
                return (matched, 0, titles.Count);

            // One-to-one: each title claims a DISTINCT file, longest title first,
            // so one filename can't satisfy overlapping titles ("Falling" vs
            // "Falling Slowly") and make an incomplete source look complete.
            HashSet<int> claimed = [];
            int covered = 0;
            foreach ((string Raw, string Norm, bool Qualified, (string[] Head, string[] Qualifier) Words) title in titles.OrderByDescending(t => t.Norm.Length))
            {
                int best = PickTitleFile(named, claimed, title.Raw, title.Qualified, title.Words, targetVariantTypes);
                if (best < 0)
                    continue;

                claimed.Add(best);
                covered++;
                if (named[best].File.Filename != null)
                    matched.Add(named[best].File);
            }

            return (matched, covered, titles.Count);
        }

        /// <summary>The unclaimed file a wanted title stands alone in, preferring one whose name ends with it; -1 when none.</summary>
        private static int PickTitleFile(
            List<(SlskdFileData File, List<string> Tokens, string Raw, bool Qualified)> named, HashSet<int> claimed,
            string titleRaw, bool titleQualified, (string[] Head, string[] Qualifier) titleWords, IReadOnlyCollection<string>? targetVariantTypes)
        {
            int best = -1;
            for (int i = 0; i < named.Count; i++)
            {
                // Stands alone, not merely contained: "Tide" must not claim "Low Tide Rising".
                int wordsAfter = claimed.Contains(i) ? -1 : SlskdTitleMatch.WordsAfter(named[i].Tokens, titleWords);
                if (wordsAfter < 0)
                    continue;

                // A wanted title is contained in its own variant ("Proposition"
                // in "Proposition (Radio Edit)"); target types forgive valid ones.
                if ((titleQualified || named[i].Qualified) &&
                    VariantQualifiers.RemixSignaturesConflict(titleRaw, named[i].Raw, targetVariantTypes))
                    continue;

                if (best < 0)
                    best = i;

                // A file the title ends beats an earlier one with words after it: "Tide" over "Tide (Reprise)".
                if (wordsAfter == 0)
                    return i;
            }

            return best;
        }

        private const double TrackLengthMatchedShare = 0.8;
        private const double TrackLengthMismatchedShare = 0.5;

        /// <summary>
        /// Share of the smaller side (files or release tracks) paired within <see cref="SlskdReleaseTracks.LengthToleranceSeconds"/>
        /// of each other, each used once; null when either side has too few lengths to judge.
        /// </summary>
        internal static double? TrackLengthMatch(IReadOnlyCollection<SlskdFileData> files, IReadOnlyList<int>? releaseTrackMs)
        {
            if (releaseTrackMs is not { Count: > 0 } || files.Count == 0)
                return null;

            List<int> expected = SlskdReleaseTracks.ReleaseSeconds(releaseTrackMs);
            List<int> actual = SlskdReleaseTracks.FileSeconds(files);
            if (expected.Count < releaseTrackMs.Count * 0.8 || actual.Count < files.Count * 0.8)
                return null;

            return SlskdReleaseTracks.Paired(expected, actual) / (double)Math.Min(expected.Count, actual.Count);
        }

        // Non-contiguous track numbers usually mean a partial rip or a mixed
        // folder — penalized in priority, not excluded outright.
        private static bool HasTrackNumberGaps(IEnumerable<SlskdFileData> files)
        {
            List<int> numbers = files
                .Select(f => TrackNumberPrefixCaptureRegex().Match(Path.GetFileName(f.Filename ?? string.Empty)))
                .Where(m => m.Success)
                .Select(m => int.Parse(m.Groups[1].Value))
                .Where(n => n is > 0 and <= 150)
                .Distinct()
                .Order()
                .ToList();

            return numbers.Count >= 3 && numbers[^1] - numbers[0] + 1 != numbers.Count;
        }

        private const string SoulseekSourceTag = "Soulseek";

        private static string DetectSourceTag(string path)
        {
            Match match = SourceTagRegex().Match(path);
            return match.Success ? SourceTagFor(match.Value) : "WEB";
        }

        /// <summary>The CD/Vinyl/SACD named nearest the album folder, skipping WEB; null when the path names none.</summary>
        private static string? NamedRipMedium(string path) =>
            SourceTagRegex().Matches(path).Select(m => SourceTagFor(m.Value)).LastOrDefault(tag => tag != "WEB");

        private static string SourceTagFor(string word)
        {
            string value = word.ToUpperInvariant();
            return value switch
            {
                "VINYL" => "Vinyl",
                "WEB" => "WEB",
                "SACD" or "MFSL" or "MOFI" => "SACD",
                _ when value.StartsWith("DSD") => "SACD",
                _ => "CD"
            };
        }

        // Soulseek offers no way to read a file header before transferring, so the
        // peer's advertised attributes are the only pre-download signal. These two
        // checks are what can still be said about them without the bytes.

        /// <summary>The value every file reports, or null if they disagree or any is missing.</summary>
        internal static int? UnanimousOrNull(IEnumerable<SlskdFileData> files, Func<SlskdFileData, int?> selector)
        {
            int? agreed = null;

            foreach (SlskdFileData file in files)
            {
                int? value = selector(file);
                if (value is null or <= 0)
                    return null;

                agreed ??= value;
                if (value != agreed)
                    return null;
            }

            return agreed;
        }

        // A heuristic, not a proof: FLAC has no guaranteed floor, so this only says a
        // claim looks unlikely. Assumes mono — the lowest floor any file could have —
        // so it flags only the egregious. 24/44.1 against 16/44.1 overlaps entirely.
        private const double FlacCompressionFloor = 0.5;

        internal static bool DepthClaimIsPlausible(int bitDepth, int? sampleRate, long totalSize, int totalDurationSeconds)
        {
            if (sampleRate is null or <= 0 || totalDurationSeconds <= 0 || totalSize <= 0)
                return true;

            double impliedBitsPerSecond = totalSize * 8.0 / totalDurationSeconds;
            double monoFloor = bitDepth * (double)sampleRate.Value * FlacCompressionFloor;

            return impliedBitsPerSecond >= monoFloor;
        }

        internal (AudioFormat Codec, int? BitRate, int? BitDepth, int? SampleRate, long TotalSize, int TotalDuration) AnalyzeAudioQuality(IEnumerable<SlskdFileData> directory)
        {
            string? commonExt = GetMostCommonExtension(directory);
            long totalSize = directory.Sum(f => f.Size);
            int totalDuration = directory.Sum(f => f.Length ?? 0);

            // Bitrate legitimately varies per track, so the majority value (or the
            // computed average below) is the honest summary. Bit depth and sample rate
            // do not: a folder mixing them cannot advertise one without misdescribing
            // the rest, so those are claimed only when every file agrees.
            int? commonBitRate = directory.GroupBy(f => f.BitRate).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;
            int? commonBitDepth = UnanimousOrNull(directory, f => f.BitDepth);
            int? commonSampleRate = UnanimousOrNull(directory, f => f.SampleRate);

            // Reported, never enforced: dropping the claim would demote a truthful
            // hi-res release. AudioQualityVerifier settles it once the bytes land.
            if (commonBitDepth.HasValue && !DepthClaimIsPlausible(commonBitDepth.Value, commonSampleRate, totalSize, totalDuration))
            {
                _logger.Warn("Slskd: the advertised {BitDepth}-bit/{SampleRate}Hz claim looks implausible — {Size} bytes over {Duration}s. Keeping it; post-download verification will confirm what actually arrived.",
                    commonBitDepth, commonSampleRate, totalSize, totalDuration);
            }

            if (!commonBitRate.HasValue && totalDuration > 0)
            {
                commonBitRate = (int)(totalSize * 8 / (totalDuration * 1000));
                _logger.Trace("Calculated bitrate: {Bitrate}", commonBitRate);
            }

            AudioFormat codec = AudioFormatHelper.GetAudioCodecFromExtension(commonExt ?? "");

            return (codec, commonBitRate, commonBitDepth, commonSampleRate, totalSize, totalDuration);
        }

        public static string? GetMostCommonExtension(IEnumerable<SlskdFileData> files)
        {
            List<string?> extensions = files
                .Select(f => string.IsNullOrEmpty(f.Extension)
                    ? Path.GetExtension(f.Filename)?.TrimStart('.').ToLowerInvariant()
                    : f.Extension)
                .Where(ext => !string.IsNullOrEmpty(ext))
                .ToList();

            if (extensions.Count == 0)
                return null;

            return extensions
                .GroupBy(ext => ext)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key)
                .FirstOrDefault();
        }

        [GeneratedRegex(@"(?ix)
            \[(?:FLAC|MP3|320|WEB|CD)[^\]]*\]|           # Audio format tags
            \(\d{5,}\)|                                  # Long numbers in parentheses
            \(\d+bit[\/\s]\d+[^\)]*\)|                   # Bit depth/sample rate
            \((?:DELUXE_)?EDITION\)|                     # Edition markers
            \s*\([^)]*edition[^)]*\)|                    # Any edition in parentheses
            \((?:Album|Single|EP|LP)\)|                  # Release type
            \s*\(remaster(?:ed)?\)|                      # Remaster tags
            \s*[\(\[][^)\]]*(?:version|reissue)[^)\]]*[\)\]]| # Version/reissue in brackets/parens
            \s*\d{4}\s*remaster|                        # Year remaster
            \s-\s.*$", RegexOptions.ExplicitCapture | RegexOptions.Compiled, "de-DE")]
        private static partial Regex CleanComponentRegex();

        [GeneratedRegex(@"(?ix)
            (?<=\b(?:volume|vol|part|pt|chapter|ep|sampler|remix(?:es)?|mix(?:es)?|edition|ed|version|ver|v|release|issue|series|no|num|phase|stage|book|side|disc|cd|dvd|track|season|installment|\#)\s*[.,\-_:#]*\s*)
            (\d+(?:\.\d+)?|[IVXLCDM]+|\d+(?:[-to&]\d+)?|one|two|three|four|five|six|seven|eight|nine|ten)(?!\w)|
            (\d+(?:\.\d+)?|[IVXLCDM]+)(?=\s*$)", RegexOptions.ExplicitCapture | RegexOptions.Compiled, "de-DE")]
        private static partial Regex VolumeRegex();

        [GeneratedRegex(@"^(?<album>[^(\[]+)(?:\s*[\(\[](?<year>19\d{2}|20\d{2})[\)\]])?", RegexOptions.ExplicitCapture | RegexOptions.Compiled)]
        private static partial Regex AlbumYearRegex();

        // Artist/album splits require " - " (space-hyphen-space): hyphenated
        // names ("Jay-Z", "AC-DC") must not be cut at their internal hyphen.
        [GeneratedRegex(@"^(?<year>19\d{2}|20\d{2})\s*-\s*(?<artist>.+?)\s-\s(?<album>.+)$", RegexOptions.ExplicitCapture | RegexOptions.Compiled)]
        private static partial Regex YearArtistAlbumRegex();

        [GeneratedRegex(@"^[\(\[]?(?<year>19\d{2}|20\d{2})[\)\]]?\s*(?:-\s*)?(?<album>.+)$", RegexOptions.ExplicitCapture | RegexOptions.Compiled)]
        private static partial Regex LeadingYearAlbumRegex();

        [GeneratedRegex(@"^[\(\[]?(?:19|20)\d{2}\b", RegexOptions.ExplicitCapture | RegexOptions.Compiled)]
        private static partial Regex LeadingYearProbeRegex();

        [GeneratedRegex(@"^(?<artist>.+?)\s-\s(?<album>[^(\[]+)(?:\s*[\(\[](?<year>19\d{2}|20\d{2})[\)\]])?", RegexOptions.ExplicitCapture | RegexOptions.Compiled)]
        private static partial Regex ArtistAlbumYearRegex();

        [GeneratedRegex(@"^(?<artist>.+?)\s-\s(?:[\(\[](?<year>19\d{2}|20\d{2})[\)\]]|(?<year>19\d{2}|20\d{2})\s*-)\s*(?<album>.+)$", RegexOptions.Compiled)]
        private static partial Regex ArtistYearAlbumRegex();

        [GeneratedRegex(@"(?i)@\w+|\((?:19|20)\d{2}-\d{2}-\d{2}\)|[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", RegexOptions.Compiled)]
        private static partial Regex JunkTokenRegex();

        [GeneratedRegex(@"^\s*[a-dA-D]?\d{1,3}\s*[-._) ]+\s*", RegexOptions.Compiled)]
        private static partial Regex TrackNumberPrefixRegex();

        [GeneratedRegex(@"^\s*(\d{1,3})\b", RegexOptions.Compiled)]
        private static partial Regex TrackNumberPrefixCaptureRegex();

        [GeneratedRegex(@"(?i)\b(vinyl|sacd|dsd\d*|mfsl|mofi|web|cd)\b", RegexOptions.Compiled)]
        private static partial Regex SourceTagRegex();

        [GeneratedRegex(@"^[a-zA-Z]:?$|^(?:vol(?:ume)?|cd|disc|disk)\s*\d*$", RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture | RegexOptions.Compiled)]
        private static partial Regex ShareRootRegex();

        [GeneratedRegex(@"(?ix)
            (?<=\b(?:volume|vol|part|pt|chapter|sampler|ed|ver|release|issue|series|no|num|phase|stage|book|side|disc|cd|dvd|season|installment|\#)\s*[.,\-_:#]*\s*)
            (\d+(?:\.\d+)?|[IVXLCDM]+|\d+(?:[-to&]\d+)?|one|two|three|four|five|six|seven|eight|nine|ten)(?!\w)", RegexOptions.ExplicitCapture | RegexOptions.Compiled, "de-DE")]
        private static partial Regex VolumeKeywordRegex();

        [GeneratedRegex(@"(?<year>19\d{2}|20\d{2})", RegexOptions.ExplicitCapture | RegexOptions.Compiled)]
        private static partial Regex YearExtractionRegex();

        [GeneratedRegex(@"(?:^|\s|\(|\[)(?<negation>Non-?|Not\s+)?Explicit(?:\s|\)|\]|$)", RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture | RegexOptions.Compiled, "de-DE")]
        private static partial Regex ExplicitTagRegex();

        [GeneratedRegex(@"[\(\[](?<edition>(?:(?:Super\s+)?Deluxe|Limited|Special|Expanded|Extended|Anniversary|Remaster(?:ed)?|Live|Acoustic|Unplugged|Japanese|Bonus|Instrumental|Collector'?s|Metal|Platinum|Gold|Clean|Tour|Censored|Uncensored|\d*\s*CD)(?:\s+(?:Edition|Version|Album|Tracks?|Exclusive))?|[^)\]]+?\s+(?:Edition|Version))[\)\]]", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
        private static partial Regex EditionRegex();

        [GeneratedRegex(@"\b(?:the|a|an|feat|featuring|ft|presents|pres|with|and)\b", RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture | RegexOptions.Compiled, "de-DE")]
        private static partial Regex RemoveWordsRegex();

        [GeneratedRegex(@"(\d+)(?:[-to&]\s*\d+)?", RegexOptions.ExplicitCapture | RegexOptions.Compiled)]
        private static partial Regex VolumeRangeRegex();

        [GeneratedRegex(@"^M{0,4}(?:CM|CD|D?C{0,3})(?:XC|XL|L?X{0,3})(?:IX|IV|V?I{0,3})$", RegexOptions.ExplicitCapture | RegexOptions.Compiled)]
        private static partial Regex RomanNumeralRegex();

        [GeneratedRegex(@"\s+", RegexOptions.Compiled)]
        private static partial Regex ReduceWhitespaceRegex();
    }
}