using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     PSX-era per-vertex UV motion retained alongside the static, frame-zero UV.
///     Velocities and frequency use the game's native fixed-point contract;
///     amplitudes are expressed in texels and phases select one of 16 quarter-
///     turn offsets in the 64-sample sine table.
/// </summary>
public readonly record struct ModelTextureWibble(
    int UVelocity,
    int VVelocity,
    int Frequency,
    byte UAmplitude,
    byte UPhase,
    byte VAmplitude,
    byte VPhase,
    int TextureWidth,
    int TextureHeight)
{
    internal static ModelTextureWibble? FromFace(
        ushort version,
        PsxFace face,
        int slot,
        (int Width, int Height) textureDimensions)
    {
        var wibble = face.TextureWibble;
        if (wibble == null || (uint)slot >= (uint)wibble.Vertices.Length)
            return null;

        var vertex = wibble.Vertices[slot];
        // The PC v6 renderer stores its base UVs in a fixed 512-coordinate
        // space and doubles only the scrolling term before converting the
        // 8.8 fixed-point result (SpideyPC.exe 0x0047619F-0x00476259).
        // Using the decoded texture's much smaller width/height here made the
        // live preview move several times too fast.
        var usesWidenedCoordinates = version == 0x06;
        return new ModelTextureWibble(
            usesWidenedCoordinates ? wibble.UVelocity * 2 : wibble.UVelocity,
            usesWidenedCoordinates ? wibble.VVelocity * 2 : wibble.VVelocity,
            wibble.Frequency,
            wibble.ZeroUAmplitudes ? (byte)0 : (byte)(vertex.UAmplitudePhase >> 4),
            (byte)(vertex.UAmplitudePhase & 0x0F),
            wibble.ZeroVAmplitudes ? (byte)0 : (byte)(vertex.VAmplitudePhase >> 4),
            (byte)(vertex.VAmplitudePhase & 0x0F),
            usesWidenedCoordinates ? 512 : Math.Max(textureDimensions.Width, 1),
            usesWidenedCoordinates ? 512 : Math.Max(textureDimensions.Height, 1));
    }
}
