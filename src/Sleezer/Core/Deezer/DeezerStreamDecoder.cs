using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;

namespace NzbDrone.Plugin.Sleezer.Core.Deezer
{
    /// <summary>Decodes Deezer BF_CBC_STRIPE streams: every 3rd 2048-byte chunk is Blowfish-CBC encrypted.</summary>
    public static class DeezerStreamDecoder
    {
        public const int ChunkSize = 2048;
        private const int StripeInterval = 3;
        private const string KeySecret = "g4el58wc0zvf9na1";
        private static readonly byte[] BlowfishIv = { 0, 1, 2, 3, 4, 5, 6, 7 };

        // Replaces DeezNET's DecodeTrackStream, which appended stale buffer bytes at EOF.
        public static async Task DecodeAsync(Stream input, Stream output, bool isEncrypted, string blowfishKey, CancellationToken token = default)
        {
            var chunk = new byte[ChunkSize];
            for (long index = 0; ; index++)
            {
                var filled = await input.ReadAtLeastAsync(chunk.AsMemory(0, ChunkSize), ChunkSize, throwOnEndOfStream: false, token);
                if (filled == 0)
                    break;

                var payload = chunk;
                if (isEncrypted && index % StripeInterval == 0 && filled == ChunkSize)
                    payload = DecryptChunk(blowfishKey, chunk);

                // Zero-padding strip on the first chunk — parity with deemix's streamTrack.
                var start = index == 0 ? LeadingPaddingLength(payload, filled) : 0;
                await output.WriteAsync(payload.AsMemory(start, filled - start), token);

                if (filled < ChunkSize)
                    break;
            }
        }

        public static string GenerateBlowfishKey(string trackId)
        {
            var md5 = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(trackId))).ToLowerInvariant();
            var key = new char[16];
            for (var i = 0; i < 16; i++)
                key[i] = (char)(md5[i] ^ md5[i + 16] ^ KeySecret[i]);
            return new string(key);
        }

        private static int LeadingPaddingLength(byte[] payload, int filled)
        {
            if (filled == 0 || payload[0] != 0)
                return 0;

            // MP4-family streams legitimately start 00 00 00 xx 'ftyp'.
            if (filled >= 8 && payload[4] == (byte)'f' && payload[5] == (byte)'t' && payload[6] == (byte)'y' && payload[7] == (byte)'p')
                return 0;

            var i = 0;
            while (i < filled && payload[i] == 0)
                i++;
            return i;
        }

        private static byte[] DecryptChunk(string key, byte[] data)
        {
            var cipher = new BufferedBlockCipher(new CbcBlockCipher(new BlowfishEngine()));
            cipher.Init(false, new ParametersWithIV(new KeyParameter(Encoding.UTF8.GetBytes(key)), BlowfishIv));

            var output = new byte[cipher.GetOutputSize(data.Length)];
            var written = cipher.ProcessBytes(data, 0, data.Length, output, 0);
            written += cipher.DoFinal(output, written);
            if (written != output.Length)
                Array.Resize(ref output, written);
            return output;
        }
    }
}
