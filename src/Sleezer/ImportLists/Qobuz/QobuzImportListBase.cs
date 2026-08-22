using System;
using System.Collections.Generic;
using FluentValidation.Results;
using NLog;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Plugin.Sleezer.Qobuz;

namespace NzbDrone.Core.ImportLists.Qobuz
{
    /// <summary>
    /// Shared plumbing for the Qobuz lists: they all page a Qobuz collection and all
    /// depend on the session the indexer establishes, so neither concern is worth
    /// three copies.
    /// </summary>
    public abstract class QobuzImportListBase<TSettings>(
        IImportListStatusService importListStatusService,
        IConfigService configService,
        IParsingService parsingService,
        Logger logger)
        : ImportListBase<TSettings>(importListStatusService, configService, parsingService, logger)
        where TSettings : IImportListSettings, new()
    {
        protected const int PageSize = 500;

        public override ImportListType ListType => ImportListType.Other;
        public override TimeSpan MinRefreshInterval => TimeSpan.FromHours(12);

        /// <summary>
        /// Pages a Qobuz collection endpoint until the reported total is reached.
        /// Stops on an empty page as well as a null one: Qobuz reporting a total it
        /// will not serve would otherwise leave the offset unmoved and spin forever.
        /// </summary>
        protected void PageThrough(string what, Func<int, (int Returned, int Total)> fetchPage)
        {
            int offset = 0;

            while (true)
            {
                (int returned, int total) = fetchPage(offset);
                if (returned <= 0)
                    break;

                offset += returned;
                if (offset >= total)
                    break;
            }

            _logger.Debug("Qobuz import list: read {Count} {What}", offset, what);
        }

        /// <summary>Fails the test with an actionable message when no session exists.</summary>
        protected bool RequireSession(List<ValidationFailure> failures)
        {
            if (QobuzAPI.Instance?.Login != null)
                return true;

            failures.Add(new ValidationFailure(string.Empty,
                "Not signed in to Qobuz. Add and save the Qobuz indexer first — the import lists reuse the session it establishes."));
            return false;
        }
    }
}
