using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;

namespace PsdlViewer.Formats.Psdl {
    public class PerimeterPoint {
        public int Vertex;
        /// <summary>Index of the block that connects at this point, -1 for none.</summary>
        public int Block = -1;

        public PerimeterPoint() { }

        public PerimeterPoint(int vertex, int block) {
            Vertex = vertex;
            Block = block;
        }

        public PerimeterPoint Clone() => new(Vertex, Block);
    }

    public class Block {
        public const int TypeBit0 = 1, TypeSubterranean = 2, TypePlain = 4, TypeRoad = 8, TypeIntersection = 16;
        public const int TypeBit5 = 32, TypeWarp = 64, TypeInst = 128;
        public static readonly string[] TypeNames = {
            "Bit 0: Unknown", "Bit 1: Subterranean", "Bit 2: Plain", "Bit 3: Road",
            "Bit 4: Intersection", "Bit 5: Unknown", "Bit 6: Warp room", "Bit 7: INST"
        };
        static readonly string[] ShortTypeNames = { "b0", "Sub", "Plain", "Road", "Inter", "b5", "Warp", "INST" };

        public List<PerimeterPoint> Perimeter = new();
        public List<BlockAttribute> Attributes = new();
        public int Type = TypePlain;
        /// <summary>Line in proprules.csv used when a prop path runs through this block (0 = none).</summary>
        public int PropRule;

        public bool IsType(int mask) => (Type & mask) != 0;
        public void SetType(int mask, bool on) => Type = on ? Type | mask : Type & ~mask;

        public string TypeText() {
            var parts = new List<string>();
            for (int i = 0; i < 8; i++) {
                if ((Type & (1 << i)) != 0) parts.Add(ShortTypeNames[i]);
            }
            return string.Join(" ", parts);
        }

        public int AttributeWords => Attributes.Sum(a => a.LengthWords);

        public Block Clone() {
            return new Block {
                Perimeter = Perimeter.Select(p => p.Clone()).ToList(),
                Attributes = Attributes.Select(a => a.Clone()).ToList(),
                Type = Type,
                PropRule = PropRule
            };
        }
    }

    /// <summary>
    /// Entry of the last PSDL section. The Java viewer calls these "roads"; they are now known to be prop paths,
    /// so the names of most fields are still guesses.
    /// </summary>
    public class RoadPath {
        public int Unknown0;
        public int Unknown1;
        public List<float> ForwardLanes = new();
        public List<float> BackwardLanes = new();
        public int Unknown2;
        public int[] StartVertices = new int[4];
        public int[] EndVertices = new int[4];
        public List<int> Blocks = new();

        public RoadPath Clone() {
            var copy = (RoadPath)MemberwiseClone();
            copy.ForwardLanes = new List<float>(ForwardLanes);
            copy.BackwardLanes = new List<float>(BackwardLanes);
            copy.StartVertices = (int[])StartVertices.Clone();
            copy.EndVertices = (int[])EndVertices.Clone();
            copy.Blocks = new List<int>(Blocks);
            return copy;
        }

        public override string ToString() {
            return $"{ForwardLanes.Count}+{BackwardLanes.Count} lanes, {Blocks.Count} blocks";
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
        public List<Block> Blocks = new();
        public byte DummyFlag;
        public byte DummyPropRule = 0xcd;
        public Dimensions Dimensions = new();
        public List<RoadPath> Roads = new();
        /// <summary>Bounding spheres per block, only present in Midnight Club (PSD1) files.</summary>
        public List<(Vector3 center, float[] unknown)> McDimensions = new();

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
            using var stream = File.OpenRead(path);
            using var r = new BinaryReader(stream);
            int vertexOffset = append ? Vertices.Count : 0;
            int heightOffset = append ? Heights.Count : 0;
            int textureOffset = append ? Textures.Count : 0;
            int blockOffset = append ? Blocks.Count : 0;
            progress?.Begin("Reading header", stream.Length);

            string magic = r.ReadMagic();
            if (magic != "PSD0" && magic != "PSD1") throw new InvalidDataException("Not a PSDL file.");
            bool mc = magic == "PSD1";
            uint targetSize = r.ReadUInt32();
            if (!append) {
                Magic = magic;
                TargetSize = targetSize;
            }

            if (progress != null) progress.Note = "Reading vertices";
            uint n = r.ReadUInt32();
            for (uint i = 0; i < n; i++) Vertices.Add(r.ReadVec3());

            if (progress != null) progress.Note = "Reading heights";
            n = r.ReadUInt32();
            for (uint i = 0; i < n; i++) Heights.Add(r.ReadSingle());

            if (progress != null) progress.Note = "Reading textures";
            n = r.ReadUInt32();
            for (uint i = 0; i + 1 < n; i++) {
                Textures.Add(r.ReadLengthString());
                if (mc) r.ReadByte();
            }
            if (progress != null) progress.Value = stream.Position;

            if (progress != null) progress.Note = "Reading blocks";
            uint roomCount = r.ReadUInt32();
            uint unknown0 = r.ReadUInt32();
            if (!append) Unknown0 = unknown0;
            var newBlocks = new List<Block>();
            for (uint i = 0; i + 1 < roomCount; i++) {
                var block = ReadBlock(r);
                foreach (var p in block.Perimeter) {
                    p.Vertex += vertexOffset;
                    if (p.Block >= 0) p.Block += blockOffset;
                }
                foreach (var a in block.Attributes) a.AddOffsets(vertexOffset, heightOffset, textureOffset);
                newBlocks.Add(block);
                if (progress != null) progress.Value = stream.Position;
            }
            Blocks.AddRange(newBlocks);

            if (progress != null) progress.Note = "Reading block types";
            byte dummyFlag = r.ReadByte();
            foreach (var b in newBlocks) b.Type = r.ReadByte();
            byte dummyProp = r.ReadByte();
            foreach (var b in newBlocks) b.PropRule = r.ReadByte();
            if (!append) {
                DummyFlag = dummyFlag;
                DummyPropRule = dummyProp;
            }

            if (mc) {
                if (progress != null) progress.Note = "Reading MC dimensions";
                for (int i = 0; i < newBlocks.Count; i++) {
                    var center = r.ReadVec3();
                    var unknown = new[] { r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle() };
                    McDimensions.Add((center, unknown));
                }
                r.BaseStream.Seek(newBlocks.Count * 2 + 702, SeekOrigin.Current);
            }

            if (progress != null) progress.Note = "Reading dimensions";
            var dims = new Dimensions {
                Min = r.ReadVec3(),
                Max = r.ReadVec3(),
                Center = r.ReadVec3(),
                Radius = r.ReadSingle()
            };
            if (!append) Dimensions = dims;
            else MergeDimensions(dims);
            if (mc) r.ReadUInt32();

            if (progress != null) progress.Note = "Reading roads";
            n = r.ReadUInt32();
            for (uint i = 0; i < n; i++) {
                var road = ReadRoad(r);
                for (int k = 0; k < 4; k++) {
                    road.StartVertices[k] += vertexOffset;
                    road.EndVertices[k] += vertexOffset;
                }
                for (int k = 0; k < road.Blocks.Count; k++) road.Blocks[k] += blockOffset;
                Roads.Add(road);
            }
            if (progress != null) progress.Value = stream.Length;
        }

        static Block ReadBlock(BinaryReader r) {
            var block = new Block();
            uint perimeterCount = r.ReadUInt32();
            uint attributeWords = r.ReadUInt32();
            for (uint i = 0; i < perimeterCount; i++) {
                int vertex = r.ReadUInt16();
                int room = r.ReadUInt16();
                block.Perimeter.Add(new PerimeterPoint(vertex, room - 1));
            }
            long end = r.BaseStream.Position + attributeWords * 2L;
            int read = 0;
            while (read < attributeWords) {
                int id = r.ReadUInt16();
                read++;
                var attribute = BlockAttribute.Create(id);
                read += attribute.Read(r, id & 7, (int)attributeWords - read);
                block.Attributes.Add(attribute);
            }
            if (r.BaseStream.Position != end) r.BaseStream.Position = end;
            return block;
        }

        static RoadPath ReadRoad(BinaryReader r) {
            var road = new RoadPath {
                Unknown0 = r.ReadUInt16(),
                Unknown1 = r.ReadUInt16()
            };
            int forward = r.ReadByte();
            int backward = r.ReadByte();
            for (int i = 0; i < forward; i++) road.ForwardLanes.Add(r.ReadSingle());
            for (int i = 0; i < backward; i++) road.BackwardLanes.Add(r.ReadSingle());
            road.Unknown2 = r.ReadUInt16();
            for (int i = 0; i < 4; i++) road.StartVertices[i] = r.ReadUInt16();
            for (int i = 0; i < 4; i++) road.EndVertices[i] = r.ReadUInt16();
            int blocks = r.ReadByte();
            for (int i = 0; i < blocks; i++) road.Blocks.Add(r.ReadUInt16() - 1);
            return road;
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
            for (int i = 0; i < Blocks.Count; i++) {
                for (int j = 0; j < Blocks[i].Attributes.Count; j++) {
                    string error = Blocks[i].Attributes[j].Validate();
                    if (error != null) throw new InvalidDataException($"Block {i:x}, attribute {j:x}: {error}");
                }
            }
            string temp = path + "#";
            using (var stream = File.Create(temp))
            using (var w = new BinaryWriter(stream)) {
                Write(w, progress);
            }
            File.Move(temp, path, true);
        }

        public void Write(BinaryWriter w, Progress progress = null) {
            progress?.Begin("Writing vertices", Blocks.Count + 1);
            w.Write(BinaryUtil.Text.GetBytes("PSD0"));
            w.Write(TargetSize);
            w.Write((uint)Vertices.Count);
            foreach (var v in Vertices) w.Write(v);
            w.Write((uint)Heights.Count);
            foreach (float h in Heights) w.Write(h);
            w.Write((uint)(Textures.Count + 1));
            foreach (string t in Textures) w.WriteLengthString(t);

            if (progress != null) progress.Note = "Writing blocks";
            w.Write((uint)(Blocks.Count + 1));
            w.Write(Unknown0);
            for (int i = 0; i < Blocks.Count; i++) {
                var block = Blocks[i];
                w.Write((uint)block.Perimeter.Count);
                w.Write((uint)block.AttributeWords);
                foreach (var p in block.Perimeter) {
                    w.Write((ushort)p.Vertex);
                    w.Write((ushort)(p.Block + 1));
                }
                foreach (var a in block.Attributes) a.Write(w);
                if (progress != null) progress.Value = i;
            }
            w.Write(DummyFlag);
            foreach (var b in Blocks) w.Write((byte)b.Type);
            w.Write(DummyPropRule);
            foreach (var b in Blocks) w.Write((byte)b.PropRule);

            if (progress != null) progress.Note = "Writing dimensions";
            w.Write(Dimensions.Min);
            w.Write(Dimensions.Max);
            w.Write(Dimensions.Center);
            w.Write(Dimensions.Radius);

            if (progress != null) progress.Note = "Writing roads";
            w.Write((uint)Roads.Count);
            foreach (var road in Roads) {
                w.Write((ushort)road.Unknown0);
                w.Write((ushort)road.Unknown1);
                w.Write((byte)road.ForwardLanes.Count);
                w.Write((byte)road.BackwardLanes.Count);
                foreach (float f in road.ForwardLanes) w.Write(f);
                foreach (float f in road.BackwardLanes) w.Write(f);
                w.Write((ushort)road.Unknown2);
                foreach (int v in road.StartVertices) w.Write((ushort)v);
                foreach (int v in road.EndVertices) w.Write((ushort)v);
                w.Write((byte)road.Blocks.Count);
                foreach (int b in road.Blocks) w.Write((ushort)(b + 1));
            }
            if (progress != null) progress.Value = progress.Maximum;
        }

        // ---- block list maintenance (keeps every block reference consistent) ----

        /// <summary>Remaps block references after the block list changed. map[old] = new index, -1 if removed.</summary>
        public void RemapBlockReferences(Func<int, int> map) {
            foreach (var b in Blocks) {
                foreach (var p in b.Perimeter) {
                    if (p.Block >= 0) p.Block = map(p.Block);
                }
            }
            foreach (var road in Roads) {
                for (int i = road.Blocks.Count - 1; i >= 0; i--) {
                    int m = map(road.Blocks[i]);
                    if (m < 0) road.Blocks.RemoveAt(i);
                    else road.Blocks[i] = m;
                }
            }
        }

        public void InsertBlock(int index, Block block) {
            RemapBlockReferences(i => i >= index ? i + 1 : i);
            Blocks.Insert(index, block);
        }

        public void RemoveBlock(int index) {
            Blocks.RemoveAt(index);
            RemapBlockReferences(i => i == index ? -1 : i > index ? i - 1 : i);
        }

        public void MoveBlock(int from, int to) {
            if (from == to) return;
            var block = Blocks[from];
            Blocks.RemoveAt(from);
            Blocks.Insert(to, block);
            RemapBlockReferences(i => MoveMap(i, from, to));
        }

        public static int MoveMap(int i, int from, int to) {
            if (i == from) return to;
            if (from < to && i > from && i <= to) return i - 1;
            if (to < from && i >= to && i < from) return i + 1;
            return i;
        }

        public Vector3 BlockCenter(Block block) {
            if (block.Perimeter.Count == 0) return Vector3.Zero;
            var sum = Vector3.Zero;
            int count = 0;
            foreach (var p in block.Perimeter) {
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
