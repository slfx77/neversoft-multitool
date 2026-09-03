using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Collision;

/// <summary>A single collision object (sector) with its own geometry.</summary>
public sealed class ColObject
{
    public required uint Checksum { get; init; }
    public required ushort Flags { get; init; }
    public required Vector3 BBoxMin { get; init; }
    public required Vector3 BBoxMax { get; init; }
    public required Vector3[] Vertices { get; init; }
    public required ColFace[] Faces { get; init; }
    public required byte[] Intensities { get; init; }

    /// <summary>
    ///     Optional packed RGBA vertex colours. THPS4 v8 stores these four bytes
    ///     inline after each float position; later COL versions instead carry one
    ///     grayscale intensity byte in a separate array.
    /// </summary>
    public byte[] VertexColorsRgba { get; init; } = [];
}
