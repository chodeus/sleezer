using System;
using NzbDrone.Common.Http;
using NzbDrone.Core.Indexers;
using Xunit;

namespace Sleezer.Tests;

public class SessionIndexerRequestTests
{
    private sealed class FakeSession { }

    [Fact]
    public void Parser_gets_the_session_that_built_the_request_not_a_later_one()
    {
        var first = new FakeSession();
        var request = new SessionIndexerRequest<FakeSession>("https://example.invalid/search", HttpAccept.Json, first);
        var later = new SessionIndexerRequest<FakeSession>("https://example.invalid/search", HttpAccept.Json, new FakeSession());

        Assert.Same(first, SessionIndexerRequest<FakeSession>.Of(new IndexerResponse(request, null)));
        Assert.NotSame(later.Session, SessionIndexerRequest<FakeSession>.Of(new IndexerResponse(request, null)));
    }

    [Fact]
    public void A_request_without_a_session_is_refused()
    {
        var plain = new IndexerRequest("https://example.invalid/search", HttpAccept.Json);

        Assert.Throws<InvalidOperationException>(() => SessionIndexerRequest<FakeSession>.Of(new IndexerResponse(plain, null)));
    }
}
