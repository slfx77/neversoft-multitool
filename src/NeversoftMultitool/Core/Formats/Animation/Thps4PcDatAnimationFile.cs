namespace NeversoftMultitool.Core.Formats.Animation;

/// <summary>
///     Strict classifier and parser gate for Aspyr THPS4 Windows animations.
///     Their delimiter-free names end in <c>ska.dat</c>, for example
///     <c>Walkska.dat</c>, while the payload remains the little-endian
///     version-1 THPS4 SKA layout.
/// </summary>
public static class Thps4PcDatAnimationFile
{
    public const string Suffix = "ska.dat";

    private const uint CompressedFlags = 0x06800000;
    private const uint CameraFlags = 0x1E000000;
    private const uint CameraWideCountsFlags = 0x1E400000;
    private const uint ObjectWideCountsFlags = 0x17400000;

    // Discovery must prove the complete compressed-key grammar without turning
    // table lookup values into exported data. A zero-valued table is sufficient
    // for structural validation; actual conversion still requires the build's
    // standardkeyQ.bin and standardkeyT.bin through SkaCommand.FindCompressTable.
    private static readonly SkaCompressTable ValidationTable = new()
    {
        Q48 = new SkaCompressEntry[256],
        T48 = new SkaCompressEntry[256]
    };

    /// <summary>
    ///     Matches only the delimiter-free PC spelling. Conventional
    ///     <c>name.ska.dat</c> files are deliberately outside this family.
    /// </summary>
    public static bool IsCandidateFileName(string fileName)
    {
        var name = Path.GetFileName(fileName);
        return name.Length > Suffix.Length
               && name.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase)
               && !name.EndsWith(".ska.dat", StringComparison.OrdinalIgnoreCase);
    }

    internal static SkaProbeResult? TryProbeExact(ReadOnlySpan<byte> data)
    {
        try
        {
            var animation = ParseExact(data, ValidationTable);
            return new SkaProbeResult(animation.Duration, animation.BoneTracks.Length);
        }
        catch (Exception exception) when (exception is InvalidDataException
                                          or ArgumentException
                                          or OverflowException
                                          or IndexOutOfRangeException)
        {
            return null;
        }
    }

    internal static SkaAnimation ParseExact(
        ReadOnlySpan<byte> data,
        SkaCompressTable? compressTable = null)
    {
        if (data.Length < 28)
            throw new InvalidDataException("THPS4 PC SKA: header is truncated");

        var version = BitConverter.ToUInt32(data);
        if (version != 1)
            throw new InvalidDataException($"THPS4 PC SKA: version {version} is not the expected version 1");

        var flags = BitConverter.ToUInt32(data[4..]);
        if (flags is not (CompressedFlags or CameraFlags or CameraWideCountsFlags or ObjectWideCountsFlags))
        {
            throw new InvalidDataException(
                $"THPS4 PC SKA: unsupported flag combination 0x{flags:X8}");
        }

        var duration = BitConverter.ToSingle(data[8..]);
        if (!float.IsFinite(duration) || duration < 0f)
            throw new InvalidDataException($"THPS4 PC SKA: invalid duration {duration}");

        var animation = SkaFile.ParseLegacyExact(data, compressTable);
        if (animation.Version != version || animation.Flags != flags)
            throw new InvalidDataException("THPS4 PC SKA: parsed header does not match the source header");

        return animation;
    }
}
