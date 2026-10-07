namespace NzbDrone.Plugin.Sleezer.Notifications.QueueCleaner
{
    /// <summary>Sorts Lidarr's import rejection messages into the two failures Queue Cleaner acts on.</summary>
    public static class ImportRejections
    {
        // Lidarr's own rejection texts; matched as substrings because most carry details after them.
        private static readonly string[] MissingTracks =
        [
            "Has missing tracks",
            "Has fewer tracks than existing release",
        ];

        private static readonly string[] PoorMatch =
        [
            "Album match is not close enough",
            "Worst track match",
            "Has unmatched tracks",
            "Couldn't find similar album",
        ];

        public static (bool MissingTracks, bool PoorMatch) Classify(IEnumerable<string> messages)
        {
            List<string> all = [.. messages];
            return (all.Any(m => ContainsAny(m, MissingTracks)), all.Any(m => ContainsAny(m, PoorMatch)));
        }

        private static bool ContainsAny(string message, string[] phrases) =>
            phrases.Any(p => message.Contains(p, StringComparison.OrdinalIgnoreCase));
    }
}
