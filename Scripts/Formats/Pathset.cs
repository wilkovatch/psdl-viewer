using System.Collections.Generic;
using System.IO;
using System.Numerics;

namespace PsdlViewer.Formats {
    public class PathPoint {
        /// <summary>Upper 8 bits unknown, lower 24 bits room id; not used by the game.</summary>
        public uint Misc;
        public Vector3 Position;

        public PathPoint Clone() => (PathPoint)MemberwiseClone();
    }

    public class PropPath {
        public const int TypePoints = 0, TypeDirectedPoints = 1, TypeLineStrip = 2;
        /// <summary>Spacing used by the game when the stored spacing is 0.</summary>
        public const float DefaultSpacing = 5;
        public static readonly string[] TypeNames = { "Points", "Directed points", "Line strip" };

        public string Name = "";
        /// <summary>Name bytes as read, written back unchanged while the name is not edited.</summary>
        public byte[] RawName;
        /// <summary>Possibly flags, meaning unknown.</summary>
        public uint Unknown1;
        public List<PathPoint> Points = new();
        public int Type;
        /// <summary>Spacing in units of 1/4 metre; 0 means the default of 5 metres.</summary>
        public int RawSpacing;
        /// <summary>Padding after the spacing.</summary>
        public int Unknown3;

        public float Spacing { get => RawSpacing / 4f; set => RawSpacing = System.Math.Clamp((int)(value * 4 + 0.5f), 0, 255); }
        public override string ToString() => Name;
    }

    /// <summary>Prop/decal placement file ("PTH1").</summary>
    public class Pathset {
        public string FileName = "";
        public uint CurrentPath;
        public List<PropPath> Paths = new();

        public string DisplayName => Path.GetFileName(FileName);

        public static Pathset Load(string path) {
            using var stream = File.OpenRead(path);
            using var r = new BinaryReader(stream);
            FileLog.Print($"Reading pathset file {path}");
            if (r.ReadMagic() != "PTH1") throw new InvalidDataException("Not a pathset file.");
            var set = new Pathset { FileName = path };
            uint n = r.ReadUInt32();
            FileLog.Print($"{n} paths");
            set.CurrentPath = r.ReadUInt32();
            for (uint i = 0; i < n; i++) {
                byte[] raw = r.ReadBytes(32);
                int end = System.Array.IndexOf(raw, (byte)0);
                var p = new PropPath { RawName = raw, Name = BinaryUtil.Text.GetString(raw, 0, end < 0 ? raw.Length : end).Trim() };
                uint points = r.ReadUInt32();
                p.Unknown1 = r.ReadUInt32();
                for (uint k = 0; k < points; k++) {
                    p.Points.Add(new PathPoint { Misc = r.ReadUInt32(), Position = r.ReadVec3() });
                }
                p.Type = r.ReadByte();
                p.RawSpacing = r.ReadByte();
                p.Unknown3 = r.ReadUInt16();
                set.Paths.Add(p);
            }
            return set;
        }

        static string NameOf(byte[] raw) {
            int end = System.Array.IndexOf(raw, (byte)0);
            return BinaryUtil.Text.GetString(raw, 0, end < 0 ? raw.Length : end).Trim();
        }

        public void Save(string path) {
            string temp = path + "#";
            using (var stream = File.Create(temp))
            using (var w = new BinaryWriter(stream)) {
                w.Write(BinaryUtil.Text.GetBytes("PTH1"));
                w.Write((uint)Paths.Count);
                w.Write(CurrentPath);
                foreach (var p in Paths) {
                    if (p.RawName != null && p.RawName.Length == 32 && NameOf(p.RawName) == p.Name) w.Write(p.RawName);
                    else w.WriteFixedString(p.Name, 32);
                    w.Write((uint)p.Points.Count);
                    w.Write(p.Unknown1);
                    foreach (var pt in p.Points) {
                        w.Write(pt.Misc);
                        w.Write(pt.Position);
                    }
                    w.Write((byte)p.Type);
                    w.Write((byte)p.RawSpacing);
                    w.Write((ushort)p.Unknown3);
                }
            }
            File.Move(temp, path, true);
            FileName = path;
        }
    }
}
