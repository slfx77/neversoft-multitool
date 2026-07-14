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
internal static class SkaFile
{
    internal const uint FlagPlatform = 1u << 28;
    // bit 26 = compressed-time keys (the decoders infer per-key timing from the
    // header/flag byte, so it isn't gated on); bit 25 = pre-rotated root
    // (neither is consumed by this parser)
    internal const uint FlagUseCompressTable = 1u << 23;
    internal const uint FlagHiResFramePointers = 1u << 22;
    internal const uint FlagPartialAnim = 1u << 19;
    internal const uint FlagObjectAnimData = 1u << 24;

    // THPS3 uses RenderWare rpHAnim instead of Neversoft's BonedAnim engine.
    // Discriminator: flags has bit 31 set, PLATFORM/USECOMPRESSTABLE clear.
    private const uint FlagThps3RpHAnim = 1u << 31;

    /// <summary>Quick check: does this look like a valid SKA file?</summary>
    internal static bool IsSkaFile(ReadOnlySpan<byte> data)
    {
        if (data.Length < 28) return false;
        if (SkaThawParser.IsThawSka(data, out _)) return true;
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

        if (SkaThawParser.IsThawSka(data, out var thawBigEndian))
        {
            var thawReader = new BinaryIO.EndianSpanReader(data, thawBigEndian);
            return new SkaProbeResult(thawReader.F32(8), data[0x0D]);
        }

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
        // THAW-era v0x28 container (LE PS2/PC, BE GC) — version-gated BEFORE
        // the flag dispatch because its flags also carry bit23/bit28 and the
        // THUG-era paths would silently mis-parse the reshaped header.
        if (SkaThawParser.IsThawSka(data, out var thawBigEndian))
            return SkaThawParser.ParseThaw(data, thawBigEndian, compressTable);

        // File header (12 bytes)
        var version = BitConverter.ToUInt32(data);
        var flags = BitConverter.ToUInt32(data[4..]);
        var duration = BitConverter.ToSingle(data[8..]);

        if ((flags & FlagUseCompressTable) != 0)
            return SkaCompressedParser.ParseCompressed(data, version, flags, duration, compressTable);
        if ((flags & FlagPlatform) != 0)
            return SkaPlatformParser.ParsePlatform(data, version, flags, duration);
        if ((flags & FlagThps3RpHAnim) != 0)
            return SkaThps3Parser.ParseThps3(data, version, flags, duration);

        throw new InvalidDataException(
            $"SKA: unrecognized flags 0x{flags:X8} (neither PLATFORM nor USECOMPRESSTABLE nor THPS3)");
    }

    /// <summary>
    ///     Reconstruct unit quaternion W from X,Y,Z (sign from signBit), then
    ///     conjugate: the engine's QuatVecToMatrix uses q* (matches
    ///     Ps2SkeletonFile so animation and skeleton share one convention).
    /// </summary>
    internal static Quaternion ReconstructQuat(float x, float y, float z, bool signBit)
    {
        // Components are integer multiples of 1/16384, so any nonzero record has
        // magnitude >= (1/16384)^2 ~ 3.7e-9; below that the record is the exact
        // zero sentinel -> canonical identity (never the signBit's (0,0,0,-1)).
        var lengthSq = x * x + y * y + z * z;
        if (lengthSq < 1e-9f)
            return Quaternion.Identity;

        var sum = 1f - lengthSq;
        var w = sum > 0 ? MathF.Sqrt(sum) : 0f;
        if (signBit) w = -w;
        return Quaternion.Conjugate(new Quaternion(x, y, z, w));
    }
}
