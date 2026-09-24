using System;
using NzbDrone.Common.Http;

namespace NzbDrone.Core.Indexers
{
    // Carries the session that built the request, so its parser cannot pick up a session swapped in since.
    public class SessionIndexerRequest<TSession> : IndexerRequest
        where TSession : class
    {
        public SessionIndexerRequest(string url, HttpAccept httpAccept, TSession session)
            : base(url, httpAccept)
        {
            Session = session;
        }

        public TSession Session { get; }

        public static TSession Of(IndexerResponse response) =>
            (response.Request as SessionIndexerRequest<TSession>)?.Session
            ?? throw new InvalidOperationException($"Response did not come from a {typeof(TSession).Name} session request");
    }
}
