using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Plugin.Sleezer.Metadata.FFmpeg
{
    /// <summary>Notes each track file Lidarr saves, for <see cref="SavedImports"/>.</summary>
    public class SavedImportsHandler : IHandle<TrackFileAddedEvent>
    {
        public void Handle(TrackFileAddedEvent message)
        {
            if (message.TrackFile != null)
                SavedImports.Saved(message.TrackFile);
        }
    }
}
