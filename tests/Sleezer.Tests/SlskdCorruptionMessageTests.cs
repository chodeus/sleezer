using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Core.History;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Sleezer.Core.PostProcessing;
using NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek;
using NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek.Models;
using Sleezer.Tests.Fakes;
using Xunit;
using Fake = Sleezer.Tests.PreImportTaggerWholeDownloadTests.Fake;

namespace Sleezer.Tests;

// The real Slskd and shared corruption handlers; only Lidarr's services are faked.
public class SlskdCorruptionMessageTests
{
    [Fact]
    public async Task The_failure_message_names_each_strikes_file_whether_or_not_it_maps_to_a_transfer()
    {
        EntityHistory grab = new() { ArtistId = 1, AlbumId = 10, DownloadId = "dl-1", SourceTitle = "Artist - Album", Data = [] };
        IHistoryService history = Fake.Of<IHistoryService>((m, _) =>
            m.Name == nameof(IHistoryService.Find) ? new List<EntityHistory> { grab } : throw new NotSupportedException(m.Name));
        IDiskProvider disk = Fake.Of<IDiskProvider>((m, _) =>
            m.Name == nameof(IDiskProvider.FolderExists) ? false : throw new NotSupportedException(m.Name));
        CapturingEvents events = new();
        Logger logger = LogManager.CreateNullLogger();
        SlskdCorruptionHandler handler = new(new CorruptionFailureHandler(history, events, disk, logger), new SlskdCorruptUserTracker(logger), logger);

        SlskdFileState mapped = new(Transfer(@"@@peer\Music\Album\07 remote name.flac"));
        string source = """[{"Filename":"@@peer\\Music\\Album\\07 remote name.flac","Size":1000}]""";
        SlskdDownloadItem item = new(new ReleaseInfo { Source = source, Title = "Artist - Album", DownloadUrl = "u" }) { ID = "dl-1" };

        await handler.HandleCorruptDownloadAsync(
            item,
            [
                new SlskdCorruptionStrike(null, "03 - Track Three.flac", "Audio won't decode (invalid residual)"),
                new SlskdCorruptionStrike(mapped, "07 - Track Seven.flac", "Audio won't decode (Header missing)"),
            ],
            "/downloads/Artist - Album",
            new SlskdProviderSettings(),
            CancellationToken.None);

        Assert.Equal(
            "Sleezer: 2 files failed the corruption check — " +
            "03 - Track Three.flac: Audio won't decode (invalid residual); " +
            "07 - Track Seven.flac: Audio won't decode (Header missing)",
            Assert.Single(events.Failed).Message);
        Assert.Equal(0, mapped.MaxRetryCount);
    }

    private static SlskdDownloadFile Transfer(string filename) => new(
        Id: "id",
        Username: "peer",
        Direction: "Download",
        Filename: filename,
        Size: 1000,
        StartOffset: 0,
        State: "Completed, Succeeded",
        RequestedAt: DateTime.UtcNow,
        EnqueuedAt: DateTime.UtcNow,
        StartedAt: DateTime.UtcNow,
        BytesTransferred: 1000,
        AverageSpeed: 0,
        BytesRemaining: 0,
        ElapsedTime: TimeSpan.Zero,
        PercentComplete: 100,
        RemainingTime: TimeSpan.Zero,
        EndedAt: null,
        BatchId: null);
}
