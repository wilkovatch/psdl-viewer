using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;

namespace PsdlViewer.Formats.Psdl {
    public class PerimeterPoint {
        public int Vertex;
        /// <summary>Index of the room that connects at this point, -1 for none.</summary>
        public int Room = -1;

        public PerimeterPoint() { }

        public PerimeterPoint(int vertex, int room) {
            Vertex = vertex;
            Room = room;
        }

        public PerimeterPoint Clone() => new(Vertex, Room);
    }

    public class Room {
        public const int FlagBit0 = 1, FlagSubterranean = 2, FlagPlain = 4, FlagRoad = 8, FlagIntersection = 16;
        public const int FlagSpecialBound = 32, FlagWarp = 64, FlagInst = 128;
        public static readonly string[] FlagNames = {
            "Bit 0: Unknown", "Bit 1: Subterranean", "Bit 2: Plain", "Bit 3: Road",
            "Bit 4: Intersection", "Bit 5: Special bound", "Bit 6: Warp room", "Bit 7: INST"
        };
        public static readonly string[] FlagDescriptions = {
            "Never set in the original cities",
            "Below ground level (subways, tunnels): echo effect, no rain",
            "Standard ground level room",
            "Room with a road",
            "Intersection for ambient traffic and pedestrians",
            "Rain is always drawn in this room, even below other geometry; the spec only says \"something about the bound\"",
            "Room above or below other rooms (bridges, overpasses): the game links it with the rooms whose perimeters overlap it",
            "Room containing INST objects"
        };
        static readonly string[] ShortFlagNames = { "b0", "Sub", "Plain", "Road", "Inter", "Bound", "Warp", "INST" };

        public List<PerimeterPoint> Perimeter = new();
        public List<RoomAttribute> Attributes = new();
        /// <summary>Room flags: the bits define the type of the room.</summary>
        public int Flags = FlagPlain;
        /// <summary>
        /// Prop rule used when a prop path runs through this room (0 = none): rule n selects lines 2(n - 1) and 2(n - 1) + 1 of
        /// proprules.csv (header excluded), for the left and right side of the road.
        /// </summary>
        public int PropRule;

        public bool HasFlag(int mask) => (Flags & mask) != 0;
        public void SetFlag(int mask, bool on) => Flags = on ? Flags | mask : Flags & ~mask;

        public string FlagsText() {
            var parts = new List<string>();
            for (int i = 0; i < 8; i++) {
                if ((Flags & (1 << i)) != 0) parts.Add(ShortFlagNames[i]);
            }
            return string.Join(" ", parts);
        }

        /// <summary>Size of the attribute list in 16-bit words.</summary>
        public int AttributeSize => Attributes.Sum(a => a.LengthWords);

        public Room Clone() {
            return new Room {
                Perimeter = Perimeter.Select(p => p.Clone()).ToList(),
                Attributes = Attributes.Select(a => a.Clone()).ToList(),
                Flags = Flags,
                PropRule = PropRule
            };
        }
    }

    /// <summary>
    /// Entry of the last PSDL section (room based props). The Java viewer calls these "roads"; the game uses them as prop paths:
    /// the props of the first room's prop rule are placed along the roads of all the rooms of the path.
    /// </summary>
    public class RoomPath {
        /// <summary>
        /// 32-bit value that is possibly a set of flags (low word nonzero = propulate, high word nonzero = invert propulation); the
        /// cities use many different bit patterns, so the meaning of the individual bits is unknown.
        /// </summary>
        public uint Flags;
        /// <summary>Position of each forward lane across the road, as a fraction of its width (e.g. 0.25 and 0.75 for 1 + 1 lanes).</summary>
        public List<float> ForwardLanes = new();
        /// <summary>Position of each backward lane across the road, as a fraction of its width.</summary>
        public List<float> BackwardLanes = new();
        /// <summary>Looks like a bit field (unknown).</summary>
        public int Unknown6;
        /// <summary>
        /// Outer vertices of the first cross section of the first room's road: outer left sidewalk, left road edge, right road
        /// edge, outer right sidewalk (vertices 0, 1, 4, 5 of a divided road, 0, 0, 1, 1 of a road without sidewalks).
        /// </summary>
        public int[] StartCrossroads = new int[4];
        /// <summary>Same as <see cref="StartCrossroads"/> for the last cross section of the last room.</summary>
        public int[] EndCrossroads = new int[4];
        public List<int> Rooms = new();
        /// <summary>The room numbers are stored as negative values (all or none of them; meaning unknown).</summary>
        public bool NegativeRooms;

        public RoomPath Clone() {
            var copy = (RoomPath)MemberwiseClone();
            copy.ForwardLanes = new List<float>(ForwardLanes);
            copy.BackwardLanes = new List<float>(BackwardLanes);
            copy.StartCrossroads = (int[])StartCrossroads.Clone();
            copy.EndCrossroads = (int[])EndCrossroads.Clone();
            copy.Rooms = new List<int>(Rooms);
            return copy;
        }

        public override string ToString() {
            return $"{ForwardLanes.Count}+{BackwardLanes.Count} lanes, {Rooms.Count} rooms";
        }
    }

    public class Dimensions {
        public Vector3 Min = new(-1000, -1000, -1000);
        public Vector3 Max = new(1000, 1000, 1000);
        public Vector3 Center;
        public float Radius;

        public Dimensions Clone() => (Dimensions)MemberwiseClone();
    }

    public class PsdlFile {
        public string Magic = "PSD0";
        public uint TargetSize = 2;
        public List<Vector3> Vertices = new();
        public List<float> Heights = new();
        /// <summary>Texture names without extension; null for an empty entry.</summary>
        public List<string> Textures = new();
        /// <summary>Unknown value stored after the room count (number of junctions?).</summary>
        public uint Unknown0;
        public List<Room> Rooms = new();
        /// <summary>Flags of the unused room 0 (the room list is stored with one extra entry).</summary>
        public byte DummyRoomFlags;
        public byte DummyPropRule = 0xcd;
        public Dimensions Dimensions = new();
        public List<RoomPath> RoomPaths = new();
        /// <summary>Midnight Club (PSD1) only: index in materials.mtl stored after each texture name.</summary>
        public List<byte> McMaterials = new();
        /// <summary>Bounding spheres per room, only present in Midnight Club (PSD1) files.</summary>
        public List<(Vector3 center, float[] unknown)> McDimensions = new();

        /// <summary>Set by Load/Append when the file is damaged: what went wrong; only the data before that point was read.</summary>
        public string LoadError;

        public bool IsMidnightClub => Magic == "PSD1";

        public static PsdlFile Load(string path, Progress progress = null) {
            var psdl = new PsdlFile();
            psdl.Read(path, false, progress);
            return psdl;
        }

        /// <summary>Appends a whole PSDL file, offsetting all indices into the existing lists.</summary>
        public void Append(string path, Progress progress = null) {
            Read(path, true, progress);
        }

        void Read(string path, bool append, Progress progress) {
            LoadError = null;
            using var stream = File.OpenRead(path);
            using var r = new BinaryReader(stream);
            int vertexOffset = append ? Vertices.Count : 0;
            int heightOffset = append ? Heights.Count : 0;
            int textureOffset = append ? Textures.Count : 0;
            int roomOffset = append ? Rooms.Count : 0;
            progress?.Begin("Reading header", stream.Length);
            FileLog.Print($"{(append ? "Appending" : "Reading")} PSDL file {path}");

            string magic = r.ReadMagic();
            FileLog.Print("Header: " + magic);
            if (magic != "PSD0" && magic != "PSD1") throw new InvalidDataException("Not a PSDL file.");
            bool mc = magic == "PSD1";
            if (!append) Magic = magic;

            // like the original viewer, a damaged file keeps everything that was read before the problem
            string section = "header";
            void Section(string name) {
                section = name;
                if (progress != null) progress.Note = "Reading " + name;
            }
            var newRooms = new List<Room>();
            var newPaths = new List<RoomPath>();
            Dimensions dims = null;
            try {
                uint targetSize = r.ReadUInt32();
                if (!append) TargetSize = targetSize;

                Section("vertices");
                uint n = r.ReadUInt32();
                FileLog.Print($"{n} vertices");
                for (uint i = 0; i < n; i++) Vertices.Add(r.ReadVec3());

                Section("heights");
                n = r.ReadUInt32();
                FileLog.Print($"{n} heights");
                for (uint i = 0; i < n; i++) Heights.Add(r.ReadSingle());

                Section("textures");
                n = r.ReadUInt32();
                FileLog.Print($"{Math.Max(0, n - 1L)} textures");
                for (uint i = 0; i + 1 < n; i++) {
                    Textures.Add(r.ReadLengthString());
                    if (mc) McMaterials.Add(r.ReadByte());
                }
                if (progress != null) progress.Value = stream.Position;

                Section("rooms");
                long roomStart = stream.Position;
                uint roomCount = r.ReadUInt32();
                FileLog.Print($"{Math.Max(0, roomCount - 1L)} rooms (at 0x{roomStart:x})");
                uint unknown0 = r.ReadUInt32();
                if (!append) Unknown0 = unknown0;
                for (uint i = 0; i + 1 < roomCount; i++) {
                    var room = new Room();
                    newRooms.Add(room); // added first so that a truncated room keeps the part that was read
                    ReadRoom(r, room);
                    if (progress != null) progress.Value = stream.Position;
                }

                Section("room flags");
                byte dummyFlags = r.ReadByte();
                foreach (var b in newRooms) b.Flags = r.ReadByte();
                byte dummyProp = r.ReadByte();
                foreach (var b in newRooms) b.PropRule = r.ReadByte();
                if (!append) {
                    DummyRoomFlags = dummyFlags;
                    DummyPropRule = dummyProp;
                }

                if (mc) {
                    Section("MC dimensions");
                    for (int i = 0; i < newRooms.Count; i++) {
                        var center = r.ReadVec3();
                        var unknown = new[] { r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle() };
                        McDimensions.Add((center, unknown));
                    }
                    r.BaseStream.Seek(newRooms.Count * 2 + 702, SeekOrigin.Current);
                }

                Section("dimensions");
                dims = new Dimensions {
                    Min = r.ReadVec3(),
                    Max = r.ReadVec3(),
                    Center = r.ReadVec3(),
                    Radius = r.ReadSingle()
                };
                FileLog.Print($"Dimensions: min {BinaryUtil.V(dims.Min)}, max {BinaryUtil.V(dims.Max)}, center {BinaryUtil.V(dims.Center)}, radius {BinaryUtil.F(dims.Radius)}");
                if (mc) r.ReadUInt32();

                Section("room paths");
                n = r.ReadUInt32();
                FileLog.Print($"{n} room paths");
                for (uint i = 0; i < n; i++) {
                    var roomPath = new RoomPath();
                    newPaths.Add(roomPath);
                    ReadRoomPath(r, roomPath);
                }
                FileLog.Print($"Bytes left in file: {stream.Length - stream.Position}");
            } catch (Exception e) {
                LoadError = e is EndOfStreamException ? $"the file ends while reading the {section}." : $"error while reading the {section}: {e.Message}";
            }

            foreach (var room in newRooms) {
                foreach (var p in room.Perimeter) {
                    p.Vertex += vertexOffset;
                    if (p.Room >= 0) p.Room += roomOffset;
                }
                foreach (var a in room.Attributes) a.AddOffsets(vertexOffset, heightOffset, textureOffset);
            }
            Rooms.AddRange(newRooms);
            foreach (var roomPath in newPaths) {
                for (int k = 0; k < 4; k++) {
                    roomPath.StartCrossroads[k] += vertexOffset;
                    roomPath.EndCrossroads[k] += vertexOffset;
                }
                for (int k = 0; k < roomPath.Rooms.Count; k++) roomPath.Rooms[k] += roomOffset;
            }
            RoomPaths.AddRange(newPaths);

            if (dims == null) {
                // the file ends before the dimensions: use the bounds of the vertices that were read
                var fitted = new Dimensions();
                if (Fit(fitted, vertexOffset)) dims = fitted;
            }
            if (dims != null) {
                if (!append) Dimensions = dims;
                else MergeDimensions(dims);
            }
            if (progress != null) progress.Value = stream.Length;
        }

        static void ReadRoom(BinaryReader r, Room room) {
            uint perimeterCount = r.ReadUInt32();
            uint attributeSize = r.ReadUInt32();
            for (uint i = 0; i < perimeterCount; i++) {
                int vertex = r.ReadUInt16();
                int connected = r.ReadUInt16();
                room.Perimeter.Add(new PerimeterPoint(vertex, connected - 1));
            }
            long end = r.BaseStream.Position + attributeSize * 2L;
            int read = 0;
            while (read < attributeSize) {
                int id = r.ReadUInt16();
                read++;
                var attribute = RoomAttribute.Create(id);
                read += attribute.Read(r, id & 7, (int)attributeSize - read);
                room.Attributes.Add(attribute);
            }
            if (r.BaseStream.Position != end) r.BaseStream.Position = end;
        }

        static void ReadRoomPath(BinaryReader r, RoomPath path) {
            path.Flags = r.ReadUInt32();
            int forward = r.ReadByte();
            int backward = r.ReadByte();
            for (int i = 0; i < forward; i++) path.ForwardLanes.Add(r.ReadSingle());
            for (int i = 0; i < backward; i++) path.BackwardLanes.Add(r.ReadSingle());
            path.Unknown6 = r.ReadUInt16();
            for (int i = 0; i < 4; i++) path.StartCrossroads[i] = r.ReadUInt16();
            for (int i = 0; i < 4; i++) path.EndCrossroads[i] = r.ReadUInt16();
            int rooms = r.ReadByte();
            for (int i = 0; i < rooms; i++) {
                // the numbers are signed (room index + 1, sometimes negated)
                int room = r.ReadInt16();
                if (room < 0) path.NegativeRooms = true;
                path.Rooms.Add(Math.Abs(room) - 1);
            }
        }

        /// <summary>Fits the bounding box and sphere to the vertices; keeps the old values when there are none.</summary>
        public void FitDimensions() => Fit(Dimensions, 0);

        /// <summary>Sets the dimensions to the bounds of the vertices from firstVertex on; false when there are none.</summary>
        bool Fit(Dimensions d, int firstVertex) {
            if (firstVertex >= Vertices.Count) return false;
            d.Min = new Vector3(float.MaxValue);
            d.Max = new Vector3(float.MinValue);
            for (int i = firstVertex; i < Vertices.Count; i++) {
                d.Min = Vector3.Min(d.Min, Vertices[i]);
                d.Max = Vector3.Max(d.Max, Vertices[i]);
            }
            d.Center = (d.Min + d.Max) / 2;
            d.Radius = (d.Max - d.Center).Length();
            return true;
        }

        void MergeDimensions(Dimensions other) {
            Dimensions.Min = Vector3.Min(Dimensions.Min, other.Min);
            Dimensions.Max = Vector3.Max(Dimensions.Max, other.Max);
            Dimensions.Center = (Dimensions.Min + Dimensions.Max) / 2;
            Dimensions.Radius = (Dimensions.Max - Dimensions.Center).Length();
        }

        /// <summary>Writes the file. Like the original viewer, the output is always a Midtown Madness 2 (PSD0) file.</summary>
        public void Save(string path, Progress progress = null) {
            // validate everything first so that a failed save never leaves a truncated file behind
            for (int i = 0; i < Rooms.Count; i++) {
                for (int j = 0; j < Rooms[i].Attributes.Count; j++) {
                    string error = Rooms[i].Attributes[j].Validate();
                    if (error != null) throw new InvalidDataException($"Room {i:x}, attribute {j:x}: {error}");
                }
            }
            for (int i = 0; i < RoomPaths.Count; i++) {
                if (RoomPaths[i].Rooms.Any(b => b < 0 || b >= short.MaxValue)) throw new InvalidDataException($"Room path {i:x}: room numbers are stored as 16-bit signed values and must be below {short.MaxValue:x}.");
            }
            string temp = path + "#";
            using (var stream = File.Create(temp))
            using (var w = new BinaryWriter(stream)) {
                Write(w, progress);
            }
            File.Move(temp, path, true);
        }

        public void Write(BinaryWriter w, Progress progress = null) {
            progress?.Begin("Writing vertices", Rooms.Count + 1);
            w.Write(BinaryUtil.Text.GetBytes("PSD0"));
            w.Write(TargetSize);
            w.Write((uint)Vertices.Count);
            foreach (var v in Vertices) w.Write(v);
            w.Write((uint)Heights.Count);
            foreach (float h in Heights) w.Write(h);
            w.Write((uint)(Textures.Count + 1));
            foreach (string t in Textures) w.WriteLengthString(t);

            if (progress != null) progress.Note = "Writing rooms";
            w.Write((uint)(Rooms.Count + 1));
            w.Write(Unknown0);
            for (int i = 0; i < Rooms.Count; i++) {
                var room = Rooms[i];
                w.Write((uint)room.Perimeter.Count);
                w.Write((uint)room.AttributeSize);
                foreach (var p in room.Perimeter) {
                    w.Write((ushort)p.Vertex);
                    w.Write((ushort)(p.Room + 1));
                }
                foreach (var a in room.Attributes) a.Write(w);
                if (progress != null) progress.Value = i;
            }
            w.Write(DummyRoomFlags);
            foreach (var b in Rooms) w.Write((byte)b.Flags);
            w.Write(DummyPropRule);
            foreach (var b in Rooms) w.Write((byte)b.PropRule);

            if (progress != null) progress.Note = "Writing dimensions";
            w.Write(Dimensions.Min);
            w.Write(Dimensions.Max);
            w.Write(Dimensions.Center);
            w.Write(Dimensions.Radius);

            if (progress != null) progress.Note = "Writing room paths";
            w.Write((uint)RoomPaths.Count);
            foreach (var path in RoomPaths) {
                w.Write(path.Flags);
                w.Write((byte)path.ForwardLanes.Count);
                w.Write((byte)path.BackwardLanes.Count);
                foreach (float f in path.ForwardLanes) w.Write(f);
                foreach (float f in path.BackwardLanes) w.Write(f);
                w.Write((ushort)path.Unknown6);
                foreach (int v in path.StartCrossroads) w.Write((ushort)v);
                foreach (int v in path.EndCrossroads) w.Write((ushort)v);
                w.Write((byte)path.Rooms.Count);
                foreach (int b in path.Rooms) w.Write((short)(path.NegativeRooms ? -(b + 1) : b + 1));
            }
            if (progress != null) progress.Value = progress.Maximum;
        }

        // ---- room list maintenance (keeps every room reference consistent) ----

        /// <summary>Remaps room references after the room list changed. map[old] = new index, -1 if removed.</summary>
        public void RemapRoomReferences(Func<int, int> map) {
            foreach (var b in Rooms) {
                foreach (var p in b.Perimeter) {
                    if (p.Room >= 0) p.Room = map(p.Room);
                }
            }
            foreach (var path in RoomPaths) {
                for (int i = path.Rooms.Count - 1; i >= 0; i--) {
                    int m = map(path.Rooms[i]);
                    if (m < 0) path.Rooms.RemoveAt(i);
                    else path.Rooms[i] = m;
                }
            }
        }

        public void InsertRoom(int index, Room room) {
            RemapRoomReferences(i => i >= index ? i + 1 : i);
            Rooms.Insert(index, room);
        }

        public void RemoveRoom(int index) {
            Rooms.RemoveAt(index);
            RemapRoomReferences(i => i == index ? -1 : i > index ? i - 1 : i);
        }

        public void MoveRoom(int from, int to) {
            if (from == to) return;
            var room = Rooms[from];
            Rooms.RemoveAt(from);
            Rooms.Insert(to, room);
            RemapRoomReferences(i => MoveMap(i, from, to));
        }

        public static int MoveMap(int i, int from, int to) {
            if (i == from) return to;
            if (from < to && i > from && i <= to) return i - 1;
            if (to < from && i >= to && i < from) return i + 1;
            return i;
        }

        public Vector3 RoomCenter(Room room) {
            if (room.Perimeter.Count == 0) return Vector3.Zero;
            var sum = Vector3.Zero;
            int count = 0;
            foreach (var p in room.Perimeter) {
                if (p.Vertex < 0 || p.Vertex >= Vertices.Count) continue;
                sum += Vertices[p.Vertex];
                count++;
            }
            return count == 0 ? Vector3.Zero : sum / count;
        }

        public float HeightAt(int index) => index >= 0 && index < Heights.Count ? Heights[index] : 0;
        public string TextureAt(int index) => index >= 0 && index < Textures.Count ? Textures[index] : null;
    }
}
