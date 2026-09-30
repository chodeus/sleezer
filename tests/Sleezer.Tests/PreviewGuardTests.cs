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
    [InlineData("", true)]
    [InlineData(" ", true)]
    public void Anything_but_a_full_or_missing_presentation_is_a_preview(string? assetPresentation, bool expected) =>
        Assert.Equal(expected, PreviewGuard.IsPreview(assetPresentation));
}
