using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

namespace PsdlViewer.Formats {
    public abstract class InstComponent {
        /// <summary>Index of the block the object is placed in (stored +1 in the file).</summary>
        public int Block;
        /// <summary>Paint job in the low bits, other bits unknown.</summary>
        public int Modifiers;
        public string Pkg = "";

        public abstract bool IsSimple { get; }
        public abstract Vector3 Origin { get; set; }
        /// <summary>Columns of the object to world matrix (x, y and z axis).</summary>
        public abstract (Vector3 x, Vector3 y, Vector3 z) Axes { get; }
        public int PaintJob => Modifiers & 0x7f;

        public InstComponent Clone() => (InstComponent)MemberwiseClone();
        public override string ToString() => Pkg;
    }

    /// <summary>Simple component: position plus a direction vector in the xz plane whose length is the scale.</summary>
    public class InstSimple : InstComponent {
        public float DeltaX = 1;
        /// <summary>The z component exactly as stored in the file.</summary>
        public float DeltaZ;
        Vector3 origin;

        public override bool IsSimple => true;
        public override Vector3 Origin { get => origin; set => origin = value; }

        public float Scale => MathF.Sqrt(DeltaX * DeltaX + DeltaZ * DeltaZ);
        /// <summary>Angle in radians as shown by the original viewer.</summary>
        public float Angle => MathF.Atan2(-DeltaZ, DeltaX);

        public void SetOrientationScale(float scale, float angle) {
            DeltaX = MathF.Cos(angle) * scale;
            DeltaZ = -MathF.Sin(angle) * scale;
        }

        public override (Vector3 x, Vector3 y, Vector3 z) Axes =>
            (new Vector3(DeltaX, 0, DeltaZ), Vector3.UnitY, new Vector3(-DeltaZ, 0, DeltaX));
    }

    /// <summary>Coordinate system component: full 3x3 matrix plus origin.</summary>
    public class InstCoordinate : InstComponent {
        public Vector3 XAxis = Vector3.UnitX;
        public Vector3 YAxis = Vector3.UnitY;
        public Vector3 ZAxis = Vector3.UnitZ;
        Vector3 origin;

        public override bool IsSimple => false;
        public override Vector3 Origin { get => origin; set => origin = value; }
        public override (Vector3 x, Vector3 y, Vector3 z) Axes => (XAxis, YAxis, ZAxis);
    }

    public class InstFile {
        public List<InstComponent> Components = new();

        public static InstFile Load(string path, Progress progress = null) {
            var inst = new InstFile();
            using var stream = File.OpenRead(path);
            using var r = new BinaryReader(stream);
            progress?.Begin("Reading components", stream.Length);
            while (stream.Position < stream.Length) {
                int block = r.ReadUInt16() - 1;
                int modifiers = r.ReadUInt16();
                int type = r.ReadByte();
                int length = type & 0x7f;
                string name = "";
                if (length != 0) {
                    byte[] bytes = r.ReadBytes(length);
                    int end = Array.IndexOf(bytes, (byte)0);
                    name = BinaryUtil.Text.GetString(bytes, 0, end < 0 ? bytes.Length : end);
                }
                InstComponent c;
                if ((type & 0x80) != 0) {
                    var s = new InstSimple { DeltaX = r.ReadSingle(), DeltaZ = r.ReadSingle() };
                    s.Origin = r.ReadVec3();
                    c = s;
                } else {
                    var cs = new InstCoordinate {
                        XAxis = r.ReadVec3(),
                        YAxis = r.ReadVec3(),
                        ZAxis = r.ReadVec3()
                    };
                    cs.Origin = r.ReadVec3();
                    c = cs;
                }
                c.Block = block;
                c.Modifiers = modifiers;
                c.Pkg = name;
                inst.Components.Add(c);
                if (progress != null) progress.Value = stream.Position;
            }
            return inst;
        }

        public void Save(string path) {
            string temp = path + "#";
            using (var stream = File.Create(temp))
            using (var w = new BinaryWriter(stream)) {
                foreach (var c in Components) {
                    w.Write((ushort)(c.Block + 1));
                    w.Write((ushort)c.Modifiers);
                    byte[] name = BinaryUtil.Text.GetBytes(c.Pkg ?? "");
                    w.Write((byte)(((name.Length + 1) & 0x7f) | (c.IsSimple ? 0x80 : 0)));
                    w.Write(name);
                    w.Write((byte)0);
                    if (c is InstSimple s) {
                        w.Write(s.DeltaX);
                        w.Write(s.DeltaZ);
                        w.Write(s.Origin);
                    } else if (c is InstCoordinate cs) {
                        w.Write(cs.XAxis);
                        w.Write(cs.YAxis);
                        w.Write(cs.ZAxis);
                        w.Write(cs.Origin);
                    }
                }
            }
            File.Move(temp, path, true);
        }

        /// <summary>Keeps block references valid after the PSDL block list changed. Removed blocks drop their objects.</summary>
        public void RemapBlocks(Func<int, int> map) {
            for (int i = Components.Count - 1; i >= 0; i--) {
                int m = map(Components[i].Block);
                if (m < 0) Components.RemoveAt(i);
                else Components[i].Block = m;
            }
        }
    }
}
