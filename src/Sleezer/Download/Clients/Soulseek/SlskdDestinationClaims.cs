using System.Collections.Concurrent;

namespace NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek;

/// <summary>Album folders a grab is being sent into, each held by one grab until it is tracked.</summary>
public sealed class SlskdDestinationClaims
{
    private readonly ConcurrentDictionary<(int DefinitionId, string Destination), byte> _held = new();

    /// <summary>Holds the folder until the returned claim is disposed; null when another grab holds it.</summary>
    public IDisposable? TryHold(int definitionId, string destination)
    {
        (int, string) key = (definitionId, destination);
        return _held.TryAdd(key, 0) ? new Claim(this, key) : null;
    }

    private sealed class Claim(SlskdDestinationClaims owner, (int, string) key) : IDisposable
    {
        public void Dispose() => owner._held.TryRemove(key, out _);
    }
}
