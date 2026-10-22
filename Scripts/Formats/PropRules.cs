using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PsdlViewer.Formats {
    /// <summary>Line of propdefs.csv: a roadside prop repeated along the sidewalk of a road.</summary>
    public class PropDef {
        public string Name = "";
        /// <summary>Distance of the first prop from the start of the road, in metres.</summary>
        public float StartDist;
        /// <summary>Distance between two props, in metres.</summary>
        public float Spacing;
        /// <summary>Maximum number of props placed along one room path.</summary>
        public int MaxUse;
        /// <summary>Range of the random position across the sidewalk: 0 = road edge, 1 = outer edge.</summary>
        public float MinLerp, MaxLerp;
        /// <summary>PKG names, one picked at random for each prop.</summary>
        public List<string> Models = new();

        public override string ToString() => Name;
    }

    /// <summary>Line of proprules.csv: the prop definitions placed along one side of a road.</summary>
    public class PropRule {
        public string Name = "";
        public List<PropDef> Defs = new();

        public override string ToString() => Name;
    }

    /// <summary>
    /// The propdefs.csv and proprules.csv files of a city folder (city/sf for sf.psdl). The prop rule n of a PSDL room
    /// selects the rules 2(n - 1) (left side of the road) and 2(n - 1) + 1 (right side). Also holds the traffic light models,
    /// from the [Traffic Lights] section of the city's aimap file (city/sf.aimap).
    /// </summary>
    public class PropRuleSet {
        public const string DefsFile = "propdefs.csv", RulesFile = "proprules.csv";

        public string Folder = "";
        public List<PropDef> Defs = new();
        public List<PropRule> Rules = new();
        /// <summary>Traffic light for roads with one lane towards the intersection, and for wider roads.</summary>
        public string SingleTrafficLight = "sp_traflitsingle_f", DualTrafficLight = "sp_traflitdual_f";

        public bool IsEmpty => Rules.Count == 0;

        /// <summary>Whether the folder contains prop rule files.</summary>
        public static bool Exists(string folder) =>
            File.Exists(Path.Combine(folder, DefsFile)) || File.Exists(Path.Combine(folder, RulesFile));

        /// <summary>Reads the prop rule files of the folder and the traffic lights of its aimap file, those that exist.</summary>
        public static PropRuleSet Load(string folder) {
            string defsPath = Path.Combine(folder, DefsFile), rulesPath = Path.Combine(folder, RulesFile);
            var set = new PropRuleSet { Folder = folder };
            set.ReadTrafficLights(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + ".aimap");
            if (File.Exists(defsPath)) {
                FileLog.Print($"Reading prop definitions {defsPath}");
                foreach (var cells in ReadLines(defsPath)) {
                    var def = new PropDef { Name = cells[0] };
                    def.StartDist = Float(cells, 1);
                    def.Spacing = Float(cells, 2);
                    def.MaxUse = (int)Float(cells, 3);
                    def.MinLerp = Float(cells, 4);
                    def.MaxLerp = Float(cells, 5);
                    def.Models.AddRange(cells.Skip(6).Where(c => c != ""));
                    set.Defs.Add(def);
                }
                FileLog.Print($"{set.Defs.Count} prop definitions");
            }
            if (File.Exists(rulesPath)) {
                FileLog.Print($"Reading prop rules {rulesPath}");
                var byName = new Dictionary<string, PropDef>(StringComparer.OrdinalIgnoreCase);
                foreach (var def in set.Defs) byName.TryAdd(def.Name, def);
                foreach (var cells in ReadLines(rulesPath)) {
                    var rule = new PropRule { Name = cells[0] };
                    foreach (string name in cells.Skip(1).Where(c => c != "")) {
                        if (byName.TryGetValue(name, out var def)) rule.Defs.Add(def);
                        else FileLog.Print($"Prop rule {rule.Name}: unknown prop definition \"{name}\"");
                    }
                    set.Rules.Add(rule);
                }
                FileLog.Print($"{set.Rules.Count} prop rules");
            }
            return set;
        }

        /// <summary>The line after "[Traffic Lights]" names the single and dual traffic lights (the single one for both if alone).</summary>
        void ReadTrafficLights(string aimapPath) {
            if (!File.Exists(aimapPath)) return;
            bool section = false;
            foreach (string line in File.ReadLines(aimapPath, BinaryUtil.Text)) {
                string text = line.Trim();
                if (text == "" || text.StartsWith("#")) continue;
                if (!section) {
                    section = text.Equals("[Traffic Lights]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                string[] names = text.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                SingleTrafficLight = names[0];
                DualTrafficLight = names.Length > 1 ? names[1] : names[0];
                FileLog.Print($"Traffic lights from {aimapPath}: {SingleTrafficLight}, {DualTrafficLight}");
                return;
            }
        }

        /// <summary>The left and right side rules of a room's prop rule, or null for 0 and missing rules.</summary>
        public (PropRule left, PropRule right)? ForRoom(int propRule) {
            int left = (propRule - 1) * 2;
            if (propRule <= 0 || left + 1 >= Rules.Count) return null;
            return (Rules[left], Rules[left + 1]);
        }

        /// <summary>The trimmed cells of each line after the header, skipping blank lines.</summary>
        static IEnumerable<string[]> ReadLines(string path) {
            bool header = true;
            foreach (string line in File.ReadLines(path, BinaryUtil.Text)) {
                if (line.Trim() == "") continue;
                if (header) {
                    header = false;
                    continue;
                }
                string[] cells = line.Split(',').Select(c => c.Trim()).ToArray();
                if (cells[0] != "") yield return cells;
            }
        }

        static float Float(string[] cells, int index) =>
            index < cells.Length && BinaryUtil.TryParseFloat(cells[index], out float v) ? v : 0;
    }
}
