using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Mesh.N64;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.N64;

public class N64BundleNameAlignmentValidationTests
{
    [Fact]
    public void TryPlace_TextureLibraryOnGeometryShell_RejectsWholeRun()
    {
        var bundles = new[]
        {
            new N64BundleNameResolver.Bundle("000", BuildNonEmptyShell()),
            new N64BundleNameResolver.Bundle("001", BuildNonEmptyShell()),
            new N64BundleNameResolver.Bundle("002", BuildAuthoredEmptyShell())
        };
        string?[] content = ["L1A1_G", null, null];

        var placement = N64BundleNameResolver.TryPlace(
            bundles, content, ["L1A1_G", "L1A1_L", "L1A1_O"], 0);

        Assert.Null(placement);
    }

    [Fact]
    public void TryPlace_TextureLibraryOnStub_PreservesValidRunAndL2Geometry()
    {
        var bundles = new[]
        {
            new N64BundleNameResolver.Bundle("000", BuildNonEmptyShell()),
            new N64BundleNameResolver.Bundle("001", BuildAuthoredEmptyShell()),
            new N64BundleNameResolver.Bundle("002", BuildNonEmptyShell())
        };
        string?[] content = ["SkDown", null, "SkDownL2"];

        var placement = N64BundleNameResolver.TryPlace(
            bundles, content, ["SkDown", "SkDown_L", "SkDownL2"], 0);

        Assert.Equal(
        [
            ("000", "SkDown"),
            ("001", "SkDown_L"),
            ("002", "SkDownL2")
        ], placement);
    }

    [Fact]
    public void TryAlign_ShiftedAnchorRejected_LaterValidAnchorStillWins()
    {
        var bundles = new[]
        {
            new N64BundleNameResolver.Bundle("000", BuildNonEmptyShell()),
            new N64BundleNameResolver.Bundle("001", BuildNonEmptyShell()),
            new N64BundleNameResolver.Bundle("002", BuildAuthoredEmptyShell()),
            new N64BundleNameResolver.Bundle("003", BuildNonEmptyShell())
        };
        string?[] content = ["L1A1_G", null, null, "L1A1_O"];

        var placement = N64BundleNameResolver.TryAlign(
            bundles, content, ["L1A1_G", "L1A1_L", "L1A1_O"]);

        Assert.Equal(
        [
            ("001", "L1A1_G"),
            ("002", "L1A1_L"),
            ("003", "L1A1_O")
        ], placement);
    }

    [Fact]
    public void IsStub_MalformedTwentyFourByteBuffer_IsNotAuthoredEmpty()
    {
        Assert.False(N64BundleNameResolver.IsStub(new byte[24]));
        Assert.True(N64BundleNameResolver.IsStub(BuildAuthoredEmptyShell()));
    }

    private static byte[] BuildAuthoredEmptyShell()
    {
        var data = new byte[24];
        BinaryPrimitives.WriteUInt32BigEndian(data, 0x0002_0004);
        return data;
    }

    private static byte[] BuildNonEmptyShell()
    {
        // One-object, one-mesh N64 shell. The stripped mesh-pointer word is
        // shared with the tag terminator at metaTop, matching the adapter's
        // measured shell layout; PsxN64ShellFile supplies the missing tail.
        var data = new byte[64];
        BinaryPrimitives.WriteUInt32BigEndian(data, 0x0002_0004);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4), 52);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(48), 1);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(52), uint.MaxValue);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(60), 1);
        return data;
    }
}
