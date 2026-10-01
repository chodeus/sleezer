using System.Text.RegularExpressions;
using FuzzySharp;
using NzbDrone.Core.Music;
using NzbDrone.Plugin.Sleezer.Indexers.Soulseek;

namespace NzbDrone.Plugin.Sleezer.Core.Utilities
{
    /// <summary>Decides whether two titles name the same recording or different variants of it.</summary>
    public static partial class VariantQualifiers
    {
        // A store "Remixes" product is forgiven only when a majority of the target's
        // tracks carry the qualifier — one bonus remix must not retype the album.
        private const double VouchingMajority = 0.5;

        /// <summary>Extracts a title's remix qualifier: the remixer, empty for a generic remix release, null for none.</summary>
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

            // A keyword confined to the artist prefix ("Lil Flip - Undaground Legend")
            // is a NAME, not a qualifier.
            int firstDash = title.IndexOf(" - ", StringComparison.Ordinal);
            if (firstDash >= 0 && !RemixKeywordRegex().IsMatch(title[(firstDash + 3)..]))
                return null;

            return string.Empty;
        }

        /// <summary>Structured variant qualifiers for a title; every dimension marks a different recording.</summary>
        public sealed record VariantProfile(bool Live, bool Demo, bool Extended, IReadOnlySet<string> Arrangements, string? MonoStereo, string? RemixSignature)
        {
            public bool AnyCut => Live || Demo || Extended || Arrangements.Count > 0;
        }

        // Only the adjectives count ending a title; a noun there is a title word ("Adagio for Strings").
        private const string ArrangementAdjectives = @"acoustic|unplugged|stripped|orchestral|symphonic|lo-?fi|re-?record(?:ed|ing)";
        private const string ArrangementWords = ArrangementAdjectives + @"|piano|cover(?:s|ed)?|strings?|lullaby|chill|guitar";

        public static VariantProfile ExtractVariantProfile(string? title)
        {
            if (string.IsNullOrWhiteSpace(title))
                return new VariantProfile(false, false, false, new HashSet<string>(), null, null);

            string lowered = title.ToLowerInvariant();
            string qualifierZones = string.Join(" ", BracketedContentRegex().Matches(title).Select(m => m.Value[1..^1])).ToLowerInvariant();

            // Trailing checks run with bracketed suffixes removed so "One More Light
            // Live [FLAC]" still reads as trailing-live.
            string trailZone = BracketedContentRegex().Replace(lowered, " ").TrimEnd(' ', '-');

            bool live = LiveQualifierRegex().IsMatch(qualifierZones) ||
                        LiveVenueRegex().IsMatch(lowered) ||
                        TrailingWord("live", trailZone) || trailZone == "live";
            bool demo = DemoRegex().IsMatch(qualifierZones) || TrailingWord("demos", trailZone) || TrailingWord("demo", trailZone) || trailZone is "demo" or "demos";
            bool extended = ExtendedRegex().IsMatch(qualifierZones) || ExtendedPhraseRegex().IsMatch(lowered);

            string? monoStereo = MonoRegex().IsMatch(qualifierZones) ? "mono"
                : StereoRegex().IsMatch(qualifierZones) ? "stereo"
                : null;

            return new VariantProfile(live, demo, extended, Arrangements(qualifierZones, lowered, trailZone), monoStereo, ExtractRemixSignature(title));
        }

        // An arrangement word in brackets, before "version", "cover" and the like anywhere, or an adjective ending the title.
        private static HashSet<string> Arrangements(string qualifierZones, string lowered, string trailZone)
        {
            HashSet<string> found = [.. ArrangementRegex().Matches(qualifierZones).Select(m => ArrangementKey(m.Value))];
            foreach (Match phrase in ArrangementPhraseRegex().Matches(lowered))
            {
                found.Add(ArrangementKey(phrase.Groups[1].Value));
                if (phrase.Groups[2].Success)
                    found.Add(ArrangementKey(phrase.Groups[2].Value));
            }
            if (TrailingArrangementRegex().Match(trailZone) is { Success: true } trailing)
                found.Add(ArrangementKey(trailing.Groups[1].Value));
            return found;
        }

        private static string ArrangementKey(string word) => word switch
        {
            "covers" or "covered" => "cover",
            "orchestra" => "orchestral",
            "string" => "strings",
            "lo-fi" => "lofi",
            _ when word.Contains("record", StringComparison.Ordinal) => "rerecorded",
            _ => word,
        };

        /// <summary>True when the two titles name different variants; deluxe/remaster editions never conflict.</summary>
        public static bool RemixSignaturesConflict(string? searchAlbum, string? candidateName) =>
            RemixSignaturesConflict(searchAlbum, candidateName, null);

        /// <summary>Target secondary types FORGIVE a candidate-side qualifier the title hides, but never demand one.</summary>
        public static bool RemixSignaturesConflict(string? searchAlbum, string? candidateName, IReadOnlyCollection<string>? targetSecondaryTypes) =>
            RemixSignaturesConflict(searchAlbum, [candidateName], targetSecondaryTypes);

        /// <summary>Path-aware: a folder's qualifier may sit in any component, so the candidate profile is the union.</summary>
        public static bool RemixSignaturesConflict(string? searchAlbum, IReadOnlyList<string?> candidateComponents, IReadOnlyCollection<string>? targetSecondaryTypes)
        {
            VariantProfile search = ExtractVariantProfile(searchAlbum);
            VariantProfile candidate = UnionVariantProfiles(candidateComponents);
            string candidateName = string.Join(" ", candidateComponents.Where(c => !string.IsNullOrWhiteSpace(c)));

            bool metaLive = HasSecondaryType(targetSecondaryTypes, "Live");
            bool metaDemo = HasSecondaryType(targetSecondaryTypes, "Demo");
            bool metaRemix = HasSecondaryType(targetSecondaryTypes, "Remix");

            if (search.Live ? !candidate.Live : (candidate.Live && !metaLive))
                return true;
            if (search.Demo ? !candidate.Demo : (candidate.Demo && !metaDemo))
                return true;
            if (search.Extended != candidate.Extended || !search.Arrangements.SetEquals(candidate.Arrangements))
                return true;

            if (search.MonoStereo != null && candidate.MonoStereo != null && search.MonoStereo != candidate.MonoStereo)
                return true;

            string? searchSignature = search.RemixSignature;
            string? candidateSignature = candidate.RemixSignature;

            // A Remix secondary type only FORGIVES remix-family text; it must not admit
            // non-remix variants, not even mixed with a remix term ("Remix Radio Edit").
            if (searchSignature == null && metaRemix)
                return candidateSignature != null && !HasOnlyRemixFamilyQualifiers(candidateName);

            if (searchSignature == null && candidateSignature == null)
                return false;
            if (searchSignature == null || candidateSignature == null)
                return true;
            if (searchSignature.Length == 0 || candidateSignature.Length == 0)
                return false;

            return Fuzz.TokenSetRatio(searchSignature, candidateSignature) < 60;
        }

        /// <summary>True when a title carries any variant qualifier; cheap pre-filter before the conflict check.</summary>
        public static bool HasVariantQualifier(string? title)
        {
            VariantProfile profile = ExtractVariantProfile(title);
            return profile.AnyCut || profile.MonoStereo != null || profile.RemixSignature != null;
        }

        /// <summary>True when a TRACK title is itself a variant cut. Narrow on purpose — "(radio edit)" is the main track of most singles.</summary>
        public static bool IsVariantTrack(string? title)
        {
            if (string.IsNullOrWhiteSpace(title))
                return false;

            return ExtractVariantProfile(title).AnyCut || HasOnlyRemixFamilyQualifiers(title);
        }

        /// <summary>Removes bracketed segments; shared so callers don't re-declare the regex.</summary>
        public static string StripBrackets(string title) => BracketedContentRegex().Replace(title, " ");

        /// <summary>
        /// Variant qualifiers the target album forgives: its MusicBrainz secondary types,
        /// plus any the monitored release's own tracklist vouches for.
        /// </summary>
        public static IReadOnlyCollection<string> ForgivenVariants(Album? album)
        {
            HashSet<string> forgiven = new(StringComparer.OrdinalIgnoreCase);
            if (album == null)
                return forgiven;

            foreach (var type in album.SecondaryTypes ?? [])
            {
                if (!string.IsNullOrWhiteSpace(type?.Name))
                    forgiven.Add(type!.Name);
            }

            foreach (string vouched in VouchedVariants(TracklistOf(album)))
                forgiven.Add(vouched);

            return forgiven;
        }

        /// <summary>Variant types a majority of the tracklist carries; empty when the tracklist is unknown.</summary>
        public static IReadOnlyCollection<string> VouchedVariants(IReadOnlyList<string?> trackTitles)
        {
            HashSet<string> vouched = new(StringComparer.OrdinalIgnoreCase);
            if (trackTitles.Count == 0)
                return vouched;

            int remix = 0, live = 0, demo = 0;
            foreach (string? title in trackTitles)
            {
                if (string.IsNullOrWhiteSpace(title))
                    continue;

                VariantProfile profile = ExtractVariantProfile(title);
                if (HasOnlyRemixFamilyQualifiers(title))
                    remix++;
                if (profile.Live)
                    live++;
                if (profile.Demo)
                    demo++;
            }

            double threshold = trackTitles.Count * VouchingMajority;
            if (remix > threshold)
                vouched.Add("Remix");
            if (live > threshold)
                vouched.Add("Live");
            if (demo > threshold)
                vouched.Add("Demo");

            return vouched;
        }

        /// <summary>Track titles of the monitored release, or the first release when none is monitored.</summary>
        public static IReadOnlyList<string?> TracklistOf(Album? album)
        {
            var releases = album?.AlbumReleases?.Value;
            if (releases == null)
                return [];

            var release = releases.FirstOrDefault(r => r.Monitored) ?? releases.FirstOrDefault();
            return TracklistOf(release);
        }

        public static IReadOnlyList<string?> TracklistOf(AlbumRelease? release) =>
            release?.Tracks?.Value?.Select(t => t.Title).ToList() ?? [];

        /// <summary>Candidate profile across path components: a qualifier in any component counts as present.</summary>
        private static VariantProfile UnionVariantProfiles(IReadOnlyList<string?> components)
        {
            if (components.Count == 1)
                return ExtractVariantProfile(components[0]);

            bool live = false, demo = false, extended = false;
            HashSet<string> arrangements = [];
            string? monoStereo = null;
            string? remixSignature = null;

            // Leaf first (components are ordered leaf-to-parent), so the nearest
            // component wins for the single-valued dimensions.
            foreach (string? component in components)
            {
                VariantProfile profile = ExtractVariantProfile(component);
                live |= profile.Live;
                demo |= profile.Demo;
                extended |= profile.Extended;
                arrangements.UnionWith(profile.Arrangements);
                monoStereo ??= profile.MonoStereo;
                remixSignature ??= profile.RemixSignature;
            }

            return new VariantProfile(live, demo, extended, arrangements, monoStereo, remixSignature);
        }

        private static bool HasSecondaryType(IReadOnlyCollection<string>? types, string name) =>
            types != null && types.Any(t => string.Equals(t, name, StringComparison.OrdinalIgnoreCase));

        // Qualifier zones mirror ExtractRemixSignature: bracketed segments, or text
        // after the first " - " (keywords in an artist prefix don't count).
        private static bool HasOnlyRemixFamilyQualifiers(string? title)
        {
            if (string.IsNullOrWhiteSpace(title))
                return false;

            List<string> zones = new();
            foreach (Match bracket in BracketedContentRegex().Matches(title))
                zones.Add(bracket.Value[1..^1]);

            int firstDash = title.IndexOf(" - ", StringComparison.Ordinal);
            zones.Add(BracketedContentRegex().Replace(firstDash >= 0 ? title[(firstDash + 3)..] : title, " "));

            // A line break, which a named mix's possessive can't reach across: "Voodoo (It's in the Wall) (original mix)".
            string joined = string.Join("\n", zones);
            if (!GenuineRemixKeywordRegex().IsMatch(joined))
                return false;

            // Any variant keyword left after removing remix-family terms is a mixed
            // qualifier ("Remix Radio Edit") — still a different recording.
            return !RemixKeywordRegex().IsMatch(GenuineRemixKeywordRegex().Replace(joined, " "));
        }

        private static bool TrailingWord(string word, string loweredTitle) =>
            loweredTitle.EndsWith(" " + word, StringComparison.Ordinal) || loweredTitle.EndsWith("-" + word, StringComparison.Ordinal);

        private static string NormalizeRemixerText(string text) =>
            SlskdTextProcessor.StripPunctuation(text).Trim().ToLowerInvariant();

        [GeneratedRegex(@"[\(\[\{].*?[\)\]\}]", RegexOptions.Compiled)]
        private static partial Regex BracketedContentRegex();

        // A mix named for its maker ("Name's Night Mix") is a remix; "Original Mix" and "It's a Mix" are not.
        [GeneratedRegex(@"\b(remix(es|ed)?|rmx|re-?work(ed)?|bootleg|v\.?i\.?p|flip|dub|edit|instrumentals?|a?\s?capp?ellas?|karaokes?|sped[\s-]?up|slowed|nightcore|daycore|reverb|8d|mashups?|cover(ed)?\s+by|(?<=\b(?!(?:it|that|let|what|there|here|he|she|who|where|how)['’])\w+['’]s\b[^()\[\]{}\n]*)mix)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
        private static partial Regex RemixKeywordRegex();

        [GeneratedRegex(@"\b(remix(es|ed)?|rmx|re-?work(ed)?|bootleg|v\.?i\.?p|flip|dub|mashups?|(?<=\b(?!(?:it|that|let|what|there|here|he|she|who|where|how)['’])\w+['’]s\b[^()\[\]{}\n]*)mix)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
        private static partial Regex GenuineRemixKeywordRegex();

        [GeneratedRegex(@"\blive\b", RegexOptions.Compiled)]
        private static partial Regex LiveQualifierRegex();

        [GeneratedRegex(@"\blive\s+(at|in|from)\b", RegexOptions.Compiled)]
        private static partial Regex LiveVenueRegex();

        // A session recording ("1986 Sound City Session", "BBC Sessions") is another take, as a demo is.
        [GeneratedRegex(@"\b(?:demos?|sessions?)\b", RegexOptions.Compiled)]
        private static partial Regex DemoRegex();

        [GeneratedRegex(@"\bextended\b(?!\s+(edition|play|liner))", RegexOptions.Compiled)]
        private static partial Regex ExtendedRegex();

        [GeneratedRegex(@"\bextended\s+(mix|version|edit)\b", RegexOptions.Compiled)]
        private static partial Regex ExtendedPhraseRegex();

        // A credit ("piano: <name>") or a work ("string quartet") names no arrangement.
        [GeneratedRegex(@"\b(?:" + ArrangementWords + @")\b(?!\s*:|\s+(?:concerto|sonata|trio|quartet|quintet)s?\b)", RegexOptions.Compiled)]
        private static partial Regex ArrangementRegex();

        // Alone, "orchestra" names the ensemble; only "orchestra version" and the like mark an arrangement.
        [GeneratedRegex(@"\b(" + ArrangementWords + @"|orchestra)\s+(?:version|ver|(covers?)|instrumental|mix|edit)\b", RegexOptions.Compiled)]
        private static partial Regex ArrangementPhraseRegex();

        [GeneratedRegex(@"(?:^|[\s-])(" + ArrangementAdjectives + @")$", RegexOptions.Compiled)]
        private static partial Regex TrailingArrangementRegex();

        [GeneratedRegex(@"\bmono\b", RegexOptions.Compiled)]
        private static partial Regex MonoRegex();

        [GeneratedRegex(@"\bstereo\b", RegexOptions.Compiled)]
        private static partial Regex StereoRegex();
    }
}
