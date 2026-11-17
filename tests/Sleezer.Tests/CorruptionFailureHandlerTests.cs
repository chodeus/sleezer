using NLog;
using NLog.Targets;
using NzbDrone.Common.Disk;
using NzbDrone.Core.History;
using NzbDrone.Plugin.Sleezer.Core.PostProcessing;
using Sleezer.Tests.Fakes;
using Xunit;
using Fake = Sleezer.Tests.PreImportTaggerWholeDownloadTests.Fake;

namespace Sleezer.Tests;

public class CorruptionFailureHandlerTests
{
    [Fact]
    public async Task Every_failed_file_is_logged_including_those_the_message_only_counts()
    {
        EntityHistory grab = new() { ArtistId = 1, AlbumId = 10, DownloadId = "dl-1", SourceTitle = "Artist - Album", Data = [] };
        IHistoryService history = Fake.Of<IHistoryService>((m, _) =>
            m.Name == nameof(IHistoryService.Find) ? new List<EntityHistory> { grab } : throw new NotSupportedException(m.Name));
        IDiskProvider disk = Fake.Of<IDiskProvider>((m, _) =>
            m.Name == nameof(IDiskProvider.FolderExists) ? false : throw new NotSupportedException(m.Name));
        CapturingEvents events = new();
        (Logger logger, MemoryTarget logs) = TestLogs.Capturing("corruption");
        CorruptionStrike[] strikes = [.. Enumerable.Range(1, 30).Select(i => new CorruptionStrike($"{i:00} - Track Number {i:00}.flac", "Audio won't decode (invalid residual)"))];

        await new CorruptionFailureHandler(history, events, disk, logger)
            .HandleAsync("dl-1", "Artist - Album", "/downloads/Artist - Album", strikes, "SoulseekDownloadProtocol", CancellationToken.None);

        Assert.EndsWith("; +17 more (see the Lidarr log)", Assert.Single(events.Failed).Message);
        List<string> warned = logs.Logs.Where(l => l.StartsWith("Warn|[post-process] Corrupt file in download \"dl-1\": ", StringComparison.Ordinal)).ToList();
        Assert.Equal(30, warned.Count);
        Assert.Contains("Warn|[post-process] Corrupt file in download \"dl-1\": \"30 - Track Number 30.flac: Audio won't decode (invalid residual)\"", warned);
    }
}
