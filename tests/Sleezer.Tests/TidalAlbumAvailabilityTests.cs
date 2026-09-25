using NzbDrone.Plugin.Sleezer.Core.Tidal;
using Xunit;

namespace Sleezer.Tests;

// Albums fetched for a track result may carry neither flag; hiding those would hide everything.
public class TidalAlbumAvailabilityTests
{
    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(false, false, true)]
    [InlineData(true, true, false)]
    [InlineData(null, null, false)]
    [InlineData(null, true, false)]
    [InlineData(true, null, false)]
    public void Only_an_explicit_false_hides_an_album(bool? allowStreaming, bool? streamReady, bool unavailable)
    {
        Assert.Equal(unavailable, TidalAlbumAvailability.Unavailable(allowStreaming, streamReady));
    }
}
