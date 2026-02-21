using System.Numerics;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Memory;
using SharpGLTF.Scenes;

namespace NeversoftMultitool.Core.Formats.Psx;

using VERTEX = VertexBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty>;

/// <summary>
/// Writes parsed PSX mesh data to glTF 2.0 (.glb) files.
/// Texture embedding is handled via a callback to keep the mesh and texture pipelines decoupled.
/// </summary>
public static class PsxGltfWriter
{
    /// <summary>
    /// Delegate that resolves a texture hash to PNG bytes for embedding in glTF.
    /// Returns null if the texture cannot be resolved.
    /// </summary>
    public delegate byte[]? TextureProvider(uint textureHash);

    /// <summary>
    /// Writes a parsed PSX file to a .glb file.
    /// </summary>
    /// <param name="psxFile">Parsed PSX mesh data.</param>
    /// <param name="outputPath">Output .glb file path.</param>
    /// <param name="textureProvider">Optional callback to resolve texture hashes to PNG bytes.</param>
    /// <returns>Total number of triangles written.</returns>
    public static int Write(PsxMeshFile psxFile, string outputPath,
        TextureProvider? textureProvider = null)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var scene = new SceneBuilder();
        var materialCache = new Dictionary<uint, MaterialBuilder>();
        var untexturedMaterial = CreateUntexturedMaterial();

        // Always use flat placement: PSX coordinates are absolute world-space
        // (psxprev: IsAbsolute = true). Hierarchy is only used for animation.
        var totalTriangles = WriteFlat(psxFile, scene, materialCache, untexturedMaterial, textureProvider);

        var model = scene.ToGltf2();
        model.SaveGLB(outputPath);

        return totalTriangles;
    }

    /// <summary>
    /// Writes non-hierarchical models (levels, standalone objects).
    /// Each object is placed independently at its world-space position.
    /// </summary>
    private static int WriteFlat(PsxMeshFile psxFile, SceneBuilder scene,
        Dictionary<uint, MaterialBuilder> materialCache, MaterialBuilder untexturedMaterial,
        TextureProvider? textureProvider)
    {
        var totalTriangles = 0;
        var textureDimensions = new Dictionary<uint, (int Width, int Height)>();

        for (var objIdx = 0; objIdx < psxFile.Objects.Count; objIdx++)
        {
            var obj = psxFile.Objects[objIdx];
            var (gltfMesh, triangles) = BuildObjectMesh(psxFile, obj, materialCache, untexturedMaterial,
                textureProvider, textureDimensions);
            if (gltfMesh == null) continue;

            totalTriangles += triangles;

            // PS1 is left-handed; glTF is right-handed Y-up. Negate Y and Z.
            var translation = new Vector3(obj.X(psxFile.TranslationDivisor), -obj.Y(psxFile.TranslationDivisor), -obj.Z(psxFile.TranslationDivisor));
            scene.AddRigidMesh(gltfMesh, Matrix4x4.CreateTranslation(translation));
        }

        return totalTriangles;
    }

    /// <summary>
    /// Writes hierarchical models (humanoid characters, skeletons).
    /// Builds a parent-child node tree so body parts are assembled correctly.
    /// </summary>
    private static int WriteHierarchical(PsxMeshFile psxFile, SceneBuilder scene,
        Dictionary<uint, MaterialBuilder> materialCache, MaterialBuilder untexturedMaterial,
        TextureProvider? textureProvider)
    {
        var objectCount = psxFile.Objects.Count;

        // Pre-compute absolute world-space positions. PSX stores these as absolute
        // coordinates, not relative to parent (confirmed by psxprev: IsAbsolute = true).
        var absolutePositions = new Vector3[objectCount];
        for (var i = 0; i < objectCount; i++)
        {
            var obj = psxFile.Objects[i];
            absolutePositions[i] = new Vector3(obj.X(psxFile.TranslationDivisor), -obj.Y(psxFile.TranslationDivisor), -obj.Z(psxFile.TranslationDivisor));
        }

        var nodes = BuildNodeHierarchy(psxFile, absolutePositions);

        // Attach meshes to their corresponding nodes
        var totalTriangles = 0;
        var textureDimensions = new Dictionary<uint, (int Width, int Height)>();
        for (var i = 0; i < objectCount; i++)
        {
            if (nodes[i] == null) continue;

            var (gltfMesh, triangles) = BuildObjectMesh(psxFile, psxFile.Objects[i],
                materialCache, untexturedMaterial, textureProvider, textureDimensions);
            if (gltfMesh == null) continue;

            totalTriangles += triangles;
            scene.AddRigidMesh(gltfMesh, nodes[i]!);
        }

        return totalTriangles;
    }

    /// <summary>
    /// Builds the glTF node hierarchy from PSX objects. Parent indices can reference
    /// higher-index objects, so we iterate: roots first, then children whose parents
    /// already exist, repeating until all nodes are placed.
    /// </summary>
    private static NodeBuilder?[] BuildNodeHierarchy(PsxMeshFile psxFile, Vector3[] absolutePositions)
    {
        var objectCount = psxFile.Objects.Count;
        var nodes = new NodeBuilder?[objectCount];
        var created = 0;

        while (created < objectCount)
        {
            var progress = false;
            for (var i = 0; i < objectCount; i++)
            {
                if (nodes[i] != null) continue;
                var obj = psxFile.Objects[i];
                var name = ResolveMeshName(psxFile, obj.MeshIndex, $"obj_{i}");
                var parentIdx = obj.ParentIndex;
                var isRoot = parentIdx < 0 || parentIdx >= objectCount || parentIdx == i;

                if (isRoot)
                {
                    nodes[i] = new NodeBuilder(name);
                    nodes[i]!.LocalMatrix = Matrix4x4.CreateTranslation(absolutePositions[i]);
                }
                else if (nodes[parentIdx] != null)
                {
                    nodes[i] = nodes[parentIdx]!.CreateNode(name);
                    nodes[i]!.LocalMatrix = Matrix4x4.CreateTranslation(
                        absolutePositions[i] - absolutePositions[parentIdx]);
                }
                else
                {
                    continue; // Parent not yet created
                }

                created++;
                progress = true;
            }

            if (!progress) break; // Cycle or invalid data
        }

        if (created < objectCount)
        {
            Console.Error.WriteLine($"WARNING: Hierarchy has cycles or invalid parent indices — placed {created}/{objectCount} nodes");
        }

        return nodes;
    }

    /// <summary>
    /// Builds a glTF mesh for a single PSX object. Returns null if the object has no renderable geometry.
    /// Uses obj.MeshIndex to select the mesh (confirmed by Ghidra decompilation of M3dInit_ParsePSX).
    /// </summary>
    private static (MeshBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty>? Mesh, int Triangles)
        BuildObjectMesh(PsxMeshFile psxFile, PsxMeshObject obj,
            Dictionary<uint, MaterialBuilder> materialCache, MaterialBuilder untexturedMaterial,
            TextureProvider? textureProvider, Dictionary<uint, (int Width, int Height)> textureDimensions)
    {
        var meshIndex = obj.MeshIndex;
        if (meshIndex >= psxFile.Meshes.Count)
            return (null, 0);

        var mesh = psxFile.Meshes[meshIndex];
        if (mesh.Faces.Count == 0)
            return (null, 0);

        var meshName = ResolveMeshName(psxFile, meshIndex, $"mesh_{meshIndex:X8}");
        var gltfMesh = new MeshBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty>(meshName);
        var triangles = 0;

        foreach (var face in mesh.Faces)
        {
            var material = face.IsTextured && face.TextureHash != 0
                ? GetOrCreateTexturedMaterial(face.TextureHash, textureProvider, materialCache,
                    textureDimensions)
                : untexturedMaterial;

            // Get texture dimensions for UV normalization (default 256 for untextured/unknown)
            var texDims = face.IsTextured && face.TextureHash != 0
                && textureDimensions.TryGetValue(face.TextureHash, out var dims)
                ? dims
                : (Width: 256, Height: 256);

            var prim = gltfMesh.UsePrimitive(material);
            triangles += AddFace(prim, mesh, face, psxFile.GouraudPalette, texDims.Width, texDims.Height);
        }

        return (gltfMesh, triangles);
    }

    private static string ResolveMeshName(PsxMeshFile psxFile, int meshIndex, string fallback)
    {
        var nameHash = meshIndex < psxFile.MeshNameHashes.Length
            ? psxFile.MeshNameHashes[meshIndex]
            : 0u;
        return QbKey.TryResolve(nameHash) ?? fallback;
    }

    private static int AddFace(
        PrimitiveBuilder<MaterialBuilder, VertexPositionNormal, VertexColor1Texture1, VertexEmpty> prim,
        PsxMesh mesh, PsxFace face, Vector4[]? gouraudPalette,
        int texWidth = 256, int texHeight = 256)
    {
        // Compute per-vertex colors. For gouraud faces, R/G/B/Mode are palette indices
        // mapping to vertices 0/1/2/3 respectively (confirmed by psxprev).
        Vector4 c0, c1, c2, c3;
        if (face.IsGouraud && gouraudPalette != null)
        {
            c0 = face.R < gouraudPalette.Length ? gouraudPalette[face.R] : Vector4.One;
            c1 = face.G < gouraudPalette.Length ? gouraudPalette[face.G] : Vector4.One;
            c2 = face.B < gouraudPalette.Length ? gouraudPalette[face.B] : Vector4.One;
            c3 = face.IsQuad && face.Mode < gouraudPalette.Length
                ? gouraudPalette[face.Mode] : c0;
        }
        else
        {
            c0 = c1 = c2 = c3 = GetFlatFaceColor(face);
        }

        var v0 = MakeVertex(mesh, face, face.Index0, face.U0, face.V0, c0, texWidth, texHeight);
        var v1 = MakeVertex(mesh, face, face.Index1, face.U1, face.V1, c1, texWidth, texHeight);
        var v2 = MakeVertex(mesh, face, face.Index2, face.U2, face.V2, c2, texWidth, texHeight);

        prim.AddTriangle(v0, v1, v2);
        var count = 1;

        if (face.IsQuad)
        {
            var v3 = MakeVertex(mesh, face, face.Index3, face.U3, face.V3, c3, texWidth, texHeight);
            prim.AddTriangle(v1, v3, v2);
            count++;
        }

        return count;
    }

    private static VERTEX MakeVertex(PsxMesh mesh, PsxFace face,
        uint vertexIndex, byte u, byte v, Vector4 color,
        int texWidth = 256, int texHeight = 256)
    {
        var vert = mesh.Vertices[(int)vertexIndex];

        // M3dInit_ParsePSX: when normalCount == vertexCount + faceCount, the first
        // vertexCount normals are per-vertex (smooth shading). Use the vertex's own
        // normal when available; fall back to the per-face normal otherwise.
        var normal = mesh.HasPerVertexNormals && vertexIndex < mesh.VertexCount
            ? mesh.Normals[(int)vertexIndex]
            : mesh.Normals[(int)face.NormalIndex];

        // PS1 is left-handed; glTF is right-handed Y-up. Negate Y and Z.
        // Matches psxprev OBJExporter.cs:166 transform: (X, -Y, -Z).
        var position = new Vector3(vert.X, -vert.Y, -vert.Z);

        var normalVec = new Vector3(normal.X, -normal.Y, -normal.Z);
        // Normalize for glTF compliance (raw normals divided by 4096 may not be unit length)
        var len = normalVec.Length();
        if (len > 0)
            normalVec /= len;

        // PS1 UV coordinates are in pixel space (0 to texture_width-1, 0 to texture_height-1).
        // Normalize to [0,1] using actual texture dimensions, not a fixed 256.
        var uv = face.IsTextured
            ? new Vector2(u / (float)texWidth, v / (float)texHeight)
            : Vector2.Zero;

        return new VERTEX(
            new VertexPositionNormal(position, normalVec),
            new VertexColor1Texture1(color, uv));
    }

    private static Vector4 GetFlatFaceColor(PsxFace face)
    {
        // For non-gouraud faces, all vertices share the same flat color.
        // For gouraud faces without a palette, use white as fallback.
        if (face.IsGouraud)
            return Vector4.One;

        return new Vector4(
            face.R / 255f,
            face.G / 255f,
            face.B / 255f,
            1f);
    }

    private static MaterialBuilder CreateUntexturedMaterial()
    {
        return new MaterialBuilder("untextured")
            .WithDoubleSide(true)
            .WithUnlitShader()
            .WithBaseColor(new Vector4(0.7f, 0.7f, 0.7f, 1f));
    }

    private static MaterialBuilder GetOrCreateTexturedMaterial(
        uint textureHash, TextureProvider? textureProvider,
        Dictionary<uint, MaterialBuilder> cache,
        Dictionary<uint, (int Width, int Height)> textureDimensions)
    {
        if (cache.TryGetValue(textureHash, out var cached))
            return cached;

        var name = QbKey.TryResolve(textureHash) ?? $"tex_{textureHash:X8}";

        var builder = new MaterialBuilder(name)
            .WithDoubleSide(true)
            .WithUnlitShader()
            .WithBaseColor(new Vector4(1, 1, 1, 1));

        // Try to embed texture via callback
        if (textureProvider != null)
        {
            var pngBytes = textureProvider(textureHash);
            if (pngBytes != null)
            {
                var memImage = new MemoryImage(pngBytes);
                builder.WithChannelImage(KnownChannel.BaseColor, memImage);

                // Extract dimensions from PNG header for UV normalization.
                // PNG format: 8-byte signature, then IHDR chunk with width/height as big-endian uint32.
                var dims = ExtractPngDimensions(pngBytes);
                if (dims.HasValue)
                    textureDimensions[textureHash] = dims.Value;
            }
        }

        cache[textureHash] = builder;
        return builder;
    }

    /// <summary>
    /// Extracts width and height from a PNG file's IHDR chunk header.
    /// PNG layout: 8-byte signature + 4-byte chunk length + 4-byte "IHDR" + 4-byte width + 4-byte height.
    /// </summary>
    private static (int Width, int Height)? ExtractPngDimensions(byte[] pngBytes)
    {
        // Minimum PNG: 8 (sig) + 4 (len) + 4 (IHDR) + 4 (w) + 4 (h) = 24 bytes
        if (pngBytes.Length < 24)
            return null;

        // Width at offset 16, height at offset 20 (big-endian)
        var w = (pngBytes[16] << 24) | (pngBytes[17] << 16) | (pngBytes[18] << 8) | pngBytes[19];
        var h = (pngBytes[20] << 24) | (pngBytes[21] << 16) | (pngBytes[22] << 8) | pngBytes[23];

        return w > 0 && h > 0 ? (w, h) : null;
    }
}
