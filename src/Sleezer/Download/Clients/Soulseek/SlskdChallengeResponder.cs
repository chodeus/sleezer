using System.Text.Json;
using System.Text.RegularExpressions;
using NLog;
using NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek.Models;

namespace NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek;

/// <summary>A private message slskd received, read from its PrivateMessageReceived event.</summary>
public sealed record SlskdPrivateMessage(string Username, string Text, DateTime SentAtUtc)
{
    /// <summary>The inbound message in an event's data, or null when the data holds none.</summary>
    public static SlskdPrivateMessage? Parse(string data)
    {
        using JsonDocument doc = JsonDocument.Parse(data);
        if (!doc.RootElement.TryGetProperty("message", out JsonElement message) || message.ValueKind != JsonValueKind.Object)
            return null;

        string? username = message.TryGetProperty("username", out JsonElement u) ? u.GetString() : null;
        string? text = message.TryGetProperty("message", out JsonElement t) ? t.GetString() : null;
        bool inbound = !message.TryGetProperty("direction", out JsonElement d) || string.Equals(d.GetString(), "In", StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(text) || !inbound)
            return null;

        // GetDateTime keeps slskd's Z as UTC; DateTime.TryParse would shift it to the container's local time.
        DateTime sentAt = message.TryGetProperty("timestamp", out JsonElement ts) && ts.TryGetDateTime(out DateTime parsed)
            ? parsed.ToUniversalTime()
            : DateTime.MinValue;

        return new SlskdPrivateMessage(username, text, sentAt);
    }
}

/// <summary>Answers a peer's "type this word" download check, once per peer and slskd server every 12 hours.</summary>
public sealed partial class SlskdChallengeResponder(
    ISlskdApiClient apiClient,
    Logger logger,
    Func<DateTime>? utcNow = null,
    Func<TimeSpan, Task>? delay = null)
{
    public static readonly TimeSpan PeerCooldown = TimeSpan.FromHours(12);

    // The Soulseek server bans an account for 30 minutes when it floods private chat.
    public static readonly TimeSpan SendGap = TimeSpan.FromSeconds(2);

    // slskd's message timestamps are whole seconds, so a check sent within a second of the grab reads as earlier.
    private static readonly TimeSpan GrabClockSlack = TimeSpan.FromMinutes(1);

    private readonly Func<DateTime> _utcNow = utcNow ?? (() => DateTime.UtcNow);
    private readonly Func<TimeSpan, Task> _delay = delay ?? Task.Delay;
    private readonly Dictionary<string, DateTime> _answeredAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _claimLock = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private DateTime _lastSentAt = DateTime.MinValue;

    public async Task HandleAsync(SlskdEventRecord record, SlskdProviderSettings settings, IEnumerable<SlskdDownloadItem> items)
    {
        if (!settings.AnswerPeerChallenges || FindPendingCheck(record, items) is not { } check)
            return;

        if (!await TryAnswerAsync(settings, check))
            return;

        // NLog quotes string values itself: no quotes or possessives around the holes.
        logger.Info("Answered the download check from {Username} for {Release} with {Answer}", check.Username, check.Release, check.Answer);
        if (!await apiClient.AcknowledgeConversationAsync(settings, check.Username))
            logger.Debug("Could not mark the messages from {Username} read in slskd", check.Username);
    }

    private PendingCheck? FindPendingCheck(SlskdEventRecord record, IEnumerable<SlskdDownloadItem> items)
    {
        if (SlskdPrivateMessage.Parse(record.Data) is not { } message || FindAnswer(message.Text) is not { } answer)
            return null;

        if (items.FirstOrDefault(item => IsWaitingOn(item, message)) is { } grab)
            return new PendingCheck(message.Username, answer, grab.ReleaseInfo.Title);

        logger.Debug("Download check from {Username} left unanswered: no unfinished grab from them", message.Username);
        return null;
    }

    private async Task<bool> TryAnswerAsync(SlskdProviderSettings settings, PendingCheck check)
    {
        // Keyed by server too: two slskd accounts each need their own answer, while two clients on one server share it.
        string key = $"{settings.BaseUrl}|{check.Username}";
        if (!TryClaim(key))
        {
            logger.Debug("Download check from {Username} left unanswered: answered within the last {Hours} hours", check.Username, PeerCooldown.TotalHours);
            return false;
        }

        if (await TrySendAsync(settings, check.Username, check.Answer))
            return true;

        Release(key);
        return false;
    }

    private sealed record PendingCheck(string Username, string Answer, string? Release);

    /// <summary>The word or phrase a download check asks for, or null when the text is not one.</summary>
    public static string? FindAnswer(string text)
    {
        if (!DownloadContextRegex().IsMatch(text))
            return null;

        foreach (Regex form in new[] { QuotedTypeRegex(), ReplyWithWordRegex() })
        {
            Match match = form.Match(text);
            if (match.Success && match.Groups["answer"].Value is { Length: > 0 } answer)
                return answer;
        }

        return null;
    }

    private static bool IsWaitingOn(SlskdDownloadItem item, SlskdPrivateMessage message) =>
        string.Equals(item.Username, message.Username, StringComparison.OrdinalIgnoreCase) &&
        !item.Removed &&
        message.SentAtUtc >= item.TrackedSinceUtc - GrabClockSlack &&
        !item.AllAcceptedFilesCompleted();

    private bool TryClaim(string key)
    {
        lock (_claimLock)
        {
            DateTime now = _utcNow();
            if (_answeredAt.TryGetValue(key, out DateTime last) && now - last < PeerCooldown)
                return false;

            _answeredAt[key] = now;
            return true;
        }
    }

    private void Release(string key)
    {
        lock (_claimLock)
            _answeredAt.Remove(key);
    }

    // Never rethrows: the event poll would offer the event again, and a send that timed out may still have gone.
    private async Task<bool> TrySendAsync(SlskdProviderSettings settings, string username, string answer)
    {
        await _sendGate.WaitAsync();
        try
        {
            TimeSpan wait = _lastSentAt + SendGap - _utcNow();
            if (wait > TimeSpan.Zero)
                await _delay(wait);

            bool sent = await apiClient.SendPrivateMessageAsync(settings, username, answer);
            _lastSentAt = _utcNow();
            if (!sent)
                logger.Warn("slskd did not send the answer to the download check from {Username}", username);
            return sent;
        }
        catch (Exception ex)
        {
            logger.Warn(ex, "Could not answer the download check from {Username}; the next check they send gets another try", username);
            return false;
        }
        finally
        {
            _sendGate.Release();
        }
    }

    [GeneratedRegex(@"\b(?:download|human|whitelist|verif|unlock)", RegexOptions.IgnoreCase)]
    private static partial Regex DownloadContextRegex();

    // please type "the phrase" in this chat / type "the phrase" here
    [GeneratedRegex(@"\btype\s+[""“”'‘’]\s*(?<answer>[^""“”\r\n]{1,40}?)\s*[""“”'‘’]\s+(?:in\s+(?:this\s+|the\s+)?chat|here)\b", RegexOptions.IgnoreCase)]
    private static partial Regex QuotedTypeRegex();

    // reply to this message with the word: X / Reply only with this word / Antworte nur mit diesem Wort: X
    [GeneratedRegex(@"\breply\b[^:\r\n]{0,80}?\bwith\s+(?:the|this)\s+word\b[^:\r\n]{0,60}:\s*[""“'‘]?(?<answer>[\p{L}\p{N}_-]{1,40})", RegexOptions.IgnoreCase)]
    private static partial Regex ReplyWithWordRegex();
}
