namespace NeversoftMultitool.Core.Formats.Gba;

/// <summary>A common view of the collision grids used by the Vicarious Visions GBA games.</summary>
public interface IGbaCollisionGrid
{
    int Width { get; }
    int Height { get; }

    /// <summary>The surface/material index used to tint or classify one cell.</summary>
    int SurfaceAt(int x, int y);

    /// <summary>
    ///     Engine-computed absolute heights across a cell, in signed 20.12 fixed point.
    /// </summary>
    int[] SampleCell(ReadOnlySpan<byte> rom, int x, int y, int samples);
}
