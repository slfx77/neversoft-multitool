namespace NeversoftMultitool.Core.Rendering;

/// <summary>
///     All renderable geometry loaded from a GLB file with bounding box.
/// </summary>
internal sealed class RenderScene
{
    public List<RenderSubmesh> Submeshes { get; } = [];
    public float MinX { get; set; } = float.MaxValue;
    public float MinY { get; set; } = float.MaxValue;
    public float MinZ { get; set; } = float.MaxValue;
    public float MaxX { get; set; } = float.MinValue;
    public float MaxY { get; set; } = float.MinValue;
    public float MaxZ { get; set; } = float.MinValue;

    public float Width => MaxX - MinX;
    public float Height => MaxY - MinY;
    public float Depth => MaxZ - MinZ;
    public bool HasGeometry => Submeshes.Count > 0;

    public void ExpandBounds(float[] positions)
    {
        for (var i = 0; i < positions.Length; i += 3)
        {
            var x = positions[i];
            var y = positions[i + 1];
            var z = positions[i + 2];
            if (x < MinX) MinX = x;
            if (y < MinY) MinY = y;
            if (z < MinZ) MinZ = z;
            if (x > MaxX) MaxX = x;
            if (y > MaxY) MaxY = y;
            if (z > MaxZ) MaxZ = z;
        }
    }
}
