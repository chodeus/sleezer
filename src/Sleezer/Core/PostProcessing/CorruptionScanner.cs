using NLog;
using System.Diagnostics;
using NzbDrone.Common.Instrumentation;
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
    private static int _ffmpegVersionLogged;

    public CorruptionScanner(Logger logger)
    {
        _logger = logger;
    }

    public record Result(bool IsCorrupt, string? Reason);

    public async Task<Result> ScanAsync(string path, int timeoutSeconds, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
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
                _logger.Error(ex, "Corruption scan: file not accessible at {Path}", path);
                return new Result(true, $"File not accessible: {ex.Message}");
            }

            if (size < MinFileBytes)
            {
                _logger.Debug("Corruption scan {Path}: corrupt \u2014 too small ({Size} bytes)", path, size);
                return new Result(true, $"File too small ({size} bytes)");
            }
            _logger.Trace("Corruption scan {Path}: size ok ({Size} bytes)", path, size);

            // Tier 2: TagLib parse
            try
            {
                using TagLib.File file = TagLib.File.Create(path);
                _ = file.Properties?.Duration;
                _logger.Trace("Corruption scan {Path}: TagLib parsed", path);
            }
            catch (TagLib.CorruptFileException ex)
            {
                _logger.Debug(ex, "Corruption scan {Path}: TagLib reports corrupt", path);
                return new Result(true, $"TagLib corrupt: {ex.Message}");
            }
            catch (TagLib.UnsupportedFormatException)
            {
                // Unsupported by TagLib is not the same as corrupt - fall through.
                _logger.Trace("Corruption scan {Path}: TagLib unsupported format, falling through to ffmpeg", path);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Corruption scan {Path}: TagLib threw unexpected error \u2014 treating as corrupt", path);
                return new Result(true, $"TagLib error: {ex.Message}");
            }

            // Tier 3: ffmpeg decode
            if (!AudioMetadataHandler.CheckFFmpegInstalled())
            {
                // Log Error per-file rather than one-time Warn: silently passing a
                // file as "scanned: clean" when the only verifier we have is missing
                // is exactly the failure mode that lets corruption through to the
                // library.
                _logger.Error("Corruption scan {Path}: ffmpeg not found at configured path or on PATH \u2014 cannot verify decode. Configure FFmpeg Path under Settings \u2192 Metadata \u2192 FFmpeg and hit Test to auto-install. Passing file with size+TagLib only.", path);
                return new Result(false, "ffmpeg unavailable; size+TagLib only");
            }

            await LogFfmpegVersionOnceAsync(ct);

            (int exitCode, string stderr) = await RunFfmpegDecodeAsync(path, timeoutSeconds, ct);
            if (exitCode == -1)
            {
                _logger.Debug("Corruption scan {Path}: ffmpeg decode timed out after {Timeout}s", path, timeoutSeconds);
                return new Result(true, $"Decode timed out (>{timeoutSeconds}s)");
            }

            if (exitCode != 0)
            {
                string reason = FfmpegErrorFormatter.CleanFfmpegErrors(stderr);
                _logger.Debug("Corruption scan {Path}: ffmpeg verdict corrupt (exit={ExitCode}) \u2014 {Reason}", path, exitCode, reason);
                return new Result(true, reason);
            }

            // Defence in depth: ffmpeg sometimes exits 0 even with `-xerror -err_detect explode`
            // when the decoder skips a bad frame in a way that doesn't propagate to the
            // transcode loop. With `-v error` any output on stderr is by definition an
            // error-level message \u2014 treat that as corruption regardless of exit code.
            if (!string.IsNullOrWhiteSpace(stderr))
            {
                string reason = FfmpegErrorFormatter.CleanFfmpegErrors(stderr);
                _logger.Debug("Corruption scan {Path}: ffmpeg exited 0 but emitted error-level output \u2014 {Reason}", path, reason);
                return new Result(true, reason);
            }

            _logger.Debug("Corruption scan {Path}: ok in {ElapsedMs}ms", path, sw.ElapsedMilliseconds);
            return new Result(false, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Fail-corrupt rather than fail-clean: if the scanner itself crashes,
            // we have NO confidence the file is good. Letting it through silently
            // is exactly the failure mode that lets damaged files into the library.
            // Force a re-search instead.
            _logger.Error(ex, "Unexpected error scanning {Path} — treating as corrupt to force re-search", path);
            return new Result(true, $"scanner crashed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Resolve the ffmpeg binary the scanner will invoke. Public so the post-import
    /// converter can reuse the exact same resolution + decode logic to verify its
    /// own outputs (see <see cref="AudioMetadataHandler.TryConvertToFormatAsync"/>).
    /// </summary>
    public static string ResolveFfmpegPath()
    {
        string ffmpegPath = Path.Combine(FFmpeg.ExecutablesPath ?? string.Empty, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
        if (!File.Exists(ffmpegPath))
            ffmpegPath = "ffmpeg";
        return ffmpegPath;
    }

    /// <summary>
    /// Run the same decode-only ffmpeg pass the scanner uses on an arbitrary path.
    /// Returns (-1, "") on timeout, otherwise (exit code, stderr).
    /// </summary>
    public static Task<(int exitCode, string stderr)> DecodeCheckAsync(string path, int timeoutSeconds, CancellationToken ct)
        => RunFfmpegDecodeAsync(path, timeoutSeconds, ct);

    private async Task LogFfmpegVersionOnceAsync(CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _ffmpegVersionLogged, 1, 0) != 0)
            return;

        try
        {
            string ffmpegPath = ResolveFfmpegPath();
            ProcessStartInfo psi = new()
            {
                FileName = ffmpegPath,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-version");

            using Process proc = new() { StartInfo = psi };
            proc.Start();
            string stdout = await proc.StandardOutput.ReadToEndAsync(ct);
            _ = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);

            // First line is "ffmpeg version <ver> Copyright ..."
            string firstLine = (stdout ?? string.Empty).Split('\n', 2)[0].Trim();
            if (string.IsNullOrEmpty(firstLine))
                firstLine = "(version probe returned empty output)";

            _logger.Info("Corruption scan: using {FfmpegPath} \u2014 {Version}. Old ffmpeg builds (4.x) silently accept malformed MP3 framing that newer builds (5.x+) flag; if scans pass but external tools report corrupt, upgrade ffmpeg.", ffmpegPath, firstLine);
        }
        catch (OperationCanceledException)
        {
            // Reset flag so a future scan retries the version probe.
            Interlocked.Exchange(ref _ffmpegVersionLogged, 0);
            throw;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Corruption scan: failed to probe ffmpeg version");
        }
    }

    private static async Task<(int exitCode, string stderr)> RunFfmpegDecodeAsync(string path, int timeoutSeconds, CancellationToken ct)
    {
        string ffmpegPath = ResolveFfmpegPath();

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
        // `-xerror` alone is NOT sufficient to fail on decode errors. Modern
        // ffmpeg only honours `-xerror` for muxing/transcoding-level errors;
        // decoder errors (invalid residual, decode_frame() failed, Header
        // missing, etc.) print at error level but the decoder recovers and
        // ffmpeg exits 0. `-err_detect explode` sets AV_EF_EXPLODE so the
        // decoder aborts instead of silently skipping bad frames — combined
        // with `-xerror`, that finally turns "Invalid data found" into a
        // non-zero exit. Without this, a clean scan is meaningless.
        psi.ArgumentList.Add("-err_detect");
        psi.ArgumentList.Add("explode");
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
            try { proc.Kill(entireProcessTree: true); }
            catch (Exception killEx)
            {
                NzbDroneLogger.GetLogger(typeof(CorruptionScanner))
                    .Trace(killEx, "Could not kill ffmpeg child after timeout — likely already exited");
            }
            return (-1, string.Empty);
        }

        string stderr = await stderrTask;
        _ = await stdoutTask;
        return (proc.ExitCode, stderr);
    }

}
