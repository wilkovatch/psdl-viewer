using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PsdlViewer.Formats.Psdl {
    /// <summary>Attribute type codes (bits 3-6 of the attribute id word).</summary>
    public static class AttributeType {
        public const int RoadWithSidewalks = 0x0;
        public const int SidewalkStrip = 0x1;
        public const int RoadWithoutSidewalks = 0x2;
        public const int Sliver = 0x3;
        public const int CrosswalkRectangle = 0x4;
        public const int RoadTriangleFan = 0x5;
        public const int TriangleFan = 0x6;
        public const int FacadeBound = 0x7;
        public const int DividedRoad = 0x8;
        public const int Tunnel = 0x9;
        public const int TextureReference = 0xa;
        public const int Facade = 0xb;
        public const int RoofTriangleFan = 0xc;

        public static string Name(int type) {
            switch (type) {
                case RoadWithSidewalks: return "Road with sidewalks";
                case SidewalkStrip: return "Sidewalk strip";
                case RoadWithoutSidewalks: return "Road without sidewalks";
                case Sliver: return "Sliver";
                case CrosswalkRectangle: return "Crosswalk rectangle";
                case RoadTriangleFan: return "Road triangle fan";
                case TriangleFan: return "Triangle fan";
                case FacadeBound: return "Facade bound";
                case DividedRoad: return "Divided road";
                case Tunnel: return "Tunnel/railing";
                case TextureReference: return "Texture reference";
                case Facade: return "Facade";
                case RoofTriangleFan: return "Roof triangle fan";
                default: return "Unknown";
            }
        }
    }

    /// <summary>
    /// One room attribute. The id word is: bit 7 = last attribute, bits 3-6 = type, bits 0-2 = subtype.
    /// </summary>
    public abstract class RoomAttribute {
        public bool Last;

        public abstract int Type { get; }
        /// <summary>Subtype that will be written; usually derived from the content.</summary>
        public abstract int Subtype { get; }
        public int Id => (Type << 3) | Subtype;
        /// <summary>Number of 16-bit words after the id word.</summary>
        public abstract int BodyWords { get; }
        public int LengthWords => 1 + BodyWords;
        public virtual string Name => AttributeType.Name(Type);

        /// <summary>Reads the attribute body and returns the number of words consumed.</summary>
        public abstract int Read(BinaryReader r, int subtype, int remainingWords);
        protected abstract void WriteBody(BinaryWriter w);
        /// <summary>Returns null when the attribute can be written, otherwise a description of the problem.</summary>
        public virtual string Validate() => null;

        public void Write(BinaryWriter w) {
            string error = Validate();
            if (error != null) throw new InvalidDataException($"Attribute 0x{Id:x4} ({Name}): {error}");
            w.Write((ushort)(Id | (Last ? 0x80 : 0)));
            WriteBody(w);
        }

        public virtual void AddOffsets(int vertexOffset, int heightOffset, int textureOffset) { }
        public virtual IEnumerable<int> VertexRefs => Enumerable.Empty<int>();

        public RoomAttribute Clone() {
            var copy = (RoomAttribute)MemberwiseClone();
            copy.CloneLists();
            return copy;
        }

        protected virtual void CloneLists() { }

        public override string ToString() {
            return $"{Id:x4} {Describe()}";
        }

        public virtual string Describe() => Name;

        public static RoomAttribute Create(int id) {
            int type = (id & 0x7f) >> 3;
            int subtype = id & 7;
            RoomAttribute a;
            switch (type) {
                case AttributeType.RoadWithSidewalks: a = new StripAttribute(type, 4); break;
                case AttributeType.SidewalkStrip: a = new StripAttribute(type, 2); break;
                case AttributeType.RoadWithoutSidewalks: a = new StripAttribute(type, 2); break;
                case AttributeType.Sliver: a = subtype == 4 ? new SliverAttribute() : null; break;
                case AttributeType.CrosswalkRectangle: a = new CrosswalkRectangleAttribute(); break;
                case AttributeType.RoadTriangleFan: a = new FanAttribute(type); break;
                case AttributeType.TriangleFan: a = new FanAttribute(type); break;
                case AttributeType.FacadeBound: a = subtype == 4 ? new FacadeBoundAttribute() : null; break;
                case AttributeType.DividedRoad: a = new DividedRoadAttribute(); break;
                case AttributeType.Tunnel: a = new TunnelAttribute(); break;
                case AttributeType.TextureReference: a = new TextureReferenceAttribute(); break;
                case AttributeType.Facade: a = subtype == 6 ? new FacadeAttribute() : null; break;
                case AttributeType.RoofTriangleFan: a = new RoofTriangleFanAttribute(); break;
                default: a = null; break;
            }
            a ??= new RawAttribute(type, subtype);
            a.Initialize(subtype);
            a.Last = (id & 0x80) != 0;
            return a;
        }

        /// <summary>Called by <see cref="Create"/> so that freshly created attributes keep the requested subtype.</summary>
        protected virtual void Initialize(int subtype) { }

        public static string KnownAttributesText() {
            var lines = new List<string>();
            void Add(int lo, int hi, string text) {
                lines.Add(lo == hi ? $"{lo:x4} {text}" : $"{lo:x4}-{hi:x4} {text}");
            }
            Add(0x00, 0x07, "Road with sidewalks (4 vertices per cross section)");
            Add(0x08, 0x0f, "Sidewalk strip (2 vertices per cross section)");
            Add(0x10, 0x17, "Road without sidewalks (2 vertices per cross section)");
            Add(0x1c, 0x1c, "Sliver");
            Add(0x24, 0x24, "Crosswalk rectangle");
            Add(0x28, 0x2f, "Road triangle fan");
            Add(0x30, 0x37, "Triangle fan");
            Add(0x3c, 0x3c, "Facade bound");
            Add(0x40, 0x47, "Divided road (6 vertices per cross section)");
            Add(0x48, 0x48, "Junction tunnel/railing");
            Add(0x49, 0x4f, "Road tunnel/railing (0x4b is the common one)");
            Add(0x50, 0x57, "Texture reference (index = data + 256 * subtype - 1)");
            Add(0x5e, 0x5e, "Facade");
            Add(0x60, 0x67, "Roof triangle fan");
            lines.Add("");
            lines.Add("Subtype 0 means that the element count is stored in the following word.");
            lines.Add("Bit 0x80 marks the last attribute that the game renders.");
            return string.Join("\n", lines);
        }
    }

    /// <summary>Attribute whose size is encoded in the subtype, or in an extra count word when the subtype is 0.</summary>
    public abstract class CountedAttribute : RoomAttribute {
        public List<int> Vertices = new();
        /// <summary>True when the count is stored in an explicit word (subtype 0) instead of the subtype.</summary>
        public bool ExplicitCount;

        /// <summary>The value encoded in the subtype or count word.</summary>
        protected abstract int EncodedCount { get; }
        public override int Subtype => !ExplicitCount && EncodedCount >= 1 && EncodedCount <= 7 ? EncodedCount : 0;
        public override IEnumerable<int> VertexRefs => Vertices;

        protected override void Initialize(int subtype) {
            ExplicitCount = subtype == 0;
        }

        protected override void CloneLists() {
            Vertices = new List<int>(Vertices);
        }

        public override void AddOffsets(int vertexOffset, int heightOffset, int textureOffset) {
            for (int i = 0; i < Vertices.Count; i++) Vertices[i] += vertexOffset;
        }

        protected int ReadCount(BinaryReader r, int subtype, ref int words) {
            ExplicitCount = subtype == 0;
            if (subtype != 0) return subtype;
            words++;
            return r.ReadUInt16();
        }

        protected void WriteCount(BinaryWriter w) {
            if (Subtype == 0) w.Write((ushort)EncodedCount);
        }

        protected void ReadVertices(BinaryReader r, int count, ref int words) {
            Vertices.Clear();
            for (int i = 0; i < count; i++) Vertices.Add(r.ReadUInt16());
            words += count;
        }

        protected void WriteVertices(BinaryWriter w) {
            foreach (int v in Vertices) w.Write((ushort)v);
        }
    }

    /// <summary>Road with sidewalks (0x00), sidewalk strip (0x08) and road without sidewalks (0x10): cross sections of N vertices.</summary>
    public class StripAttribute : CountedAttribute {
        readonly int type;
        public readonly int VerticesPerSection;

        public StripAttribute(int type, int verticesPerSection) {
            this.type = type;
            VerticesPerSection = verticesPerSection;
        }

        public override int Type => type;
        public int Sections => Vertices.Count / VerticesPerSection;
        protected override int EncodedCount => Sections;
        public override int BodyWords => (Subtype == 0 ? 1 : 0) + Vertices.Count;

        public override int Read(BinaryReader r, int subtype, int remainingWords) {
            int words = 0;
            int n = ReadCount(r, subtype, ref words);
            ReadVertices(r, n * VerticesPerSection, ref words);
            return words;
        }

        protected override void WriteBody(BinaryWriter w) {
            WriteCount(w);
            WriteVertices(w);
        }

        public override string Validate() {
            if (Vertices.Count % VerticesPerSection != 0) return $"requires a multiple of {VerticesPerSection} vertices";
            if (Sections < 1) return "requires at least one cross section";
            return null;
        }

        public override string Describe() => $"{Name} ({Sections} cross sections)";
    }

    public class DividedRoadAttribute : CountedAttribute {
        public const int DividerInvisible = 0, DividerFlat = 1, DividerElevated = 2, DividerWedged = 3;
        public const int FlagUnknown0 = 4, FlagUnknown1 = 8, FlagUnknown2 = 16, FlagUnknown3 = 32, FlagCloseStart = 64, FlagCloseEnd = 128;
        public static readonly string[] DividerNames = { "Invisible", "Flat", "Elevated", "Wedged" };

        /// <summary>Low byte of the parameter word: bits 0-1 divider type, the rest flags.</summary>
        public int FlagsByte;
        /// <summary>Index in the texture list (stored in the file as index + 1).</summary>
        public int DividerTexture = -1;
        /// <summary>Raw divider value in units of 1/256; texture repeat for flat dividers, height for elevated ones.</summary>
        public int Value;

        public override int Type => AttributeType.DividedRoad;
        public int Sections => Vertices.Count / 6;
        protected override int EncodedCount => Sections;
        public override int BodyWords => (Subtype == 0 ? 1 : 0) + 2 + Vertices.Count;

        public int DividerType { get => FlagsByte & 3; set => FlagsByte = (FlagsByte & ~3) | (value & 3); }
        public bool GetFlag(int flag) => (FlagsByte & flag) != 0;
        public void SetFlag(int flag, bool on) => FlagsByte = on ? FlagsByte | flag : FlagsByte & ~flag;
        /// <summary>Value / 256: texture repeat count (flat) or height and side strip width in metres (elevated).</summary>
        public float ScaledValue { get => Value / 256f; set => Value = Math.Clamp((int)(value * 256f + 0.5f), 0, 0xffff); }

        public override int Read(BinaryReader r, int subtype, int remainingWords) {
            int words = 0;
            int n = ReadCount(r, subtype, ref words);
            FlagsByte = r.ReadByte();
            DividerTexture = r.ReadByte() - 1;
            Value = r.ReadUInt16();
            words += 2;
            ReadVertices(r, n * 6, ref words);
            return words;
        }

        protected override void WriteBody(BinaryWriter w) {
            WriteCount(w);
            w.Write((byte)FlagsByte);
            w.Write((byte)(DividerTexture + 1));
            w.Write((ushort)Value);
            WriteVertices(w);
        }

        public override void AddOffsets(int vertexOffset, int heightOffset, int textureOffset) {
            base.AddOffsets(vertexOffset, heightOffset, textureOffset);
            if (DividerTexture >= 0) DividerTexture += textureOffset;
        }

        public override string Validate() {
            if (Vertices.Count % 6 != 0) return "requires a multiple of 6 vertices";
            if (Sections < 1) return "requires at least one cross section";
            if (DividerTexture > 254) return "divider texture index must be below 255";
            return null;
        }

        public override string Describe() => $"{Name} ({Sections} cross sections, {DividerNames[DividerType]})";
    }

    /// <summary>Triangle fan (0x30) and road triangle fan (0x28). The count is the number of triangles.</summary>
    public class FanAttribute : CountedAttribute {
        readonly int type;

        public FanAttribute(int type) {
            this.type = type;
        }

        public override int Type => type;
        protected override int EncodedCount => Vertices.Count - 2;
        public override int BodyWords => (Subtype == 0 ? 1 : 0) + Vertices.Count;

        public override int Read(BinaryReader r, int subtype, int remainingWords) {
            int words = 0;
            int n = ReadCount(r, subtype, ref words);
            ReadVertices(r, n + 2, ref words);
            return words;
        }

        protected override void WriteBody(BinaryWriter w) {
            WriteCount(w);
            WriteVertices(w);
        }

        public override string Validate() => Vertices.Count < 3 ? "requires at least three vertices" : null;
        public override string Describe() => $"{Name} ({Vertices.Count} vertices)";
    }

    /// <summary>Roof triangle fan (0x60): a fan whose y coordinates are replaced by a height.</summary>
    public class RoofTriangleFanAttribute : CountedAttribute {
        public int HeightRef;

        public override int Type => AttributeType.RoofTriangleFan;
        protected override int EncodedCount => Vertices.Count - 1;
        public override int BodyWords => (Subtype == 0 ? 1 : 0) + 1 + Vertices.Count;

        public override int Read(BinaryReader r, int subtype, int remainingWords) {
            int words = 0;
            int n = ReadCount(r, subtype, ref words);
            HeightRef = r.ReadUInt16();
            words++;
            ReadVertices(r, n + 1, ref words);
            return words;
        }

        protected override void WriteBody(BinaryWriter w) {
            WriteCount(w);
            w.Write((ushort)HeightRef);
            WriteVertices(w);
        }

        public override void AddOffsets(int vertexOffset, int heightOffset, int textureOffset) {
            base.AddOffsets(vertexOffset, heightOffset, textureOffset);
            HeightRef += heightOffset;
        }

        public override string Validate() => Vertices.Count < 3 ? "requires at least three vertices" : null;
        public override string Describe() => $"{Name} ({Vertices.Count} vertices)";
    }

    public class CrosswalkRectangleAttribute : CountedAttribute {
        public CrosswalkRectangleAttribute() {
            Vertices.AddRange(new[] { 0, 0, 0, 0 });
        }

        public override int Type => AttributeType.CrosswalkRectangle;
        protected override int EncodedCount => Vertices.Count;
        public override int BodyWords => (Subtype == 0 ? 1 : 0) + Vertices.Count;

        public override int Read(BinaryReader r, int subtype, int remainingWords) {
            int words = 0;
            int n = ReadCount(r, subtype, ref words);
            ReadVertices(r, n, ref words);
            return words;
        }

        protected override void WriteBody(BinaryWriter w) {
            WriteCount(w);
            WriteVertices(w);
        }

        public override string Validate() => Vertices.Count != 4 ? "requires four vertices" : null;
    }

    /// <summary>Base for the fixed-size attributes that end with two vertex references.</summary>
    public abstract class TwoVertexAttribute : RoomAttribute {
        public List<int> Vertices = new() { 0, 0 };
        public override IEnumerable<int> VertexRefs => Vertices;

        protected override void CloneLists() {
            Vertices = new List<int>(Vertices);
        }

        public override void AddOffsets(int vertexOffset, int heightOffset, int textureOffset) {
            for (int i = 0; i < Vertices.Count; i++) Vertices[i] += vertexOffset;
        }

        protected void ReadVertices(BinaryReader r) {
            Vertices = new List<int> { r.ReadUInt16(), r.ReadUInt16() };
        }

        protected void WriteVertices(BinaryWriter w) {
            foreach (int v in Vertices) w.Write((ushort)v);
        }

        public override string Validate() => Vertices.Count != 2 ? "requires two vertices" : null;
    }

    public class SliverAttribute : TwoVertexAttribute {
        public int TopHeight;
        public int TextureScale;

        public override int Type => AttributeType.Sliver;
        public override int Subtype => 4;
        public override int BodyWords => 4;

        public override int Read(BinaryReader r, int subtype, int remainingWords) {
            TopHeight = r.ReadUInt16();
            TextureScale = r.ReadUInt16();
            ReadVertices(r);
            return 4;
        }

        protected override void WriteBody(BinaryWriter w) {
            w.Write((ushort)TopHeight);
            w.Write((ushort)TextureScale);
            WriteVertices(w);
        }

        public override void AddOffsets(int vertexOffset, int heightOffset, int textureOffset) {
            base.AddOffsets(vertexOffset, heightOffset, textureOffset);
            TopHeight += heightOffset;
            TextureScale += heightOffset;
        }
    }

    public class FacadeBoundAttribute : TwoVertexAttribute {
        /// <summary>Angle between the facade and the sunlight (0-255).</summary>
        public int Angle;
        public int TopHeight;

        public override int Type => AttributeType.FacadeBound;
        public override int Subtype => 4;
        public override int BodyWords => 4;

        public override int Read(BinaryReader r, int subtype, int remainingWords) {
            Angle = r.ReadUInt16();
            TopHeight = r.ReadUInt16();
            ReadVertices(r);
            return 4;
        }

        protected override void WriteBody(BinaryWriter w) {
            w.Write((ushort)Angle);
            w.Write((ushort)TopHeight);
            WriteVertices(w);
        }

        public override void AddOffsets(int vertexOffset, int heightOffset, int textureOffset) {
            base.AddOffsets(vertexOffset, heightOffset, textureOffset);
            TopHeight += heightOffset;
        }
    }

    public class FacadeAttribute : TwoVertexAttribute {
        public int BottomHeight;
        public int TopHeight;
        public int URepeat = 1;
        public int VRepeat = 1;

        public override int Type => AttributeType.Facade;
        public override int Subtype => 6;
        public override int BodyWords => 6;

        public override int Read(BinaryReader r, int subtype, int remainingWords) {
            BottomHeight = r.ReadUInt16();
            TopHeight = r.ReadUInt16();
            URepeat = r.ReadUInt16();
            VRepeat = r.ReadUInt16();
            ReadVertices(r);
            return 6;
        }

        protected override void WriteBody(BinaryWriter w) {
            w.Write((ushort)BottomHeight);
            w.Write((ushort)TopHeight);
            w.Write((ushort)URepeat);
            w.Write((ushort)VRepeat);
            WriteVertices(w);
        }

        public override void AddOffsets(int vertexOffset, int heightOffset, int textureOffset) {
            base.AddOffsets(vertexOffset, heightOffset, textureOffset);
            BottomHeight += heightOffset;
            TopHeight += heightOffset;
        }
    }

    /// <summary>Texture reference: index = data + 256 * subtype - 1. Index -1 prevents rendering and collision of the following geometry.</summary>
    public class TextureReferenceAttribute : RoomAttribute {
        public int Index = -1;

        public override int Type => AttributeType.TextureReference;
        public override int Subtype => Math.Clamp((Index + 1) >> 8, 0, 7);
        public override int BodyWords => 1;

        public override int Read(BinaryReader r, int subtype, int remainingWords) {
            int data = r.ReadUInt16();
            Index = data + 256 * subtype - 1;
            return 1;
        }

        protected override void WriteBody(BinaryWriter w) {
            w.Write((ushort)((Index + 1) - 256 * Subtype));
        }

        public override void AddOffsets(int vertexOffset, int heightOffset, int textureOffset) {
            if (Index >= 0) Index += textureOffset;
        }

        public override string Validate() => Index < -1 || Index + 1 >= 8 * 256 ? "texture index out of range" : null;
        public override string Describe() => Index < 0 ? "Texture reference: none" : $"Texture reference: {Index:x}";
    }

    /// <summary>
    /// Road tunnel/railing (subtype = number of data words) or junction tunnel/railing (subtype 0, an explicit size word follows the id).
    /// </summary>
    public class TunnelAttribute : RoomAttribute {
        /// <summary>Flag bits. StyleWall is the "style" bit: railing when clear, wall when set.</summary>
        public const int LeftSide = 1, RightSide = 2, StyleWall = 4, FlatCeiling = 8;
        public const int ClosedStartLeft = 16, ClosedEndLeft = 32, ClosedStartRight = 64, ClosedEndRight = 128;
        public const int CurvedCeiling = 256, OffsetStartLeft = 512, OffsetEndLeft = 1024, OffsetStartRight = 2048, OffsetEndRight = 4096;
        public const int CurvedSides = 8192, Culled = 16384, Unknown = 32768;

        public bool Junction;
        /// <summary>Data words after the id (and after the size word for junctions).</summary>
        public List<int> Words = new() { 0, 0, 0 };

        public override int Type => AttributeType.Tunnel;
        public override int Subtype => Junction ? 0 : Math.Clamp(Words.Count, 1, 7);
        public override int BodyWords => (Junction ? 1 : 0) + Words.Count;
        public override string Name => Junction ? "Junction tunnel/railing" : "Road tunnel/railing";

        protected override void Initialize(int subtype) {
            Junction = subtype == 0;
            if (Junction) Words = new List<int>(new int[10]);
            else Words = new List<int>(new int[Math.Max(subtype, 1)]);
        }

        protected override void CloneLists() {
            Words = new List<int>(Words);
        }

        int Word(int i) => i < Words.Count ? Words[i] : 0;

        void SetWord(int i, int value) {
            while (Words.Count <= i) Words.Add(0);
            Words[i] = value & 0xffff;
        }

        public int Flags { get => Word(0); set => SetWord(0, value); }
        public bool GetFlag(int flag) => (Flags & flag) != 0;
        public void SetFlag(int flag, bool on) => Flags = on ? Flags | flag : Flags & ~flag;
        /// <summary>Height in metres * 256.</summary>
        public int RawHeight { get => Word(1); set => SetWord(1, value); }
        public float Height { get => RawHeight / 256f; set => RawHeight = Math.Clamp((int)(value * 256f + 0.5f), 0, 0xffff); }
        /// <summary>Second unknown value (missing in subtype 2 road tunnels).</summary>
        public int UnknownValue { get => Word(2); set => SetWord(2, value); }
        /// <summary>Junction only: perimeter index where the ceiling fan starts.</summary>
        public int CeilingFanOrigin { get => Word(3); set => SetWord(3, value); }

        uint Long(int firstWord) => (uint)(Word(firstWord) | (Word(firstWord + 1) << 16));

        void SetLong(int firstWord, uint value) {
            SetWord(firstWord, (int)(value & 0xffff));
            SetWord(firstWord + 1, (int)(value >> 16));
        }

        public uint WallVisibility { get => Long(4); set => SetLong(4, value); }
        public uint WallDirectionA { get => Long(6); set => SetLong(6, value); }
        public uint WallDirectionB { get => Long(8); set => SetLong(8, value); }

        public override int Read(BinaryReader r, int subtype, int remainingWords) {
            Junction = subtype == 0;
            int words = 0;
            int n = subtype;
            if (Junction) {
                n = r.ReadUInt16();
                words++;
            }
            Words = new List<int>();
            for (int i = 0; i < n; i++) Words.Add(r.ReadUInt16());
            return words + n;
        }

        protected override void WriteBody(BinaryWriter w) {
            if (Junction) w.Write((ushort)Words.Count);
            foreach (int v in Words) w.Write((ushort)v);
        }

        public override string Validate() => !Junction && (Words.Count < 1 || Words.Count > 7) ? "road tunnels need 1-7 data words" : null;
    }

    /// <summary>Attribute of an unknown type; keeps the raw words (Java viewer behaviour: consumes the rest of the list).</summary>
    public class RawAttribute : RoomAttribute {
        readonly int type;
        int subtype;
        public List<int> Words = new();

        public RawAttribute(int type, int subtype) {
            this.type = type;
            this.subtype = subtype;
        }

        public override int Type => type;
        public override int Subtype => subtype;
        public override int BodyWords => Words.Count;

        protected override void CloneLists() {
            Words = new List<int>(Words);
        }

        public override int Read(BinaryReader r, int subtype, int remainingWords) {
            this.subtype = subtype;
            Words = new List<int>();
            for (int i = 0; i < remainingWords; i++) Words.Add(r.ReadUInt16());
            return remainingWords;
        }

        protected override void WriteBody(BinaryWriter w) {
            foreach (int v in Words) w.Write((ushort)v);
        }

        public override string Describe() => $"Unknown attribute ({Words.Count} words)";
    }
}
