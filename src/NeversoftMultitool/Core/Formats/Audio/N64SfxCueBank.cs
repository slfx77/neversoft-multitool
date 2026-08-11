using System.Buffers.Binary;
using System.Security.Cryptography;

namespace NeversoftMultitool.Core.Formats.Audio;

/// <summary>
///     Strict, inspection-only view of one Neversoft N64 <c>.sfx.n64</c> cue
///     table. This is not a Nintendo Sound Tools BFX/PTR relationship: the
///     records are preserved independently and no target, rate, pitch, loop
///     schedule, or playback behavior is inferred.
/// </summary>
public sealed record N64SfxCueBank(
    int SerializedSize,
    string SerializedSha256,
    int TerminatorOffset,
    IReadOnlyList<byte> TerminatorRaw,
    IReadOnlyList<N64SfxCueRecord> Records)
{
    public const int RecordSize = 16;
    public const int TerminatorSize = 4;
    public const uint TerminatorValue = uint.MaxValue;

    /// <summary>
    ///     Parses the complete serialized table. The N64 grammar is zero or
    ///     more 16-byte big-endian records followed immediately by the
    ///     four-byte <c>FFFFFFFF</c> terminator. Bytes +12..+15 of every record
    ///     are required zero padding.
    /// </summary>
    public static N64SfxCueBank Parse(ReadOnlySpan<byte> data)
    {
        Require(data.Length >= TerminatorSize,
            "N64 SFX cue table is truncated before its terminator");
        Require((data.Length - TerminatorSize) % RecordSize == 0,
            "N64 SFX cue table size is not records plus a four-byte terminator");

        var terminatorOffset = data.Length - TerminatorSize;
        Require(BinaryPrimitives.ReadUInt32BigEndian(data[terminatorOffset..]) == TerminatorValue,
            "N64 SFX cue table does not end in FFFFFFFF");

        var recordCount = terminatorOffset / RecordSize;
        var records = new N64SfxCueRecord[recordCount];
        for (var index = 0; index < records.Length; index++)
        {
            var offset = index * RecordSize;
            var raw = data.Slice(offset, RecordSize);
            for (var padOffset = 12; padOffset < RecordSize; padOffset++)
            {
                Require(raw[padOffset] == 0,
                    $"N64 SFX cue record {index} padding is nonzero");
            }

            records[index] = new N64SfxCueRecord(
                index,
                offset,
                raw[0],
                raw[1],
                raw[2],
                raw[3],
                BinaryPrimitives.ReadUInt16BigEndian(raw[4..]),
                BinaryPrimitives.ReadUInt16BigEndian(raw[6..]),
                BinaryPrimitives.ReadUInt32BigEndian(raw[8..]),
                raw[12..16].ToArray(),
                raw.ToArray());
        }

        return new N64SfxCueBank(
            data.Length,
            Convert.ToHexString(SHA256.HashData(data)),
            terminatorOffset,
            data[terminatorOffset..].ToArray(),
            records);
    }

    /// <summary>Applies the complete strict predicate without throwing.</summary>
    public static bool TryParse(ReadOnlySpan<byte> data, out N64SfxCueBank? bank)
    {
        try
        {
            bank = Parse(data);
            return true;
        }
        catch (InvalidDataException)
        {
            bank = null;
            return false;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidDataException(message);
    }
}

/// <summary>
///     One raw 16-byte big-endian N64 cue record. Field names deliberately end
///     in <c>Raw</c>: parsing does not authorize applying pitch, resolving the
///     alias, scheduling a loop, or executing playback.
/// </summary>
public sealed record N64SfxCueRecord(
    int Index,
    int Offset,
    byte LoopFlagRaw,
    byte ProgramRaw,
    byte CategoryRaw,
    byte NoteRaw,
    ushort PitchRaw,
    ushort VolumeRaw,
    uint AliasRaw,
    IReadOnlyList<byte> PadRaw,
    IReadOnlyList<byte> RecordRaw);
