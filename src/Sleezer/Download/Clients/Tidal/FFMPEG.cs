using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace NzbDrone.Core.Download.Clients.Tidal
{
    internal static class FFMPEG
    {
        // Configured ffmpeg/ffprobe binary directory (set by DownloadTaskQueue
        // from Lidarr's FFmpeg metadata-consumer settings on first post-process).
        // null = no override; bare PATH lookup is used.
        // Mirrors how the Deezer queue calls XabeFFmpeg.SetExecutablesPath.
        // Without this, Lidarr's container PATH (typically just /app/bin and
        // a couple of system dirs) often misses ffprobe/ffmpeg installed at
        // /usr/bin or /usr/local/bin.
        private static string? _binaryDirectory;

        public static void SetBinaryDirectory(string? directory)
        {
            _binaryDirectory = string.IsNullOrWhiteSpace(directory) ? null : directory;
        }

        private static string ResolveBinary(string name)
        {
            if (string.IsNullOrEmpty(_binaryDirectory))
                return name;

            string candidate = Path.Combine(_binaryDirectory, name);
            if (File.Exists(candidate))
                return candidate;

            // .exe suffix on Windows hosts where the configured directory may
            // contain ffmpeg.exe / ffprobe.exe rather than bare names.
            string winCandidate = Path.Combine(_binaryDirectory, name + ".exe");
            if (File.Exists(winCandidate))
                return winCandidate;

            // Fall back to PATH if neither is present at the configured path.
            return name;
        }

        public static string[] ProbeCodecs(string filePath)
        {
            var (exitCode, output, err, args) = Call(
                "ffprobe",
                $"-select_streams a -show_entries stream=codec_name:stream_tags=language -of default=nk=1:nw=1 {EncodeParameterArgument(filePath)}");

            if (exitCode != 0)
                throw new FFMPEGException($"Probing codecs failed\n{args}\n{err}");

            return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        public static void ConvertWithoutReencode(string input, string output)
        {
            // -y: overwrite output without prompting (fixes hang from #12 when leftover files exist)
            // -nostdin: never read stdin, prevents another stall variant
            // -err_detect explode + -xerror + stderr check: same pattern as the
            // corruption scanner. Without this, a partially-corrupt input passes
            // through demuxing silently and we end up with a "successful"
            // re-mux that's missing frames. See CorruptionScanner.cs for the
            // detailed rationale on why exit code alone is insufficient.
            var (exitCode, _, err, args) = Call(
                "ffmpeg",
                $"-v error -err_detect explode -xerror -y -nostdin -i {EncodeParameterArgument(input)} -vn -acodec copy {EncodeParameterArgument(output)}");
            if (exitCode != 0 || !string.IsNullOrWhiteSpace(err))
                throw new FFMPEGException($"Conversion without re-encode failed (exit={exitCode})\n{args}\n{err}");
        }

        public static void Reencode(string input, string output, int bitrate)
        {
            // Same hardening as ConvertWithoutReencode — see comment above.
            var (exitCode, _, err, args) = Call(
                "ffmpeg",
                $"-v error -err_detect explode -xerror -y -nostdin -i {EncodeParameterArgument(input)} -b:a {bitrate}k {EncodeParameterArgument(output)}");
            if (exitCode != 0 || !string.IsNullOrWhiteSpace(err))
                throw new FFMPEGException($"Re-encoding failed (exit={exitCode})\n{args}\n{err}");
        }

        private static (int exitCode, string output, string err, string args) Call(string executable, string arguments)
        {
            string resolved = ResolveBinary(executable);
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = resolved,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true
                }
            };

            try
            {
                proc.Start();
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 2)
            {
                // ENOENT — binary not on PATH and no override directory contained it.
                // Translate to FFMPEGException so the caller's normal "FFmpeg unavailable"
                // handler in DownloadItem.HandleAudioConversion takes the safe path
                // (skip conversion, leave the original M4A alone) instead of failing
                // the entire track download.
                throw new FFMPEGException($"{resolved} not found on PATH or in configured FFmpeg directory. " +
                    "Install ffmpeg/ffprobe in the Lidarr container, or set the FFmpeg path in Settings → Metadata → FFmpeg.");
            }
            // Close stdin immediately so any prompt the child wants to write
            // gets EOF instead of waiting forever for an answer.
            proc.StandardInput.Close();

            string output = proc.StandardOutput.ReadToEnd();
            string err = proc.StandardError.ReadToEnd();

            if (!proc.WaitForExit(60000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                throw new FFMPEGException($"{executable} did not exit within 60s; killed.\n{arguments}\n{err}");
            }

            return (proc.ExitCode, output, err, $"{executable} {arguments}");
        }

        private static string EncodeParameterArgument(string original)
        {
            if (string.IsNullOrEmpty(original))
                return "\"\"";

            string value = original.Replace("\"", "\\\"");
            return $"\"{value}\"";
        }
    }

    internal class FFMPEGException : Exception
    {
        public FFMPEGException(string message) : base(message) { }
    }
}
