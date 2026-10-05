using Newtonsoft.Json.Linq;
using NzbDrone.Plugin.Sleezer.Core.Tidal;
using TidalSharp;
using Xunit;

namespace Sleezer.Tests;

public class TidalStreamExtrasTests
{
    [Theory]
    [InlineData("STEREO", false)]
    [InlineData("stereo", false)]
    [InlineData(null, false)]
    [InlineData("DOLBY_ATMOS", true)]
    [InlineData("SONY_360RA", true)]
    public void Only_stereo_or_an_absent_mode_passes(string? audioMode, bool refused)
    {
        Assert.Equal(refused, StereoGuard.IsNotStereo(audioMode));
    }

    [Fact]
    public void Only_a_tracks_composers_are_kept()
    {
        var page = JObject.Parse("""
            {"items":[
              {"type":"track","item":{"id":11},"credits":[
                {"type":"Composer","contributors":[{"name":"Ann Writer"},{"name":"Bo Writer"},{"name":"ann writer"}]},
                {"type":"Producer","contributors":[{"name":"Cy Producer"}]}]},
              {"type":"track","item":{"id":12},"credits":[{"type":"Producer","contributors":[{"name":"Cy Producer"}]}]},
              {"type":"video","item":{"id":13},"credits":[{"type":"Composer","contributors":[{"name":"Dee Video"}]}]}
            ],"totalNumberOfItems":3}
            """);
        var composers = new Dictionary<string, string[]>();

        TidalCredits.AddComposers(page, composers);

        Assert.Equal(["Ann Writer", "Bo Writer"], composers["11"]);
        Assert.False(composers.ContainsKey("12"));
        Assert.False(composers.ContainsKey("13"));
    }
}
