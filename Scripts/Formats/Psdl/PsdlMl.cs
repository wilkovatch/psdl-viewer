using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using System.Xml;

namespace PsdlViewer.Formats.Psdl {
    /// <summary>
    /// Importer for "psdl-ml" XML city descriptions, plus the plain text dump the original viewer could write.
    /// Vertices and heights within the weld range are merged.
    /// </summary>
    public class PsdlMl {
        readonly PsdlFile psdl = new();
        readonly float weldRange;
        const bool WeldTextures = true;

        PsdlMl(float weldRange) {
            this.weldRange = weldRange;
        }

        public static PsdlFile Import(string path, float weldRange, Progress progress = null) {
            var ml = new PsdlMl(weldRange);
            var doc = new XmlDocument();
            doc.Load(path);
            var root = doc.DocumentElement;
            if (root == null || root.Name != "psdl-ml") throw new InvalidDataException("Not a psdl-ml file.");
            var nodes = root.ChildNodes;
            progress?.Begin("Reading PSDL-ML", nodes.Count);
            for (int i = 0; i < nodes.Count; i++) {
                if (nodes[i] is not XmlElement e) continue;
                switch (e.Name) {
                    case "target_size": ml.psdl.TargetSize = uint.Parse(e.GetAttribute("target_size")); break;
                    case "vertex": ml.AddVertex(e, false); break;
                    case "height": ml.AddHeight(e, false); break;
                    case "texture": ml.AddTexture(e); break;
                    case "block": ml.AddBlock(e); break;
                }
                if (progress != null) progress.Value = i;
            }
            var p = ml.psdl;
            if (p.Vertices.Count > 0) {
                var min = new Vector3(float.MaxValue);
                var max = new Vector3(float.MinValue);
                foreach (var v in p.Vertices) {
                    min = Vector3.Min(min, v);
                    max = Vector3.Max(max, v);
                }
                p.Dimensions = new Dimensions { Min = min, Max = max, Center = (min + max) / 2, Radius = (max - (min + max) / 2).Length() };
            }
            return p;
        }

        static float F(XmlElement e, string name) => float.Parse(e.GetAttribute(name), CultureInfo.InvariantCulture);

        int AddVertex(XmlElement e, bool weld) {
            string index = e.GetAttribute("index");
            if (index.Length != 0) return int.Parse(index);
            var v = new Vector3(F(e, "x"), F(e, "y"), F(e, "z"));
            if (weld) {
                for (int i = 0; i < psdl.Vertices.Count; i++) {
                    if (Vector3.Distance(v, psdl.Vertices[i]) <= weldRange) return i;
                }
            }
            psdl.Vertices.Add(v);
            return psdl.Vertices.Count - 1;
        }

        int AddHeight(XmlElement e, bool weld) {
            string index = e.GetAttribute("index");
            if (index.Length != 0) return int.Parse(index);
            return AddHeight(F(e, "value"), weld);
        }

        int AddHeight(float h, bool weld) {
            if (weld) {
                for (int i = 0; i < psdl.Heights.Count; i++) {
                    if (Math.Abs(psdl.Heights[i] - h) <= weldRange) return i;
                }
            }
            psdl.Heights.Add(h);
            return psdl.Heights.Count - 1;
        }

        int AddTexture(XmlElement e) {
            string index = e.GetAttribute("index");
            if (index.Length != 0) return int.Parse(index);
            psdl.Textures.Add(e.GetAttribute("name"));
            return psdl.Textures.Count - 1;
        }

        void AddBlock(XmlElement e) {
            var block = new Block();
            foreach (XmlNode child in e.ChildNodes) {
                if (child is not XmlElement c) continue;
                if (c.Name == "perimeter") {
                    XmlElement vertex = null;
                    foreach (XmlNode n in c.ChildNodes) {
                        if (n is XmlElement v && v.Name == "vertex") vertex = v;
                    }
                    int connected = -1;
                    string s = c.GetAttribute("block");
                    if (s.Length != 0) connected = int.Parse(s);
                    if (vertex != null) block.Perimeter.Add(new PerimeterPoint(AddVertex(vertex, true), connected));
                } else if (c.Name == "attributes") {
                    foreach (XmlNode n in c.ChildNodes) {
                        if (n is XmlElement a) AddAttribute(a, block);
                    }
                }
            }
            psdl.Blocks.Add(block);
        }

        void ReadVertices(XmlElement e, List<int> target) {
            target.Clear();
            foreach (XmlNode n in e.ChildNodes) {
                if (n is XmlElement v && v.Name == "vertex") target.Add(AddVertex(v, true));
            }
        }

        static bool Yes(XmlElement e, string name) => e.GetAttribute(name) == "yes";

        void AddAttribute(XmlElement e, Block block) {
            string name = e.Name;
            if (!BinaryUtil.TryParseHex(name.Substring(name.LastIndexOf('_') + 1), out int id)) return;
            var attribute = BlockAttribute.Create(id);
            string s;
            switch (attribute) {
                case TextureAttribute t:
                    t.Index = ReadMaterials(e);
                    break;
                case DividedRoadAttribute d:
                    if ((s = e.GetAttribute("type")).Length != 0) d.DividerType = int.Parse(s);
                    if ((s = e.GetAttribute("value")).Length != 0) d.Height = float.Parse(s, CultureInfo.InvariantCulture);
                    d.SetFlag(DividedRoadAttribute.FlagCloseStart, Yes(e, "closestart"));
                    d.SetFlag(DividedRoadAttribute.FlagCloseEnd, Yes(e, "closeend"));
                    foreach (XmlNode n in e.ChildNodes) {
                        if (n is XmlElement m && m.Name == "attribute_50") d.DividerTexture = ReadMaterials(m);
                    }
                    ReadVertices(e, d.Vertices);
                    break;
                case RoofAttribute roof:
                    if ((s = e.GetAttribute("height")).Length != 0) roof.HeightRef = AddHeight(float.Parse(s, CultureInfo.InvariantCulture), true);
                    ReadVertices(e, roof.Vertices);
                    break;
                case CountedAttribute counted:
                    ReadVertices(e, counted.Vertices);
                    break;
                case SliverAttribute sliver:
                    if ((s = e.GetAttribute("topheight")).Length != 0) sliver.TopHeight = AddHeight(float.Parse(s, CultureInfo.InvariantCulture), true);
                    if ((s = e.GetAttribute("textureScale")).Length != 0) sliver.TextureScale = AddHeight(float.Parse(s, CultureInfo.InvariantCulture), true);
                    ReadVertices(e, sliver.Vertices);
                    break;
                case FacadeBoundAttribute bound:
                    if ((s = e.GetAttribute("shade")).Length != 0) bound.Angle = int.Parse(s);
                    if ((s = e.GetAttribute("height")).Length != 0) bound.TopHeight = AddHeight(float.Parse(s, CultureInfo.InvariantCulture), true);
                    ReadVertices(e, bound.Vertices);
                    break;
                case FacadeAttribute facade:
                    if ((s = e.GetAttribute("xRepeat")).Length != 0) facade.URepeat = int.Parse(s);
                    if ((s = e.GetAttribute("yRepeat")).Length != 0) facade.VRepeat = int.Parse(s);
                    if ((s = e.GetAttribute("bottomheight")).Length != 0) facade.BottomHeight = AddHeight(float.Parse(s, CultureInfo.InvariantCulture), true);
                    if ((s = e.GetAttribute("topheight")).Length != 0) facade.TopHeight = AddHeight(float.Parse(s, CultureInfo.InvariantCulture), true);
                    ReadVertices(e, facade.Vertices);
                    break;
                case TunnelAttribute tunnel:
                    tunnel.SetFlag(TunnelAttribute.LeftSide, Yes(e, "left"));
                    tunnel.SetFlag(TunnelAttribute.RightSide, Yes(e, "right"));
                    tunnel.SetFlag(TunnelAttribute.Wall, Yes(e, "wall"));
                    if (Yes(e, "ceiling")) tunnel.SetFlag(Yes(e, "subdividedceiling") ? TunnelAttribute.CurvedCeiling : TunnelAttribute.FlatCeiling, true);
                    tunnel.SetFlag(TunnelAttribute.CurvedSides, Yes(e, "subdividedwall"));
                    tunnel.SetFlag(TunnelAttribute.Culled, Yes(e, "thick"));
                    if ((s = e.GetAttribute("rightheight")).Length != 0) tunnel.Height = float.Parse(s, CultureInfo.InvariantCulture);
                    else if ((s = e.GetAttribute("leftheight")).Length != 0) tunnel.Height = float.Parse(s, CultureInfo.InvariantCulture);
                    break;
                default:
                    return;
            }
            block.Attributes.Add(attribute);
        }

        /// <summary>Reads a list of texture names and returns the index of the first one, reusing an identical run if present.</summary>
        int ReadMaterials(XmlElement e) {
            var names = new List<string>();
            foreach (XmlNode n in e.ChildNodes) {
                if (n is XmlElement t && t.Name == "texture") {
                    string name = t.GetAttribute("name");
                    if (name.Length != 0) names.Add(name);
                }
            }
            if (names.Count == 0) return -1;
            if (WeldTextures) {
                for (int i = 0; i + names.Count <= psdl.Textures.Count; i++) {
                    bool match = true;
                    for (int k = 0; k < names.Count && match; k++) match = names[k] == psdl.Textures[i + k];
                    if (match) return i;
                }
            }
            int first = psdl.Textures.Count;
            psdl.Textures.AddRange(names);
            return first;
        }

        /// <summary>The plain text dump written by the original viewer's (unfinished) PSDL-ML export.</summary>
        public static void ExportText(PsdlFile psdl, string path) {
            var sb = new StringBuilder();
            var ci = CultureInfo.InvariantCulture;
            foreach (var v in psdl.Vertices) sb.Append("v ").Append(v.X.ToString(ci)).Append(' ').Append(v.Y.ToString(ci)).Append(' ').Append(v.Z.ToString(ci)).Append('\n');
            sb.Append('\n');
            foreach (string t in psdl.Textures) sb.Append("td ").Append(string.IsNullOrEmpty(t) ? "---notexture---" : t).Append('\n');
            sb.Append("\nrooms 1 1 ").Append(psdl.Blocks.Count - 1).Append('\n');
            for (int i = 0; i < psdl.Blocks.Count; i++) {
                var block = psdl.Blocks[i];
                sb.Append("\nroom ").Append(i + 1).Append('\n');
                sb.Append("id ").Append(i + 1).Append('\n');
                sb.Append("type 1\n");
                foreach (var a in block.Attributes) {
                    if (a is TextureAttribute t) {
                        sb.Append("tex ").Append(t.Index).Append('\n');
                        continue;
                    }
                    using var ms = new MemoryStream();
                    using (var w = new BinaryWriter(ms)) a.Write(w);
                    byte[] bytes = ms.ToArray();
                    for (int k = 2; k + 1 < bytes.Length; k += 2) sb.Append(BitConverter.ToUInt16(bytes, k).ToString("x4")).Append(' ');
                    sb.Append('\n');
                }
                sb.Append("perimeter ").Append(block.Perimeter.Count).Append('\n');
                foreach (var p in block.Perimeter) sb.Append("    ").Append(p.Vertex).Append(' ').Append(p.Block + 1).Append('\n');
            }
            File.WriteAllText(path, sb.ToString());
        }
    }
}
