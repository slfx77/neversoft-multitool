using System.Text;
using NeversoftMultitool.Core.Formats.Video;

namespace NeversoftMultitool.Tests.Core.Formats.Video;

/// <summary>
///     Pins that a corrupt MDEC bitstream is REJECTED rather than turned into a
///     partial image, and pins where the decoder says the corruption is.
///     <para>
///         <see cref="MdecDecoder" /> funnels five distinct block failures into
///         one exception, so "it throws" is not enough — the macroblock, block
///         and bit position in the message are the whole diagnostic value, and
///         only they distinguish "the frame ran out" from "the frame lied".
///         Until now the sole test reaching that throw passed a 9-byte frame,
///         which trips the very first guard before a single VLC symbol is read
///         and asserts nothing about the message. The interesting paths —
///         unrecognized code, run-length overflow, truncated escape, and bits
///         exhausted with no end-of-block — had no coverage at all.
///     </para>
///     <para>
///         Synthetic by design. The corpus holds exactly one corrupt-frame
///         source (SM2 Final's <c>E5M6.STR</c>) and the demuxer now withholds
///         its bad frames before the decoder ever sees them, so real-file
///         coverage of this contract no longer exists and could not be relied
///         on if it did.
///     </para>
/// </summary>
public sealed class MdecCorruptionRejectionTests
{
    private const string EndOfBlock = "10";
    private const string Escape = "000001";

    /// <summary>
    ///     Builds a v2 frame: the 8-byte header the decoder validates, then
    ///     <paramref name="bits" /> written MSB-first into 16-bit little-endian
    ///     words, which is the order <see cref="MdecBitReader" /> reads.
    /// </summary>
    private static byte[] Frame(string bits, int payloadWords = 0)
    {
        var words = Math.Max(payloadWords, (bits.Length + 15) / 16);
        var frame = new byte[8 + words * 2];
        BitConverter.TryWriteBytes(frame.AsSpan(2, 2), (ushort)0x3800);   // magic
        BitConverter.TryWriteBytes(frame.AsSpan(4, 2), (ushort)1);        // qscale
        BitConverter.TryWriteBytes(frame.AsSpan(6, 2), (ushort)2);        // version

        for (var i = 0; i < bits.Length; i++)
        {
            if (bits[i] != '1')
                continue;
            var word = i / 16;
            var bitInWord = 15 - i % 16;
            var value = (ushort)(1 << bitInWord);
            var at = 8 + word * 2;
            frame[at] |= (byte)(value & 0xFF);
            frame[at + 1] |= (byte)(value >> 8);
        }

        return frame;
    }

    /// <summary>A 10-bit DC coefficient of zero — the block prelude every path shares.</summary>
    private static string Dc() => new('0', 10);

    private static string Bits(int value, int count)
    {
        var sb = new StringBuilder(count);
        for (var i = count - 1; i >= 0; i--)
            sb.Append((value >> i) & 1);
        return sb.ToString();
    }

    /// <summary>
    ///     Recovers a real run/level code of the requested length straight out
    ///     of the decoder's own table, so these tests do not hard-code a VLC
    ///     assignment that only the table is authoritative about.
    /// </summary>
    private static string? FindRunLevelCode(int bitLength)
    {
        var shift = MdecTables.VlcBits - bitLength;
        for (var prefix = 0; prefix < 1 << bitLength; prefix++)
        {
            var entry = MdecTables.VlcTable[prefix << shift];
            if (entry.BitLength == bitLength && !entry.IsEscape && !entry.IsEndOfBlock)
                return Bits(prefix, bitLength);
        }

        return null;
    }

    private static InvalidDataException Rejects(byte[] frame)
    {
        return Assert.Throws<InvalidDataException>(() => MdecDecoder.DecodeFrame(frame, 16, 16));
    }

    // ── Header ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public void DecodeFrame_HeaderTooShort_IsRejected(int length)
    {
        var error = Rejects(new byte[length]);

        Assert.Contains("header is truncated", error.Message);
    }

    [Fact]
    public void DecodeFrame_WrongMagic_IsRejected()
    {
        var frame = Frame(Dc());
        BitConverter.TryWriteBytes(frame.AsSpan(2, 2), (ushort)0x1234);

        var error = Rejects(frame);

        Assert.Contains("Invalid STR bitstream magic 0x1234", error.Message);
    }

    // ── Block bitstream ────────────────────────────────────────────────

    /// <summary>
    ///     Six zero bits is not the escape ("000001") and not any run/level
    ///     code, so the table entry has zero length. Plenty of payload follows,
    ///     which is what separates this from simple exhaustion: the frame has
    ///     bits left and they do not mean anything.
    /// </summary>
    [Fact]
    public void DecodeFrame_UnrecognizedVlcCode_IsRejectedAtTheReadingPosition()
    {
        var frame = Frame(Dc() + new string('0', 40), payloadWords: 8);

        var error = Rejects(frame);

        // 10 bits of DC consumed, and the decoder reports position + 64.
        Assert.Contains("macroblock (0, 0), block 0, bit 74", error.Message);
    }

    /// <summary>
    ///     An escape's 6-bit run addresses a coefficient past the block's 64,
    ///     which is corruption rather than a long legitimate skip.
    /// </summary>
    [Fact]
    public void DecodeFrame_RunLengthPastTheEndOfTheBlock_IsRejected()
    {
        // run 63 + 1 lands exactly on 64, the first out-of-range coefficient.
        var escapeCode = Bits(63, 6) + Bits(1, 10);
        var frame = Frame(Dc() + Escape + escapeCode, payloadWords: 8);

        var error = Rejects(frame);

        // 10 DC + 6 escape + 16 payload = 32 bits consumed.
        Assert.Contains("macroblock (0, 0), block 0, bit 96", error.Message);
    }

    /// <summary>
    ///     An escape promises 16 more bits. Ending the frame inside them is a
    ///     different failure from an unrecognized code and must not be silently
    ///     treated as an end of block.
    /// </summary>
    [Fact]
    public void DecodeFrame_EscapeWithoutItsPayload_IsRejected()
    {
        // 10 DC + 6 escape = 16 bits, so one word holds exactly the escape and
        // nothing of the 16-bit code it introduces.
        var frame = Frame(Dc() + Escape, payloadWords: 1);

        var error = Rejects(frame);

        Assert.Contains("macroblock (0, 0), block 0, bit 80", error.Message);
    }

    /// <summary>
    ///     A block that simply stops, with every bit consumed by valid codes and
    ///     no end-of-block marker, must be rejected — accepting it would emit a
    ///     half-decoded block as if it were complete.
    /// </summary>
    [Fact]
    public void DecodeFrame_BitsExhaustedWithoutEndOfBlock_IsRejected()
    {
        var code = FindRunLevelCode(6) ?? FindRunLevelCode(5);
        Assert.NotNull(code);

        // One 16-bit word: 10 bits of DC plus the code leaves under two bits,
        // so the block ends without ever seeing an end-of-block marker.
        var frame = Frame(Dc() + code, payloadWords: 1);

        var error = Rejects(frame);

        Assert.Contains("macroblock (0, 0), block 0", error.Message);
    }

    /// <summary>
    ///     The control: the same construction with an end-of-block marker on
    ///     every block decodes, so the rejections above are about the
    ///     corruption and not about the frames being synthetic.
    /// </summary>
    [Fact]
    public void DecodeFrame_BlocksThatEndProperly_Decode()
    {
        // 16x16 is one macroblock: 6 blocks, each 10 bits of DC then EOB.
        var block = Dc() + EndOfBlock;
        var frame = Frame(string.Concat(Enumerable.Repeat(block, 6)), payloadWords: 8);

        var rgb = MdecDecoder.DecodeFrame(frame, 16, 16);

        Assert.Equal(16 * 16 * 3, rgb.Length);
    }
}
