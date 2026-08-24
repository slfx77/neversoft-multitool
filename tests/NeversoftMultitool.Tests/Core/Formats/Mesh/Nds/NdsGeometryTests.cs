using System.Buffers.Binary;
using System.Numerics;
using NeversoftMultitool.Core.Formats.ArchiveFs;
using NeversoftMultitool.Core.Formats.Mesh.Nds;
using NeversoftMultitool.Core.Formats.Texture.Nds;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Nds;

/// <summary>
///     Pins the Vicarious Visions DS geometry format and the GX display-list decode.
///
///     The format has no magic and its files have no recoverable names, so detection
///     rests on structure: the declared display-list span must parse as GX commands
///     and consume EXACTLY. That is a strong statement — commands are packed four to
///     a word with their parameters following, so a single wrong parameter width
///     desynchronises the stream within a few words.
///
///     The corpus test adds a second, independent check the decoder cannot fake: the
///     header declares the model's own bounding box, and a decode with the wrong
///     vertex format, fixed-point scale or matrix convention will not reproduce it.
/// </summary>
public sealed class NdsGeometryTests(TestPaths paths)
{
    private static readonly (string Build, string Rom, string Gob, int Files)[] Carts =
    [
        ("Tony Hawk's American Sk8land (2005-11-15, DS - Final)",
            "Tony Hawk's American Sk8land (USA).nds", "vvobj/generated/gob/main.gob", 1167),
        ("Tony Hawk's Downhill Jam (2006-10-24, DS - Final)",
            "Tony Hawk's Downhill Jam (USA).nds", "vvobj/generated/gob/main.gob", 1404),
        ("Tony Hawk's Proving Ground (2007-10-15, DS - Final)",
            "Tony Hawk's Proving Ground (USA).nds", "gob/mainUS.gob", 2170)
    ];

    [Fact]
    public void TryParse_LocatesTheDisplayListFromTheStoredPrologueSize()
    {
        // w19 counts the prologue from 76, not from the end of the 84-byte header.
        // That reads oddly, and it is the only formula exact across the corpus: the
        // count-derived 84 + joints*12 + subObjects*4 is 93% right because joint
        // records are not a fixed 12 bytes in every file.
        var file = BuildGeometry(prologueBytes: 8, subObjectOffsets: [], displayList: [0x00000000u]);
        Assert.True(NdsGeometryFile.TryParse(file, out var parsed));
        Assert.Equal(84 + 8, parsed!.DisplayListStart);
    }

    [Fact]
    public void TryParse_EndsTheListAtTheFirstSubObjectOffset()
    {
        var file = BuildGeometry(prologueBytes: 12, subObjectOffsets: [96 + 8], displayList: [0u, 0u]);
        Assert.True(NdsGeometryFile.TryParse(file, out var parsed));
        Assert.Equal(96, parsed!.DisplayListStart);
        Assert.Equal(104, parsed.DisplayListEnd);
        Assert.Equal([104], parsed.SubObjectOffsets);
    }

    [Fact]
    public void TryParse_RejectsAnotherVersion()
    {
        var file = BuildGeometry(8, [], [0u]);
        BinaryPrimitives.WriteUInt32LittleEndian(file, 3);
        Assert.False(NdsGeometryFile.TryParse(file, out _));
    }

    [Fact]
    public void TryParseValidated_RejectsASpanThatIsNotADisplayList()
    {
        // 0x7F is not a GX opcode, so the span cannot consume.
        var file = BuildGeometry(8, [], [0x7F7F7F7Fu]);
        Assert.True(NdsGeometryFile.TryParse(file, out _));
        Assert.False(NdsGeometryFile.TryParseValidated(file, out _));
    }

    [Fact]
    public void Walk_ConsumesExactlyWhenEveryParameterWidthIsRight()
    {
        // MTX_MODE(1 param) + MTX_PUSH(0) + BEGIN_VTXS(1) + END_VTXS(0).
        var file = BuildGeometry(8, [], [
            Pack(NdsGxCommand.MatrixMode, NdsGxCommand.MatrixPush,
                NdsGxCommand.BeginVertices, NdsGxCommand.EndVertices),
            1u, 0u
        ]);
        Assert.True(NdsGeometryFile.TryParse(file, out var parsed));
        Assert.Equal(parsed!.DisplayListEnd,
            NdsDisplayList.Walk(file, parsed.DisplayListStart, parsed.DisplayListEnd, null));
    }

    [Fact]
    public void VertexDiff_AddsTheTenBitDeltaWithNoExtraScaling()
    {
        // Measured against the header's own bounding box, not assumed: the common
        // "sign extend then divide by 8" reading inflates every axis, and Sk8land
        // 0067ee06 declares 21.78/79.01/0.24 which only the unscaled reading gives.
        var interpreter = new NdsGxInterpreter();
        interpreter.Execute(NdsGxCommand.BeginVertices, [0u]);
        interpreter.Execute(NdsGxCommand.Vertex16, [0u, 0u]);
        // +16 on X, +1 on Y, -1 on Z, as three 10-bit signed fields.
        var delta = 16u | (1u << 10) | (0x3FFu << 20);
        interpreter.Execute(NdsGxCommand.VertexDiff, [delta]);

        var group = Assert.Single(interpreter.Groups);
        var moved = group.Vertices[1].Position;
        Assert.Equal(16 / 4096f, moved.X, 6);
        Assert.Equal(1 / 4096f, moved.Y, 6);
        Assert.Equal(-1 / 4096f, moved.Z, 6);
    }

    [Fact]
    public void Quads_BecomeTwoTrianglesAndTrianglesStandAlone()
    {
        Assert.Equal(3, RunPrimitive(mode: 0, vertices: 3).Indices.Count);
        Assert.Equal(6, RunPrimitive(mode: 1, vertices: 4).Indices.Count);
        Assert.Equal(9, RunPrimitive(mode: 2, vertices: 5).Indices.Count);  // strip: n-2 triangles
        Assert.Equal(12, RunPrimitive(mode: 3, vertices: 6).Indices.Count); // quad strip: 2 quads
    }

    [Fact]
    public void VerticesOutsideABeginBlockAreNotEmitted()
    {
        var interpreter = new NdsGxInterpreter();
        interpreter.Execute(NdsGxCommand.Vertex16, [0u, 0u]);
        Assert.Empty(interpreter.Groups);
    }

    [Fact]
    public void TextureMatrixOperationsDoNotMoveGeometry()
    {
        // One matrix serving all four modes lets a texture transform drag the
        // model; each mode needs its own stack.
        var interpreter = new NdsGxInterpreter();
        interpreter.Execute(NdsGxCommand.MatrixMode, [3u]); // texture
        interpreter.Execute(NdsGxCommand.MatrixTranslate, [4096u, 4096u, 4096u]);
        interpreter.Execute(NdsGxCommand.MatrixMode, [1u]); // position
        interpreter.Execute(NdsGxCommand.BeginVertices, [0u]);
        interpreter.Execute(NdsGxCommand.Vertex16, [0u, 0u]);

        var group = Assert.Single(interpreter.Groups);
        Assert.Equal(Vector3.Zero, group.Vertices[0].Position);
    }

    [Fact]
    public void MaterialKeyDecodesTheTextureImageParameter()
    {
        // 0x4db30000: 64x64 Palette16 with S and T repeat, VRAM address still zero
        // because the runtime patches it in.
        var key = new NdsMaterialKey(0x4DB30000u, 0u, 0u, TextureIndex: 3);
        Assert.Equal(64, key.TextureWidth);
        Assert.Equal(64, key.TextureHeight);
        Assert.Equal(NdsTextureFormat.Palette16, key.TextureFormat);
        Assert.True(key.HasTexture);
    }

    [CorpusTheory]
    [MemberData(nameof(CartCases))]
    public void RealCart_EveryGeometryFilesDisplayListConsumesExactly(
        string build, string rom, string gobPath, int expectedFiles)
    {
        var romPath = paths.FindSampleFile(build, rom);
        Assert.SkipWhen(romPath == null, $"{build} ROM sample not available");

        using var cart = ArchiveFileSystem.TryOpen(romPath!);
        using var gob = cart!.TryOpenNested(cart.FindByPath(gobPath)!);
        Assert.NotNull(gob);

        var files = 0;
        var exact = 0;
        var triangles = 0;
        foreach (var entry in gob!.Entries)
        {
            byte[] data;
            try
            {
                data = gob.ReadEntry(entry);
            }
            catch (InvalidDataException)
            {
                continue;
            }

            if (!NdsGeometryFile.TryParse(data, out var geometry))
                continue;

            files++;
            if (NdsDisplayList.Walk(data, geometry!.DisplayListStart, geometry.DisplayListEnd, null)
                == geometry.DisplayListEnd)
            {
                exact++;
            }

            foreach (var group in NdsGxInterpreter.Run(data, geometry))
                triangles += group.Indices.Count / 3;
        }

        Assert.Equal(expectedFiles, files);
        Assert.Equal(files, exact);
        Assert.True(triangles > 0);
    }

    [CorpusTheory]
    [MemberData(nameof(BoundsCases))]
    public void RealCart_SelfContainedRigidModelsReproduceTheirDeclaredBoundingBox(
        string build, string rom, string gobPath, string expected)
    {
        // The header declares the model's own extents, which a decoder with a wrong
        // vertex format, fixed-point scale or matrix convention will not reproduce.
        //
        // Two classes are excluded because the FILE genuinely does not determine
        // their vertices. A skinned model (joints > 0) takes its bind pose from
        // joint matrices the runtime loads first. And a model whose list restores a
        // matrix slot it never stored is drawn relative to a runtime matrix; those
        // come out uniformly scaled, right shape and wrong size, which is exactly
        // what a missing outer transform looks like.
        var romPath = paths.FindSampleFile(build, rom);
        Assert.SkipWhen(romPath == null, $"{build} ROM sample not available");

        using var cart = ArchiveFileSystem.TryOpen(romPath!);
        using var gob = cart!.TryOpenNested(cart.FindByPath(gobPath)!);
        Assert.NotNull(gob);

        var matches = 0;
        var total = 0;
        foreach (var entry in gob!.Entries)
        {
            byte[] data;
            try
            {
                data = gob.ReadEntry(entry);
            }
            catch (InvalidDataException)
            {
                continue;
            }

            if (!NdsGeometryFile.TryParseValidated(data, out var geometry) || geometry.JointCount != 0)
                continue;
            if (UsesRuntimeMatrix(data, geometry))
                continue;

            var groups = NdsGxInterpreter.Run(data, geometry);
            if (!TryMeasure(groups, out var measured))
                continue;

            total++;
            if (Matches(measured, geometry.DeclaredExtent))
                matches++;
        }

        // One assertion so a failure reports both halves of the ratio at once.
        Assert.Equal(expected, $"{matches}/{total}");
    }

    private static bool UsesRuntimeMatrix(ReadOnlySpan<byte> data, NdsGeometryFile file)
    {
        var stored = 0u;
        var external = false;
        NdsDisplayList.Walk(data, file.DisplayListStart, file.DisplayListEnd, (opcode, p, _) =>
        {
            if (opcode == NdsGxCommand.MatrixStore)
                stored |= 1u << (int)(p[0] & 31);
            else if (opcode == NdsGxCommand.MatrixRestore && (stored & (1u << (int)(p[0] & 31))) == 0)
                external = true;
        });
        return external;
    }

    private static bool TryMeasure(IReadOnlyList<NdsGeometryGroup> groups, out Vector3 extent)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        var any = false;
        foreach (var group in groups)
        {
            foreach (var index in group.Indices)
            {
                min = Vector3.Min(min, group.Vertices[index].Position);
                max = Vector3.Max(max, group.Vertices[index].Position);
                any = true;
            }
        }

        extent = any ? max - min : Vector3.Zero;
        return any;
    }

    private static bool Matches(Vector3 measured, Vector3 declared)
    {
        return Axis(measured.X, declared.X) && Axis(measured.Y, declared.Y)
                                            && Axis(measured.Z, declared.Z);

        // Axes below a twentieth of a unit are flat planes where the declared value
        // is rounded to nothing, so they carry no information either way.
        static bool Axis(float measured, float declared)
        {
            return measured <= 0.05f || declared <= 0.05f
                                     || Math.Abs(declared - measured) <= 0.02f * declared;
        }
    }

    [Fact]
    public void SubObjects_BindATextureIndexToDisplayListSites()
    {
        // A record is {u32 scratch, u32 textureIndex, u32 count, i32 rel[count]},
        // and every rel is RECORD-relative, pointing back into the list.
        var file = BuildTexturedGeometry(textureIndex: 7);
        Assert.True(NdsGeometryFile.TryParse(file, out var parsed));

        var subObject = Assert.Single(parsed!.SubObjects);
        Assert.Equal(7, subObject.TextureIndex);
        var site = Assert.Single(subObject.PatchSites);
        Assert.InRange(site, parsed.DisplayListStart, parsed.DisplayListEnd - 4);
    }

    [Fact]
    public void Interpreter_TakesTheTextureIndexFromTheSiteThatOwnsIt()
    {
        // TEXIMAGE_PARAM's VRAM address is blank on disk, so the texture is named
        // only by whichever sub-object lists that word as a patch site.
        var file = BuildTexturedGeometry(textureIndex: 7);
        Assert.True(NdsGeometryFile.TryParse(file, out var parsed));

        var group = Assert.Single(NdsGxInterpreter.Run(file, parsed!));
        Assert.Equal(7, group.Material.TextureIndex);
        Assert.Equal(NdsTextureFormat.Palette16, group.Material.TextureFormat);
    }

    [Fact]
    public void Interpreter_LeavesTheIndexUnboundWhenNoSubObjectClaimsTheSite()
    {
        var interpreter = new NdsGxInterpreter();
        interpreter.Execute(NdsGxCommand.TexImageParam, [0x4DB30000u]);
        interpreter.Execute(NdsGxCommand.BeginVertices, [0u]);
        for (var i = 0; i < 3; i++)
            interpreter.Execute(NdsGxCommand.Vertex16, [(uint)(i * 0x10), 0u]);

        Assert.Equal(-1, Assert.Single(interpreter.Groups).Material.TextureIndex);
    }

    [Fact]
    public void BankResolver_TakesTheOnlyCompatibleBank()
    {
        // The true bank always satisfies the size/format constraints, so it is always
        // a candidate; a single survivor is therefore the answer rather than a guess.
        var groups = TexturedGroups(index: 2, param: 0x4DB30000u);
        var right = Bank(0x4DB30000u, 0x4DB30000u, 0x4DB30000u);
        var wrongSize = Bank(0x4DB30000u, 0x4DB30000u, 0x4D230000u);
        var tooSmall = Bank(0x4DB30000u, 0x4DB30000u);

        Assert.Same(right, NdsTextureBankResolver.Resolve(groups, [right, wrongSize, tooSmall]));
    }

    [Fact]
    public void BankResolver_DeclinesWhenSeveralBanksFit()
    {
        // Candidate banks never agree on the actual texel blob, so binding one of
        // several would put a plausible wrong image on the model.
        var groups = TexturedGroups(index: 0, param: 0x4DB30000u);
        Assert.Null(NdsTextureBankResolver.Resolve(
            groups, [Bank(0x4DB30000u), Bank(0x4DB30000u)]));
    }

    [Fact]
    public void BankResolver_IgnoresTheColourZeroBitWhichOnlyBanksSet()
    {
        // Banks set bit 29 on 99-197 records per cart and no model site ever does,
        // so comparing it rejects the true bank for about a sixth of all models.
        var groups = TexturedGroups(index: 0, param: 0x4DB30000u);
        var keyed = Bank(0x4DB30000u | (1u << 29));
        Assert.Same(keyed, NdsTextureBankResolver.Resolve(groups, [keyed]));
    }

    [CorpusTheory]
    [MemberData(nameof(TextureCases))]
    public void RealCart_EveryPatchSiteIsATextureImageParameter(
        string build, string rom, string gobPath, string expected)
    {
        // This is the whole basis of the texture binding: if a listed offset were
        // anything but a TEXIMAGE_PARAM parameter, the reading would be wrong.
        var romPath = paths.FindSampleFile(build, rom);
        Assert.SkipWhen(romPath == null, $"{build} ROM sample not available");

        using var cart = ArchiveFileSystem.TryOpen(romPath!);
        using var gob = cart!.TryOpenNested(cart.FindByPath(gobPath)!);
        Assert.NotNull(gob);

        var banks = ReadBanks(gob!);
        var sites = 0;
        var onTexture = 0;
        var textured = 0;
        var resolved = 0;
        foreach (var entry in gob!.Entries)
        {
            byte[] data;
            try
            {
                data = gob.ReadEntry(entry);
            }
            catch (InvalidDataException)
            {
                continue;
            }

            if (!NdsGeometryFile.TryParseValidated(data, out var geometry))
                continue;

            var wanted = new HashSet<int>();
            foreach (var subObject in geometry.SubObjects)
            foreach (var site in subObject.PatchSites)
                wanted.Add(site);
            sites += wanted.Count;

            NdsDisplayList.Walk(data, geometry.DisplayListStart, geometry.DisplayListEnd,
                (opcode, _, offset) =>
                {
                    if (opcode == NdsGxCommand.TexImageParam && wanted.Contains(offset))
                        onTexture++;
                });

            var groups = NdsGxInterpreter.Run(data, geometry);
            if (!groups.Any(g => g.Indices.Count > 0 && g.Material.HasTexture
                                                     && g.Material.TextureIndex >= 0))
            {
                continue;
            }

            textured++;
            if (NdsTextureBankResolver.Resolve(groups, banks) != null)
                resolved++;
        }

        Assert.Equal(expected, $"{onTexture}/{sites} {resolved}/{textured}");
    }

    private static List<IReadOnlyList<NdsTextureEntry>> ReadBanks(IArchiveFileSystem gob)
    {
        var texels = new Dictionary<uint, long>();
        foreach (var entry in gob.Entries)
        {
            if (entry.Name.EndsWith(".texture.bin", StringComparison.Ordinal)
                && uint.TryParse(entry.Name.AsSpan(0, 8),
                    System.Globalization.NumberStyles.HexNumber, null, out var id))
            {
                texels[id] = entry.Size;
            }
        }

        long? Length(uint id) => texels.TryGetValue(id, out var size) ? size : null;

        var banks = new List<IReadOnlyList<NdsTextureEntry>>();
        foreach (var entry in gob.Entries)
        {
            byte[] data;
            try
            {
                data = gob.ReadEntry(entry);
            }
            catch (InvalidDataException)
            {
                continue;
            }

            if (NdsTextureBank.TryParseValidated(data, Length, out var bank))
                banks.Add(bank);
        }

        return banks;
    }

    public static TheoryData<string, string, string, string> TextureCases()
    {
        var data = new TheoryData<string, string, string, string>();
        data.Add(Carts[0].Build, Carts[0].Rom, Carts[0].Gob, "4578/4578 461/866");
        data.Add(Carts[1].Build, Carts[1].Rom, Carts[1].Gob, "9619/9619 280/946");
        data.Add(Carts[2].Build, Carts[2].Rom, Carts[2].Gob, "11154/11154 324/1330");
        return data;
    }

    private static IReadOnlyList<NdsGeometryGroup> TexturedGroups(int index, uint param)
    {
        var group = new NdsGeometryGroup { Material = new NdsMaterialKey(param, 0, 0, index) };
        group.Vertices.Add(new NdsVertex(Vector3.Zero, Vector4.One, Vector2.Zero));
        group.Indices.AddRange([0, 0, 0]);
        return [group];
    }

    private static NdsTextureEntry[] Bank(params uint[] parameters)
    {
        return [.. parameters.Select((p, i) => new NdsTextureEntry(
            (uint)(0x1000 + i), 2048, p, NdsTextureFormat.Palette16, 64, 64, false, []))];
    }

    /// <summary>A one-sub-object model: TEXIMAGE_PARAM, a triangle, and a record naming both.</summary>
    private static byte[] BuildTexturedGeometry(int textureIndex)
    {
        var list = new List<uint>
        {
            Pack(NdsGxCommand.TexImageParam, NdsGxCommand.BeginVertices,
                NdsGxCommand.Vertex16, NdsGxCommand.Vertex16),
            0x4DB30000u, 0u, 0x00100010u, 0u, 0x00200020u, 0u,
            Pack(NdsGxCommand.Vertex16, NdsGxCommand.Nop, NdsGxCommand.Nop, NdsGxCommand.Nop),
            0x00300030u, 0u
        };

        const int prologue = 8;
        var start = 84 + prologue;
        var recordAt = start + list.Count * 4;
        var file = new byte[recordAt + 16];

        BinaryPrimitives.WriteUInt32LittleEndian(file, 4);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(60), 84);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(64), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(68), (uint)(start - 4));
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(72), (uint)file.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(76), (uint)(start - 76));
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(start - 4), (uint)recordAt);

        for (var i = 0; i < list.Count; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(start + i * 4), list[i]);

        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(recordAt + 4), (uint)textureIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(recordAt + 8), 1);
        // The site is the TEXIMAGE_PARAM parameter, one word after the command word.
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(recordAt + 12), start + 4 - recordAt);
        return file;
    }

    public static TheoryData<string, string, string, string> BoundsCases()
    {
        var data = new TheoryData<string, string, string, string>();
        data.Add(Carts[0].Build, Carts[0].Rom, Carts[0].Gob, "731/808");
        data.Add(Carts[1].Build, Carts[1].Rom, Carts[1].Gob, "793/808");
        data.Add(Carts[2].Build, Carts[2].Rom, Carts[2].Gob, "944/973");
        return data;
    }

    public static TheoryData<string, string, string, int> CartCases()
    {
        var data = new TheoryData<string, string, string, int>();
        foreach (var (build, rom, gob, files) in Carts)
            data.Add(build, rom, gob, files);
        return data;
    }

    private static NdsGeometryGroup RunPrimitive(int mode, int vertices)
    {
        var interpreter = new NdsGxInterpreter();
        interpreter.Execute(NdsGxCommand.BeginVertices, [(uint)mode]);
        for (var i = 0; i < vertices; i++)
            interpreter.Execute(NdsGxCommand.Vertex16, [(uint)(i * 0x10), 0u]);
        return Assert.Single(interpreter.Groups);
    }

    private static uint Pack(byte a, byte b, byte c, byte d)
    {
        return a | ((uint)b << 8) | ((uint)c << 16) | ((uint)d << 24);
    }

    /// <summary>Builds a minimal version-4 file: header, prologue, then a display list.</summary>
    private static byte[] BuildGeometry(int prologueBytes, int[] subObjectOffsets, uint[] displayList)
    {
        var start = 84 + prologueBytes;
        var end = start + displayList.Length * 4;
        var file = new byte[end + 4];

        BinaryPrimitives.WriteUInt32LittleEndian(file, 4);            // version
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(60), 84); // w15, constant
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(64), (uint)subObjectOffsets.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(68), (uint)(start - subObjectOffsets.Length * 4));
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(72), (uint)end);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(76), (uint)(start - 76));

        var table = start - subObjectOffsets.Length * 4;
        for (var i = 0; i < subObjectOffsets.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(table + i * 4), (uint)subObjectOffsets[i]);
        for (var i = 0; i < displayList.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(start + i * 4), displayList[i]);
        return file;
    }
}
