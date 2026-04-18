namespace NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek.Models;

public readonly struct DownloadKey<TOuterKey, TInnerKey>(TOuterKey outerKey, TInnerKey innerKey)
    where TOuterKey : notnull
    where TInnerKey : notnull
{
    public TOuterKey OuterKey { get; } = outerKey;
    public TInnerKey InnerKey { get; } = innerKey;

    public override readonly bool Equals(object? obj) =>
        obj is DownloadKey<TOuterKey, TInnerKey> other &&
        EqualityComparer<TOuterKey>.Default.Equals(OuterKey, other.OuterKey) &&
        EqualityComparer<TInnerKey>.Default.Equals(InnerKey, other.InnerKey);

    public override readonly int GetHashCode() =>
        HashCode.Combine(OuterKey, InnerKey);

    public static bool operator ==(DownloadKey<TOuterKey, TInnerKey> left, DownloadKey<TOuterKey, TInnerKey> right) =>
        left.Equals(right);

    public static bool operator !=(DownloadKey<TOuterKey, TInnerKey> left, DownloadKey<TOuterKey, TInnerKey> right) =>
        !(left == right);
}
