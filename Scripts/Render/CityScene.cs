using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using PsdlViewer.Core;
using PsdlViewer.Formats;
using PsdlViewer.Formats.Psdl;

namespace PsdlViewer.Render {
    /// <summary>
    /// Everything that is drawn in the 3D views (the original viewer's Terrain universe).
    /// Lives in the root viewport's world, which every terrain view shares.
    /// </summary>
    public partial class CityScene : Node3D {
        public static CityScene Instance { get; private set; }

        Node3D cityRoot, instRoot, baiRoot, pathsetRoot, labelRoot, boundRoot, levelRoot;
        MeshInstance3D vertexMarkers, blockBounds, routeMarkers;
        DirectionalLight3D headlight;
        WorldEnvironment environment;
        Materials materials;
        Vector3[] pickTriangles = Array.Empty<Vector3>();
        int[] pickBlocks = Array.Empty<int>();

        public Materials Materials => materials;
        public bool Busy { get; private set; }

        public override void _Ready() {
            Instance = this;
            cityRoot = AddRoot("City");
            instRoot = AddRoot("INST");
            baiRoot = AddRoot("BAI");
            pathsetRoot = AddRoot("Pathsets");
            labelRoot = AddRoot("BlockLabels");
            boundRoot = AddRoot("CityBound");
            levelRoot = AddRoot("Level");
            vertexMarkers = new MeshInstance3D { Name = "VertexMarkers" };
            blockBounds = new MeshInstance3D { Name = "BlockBounds" };
            routeMarkers = new MeshInstance3D { Name = "RouteMarkers" };
            AddChild(vertexMarkers);
            AddChild(blockBounds);
            AddChild(routeMarkers);
            headlight = new DirectionalLight3D { Name = "Headlight", LightEnergy = 1.0f, ShadowEnabled = false };
            AddChild(headlight);
            environment = new WorldEnvironment { Environment = new Godot.Environment() };
            AddChild(environment);
            ApplySettings();

            Doc.VerticesSelected += OnVerticesSelected;
            Doc.BlocksSelected += (blocks, source) => UpdateBlockBounds();
            Doc.RoutesSelected += OnRoutesSelected;
            Doc.PsdlReplaced += () => {
                vertexMarkers.Mesh = null;
                blockBounds.Mesh = null;
                ClearChildren(levelRoot);
            };
        }

        public override void _ExitTree() {
            if (Instance == this) Instance = null;
        }

        Node3D AddRoot(string name) {
            var n = new Node3D { Name = name };
            AddChild(n);
            return n;
        }

        static void ClearChildren(Node n) {
            foreach (var c in n.GetChildren()) {
                n.RemoveChild(c);
                c.QueueFree();
            }
        }

        /// <summary>Applies the options that don't require rebuilding geometry.</summary>
        public void ApplySettings() {
            var s = Settings.Current;
            bool lit = s.LightingMode == Settings.LightingLit;
            if (materials == null || materials.Lit != lit) {
                materials = new Materials(lit);
                PkgStore.ResetMeshes();
            }
            headlight.Visible = lit;
            var env = environment.Environment;
            float b = s.BackgroundBrightness;
            env.BackgroundMode = Godot.Environment.BGMode.Color;
            env.BackgroundColor = new Color(b, b * 1.1f, b * 1.3f);
            env.AmbientLightSource = Godot.Environment.AmbientSource.Color;
            env.AmbientLightColor = new Color(0.45f, 0.45f, 0.5f);
            env.AmbientLightEnergy = 1;
            env.TonemapMode = Godot.Environment.ToneMapper.Linear;
            instRoot.Visible = s.RenderInst;
            pathsetRoot.Visible = s.ShowPathsets;
            baiRoot.Visible = s.ShowBai;
            labelRoot.Visible = s.ShowBlockIndices;
            boundRoot.Visible = s.ShowCityBounds;
            routeMarkers.Visible = s.ShowBai;
            UpdateBlockBounds();
        }

        /// <summary>Lights the scene from the camera like the original viewer's camera-mounted light.</summary>
        public void FollowCamera(Camera3D camera) {
            headlight.GlobalBasis = camera.GlobalBasis;
        }

        static async Task NextFrame(Node n) {
            await n.ToSignal(n.GetTree(), SceneTree.SignalName.ProcessFrame);
        }

        // ---- PSDL ----

        public async Task RebuildPsdl(Progress progress) {
            var psdl = Doc.Psdl;
            var settings = Settings.Current.Clone();
            var data = await Task.Run(() => CityBuilder.Build(psdl, settings, progress));
            progress.Begin("Uploading geometry", data.Batches.Count);
            ClearChildren(cityRoot);
            int i = 0;
            foreach (var batch in data.Batches.Values) {
                if (batch.Positions.Count == 0) continue;
                var arrays = new Godot.Collections.Array();
                arrays.Resize((int)Mesh.ArrayType.Max);
                arrays[(int)Mesh.ArrayType.Vertex] = batch.Positions.ToArray();
                arrays[(int)Mesh.ArrayType.Normal] = batch.Normals.ToArray();
                arrays[(int)Mesh.ArrayType.TexUV] = batch.UVs.ToArray();
                arrays[(int)Mesh.ArrayType.Color] = batch.Colors.ToArray();
                var mesh = new ArrayMesh();
                mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
                mesh.SurfaceSetMaterial(0, materials.ForCity(batch.Texture));
                cityRoot.AddChild(new MeshInstance3D { Mesh = mesh, Name = string.IsNullOrEmpty(batch.Texture?.Name) ? "untextured" : batch.Texture.Name });
                progress.Value = ++i;
                if (i % 24 == 0) await NextFrame(this);
            }
            pickTriangles = data.PickTriangles.ToArray();
            pickBlocks = data.PickBlocks.ToArray();
            if (data.SkippedAttributes > 0) Doc.Status($"{data.SkippedAttributes} attributes could not be rendered (first: {data.FirstError})");
            RebuildLabels();
            RebuildCityBound();
            UpdateBlockBounds();
            OnVerticesSelected(Doc.SelectedVertices);
        }

        // ---- INST ----

        class Placement {
            public PkgMeshData Mesh;
            public List<Transform3D> Transforms = new();
        }

        public async Task RebuildInst(Progress progress) {
            var inst = Doc.Inst;
            var groups = await Task.Run(() => {
                var result = new Dictionary<string, Placement>();
                progress.Begin("Rendering INST", inst.Components.Count);
                for (int i = 0; i < inst.Components.Count; i++) {
                    var c = inst.Components[i];
                    progress.Value = i;
                    var mesh = PkgStore.GetMeshData(c.Pkg, c.PaintJob);
                    if (mesh == null) continue;
                    var (x, y, z) = c.Axes;
                    var t = new Transform3D(new Basis(x.ToGodot(), y.ToGodot(), z.ToGodot()), c.Origin.ToGodot());
                    if (!result.TryGetValue(mesh.Key, out var p)) result[mesh.Key] = p = new Placement { Mesh = mesh };
                    p.Transforms.Add(t);
                }
                return result;
            });
            await Commit(instRoot, groups, progress, "Uploading INST");
        }

        async Task Commit(Node3D root, Dictionary<string, Placement> groups, Progress progress, string note) {
            ClearChildren(root);
            progress.Begin(note, groups.Count);
            int i = 0;
            foreach (var p in groups.Values) {
                var mesh = PkgStore.GetMesh(p.Mesh, materials);
                if (mesh == null || mesh.GetSurfaceCount() == 0) continue;
                var mm = new MultiMesh {
                    TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                    Mesh = mesh,
                    InstanceCount = p.Transforms.Count
                };
                for (int k = 0; k < p.Transforms.Count; k++) mm.SetInstanceTransform(k, p.Transforms[k]);
                root.AddChild(new MultiMeshInstance3D { Multimesh = mm, Name = p.Mesh.Pkg.Name });
                progress.Value = ++i;
                if (i % 32 == 0) await NextFrame(this);
            }
        }

        // ---- Pathsets ----

        static Basis FacingBasis(Vector3 direction) {
            var d = new Vector3(direction.X, 0, direction.Z);
            if (d.LengthSquared() < 1e-12f) return Basis.Identity;
            d = d.Normalized();
            return new Basis(d, Vector3.Up, new Vector3(-d.Z, 0, d.X));
        }

        public async Task RebuildPathsets(Progress progress) {
            var sets = new List<Pathset>(Doc.Pathsets);
            var decals = new Dictionary<string, MeshBatch>();
            var groups = await Task.Run(() => {
                var result = new Dictionary<string, Placement>();
                int total = 0;
                foreach (var s in sets) total += s.Paths.Count;
                progress.Begin("Rendering pathsets", total);
                int done = 0;
                foreach (var set in sets) {
                    foreach (var path in set.Paths) {
                        progress.Value = done++;
                        var points = path.Points;
                        if (points.Count == 0) continue;
                        var mesh = PkgStore.GetMeshData(path.Name, 0);
                        if (mesh != null) {
                            if (!result.TryGetValue(mesh.Key, out var placement)) result[mesh.Key] = placement = new Placement { Mesh = mesh };
                            PlaceProps(path, placement.Transforms);
                            continue;
                        }
                        var texture = TextureStore.Get(path.Name);
                        if (texture == null || texture.NotFound || points.Count < 4) continue;
                        if (!decals.TryGetValue(path.Name, out var batch)) decals[path.Name] = batch = new MeshBatch { Texture = texture };
                        AddDecal(batch, path);
                    }
                }
                return result;
            });
            await Commit(pathsetRoot, groups, progress, "Uploading pathsets");
            foreach (var batch in decals.Values) {
                if (batch.Positions.Count == 0) continue;
                var arrays = new Godot.Collections.Array();
                arrays.Resize((int)Mesh.ArrayType.Max);
                arrays[(int)Mesh.ArrayType.Vertex] = batch.Positions.ToArray();
                arrays[(int)Mesh.ArrayType.Normal] = batch.Normals.ToArray();
                arrays[(int)Mesh.ArrayType.TexUV] = batch.UVs.ToArray();
                var mesh = new ArrayMesh();
                mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
                mesh.SurfaceSetMaterial(0, materials.ForCity(batch.Texture));
                pathsetRoot.AddChild(new MeshInstance3D { Mesh = mesh, Name = "decal_" + batch.Texture.Name });
            }
        }

        static void PlaceProps(PropPath path, List<Transform3D> transforms) {
            var pts = path.Points;
            switch (path.Type) {
                case PropPath.TypePoints:
                    foreach (var p in pts) transforms.Add(new Transform3D(Basis.Identity, p.Position.ToGodot()));
                    break;
                case PropPath.TypeDirectedPoints:
                    for (int i = 0; i + 1 < pts.Count; i += 2) {
                        var a = pts[i].Position.ToGodot();
                        var b = pts[i + 1].Position.ToGodot();
                        transforms.Add(new Transform3D(FacingBasis(b - a), a));
                    }
                    break;
                default:
                    float spacing = path.Spacing;
                    for (int i = 0; i + 1 < pts.Count; i++) {
                        var a = pts[i].Position.ToGodot();
                        var b = pts[i + 1].Position.ToGodot();
                        var dir = b - a;
                        float len = dir.Length();
                        if (len < 1e-6f) continue;
                        var basis = FacingBasis(dir);
                        int count = spacing > 0.01f ? Math.Max(1, (int)(len / spacing)) : 1;
                        for (int k = 0; k < count; k++) transforms.Add(new Transform3D(basis, a + dir / len * (spacing * k)));
                    }
                    break;
            }
        }

        /// <summary>Shape_Decal: the points form a triangle strip of pairs, lifted 1 cm above the ground.</summary>
        static void AddDecal(MeshBatch batch, PropPath path) {
            var p = new List<Vector3>();
            var uv = new List<Vector2>();
            for (int i = 0; i + 1 < path.Points.Count; i += 2) {
                p.Add(path.Points[i].Position.ToGodot() + new Vector3(0, 0.01f, 0));
                uv.Add(new Vector2(0, i / 2));
                p.Add(path.Points[i + 1].Position.ToGodot() + new Vector3(0, 0.01f, 0));
                uv.Add(new Vector2(1, i / 2));
            }
            for (int i = 0; i + 2 < p.Count; i++) {
                int a = i, b = i + 1, c = i + 2;
                if ((i & 1) == 1) (a, b) = (b, a);
                var normal = (p[b] - p[a]).Cross(p[c] - p[a]).Normalized();
                // emit both faces: decal winding in pathsets is arbitrary
                foreach (var (x, y, z, n) in new[] { (a, c, b, normal), (a, b, c, -normal) }) {
                    batch.Positions.Add(p[x]); batch.UVs.Add(uv[x]); batch.Normals.Add(n);
                    batch.Positions.Add(p[y]); batch.UVs.Add(uv[y]); batch.Normals.Add(n);
                    batch.Positions.Add(p[z]); batch.UVs.Add(uv[z]); batch.Normals.Add(n);
                }
            }
        }

        // ---- BAI ----

        public async Task RebuildBai(Progress progress) {
            var bai = Doc.Bai;
            var (pos, col) = await Task.Run(() => {
                var p = new List<Vector3>();
                var c = new List<Color>();
                progress.Begin("Rendering BAI", bai.Routes.Count);
                for (int i = 0; i < bai.Routes.Count; i++) {
                    progress.Value = i;
                    var route = bai.Routes[i];
                    int n = route.Points;
                    Curve(p, c, 0, route.CentralLine, 0, n, true);
                    RoadData(p, c, route.Right, 0.6666667f, n);
                    RoadData(p, c, route.Left, 0.33333334f, n);
                }
                return (p, c);
            });
            ClearChildren(baiRoot);
            if (pos.Count > 0) baiRoot.AddChild(new MeshInstance3D { Mesh = Lines(pos, col, false), Name = "Routes" });
        }

        static void RoadData(List<Vector3> p, List<Color> c, BaiRoadData d, float hue, int n) {
            for (int k = 0; k < d.Lanes; k++) Curve(p, c, hue, d.LaneVertices, k * n, (k + 1) * n);
            Curve(p, c, 0.2f, d.SidewalkCenter, 0, n);
            Curve(p, c, 0.2f, d.SidewalkInner, 0, n);
            Curve(p, c, 0.2f, d.SidewalkOuter, 0, n);
            for (int k = 0; k < d.Trams; k++) Curve(p, c, 0.8f, d.TramVertices, k * n, (k + 1) * n);
            for (int k = 0; k < d.Trains; k++) Curve(p, c, 0.11111111f, d.TrainVertices, k * n, (k + 1) * n);
        }

        /// <summary>Terrain.generateCurve: hue fixed, saturation ramping along the curve. Dashed for centre lines.</summary>
        static void Curve(List<Vector3> p, List<Color> c, float hue, List<System.Numerics.Vector3> v, int from, int to, bool dashed = false) {
            to = Math.Min(to, v.Count);
            int n = to - from;
            if (n < 2) return;
            for (int i = 0; i + 1 < n; i++) {
                if (dashed && (i & 1) == 1) continue;
                p.Add(v[from + i].ToGodot());
                c.Add(Color.FromHsv(hue, (float)i / n, 1));
                p.Add(v[from + i + 1].ToGodot());
                c.Add(Color.FromHsv(hue, (float)(i + 1) / n, 1));
            }
        }

        ArrayMesh Lines(List<Vector3> pos, List<Color> col, bool onTop) {
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = pos.ToArray();
            arrays[(int)Mesh.ArrayType.Color] = col.ToArray();
            var mesh = new ArrayMesh();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Lines, arrays);
            mesh.SurfaceSetMaterial(0, materials.Lines(onTop));
            return mesh;
        }

        void OnRoutesSelected(int[] routes) {
            var pos = new List<Vector3>();
            var col = new List<Color>();
            foreach (int r in routes) {
                if (r < 0 || r >= Doc.Bai.Routes.Count) continue;
                var route = Doc.Bai.Routes[r];
                for (int j = 0; j < route.Points; j++) {
                    var o = route.CentralLine[j].ToGodot();
                    void Axis(List<System.Numerics.Vector3> axis, Color color) {
                        if (j >= axis.Count) return;
                        pos.Add(o); col.Add(color);
                        pos.Add(o + axis[j].ToGodot()); col.Add(color);
                    }
                    Axis(route.XOrientation, Colors.Red);
                    Axis(route.YOrientation, Colors.Lime);
                    Axis(route.ZOrientation, Colors.Blue);
                    Axis(route.WOrientation, Colors.Magenta);
                }
            }
            routeMarkers.Mesh = pos.Count > 0 ? Lines(pos, col, true) : null;
        }

        // ---- labels, bounds and markers ----

        public void RebuildLabels() {
            ClearChildren(labelRoot);
            if (!Settings.Current.ShowBlockIndices) return;
            var psdl = Doc.Psdl;
            float size = Math.Max(0.1f, Settings.Current.LabelSize);
            for (int i = 0; i < psdl.Blocks.Count; i++) {
                var c = psdl.BlockCenter(psdl.Blocks[i]).ToGodot();
                labelRoot.AddChild(new Label3D {
                    Text = i.ToString("x"),
                    Position = c + new Vector3(0, 2, 0),
                    Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                    Modulate = Colors.Red,
                    OutlineModulate = Colors.Black,
                    FontSize = 32,
                    OutlineSize = 6,
                    PixelSize = size / 32f
                });
            }
        }

        public void RebuildCityBound() {
            ClearChildren(boundRoot);
            var d = Doc.Psdl.Dimensions;
            var pos = new List<Vector3>();
            var col = new List<Color>();
            var color = new Color(0.3f, 0.7f, 1f);
            var center = d.Center.ToGodot();
            float r = d.Radius;
            const int segments = 48;
            for (int lat = -2; lat <= 2; lat++) {
                float phi = lat * Mathf.Pi / 6;
                float y = Mathf.Sin(phi) * r, rr = Mathf.Cos(phi) * r;
                for (int k = 0; k < segments; k++) {
                    float a0 = k * Mathf.Tau / segments, a1 = (k + 1) * Mathf.Tau / segments;
                    pos.Add(center + new Vector3(Mathf.Cos(a0) * rr, y, Mathf.Sin(a0) * rr)); col.Add(color);
                    pos.Add(center + new Vector3(Mathf.Cos(a1) * rr, y, Mathf.Sin(a1) * rr)); col.Add(color);
                }
            }
            for (int m = 0; m < 6; m++) {
                float theta = m * Mathf.Pi / 6;
                for (int k = 0; k < segments; k++) {
                    float a0 = k * Mathf.Tau / segments, a1 = (k + 1) * Mathf.Tau / segments;
                    pos.Add(center + new Vector3(Mathf.Cos(a0) * Mathf.Cos(theta), Mathf.Sin(a0), Mathf.Cos(a0) * Mathf.Sin(theta)) * r); col.Add(color);
                    pos.Add(center + new Vector3(Mathf.Cos(a1) * Mathf.Cos(theta), Mathf.Sin(a1), Mathf.Cos(a1) * Mathf.Sin(theta)) * r); col.Add(color);
                }
            }
            if (r > 0) boundRoot.AddChild(new MeshInstance3D { Mesh = Lines(pos, col, false) });
        }

        void OnVerticesSelected(int[] indices) {
            var pos = new List<Vector3>();
            var col = new List<Color>();
            var psdl = Doc.Psdl;
            foreach (int i in indices) {
                if (i < 0 || i >= psdl.Vertices.Count) continue;
                var v = psdl.Vertices[i].ToGodot();
                foreach (var axis in new[] { Vector3.Right, Vector3.Up, Vector3.Back }) {
                    pos.Add(v - axis); col.Add(Colors.Red);
                    pos.Add(v + axis); col.Add(Colors.Red);
                }
            }
            vertexMarkers.Mesh = pos.Count > 0 ? Lines(pos, col, true) : null;
        }

        void UpdateBlockBounds() {
            if (blockBounds == null) return;
            if (!Settings.Current.ShowBlockBounds) {
                blockBounds.Mesh = null;
                return;
            }
            var pos = new List<Vector3>();
            var col = new List<Color>();
            var psdl = Doc.Psdl;
            foreach (int b in Doc.SelectedBlocks) {
                if (b < 0 || b >= psdl.Blocks.Count) continue;
                var points = new List<Vector3>();
                int last = -1;
                foreach (var p in psdl.Blocks[b].Perimeter) {
                    if (p.Vertex == last || p.Vertex >= psdl.Vertices.Count) continue;
                    last = p.Vertex;
                    points.Add(psdl.Vertices[p.Vertex].ToGodot());
                }
                int n = points.Count;
                for (int i = 0; i + 1 < n; i++) {
                    pos.Add(points[i]); col.Add(Color.FromHsv(0.6666667f, (float)i / n, 1));
                    pos.Add(points[i + 1]); col.Add(Color.FromHsv(0.6666667f, (float)(i + 1) / n, 1));
                }
                if (n > 2) {
                    pos.Add(points[n - 1]); col.Add(Color.FromHsv(0.6666667f, 1, 1));
                    pos.Add(points[0]); col.Add(Color.FromHsv(0.6666667f, 0, 1));
                }
            }
            blockBounds.Mesh = pos.Count > 0 ? Lines(pos, col, true) : null;
        }

        /// <summary>Random city wizard: horizontal plane at the water level.</summary>
        public void ShowLevel(Vector3 min, Vector3 max, float height) {
            ClearChildren(levelRoot);
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = new[] {
                new Vector3(min.X, height, min.Z), new Vector3(max.X, height, min.Z), new Vector3(max.X, height, max.Z),
                new Vector3(min.X, height, min.Z), new Vector3(max.X, height, max.Z), new Vector3(min.X, height, max.Z)
            };
            var mesh = new ArrayMesh();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            mesh.SurfaceSetMaterial(0, materials.Water());
            levelRoot.AddChild(new MeshInstance3D { Mesh = mesh, Name = "WaterLevel" });
        }

        /// <summary>Random city wizard: coloured shore line polygons.</summary>
        public void ShowEdges(List<List<(System.Numerics.Vector3 a, System.Numerics.Vector3 b)>> polygons) {
            var pos = new List<Vector3>();
            var col = new List<Color>();
            for (int i = 0; i < polygons.Count; i++) {
                var poly = polygons[i];
                float f = 2f / (3f * poly.Count);
                for (int j = 0; j < poly.Count; j++) {
                    pos.Add(poly[j].a.ToGodot()); col.Add(Color.FromHsv((float)i / polygons.Count, 1, j * f + 0.33333334f));
                    pos.Add(poly[j].b.ToGodot()); col.Add(Color.FromHsv((float)i / polygons.Count, 1, (j + 1) * f + 0.33333334f));
                }
            }
            foreach (var c in levelRoot.GetChildren()) {
                if (c.Name == "Edges") {
                    levelRoot.RemoveChild(c);
                    c.QueueFree();
                }
            }
            if (pos.Count > 0) levelRoot.AddChild(new MeshInstance3D { Mesh = Lines(pos, col, true), Name = "Edges" });
        }

        public void ClearLevel() => ClearChildren(levelRoot);

        // ---- picking ----

        /// <summary>CPU ray cast against the city triangles. Returns the block index or -1.</summary>
        public int Raycast(Vector3 origin, Vector3 direction, out Vector3 hit) {
            hit = default;
            float best = float.MaxValue;
            int block = -1;
            var tris = pickTriangles;
            for (int i = 0, t = 0; i + 2 < tris.Length; i += 3, t++) {
                if (!IntersectTriangle(origin, direction, tris[i], tris[i + 1], tris[i + 2], out float dist) || dist >= best) continue;
                best = dist;
                block = pickBlocks[t];
            }
            if (block >= 0) hit = origin + direction * best;
            return block;
        }

        static bool IntersectTriangle(Vector3 o, Vector3 d, Vector3 a, Vector3 b, Vector3 c, out float t) {
            t = 0;
            var e1 = b - a;
            var e2 = c - a;
            var p = d.Cross(e2);
            float det = e1.Dot(p);
            if (Math.Abs(det) < 1e-9f) return false;
            float inv = 1 / det;
            var s = o - a;
            float u = s.Dot(p) * inv;
            if (u < 0 || u > 1) return false;
            var q = s.Cross(e1);
            float v = d.Dot(q) * inv;
            if (v < 0 || u + v > 1) return false;
            t = e2.Dot(q) * inv;
            return t > 0;
        }
    }
}
