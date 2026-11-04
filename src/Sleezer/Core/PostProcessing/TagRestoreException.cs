namespace NzbDrone.Plugin.Sleezer.Core.PostProcessing;

/// <summary>A rolled-back tagging pass could not put every file back, so the download must not be imported as it stands.</summary>
public sealed class TagRestoreException(string message, Exception inner) : IOException(message, inner);
