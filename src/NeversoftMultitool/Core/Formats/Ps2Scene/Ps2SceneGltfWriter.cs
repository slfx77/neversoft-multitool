using System.Numerics;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Memory;
using SharpGLTF.Scenes;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace NeversoftMultitool.Core.Formats.Ps2Scene;

using VERTEX = VertexBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty>;

/// <summary>
/// Writes parsed PS2 scene data (MDL/SKIN/GEOM) to glTF 2.0 (.glb) files.
/// Uses ADC-based triangle strip conversion: each vertex's IsStripRestart flag
/// controls whether a triangle is drawn (ADC=0 → draw, ADC=0x8000 → skip).
/// </summary>
public static class Ps2SceneGltfWriter
{
    /// <summary>
    /// Minimum FIX value (0-128 scale) at or above which FixBlend materials are treated
    /// as OPAQUE instead of BLEND. 96/128 = 75% opacity. High-FIX materials are nearly
    /// opaque; marking them BLEND causes z-sorting artifacts in glTF viewers that don't
    /// depth-sort transparent geometry.
    /// </summary>
    private const int FixBlendOpaqueThreshold = 96;

    /// <summary>
    /// Delegate that resolves a texture checksum to PNG bytes for embedding in glTF.
    /// Returns null if the texture cannot be resolved.
    /// </summary>
    public delegate byte[]? TextureProvider(uint textureChecksum);

    /// <summary>
    /// Delegate that resolves a raw TEX0_1 GS register value to a texture checksum.
    /// Used for THPS4 GEOM files where CGeomNode.texture_checksum is always 0
    /// and textures are identified by VRAM addresses embedded in the DMA chain.
    /// The group checksum disambiguates double-buffered VRAM banks where different
    /// texture groups reuse the same TBP/CBP addresses.
    /// Returns 0 if the TEX0 value cannot be resolved.
    /// </summary>
    public delegate uint Tex0Resolver(ulong dmaTex0, uint groupChecksum);

    /// <summary>
    /// Writes a parsed PS2 scene to a .glb file.
    /// </summary>
    /// <param name="ps2Scene">Parsed PS2 scene data.</param>
    /// <param name="outputPath">Output .glb file path.</param>
    /// <param name="textureProvider">Optional callback to resolve texture checksums to PNG bytes.</param>
    /// <returns>Total number of triangles written.</returns>
    public static int Write(Ps2Scene ps2Scene, string outputPath,
        TextureProvider? textureProvider = null)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var scene = new SceneBuilder();
        var materialCache = new Dictionary<uint, MaterialBuilder>();
        var totalTriangles = 0;

        foreach (var group in ps2Scene.MeshGroups)
        {
            if (group.Meshes.Count == 0) continue;

            var groupName = QbKey.TryResolve(group.Checksum) ?? $"group_{group.Checksum:X8}";

            foreach (var mesh in group.Meshes)
            {
                if (mesh.Vertices.Length < 3) continue;

                var gltfMesh = BuildMesh(groupName, mesh,
                    ps2Scene.Materials, materialCache, textureProvider, out var tris);
                if (tris == 0) continue;

                totalTriangles += tris;
                var node = new NodeBuilder(groupName);
                scene.AddRigidMesh(gltfMesh, node);
            }
        }

        if (totalTriangles == 0)
            return 0;

        var model = scene.ToGltf2();
        model.SaveGLB(outputPath);

        return totalTriangles;
    }

    /// <summary>
    /// Writes a parsed PS2 scene with skeleton to a skinned .glb file.
    /// Creates bone hierarchy, JOINTS_0/WEIGHTS_0 vertex attributes, and inverse bind matrices.
    /// </summary>
    public static int WriteSkinned(Ps2Scene ps2Scene, Ps2Skeleton skeleton, string outputPath,
        TextureProvider? textureProvider = null)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var scene = new SceneBuilder();
        var materialCache = new Dictionary<uint, MaterialBuilder>();
        var totalTriangles = 0;

        // Build skeleton hierarchy as NodeBuilder tree
        var jointNodes = new NodeBuilder[skeleton.Bones.Length];
        for (var i = 0; i < skeleton.Bones.Length; i++)
        {
            var bone = skeleton.Bones[i];
            var boneName = QbKey.TryResolve(bone.NameChecksum) ?? $"bone_{bone.NameChecksum:X8}";

            if (bone.ParentIndex < 0)
            {
                // Root bone
                jointNodes[i] = new NodeBuilder(boneName);
            }
            else
            {
                // Child bone — create under parent
                jointNodes[i] = jointNodes[bone.ParentIndex].CreateNode(boneName);
            }

            // Set local transform from skeleton neutral pose
            jointNodes[i].LocalTransform = new SharpGLTF.Transforms.AffineTransform(
                null,  // scale
                bone.LocalRotation,
                bone.LocalTranslation);
        }

        // Build skinned mesh with VertexJoints4
        var gltfMesh = new MeshBuilder<VertexPositionNormal, VertexColor1Texture1, VertexJoints4>("skinned_mesh");

        foreach (var group in ps2Scene.MeshGroups)
        {
            if (group.Meshes.Count == 0) continue;

            foreach (var mesh in group.Meshes)
            {
                if (mesh.Vertices.Length < 3) continue;

                var material = GetOrCreateMaterial(ps2Scene.Materials, mesh.MaterialChecksum,
                    materialCache, textureProvider);
                var prim = gltfMesh.UsePrimitive(material);

                var tris = AddSkinnedTriangleStrip(prim, mesh.Vertices);
                totalTriangles += tris;
            }
        }

        if (totalTriangles == 0)
            return 0;

        // Add the skinned mesh with joint bindings
        scene.AddSkinnedMesh(gltfMesh, Matrix4x4.Identity, jointNodes);

        var model = scene.ToGltf2();
        model.SaveGLB(outputPath);

        return totalTriangles;
    }

    /// <summary>
    /// Converts ADC-flagged triangle strips to individual triangles with joint/weight data.
    /// </summary>
    private static int AddSkinnedTriangleStrip(
        PrimitiveBuilder<MaterialBuilder, VertexPositionNormal, VertexColor1Texture1, VertexJoints4> prim,
        Ps2Vertex[] verts)
    {
        var count = 0;
        for (var i = 2; i < verts.Length; i++)
        {
            if (verts[i].IsStripRestart) continue;

            VertexBuilder<VertexPositionNormal, VertexColor1Texture1, VertexJoints4> va, vb, vc;
            if (i % 2 == 0)
            {
                va = MakeSkinnedVertex(verts[i - 2]);
                vb = MakeSkinnedVertex(verts[i - 1]);
                vc = MakeSkinnedVertex(verts[i]);
            }
            else
            {
                va = MakeSkinnedVertex(verts[i - 1]);
                vb = MakeSkinnedVertex(verts[i - 2]);
                vc = MakeSkinnedVertex(verts[i]);
            }

            prim.AddTriangle(va, vb, vc);
            count++;
        }
        return count;
    }

    /// <summary>
    /// Creates a glTF vertex with skinning data from a PS2 vertex.
    /// </summary>
    private static VertexBuilder<VertexPositionNormal, VertexColor1Texture1, VertexJoints4> MakeSkinnedVertex(
        in Ps2Vertex v)
    {
        // Position and normal (same as rigid MakeVertex)
        var pos = v.Position;
        var normal = Vector3.UnitY;
        if (v.HasNormal)
        {
            var len = v.Normal.Length();
            normal = len > 0.001f ? v.Normal / len : Vector3.UnitY;
        }

        var r = Math.Min(v.R / 128f, 1f);
        var g = Math.Min(v.G / 128f, 1f);
        var b = Math.Min(v.B / 128f, 1f);
        var a = Math.Min(v.A / 128f, 1f);
        var uv = v.HasUV ? new Vector2(v.U, 1f - v.V) : Vector2.Zero;

        // Build joint weights — up to 3 bone influences
        var skinning = v.HasSkinData
            ? new VertexJoints4(
                (v.BoneIndex0, v.BoneWeight0),
                (v.BoneIndex1, v.BoneWeight1),
                (v.BoneIndex2, v.BoneWeight2))
            : new VertexJoints4((0, 1f)); // fallback: 100% bone 0

        return new VertexBuilder<VertexPositionNormal, VertexColor1Texture1, VertexJoints4>(
            new VertexPositionNormal(pos, normal),
            new VertexColor1Texture1(new Vector4(r, g, b, a), uv),
            skinning);
    }

    /// <summary>
    /// Writes a parsed PS2 GEOM scene to a .glb file.
    /// GEOM leaves have texture checksums but no full material data.
    /// Material properties (clamp, alpha) are extracted from DMA chain GS registers.
    /// For THPS4 files where texture_checksum is 0, the tex0Resolver maps
    /// DMA chain TEX0 register values to texture checksums via VRAM simulation.
    /// </summary>
    public static int Write(Ps2GeomScene geomScene, string outputPath,
        TextureProvider? textureProvider = null, Tex0Resolver? tex0Resolver = null)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var scene = new SceneBuilder();
        var materialCache = new Dictionary<GeomMaterialKey, MaterialBuilder>();
        var totalTriangles = 0;

        foreach (var leaf in geomScene.Leaves)
        {
            if (leaf.Vertices.Length < 3) continue;

            // Resolve texture checksum: use node field if non-zero (THUG/THUG2),
            // otherwise fall back to DMA TEX0 VRAM lookup (THPS4)
            var texChecksum = leaf.TextureChecksum;
            if (texChecksum == 0 && leaf.DmaTex0 != 0 && tex0Resolver != null)
                texChecksum = tex0Resolver(leaf.DmaTex0, leaf.GroupChecksum);

            var name = QbKey.TryResolve(leaf.Checksum) ?? $"node_{leaf.Checksum:X8}";
            var material = GetOrCreateGeomMaterial(texChecksum, leaf.DmaClamp1,
                leaf.DmaAlpha1, leaf.DmaTest1, materialCache, textureProvider);

            var gltfMesh = new MeshBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty>(name);
            var prim = gltfMesh.UsePrimitive(material);
            var tris = AddTriangleStrip(prim, leaf.Vertices);

            if (tris == 0) continue;

            totalTriangles += tris;
            var node = new NodeBuilder(name);
            scene.AddRigidMesh(gltfMesh, node);
        }

        if (totalTriangles == 0)
            return 0;

        var model = scene.ToGltf2();
        model.SaveGLB(outputPath);

        return totalTriangles;
    }

    /// <summary>
    /// Converts ADC-flagged triangle strips to individual triangles.
    /// Shared between MDL/SKIN and GEOM pipelines.
    /// </summary>
    private static int AddTriangleStrip(
        PrimitiveBuilder<MaterialBuilder, VertexPositionNormal, VertexColor1Texture1, VertexEmpty> prim,
        Ps2Vertex[] verts)
    {
        var count = 0;
        for (var i = 2; i < verts.Length; i++)
        {
            if (verts[i].IsStripRestart) continue;

            VERTEX va, vb, vc;
            if (i % 2 == 0)
            {
                va = MakeVertex(verts[i - 2]);
                vb = MakeVertex(verts[i - 1]);
                vc = MakeVertex(verts[i]);
            }
            else
            {
                va = MakeVertex(verts[i - 1]);
                vb = MakeVertex(verts[i - 2]);
                vc = MakeVertex(verts[i]);
            }

            prim.AddTriangle(va, vb, vc);
            count++;
        }
        return count;
    }

    /// <summary>
    /// Composite key for GEOM material caching. Different leaves with the same texture
    /// may need different materials if they have different clamp or alpha settings.
    /// </summary>
    private readonly record struct GeomMaterialKey(
        uint TextureChecksum, byte ClampBits, byte AlphaBlend, byte AlphaRef);

    private static MaterialBuilder GetOrCreateGeomMaterial(
        uint textureChecksum, ulong clamp1, ulong alpha1, ulong test1,
        Dictionary<GeomMaterialKey, MaterialBuilder> cache,
        TextureProvider? textureProvider)
    {
        // Decode GS register fields for material key
        var clampBits = (byte)(clamp1 & 0x0F); // WMS (bits 0-1) + WMT (bits 2-3)
        var alphaBlend = (byte)(alpha1 & 0xFF); // A,B,C,D fields
        var ate = (test1 & 1) != 0;
        var aref = (byte)((test1 >> 4) & 0xFF);

        var key = new GeomMaterialKey(textureChecksum, clampBits, alphaBlend, aref);
        if (cache.TryGetValue(key, out var existing))
            return existing;

        var matName = textureChecksum != 0
            ? QbKey.TryResolve(textureChecksum) ?? $"tex_{textureChecksum:X8}"
            : "default";

        // Decode blend equation fields from ALPHA_1 register.
        // Cv = ((A-B)*C)>>7 + D where A/B/D select Cs/Cd/0, C selects As/Ad/FIX.
        var aField = alphaBlend & 0x03;
        var bField = (alphaBlend >> 2) & 0x03;
        var cField = (alphaBlend >> 4) & 0x03;
        var dField = (alphaBlend >> 6) & 0x03;

        // Additive: A=Cs(0), B=0(2), D=Cd(1) → Cs*C + Cd (foam, glow, water spray)
        var isAdditive = aField == 0 && bField == 2 && dField == 1;
        // Subtractive: A=0(2), B=Cs(0), D=Cd(1) → (0-Cs)*C + Cd = Cd - Cs*C (shadows)
        var isSubtractive = aField == 2 && bField == 0 && dField == 1;

        var builder = new MaterialBuilder(matName)
            .WithUnlitShader()
            .WithBaseColor(Vector4.One)
            .WithDoubleSide(true);

        if (textureProvider != null && textureChecksum != 0)
        {
            var pngBytes = textureProvider(textureChecksum);
            if (pngBytes != null)
            {
                // For additive blend, convert texture to luminance-alpha:
                // RGB → white, Alpha = max(R,G,B). This approximates additive
                // blending (black=invisible, white=bright overlay) using glTF BLEND
                // which doesn't natively support additive.
                if (isAdditive)
                    pngBytes = ConvertBlendTexture(pngBytes, 255, 255, 255);
                else if (isSubtractive)
                    pngBytes = ConvertBlendTexture(pngBytes, 0, 0, 0);

                var memImage = new MemoryImage(pngBytes);
                builder.WithChannelImage(KnownChannel.BaseColor, memImage);

                // Set texture wrap mode from CLAMP_1 register.
                // WMS/WMT: 0=REPEAT, 1=CLAMP. Only simple modes used in practice.
                var wms = clampBits & 0x03;
                var wmt = (clampBits >> 2) & 0x03;
                if (wms != 0 || wmt != 0)
                {
                    var wrapS = wms != 0
                        ? SharpGLTF.Schema2.TextureWrapMode.CLAMP_TO_EDGE
                        : SharpGLTF.Schema2.TextureWrapMode.REPEAT;
                    var wrapT = wmt != 0
                        ? SharpGLTF.Schema2.TextureWrapMode.CLAMP_TO_EDGE
                        : SharpGLTF.Schema2.TextureWrapMode.REPEAT;
                    builder.GetChannel(KnownChannel.BaseColor)
                        .Texture
                        .WithSampler(wrapS, wrapT);
                }
            }
        }

        // Alpha handling from GS registers.
        // ALPHA_1 low byte: 0x0A/0x1A = opaque (output=Cs). Anything else = blending.
        // TEST_1 alpha test: PackTEST(1,AGEQUAL,Aref,KEEP,0,0,1,ZGEQUAL)
        //   AREF=0: alpha >= 0 always passes → truly OPAQUE.
        //   AREF=1: discards alpha=0 pixels → MASK cutout (fences, foliage, etc.)
        //   AREF>=2: higher-threshold cutout → MASK with visible cutoff.
        var isOpaqueBlend = alphaBlend is 0x0A or 0x1A or 0x00;
        if (isAdditive)
        {
            builder.WithAlpha(AlphaMode.BLEND);

            // For FIX-mode additive (Cs*FIX/128 + Cd), scale brightness
            if (cField == 2)
            {
                var fix = (byte)((alpha1 >> 32) & 0xFF);
                var intensity = Math.Min(fix / 128f, 1f);
                builder.WithBaseColor(new Vector4(intensity, intensity, intensity, 1f));
            }
        }
        else if (isSubtractive)
        {
            // Subtractive: Cd - Cs*FIX/128. Texture converted to black + luminance alpha.
            // BLEND output = black * alpha + dst * (1-alpha) = dst * (1-alpha) → darkens.
            builder.WithAlpha(AlphaMode.BLEND);

            if (cField == 2)
            {
                var fix = (byte)((alpha1 >> 32) & 0xFF);
                var opacity = Math.Min(fix / 128f, 1f);
                builder.WithBaseColor(new Vector4(0f, 0f, 0f, opacity));
            }
            else
            {
                builder.WithBaseColor(new Vector4(0f, 0f, 0f, 1f));
            }
        }
        else if (!isOpaqueBlend)
        {
            // Fixed-blend opacity: C field (bits 4-5) == 2 means FIX mode.
            // FIX value in ALPHA_1 bits 32-39. Opacity = FIX/128.
            // High FIX values (>= threshold) are treated as OPAQUE to avoid
            // z-sorting artifacts in glTF viewers that don't depth-sort BLEND.
            if (cField == 2)
            {
                var fix = (byte)((alpha1 >> 32) & 0xFF);
                if (fix < FixBlendOpaqueThreshold)
                {
                    builder.WithAlpha(AlphaMode.BLEND);
                    builder.WithBaseColor(new Vector4(1f, 1f, 1f, fix / 128f));
                }
                // else: fix >= threshold → leave as default OPAQUE
            }
            else
            {
                // Non-FIX blend modes (source-alpha, dest-alpha) → always BLEND
                builder.WithAlpha(AlphaMode.BLEND);
            }
        }
        else if (ate && aref >= 1)
        {
            builder.WithAlpha(AlphaMode.MASK, aref / 255f);
        }

        cache[key] = builder;
        return builder;
    }

    /// <summary>
    /// Converts a texture for additive/subtractive blend approximation in glTF.
    /// Sets alpha = max(R,G,B) (luminance) and RGB = specified color.
    /// Additive (white): bright areas render as opaque white overlay, dark = transparent.
    /// Subtractive (black): bright areas render as dark shadow overlay, dark = transparent.
    /// </summary>
    private static byte[] ConvertBlendTexture(byte[] pngBytes, byte r, byte g, byte b)
    {
        using var image = Image.Load<Rgba32>(pngBytes);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var p = row[x];
                    var luminance = Math.Max(p.R, Math.Max(p.G, p.B));
                    row[x] = new Rgba32(r, g, b, (byte)((luminance * p.A) / 255));
                }
            }
        });

        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }

    private static MeshBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty> BuildMesh(
        string name,
        Ps2Mesh mesh,
        List<Ps2Material> materials,
        Dictionary<uint, MaterialBuilder> materialCache,
        TextureProvider? textureProvider,
        out int triangleCount)
    {
        var gltfMesh = new MeshBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty>(name);
        var material = GetOrCreateMaterial(materials, mesh.MaterialChecksum, materialCache, textureProvider);
        var prim = gltfMesh.UsePrimitive(material);

        triangleCount = AddTriangleStrip(prim, mesh.Vertices);

        return gltfMesh;
    }

    private static VERTEX MakeVertex(Ps2Vertex v)
    {
        var pos = v.Position;
        var normal = Vector3.UnitY;
        if (v.HasNormal)
        {
            var len = v.Normal.Length();
            normal = len > 0.001f ? v.Normal / len : Vector3.UnitY;
        }

        // Vertex colors: PS2 uses 0-128 range (128 = full bright), divide by 128
        var r = Math.Min(v.R / 128f, 1f);
        var g = Math.Min(v.G / 128f, 1f);
        var b = Math.Min(v.B / 128f, 1f);
        var a = Math.Min(v.A / 128f, 1f);

        // UV: flip V. PS2 GS samples bottom-up but our TEX pipeline flips
        // pixel data to top-down PNGs; mesh UVs remain in bottom-up space.
        var uv = v.HasUV ? new Vector2(v.U, 1f - v.V) : Vector2.Zero;

        return new VERTEX(
            new VertexPositionNormal(pos, normal),
            new VertexColor1Texture1(new Vector4(r, g, b, a), uv));
    }

    private static MaterialBuilder GetOrCreateMaterial(
        List<Ps2Material> materials,
        uint matChecksum,
        Dictionary<uint, MaterialBuilder> cache,
        TextureProvider? textureProvider)
    {
        if (cache.TryGetValue(matChecksum, out var existing))
            return existing;

        var mat = materials.FirstOrDefault(m => m.Checksum == matChecksum);
        var matName = mat != null
            ? QbKey.TryResolve(mat.Checksum) ?? $"mat_{mat.Checksum:X8}"
            : $"mat_{matChecksum:X8}";

        var builder = new MaterialBuilder(matName)
            .WithUnlitShader()
            .WithBaseColor(Vector4.One)
            .WithDoubleSide(true);

        // Embed texture if provider is available and material has a texture reference
        if (textureProvider != null && mat?.TextureChecksum is > 0)
        {
            var pngBytes = textureProvider(mat.TextureChecksum);
            if (pngBytes != null)
            {
                var memImage = new MemoryImage(pngBytes);
                builder.WithChannelImage(KnownChannel.BaseColor, memImage);

                // Set texture wrap mode from material clamp flags.
                // PS2: 0=repeat, non-zero=clamp. glTF defaults to REPEAT.
                if (mat.ClampU || mat.ClampV)
                {
                    var wrapS = mat.ClampU
                        ? SharpGLTF.Schema2.TextureWrapMode.CLAMP_TO_EDGE
                        : SharpGLTF.Schema2.TextureWrapMode.REPEAT;
                    var wrapT = mat.ClampV
                        ? SharpGLTF.Schema2.TextureWrapMode.CLAMP_TO_EDGE
                        : SharpGLTF.Schema2.TextureWrapMode.REPEAT;
                    builder.GetChannel(KnownChannel.BaseColor)
                        .Texture
                        .WithSampler(wrapS, wrapT);
                }
            }
        }

        // Alpha handling (from THUG mesh.cpp line 402):
        // PS2 GS always runs alpha test: PackTEST(1,AGEQUAL,Aref,KEEP,0,0,1,ZGEQUAL)
        //   Aref=0: alpha >= 0 always passes → truly OPAQUE.
        //   Aref=1: discards alpha=0 pixels → MASK cutout (fences, foliage, etc.)
        //   Aref>=2: higher-threshold cutout → MASK with visible cutoff.
        // MATFLAG_TRANSPARENT + RegALPHA: true blending (glass, shadows, ghosts).
        // RegALPHA FIXED_BLEND: (Cs-Cd)*FIX/128+Cd → apply FIX/128 as material opacity.
        if (mat != null)
        {
            var isTransparent = (mat.Flags & (uint)Ps2MaterialFlags.Transparent) != 0;
            if (isTransparent)
            {
                // Check if this is a high-opacity fixed-blend that should be OPAQUE.
                var fixedOpacity = mat.FixedBlendOpacity;
                if (fixedOpacity.HasValue && fixedOpacity.Value >= FixBlendOpaqueThreshold / 128f)
                {
                    // Near-opaque fixed blend: leave as default OPAQUE to avoid z-sorting artifacts.
                }
                else
                {
                    builder.WithAlpha(AlphaMode.BLEND);

                    // Apply fixed-blend opacity from RegALPHA if available.
                    // E.g., ghost models use FIX=50 → 39% opacity.
                    if (fixedOpacity.HasValue)
                    {
                        builder.WithBaseColor(new Vector4(1f, 1f, 1f, fixedOpacity.Value));
                    }
                }
            }
            else if (mat.AlphaRef >= 1)
            {
                builder.WithAlpha(AlphaMode.MASK, mat.AlphaRef / 255f);
            }
        }

        cache[matChecksum] = builder;
        return builder;
    }
}
