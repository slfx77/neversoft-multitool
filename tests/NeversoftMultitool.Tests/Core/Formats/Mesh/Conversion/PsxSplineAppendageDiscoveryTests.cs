using NeversoftMultitool.Core.Formats.Animation;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

public sealed class PsxSplineAppendageDiscoveryTests
{
    [Fact]
    public void OneChain_AcceptsUniqueAnimationPairedDrawableTip()
    {
        var (file, animation) = BuildCandidate();

        var chain = Assert.Single(PsxSplineAppendageGeometry.FindControllerChains(
            file, [animation]));

        Assert.Equal(0, chain.EmbeddedTipObjectIndex);
        Assert.Equal(Enumerable.Range(1, 7), chain.ObjectIndices);
    }

    [Theory]
    [InlineData("no-animation")]
    [InlineData("one-frame")]
    [InlineData("zero-translation")]
    [InlineData("translation-diverges")]
    [InlineData("parent-differs")]
    [InlineData("two-tips")]
    public void OneChain_RejectsMissingContradictoryOrAmbiguousEndpointEvidence(
        string scenario)
    {
        var tipCount = scenario == "two-tips" ? 2 : 1;
        var (file, animation) = BuildCandidate(
            tipCount,
            tipParentDiffers: scenario == "parent-differs",
            frameCount: scenario == "one-frame" ? 1 : 3);
        if (scenario == "zero-translation")
        {
            var endpointIndex = file.Objects.Count - 1;
            for (var frame = 0; frame < animation.FrameCount; frame++)
            {
                for (var channel = 3; channel < PsxAnimation.ChannelsPerBone; channel++)
                {
                    animation.Channels[0, channel, frame] = 0;
                    animation.Channels[endpointIndex, channel, frame] = 0;
                }
            }
        }
        if (scenario == "translation-diverges")
            animation.Channels[0, 3, 1]++;

        var evidence = scenario == "no-animation"
            ? Array.Empty<PsxAnimation>()
            : [animation];

        Assert.Empty(PsxSplineAppendageGeometry.FindControllerChains(file, evidence));
    }

    private static (PsxMeshFile File, PsxAnimation Animation) BuildCandidate(
        int tipCount = 1,
        bool tipParentDiffers = false,
        int frameCount = 3)
    {
        var objectCount = tipCount + 7;
        var objects = new List<PsxMeshObject>(objectCount);
        var meshes = new List<PsxMesh>(objectCount);
        for (var tip = 0; tip < tipCount; tip++)
        {
            objects.Add(new PsxMeshObject
            {
                MeshIndex = (ushort)tip,
                ParentIndex = tipParentDiffers ? 0 : -1
            });
            meshes.Add(CreateTipMesh());
        }

        for (var controller = 0; controller < 7; controller++)
        {
            var objectIndex = tipCount + controller;
            objects.Add(new PsxMeshObject
            {
                MeshIndex = (ushort)objectIndex,
                ParentIndex = -1,
                RawX = controller * 50 * 4096
            });
            meshes.Add(CreateControllerCube());
        }

        var file = new PsxMeshFile
        {
            Version = 0x04,
            Objects = objects,
            Meshes = meshes,
            MeshNameHashes = new uint[objectCount],
            TextureHashes = [],
            HasHierarchy = true,
            IsSuperModel = true,
            ScaleDivisor = 1f,
            TranslationDivisor = 1f
        };

        var channels = new short[objectCount, PsxAnimation.ChannelsPerBone, frameCount];
        var endpointIndex = objectCount - 1;
        for (var frame = 0; frame < frameCount; frame++)
        {
            for (var tip = 0; tip < tipCount; tip++)
            {
                channels[tip, 0, frame] = (short)(frame + 1);
                for (var channel = 3; channel < PsxAnimation.ChannelsPerBone; channel++)
                    channels[tip, channel, frame] = (short)(100 * channel + frame);
            }

            for (var channel = 3; channel < PsxAnimation.ChannelsPerBone; channel++)
                channels[endpointIndex, channel, frame] = (short)(100 * channel + frame);
        }

        return (file, new PsxAnimation
        {
            BoneCount = objectCount,
            FrameCount = frameCount,
            Channels = channels
        });
    }

    private static PsxMesh CreateTipMesh()
    {
        return new PsxMesh
        {
            Vertices =
            [
                new PsxVertex(),
                new PsxVertex { X = 1f, RawX = 1 },
                new PsxVertex { Y = 1f, RawY = 1 }
            ],
            Normals = [],
            Faces =
            [
                new PsxFace
                {
                    Index0 = 0,
                    Index1 = 1,
                    Index2 = 2
                }
            ],
            VertexCount = 3
        };
    }

    private static PsxMesh CreateControllerCube()
    {
        var vertices = new List<PsxVertex>(8);
        foreach (var x in new short[] { -5, 5 })
        {
            foreach (var y in new short[] { -5, 5 })
            {
                foreach (var z in new short[] { -5, 5 })
                {
                    vertices.Add(new PsxVertex
                    {
                        X = x,
                        Y = y,
                        Z = z,
                        RawX = x,
                        RawY = y,
                        RawZ = z
                    });
                }
            }
        }

        return new PsxMesh
        {
            Vertices = vertices,
            Normals = [],
            Faces = Enumerable.Range(0, 6)
                .Select(static _ => new PsxFace { IsQuad = true })
                .ToList(),
            VertexCount = 8
        };
    }
}
