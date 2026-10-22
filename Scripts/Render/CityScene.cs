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

        Node3D cityRoot, instRoot, baiRoot, pathsetRoot, propRoot, labelRoot, boundRoot, levelRoot, instHighlight;
        MeshInstance3D vertexMarkers, roomBounds, instBounds, attributeOutline, routeMarkers;
        DirectionalLight3D headlight;
        WorldEnvironment environment;
        Materials materials;
        Vector3[] pickTriangles = Array.Empty<Vector3>();
        int[] pickRooms = Array.Empty<int>();
        InstPick[] instPicks = Array.Empty<InstPick>();
        /// <summary>Attribute geometry of the selected room, for clicking on its attributes.</summary>
        Room attributePickRoom;
        Vector3[] attributePickTriangles = Array.Empty<Vector3>();
        int[] attributePickIndices = Array.Empty<int>();

        public Materials Materials => materials;
        public bool Busy { get; private set; }
        /// <summary>Number of roadside props and traffic lights placed by the last <see cref="RebuildProps"/>.</summary>
        public int PropCount { get; private set; }
        public int TrafficLightCount { get; private set; }

        // what the last rebuild of each layer placed, for the glTF export
        internal IReadOnlyCollection<Placement> InstPlacements { get; private set; } = Array.Empty<Placement>();
        internal IReadOnlyCollection<Placement> PathsetPlacements { get; private set; } = Array.Empty<Placement>();
        internal IReadOnlyCollection<MeshBatch> PathsetDecals { get; private set; } = Array.Empty<MeshBatch>();
        internal IReadOnlyCollection<Placement> PropPlacements { get; private set; } = Array.Empty<Placement>();

        public override void _Ready() {
            Instance = this;
            cityRoot = AddRoot("City");
            instRoot = AddRoot("INST");
            baiRoot = AddRoot("BAI");
            pathsetRoot = AddRoot("Pathsets");
            propRoot = AddRoot("Props");
            labelRoot = AddRoot("RoomLabels");
            boundRoot = AddRoot("CityBound");
            levelRoot = AddRoot("Level");
            instHighlight = AddRoot("InstHighlight");
            vertexMarkers = new MeshInstance3D { Name = "VertexMarkers" };
            roomBounds = new MeshInstance3D { Name = "RoomBounds" };
            instBounds = new MeshInstance3D { Name = "InstBounds" };
            attributeOutline = new MeshInstance3D { Name = "AttributeOutline" };
            routeMarkers = new MeshInstance3D { Name = "RouteMarkers" };
            AddChild(vertexMarkers);
            AddChild(roomBounds);
            AddChild(instBounds);
            AddChild(attributeOutline);
            AddChild(routeMarkers);
            headlight = new DirectionalLight3D { Name = "Headlight", LightEnergy = 1.0f, ShadowEnabled = false };
            AddChild(headlight);
            environment = new WorldEnvironment { Environment = new Godot.Environment() };
            AddChild(environment);
            ApplySettings();

            Doc.VerticesSelected += OnVerticesSelected;
            Doc.RoomsSelected += (rooms, source) => {
                UpdateRoomBounds();
                UpdateAttributePicks();
            };
            Doc.InstSelected += (components, source) => UpdateInstHighlight();
            Doc.InstReplaced += UpdateInstHighlight;
            Doc.RoomAttributesSelected += source => UpdateAttributeOutline();
            Doc.DataChanged += kind => {
                if (kind == DataKind.Inst) UpdateInstHighlight();
                else if (kind is DataKind.Rooms or DataKind.Vertices or DataKind.Heights or DataKind.Textures) {
                    UpdateAttributeOutline();
                    UpdateAttributePicks();
                }
            };
            Doc.RoutesSelected += OnRoutesSelected;
            Doc.PsdlReplaced += () => {
                vertexMarkers.Mesh = null;
                roomBounds.Mesh = null;
                attributeOutline.Mesh = null;
                UpdateAttributePicks();
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
            propRoot.Visible = s.ShowProps;
            baiRoot.Visible = s.ShowBai;
            labelRoot.Visible = s.ShowRoomIndices;
            boundRoot.Visible = s.ShowCityBounds;
            routeMarkers.Visible = s.ShowBai;
            UpdateRoomBounds();
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
            pickRooms = data.PickRooms.ToArray();
            foreach (var (name, (count, room)) in data.NotRendered) GD.Print($"Attribute \"{name}\" not rendered ({count} times, first in room {room:x}).");
            if (data.SkippedAttributes > 0) {
                string message = $"{data.SkippedAttributes} attributes could not be rendered (first: {data.FirstError})";
                GD.PushWarning(message);
                Doc.Status(message);
            }
            RebuildLabels();
            RebuildCityBound();
            UpdateRoomBounds();
            OnVerticesSelected(Doc.SelectedVertices);
        }

        // ---- INST ----

        internal class Placement {
            public PkgMeshData Mesh;
            public List<Transform3D> Transforms = new();
        }

        /// <summary>A rendered INST object, kept for picking.</summary>
        struct InstPick {
            public int Component;
            public PkgMeshData Mesh;
            public Transform3D Transform;
        }

        static Transform3D InstTransform(InstComponent c) {
            var (x, y, z) = c.Axes;
            var t = new Transform3D(new Basis(x.ToGodot(), y.ToGodot(), z.ToGodot()), c.Origin.ToGodot());
            return (c.Modifiers & InstComponent.FlagBanger) != 0 ? BangerTransform(c.Pkg, t) : t;
        }

        /// <summary>
        /// Moves a banger (prop) placed at the transform's origin to its centre of gravity, where the origin of its meshes is
        /// (tune/banger/&lt;pkg&gt;.dgBangerData).
        /// </summary>
        static Transform3D BangerTransform(string pkg, Transform3D t) {
            var cg = BangerStore.GetCG(pkg);
            if (cg == Vector3.Zero) return t;
            return new Transform3D(t.Basis, t.Origin + t.Basis.Orthonormalized() * cg);
        }

        public async Task RebuildInst(Progress progress) {
            var inst = Doc.Inst;
            var picks = new List<InstPick>();
            var groups = await Task.Run(() => {
                var result = new Dictionary<string, Placement>();
                progress.Begin("Rendering INST", inst.Components.Count);
                for (int i = 0; i < inst.Components.Count; i++) {
                    var c = inst.Components[i];
                    progress.Value = i;
                    var mesh = PkgStore.GetMeshData(c.Pkg, c.PaintJob);
                    if (mesh == null) continue;
                    var t = InstTransform(c);
                    if (!result.TryGetValue(mesh.Key, out var p)) result[mesh.Key] = p = new Placement { Mesh = mesh };
                    p.Transforms.Add(t);
                    picks.Add(new InstPick { Component = i, Mesh = mesh, Transform = t });
                }
                return result;
            });
            await Commit(instRoot, groups, progress, "Uploading INST");
            InstPlacements = groups.Values;
            instPicks = picks.ToArray();
            UpdateInstHighlight();
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

        /// <summary>Rotation about the vertical axis that turns the X axis towards direction.</summary>
        internal static Basis FacingBasis(Vector3 direction) {
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
                            int first = placement.Transforms.Count;
                            PlaceProps(path, placement.Transforms);
                            for (int k = first; k < placement.Transforms.Count; k++) placement.Transforms[k] = BangerTransform(path.Name, placement.Transforms[k]);
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
            PathsetPlacements = groups.Values;
            PathsetDecals = decals.Values;
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
                    float spacing = path.RawSpacing == 0 ? PropPath.DefaultSpacing : path.Spacing;
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

        // ---- Props ----

        /// <summary>Places the roadside props of the PSDL room paths with the loaded prop rules, and the traffic lights of the BAI file.</summary>
        public async Task RebuildProps(Progress progress) {
            var psdl = Doc.Psdl;
            var rules = Doc.Props;
            var bai = Doc.Bai;
            int count = 0, lights = 0;
            var missing = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var groups = await Task.Run(() => {
                var result = new Dictionary<string, Placement>();
                bool Add(string model, Transform3D transform) {
                    var mesh = PkgStore.GetMeshData(model, 0);
                    if (mesh == null) {
                        missing.Add(model);
                        return false;
                    }
                    if (!result.TryGetValue(mesh.Key, out var p)) result[mesh.Key] = p = new Placement { Mesh = mesh };
                    p.Transforms.Add(BangerTransform(model, transform));
                    count++;
                    return true;
                }
                if (!rules.IsEmpty) Propulator.Place(psdl, rules, (model, transform) => Add(model, transform), progress);
                Propulator.PlaceTrafficLights(bai, rules, (model, transform) => {
                    if (Add(model, transform)) lights++;                });
                return result;
            });
            await Commit(propRoot, groups, progress, "Uploading props");
            PropPlacements = groups.Values;
            PropCount = count;
            TrafficLightCount = lights;
            if (missing.Count > 0) GD.PushWarning($"Props without geometry: {string.Join(", ", missing)}");
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
                    Axis(route.Tangents, Colors.Magenta);
                }
            }
            routeMarkers.Mesh = pos.Count > 0 ? Lines(pos, col, true) : null;
        }

        // ---- labels, bounds and markers ----

        public void RebuildLabels() {
            ClearChildren(labelRoot);
            if (!Settings.Current.ShowRoomIndices) return;
            var psdl = Doc.Psdl;
            float size = Math.Max(0.1f, Settings.Current.LabelSize);
            for (int i = 0; i < psdl.Rooms.Count; i++) {
                var c = psdl.RoomCenter(psdl.Rooms[i]).ToGodot();
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
            if (r > 0 && float.IsFinite(r) && center.IsFinite()) boundRoot.AddChild(new MeshInstance3D { Mesh = Lines(pos, col, false) });
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

        void UpdateRoomBounds() {
            if (roomBounds == null) return;
            if (!Settings.Current.ShowRoomBounds) {
                roomBounds.Mesh = null;
                return;
            }
            var pos = new List<Vector3>();
            var col = new List<Color>();
            var psdl = Doc.Psdl;
            foreach (int b in Doc.SelectedRooms) {
                if (b < 0 || b >= psdl.Rooms.Count) continue;
                var points = new List<Vector3>();
                int last = -1;
                foreach (var p in psdl.Rooms[b].Perimeter) {
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
            roomBounds.Mesh = pos.Count > 0 ? Lines(pos, col, true) : null;
        }

        /// <summary>Green outline of the geometry generated by the selected room attributes.</summary>
        void UpdateAttributeOutline() {
            if (attributeOutline == null) return;
            var room = Doc.AttributeRoom;
            if (room == null || Doc.SelectedAttributes.Length == 0 || !Doc.Psdl.Rooms.Contains(room)) {
                attributeOutline.Mesh = null;
                return;
            }
            var tris = CityBuilder.RoomAttributeGeometry(Doc.Psdl, Settings.Current, room, Doc.SelectedAttributes).PickTriangles;
            // boundary edges are the ones used by a single triangle; shared (inner) edges are left out
            var edges = new Dictionary<(Vector3, Vector3), int>();
            var order = new List<(Vector3, Vector3)>();
            for (int i = 0; i + 2 < tris.Count; i += 3) {
                for (int k = 0; k < 3; k++) {
                    var a = tris[i + k];
                    var b = tris[i + (k + 1) % 3];
                    var key = a < b ? (a, b) : (b, a);
                    if (edges.TryGetValue(key, out int n)) edges[key] = n + 1;
                    else {
                        edges[key] = 1;
                        order.Add(key);
                    }
                }
            }
            var pos = new List<Vector3>();
            var col = new List<Color>();
            var color = Color.FromHsv(0.3333333f, 0.85f, 1);
            foreach (var key in order) {
                if (edges[key] != 1) continue;
                pos.Add(key.Item1); col.Add(color);
                pos.Add(key.Item2); col.Add(color);
            }
            attributeOutline.Mesh = pos.Count > 0 ? Lines(pos, col, true) : null;
        }

        /// <summary>Builds the per attribute pick triangles when exactly one room is selected.</summary>
        void UpdateAttributePicks() {
            attributePickRoom = null;
            attributePickTriangles = Array.Empty<Vector3>();
            attributePickIndices = Array.Empty<int>();
            var psdl = Doc.Psdl;
            if (Doc.SelectedRooms.Length != 1) return;
            int r = Doc.SelectedRooms[0];
            if (r < 0 || r >= psdl.Rooms.Count) return;
            var data = CityBuilder.RoomAttributeGeometry(psdl, Settings.Current, psdl.Rooms[r]);
            attributePickRoom = psdl.Rooms[r];
            attributePickTriangles = data.PickTriangles.ToArray();
            attributePickIndices = data.PickAttributes.ToArray();
        }

        /// <summary>Red bounding box and translucent overlay on the selected INST objects.</summary>
        void UpdateInstHighlight() {
            if (instBounds == null) return;
            ClearChildren(instHighlight);
            var pos = new List<Vector3>();
            var col = new List<Color>();
            var color = Color.FromHsv(0, 0.85f, 1);
            var components = Doc.Inst.Components;
            foreach (int i in Doc.SelectedInst) {
                if (i < 0 || i >= components.Count) continue;
                var c = components[i];
                var t = InstTransform(c);
                var data = PkgStore.GetMeshData(c.Pkg, c.PaintJob);
                // objects without geometry still get a 2 m marker box at their origin
                var box = data != null && data.Surfaces.Count > 0 ? data.Bounds : new Aabb(-Vector3.One, Vector3.One * 2);
                BoxLines(pos, col, t, box, color);
                var mesh = PkgStore.GetMesh(data, materials);
                if (mesh != null && mesh.GetSurfaceCount() > 0) {
                    instHighlight.AddChild(new MeshInstance3D {
                        Mesh = mesh,
                        Transform = t,
                        MaterialOverride = materials.InstHighlight(),
                        CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
                    });
                }
            }
            instBounds.Mesh = pos.Count > 0 ? Lines(pos, col, true) : null;
        }

        static void BoxLines(List<Vector3> pos, List<Color> col, Transform3D t, Aabb box, Color color) {
            var corners = new Vector3[8];
            for (int k = 0; k < 8; k++) corners[k] = t * box.GetEndpoint(k);
            // GetEndpoint numbering: bit 0 = z, bit 1 = y, bit 2 = x
            foreach (var (a, b) in new[] { (0, 1), (2, 3), (4, 5), (6, 7), (0, 2), (1, 3), (4, 6), (5, 7), (0, 4), (1, 5), (2, 6), (3, 7) }) {
                pos.Add(corners[a]); col.Add(color);
                pos.Add(corners[b]); col.Add(color);
            }
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

        /// <summary>CPU ray cast against the city triangles. Returns the room index or -1.</summary>
        public int Raycast(Vector3 origin, Vector3 direction, out Vector3 hit) {
            hit = default;
            float best = float.MaxValue;
            int t = NearestTriangle(pickTriangles, origin, direction, ref best);
            if (t < 0) return -1;
            hit = origin + direction * best;
            return pickRooms[t];
        }

        /// <summary>
        /// Ray cast against the attribute geometry of the selected room (only available while a single room is selected).
        /// Returns the attribute index or -1.
        /// </summary>
        public int RaycastAttribute(Vector3 origin, Vector3 direction, out Room room, out float distance) {
            distance = float.MaxValue;
            room = attributePickRoom;
            int t = NearestTriangle(attributePickTriangles, origin, direction, ref distance);
            if (t < 0 || room == null) return -1;
            int attribute = attributePickIndices[t];
            return attribute >= 0 && attribute < room.Attributes.Count ? attribute : -1;
        }

        /// <summary>Index of the nearest triangle closer than best (which is updated), or -1.</summary>
        static int NearestTriangle(Vector3[] tris, Vector3 origin, Vector3 direction, ref float best) {
            int nearest = -1;
            for (int i = 0, t = 0; i + 2 < tris.Length; i += 3, t++) {
                if (!IntersectTriangle(origin, direction, tris[i], tris[i + 1], tris[i + 2], out float dist) || dist >= best) continue;
                best = dist;
                nearest = t;
            }
            return nearest;
        }

        /// <summary>CPU ray cast against the rendered INST objects. Returns the component index or -1.</summary>
        public int RaycastInst(Vector3 origin, Vector3 direction, out float distance) {
            distance = float.MaxValue;
            int component = -1;
            if (!instRoot.Visible) return -1;
            foreach (var p in instPicks) {
                // components removed since the last render
                if (p.Component >= Doc.Inst.Components.Count) continue;
                // in object space the ray is o + d * t with the same t as in world space
                var inverse = p.Transform.AffineInverse();
                var o = inverse * origin;
                var d = inverse.Basis * direction;
                if (!IntersectBox(o, d, p.Mesh.Bounds, distance)) continue;
                foreach (var surface in p.Mesh.Surfaces) {
                    var tris = surface.pos;
                    for (int i = 0; i + 2 < tris.Length; i += 3) {
                        if (!IntersectTriangle(o, d, tris[i], tris[i + 1], tris[i + 2], out float dist) || dist >= distance) continue;
                        distance = dist;
                        component = p.Component;
                    }
                }
            }
            return component;
        }

        static bool IntersectBox(Vector3 o, Vector3 d, Aabb box, float maxDistance) {
            float near = 0, far = maxDistance;
            var min = box.Position;
            var max = box.End;
            for (int a = 0; a < 3; a++) {
                if (Math.Abs(d[a]) < 1e-12f) {
                    if (o[a] < min[a] || o[a] > max[a]) return false;
                    continue;
                }
                float t1 = (min[a] - o[a]) / d[a], t2 = (max[a] - o[a]) / d[a];
                if (t1 > t2) (t1, t2) = (t2, t1);
                near = Math.Max(near, t1);
                far = Math.Min(far, t2);
                if (near > far) return false;
            }
            return true;
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
