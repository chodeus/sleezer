using NLog;
using NzbDrone.Core.Indexers;
using NzbDrone.Plugin.Sleezer.Core.PostProcessing;
using NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek.Models;

namespace NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek;

public interface ISlskdCorruptionHandler
{
    Task HandleCorruptDownloadAsync(
        SlskdDownloadItem item,
        IReadOnlyList<SlskdCorruptionStrike> strikes,
        string? completedFolderPath,
        SlskdProviderSettings settings,
        CancellationToken ct);
}

// Slskd-internal strike shape: carries the SlskdFileState so the handler can
// zero its retry budget. Projected to the shared CorruptionStrike before
// delegating folder deletion / event publishing to ICorruptionFailureHandler.
public record SlskdCorruptionStrike(SlskdFileState? FileState, string? Reason);

public class SlskdCorruptionHandler : ISlskdCorruptionHandler
{
    private readonly ICorruptionFailureHandler _failureHandler;
    private readonly ISlskdCorruptUserTracker _userTracker;
    private readonly Logger _logger;

    public SlskdCorruptionHandler(
        ICorruptionFailureHandler failureHandler,
        ISlskdCorruptUserTracker userTracker,
        Logger logger)
    {
        _failureHandler = failureHandler;
        _userTracker = userTracker;
        _logger = logger;
    }

    public async Task HandleCorruptDownloadAsync(
        SlskdDownloadItem item,
        IReadOnlyList<SlskdCorruptionStrike> strikes,
        string? completedFolderPath,
        SlskdProviderSettings settings,
        CancellationToken ct)
    {
        if (strikes.Count == 0)
            return;

        // 1. Short-circuit the in-flight retry loop for every corrupt file.
        //    SlskdRetryHandler.OnFileStateChanged bails when MaxRetryCount <= RetryCount,
        //    so zeroing the budget prevents the re-enqueue path from firing even if a
        //    later state transition comes through.
        foreach (SlskdCorruptionStrike strike in strikes)
        {
            strike.FileState?.UpdateMaxRetryCount(0);
            string fn = strike.FileState != null ? Path.GetFileName(strike.FileState.File.Filename) : "(unmapped)";
            _logger.Info("Corruption strike: {File} | reason: {Reason}", fn, strike.Reason ?? "(unknown)");
        }

        // 2. Record per-user strikes so repeat offenders can be auto-banned.
        if (!string.IsNullOrEmpty(item.Username) && settings.BanUserAfterCorruptCount > 0)
        {
            _userTracker.RecordStrike(item.Username, strikes.Count, settings.BanUserAfterCorruptCount);
        }

        // 3. Delegate the universal steps (wipe folder, publish DownloadFailedEvent
        //    so Lidarr blocklists and re-searches) to the shared handler.
        CorruptionStrike[] shared = strikes
            .Select(s => new CorruptionStrike(
                File: s.FileState != null ? Path.GetFileName(s.FileState.File.Filename) : "(unmapped)",
                Reason: s.Reason))
            .ToArray();

        await _failureHandler.HandleAsync(
            downloadId: item.ID,
            releaseTitle: item.ReleaseInfo?.Title ?? string.Empty,
            folder: completedFolderPath ?? string.Empty,
            strikes: shared,
            protocolName: nameof(SoulseekDownloadProtocol),
            ct: ct);
    }
}
