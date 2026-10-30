using NzbDrone.Core.Download.History;
using NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek.Models;

namespace NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek;

/// <summary>
/// The Soulseek grabs of the last 14 days, newest first, with their files read once: a folder no grab owns is probed
/// on every poll, so the whole window is read once a minute and each call reads only the grabs written since.
/// </summary>
public sealed class SlskdRecentGrabs(Func<DateTime, IReadOnlyList<DownloadHistory>> grabsSince, Func<DateTime> now)
{
    private static readonly TimeSpan Window = TimeSpan.FromDays(14);
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(1);

    // A grab's row can be written after a read that started later than its date.
    private static readonly TimeSpan Overlap = TimeSpan.FromMinutes(1);

    private readonly object _lock = new();
    private List<(DownloadHistory Grab, SlskdDownloadItem Files)>? _grabs;
    private DateTime _readAt;
    private DateTime _checkedAt;

    /// <summary>The grabs whose files include <paramref name="file"/>, newest first.</summary>
    public List<DownloadHistory> Owning(string file) => [.. Current().Where(g => g.Files.OwnsFile(file)).Select(g => g.Grab)];

    private List<(DownloadHistory Grab, SlskdDownloadItem Files)> Current()
    {
        lock (_lock)
        {
            DateTime at = now();
            if (_grabs == null || at - _readAt >= Lifetime)
            {
                _grabs = WithFiles(grabsSince(at - Window));
                _readAt = at;
            }
            else if (WithFiles(grabsSince(_checkedAt - Overlap)) is { Count: > 0 } added)
            {
                HashSet<string> ids = new(added.Select(g => g.Grab.DownloadId), StringComparer.OrdinalIgnoreCase);
                _grabs = [.. added, .. _grabs.Where(g => !ids.Contains(g.Grab.DownloadId))];
            }

            _checkedAt = at;
            return _grabs;
        }
    }

    private static List<(DownloadHistory Grab, SlskdDownloadItem Files)> WithFiles(IReadOnlyList<DownloadHistory> history)
    {
        List<(DownloadHistory Grab, SlskdDownloadItem Files)> grabs = [];
        foreach (DownloadHistory grab in history)
        {
            try
            {
                grabs.Add((grab, new SlskdDownloadItem(grab.Release)));
            }
            catch (Exception)
            {
                // A release whose file list can't be read owns no folder.
            }
        }

        return grabs;
    }
}
