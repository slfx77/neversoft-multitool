namespace NeversoftMultitool.Core.Formats.Audio;

/// <summary>
///     Conservatively resolves the initial local-wave operand used by the
///     recognized BFX component prefixes. This does not execute or scan the
///     remaining opaque component bytecode.
/// </summary>
public static class N64SoundToolsFxInitialWaveResolver
{
    public const string LeadingOpcode81Basis = "leadingOpcode81";
    public const string LeadingOpcode95OneByteThen81Basis =
        "leadingOpcode95OneByteThen81";

    /// <summary>
    ///     Resolves a recognized initial component operand through the BFX
    ///     local-wave table to its exact descriptor in the supplied PTR bank.
    ///     Unknown, truncated, or out-of-range prefixes remain unresolved.
    /// </summary>
    public static N64SoundToolsFxInitialWaveBinding? Resolve(
        N64SoundToolsFxBank bank,
        N64SoundToolsPointerBank pointerBank,
        N64SoundToolsFxComponent component)
    {
        ArgumentNullException.ThrowIfNull(bank);
        ArgumentNullException.ThrowIfNull(pointerBank);
        ArgumentNullException.ThrowIfNull(component);

        var data = component.OpaqueData;
        var operandOffset = 0;
        string basis;
        if (data.Count >= 1 && data[0] == 0x81)
        {
            operandOffset = 1;
            basis = LeadingOpcode81Basis;
        }
        else if (data.Count >= 3 && data[0] == 0x95 && data[2] == 0x81)
        {
            operandOffset = 3;
            basis = LeadingOpcode95OneByteThen81Basis;
        }
        else
        {
            return null;
        }

        if (!TryReadPackedIndex(data, operandOffset, out var localWaveIndex,
                out var encodedLength))
        {
            return null;
        }

        if ((uint)localWaveIndex >= (uint)bank.LocalWaveMap.Count)
            return null;
        var localBinding = bank.LocalWaveMap[localWaveIndex];
        if (localBinding.LocalWaveIndex != localWaveIndex ||
            localBinding.PointerWaveIndex >= pointerBank.Waves.Count)
        {
            return null;
        }

        var pointerDescriptor = pointerBank.Waves[localBinding.PointerWaveIndex];
        if (pointerDescriptor.Index != localBinding.PointerWaveIndex)
            return null;

        return new N64SoundToolsFxInitialWaveBinding(
            component.Index,
            basis,
            operandOffset + encodedLength,
            localWaveIndex,
            localBinding.PointerWaveIndex,
            pointerDescriptor);
    }

    private static bool TryReadPackedIndex(
        IReadOnlyList<byte> data,
        int offset,
        out int value,
        out int encodedLength)
    {
        value = 0;
        encodedLength = 0;
        if ((uint)offset >= (uint)data.Count)
            return false;

        var first = data[offset];
        if ((first & 0x80) == 0)
        {
            value = first;
            encodedLength = 1;
            return true;
        }

        if (offset + 1 >= data.Count)
            return false;

        value = ((first & 0x7F) << 8) | data[offset + 1];
        encodedLength = 2;
        return true;
    }
}

public sealed record N64SoundToolsFxInitialWaveBinding(
    int ComponentIndex,
    string Basis,
    int PrefixByteLength,
    int LocalWaveIndex,
    ushort PointerWaveIndex,
    N64SoundToolsWaveDescriptor PointerWaveDescriptor);
