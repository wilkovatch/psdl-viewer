using System;
using System.IO;
using System.Numerics;
using System.Text;

namespace PsdlViewer.Formats {
    /// <summary>Little-endian helpers shared by all Angel Studios file formats.</summary>
    public static class BinaryUtil {
        public static readonly Encoding Text = Encoding.Latin1;

        public static Vector3 ReadVec3(this BinaryReader r) {
            float x = r.ReadSingle();
            float y = r.ReadSingle();
            float z = r.ReadSingle();
            return new Vector3(x, y, z);
        }

        public static void Write(this BinaryWriter w, Vector3 v) {
            w.Write(v.X);
            w.Write(v.Y);
            w.Write(v.Z);
        }

        /// <summary>Reads a string prefixed by a length byte that includes the zero terminator.</summary>
        public static string ReadLengthString(this BinaryReader r) {
            int n = r.ReadByte();
            if (n == 0) return null;
            byte[] bytes = r.ReadBytes(n);
            int end = Array.IndexOf(bytes, (byte)0);
            if (end < 0) end = bytes.Length;
            return Text.GetString(bytes, 0, end);
        }

        public static void WriteLengthString(this BinaryWriter w, string s) {
            if (s == null) {
                w.Write((byte)0);
                return;
            }
            byte[] bytes = Text.GetBytes(s);
            w.Write((byte)(bytes.Length + 1));
            w.Write(bytes);
            w.Write((byte)0);
        }

        public static string ReadFixedString(this BinaryReader r, int length) {
            byte[] bytes = r.ReadBytes(length);
            int end = Array.IndexOf(bytes, (byte)0);
            if (end < 0) end = bytes.Length;
            return Text.GetString(bytes, 0, end).Trim();
        }

        public static void WriteFixedString(this BinaryWriter w, string s, int length) {
            byte[] buf = new byte[length];
            if (s != null) {
                byte[] bytes = Text.GetBytes(s);
                Array.Copy(bytes, buf, Math.Min(bytes.Length, length - 1));
            }
            w.Write(buf);
        }

        public static string ReadMagic(this BinaryReader r) {
            return Text.GetString(r.ReadBytes(4));
        }

        public static string Hex(int v, int digits = 4) {
            return v.ToString("x" + digits);
        }

        public static bool TryParseHex(string s, out int value) {
            s = s?.Trim() ?? "";
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
            return int.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out value);
        }

        public static string F(float v) {
            return v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        }

        public static bool TryParseFloat(string s, out float value) {
            return float.TryParse(s?.Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out value);
        }

        public static string V(Vector3 v) {
            return F(v.X) + ", " + F(v.Y) + ", " + F(v.Z);
        }

        public static bool TryParseVec3(string s, out Vector3 v) {
            v = default;
            string[] parts = (s ?? "").Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3) return false;
            if (!TryParseFloat(parts[0], out float x) || !TryParseFloat(parts[1], out float y) || !TryParseFloat(parts[2], out float z)) return false;
            v = new Vector3(x, y, z);
            return true;
        }
    }

    /// <summary>Thread-safe progress shared between background loaders and the UI.</summary>
    public class Progress {
        volatile string note = "";
        long value;
        long maximum = 1;

        public string Note { get => note; set => note = value ?? ""; }
        public long Value { get => System.Threading.Interlocked.Read(ref value); set => System.Threading.Interlocked.Exchange(ref this.value, value); }
        public long Maximum { get => System.Threading.Interlocked.Read(ref maximum); set => System.Threading.Interlocked.Exchange(ref maximum, Math.Max(1, value)); }
        public float Fraction => Math.Clamp((float)Value / Maximum, 0, 1);

        public void Begin(string note, long max) {
            Note = note;
            Maximum = max;
            Value = 0;
        }
    }
}
