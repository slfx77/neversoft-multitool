using NeversoftMultitool.Core.Formats.Qb;

namespace NeversoftMultitool.Tests.Core.Formats.Qb;

public sealed class QbFastBranchTokenValidationTests
{
    [Theory]
    [InlineData(0x47, 0)]
    [InlineData(0x47, 1)]
    [InlineData(0x48, 0)]
    [InlineData(0x48, 1)]
    [InlineData(0x49, 0)]
    [InlineData(0x49, 1)]
    public void TokenizeScriptBody_TruncatedFastOpcodeReturnsAccumulatedPrefix(
        byte opcode,
        int operandBytes)
    {
        var body = new byte[2 + operandBytes];
        body[0] = (byte)QbTokenType.EndOfLine;
        body[1] = opcode;

        var tokens = QbFile.TokenizeScriptBody(body, structsBigEndian: false, newEncoding: false);

        Assert.Collection(tokens, token => Assert.Equal(QbTokenType.EndOfLine, token.Type));
    }

    [Fact]
    public void TokenizeScriptBody_CompleteFastOpcodesPreserveStructuralTokensAndContinuation()
    {
        byte[] body =
        [
            0x47, 0x34, 0x12,
            0x48, 0x78, 0x56,
            0x49, 0xBC, 0x9A,
            (byte)QbTokenType.EndOfFile
        ];

        var tokens = QbFile.TokenizeScriptBody(body, structsBigEndian: false, newEncoding: false);

        Assert.Equal(
            [QbTokenType.KeywordIf, QbTokenType.KeywordElse, QbTokenType.EndOfFile],
            tokens.Select(static token => token.Type));
    }
}
