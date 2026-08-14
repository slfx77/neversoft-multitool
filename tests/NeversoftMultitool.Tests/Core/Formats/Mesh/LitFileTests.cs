using System.Numerics;
using System.Text;
using NeversoftMultitool.Core.Formats.Mesh.Lit;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh;

public sealed class LitFileTests
{
    [Fact]
    public void Parse_LightDeclarationWithoutOpeningBrace_DoesNotCreatePhantomLight()
    {
        var data = Encoding.ASCII.GetBytes(
            "AdvLights 2.000\n" +
            "OmniLight Ghost\n");

        var lights = LitFile.Parse(data);

        Assert.Empty(lights);
    }

    [Fact]
    public void Parse_MissingOpeningBrace_DoesNotConsumeFollowingValidLight()
    {
        var data = Encoding.ASCII.GetBytes(
            "AdvLights 2.000\n" +
            "OmniLight Ghost\n" +
            "OmniLight Key\n" +
            "{\n" +
            "Pos (1 2 3)\n" +
            "}\n");

        var light = Assert.Single(LitFile.Parse(data));

        Assert.Equal("Key", light.Name);
        Assert.Equal(new Vector3(1, 2, 3), light.Position);
    }

    [Fact]
    public void Parse_MinimalWellFormedLight_ParsesProperties()
    {
        var data = Encoding.ASCII.GetBytes(
            "AdvLights 2.000\n" +
            "OmniLight Key\n" +
            "{\n" +
            "Pos (1 2 3)\n" +
            "Color (0.25 0.5 0.75)\n" +
            "}\n");

        var light = Assert.Single(LitFile.Parse(data));

        Assert.Equal("Key", light.Name);
        Assert.Equal(LitLightType.Point, light.Type);
        Assert.Equal(new Vector3(1, 2, 3), light.Position);
        Assert.Equal(new Vector3(0.25f, 0.5f, 0.75f), light.Color);
    }

    [Theory]
    [InlineData("BoxGradShadow\n{")]
    [InlineData("BoxGradShadow {")]
    public void Parse_RecognizedNestedBlock_DoesNotLeakIntoParentLight(string nestedBlockStart)
    {
        var data = Encoding.ASCII.GetBytes(
            "AdvLights 2.000\n" +
            "OmniLight Key\n" +
            "{\n" +
            "Pos (1 2 3)\n" +
            nestedBlockStart + "\n" +
            "Pos (9 9 9)\n" +
            "}\n" +
            "Color (0.25 0.5 0.75)\n" +
            "}\n");

        var light = Assert.Single(LitFile.Parse(data));

        Assert.Equal(new Vector3(1, 2, 3), light.Position);
        Assert.Equal(new Vector3(0.25f, 0.5f, 0.75f), light.Color);
    }

    [Fact]
    public void Parse_CompactNestedAndParentClose_PreservesFollowingLight()
    {
        var data = Encoding.ASCII.GetBytes(
            "AdvLights 2.000\n" +
            "OmniLight First\n" +
            "{\n" +
            "Pos (1 2 3)\n" +
            "BoxGradShadow {\n" +
            "Pos (9 9 9)\n" +
            "}}\n" +
            "OmniLight Second\n" +
            "{\n" +
            "Pos (4 5 6)\n" +
            "}\n");

        var lights = LitFile.Parse(data);

        Assert.Collection(
            lights,
            first => Assert.Equal(new Vector3(1, 2, 3), first.Position),
            second => Assert.Equal(new Vector3(4, 5, 6), second.Position));
    }
}
