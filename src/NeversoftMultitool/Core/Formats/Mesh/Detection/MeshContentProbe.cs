
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Scene;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Skin;
using NeversoftMultitool.Core.Formats.Mesh.XbxScene;

namespace NeversoftMultitool.Core.Formats.Mesh.Detection;

/// <summary>
///     Content checks behind <see cref="MeshTypeDetector.DetectFromBytes" />.
///     Split out so the detector file stays a name/table module.
/// </summary>
internal static class MeshContentProbe
{
    private const string UnknownFormat = "Unknown";

    public static MeshFileRoute Resolve(MeshFileRoute route, byte[] data, long fileSize)
    {
        return route.Kind switch
        {
            MeshFileKind.XbxScene => ResolveXbxScene(route, data),
            MeshFileKind.Ps2Scene => ResolveSuffixedPs2Scene(route, data, fileSize),
            MeshFileKind.Collision => ResolveCollision(route, data),
            MeshFileKind.Psx => ResolvePsx(route, data),
            MeshFileKind.RenderWareDff => ResolveChunk(route, data, 0x0010, "RW DFF Mesh", "DFF"),
            MeshFileKind.RenderWareBsp => ResolveChunk(route, data, 0x000B, "RW BSP Level", "BSP"),
            MeshFileKind.Ps2Worldzone => ResolveWorldzone(route, data),
            // Bare .skin/.mdl carry no kind from the name — the ladder decides.
            _ => ResolveAmbiguousScene(route, data, fileSize)
        };
    }

    private static MeshFileRoute ResolveXbxScene(MeshFileRoute route, byte[] data)
    {
        if (data.Length < 12)
            return TooSmall(route);

        if (NgcSceneFile.IsNgcScene(data))
            return route with { RequiresContentProbe = false, DisplayFormat = "GameCube Scene" };

        if (ThawSceneFile.IsThawScene(data))
            return route with { RequiresContentProbe = false, DisplayFormat = "THAW Scene" };

        if (XbxSceneFile.IsXbxScene(data))
            return route with { RequiresContentProbe = false, DisplayFormat = "Xbox Scene" };

        var v0 = BitConverter.ToUInt32(data, 0);
        var v1 = BitConverter.ToUInt32(data, 4);
        var v2 = BitConverter.ToUInt32(data, 8);
        return MeshFileRoute.Rejected(
            route.Suffix,
            "Xbox Scene",
            $"Unsupported version ({v0},{v1},{v2}), expected (1,1,1) or THAW");
    }

    private static MeshFileRoute ResolveSuffixedPs2Scene(MeshFileRoute route, byte[] data, long fileSize)
    {
        if (data.Length < 12)
            return TooSmall(route);

        if (Ps2SceneFile.IsPs2Scene(data))
        {
            var game = BitConverter.ToUInt32(data, 0) switch
            {
                3 => "THPS4",
                5 => "THUG",
                6 => "THUG2",
                _ => "Unknown"
            };
            return Resolved(route, Ps2SceneSubFormat.Standard, $"PS2 Scene ({game})");
        }

        if (ThawPs2SkinFile.IsThawPs2Skin(data, fileSize))
            return Resolved(route, Ps2SceneSubFormat.ThawSkin, "THAW/Pre-compiled PS2 Scene");

        // PakSkin/PakMdl also ship under .skin.ps2/.mdl.ps2 in PAK-extracted trees.
        // The four PS2 predicates are mutually exclusive by construction
        // (IsPakMdl and IsPakSkin each exclude the others), so ladder order here
        // is immaterial — see ThawPs2SkinFile.IsPakSkin and Ps2GeomFile.IsPakMdl.
        if (ThawPs2SkinFile.IsPakSkin(data))
            return Resolved(route, Ps2SceneSubFormat.PakSkin, "PAK Skin (THAW PS2)");

        if (Ps2GeomFile.IsPakMdl(data))
            return Resolved(route, Ps2SceneSubFormat.PakMdl, "PAK MDL (THAW PS2)");

        var matVer = BitConverter.ToUInt32(data, 0);
        var meshVer = BitConverter.ToUInt32(data, 4);
        var vertVer = BitConverter.ToUInt32(data, 8);
        return MeshFileRoute.Rejected(
            route.Suffix,
            "Unknown PS2 Scene",
            $"Unrecognized version triple ({matVer},{meshVer},{vertVer})");
    }

    /// <summary>
    ///     Bare <c>.skin</c>/<c>.mdl</c>. Order matters here in one place only:
    ///     <see cref="XbxSceneFile.IsXbxScene" /> is nothing but a (1,1,1) version
    ///     triple, which ~32% of PS2-build bare .skin files also satisfy, so it
    ///     must be tried LAST. Everything above it carries a real structural
    ///     signature.
    /// </summary>
    private static MeshFileRoute ResolveAmbiguousScene(MeshFileRoute route, byte[] data, long fileSize)
    {
        if (data.Length < 12)
            return TooSmall(route);

        if (NgcSceneFile.IsNgcScene(data))
            return Scene(route, "GameCube Scene");

        if (ThawSceneFile.IsThawScene(data))
            return Scene(route, "THAW Scene");

        if (data.Length >= 256)
        {
            if (ThawPs2SkinFile.IsPakSkin(data))
                return Ps2(route, Ps2SceneSubFormat.PakSkin, "PAK Skin (THAW PS2)");

            if (string.Equals(route.Suffix, ".mdl", StringComparison.Ordinal) && Ps2GeomFile.IsPakMdl(data))
                return Ps2(route, Ps2SceneSubFormat.PakMdl, "PAK MDL (THAW PS2)");
        }

        if (ThawPs2SkinFile.IsThawPs2Skin(data, fileSize))
            return Ps2(route, Ps2SceneSubFormat.ThawSkin, "THAW/Pre-compiled PS2 Scene");

        if (Ps2SceneFile.IsPs2Scene(data))
        {
            var game = BitConverter.ToUInt32(data, 0) switch
            {
                3 => "THPS4",
                5 => "THUG",
                6 => "THUG2",
                _ => "Unknown"
            };
            return Ps2(route, Ps2SceneSubFormat.Standard, $"PS2 Scene ({game})");
        }

        if (XbxSceneFile.IsXbxScene(data))
            return Scene(route, "Xbox Scene");

        return MeshFileRoute.Rejected(
            route.Suffix,
            $"Unknown {route.Suffix}",
            $"Bare {route.Suffix} is not a recognized GC/PC/Xbox scene or PS2 scene/skin");

        static MeshFileRoute Scene(MeshFileRoute route, string label)
        {
            return route with { Kind = MeshFileKind.XbxScene, RequiresContentProbe = false, DisplayFormat = label };
        }

        static MeshFileRoute Ps2(MeshFileRoute route, Ps2SceneSubFormat subFormat, string label)
        {
            return route with
            {
                Kind = MeshFileKind.Ps2Scene,
                Ps2SubFormat = subFormat,
                RequiresContentProbe = false,
                DisplayFormat = label
            };
        }
    }

    private static MeshFileRoute ResolveCollision(MeshFileRoute route, byte[] data)
    {
        if (data.Length < 4)
            return TooSmall(route);

        // Read the version directly rather than calling ColFile.IsColFile, which
        // requires a whole 32-byte header — the probe budget here is 4 bytes and
        // the version is the only field the verdict depends on.
        var version = BitConverter.ToInt32(data, 0);
        return version is 9 or 10
            ? route with { RequiresContentProbe = false, DisplayFormat = $"COL Collision (v{version})" }
            : MeshFileRoute.Rejected(
                route.Suffix,
                $"COL (v{version})",
                $"Unsupported COL version {version} (supported: 9, 10)");
    }

    private static MeshFileRoute ResolvePsx(MeshFileRoute route, byte[] data)
    {
        if (data.Length < 4)
            return TooSmall(route);

        var version = BitConverter.ToUInt16(data, 0);
        var magic = BitConverter.ToUInt16(data, 2);
        return magic == 0x0002 && version is 0x03 or 0x04 or 0x06
            ? route with { RequiresContentProbe = false, DisplayFormat = "PSX Mesh" }
            : MeshFileRoute.Rejected(
                route.Suffix,
                UnknownFormat,
                $"Not a valid PSX mesh (header: 0x{version:X4} 0x{magic:X4})");
    }

    private static MeshFileRoute ResolveChunk(
        MeshFileRoute route, byte[] data, uint expected, string label, string what)
    {
        if (data.Length < 4)
            return TooSmall(route);

        var chunkType = BitConverter.ToUInt32(data, 0);
        return chunkType == expected
            ? route with { RequiresContentProbe = false, DisplayFormat = label }
            : MeshFileRoute.Rejected(
                route.Suffix,
                UnknownFormat,
                $"Not a valid RenderWare {what} file (header: 0x{chunkType:X8})");
    }

    private static MeshFileRoute ResolveWorldzone(MeshFileRoute route, byte[] data)
    {
        return Ps2WorldzoneDetection.IsWorldzonePak(data)
            ? route with { RequiresContentProbe = false }
            : MeshFileRoute.Rejected(
                route.Suffix,
                "THAW PS2 Worldzone",
                "Not a recognized THAW PS2 worldzone PAK");
    }

    private static MeshFileRoute Resolved(MeshFileRoute route, Ps2SceneSubFormat subFormat, string label)
    {
        return route with { Ps2SubFormat = subFormat, RequiresContentProbe = false, DisplayFormat = label };
    }

    private static MeshFileRoute TooSmall(MeshFileRoute route)
    {
        return MeshFileRoute.Rejected(route.Suffix, UnknownFormat, "File too small");
    }
}
