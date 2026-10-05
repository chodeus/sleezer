using NzbDrone.Plugin.Sleezer.Core.Tidal;
using Xunit;

namespace Sleezer.Tests;

public class TidalLoadedLoginTests
{
    private static readonly DateTime Now = new(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_refreshed_login_stays_loaded_until_the_token_or_client_changes()
    {
        var loaded = TidalLoadedLogin.Loaded("token", "client", refreshed: true, Now);

        Assert.True(loaded.IsCurrentFor("token", "client", Now.AddDays(30)));
        Assert.False(loaded.IsCurrentFor("other-token", "client", Now));
        Assert.False(loaded.IsCurrentFor("token", "other-client", Now));
    }

    [Fact]
    public void A_login_whose_refresh_failed_is_loaded_again_after_the_retry_interval()
    {
        // Left loaded, a login saved under a refused client would keep failing playback until its token expired.
        var loaded = TidalLoadedLogin.Loaded("token", "client", refreshed: false, Now);

        Assert.True(loaded.IsCurrentFor("token", "client", Now + TidalLoadedLogin.RefreshRetryInterval - TimeSpan.FromSeconds(1)));
        Assert.False(loaded.IsCurrentFor("token", "client", Now + TidalLoadedLogin.RefreshRetryInterval));
    }

    [Fact]
    public void A_later_refresh_clears_the_pending_retry()
    {
        var loaded = TidalLoadedLogin.Loaded("token", "client", refreshed: false, Now) with { AccessToken = "new-token", RetryRefreshAt = DateTime.MaxValue };

        Assert.True(loaded.IsCurrentFor("new-token", "client", Now.AddDays(30)));
    }
}
