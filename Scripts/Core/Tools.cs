using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using PsdlViewer.Formats;
using PsdlViewer.Formats.Psdl;

namespace PsdlViewer.Core {
    /// <summary>The "Tools" menu operations of the original viewer.</summary>
    public static class Tools {
        /// <summary>Sets up the neighbour reference of every perimeter point (vertices shared with other blocks).</summary>
        public static void AutoNeighbours(PsdlFile psdl, Progress progress) {
            var blocks = psdl.Blocks;
            progress.Begin("Computing perimeters", blocks.Count);
            // vertex -> blocks whose perimeter contains it, in block order
            var users = new Dictionary<int, List<int>>();
            for (int k = 0; k < blocks.Count; k++) {
                int last = -1;
                foreach (var p in blocks[k].Perimeter) {
                    if (p.Vertex == last) continue;
                    last = p.Vertex;
                    if (!users.TryGetValue(p.Vertex, out var list)) users[p.Vertex] = list = new List<int>();
                    list.Add(k);
                }
            }
            for (int i = 0; i < blocks.Count; i++) {
                progress.Value = i;
                var perimeter = blocks[i].Perimeter;
                int previous = -1;
                for (int j = 0; j < perimeter.Count; j++) {
                    var point = perimeter[j];
                    if (point.Vertex == previous) {
                        perimeter.RemoveAt(j);
                        j--;
                        continue;
                    }
                    previous = point.Vertex;
                    point.Block = -1;
                    bool first = true;
                    if (!users.TryGetValue(point.Vertex, out var list)) continue;
                    foreach (int k in list) {
                        if (k == i) continue;
                        if (first) {
                            point.Block = k;
                            first = false;
                        } else {
                            perimeter.Insert(j++, new PerimeterPoint(point.Vertex, k));
                        }
                    }
                }
            }
        }

        /// <summary>Sets the "last attribute" flag on the final attribute of every block only.</summary>
        public static void AutoLastAttribute(PsdlFile psdl, Progress progress) {
            progress.Begin("Computing last attribute flags", psdl.Blocks.Count);
            for (int i = 0; i < psdl.Blocks.Count; i++) {
                var attributes = psdl.Blocks[i].Attributes;
                for (int j = 0; j < attributes.Count; j++) attributes[j].Last = j == attributes.Count - 1;
                progress.Value = i;
            }
        }

        /// <summary>Sets the road and intersection bits from the attributes each block contains.</summary>
        public static void AutoBlockType(PsdlFile psdl, Progress progress) {
            progress.Begin("Computing block types", psdl.Blocks.Count);
            for (int i = 0; i < psdl.Blocks.Count; i++) {
                var block = psdl.Blocks[i];
                bool road = false, intersection = false;
                foreach (var a in block.Attributes) {
                    switch (a.Type) {
                        case AttributeType.Road:
                        case AttributeType.RoadNoSidewalk:
                        case AttributeType.DividedRoad:
                            road = true;
                            break;
                        case AttributeType.SidewalkStrip:
                            intersection = true;
                            break;
                    }
                }
                block.SetType(Block.TypeRoad, road);
                block.SetType(Block.TypeIntersection, intersection);
                progress.Value = i;
            }
        }

        /// <summary>Sorts blocks: plain blocks first, then roads, then intersections (references are updated).</summary>
        public static void AutoBlockSort(PsdlFile psdl, Progress progress) {
            progress.Begin("Sorting blocks", psdl.Blocks.Count);
            int plainEnd = 0, roadEnd = 0, crossingEnd = 0;
            for (int i = 0; i < psdl.Blocks.Count; i++) {
                progress.Value = i;
                var block = psdl.Blocks[i];
                if (block.IsType(Block.TypeIntersection)) {
                    if (i != crossingEnd) Doc.MoveBlock(i, crossingEnd);
                    crossingEnd++;
                } else if (block.IsType(Block.TypeRoad)) {
                    if (i != roadEnd) Doc.MoveBlock(i, roadEnd);
                    roadEnd++;
                    crossingEnd++;
                } else if (block.IsType(Block.TypePlain)) {
                    if (i != plainEnd) Doc.MoveBlock(i, plainEnd);
                    plainEnd++;
                    roadEnd++;
                    crossingEnd++;
                }
            }
        }

        /// <summary>Recomputes the bounding box and sphere from the vertex list.</summary>
        public static void AutoDimensions(PsdlFile psdl) {
            var d = psdl.Dimensions;
            if (psdl.Vertices.Count == 0) return;
            d.Min = new Vector3(float.MaxValue);
            d.Max = new Vector3(float.MinValue);
            foreach (var v in psdl.Vertices) {
                d.Min = Vector3.Min(d.Min, v);
                d.Max = Vector3.Max(d.Max, v);
            }
            d.Center = (d.Min + d.Max) / 2;
            d.Radius = (d.Max - d.Center).Length();
        }

        class FacadeEdge {
            public int P0, P1;
            public int Top;
            public BlockAttribute First;
            public bool HasBound;
        }

        /// <summary>Adds a facade bound for every facade/sliver edge that doesn't have one.</summary>
        public static int AutoFacadeBounds(PsdlFile psdl, Progress progress) {
            progress.Begin("Computing facade bounds", psdl.Blocks.Count);
            int added = 0;
            for (int i = 0; i < psdl.Blocks.Count; i++) {
                progress.Value = i;
                var attributes = psdl.Blocks[i].Attributes;
                var edges = new List<FacadeEdge>();
                FacadeEdge AddEdge(int p0, int p1, int top, BlockAttribute a) {
                    foreach (var e in edges) {
                        if (e.P0 == p0 && e.P1 == p1) {
                            if (psdl.HeightAt(top) > psdl.HeightAt(e.Top)) e.Top = top;
                            return e;
                        }
                    }
                    var edge = new FacadeEdge { P0 = p0, P1 = p1, Top = top, First = a };
                    edges.Add(edge);
                    return edge;
                }
                foreach (var a in attributes) {
                    switch (a) {
                        case FacadeBoundAttribute b: AddEdge(b.Vertices[0], b.Vertices[1], b.TopHeight, a).HasBound = true; break;
                        case SliverAttribute s: AddEdge(s.Vertices[0], s.Vertices[1], s.TopHeight, a); break;
                        case FacadeAttribute f: AddEdge(f.Vertices[0], f.Vertices[1], f.TopHeight, a); break;
                    }
                }
                foreach (var e in edges) {
                    if (e.HasBound) continue;
                    var bound = new FacadeBoundAttribute { TopHeight = e.Top, Vertices = new List<int> { e.P0, e.P1 } };
                    attributes.Insert(attributes.IndexOf(e.First), bound);
                    added++;
                }
            }
            return added;
        }

        /// <summary>Writes the simplest possible CPVS: every block sees all blocks within the given radius.</summary>
        public static void GenerateCpvs(PsdlFile psdl, float radius, string path, Progress progress) {
            var blocks = psdl.Blocks;
            progress.Begin("Generating CPVS file", blocks.Count);
            var sets = new List<byte[]>();
            for (int i = 0; i < blocks.Count; i++) {
                progress.Note = "Block " + i;
                progress.Value = i;
                var center = psdl.BlockCenter(blocks[i]);
                var visible = new bool[blocks.Count];
                visible[i] = true;
                for (int j = 0; j < blocks.Count; j++) {
                    if (visible[j]) continue;
                    foreach (var p in blocks[j].Perimeter) {
                        if (p.Vertex < psdl.Vertices.Count && Vector3.Distance(psdl.Vertices[p.Vertex], center) < radius) {
                            visible[j] = true;
                            break;
                        }
                    }
                }
                sets.Add(EncodeVisibility(visible));
            }
            progress.Note = "Writing file";
            string temp = path + "#";
            using (var stream = File.Create(temp))
            using (var w = new BinaryWriter(stream)) {
                w.Write(BinaryUtil.Text.GetBytes("PVS0"));
                w.Write((uint)(blocks.Count + 2));
                uint offset = 0;
                foreach (var s in sets) {
                    w.Write(offset);
                    offset += (uint)s.Length;
                }
                w.Write(offset);
                foreach (var s in sets) w.Write(s);
            }
            File.Move(temp, path, true);
        }

        /// <summary>Run-length style encoding of the original viewer: 2 bits per block, 0x80 headers per 128 bytes.</summary>
        static byte[] EncodeVisibility(bool[] visible) {
            var buf = new byte[visible.Length + visible.Length / 127 + 4];
            int shift = 2, n = 0, header = 0, count = 0;
            buf[n++] = 0x80;
            for (int i = 0; i < visible.Length; i++) {
                if (visible[i]) buf[n] = (byte)(buf[n] + (3 << shift));
                shift += 2;
                if (shift == 8) {
                    n++;
                    shift = 0;
                    count++;
                }
                if (count != 128) continue;
                buf[header] = (byte)(buf[header] + (count - 1));
                header = n;
                buf[n++] = 0x80;
                count = 0;
            }
            if (count != 0) buf[header] = (byte)(buf[header] + (count - 1));
            return buf.AsSpan(0, n).ToArray();
        }

        /// <summary>Appends vertices from a text file with one "x,y,z" per line.</summary>
        public static int ImportVertices(PsdlFile psdl, string path) {
            int added = 0;
            foreach (string line in File.ReadAllLines(path)) {
                if (string.IsNullOrWhiteSpace(line)) continue;
                string[] parts = line.Split(',');
                if (parts.Length < 3) throw new InvalidDataException("Expected \"x,y,z\" on line: " + line);
                psdl.Vertices.Add(new Vector3(
                    float.Parse(parts[0], CultureInfo.InvariantCulture),
                    float.Parse(parts[1], CultureInfo.InvariantCulture),
                    float.Parse(parts[2], CultureInfo.InvariantCulture)));
                added++;
            }
            return added;
        }
    }
}
