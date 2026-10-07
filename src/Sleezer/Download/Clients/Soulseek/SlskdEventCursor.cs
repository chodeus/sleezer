namespace NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek;

/// <summary>
/// Polls slskd's event log for one download client. slskd lists events newest first,
/// so a growing offset walks into older history and never reaches new events.
/// </summary>
public sealed class SlskdEventCursor
{
    public const int MaxAttempts = 3;

    private readonly SemaphoreSlim _pollGate = new(1, 1);
    private readonly Dictionary<Guid, (SlskdEventRecord Record, int Failures)> _failed = [];
    private bool _seeded;
    private DateTime _newest = DateTime.MinValue;
    private HashSet<Guid> _idsAtNewest = [];

    /// <summary>
    /// Hands each new event to <paramref name="handle"/>, oldest first; skipped while another poll runs.
    /// A failed event is offered again on later polls, <see cref="MaxAttempts"/> times in all.
    /// </summary>
    public async Task PollAsync(
        Func<int, int, Task<List<SlskdEventRecord>>> fetchPage,
        int pageSize,
        Func<SlskdEventRecord, Task> handle,
        Action<SlskdEventRecord, Exception, bool> onFailure)
    {
        if (!await _pollGate.WaitAsync(0))
            return;

        try
        {
            foreach (SlskdEventRecord record in await ReadUnseenAsync(fetchPage, pageSize))
            {
                try
                {
                    await handle(record);
                    _failed.Remove(record.Id);
                }
                catch (Exception ex)
                {
                    onFailure(record, ex, KeepForRetry(record));
                }
            }
        }
        finally
        {
            _pollGate.Release();
        }
    }

    // Pages until one reaches below the newest handled timestamp, so this ends.
    internal async Task<List<SlskdEventRecord>> ReadUnseenAsync(Func<int, int, Task<List<SlskdEventRecord>>> fetchPage, int pageSize)
    {
        List<SlskdEventRecord> events = [];
        for (int offset = 0; ; offset += pageSize)
        {
            List<SlskdEventRecord> page = await fetchPage(offset, pageSize);
            events.AddRange(page);
            if (!NeedsNextPage(page, pageSize))
                break;
        }

        return TakeUnseen(events);
    }

    // The first poll reads one page, as before the cursor existed.
    private bool NeedsNextPage(List<SlskdEventRecord> page, int pageSize) =>
        _seeded && page.Count >= pageSize && page.Min(e => e.Timestamp) >= _newest;

    /// <summary>Returns the unseen events plus those due a retry, oldest first, and marks the unseen ones seen.</summary>
    internal List<SlskdEventRecord> TakeUnseen(IEnumerable<SlskdEventRecord> events)
    {
        List<SlskdEventRecord> fresh = [.. events.Where(IsUnseen).DistinctBy(e => e.Id)];
        foreach (SlskdEventRecord record in fresh)
        {
            if (record.Timestamp > _newest)
            {
                _newest = record.Timestamp;
                _idsAtNewest = [record.Id];
            }
            else if (record.Timestamp == _newest)
            {
                _idsAtNewest.Add(record.Id);
            }
        }

        _seeded = true;
        return [.. fresh.Concat(_failed.Values.Select(f => f.Record)).DistinctBy(e => e.Id).OrderBy(e => e.Timestamp)];
    }

    private bool KeepForRetry(SlskdEventRecord record)
    {
        int failures = (_failed.TryGetValue(record.Id, out (SlskdEventRecord Record, int Failures) entry) ? entry.Failures : 0) + 1;
        if (failures >= MaxAttempts)
        {
            _failed.Remove(record.Id);
            return false;
        }

        _failed[record.Id] = (record, failures);
        return true;
    }

    private bool IsUnseen(SlskdEventRecord record) =>
        record.Timestamp > _newest || (record.Timestamp == _newest && !_idsAtNewest.Contains(record.Id));
}
