namespace NzbDrone.Plugin.Sleezer.Core.Model
{
    /// <summary>Indexer settings that can switch the shared store-result verifier off.</summary>
    public interface IStoreMatchingSettings
    {
        bool StrictMatching { get; }
    }
}
