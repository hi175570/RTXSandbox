using System.Numerics;
using RTXSandbox.World;

namespace RTXSandbox.Rendering;

/// <summary>
/// One vertex of the shared deformable-voxel render skin.  The physics grid keeps
/// voxel-sized collision and edit records; this skin only replaces the old visual
/// representation that drew one disconnected sphere for every source cell.
/// </summary>
public readonly record struct SoftBodySurfaceVertex(
    Vector3 Position,
    Vector3 Normal,
    Vector2 Uv,
    ushort MaterialId,
    int FaceIndex,
    int CellX,
    int CellY,
    int CellZ);

/// <summary>
/// Square beam fallback used by the compute path tracer. Raster and DXR consume the
/// subdivided surface; the compute renderer traces connected oriented boxes so the
/// soft voxel silhouette stays block-shaped rather than becoming a capsule.
/// </summary>
public readonly record struct SoftBodyBeamSegment(
    Vector3 Start,
    Vector3 End,
    float HalfWidth,
    ushort MaterialId,
    int CellX,
    int CellY,
    int CellZ);

public static class SoftBodySurfaceBuilder
{
    private static readonly (int X, int Y, int Z, int Face)[] NeighborFaces =
    [
        (0, 1, 0, 0),
        (-1, 0, 0, 1),
        (1, 0, 0, 2),
        (0, 0, -1, 3),
        (0, 0, 1, 4),
        (0, -1, 0, 5),
    ];

    public static SoftBodySurfaceVertex[] Build(IReadOnlyList<PhysicsGridSmoothSphere> cells)
    {
        if (cells.Count == 0) return Array.Empty<SoftBodySurfaceVertex>();
        if (cells.Count == 1 && TryBuildTrianglePrism(cells[0], out SoftBodySurfaceVertex[]? prism))
            return prism;

        Dictionary<(int X, int Y, int Z), PhysicsGridSmoothSphere> cellMap = cells
            .GroupBy(cell => (cell.CellX, cell.CellY, cell.CellZ))
            .ToDictionary(group => group.Key, group => group.First());

        // Cell coordinates describe the undeformed lattice.  Smooth-sphere centres
        // describe the current solver state in grid-local space.  Their common offset
        // lets shared lattice corners follow all adjoining cells without opening gaps.
        Vector3 latticeOffset = Vector3.Zero;
        foreach (PhysicsGridSmoothSphere cell in cellMap.Values)
            latticeOffset += cell.Center - new Vector3(cell.CellX + 0.5f, cell.CellY + 0.5f, cell.CellZ + 0.5f);
        latticeOffset /= cellMap.Count;

        var positions = new List<Vector3>();
        // Material coordinates belong to the undeformed lattice, not the current
        // world-projected surface.  Keeping a parallel rest-position mesh makes the
        // texture stretch and shear with rubber/gel instead of repainting a fresh,
        // perfectly square tile on every deformed frame.
        var restPositions = new List<Vector3>();
        var vertexByCorner = new Dictionary<(int X, int Y, int Z), int>();
        var faces = new List<QuadFace>();

        int Corner(int x, int y, int z)
        {
            if (vertexByCorner.TryGetValue((x, y, z), out int existing)) return existing;

            Vector3 explicitPosition = Vector3.Zero;
            int explicitContributors = 0;
            Vector3 displacement = Vector3.Zero;
            int contributors = 0;
            for (int ox = 0; ox <= 1; ox++)
            for (int oy = 0; oy <= 1; oy++)
            for (int oz = 0; oz <= 1; oz++)
            {
                if (!cellMap.TryGetValue((x - ox, y - oy, z - oz), out PhysicsGridSmoothSphere adjoining))
                    continue;
                if (adjoining.HasExplicitCorners)
                {
                    explicitPosition += adjoining.GetCorner(ox, oy, oz);
                    explicitContributors++;
                    continue;
                }
                Vector3 restCenter = new(adjoining.CellX + 0.5f, adjoining.CellY + 0.5f, adjoining.CellZ + 0.5f);
                displacement += adjoining.Center - (restCenter + latticeOffset);
                contributors++;
            }
            Vector3 position;
            if (explicitContributors > 0)
            {
                position = explicitPosition / explicitContributors;
            }
            else
            {
                if (contributors > 0) displacement /= contributors;
                position = new Vector3(x, y, z) + latticeOffset + displacement;
            }
            int index = positions.Count;
            positions.Add(position);
            restPositions.Add(new Vector3(x, y, z));
            vertexByCorner[(x, y, z)] = index;
            return index;
        }

        foreach (PhysicsGridSmoothSphere cell in cellMap.Values)
        {
            int x = cell.CellX;
            int y = cell.CellY;
            int z = cell.CellZ;
            foreach ((int nx, int ny, int nz, int face) in NeighborFaces)
            {
                if (cellMap.ContainsKey((x + nx, y + ny, z + nz))) continue;
                (int A, int B, int C, int D) corners = face switch
                {
                    0 => (Corner(x, y + 1, z), Corner(x, y + 1, z + 1), Corner(x + 1, y + 1, z + 1), Corner(x + 1, y + 1, z)),
                    1 => (Corner(x, y, z), Corner(x, y, z + 1), Corner(x, y + 1, z + 1), Corner(x, y + 1, z)),
                    2 => (Corner(x + 1, y, z), Corner(x + 1, y + 1, z), Corner(x + 1, y + 1, z + 1), Corner(x + 1, y, z + 1)),
                    3 => (Corner(x, y, z), Corner(x, y + 1, z), Corner(x + 1, y + 1, z), Corner(x + 1, y, z)),
                    4 => (Corner(x, y, z + 1), Corner(x + 1, y, z + 1), Corner(x + 1, y + 1, z + 1), Corner(x, y + 1, z + 1)),
                    _ => (Corner(x, y, z), Corner(x + 1, y, z), Corner(x + 1, y, z + 1), Corner(x, y, z + 1)),
                };
                faces.Add(new QuadFace(corners.A, corners.B, corners.C, corners.D,
                    cell.MaterialId, face, x, y, z));
            }
        }

        // Smooth only across coplanar source faces. Edges separating top/side/end
        // faces are explicit creases, so a soft voxel beam can bend without its
        // square cross-section turning into a capsule.
        HashSet<(int A, int B)> sharpEdges = FindSharpEdges(faces);
        // Explicit corners are the final authored geometry. Subdividing them invents
        // vertices that do not exist in collision/edit topology, rounds metal into
        // rubber and multiplies reflective/refractive triangles near the camera.
        bool allExplicit = cellMap.Values.All(cell => cell.HasExplicitCorners);
        int subdivisionPasses = allExplicit
            ? 0
            : cellMap.Count <= 64 ? 3 : cellMap.Count <= 384 ? 2 : 1;
        for (int pass = 0; pass < subdivisionPasses; pass++)
            Subdivide(ref positions, ref restPositions, ref faces, ref sharpEdges);

        return Triangulate(positions, restPositions, faces);
    }

    /// <summary>
    /// Glass fragments are triangular prisms encoded in the eight-corner transport
    /// type by repeating corner 2 as corner 3 (and 6 as 7). Treating that encoding as
    /// a hexahedron emitted two zero-area quads plus overlapping triangles. DXR then
    /// repeatedly hit those coplanar faces at close range. Emit the actual five prism
    /// faces instead.
    /// </summary>
    private static bool TryBuildTrianglePrism(
        PhysicsGridSmoothSphere cell,
        out SoftBodySurfaceVertex[] vertices)
    {
        vertices = Array.Empty<SoftBodySurfaceVertex>();
        if (!cell.HasExplicitCorners) return false;
        Vector3[] c = cell.ExplicitCorners!;
        const float duplicateEpsilonSquared = 0.000001f * 0.000001f;
        if (c.Length != 8 ||
            Vector3.DistanceSquared(c[2], c[3]) > duplicateEpsilonSquared ||
            Vector3.DistanceSquared(c[6], c[7]) > duplicateEpsilonSquared)
            return false;

        Vector3 frontCenter = (c[0] + c[1] + c[2]) / 3f;
        Vector3 backCenter = (c[4] + c[5] + c[6]) / 3f;
        Vector3 depth = backCenter - frontCenter;
        if (depth.LengthSquared() < 0.00000001f) return true;

        Vector3 prismCenter = (frontCenter + backCenter) * 0.5f;
        var result = new List<SoftBodySurfaceVertex>(24);
        EmitTriangle(c[0], c[1], c[2], -depth);
        EmitTriangle(c[4], c[5], c[6], depth);
        EmitSide(0, 1, 4, 5);
        EmitSide(1, 2, 5, 6);
        EmitSide(2, 0, 6, 4);
        vertices = result.ToArray();
        return true;

        void EmitSide(int frontA, int frontB, int backA, int backB)
        {
            Vector3 faceCenter = (c[frontA] + c[frontB] + c[backA] + c[backB]) * 0.25f;
            Vector3 outward = faceCenter - prismCenter;
            EmitTriangle(c[frontA], c[frontB], c[backB], outward);
            EmitTriangle(c[frontA], c[backB], c[backA], outward);
        }

        void EmitTriangle(Vector3 a, Vector3 b, Vector3 d, Vector3 outward)
        {
            Vector3 normal = Vector3.Cross(b - a, d - a);
            if (normal.LengthSquared() < 0.00000001f) return;
            if (Vector3.Dot(normal, outward) < 0f)
            {
                (b, d) = (d, b);
                normal = -normal;
            }
            normal = Vector3.Normalize(normal);
            int directedFace = DirectedFace(normal);
            int atlasFace = directedFace == 0 ? 0 : directedFace == 5 ? 2 : 1;
            result.Add(Vertex(a));
            result.Add(Vertex(b));
            result.Add(Vertex(d));

            SoftBodySurfaceVertex Vertex(Vector3 position) => new(
                position,
                normal,
                ProjectUv(position, directedFace),
                cell.MaterialId,
                atlasFace,
                cell.CellX,
                cell.CellY,
                cell.CellZ);
        }

        static int DirectedFace(Vector3 normal)
        {
            Vector3 absolute = Vector3.Abs(normal);
            if (absolute.Y >= absolute.X && absolute.Y >= absolute.Z) return normal.Y >= 0f ? 0 : 5;
            if (absolute.X >= absolute.Z) return normal.X >= 0f ? 2 : 1;
            return normal.Z >= 0f ? 4 : 3;
        }

        static Vector2 ProjectUv(Vector3 position, int face) => face switch
        {
            0 or 5 => new Vector2(position.X, position.Z),
            1 or 2 => new Vector2(position.Z, position.Y),
            _ => new Vector2(position.X, position.Y),
        };
    }

    public static SoftBodyBeamSegment[] BuildBeamSegments(IReadOnlyList<PhysicsGridSmoothSphere> cells)
    {
        if (cells.Count == 0) return Array.Empty<SoftBodyBeamSegment>();
        if (cells.All(cell => cell.HasExplicitCorners))
        {
            // Compute PT cannot consume the raster/DXR triangle surface directly. Use
            // one oriented prism proxy per authored cell instead of connecting cell
            // centres with half-block beams (which made a 6x6 metal dent explode into
            // oversized bars). Local Z is the authored depth direction.
            return cells.Select(cell =>
            {
                Vector3 front = (cell.GetCorner(0, 0, 0) + cell.GetCorner(1, 0, 0) +
                                 cell.GetCorner(0, 1, 0) + cell.GetCorner(1, 1, 0)) * 0.25f;
                Vector3 back = (cell.GetCorner(0, 0, 1) + cell.GetCorner(1, 0, 1) +
                                cell.GetCorner(0, 1, 1) + cell.GetCorner(1, 1, 1)) * 0.25f;
                float widthU = (Vector3.Distance(cell.GetCorner(0, 0, 0), cell.GetCorner(1, 0, 0)) +
                                Vector3.Distance(cell.GetCorner(0, 1, 0), cell.GetCorner(1, 1, 0))) * 0.5f;
                float widthV = (Vector3.Distance(cell.GetCorner(0, 0, 0), cell.GetCorner(0, 1, 0)) +
                                Vector3.Distance(cell.GetCorner(1, 0, 0), cell.GetCorner(1, 1, 0))) * 0.5f;
                float halfWidth = Math.Clamp(MathF.Min(widthU, widthV) * 0.48f, 0.018f, 0.5f);
                return new SoftBodyBeamSegment(front, back, halfWidth, cell.MaterialId,
                    cell.CellX, cell.CellY, cell.CellZ);
            }).ToArray();
        }
        Dictionary<(int X, int Y, int Z), PhysicsGridSmoothSphere> map = cells
            .GroupBy(cell => (cell.CellX, cell.CellY, cell.CellZ))
            .ToDictionary(group => group.Key, group => group.First());
        var result = new List<SoftBodyBeamSegment>();
        var connected = new HashSet<(int X, int Y, int Z)>();
        ReadOnlySpan<(int X, int Y, int Z)> positiveNeighbors = [(1, 0, 0), (0, 1, 0), (0, 0, 1)];
        foreach (((int x, int y, int z), PhysicsGridSmoothSphere cell) in map)
        {
            foreach ((int dx, int dy, int dz) in positiveNeighbors)
            {
                if (!map.TryGetValue((x + dx, y + dy, z + dz), out PhysicsGridSmoothSphere neighbor)) continue;
                connected.Add((x, y, z));
                connected.Add((x + dx, y + dy, z + dz));
                result.Add(new SoftBodyBeamSegment(
                    cell.Center,
                    neighbor.Center,
                    0.5f,
                    cell.MaterialId,
                    cell.CellX,
                    cell.CellY,
                    cell.CellZ));
            }
        }
        foreach (((int x, int y, int z), PhysicsGridSmoothSphere cell) in map)
        {
            if (connected.Contains((x, y, z))) continue;
            // A zero-length segment is one square soft voxel.
            result.Add(new SoftBodyBeamSegment(cell.Center, cell.Center,
                0.5f, cell.MaterialId,
                cell.CellX, cell.CellY, cell.CellZ));
        }
        return result.ToArray();
    }

    private static HashSet<(int A, int B)> FindSharpEdges(IReadOnlyList<QuadFace> faces)
    {
        static (int A, int B) Edge(int a, int b) => a < b ? (a, b) : (b, a);
        var adjoiningFaces = new Dictionary<(int A, int B), List<int>>();
        for (int faceIndex = 0; faceIndex < faces.Count; faceIndex++)
        {
            QuadFace face = faces[faceIndex];
            int[] vertices = [face.A, face.B, face.C, face.D];
            for (int corner = 0; corner < 4; corner++)
            {
                (int A, int B) edge = Edge(vertices[corner], vertices[(corner + 1) & 3]);
                if (!adjoiningFaces.TryGetValue(edge, out List<int>? adjoining))
                    adjoiningFaces[edge] = adjoining = new List<int>(2);
                adjoining.Add(faceIndex);
            }
        }

        return adjoiningFaces
            .Where(pair => pair.Value.Count != 2 ||
                           faces[pair.Value[0]].FaceIndex != faces[pair.Value[1]].FaceIndex)
            .Select(pair => pair.Key)
            .ToHashSet();
    }

    private static void Subdivide(
        ref List<Vector3> positions,
        ref List<Vector3> restPositions,
        ref List<QuadFace> faces,
        ref HashSet<(int A, int B)> sharpEdges)
    {
        int oldVertexCount = positions.Count;
        Vector3[] facePoints = new Vector3[faces.Count];
        Vector3[] restFacePoints = new Vector3[faces.Count];
        var vertexFaces = Enumerable.Range(0, oldVertexCount).Select(_ => new List<int>()).ToArray();
        var vertexEdges = Enumerable.Range(0, oldVertexCount).Select(_ => new List<(int A, int B)>()).ToArray();
        var edgeFaces = new Dictionary<(int A, int B), List<int>>();

        static (int A, int B) Edge(int a, int b) => a < b ? (a, b) : (b, a);
        for (int faceIndex = 0; faceIndex < faces.Count; faceIndex++)
        {
            QuadFace face = faces[faceIndex];
            facePoints[faceIndex] = (positions[face.A] + positions[face.B] + positions[face.C] + positions[face.D]) * 0.25f;
            restFacePoints[faceIndex] = (restPositions[face.A] + restPositions[face.B] +
                                         restPositions[face.C] + restPositions[face.D]) * 0.25f;
            int[] vertices = [face.A, face.B, face.C, face.D];
            for (int corner = 0; corner < 4; corner++)
            {
                int a = vertices[corner];
                int b = vertices[(corner + 1) & 3];
                (int A, int B) edge = Edge(a, b);
                vertexFaces[a].Add(faceIndex);
                if (!vertexEdges[a].Contains(edge)) vertexEdges[a].Add(edge);
                if (!edgeFaces.TryGetValue(edge, out List<int>? adjoining))
                    edgeFaces[edge] = adjoining = new List<int>(2);
                adjoining.Add(faceIndex);
            }
        }

        var nextPositions = new List<Vector3>(oldVertexCount + faces.Count + edgeFaces.Count);
        var nextRestPositions = new List<Vector3>(oldVertexCount + faces.Count + edgeFaces.Count);
        for (int vertex = 0; vertex < oldVertexCount; vertex++)
        {
            List<(int A, int B)> edges = vertexEdges[vertex];
            List<(int A, int B)> creases = edges.Where(sharpEdges.Contains).ToList();
            if (creases.Count >= 3)
            {
                // Three or more creases define a voxel corner. Keeping it fixed is
                // what preserves a square block instead of rounding it into a ball.
                nextPositions.Add(positions[vertex]);
                nextRestPositions.Add(restPositions[vertex]);
                continue;
            }
            if (creases.Count == 2)
            {
                Vector3 neighborAverage = Vector3.Zero;
                Vector3 restNeighborAverage = Vector3.Zero;
                foreach ((int a, int b) in creases)
                {
                    neighborAverage += positions[a == vertex ? b : a];
                    restNeighborAverage += restPositions[a == vertex ? b : a];
                }
                // Cubic B-spline crease rule: 6P + P0 + P1, divided by eight.
                nextPositions.Add(positions[vertex] * 0.75f + neighborAverage * 0.125f);
                nextRestPositions.Add(restPositions[vertex] * 0.75f + restNeighborAverage * 0.125f);
                continue;
            }

            int count = vertexFaces[vertex].Count;
            if (count == 0)
            {
                nextPositions.Add(positions[vertex]);
                nextRestPositions.Add(restPositions[vertex]);
                continue;
            }
            Vector3 faceAverage = Vector3.Zero;
            Vector3 restFaceAverage = Vector3.Zero;
            foreach (int face in vertexFaces[vertex]) faceAverage += facePoints[face];
            foreach (int face in vertexFaces[vertex]) restFaceAverage += restFacePoints[face];
            faceAverage /= count;
            restFaceAverage /= count;
            Vector3 edgeAverage = Vector3.Zero;
            Vector3 restEdgeAverage = Vector3.Zero;
            foreach ((int a, int b) in edges)
            {
                edgeAverage += (positions[a] + positions[b]) * 0.5f;
                restEdgeAverage += (restPositions[a] + restPositions[b]) * 0.5f;
            }
            edgeAverage /= Math.Max(1, edges.Count);
            restEdgeAverage /= Math.Max(1, edges.Count);
            nextPositions.Add((faceAverage + edgeAverage * 2f + positions[vertex] * (count - 3f)) / count);
            nextRestPositions.Add((restFaceAverage + restEdgeAverage * 2f +
                                   restPositions[vertex] * (count - 3f)) / count);
        }

        int facePointBase = nextPositions.Count;
        nextPositions.AddRange(facePoints);
        nextRestPositions.AddRange(restFacePoints);
        var edgePointIndex = new Dictionary<(int A, int B), int>(edgeFaces.Count);
        foreach (KeyValuePair<(int A, int B), List<int>> pair in edgeFaces)
        {
            (int a, int b) = pair.Key;
            List<int> adjoining = pair.Value;
            Vector3 point = !sharpEdges.Contains(pair.Key) && adjoining.Count == 2
                ? (positions[a] + positions[b] + facePoints[adjoining[0]] + facePoints[adjoining[1]]) * 0.25f
                : (positions[a] + positions[b]) * 0.5f;
            Vector3 restPoint = !sharpEdges.Contains(pair.Key) && adjoining.Count == 2
                ? (restPositions[a] + restPositions[b] + restFacePoints[adjoining[0]] +
                   restFacePoints[adjoining[1]]) * 0.25f
                : (restPositions[a] + restPositions[b]) * 0.5f;
            edgePointIndex[pair.Key] = nextPositions.Count;
            nextPositions.Add(point);
            nextRestPositions.Add(restPoint);
        }

        var nextFaces = new List<QuadFace>(faces.Count * 4);
        for (int index = 0; index < faces.Count; index++)
        {
            QuadFace face = faces[index];
            int ab = edgePointIndex[Edge(face.A, face.B)];
            int bc = edgePointIndex[Edge(face.B, face.C)];
            int cd = edgePointIndex[Edge(face.C, face.D)];
            int da = edgePointIndex[Edge(face.D, face.A)];
            int fp = facePointBase + index;
            nextFaces.Add(face.WithVertices(face.A, ab, fp, da));
            nextFaces.Add(face.WithVertices(face.B, bc, fp, ab));
            nextFaces.Add(face.WithVertices(face.C, cd, fp, bc));
            nextFaces.Add(face.WithVertices(face.D, da, fp, cd));
        }
        var nextSharpEdges = new HashSet<(int A, int B)>();
        foreach ((int a, int b) in sharpEdges)
        {
            if (!edgePointIndex.TryGetValue((a, b), out int midpoint)) continue;
            nextSharpEdges.Add(Edge(a, midpoint));
            nextSharpEdges.Add(Edge(midpoint, b));
        }
        positions = nextPositions;
        restPositions = nextRestPositions;
        faces = nextFaces;
        sharpEdges = nextSharpEdges;
    }

    private static SoftBodySurfaceVertex[] Triangulate(
        IReadOnlyList<Vector3> positions,
        IReadOnlyList<Vector3> restPositions,
        IReadOnlyList<QuadFace> faces)
    {
        var normals = new Dictionary<(int Vertex, int Face), Vector3>();
        foreach (QuadFace face in faces)
        {
            Accumulate(face.A, face.B, face.C, face.FaceIndex);
            Accumulate(face.A, face.C, face.D, face.FaceIndex);
        }

        var result = new SoftBodySurfaceVertex[faces.Count * 6];
        int output = 0;
        foreach (QuadFace face in faces)
        {
            Emit(face.A, face);
            Emit(face.B, face);
            Emit(face.C, face);
            Emit(face.A, face);
            Emit(face.C, face);
            Emit(face.D, face);
        }
        return result;

        void Accumulate(int ia, int ib, int ic, int faceIndex)
        {
            Vector3 normal = Vector3.Cross(positions[ib] - positions[ia], positions[ic] - positions[ia]);
            if (normal.LengthSquared() <= 0.0000001f) return;
            AddNormal(ia, faceIndex, normal);
            AddNormal(ib, faceIndex, normal);
            AddNormal(ic, faceIndex, normal);
        }

        void AddNormal(int vertex, int faceIndex, Vector3 normal)
        {
            (int Vertex, int Face) key = (vertex, faceIndex);
            normals[key] = normals.GetValueOrDefault(key) + normal;
        }

        void Emit(int index, QuadFace face)
        {
            Vector3 position = positions[index];
            Vector3 restPosition = restPositions[index];
            Vector2 uv = face.FaceIndex switch
            {
                0 or 5 => new Vector2(restPosition.X, restPosition.Z),
                1 or 2 => new Vector2(restPosition.Z, restPosition.Y),
                _ => new Vector2(restPosition.X, restPosition.Y),
            };
            Vector3 accumulated = normals.GetValueOrDefault((index, face.FaceIndex), Vector3.UnitY);
            Vector3 normal = accumulated.LengthSquared() > 0.0000001f
                ? Vector3.Normalize(accumulated)
                : Vector3.UnitY;
            // Geometry has six directed faces, while the procedural material atlas has
            // only top / side / bottom. Passing 2..5 through as atlas indices sampled
            // neighbouring materials, which made a dent lose the texture of its source
            // block even though its MaterialId was still correct.
            int atlasFace = face.FaceIndex == 0 ? 0 : face.FaceIndex == 5 ? 2 : 1;
            result[output++] = new SoftBodySurfaceVertex(position, normal, uv,
                face.MaterialId, atlasFace, face.CellX, face.CellY, face.CellZ);
        }
    }

    private readonly record struct QuadFace(
        int A,
        int B,
        int C,
        int D,
        ushort MaterialId,
        int FaceIndex,
        int CellX,
        int CellY,
        int CellZ)
    {
        public QuadFace WithVertices(int a, int b, int c, int d) =>
            new(a, b, c, d, MaterialId, FaceIndex, CellX, CellY, CellZ);
    }
}
