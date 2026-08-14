using NeversoftMultitool.Core.Formats.Mesh.Lit;
using SharpGLTF.Scenes;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh;

public sealed class GltfLightWriterTests
{
    [Fact]
    public void AddLightsToScene_SpotAnglesConvertFromFullDegreesToHalfRadians()
    {
        var scene = new SceneBuilder();
        GltfLightWriter.AddLightsToScene(scene,
        [
            new LitLight
            {
                Name = "spot",
                Type = LitLightType.Spot,
                Hotspot = 30f,
                Radius = 60f
            }
        ]);

        var model = scene.ToGltf2();
        var light = Assert.Single(model.LogicalPunctualLights);

        Assert.Equal(MathF.PI / 12f, light.InnerConeAngle, 6);
        Assert.Equal(MathF.PI / 6f, light.OuterConeAngle, 6);
    }
}
