using System.Collections.Concurrent;

namespace NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek;

/// <summary>Album folders a grab is being sent into, each held by one grab until it is tracked.</summary>
public sealed class SlskdDestinationClaims
{
    private readonly ConcurrentDictionary<int, ConcurrentDictionary<string, byte>> _held = new();

    /// <summary>Holds the folder until the returned claim is disposed; null when another grab holds it under a name the filesystem treats as the same.</summary>
    public IDisposable? TryHold(int definitionId, string destination)
    {
        ConcurrentDictionary<string, byte> held = _held.GetOrAdd(definitionId, _ => new(SlskdLeftoverFolder.NameComparer));
        return held.TryAdd(destination, 0) ? new Claim(held, destination) : null;
    }

    private sealed class Claim(ConcurrentDictionary<string, byte> held, string destination) : IDisposable
    {
        public void Dispose() => held.TryRemove(destination, out _);
    }
}
