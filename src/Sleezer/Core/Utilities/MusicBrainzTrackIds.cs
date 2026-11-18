using System;
using System.Reflection;
using System.Threading;
using NLog;
using NzbDrone.Core.MediaFiles;

namespace NzbDrone.Plugin.Sleezer.Core.Utilities
{
    // Lidarr 4.0 moved the recording id to MusicBrainzRecordingId and dropped MusicBrainzReleaseTrackId.
    // Never name either property directly: the plugin builds against the older AudioTag and must load on both.
    public static class MusicBrainzTrackIds
    {
        private static readonly Logger _logger = LogManager.GetCurrentClassLogger();
        private static int _warnedUnknownShape;

        /// Sets the ids where the running Lidarr's AudioTag keeps them, else warns once and sets neither; a null id keeps the tag's value.
        public static void Set(AudioTag tag, string? recordingId, string? releaseTrackId) => SetOn(tag, recordingId, releaseTrackId);

        internal static void SetOn(object tag, string? recordingId, string? releaseTrackId)
        {
            var type = tag.GetType();
            var trackId = StringProperty(type, "MusicBrainzTrackId");
            var recording = StringProperty(type, "MusicBrainzRecordingId");
            var releaseTrack = StringProperty(type, "MusicBrainzReleaseTrackId");

            if (trackId != null && recording != null)
            {
                SetOrKeep(tag, trackId, releaseTrackId);
                SetOrKeep(tag, recording, recordingId);
            }
            else if (trackId != null && releaseTrack != null)
            {
                SetOrKeep(tag, trackId, recordingId);
                SetOrKeep(tag, releaseTrack, releaseTrackId);
            }
            else if (Interlocked.Exchange(ref _warnedUnknownShape, 1) == 0)
            {
                // Not a throw: the caller's retag must still write every other field.
                _logger.Warn("{0} keeps the MusicBrainz recording and release track ids in properties this plugin does not know; retagged files keep their own", type.FullName);
            }
        }

        private static PropertyInfo? StringProperty(Type type, string name) =>
            type.GetProperty(name) is { CanRead: true, CanWrite: true } property && property.PropertyType == typeof(string) ? property : null;

        private static void SetOrKeep(object tag, PropertyInfo property, string? value) => property.SetValue(tag, value ?? property.GetValue(tag));
    }
}
