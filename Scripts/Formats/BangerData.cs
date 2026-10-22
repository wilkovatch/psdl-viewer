using System.IO;
using System.Numerics;

namespace PsdlViewer.Formats {
    /// <summary>
    /// Physics data of a banger (breakable prop) from tune/banger/&lt;pkg&gt;.dgBangerData, a text file of "Tag values" lines inside
    /// a "dgBangerData { }" block. Only the fields that affect how the prop is drawn are kept; missing tags keep the values of
    /// the defaults (default.dgBangerData).
    /// </summary>
    public class BangerData {
        public const string Folder = "banger", Extension = ".dgBangerData", DefaultName = "default";

        /// <summary>Centre of gravity: the banger's meshes are centred on it, so the prop is drawn this far from its position.</summary>
        public Vector3 CG;

        public BangerData Clone() => (BangerData)MemberwiseClone();

        public static BangerData Load(string path, BangerData defaults) {
            var data = defaults?.Clone() ?? new BangerData();
            int depth = 0;
            foreach (string line in File.ReadLines(path, BinaryUtil.Text)) {
                string[] t = line.Split(new[] { ' ', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
                if (t.Length == 0) continue;
                if (t[^1] == "{") {
                    depth++;
                    continue;
                }
                if (t[0] == "}") {
                    depth--;
                    continue;
                }
                // tags of nested blocks (BirthRule) are not banger fields
                if (depth == 1 && t[0] == "CG" && t.Length >= 4 && BinaryUtil.TryParseVec3($"{t[1]} {t[2]} {t[3]}", out var cg)) data.CG = cg;
            }
            return data;
        }
    }
}
