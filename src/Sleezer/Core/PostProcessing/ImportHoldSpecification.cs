using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.MediaFiles.TrackImport;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Plugin.Sleezer.Core.PostProcessing;

/// <summary>
/// Refuses the automatic import of a held download, so Lidarr's queue shows it blocked with the reason and a Manual Import.
/// Manual Import lists the same reason beside each file and still imports what is picked.
/// </summary>
public class ImportHoldSpecification(IImportHolds holds) : IImportDecisionEngineSpecification<LocalAlbumRelease>
{
    public Decision IsSatisfiedBy(LocalAlbumRelease item, DownloadClientItem downloadClientItem) =>
        downloadClientItem?.DownloadId is { Length: > 0 } id && holds.ReasonFor(id) is { } reason
            ? Decision.Reject($"Held for a manual import: {reason}")
            : Decision.Accept();
}
