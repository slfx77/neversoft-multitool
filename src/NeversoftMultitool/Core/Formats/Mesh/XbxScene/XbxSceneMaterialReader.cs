using System.Numerics;

namespace NeversoftMultitool.Core.Formats.XbxScene;

internal static class XbxSceneMaterialReader
{
    private const uint MatflagUvWibble = 1 << 0;
    private const uint MatflagVcWibble = 1 << 1;
    private const uint MatflagPassTextureAnimates = 1 << 11;

    public static XbxMaterial ReadMaterial(BinaryReader r)
    {
        var checksum = r.ReadUInt32();
        var nameChecksum = r.ReadUInt32();
        var numPasses = r.ReadInt32();
        var alphaCutoff = r.ReadInt32();
        var sorted = r.ReadByte() != 0;
        var drawOrder = r.ReadSingle();
        var singleSided = r.ReadByte() != 0;
        var noBfc = r.ReadByte() != 0;
        var zbias = r.ReadInt32();
        var grassify = r.ReadByte() != 0;

        float grassHeight = 0;
        var grassLayers = 0;
        if (grassify)
        {
            grassHeight = r.ReadSingle();
            grassLayers = r.ReadInt32();
        }

        var specularPower = r.ReadSingle();
        var specularColor = Vector3.Zero;
        if (specularPower > 0)
            specularColor = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

        var passes = new XbxPass[numPasses];
        for (var passIndex = 0; passIndex < numPasses; passIndex++)
            passes[passIndex] = ReadPass(r, passIndex);

        return new XbxMaterial
        {
            Checksum = checksum,
            NameChecksum = nameChecksum,
            NumPasses = numPasses,
            AlphaCutoff = alphaCutoff,
            Sorted = sorted,
            DrawOrder = drawOrder,
            SingleSided = singleSided,
            NoBfc = noBfc,
            ZBias = zbias,
            Grassify = grassify,
            GrassHeight = grassHeight,
            GrassLayers = grassLayers,
            SpecularPower = specularPower,
            SpecularColor = specularColor,
            Passes = passes
        };
    }

    private static XbxPass ReadPass(BinaryReader r, int passIndex)
    {
        var texChecksum = r.ReadUInt32();
        var flags = r.ReadUInt32();
        var hasColor = r.ReadByte() != 0;
        var color = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

        var regAlpha = r.ReadUInt64();
        var blendMode = (uint)(regAlpha & 0x00FFFFFFUL);
        var fixedAlpha = (uint)(regAlpha >> 32);

        var uAddressing = r.ReadUInt32();
        var vAddressing = r.ReadUInt32();
        var envmapTiling = new Vector2(r.ReadSingle(), r.ReadSingle());
        var filteringMode = r.ReadUInt32();

        if ((flags & MatflagUvWibble) != 0)
            r.BaseStream.Position += 32;

        if (passIndex == 0 && (flags & MatflagVcWibble) != 0)
        {
            var numSeqs = r.ReadInt32();
            for (var seq = 0; seq < numSeqs; seq++)
            {
                var numKeys = r.ReadInt32();
                r.ReadInt32();
                r.BaseStream.Position += numKeys * 8;
            }
        }

        if ((flags & MatflagPassTextureAnimates) != 0)
        {
            var numKeyframes = r.ReadInt32();
            r.ReadInt32();
            r.ReadInt32();
            r.ReadInt32();
            r.BaseStream.Position += numKeyframes * 8;
        }

        r.BaseStream.Position += 16;

        return new XbxPass
        {
            TextureChecksum = texChecksum,
            Flags = flags,
            HasColor = hasColor,
            Color = color,
            BlendMode = blendMode,
            FixedAlpha = fixedAlpha,
            UAddressing = uAddressing,
            VAddressing = vAddressing,
            EnvmapTiling = envmapTiling,
            FilteringMode = filteringMode
        };
    }
}
