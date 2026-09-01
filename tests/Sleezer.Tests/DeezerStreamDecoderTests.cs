using System.Text;
using NzbDrone.Plugin.Sleezer.Core.Deezer;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;
using Xunit;

namespace Sleezer.Tests;

// Regression lock for the DeezNET stale-tail corruption (buffer written past bytesRead).
public class DeezerStreamDecoderTests
{
    private const int Chunk = DeezerStreamDecoder.ChunkSize;
    private const int Group = Chunk * 3;
    private const string TrackId = "2748723201";

    private static readonly string Key = DeezerStreamDecoder.GenerateBlowfishKey(TrackId);

    [Theory]
    // Vectors from DeezNET's own test table (GPL-3.0), which this decoder replaces.
    [InlineData("123123123", "55eog9)30}whn;5c")]
    [InlineData("401934282", "1d0<d;!gfwuej9ed")]
    [InlineData("2748723201", "fii;ih'61)r4l36c")]
    public void Key_derivation_matches_known_vectors(string trackId, string expected)
    {
        Assert.Equal(expected, DeezerStreamDecoder.GenerateBlowfishKey(trackId));
    }

    [Theory]
    [InlineData(4 * Group)]        // control: the only shape DeezNET's decoder handled
    [InlineData(3 * Group + 1568)] // tail chunk shorter than 2048
    [InlineData(3 * Group + 3000)] // tail chunk of 2048 plus a partial second
    [InlineData(Group - 1)]        // single incomplete group
    [InlineData(Chunk)]            // exactly one encrypted chunk
    [InlineData(1000)]             // shorter than one chunk: stored plain, passes through
    public async Task Encrypted_stream_decodes_byte_identical(int length)
    {
        var plain = MakePayload(length);

        using var input = new MemoryStream(StripeEncrypt(plain));
        using var output = new MemoryStream();
        await DeezerStreamDecoder.DecodeAsync(input, output, isEncrypted: true, Key);

        Assert.Equal(plain, output.ToArray());
    }

    [Fact]
    public async Task Short_network_reads_decode_byte_identical()
    {
        var plain = MakePayload(3 * Group + 1568);

        using var input = new CappedReadStream(StripeEncrypt(plain), maxBytesPerRead: 1400);
        using var output = new MemoryStream();
        await DeezerStreamDecoder.DecodeAsync(input, output, isEncrypted: true, Key);

        Assert.Equal(plain, output.ToArray());
    }

    [Fact]
    public async Task Unencrypted_stream_passes_through_unchanged()
    {
        var plain = MakePayload(2 * Group + 777);

        using var input = new MemoryStream(plain);
        using var output = new MemoryStream();
        await DeezerStreamDecoder.DecodeAsync(input, output, isEncrypted: false, Key);

        Assert.Equal(plain, output.ToArray());
    }

    [Fact]
    public async Task Leading_zero_padding_is_stripped_from_first_chunk()
    {
        var plain = MakePayload(Group + 100);
        plain[0] = 0;
        plain[1] = 0;
        plain[2] = 0;
        plain[3] = (byte)'x';
        plain[4] = (byte)'y';

        using var input = new MemoryStream(StripeEncrypt(plain));
        using var output = new MemoryStream();
        await DeezerStreamDecoder.DecodeAsync(input, output, isEncrypted: true, Key);

        Assert.Equal(plain[3..], output.ToArray());
    }

    [Fact]
    public async Task Mp4_ftyp_header_is_not_stripped()
    {
        var plain = MakePayload(Group + 100);
        plain[0] = 0;
        plain[1] = 0;
        plain[2] = 0;
        plain[3] = 0x20;
        plain[4] = (byte)'f';
        plain[5] = (byte)'t';
        plain[6] = (byte)'y';
        plain[7] = (byte)'p';

        using var input = new MemoryStream(StripeEncrypt(plain));
        using var output = new MemoryStream();
        await DeezerStreamDecoder.DecodeAsync(input, output, isEncrypted: true, Key);

        Assert.Equal(plain, output.ToArray());
    }

    [Fact]
    public async Task Empty_stream_produces_empty_output()
    {
        using var input = new MemoryStream();
        using var output = new MemoryStream();
        await DeezerStreamDecoder.DecodeAsync(input, output, isEncrypted: true, Key);

        Assert.Empty(output.ToArray());
    }

    private static byte[] MakePayload(int length)
    {
        var payload = new byte[length];
        new Random(42 + length).NextBytes(payload);
        // A non-zero first byte keeps the padding strip out of the byte-identity cases.
        if (length >= 4)
        {
            payload[0] = (byte)'f';
            payload[1] = (byte)'L';
            payload[2] = (byte)'a';
            payload[3] = (byte)'C';
        }
        return payload;
    }

    // BF_CBC_STRIPE: every 3rd 2048-byte chunk encrypted; trailing partial chunk in the clear.
    private static byte[] StripeEncrypt(byte[] plain)
    {
        using var encrypted = new MemoryStream();
        for (int offset = 0, index = 0; offset < plain.Length; offset += Chunk, index++)
        {
            var size = Math.Min(Chunk, plain.Length - offset);
            var chunk = plain[offset..(offset + size)];
            if (index % 3 == 0 && size == Chunk)
                chunk = EncryptChunk(chunk);
            encrypted.Write(chunk);
        }
        return encrypted.ToArray();
    }

    private static byte[] EncryptChunk(byte[] data)
    {
        var cipher = new BufferedBlockCipher(new CbcBlockCipher(new BlowfishEngine()));
        cipher.Init(true, new ParametersWithIV(new KeyParameter(Encoding.UTF8.GetBytes(Key)), new byte[] { 0, 1, 2, 3, 4, 5, 6, 7 }));

        var output = new byte[cipher.GetOutputSize(data.Length)];
        var written = cipher.ProcessBytes(data, 0, data.Length, output, 0);
        written += cipher.DoFinal(output, written);
        if (written != output.Length)
            Array.Resize(ref output, written);
        return output;
    }

    // Returns at most maxBytesPerRead per call, the way a live HTTP response stream does.
    private sealed class CappedReadStream(byte[] data, int maxBytesPerRead) : Stream
    {
        private readonly MemoryStream _inner = new(data);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) =>
            _inner.Read(buffer, offset, Math.Min(count, maxBytesPerRead));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
