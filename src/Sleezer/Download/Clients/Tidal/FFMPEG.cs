using System;
using System.Diagnostics;
using System.Text;

namespace NzbDrone.Core.Download.Clients.Tidal
{
    internal static class FFMPEG
    {
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
            var (exitCode, _, err, args) = Call(
                "ffmpeg",
                $"-y -nostdin -i {EncodeParameterArgument(input)} -vn -acodec copy {EncodeParameterArgument(output)}");
            if (exitCode != 0)
                throw new FFMPEGException($"Conversion without re-encode failed\n{args}\n{err}");
        }

        public static void Reencode(string input, string output, int bitrate)
        {
            var (exitCode, _, err, args) = Call(
                "ffmpeg",
                $"-y -nostdin -i {EncodeParameterArgument(input)} -b:a {bitrate}k {EncodeParameterArgument(output)}");
            if (exitCode != 0)
                throw new FFMPEGException($"Re-encoding failed\n{args}\n{err}");
        }

        private static (int exitCode, string output, string err, string args) Call(string executable, string arguments)
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true
                }
            };

            proc.Start();
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
