using System.Collections.Concurrent;
using NLog;

namespace NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek;

public interface ISlskdCorruptUserTracker
{
    /// <summary>
    /// Record one or more corruption strikes against a user; strikes last until the
    /// plugin restarts. <paramref name="banThreshold"/> only words the log line: the
    /// clients' thresholds from <see cref="UseThreshold"/> decide the ban.
    /// </summary>
    void RecordStrike(string username, int strikeCount, int banThreshold);

    /// <summary>
    /// Current set of users whose strike count is at or above the lowest
    /// <c>BanUserAfterCorruptCount</c> above 0 of any slskd client. Consulted by
    /// <c>SlskdIndexerParser</c> to filter them out of search results. Returns
    /// empty when no strikes have been recorded or every client's count is 0.
    /// </summary>
    IReadOnlySet<string> GetBannedUsers();

    /// <summary>A download client's current Ban User After Corrupt Count; 0 releases the bans it alone imposed.</summary>
    void UseThreshold(int clientId, int banThreshold);
}

public class SlskdCorruptUserTracker : ISlskdCorruptUserTracker
{
    private readonly ConcurrentDictionary<string, int> _strikes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Logger _logger;
    private readonly ConcurrentDictionary<int, int> _thresholds = new();

    public SlskdCorruptUserTracker(Logger logger)
    {
        _logger = logger;
    }

    public void RecordStrike(string username, int strikeCount, int banThreshold)
    {
        if (string.IsNullOrEmpty(username) || strikeCount <= 0)
            return;

        int total = _strikes.AddOrUpdate(username, strikeCount, (_, current) => current + strikeCount);

        if (banThreshold > 0 && total >= banThreshold)
            _logger.Warn("Slskd user banned after {Total} corruption strikes: {Username}", total, username);
        else
            _logger.Info("Slskd corruption strike recorded for {Username}: {Total} total (threshold {Threshold})", username, total, banThreshold);
    }

    // Read on every download-client poll, per client: one client's 0 must not lift another's bans.
    public void UseThreshold(int clientId, int banThreshold) => _thresholds[clientId] = banThreshold;

    public IReadOnlySet<string> GetBannedUsers()
    {
        int threshold = _thresholds.Values.Where(t => t > 0).DefaultIfEmpty(0).Min();
        if (threshold <= 0)
            return new HashSet<string>();

        HashSet<string> banned = new(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, int> kv in _strikes)
            if (kv.Value >= threshold)
                banned.Add(kv.Key);

        return banned;
    }
}
