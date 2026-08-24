namespace NeversoftMultitool.Core.Formats.Mesh.Nds;

/// <summary>
///     Nintendo DS geometry-engine command opcodes, and how many 32-bit parameters
///     each takes.
///
///     The parameter counts are what make a packed display list self-delimiting:
///     commands arrive four-to-a-word and their parameters follow in order, so one
///     wrong width desynchronises the stream within a few words. That is why an
///     exact parse to a declared boundary is strong evidence the reading is right,
///     and why <see cref="ParameterCount" /> returns -1 rather than guessing 0 for
///     an unknown opcode.
/// </summary>
public static class NdsGxCommand
{
    public const byte Nop = 0x00;
    public const byte MatrixMode = 0x10;
    public const byte MatrixPush = 0x11;
    public const byte MatrixPop = 0x12;
    public const byte MatrixStore = 0x13;
    public const byte MatrixRestore = 0x14;
    public const byte MatrixIdentity = 0x15;
    public const byte MatrixLoad4x4 = 0x16;
    public const byte MatrixLoad4x3 = 0x17;
    public const byte MatrixMultiply4x4 = 0x18;
    public const byte MatrixMultiply4x3 = 0x19;
    public const byte MatrixMultiply3x3 = 0x1A;
    public const byte MatrixScale = 0x1B;
    public const byte MatrixTranslate = 0x1C;
    public const byte Color = 0x20;
    public const byte Normal = 0x21;
    public const byte TexCoord = 0x22;
    public const byte Vertex16 = 0x23;
    public const byte Vertex10 = 0x24;
    public const byte VertexXy = 0x25;
    public const byte VertexXz = 0x26;
    public const byte VertexYz = 0x27;
    public const byte VertexDiff = 0x28;
    public const byte PolygonAttr = 0x29;
    public const byte TexImageParam = 0x2A;
    public const byte PaletteBase = 0x2B;
    public const byte DiffuseAmbient = 0x30;
    public const byte SpecularEmission = 0x31;
    public const byte LightVector = 0x32;
    public const byte LightColor = 0x33;
    public const byte Shininess = 0x34;
    public const byte BeginVertices = 0x40;
    public const byte EndVertices = 0x41;
    public const byte SwapBuffers = 0x50;
    public const byte Viewport = 0x60;
    public const byte BoxTest = 0x70;
    public const byte PositionTest = 0x71;
    public const byte VectorTest = 0x72;

    /// <summary>Parameter words for an opcode, or -1 when the byte is not a command.</summary>
    public static int ParameterCount(byte opcode)
    {
        return opcode switch
        {
            Nop => 0,
            MatrixMode or MatrixPop or MatrixStore or MatrixRestore => 1,
            MatrixPush or MatrixIdentity => 0,
            MatrixLoad4x4 or MatrixMultiply4x4 => 16,
            MatrixLoad4x3 or MatrixMultiply4x3 => 12,
            MatrixMultiply3x3 => 9,
            MatrixScale or MatrixTranslate => 3,
            Color or Normal or TexCoord or Vertex10 => 1,
            Vertex16 => 2,
            VertexXy or VertexXz or VertexYz or VertexDiff => 1,
            PolygonAttr or TexImageParam or PaletteBase => 1,
            DiffuseAmbient or SpecularEmission or LightVector or LightColor => 1,
            Shininess => 32,
            BeginVertices => 1,
            EndVertices => 0,
            SwapBuffers or Viewport => 1,
            BoxTest => 3,
            PositionTest => 2,
            VectorTest => 1,
            _ => -1
        };
    }

    /// <summary>True when the opcode places a vertex (and so advances primitive assembly).</summary>
    public static bool IsVertex(byte opcode)
    {
        return opcode is Vertex16 or Vertex10 or VertexXy or VertexXz or VertexYz or VertexDiff;
    }
}
