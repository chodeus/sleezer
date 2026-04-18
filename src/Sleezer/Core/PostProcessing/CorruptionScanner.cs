using NLog;
using System.Diagnostics;
using NzbDrone.Plugin.Sleezer.Core.Model;
using Xabe.FFmpeg;

namespace NzbDrone.Plugin.Sleezer.Core.PostProcessing;

public interface ICorruptionScanner
{
    Task<CorruptionScanner.Result> ScanAsync(string path, int timeoutSeconds, CancellationToken ct);
}

public class CorruptionScanner : ICorruptionScanner
{
    private const long MinFileBytes = 1024;

    private readonly Logger _logger;

    public CorruptionScanner(Logger logger)
    {
        _logger = logger;
    }

    public record Result(bool IsCorrupt, string? Reason);

    public async Task<Result> ScanAsync(string path, int timeoutSeconds, CancellationToken ct)
    {
        try
        {
            // Tier 1: size check
            long size;
            try
            {
                size = new FileInfo(path).Length;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Corruption scan: file not accessible at {path}");
                return new Result(true, $"File not accessible: {ex.Message}");
            }

            if (size < MinFileBytes)
            {
                return new Result(true, $"File too small ({size} bytes)");
            }

            // Tier 2: TagLib parse
            try
            {
                using TagLib.File file = TagLib.File.Create(path);
                _ = file.Properties?.Duration;
            }
            catch (TagLib.CorruptFileException ex)
            {
                return new Result(true, $"TagLib corrupt: {ex.Message}");
            }
            catch (TagLib.UnsupportedFormatException)
            {
                // Unsupported by TagLib is not the same as corrupt - fall through.
            }
            catch (Exception ex)
            {
                return new Result(true, $"TagLib error: {ex.Message}");
            }

            // Tier 3: ffmpeg decode
            if (!AudioMetadataHandler.CheckFFmpegInstalled())
            {
                _logger.Warn("Corruption check: ffmpeg not found. Configure FFmpeg Path under Settings \u2192 Metadata \u2192 FFmpeg and hit Test to auto-install. Accepting file based on size+TagLib only until then.");
                return new Result(false, null);
            }

            (int exitCode, string stderr) = await RunFfmpegDecodeAsync(path, timeoutSeconds, ct);
            if (exitCode == -1)
            {
                return new Result(true, $"Decode timed out (>{timeoutSeconds}s)");
            }

            if (exitCode != 0)
            {
                string reason = FfmpegErrorFormatter.CleanFfmpegErrors(stderr);
                return new Result(true, reason);
            }

            return new Result(false, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, $"Unexpected error scanning {path}");
            return new Result(false, null);
        }
    }

    private static async Task<(int exitCode, string stderr)> RunFfmpegDecodeAsync(string path, int timeoutSeconds, CancellationToken ct)
    {
        string ffmpegPath = Path.Combine(FFmpeg.ExecutablesPath ?? string.Empty, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
        if (!File.Exists(ffmpegPath))
            ffmpegPath = "ffmpeg";

        ProcessStartInfo psi = new()
        {
            FileName = ffmpegPath,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-xerror");
        psi.ArgumentList.Add("-nostdin");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(path);
        psi.ArgumentList.Add("-map");
        psi.ArgumentList.Add("0:a");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("null");
        psi.ArgumentList.Add("-");

        using Process proc = new() { StartInfo = psi };
        proc.Start();

        Task<string> stderrTask = proc.StandardError.ReadToEndAsync(ct);
        Task<string> stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);

        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            return (-1, string.Empty);
        }

        string stderr = await stderrTask;
        _ = await stdoutTask;
        return (proc.ExitCode, stderr);
    }

}
