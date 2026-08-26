using System.Numerics;
using NeversoftMultitool.Core.Formats.Texture.Nds;

namespace NeversoftMultitool.Core.Formats.Mesh.Nds;

/// <summary>
///     A vertex produced by running a DS display list. <see cref="Matrix" /> is the
///     provenance of the matrix that transformed it — the display-list offset of
///     the last matrix command that produced the value (-1 = the initial identity).
///     The DS has no per-vertex bone indices; a vertex belongs to whatever matrix
///     was current when it was drawn, so provenance IS its joint identity, stable
///     across frames because the runtime animates by rewriting matrix operands in
///     place rather than by restructuring the list.
/// </summary>
public readonly record struct NdsVertex(
    Vector3 Position, Vector4 Color, Vector2 TexCoord, int Matrix = -1);

/// <summary>
///     The render state a run of triangles was drawn under. The low 16 bits of
///     <see cref="TexImageParam" /> are a VRAM address the runtime patches in, so
///     they are zero on disk and carry no meaning here; the format, size and wrap
///     bits above them do.
/// </summary>
public readonly record struct NdsMaterialKey(
    uint TexImageParam, uint PaletteBase, uint PolygonAttr, int TextureIndex)
{
    public int TextureWidth => 8 << (int)((TexImageParam >> 20) & 7);
    public int TextureHeight => 8 << (int)((TexImageParam >> 23) & 7);

    /// <summary>
    ///     The same GX field the texture banks encode, so it reuses their enum
    ///     rather than declaring a parallel one that could drift.
    /// </summary>
    public NdsTextureFormat TextureFormat => (NdsTextureFormat)((TexImageParam >> 26) & 7);

    public bool HasTexture => TextureFormat != NdsTextureFormat.None;

    /// <summary>TEXIMAGE_PARAM bit 16/17 repeat, 18/19 flip (mirror when repeating).</summary>
    public bool RepeatS => (TexImageParam & (1u << 16)) != 0;
    public bool RepeatT => (TexImageParam & (1u << 17)) != 0;
    public bool MirrorS => (TexImageParam & (1u << 18)) != 0;
    public bool MirrorT => (TexImageParam & (1u << 19)) != 0;

    /// <summary>Polygon alpha, 0 (wireframe) to 31 (opaque).</summary>
    public int Alpha => (int)((PolygonAttr >> 16) & 0x1F);
}

/// <summary>A run of triangles sharing one render state.</summary>
public sealed class NdsGeometryGroup
{
    public NdsMaterialKey Material { get; init; }
    public List<NdsVertex> Vertices { get; } = [];
    public List<int> Indices { get; } = [];
}

/// <summary>
///     Runs the DS geometry engine over a display list and returns triangles.
///
///     Fixed-point conventions are the hardware's: matrices and translations 20.12,
///     vertex coordinates 4.12, VTX_10's three 10-bit fields scaled to 4.12 by a
///     left shift of 6, texcoords 12.4 texels, COLOR is BGR555.
///
///     Two details are easy to get wrong and both were measured rather than assumed.
///     VTX_DIFF's 10-bit deltas are added to the 4.12 coordinate with NO further
///     scaling; the widely copied "sign extend then divide by 8" reading inflates
///     every axis, and the header's own bounding box shows it (Sk8land 0067ee06
///     declares 21.78/79.01/0.24 and the unscaled reading reproduces it exactly).
///     And each matrix mode needs its own stack: sharing one matrix across
///     projection, position and texture lets a texture transform move the geometry.
/// </summary>
public sealed class NdsGxInterpreter
{
    private const int MatrixSlots = 32;

    private readonly Matrix4x4[] _matrices = new Matrix4x4[4];
    private readonly List<Matrix4x4>[] _stacks =
        [[], [], [], []];

    /// <summary>
    ///     The position-matrix stack and the MTX_STORE/MTX_RESTORE slots are THE SAME
    ///     32-entry memory on the GX — PUSH writes <c>slots[ptr++]</c>, POP moves the
    ///     pointer and reloads, STORE/RESTORE address the array directly. A compiled
    ///     display list may lean on that aliasing (a matrix pushed at depth d
    ///     RESTOREd by slot number), so the stack is modelled the hardware's way.
    ///     Measured against the separate-storage model this changes no shipped
    ///     Sk8land model's output — the carts' lists happen to keep the two roles
    ///     disjoint — but only the hardware model is safe to patch animation
    ///     through, and the provenance tracking below depends on the pointer.
    /// </summary>
    private readonly Matrix4x4[] _slots = new Matrix4x4[MatrixSlots];
    private readonly int[] _slotProvenance = new int[MatrixSlots];
    private readonly Dictionary<int, Matrix4x4> _usedMatrices = [];

    private int _positionStackPointer;
    private int _provenance = -1;
    private readonly List<NdsGeometryGroup> _groups = [];
    private readonly Dictionary<NdsMaterialKey, NdsGeometryGroup> _byMaterial = [];
    private readonly List<int> _strip = [];
    private readonly Dictionary<int, int> _siteTextures = [];

    private int _mode;
    private int _vx, _vy, _vz;
    private Vector4 _color = Vector4.One;
    private Vector2 _texCoord = Vector2.Zero;
    private int _primitive = -1;
    private NdsMaterialKey _material = new(0, 0, 0, -1);
    private NdsGeometryGroup? _group;
    private NdsGeometryGroup? _stripGroup;

    public NdsGxInterpreter()
    {
        for (var i = 0; i < _matrices.Length; i++)
            _matrices[i] = Matrix4x4.Identity;
        for (var i = 0; i < MatrixSlots; i++)
        {
            _slots[i] = Matrix4x4.Identity;
            _slotProvenance[i] = -1;
        }
    }

    public IReadOnlyList<NdsGeometryGroup> Groups => _groups;

    /// <summary>
    ///     Every matrix a vertex was transformed by, keyed by provenance. These are
    ///     the model's effective joint globals for the pose the list was run with —
    ///     the bind pose on unpatched data, an animation frame on patched data.
    /// </summary>
    public IReadOnlyDictionary<int, Matrix4x4> UsedMatrices => _usedMatrices;

    public static IReadOnlyList<NdsGeometryGroup> Run(ReadOnlySpan<byte> data, NdsGeometryFile file)
    {
        return RunInterpreter(data, file).Groups;
    }

    /// <summary>Runs the list and returns the interpreter itself, for callers that also need <see cref="UsedMatrices" />.</summary>
    public static NdsGxInterpreter RunInterpreter(ReadOnlySpan<byte> data, NdsGeometryFile file)
    {
        var interpreter = new NdsGxInterpreter();
        foreach (var subObject in file.SubObjects)
        foreach (var site in subObject.PatchSites)
            interpreter._siteTextures[site] = subObject.TextureIndex;

        NdsDisplayList.Walk(data, file.DisplayListStart, file.DisplayListEnd, interpreter.Execute);
        return interpreter;
    }

    /// <summary>Modes 1 (position) and 2 (position+vector) share the position matrix.</summary>
    private int Slot => _mode == 2 ? 1 : _mode;

    private ref Matrix4x4 Current => ref _matrices[Slot];

    public void Execute(byte opcode, ReadOnlySpan<uint> p)
    {
        Execute(opcode, p, -1);
    }

    public void Execute(byte opcode, ReadOnlySpan<uint> p, int parameterOffset)
    {
        switch (opcode)
        {
            case NdsGxCommand.MatrixMode:
                _mode = (int)(p[0] & 3);
                break;
            case NdsGxCommand.MatrixPush:
                if (Slot == 1)
                {
                    if (_positionStackPointer < 31)
                    {
                        _slots[_positionStackPointer] = Current;
                        _slotProvenance[_positionStackPointer] = _provenance;
                    }

                    _positionStackPointer = Math.Min(_positionStackPointer + 1, 63);
                }
                else
                {
                    _stacks[Slot].Add(Current);
                }

                break;
            case NdsGxCommand.MatrixPop:
                PopMatrix((int)(p[0] & 0x3F));
                break;
            case NdsGxCommand.MatrixStore:
                _slots[p[0] & 31] = Current;
                _slotProvenance[p[0] & 31] = _provenance;
                break;
            case NdsGxCommand.MatrixRestore:
                Current = _slots[p[0] & 31];
                _provenance = _slotProvenance[p[0] & 31];
                break;
            case NdsGxCommand.MatrixIdentity:
                Current = Matrix4x4.Identity;
                Mutated(parameterOffset);
                break;
            case NdsGxCommand.MatrixLoad4x4:
                Current = Read4x4(p);
                Mutated(parameterOffset);
                break;
            case NdsGxCommand.MatrixLoad4x3:
                Current = Read4x3(p);
                Mutated(parameterOffset);
                break;
            case NdsGxCommand.MatrixMultiply4x4:
                Current = Read4x4(p) * Current;
                Mutated(parameterOffset);
                break;
            case NdsGxCommand.MatrixMultiply4x3:
                Current = Read4x3(p) * Current;
                Mutated(parameterOffset);
                break;
            case NdsGxCommand.MatrixMultiply3x3:
                Current = Read3x3(p) * Current;
                Mutated(parameterOffset);
                break;
            case NdsGxCommand.MatrixScale:
                Current = Matrix4x4.CreateScale(Fixed(p[0]), Fixed(p[1]), Fixed(p[2])) * Current;
                Mutated(parameterOffset);
                break;
            case NdsGxCommand.MatrixTranslate:
                Current = Matrix4x4.CreateTranslation(Fixed(p[0]), Fixed(p[1]), Fixed(p[2])) * Current;
                Mutated(parameterOffset);
                break;
            case NdsGxCommand.Color:
                _color = FromBgr555(p[0]);
                break;
            case NdsGxCommand.TexCoord:
                _texCoord = new Vector2(
                    (short)(p[0] & 0xFFFF) / 16f, (short)(p[0] >> 16) / 16f);
                break;
            case NdsGxCommand.Vertex16:
                _vx = (short)(p[0] & 0xFFFF);
                _vy = (short)(p[0] >> 16);
                _vz = (short)(p[1] & 0xFFFF);
                Emit();
                break;
            case NdsGxCommand.Vertex10:
                _vx = (short)((p[0] & 0x3FF) << 6);
                _vy = (short)(((p[0] >> 10) & 0x3FF) << 6);
                _vz = (short)(((p[0] >> 20) & 0x3FF) << 6);
                Emit();
                break;
            case NdsGxCommand.VertexXy:
                _vx = (short)(p[0] & 0xFFFF);
                _vy = (short)(p[0] >> 16);
                Emit();
                break;
            case NdsGxCommand.VertexXz:
                _vx = (short)(p[0] & 0xFFFF);
                _vz = (short)(p[0] >> 16);
                Emit();
                break;
            case NdsGxCommand.VertexYz:
                _vy = (short)(p[0] & 0xFFFF);
                _vz = (short)(p[0] >> 16);
                Emit();
                break;
            case NdsGxCommand.VertexDiff:
                _vx += (short)((p[0] & 0x3FF) << 6) >> 6;
                _vy += (short)(((p[0] >> 10) & 0x3FF) << 6) >> 6;
                _vz += (short)(((p[0] >> 20) & 0x3FF) << 6) >> 6;
                Emit();
                break;
            case NdsGxCommand.PolygonAttr:
                SetMaterial(_material with { PolygonAttr = p[0] });
                break;
            case NdsGxCommand.TexImageParam:
                // The parameter's VRAM address is blank on disk; the texture is
                // named by whichever sub-object lists THIS word as a patch site.
                SetMaterial(_material with
                {
                    TexImageParam = p[0],
                    TextureIndex = _siteTextures.TryGetValue(parameterOffset, out var index)
                        ? index
                        : -1
                });
                break;
            case NdsGxCommand.PaletteBase:
                SetMaterial(_material with { PaletteBase = p[0] });
                break;
            case NdsGxCommand.BeginVertices:
                _primitive = (int)(p[0] & 3);
                _strip.Clear();
                break;
            case NdsGxCommand.EndVertices:
                _primitive = -1;
                _strip.Clear();
                break;
        }
    }

    /// <summary>A position-mode mutation site becomes the current matrix's identity.</summary>
    private void Mutated(int parameterOffset)
    {
        if (Slot == 1)
            _provenance = parameterOffset;
    }

    private void PopMatrix(int count)
    {
        // The pop count is a 6-bit SIGNED stack-pointer delta: positive pops,
        // negative moves the pointer back up, zero reloads the current top.
        var n = count >= 0x20 ? count - 64 : count;
        if (Slot == 1)
        {
            _positionStackPointer = Math.Clamp(_positionStackPointer - n, 0, 63);
            if (_positionStackPointer < 31)
            {
                Current = _slots[_positionStackPointer];
                _provenance = _slotProvenance[_positionStackPointer];
            }

            return;
        }

        var stack = _stacks[Slot];
        for (var i = 0; i < Math.Max(1, Math.Abs(n)) && stack.Count > 0; i++)
        {
            Current = stack[^1];
            stack.RemoveAt(stack.Count - 1);
        }
    }

    private void SetMaterial(NdsMaterialKey key)
    {
        _material = key;
        _group = null;
    }

    private NdsGeometryGroup Group()
    {
        if (_group != null)
            return _group;
        if (!_byMaterial.TryGetValue(_material, out var group))
        {
            group = new NdsGeometryGroup { Material = _material };
            _byMaterial[_material] = group;
            _groups.Add(group);
        }

        _group = group;
        return group;
    }

    private void Emit()
    {
        if (_primitive < 0)
            return;

        var group = Group();
        if (!ReferenceEquals(group, _stripGroup))
        {
            // Vertex indices are per group, so a render-state change mid-primitive
            // would otherwise index the previous group's vertex list.
            _strip.Clear();
            _stripGroup = group;
        }

        var position = Vector3.Transform(
            new Vector3(_vx / 4096f, _vy / 4096f, _vz / 4096f), _matrices[1]);
        _usedMatrices[_provenance] = _matrices[1];
        group.Vertices.Add(new NdsVertex(position, _color, _texCoord, _provenance));
        _strip.Add(group.Vertices.Count - 1);
        Assemble(group);
    }

    private void Assemble(NdsGeometryGroup group)
    {
        var n = _strip.Count;
        switch (_primitive)
        {
            case 0 when n == 3:
                Triangle(group, _strip[0], _strip[1], _strip[2]);
                _strip.Clear();
                break;
            case 1 when n == 4:
                Triangle(group, _strip[0], _strip[1], _strip[2]);
                Triangle(group, _strip[0], _strip[2], _strip[3]);
                _strip.Clear();
                break;
            case 2 when n >= 3:
                // Triangle strip: alternate winding so every face keeps one orientation.
                if (n % 2 == 1)
                    Triangle(group, _strip[n - 3], _strip[n - 2], _strip[n - 1]);
                else
                    Triangle(group, _strip[n - 2], _strip[n - 3], _strip[n - 1]);
                break;
            case 3 when n >= 4 && n % 2 == 0:
                Triangle(group, _strip[n - 4], _strip[n - 3], _strip[n - 1]);
                Triangle(group, _strip[n - 4], _strip[n - 1], _strip[n - 2]);
                break;
        }
    }

    private static void Triangle(NdsGeometryGroup group, int a, int b, int c)
    {
        group.Indices.Add(a);
        group.Indices.Add(b);
        group.Indices.Add(c);
    }

    private static float Fixed(uint value)
    {
        return (int)value / 4096f;
    }

    private static Vector4 FromBgr555(uint value)
    {
        return new Vector4(
            (value & 31) / 31f,
            ((value >> 5) & 31) / 31f,
            ((value >> 10) & 31) / 31f,
            1f);
    }

    /// <summary>
    ///     DS matrices are row-vector: rows 0-2 are the basis and row 3 the
    ///     translation, which is <see cref="Matrix4x4" />'s own convention, so the
    ///     words load in order.
    /// </summary>
    private static Matrix4x4 Read4x4(ReadOnlySpan<uint> p)
    {
        return new Matrix4x4(
            Fixed(p[0]), Fixed(p[1]), Fixed(p[2]), Fixed(p[3]),
            Fixed(p[4]), Fixed(p[5]), Fixed(p[6]), Fixed(p[7]),
            Fixed(p[8]), Fixed(p[9]), Fixed(p[10]), Fixed(p[11]),
            Fixed(p[12]), Fixed(p[13]), Fixed(p[14]), Fixed(p[15]));
    }

    private static Matrix4x4 Read4x3(ReadOnlySpan<uint> p)
    {
        return new Matrix4x4(
            Fixed(p[0]), Fixed(p[1]), Fixed(p[2]), 0f,
            Fixed(p[3]), Fixed(p[4]), Fixed(p[5]), 0f,
            Fixed(p[6]), Fixed(p[7]), Fixed(p[8]), 0f,
            Fixed(p[9]), Fixed(p[10]), Fixed(p[11]), 1f);
    }

    private static Matrix4x4 Read3x3(ReadOnlySpan<uint> p)
    {
        return new Matrix4x4(
            Fixed(p[0]), Fixed(p[1]), Fixed(p[2]), 0f,
            Fixed(p[3]), Fixed(p[4]), Fixed(p[5]), 0f,
            Fixed(p[6]), Fixed(p[7]), Fixed(p[8]), 0f,
            0f, 0f, 0f, 1f);
    }
}
