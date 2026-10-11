using System;
using System.Collections.Generic;
using Godot;
using PsdlViewer.Core;
using PsdlViewer.Formats;
using PsdlViewer.Formats.Psdl;

namespace PsdlViewer.Render {
    /// <summary>Triangle soup for one material, already in Godot's clockwise winding.</summary>
    public class MeshBatch {
        public TextureEntry Texture;
        public readonly List<Vector3> Positions = new();
        public readonly List<Vector3> Normals = new();
        public readonly List<Vector2> UVs = new();
        public readonly List<Color> Colors = new();
    }

    public class CityMeshData {
        public readonly Dictionary<string, MeshBatch> Batches = new();
        /// <summary>Counter-clockwise triangles (3 vertices each) used for picking.</summary>
        public readonly List<Vector3> PickTriangles = new();
        public readonly List<int> PickBlocks = new();
        public int SkippedAttributes;
        public string FirstError;
    }

    /// <summary>
    /// Turns PSDL blocks into geometry the same way the original viewer did (Terrain.createBlockObject and the Shape_* classes).
    /// Input triangles are counter-clockwise (Java3D convention) and get flipped for Godot.
    /// </summary>
    public class CityBuilder {
        const float SidewalkHeight = 0.15f;
        readonly PsdlFile psdl;
        readonly Settings settings;
        readonly CityMeshData data = new();
        readonly Random random = new(12345);
        int blockIndex;
        Color blockColor;

        CityBuilder(PsdlFile psdl, Settings settings) {
            this.psdl = psdl;
            this.settings = settings;
        }

        public static CityMeshData Build(PsdlFile psdl, Settings settings, Progress progress) {
            var builder = new CityBuilder(psdl, settings);
            progress?.Begin("Rendering PSDL", psdl.Blocks.Count);
            for (int i = 0; i < psdl.Blocks.Count; i++) {
                builder.BuildBlock(i, psdl.Blocks[i]);
                if (progress != null) progress.Value = i;
            }
            return builder.data;
        }

        bool TextureMode => settings.ColouringMode == Settings.ColouringTextureMap;

        Vector3 P(int vertex) {
            if (vertex < 0 || vertex >= psdl.Vertices.Count) throw new IndexOutOfRangeException($"vertex {vertex:x} doesn't exist");
            return psdl.Vertices[vertex].ToGodot();
        }

        static Vector3 Up(Vector3 v, float dy) => new(v.X, v.Y + dy, v.Z);

        /// <summary>Texture index n + k as used by multi texture attributes; -1 when unavailable.</summary>
        int Derived(int baseIndex, int k) {
            if (baseIndex < 0) return -1;
            int i = baseIndex + k;
            return i < psdl.Textures.Count ? i : -1;
        }

        TextureEntry Tex(int index) {
            if (!TextureMode || index < 0 || index >= psdl.Textures.Count) return null;
            string name = psdl.Textures[index];
            return string.IsNullOrEmpty(name) ? null : TextureStore.Get(name);
        }

        Color ColorFor(int textureIndex) {
            switch (settings.ColouringMode) {
                case Settings.ColouringGrey: {
                    float g = 0.35f + 0.6f * blockIndex / Math.Max(1, psdl.Blocks.Count - 1);
                    return new Color(g, g, g);
                }
                case Settings.ColouringRandom:
                    return blockColor;
                case Settings.ColouringTextureIndex:
                    return textureIndex < 0 ? new Color(0.8f, 0.8f, 0.8f) : Color.FromHsv((textureIndex * 0.618034f) % 1f, 0.55f, 0.95f);
                default:
                    return Colors.White;
            }
        }

        void BuildBlock(int index, Block block) {
            blockIndex = index;
            blockColor = new Color((float)random.NextDouble() * 0.7f + 0.3f, (float)random.NextDouble() * 0.7f + 0.3f, (float)random.NextDouble() * 0.7f + 0.3f);
            if (!settings.RenderBlockAttributes) {
                try {
                    var points = new List<Vector3>();
                    int last = -1;
                    foreach (var p in block.Perimeter) {
                        if (p.Vertex == last) continue;
                        last = p.Vertex;
                        points.Add(P(p.Vertex));
                    }
                    if (points.Count >= 3 && points[0] == points[^1]) points.RemoveAt(points.Count - 1);
                    var uv = new Vector2[points.Count];
                    Fan(points, uv, null, ColorFor(-1));
                } catch (Exception e) {
                    Fail(e);
                }
                return;
            }
            int texture = -1;
            foreach (var a in block.Attributes) {
                try {
                    switch (a) {
                        case TextureAttribute t:
                            texture = t.Index;
                            break;
                        case DividedRoadAttribute d:
                            RoadSection(d.Vertices, 6, texture);
                            Divider(d);
                            break;
                        case StripAttribute s when s.Type == AttributeType.Road:
                            RoadSection(s.Vertices, 4, texture);
                            break;
                        case StripAttribute s when s.Type == AttributeType.RoadNoSidewalk:
                            RoadSection(s.Vertices, 2, texture);
                            break;
                        case StripAttribute s when s.Type == AttributeType.SidewalkStrip:
                            SidewalkStrip(s.Vertices, Derived(texture, 1));
                            break;
                        case CrosswalkAttribute c:
                            Crosswalk(c.Vertices, Derived(texture, 2));
                            break;
                        case RoofAttribute r:
                            TriangleFan(r.Vertices, texture, psdl.HeightAt(r.HeightRef));
                            break;
                        case FanAttribute f:
                            TriangleFan(f.Vertices, texture, null);
                            break;
                        case FacadeAttribute f:
                            Facade(f, texture);
                            break;
                        case SliverAttribute s:
                            Sliver(s, texture);
                            break;
                        // facade bounds, tunnels and unknown attributes are not rendered (as in the original viewer)
                    }
                } catch (Exception e) {
                    Fail(e);
                }
            }
        }

        void Fail(Exception e) {
            data.SkippedAttributes++;
            data.FirstError ??= $"block {blockIndex:x}: {e.Message}";
        }

        // ---- primitive emitters ----

        MeshBatch Batch(TextureEntry texture) {
            string key = texture?.Name ?? "";
            if (!data.Batches.TryGetValue(key, out var batch)) {
                batch = new MeshBatch { Texture = texture };
                data.Batches[key] = batch;
            }
            return batch;
        }

        /// <summary>Adds a counter-clockwise triangle.</summary>
        void Tri(Vector3 a, Vector3 b, Vector3 c, Vector2 ua, Vector2 ub, Vector2 uc, TextureEntry texture, Color color) {
            var normal = (b - a).Cross(c - a);
            float len = normal.Length();
            if (len < 1e-9f) return;
            normal /= len;
            var batch = Batch(texture);
            batch.Positions.Add(a);
            batch.Positions.Add(c);
            batch.Positions.Add(b);
            batch.UVs.Add(ua);
            batch.UVs.Add(uc);
            batch.UVs.Add(ub);
            for (int i = 0; i < 3; i++) {
                batch.Normals.Add(normal);
                batch.Colors.Add(color);
            }
            data.PickTriangles.Add(a);
            data.PickTriangles.Add(b);
            data.PickTriangles.Add(c);
            data.PickBlocks.Add(blockIndex);
        }

        /// <summary>Java3D triangle strip semantics: odd triangles swap their first two vertices.</summary>
        void Strip(IList<Vector3> p, IList<Vector2> uv, TextureEntry texture, Color color) {
            for (int i = 0; i + 2 < p.Count; i++) {
                if ((i & 1) == 0) Tri(p[i], p[i + 1], p[i + 2], uv[i], uv[i + 1], uv[i + 2], texture, color);
                else Tri(p[i + 1], p[i], p[i + 2], uv[i + 1], uv[i], uv[i + 2], texture, color);
            }
        }

        void Fan(IList<Vector3> p, IList<Vector2> uv, TextureEntry texture, Color color) {
            for (int i = 1; i + 1 < p.Count; i++) Tri(p[0], p[i], p[i + 1], uv[0], uv[i], uv[i + 1], texture, color);
        }

        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, TextureEntry texture, Color color) {
            var u0 = new Vector2(0, 0);
            var u1 = new Vector2(1, 0);
            var u2 = new Vector2(1, 1);
            var u3 = new Vector2(0, 1);
            Tri(a, b, c, u0, u1, u2, texture, color);
            Tri(a, c, d, u0, u2, u3, texture, color);
        }

        // ---- Shape_Roadsection ----

        struct Offset {
            public bool Order;
            public int SidewalkOuter, RoadOuter, RoadInner;
        }

        void RoadSection(List<int> refs, int perSection, int texture) {
            int sections = refs.Count / perSection;
            if (sections < 1) return;
            Offset left, right;
            switch (perSection) {
                case 2:
                    right = new Offset { Order = true, RoadOuter = 1, RoadInner = 0 };
                    DrivingLane(refs, perSection, sections, right, texture);
                    return;
                case 4:
                    left = new Offset { Order = false, SidewalkOuter = 0, RoadOuter = 1, RoadInner = -2 };
                    right = new Offset { Order = true, SidewalkOuter = 3, RoadOuter = 2, RoadInner = -1 };
                    break;
                default:
                    left = new Offset { Order = false, SidewalkOuter = 0, RoadOuter = 1, RoadInner = 2 };
                    right = new Offset { Order = true, SidewalkOuter = 5, RoadOuter = 4, RoadInner = 3 };
                    break;
            }
            Sidewalk(refs, perSection, sections, left, texture);
            DrivingLane(refs, perSection, sections, left, texture);
            DrivingLane(refs, perSection, sections, right, texture);
            Sidewalk(refs, perSection, sections, right, texture);
        }

        void Sidewalk(List<int> refs, int perSection, int sections, Offset o, int texture) {
            if (refs[o.RoadOuter] == refs[o.SidewalkOuter]) return;
            int tex = Derived(texture, 1);
            var top = new List<Vector3>();
            var topUV = new List<Vector2>();
            var curb = new List<Vector3>();
            var curbUV = new List<Vector2>();
            float x = 1;
            Vector3? previous = null;
            for (int i = 0, n = 0; i < sections; i++, n += perSection) {
                var outer = P(refs[n + o.RoadOuter]);
                var raised = Up(outer, SidewalkHeight);
                var side = P(refs[n + o.SidewalkOuter]);
                if (previous != null) {
                    float width = side.DistanceTo(raised);
                    if (width > 1e-6f) x += side.DistanceTo(previous.Value) / width;
                }
                var t0 = new Vector2(x, 0);
                var t1 = new Vector2(x, 1);
                if (o.Order) {
                    top.Add(raised); topUV.Add(t0);
                    top.Add(side); topUV.Add(t1);
                    curb.Add(outer); curbUV.Add(t0);
                    curb.Add(raised); curbUV.Add(t0);
                } else {
                    top.Add(side); topUV.Add(t1);
                    top.Add(raised); topUV.Add(t0);
                    curb.Add(raised); curbUV.Add(t0);
                    curb.Add(outer); curbUV.Add(t0);
                }
                previous = side;
            }
            var color = ColorFor(tex);
            Strip(top, topUV, Tex(tex), color);
            Strip(curb, curbUV, Tex(tex), Darker(color));
        }

        static Color Darker(Color c) => new(Math.Max(0, c.R - 0.1f), Math.Max(0, c.G - 0.1f), Math.Max(0, c.B - 0.1f));

        void DrivingLane(List<int> refs, int perSection, int sections, Offset o, int texture) {
            var p = new List<Vector3>();
            var uv = new List<Vector2>();
            float x = 1;
            Vector3? previous = null;
            for (int i = 0, n = 0; i < sections; i++, n += perSection) {
                var outer = P(refs[n + o.RoadOuter]);
                Vector3 inner;
                if (o.RoadInner < 0) inner = (outer + P(refs[n - o.RoadInner])) / 2;
                else inner = P(refs[n + o.RoadInner]);
                if (previous != null) {
                    float width = outer.DistanceTo(inner);
                    if (width > 1e-6f) x += outer.DistanceTo(previous.Value) / width;
                }
                var tInner = new Vector2(x, 0);
                var tOuter = new Vector2(x, 1);
                if (o.Order) {
                    p.Add(inner); uv.Add(tInner);
                    p.Add(outer); uv.Add(tOuter);
                } else {
                    p.Add(outer); uv.Add(tOuter);
                    p.Add(inner); uv.Add(tInner);
                }
                previous = outer;
            }
            Strip(p, uv, Tex(texture), ColorFor(texture));
        }

        void Divider(DividedRoadAttribute a) {
            var refs = a.Vertices;
            int sections = a.Sections;
            int dividerType = a.DividerType;
            if (sections < 1 || dividerType == DividedRoadAttribute.DividerInvisible) return;
            int baseTexture = a.DividerTexture;
            int T(int k) => baseTexture < 0 || baseTexture + k >= psdl.Textures.Count ? -1 : baseTexture + k;
            float h = a.Height;
            if (dividerType == DividedRoadAttribute.DividerFlat) {
                var p = new List<Vector3>();
                var uv = new List<Vector2>();
                float x = 1;
                Vector3? previous = null;
                for (int i = 0, n = 0; i < sections; i++, n += 6) {
                    var a0 = P(refs[n + 2]);
                    var a1 = P(refs[n + 3]);
                    if (previous != null) {
                        float width = a0.DistanceTo(a1);
                        if (width > 1e-6f) x += a0.DistanceTo(previous.Value) / width * h;
                    }
                    p.Add(a0); uv.Add(new Vector2(x, 0));
                    p.Add(a1); uv.Add(new Vector2(x, h));
                    previous = a0;
                }
                Strip(p, uv, Tex(T(1)), ColorFor(T(1)));
                return;
            }
            bool elevated = dividerType == DividedRoadAttribute.DividerElevated;
            var sideL = new List<Vector3>(); var sideLUV = new List<Vector2>();
            var sideR = new List<Vector3>(); var sideRUV = new List<Vector2>();
            var topL = new List<Vector3>(); var topLUV = new List<Vector2>();
            var topR = new List<Vector3>(); var topRUV = new List<Vector2>();
            var center = new List<Vector3>(); var centerUV = new List<Vector2>();
            var capTex = Tex(T(3));
            var capColor = ColorFor(T(3));
            float xs = 1;
            Vector3? prev = null;
            for (int i = 0, n = 0; i < sections; i++, n += 6) {
                var p0 = P(refs[n + 2]);
                var p5 = P(refs[n + 3]);
                var dir = p5 - p0;
                float len = dir.Length();
                float rise = elevated ? h : 1;
                float k = len > 1e-6f ? (elevated ? h : 0.5f) / len : 0;
                var off = new Vector3(dir.X * k, 0, dir.Z * k);
                var p1 = Up(p0, rise);
                var p2 = p1 + off;
                var p4 = Up(p5, rise);
                var p3 = p4 - off;
                if (prev != null && rise > 1e-6f) xs += p0.DistanceTo(prev.Value) / rise * h;
                var tA = new Vector2(xs, 1);
                var tB = new Vector2(xs, 0);
                if (elevated) {
                    sideL.Add(p0); sideLUV.Add(tB);
                    sideL.Add(p1); sideLUV.Add(tA);
                    sideR.Add(p4); sideRUV.Add(tA);
                    sideR.Add(p5); sideRUV.Add(tB);
                    topL.Add(p1); topLUV.Add(tB);
                    topL.Add(p2); topLUV.Add(tA);
                    topR.Add(p3); topRUV.Add(tA);
                    topR.Add(p4); topRUV.Add(tB);
                    if (i == 0 && a.GetFlag(DividedRoadAttribute.FlagCloseStart)) Quad(p0, p5, p4, p1, capTex, capColor);
                    if (i == sections - 1 && a.GetFlag(DividedRoadAttribute.FlagCloseEnd)) Quad(p5, p0, p1, p4, capTex, capColor);
                } else {
                    sideL.Add(p0); sideLUV.Add(tB);
                    sideL.Add(p2); sideLUV.Add(tA);
                    sideR.Add(p3); sideRUV.Add(tA);
                    sideR.Add(p5); sideRUV.Add(tB);
                    if (i == 0 && a.GetFlag(DividedRoadAttribute.FlagCloseStart)) Quad(p0, p5, p3, p2, capTex, capColor);
                    if (i == sections - 1 && a.GetFlag(DividedRoadAttribute.FlagCloseEnd)) Quad(p5, p0, p2, p3, capTex, capColor);
                }
                center.Add(p2); centerUV.Add(tB);
                center.Add(p3); centerUV.Add(tA);
                prev = p0;
            }
            int sideTexture = elevated ? T(0) : T(1);
            Strip(sideL, sideLUV, Tex(sideTexture), ColorFor(sideTexture));
            Strip(sideR, sideRUV, Tex(sideTexture), ColorFor(sideTexture));
            if (elevated) {
                Strip(topL, topLUV, Tex(T(1)), ColorFor(T(1)));
                Strip(topR, topRUV, Tex(T(1)), ColorFor(T(1)));
            }
            Strip(center, centerUV, Tex(T(2)), ColorFor(T(2)));
        }

        // ---- SidewalkStrip / Shape_Triangle ----

        void SidewalkStrip(List<int> refs, int texture) {
            var tex = Tex(texture);
            var color = ColorFor(texture);
            if (refs.Count == 4 && refs[0] == refs[1] && refs[0] <= 1) {
                // road end piece: a single triangle
                Vector3 a, b, c;
                if (refs[0] == 0) {
                    c = P(refs[2]);
                    b = P(refs[3]);
                    a = Up(c, SidewalkHeight);
                } else {
                    a = P(refs[2]);
                    b = P(refs[3]);
                    c = Up(a, SidewalkHeight);
                }
                Tri(a, b, c, new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), tex, color);
                return;
            }
            int sections = refs.Count / 2;
            if (sections < 2) return;
            var top = new List<Vector3>(); var topUV = new List<Vector2>();
            var curb = new List<Vector3>(); var curbUV = new List<Vector2>();
            float x = 1;
            var previous = P(refs[0]);
            for (int i = 0; i + 1 < refs.Count; i += 2) {
                var outer = P(refs[i + 1]);
                var inner = P(refs[i]);
                var raised = Up(inner, SidewalkHeight);
                x -= inner.DistanceTo(previous);
                previous = inner;
                var t0 = new Vector2(x, 0);
                var t1 = new Vector2(x, 1);
                top.Add(raised); topUV.Add(t0);
                top.Add(outer); topUV.Add(t1);
                curb.Add(inner); curbUV.Add(t0);
                curb.Add(raised); curbUV.Add(t0);
            }
            Strip(top, topUV, tex, color);
            Strip(curb, curbUV, tex, Darker(color));
        }

        // ---- TriangleStrip (crosswalks) ----

        void Crosswalk(List<int> refs, int texture) {
            int sections = refs.Count / 2;
            if (sections < 2) return;
            var p = new List<Vector3>();
            var uv = new List<Vector2>();
            float y = 1;
            var previous = P(refs[1]);
            for (int i = 0; i + 1 < refs.Count; i += 2) {
                var a = P(refs[i]);
                var b = P(refs[i + 1]);
                y -= b.DistanceTo(previous);
                previous = b;
                p.Add(b); uv.Add(new Vector2(0, y));
                p.Add(a); uv.Add(new Vector2(1, y));
            }
            Strip(p, uv, Tex(texture), ColorFor(texture));
        }

        // ---- TriangleFan ----

        void TriangleFan(List<int> refs, int texture, float? height) {
            if (refs.Count < 3) return;
            var p = new Vector3[refs.Count];
            var uv = new Vector2[refs.Count];
            for (int i = 0; i < refs.Count; i++) {
                p[i] = P(refs[i]);
                if (height != null) p[i].Y = height.Value;
                uv[i] = new Vector2((p[0].X - p[i].X) / 10f, (p[0].Z - p[i].Z) / 10f);
            }
            Fan(p, uv, Tex(texture), ColorFor(texture));
        }

        // ---- Shape_Facade ----

        void Facade(FacadeAttribute f, int texture) {
            float bottom = psdl.HeightAt(f.BottomHeight);
            float top = psdl.HeightAt(f.TopHeight);
            var a = P(f.Vertices[0]);
            var b = P(f.Vertices[1]);
            var p0 = new Vector3(a.X, bottom, a.Z);
            var p1 = new Vector3(b.X, bottom, b.Z);
            var p2 = new Vector3(a.X, top, a.Z);
            var p3 = new Vector3(b.X, top, b.Z);
            float u = f.URepeat, v = f.VRepeat;
            Strip(new[] { p0, p1, p2, p3 }, new[] { new Vector2(0, 0), new Vector2(u, 0), new Vector2(0, v), new Vector2(u, v) }, Tex(texture), ColorFor(texture));
        }

        void Sliver(SliverAttribute s, int texture) {
            float top = psdl.HeightAt(s.TopHeight);
            float scale = psdl.HeightAt(s.TextureScale);
            var p0 = P(s.Vertices[0]);
            var p1 = P(s.Vertices[1]);
            var p2 = new Vector3(p0.X, top, p0.Z);
            var p3 = new Vector3(p1.X, top, p1.Z);
            float width = p0.DistanceTo(p1);
            float rightHeight = p1.DistanceTo(p3);
            float u = (int)(width * scale + 0.5f);
            float v = width > 1e-6f ? (int)(rightHeight / width * u + 0.5f) : 1;
            Strip(new[] { p0, p1, p2, p3 }, new[] { new Vector2(0, 0), new Vector2(u, 0), new Vector2(0, v), new Vector2(u, v) }, Tex(texture), ColorFor(texture));
        }
    }
}
