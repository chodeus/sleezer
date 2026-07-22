using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using FuzzySharp;

namespace NzbDrone.Plugin.Sleezer.Indexers.Soulseek
{
    /// <summary>
    /// Handles text processing, normalization, and variations for search queries
    /// </summary>
    public static partial class SlskdTextProcessor
    {
        private static readonly Dictionary<string, int> RomanNumerals = new(StringComparer.OrdinalIgnoreCase)
        {
            { "I", 1 }, { "II", 2 }, { "III", 3 }, { "IV", 4 }, { "V", 5 },
            { "VI", 6 }, { "VII", 7 }, { "VIII", 8 }, { "IX", 9 }, { "X", 10 },
            { "XI", 11 }, { "XII", 12 }, { "XIII", 13 }, { "XIV", 14 }, { "XV", 15 },
            { "XVI", 16 }, { "XVII", 17 }, { "XVIII", 18 }, { "XIX", 19 }, { "XX", 20 }
        };

        private static readonly string[] VolumeFormats = { "Volume", "Vol.", "Vol", "v", "V" };
        private static readonly Regex PunctuationPattern = new(@"[^\w\s-&]", RegexOptions.Compiled);
        private static readonly Regex VolumePattern = new(@"(Vol(?:ume)?\.?)\s*([0-9]+|[IVXLCDM]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RomanNumeralPattern = new(@"\b([IVXLCDM]+)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Terms the Soulseek SERVER silently filters — any search containing one
        // returns zero results network-wide, regardless of client. Discovered
        // empirically upstream (TypNull/Tubifarry ae3ce28); mostly DMCA-listed
        // artists plus a few collateral words. Case-insensitive whole-word match.
        private static readonly HashSet<string> BlockedSearchTerms = new(StringComparer.OrdinalIgnoreCase)
        {
            "beyonce",
            "Beyoncé",
            "beyoncè",
            "gorillaz",
            "depeche mode",
            "village people",
            "chicane",
            "bryan",
            "cat power",
            "lady gaga",
            "michael jackson",
            "beatles",
            "adele",
            "ymca",
            "lemonade",
            "macho man",
            "rihanna",
            "weeknd",
            "kanye west",
            "kendrick lamar",
            "frank ocean",
            "minaj",
            "linkin park"
        };

        private static readonly string[][] BlockedTermWords = [.. BlockedSearchTerms
            .OrderByDescending(t => t.Length)
            .Select(t => t.Split(' ', StringSplitOptions.RemoveEmptyEntries))];

        public static string BuildSearchText(string? artist, string? album)
            => string.Join(" ", new[] { album, artist }.Where(term => !string.IsNullOrWhiteSpace(term)).Select(term => term?.Trim()));

        public static bool ShouldNormalizeCharacters(string? artist, string? album)
        {
            string? normalizedArtist = artist != null ? NormalizeSpecialCharacters(artist) : null;
            string? normalizedAlbum = album != null ? NormalizeSpecialCharacters(album) : null;
            return (normalizedArtist != null && !string.Equals(normalizedArtist, artist, StringComparison.OrdinalIgnoreCase)) ||
                   (normalizedAlbum != null && !string.Equals(normalizedAlbum, album, StringComparison.OrdinalIgnoreCase));
        }

        public static bool ShouldStripPunctuation(string? artist, string? album)
        {
            string? strippedArtist = artist != null ? StripPunctuation(artist) : null;
            string? strippedAlbum = album != null ? StripPunctuation(album) : null;
            return (strippedArtist != null && !string.Equals(strippedArtist, artist, StringComparison.OrdinalIgnoreCase)) ||
                   (strippedAlbum != null && !string.Equals(strippedAlbum, album, StringComparison.OrdinalIgnoreCase));
        }

        public static bool IsVariousArtists(string artist)
            => artist.Equals("Various Artists", StringComparison.OrdinalIgnoreCase) || artist.Equals("VA", StringComparison.OrdinalIgnoreCase);

        public static bool ContainsVolumeReference(string album)
            => album.Contains("Volume", StringComparison.OrdinalIgnoreCase) || album.Contains("Vol", StringComparison.OrdinalIgnoreCase);

        public static bool ShouldGenerateRomanVariations(string album)
        {
            Match romanMatch = RomanNumeralPattern.Match(album);
            if (!romanMatch.Success) return false;

            Match volumeMatch = VolumePattern.Match(album);
            return !(volumeMatch.Success && volumeMatch.Groups[2].Value.Equals(romanMatch.Groups[1].Value, StringComparison.OrdinalIgnoreCase));
        }

        public static string StripPunctuation(string? input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;

            string stripped = PunctuationPattern.Replace(input, "");
            return StripPunctuationRegex().Replace(stripped, " ").Trim();
        }

        public static string NormalizeSpecialCharacters(string? input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;

            bool isAscii = true;
            foreach (char c in input)
            {
                if (c > 127)
                {
                    isAscii = false;
                    break;
                }
            }
            if (isAscii) return input;

            string decomposed = input.Normalize(NormalizationForm.FormD);
            StringBuilder sb = new(decomposed.Length);

            foreach (char c in decomposed)
            {
                UnicodeCategory cat = CharUnicodeInfo.GetUnicodeCategory(c);
                if (cat != UnicodeCategory.NonSpacingMark && cat != UnicodeCategory.SpacingCombiningMark && cat != UnicodeCategory.EnclosingMark)
                    sb.Append(c);
            }

            return sb.ToString().Normalize(NormalizationForm.FormC);
        }

        public static IEnumerable<string> GenerateVolumeVariations(string album)
        {
            if (string.IsNullOrEmpty(album)) yield break;

            Match volumeMatch = VolumePattern.Match(album);
            if (!volumeMatch.Success) yield break;

            string volumeFormat = volumeMatch.Groups[1].Value;
            string volumeNumber = volumeMatch.Groups[2].Value;

            if (RomanNumerals.TryGetValue(volumeNumber, out int arabicNumber))
            {
                yield return album.Replace(volumeMatch.Value, $"{volumeFormat} {arabicNumber}");
            }
            else if (int.TryParse(volumeNumber, out arabicNumber) && arabicNumber > 0 && arabicNumber <= 20)
            {
                KeyValuePair<string, int> romanPair = RomanNumerals.FirstOrDefault(x => x.Value == arabicNumber);
                if (!string.IsNullOrEmpty(romanPair.Key))
                    yield return album.Replace(volumeMatch.Value, $"{volumeFormat} {romanPair.Key}");
            }
            foreach (string format in VolumeFormats)
            {
                if (!format.Equals(volumeFormat, StringComparison.OrdinalIgnoreCase))
                    yield return album.Replace(volumeMatch.Value, $"{format} {volumeNumber}");
            }
            if (album.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 3)
            {
                string withoutVolume = album.Replace(volumeMatch.Value, "").Trim();
                if (withoutVolume.Length > 10)
                    yield return withoutVolume;
            }
        }

        public static IEnumerable<string> GenerateRomanNumeralVariations(string album)
        {
            if (string.IsNullOrEmpty(album)) yield break;

            Match romanMatch = RomanNumeralPattern.Match(album);
            if (!romanMatch.Success) yield break;
            Match volumeMatch = VolumePattern.Match(album);
            if (volumeMatch.Success && volumeMatch.Groups[2].Value.Equals(romanMatch.Groups[1].Value, StringComparison.OrdinalIgnoreCase))
                yield break;

            string romanNumeral = romanMatch.Groups[1].Value;
            if (RomanNumerals.TryGetValue(romanNumeral, out int arabicNumber))
                yield return album.Replace(romanMatch.Value, arabicNumber.ToString());
        }

        /// <summary>
        /// Removes any blocked term (whole-word, case-insensitive) from a query.
        /// </summary>
        public static string RemoveBlockedTerms(string searchText)
        {
            if (string.IsNullOrWhiteSpace(searchText))
                return string.Empty;

            string[] words = searchText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            bool[] removed = new bool[words.Length];

            foreach (string[] termWords in BlockedTermWords)
            {
                for (int i = 0; i + termWords.Length <= words.Length; i++)
                {
                    bool match = !removed[i];
                    for (int j = 0; match && j < termWords.Length; j++)
                        match = !removed[i + j] && string.Equals(words[i + j], termWords[j], StringComparison.OrdinalIgnoreCase);
                    if (match)
                        for (int j = 0; j < termWords.Length; j++)
                            removed[i + j] = true;
                }
            }

            return string.Join(' ', words.Where((_, i) => !removed[i]));
        }

        public static bool ContainsBlockedTerms(string searchText)
        {
            if (string.IsNullOrWhiteSpace(searchText))
                return false;

            string[] words = searchText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return RemoveBlockedTerms(searchText).Split(' ', StringSplitOptions.RemoveEmptyEntries).Length != words.Length;
        }

        private static readonly Dictionary<char, string> AccentInjectionMap = new()
        {
            { 'a', "àáâä" }, { 'A', "ÀÁÂÄ" },
            { 'e', "èéêë" }, { 'E', "ÈÉÊË" },
            { 'i', "ìíîï" }, { 'I', "ÌÍÎÏ" },
            { 'o', "òóôö" }, { 'O', "ÒÓÔÖ" },
            { 'u', "ùúûü" }, { 'U', "ÙÚÛÜ" },
            { 'y', "ýÿ" }, { 'Y', "ÝŸ" },
            { 'c', "çć" }, { 'C', "ÇĆ" },
            { 'n', "ñń" }, { 'N', "ÑŃ" },
            { 's', "śş" }, { 'S', "ŚŞ" },
        };

        /// <summary>
        /// Rewrites blocked terms by injecting an accented character so the
        /// query slips past the Soulseek server filter while still matching
        /// share paths that use accented spellings. When no accent can be
        /// injected the blocked term is dropped entirely.
        /// </summary>
        public static string RewriteRestrictedTerms(string searchText, int variant = 0)
        {
            if (string.IsNullOrWhiteSpace(searchText))
                return string.Empty;

            string[] words = searchText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string[] normalized = [.. words.Select(NormalizeSpecialCharacters)];
            bool[] keep = [.. words.Select(_ => true)];

            foreach (string[] termWords in BlockedTermWords)
                for (int i = 0; i + termWords.Length <= words.Length; i++)
                {
                    bool match = true;
                    for (int j = 0; match && j < termWords.Length; j++)
                        match = string.Equals(normalized[i + j], termWords[j], StringComparison.OrdinalIgnoreCase);
                    if (!match)
                        continue;

                    bool injected = false;
                    for (int j = 0; j < termWords.Length && !injected; j++)
                        injected = TryInjectAccent(ref words[i + j], variant);

                    if (!injected)
                        for (int j = 0; j < termWords.Length; j++)
                            keep[i + j] = false;
                }

            return string.Join(' ', words.Where((_, i) => keep[i]));
        }

        private static bool TryInjectAccent(ref string word, int variant)
        {
            for (int c = 0; c < word.Length; c++)
            {
                if (AccentInjectionMap.TryGetValue(word[c], out string? variants))
                {
                    word = word[..c] + variants[variant % variants.Length] + word[(c + 1)..];
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Distinctive track titles usable as search evidence when the artist
        /// name itself is server-blocked: cleaned, non-blocked, distinct from
        /// the album title, longest first.
        /// </summary>
        public static IEnumerable<string> GetBlockedTermEvidenceTracks(IEnumerable<string> tracks, string? album)
        {
            return tracks
                .Select(CleanEvidenceTitle)
                .Where(t => t.Length >= 3
                            && !ContainsBlockedTerms(t)
                            && !string.Equals(t, album?.Trim(), StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(t => t.Length);

            static string CleanEvidenceTitle(string title)
            {
                string cleaned = BracketedContentRegex().Replace(title, " ");
                cleaned = StripPunctuation(cleaned);
                return cleaned.Trim();
            }
        }

        private static readonly char[] PathSeparators = ['\\', '/'];

        // Soulseek paths are canonically backslash-separated, but some clients
        // (Nicotine+ on POSIX) leak forward slashes into shared paths.
        public static string GetDirectoryFromFilename(string? filename)
        {
            if (string.IsNullOrEmpty(filename))
                return "";
            int lastSeparatorIndex = filename.LastIndexOfAny(PathSeparators);
            return lastSeparatorIndex >= 0 ? filename[..lastSeparatorIndex] : "";
        }

        public static bool IsDiscFolderName(string? name) =>
            !string.IsNullOrWhiteSpace(name) && DiscFolderRegex().IsMatch(name.Trim());

        /// <summary>
        /// Directory key for grouping search-result files into releases. Files
        /// inside a disc subfolder (Album\CD1, Album\Disc 2) group under the
        /// parent album folder, so multi-disc shares surface as one release
        /// instead of per-disc fragments that fail track-count filtering.
        /// </summary>
        public static string GetMergedDirectoryKey(string? filename)
        {
            string directory = GetDirectoryFromFilename(filename);
            int lastSeparatorIndex = directory.LastIndexOfAny(PathSeparators);
            if (lastSeparatorIndex < 0)
                return directory;

            return IsDiscFolderName(directory[(lastSeparatorIndex + 1)..])
                ? directory[..lastSeparatorIndex]
                : directory;
        }

        /// <summary>
        /// Extracts the remix qualifier from a title: the remixer text when one
        /// is named ("Never Say Never (Colyn Remix)" → "colyn"), an empty string
        /// when the title is a generic remix release ("Lets Get Lost Remixes"),
        /// or null when the title has no remix qualifier at all. Edition
        /// qualifiers ("Deluxe Edition", "Remastered") are NOT remix qualifiers —
        /// the word boundary in the keyword regex keeps "edit" from matching
        /// "Edition".
        /// </summary>
        public static string? ExtractRemixSignature(string? title)
        {
            if (string.IsNullOrWhiteSpace(title) || !RemixKeywordRegex().IsMatch(title))
                return null;

            foreach (Match bracket in BracketedContentRegex().Matches(title))
            {
                string inner = bracket.Value[1..^1];
                if (RemixKeywordRegex().IsMatch(inner))
                    return NormalizeRemixerText(RemixKeywordRegex().Replace(inner, " "));
            }

            int dash = title.LastIndexOf(" - ", StringComparison.Ordinal);
            if (dash >= 0)
            {
                string tail = title[(dash + 3)..];
                if (RemixKeywordRegex().IsMatch(tail))
                    return NormalizeRemixerText(RemixKeywordRegex().Replace(tail, " "));
            }

            // "Artist - Album" leaves: a keyword confined to the artist prefix
            // ("Lil Flip - Undaground Legend") is a NAME, not a qualifier —
            // only the portion after the first " - " counts as title zone.
            int firstDash = title.IndexOf(" - ", StringComparison.Ordinal);
            if (firstDash >= 0 && !RemixKeywordRegex().IsMatch(title[(firstDash + 3)..]))
                return null;

            return string.Empty;
        }

        /// <summary>
        /// Structured variant qualifiers for a title. Every dimension marks a
        /// DIFFERENT recording of nominally the same music: live, acoustic and
        /// demo cuts, extended mixes, mono/stereo masters, and the remix family
        /// (which also covers instrumental/acapella/karaoke via the keyword
        /// regex). Live/acoustic/demo detection is restricted to qualifier
        /// zones — bracketed segments, a "live at/in/from" phrase, or a
        /// trailing word — so titles that merely CONTAIN the word ("Live
        /// Forever") never trip it.
        /// </summary>
        public sealed record VariantProfile(bool Live, bool Acoustic, bool Demo, bool Extended, string? MonoStereo, string? RemixSignature);

        public static VariantProfile ExtractVariantProfile(string? title)
        {
            if (string.IsNullOrWhiteSpace(title))
                return new VariantProfile(false, false, false, false, null, null);

            string lowered = title.ToLowerInvariant();
            string qualifierZones = string.Join(" ", BracketedContentRegex().Matches(title).Select(m => m.Value[1..^1])).ToLowerInvariant();

            // Trailing checks run with bracketed suffixes removed so
            // "One More Light Live [FLAC]" still reads as trailing-live, and a
            // whole-title variant word covers albums literally titled "Live".
            string trailZone = BracketedContentRegex().Replace(lowered, " ").TrimEnd(' ', '-');

            bool live = LiveQualifierRegex().IsMatch(qualifierZones) ||
                        LiveVenueRegex().IsMatch(lowered) ||
                        TrailingWordRegex("live", trailZone) || trailZone == "live";
            bool acoustic = AcousticRegex().IsMatch(qualifierZones) || TrailingWordRegex("acoustic", trailZone) || trailZone == "acoustic";
            bool demo = DemoRegex().IsMatch(qualifierZones) || TrailingWordRegex("demos", trailZone) || TrailingWordRegex("demo", trailZone) || trailZone is "demo" or "demos";
            bool extended = ExtendedRegex().IsMatch(qualifierZones) || ExtendedPhraseRegex().IsMatch(lowered);

            string? monoStereo = MonoRegex().IsMatch(qualifierZones) ? "mono"
                : StereoRegex().IsMatch(qualifierZones) ? "stereo"
                : null;

            return new VariantProfile(live, acoustic, demo, extended, monoStereo, ExtractRemixSignature(title));
        }

        /// <summary>
        /// A variant qualifier marks a DIFFERENT release, not an edition of the
        /// same one: "Never Say Never" must never match "Never Say Never (Colyn
        /// Remix)", a studio album must never match its "(Live)" cut, and vice
        /// versa. One-sided live/acoustic/demo/extended qualifiers conflict, a
        /// mono master conflicts with a stereo one, and two NAMED remixers
        /// conflict unless they agree (generic vs named is allowed).
        /// Deluxe/remastered editions carry no variant qualifier and are
        /// unaffected.
        /// </summary>
        public static bool RemixSignaturesConflict(string? searchAlbum, string? candidateName) =>
            RemixSignaturesConflict(searchAlbum, candidateName, null);

        /// <summary>
        /// Metadata-aware variant: MusicBrainz secondary types on the TARGET
        /// album (Live, Demo, Remix) FORGIVE a candidate-side qualifier the
        /// title string hides ("Apple Music Live: ..." + folder "(Live)"), but
        /// never demand one — an undecorated exact-title folder still matches.
        /// </summary>
        public static bool RemixSignaturesConflict(string? searchAlbum, string? candidateName, IReadOnlyCollection<string>? targetSecondaryTypes)
        {
            VariantProfile search = ExtractVariantProfile(searchAlbum);
            VariantProfile candidate = ExtractVariantProfile(candidateName);

            bool metaLive = HasSecondaryType(targetSecondaryTypes, "Live");
            bool metaDemo = HasSecondaryType(targetSecondaryTypes, "Demo");
            bool metaRemix = HasSecondaryType(targetSecondaryTypes, "Remix");

            if (search.Live ? !candidate.Live : (candidate.Live && !metaLive))
                return true;
            if (search.Demo ? !candidate.Demo : (candidate.Demo && !metaDemo))
                return true;
            if (search.Acoustic != candidate.Acoustic || search.Extended != candidate.Extended)
                return true;

            if (search.MonoStereo != null && candidate.MonoStereo != null && search.MonoStereo != candidate.MonoStereo)
                return true;

            string? searchSignature = search.RemixSignature;
            if (searchSignature == null && metaRemix)
                searchSignature = string.Empty;
            string? candidateSignature = candidate.RemixSignature;

            if (searchSignature == null && candidateSignature == null)
                return false;
            if (searchSignature == null || candidateSignature == null)
                return true;
            if (searchSignature.Length == 0 || candidateSignature.Length == 0)
                return false;

            return Fuzz.TokenSetRatio(searchSignature, candidateSignature) < 60;
        }

        private static bool HasSecondaryType(IReadOnlyCollection<string>? types, string name) =>
            types != null && types.Any(t => string.Equals(t, name, StringComparison.OrdinalIgnoreCase));

        private static bool TrailingWordRegex(string word, string loweredTitle) =>
            loweredTitle.EndsWith(" " + word, StringComparison.Ordinal) || loweredTitle.EndsWith("-" + word, StringComparison.Ordinal);

        private static string NormalizeRemixerText(string text) =>
            StripPunctuation(text).Trim().ToLowerInvariant();

        public static HashSet<string> ParseListContent(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            return content
                .Split(['\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Where(username => !string.IsNullOrWhiteSpace(username))
                .Select(username => username.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        [GeneratedRegex(@"\s+")]
        private static partial Regex StripPunctuationRegex();

        [GeneratedRegex(@"^(?:cd|disc|disk|dvd)\s*[-_. ]?\s*\d{1,2}$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
        private static partial Regex DiscFolderRegex();

        [GeneratedRegex(@"[\(\[\{].*?[\)\]\}]", RegexOptions.Compiled)]
        private static partial Regex BracketedContentRegex();

        [GeneratedRegex(@"\b(remix(es|ed)?|rmx|re-?work(ed)?|bootleg|vip|flip|edit|instrumentals?|a?\s?capp?ellas?|karaokes?|sped[\s-]?up|slowed|nightcore|daycore|reverb|8d|mashups?|cover(ed)?\s+by)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
        private static partial Regex RemixKeywordRegex();

        [GeneratedRegex(@"\blive\b", RegexOptions.Compiled)]
        private static partial Regex LiveQualifierRegex();

        [GeneratedRegex(@"\blive\s+(at|in|from)\b", RegexOptions.Compiled)]
        private static partial Regex LiveVenueRegex();

        [GeneratedRegex(@"\bacoustic\b", RegexOptions.Compiled)]
        private static partial Regex AcousticRegex();

        [GeneratedRegex(@"\bdemos?\b", RegexOptions.Compiled)]
        private static partial Regex DemoRegex();

        [GeneratedRegex(@"\bextended\b(?!\s+(edition|play|liner))", RegexOptions.Compiled)]
        private static partial Regex ExtendedRegex();

        [GeneratedRegex(@"\bextended\s+(mix|version|edit)\b", RegexOptions.Compiled)]
        private static partial Regex ExtendedPhraseRegex();

        [GeneratedRegex(@"\bmono\b", RegexOptions.Compiled)]
        private static partial Regex MonoRegex();

        [GeneratedRegex(@"\bstereo\b", RegexOptions.Compiled)]
        private static partial Regex StereoRegex();
    }
}