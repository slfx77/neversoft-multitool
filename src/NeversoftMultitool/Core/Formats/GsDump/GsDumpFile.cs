using System.Buffers.Binary;

namespace NeversoftMultitool.Core.Formats.GsDump;

internal enum GsDumpPacketKind
{
    Transfer,
    VSync,
    ReadFifo2,
    Registers
}

internal enum GsTransferPath
{
    Path1Old = 0,
    Path2 = 1,
    Path3 = 2,
    Path1New = 3,
    Dummy = 4
}

internal sealed record GsDumpPacket(
    GsDumpPacketKind Kind,
    GsTransferPath? Path,
    byte[] Data);

internal sealed class GsDumpFile
{
    private const int GsLocalMemorySize = 4 * 1024 * 1024;
    private const int StateVersion9LocalMemoryOffset = 425;

    public uint Crc { get; private init; }
    public int StateVersion { get; private init; }
    public string Serial { get; private init; } = "";
    public int ScreenshotWidth { get; private init; }
    public int ScreenshotHeight { get; private init; }
    public int ScreenshotOffset { get; private init; }
    public int ScreenshotSize { get; private init; }
    public byte[] ScreenshotPixels { get; private init; } = [];
    public byte[] State { get; private init; } = [];
    public byte[] Registers { get; private init; } = [];
    public List<GsDumpPacket> Packets { get; private init; } = [];

    public static GsDumpFile ParseFile(string path)
    {
        var name = Path.GetFileName(path);
        if (name.EndsWith(".xz", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".zst", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                "Compressed GS dumps are not supported by the C# gsdump command yet. Decompress to raw .gs first.");
        }

        return Parse(File.ReadAllBytes(path));
    }

    public static GsDumpFile Parse(ReadOnlySpan<byte> raw)
    {
        var offset = 0;
        static uint U32(ReadOnlySpan<byte> data, ref int offset)
        {
            Require(data, offset, 4);
            var value = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
            offset += 4;
            return value;
        }

        static byte[] Bytes(ReadOnlySpan<byte> data, ref int offset, int count)
        {
            Require(data, offset, count);
            var value = data.Slice(offset, count).ToArray();
            offset += count;
            return value;
        }

        var crcOrSentinel = U32(raw, ref offset);
        var headerBlockSize = checked((int)U32(raw, ref offset));
        var headerBlock = Bytes(raw, ref offset, headerBlockSize);

        uint crc;
        var stateVersion = 0;
        var serial = "";
        var screenshotWidth = 0;
        var screenshotHeight = 0;
        var screenshotOffset = 0;
        var screenshotSize = 0;
        byte[] screenshotPixels = [];
        byte[] state;

        if (crcOrSentinel == 0xFFFFFFFF)
        {
            if (headerBlock.Length < 36)
                throw new InvalidDataException("GS dump header block is shorter than GSDumpHeader.");

            var header = headerBlock.AsSpan();
            stateVersion = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(header[0..]));
            var stateSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(header[4..]));
            var serialOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(header[8..]));
            var serialSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(header[12..]));
            crc = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
            screenshotWidth = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(header[20..]));
            screenshotHeight = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(header[24..]));
            screenshotOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(header[28..]));
            screenshotSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(header[32..]));

            if (serialSize > 0 &&
                serialOffset >= 0 &&
                serialOffset <= headerBlock.Length &&
                serialSize <= headerBlock.Length - serialOffset)
            {
                serial = System.Text.Encoding.ASCII.GetString(headerBlock, serialOffset, serialSize)
                    .TrimEnd('\0');
            }

            if (screenshotWidth > 0 &&
                screenshotHeight > 0 &&
                screenshotSize == screenshotWidth * screenshotHeight * 4 &&
                screenshotOffset >= 0 &&
                screenshotOffset <= headerBlock.Length &&
                screenshotSize <= headerBlock.Length - screenshotOffset)
            {
                screenshotPixels = headerBlock.AsSpan(screenshotOffset, screenshotSize).ToArray();
            }

            state = Bytes(raw, ref offset, stateSize);
        }
        else
        {
            crc = crcOrSentinel;
            state = headerBlock;
        }

        var registers = Bytes(raw, ref offset, 8192);
        var packets = new List<GsDumpPacket>();
        while (offset < raw.Length)
        {
            var packetOffset = offset;
            var packetId = raw[offset++];
            switch (packetId)
            {
                case 0:
                {
                    Require(raw, offset, 5);
                    var path = raw[offset++];
                    var length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(raw[offset..]));
                    offset += 4;
                    var data = Bytes(raw, ref offset, length);
                    packets.Add(new GsDumpPacket(
                        GsDumpPacketKind.Transfer,
                        Enum.IsDefined(typeof(GsTransferPath), (int)path)
                            ? (GsTransferPath)path
                            : null,
                        data));
                    break;
                }
                case 1:
                    Require(raw, offset, 1);
                    packets.Add(new GsDumpPacket(GsDumpPacketKind.VSync, null, [raw[offset++]]));
                    break;
                case 2:
                    packets.Add(new GsDumpPacket(GsDumpPacketKind.ReadFifo2, null, Bytes(raw, ref offset, 4)));
                    break;
                case 3:
                    packets.Add(new GsDumpPacket(GsDumpPacketKind.Registers, null, Bytes(raw, ref offset, 8192)));
                    break;
                default:
                    throw new InvalidDataException($"Unknown GS dump packet id {packetId} at offset 0x{packetOffset:X}.");
            }
        }

        return new GsDumpFile
        {
            Crc = crc,
            StateVersion = stateVersion,
            Serial = serial,
            ScreenshotWidth = screenshotWidth,
            ScreenshotHeight = screenshotHeight,
            ScreenshotOffset = screenshotOffset,
            ScreenshotSize = screenshotSize,
            ScreenshotPixels = screenshotPixels,
            State = state,
            Registers = registers,
            Packets = packets
        };
    }

    public bool TryGetInitialGsMemory(out ReadOnlySpan<byte> memory)
    {
        // PCSX2 GSState::Freeze() state version 9 writes the drawing environment,
        // transfer state, then GSLocalMemory::m_vm8.  The trailing GIF path state
        // and q value are 84 bytes in the current version-9 layout.
        var offset = StateVersion >= 9 ? StateVersion9LocalMemoryOffset : -1;
        if (offset >= 0 && State.Length >= offset + GsLocalMemorySize)
        {
            memory = State.AsSpan(offset, GsLocalMemorySize);
            return true;
        }

        memory = default;
        return false;
    }

    private static void Require(ReadOnlySpan<byte> data, int offset, int count)
    {
        if (offset < 0 || count < 0 || offset > data.Length || count > data.Length - offset)
            throw new EndOfStreamException($"Unexpected end of GS dump at 0x{offset:X}; wanted {count} bytes.");
    }
}
