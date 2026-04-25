using System.Collections.Concurrent;
using NLog;

namespace NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek;

public interface ISlskdCorruptUserTracker
{
    /// <summary>
    /// Record one or more corruption strikes against a user. If the running
    /// strike count reaches <paramref name="banThreshold"/>, the user is added
    /// to the ban set returned by <see cref="GetBannedUsers"/> until the plugin
    /// is restarted.
    /// </summary>
    void RecordStrike(string username, int strikeCount, int banThreshold);

    /// <summary>
    /// Current set of users whose strike count is at or above the last-configured
    /// <c>BanUserAfterCorruptCount</c> threshold. Consulted by
    /// <c>SlskdIndexerParser</c> to filter them out of search results. Returns
    /// empty when no strikes have been recorded or the threshold is 0.
    /// </summary>
    IReadOnlySet<string> GetBannedUsers();
}

public class SlskdCorruptUserTracker : ISlskdCorruptUserTracker
{
    private readonly ConcurrentDictionary<string, int> _strikes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Logger _logger;
    private volatile int _lastKnownThreshold;

    public SlskdCorruptUserTracker(Logger logger)
    {
        _logger = logger;
    }

    public void RecordStrike(string username, int strikeCount, int banThreshold)
    {
        if (string.IsNullOrEmpty(username) || strikeCount <= 0)
            return;

        _lastKnownThreshold = banThreshold;
        int total = _strikes.AddOrUpdate(username, strikeCount, (_, current) => current + strikeCount);

        if (banThreshold > 0 && total >= banThreshold)
            _logger.Warn("Slskd user banned after {Total} corruption strikes: {Username}", total, username);
        else
            _logger.Info("Slskd corruption strike recorded for {Username}: {Total} total (threshold {Threshold})", username, total, banThreshold);
    }

    public IReadOnlySet<string> GetBannedUsers()
    {
        int threshold = _lastKnownThreshold;
        if (threshold <= 0)
            return new HashSet<string>();

        HashSet<string> banned = new(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, int> kv in _strikes)
            if (kv.Value >= threshold)
                banned.Add(kv.Key);

        return banned;
    }
}
