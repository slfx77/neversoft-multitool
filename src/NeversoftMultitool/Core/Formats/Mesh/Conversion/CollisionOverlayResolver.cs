using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Collision;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using NeversoftMultitool.Core.Formats.Mesh.RenderWare;
using NeversoftMultitool.Core.Formats.Mesh.XbxScene;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     Resolves a collision surface only when the render scene either owns the
///     collision topology inline or has an exact, proof-bound companion. This
///     deliberately does not search ancestors or guess between hash-named PAK
///     entries: a false match puts unrelated worlds in one coordinate space and
///     is worse than no overlay.
/// </summary>
internal static class CollisionOverlayResolver
{
    /// <summary>
    ///     Returns true when an inline collision payload is structurally usable
    ///     or precisely one supported companion resolves. Filesystem companions
    ///     stay in the scene's directory; archive companions require precisely
    ///     one match in the selected entry's directory and do not use the asset
    ///     source's archive-wide fallback.
    /// </summary>
    public static bool HasSupportedCompanion(
        AssetSource source,
        string sceneFileName,
        ModelSourceKind sourceKind)
    {
        if (sourceKind == ModelSourceKind.Psx)
            return HasSupportedInlinePsx(source, sceneFileName);

        if (sourceKind == ModelSourceKind.RenderWareBsp)
            return HasSupportedInlineRwBsp(source);

        if (sourceKind == ModelSourceKind.XbxScene
            && NgcCollisionBindingResolver.IsSceneName(sceneFileName))
        {
            return NgcCollisionBindingResolver.HasRenderableCompanionForScene(source, sceneFileName);
        }

        return FindUniqueCandidateName(source, sceneFileName, sourceKind) != null;
    }

    /// <summary>
    ///     Adds an optional, translucent collision surface. A missing, ambiguous,
    ///     mislabeled, or malformed inline source/companion leaves the render
    ///     document unchanged.
    /// </summary>
    public static bool TryPopulate(
        ModelDocument document,
        AssetSource source,
        string sceneFileName,
        ModelSourceKind sourceKind)
    {
        if (document.NativeMetadata.OfType<CollisionOverlayRenderMetadata>().Any())
            return false;

        if (sourceKind == ModelSourceKind.Psx)
            return TryPopulateInlinePsx(document, source, sceneFileName);

        if (sourceKind == ModelSourceKind.RenderWareBsp)
            return TryPopulateInlineRwBsp(document, sceneFileName);

        if (sourceKind == ModelSourceKind.XbxScene
            && NgcCollisionBindingResolver.IsSceneName(sceneFileName))
        {
            return TryPopulateNgc(document, source, sceneFileName);
        }

        var companionName = FindUniqueCandidateName(source, sceneFileName, sourceKind);
        if (companionName == null)
            return false;

        if (sourceKind == ModelSourceKind.Ddm)
        {
            return TryPopulateThps2xDdm(
                document, source, companionName);
        }

        DocumentMutationSnapshot? mutation = null;
        try
        {
            var data = source.TryReadCompanion(companionName);
            if (data == null || !MatchesPlatformEncoding(companionName, data))
                return false;

            var scene = ColFile.Parse(data);
            if (!HasRenderableTriangle(scene))
                return false;

            mutation = DocumentMutationSnapshot.Capture(document);
            var before = CountDocumentTriangles(document);
            CollisionGeometryWriter.PopulateCollision(document, scene, asOverlay: true);
            var added = CountDocumentTriangles(document) - before;
            if (added <= 0)
            {
                mutation.Value.Restore(document);
                return false;
            }

            document.NativeMetadata.Add(new CollisionOverlayRenderMetadata(
                companionName,
                scene.Objects.Length,
                added));
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException
                                   or IOException
                                   or UnauthorizedAccessException
                                   or OverflowException
                                   or ArgumentException
                                   or IndexOutOfRangeException
                                   or NotSupportedException)
        {
            // Optional composition must never make an otherwise readable level
            // fail. The standalone COL route remains available for diagnostics.
            mutation?.Restore(document);
            return false;
        }
    }

    private static bool HasSupportedInlinePsx(
        AssetSource source,
        string sceneFileName)
    {
        try
        {
            // In addition to ordinary exact level ownership, Apocalypse's
            // v2.0 TRGs explicitly SpoolEnv-register each split geometry region.
            // That collision-only role does not attach the shared object bank.
            if (!MeshCompanionResolver.TryResolvePsxInlineCollisionLevel(
                    source, sceneFileName, out _))
            {
                return false;
            }

            var level = PsxMeshFile.Parse(source.ReadBytes());
            return level != null && PsxInlineCollisionGeometryWriter.CanPopulate(level);
        }
        catch (Exception ex) when (IsOptionalCollisionFailure(ex))
        {
            return false;
        }
    }

    private static bool HasSupportedInlineRwBsp(AssetSource source)
    {
        try
        {
            var data = source.ReadBytes();
            return RwBspFile.IsBspFile(data)
                   && RwBspCollisionGeometryWriter.CanPopulate(RwBspFile.Parse(data));
        }
        catch (Exception ex) when (IsOptionalCollisionFailure(ex))
        {
            return false;
        }
    }

    private static bool TryPopulateInlinePsx(
        ModelDocument document,
        AssetSource source,
        string sceneFileName)
    {
        DocumentMutationSnapshot? mutation = null;
        try
        {
            if (!MeshCompanionResolver.TryResolvePsxInlineCollisionLevel(
                    source, sceneFileName, out _)
                || document.NativeSource is not PsxNativeSource native)
            {
                return false;
            }

            mutation = DocumentMutationSnapshot.Capture(document);
            var added = PsxInlineCollisionGeometryWriter.PopulateOverlay(
                document, native.File);
            if (added <= 0)
            {
                mutation.Value.Restore(document);
                return false;
            }

            document.NativeMetadata.Add(new CollisionOverlayRenderMetadata(
                Path.GetFileName(sceneFileName),
                native.File.Objects.Count,
                added));
            return true;
        }
        catch (Exception ex) when (IsOptionalCollisionFailure(ex))
        {
            mutation?.Restore(document);
            return false;
        }
    }

    private static bool TryPopulateInlineRwBsp(
        ModelDocument document,
        string sceneFileName)
    {
        if (document.NativeSource is not RenderWareBspNativeSource native)
            return false;

        DocumentMutationSnapshot? mutation = null;
        try
        {
            mutation = DocumentMutationSnapshot.Capture(document);
            var added = RwBspCollisionGeometryWriter.PopulateOverlay(
                document, native.World);
            if (added <= 0)
            {
                mutation.Value.Restore(document);
                return false;
            }

            document.NativeMetadata.Add(new CollisionOverlayRenderMetadata(
                Path.GetFileName(sceneFileName),
                native.World.Sections.Length,
                added));
            return true;
        }
        catch (Exception ex) when (IsOptionalCollisionFailure(ex))
        {
            mutation?.Restore(document);
            return false;
        }
    }

    private static bool IsOptionalCollisionFailure(Exception exception) =>
        exception is InvalidDataException
            or IOException
            or UnauthorizedAccessException
            or OverflowException
            or ArgumentException
            or IndexOutOfRangeException
            or KeyNotFoundException
            or NotSupportedException;

    private static bool TryPopulateNgc(
        ModelDocument document,
        AssetSource source,
        string sceneFileName)
    {
        if (document.NativeSource is not XbxSceneNativeSource native
            || native.Scene.NgcPositionPools == null)
            return false;

        DocumentMutationSnapshot? mutation = null;
        try
        {
            if (!NgcCollisionBindingResolver.TryResolveForScene(
                    source,
                    sceneFileName,
                    native.Scene,
                    out var collision,
                    out var binding,
                    out var companionName)
                || collision == null || binding == null || companionName == null)
            {
                return false;
            }

            mutation = DocumentMutationSnapshot.Capture(document);
            var added = NgcCollisionGeometryWriter.Populate(
                document, collision, binding, asOverlay: true);
            if (added <= 0)
            {
                mutation.Value.Restore(document);
                return false;
            }

            document.NativeMetadata.Add(new CollisionOverlayRenderMetadata(
                companionName,
                collision.Objects.Length,
                added));
            document.NativeMetadata.Add(new NgcCollisionRenderMetadata(
                companionName,
                binding.PoolKind.ToString(),
                collision.Objects.Length,
                added));
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException
                                   or IOException
                                   or UnauthorizedAccessException
                                   or OverflowException
                                   or ArgumentException
                                   or IndexOutOfRangeException
                                   or NotSupportedException)
        {
            mutation?.Restore(document);
            return false;
        }
    }

    private static bool TryPopulateThps2xDdm(
        ModelDocument document,
        AssetSource source,
        string companionName)
    {
        // A DDM parsed without its exact PSX layout has a different placement
        // contract, so composing collision into that fallback would be wrong.
        if (document.SourceKind != ModelSourceKind.DdmPlacedLevel)
            return false;

        DocumentMutationSnapshot? mutation = null;
        try
        {
            var data = source.TryReadCompanion(companionName);
            if (data is not { Length: >= sizeof(uint) }
                || BinaryPrimitives.ReadUInt32LittleEndian(data) != 0x00020006)
            {
                return false;
            }

            var collision = PsxMeshFile.Parse(data, bakeColourPulses: false);
            if (collision is not { Version: 0x06, IsSuperModel: false })
                return false;

            mutation = DocumentMutationSnapshot.Capture(document);
            var added = Thps2XPsxCollisionGeometryWriter.PopulateOverlay(
                document, collision);
            if (added <= 0)
            {
                mutation.Value.Restore(document);
                return false;
            }

            document.NativeMetadata.Add(new CollisionOverlayRenderMetadata(
                companionName,
                collision.Objects.Count,
                added));
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException
                                   or IOException
                                   or UnauthorizedAccessException
                                   or OverflowException
                                   or ArgumentException
                                   or IndexOutOfRangeException
                                   or KeyNotFoundException
                                   or NotSupportedException)
        {
            mutation?.Restore(document);
            return false;
        }
    }

    internal static IReadOnlyList<string> CandidateNamesFor(
        string sceneFileName,
        ModelSourceKind sourceKind)
    {
        var name = Path.GetFileName(sceneFileName);
        var suffixes = GetCollisionSuffixes(name, sourceKind, out var sceneSuffix);
        if (suffixes.Length == 0 || name.Length <= sceneSuffix.Length)
            return [];

        var stem = name[..^sceneSuffix.Length];
        return suffixes.Select(suffix => stem + suffix).ToArray();
    }

    private static string? FindUniqueCandidateName(
        AssetSource source,
        string sceneFileName,
        ModelSourceKind sourceKind)
    {
        if (sourceKind == ModelSourceKind.Ddm
            && !HasThps2xAuthoredLevelMarkers(source, sceneFileName))
        {
            return null;
        }

        string? match = null;
        foreach (var candidate in CandidateNamesFor(sceneFileName, sourceKind))
        {
            bool exists;
            try
            {
                exists = CompanionExistsInSameOwner(source, candidate);
            }
            catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or ArgumentException
                                       or NotSupportedException)
            {
                return null;
            }

            if (!exists)
                continue;
            if (match != null)
                return null;
            match = candidate;
        }

        return match;
    }

    private static bool HasThps2xAuthoredLevelMarkers(
        AssetSource source,
        string sceneFileName)
    {
        var name = Path.GetFileName(sceneFileName);
        const string suffix = ".ddm";
        if (!name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            || name.Length <= suffix.Length)
        {
            return false;
        }

        var stem = name[..^suffix.Length];
        try
        {
            // These are the two independent authored-family markers already
            // used by DDM level/sky composition. Requiring both keeps generic
            // DDM props, characters, front-end meshes, and *_o banks out.
            return CompanionExistsInSameOwner(source, stem + "_o.ddm")
                   && CompanionExistsInSameOwner(source, stem + "_t.trg");
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or ArgumentException
                                   or NotSupportedException)
        {
            return false;
        }
    }

    private static bool CompanionExistsInSameOwner(AssetSource source, string candidateName)
    {
        if (source is not ArchiveAssetSource archive)
            return source.CompanionExists(candidateName);

        var selectedDirectory = ArchiveDirectory(archive.Entry);
        var matches = archive.Backend.FindAllByName(candidateName);
        var count = 0;
        foreach (var candidate in matches)
        {
            if (!string.Equals(
                    ArchiveDirectory(candidate),
                    selectedDirectory,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (++count > 1)
                return false;
        }

        return count == 1;
    }

    private static string ArchiveDirectory(Archives.ArchiveEntry entry)
    {
        if (!string.IsNullOrEmpty(entry.Directory))
            return entry.Directory.Replace('\\', '/').Trim('/');

        var normalized = entry.Name.Replace('\\', '/');
        var separator = normalized.LastIndexOf('/');
        return separator < 0 ? string.Empty : normalized[..separator].Trim('/');
    }

    private static string[] GetCollisionSuffixes(
        string sceneFileName,
        ModelSourceKind sourceKind,
        out string sceneSuffix)
    {
        if (sourceKind == ModelSourceKind.Ps2Geom &&
            sceneFileName.EndsWith(".geom.ps2", StringComparison.OrdinalIgnoreCase))
        {
            sceneSuffix = ".geom.ps2";
            return [".col.ps2", ".col"];
        }

        if (sourceKind == ModelSourceKind.Ddm
            && sceneFileName.EndsWith(".ddm", StringComparison.OrdinalIgnoreCase))
        {
            sceneSuffix = ".ddm";
            return [".psx"];
        }

        if (sourceKind == ModelSourceKind.XbxScene)
        {
            // Aspyr's THPS4 PC port removes the delimiter between an asset stem
            // and its kind: Alcscn.dat owns Alccol.dat. Keep the same strict
            // non-empty/no-dot namespace gate used by the standalone DAT
            // routers so a generic *.dat file can never acquire a companion by
            // suffix coincidence alone.
            if (IsDelimiterFreeSuffix(sceneFileName, Thps4PcDatSceneFile.SceneSuffix))
            {
                sceneSuffix = Thps4PcDatSceneFile.SceneSuffix;
                return ["col.dat"];
            }

            if (sceneFileName.EndsWith(".scn.xbx", StringComparison.OrdinalIgnoreCase))
            {
                sceneSuffix = ".scn.xbx";
                return [".col.xbx", ".col"];
            }

            if (sceneFileName.EndsWith(".scn.wpc", StringComparison.OrdinalIgnoreCase))
            {
                sceneSuffix = ".scn.wpc";
                return [".col.wpc", ".col"];
            }

            // LE PAK tables identify these payloads by the bare file-type
            // extensions. SourceKind is still required, so an arbitrary .scn
            // is never paired without first passing the XbxScene content gate.
            if (sceneFileName.EndsWith(".scn", StringComparison.OrdinalIgnoreCase))
            {
                sceneSuffix = ".scn";
                return [".col"];
            }

            if (sceneFileName.EndsWith(".scn.xen", StringComparison.OrdinalIgnoreCase))
            {
                sceneSuffix = ".scn.xen";
                return [".col.xen"];
            }
        }

        sceneSuffix = string.Empty;
        return [];
    }

    private static bool IsDelimiterFreeSuffix(string fileName, string suffix) =>
        fileName.Length > suffix.Length
        && fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
        && !fileName.EndsWith('.' + suffix, StringComparison.OrdinalIgnoreCase);

    private static bool MatchesPlatformEncoding(string companionName, ReadOnlySpan<byte> data)
    {
        if (data.Length < sizeof(int))
            return false;

        if (companionName.EndsWith(".col.xen", StringComparison.OrdinalIgnoreCase))
            return BinaryPrimitives.ReadInt32BigEndian(data) == 10;

        var version = BinaryPrimitives.ReadInt32LittleEndian(data);
        if (IsDelimiterFreeSuffix(companionName, "col.dat"))
            return version == 8;
        return version is 8 or 9 or 10;
    }

    private static bool HasRenderableTriangle(ColScene scene)
    {
        foreach (var obj in scene.Objects)
        {
            foreach (var face in obj.Faces)
            {
                if ((uint)face.V0 >= (uint)obj.Vertices.Length ||
                    (uint)face.V1 >= (uint)obj.Vertices.Length ||
                    (uint)face.V2 >= (uint)obj.Vertices.Length)
                {
                    continue;
                }

                if (!ModelDocumentGeometryAdapter.IsDegenerate(
                        obj.Vertices[face.V0],
                        obj.Vertices[face.V1],
                        obj.Vertices[face.V2]))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static int CountDocumentTriangles(ModelDocument document) =>
        document.Meshes
            .SelectMany(static mesh => mesh.Primitives)
            .Sum(static primitive => primitive.TriangleCount);

    private readonly record struct DocumentMutationSnapshot(
        int MaterialCount,
        int MeshCount,
        int NodeCount,
        int SceneCount,
        int MetadataCount,
        int TriangleCount,
        int[] SceneRootCounts)
    {
        public static DocumentMutationSnapshot Capture(ModelDocument document) =>
            new(
                document.Materials.Count,
                document.Meshes.Count,
                document.Nodes.Count,
                document.Scenes.Count,
                document.NativeMetadata.Count,
                document.TriangleCount,
                document.Scenes.Select(static scene => scene.RootNodeIndices.Count).ToArray());

        public void Restore(ModelDocument document)
        {
            Trim(document.Materials, MaterialCount);
            Trim(document.Meshes, MeshCount);
            Trim(document.Nodes, NodeCount);
            Trim(document.NativeMetadata, MetadataCount);

            for (var i = 0; i < Math.Min(SceneCount, document.Scenes.Count); i++)
                Trim(document.Scenes[i].RootNodeIndices, SceneRootCounts[i]);
            Trim(document.Scenes, SceneCount);
            document.TriangleCount = TriangleCount;
        }

        private static void Trim<T>(List<T> list, int count)
        {
            if (list.Count > count)
                list.RemoveRange(count, list.Count - count);
        }
    }
}

/// <summary>Records the exact collision source composed into a render document.</summary>
public sealed record CollisionOverlayRenderMetadata(
    string CompanionName,
    int ObjectCount,
    int TriangleCount)
    : NativeRenderMetadata("collision-overlay");
