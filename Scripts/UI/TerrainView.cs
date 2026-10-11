using System;
using System.Threading.Tasks;
using Godot;
using PsdlViewer.Core;
using PsdlViewer.Render;

namespace PsdlViewer.UI {
    /// <summary>
    /// 3D view of the city. Several can be open at once; they all show the same world.
    /// Camera controls follow the km2 city builder (right drag: look, middle drag: pan, middle click: zoom to point,
    /// wheel / PgUp / PgDn: zoom, Home + mouse: pan); the toolbar has the original viewer's buttons.
    /// </summary>
    public partial class TerrainView : DockPanel {
        public override string Kind => "Terrain";

        const float DefaultZoomFactor = 20;
        const float RotSpeed = 200, PanSpeed = 10, ZoomSpeed = 2;

        SubViewportContainer container;
        SubViewport viewport;
        Camera3D camera;
        Label hint;
        Vector3 curRotation = new(-35, 0, 0);
        float zoomFactor = DefaultZoomFactor;
        Vector2 oldMouse, middleOrigin;
        bool rotating, panning, middleDown, canZoomIn, zoomingIn, leftDown;
        Vector2 leftOrigin;
        static bool framedOnce;

        public TerrainView() {
            AddThemeConstantOverride("separation", 0);
        }

        public override void _Ready() {
            var row = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
            row.AddThemeConstantOverride("separation", 2);
            AddChild(row);
            row.AddChild(BuildToolbar());

            container = new SubViewportContainer { Stretch = true, SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill, FocusMode = FocusModeEnum.Click };
            row.AddChild(container);
            viewport = new SubViewport { World3D = GetTree().Root.World3D, Msaa3D = Viewport.Msaa.Msaa2X, HandleInputLocally = false };
            container.AddChild(viewport);
            camera = new Camera3D { Far = 20000, Near = 0.1f, Fov = Settings.Current.CameraFov, Current = true };
            viewport.AddChild(camera);
            camera.RotationDegrees = curRotation;
            camera.Position = new Vector3(0, 150, 200);
            container.GuiInput += OnViewInput;

            hint = new Label {
                Text = "Right drag: look   Middle drag / Home: pan   Middle click: zoom to point   Wheel / PgUp / PgDn: zoom   Left click: select block",
                Modulate = new Color(1, 1, 1, 0.55f),
                MouseFilter = MouseFilterEnum.Ignore
            };
            hint.AddThemeFontSizeOverride("font_size", 11);
            container.AddChild(hint);
            hint.Position = new Vector2(6, 4);

            Subscribe(() => {
                Settings.Changed += ApplySettings;
                Doc.PsdlReplaced += OnPsdlReplaced;
            }, () => {
                Settings.Changed -= ApplySettings;
                Doc.PsdlReplaced -= OnPsdlReplaced;
            });
            ApplySettings();
            if (framedOnce) FrameCity(false);
        }

        Control BuildToolbar() {
            var bar = new VBoxContainer();
            bar.AddThemeConstantOverride("separation", 2);
            bar.AddChild(DragButton("rotate", "Rotate (drag)", d => {
                curRotation += new Vector3(-d.Y, -d.X, 0) * 0.5f;
                camera.RotationDegrees = curRotation;
            }));
            bar.AddChild(DragButton("zoom", "Zoom / field of view (drag up and down)", d => {
                camera.Fov = Mathf.Clamp(camera.Fov + d.Y * 0.3f, 5, 150);
            }));
            bar.AddChild(DragButton("pan", "Pan (drag)", d => {
                camera.Position += camera.Basis.X * (-d.X * 0.5f) + camera.Basis.Y * (d.Y * 0.5f);
            }));
            bar.AddChild(DragButton("distance", "Move forward / backward (drag up and down)", d => {
                camera.Position += camera.Basis.Z * (d.Y * 0.5f);
            }));
            bar.AddChild(IconButton("front", "Front view", () => SetRotation(new Vector3(0, 0, 0))));
            bar.AddChild(IconButton("side", "Side view", () => SetRotation(new Vector3(0, 90, 0))));
            bar.AddChild(IconButton("top", "Top view", () => SetRotation(new Vector3(-90, 0, 0))));
            bar.AddChild(IconButton("origo", "Move to the origin", () => camera.Position = Vector3.Zero));
            bar.AddChild(IconButton("autofocus", "Focus on the selected vertices (or the selected block)", Autofocus));
            var frame = new Button { Text = "All", TooltipText = "Show the whole city", FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(42, 28) };
            frame.Pressed += () => FrameCity(true);
            bar.AddChild(frame);
            return bar;
        }

        static Texture2D Icon(string name) => GD.Load<Texture2D>($"res://Assets/Icons/{name}.png");

        static Button IconButton(string icon, string tooltip, Action pressed) {
            var b = new Button { Icon = Icon(icon), TooltipText = tooltip, FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(42, 33), IconAlignment = HorizontalAlignment.Center };
            b.Pressed += pressed;
            return b;
        }

        /// <summary>Button that reports mouse drag deltas while held, like the original viewer's control buttons.</summary>
        static Button DragButton(string icon, string tooltip, Action<Vector2> drag) {
            var b = new Button { Icon = Icon(icon), TooltipText = tooltip, FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(42, 33), IconAlignment = HorizontalAlignment.Center, MouseDefaultCursorShape = CursorShape.Drag };
            bool down = false;
            b.GuiInput += e => {
                if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left } mb) down = mb.Pressed;
                else if (e is InputEventMouseMotion mm && down) drag(mm.Relative);
            };
            return b;
        }

        public void SetCamera(Vector3 position, Vector3 rotationDegrees) {
            camera.Position = position;
            SetRotation(rotationDegrees);
        }

        void SetRotation(Vector3 degrees) {
            curRotation = degrees;
            camera.RotationDegrees = curRotation;
        }

        void ApplySettings() {
            var s = Settings.Current;
            viewport.DebugDraw = s.DrawingMode == Settings.DrawingLines ? Viewport.DebugDrawEnum.Wireframe : Viewport.DebugDrawEnum.Disabled;
            camera.Fov = s.CameraFov;
        }

        void OnPsdlReplaced() {
            FrameCity(false);
            framedOnce = true;
        }

        /// <summary>Places the camera so that the city's bounding sphere is in view.</summary>
        public void FrameCity(bool animate) {
            var d = Doc.Psdl.Dimensions;
            var center = d.Center.ToGodot();
            float radius = Math.Max(d.Radius, 50);
            if (Doc.Psdl.Vertices.Count == 0) {
                center = Vector3.Zero;
                radius = 200;
            }
            SetRotation(new Vector3(-40, 0, 0));
            zoomFactor = DefaultZoomFactor;
            var target = center + camera.Basis.Z * radius * 1.4f;
            if (animate) _ = MoveTo(target);
            else camera.Position = target;
        }

        void Autofocus() {
            var psdl = Doc.Psdl;
            var sum = Vector3.Zero;
            int n = 0;
            foreach (int v in Doc.SelectedVertices) {
                if (v < 0 || v >= psdl.Vertices.Count) continue;
                sum += psdl.Vertices[v].ToGodot();
                n++;
            }
            if (n == 0) {
                foreach (int b in Doc.SelectedBlocks) {
                    if (b < 0 || b >= psdl.Blocks.Count) continue;
                    sum += psdl.BlockCenter(psdl.Blocks[b]).ToGodot();
                    n++;
                }
            }
            if (n == 0) {
                Doc.Status("Select vertices or a block to focus on.");
                return;
            }
            ZoomInOutOfView(sum / n, 100);
        }

        public override void _Process(double delta) {
            if (camera == null) return;
            bool hovered = container.GetGlobalRect().HasPoint(GetGlobalMousePosition()) && IsVisibleInTree();
            if (hovered) CityScene.Instance?.FollowCamera(camera);
            if (zoomingIn || !hovered && !rotating && !panning) return;
            // keyboard zoom/pan only acts when no text field has focus
            if (GetViewport().GuiGetFocusOwner() is LineEdit or TextEdit) return;
            if (Input.IsKeyPressed(Key.Pageup)) Zoom(1);
            else if (Input.IsKeyPressed(Key.Pagedown)) Zoom(-1);
            bool homePan = hovered && Input.IsKeyPressed(Key.Home);
            if (homePan && !panning && !middleDown) {
                oldMouse = MousePoint();
                panning = true;
            } else if (!homePan && panning && !middleDown) {
                panning = false;
            }
            if (panning && homePan) Pan();
        }

        Vector2 MousePoint() {
            var size = container.Size;
            var p = container.GetLocalMousePosition();
            return new Vector2(p.X / Math.Max(1, size.X), p.Y / Math.Max(1, size.Y));
        }

        void OnViewInput(InputEvent e) {
            if (zoomingIn) return;
            if (e is InputEventMouseButton mb) {
                switch (mb.ButtonIndex) {
                    case MouseButton.WheelUp when mb.Pressed:
                        Zoom(1);
                        break;
                    case MouseButton.WheelDown when mb.Pressed:
                        Zoom(-1);
                        break;
                    case MouseButton.Right:
                        rotating = mb.Pressed && !panning;
                        oldMouse = MousePoint();
                        break;
                    case MouseButton.Middle:
                        if (mb.Pressed) {
                            if (rotating) break;
                            oldMouse = MousePoint();
                            middleOrigin = oldMouse;
                            panning = true;
                            canZoomIn = true;
                            middleDown = true;
                        } else {
                            if (middleDown && panning && canZoomIn) ZoomInAtMouse(mb.Position);
                            panning = false;
                            middleDown = false;
                        }
                        break;
                    case MouseButton.Left:
                        if (mb.Pressed) {
                            leftDown = true;
                            leftOrigin = mb.Position;
                        } else if (leftDown) {
                            leftDown = false;
                            if (leftOrigin.DistanceTo(mb.Position) < 4) Pick(mb.Position);
                        }
                        break;
                }
                container.AcceptEvent();
            } else if (e is InputEventMouseMotion) {
                if (rotating) Rotate();
                else if (panning && middleDown) {
                    if (middleOrigin.DistanceTo(MousePoint()) > 0.001f) canZoomIn = false;
                    Pan();
                }
            }
        }

        void Rotate() {
            var p = MousePoint();
            var d = (p - oldMouse) * RotSpeed;
            curRotation += new Vector3(-d.Y, -d.X, 0);
            curRotation.X = Mathf.Clamp(curRotation.X, -90, 90);
            camera.RotationDegrees = curRotation;
            oldMouse = p;
        }

        void Pan() {
            var p = MousePoint();
            var d = PanSpeed * zoomFactor * zoomFactor * (p - oldMouse) / 100f;
            camera.Position -= camera.Basis.Y * -d.Y + camera.Basis.X * d.X;
            oldMouse = p;
        }

        void Zoom(float value) {
            zoomFactor = Math.Max(zoomFactor - value, 5);
            camera.Position += value * zoomFactor * ZoomSpeed * -camera.Basis.Z / 10f;
        }

        (Vector3 origin, Vector3 dir) Ray(Vector2 containerPos) {
            var scale = viewport.Size / container.Size;
            var p = containerPos * scale;
            return (camera.ProjectRayOrigin(p), camera.ProjectRayNormal(p));
        }

        void Pick(Vector2 position) {
            var scene = CityScene.Instance;
            if (scene == null) return;
            var (origin, dir) = Ray(position);
            int block = scene.Raycast(origin, dir, out var hit);
            if (block < 0) {
                Doc.SelectBlocks(Array.Empty<int>(), this);
                return;
            }
            Doc.SelectBlocks(new[] { block }, this);
            Doc.Status($"Block {block:x} at {hit.X:0.##}, {hit.Y:0.##}, {hit.Z:0.##}");
        }

        void ZoomInAtMouse(Vector2 position) {
            var scene = CityScene.Instance;
            if (scene == null) return;
            var (origin, dir) = Ray(position);
            if (scene.Raycast(origin, dir, out var hit) < 0) return;
            var target = ClosestPointOnPlane(hit, -camera.Basis.Z, camera.Position);
            _ = MoveTo(target, hit);
        }

        public void ZoomInOutOfView(Vector3 point, float distance) {
            _ = MoveTo(point + camera.Basis.Z * distance, point);
        }

        static Vector3 ClosestPointOnPlane(Vector3 point, Vector3 normal, Vector3 planePoint) {
            normal = normal.Normalized();
            return point - normal.Dot(point - planePoint) * normal;
        }

        static float EaseOutCubic(float t) => (t - 1) * (t - 1) * (t - 1) + 1;

        async Task MoveTo(Vector3 target, Vector3? focus = null) {
            var start = camera.Position;
            ulong t0 = Time.GetTicksMsec();
            const float duration = 0.5f;
            zoomingIn = true;
            zoomFactor = DefaultZoomFactor;
            while (true) {
                float a = Math.Min(1, (Time.GetTicksMsec() - t0) / 1000f / duration);
                camera.Position = start.Lerp(target, EaseOutCubic(a));
                if (a >= 1) break;
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                if (!IsInstanceValid(camera)) return;
            }
            if (focus != null) zoomFactor = Mathf.Sqrt(camera.Position.DistanceTo(focus.Value)) * 4 + 5;
            zoomingIn = false;
        }
    }
}
