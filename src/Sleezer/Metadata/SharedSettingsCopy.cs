using NzbDrone.Core.Download.Clients.Deezer;
using NzbDrone.Core.Download.Clients.Qobuz;
using NzbDrone.Core.Download.Clients.Tidal;
using NzbDrone.Core.Indexers.Bandcamp;
using NzbDrone.Core.Indexers.Deezer;
using NzbDrone.Core.Indexers.Qobuz;
using NzbDrone.Core.Indexers.Tidal;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek;
using NzbDrone.Plugin.Sleezer.Download.Clients.SubSonic;
using NzbDrone.Plugin.Sleezer.Indexers.Soulseek;
using NzbDrone.Plugin.Sleezer.Indexers.SubSonic;
using NzbDrone.Plugin.Sleezer.Metadata.DownloadRules;
using NzbDrone.Plugin.Sleezer.Metadata.Lyrics;

namespace NzbDrone.Plugin.Sleezer.Metadata
{
    /// <summary>What the one-time copy writes, worked out from the old per-provider settings.</summary>
    public sealed record SharedSettingsPlan(
        DownloadRulesSettings Rules,
        LyricsSettings Lyrics,
        bool LyricsEnabled,
        bool TidalExtractFlac,
        bool TidalReEncodeAAC,
        IReadOnlyDictionary<int, int> ClientIndexerIds,
        IReadOnlyList<string> Notes);

    /// <summary>Merges the old per-provider settings into the shared entries; where providers disagree, the stricter value wins.</summary>
    public static class SharedSettingsCopy
    {
        public static SharedSettingsPlan Plan(
            IReadOnlyList<ProviderDefinition> indexers,
            IReadOnlyList<ProviderDefinition> clients,
            IEnumerable<int> taggingClients,
            bool stripFeaturedArtists)
        {
            List<string> notes = [];

            List<(string, bool)> whole = [];
            List<(string, bool)> unstreamable = [];
            List<(string, bool)> strict = [];
            foreach (ProviderDefinition d in indexers)
            {
                switch (d.Settings)
                {
                    case DeezerIndexerSettings s:
                        whole.Add(($"{d.Name} (hide albums with a track it won't serve)", s.HideAlbumsWithMissing));
                        whole.Add(($"{d.Name} (no MP3 320 fallback)", !s.AllowMp3FallbackForMissingFlac));
                        strict.Add((d.Name, s.StrictMatching));
                        break;
                    case QobuzIndexerSettings s:
                        unstreamable.Add((d.Name, s.HideNonStreamable));
                        strict.Add((d.Name, s.StrictMatching));
                        break;
                    case TidalIndexerSettings s:
                        unstreamable.Add((d.Name, s.HideAlbumsWithMissing));
                        strict.Add((d.Name, s.StrictMatching));
                        break;
                    case SubSonicIndexerSettings s:
                        strict.Add((d.Name, s.StrictMatching));
                        break;
                    case BandcampIndexerSettings s:
                        strict.Add((d.Name, s.StrictMatching));
                        break;
                    case SlskdSettings s:
                        whole.Add(($"{d.Name} (track count filter)", (TrackCountFilterType)s.TrackCountFilter is TrackCountFilterType.Exact or TrackCountFilterType.Lower));
                        whole.Add(($"{d.Name} (coherent single source)", s.RequireCoherentSingleSource));
                        break;
                }
            }

            List<(string, bool)> synced = [];
            List<(string, bool)> lrclib = [];
            List<(string, bool)> extractFlac = [];
            List<(string, bool)> reEncode = [];
            Dictionary<int, int> clientIndexerIds = [];
            foreach (ProviderDefinition d in clients)
            {
                switch (d.Settings)
                {
                    case DeezerSettings s:
                        synced.Add((d.Name, s.SaveSyncedLyrics));
                        lrclib.Add((d.Name, s.UseLRCLIB));
                        break;
                    case QobuzSettings s:
                        whole.Add(($"{d.Name} (require complete album)", s.RequireCompleteAlbum));
                        synced.Add((d.Name, s.SaveSyncedLyrics));
                        lrclib.Add((d.Name, s.UseLRCLIB));
                        break;
                    case TidalSettings s:
                        synced.Add((d.Name, s.SaveSyncedLyrics));
                        lrclib.Add((d.Name, s.UseLRCLIB));
                        extractFlac.Add((d.Name, s.ExtractFlac));
                        reEncode.Add((d.Name, s.ReEncodeAAC));
                        break;
                    case SlskdProviderSettings s:
                        LinkClient(d, indexers.Where(i => i.Settings is SlskdSettings i2 && i2.BaseUrl == s.BaseUrl && i2.ApiKey == s.ApiKey),
                            indexers.Count(i => i.Settings is SlskdSettings), clientIndexerIds, notes);
                        break;
                    case SubSonicProviderSettings s:
                        LinkClient(d, indexers.Where(i => i.Settings is SubSonicIndexerSettings i2 && i2.BaseUrl == s.ServerUrl && i2.Username == s.Username),
                            indexers.Count(i => i.Settings is SubSonicIndexerSettings), clientIndexerIds, notes);
                        break;
                }
            }

            DownloadRulesSettings rules = new()
            {
                WholeAlbumsOnly = Merge("Whole Albums Only", whole, true, notes),
                HideUnstreamable = Merge("Hide Albums This Account Can't Stream", unstreamable, true, notes),
                StrictMatching = Merge("Strict Matching", strict, true, notes),
                PreImportTaggingClients = taggingClients.ToArray(),
                StripFeaturedArtists = stripFeaturedArtists,
                ValuesImported = true
            };

            LyricsSettings lyrics = new()
            {
                SaveSyncedLyrics = Merge("Save Synced Lyrics", synced, false, notes),
                UseLRCLIB = Merge("Use LRCLIB", lrclib, false, notes)
            };

            return new(
                rules,
                lyrics,
                lyrics.SaveSyncedLyrics || lyrics.UseLRCLIB,
                Merge("Tidal: Extract FLAC From M4A", extractFlac, false, notes),
                Merge("Tidal: Re-encode AAC Into MP3", reEncode, false, notes),
                clientIndexerIds,
                notes);
        }

        // On wins: for a rule it is the stricter value, for lyrics and conversion it means someone wanted it.
        private static bool Merge(string setting, List<(string Source, bool Value)> votes, bool fallback, List<string> notes)
        {
            if (votes.Count == 0)
                return fallback;

            bool value = votes.Any(v => v.Value);
            List<string> overruled = votes.Where(v => v.Value != value).Select(v => v.Source).ToList();
            if (overruled.Count > 0)
                notes.Add($"{setting}: set {(value ? "on" : "off")}; it was {(value ? "off" : "on")} for {string.Join(", ", overruled)}.");

            return value;
        }

        private static void LinkClient(ProviderDefinition client, IEnumerable<ProviderDefinition> matching, int indexerCount, Dictionary<int, int> ids, List<string> notes)
        {
            List<ProviderDefinition> found = matching.ToList();
            if (found.Count >= 1 && indexerCount > 1)
                ids[client.Id] = found[0].Id;
            else if (found.Count == 0 && indexerCount > 0)
                notes.Add($"{client.Name}: its own login matched no indexer; it now uses the indexer's. Check it under Download Clients.");
            else if (indexerCount == 0)
                notes.Add($"{client.Name}: there is no matching indexer to take a login from; add one or the client stops working.");
        }
    }
}
