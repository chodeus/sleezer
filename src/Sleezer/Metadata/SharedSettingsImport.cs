using NLog;
using NzbDrone.Core.Download;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek;
using NzbDrone.Plugin.Sleezer.Download.Clients.SubSonic;
using NzbDrone.Plugin.Sleezer.Metadata.DownloadRules;
using NzbDrone.Plugin.Sleezer.Metadata.FFmpeg;
using NzbDrone.Plugin.Sleezer.Metadata.Lyrics;

namespace NzbDrone.Plugin.Sleezer.Metadata
{
    /// <summary>Copies the old per-provider settings into the shared Metadata entries, once.</summary>
    // Async so it runs after Lidarr has seeded the new Metadata entries on startup.
    public class SharedSettingsImport(
        IMetadataFactory metadataFactory,
        IIndexerFactory indexerFactory,
        IDownloadClientFactory downloadClientFactory,
        Logger logger) : IHandleAsync<ApplicationStartedEvent>
    {
        public void HandleAsync(ApplicationStartedEvent message)
        {
            try
            {
                Import();
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Could not copy the old Sleezer settings into Sleezer Download Rules and Lyrics; it is retried on the next start");
            }
        }

        private void Import()
        {
            List<MetadataDefinition> entries = metadataFactory.All();
            MetadataDefinition? rulesEntry = entries.FirstOrDefault(d => d.Settings is DownloadRulesSettings);
            if (rulesEntry?.Settings is not DownloadRulesSettings { ValuesImported: false })
                return;

            MetadataDefinition? ffmpegEntry = entries.FirstOrDefault(d => d.Settings is FFmpegSettings);
            MetadataDefinition? lyricsEntry = entries.FirstOrDefault(d => d.Settings is LyricsSettings);
            if (ffmpegEntry?.Settings is not FFmpegSettings ffmpeg || lyricsEntry == null)
            {
                logger.Warn("Sleezer settings copy deferred to the next start: the {Entry} metadata entry is missing", lyricsEntry == null ? "Lyrics" : "FFmpeg");
                return;
            }

            List<DownloadClientDefinition> clients = downloadClientFactory.All();

            SharedSettingsPlan plan = SharedSettingsCopy.Plan(
                indexerFactory.All(),
                clients,
                ffmpeg.PreImportTaggingClients,
                ffmpeg.StripFeaturedArtists);

            ffmpeg.TidalExtractFlac = plan.TidalExtractFlac;
            ffmpeg.TidalReEncodeAAC = plan.TidalReEncodeAAC;
            metadataFactory.Update(ffmpegEntry);

            lyricsEntry.Settings = plan.Lyrics;
            lyricsEntry.Enable = plan.LyricsEnabled;
            metadataFactory.Update(lyricsEntry);

            foreach (DownloadClientDefinition client in clients)
            {
                if (!plan.ClientIndexerIds.TryGetValue(client.Id, out int indexerId))
                    continue;

                switch (client.Settings)
                {
                    case SlskdProviderSettings s:
                        s.IndexerId = indexerId;
                        break;
                    case SubSonicProviderSettings s:
                        s.IndexerId = indexerId;
                        break;
                    default:
                        continue;
                }

                downloadClientFactory.Update(client);
            }

            foreach (string note in plan.Notes)
                logger.Warn("Sleezer settings copy: {Note}", note);

            // Last, so a failure above leaves the flag unset and the copy runs again.
            rulesEntry.Settings = plan.Rules;
            metadataFactory.Update(rulesEntry);

            logger.Info("Copied the old Sleezer settings into Sleezer Download Rules and Lyrics (Whole Albums Only {Whole}, Strict Matching {Strict}, lyrics {Lyrics})",
                plan.Rules.WholeAlbumsOnly, plan.Rules.StrictMatching, plan.LyricsEnabled);
        }
    }
}
