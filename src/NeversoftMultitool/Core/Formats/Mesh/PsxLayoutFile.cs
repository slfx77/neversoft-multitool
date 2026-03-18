namespace NeversoftMultitool.Core.Formats.Mesh;

/// <summary>
///     Minimal PSX layout parser for DDM world placement.
///     Reads only object positions and mesh name hashes — no mesh geometry.
///     Completely isolated from PsxMeshFile to prevent PS1 mesh conversion
///     changes from breaking DDM level assembly.
///     Position conversion: raw 20.12 fixed-point → float via raw/4096.
///     No additional scaling (TranslationDivisor, WorldScale, etc.).
/// </summary>
public sealed class PsxLayoutFile
{
    public required List<PsxLayoutObject> Objects { get; init; }
    public required uint[] MeshNameHashes { get; init; }

    /// <summary>
    ///     Parses a PSX file for layout data (object positions + mesh name hashes).
    ///     Returns null if the file is invalid or has no objects.
    /// </summary>
    public static PsxLayoutFile? Parse(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        using var reader = new BinaryReader(stream);

        var version = reader.ReadUInt16();
        if (version is not (0x03 or 0x04 or 0x06))
            return null;

        var magic = reader.ReadUInt16();
        if (magic != 0x0002)
            return null;

        var metaTop = reader.ReadUInt32();
        var objectCount = reader.ReadUInt32();

        if (objectCount == 0)
            return null;

        // Read objects (36 bytes each)
        var objects = new List<PsxLayoutObject>((int)objectCount);
        for (uint i = 0; i < objectCount; i++)
        {
            var flags = reader.ReadUInt32();
            var rawX = reader.ReadInt32();
            var rawY = reader.ReadInt32();
            var rawZ = reader.ReadInt32();
            reader.ReadUInt32(); // unk1
            reader.ReadUInt16(); // unk2
            var meshIndex = reader.ReadUInt16();
            reader.ReadInt16(); // tx
            reader.ReadInt16(); // ty
            reader.ReadUInt32(); // unk3
            reader.ReadUInt32(); // paletteTop

            objects.Add(new PsxLayoutObject
            {
                Flags = flags,
                RawX = rawX,
                RawY = rawY,
                RawZ = rawZ,
                MeshIndex = meshIndex
            });
        }

        var meshCount = reader.ReadUInt32();
        if (meshCount == 0)
            return null;

        // Skip mesh top pointers (we don't need geometry offsets)
        reader.BaseStream.Seek(meshCount * 4, SeekOrigin.Current);

        // Seek to tagged chunks at metaTop, skip them to reach mesh name hashes
        reader.BaseStream.Seek(metaTop, SeekOrigin.Begin);
        SkipTaggedChunks(reader);

        // Read mesh name hashes
        var meshNameHashes = new uint[meshCount];
        for (uint i = 0; i < meshCount; i++)
        {
            meshNameHashes[i] = reader.ReadUInt32();
        }

        return new PsxLayoutFile
        {
            Objects = objects,
            MeshNameHashes = meshNameHashes
        };
    }

    /// <summary>
    ///     Skips tagged chunks (HIER, RGBs, etc.) until the stop sentinel.
    /// </summary>
    private static void SkipTaggedChunks(BinaryReader reader)
    {
        const uint TagStop = 0xFFFFFFFF;

        var tag = reader.ReadUInt32();
        while (tag != TagStop)
        {
            var length = reader.ReadUInt32();
            reader.BaseStream.Seek(length, SeekOrigin.Current);
            tag = reader.ReadUInt32();
        }
    }
}
