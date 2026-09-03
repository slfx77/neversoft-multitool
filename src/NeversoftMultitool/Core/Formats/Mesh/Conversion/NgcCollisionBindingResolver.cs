using System.Numerics;
using NeversoftMultitool.Core.Formats.Collision;
using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Core.Formats.Mesh.XbxScene;
using ParsedXbxScene = NeversoftMultitool.Core.Formats.Mesh.XbxScene.XbxScene;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     Proves the external position-pool binding used by THAW GameCube COL.
///     Loose assets require the exact same-directory stem. Hash-named PAK
///     entries are admitted only when their typed archive directory contains
///     exactly one NGC COL and exactly one NGC render scene.
/// </summary>
internal static class NgcCollisionBindingResolver
{
    internal const string CollisionSuffix = ".col.ngc";
    // The authored collision bounds are quantized to the same 1/32-unit grid
    // used by NGC position streams. The corpus needs this tolerance for both
    // static MDL and skin winners; smaller epsilon sweeps reject real pairs.
    private const float BoundsTolerance = 1f / 32f;
    private static readonly string[] SceneSuffixes = [".mdl.ngc", ".skin.ngc", ".scn.ngc"];

    public static bool IsCollisionName(string fileName) =>
        Path.GetFileName(fileName).EndsWith(CollisionSuffix, StringComparison.OrdinalIgnoreCase);

    public static bool IsSceneName(string fileName) =>
        SceneSuffixes.Any(suffix =>
            Path.GetFileName(fileName).EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

    public static bool HasRenderableCompanionForScene(AssetSource source, string sceneFileName)
    {
        try
        {
            var sceneBytes = source.ReadBytes();
            if (!NgcSceneFile.TryParse(sceneBytes, out var scene) || scene == null)
                return false;
            return TryResolveForScene(source, sceneFileName, scene, out _, out _, out _);
        }
        catch (Exception ex) when (IsExpectedFailure(ex))
        {
            return false;
        }
    }

    public static bool TryResolveForCollision(
        AssetSource source,
        string collisionFileName,
        NgcColScene collision,
        out ParsedXbxScene? scene,
        out NgcCollisionPositionBinding? binding,
        out string? companionName)
    {
        scene = null;
        binding = null;
        companionName = null;
        try
        {
            if (!TryReadSceneCompanion(source, collisionFileName, out var name, out var bytes)
                || !NgcSceneFile.TryParse(bytes, out scene)
                || scene == null
                || !TryBind(collision, scene, out binding))
            {
                scene = null;
                binding = null;
                return false;
            }

            companionName = name;
            return true;
        }
        catch (Exception ex) when (IsExpectedFailure(ex))
        {
            scene = null;
            binding = null;
            companionName = null;
            return false;
        }
    }

    public static bool TryResolveForScene(
        AssetSource source,
        string sceneFileName,
        ParsedXbxScene scene,
        out NgcColScene? collision,
        out NgcCollisionPositionBinding? binding,
        out string? companionName)
    {
        collision = null;
        binding = null;
        companionName = null;
        try
        {
            if (!TryReadCollisionCompanion(source, sceneFileName, out var name, out var bytes)
                || !NgcColFile.IsNgcColFile(bytes))
            {
                return false;
            }

            collision = NgcColFile.Parse(bytes);
            if (!TryBind(collision, scene, out binding))
            {
                collision = null;
                return false;
            }

            companionName = name;
            return true;
        }
        catch (Exception ex) when (IsExpectedFailure(ex))
        {
            collision = null;
            binding = null;
            companionName = null;
            return false;
        }
    }

    internal static bool TryBind(
        NgcColScene collision,
        ParsedXbxScene scene,
        out NgcCollisionPositionBinding? binding)
    {
        binding = null;
        var pools = scene.NgcPositionPools;
        if (collision.TotalFaces == 0 || collision.TotalVerts == 0 || pools == null)
            return false;
        if (collision.Objects.Length != pools.Objects.Length)
            return false;

        for (var i = 0; i < collision.Objects.Length; i++)
        {
            if (!pools.Objects[i].HasRenderChecksum
                || !pools.Objects[i].RenderChecksumIsUniform
                || collision.Objects[i].Checksum != pools.Objects[i].RenderChecksum
                || pools.Objects[i].ObjectIndex != i)
            {
                return false;
            }
        }

        var skinPositions = pools.Objects.SelectMany(static obj => obj.SkinPositions).ToArray();
        var staticValid = TryValidatePool(collision, pools.StaticPositions, out var staticTriangles);
        var skinValid = TryValidatePool(collision, skinPositions, out var skinTriangles);

        // More than one count/bounds-valid pool is an ownership ambiguity, not
        // a reason to select one by file kind or proximity.
        if (staticValid == skinValid)
            return false;

        binding = staticValid
            ? new NgcCollisionPositionBinding(
                NgcCollisionPositionPoolKind.StaticScene, pools.StaticPositions, staticTriangles)
            : new NgcCollisionPositionBinding(
                NgcCollisionPositionPoolKind.SkinObjectLists, skinPositions, skinTriangles);
        return true;
    }

    private static bool TryValidatePool(
        NgcColScene collision,
        Vector3[] positions,
        out int renderableTriangles)
    {
        renderableTriangles = 0;
        if (positions.Length != collision.TotalVerts)
            return false;
        if (positions.Any(static position =>
                !float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z)))
        {
            return false;
        }

        foreach (var obj in collision.Objects)
        {
            foreach (var face in obj.Faces)
            {
                if ((uint)face.V0 >= (uint)positions.Length
                    || (uint)face.V1 >= (uint)positions.Length
                    || (uint)face.V2 >= (uint)positions.Length)
                {
                    return false;
                }

                var a = positions[face.V0];
                var b = positions[face.V1];
                var c = positions[face.V2];
                if (!Inside(a, obj.BBoxMin, obj.BBoxMax)
                    || !Inside(b, obj.BBoxMin, obj.BBoxMax)
                    || !Inside(c, obj.BBoxMin, obj.BBoxMax)
                    || !Inside(a, collision.SceneBoundsMin, collision.SceneBoundsMax)
                    || !Inside(b, collision.SceneBoundsMin, collision.SceneBoundsMax)
                    || !Inside(c, collision.SceneBoundsMin, collision.SceneBoundsMax))
                {
                    return false;
                }

                if (!ModelDocumentGeometryAdapter.IsDegenerate(a, b, c))
                    renderableTriangles++;
            }
        }

        return renderableTriangles > 0;
    }

    private static bool Inside(Vector3 point, Vector4 min, Vector4 max) =>
        point.X >= min.X - BoundsTolerance && point.X <= max.X + BoundsTolerance
        && point.Y >= min.Y - BoundsTolerance && point.Y <= max.Y + BoundsTolerance
        && point.Z >= min.Z - BoundsTolerance && point.Z <= max.Z + BoundsTolerance;

    private static bool TryReadSceneCompanion(
        AssetSource source,
        string collisionFileName,
        out string name,
        out byte[] bytes)
    {
        name = string.Empty;
        bytes = [];
        if (!IsCollisionName(collisionFileName))
            return false;

        if (source is ArchiveAssetSource archive)
        {
            return TryReadUniqueTypedArchivePeer(
                archive, requireSelectedCollision: true, out name, out bytes);
        }
        if (source is not FileSystemAssetSource)
            return false;

        var fileName = Path.GetFileName(collisionFileName);
        var stem = fileName[..^CollisionSuffix.Length];
        if (stem.Length == 0)
            return false;
        var matches = SceneSuffixes
            .Select(suffix => stem + suffix)
            .Where(source.CompanionExists)
            .ToArray();
        if (matches.Length != 1 || source.TryReadCompanion(matches[0]) is not { } payload)
            return false;
        name = matches[0];
        bytes = payload;
        return true;
    }

    private static bool TryReadCollisionCompanion(
        AssetSource source,
        string sceneFileName,
        out string name,
        out byte[] bytes)
    {
        name = string.Empty;
        bytes = [];
        if (!IsSceneName(sceneFileName))
            return false;

        if (source is ArchiveAssetSource archive)
        {
            return TryReadUniqueTypedArchivePeer(
                archive, requireSelectedCollision: false, out name, out bytes);
        }
        if (source is not FileSystemAssetSource)
            return false;

        var fileName = Path.GetFileName(sceneFileName);
        var suffix = SceneSuffixes.Single(candidate =>
            fileName.EndsWith(candidate, StringComparison.OrdinalIgnoreCase));
        var collisionName = fileName[..^suffix.Length] + CollisionSuffix;
        if (collisionName.Length == CollisionSuffix.Length
            || source.TryReadCompanion(collisionName) is not { } payload)
        {
            return false;
        }

        name = collisionName;
        bytes = payload;
        return true;
    }

    private static bool TryReadUniqueTypedArchivePeer(
        ArchiveAssetSource source,
        bool requireSelectedCollision,
        out string name,
        out byte[] bytes)
    {
        name = string.Empty;
        bytes = [];
        if (source.Backend.Type != ArchiveAssetType.Pak)
            return false;

        var selectedIsExpected = requireSelectedCollision
            ? IsCollisionName(source.Entry.Name)
            : IsSceneName(source.Entry.Name);
        if (!selectedIsExpected)
            return false;

        var directory = ArchiveDirectory(source.Entry);
        var collisions = source.Backend.Entries.Where(entry =>
            string.Equals(ArchiveDirectory(entry), directory, StringComparison.OrdinalIgnoreCase)
            && IsCollisionName(entry.Name)).ToArray();
        var scenes = source.Backend.Entries.Where(entry =>
            string.Equals(ArchiveDirectory(entry), directory, StringComparison.OrdinalIgnoreCase)
            && IsSceneName(entry.Name)).ToArray();
        if (collisions.Length != 1 || scenes.Length != 1)
            return false;
        if ((requireSelectedCollision && !ReferenceEquals(collisions[0], source.Entry))
            || (!requireSelectedCollision && !ReferenceEquals(scenes[0], source.Entry)))
        {
            return false;
        }

        var peer = requireSelectedCollision ? scenes[0] : collisions[0];
        name = peer.Name;
        bytes = source.Backend.ReadEntryBytes(peer);
        return true;
    }

    private static string ArchiveDirectory(ArchiveEntry entry)
    {
        if (!string.IsNullOrEmpty(entry.Directory))
            return entry.Directory.Replace('\\', '/').Trim('/');
        var normalized = entry.Name.Replace('\\', '/');
        var separator = normalized.LastIndexOf('/');
        return separator < 0 ? string.Empty : normalized[..separator].Trim('/');
    }

    private static bool IsExpectedFailure(Exception ex) =>
        ex is InvalidDataException or IOException or UnauthorizedAccessException
            or OverflowException or ArgumentException or IndexOutOfRangeException
            or NotSupportedException;
}

internal enum NgcCollisionPositionPoolKind
{
    StaticScene,
    SkinObjectLists
}

internal sealed record NgcCollisionPositionBinding(
    NgcCollisionPositionPoolKind PoolKind,
    Vector3[] Positions,
    int RenderableTriangleCount);
