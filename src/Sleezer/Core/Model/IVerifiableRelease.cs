namespace NzbDrone.Plugin.Sleezer.Core.Model
{
    /// <summary>A release Sleezer produced and can judge; nothing else implements it.</summary>
    public interface IVerifiableRelease
    {
        /// <summary>Why the release failed verification, or null when it passed.</summary>
        string? Rejection { get; set; }
    }
}
