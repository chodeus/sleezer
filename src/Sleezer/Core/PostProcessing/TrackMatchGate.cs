using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Plugin.Sleezer.Core.PostProcessing;

/// <summary>Per-track confidence for a release the album-level match accepted.</summary>
public static class TrackMatchGate
{
    /// <summary>Mapped files whose own match is weaker than <paramref name="maxDistance"/>.</summary>
    public static int WeakTracks(TrackMapping mapping, double maxDistance) =>
        mapping.Mapping.Values.Count(m => (m.Item2?.NormalizedDistance() ?? 1.0) > maxDistance);
}
