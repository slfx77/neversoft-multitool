using System.Numerics;
using System.Text;
using System.Text.Json;
using NeversoftMultitool.Core.Formats.Collision;
using NeversoftMultitool.Core.Formats.Mesh;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Ddm;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Scene;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using NeversoftMultitool.Core.Formats.Mesh.RenderWare;
using NeversoftMultitool.Core.Formats.XbxScene;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

using ParsedPs2Scene = NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Scene.Ps2Scene;
using ParsedXbxScene = NeversoftMultitool.Core.Formats.XbxScene.XbxScene;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

public sealed class LegacyGltfParityTests
{
    private const uint MaterialChecksum = 0x11223344;
    private const uint TextureChecksum = 0x55667788;
    private const string TextureName = "unit_texture";

    [Fact]
    public void Collision_GenericGlb_MatchesLegacyWriter()
    {
        using var temp = new TempDirectory();
        var scene = CreateCollisionScene();

        var oldStats = ExportLegacy("collision", temp, path => ColGltfWriter.Write(scene, path));
        var document = new ModelDocument { Name = "collision", SourceKind = ModelSourceKind.Collision };
        ModelDocumentGeometryAdapter.PopulateCollision(document, scene);
        var genericStats = ExportGeneric("collision", temp, document);

        AssertStructuralParity("collision", oldStats, genericStats);
    }

    [Fact]
    public void Ddm_GenericGlb_MatchesLegacyWriter()
    {
        using var temp = new TempDirectory();
        var ddm = CreateDdmFile();

        var oldStats = ExportLegacy(
            "ddm",
            temp,
            path => GltfWriter.WriteDdm(ddm, path));
        var document = new ModelDocument { Name = "ddm", SourceKind = ModelSourceKind.Ddm };
        SeedDdmMaterials(document, ddm);
        ModelDocumentGeometryAdapter.PopulateDdm(document, ddm, ddxTextures: null);
        var genericStats = ExportGeneric("ddm", temp, document);

        AssertStructuralParity("ddm", oldStats, genericStats);
    }

    [Fact]
    public void Psx_GenericGlb_MatchesLegacyWriter()
    {
        using var temp = new TempDirectory();
        var psxFile = CreatePsxMeshFile();

        var oldStats = ExportLegacy(
            "psx",
            temp,
            path => PsxGltfWriter.Write(psxFile, path, ResolveTextureByChecksum));
        var document = new ModelDocument { Name = "psx", SourceKind = ModelSourceKind.Psx };
        ModelDocumentGeometryAdapter.PopulatePsx(document, psxFile, ResolveTextureByChecksum);
        var genericStats = ExportGeneric("psx", temp, document);

        AssertStructuralParity("psx", oldStats, genericStats);
    }

    [Fact]
    public void Ps2Scene_GenericGlb_MatchesLegacyWriter()
    {
        using var temp = new TempDirectory();
        var scene = CreatePs2Scene();

        var oldStats = ExportLegacy(
            "ps2_scene",
            temp,
            path => Ps2SceneGltfWriter.Write(scene, path, ResolveTextureByChecksum));
        var document = new ModelDocument { Name = "ps2_scene", SourceKind = ModelSourceKind.Ps2Scene };
        SeedPs2SceneMaterials(document, scene);
        ModelDocumentGeometryAdapter.PopulatePs2Scene(document, scene, ResolveTextureByChecksum);
        var genericStats = ExportGeneric("ps2_scene", temp, document);

        AssertStructuralParity("ps2_scene", oldStats, genericStats);
    }

    [Fact]
    public void Ps2Geom_GenericGlb_MatchesLegacyWriter()
    {
        using var temp = new TempDirectory();
        var scene = CreatePs2GeomScene();

        var oldStats = ExportLegacy(
            "ps2_geom",
            temp,
            path => Ps2GeomGltfWriter.Write(scene, path, ResolveTextureByChecksum));
        var document = new ModelDocument { Name = "ps2_geom", SourceKind = ModelSourceKind.Ps2Geom };
        ModelDocumentGeometryAdapter.PopulatePs2Geom(document, scene, ResolveTextureByChecksum, tex0Resolver: null);
        var genericStats = ExportGeneric("ps2_geom", temp, document);

        AssertStructuralParity("ps2_geom", oldStats, genericStats);
    }

    [Fact]
    public void XbxScene_GenericGlb_MatchesLegacyWriter()
    {
        using var temp = new TempDirectory();
        var scene = CreateXbxScene();

        var oldStats = ExportLegacy(
            "xbx_scene",
            temp,
            path => XbxSceneGltfWriter.Write(scene, path, ResolveTextureByChecksum));
        var document = new ModelDocument { Name = "xbx_scene", SourceKind = ModelSourceKind.XbxScene };
        SeedXbxMaterials(document, scene);
        ModelDocumentGeometryAdapter.PopulateXbxScene(document, scene, ResolveTextureByChecksum);
        var genericStats = ExportGeneric("xbx_scene", temp, document);

        AssertStructuralParity("xbx_scene", oldStats, genericStats);
    }

    [Fact]
    public void RwDff_GenericGlb_MatchesLegacyWriter()
    {
        using var temp = new TempDirectory();
        var clump = CreateRwDffClump();

        var oldStats = ExportLegacy(
            "rw_dff",
            temp,
            path => RwDffGltfWriter.Write(clump, path, ResolveTextureByName));
        var document = new ModelDocument { Name = "rw_dff", SourceKind = ModelSourceKind.RenderWareDff };
        ModelDocumentGeometryAdapter.PopulateRwDff(document, clump, ResolveTextureByName);
        var genericStats = ExportGeneric("rw_dff", temp, document);

        AssertStructuralParity("rw_dff", oldStats, genericStats);
    }

    [Fact]
    public void RwBsp_GenericGlb_MatchesLegacyWriter()
    {
        using var temp = new TempDirectory();
        var world = CreateRwBspWorld();

        var oldStats = ExportLegacy(
            "rw_bsp",
            temp,
            path => RwBspGltfWriter.Write(world, path, ResolveTextureByName));
        var document = new ModelDocument { Name = "rw_bsp", SourceKind = ModelSourceKind.RenderWareBsp };
        ModelDocumentGeometryAdapter.PopulateRwBsp(document, world, ResolveTextureByName);
        var genericStats = ExportGeneric("rw_bsp", temp, document);

        AssertStructuralParity("rw_bsp", oldStats, genericStats);
    }

    private static void SeedPs2SceneMaterials(ModelDocument document, ParsedPs2Scene scene)
    {
        foreach (var material in scene.Materials)
        {
            document.Materials.Add(new RenderMaterial
            {
                Name = $"mat_{material.Checksum:X8}"
            });
        }
    }

    private static void SeedDdmMaterials(ModelDocument document, DdmFile ddm)
    {
        foreach (var material in ddm.Objects.SelectMany(static obj => obj.Materials))
        {
            document.Materials.Add(new RenderMaterial
            {
                Name = material.Name
            });
        }
    }

    private static void SeedXbxMaterials(ModelDocument document, ParsedXbxScene scene)
    {
        foreach (var material in scene.Materials)
        {
            document.Materials.Add(new RenderMaterial
            {
                Name = $"mat_{material.Checksum:X8}"
            });
        }
    }

    private static GlbStats ExportLegacy(string stem, TempDirectory temp, Func<string, int> writer)
    {
        var outputPath = Path.Combine(temp.Path, stem + "_legacy.glb");
        var triangles = writer(outputPath);
        Assert.True(File.Exists(outputPath), $"{stem}: legacy writer did not create a GLB.");

        var stats = ReadGlbStats(outputPath);
        Assert.Equal(triangles, stats.Triangles);
        return stats;
    }

    private static GlbStats ExportGeneric(string stem, TempDirectory temp, ModelDocument document)
    {
        var result = ModelExportService.Export(
            document,
            new MeshExportRequest
            {
                OutputDirectory = temp.Path,
                OutputStem = stem + "_generic",
                Format = MeshOutputFormat.Glb
            });

        var outputPath = Assert.Single(result.OutputPaths);
        Assert.True(File.Exists(outputPath), $"{stem}: generic exporter did not create a GLB.");

        var stats = ReadGlbStats(outputPath);
        Assert.Equal(result.Triangles, stats.Triangles);
        return stats;
    }

    private static void AssertStructuralParity(string source, GlbStats legacy, GlbStats generic)
    {
        Assert.Equal(legacy.Triangles, generic.Triangles);
        Assert.Equal(legacy.Meshes, generic.Meshes);
        Assert.Equal(legacy.Materials, generic.Materials);
        Assert.Equal(legacy.Textures, generic.Textures);
        Assert.Equal(legacy.Images, generic.Images);
        Assert.Equal(legacy.Nodes, generic.Nodes);
        Assert.Equal(legacy.HasBounds, generic.HasBounds);

        if (!legacy.HasBounds || !generic.HasBounds)
            return;

        Assert.True(
            Vector3.Distance(legacy.MinBounds, generic.MinBounds) <= 0.0001f,
            $"{source}: min bounds differ. Legacy={legacy.MinBounds}, Generic={generic.MinBounds}");
        Assert.True(
            Vector3.Distance(legacy.MaxBounds, generic.MaxBounds) <= 0.0001f,
            $"{source}: max bounds differ. Legacy={legacy.MaxBounds}, Generic={generic.MaxBounds}");
    }

    private static GlbStats ReadGlbStats(string glbPath)
    {
        var (jsonBytes, _) = ReadGlbChunks(glbPath);
        var json = Encoding.UTF8.GetString(jsonBytes).TrimEnd('\0', ' ', '\r', '\n', '\t');
        using var gltf = JsonDocument.Parse(json);
        var root = gltf.RootElement;

        return new GlbStats(
            Triangles: CountTriangles(root),
            Meshes: ReadArrayLength(root, "meshes"),
            Materials: ReadArrayLength(root, "materials"),
            Textures: ReadArrayLength(root, "textures"),
            Images: ReadArrayLength(root, "images"),
            Nodes: ReadArrayLength(root, "nodes"),
            HasBounds: TryReadPositionBounds(root, out var min, out var max),
            MinBounds: min,
            MaxBounds: max);
    }

    private static int CountTriangles(JsonElement root)
    {
        if (!root.TryGetProperty("meshes", out var meshes) ||
            !root.TryGetProperty("accessors", out var accessors))
        {
            return 0;
        }

        var triangles = 0;
        foreach (var mesh in meshes.EnumerateArray())
        {
            if (!mesh.TryGetProperty("primitives", out var primitives))
                continue;

            foreach (var primitive in primitives.EnumerateArray())
            {
                var mode = primitive.TryGetProperty("mode", out var modeElement)
                    ? modeElement.GetInt32()
                    : 4;
                if (mode != 4)
                    continue;

                if (primitive.TryGetProperty("indices", out var indices))
                {
                    triangles += ReadAccessorCount(accessors, indices.GetInt32()) / 3;
                    continue;
                }

                if (primitive.TryGetProperty("attributes", out var attributes) &&
                    attributes.TryGetProperty("POSITION", out var position))
                {
                    triangles += ReadAccessorCount(accessors, position.GetInt32()) / 3;
                }
            }
        }

        return triangles;
    }

    private static int ReadAccessorCount(JsonElement accessors, int accessorIndex)
    {
        if (accessorIndex < 0 || accessorIndex >= accessors.GetArrayLength())
            return 0;

        var accessor = accessors[accessorIndex];
        return accessor.TryGetProperty("count", out var count) ? count.GetInt32() : 0;
    }

    private static bool TryReadPositionBounds(JsonElement root, out Vector3 min, out Vector3 max)
    {
        min = new Vector3(float.PositiveInfinity);
        max = new Vector3(float.NegativeInfinity);

        if (!root.TryGetProperty("meshes", out var meshes) ||
            !root.TryGetProperty("accessors", out var accessors))
        {
            return false;
        }

        var found = false;
        foreach (var mesh in meshes.EnumerateArray())
        {
            if (!mesh.TryGetProperty("primitives", out var primitives))
                continue;

            foreach (var primitive in primitives.EnumerateArray())
            {
                if (!primitive.TryGetProperty("attributes", out var attributes) ||
                    !attributes.TryGetProperty("POSITION", out var position))
                {
                    continue;
                }

                var accessorIndex = position.GetInt32();
                if (accessorIndex < 0 || accessorIndex >= accessors.GetArrayLength())
                    continue;

                var accessor = accessors[accessorIndex];
                if (!accessor.TryGetProperty("min", out var accessorMin) ||
                    !accessor.TryGetProperty("max", out var accessorMax) ||
                    accessorMin.GetArrayLength() < 3 ||
                    accessorMax.GetArrayLength() < 3)
                {
                    continue;
                }

                min = Vector3.Min(min, ReadVector3(accessorMin));
                max = Vector3.Max(max, ReadVector3(accessorMax));
                found = true;
            }
        }

        if (!found)
        {
            min = Vector3.Zero;
            max = Vector3.Zero;
        }

        return found;
    }

    private static Vector3 ReadVector3(JsonElement value) =>
        new(
            (float)value[0].GetDouble(),
            (float)value[1].GetDouble(),
            (float)value[2].GetDouble());

    private static int ReadArrayLength(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Array
            ? property.GetArrayLength()
            : 0;

    private static (byte[] Json, byte[] Bin) ReadGlbChunks(string glbPath)
    {
        using var stream = File.OpenRead(glbPath);
        using var reader = new BinaryReader(stream);
        Assert.Equal(0x46546C67u, reader.ReadUInt32());
        Assert.Equal(2u, reader.ReadUInt32());
        _ = reader.ReadUInt32();

        var jsonLength = reader.ReadUInt32();
        Assert.Equal(0x4E4F534Au, reader.ReadUInt32());
        var jsonBytes = reader.ReadBytes(checked((int)jsonLength));

        var binLength = reader.ReadUInt32();
        Assert.Equal(0x004E4942u, reader.ReadUInt32());
        var binBytes = reader.ReadBytes(checked((int)binLength));
        return (jsonBytes, binBytes);
    }

    private static ColScene CreateCollisionScene()
    {
        var vertices = QuadPositions();
        return new ColScene
        {
            Version = 10,
            Objects =
            [
                new ColObject
                {
                    Checksum = 0xAABBCCDD,
                    Flags = 0,
                    BBoxMin = new Vector3(0f, 0f, 0f),
                    BBoxMax = new Vector3(1f, 1f, 0f),
                    Vertices = vertices,
                    Faces =
                    [
                        new ColFace(0, 0, 0, 1, 2),
                        new ColFace(0, 0, 1, 3, 2)
                    ],
                    Intensities = [255, 192, 128, 64]
                }
            ]
        };
    }

    private static DdmFile CreateDdmFile()
    {
        return new DdmFile
        {
            Objects =
            [
                new DdmObject
                {
                    Name = "ddm_quad",
                    Checksum = 0xDEADBEAF,
                    BBoxCenterX = 0.5f,
                    BBoxCenterY = 0.5f,
                    BBoxCenterZ = 0f,
                    BBoxExtentX = 2f,
                    BBoxExtentY = 2f,
                    BBoxExtentZ = 2f,
                    Materials =
                    [
                        new DdmMaterial
                        {
                            Name = "ddm_material",
                            TextureName = "No_Texture_Map",
                            DiffuseR = 255,
                            DiffuseG = 255,
                            DiffuseB = 255,
                            DiffuseA = 255
                        }
                    ],
                    Vertices = QuadDdmVertices(),
                    Indices = [0, 1, 2, 3],
                    Splits = [new DdmSplit(0, 0, 4)]
                }
            ]
        };
    }

    private static PsxMeshFile CreatePsxMeshFile()
    {
        return new PsxMeshFile
        {
            Version = 0x04,
            Objects =
            [
                new PsxMeshObject
                {
                    MeshIndex = 0
                }
            ],
            Meshes =
            [
                new PsxMesh
                {
                    Vertices = QuadPsxVertices(),
                    Normals = [new PsxNormal { X = 0f, Y = 0f, Z = 1f }],
                    Faces =
                    [
                        new PsxFace
                        {
                            IsQuad = true,
                            IsTextured = true,
                            TextureHash = TextureChecksum,
                            Index0 = 0,
                            Index1 = 1,
                            Index2 = 2,
                            Index3 = 3,
                            NormalIndex = 0,
                            R = 128,
                            G = 128,
                            B = 128,
                            TextureCoordinates =
                            [
                                new PsxTextureCoordinate(0, 0),
                                new PsxTextureCoordinate(1, 0),
                                new PsxTextureCoordinate(0, 1),
                                new PsxTextureCoordinate(1, 1)
                            ]
                        }
                    ],
                    LodNextMeshIndex = ushort.MaxValue
                }
            ],
            MeshNameHashes = [0],
            TextureHashes = [TextureChecksum],
            ScaleDivisor = 1f,
            TranslationDivisor = 1f
        };
    }

    private static ParsedPs2Scene CreatePs2Scene()
    {
        return new ParsedPs2Scene
        {
            MaterialVersion = 6,
            MeshVersion = 6,
            VertexVersion = 1,
            Materials =
            [
                new Ps2Material
                {
                    Checksum = MaterialChecksum,
                    TextureChecksum = TextureChecksum,
                    ClampUMode = 1,
                    ClampVMode = 0,
                    RegAlpha = 0x0A
                }
            ],
            MeshGroups =
            [
                new Ps2MeshGroup
                {
                    Checksum = 0x01020304,
                    Meshes =
                    [
                        new Ps2Mesh
                        {
                            Checksum = 0x02030405,
                            MaterialChecksum = MaterialChecksum,
                            BoundingSphere = new Vector4(0.5f, 0.5f, 0f, 1f),
                            Vertices = QuadPs2StripVertices()
                        }
                    ]
                }
            ]
        };
    }

    private static Ps2GeomScene CreatePs2GeomScene()
    {
        return new Ps2GeomScene
        {
            Leaves =
            [
                new Ps2GeomLeaf
                {
                    Checksum = 0x33445566,
                    TextureChecksum = TextureChecksum,
                    BoundingSphere = new Vector4(0.5f, 0.5f, 0f, 1f),
                    Vertices = QuadPs2StripVertices(),
                    DmaClamp1 = 1,
                    DmaAlpha1 = 0x0A,
                    DmaTest1 = 0
                }
            ]
        };
    }

    private static ParsedXbxScene CreateXbxScene()
    {
        return new ParsedXbxScene
        {
            Materials =
            [
                new XbxMaterial
                {
                    Checksum = MaterialChecksum,
                    NumPasses = 1,
                    Passes =
                    [
                        new XbxPass
                        {
                            TextureChecksum = TextureChecksum,
                            UAddressing = 3,
                            VAddressing = 1
                        }
                    ]
                }
            ],
            Sectors =
            [
                new XbxSector
                {
                    Checksum = 0x8899AABB,
                    BboxMin = new Vector3(0f, 0f, 0f),
                    BboxMax = new Vector3(1f, 1f, 0f),
                    BsphereCenter = new Vector3(0.5f, 0.5f, 0f),
                    BsphereRadius = 1f,
                    Meshes =
                    [
                        new XbxMesh
                        {
                            MaterialChecksum = MaterialChecksum,
                            Vertices = QuadXbxVertices(),
                            FaceIndices = [0, 1, 2, 1, 3, 2],
                            IsPreTriangulated = true,
                            BboxMin = new Vector3(0f, 0f, 0f),
                            BboxMax = new Vector3(1f, 1f, 0f),
                            BsphereCenter = new Vector3(0.5f, 0.5f, 0f),
                            BsphereRadius = 1f
                        }
                    ]
                }
            ],
            Links = []
        };
    }

    private static RwDffClump CreateRwDffClump()
    {
        return new RwDffClump
        {
            Frames =
            [
                new RwFrame
                {
                    LocalTransform = Matrix4x4.Identity,
                    ParentIndex = -1,
                    Flags = 0
                }
            ],
            Geometries = [CreateRwGeometry()],
            Atomics =
            [
                new RwAtomic
                {
                    FrameIndex = 0,
                    GeometryIndex = 0,
                    Flags = 0
                }
            ]
        };
    }

    private static RwBspWorld CreateRwBspWorld()
    {
        return new RwBspWorld
        {
            FormatFlags = 0,
            TotalTriangles = 2,
            TotalVertices = 4,
            Materials = [CreateRwMaterial()],
            Sections =
            [
                new RwBspSection
                {
                    MatListWindowBase = 0,
                    Vertices = QuadPositions(),
                    Normals = QuadNormals(),
                    Colors = QuadRwColors(),
                    UVs = QuadUvs(),
                    Triangles = QuadRwTriangles()
                }
            ]
        };
    }

    private static RwGeometry CreateRwGeometry()
    {
        return new RwGeometry
        {
            Flags = 0,
            Vertices = QuadPositions(),
            Normals = QuadNormals(),
            UVs = QuadUvs(),
            Colors = QuadRwColors(),
            Triangles = QuadRwTriangles(),
            Materials = [CreateRwMaterial()],
            BoundingSphere = new Vector4(0.5f, 0.5f, 0f, 1f)
        };
    }

    private static RwMaterial CreateRwMaterial()
    {
        return new RwMaterial
        {
            R = 255,
            G = 255,
            B = 255,
            A = 255,
            TextureName = TextureName,
            MaskName = null,
            Ambient = 1f,
            Specular = 0f,
            Diffuse = 1f
        };
    }

    private static Ps2Vertex[] QuadPs2StripVertices()
    {
        var positions = QuadPositions();
        var uvs = QuadUvs();
        return
        [
            MakePs2Vertex(positions[0], uvs[0]),
            MakePs2Vertex(positions[1], uvs[1]),
            MakePs2Vertex(positions[2], uvs[2]),
            MakePs2Vertex(positions[3], uvs[3])
        ];
    }

    private static Ps2Vertex MakePs2Vertex(Vector3 position, Vector2 uv) =>
        new(position, Vector3.UnitZ, 128, 128, 128, 128, uv.X, uv.Y, true, true, true, false);

    private static XbxVertex[] QuadXbxVertices()
    {
        var positions = QuadPositions();
        var uvs = QuadUvs();
        return
        [
            MakeXbxVertex(positions[0], uvs[0]),
            MakeXbxVertex(positions[1], uvs[1]),
            MakeXbxVertex(positions[2], uvs[2]),
            MakeXbxVertex(positions[3], uvs[3])
        ];
    }

    private static XbxVertex MakeXbxVertex(Vector3 position, Vector2 uv) =>
        new()
        {
            Position = position,
            Normal = Vector3.UnitZ,
            Color = Vector4.One,
            TexCoord = uv,
            HasNormal = true,
            HasColor = true
        };

    private static Vector3[] QuadPositions() =>
    [
        new Vector3(0f, 0f, 0f),
        new Vector3(1f, 0f, 0f),
        new Vector3(0f, 1f, 0f),
        new Vector3(1f, 1f, 0f)
    ];

    private static Vector3[] QuadNormals() =>
    [
        Vector3.UnitZ,
        Vector3.UnitZ,
        Vector3.UnitZ,
        Vector3.UnitZ
    ];

    private static Vector2[] QuadUvs() =>
    [
        new Vector2(0f, 0f),
        new Vector2(1f, 0f),
        new Vector2(0f, 1f),
        new Vector2(1f, 1f)
    ];

    private static RwVertexColor[] QuadRwColors() =>
    [
        new RwVertexColor(255, 255, 255, 255),
        new RwVertexColor(255, 255, 255, 255),
        new RwVertexColor(255, 255, 255, 255),
        new RwVertexColor(255, 255, 255, 255)
    ];

    private static RwTriangle[] QuadRwTriangles() =>
    [
        new RwTriangle(0, 1, 2, 0),
        new RwTriangle(1, 3, 2, 0)
    ];

    private static List<DdmVertex> QuadDdmVertices()
    {
        var positions = QuadPositions();
        var uvs = QuadUvs();
        return
        [
            MakeDdmVertex(positions[0], uvs[0]),
            MakeDdmVertex(positions[1], uvs[1]),
            MakeDdmVertex(positions[2], uvs[2]),
            MakeDdmVertex(positions[3], uvs[3])
        ];
    }

    private static DdmVertex MakeDdmVertex(Vector3 position, Vector2 uv) =>
        new(position.X, position.Y, position.Z, 0f, 0f, 1f, 255, 255, 255, 255, uv.X, uv.Y);

    private static List<PsxVertex> QuadPsxVertices()
    {
        var positions = QuadPositions();
        return
        [
            new PsxVertex { X = positions[0].X, Y = positions[0].Y, Z = positions[0].Z },
            new PsxVertex { X = positions[1].X, Y = positions[1].Y, Z = positions[1].Z },
            new PsxVertex { X = positions[2].X, Y = positions[2].Y, Z = positions[2].Z },
            new PsxVertex { X = positions[3].X, Y = positions[3].Y, Z = positions[3].Z }
        ];
    }

    private static byte[]? ResolveTextureByChecksum(uint checksum) =>
        checksum == TextureChecksum ? CreateTexturePngBytes() : null;

    private static byte[]? ResolveTextureByName(string name) =>
        string.Equals(Path.GetFileNameWithoutExtension(name), TextureName, StringComparison.OrdinalIgnoreCase)
            ? CreateTexturePngBytes()
            : null;

    private static byte[] CreateTexturePngBytes()
    {
        using var image = new Image<Rgba32>(2, 1);
        image[0, 0] = new Rgba32(16, 32, 64, 255);
        image[1, 0] = new Rgba32(24, 32, 64, 255);
        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }

    private sealed record GlbStats(
        int Triangles,
        int Meshes,
        int Materials,
        int Textures,
        int Images,
        int Nodes,
        bool HasBounds,
        Vector3 MinBounds,
        Vector3 MaxBounds);

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "NsMtParityTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                    Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Test cleanup is best-effort.
            }
        }
    }
}
