using System;
using System.Collections.Generic;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Indexers.Bandcamp
{
    /// Contract-only: HttpIndexerBase requires a parser, but BandcampIndexer overrides
    /// every fetch path and builds releases from the collection API, so nothing routes a
    /// response through here. It throws rather than returning empty so that un-overriding
    /// a fetch path fails loudly instead of silently yielding no releases.
    public class BandcampParser : IParseIndexerResponse
    {
        public IList<ReleaseInfo> ParseResponse(IndexerResponse indexerResponse)
            => throw new NotSupportedException(
                "Bandcamp releases come from the authenticated collection API via BandcampIndexer's fetch overrides, not from parsing an indexer response.");
    }
}
