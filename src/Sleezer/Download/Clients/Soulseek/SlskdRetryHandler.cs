using NLog;
using NzbDrone.Core.Download;
using System.Text.Json;
using NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek.Models;

namespace NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek;

public class SlskdRetryHandler(ISlskdApiClient apiClient, Logger logger)
{
    private readonly ISlskdApiClient _apiClient = apiClient;
    private readonly Logger _logger = logger;

    public void OnFileStateChanged(SlskdDownloadItem? item, SlskdFileState fileState, SlskdProviderSettings settings)
    {
        fileState.UpdateMaxRetryCount(settings.RetryAttempts);

        if (fileState.GetStatus() != DownloadItemStatus.Warning)
            return;
        if (item == null)
            return;

        _logger.Trace("Retry triggered: {Filename} | State: {State} | Attempt: {Attempt}/{Max}", Path.GetFileName(fileState.File.Filename), fileState.State, fileState.RetryCount + 1, fileState.MaxRetryCount);
        _ = RetryDownloadAsync(item, fileState, settings);
    }

    private async Task RetryDownloadAsync(SlskdDownloadItem item, SlskdFileState fileState, SlskdProviderSettings settings)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(item.ReleaseInfo.Source);
            JsonElement matchingEl = doc.RootElement.EnumerateArray()
                .FirstOrDefault(x =>
                    x.TryGetProperty("Filename", out JsonElement fn) &&
                    fn.GetString() == fileState.File.Filename);

            if (matchingEl.ValueKind == JsonValueKind.Undefined)
            {
                return;
            }

            long size = matchingEl.TryGetProperty("Size", out JsonElement sz) ? sz.GetInt64() : 0L;
            string username = item.Username ?? ExtractUsernameFromPath(item.ReleaseInfo.DownloadUrl);

            await _apiClient.EnqueueDownloadAsync(settings, username, [(fileState.File.Filename, size)]);
            _logger.Trace("Retry enqueued: {Filename}", Path.GetFileName(fileState.File.Filename));
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to retry download for file: {Filename}", fileState.File.Filename);
        }
        finally
        {
            fileState.IncrementAttempt();
            // After this attempt the retry budget is gone. Mark the file
            // terminally failed so SlskdStatusResolver promotes the item to
            // DownloadItemStatus.Failed on the next poll, even if the retry
            // sits in "Queued, Remotely" against a dead peer indefinitely.
            // GetStatus still lets a Completed/Downloading transport state win,
            // so a healthy retry that succeeds isn't cancelled.
            if (fileState.RetryCount >= fileState.MaxRetryCount)
                fileState.MarkRetriesExhausted();
        }
    }

    private static string ExtractUsernameFromPath(string path)
    {
        string[] parts = path.TrimEnd('/').Split('/');
        return Uri.UnescapeDataString(parts[^1]);
    }
}
