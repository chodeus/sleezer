namespace TidalSharp.Data;

public sealed record ClientCredentials(string Id, string Secret)
{
    public static ClientCredentials Playback { get; } = new(Globals.CLIENT_ID_PKCE, Globals.CLIENT_SECRET_PKCE);
}
