using System.Collections.Concurrent;

namespace NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek;

/// <summary>Album folders a grab is being sent into, each held by one grab until it is tracked.</summary>
public sealed class SlskdDestinationClaims
{
    private readonly ConcurrentDictionary<string, byte> _held = new(SlskdLeftoverFolder.FolderComparer);

    /// <summary>Holds the local folder until the returned claim is disposed; null when another grab, through any client, holds it.</summary>
    public IDisposable? TryHold(string folder) => _held.TryAdd(folder, 0) ? new Claim(_held, folder) : null;

    private sealed class Claim(ConcurrentDictionary<string, byte> held, string folder) : IDisposable
    {
        public void Dispose() => held.TryRemove(folder, out _);
    }
}
