using System;
using System.Threading.Tasks;
using NzbDrone.Core.Download;

namespace NzbDrone.Plugin.Sleezer.Core.Download
{
    public static class PostProcessGate
    {
        /// The proxies surface exactly the Completed items, so an item left Completed here
        /// is imported mid-scan — moving the files out from under it.
        public static async Task<bool> RunHeldAsync(IQueuedDownload item, Func<Task<bool>> postProcess)
        {
            if (item.Status != DownloadItemStatus.Completed)
                return true;

            item.Status = DownloadItemStatus.Downloading;

            bool accepted;
            try
            {
                accepted = await postProcess();
            }
            catch
            {
                // Never strand the item mid-flight: a throwing post-process would otherwise
                // leave it Downloading forever, invisible to both import and cleanup.
                item.Status = DownloadItemStatus.Failed;
                throw;
            }

            item.Status = accepted ? DownloadItemStatus.Completed : DownloadItemStatus.Failed;
            return accepted;
        }
    }
}
