namespace NzbDrone.Plugin.Sleezer.Core.Model
{
    /// <summary>
    /// A release Sleezer produced and can therefore judge. Only Sleezer's own parsers
    /// implement it, so StoreMatchSpecification can never reject a result from the
    /// user's torrent or Usenet indexers.
    /// </summary>
    public interface IVerifiableRelease
    {
        /// <summary>Why the release failed verification, or null when it passed.</summary>
        string? Rejection { get; set; }
    }
}
