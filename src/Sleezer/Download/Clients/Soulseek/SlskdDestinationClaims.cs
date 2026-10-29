namespace NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek;

/// <summary>Album folders a grab is being sent into, each held by one grab until it is tracked.</summary>
public sealed class SlskdDestinationClaims
{
    private readonly HashSet<string> _held = new(SlskdLeftoverFolder.FolderComparer);

    /// <summary>Holds the local folder until the returned claim is disposed; null when another grab, through any client, holds it or a folder inside or above it.</summary>
    public IDisposable? TryHold(string folder)
    {
        lock (_held)
        {
            if (_held.Any(h => SlskdLeftoverFolder.Overlaps(h, folder)))
                return null;
            _held.Add(folder);
        }

        return new Claim(this, folder);
    }

    private void Release(string folder)
    {
        lock (_held)
            _held.Remove(folder);
    }

    private sealed class Claim(SlskdDestinationClaims claims, string folder) : IDisposable
    {
        public void Dispose() => claims.Release(folder);
    }
}
