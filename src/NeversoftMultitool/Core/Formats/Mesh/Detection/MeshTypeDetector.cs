using NeversoftMultitool.Core.Formats.Mesh.Conversion;

namespace NeversoftMultitool.Core.Formats.Mesh.Detection;

/// <summary>
///     Single home for mesh extension and type detection. Folds the previously
///     duplicated suffix lists and classification switches (FormatProbeMesh,
///     MeshCommand, MeshConverterTabFileScanner, MeshConverterTabScanAnalysis and
///     the per-command filters) so every consumer answers "what kind of mesh is
///     this?" the same way. Mirrors <see cref="Archives.ArchiveTypeDetector" />.
/// </summary>
public static class MeshTypeDetector
{
    /// <summary>
    ///     Carved N64 model bundles (<c>models/NNN/NNN_&lt;name&gt;.psx.n64</c>).
    ///     The file name carries the bundle slot and, where content identified
    ///     it, the original PS1 file name — see <see cref="GetN64BundleStem" />.
    /// </summary>
    public const string N64ModelSuffix = ".psx.n64";

    /// <summary>THAW PS2 worldzone PAK. Deliberately not a generic mesh candidate.</summary>
    public const string WorldzoneSuffix = ".pak.ps2";

    private static readonly string[] XboxSceneSuffixes =
    [
        ".skin.xbx", ".mdl.xbx", ".scn.xbx",
        ".skin.wpc", ".mdl.wpc", ".scn.wpc",
        ".skin.ngc", ".mdl.ngc", ".scn.ngc"
    ];

    private static readonly string[] Ps2SceneSuffixes = [".iskin.ps2", ".skin.ps2", ".mdl.ps2"];

    private static readonly string[] CollisionSuffixes = [".col.xbx", ".col.wpc", ".col.ps2", ".col.psp", ".col"];

    private static readonly string[] RenderWareDffSuffixes = [".skn", ".dff"];

    private static readonly string[] AmbiguousSceneSuffixes = [".skin", ".mdl"];

    /// <summary>
    ///     Every suffix any mesh parser claims, longest-first so that
    ///     <see cref="MatchSuffix" /> and <see cref="GetStem" /> prefer the
    ///     compound form (".iskin.ps2" over ".skin.ps2" over ".skin").
    /// </summary>
    public static readonly string[] KnownSuffixes =
    [
        .. XboxSceneSuffixes,
        .. Ps2SceneSuffixes,
        ".geom.ps2",
        WorldzoneSuffix,
        .. CollisionSuffixes,
        N64ModelSuffix,
        ".ddm",
        ".psx",
        .. RenderWareDffSuffixes,
        ".bsp",
        .. AmbiguousSceneSuffixes
    ];

    /// <summary>
    ///     The longest known suffix the name ends with, or null. Longest-match so
    ///     "a.skin.ps2" resolves to ".skin.ps2" rather than ".skin".
    /// </summary>
    public static string? MatchSuffix(string fileName)
    {
        string? best = null;
        foreach (var suffix in KnownSuffixes)
        {
            if (!fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                continue;
            if (best == null || suffix.Length > best.Length)
                best = suffix;
        }

        return best;
    }

    /// <summary>
    ///     Cheap name-only gate for directory walks and archive entry walks: could
    ///     any mesh parser own this? Worldzone PAKs are excluded so archive
    ///     enumeration keeps falling through to nested-archive opening — ask
    ///     <see cref="IsWorldzoneCandidate" /> separately.
    /// </summary>
    public static bool IsMeshCandidate(string fileName)
    {
        var suffix = MatchSuffix(fileName);
        return suffix != null && !string.Equals(suffix, WorldzoneSuffix, StringComparison.Ordinal);
    }

    /// <summary>THAW PS2 worldzone PAK by name. Content is gated by the caller.</summary>
    public static bool IsWorldzoneCandidate(string fileName)
    {
        return fileName.EndsWith(WorldzoneSuffix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Object DDMs (<c>*_o.ddm</c>) are placement companions of a level DDM,
    ///     not standalone meshes. The GUI scanner skips them.
    /// </summary>
    public static bool IsObjectDdm(string fileName)
    {
        return fileName.EndsWith(".ddm", StringComparison.OrdinalIgnoreCase)
               && Path.GetFileNameWithoutExtension(fileName)
                   .EndsWith("_o", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Name-only classification. Bare <c>.skin</c>/<c>.mdl</c> come back with
    ///     <see cref="MeshFileRoute.RequiresContentProbe" /> set and no kind —
    ///     they need <see cref="DetectFromBytes" />.
    /// </summary>
    public static MeshFileRoute DetectByName(string fileName)
    {
        var suffix = MatchSuffix(fileName);
        if (suffix == null)
            return MeshFileRoute.NotAMesh(Path.GetExtension(fileName));

        if (XboxSceneSuffixes.Contains(suffix))
            return new MeshFileRoute(MeshFileKind.XbxScene, suffix, RequiresContentProbe: true);

        if (Ps2SceneSuffixes.Contains(suffix))
            return new MeshFileRoute(MeshFileKind.Ps2Scene, suffix, RequiresContentProbe: true);

        if (CollisionSuffixes.Contains(suffix))
            return new MeshFileRoute(MeshFileKind.Collision, suffix, RequiresContentProbe: true);

        if (RenderWareDffSuffixes.Contains(suffix))
            return new MeshFileRoute(MeshFileKind.RenderWareDff, suffix, RequiresContentProbe: true);

        return suffix switch
        {
            ".geom.ps2" => new MeshFileRoute(
                MeshFileKind.Ps2Geom, suffix, Ps2SceneSubFormat.Geom, DisplayFormat: "PS2 GEOM"),
            WorldzoneSuffix => new MeshFileRoute(
                MeshFileKind.Ps2Worldzone, suffix, Ps2SceneSubFormat.PakWorldzone,
                RequiresContentProbe: true, DisplayFormat: "THAW PS2 Worldzone"),
            N64ModelSuffix => new MeshFileRoute(MeshFileKind.N64Model, suffix, DisplayFormat: "N64 Model"),
            ".ddm" => new MeshFileRoute(MeshFileKind.Ddm, suffix, DisplayFormat: "DDM Mesh"),
            ".psx" => new MeshFileRoute(MeshFileKind.Psx, suffix, RequiresContentProbe: true),
            ".bsp" => new MeshFileRoute(MeshFileKind.RenderWareBsp, suffix, RequiresContentProbe: true),
            ".skin" or ".mdl" => new MeshFileRoute(MeshFileKind.None, suffix, RequiresContentProbe: true),
            _ => MeshFileRoute.NotAMesh(Path.GetExtension(fileName))
        };
    }

    /// <summary>
    ///     How many header bytes <see cref="DetectFromBytes" /> needs to reach the
    ///     same verdict it would on the whole file. Pinned by a corpus test —
    ///     a shorter buffer can only downgrade a route to
    ///     <see cref="MeshFileKind.None" />, never yield a wrong kind.
    /// </summary>
    public static int GetProbeByteBudget(string fileName)
    {
        var suffix = MatchSuffix(fileName);
        if (suffix == null)
            return 4;

        if (string.Equals(suffix, ".mdl", StringComparison.Ordinal))
            return 256 * 1024;
        if (string.Equals(suffix, ".skin", StringComparison.Ordinal))
            return 64 * 1024;
        if (Ps2SceneSuffixes.Contains(suffix))
            return 32;
        if (XboxSceneSuffixes.Contains(suffix))
            return 48;

        return 4;
    }

    /// <summary>
    ///     Resolves the route from name plus content. Never throws; a buffer that
    ///     is too short simply fails the content check.
    /// </summary>
    public static MeshFileRoute DetectFromBytes(string fileName, byte[] data, long fileSize)
    {
        var route = DetectByName(fileName);
        return route.RequiresContentProbe
            ? MeshContentProbe.Resolve(route, data, fileSize)
            : route;
    }

    /// <summary>Reads exactly <see cref="GetProbeByteBudget" /> bytes and classifies.</summary>
    public static MeshFileRoute Detect(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        var route = DetectByName(fileName);
        if (!route.RequiresContentProbe)
            return route;

        // A worldzone verdict needs the whole PAK entry table, not a header
        // prefix, so it takes the path-based check rather than a bounded read.
        if (route.Kind == MeshFileKind.Ps2Worldzone)
        {
            return Ps2Scene.Ps2WorldzoneDetection.IsWorldzonePak(filePath)
                ? route with { RequiresContentProbe = false }
                : MeshFileRoute.Rejected(
                    route.Suffix, "THAW PS2 Worldzone", "Not a recognized THAW PS2 worldzone PAK");
        }

        try
        {
            var budget = GetProbeByteBudget(fileName);
            var info = new FileInfo(filePath);
            var toRead = (int)Math.Min(budget, info.Length);
            var buffer = new byte[toRead];
            using (var stream = File.OpenRead(filePath))
            {
                stream.ReadExactly(buffer, 0, toRead);
            }

            return MeshContentProbe.Resolve(route, buffer, info.Length);
        }
        catch (IOException)
        {
            return MeshFileRoute.Rejected(route.Suffix, "Unknown", "Failed to read file header");
        }
        catch (UnauthorizedAccessException)
        {
            return MeshFileRoute.Rejected(route.Suffix, "Unknown", "Failed to read file header");
        }
    }

    /// <summary>Classifies an entry already read out of an archive.</summary>
    public static MeshFileRoute DetectNested(string entryName, byte[] data)
    {
        return DetectFromBytes(entryName, data, data.Length);
    }

    /// <summary>Maps a detected kind onto the conversion pipeline's source kind.</summary>
    public static ModelSourceKind ToSourceKind(MeshFileKind kind)
    {
        return kind switch
        {
            MeshFileKind.Collision => ModelSourceKind.Collision,
            MeshFileKind.Ps2Scene => ModelSourceKind.Ps2Scene,
            MeshFileKind.Ps2Geom => ModelSourceKind.Ps2Geom,
            MeshFileKind.Ps2Worldzone => ModelSourceKind.Ps2Worldzone,
            MeshFileKind.XbxScene => ModelSourceKind.XbxScene,
            MeshFileKind.Ddm => ModelSourceKind.Ddm,
            MeshFileKind.Psx => ModelSourceKind.Psx,
            MeshFileKind.N64Model => ModelSourceKind.N64Model,
            MeshFileKind.RenderWareDff => ModelSourceKind.RenderWareDff,
            MeshFileKind.RenderWareBsp => ModelSourceKind.RenderWareBsp,
            _ => ModelSourceKind.Generic
        };
    }

    /// <summary>
    ///     The single stem rule: strip the longest known suffix, else drop the
    ///     last extension. "mission.col" -> "mission"; "Arrow.col.xbx" -> "Arrow";
    ///     "a.iskin.ps2" -> "a".
    /// </summary>
    public static string GetStem(string fileName)
    {
        var name = Path.GetFileName(fileName);
        var suffix = MatchSuffix(name);
        return suffix == null ? Path.GetFileNameWithoutExtension(name) : name[..^suffix.Length];
    }

    /// <summary>
    ///     Export stem for a carved N64 model bundle: <c>n64_007_c_kart</c>.
    ///     <para>
    ///         The carved file name already carries the bundle slot and, where
    ///         content identified it, the source PS1 file name, so the stem comes
    ///         from the FILE rather than the parent directory. A carve made
    ///         before names were recovered spells it <c>geometry_007</c>; that
    ///         prefix is stripped so such a tree still exports the stable
    ///         <c>n64_007</c> it always did.
    ///     </para>
    /// </summary>
    public static string GetN64BundleStem(string fileNameOrPath)
    {
        var stem = GetStem(fileNameOrPath);
        if (stem.StartsWith("geometry_", StringComparison.OrdinalIgnoreCase))
            stem = stem["geometry_".Length..];

        return stem.Length == 0 ? "n64_model" : "n64_" + stem;
    }

    /// <summary>
    ///     Formats the pre-scan dialog surfaces PartiallySupported warnings for.
    /// </summary>
    public static bool ReportsPartialSupport(in MeshFileRoute route)
    {
        return route.Kind is MeshFileKind.Collision or MeshFileKind.RenderWareBsp
               || (route.Kind == MeshFileKind.XbxScene
                   && !route.Suffix.EndsWith(".ngc", StringComparison.OrdinalIgnoreCase));
    }
}
