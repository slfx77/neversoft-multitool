using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Mesh.XbxScene;

public sealed class XbxPass
{
    public uint TextureChecksum { get; init; }
    public uint Flags { get; init; }
    public bool HasColor { get; init; }
    public Vector3 Color { get; init; }
    public uint BlendMode { get; init; }
    public uint FixedAlpha { get; init; }
    public uint UAddressing { get; init; }
    public uint VAddressing { get; init; }
    public Vector2 EnvmapTiling { get; init; }
    public uint FilteringMode { get; init; }

    /// <summary>
    ///     The 4th component of the pass colour as serialized by THAW PC
    ///     (ThawSceneMeshSupport). The record mirrors the engine's in-memory
    ///     material, where m_color[pass][3] = fixed_alpha / 128 (THUG
    ///     material.cpp:671). It is retained as the candidate FixedAlpha source
    ///     for the *_FIXED blend modes. THUG2 records read by
    ///     XbxSceneMaterialReader store zero here and carry FixedAlpha directly.
    /// </summary>
    public float ColorW { get; init; }

    /// <summary>
    ///     The 16 bits following the u16 blend mode in the THAW PC pass record.
    ///     Corpus observations found zero in this field, consistent with the
    ///     high half of a 32-bit blend-mode value; it remains exposed in case a
    ///     build uses it as a packed second value.
    /// </summary>
    public short BlendModeExtra { get; init; }
}
