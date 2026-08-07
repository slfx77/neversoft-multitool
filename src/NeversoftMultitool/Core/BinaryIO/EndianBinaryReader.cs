using System.Buffers.Binary;
using System.Text;

namespace NeversoftMultitool.Core.BinaryIO;

/// <summary>
///     Sequential reader over a stream, parameterized on byte order — the
///     counterpart to <see cref="EndianSpanReader" /> for formats whose readers
///     walk forward rather than address fields by offset.
///     <para>
///         This exists for the Neversoft container grammars the N64 ports
///         re-encoded rather than redesigned. The Edge of Reality ports keep the
///         PS1 model shell and TRG layouts field for field and change only the
///         byte order, so one reader can serve both once the order is a
///         parameter instead of an assumption.
///     </para>
///     <para>
///         The alternative already in the tree — reversing every 4-byte word and
///         handing the result to the little-endian reader — is why
///         <c>PsxN64ShellFile</c> needed a mesh-index patch: a word reverse
///         restores u32 fields but EXCHANGES the two u16s packed inside a word,
///         so a u16 at +0x16 is read from +0x14. Reading the real byte order per
///         field has no such failure mode.
///     </para>
///     <para>
///         Deliberately a drop-in for <see cref="BinaryReader" /> over the member
///         set those readers use, including <see cref="BaseStream" />: adopting
///         it should change parameter types and nothing else, so an
///         endian-parameterized reader stays reviewable against its
///         single-endian original.
///     </para>
/// </summary>
internal sealed class EndianBinaryReader : IDisposable
{
    private readonly bool _ownsReader;
    private readonly BinaryReader _reader;

    /// <summary>Reads <paramref name="stream" />; the stream itself is left open.</summary>
    public EndianBinaryReader(Stream stream, bool bigEndian)
    {
        _reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        _ownsReader = true;
        BigEndian = bigEndian;
    }

    /// <summary>
    ///     Wraps an existing reader without taking ownership, so a caller that
    ///     already holds a <see cref="BinaryReader" /> (and its position) can
    ///     hand it to an endian-parameterized parse path unchanged.
    /// </summary>
    public EndianBinaryReader(BinaryReader reader, bool bigEndian)
    {
        _reader = reader;
        _ownsReader = false;
        BigEndian = bigEndian;
    }

    public bool BigEndian { get; }

    /// <summary>
    ///     The underlying stream, so <c>Position</c>, <c>Seek</c> and
    ///     <c>Length</c> read exactly as they do on <see cref="BinaryReader" />.
    /// </summary>
    public Stream BaseStream => _reader.BaseStream;

    public void Dispose()
    {
        if (_ownsReader)
            _reader.Dispose();
    }

    public byte ReadByte()
    {
        return _reader.ReadByte();
    }

    public byte[] ReadBytes(int count)
    {
        return _reader.ReadBytes(count);
    }

    public ushort ReadUInt16()
    {
        var value = _reader.ReadUInt16();
        return BigEndian ? BinaryPrimitives.ReverseEndianness(value) : value;
    }

    public short ReadInt16()
    {
        var value = _reader.ReadInt16();
        return BigEndian ? BinaryPrimitives.ReverseEndianness(value) : value;
    }

    public uint ReadUInt32()
    {
        var value = _reader.ReadUInt32();
        return BigEndian ? BinaryPrimitives.ReverseEndianness(value) : value;
    }

    public int ReadInt32()
    {
        var value = _reader.ReadInt32();
        return BigEndian ? BinaryPrimitives.ReverseEndianness(value) : value;
    }
}
