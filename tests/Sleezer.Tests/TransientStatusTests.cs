using System.Net;
using NzbDrone.Plugin.Sleezer.Core.Utilities;
using Xunit;

namespace Sleezer.Tests;

public class TransientStatusTests
{
    [Theory]
    [InlineData(429, true)]
    [InlineData(500, true)]
    [InlineData(503, true)]
    [InlineData(599, true)]
    [InlineData(600, false)]
    [InlineData(499, false)]
    [InlineData(404, false)]
    [InlineData(401, false)]
    [InlineData(200, false)]
    public void Only_a_429_or_a_5xx_is_transient(int status, bool transient)
    {
        Assert.Equal(transient, TransientStatus.IsTransient(status));
        Assert.Equal(transient, TransientStatus.IsTransient((HttpStatusCode)status));
    }
}
