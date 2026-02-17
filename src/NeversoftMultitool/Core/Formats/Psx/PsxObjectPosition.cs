namespace NeversoftMultitool.Core.Formats.Psx;

/// <summary>
/// World-space position for a DDM mesh object, extracted from a PSX file's Object Position section.
/// </summary>
public readonly record struct PsxObjectPosition(float X, float Y, float Z, ushort MeshIndex);

/// <summary>
/// Parses Object Position entries from PSX files to provide world-space placement for DDM objects.
/// </summary>
public static class PsxObjectPositionParser
{
    private const int EntrySize = 36;
    private const float FixedPointDivisor = 4096.0f;

    /// <summary>
    /// Parses all Object Position entries from a PSX file.
    /// Returns null if the file is not a valid PSX file.
    /// </summary>
    public static List<PsxObjectPosition>? ParsePositions(string psxFilePath)
    {
        using var stream = File.OpenRead(psxFilePath);
        using var reader = new BinaryReader(stream);

        var magic = reader.ReadBytes(4);
        if (!PsxLibrary.IsValidMagic(magic))
            return null;

        reader.ReadUInt32(); // ptrMeta — not needed for positions
        var objectCount = reader.ReadUInt32();

        var positions = new List<PsxObjectPosition>((int)objectCount);

        for (var i = 0; i < objectCount; i++)
        {
            var entry = reader.ReadBytes(EntrySize);

            // Int32 fixed-point positions at byte offsets 4, 8, 12
            var rawX = BitConverter.ToInt32(entry, 4);
            var rawY = BitConverter.ToInt32(entry, 8);
            var rawZ = BitConverter.ToInt32(entry, 12);

            // Mesh index at byte offset 22
            var meshIndex = BitConverter.ToUInt16(entry, 22);

            positions.Add(new PsxObjectPosition(
                rawX / FixedPointDivisor,
                rawY / FixedPointDivisor,
                rawZ / FixedPointDivisor,
                meshIndex));
        }

        return positions;
    }
}
