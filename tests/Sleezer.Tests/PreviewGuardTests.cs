using TidalSharp;
using Xunit;

namespace Sleezer.Tests;

public class PreviewGuardTests
{
    [Theory]
    [InlineData("FULL", false)]
    [InlineData("full", false)]
    [InlineData("PREVIEW", true)]
    [InlineData("preview", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void Only_a_non_full_presentation_is_a_preview(string? assetPresentation, bool expected) =>
        Assert.Equal(expected, PreviewGuard.IsPreview(assetPresentation));
}
