using System.Text.RegularExpressions;
using FuzzySharp;
using NzbDrone.Core.Music;

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
            // Both DJ-cut markers in separate brackets, "(Intro) (5A - 95) (Clean)", make an edit too.
            if (RemixZone(title) is not { } zone)
                return NamesDjCut([title]) ? string.Empty : null;

            if (zone.Brackets.Length > 0)
                return NormalizeRemixerText(RemixKeywordRegex().Replace(SignatureBracket(zone.Brackets), " "));

            return zone.Tail is { } tail ? NormalizeRemixerText(RemixKeywordRegex().Replace(tail, " ")) : string.Empty;
        }

        // Where a title's remix words count: the bracketed segments naming one, else the tail after the last " - ", else the whole title.
        private sealed record RemixQualifierZone(string[] Brackets, string? Tail, string Text);

        private static RemixQualifierZone? RemixZone(string? title)
        {
            if (string.IsNullOrWhiteSpace(title) || !RemixKeywordRegex().IsMatch(title))
                return null;

            // Brackets keep their characters: "(ReDrum)" names a remix where a bare "Redrum" title doesn't.
            string[] remixBrackets = [.. BracketedContentRegex().Matches(title).Select(b => b.Value).Where(bracket => RemixKeywordRegex().IsMatch(bracket))];
            if (remixBrackets.Length > 0)
                return new RemixQualifierZone(remixBrackets, null, string.Join("\n", remixBrackets));

            int dash = title.LastIndexOf(" - ", StringComparison.Ordinal);
            if (dash >= 0 && RemixKeywordRegex().IsMatch(title[(dash + 3)..]))
                return new RemixQualifierZone([], title[(dash + 3)..], title[(dash + 3)..]);

            // A keyword confined to the artist prefix ("Lil Flip - Undaground Legend")
            // is a NAME, not a qualifier.
            int firstDash = title.IndexOf(" - ", StringComparison.Ordinal);
            if (firstDash >= 0 && !RemixKeywordRegex().IsMatch(title[(firstDash + 3)..]))
                return null;

            return new RemixQualifierZone([], null, title);
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

            return RemixLabelsConflict(searchSignature, candidateSignature, searchAlbum, candidateComponents);
        }

        // An instrumental is no remix, nor an acapella an instrumental: a label forgives only a copy sharing one of its kinds.
        private static bool RemixLabelsConflict(string searchSignature, string candidateSignature, string? searchAlbum, IReadOnlyList<string?> candidateComponents)
        {
            HashSet<string> searchClasses = [.. RemixKinds(searchAlbum).Select(RemixClass)];
            HashSet<string> candidateClasses = [.. candidateComponents.SelectMany(RemixKinds).Select(RemixClass)];
            if ((searchClasses.Count > 0 && candidateClasses.Count > 0 && !searchClasses.Overlaps(candidateClasses)) || DjCutsDiffer(searchAlbum, candidateComponents))
                return true;

            if (searchSignature.Length == 0 || candidateSignature.Length == 0)
                return false;

            return Fuzz.TokenSetRatio(searchSignature, candidateSignature) < 60
                && !NameStartsWithin(searchSignature, candidateSignature)
                && !NameStartsWithin(candidateSignature, searchSignature);
        }

        // Clean and dirty, or intro and outro, are different DJ-pool cuts under one edit label; both sides must say which.
        private static bool DjCutsDiffer(string? searchAlbum, IReadOnlyList<string?> candidateComponents)
        {
            HashSet<string> search = DjCutWords(searchAlbum);
            List<HashSet<string>> components = [.. candidateComponents.Select(DjCutWords)];
            return AxisDiffers(search, components, "clean", "dirty") || AxisDiffers(search, components, "intro", "outro");
        }

        // The innermost component naming the axis decides: a dirty file in a "(Clean)" pool folder is dirty, and naming both is no match.
        private static bool AxisDiffers(HashSet<string> search, IReadOnlyList<HashSet<string>> components, string one, string other)
        {
            if (search.Contains(one) == search.Contains(other))
                return false;

            string wanted = search.Contains(one) ? one : other;
            HashSet<string>? named = components.FirstOrDefault(c => c.Contains(one) || c.Contains(other));
            return named != null && !(named.Contains(wanted) && !named.Contains(wanted == one ? other : one));
        }

        // Bracketed only: "Clean Bandit" and "Come Clean" name an artist and a song.
        private static HashSet<string> DjCutWords(string? title) =>
            string.IsNullOrWhiteSpace(title) ? [] : [.. BracketedContentRegex().Matches(title).SelectMany(b => DjCutWordRegex().Matches(AfterRemixer(b.Value))).Select(m => m.Value.ToLowerInvariant())];

        // Before a remix word stands the remixer's name, "(Dirty South Remix)"; a cut label follows it, "(Moonward Remix - Clean)".
        private static string AfterRemixer(string bracket) =>
            GenuineRemixKeywordRegex().Matches(bracket) is { Count: > 0 } remix ? bracket[(remix[^1].Index + remix[^1].Length)..] : bracket;

        // Each remix-family word's kind, read where the signature is; edits, bootlegs and covers are remixes.
        private static HashSet<string> RemixKinds(string? title) => RemixZone(title) is { } zone ? KindsIn(zone.Text) : [];

        private static HashSet<string> KindsIn(string text) => [.. RemixKeywordRegex().Matches(text).Select(m => RemixKind(m.Value))];

        private static string RemixKind(string keyword) =>
            RemixKindRegex().Match(keyword) is { Success: true } kind ? kind.Groups.Cast<Group>().Skip(1).First(g => g.Success).Name : "remix";

        private static string RemixClass(string kind) => kind is "edit" or "bootleg" or "cover" ? "remix" : kind;

        // Joined forms forgive a stylised or longer name ("k?d" vs "kd", "Avicii" in "Avicii's Dirty Mouth"): it must
        // begin a word of the other and end one, a possessive "s" allowed; "Corvin" is not "Corvina Brothers".
        private static bool NameStartsWithin(string name, string other)
        {
            string joined = name.Replace(" ", string.Empty);
            string otherJoined = other.Replace(" ", string.Empty);
            int start = 0;
            foreach (string word in other.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (otherJoined.AsSpan(start).StartsWith(joined, StringComparison.Ordinal))
                {
                    int end = start + joined.Length;
                    bool possessive = end < otherJoined.Length && otherJoined[end] == 's' && EndsWord(other, end + 1);
                    if (EndsWord(other, end) || possessive)
                        return true;
                }

                start += word.Length;
            }

            return false;
        }

        // Whether `joinedIndex`, counted in `spaced` without its spaces, falls at the end of a word.
        private static bool EndsWord(string spaced, int joinedIndex)
        {
            int letters = 0;
            foreach (string word in spaced.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                letters += word.Length;
                if (letters == joinedIndex)
                    return true;
                if (letters > joinedIndex)
                    return false;
            }

            return false;
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

        /// <summary>The title with the album's disambiguation added when that names a variant at least half the album's own tracks carry; otherwise null.</summary>
        // MusicBrainz keeps "(Instrumentals)" or "(Huglife remix)" in the disambiguation while the title stays plain;
        // "blue cover" names the artwork, and no track says so.
        public static string? VariantTitleOf(Album? album, string title) =>
            VariantTitleOf(title, album?.Disambiguation, TracklistOf(album));

        /// <summary>As <see cref="VariantTitleOf(Album?, string)"/>, judged against the given track titles.</summary>
        public static string? VariantTitleOf(string title, string? disambiguation, IReadOnlyList<string?> trackTitles)
        {
            if (string.IsNullOrWhiteSpace(disambiguation))
                return null;

            string variantTitle = $"{title} ({disambiguation})";
            List<TrackVariant> tracks = [.. trackTitles.Select(t => new TrackVariant(ExtractVariantProfile(t), RemixKinds(t)))];
            return TracksCarryAddedVariants(title, ExtractVariantProfile(variantTitle), KindsIn(disambiguation), tracks) ? variantTitle : null;
        }

        private readonly record struct TrackVariant(VariantProfile Profile, HashSet<string> RemixKinds);

        // The disambiguation's kinds come from its whole text: "UA bootleg - blue vinyl" is still a bootleg.
        private static bool TracksCarryAddedVariants(string title, VariantProfile named, HashSet<string> namedKinds, IReadOnlyList<TrackVariant> tracks)
        {
            VariantProfile plain = ExtractVariantProfile(title);
            bool remixAdded = plain.RemixSignature != named.RemixSignature || !namedKinds.IsSubsetOf(RemixKinds(title));
            HashSet<string> addedArrangements = [.. named.Arrangements.Except(plain.Arrangements)];
            (bool Added, Func<TrackVariant, bool> Carried)[] dimensions =
            [
                // "Remixes" is borne out by any remix-class word, a narrower kind only by its own: "(radio edit)" tracks don't make a "bootleg".
                (remixAdded, t => namedKinds.Count > 0 && t.Profile.RemixSignature != null && namedKinds.All(k => k == "remix" ? t.RemixKinds.Any(tk => RemixClass(tk) == "remix") : t.RemixKinds.Contains(k))),
                (plain.Live != named.Live, t => t.Profile.Live),
                (plain.Demo != named.Demo, t => t.Profile.Demo),
                (plain.Extended != named.Extended, t => t.Profile.Extended),
                (addedArrangements.Count > 0, t => addedArrangements.IsSubsetOf(t.Profile.Arrangements)),
                (plain.MonoStereo != named.MonoStereo, t => t.Profile.MonoStereo == named.MonoStereo),
            ];

            List<Func<TrackVariant, bool>> added = [.. dimensions.Where(d => d.Added).Select(d => d.Carried)];
            return added.Count > 0 && tracks.Count > 0 && added.All(carried => tracks.Count(carried) * 2 >= tracks.Count);
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

            // A DJ cut can be split over the path, "DJ Pool (Clean)" above "Song (Intro)": both markers make it an edit.
            remixSignature ??= NamesDjCut(components) ? string.Empty : null;
            return new VariantProfile(live, demo, extended, arrangements, monoStereo, remixSignature);
        }

        private static bool NamesDjCut(IReadOnlyList<string?> components)
        {
            HashSet<string> cuts = [.. components.SelectMany(DjCutWords)];
            return (cuts.Contains("intro") || cuts.Contains("outro")) && (cuts.Contains("clean") || cuts.Contains("dirty"));
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
                zones.Add(bracket.Value);

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

        private static string NormalizeRemixerText(string text) => ComparableText.Of(text);

        // The track's own "(Grandtheft Remix)" outranks an album's "(The Remixes)"; a named "(KIKI Remixes)" outranks "(Radio Edit)".
        private static string SignatureBracket(string[] remixBrackets) =>
            remixBrackets.FirstOrDefault(b => !PluralRemixesRegex().IsMatch(b) && GenuineRemixKeywordRegex().IsMatch(b))
            ?? remixBrackets.FirstOrDefault(b => PluralRemixesRegex().IsMatch(b) && !IsGenericRemixesLabel(b))
            ?? remixBrackets.FirstOrDefault(b => !IsGenericRemixesLabel(b))
            ?? remixBrackets[0];

        // "(The Remixes)", "(Remixes, Part 2)": a release label, not a remixer.
        private static bool IsGenericRemixesLabel(string bracket) =>
            PluralRemixesRegex().IsMatch(bracket) &&
            NormalizeRemixerText(RemixKeywordRegex().Replace(bracket, " ")).Split(' ', StringSplitOptions.RemoveEmptyEntries).All(word => GenericLabelWordRegex().IsMatch(word));

        [GeneratedRegex(@"^(?:the|vol|volume|pt|part|no|ep|\d+|[ivx]+)$")]
        private static partial Regex GenericLabelWordRegex();

        [GeneratedRegex(@"\bremixes\b", RegexOptions.IgnoreCase)]
        private static partial Regex PluralRemixesRegex();

        [GeneratedRegex(@"[\(\[\{].*?[\)\]\}]", RegexOptions.Compiled)]
        private static partial Regex BracketedContentRegex();

        // A mix named for its maker ("Name's Night Mix") is a remix; "Original Mix" and "It's a Mix" are not.
        // DJ-pool edits count too: "ReDrum" after a word or a bracket (a bare title "Redrum" is a song), "Quick Hit", a bracketed intro or outro marked clean or dirty.
        [GeneratedRegex(@"\b(remix(es|ed)?|rmx|re-?work(ed)?|(?<=\b\w[\w'’]*\s+|[\(\[\{])re-?drum|bootleg|v\.?i\.?p|flip|dub|edit|quick\s?hits?|(?<=[\(\[\{][^()\[\]{}\n]*)(?:intro|outro)[\s\-/()\[\]{}]*(?:clean|dirty)|(?<=[\(\[\{][^()\[\]{}\n]*)(?:clean|dirty)[\s\-/()\[\]{}]*(?:intro|outro)|instrumentals?|a?\s?capp?ellas?|karaokes?|sped[\s-]?up|slowed|nightcore|daycore|reverb|8d|mashups?|cover(ed)?\s+by|(?<=\b(?!(?:it|that|let|what|there|here|he|she|who|where|how)['’])\w+['’]s\b[^()\[\]{}\n]*)mix)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
        private static partial Regex RemixKeywordRegex();

        [GeneratedRegex(@"\b(remix(es|ed)?|rmx|re-?work(ed)?|(?<=\b\w[\w'’]*\s+|[\(\[\{])re-?drum|bootleg|v\.?i\.?p|flip|dub|mashups?|(?<=\b(?!(?:it|that|let|what|there|here|he|she|who|where|how)['’])\w+['’]s\b[^()\[\]{}\n]*)mix)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
        private static partial Regex GenuineRemixKeywordRegex();

        [GeneratedRegex(@"\b(clean|dirty|intro|outro)\b", RegexOptions.IgnoreCase)]
        private static partial Regex DjCutWordRegex();

        // Names the kind of one RemixKeywordRegex match; any other match is a remix.
        [GeneratedRegex(@"^(?:(?<instrumental>instrumentals?)|(?<acapella>a?\s?capp?ellas?)|(?<karaoke>karaokes?)|(?<speed>sped[\s-]?up|slowed|nightcore|daycore|reverb|8d)|(?<edit>edit|quick\s?hits?|(intro|outro)[\s\-/()\[\]{}]*(clean|dirty)|(clean|dirty)[\s\-/()\[\]{}]*(intro|outro))|(?<bootleg>bootleg)|(?<cover>cover(ed)?\s+by))$", RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture)]
        private static partial Regex RemixKindRegex();

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
