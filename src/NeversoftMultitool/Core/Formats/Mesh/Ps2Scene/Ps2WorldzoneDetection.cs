using NeversoftMultitool.Core.Formats.Archives;

namespace NeversoftMultitool.Core.Formats.Mesh.Ps2Scene;

/// <summary>
///     THAW worldzone PAK identification: the type-hash sentinels written by the
///     game's PAK assembler plus the cheap "is this PAK a worldzone?" probe used
///     by the GUI scanner and the CLI mesh command.
/// </summary>
public static class Ps2WorldzoneDetection
{
    public const uint WorldzoneMdlTypeHash = 0x9BCC234D; // QbKey(".mdl") — object MDL
    public const uint WorldzoneLevelMdlTypeHash = 0x7EA7357B; // THAW shell/CAP geometry chunk
    public const uint WorldzonePlacementTypeHash = 0x91E1028D;

    /// <summary>
    ///     Cheap check used by the GUI scanner to decide whether a .pak.ps2 file
    ///     should appear as a worldzone mesh entry. Requires PAK magic, at least
    ///     one object/level MDL entry, and a paired placement entry.
    /// </summary>
    public static bool IsWorldzonePak(string pakPath)
    {
        try
        {
            if (!PakArchive.IsPakArchive(pakPath))
                return false;
            var typed = PakArchive.GetTypedEntries(pakPath);
            var hasMdl = typed.Any(e =>
                e.TypeHash == WorldzoneMdlTypeHash || e.TypeHash == WorldzoneLevelMdlTypeHash);
            if (!hasMdl) return false;
            return typed.Any(e => e.TypeHash == WorldzonePlacementTypeHash);
        }
        catch
        {
            return false;
        }
    }
}
