using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Animation;

/// <summary>
///     Parser for Neversoft SKA animation files (THPS4/THUG/THUG2).
///     Format reference: THUG source Gfx/BonedAnim.cpp + BonedAnimTypes.h.
///     File layout (USECOMPRESSTABLE path — flags bit 23):
///     <code>
///     [File header]       12 bytes: version(u32) + flags(u32) + duration(float)
///     [Platform header]   16 bytes: numBones(u32) + numQKeys(u32) + numTKeys(u32) + numCustomKeys(u32)
///     [Alloc sizes]        8 bytes: qAllocSize(u32) + tAllocSize(u32)
///     [Per-bone Q sizes]  numBones × u16
///     [Per-bone T sizes]  numBones × u16
///     [4-byte alignment pad]
///     [Q keyframe data]   qAllocSize bytes (variable-length compressed keys)
///     [T keyframe data]   tAllocSize bytes (variable-length compressed keys)
///     </code>
///     File layout (PLATFORM path — flags bit 28):
///     <code>
///     [File header]       12 bytes
///     [Platform header]   16 bytes
///     [Per-bone frames]   numBones × 2 bytes (standard) or × 4 bytes (hi-res)
///     [4-byte alignment pad]
///     [Q keyframe data]   numQKeys × 8 bytes (standard) or × 14 bytes (hi-res)
///     [T keyframe data]   numTKeys × 8 bytes (standard) or × 14 bytes (hi-res)
///     </code>
/// </summary>
internal static partial class SkaFile
{
    private const uint FlagPlatform = 1u << 28;
    private const uint FlagCompressedTime = 1u << 26;
    private const uint FlagPreRotatedRoot = 1u << 25;
    private const uint FlagUseCompressTable = 1u << 23;
    private const uint FlagHiResFramePointers = 1u << 22;

    // THPS3 uses RenderWare rpHAnim instead of Neversoft's BonedAnim engine.
    // Discriminator: flags has bit 31 set, PLATFORM/USECOMPRESSTABLE clear.
    private const uint FlagThps3RpHAnim = 1u << 31;

    /// <summary>Quick check: does this look like a valid SKA file?</summary>
    internal static bool IsSkaFile(ReadOnlySpan<byte> data)
    {
        if (data.Length < 28) return false;
        var flags = BitConverter.ToUInt32(data[4..]);
        return (flags & FlagPlatform) != 0
               || (flags & FlagUseCompressTable) != 0
               || (flags & FlagThps3RpHAnim) != 0;
    }

    /// <summary>
    ///     Header-only probe for animation discovery. Returns duration and bone
    ///     count without decoding keyframes — does not require a compress table.
    ///     <see cref="SkaProbeResult.BoneCount" /> is null when unknown (THPS3,
    ///     where the count is implicit and only the full parser can derive it).
    /// </summary>
    internal static SkaProbeResult? TryProbe(ReadOnlySpan<byte> data)
    {
        if (!IsSkaFile(data)) return null;
        var flags = BitConverter.ToUInt32(data[4..]);
        var duration = BitConverter.ToSingle(data[8..]);

        if (((flags & FlagPlatform) != 0 || (flags & FlagUseCompressTable) != 0)
            && data.Length >= 16)
        {
            var numBones = (int)BitConverter.ToUInt32(data[12..]);
            return new SkaProbeResult(duration, numBones);
        }

        // THPS3 RpHAnim has no explicit bone count in the header; signal "unknown".
        return new SkaProbeResult(duration, null);
    }

    internal static SkaAnimation Parse(byte[] data, SkaCompressTable? compressTable = null)
    {
        return Parse((ReadOnlySpan<byte>)data, compressTable);
    }

    internal static SkaAnimation Parse(ReadOnlySpan<byte> data, SkaCompressTable? compressTable = null)
    {
        // File header (12 bytes)
        var version = BitConverter.ToUInt32(data);
        var flags = BitConverter.ToUInt32(data[4..]);
        var duration = BitConverter.ToSingle(data[8..]);

        if ((flags & FlagUseCompressTable) != 0)
            return ParseCompressed(data, version, flags, duration, compressTable);
        if ((flags & FlagPlatform) != 0)
            return ParsePlatform(data, version, flags, duration);
        if ((flags & FlagThps3RpHAnim) != 0)
            return ParseThps3(data, version, flags, duration);

        throw new InvalidDataException(
            $"SKA: unrecognized flags 0x{flags:X8} (neither PLATFORM nor USECOMPRESSTABLE nor THPS3)");
    }

    /// <summary>
    ///     Parse THPS3 PS2 SKA format (RenderWare rpHAnim variant).
    ///     File layout (verified on Bird_A_Flap 524 B + Crowd_A_CrowdClap 6844 B):
    ///     <code>
    ///     [File header]       28 bytes: version(u32) + flags(u32) + duration(f32)
    ///                                   + numQKeys(u32) + numTKeys(u32) + unk[2](u32)
    ///     [Pre-Q metadata]    12 bytes: reserved (possibly interpolation-scheme ID)
    ///     [Q keyframes]       (numQKeys − 1) × 24 B: prev(i32) + quat(4×f32) + time(f32)
    ///     [T keyframes]       numTKeys × 20 B: trans(3×f32) + time(f32) + prev(i32)
    ///     [Trailing pad]      4 bytes
    ///     </code>
    ///     T uses <c>prev-at-end</c>. numQKeys is always 1 greater than the
    ///     actual stored record count (RW allocates an extra slot at serialise
    ///     time). T <c>prev</c> is a byte offset back into the array, chaining
    ///     same-bone keys. A bone's first T key has <c>prev</c> set to a
    ///     per-file sentinel value (an uninitialised pointer from RW's writer).
    ///     Q <c>prev</c> does not identify runtime bone tracks; the game loads
    ///     Q records into non-root bone tracks using serialized time order.
    ///     Record strides and field offsets were confirmed against the THPS3
    ///     PS2 in-memory interpolator (FUN_00230f68 / FUN_00231048 at
    ///     SLUS_200.13 +0x230F68): 0x18 stride for Q, 0x14 stride for T,
    ///     Hamilton product composing quat.w via <c>pfVar[3]</c>.
    /// </summary>
    private static Quaternion ReconstructQuat(float x, float y, float z, bool signBit)
    {
        if (x == 0 && y == 0 && z == 0)
            return Quaternion.Identity;

        var sum = 1f - x * x - y * y - z * z;
        var w = sum > 0 ? MathF.Sqrt(sum) : 0f;
        if (signBit) w = -w;
        return Quaternion.Conjugate(new Quaternion(x, y, z, w));
    }

    private readonly record struct ThpsRawKey(float X, float Y, float Z, float W, float Time, int Prev, int RecIndex);

    private enum ThpsRecordKind
    {
        Q,
        T
    }
}
