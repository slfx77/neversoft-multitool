using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace NeversoftMultitool.Core.Formats.Ps2Scene;

internal enum GifKickPacketKind
{
    None = 0,
    XtopWindow = 1,
    PostBatchFirstWord = 2,
    PostBatchSecondPair = 3
}

internal enum VifReplayCommandKind
{
    Nop = 0,
    Stcycl = 1,
    Base = 2,
    Offset = 3,
    Itop = 4,
    Stmod = 5,
    Stmask = 6,
    Unpack = 7,
    Mscal = 8,
    Mscalf = 9,
    Mscnt = 10,
    Flush = 11,
    Flushe = 12,
    Direct = 13,
    DirectHl = 14,
    Mpg = 15,
    Unknown = 16
}

internal readonly record struct Vu1Qword(uint X, uint Y, uint Z, uint W);

internal readonly record struct PostBatchElement(ushort C0, ushort C1, ushort C2, ushort C3);

internal sealed class ReplayContextWrite
{
    public required VifUnpackCommand Unpack { get; init; }
    public required int[] WriteAddresses { get; init; }
    public required Vu1Qword[] Words { get; init; }
}

internal readonly record struct VifReplayRegisters(
    byte Cl,
    byte Wl,
    int Base,
    int Offset,
    int Tops,
    int Top,
    int Dbf,
    int Itop,
    int Stmod,
    uint Stmask);

internal readonly record struct ReplayVertexSource(
    Vector3 Position,
    Vector3 Normal,
    bool HasNormal,
    float U,
    float V,
    bool HasUv,
    int OutputFullAddress,
    int DuplicateFullAddress,
    byte OutputAddress,
    byte DuplicateAddress,
    bool OutputNoKick,
    bool DuplicateNoKick);

internal sealed class GifKickPacket
{
    public static GifKickPacket None { get; } = new()
    {
        Kind = GifKickPacketKind.None
    };

    public GifKickPacketKind Kind { get; init; }
    public bool IsPresent => Kind != GifKickPacketKind.None;
    public bool Eop { get; init; }
    public int Nloop { get; init; }
    public int Address { get; init; }
    public int Size { get; init; }
    public int SourceElementIndex { get; init; }
}

internal sealed class VifReplayCommandTrace
{
    public required int CommandOffset { get; init; }
    public required ushort Imm { get; init; }
    public required byte RawCommand { get; init; }
    public required VifReplayCommandKind Kind { get; init; }
    public required VifReplayRegisters Before { get; init; }
    public required VifReplayRegisters After { get; init; }
    public VifUnpackCommand? Unpack { get; init; }
}

internal sealed class VifUnpackCommand
{
    public required int CommandOffset { get; init; }
    public required int DataOffset { get; init; }
    public required int Vn { get; init; }
    public required int Vl { get; init; }
    public required int Num { get; init; }
    public required int Address { get; init; }
    public required bool Flg { get; init; }
    public required bool Usn { get; init; }
    public required byte CycleCl { get; init; }
    public required byte CycleWl { get; init; }
    public required int EffectiveAddress { get; init; }
    public required int EndAddress { get; init; }
    public required int[] WriteAddresses { get; init; }
}

internal sealed class Vu1BatchSnapshot
{
    public required int SetupIndex { get; init; }
    public required int CommandOffset { get; init; }
    public required ushort Imm { get; init; }
    public required int PreTops { get; init; }
    public required int Xtop { get; init; }
    public required int PostTops { get; init; }
    public required int Dbf { get; init; }
    public required int MinWrittenAddress { get; init; }
    public required int MaxWrittenAddress { get; init; }
    public required int MinWriteWindowStart { get; init; }
    public required Vu1Qword[] XtopWindow { get; init; }
    public required Vu1Qword[] MinWriteWindow { get; init; }
    public required int KickBaseWindowStart { get; init; }
    public required Vu1Qword[] KickBaseWindow { get; init; }
    public required GifKickPacket ParserTag { get; init; }
    public required VifUnpackCommand[] Unpacks { get; init; }
}

internal sealed class ThawReplayBatch
{
    public required int SetupIndex { get; init; }
    public required bool IsPreambleBatch { get; init; }
    public required int FirstCommandOffset { get; init; }
    public required int PositionOffset { get; init; }
    public required int NormalOffset { get; init; }
    public required int UvAdcOffset { get; init; }
    public required int VertexCount { get; init; }
    public required int OutputVertexCount { get; init; }
    public required ReplayVertexSource[] VertexSources { get; init; }
    public required ReplayContextWrite[] ContextWrites { get; init; }
    public required PostBatchElement[] PostBatchElements { get; init; }
    public required GifKickPacket OutputKickPacket { get; init; }
    public required VifReplayCommandTrace[] CommandTrace { get; init; }
    public required Vu1BatchSnapshot Snapshot { get; init; }
}

internal sealed class VifReplayState
{
    public byte Cl { get; private set; } = 1;
    public byte Wl { get; private set; } = 1;
    public int Base { get; private set; }
    public int Offset { get; private set; }
    public int Tops { get; private set; }
    public int Top { get; private set; }
    public int Dbf { get; private set; }
    public int Itop { get; private set; }
    public int Stmod { get; private set; }
    public uint Stmask { get; private set; }

    public void SetCycle(byte cl, byte wl)
    {
        Cl = cl;
        Wl = wl;
    }

    public void SetBase(int baseAddress)
    {
        Base = baseAddress % Vu1Memory.SizeQwords;
    }

    public void SetOffset(int offset)
    {
        Offset = offset % Vu1Memory.SizeQwords;
        Dbf = 0;
        Tops = Base;
    }

    public void SetItop(int itop)
    {
        Itop = itop % Vu1Memory.SizeQwords;
    }

    public void SetStmod(int stmod)
    {
        Stmod = stmod & 3;
    }

    public void SetStmask(uint stmask)
    {
        Stmask = stmask;
    }

    public VifReplayRegisters Snapshot()
    {
        return new VifReplayRegisters(Cl, Wl, Base, Offset, Tops, Top, Dbf, Itop, Stmod, Stmask);
    }

    public Vu1BatchSnapshot CompleteBatch(
        Vu1Memory memory,
        int commandOffset,
        ushort imm,
        int setupIndex,
        GifKickPacket outputKickPacket,
        IReadOnlyList<VifUnpackCommand> unpacks)
    {
        var preTops = Tops;
        Top = Tops;
        Dbf ^= 1;
        Tops = Dbf != 0 ? (Base + Offset) % Vu1Memory.SizeQwords : Base;

        var minWritten = Top;
        var maxWritten = Top;
        var minWriteWindowStart = -1;
        Vu1Qword[] minWriteWindow = [];
        if (unpacks.Count > 0)
        {
            minWritten = unpacks[0].EffectiveAddress;
            maxWritten = (unpacks[0].EndAddress - 1 + Vu1Memory.SizeQwords) % Vu1Memory.SizeQwords;
            for (var i = 1; i < unpacks.Count; i++)
            {
                if (unpacks[i].EffectiveAddress < minWritten)
                    minWritten = unpacks[i].EffectiveAddress;

                var unpackMax = (unpacks[i].EndAddress - 1 + Vu1Memory.SizeQwords) % Vu1Memory.SizeQwords;
                if (unpackMax > maxWritten)
                    maxWritten = unpackMax;
            }

            minWriteWindowStart = (minWritten - 2 + Vu1Memory.SizeQwords) % Vu1Memory.SizeQwords;
            minWriteWindow = memory.ReadWindow(minWriteWindowStart, 6);
        }

        var xtopWindow = memory.ReadWindow(Top, 16);
        var kickBaseWindowStart = -1;
        Vu1Qword[] kickBaseWindow = [];
        if (outputKickPacket.IsPresent)
        {
            kickBaseWindowStart = (outputKickPacket.Address - 17 + Vu1Memory.SizeQwords) % Vu1Memory.SizeQwords;
            kickBaseWindow = memory.ReadWindow(kickBaseWindowStart, 18);
        }

        return new Vu1BatchSnapshot
        {
            SetupIndex = setupIndex,
            CommandOffset = commandOffset,
            Imm = imm,
            PreTops = preTops,
            Xtop = Top,
            PostTops = Tops,
            Dbf = Dbf,
            MinWrittenAddress = minWritten,
            MaxWrittenAddress = maxWritten,
            MinWriteWindowStart = minWriteWindowStart,
            XtopWindow = xtopWindow,
            MinWriteWindow = minWriteWindow,
            KickBaseWindowStart = kickBaseWindowStart,
            KickBaseWindow = kickBaseWindow,
            ParserTag = ThawPs2ReplayEngine.DecodeXtopParserTag(xtopWindow[0]),
            Unpacks = [.. unpacks]
        };
    }
}

internal sealed class Vu1Memory
{
    public const int SizeQwords = 1024;

    private readonly Vu1Qword[] _words = new Vu1Qword[SizeQwords];
    private readonly bool[] _written = new bool[SizeQwords];

    public void WriteQword(int address, uint x, uint y, uint z, uint w)
    {
        var wrapped = Wrap(address);
        _words[wrapped] = new Vu1Qword(x, y, z, w);
        _written[wrapped] = true;
    }

    public Vu1Qword ReadQword(int address)
    {
        return _words[Wrap(address)];
    }

    public bool IsWritten(int address)
    {
        return _written[Wrap(address)];
    }

    public Vu1Qword[] ReadWindow(int startAddress, int length)
    {
        var window = new Vu1Qword[length];
        for (var i = 0; i < length; i++)
            window[i] = ReadQword(startAddress + i);
        return window;
    }

    private static int Wrap(int address)
    {
        var wrapped = address % SizeQwords;
        return wrapped < 0 ? wrapped + SizeQwords : wrapped;
    }
}

internal static class ThawPs2ReplayEngine
{
    private const float PositionScale = 1f / 16f;
    private const float NormalScale = 1f / 127f;
    private const float UvScale = 1f / 4096f;

    public static List<ThawReplayBatch> ReplayBatches(
        byte[] data,
        int chainStart,
        int chainEnd,
        IReadOnlyList<int> setupStarts)
    {
        var batches = new List<ThawReplayBatch>();
        var state = new VifReplayState();
        var memory = new Vu1Memory();
        var builder = new ReplayBatchBuilder();
        var position = chainStart;
        var inInterleaved = false;
        var collectingPostBatch = false;
        var currentSetupIndex = 0;
        var nextBoundaryIndex = 0;
        var firstSetupStart = setupStarts.Count > 0 ? setupStarts[0] : chainStart;

        while (position < chainEnd && position + 4 <= data.Length)
        {
            while (nextBoundaryIndex < setupStarts.Count && position >= setupStarts[nextBoundaryIndex])
            {
                nextBoundaryIndex++;
                currentSetupIndex = nextBoundaryIndex;
            }

            var cmd = data[position + 3];
            var imm = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(position));
            var command = cmd & 0x7F;
            var before = state.Snapshot();
            if (builder.FirstCommandOffset < 0)
                builder.FirstCommandOffset = position;

            if (command == 0x01)
            {
                var cl = data[position];
                var wl = data[position + 1];
                state.SetCycle(cl, wl);
                if (cl == 3 && wl == 1)
                {
                    inInterleaved = true;
                    collectingPostBatch = false;
                }
                else if (cl == 1 && wl == 1)
                {
                    inInterleaved = false;
                    collectingPostBatch = builder.HasVertices;
                }
                else
                {
                    inInterleaved = false;
                    collectingPostBatch = false;
                }

                builder.Commands.Add(CreateCommandTrace(position, imm, cmd, VifReplayCommandKind.Stcycl, before, state.Snapshot()));
                position = ThawPs2SkinFile.VifNextCode(data, position, chainEnd);
                continue;
            }

            switch (command)
            {
                case 0x03:
                    state.SetBase(imm & 0x3FF);
                    builder.Commands.Add(CreateCommandTrace(position, imm, cmd, VifReplayCommandKind.Base, before, state.Snapshot()));
                    position = ThawPs2SkinFile.VifNextCode(data, position, chainEnd);
                    continue;
                case 0x02:
                    state.SetOffset(imm & 0x3FF);
                    builder.Commands.Add(CreateCommandTrace(position, imm, cmd, VifReplayCommandKind.Offset, before, state.Snapshot()));
                    position = ThawPs2SkinFile.VifNextCode(data, position, chainEnd);
                    continue;
                case 0x04:
                    state.SetItop(imm & 0x3FF);
                    builder.Commands.Add(CreateCommandTrace(position, imm, cmd, VifReplayCommandKind.Itop, before, state.Snapshot()));
                    position = ThawPs2SkinFile.VifNextCode(data, position, chainEnd);
                    continue;
                case 0x05:
                    state.SetStmod(imm & 3);
                    builder.Commands.Add(CreateCommandTrace(position, imm, cmd, VifReplayCommandKind.Stmod, before, state.Snapshot()));
                    position = ThawPs2SkinFile.VifNextCode(data, position, chainEnd);
                    continue;
                case 0x20:
                    if (position + 8 <= data.Length)
                        state.SetStmask(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position + 4)));
                    builder.Commands.Add(CreateCommandTrace(position, imm, cmd, VifReplayCommandKind.Stmask, before, state.Snapshot()));
                    position = ThawPs2SkinFile.VifNextCode(data, position, chainEnd);
                    continue;
                case 0x10:
                    builder.Commands.Add(CreateCommandTrace(position, imm, cmd, VifReplayCommandKind.Flush, before, before));
                    position = ThawPs2SkinFile.VifNextCode(data, position, chainEnd);
                    continue;
                case 0x11:
                    builder.Commands.Add(CreateCommandTrace(position, imm, cmd, VifReplayCommandKind.Flushe, before, before));
                    position = ThawPs2SkinFile.VifNextCode(data, position, chainEnd);
                    continue;
                case 0x50:
                    builder.Commands.Add(CreateCommandTrace(position, imm, cmd, VifReplayCommandKind.Direct, before, before));
                    position = ThawPs2SkinFile.VifNextCode(data, position, chainEnd);
                    continue;
                case 0x51:
                    builder.Commands.Add(CreateCommandTrace(position, imm, cmd, VifReplayCommandKind.DirectHl, before, before));
                    position = ThawPs2SkinFile.VifNextCode(data, position, chainEnd);
                    continue;
                case 0x4A:
                    builder.Commands.Add(CreateCommandTrace(position, imm, cmd, VifReplayCommandKind.Mpg, before, before));
                    position = ThawPs2SkinFile.VifNextCode(data, position, chainEnd);
                    continue;
            }

            if ((cmd & 0x60) == 0x60)
            {
                var unpack = ApplyUnpack(data, position, cmd, imm, state, memory, chainEnd);
                builder.Unpacks.Add(unpack);

                if (builder.FirstCommandOffset < 0)
                    builder.FirstCommandOffset = position;

                if (inInterleaved && unpack.Num > 1)
                {
                    if (unpack.Vn == 2 && unpack.Vl == 1)
                    {
                builder.PositionOffset = unpack.DataOffset;
                builder.VertexCount = unpack.Num;
            }
            else if (unpack.Vn == 2 && unpack.Vl == 2)
            {
                        builder.NormalOffset = unpack.DataOffset;
                    }
                    else if (unpack.Vn == 3 && unpack.Vl == 1)
                {
                    builder.UvAdcOffset = unpack.DataOffset;
                }
            }
            else if (!collectingPostBatch && unpack.Usn && unpack.Num > 0)
            {
                builder.ContextWrites.Add(CreateContextWrite(memory, unpack));
            }
            else if (collectingPostBatch && unpack.Vn == 3 && unpack.Vl == 1 && unpack.Num > 0)
            {
                for (var i = 0; i < unpack.Num; i++)
                {
                        var elementOffset = unpack.DataOffset + i * 8;
                        if (elementOffset + 8 > data.Length)
                            break;

                        builder.PostBatchElements.Add(new PostBatchElement(
                            BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(elementOffset)),
                            BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(elementOffset + 2)),
                            BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(elementOffset + 4)),
                            BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(elementOffset + 6))));
                    }
                }

                builder.Commands.Add(CreateCommandTrace(position, imm, cmd, VifReplayCommandKind.Unpack, before, state.Snapshot(), unpack));
                position = ThawPs2SkinFile.VifNextCode(data, position, chainEnd);
                continue;
            }

            if (command is 0x14 or 0x15 or 0x17)
            {
                var outputKickPacket = DecodeOutputKickPacket(builder.PostBatchElements);
                var snapshot = state.CompleteBatch(
                    memory,
                    position,
                    imm,
                    currentSetupIndex,
                    outputKickPacket,
                    builder.Unpacks);
                builder.Commands.Add(CreateCommandTrace(
                    position,
                    imm,
                    cmd,
                    command switch
                    {
                        0x14 => VifReplayCommandKind.Mscal,
                        0x15 => VifReplayCommandKind.Mscalf,
                        _ => VifReplayCommandKind.Mscnt
                    },
                    before,
                    state.Snapshot()));

                if (builder.HasReplayActivity)
                {
                    batches.Add(builder.Build(
                        data,
                        currentSetupIndex,
                        snapshot,
                        builder.FirstCommandOffset >= 0 && builder.FirstCommandOffset < firstSetupStart));
                }

                builder = new ReplayBatchBuilder();
                inInterleaved = false;
                collectingPostBatch = false;
                position = ThawPs2SkinFile.VifNextCode(data, position, chainEnd);
                continue;
            }

            builder.Commands.Add(CreateCommandTrace(position, imm, cmd, VifReplayCommandKind.Unknown, before, before));
            position = ThawPs2SkinFile.VifNextCode(data, position, chainEnd);
        }

        if (builder.HasReplayActivity)
        {
            var outputKickPacket = DecodeOutputKickPacket(builder.PostBatchElements);
            var snapshot = state.CompleteBatch(
                memory,
                chainEnd,
                0,
                currentSetupIndex,
                outputKickPacket,
                builder.Unpacks);
            batches.Add(builder.Build(
                data,
                currentSetupIndex,
                snapshot,
                builder.FirstCommandOffset >= 0 && builder.FirstCommandOffset < firstSetupStart));
        }

        return batches;
    }

    internal static GifKickPacket DecodeXtopParserTag(Vu1Qword firstWord)
    {
        var nloop = (int)(firstWord.X & 0x7FFF);
        var eop = ((firstWord.X >> 15) & 1) != 0;
        var address = (int)(firstWord.Y & 0xFFFF);
        var size = (int)(firstWord.W & 0xFFFF);

        if (nloop == 0 && address == 0 && size == 0)
            return GifKickPacket.None;

        return new GifKickPacket
        {
            Kind = GifKickPacketKind.XtopWindow,
            Eop = eop,
            Nloop = nloop,
            Address = address,
            Size = size,
            SourceElementIndex = 0
        };
    }

    private static GifKickPacket DecodeOutputKickPacket(IReadOnlyList<PostBatchElement> postBatchElements)
    {
        if (postBatchElements.Count == 0)
            return GifKickPacket.None;

        var first = postBatchElements[0];
        if ((first.C0 & 0x8000) != 0)
        {
            return new GifKickPacket
            {
                Kind = GifKickPacketKind.PostBatchFirstWord,
                Eop = (first.C0 & 0x8000) != 0,
                Nloop = first.C0 & 0x7FFF,
                Address = first.C1,
                Size = first.C3,
                SourceElementIndex = 0
            };
        }

        if ((first.C2 & 0x8000) != 0)
        {
            return new GifKickPacket
            {
                Kind = GifKickPacketKind.PostBatchSecondPair,
                Eop = (first.C2 & 0x8000) != 0,
                Nloop = first.C2 & 0x7FFF,
                Address = first.C3,
                Size = 0,
                SourceElementIndex = 0
            };
        }

        return GifKickPacket.None;
    }

    private static ReplayVertexSource[] DecodeVertexSources(
        byte[] data,
        int positionOffset,
        int normalOffset,
        int uvAdcOffset,
        int count)
    {
        var sources = new ReplayVertexSource[count];
        for (var i = 0; i < count; i++)
        {
            var position = Vector3.Zero;
            var posOffset = positionOffset + i * 6;
            if (posOffset + 6 <= data.Length)
            {
                position = new Vector3(
                    BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(posOffset)) * PositionScale,
                    BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(posOffset + 2)) * PositionScale,
                    BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(posOffset + 4)) * PositionScale);
            }

            var normal = Vector3.UnitY;
            var hasNormal = false;
            if (normalOffset >= 0)
            {
                var nrmOffset = normalOffset + i * 3;
                if (nrmOffset + 3 <= data.Length)
                {
                    var rawNormal = new Vector3(
                        (sbyte)data[nrmOffset] * NormalScale,
                        (sbyte)data[nrmOffset + 1] * NormalScale,
                        (sbyte)data[nrmOffset + 2] * NormalScale);
                    var length = rawNormal.Length();
                    normal = length > 0.001f ? rawNormal / length : Vector3.UnitY;
                    hasNormal = true;
                }
            }

            var u = 0f;
            var v = 0f;
            var hasUv = false;
            byte outputAddress = 0;
            byte duplicateAddress = 0;
            var outputNoKick = false;
            var duplicateNoKick = false;
            if (uvAdcOffset >= 0)
            {
                var uvOffset = uvAdcOffset + i * 8;
                if (uvOffset + 8 <= data.Length)
                {
                    u = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(uvOffset)) * UvScale;
                    v = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(uvOffset + 2)) * UvScale;
                    var outputWord = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(uvOffset + 4));
                    var duplicateWord = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(uvOffset + 6));
                    var outputFullAddress = outputWord & 0x3FF;
                    var duplicateFullAddress = duplicateWord & 0x3FF;
                    outputAddress = (byte)outputWord;
                    duplicateAddress = (byte)duplicateWord;
                    outputNoKick = (outputWord & 0x8000) != 0;
                    duplicateNoKick = (duplicateWord & 0x8000) != 0;
                    hasUv = true;

                    sources[i] = new ReplayVertexSource(
                        position,
                        normal,
                        hasNormal,
                        u,
                        v,
                        hasUv,
                        outputFullAddress,
                        duplicateFullAddress,
                        outputAddress,
                        duplicateAddress,
                        outputNoKick,
                        duplicateNoKick);
                    continue;
                }
            }

            sources[i] = new ReplayVertexSource(
                position,
                normal,
                hasNormal,
                u,
                v,
                hasUv,
                0,
                0,
                outputAddress,
                duplicateAddress,
                outputNoKick,
                duplicateNoKick);
        }

        return sources;
    }

    private static VifUnpackCommand ApplyUnpack(
        byte[] data,
        int commandOffset,
        byte cmd,
        ushort imm,
        VifReplayState state,
        Vu1Memory memory,
        int chainEnd)
    {
        var vn = (cmd >> 2) & 3;
        var vl = cmd & 3;
        var num = data[commandOffset + 2];
        var address = imm & 0x3FF;
        var flg = ((imm >> 15) & 1) != 0;
        var usn = ((imm >> 14) & 1) != 0;
        var dataOffset = commandOffset + 4;
        var effectiveAddress = flg ? (address + state.Tops) % Vu1Memory.SizeQwords : address % Vu1Memory.SizeQwords;

        var componentCount = vn + 1;
        var componentBits = 32 >> vl;
        var elementBytes = (componentBits * componentCount + 7) / 8;
        var source = dataOffset;
        var currentAddress = effectiveAddress;
        var cyclePosition = 0;
        var writeAddresses = new int[num];

        for (var i = 0; i < num; i++)
        {
            Span<uint> words = stackalloc uint[4];
            if (vl == 0)
            {
                for (var component = 0; component < componentCount; component++)
                {
                    if (source + 4 <= data.Length && source + 4 <= chainEnd)
                        words[component] = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(source));
                    source += 4;
                }
            }
            else if (vl == 1)
            {
                for (var component = 0; component < componentCount; component++)
                {
                    if (source + 2 <= data.Length && source + 2 <= chainEnd)
                    {
                        var raw = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(source));
                        words[component] = usn ? raw : SignExtendToUInt32(raw, 16);
                    }

                    source += 2;
                }
            }
            else if (vl == 2)
            {
                for (var component = 0; component < componentCount; component++)
                {
                    if (source < data.Length && source < chainEnd)
                    {
                        var raw = data[source];
                        words[component] = usn ? raw : SignExtendToUInt32(raw, 8);
                    }

                    source += 1;
                }
            }

            source = dataOffset + (i + 1) * elementBytes;
            var wrappedAddress = ((currentAddress % Vu1Memory.SizeQwords) + Vu1Memory.SizeQwords) % Vu1Memory.SizeQwords;
            writeAddresses[i] = wrappedAddress;
            memory.WriteQword(wrappedAddress, words[0], words[1], words[2], words[3]);

            cyclePosition++;
            if (state.Cl <= 1 || state.Cl == state.Wl)
            {
                currentAddress++;
            }
            else if (cyclePosition >= state.Wl)
            {
                currentAddress += 1 + (state.Cl - state.Wl);
                cyclePosition = 0;
            }
            else
            {
                currentAddress++;
            }
        }

        return new VifUnpackCommand
        {
            CommandOffset = commandOffset,
            DataOffset = dataOffset,
            Vn = vn,
            Vl = vl,
            Num = num,
            Address = address,
            Flg = flg,
            Usn = usn,
            CycleCl = state.Cl,
            CycleWl = state.Wl,
            EffectiveAddress = effectiveAddress,
            EndAddress = currentAddress % Vu1Memory.SizeQwords,
            WriteAddresses = writeAddresses
        };
    }

    private static ReplayContextWrite CreateContextWrite(Vu1Memory memory, VifUnpackCommand unpack)
    {
        var words = new Vu1Qword[unpack.WriteAddresses.Length];
        for (var i = 0; i < unpack.WriteAddresses.Length; i++)
            words[i] = memory.ReadQword(unpack.WriteAddresses[i]);

        return new ReplayContextWrite
        {
            Unpack = unpack,
            WriteAddresses = [.. unpack.WriteAddresses],
            Words = words
        };
    }

    private static uint SignExtendToUInt32(uint value, int bits)
    {
        var mask = 1u << (bits - 1);
        if ((value & mask) == 0)
            return value;

        var signed = (int)(value | (~0u << bits));
        return unchecked((uint)signed);
    }

    private static VifReplayCommandTrace CreateCommandTrace(
        int commandOffset,
        ushort imm,
        byte rawCommand,
        VifReplayCommandKind kind,
        VifReplayRegisters before,
        VifReplayRegisters after,
        VifUnpackCommand? unpack = null)
    {
        return new VifReplayCommandTrace
        {
            CommandOffset = commandOffset,
            Imm = imm,
            RawCommand = rawCommand,
            Kind = kind,
            Before = before,
            After = after,
            Unpack = unpack
        };
    }

    private sealed class ReplayBatchBuilder
    {
        public readonly List<VifReplayCommandTrace> Commands = [];
        public readonly List<VifUnpackCommand> Unpacks = [];
        public readonly List<ReplayContextWrite> ContextWrites = [];
        public readonly List<PostBatchElement> PostBatchElements = [];

        public int FirstCommandOffset { get; set; } = -1;
        public int PositionOffset { get; set; } = -1;
        public int NormalOffset { get; set; } = -1;
        public int UvAdcOffset { get; set; } = -1;
        public int VertexCount { get; set; }

        public bool HasReplayActivity => Commands.Count > 0 || Unpacks.Count > 0 || PostBatchElements.Count > 0;
        public bool HasVertices => PositionOffset >= 0 && VertexCount > 0;

        public ThawReplayBatch Build(
            byte[] data,
            int setupIndex,
            Vu1BatchSnapshot snapshot,
            bool isPreambleBatch)
        {
            var outputKickPacket = DecodeOutputKickPacket(PostBatchElements);
            return new ThawReplayBatch
            {
                SetupIndex = setupIndex,
                IsPreambleBatch = isPreambleBatch,
                FirstCommandOffset = FirstCommandOffset,
                PositionOffset = PositionOffset,
                NormalOffset = NormalOffset,
                UvAdcOffset = UvAdcOffset,
                VertexCount = VertexCount,
                OutputVertexCount = outputKickPacket.Nloop,
                VertexSources = DecodeVertexSources(data, PositionOffset, NormalOffset, UvAdcOffset, VertexCount),
                ContextWrites = HasVertices ? [.. ContextWrites] : [],
                PostBatchElements = [.. PostBatchElements],
                OutputKickPacket = outputKickPacket,
                CommandTrace = [.. Commands],
                Snapshot = snapshot
            };
        }
    }
}

internal static class ThawPs2ReplayTraceFormatter
{
    public static string FormatBatches(IReadOnlyList<ThawReplayBatch> batches)
    {
        var sb = new StringBuilder();

        for (var i = 0; i < batches.Count; i++)
        {
            var batch = batches[i];
            if (i > 0)
                sb.AppendLine();

            sb.Append("BATCH ").Append(i);
            sb.Append(" setup=").Append(batch.SetupIndex);
            if (batch.IsPreambleBatch)
                sb.Append(" PREAMBLE");
            sb.AppendLine();

            sb.Append("  first=0x").Append(batch.FirstCommandOffset.ToString("X6"));
            sb.Append(" verts=").Append(batch.VertexCount);
            sb.Append(" out=").Append(batch.OutputVertexCount);
            sb.AppendLine();

            var kick = batch.OutputKickPacket;
            sb.Append("  kick=").Append(kick.Kind);
            sb.Append(" nloop=").Append(kick.Nloop);
            sb.Append(" addr=").Append(kick.Address);
            sb.Append(" size=").Append(kick.Size);
            sb.AppendLine();

            var snapshot = batch.Snapshot;
            sb.Append("  xtop=").Append(snapshot.Xtop);
            sb.Append(" preTops=").Append(snapshot.PreTops);
            sb.Append(" postTops=").Append(snapshot.PostTops);
            sb.Append(" dbf=").Append(snapshot.Dbf);
            sb.AppendLine();

            sb.Append("  minWrite=").Append(snapshot.MinWrittenAddress);
            sb.Append(" maxWrite=").Append(snapshot.MaxWrittenAddress);
            if (snapshot.MinWriteWindowStart >= 0)
                sb.Append(" minWindowStart=").Append(snapshot.MinWriteWindowStart);
            sb.AppendLine();

            var parser = snapshot.ParserTag;
            sb.Append("  parser=").Append(parser.Kind);
            sb.Append(" nloop=").Append(parser.Nloop);
            sb.Append(" addr=").Append(parser.Address);
            sb.Append(" size=").Append(parser.Size);
            sb.AppendLine();

            if (batch.ContextWrites.Length > 0)
            {
                sb.AppendLine("  context:");
                foreach (var contextWrite in batch.ContextWrites)
                    AppendContextWrite(sb, contextWrite);
            }

            if (snapshot.XtopWindow.Length > 0)
            {
                var first = snapshot.XtopWindow[0];
                sb.Append("  xtop[0]=(");
                sb.Append(first.X.ToString("X8")).Append(", ");
                sb.Append(first.Y.ToString("X8")).Append(", ");
                sb.Append(first.Z.ToString("X8")).Append(", ");
                sb.Append(first.W.ToString("X8")).Append(')');
                sb.AppendLine();
            }

            if (snapshot.MinWriteWindow.Length > 0)
            {
                for (var windowIndex = 0; windowIndex < snapshot.MinWriteWindow.Length; windowIndex++)
                {
                    var address = (snapshot.MinWriteWindowStart + windowIndex) % Vu1Memory.SizeQwords;
                    var word = snapshot.MinWriteWindow[windowIndex];
                    sb.Append("  minWindow[").Append(address).Append("]=(");
                    sb.Append(word.X.ToString("X8")).Append(", ");
                    sb.Append(word.Y.ToString("X8")).Append(", ");
                    sb.Append(word.Z.ToString("X8")).Append(", ");
                    sb.Append(word.W.ToString("X8")).Append(')');
                    sb.AppendLine();
                }
            }

            if (snapshot.KickBaseWindow.Length > 0)
            {
                for (var windowIndex = 0; windowIndex < snapshot.KickBaseWindow.Length; windowIndex++)
                {
                    var address = (snapshot.KickBaseWindowStart + windowIndex) % Vu1Memory.SizeQwords;
                    var word = snapshot.KickBaseWindow[windowIndex];
                    sb.Append("  kickBase[").Append(address).Append("]=(");
                    sb.Append(word.X.ToString("X8")).Append(", ");
                    sb.Append(word.Y.ToString("X8")).Append(", ");
                    sb.Append(word.Z.ToString("X8")).Append(", ");
                    sb.Append(word.W.ToString("X8")).Append(')');
                    sb.AppendLine();
                }
            }

            if (batch.CommandTrace.Length > 0)
            {
                sb.AppendLine("  commands:");
                foreach (var command in batch.CommandTrace)
                    sb.Append("    ").AppendLine(FormatCommand(command));
            }
        }

        return sb.ToString();
    }

    private static string FormatCommand(VifReplayCommandTrace command)
    {
        var prefix = $"[0x{command.CommandOffset:X6}] ";
        return command.Kind switch
        {
            VifReplayCommandKind.Stcycl => $"{prefix}STCYCL CL={command.After.Cl} WL={command.After.Wl}",
            VifReplayCommandKind.Base => $"{prefix}BASE = {command.After.Base}",
            VifReplayCommandKind.Offset =>
                $"{prefix}OFFSET = {command.After.Offset} (TOPS={command.After.Tops}, DBF={command.After.Dbf})",
            VifReplayCommandKind.Itop => $"{prefix}ITOP = {command.After.Itop}",
            VifReplayCommandKind.Stmod => $"{prefix}STMOD = {command.After.Stmod}",
            VifReplayCommandKind.Stmask => $"{prefix}STMASK = 0x{command.After.Stmask:X8}",
            VifReplayCommandKind.Unpack => FormatUnpack(command),
            VifReplayCommandKind.Mscal or VifReplayCommandKind.Mscalf or VifReplayCommandKind.Mscnt =>
                $"{prefix}{command.Kind.ToString().ToUpperInvariant()} imm={command.Imm} (TOP={command.After.Top}, TOPS: {command.Before.Tops}->{command.After.Tops}, DBF: {command.Before.Dbf}->{command.After.Dbf})",
            VifReplayCommandKind.Flush => $"{prefix}FLUSH",
            VifReplayCommandKind.Flushe => $"{prefix}FLUSHE",
            VifReplayCommandKind.Direct => $"{prefix}DIRECT nloop={command.Imm}",
            VifReplayCommandKind.DirectHl => $"{prefix}DIRECTHL nloop={command.Imm}",
            VifReplayCommandKind.Mpg => $"{prefix}MPG imm={command.Imm}",
            VifReplayCommandKind.Nop => $"{prefix}NOP",
            _ => $"{prefix}VIF_0x{(command.RawCommand & 0x7F):X2} imm={command.Imm}"
        };
    }

    private static string FormatUnpack(VifReplayCommandTrace command)
    {
        if (command.Unpack is null)
            return $"[0x{command.CommandOffset:X6}] UNPACK";

        var unpack = command.Unpack;
        var format = GetFormatName(unpack.Vn, unpack.Vl);
        var unsigned = unpack.Usn ? "U" : string.Empty;
        var tops = unpack.Flg ? $"+TOPS({command.Before.Tops})" : string.Empty;
        return $"[0x{command.CommandOffset:X6}] UNPACK {unsigned}{format} NUM={unpack.Num} ADDR={unpack.Address}{tops} -> eff={unpack.EffectiveAddress} (end={unpack.EndAddress})";
    }

    private static void AppendContextWrite(StringBuilder sb, ReplayContextWrite contextWrite)
    {
        var unpack = contextWrite.Unpack;
        var format = GetFormatName(unpack.Vn, unpack.Vl);
        var unsigned = unpack.Usn ? "U" : string.Empty;
        sb.Append("    [0x").Append(unpack.CommandOffset.ToString("X6")).Append("] ");
        sb.Append("UNPACK ").Append(unsigned).Append(format);
        sb.Append(" NUM=").Append(unpack.Num);
        sb.Append(" ADDR=").Append(unpack.Address);
        sb.Append(" -> ");
        sb.Append(string.Join(", ", contextWrite.WriteAddresses.Select(address => address.ToString())));
        sb.AppendLine();

        for (var i = 0; i < contextWrite.Words.Length; i++)
        {
            var address = contextWrite.WriteAddresses[i];
            var word = contextWrite.Words[i];
            sb.Append("      ctx[").Append(address).Append("]=(");
            sb.Append(word.X.ToString("X8")).Append(", ");
            sb.Append(word.Y.ToString("X8")).Append(", ");
            sb.Append(word.Z.ToString("X8")).Append(", ");
            sb.Append(word.W.ToString("X8")).Append(')');
            sb.AppendLine();
        }
    }

    private static string GetFormatName(int vn, int vl)
    {
        return (vn, vl) switch
        {
            (0, 0) => "S_32",
            (0, 1) => "S_16",
            (0, 2) => "S_8",
            (1, 0) => "V2_32",
            (1, 1) => "V2_16",
            (1, 2) => "V2_8",
            (2, 0) => "V3_32",
            (2, 1) => "V3_16",
            (2, 2) => "V3_8",
            (3, 0) => "V4_32",
            (3, 1) => "V4_16",
            (3, 2) => "V4_8",
            _ => $"?({vn},{vl})"
        };
    }
}
