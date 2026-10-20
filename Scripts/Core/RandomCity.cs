using System;
using System.Collections.Generic;
using System.Numerics;
using PsdlViewer.Formats.Psdl;

namespace PsdlViewer.Core {
    /// <summary>Random terrain generator behind the Random City wizard.</summary>
    public class RandomCity {
        public int Size = 9;
        public float SideLength = 500;
        public float InitialHeight;
        public float Smoothness = 0.001f;
        public float HeightScale = 10;
        public float WaterHeight;

        int[,] grid; // grid[x, z] -> vertex index
        PsdlFile psdl;
        readonly Random random = new();

        public bool Initialized => psdl != null;

        /// <summary>Creates the flat grid of rooms. Returns the new PSDL.</summary>
        public PsdlFile Init() {
            if (Size < 3) Size = 3;
            psdl = new PsdlFile();
            grid = new int[Size, Size];
            float start = -(SideLength / 2);
            float step = SideLength / Size;
            float z = start;
            for (int i = 0; i < Size; i++) {
                float x = start;
                for (int n = 0; n < Size; n++) {
                    grid[n, i] = psdl.Vertices.Count;
                    psdl.Vertices.Add(new Vector3(x, InitialHeight, z));
                    x += step;
                }
                z += step;
            }
            psdl.Textures.Add("s_grass");
            psdl.Textures.Add("s_grass2mud");
            psdl.Textures.Add("s_rock_concrt");
            int index = 0;
            for (int i = 1; i < Size - 1; i += 2) {
                for (int j = 1; j < Size - 1; j += 2) {
                    var room = new Room();
                    AddPerimeter(j, i, room, index);
                    AddGround(j, i, room);
                    psdl.Rooms.Add(room);
                    index++;
                }
            }
            Tools.AutoDimensions(psdl);
            return psdl;
        }

        int Offset(int x, int z) => z * Size + x;

        void AddPerimeter(int n, int n2, Room room, int id) {
            int size = Size;
            const int none = -1;
            int c2 = n == size - 2 ? none : id + 1;
            int c3 = n == 1 ? none : id - 1;
            int c4 = n2 == size - 2 ? none : id + size / 2;
            int c5 = n2 == 1 ? none : id - size / 2;
            int c6 = n2 == size - 2 || n == size - 2 ? none : id + size / 2 + 1;
            int c7 = n2 == size - 2 || n == 1 ? none : id + size / 2 - 1;
            int c8 = n2 == 1 || n == size - 2 ? none : id - size / 2 + 1;
            int c9 = n2 == 1 || n == 1 ? none : id - size / 2 - 1;
            void Add(int x, int z, int b) => room.Perimeter.Add(new PerimeterPoint(Offset(x, z), b));
            Add(n, n2, none);
            Add(n - 1, n2 + 1, c3);
            Add(n - 1, n2, c3);
            if (c3 != none || (c5 == none && c9 == none)) Add(n - 1, n2 - 1, c3);
            if (c9 != none) Add(n - 1, n2 - 1, c9);
            if (c5 != none) Add(n - 1, n2 - 1, c5);
            Add(n, n2 - 1, c5);
            if (c5 != none || (c2 == none && c8 == none)) Add(n + 1, n2 - 1, c5);
            if (c8 != none) Add(n + 1, n2 - 1, c8);
            if (c2 != none) Add(n + 1, n2 - 1, c2);
            Add(n + 1, n2, c2);
            if (c2 != none || (c4 == none && c6 == none)) Add(n + 1, n2 + 1, c2);
            if (c6 != none) Add(n + 1, n2 + 1, c6);
            if (c4 != none) Add(n + 1, n2 + 1, c4);
            Add(n, n2 + 1, c4);
            if (c4 != none || c7 == none) Add(n - 1, n2 + 1, c4);
            if (c7 != none) Add(n - 1, n2 + 1, c7);
            Add(n, n2, none);
        }

        void AddGround(int n, int n2, Room room) {
            room.Attributes.Add(new TextureReferenceAttribute { Index = (int)(random.NextDouble() * 2 + 0.5) });
            var fan = new FanAttribute(AttributeType.TriangleFan) { ExplicitCount = true };
            fan.Vertices.AddRange(new[] {
                Offset(n, n2), Offset(n - 1, n2 + 1), Offset(n, n2 + 1), Offset(n + 1, n2 + 1), Offset(n + 1, n2),
                Offset(n + 1, n2 - 1), Offset(n, n2 - 1), Offset(n - 1, n2 - 1), Offset(n - 1, n2), Offset(n - 1, n2 + 1), Offset(n, n2)
            });
            room.Attributes.Add(fan);
        }

        float Y(int x, int z) => psdl.Vertices[grid[x, z]].Y;

        void SetY(int x, int z, float y) {
            var v = psdl.Vertices[grid[x, z]];
            psdl.Vertices[grid[x, z]] = new Vector3(v.X, y, v.Z);
        }

        /// <summary>Diamond-square height generation, as in the original wizard.</summary>
        public void Smooth() {
            if (psdl == null) return;
            float ratio = (float)Math.Pow(2.0, -Smoothness);
            float scale = HeightScale * ratio;
            int max = Size - 1;
            for (int step = max / 2; step != 0; step >>= 1) {
                for (int z = step; z < max; z += step) {
                    for (int x = step; x < max; x += step) {
                        SetY(x, z, (float)(scale * (random.NextDouble() - 0.5)) + AvgSquare(z, x, step));
                        x += step;
                    }
                    z += step;
                }
                bool odd = false;
                for (int z = 0; z < max; z += step) {
                    odd = !odd;
                    for (int x = 0; x < max; x += step) {
                        if (odd && x == 0) x += step;
                        if (x >= Size) break;
                        SetY(x, z, (float)(scale * (random.NextDouble() - 0.5)) + AvgDiamond(z, x, step, max));
                        if (z == 0) SetY(x, max, Y(x, z));
                        if (x == 0) SetY(max, z, Y(x, z));
                        x += step;
                    }
                }
                scale *= ratio;
            }
            Tools.AutoDimensions(psdl);
        }

        float SafeY(int x, int z) => Y(Math.Clamp(x, 0, Size - 1), Math.Clamp(z, 0, Size - 1));

        float AvgSquare(int z, int x, int step) {
            return (SafeY(x - step, z - step) + SafeY(x + step, z - step) + SafeY(x - step, z + step) + SafeY(x + step, z + step)) * 0.25f;
        }

        float AvgDiamond(int z, int x, int step, int max) {
            if (z == 0) return (SafeY(x - step, z) + SafeY(x + step, z) + SafeY(x, max - step) + SafeY(x, z + step)) * 0.25f;
            if (z == Size - 1) return (SafeY(x - step, z) + SafeY(x + step, z) + SafeY(x, z - step) + SafeY(x, step)) * 0.25f;
            if (x == 0) return (SafeY(x, z - step) + SafeY(x, z + step) + SafeY(x + step, z) + SafeY(max - step, z)) * 0.25f;
            if (x == Size - 1) return (SafeY(x, z - step) + SafeY(x, z + step) + SafeY(x - step, z) + SafeY(step, z)) * 0.25f;
            return (SafeY(x, z - step) + SafeY(x, z + step) + SafeY(x - step, z) + SafeY(x + step, z)) * 0.25f;
        }

        public (Vector3 min, Vector3 max) Extent() {
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            foreach (var v in psdl.Vertices) {
                min = Vector3.Min(min, v);
                max = Vector3.Max(max, v);
            }
            return (min, max);
        }

        /// <summary>Computes the shore lines where the terrain crosses the water level, grouped into polygons.</summary>
        public List<List<(Vector3 a, Vector3 b)>> Water() {
            var edges = new List<(Vector3 a, Vector3 b)>();
            if (psdl == null) return new List<List<(Vector3, Vector3)>>();
            foreach (var room in psdl.Rooms) {
                var per = room.Perimeter;
                if (per.Count < 3) continue;
                var center = psdl.Vertices[per[0].Vertex];
                int n = 1;
                var current = psdl.Vertices[per[n].Vertex];
                while (n < per.Count) {
                    var previous = current;
                    while (n < per.Count && psdl.Vertices[per[n].Vertex] == previous) n++;
                    if (n >= per.Count) break;
                    current = psdl.Vertices[per[n].Vertex];
                    IntersectFace(center, previous, current, edges);
                }
            }
            Vector3? e0 = null, e1 = null, e2 = null, e3 = null;
            int s = Size - 1;
            for (int n = 0; n < Size - 1; n++) {
                e0 = CheckEdgeSegment(n, V(n, s), V(n + 1, s), e0, edges);
                e1 = CheckEdgeSegment(n, V(s, s - n), V(s, s - n - 1), e1, edges);
                e2 = CheckEdgeSegment(n, V(s - n, 0), V(s - n - 1, 0), e2, edges);
                e3 = CheckEdgeSegment(n, V(0, n), V(0, n + 1), e3, edges);
            }
            return GroupEdges(edges);
        }

        Vector3 V(int x, int z) => psdl.Vertices[grid[x, z]];

        Vector3? CheckEdgeSegment(int n, Vector3 a, Vector3 b, Vector3? open, List<(Vector3, Vector3)> edges) {
            float h = WaterHeight;
            if (n == 0 && a.Y < h) open = a;
            if (a.Y > h && b.Y < h) {
                open = IntersectLine(a, b, h);
            } else if (a.Y < h && b.Y > h && open != null) {
                var x = IntersectLine(a, b, h);
                if (x != null) edges.Add((open.Value, x.Value));
                open = null;
            }
            if (n == Size - 2 && b.Y < h && open != null) {
                edges.Add((open.Value, b));
                open = null;
            }
            return open;
        }

        void IntersectFace(Vector3 a, Vector3 b, Vector3 c, List<(Vector3, Vector3)> edges) {
            float h = WaterHeight;
            var ab = IntersectLine(a, b, h);
            var bc = IntersectLine(b, c, h);
            var ca = IntersectLine(c, a, h);
            if (ab == null && bc != null && ca != null) edges.Add(c.Y < h ? (bc.Value, ca.Value) : (ca.Value, bc.Value));
            else if (ab != null && bc == null && ca != null) edges.Add(a.Y < h ? (ca.Value, ab.Value) : (ab.Value, ca.Value));
            else if (ab != null && bc != null && ca == null) edges.Add(b.Y < h ? (ab.Value, bc.Value) : (bc.Value, ab.Value));
        }

        static Vector3? IntersectLine(Vector3 a, Vector3 b, float h) {
            if (b.Y == a.Y) return null;
            float t = (h - a.Y) / (b.Y - a.Y);
            if (t > 0 && t < 1) return new Vector3(a.X + t * (b.X - a.X), h, a.Z + t * (b.Z - a.Z));
            return null;
        }

        static bool Same(Vector3 a, Vector3 b) => Vector3.DistanceSquared(a, b) < 1e-4f;

        static List<List<(Vector3 a, Vector3 b)>> GroupEdges(List<(Vector3 a, Vector3 b)> edges) {
            var polygons = new List<List<(Vector3, Vector3)>>();
            while (edges.Count != 0) {
                var edge = edges[0];
                edges.RemoveAt(0);
                var polygon = new List<(Vector3, Vector3)> { edge };
                polygons.Add(polygon);
                bool found = true;
                while (found) {
                    found = false;
                    for (int i = 0; i < edges.Count; i++) {
                        if (!Same(edges[i].a, edge.b)) continue;
                        edge = edges[i];
                        edges.RemoveAt(i);
                        polygon.Add(edge);
                        found = true;
                        break;
                    }
                }
            }
            return polygons;
        }
    }
}
