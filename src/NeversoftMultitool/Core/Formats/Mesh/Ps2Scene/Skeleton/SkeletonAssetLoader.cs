using NeversoftMultitool.Core.Formats;

namespace NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Skeleton;

/// <summary>
///     Parses a standalone skeleton uniformly from filesystem and archive-backed
///     sources. The compound entry name selects the PS2 layout; THAW's structural
///     discriminator remains authoritative for its endian-specific variants,
///     including Xbox and GameCube skeleton companions.
/// </summary>
internal static class SkeletonAssetLoader
{
    public static bool IsSkeletonFileName(string name) =>
        name.EndsWith(".ske", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".ske.ps2", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".ske.ngc", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".ske.xbx", StringComparison.OrdinalIgnoreCase);

    public static Ps2Skeleton Load(AssetSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Parse(source.EntryName, source.ReadBytes());
    }

    public static Ps2Skeleton Parse(string entryName, byte[] data)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryName);
        ArgumentNullException.ThrowIfNull(data);

        if (!IsSkeletonFileName(entryName))
            throw new InvalidDataException(
                $"'{entryName}' is not a supported skeleton file " +
                "(.ske, .ske.ps2, .ske.ngc, or .ske.xbx).");

        if (ThawSkeletonFile.IsThawSkeleton(data))
            return ThawSkeletonFile.Parse(data);

        return entryName.EndsWith(".ske.ps2", StringComparison.OrdinalIgnoreCase)
            ? Ps2SkeletonFile.Parse(data)
            : SkeletonFile.Parse(data);
    }
}
