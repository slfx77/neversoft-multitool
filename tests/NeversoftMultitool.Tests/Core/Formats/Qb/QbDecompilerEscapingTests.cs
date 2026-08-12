using NeversoftMultitool.Core.Formats.Qb;

namespace NeversoftMultitool.Tests.Core.Formats.Qb;

public sealed class QbDecompilerEscapingTests
{
    [Fact]
    public void Decompile_LocalStringContainingApostrophe_EscapesItsDelimiter()
    {
        var file = new QbFile
        {
            Tokens =
            [
                new QbToken
                {
                    Type = QbTokenType.LocalString,
                    StringValue = "skater\\'s"
                }
            ]
        };

        Assert.Equal("'skater\\\\\\'s'", QbDecompiler.Decompile(file));
    }
}
