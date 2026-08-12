namespace NeversoftMultitool.Core.Formats.Audio;

/// <summary>
///     Conservatively resolves the interpreter-proven initial event at byte
///     zero of a BFX component. This recognizes one fixed grammar after an
///     already validated initial-wave binding and never scans or executes the
///     remaining component bytes.
/// </summary>
public static class N64SoundToolsFxInitialEventResolver
{
    public const string InterpreterProvenInitialEventBasis =
        "interpreterProvenInitialEvent";
    public const string NoteKind = "note";
    public const string RestKind = "rest";
    public const string FiniteLengthMode = "finite";
    public const string IndefiniteLengthMode = "indefinite";

    private const byte RestNoteValue = 0x60;
    private const int IndefiniteLength = 0x7FFF;

    /// <summary>
    ///     Resolves only <c>84 env[7] 9C pan A6 volume note packed-length</c>
    ///     beginning at the existing binding's exact exclusive prefix length.
    ///     A wrong token, a truncated operand, or a note token with its high bit
    ///     set leaves the initial event unresolved.
    /// </summary>
    public static N64SoundToolsFxInitialEvent? Resolve(
        N64SoundToolsFxBank bank,
        N64SoundToolsPointerBank pointerBank,
        N64SoundToolsFxComponent component)
    {
        ArgumentNullException.ThrowIfNull(bank);
        ArgumentNullException.ThrowIfNull(pointerBank);
        ArgumentNullException.ThrowIfNull(component);
        var initialWaveBinding = N64SoundToolsFxInitialWaveResolver.Resolve(
            bank, pointerBank, component);
        if (initialWaveBinding == null ||
            initialWaveBinding.ComponentIndex != component.Index)
        {
            return null;
        }

        var data = component.OpaqueData;
        var offset = initialWaveBinding.PrefixByteLength;
        if (!TryConsume(data, ref offset, 0x84) ||
            !TryReadByte(data, ref offset, out var speedRaw) ||
            !TryReadByte(data, ref offset, out var initialVolumeRaw) ||
            !TryReadByte(data, ref offset, out var attackSpeedRaw) ||
            !TryReadByte(data, ref offset, out var peakVolumeRaw) ||
            !TryReadByte(data, ref offset, out var decaySpeedRaw) ||
            !TryReadByte(data, ref offset, out var sustainVolumeRaw) ||
            !TryReadByte(data, ref offset, out var releaseSpeedRaw) ||
            !TryConsume(data, ref offset, 0x9C) ||
            !TryReadByte(data, ref offset, out var panOperandRaw) ||
            !TryConsume(data, ref offset, 0xA6) ||
            !TryReadByte(data, ref offset, out var volumeOperandRaw) ||
            !TryReadByte(data, ref offset, out var noteValueRaw) ||
            (noteValueRaw & 0x80) != 0 ||
            !TryReadPacked15(data, ref offset, out var lengthRaw,
                out var lengthEncodingByteLength))
        {
            return null;
        }

        byte? leadingLoopCountRaw = initialWaveBinding.Basis switch
        {
            N64SoundToolsFxInitialWaveResolver.LeadingOpcode81Basis => null,
            N64SoundToolsFxInitialWaveResolver.LeadingOpcode95OneByteThen81Basis
                when data.Count >= 2 => data[1],
            _ => null
        };
        if (initialWaveBinding.Basis != N64SoundToolsFxInitialWaveResolver.LeadingOpcode81Basis &&
            leadingLoopCountRaw == null)
        {
            return null;
        }

        return new N64SoundToolsFxInitialEvent(
            component.Index,
            InterpreterProvenInitialEventBasis,
            offset,
            offset,
            leadingLoopCountRaw,
            new N64SoundToolsFxEnvelope(
                speedRaw,
                initialVolumeRaw,
                attackSpeedRaw,
                peakVolumeRaw,
                decaySpeedRaw,
                sustainVolumeRaw,
                releaseSpeedRaw),
            panOperandRaw,
            (byte)(panOperandRaw >> 1),
            volumeOperandRaw,
            noteValueRaw,
            noteValueRaw == RestNoteValue ? RestKind : NoteKind,
            lengthRaw,
            lengthEncodingByteLength,
            lengthRaw == IndefiniteLength ? IndefiniteLengthMode : FiniteLengthMode);
    }

    private static bool TryConsume(IReadOnlyList<byte> data, ref int offset, byte expected)
    {
        if (!TryReadByte(data, ref offset, out var actual) || actual != expected)
            return false;
        return true;
    }

    private static bool TryReadByte(
        IReadOnlyList<byte> data,
        ref int offset,
        out byte value)
    {
        value = 0;
        if ((uint)offset >= (uint)data.Count)
            return false;
        value = data[offset++];
        return true;
    }

    private static bool TryReadPacked15(
        IReadOnlyList<byte> data,
        ref int offset,
        out int value,
        out int encodedLength)
    {
        value = 0;
        encodedLength = 0;
        if (!TryReadByte(data, ref offset, out var first))
            return false;

        if ((first & 0x80) == 0)
        {
            value = first;
            encodedLength = 1;
            return true;
        }

        if (!TryReadByte(data, ref offset, out var second))
            return false;
        value = ((first & 0x7F) << 8) | second;
        encodedLength = 2;
        return true;
    }
}

/// <summary>
///     Classifies only the exact suffixes proven for canonical initial events.
///     Unrecognized or additional bytes remain opaque and unresolved.
/// </summary>
public static class N64SoundToolsFxContinuationResolver
{
    public const string StopAfterFiniteEventClassification =
        "stopAfterFiniteEvent";
    public const string StopUnreachableWhileIndefiniteClassification =
        "stopUnreachableWhileIndefinite";
    public const string InfiniteRepeatClassification = "infiniteRepeat";

    public static N64SoundToolsFxContinuation? Resolve(
        N64SoundToolsFxBank bank,
        N64SoundToolsPointerBank pointerBank,
        N64SoundToolsFxComponent component)
    {
        ArgumentNullException.ThrowIfNull(bank);
        ArgumentNullException.ThrowIfNull(pointerBank);
        ArgumentNullException.ThrowIfNull(component);
        var initialEvent = N64SoundToolsFxInitialEventResolver.Resolve(
            bank, pointerBank, component);
        if (initialEvent == null || initialEvent.ComponentIndex != component.Index)
            return null;

        var data = component.OpaqueData;
        var offset = initialEvent.EndOffset;
        if ((uint)offset > (uint)data.Count)
            return null;
        var remaining = data.Count - offset;
        if (initialEvent.LeadingLoopCountRaw == null &&
            remaining == 1 && data[offset] == 0x80)
        {
            return new N64SoundToolsFxContinuation(
                StopClassification(initialEvent),
                1,
                null,
                null);
        }

        if (initialEvent.LeadingLoopCountRaw == null &&
            remaining == 2 && data[offset] == 0x80 && data[offset + 1] == 0xE2)
        {
            return new N64SoundToolsFxContinuation(
                StopClassification(initialEvent),
                1,
                offset + 1,
                new byte[] { 0xE2 });
        }

        if (remaining == 2 &&
            initialEvent.LeadingLoopCountRaw == 0xFF &&
            data[offset] == 0x96 &&
            data[offset + 1] == 0x80)
        {
            return new N64SoundToolsFxContinuation(
                InfiniteRepeatClassification,
                2,
                null,
                null);
        }

        return null;
    }

    private static string StopClassification(N64SoundToolsFxInitialEvent initialEvent) =>
        initialEvent.LengthMode == N64SoundToolsFxInitialEventResolver.IndefiniteLengthMode
            ? StopUnreachableWhileIndefiniteClassification
            : StopAfterFiniteEventClassification;
}

public sealed record N64SoundToolsFxInitialEvent(
    int ComponentIndex,
    string Basis,
    int EncodedByteLength,
    int EndOffset,
    byte? LeadingLoopCountRaw,
    N64SoundToolsFxEnvelope Envelope,
    byte PanOperandRaw,
    byte RuntimePan,
    byte VolumeOperandRaw,
    byte NoteValueRaw,
    string NoteKind,
    int LengthRaw,
    int LengthEncodingByteLength,
    string LengthMode);

public sealed record N64SoundToolsFxEnvelope(
    byte SpeedRaw,
    byte InitialVolumeRaw,
    byte AttackSpeedRaw,
    byte PeakVolumeRaw,
    byte DecaySpeedRaw,
    byte SustainVolumeRaw,
    byte ReleaseSpeedRaw);

public sealed record N64SoundToolsFxContinuation(
    string Classification,
    int RecognizedByteLength,
    int? UninterpretedAfterStopOffset,
    IReadOnlyList<byte>? UninterpretedAfterStopRaw);
