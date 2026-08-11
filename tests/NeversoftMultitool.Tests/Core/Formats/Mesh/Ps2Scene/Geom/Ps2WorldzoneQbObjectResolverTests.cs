using System.Numerics;
using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;
using NeversoftMultitool.Core.Formats.Qb;
using QbKeyHash = NeversoftMultitool.Core.QbKey.QbKey;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Ps2Scene.Geom;

public sealed class Ps2WorldzoneQbObjectResolverTests
{
    private const uint RnbTypeHash = 0x91E1028D;
    private const uint StexTypeHash = 0x2B0A3095;

    [Fact]
    public void PopulatePs2Worldzone_EmptyArchive_ClearsThreadStaticLighting()
    {
        var document = new ModelDocument { Name = "empty" };

        Ps2WorldzoneGeometryWriter.PopulatePs2Worldzone(
            document,
            [],
            "empty.pak.ps2",
            textureProvider: null,
            texaTextureProvider: null,
            tex0Resolver: null,
            textureCatalog: null,
            textureSourceHint: null,
            timeOfDay: WorldzoneTimeOfDay.All,
            coordinateScale: 1f,
            lighting: Ps2WorldzoneLighting.Default);

        Assert.Null(ModelDocumentGeometryAdapter.ActivePs2WorldzoneLighting);
    }

    [Fact]
    public void FindObjectResourceTriads_ExactOrderedCrcZeroTriad_OwnsMdl()
    {
        var owner = Entry(
            "ac_unit01.qb.ps2",
            100,
            crc: 0x61B2A297,
            directory: "models/props/ac_unit01");
        var mdl = Entry("0003D1D0.mdl", 200);
        var collision = Entry("0003D1D0.mcol", 300);
        var entries = new[]
        {
            Typed(Ps2WorldzoneQbObjectResolver.ModelQbTypeHash, owner),
            Typed(Ps2WorldzoneDetection.WorldzoneMdlTypeHash, mdl),
            Typed(Ps2WorldzoneQbObjectResolver.CollisionTypeHash, collision)
        };

        var triad = Assert.Single(Ps2WorldzoneQbObjectResolver.FindObjectResourceTriads(entries));

        Assert.Same(owner, triad.OwnerQbEntry);
        Assert.Same(mdl, triad.MdlEntry);
        Assert.Same(collision, triad.CollisionEntry);
        Assert.Equal("ac_unit01", triad.OwnerName);
        Assert.Equal(Hash("ac_unit01"), triad.ProfileChecksum);
    }

    [Fact]
    public void FindObjectResourceTriads_AdjacentTypeAndCrcNearMisses_AreRejected()
    {
        var cases = new IReadOnlyList<(uint TypeHash, ArchiveEntry Entry)>[]
        {
            // An RNB after the MDL identifies the older placement-family resource.
            [Owner(100), Mdl(200), Typed(RnbTypeHash, Entry("resource.rnb", 300))],
            // Main-menu resources can put STEX after the MDL and are not object triads.
            [Owner(100), Mdl(200), Typed(StexTypeHash, Entry("resource.stex", 300))],
            // Named owner MQBs carry a name CRC; a zero value is not sufficient ownership evidence.
            [Owner(100, crc: 0), Mdl(200), Collision(300)],
            [Owner(100), Mdl(200, crc: 1), Collision(300)],
            [Owner(100), Mdl(200), Collision(300, crc: 1)],
            // All three types occur, but the unrelated entry breaks immediate ownership.
            [Owner(100), Typed(0x12345678, Entry("intervening.bin", 150)), Mdl(200), Collision(300)]
        };

        Assert.All(cases, entries =>
            Assert.Empty(Ps2WorldzoneQbObjectResolver.FindObjectResourceTriads(entries)));
    }

    [Fact]
    public void HasSingleModelExportNode_RejectsMalformedOrNamelessOwnerStructs()
    {
        var ownerName = "ac_unit01";
        var nodeArray = Hash(ownerName + "_NodeArray");
        var cycleA = Hash("cycle_a");
        var cycleB = Hash("cycle_b");
        var validNode = Struct(
            NameField("name", "AC_unit_01"),
            NameField("Class", "ModelExport"));
        var malformedOwner = new SyntheticQbBuilder()
            .Global(cycleA, Struct(Flag(cycleB)))
            .Global(cycleB, Struct(Flag(cycleA)))
            .Global(nodeArray, Array(validNode, Struct(Flag(cycleA))))
            .Build();
        var namelessOwner = new SyntheticQbBuilder()
            .Global(nodeArray, Array(
                validNode,
                Struct(NameField("Class", "ModelExport"))))
            .Build();
        var nonStructOwner = new SyntheticQbBuilder()
            .Global(nodeArray, Array(
                validNode,
                Flag("unexpected_array_item")))
            .Build();

        Assert.False(Ps2WorldzoneQbObjectResolver.HasSingleModelExportNode(
            malformedOwner, ownerName));
        Assert.False(Ps2WorldzoneQbObjectResolver.HasSingleModelExportNode(
            namelessOwner, ownerName));
        Assert.False(Ps2WorldzoneQbObjectResolver.HasSingleModelExportNode(
            nonStructOwner, ownerName));
    }

    [Fact]
    public void TryResolveProfileInstances_InheritedTemplate_NodeLocalFieldsOverrideDefaults()
    {
        var profile = Hash("AC_Unit01");
        var baseTemplate = Hash("base_object_template");
        var profileTemplate = Hash("compressed_profile_template");
        var nodeName = Hash("Z_TEST_Bouncy_AC_01");
        var expectedPosition = new Vector3(-12137.661f, 538.9507f, 4473.5576f);
        var expectedAngles = new Vector3(0.37f, -0.61f, 1.04f);
        var qb = new SyntheticQbBuilder()
            .Global(baseTemplate, Struct(
                NameField("Class", "gameobject"),
                NameField("Type", profile),
                NameField("profile", profile),
                Flag("CreatedAtStart"),
                Flag("RenderToViewport"),
                NameField("name", "template_default_name"),
                VectorField("pos", new Vector3(1, 2, 3)),
                VectorField("Angles", new Vector3(4, 5, 6))))
            .Global(profileTemplate, Struct(Flag(baseTemplate)))
            .Global(Hash("Z_TEST_NodeArray"), Array(Struct(
                NameField("name", nodeName),
                VectorField("pos", expectedPosition),
                VectorField("Angles", expectedAngles),
                Flag(profileTemplate))))
            .Build();

        var resolved = Ps2WorldzoneQbObjectResolver.TryResolveProfileInstances(
            [qb], profile, isNetworkArchive: false, out var instances);

        Assert.True(resolved);
        var instance = Assert.Single(instances);
        Assert.Equal(nodeName, instance.NodeChecksum);
        Assert.Equal(expectedPosition, instance.Position);
        Assert.Equal(expectedAngles, instance.Angles);
        AssertQuaternionEquivalent(ExpectedRotation(expectedAngles), instance.Rotation);
    }

    [Fact]
    public void TryResolveProfileInstances_MissingCreatedAtStart_IsResolvedEmpty()
    {
        var profile = Hash("grbg_pizza01");
        var qb = BuildSingleProfileQb(profile, flags: ["RenderToViewport"]);

        var resolved = Ps2WorldzoneQbObjectResolver.TryResolveProfileInstances(
            [qb], profile, isNetworkArchive: false, out var instances);

        Assert.True(resolved);
        Assert.Empty(instances);
    }

    [Fact]
    public void TryResolveProfileInstances_AbsentInNetGames_IsOnlyExcludedForNetworkArchive()
    {
        var profile = Hash("metal_barrel01");
        var qb = BuildSingleProfileQb(
            profile,
            flags: ["CreatedAtStart", "RenderToViewport", "AbsentInNetGames"]);

        Assert.True(Ps2WorldzoneQbObjectResolver.TryResolveProfileInstances(
            [qb], profile, isNetworkArchive: false, out var singlePlayerInstances));
        Assert.Single(singlePlayerInstances);

        Assert.True(Ps2WorldzoneQbObjectResolver.TryResolveProfileInstances(
            [qb], profile, isNetworkArchive: true, out var networkInstances));
        Assert.Empty(networkInstances);
    }

    [Fact]
    public void TryResolveProfileInstances_DuplicateProfileNodeArrays_AreRejected()
    {
        var profile = Hash("chair_Iron_01");
        var template = Hash("chair_template");
        var qb = new SyntheticQbBuilder()
            .Global(template, ProfileTemplate(profile))
            .Global(Hash("Z_A_NodeArray"), Array(ProfileNode(template, "chair_a")))
            .Global(Hash("Z_B_NodeArray"), Array(ProfileNode(template, "chair_b")))
            .Build();

        Assert.False(Ps2WorldzoneQbObjectResolver.TryResolveProfileInstances(
            [qb], profile, isNetworkArchive: false, out _));
    }

    [Fact]
    public void TryResolveProfileInstances_ValidAndMalformedTargetNodeArrays_AreRejected()
    {
        var profile = Hash("chair_Iron_01");
        var template = Hash("chair_template");
        var qb = new SyntheticQbBuilder()
            .Global(template, ProfileTemplate(profile))
            .Global(Hash("Z_VALID_NodeArray"), Array(ProfileNode(template, "chair_a")))
            .Global(Hash("Z_MALFORMED_NodeArray"), Array(Struct(
                Flag(template),
                NameField("name", "chair_b"),
                // A known target without an authored position is invalid, not a
                // second array that can be silently ignored.
                VectorField("Angles", Vector3.Zero))))
            .Build();

        Assert.False(Ps2WorldzoneQbObjectResolver.TryResolveProfileInstances(
            [qb], profile, isNetworkArchive: false, out _));
    }

    [Fact]
    public void TryResolveProfileInstances_DuplicateTemplateDefinition_IsRejected()
    {
        var profile = Hash("plant_bh_01");
        var template = Hash("plant_template");
        var qb = new SyntheticQbBuilder()
            .Global(template, ProfileTemplate(profile))
            .Global(template, ProfileTemplate(profile))
            .Global(Hash("Z_TEST_NodeArray"), Array(ProfileNode(template, "plant")))
            .Build();

        Assert.False(Ps2WorldzoneQbObjectResolver.TryResolveProfileInstances(
            [qb], profile, isNetworkArchive: false, out _));
    }

    [Fact]
    public void TryResolveProfileInstances_CyclicTemplateInheritance_IsRejected()
    {
        var profile = Hash("plant_bh_02");
        var templateA = Hash("cycle_a");
        var templateB = Hash("cycle_b");
        var qb = new SyntheticQbBuilder()
            .Global(templateA, Struct(
                Flag(templateB),
                NameField("Class", "gameobject"),
                NameField("Type", profile),
                NameField("profile", profile)))
            .Global(templateB, Struct(Flag(templateA)))
            .Global(Hash("Z_TEST_NodeArray"), Array(ProfileNode(templateA, "plant")))
            .Build();

        Assert.False(Ps2WorldzoneQbObjectResolver.TryResolveProfileInstances(
            [qb], profile, isNetworkArchive: false, out _));
    }

    [Fact]
    public void TryResolveProfileInstances_OverlongAcyclicInheritance_IsRejected()
    {
        var profile = Hash("plant_bh_02");
        var templates = Enumerable.Range(0, 65)
            .Select(index => Hash($"deep_template_{index}"))
            .ToArray();
        var builder = new SyntheticQbBuilder();
        for (var index = 0; index < templates.Length - 1; index++)
            builder.Global(templates[index], Struct(Flag(templates[index + 1])));
        var qb = builder
            .Global(templates[^1], ProfileTemplate(profile))
            .Global(Hash("Z_TEST_NodeArray"), Array(ProfileNode(templates[0], "plant")))
            .Build();

        Assert.False(Ps2WorldzoneQbObjectResolver.TryResolveProfileInstances(
            [qb], profile, isNetworkArchive: false, out _));
    }

    [Fact]
    public void TryResolveProfileInstances_MultipleProfileTemplatesWithinOneNodeArray_AreCombined()
    {
        var profile = Hash("Table_Iron_01");
        var templateA = Hash("table_template_a");
        var templateB = Hash("table_template_b");
        var qb = new SyntheticQbBuilder()
            .Global(templateA, ProfileTemplate(profile))
            .Global(templateB, ProfileTemplate(profile))
            .Global(Hash("Z_TEST_NodeArray"), Array(
                ProfileNode(templateA, "table_a"),
                ProfileNode(templateB, "table_b")))
            .Build();

        Assert.True(Ps2WorldzoneQbObjectResolver.TryResolveProfileInstances(
            [qb], profile, isNetworkArchive: false, out var instances));
        Assert.Equal(2, instances.Count);
    }

    [Fact]
    public void TryResolveProfileInstances_DirectInlineProfileNode_IsAccepted()
    {
        var profile = Hash("Table_little01");
        var qb = new SyntheticQbBuilder()
            .Global(Hash("Z_TEST_NodeArray"), Array(Struct(
                NameField("name", "Z_TEST_Bouncy_Table01"),
                VectorField("pos", new Vector3(1f, 2f, 3f)),
                VectorField("Angles", new Vector3(0f, 0.785398f, 0f)),
                NameField("Class", "gameobject"),
                NameField("Type", profile),
                Flag("CreatedAtStart"),
                Flag("RenderToViewport"),
                NameField("profile", profile))))
            .Build();

        Assert.True(Ps2WorldzoneQbObjectResolver.TryResolveProfileInstances(
            [qb], profile, isNetworkArchive: false, out var instances));
        Assert.Single(instances);
    }

    [Fact]
    public void TryResolveProfileInstances_ConflictingInheritedFieldFromNonTargetBase_IsRejected()
    {
        var profile = Hash("Table_Iron_01");
        var conflictingBase = Hash("unrelated_base");
        var profileTemplate = Hash("table_profile_template");
        var qb = new SyntheticQbBuilder()
            .Global(conflictingBase, Struct(NameField("Type", "unrelated_type")))
            .Global(profileTemplate, ProfileTemplate(profile))
            .Global(Hash("Z_TEST_NodeArray"), Array(Struct(
                // Target last would win in a last-write-wins resolver; the competing
                // inherited Type still makes the authored template set ambiguous.
                Flag(conflictingBase),
                Flag(profileTemplate),
                NameField("name", "table"),
                VectorField("pos", Vector3.Zero),
                VectorField("Angles", Vector3.Zero))))
            .Build();

        Assert.False(Ps2WorldzoneQbObjectResolver.TryResolveProfileInstances(
            [qb], profile, isNetworkArchive: false, out _));
    }

    [Fact]
    public void TryResolveProfileInstances_TwoTargetReferencesOnOneNode_AreRejected()
    {
        var profile = Hash("AC_Unit01");
        var templateA = Hash("ac_template_a");
        var templateB = Hash("ac_template_b");
        var qb = new SyntheticQbBuilder()
            .Global(templateA, ProfileTemplate(profile))
            .Global(templateB, ProfileTemplate(profile))
            .Global(Hash("Z_TEST_NodeArray"), Array(Struct(
                Flag(templateA),
                Flag(templateB),
                NameField("name", "ac"),
                VectorField("pos", Vector3.Zero),
                VectorField("Angles", Vector3.Zero))))
            .Build();

        Assert.False(Ps2WorldzoneQbObjectResolver.TryResolveProfileInstances(
            [qb], profile, isNetworkArchive: false, out _));
    }

    [Fact]
    public void CreateNodeArrayRotation_NonCommutingAngles_UsesRxThenRyThenRz()
    {
        var angles = new Vector3(0.37f, -0.61f, 1.04f);
        var expected = ExpectedRotation(angles);

        var actual = Ps2WorldzoneQbObjectResolver.CreateNodeArrayRotation(angles);

        AssertQuaternionEquivalent(expected, actual);

        var reverseOrderMatrix = Matrix4x4.CreateRotationZ(angles.Z) *
                                 Matrix4x4.CreateRotationY(angles.Y) *
                                 Matrix4x4.CreateRotationX(angles.X);
        var reverseOrder = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(reverseOrderMatrix));
        Assert.True(MathF.Abs(Quaternion.Dot(reverseOrder, actual)) < 0.999f);
    }

    private static QbFile BuildSingleProfileQb(uint profile, IReadOnlyList<string> flags)
    {
        var template = Hash("compressed_profile_template");
        var templateMembers = new List<IReadOnlyList<QbToken>>
        {
            NameField("Class", "gameobject"),
            NameField("Type", profile),
            NameField("profile", profile)
        };
        templateMembers.AddRange(flags.Select(Flag));

        return new SyntheticQbBuilder()
            .Global(template, Struct([.. templateMembers]))
            .Global(Hash("Z_TEST_NodeArray"), Array(ProfileNode(template, "test_node")))
            .Build();
    }

    private static List<QbToken> ProfileTemplate(uint profile)
    {
        return Struct(
            NameField("Class", "gameobject"),
            NameField("Type", profile),
            NameField("profile", profile),
            Flag("CreatedAtStart"),
            Flag("RenderToViewport"));
    }

    private static List<QbToken> ProfileNode(uint template, string name)
    {
        return Struct(
            Flag(template),
            NameField("name", name),
            VectorField("pos", Vector3.Zero),
            VectorField("Angles", Vector3.Zero));
    }

    private static Quaternion ExpectedRotation(Vector3 angles)
    {
        var matrix = Matrix4x4.CreateRotationX(angles.X) *
                     Matrix4x4.CreateRotationY(angles.Y) *
                     Matrix4x4.CreateRotationZ(angles.Z);
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(matrix));
    }

    private static void AssertQuaternionEquivalent(Quaternion expected, Quaternion actual)
    {
        Assert.InRange(MathF.Abs(Quaternion.Dot(expected, actual)), 0.99999f, 1.00001f);
    }

    private static List<QbToken> Struct(params IReadOnlyList<QbToken>[] members)
    {
        var tokens = new List<QbToken> { Token(QbTokenType.StartStruct) };
        foreach (var member in members)
            tokens.AddRange(member);
        tokens.Add(Token(QbTokenType.EndStruct));
        return tokens;
    }

    private static List<QbToken> Array(params IReadOnlyList<QbToken>[] values)
    {
        var tokens = new List<QbToken> { Token(QbTokenType.StartArray) };
        foreach (var value in values)
            tokens.AddRange(value);
        tokens.Add(Token(QbTokenType.EndArray));
        return tokens;
    }

    private static IReadOnlyList<QbToken> NameField(string key, string value)
    {
        return NameField(key, Hash(value));
    }

    private static IReadOnlyList<QbToken> NameField(string key, uint value)
    {
        return [Name(Hash(key)), Token(QbTokenType.Equals), Name(value)];
    }

    private static IReadOnlyList<QbToken> VectorField(string key, Vector3 value)
    {
        return
        [
            Name(Hash(key)),
            Token(QbTokenType.Equals),
            new QbToken
            {
                Type = QbTokenType.Vector,
                FloatX = value.X,
                FloatY = value.Y,
                FloatZ = value.Z
            }
        ];
    }

    private static IReadOnlyList<QbToken> Flag(string name)
    {
        return Flag(Hash(name));
    }

    private static IReadOnlyList<QbToken> Flag(uint checksum)
    {
        return [Name(checksum)];
    }

    private static QbToken Name(uint checksum)
    {
        return new QbToken { Type = QbTokenType.Name, NameChecksum = checksum };
    }

    private static QbToken Token(QbTokenType type)
    {
        return new QbToken { Type = type };
    }

    private static uint Hash(string value)
    {
        return QbKeyHash.HashLower(value);
    }

    private static (uint TypeHash, ArchiveEntry Entry) Owner(long offset, uint crc = 0x61B2A297)
    {
        return Typed(
            Ps2WorldzoneQbObjectResolver.ModelQbTypeHash,
            Entry("ac_unit01.qb.ps2", offset, crc, directory: "models/props/ac_unit01"));
    }

    private static (uint TypeHash, ArchiveEntry Entry) Mdl(long offset, uint crc = 0)
    {
        return Typed(
            Ps2WorldzoneDetection.WorldzoneMdlTypeHash,
            Entry("object.mdl", offset, crc));
    }

    private static (uint TypeHash, ArchiveEntry Entry) Collision(long offset, uint crc = 0)
    {
        return Typed(
            Ps2WorldzoneQbObjectResolver.CollisionTypeHash,
            Entry("object.mcol", offset, crc));
    }

    private static (uint TypeHash, ArchiveEntry Entry) Typed(uint typeHash, ArchiveEntry entry)
    {
        return (typeHash, entry);
    }

    private static ArchiveEntry Entry(
        string name,
        long offset,
        uint crc = 0,
        string directory = "zone")
    {
        return new ArchiveEntry
        {
            Name = name,
            Directory = directory,
            Offset = offset,
            Size = 16,
            Crc = crc
        };
    }

    private sealed class SyntheticQbBuilder
    {
        private readonly List<QbToken> _tokens = [];
        private readonly List<QbItem> _items = [];

        public SyntheticQbBuilder Global(uint checksum, IReadOnlyList<QbToken> value)
        {
            var start = _tokens.Count;
            _tokens.Add(Name(checksum));
            _tokens.Add(Token(QbTokenType.Equals));
            _tokens.AddRange(value);
            _tokens.Add(Token(QbTokenType.EndOfLine));
            _items.Add(new QbItem
            {
                Kind = QbItemKind.Global,
                NameChecksum = checksum,
                StartTokenIndex = start,
                EndTokenIndex = _tokens.Count
            });
            return this;
        }

        public QbFile Build()
        {
            return new QbFile
            {
                FileName = "synthetic.nqb.ps2",
                Tokens = _tokens,
                Items = _items
            };
        }
    }
}
