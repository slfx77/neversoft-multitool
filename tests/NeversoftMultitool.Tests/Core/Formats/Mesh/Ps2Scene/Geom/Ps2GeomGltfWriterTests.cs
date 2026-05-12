using System.Numerics;
using System.Text.Json;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Scene;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SharpGLTF.Scenes;
using SharpGLTF.Schema2;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Ps2Scene.Geom;

public sealed class Ps2GeomGltfWriterTests
{
    [Fact]
    public void Write_DeduplicatesIdenticalGeomLeavesIntoSingleMeshBucket()
    {
        var leaf = new Ps2GeomLeaf
        {
            Checksum = 0,
            TextureChecksum = 0x12345678,
            GroupChecksum = 0,
            Colour = 0,
            BoundingSphere = Vector4.Zero,
            Vertices =
            [
                MakeVertex(0, 0),
                MakeVertex(1, 0),
                MakeVertex(0, 1),
                MakeVertex(1, 1)
            ],
            DmaTex0 = 0,
            DmaClamp1 = 0,
            DmaAlpha1 = 0x0A,
            DmaTest1 = 0
        };

        var geom = new Ps2GeomScene
        {
            Leaves = [leaf, leaf]
        };

        var tempDir = Path.Combine(Path.GetTempPath(), "NsMultitool_Test_Ps2Geom_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempDir);
            var outputFile = Path.Combine(tempDir, "geom_dedup.glb");

            var triangles = Ps2GeomGltfWriter.Write(geom, outputFile);

            Assert.Equal(2, triangles);
            Assert.True(File.Exists(outputFile), "GLB file was not created");

            var model = ModelRoot.Load(outputFile);
            Assert.Single(model.LogicalMeshes);
            Assert.Single(model.LogicalNodes);

            var primitive = model.LogicalMeshes.Single().Primitives.Single();
            Assert.Equal(6, primitive.IndexAccessor!.Count);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Write_WorldZoneSkipsHugeOriginCenteredHelperLeaves()
    {
        var helperLeaf = new Ps2GeomLeaf
        {
            Checksum = 0,
            TextureChecksum = 0,
            GroupChecksum = 0,
            Colour = 0,
            BoundingSphere = Vector4.Zero,
            Vertices =
            [
                MakeHelperVertex(-600, 0, 600, true),
                MakeHelperVertex(600, 0, 600, true),
                MakeHelperVertex(-600, 0, -600, false),
                MakeHelperVertex(600, 0, -600, false)
            ],
            DmaTex0 = 0x2,
            DmaClamp1 = 0,
            DmaAlpha1 = 0x44,
            DmaTest1 = 0x5001B
        };

        var translatedLeaf = new Ps2GeomLeaf
        {
            Checksum = 0,
            TextureChecksum = 0,
            GroupChecksum = 0,
            Colour = 0,
            BoundingSphere = Vector4.Zero,
            Vertices =
            [
                MakeHelperVertex(10, 0, 40, true),
                MakeHelperVertex(90, 0, 40, true),
                MakeHelperVertex(10, 0, -40, false),
                MakeHelperVertex(90, 0, -40, false)
            ],
            DmaTex0 = 0x3,
            DmaClamp1 = 0,
            DmaAlpha1 = 0x44,
            DmaTest1 = 0x5001B
        };

        var validLargeFlatLeaf = new Ps2GeomLeaf
        {
            Checksum = 0,
            TextureChecksum = 0,
            GroupChecksum = 0,
            Colour = 0,
            BoundingSphere = Vector4.Zero,
            Vertices =
            [
                MakeHelperVertex(7500, 0, 2500, true),
                MakeHelperVertex(12500, 0, 2500, true),
                MakeHelperVertex(7500, 0, -2500, false),
                MakeHelperVertex(12500, 0, -2500, false)
            ],
            DmaTex0 = 0x4,
            DmaClamp1 = 0,
            DmaAlpha1 = 0x44,
            DmaTest1 = 0x5001B
        };

        var baseLeaves = Enumerable.Range(0, 500)
            .Select(i => new Ps2GeomLeaf
            {
                Checksum = 0,
                TextureChecksum = 0x12345678,
                GroupChecksum = 0,
                Colour = 0,
                BoundingSphere = Vector4.Zero,
                Vertices =
                [
                    MakeVertex(i * 2, 0),
                    MakeVertex(i * 2 + 1, 0),
                    MakeVertex(i * 2, 1),
                    MakeVertex(i * 2 + 1, 1)
                ],
                DmaTex0 = 0x1,
                DmaClamp1 = 0,
                DmaAlpha1 = 0x0A,
                DmaTest1 = 0
            });

        var leaves = baseLeaves
            .Concat([helperLeaf, translatedLeaf, validLargeFlatLeaf])
            .ToArray();

        var geom = new Ps2GeomScene { Leaves = [.. leaves] };

        var tempDir = Path.Combine(Path.GetTempPath(), "NsMultitool_Test_Ps2Geom_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempDir);
            var outputFile = Path.Combine(tempDir, "geom_worldzone_filter.glb");

            var triangles = Ps2GeomGltfWriter.Write(geom, outputFile);

            Assert.Equal(1004, triangles);
            Assert.True(File.Exists(outputFile), "GLB file was not created");

            var model = ModelRoot.Load(outputFile);
            Assert.True(model.LogicalMeshes.Count >= 2, "Expected translated helper geometry to remain");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void AppendLeafIdDebugScene_EmitsColorMappedLeafRecords()
    {
        var leafA = MakeTexturedLeaf(0x11111111, isBillboard: false);
        var leafB = MakeTexturedLeaf(0x22222222, isBillboard: true);
        var geom = new Ps2GeomScene { Leaves = [leafA, leafB] };
        var scene = new SceneBuilder();
        var records = new List<Ps2GeomLeafIdDebugRecord>();
        var nextId = 1;

        var triangles = Ps2GeomGltfWriter.AppendLeafIdDebugScene(
            scene,
            geom,
            [(new Vector3(10, 0, 0), Quaternion.Identity)],
            leafFilter: null,
            "test_mdl",
            records,
            ref nextId);

        Assert.Equal(4, triangles);
        Assert.Equal(3, nextId);
        Assert.Equal(2, records.Count);
        Assert.Equal([1, 2], records.Select(static record => record.Id).ToArray());
        Assert.All(records, static record => Assert.StartsWith("#", record.ColorHex));
        Assert.Equal(new Vector3(10, 0, 0), records[0].Min);
        Assert.Equal(new Vector3(11, 1, 0), records[0].Max);
        Assert.True(records[1].IsBillboard);

        var model = scene.ToGltf2();
        Assert.Equal(2, model.LogicalMaterials.Count);
        Assert.Equal(2, model.LogicalMeshes.Count);
    }

    [Fact]
    public void AppendToScene_LocalizeMeshOriginsMovesBucketCenterToNodeTranslation()
    {
        var leaf = new Ps2GeomLeaf
        {
            Checksum = 0,
            TextureChecksum = 0x12345678,
            GroupChecksum = 0,
            Colour = 0,
            BoundingSphere = Vector4.Zero,
            Vertices =
            [
                MakeVertex(100, 200),
                MakeVertex(101, 200),
                MakeVertex(100, 201),
                MakeVertex(101, 201)
            ],
            DmaTex0 = 0,
            DmaClamp1 = 0,
            DmaAlpha1 = 0x0A,
            DmaTest1 = 0
        };
        var geom = new Ps2GeomScene { Leaves = [leaf] };
        var scene = new SceneBuilder();

        var triangles = Ps2GeomGltfWriter.AppendToScene(
            scene,
            geom,
            [(new Vector3(10, -20, 2), Quaternion.Identity)],
            localizeMeshOrigins: true);

        Assert.Equal(2, triangles);

        var model = scene.ToGltf2();
        var node = Assert.Single(model.LogicalNodes);
        Assert.Equal(new Vector3(110.5f, 180.5f, 2f), node.LocalTransform.Translation);
    }

    [Fact]
    public void AppendToScene_CoordinateScaleScalesLocalizedNodeTranslation()
    {
        var leaf = new Ps2GeomLeaf
        {
            Checksum = 0,
            TextureChecksum = 0x12345678,
            GroupChecksum = 0,
            Colour = 0,
            BoundingSphere = Vector4.Zero,
            Vertices =
            [
                MakeVertex(100, 200),
                MakeVertex(101, 200),
                MakeVertex(100, 201),
                MakeVertex(101, 201)
            ],
            DmaTex0 = 0,
            DmaClamp1 = 0,
            DmaAlpha1 = 0x0A,
            DmaTest1 = 0
        };
        var geom = new Ps2GeomScene { Leaves = [leaf] };
        var scene = new SceneBuilder();

        var triangles = Ps2GeomGltfWriter.AppendToScene(
            scene,
            geom,
            [(new Vector3(10, -20, 2), Quaternion.Identity)],
            localizeMeshOrigins: true,
            coordinateScale: 0.01f);

        Assert.Equal(2, triangles);

        var model = scene.ToGltf2();
        var translation = Assert.Single(model.LogicalNodes).LocalTransform.Translation;
        Assert.Equal(1.105f, translation.X, 4);
        Assert.Equal(1.805f, translation.Y, 4);
        Assert.Equal(0.02f, translation.Z, 4);
    }

    [Fact]
    public void Write_DarkStandardBlendOverlayExportsAsBlendNotMask()
    {
        const uint textureChecksum = 0xAABBCCDD;
        var leaf = new Ps2GeomLeaf
        {
            Checksum = 0,
            TextureChecksum = textureChecksum,
            GroupChecksum = 0,
            Colour = 0,
            BoundingSphere = Vector4.Zero,
            Vertices =
            [
                MakeVertex(0, 0),
                MakeVertex(1, 0),
                MakeVertex(0, 1),
                MakeVertex(1, 1)
            ],
            DmaTex0 = 0,
            DmaClamp1 = 0,
            DmaAlpha1 = 0x44,
            DmaTest1 = 0
        };

        var geom = new Ps2GeomScene { Leaves = [leaf] };
        var png = MakeDarkBimodalOverlayPng();
        var tempDir = Path.Combine(Path.GetTempPath(), "NsMultitool_Test_Ps2Geom_" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            Directory.CreateDirectory(tempDir);
            var outputFile = Path.Combine(tempDir, "geom_dark_overlay.glb");

            var triangles = Ps2GeomGltfWriter.Write(geom, outputFile,
                checksum => checksum == textureChecksum ? png : null);

            Assert.Equal(2, triangles);
            Assert.Equal("BLEND", ReadFirstMaterialAlphaMode(outputFile));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Write_GreyBimodalShadowOverlayExportsAsSoftBlend()
    {
        const uint textureChecksum = 0x4A7F5C17;
        var leaf = MakeTexturedLeaf(textureChecksum, isBillboard: false, test1: 0x0005001B);
        var geom = new Ps2GeomScene { Leaves = [leaf] };
        var png = MakeGreyBimodalShadowPng();
        var tempDir = Path.Combine(Path.GetTempPath(), "NsMultitool_Test_Ps2Geom_" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            Directory.CreateDirectory(tempDir);
            var outputFile = Path.Combine(tempDir, "geom_grey_shadow.glb");

            var triangles = Ps2GeomGltfWriter.Write(geom, outputFile,
                checksum => checksum == textureChecksum ? png : null);

            Assert.Equal(2, triangles);
            Assert.Equal("BLEND", ReadFirstMaterialAlphaMode(outputFile));
            Assert.True(ReadFirstImageAlphaStats(outputFile).HasIntermediateAlpha);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Write_TransparentRgbStandardBlendTextureIsRecoveredAsOpaque()
    {
        const uint textureChecksum = 0xE6ED88DE;
        var leaf = MakeTexturedLeaf(textureChecksum, isBillboard: false, test1: 0x0005001B);
        var geom = new Ps2GeomScene { Leaves = [leaf] };
        var png = MakeTransparentUsefulRgbPng();
        var tempDir = Path.Combine(Path.GetTempPath(), "NsMultitool_Test_Ps2Geom_" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            Directory.CreateDirectory(tempDir);
            var outputFile = Path.Combine(tempDir, "geom_transparent_rgb.glb");

            var triangles = Ps2GeomGltfWriter.Write(geom, outputFile,
                checksum => checksum == textureChecksum ? png : null);

            Assert.Equal(2, triangles);
            Assert.Equal("OPAQUE", ReadFirstMaterialAlphaMode(outputFile));
            Assert.Equal(255, ReadFirstImageFirstPixel(outputFile).A);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Write_TextureSamplerUsesExplicitRepeatWrapWithoutForcedFiltering()
    {
        const uint textureChecksum = 0x34623305;
        var leaf = MakeTexturedLeaf(textureChecksum, isBillboard: false);
        var geom = new Ps2GeomScene { Leaves = [leaf] };
        var png = MakeSolidPng(new Rgba32(128, 96, 64, 255));
        var tempDir = Path.Combine(Path.GetTempPath(), "NsMultitool_Test_Ps2Geom_" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            Directory.CreateDirectory(tempDir);
            var outputFile = Path.Combine(tempDir, "geom_repeat_sampler.glb");

            var triangles = Ps2GeomGltfWriter.Write(geom, outputFile,
                checksum => checksum == textureChecksum ? png : null);

            Assert.Equal(2, triangles);
            var sampler = ReadFirstTextureSampler(outputFile);
            Assert.Null(sampler.MagFilter);
            Assert.Null(sampler.MinFilter);
            Assert.Equal(10497, sampler.WrapS);
            Assert.Equal(10497, sampler.WrapT);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Write_WorldzoneStandaloneSparseNeutralMaskLayerIsSkipped()
    {
        const uint textureChecksum = 0x488EECA6;
        var leaf = new Ps2GeomLeaf
        {
            Checksum = 0,
            TextureChecksum = textureChecksum,
            GroupChecksum = 0,
            Colour = 0,
            BoundingSphere = Vector4.Zero,
            Vertices =
            [
                MakeVertex(0, 0),
                MakeVertex(500, 0),
                MakeVertex(0, 500),
                MakeVertex(500, 500)
            ],
            DmaTex0 = 0,
            DmaClamp1 = 0,
            DmaAlpha1 = 0x44,
            DmaTest1 = 0x5001B
        };
        var leaves = Enumerable.Range(0, 499)
            .Select(_ => new Ps2GeomLeaf
            {
                Checksum = 0,
                TextureChecksum = 0,
                GroupChecksum = 0,
                Colour = 0,
                BoundingSphere = Vector4.Zero,
                Vertices = []
            })
            .ToList();
        leaves.Add(leaf);
        var geom = new Ps2GeomScene { Leaves = leaves };
        var png = MakeSparseNeutralMaskPng();
        var tempDir = Path.Combine(Path.GetTempPath(), "NsMultitool_Test_Ps2Geom_" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            Directory.CreateDirectory(tempDir);
            var outputFile = Path.Combine(tempDir, "geom_sparse_mask_layer.glb");

            var triangles = Ps2GeomGltfWriter.Write(geom, outputFile,
                checksum => checksum == textureChecksum ? png : null);

            Assert.Equal(0, triangles);
            Assert.False(File.Exists(outputFile));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Write_DitheredStandardBlendExportsAsSmoothedBlend()
    {
        const uint textureChecksum = 0x1234ABCD;
        var leaf = MakeTexturedLeaf(textureChecksum, isBillboard: false);
        var geom = new Ps2GeomScene { Leaves = [leaf] };
        var png = MakeDitheredWindowPng();
        var tempDir = Path.Combine(Path.GetTempPath(), "NsMultitool_Test_Ps2Geom_" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            Directory.CreateDirectory(tempDir);
            var outputFile = Path.Combine(tempDir, "geom_dithered_window.glb");

            var triangles = Ps2GeomGltfWriter.Write(geom, outputFile,
                checksum => checksum == textureChecksum ? png : null);

            Assert.Equal(2, triangles);
            Assert.Equal("BLEND", ReadFirstMaterialAlphaMode(outputFile));
            Assert.True(ReadFirstImageAlphaStats(outputFile).HasIntermediateAlpha);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Write_LowOpacityMaskPreservesDecodedTextureAlpha()
    {
        const uint textureChecksum = 0x0BADF00D;
        var leaf = MakeTexturedLeaf(textureChecksum, isBillboard: false);
        var geom = new Ps2GeomScene { Leaves = [leaf] };
        var png = MakeLowOpacityMaskPng();
        var tempDir = Path.Combine(Path.GetTempPath(), "NsMultitool_Test_Ps2Geom_" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            Directory.CreateDirectory(tempDir);
            var outputFile = Path.Combine(tempDir, "geom_low_alpha_mask.glb");

            var triangles = Ps2GeomGltfWriter.Write(geom, outputFile,
                checksum => checksum == textureChecksum ? png : null);

            var pixel = ReadFirstImageFirstPixel(outputFile);
            Assert.Equal(2, triangles);
            Assert.Equal("BLEND", ReadFirstMaterialAlphaMode(outputFile));
            Assert.Equal(132, pixel.A);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Write_DestinationAlphaLayerUsesPreviousCoplanarMask()
    {
        const uint maskChecksum = 0x11112222;
        const uint layerChecksum = 0x33334444;
        var maskLeaf = MakeTexturedLeaf(maskChecksum, isBillboard: false);
        var layerLeaf = MakeTexturedLeaf(layerChecksum, isBillboard: false, alpha1: 0x54);
        var geom = new Ps2GeomScene { Leaves = [maskLeaf, layerLeaf] };
        var maskPng = MakeLowOpacityMaskPng();
        var layerPng = MakeSolidPng(new Rgba32(0, 64, 255, 255));
        var tempDir = Path.Combine(Path.GetTempPath(), "NsMultitool_Test_Ps2Geom_" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            Directory.CreateDirectory(tempDir);
            var outputFile = Path.Combine(tempDir, "geom_dest_alpha_mask.glb");

            var triangles = Ps2GeomGltfWriter.Write(geom, outputFile,
                checksum => checksum switch
                {
                    maskChecksum => maskPng,
                    layerChecksum => layerPng,
                    _ => null
                });

            var firstPixels = ReadEmbeddedImageFirstPixels(outputFile);
            Assert.Equal(4, triangles);
            Assert.Contains(firstPixels, p => p.R == 0 && p.G == 64 && p.B == 255 && p.A == 132);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Write_DestinationAlphaLayerIgnoresSourceAlphaWhenAlphaTestPasses()
    {
        const uint maskChecksum = 0x11112222;
        const uint layerChecksum = 0x33334444;
        var maskLeaf = MakeTexturedLeaf(maskChecksum, isBillboard: false);
        var layerLeaf = MakeTexturedLeaf(
            layerChecksum,
            isBillboard: false,
            alpha1: 0x54,
            test1: MakeTest(atst: 5, aref: 0));
        var geom = new Ps2GeomScene { Leaves = [maskLeaf, layerLeaf] };
        var maskPng = MakeUniformAlphaMaskPng(192);
        var layerPng = MakeSolidPng(new Rgba32(0, 64, 255, 64));
        var tempDir = Path.Combine(Path.GetTempPath(), "NsMultitool_Test_Ps2Geom_" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            Directory.CreateDirectory(tempDir);
            var outputFile = Path.Combine(tempDir, "geom_dest_alpha_source_alpha_ignored.glb");

            var triangles = Ps2GeomGltfWriter.Write(geom, outputFile,
                checksum => checksum switch
                {
                    maskChecksum => maskPng,
                    layerChecksum => layerPng,
                    _ => null
                });

            var firstPixels = ReadEmbeddedImageFirstPixels(outputFile);
            Assert.Equal(4, triangles);
            Assert.Contains(firstPixels, p => p.R == 0 && p.G == 64 && p.B == 255 && p.A == 192);
            Assert.DoesNotContain(firstPixels, p => p.R == 0 && p.G == 64 && p.B == 255 && p.A == 48);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Write_DestinationAlphaLayerFindsLaterCoplanarMask()
    {
        const uint maskChecksum = 0x11112222;
        const uint layerChecksum = 0x33334444;
        var layerLeaf = MakeTexturedLeaf(layerChecksum, isBillboard: false, alpha1: 0x54);
        var maskLeaf = MakeTexturedLeaf(maskChecksum, isBillboard: false);
        var geom = new Ps2GeomScene { Leaves = [layerLeaf, maskLeaf] };
        var maskPng = MakeLowOpacityMaskPng();
        var layerPng = MakeSolidPng(new Rgba32(0, 64, 255, 255));
        var tempDir = Path.Combine(Path.GetTempPath(), "NsMultitool_Test_Ps2Geom_" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            Directory.CreateDirectory(tempDir);
            var outputFile = Path.Combine(tempDir, "geom_dest_alpha_later_mask.glb");

            var triangles = Ps2GeomGltfWriter.Write(geom, outputFile,
                checksum => checksum switch
                {
                    maskChecksum => maskPng,
                    layerChecksum => layerPng,
                    _ => null
                });

            var firstPixels = ReadEmbeddedImageFirstPixels(outputFile);
            Assert.Equal(4, triangles);
            Assert.Contains(firstPixels, p => p.R == 0 && p.G == 64 && p.B == 255 && p.A == 132);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Write_DestinationAlphaLayerPrefersLatestExactCoplanarMask()
    {
        const uint oldMaskChecksum = 0x11112222;
        const uint layerChecksum = 0x33334444;
        const uint newerMaskChecksum = 0x55556666;
        var oldMaskLeaf = MakeTexturedLeaf(oldMaskChecksum, isBillboard: false);
        var layerLeaf = MakeTexturedLeaf(layerChecksum, isBillboard: false, alpha1: 0x54);
        var newerMaskLeaf = MakeTexturedLeaf(newerMaskChecksum, isBillboard: false);
        var geom = new Ps2GeomScene { Leaves = [oldMaskLeaf, layerLeaf, newerMaskLeaf] };
        var oldMaskPng = MakeUniformAlphaMaskPng(64);
        var newerMaskPng = MakeUniformAlphaMaskPng(192);
        var layerPng = MakeSolidPng(new Rgba32(0, 64, 255, 255));
        var tempDir = Path.Combine(Path.GetTempPath(), "NsMultitool_Test_Ps2Geom_" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            Directory.CreateDirectory(tempDir);
            var outputFile = Path.Combine(tempDir, "geom_dest_alpha_latest_exact_mask.glb");

            var triangles = Ps2GeomGltfWriter.Write(geom, outputFile,
                checksum => checksum switch
                {
                    oldMaskChecksum => oldMaskPng,
                    newerMaskChecksum => newerMaskPng,
                    layerChecksum => layerPng,
                    _ => null
                });

            var firstPixels = ReadEmbeddedImageFirstPixels(outputFile);
            Assert.Equal(6, triangles);
            Assert.Contains(firstPixels, p => p.R == 0 && p.G == 64 && p.B == 255 && p.A == 192);
            Assert.DoesNotContain(firstPixels, p => p.R == 0 && p.G == 64 && p.B == 255 && p.A == 64);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Write_DestinationAlphaLayerRespectsMaskUvTransform()
    {
        const uint maskChecksum = 0x11112222;
        const uint layerChecksum = 0x33334444;
        var layerLeaf = MakeTexturedQuadLeaf(layerChecksum, alpha1: 0x54,
        [
            (0f, 0f),
            (1f, 0f),
            (0f, 1f),
            (1f, 1f)
        ]);
        var maskLeaf = MakeTexturedQuadLeaf(maskChecksum, alpha1: 0x44,
        [
            (1f, 0f),
            (0f, 0f),
            (1f, 1f),
            (0f, 1f)
        ]);
        var geom = new Ps2GeomScene { Leaves = [layerLeaf, maskLeaf] };
        var maskPng = MakeHorizontalAlphaMaskPng();
        var layerPng = MakeSolidPng(new Rgba32(0, 64, 255, 255));
        var tempDir = Path.Combine(Path.GetTempPath(), "NsMultitool_Test_Ps2Geom_" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            Directory.CreateDirectory(tempDir);
            var outputFile = Path.Combine(tempDir, "geom_dest_alpha_flipped_mask_uv.glb");

            var triangles = Ps2GeomGltfWriter.Write(geom, outputFile,
                checksum => checksum switch
                {
                    maskChecksum => maskPng,
                    layerChecksum => layerPng,
                    _ => null
                });

            var firstPixels = ReadEmbeddedImageFirstPixels(outputFile);
            Assert.Equal(4, triangles);
            Assert.Contains(firstPixels, p => p.R == 0 && p.G == 64 && p.B == 255 && p.A == 200);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void WorldzoneCutoutDepthBiasIsAboveBlendOverlayBias()
    {
        var leaf = MakeTexturedLeaf(0x11112222, isBillboard: false);
        var billboard = MakeTexturedLeaf(0x11112222, isBillboard: true);

        var maskBias = Ps2GeomGltfWriter.ComputeWorldzoneMaterialDepthBias(true, leaf, "MASK");
        var blendBias = Ps2GeomGltfWriter.ComputeWorldzoneMaterialDepthBias(true, leaf, "BLEND");

        Assert.True(maskBias > blendBias);
        Assert.True(blendBias > 0);
        Assert.Equal(0, Ps2GeomGltfWriter.ComputeWorldzoneMaterialDepthBias(false, leaf, "MASK"));
        Assert.Equal(0, Ps2GeomGltfWriter.ComputeWorldzoneMaterialDepthBias(true, billboard, "MASK"));
        Assert.Equal(0, Ps2GeomGltfWriter.ComputeWorldzoneMaterialDepthBias(true, leaf, "OPAQUE"));
    }

    [Fact]
    public void Write_HardCutoutWithVertexAlphaExportsAsMaskAndOpaqueVertexAlpha()
    {
        const uint textureChecksum = 0xCAFE1234;
        var leaf = new Ps2GeomLeaf
        {
            Checksum = 0,
            TextureChecksum = textureChecksum,
            GroupChecksum = 0,
            Colour = 0,
            BoundingSphere = Vector4.Zero,
            Vertices =
            [
                MakeVertex(0, 0, alpha: 64, hasColor: true),
                MakeVertex(1, 0, alpha: 64, hasColor: true),
                MakeVertex(0, 1, alpha: 64, hasColor: true),
                MakeVertex(1, 1, alpha: 64, hasColor: true)
            ],
            DmaTex0 = 0,
            DmaClamp1 = 0,
            DmaAlpha1 = 0x44,
            DmaTest1 = 0x5001B
        };
        var geom = new Ps2GeomScene { Leaves = [leaf] };
        var png = MakeHardCutoutPng();
        var tempDir = Path.Combine(Path.GetTempPath(), "NsMultitool_Test_Ps2Geom_" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            Directory.CreateDirectory(tempDir);
            var outputFile = Path.Combine(tempDir, "geom_vertex_alpha_blend.glb");

            var triangles = Ps2GeomGltfWriter.Write(geom, outputFile,
                checksum => checksum == textureChecksum ? png : null);

            Assert.Equal(2, triangles);
            Assert.Equal("MASK", ReadFirstMaterialAlphaMode(outputFile));
            Assert.Equal(((byte)255, (byte)255), ReadFirstColorAlphaRange(outputFile));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Write_OpaqueMaterialIgnoresVertexAlphaTransparency()
    {
        const uint textureChecksum = 0xACED1234;
        var leaf = new Ps2GeomLeaf
        {
            Checksum = 0,
            TextureChecksum = textureChecksum,
            GroupChecksum = 0,
            Colour = 0,
            BoundingSphere = Vector4.Zero,
            Vertices =
            [
                MakeVertex(0, 0, alpha: 0, hasColor: true),
                MakeVertex(1, 0, alpha: 64, hasColor: true),
                MakeVertex(0, 1, alpha: 96, hasColor: true),
                MakeVertex(1, 1, alpha: 127, hasColor: true)
            ],
            DmaTex0 = 0,
            DmaClamp1 = 0,
            DmaAlpha1 = 0x0A,
            DmaTest1 = 0
        };
        var geom = new Ps2GeomScene { Leaves = [leaf] };
        var png = MakeSolidPng(new Rgba32(100, 100, 100, 255));
        var tempDir = Path.Combine(Path.GetTempPath(), "NsMultitool_Test_Ps2Geom_" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            Directory.CreateDirectory(tempDir);
            var outputFile = Path.Combine(tempDir, "geom_opaque_vertex_alpha.glb");

            var triangles = Ps2GeomGltfWriter.Write(geom, outputFile,
                checksum => checksum == textureChecksum ? png : null);

            Assert.Equal(2, triangles);
            Assert.Equal("OPAQUE", ReadFirstMaterialAlphaMode(outputFile));
            Assert.Equal(((byte)255, (byte)255), ReadFirstColorAlphaRange(outputFile));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Write_AdditiveBlendPreservesSourceHue()
    {
        const uint textureChecksum = 0xBEEFFACE;
        var leaf = new Ps2GeomLeaf
        {
            Checksum = 0,
            TextureChecksum = textureChecksum,
            GroupChecksum = 0,
            Colour = 0,
            BoundingSphere = Vector4.Zero,
            Vertices =
            [
                MakeVertex(0, 0),
                MakeVertex(1, 0),
                MakeVertex(0, 1),
                MakeVertex(1, 1)
            ],
            DmaTex0 = 0,
            DmaClamp1 = 0,
            DmaAlpha1 = 0x48,
            DmaTest1 = 0
        };
        var geom = new Ps2GeomScene { Leaves = [leaf] };
        var png = MakeSolidPng(new Rgba32(128, 64, 0, 255));
        var tempDir = Path.Combine(Path.GetTempPath(), "NsMultitool_Test_Ps2Geom_" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            Directory.CreateDirectory(tempDir);
            var outputFile = Path.Combine(tempDir, "geom_additive_hue.glb");

            var triangles = Ps2GeomGltfWriter.Write(geom, outputFile,
                checksum => checksum == textureChecksum ? png : null);

            var pixel = ReadFirstImageFirstPixel(outputFile);
            Assert.Equal(2, triangles);
            Assert.Equal("BLEND", ReadFirstMaterialAlphaMode(outputFile));
            Assert.True(pixel.R > pixel.G, $"Expected red-dominant additive hue, got {pixel}");
            Assert.Equal(0, pixel.B);
            Assert.InRange(pixel.A, 120, 130);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ClassifyWorldzoneRenderLayer_AdditiveStaticGeometryIsNightOverlay()
    {
        var staticLight = MakeTexturedLeaf(0x11111111, isBillboard: false, alpha1: 0x48);
        var billboardCutout = MakeTexturedLeaf(0x22222222, isBillboard: true, alpha1: 0x48);

        Assert.Equal(Ps2GeomRenderLayer.NightOverlay,
            Ps2GeomGltfWriter.ClassifyWorldzoneRenderLayer(staticLight));
        Assert.Equal(Ps2GeomRenderLayer.Base,
            Ps2GeomGltfWriter.ClassifyWorldzoneRenderLayer(billboardCutout));
    }

    [Fact]
    public void Write_WorldzoneLeavesEmitInPreambleMaterialGroupOrder()
    {
        const uint overlayChecksum = 0xB0A41683;
        const uint wearChecksum = 0xD98DDC23;
        const uint baseChecksum = 0x0A41683B;
        var overlay = MakeTexturedLeaf(overlayChecksum, isBillboard: false, alpha1: 0x44, groupChecksum: 12);
        var wear = MakeTexturedLeaf(wearChecksum, isBillboard: false, alpha1: 0x44, groupChecksum: 6);
        var baseLeaf = MakeTexturedLeaf(baseChecksum, isBillboard: false, alpha1: 0x0A, groupChecksum: 5);
        var filler = Enumerable.Range(0, 498).Select(static _ => MakeEmptyLeaf());
        var geom = new Ps2GeomScene { Leaves = [overlay, wear, baseLeaf, .. filler] };
        var debugCollector = new Ps2GeomDebugCollector("test_worldzone");
        var tempDir = Path.Combine(Path.GetTempPath(), "NsMultitool_Test_Ps2Geom_" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            Directory.CreateDirectory(tempDir);
            var outputFile = Path.Combine(tempDir, "geom_worldzone_group_order.glb");

            var triangles = Ps2GeomGltfWriter.Write(geom, outputFile, debugCollector: debugCollector);

            Assert.Equal(6, triangles);
            Assert.Equal(
                [baseChecksum, wearChecksum, overlayChecksum],
                debugCollector.Materials.Select(static material => material.TextureChecksum).ToArray());
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Write_BillboardFoliageCutoutExportsAsMask()
    {
        const uint textureChecksum = 0x5678ABCD;
        var leaf = MakeTexturedLeaf(textureChecksum, isBillboard: true);
        var geom = new Ps2GeomScene { Leaves = [leaf] };
        var png = MakeSoftFoliageCutoutPng();
        var tempDir = Path.Combine(Path.GetTempPath(), "NsMultitool_Test_Ps2Geom_" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            Directory.CreateDirectory(tempDir);
            var outputFile = Path.Combine(tempDir, "geom_billboard_cutout.glb");

            var triangles = Ps2GeomGltfWriter.Write(geom, outputFile,
                checksum => checksum == textureChecksum ? png : null);

            Assert.Equal(2, triangles);
            Assert.Equal("MASK", ReadFirstMaterialAlphaMode(outputFile));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Write_NonBillboardFoliageCutoutExportsAsMask()
    {
        const uint textureChecksum = 0x7654ABCD;
        var leaf = MakeTexturedLeaf(textureChecksum, isBillboard: false);
        var geom = new Ps2GeomScene { Leaves = [leaf] };
        var png = MakeLeafyCutoutPng();
        var tempDir = Path.Combine(Path.GetTempPath(), "NsMultitool_Test_Ps2Geom_" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            Directory.CreateDirectory(tempDir);
            var outputFile = Path.Combine(tempDir, "geom_foliage_cutout.glb");

            var triangles = Ps2GeomGltfWriter.Write(geom, outputFile,
                checksum => checksum == textureChecksum ? png : null);

            Assert.Equal(2, triangles);
            Assert.Equal("MASK", ReadFirstMaterialAlphaMode(outputFile));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    private static Ps2GeomLeaf MakeTexturedLeaf(
        uint textureChecksum,
        bool isBillboard,
        ulong alpha1 = 0x44,
        ulong test1 = 0,
        uint groupChecksum = 0)
    {
        return new Ps2GeomLeaf
        {
            Checksum = 0,
            TextureChecksum = textureChecksum,
            GroupChecksum = groupChecksum,
            Colour = 0,
            BoundingSphere = Vector4.Zero,
            Vertices =
            [
                MakeVertex(0, 0),
                MakeVertex(1, 0),
                MakeVertex(0, 1),
                MakeVertex(1, 1)
            ],
            DmaTex0 = 0,
            DmaClamp1 = 0,
            DmaAlpha1 = alpha1,
            DmaTest1 = test1,
            IsBillboard = isBillboard
        };
    }

    private static Ps2GeomLeaf MakeEmptyLeaf()
    {
        return new Ps2GeomLeaf
        {
            Checksum = 0,
            TextureChecksum = 0,
            GroupChecksum = 0,
            Colour = 0,
            BoundingSphere = Vector4.Zero,
            Vertices = [],
            DmaTex0 = 0,
            DmaClamp1 = 0,
            DmaAlpha1 = 0,
            DmaTest1 = 0
        };
    }

    private static ulong MakeTest(int atst, int aref, int afail = 0) =>
        0x1UL |
        ((ulong)(atst & 0x7) << 1) |
        ((ulong)(aref & 0xFF) << 4) |
        ((ulong)(afail & 0x3) << 12);

    private static Ps2GeomLeaf MakeTexturedQuadLeaf(
        uint textureChecksum,
        ulong alpha1,
        IReadOnlyList<(float U, float V)> uvs)
    {
        Assert.Equal(4, uvs.Count);
        return new Ps2GeomLeaf
        {
            Checksum = 0,
            TextureChecksum = textureChecksum,
            GroupChecksum = 0,
            Colour = 0,
            BoundingSphere = Vector4.Zero,
            Vertices =
            [
                MakeUvVertex(0, 0, uvs[0].U, uvs[0].V),
                MakeUvVertex(1, 0, uvs[1].U, uvs[1].V),
                MakeUvVertex(0, 1, uvs[2].U, uvs[2].V),
                MakeUvVertex(1, 1, uvs[3].U, uvs[3].V)
            ],
            DmaTex0 = 0,
            DmaClamp1 = 0,
            DmaAlpha1 = alpha1,
            DmaTest1 = 0
        };
    }

    private static Ps2Vertex MakeVertex(float x, float y, byte alpha = 128, bool hasColor = false)
    {
        return new Ps2Vertex(
            new Vector3(x, y, 0),
            Vector3.UnitZ,
            128, 128, 128, alpha,
            0f, 0f,
            true,
            hasColor,
            false,
            false);
    }

    private static Ps2Vertex MakeUvVertex(float x, float y, float u, float v)
    {
        return new Ps2Vertex(
            new Vector3(x, y, 0),
            Vector3.UnitZ,
            128, 128, 128, 128,
            u, v,
            true,
            false,
            true,
            false);
    }

    private static Ps2Vertex MakeHelperVertex(float x, float y, float z, bool restart)
    {
        return new Ps2Vertex(
            new Vector3(x, y, z),
            Vector3.UnitY,
            128, 128, 128, 128,
            0f, 0f,
            false,
            false,
            true,
            restart);
    }

    private static byte[] MakeDarkBimodalOverlayPng()
    {
        using var image = new Image<Rgba32>(4, 4);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                    row[x] = new Rgba32(0, 0, 0, (byte)((x + y) % 2 == 0 ? 255 : 0));
            }
        });

        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }

    private static byte[] MakeGreyBimodalShadowPng()
    {
        using var image = new Image<Rgba32>(4, 4);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                    row[x] = new Rgba32(104, 104, 104, (byte)((x + y) % 4 == 0 ? 0 : 255));
            }
        });

        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }

    private static byte[] MakeTransparentUsefulRgbPng()
    {
        using var image = new Image<Rgba32>(4, 4);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                    row[x] = new Rgba32((byte)(80 + x * 20), (byte)(70 + y * 18), 120, 0);
            }
        });

        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }

    private static byte[] MakeSparseNeutralMaskPng()
    {
        using var image = new Image<Rgba32>(8, 8);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var visible = (x + y * 3) % 4 == 0;
                    row[x] = visible
                        ? new Rgba32(160, 160, 160, 255)
                        : new Rgba32(0, 0, 0, 0);
                }
            }
        });

        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }

    private static byte[] MakeDitheredWindowPng()
    {
        using var image = new Image<Rgba32>(8, 8);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var opaque = (x + y) % 2 == 0;
                    row[x] = opaque
                        ? new Rgba32(230, 242, 245, 255)
                        : new Rgba32(0, 0, 0, 0);
                }
            }
        });

        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }

    private static byte[] MakeSoftFoliageCutoutPng()
    {
        using var image = new Image<Rgba32>(8, 8);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                    row[x] = new Rgba32(35, 140, 54, (byte)(x < 4 ? 96 : 192));
            }
        });

        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }

    private static byte[] MakeLeafyCutoutPng()
    {
        using var image = new Image<Rgba32>(8, 8);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    if ((x + y) % 3 == 0)
                    {
                        row[x] = new Rgba32(0, 0, 0, 0);
                    }
                    else
                    {
                        var alpha = (byte)(x % 2 == 0 ? 128 : 224);
                        row[x] = new Rgba32(76, 112, 64, alpha);
                    }
                }
            }
        });

        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }

    private static byte[] MakeLowOpacityMaskPng()
    {
        using var image = new Image<Rgba32>(2, 2);
        image[0, 0] = new Rgba32(123, 126, 133, 132);
        image[1, 0] = new Rgba32(123, 126, 133, 132);
        image[0, 1] = new Rgba32(0, 0, 0, 0);
        image[1, 1] = new Rgba32(0, 0, 0, 0);

        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }

    private static byte[] MakeHorizontalAlphaMaskPng()
    {
        using var image = new Image<Rgba32>(2, 2);
        image[0, 0] = new Rgba32(255, 255, 255, 0);
        image[1, 0] = new Rgba32(255, 255, 255, 200);
        image[0, 1] = new Rgba32(255, 255, 255, 0);
        image[1, 1] = new Rgba32(255, 255, 255, 200);

        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }

    private static byte[] MakeUniformAlphaMaskPng(byte alpha)
    {
        using var image = new Image<Rgba32>(2, 2);
        image[0, 0] = new Rgba32(255, 255, 255, alpha);
        image[1, 0] = new Rgba32(255, 255, 255, alpha);
        image[0, 1] = new Rgba32(255, 255, 255, alpha);
        image[1, 1] = new Rgba32(255, 255, 255, alpha);

        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }

    private static byte[] MakeHardCutoutPng()
    {
        using var image = new Image<Rgba32>(16, 16);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    row[x] = x < row.Length / 2
                        ? new Rgba32(100, 100, 100, 255)
                        : new Rgba32(100, 100, 100, 0);
                }
            }
        });

        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }

    private static byte[] MakeSolidPng(Rgba32 color)
    {
        using var image = new Image<Rgba32>(1, 1);
        image[0, 0] = color;

        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }

    private static string ReadFirstMaterialAlphaMode(string glbPath)
    {
        using var stream = File.OpenRead(glbPath);
        using var reader = new BinaryReader(stream);
        Assert.Equal(0x46546C67u, reader.ReadUInt32());
        _ = reader.ReadUInt32();
        _ = reader.ReadUInt32();
        var jsonLength = reader.ReadUInt32();
        Assert.Equal(0x4E4F534Au, reader.ReadUInt32());
        var jsonBytes = reader.ReadBytes(checked((int)jsonLength));

        using var doc = JsonDocument.Parse(jsonBytes);
        var material = doc.RootElement.GetProperty("materials")[0];
        return material.TryGetProperty("alphaMode", out var alphaMode)
            ? alphaMode.GetString()!
            : "OPAQUE";
    }

    private static AlphaStats ReadFirstImageAlphaStats(string glbPath)
    {
        var (jsonBytes, binBytes) = ReadGlbChunks(glbPath);

        using var doc = JsonDocument.Parse(jsonBytes);
        var image = doc.RootElement.GetProperty("images")[0];
        var bufferViewIndex = image.GetProperty("bufferView").GetInt32();
        var bufferView = doc.RootElement.GetProperty("bufferViews")[bufferViewIndex];
        var offset = bufferView.TryGetProperty("byteOffset", out var byteOffset)
            ? byteOffset.GetInt32()
            : 0;
        var length = bufferView.GetProperty("byteLength").GetInt32();
        var pngBytes = binBytes.AsSpan(offset, length).ToArray();

        using var png = SixLabors.ImageSharp.Image.Load<Rgba32>(pngBytes);
        var hasIntermediate = false;
        png.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var alpha = row[x].A;
                    if (alpha > 0 && alpha < 255)
                        hasIntermediate = true;
                }
            }
        });

        return new AlphaStats(hasIntermediate);
    }

    private static Rgba32 ReadFirstImageFirstPixel(string glbPath)
    {
        var (jsonBytes, binBytes) = ReadGlbChunks(glbPath);

        using var doc = JsonDocument.Parse(jsonBytes);
        var image = doc.RootElement.GetProperty("images")[0];
        var bufferViewIndex = image.GetProperty("bufferView").GetInt32();
        var bufferView = doc.RootElement.GetProperty("bufferViews")[bufferViewIndex];
        var offset = bufferView.TryGetProperty("byteOffset", out var byteOffset)
            ? byteOffset.GetInt32()
            : 0;
        var length = bufferView.GetProperty("byteLength").GetInt32();
        var pngBytes = binBytes.AsSpan(offset, length).ToArray();

        using var png = SixLabors.ImageSharp.Image.Load<Rgba32>(pngBytes);
        return png[0, 0];
    }

    private static GltfSampler ReadFirstTextureSampler(string glbPath)
    {
        var (jsonBytes, _) = ReadGlbChunks(glbPath);
        using var doc = JsonDocument.Parse(jsonBytes);
        var texture = doc.RootElement.GetProperty("textures")[0];
        var samplerIndex = texture.GetProperty("sampler").GetInt32();
        var sampler = doc.RootElement.GetProperty("samplers")[samplerIndex];
        return new GltfSampler(
            sampler.TryGetProperty("magFilter", out var magFilter) ? magFilter.GetInt32() : null,
            sampler.TryGetProperty("minFilter", out var minFilter) ? minFilter.GetInt32() : null,
            sampler.GetProperty("wrapS").GetInt32(),
            sampler.GetProperty("wrapT").GetInt32());
    }

    private static IReadOnlyList<Rgba32> ReadEmbeddedImageFirstPixels(string glbPath)
    {
        var (jsonBytes, binBytes) = ReadGlbChunks(glbPath);
        using var doc = JsonDocument.Parse(jsonBytes);
        var result = new List<Rgba32>();
        foreach (var image in doc.RootElement.GetProperty("images").EnumerateArray())
        {
            var bufferViewIndex = image.GetProperty("bufferView").GetInt32();
            var bufferView = doc.RootElement.GetProperty("bufferViews")[bufferViewIndex];
            var offset = bufferView.TryGetProperty("byteOffset", out var byteOffset)
                ? byteOffset.GetInt32()
                : 0;
            var length = bufferView.GetProperty("byteLength").GetInt32();
            var pngBytes = binBytes.AsSpan(offset, length).ToArray();
            using var png = SixLabors.ImageSharp.Image.Load<Rgba32>(pngBytes);
            result.Add(png[0, 0]);
        }

        return result;
    }

    private static (byte Min, byte Max) ReadFirstColorAlphaRange(string glbPath)
    {
        var (jsonBytes, binBytes) = ReadGlbChunks(glbPath);
        using var doc = JsonDocument.Parse(jsonBytes);
        var primitive = doc.RootElement.GetProperty("meshes")[0]
            .GetProperty("primitives")[0];
        var colorAccessorIndex = primitive.GetProperty("attributes")
            .GetProperty("COLOR_0")
            .GetInt32();
        var accessor = doc.RootElement.GetProperty("accessors")[colorAccessorIndex];
        Assert.Equal(5121, accessor.GetProperty("componentType").GetInt32());
        Assert.Equal("VEC4", accessor.GetProperty("type").GetString());

        var bufferViewIndex = accessor.GetProperty("bufferView").GetInt32();
        var bufferView = doc.RootElement.GetProperty("bufferViews")[bufferViewIndex];
        var bufferViewOffset = bufferView.TryGetProperty("byteOffset", out var byteOffset)
            ? byteOffset.GetInt32()
            : 0;
        var accessorOffset = accessor.TryGetProperty("byteOffset", out var accessorByteOffset)
            ? accessorByteOffset.GetInt32()
            : 0;
        var stride = bufferView.TryGetProperty("byteStride", out var byteStride)
            ? byteStride.GetInt32()
            : 4;
        var count = accessor.GetProperty("count").GetInt32();
        var start = bufferViewOffset + accessorOffset;

        var min = byte.MaxValue;
        var max = byte.MinValue;
        for (var i = 0; i < count; i++)
        {
            var alpha = binBytes[start + i * stride + 3];
            min = Math.Min(min, alpha);
            max = Math.Max(max, alpha);
        }

        return (min, max);
    }

    private static (byte[] Json, byte[] Bin) ReadGlbChunks(string glbPath)
    {
        using var stream = File.OpenRead(glbPath);
        using var reader = new BinaryReader(stream);
        Assert.Equal(0x46546C67u, reader.ReadUInt32());
        _ = reader.ReadUInt32();
        _ = reader.ReadUInt32();

        var jsonLength = reader.ReadUInt32();
        Assert.Equal(0x4E4F534Au, reader.ReadUInt32());
        var jsonBytes = reader.ReadBytes(checked((int)jsonLength));

        var binLength = reader.ReadUInt32();
        Assert.Equal(0x004E4942u, reader.ReadUInt32());
        var binBytes = reader.ReadBytes(checked((int)binLength));
        return (jsonBytes, binBytes);
    }

    private readonly record struct AlphaStats(bool HasIntermediateAlpha);

    private readonly record struct GltfSampler(int? MagFilter, int? MinFilter, int WrapS, int WrapT);
}
