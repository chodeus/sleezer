using NzbDrone.Plugin.Sleezer.Metadata.FFmpeg;
using Xunit;

namespace Sleezer.Tests;

// Issue #102: only a storefront guarantees the product is a digital release. A peer-to-peer
// source can serve anything — steering a Soulseek CD rip onto a Digital Media release would
// be the same mistagging in reverse.
public class PostProcessClientTests
{
    [Theory]
    [InlineData(PostProcessClient.Qobuz)]
    [InlineData(PostProcessClient.Tidal)]
    [InlineData(PostProcessClient.Deezer)]
    [InlineData(PostProcessClient.Bandcamp)]
    [InlineData(PostProcessClient.Lucida)]
    [InlineData(PostProcessClient.DABMusic)]
    [InlineData(PostProcessClient.TripleTriple)]
    public void Storefronts_can_only_deliver_a_digital_release(PostProcessClient client)
    {
        Assert.True(client.IsDigitalStorefront());
    }

    [Theory]
    [InlineData(PostProcessClient.Slskd)]     // peer-to-peer: provenance unknowable
    [InlineData(PostProcessClient.SubSonic)]  // personal library: usually your own rips
    public void Sources_that_could_serve_anything_are_not_storefronts(PostProcessClient client)
    {
        Assert.False(client.IsDigitalStorefront());
    }

    // Fail closed: a client added later must be classified deliberately, not inherit
    // digital steering from a catch-all default.
    [Fact]
    public void An_unlisted_client_is_not_treated_as_a_storefront()
    {
        Assert.False(((PostProcessClient)999).IsDigitalStorefront());
    }
}
