using System.Buffers.Binary;

namespace NeversoftMultitool.Core.Formats.Audio;

/// <summary>
///     Checked, inspection-only view of a Nintendo 64 Sound Tools effects bank
///     (<c>.bfx</c>) bound to a separately validated PTR descriptor index space.
///     Component payloads remain opaque; this does not execute effect bytecode,
///     join cues, infer rates, apply pitch, or schedule playback.
/// </summary>
public sealed record N64SoundToolsFxBank(
    int SerializedSize,
    int ComponentCount,
    int EffectCount,
    int LocalWaveCount,
    uint FlagsRaw,
    uint PointerBankAddressRaw,
    int ComponentDataOffset,
    int WaveTableOffset,
    IReadOnlyList<N64SoundToolsFxComponent> Components,
    IReadOnlyList<N64SoundToolsLocalWaveBinding> LocalWaveMap,
    IReadOnlyList<byte> ComponentEntryTableRaw,
    IReadOnlyList<byte> OpaqueComponentRegionRaw,
    IReadOnlyList<byte> LocalWaveMapRaw)
{
    public const int HeaderSize = 0x18;
    public const int ComponentEntrySize = 8;

    /// <summary>
    ///     Parses a canonical pre-initialization BFX payload and validates every
    ///     local-wave target against the supplied, fully validated PTR bank.
    /// </summary>
    public static N64SoundToolsFxBank Parse(
        ReadOnlySpan<byte> data,
        N64SoundToolsPointerBank pointerBank)
    {
        ArgumentNullException.ThrowIfNull(pointerBank);
        var layout = ValidateLayout(data, pointerBank.Waves.Count);

        var components = new N64SoundToolsFxComponent[layout.ComponentCount];
        for (var i = 0; i < components.Length; i++)
        {
            var entryOffset = HeaderSize + i * ComponentEntrySize;
            var dataOffset = (int)ReadUInt32(data, entryOffset);
            var nextOffset = i + 1 == components.Length
                ? layout.WaveTableOffset
                : (int)ReadUInt32(data, entryOffset + ComponentEntrySize);
            components[i] = new N64SoundToolsFxComponent(
                i,
                dataOffset,
                ReadInt32(data, entryOffset + 4),
                data[dataOffset..nextOffset].ToArray());
        }

        var localWaveMap = new N64SoundToolsLocalWaveBinding[layout.LocalWaveCount];
        for (var i = 0; i < localWaveMap.Length; i++)
        {
            localWaveMap[i] = new N64SoundToolsLocalWaveBinding(
                i,
                ReadUInt16(data, layout.WaveTableOffset + i * 2));
        }

        return new N64SoundToolsFxBank(
            data.Length,
            layout.ComponentCount,
            layout.EffectCount,
            layout.LocalWaveCount,
            layout.FlagsRaw,
            layout.PointerBankAddressRaw,
            layout.ComponentDataOffset,
            layout.WaveTableOffset,
            components,
            localWaveMap,
            data[HeaderSize..layout.ComponentDataOffset].ToArray(),
            data[layout.ComponentDataOffset..layout.WaveTableOffset].ToArray(),
            data[layout.WaveTableOffset..].ToArray());
    }

    /// <summary>
    ///     Applies the complete canonical structural predicate. False is used
    ///     by the ROM resolver; BFX has no file magic or filename dependency.
    /// </summary>
    public static bool TryParse(
        ReadOnlySpan<byte> data,
        N64SoundToolsPointerBank pointerBank,
        out N64SoundToolsFxBank? bank)
    {
        ArgumentNullException.ThrowIfNull(pointerBank);
        try
        {
            bank = Parse(data, pointerBank);
            return true;
        }
        catch (InvalidDataException)
        {
            bank = null;
            return false;
        }
    }

    private static ValidatedLayout ValidateLayout(ReadOnlySpan<byte> data, int pointerWaveCount)
    {
        Require(pointerWaveCount > 0, "PTR descriptor index space is empty");
        Require(data.Length >= HeaderSize, "BFX header is truncated");

        var componentCount = ReadInt32(data, 0x00);
        var effectCount = ReadInt32(data, 0x04);
        var localWaveCount = ReadInt32(data, 0x08);
        var flagsRaw = ReadUInt32(data, 0x0C);
        var pointerBankAddressRaw = ReadUInt32(data, 0x10);
        var waveTableOffsetRaw = ReadUInt32(data, 0x14);

        Require(componentCount > 0, "BFX component count is not positive");
        Require(effectCount > 0, "BFX effect count is not positive");
        Require(effectCount <= componentCount,
            "BFX effect count exceeds the component count");
        Require(localWaveCount > 0, "BFX local wave count is not positive");
        Require(flagsRaw == 0, "BFX serialized flags are nonzero");
        Require(pointerBankAddressRaw == 0, "BFX serialized PTR address is nonzero");

        var componentDataOffsetLong = HeaderSize + (long)componentCount * ComponentEntrySize;
        Require(componentDataOffsetLong <= int.MaxValue && componentDataOffsetLong <= data.Length,
            "BFX component entry table is truncated or overflows");
        var componentDataOffset = (int)componentDataOffsetLong;
        Require(waveTableOffsetRaw <= int.MaxValue,
            "BFX local-wave table offset is out of range");
        var waveTableOffset = (int)waveTableOffsetRaw;
        Require(waveTableOffset >= componentDataOffset,
            "BFX local-wave table overlaps the header or component entries");
        Require((waveTableOffset & 1) == 0,
            "BFX local-wave table offset is not two-byte aligned");

        var logicalEnd = waveTableOffset + (long)localWaveCount * 2;
        Require(logicalEnd == data.Length,
            "BFX local-wave table does not consume the file exactly");

        var previousOffset = -1;
        for (var i = 0; i < componentCount; i++)
        {
            var entryOffset = HeaderSize + i * ComponentEntrySize;
            var componentOffsetRaw = ReadUInt32(data, entryOffset);
            Require(componentOffsetRaw <= int.MaxValue, $"BFX component {i} offset is out of range");
            var componentOffset = (int)componentOffsetRaw;
            Require(componentOffset >= componentDataOffset && componentOffset < waveTableOffset,
                $"BFX component {i} offset is outside the opaque component region");
            Require(i == 0 ? componentOffset == componentDataOffset : componentOffset > previousOffset,
                i == 0
                    ? "BFX first component does not start immediately after the entry table"
                    : $"BFX component {i} offset is not strictly ascending");
            previousOffset = componentOffset;
        }

        for (var i = 0; i < localWaveCount; i++)
        {
            var pointerWaveIndex = ReadUInt16(data, waveTableOffset + i * 2);
            Require(pointerWaveIndex < pointerWaveCount,
                $"BFX local wave {i} targets PTR descriptor {pointerWaveIndex}, " +
                $"outside the {pointerWaveCount}-descriptor bank");
        }

        return new ValidatedLayout(
            componentCount,
            effectCount,
            localWaveCount,
            flagsRaw,
            pointerBankAddressRaw,
            componentDataOffset,
            waveTableOffset);
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);

    private static int ReadInt32(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadInt32BigEndian(data[offset..]);

    private static ushort ReadUInt16(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidDataException(message);
    }

    private readonly record struct ValidatedLayout(
        int ComponentCount,
        int EffectCount,
        int LocalWaveCount,
        uint FlagsRaw,
        uint PointerBankAddressRaw,
        int ComponentDataOffset,
        int WaveTableOffset);
}

public sealed record N64SoundToolsFxComponent(
    int Index,
    int FxDataOffset,
    int DefaultPriority,
    IReadOnlyList<byte> OpaqueData);

public sealed record N64SoundToolsLocalWaveBinding(
    int LocalWaveIndex,
    ushort PointerWaveIndex);
