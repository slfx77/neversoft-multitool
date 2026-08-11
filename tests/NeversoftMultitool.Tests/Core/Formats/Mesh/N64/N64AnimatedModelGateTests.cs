using NeversoftMultitool.Core.Formats.Animation;
using NeversoftMultitool.Core.Formats.Mesh.N64;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using NeversoftMultitool.Tests.Core.Formats.Animation;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.N64;

public sealed class N64AnimatedModelGateTests
{
    [Fact]
    public void Gate_AcceptsOnePlacementAtObjectZeroWithGlobalJoints()
    {
        var shell = CreateShell(0, 99);
        var meshes = new[] { CreateMesh(0, 0, 1, 1) };

        var plan = N64AnimatedModelGate.TryOpen(CreateAnimationBytes(), shell, meshes);

        Assert.NotNull(plan);
        Assert.Single(plan!.Animations.Entries);
        Assert.Equal(N64MatrixOffsetMode.GlobalJoint, plan.Geometry.OffsetMode);
    }

    [Fact]
    public void Gate_RejectsCornerOutsideGlobalJointRange()
    {
        var shell = CreateShell(0, 99);
        var meshes = new[] { CreateMesh(0, 0, 1, 2) };

        Assert.Null(N64AnimatedModelGate.TryOpen(CreateAnimationBytes(), shell, meshes));
    }

    [Fact]
    public void Gate_AcceptsMultiPlacementOnlyWhenRelativeModeProvablyFails()
    {
        var shell = CreateShell(0, 1);
        var meshes = new[]
        {
            CreateMesh(0, 0, 0, 0),
            // Global joint 1 is valid; placement 1 + matrix 1 is not.
            CreateMesh(1, 1, 1, 1)
        };

        var plan = N64AnimatedModelGate.TryOpen(CreateAnimationBytes(), shell, meshes);

        Assert.NotNull(plan);
        Assert.Equal(N64MatrixOffsetMode.GlobalJoint, plan!.Geometry.OffsetMode);
        Assert.Equal(1, plan.Geometry.ResolveOffsetObjectIndexOrDefault(1, 1));
        Assert.Equal(1, plan.Geometry.ResolveSkinJoint(1));
    }

    [Fact]
    public void Gate_RejectsMultiPlacementWhenGlobalAndRelativeModesBothRemainPossible()
    {
        var shell = CreateShell(99, 0, 1);
        var meshes = new[]
        {
            CreateMesh(0, 0, 0, 0),
            CreateMesh(1, 0, 0, 0)
        };

        Assert.Null(N64AnimatedModelGate.TryOpen(CreateAnimationBytes(), shell, meshes));
    }

    [Fact]
    public void BindingPlans_PinGlobalVersusPlacementRelativeAddressing()
    {
        var rigid = N64GeometryBindingPlan.Static(4);
        var animated = N64GeometryBindingPlan.Animated(4);

        Assert.Equal(3, rigid.ResolveOffsetObjectIndexOrDefault(2, 1));
        Assert.Equal(1, animated.ResolveOffsetObjectIndexOrDefault(2, 1));
        Assert.Equal(-1, rigid.ResolveOffsetObjectIndexOrDefault(3, 1));
        Assert.Equal(1, animated.ResolveSkinJoint(1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Gate_RejectsAmbiguousNonZeroOrRepeatedPlacement(bool multiple)
    {
        var shell = multiple ? CreateShell(0, 0) : CreateShell(99, 0);
        var meshes = new[] { CreateMesh(0, 0, 0, 0) };

        Assert.Null(N64AnimatedModelGate.TryOpen(CreateAnimationBytes(), shell, meshes));
    }

    private static byte[] CreateAnimationBytes()
    {
        return N64CompressedAnimationBankTests.BuildShell(
            PsxMeshFile.HierChunkV2Tag,
            [(2, N64CompressedAnimationBankTests.ConstantChannels(0, 0, 0, 0, 0, 0))]);
    }

    private static PsxMeshFile CreateShell(params ushort[] meshIndices)
    {
        return new PsxMeshFile
        {
            Version = 4,
            Objects = meshIndices.Select(index => new PsxMeshObject { MeshIndex = index }).ToList(),
            Meshes = [],
            MeshNameHashes = [],
            TextureHashes = [],
            HasHierarchy = true,
            IsSuperModel = true,
            ScaleDivisor = 36f,
            TranslationDivisor = 2.25f
        };
    }

    private static N64RenderBankFile.N64RenderMesh CreateMesh(
        int nodeIndex,
        int joint0,
        int joint1,
        int joint2)
    {
        N64RenderBankFile.N64Corner Corner(int vertex, int joint) =>
            new(vertex, 0, 0, joint);

        return new N64RenderBankFile.N64RenderMesh(
            [
                new N64RenderBankFile.N64Vertex(0, 0, 0, 0, 0, 255, 255, 255, 255),
                new N64RenderBankFile.N64Vertex(1, 0, 0, 0, 0, 255, 255, 255, 255),
                new N64RenderBankFile.N64Vertex(0, 1, 0, 0, 0, 255, 255, 255, 255)
            ],
            [new N64RenderBankFile.N64Triangle(
                Corner(0, joint0), Corner(1, joint1), Corner(2, joint2),
                0, joint0, 0)],
            [0f, 0f, 0f, 1f, 1f, 0f],
            HasNormals: false,
            nodeIndex);
    }
}
