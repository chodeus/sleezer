using System.Diagnostics;
using Xunit;

namespace Sleezer.Tests;

/// <summary>
/// Contract tests for the corruption scanner's ffmpeg invocation. These don't
/// instantiate <c>CorruptionScanner</c> (which would drag in NLog + Lidarr core
/// + Xabe.FFmpeg) — instead they run the EXACT command-line args the scanner
/// uses against fixture files and assert ffmpeg's behaviour matches what the
/// scanner expects.
///
/// The original bug we shipped: scanner used <c>-v error -xerror</c> alone,
/// which silently passed files with decoder errors because <c>-xerror</c> only
/// trips on muxing errors. The fix added <c>-err_detect explode</c> and a
/// stderr-non-empty check. These tests guard against regressions in either.
///
/// Tests skip themselves when ffmpeg isn't on PATH so a developer's bare-metal
/// box without ffmpeg installed doesn't break <c>dotnet test</c>.
/// </summary>
public class CorruptionScannerContractTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "sleezer-scanner-tests-" + Guid.NewGuid().ToString("N"));

    public CorruptionScannerContractTests()
    {
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Cleanup failed: " + ex.Message); }
    }

    [Fact]
    public void Clean_audio_file_passes_with_empty_stderr_and_zero_exit()
    {
        if (!IsFfmpegAvailable())
        {
            // No clean way to skip without Xunit.SkippableFact; just early-return.
            // Test name still appears as "passed" but the actual contract isn't
            // verified on this dev box. CI containers always have ffmpeg.
            return;
        }

        string clean = Path.Combine(_tmp, "clean.flac");
        GenerateCleanTone(clean);

        (int exit, string stderr) = RunScannerCommand(clean);

        Assert.Equal(0, exit);
        Assert.True(string.IsNullOrWhiteSpace(stderr),
            $"Clean file produced unexpected stderr — would cause false positive in defense-in-depth check.\nstderr:\n{stderr}");
    }

    [Fact]
    public void Truncated_audio_file_is_detected_as_corrupt()
    {
        if (!IsFfmpegAvailable())
        {
            // No clean way to skip without Xunit.SkippableFact; just early-return.
            // Test name still appears as "passed" but the actual contract isn't
            // verified on this dev box. CI containers always have ffmpeg.
            return;
        }

        string clean = Path.Combine(_tmp, "clean.flac");
        string truncated = Path.Combine(_tmp, "truncated.flac");
        GenerateCleanTone(clean);
        TruncateMidStream(clean, truncated);

        (int exit, string stderr) = RunScannerCommand(truncated);

        // Either non-zero exit OR non-empty stderr signals corruption to the
        // scanner. A truncated FLAC typically produces both; the test only
        // requires one to assert the contract.
        bool detected = exit != 0 || !string.IsNullOrWhiteSpace(stderr);
        Assert.True(detected,
            $"Truncated file passed both checks — scanner would silently accept corruption.\nexit={exit}\nstderr:\n{stderr}");
    }

    [Fact]
    public void Random_bytes_with_audio_extension_is_detected_as_corrupt()
    {
        if (!IsFfmpegAvailable())
        {
            // No clean way to skip without Xunit.SkippableFact; just early-return.
            // Test name still appears as "passed" but the actual contract isn't
            // verified on this dev box. CI containers always have ffmpeg.
            return;
        }

        string junk = Path.Combine(_tmp, "junk.mp3");
        File.WriteAllBytes(junk, GenerateRandomBytes(64 * 1024));

        (int exit, string stderr) = RunScannerCommand(junk);

        bool detected = exit != 0 || !string.IsNullOrWhiteSpace(stderr);
        Assert.True(detected,
            $"Random bytes passed scanner — input format detection should have failed.\nexit={exit}\nstderr:\n{stderr}");
    }

    /// <summary>
    /// Mirrors the args built in CorruptionScanner.RunFfmpegDecodeAsync. If you
    /// edit the scanner's flag list, edit this list too.
    /// </summary>
    private static (int exit, string stderr) RunScannerCommand(string path)
    {
        ProcessStartInfo psi = new()
        {
            FileName = "ffmpeg",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("error");
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
        string stderr = proc.StandardError.ReadToEnd();
        _ = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(60_000);
        return (proc.ExitCode, stderr);
    }

    private static void GenerateCleanTone(string outPath)
    {
        // 0.5 s of 440 Hz sine, FLAC. Short to keep the test fast; long enough
        // that ffmpeg writes a valid FLAC stream block.
        ProcessStartInfo psi = new()
        {
            FileName = "ffmpeg",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-v"); psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("lavfi");
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add("sine=frequency=440:duration=0.5");
        psi.ArgumentList.Add("-c:a"); psi.ArgumentList.Add("flac");
        psi.ArgumentList.Add(outPath);

        using Process proc = new() { StartInfo = psi };
        proc.Start();
        string err = proc.StandardError.ReadToEnd();
        proc.WaitForExit(15_000);
        if (proc.ExitCode != 0 || !File.Exists(outPath))
            throw new InvalidOperationException($"ffmpeg fixture generation failed: exit={proc.ExitCode}\n{err}");
    }

    private static void TruncateMidStream(string source, string dest)
    {
        // Cut to the first 70% of bytes so the FLAC header survives but the
        // decoder hits an unexpected EOF mid-stream. That's the failure mode
        // we want the scanner to detect.
        byte[] bytes = File.ReadAllBytes(source);
        int truncateTo = (int)(bytes.Length * 0.7);
        File.WriteAllBytes(dest, bytes.AsSpan(0, truncateTo).ToArray());
    }

    private static byte[] GenerateRandomBytes(int count)
    {
        byte[] buf = new byte[count];
        new Random(42).NextBytes(buf);
        return buf;
    }

    private static bool? _ffmpegCached;
    private static bool IsFfmpegAvailable()
    {
        if (_ffmpegCached.HasValue) return _ffmpegCached.Value;
        try
        {
            ProcessStartInfo psi = new()
            {
                FileName = "ffmpeg",
                Arguments = "-version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using Process proc = new() { StartInfo = psi };
            proc.Start();
            _ = proc.StandardOutput.ReadToEnd();
            _ = proc.StandardError.ReadToEnd();
            proc.WaitForExit(5_000);
            _ffmpegCached = proc.ExitCode == 0;
        }
        catch (Exception)
        {
            _ffmpegCached = false;
        }
        return _ffmpegCached.Value;
    }
}
