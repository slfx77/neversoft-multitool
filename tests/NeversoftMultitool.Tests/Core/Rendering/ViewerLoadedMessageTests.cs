using System.Text.Json;
using NeversoftMultitool.Core.Rendering;

namespace NeversoftMultitool.Tests.Core.Rendering;

public sealed class ViewerLoadedMessageTests
{
    private static ViewerLoadedMessage Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return ViewerLoadedMessage.Parse(doc.RootElement);
    }

    [Fact]
    public void Parse_ReadsThePagePayloadShape()
    {
        var message = Parse(
            """
            {
              "type": "loaded",
              "animations": ["anim_0", "anim_1"],
              "duration": 2.5,
              "hasColourPulses": true,
              "hasTextureWibbles": false
            }
            """);

        Assert.Equal(2, message.AnimationCount);
        Assert.Equal(2.5, message.Duration);
        Assert.True(message.HasAnimations);
        Assert.True(message.HasColourPulses);
        Assert.False(message.HasTextureWibbles);
    }

    [Fact]
    public void Parse_AbsentFieldsDefaultToNoContent()
    {
        // A stale cached page that predates the presence flags must parse as
        // "no surface animations", never throw or invent content.
        var message = Parse("""{ "type": "loaded", "animations": [], "duration": 0 }""");

        Assert.Equal(0, message.AnimationCount);
        Assert.False(message.HasAnimations);
        Assert.False(message.HasColourPulses);
        Assert.False(message.HasTextureWibbles);

        var empty = Parse("""{ "type": "loaded" }""");
        Assert.False(empty.HasAnimations);
        Assert.False(empty.HasColourPulses);
        Assert.False(empty.HasTextureWibbles);
    }

    [Fact]
    public void Parse_HasAnimationsRequiresBothClipsAndDuration()
    {
        Assert.False(Parse("""{ "animations": ["a"], "duration": 0 }""").HasAnimations);
        Assert.False(Parse("""{ "animations": [], "duration": 3.0 }""").HasAnimations);
        Assert.True(Parse("""{ "animations": ["a"], "duration": 3.0 }""").HasAnimations);
    }

    [Fact]
    public void Parse_ToleratesMalformedFieldTypes()
    {
        var message = Parse(
            """
            {
              "animations": "not-an-array",
              "duration": "not-a-number",
              "hasColourPulses": "yes",
              "hasTextureWibbles": 1
            }
            """);

        Assert.Equal(0, message.AnimationCount);
        Assert.Equal(0, message.Duration);
        Assert.False(message.HasColourPulses);
        Assert.False(message.HasTextureWibbles);
    }
}
