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
        /// <summary>Sets up the neighbour reference of every perimeter point (vertices shared with other rooms).</summary>
        public static void AutoNeighbours(PsdlFile psdl, Progress progress) {
            var rooms = psdl.Rooms;
            progress.Begin("Computing perimeters", rooms.Count);
            // vertex -> rooms whose perimeter contains it, in room order
            var users = new Dictionary<int, List<int>>();
            for (int k = 0; k < rooms.Count; k++) {
                int last = -1;
                foreach (var p in rooms[k].Perimeter) {
                    if (p.Vertex == last) continue;
                    last = p.Vertex;
                    if (!users.TryGetValue(p.Vertex, out var list)) users[p.Vertex] = list = new List<int>();
                    list.Add(k);
                }
            }
            for (int i = 0; i < rooms.Count; i++) {
                progress.Value = i;
                var perimeter = rooms[i].Perimeter;
                int previous = -1;
                for (int j = 0; j < perimeter.Count; j++) {
                    var point = perimeter[j];
                    if (point.Vertex == previous) {
                        perimeter.RemoveAt(j);
                        j--;
                        continue;
                    }
                    previous = point.Vertex;
                    point.Room = -1;
                    bool first = true;
                    if (!users.TryGetValue(point.Vertex, out var list)) continue;
                    foreach (int k in list) {
                        if (k == i) continue;
                        if (first) {
                            point.Room = k;
                            first = false;
                        } else {
                            perimeter.Insert(j++, new PerimeterPoint(point.Vertex, k));
                        }
                    }
                }
            }
        }

        /// <summary>Sets the "last attribute" flag on the final attribute of every room only.</summary>
        public static void AutoLastAttribute(PsdlFile psdl, Progress progress) {
            progress.Begin("Computing last attribute flags", psdl.Rooms.Count);
            for (int i = 0; i < psdl.Rooms.Count; i++) {
                var attributes = psdl.Rooms[i].Attributes;
                for (int j = 0; j < attributes.Count; j++) attributes[j].Last = j == attributes.Count - 1;
                progress.Value = i;
            }
        }

        /// <summary>Sets the road and intersection flags from the attributes each room contains.</summary>
        public static void AutoRoomFlags(PsdlFile psdl, Progress progress) {
            progress.Begin("Computing room flags", psdl.Rooms.Count);
            for (int i = 0; i < psdl.Rooms.Count; i++) {
                var room = psdl.Rooms[i];
                bool road = false, intersection = false;
                foreach (var a in room.Attributes) {
                    switch (a.Type) {
                        case AttributeType.RoadWithSidewalks:
                        case AttributeType.RoadWithoutSidewalks:
                        case AttributeType.DividedRoad:
                            road = true;
                            break;
                        case AttributeType.SidewalkStrip:
                            intersection = true;
                            break;
                    }
                }
                room.SetFlag(Room.FlagRoad, road);
                room.SetFlag(Room.FlagIntersection, intersection);
                progress.Value = i;
            }
        }

        /// <summary>Sorts rooms: plain rooms first, then road rooms, then intersections (references are updated).</summary>
        public static void AutoRoomSort(PsdlFile psdl, Progress progress) {
            progress.Begin("Sorting rooms", psdl.Rooms.Count);
            int plainEnd = 0, roadEnd = 0, crossingEnd = 0;
            for (int i = 0; i < psdl.Rooms.Count; i++) {
                progress.Value = i;
                var room = psdl.Rooms[i];
                if (room.HasFlag(Room.FlagIntersection)) {
                    if (i != crossingEnd) Doc.MoveRoom(i, crossingEnd);
                    crossingEnd++;
                } else if (room.HasFlag(Room.FlagRoad)) {
                    if (i != roadEnd) Doc.MoveRoom(i, roadEnd);
                    roadEnd++;
                    crossingEnd++;
                } else if (room.HasFlag(Room.FlagPlain)) {
                    if (i != plainEnd) Doc.MoveRoom(i, plainEnd);
                    plainEnd++;
                    roadEnd++;
                    crossingEnd++;
                }
            }
        }

        /// <summary>Recomputes the bounding box and sphere from the vertex list.</summary>
        public static void AutoDimensions(PsdlFile psdl) => psdl.FitDimensions();

        class FacadeEdge {
            public int P0, P1;
            public int Top;
            public RoomAttribute First;
            public bool HasBound;
        }

        /// <summary>Adds a facade bound for every facade/sliver edge that doesn't have one.</summary>
        public static int AutoFacadeBounds(PsdlFile psdl, Progress progress) {
            progress.Begin("Computing facade bounds", psdl.Rooms.Count);
            int added = 0;
            for (int i = 0; i < psdl.Rooms.Count; i++) {
                progress.Value = i;
                var attributes = psdl.Rooms[i].Attributes;
                var edges = new List<FacadeEdge>();
                FacadeEdge AddEdge(int p0, int p1, int top, RoomAttribute a) {
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
                    if (e.P0 >= 0 && e.P0 < psdl.Vertices.Count && e.P1 >= 0 && e.P1 < psdl.Vertices.Count) {
                        bound.Angle = FacadeBoundAttribute.SunAngle(psdl.Vertices[e.P0], psdl.Vertices[e.P1]);
                    }
                    attributes.Insert(attributes.IndexOf(e.First), bound);
                    added++;
                }
            }
            return added;
        }

        /// <summary>Writes the simplest possible CPVS: every room sees all rooms within the given radius.</summary>
        public static void GenerateCpvs(PsdlFile psdl, float radius, string path, Progress progress) {
            var rooms = psdl.Rooms;
            progress.Begin("Generating CPVS file", rooms.Count);
            var sets = new List<byte[]>();
            for (int i = 0; i < rooms.Count; i++) {
                progress.Note = "Room " + i;
                progress.Value = i;
                var center = psdl.RoomCenter(rooms[i]);
                var visible = new bool[rooms.Count];
                visible[i] = true;
                for (int j = 0; j < rooms.Count; j++) {
                    if (visible[j]) continue;
                    foreach (var p in rooms[j].Perimeter) {
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
                w.Write((uint)(rooms.Count + 2));
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

        /// <summary>Run-length style encoding of the original viewer: 2 bits per room, 0x80 headers per 128 bytes.</summary>
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
