namespace NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek;

/// <summary>
/// Remembers which slskd events were handled. slskd lists events newest first,
/// so a growing offset walks into older history and never reaches new events.
/// </summary>
public sealed class SlskdEventCursor
{
    private readonly object _lock = new();
    private bool _seeded;
    private DateTime _newest = DateTime.MinValue;
    private HashSet<Guid> _idsAtNewest = [];

    /// <summary>A full page of unseen events means older unseen ones may sit on the next page.</summary>
    public bool NeedsNextPage(IReadOnlyCollection<SlskdEventRecord> page, int pageSize)
    {
        lock (_lock)
            return _seeded && page.Count >= pageSize && page.All(IsUnseen);
    }

    /// <summary>Returns the unseen events oldest first and marks them seen.</summary>
    public List<SlskdEventRecord> TakeUnseen(IEnumerable<SlskdEventRecord> events)
    {
        lock (_lock)
        {
            List<SlskdEventRecord> fresh = [.. events.Where(IsUnseen).DistinctBy(e => e.Id).OrderBy(e => e.Timestamp)];
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
            return fresh;
        }
    }

    private bool IsUnseen(SlskdEventRecord record) =>
        record.Timestamp > _newest || (record.Timestamp == _newest && !_idsAtNewest.Contains(record.Id));
}
