using System;
using System.Threading.Tasks;
using Godot;
using PsdlViewer.Core;
using PsdlViewer.Render;

namespace PsdlViewer.UI {
    /// <summary>
    /// 3D view of the city. Several can be open at once; they all show the same world.
    /// Camera controls follow the km2 city builder, with its two schemes (see ControlsDescription);
    /// the toolbar has the original viewer's buttons.
    /// </summary>
    public partial class TerrainView : DockPanel {
        public override string Kind => "Terrain";

        const float DefaultZoomFactor = 20;
        const float RotSpeed = 200, PanSpeed = 10, ZoomSpeed = 2;
        // scheme B
        const float MinPivotDistance = 1;
        const float DollyStep = 1.2f; // distance ratio of one wheel step
        const float KeyDollyRate = 10; // wheel steps per second while holding PgUp / PgDn
        const float WheelPanStep = 0.1f; // fraction of the view size panned by one wheel step
        const float DragZoomSpeed = 3;

        enum DragMode { None, Orbit, Pan, Zoom }

        SubViewportContainer container;
        SubViewport viewport;
        Camera3D camera;
        Label hint;
        Vector3 curRotation = new(-35, 0, 0);
        float zoomFactor = DefaultZoomFactor;
        Vector2 oldMouse, middleOrigin;
        bool rotating, panning, middleDown, canZoomIn, zoomingIn, leftDown;
        Vector2 leftOrigin;
        DragMode drag;
        bool dragFromMiddle, emulatedMiddle;
        Vector3 pivot;
        float lastPivotDistance = 50;
        static bool framedOnce;

        static bool SchemeB => Settings.Current.CameraControls == Settings.CameraSchemeB;

        public static string ControlsDescription(int scheme) => scheme == Settings.CameraSchemeB
            ? "Middle drag: orbit around the center of the view (click to zoom in on a point)\nShift + middle drag / Home + mouse: pan\nCtrl + middle drag, wheel, PgUp / PgDn: zoom\nCtrl / Shift + wheel: pan"
            : "Right drag: look\nMiddle drag / Home + mouse: pan (click to zoom in on a point)\nWheel / PgUp / PgDn: zoom";

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
            bar.AddChild(IconButton("autofocus", "Focus on the selected vertices (or the selected room)", Autofocus));
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
            hint.Text = ControlsDescription(s.CameraControls).Replace("\n", "   ") + "   Left click: select room / INST object (attribute of the selected room)";
            // the scheme may have changed in the middle of a drag
            rotating = panning = middleDown = emulatedMiddle = false;
            drag = DragMode.None;
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
            // damaged files can have garbage dimensions
            if (Doc.Psdl.Vertices.Count == 0 || !center.IsFinite() || !float.IsFinite(radius)) {
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
                foreach (int b in Doc.SelectedRooms) {
                    if (b < 0 || b >= psdl.Rooms.Count) continue;
                    sum += psdl.RoomCenter(psdl.Rooms[b]).ToGodot();
                    n++;
                }
            }
            if (n == 0) {
                Doc.Status("Select vertices or a room to focus on.");
                return;
            }
            ZoomInOutOfView(sum / n, 100);
        }

        public override void _Process(double delta) {
            if (camera == null) return;
            bool hovered = container.GetGlobalRect().HasPoint(GetGlobalMousePosition()) && IsVisibleInTree();
            if (hovered) CityScene.Instance?.FollowCamera(camera);
            if (zoomingIn || !hovered && !rotating && !panning && drag == DragMode.None) return;
            // keyboard zoom/pan only acts when no text field has focus
            if (GetViewport().GuiGetFocusOwner() is LineEdit or TextEdit) return;
            if (SchemeB) {
                SchemeBProcess((float)delta, hovered);
                return;
            }
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
                var button = mb.ButtonIndex;
                // with the 3-button mouse emulation, a left button press with Alt held acts as the middle button until it is released
                if (button == MouseButton.Left && (emulatedMiddle || mb.Pressed && mb.AltPressed && Settings.Current.EmulateMiddleButton)) {
                    button = MouseButton.Middle;
                    emulatedMiddle = mb.Pressed;
                }
                if (SchemeB && button != MouseButton.Left) {
                    SchemeBButton(mb, button);
                    container.AcceptEvent();
                    return;
                }
                switch (button) {
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
                if (SchemeB) SchemeBDrag();
                else if (rotating) Rotate();
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

        // ---- Scheme B: middle drag orbits around a pivot, with Shift pans and with Ctrl zooms ----

        void SchemeBButton(InputEventMouseButton mb, MouseButton button) {
            int wheel = button switch { MouseButton.WheelUp => 1, MouseButton.WheelDown => -1, _ => 0 };
            if (wheel != 0) {
                if (!mb.Pressed) return;
                if (mb.CtrlPressed) WheelPan(wheel, true);
                else if (mb.ShiftPressed) WheelPan(wheel, false);
                else Dolly(wheel);
                return;
            }
            if (button != MouseButton.Middle) return;
            if (mb.Pressed) {
                if (drag == DragMode.None) StartDrag(mb.ShiftPressed ? DragMode.Pan : mb.CtrlPressed ? DragMode.Zoom : DragMode.Orbit, true);
            } else if (drag != DragMode.None && dragFromMiddle) {
                EndDrag();
                if (canZoomIn) ZoomInAtMouse(mb.Position);
            }
        }

        void SchemeBProcess(float delta, bool hovered) {
            if (Input.IsKeyPressed(Key.Pageup)) Dolly(delta * KeyDollyRate);
            else if (Input.IsKeyPressed(Key.Pagedown)) Dolly(-delta * KeyDollyRate);
            bool homePan = hovered && Input.IsKeyPressed(Key.Home);
            if (homePan && drag == DragMode.None) StartDrag(DragMode.Pan, false);
            else if (!homePan && drag != DragMode.None && !dragFromMiddle) EndDrag();
        }

        void StartDrag(DragMode mode, bool fromMiddle) {
            drag = mode;
            dragFromMiddle = fromMiddle;
            oldMouse = MousePoint();
            middleOrigin = oldMouse;
            canZoomIn = fromMiddle;
            pivot = PickPivot();
        }

        void EndDrag() {
            drag = DragMode.None;
            UpdateZoomFactor(DepthOf(pivot));
        }

        void SchemeBDrag() {
            if (drag == DragMode.None) return;
            var p = MousePoint();
            if (middleOrigin.DistanceTo(p) > 0.001f) canZoomIn = false;
            var d = p - oldMouse;
            switch (drag) {
                case DragMode.Orbit: Orbit(d); break;
                case DragMode.Pan: PanAtPivot(d); break;
                case DragMode.Zoom: DragZoom(d); break;
            }
            oldMouse = p;
        }

        Vector3 Forward => -camera.Basis.Z.Normalized();

        /// <summary>The point under the center of the view, or a point at the last pivot distance if there is nothing there.</summary>
        Vector3 PickPivot() {
            var scene = CityScene.Instance;
            var (origin, dir) = Ray(container.Size / 2);
            if (scene != null && scene.Raycast(origin, dir, out var hit) >= 0) {
                lastPivotDistance = Math.Max(camera.Position.DistanceTo(hit), MinPivotDistance);
                return hit;
            }
            return camera.Position + Forward * lastPivotDistance;
        }

        float DepthOf(Vector3 point) => Math.Max((point - camera.Position).Dot(Forward), MinPivotDistance);

        /// <summary>World size of the view at the given depth.</summary>
        Vector2 ViewSizeAt(float depth) {
            var size = container.Size;
            float h = 2 * depth * Mathf.Tan(Mathf.DegToRad(camera.Fov) / 2);
            return new Vector2(h * size.X / Math.Max(1, size.Y), h);
        }

        /// <summary>Keeps scheme A's speeds consistent with the distance reached.</summary>
        void UpdateZoomFactor(float depth) => zoomFactor = Mathf.Sqrt(depth) * 4 + 5;

        void Orbit(Vector2 d) {
            float yaw = Mathf.DegToRad(-d.X * RotSpeed);
            float curPitch = Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(Forward.Y, -1, 1)));
            float newPitch = Mathf.Clamp(curPitch - d.Y * RotSpeed, -89, 89);
            float pitch = Mathf.DegToRad(newPitch - curPitch);
            var rot = new Basis(Vector3.Up, yaw) * new Basis(camera.Basis.X.Normalized(), pitch);
            camera.Position = pivot + rot * (camera.Position - pivot);
            camera.Basis = (rot * camera.Basis).Orthonormalized();
            curRotation = camera.RotationDegrees;
        }

        /// <summary>The point under the cursor at the depth of the pivot follows the cursor.</summary>
        void PanAtPivot(Vector2 d) {
            var size = ViewSizeAt(DepthOf(pivot));
            var offset = -camera.Basis.X * d.X * size.X + camera.Basis.Y * d.Y * size.Y;
            camera.Position += offset;
            pivot += offset;
        }

        void DragZoom(Vector2 d) {
            float depth = DepthOf(pivot);
            float newDepth = Math.Max(depth * Mathf.Exp(d.Y * DragZoomSpeed), MinPivotDistance);
            camera.Position += Forward * (depth - newDepth);
        }

        /// <summary>Moves toward the point under the center of the view; positive steps zoom in.</summary>
        void Dolly(float steps) {
            float depth = DepthOf(PickPivot());
            float newDepth = Math.Max(depth / Mathf.Pow(DollyStep, steps), MinPivotDistance);
            camera.Position += Forward * (depth - newDepth);
            lastPivotDistance = newDepth;
            UpdateZoomFactor(newDepth);
        }

        void WheelPan(float steps, bool horizontal) {
            var size = ViewSizeAt(DepthOf(PickPivot()));
            camera.Position += horizontal ? camera.Basis.X * steps * size.X * WheelPanStep : camera.Basis.Y * steps * size.Y * WheelPanStep;
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
            int room = scene.Raycast(origin, dir, out var hit);
            float roomDistance = room >= 0 ? origin.DistanceTo(hit) : float.MaxValue;
            int inst = scene.RaycastInst(origin, dir, out float instDistance);
            int attribute = scene.RaycastAttribute(origin, dir, out var attributeRoom, out float attributeDistance);
            // the selected room's attribute geometry is the same as the rendered city surface, hence the tolerance
            if (attribute >= 0 && attributeDistance <= roomDistance + 0.05f && attributeDistance < instDistance) {
                var a = attributeRoom.Attributes[attribute];
                var at = origin + dir * attributeDistance;
                Doc.SelectInst(Array.Empty<int>(), this);
                Doc.SelectRoomAttributes(attributeRoom, new[] { a }, this);
                Doc.Status($"Room {Doc.Psdl.Rooms.IndexOf(attributeRoom):x}, attribute {attribute:x} ({a}) at {at.X:0.##}, {at.Y:0.##}, {at.Z:0.##}");
                return;
            }
            if (inst >= 0 && instDistance < roomDistance) {
                // an INST object in front of the city: select it and the room it belongs to
                var c = Doc.Inst.Components[inst];
                var at = origin + dir * instDistance;
                Doc.SelectInst(new[] { inst }, this);
                Doc.SelectRooms(c.Room >= 0 ? new[] { c.Room } : Array.Empty<int>(), this);
                Doc.Status($"INST {inst:x} ({c.Pkg}, room {c.Room:x}) at {at.X:0.##}, {at.Y:0.##}, {at.Z:0.##}");
                return;
            }
            Doc.SelectInst(Array.Empty<int>(), this);
            if (room < 0) {
                Doc.SelectRooms(Array.Empty<int>(), this);
                return;
            }
            Doc.SelectRooms(new[] { room }, this);
            Doc.Status($"Room {room:x} at {hit.X:0.##}, {hit.Y:0.##}, {hit.Z:0.##}");
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
