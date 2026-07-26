using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     A downward "what floor is under this point" query over a PSX level's
///     render geometry, replicating the engine's <c>Utils_GetGroundHeight</c>
///     (THPS2 proto decomp, address 0x80048F18): a vertical line-cast that
///     accepts only floor-facing surfaces and returns the floor height.
///
///     Spider-Man PS1 has NO separate collision mesh — <c>M3dColij</c> casts the
///     ray against the render model's own vertices/faces
///     (<c>ppModels[item.Model]</c>, M3DCOLIJ.cpp), so the <c>_g.psx</c> geometry
///     this converter already produces IS the collision surface.
///
///     All coordinates are NATIVE engine space, where <b>+Y points DOWN</b>
///     (glTF export later maps native <c>(x,y,z)</c> to <c>(x,-y,-z)</c>). A floor
///     therefore faces "up" = its triangle normal's Y component is negative, and
///     a lower floor has a LARGER native Y than a higher one.
/// </summary>
internal sealed class PsxTerrainHeightField
{
    // A floor's up-normal must dominate the other axes. cos(45°) ≈ 0.707; the
    // engine's own gate (lineInfo normal.y < -0xBFF over a normalized short) is
    // ~0.75, so this is deliberately a touch more permissive to catch gently
    // sloped ground while still rejecting walls (|ny| small) and ceilings (ny>0).
    private const float FloorNormalYMax = -0.5f;

    private readonly IReadOnlyList<FloorTriangle> _floors;

    private PsxTerrainHeightField(IReadOnlyList<FloorTriangle> floors)
    {
        _floors = floors;
    }

    /// <summary>True when the level yielded no usable floor geometry.</summary>
    public bool IsEmpty => _floors.Count == 0;

    /// <summary>
    ///     Builds the field from native-space triangles. Only floor-facing
    ///     triangles are retained; walls and ceilings are ignored, as are
    ///     degenerate triangles with no XZ footprint (a vertical wall projects to
    ///     a line and can never be "under" a point).
    /// </summary>
    /// <summary>
    ///     Builds the field from a parsed PSX level (<c>_g.psx</c>) mesh. Each
    ///     object places its mesh at its stored offset (native world space); every
    ///     face contributes triangle(s) in the geometry writer's emitted winding
    ///     <c>(0,2,1)</c> (+ <c>(1,2,3)</c> for quads). Empirically the raw slot
    ///     order <c>(0,1,2)</c> gives PSX floors an "up" normal of +Y; this
    ///     reversed order flips it to native-up = <b>−Y</b>, matching the engine's
    ///     floor-facing normal gate and the <see cref="Build" /> overload, so the
    ///     floor-facing filter keeps floors and rejects ceilings. This is the same
    ///     collision surface the engine ray-casts (M3dColij runs against the
    ///     render model itself).
    /// </summary>
    public static PsxTerrainHeightField BuildFromLevel(PsxMeshFile level)
    {
        var floors = new List<FloorTriangle>();
        foreach (var obj in level.Objects)
        {
            if (obj.MeshIndex >= level.Meshes.Count)
                continue;

            var offset = PsxMeshSemantics.GetObjectOffset(level, obj);
            var mesh = level.Meshes[obj.MeshIndex];
            foreach (var face in mesh.Faces)
            {
                AddLevelTriangle(floors, mesh, offset, face.Index0, face.Index2, face.Index1);
                if (face.IsQuad)
                    AddLevelTriangle(floors, mesh, offset, face.Index1, face.Index2, face.Index3);
            }
        }

        return new PsxTerrainHeightField(floors);
    }

    private static void AddLevelTriangle(
        List<FloorTriangle> floors, PsxMesh mesh, Vector3 offset, uint i0, uint i1, uint i2)
    {
        if (i0 >= mesh.Vertices.Count || i1 >= mesh.Vertices.Count || i2 >= mesh.Vertices.Count)
            return;

        var a = LocalVertex(mesh, i0) + offset;
        var b = LocalVertex(mesh, i1) + offset;
        var c = LocalVertex(mesh, i2) + offset;
        var normal = Vector3.Cross(b - a, c - a);
        var length = normal.Length();
        if (length <= float.Epsilon)
            return;

        // Native +Y is DOWN, so a floor's up-normal has NEGATIVE Y. Keep ONLY
        // floor-facing surfaces (like the Build overload and the engine's
        // normal.y gate): walls have |ny| near 0 and ceilings have ny > 0. This
        // matters because TryGetRestingFloor picks the HIGHEST covering surface,
        // so an accepted ceiling/overhang above a pickup would be chosen wrongly.
        if (normal.Y / length > FloorNormalYMax)
            return;

        floors.Add(new FloorTriangle(a, b, c));
    }

    private static Vector3 LocalVertex(PsxMesh mesh, uint index)
    {
        var v = mesh.Vertices[(int)index];
        return new Vector3(v.X, v.Y, v.Z);
    }

    public static PsxTerrainHeightField Build(IEnumerable<(Vector3 A, Vector3 B, Vector3 C)> triangles)
    {
        var floors = new List<FloorTriangle>();
        foreach (var (a, b, c) in triangles)
        {
            var normal = Vector3.Cross(b - a, c - a);
            var length = normal.Length();
            if (length <= float.Epsilon)
                continue;

            var ny = normal.Y / length;
            if (ny > FloorNormalYMax)
                continue; // wall or ceiling (native up-normal is negative)

            floors.Add(new FloorTriangle(a, b, c));
        }

        return new PsxTerrainHeightField(floors);
    }

    /// <summary>
    ///     Returns the native Y of the floor beneath <paramref name="x" />,
    ///     <paramref name="z" /> that sits nearest <paramref name="nearNativeY" />
    ///     (the object's own resting level — its base or origin). Mirrors the
    ///     engine picking the ground the object stands on rather than the level's
    ///     lowest floor: for a multi-storey level this keeps an object on its own
    ///     deck. Returns false when no floor covers the point.
    /// </summary>
    public bool TryGetGroundY(float x, float z, float nearNativeY, out float groundY)
    {
        groundY = 0f;
        var found = false;
        var bestDelta = float.PositiveInfinity;
        foreach (var floor in _floors)
        {
            if (!floor.TryHeightAt(x, z, out var y))
                continue;

            var delta = MathF.Abs(y - nearNativeY);
            if (delta < bestDelta)
            {
                bestDelta = delta;
                groundY = y;
                found = true;
            }
        }

        return found;
    }

    /// <summary>
    ///     The floor an object rests on: the highest near-horizontal surface at or
    ///     below <paramref name="baseNativeY" /> (native +Y down, so "below" = a
    ///     LARGER Y), searching down at most <paramref name="maxDrop" /> and
    ///     tolerating <paramref name="overhang" /> of surface slightly above the
    ///     base (a marginally-sunk object still finds the floor just over it).
    ///     Mirrors the engine's downward line-cast (<c>Utils_GetGroundHeight</c>
    ///     with <c>above≈0</c>); returns false when nothing qualifies (a hole, or
    ///     the object floats farther than <paramref name="maxDrop" /> above any
    ///     floor — left where it was rather than yanked down).
    /// </summary>
    public bool TryGetRestingFloor(
        float x, float z, float baseNativeY, float overhang, float maxDrop, out float floorY)
    {
        floorY = 0f;
        var found = false;
        var best = float.PositiveInfinity;
        foreach (var floor in _floors)
        {
            if (!floor.TryHeightAt(x, z, out var y))
                continue;
            if (y < baseNativeY - overhang || y > baseNativeY + maxDrop)
                continue;
            if (y < best)
            {
                best = y;
                found = true;
            }
        }

        if (found)
            floorY = best;
        return found;
    }

    private readonly struct FloorTriangle
    {
        private readonly Vector3 _a;
        private readonly Vector3 _b;
        private readonly Vector3 _c;
        private readonly float _invDenominator;
        private readonly bool _valid;

        public FloorTriangle(Vector3 a, Vector3 b, Vector3 c)
        {
            _a = a;
            _b = b;
            _c = c;
            var denominator = (b.Z - c.Z) * (a.X - c.X) + (c.X - b.X) * (a.Z - c.Z);
            _valid = MathF.Abs(denominator) > 1e-6f;
            _invDenominator = _valid ? 1f / denominator : 0f;
        }

        /// <summary>
        ///     Interpolates the triangle's Y at (x,z) via barycentric weights in
        ///     the XZ plane, or false when the point lies outside the footprint.
        /// </summary>
        public bool TryHeightAt(float x, float z, out float y)
        {
            y = 0f;
            if (!_valid)
                return false;

            var u = ((_b.Z - _c.Z) * (x - _c.X) + (_c.X - _b.X) * (z - _c.Z)) * _invDenominator;
            var v = ((_c.Z - _a.Z) * (x - _c.X) + (_a.X - _c.X) * (z - _c.Z)) * _invDenominator;
            var w = 1f - u - v;

            const float edge = -1e-4f;
            if (u < edge || v < edge || w < edge)
                return false;

            y = u * _a.Y + v * _b.Y + w * _c.Y;
            return true;
        }
    }
}
