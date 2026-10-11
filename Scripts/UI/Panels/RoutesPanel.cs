using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using PsdlViewer.Core;
using PsdlViewer.Formats;
using NVector3 = System.Numerics.Vector3;

namespace PsdlViewer.UI.Panels {
    /// <summary>BAI route list (RouteListView/RouteView/RoadDataView/RoadEndView).</summary>
    public partial class RoutesPanel : DockPanel {
        public override string Kind => "Routes";
        readonly VirtualTable table = new();
        readonly TabContainer detail = new() { SizeFlagsVertical = SizeFlags.ExpandFill };
        readonly Label file = new() { Modulate = new Color(1, 1, 1, 0.6f), ClipText = true, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        BaiRoute current;

        public RoutesPanel() {
            AddChild(file);
            var split = new VSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
            AddChild(split);
            table.AddColumn("Index", 50);
            table.AddColumn("ID", 56);
            table.AddColumn("Points", 56, false, HorizontalAlignment.Right);
            table.AddColumn("Lanes R/L", 70);
            table.AddColumn("Blocks", 200);
            table.CustomMinimumSize = new Vector2(100, 90);
            table.RowCount = () => Doc.Bai.Routes.Count;
            table.GetCell = (r, c) => {
                var route = Doc.Bai.Routes[r];
                return c switch {
                    0 => r.ToString("x"),
                    1 => route.Id.ToString("x"),
                    2 => route.Points.ToString(),
                    3 => $"{route.Right.Lanes}/{route.Left.Lanes}",
                    _ => string.Join(" ", route.Blocks.Select(b => b.ToString("x")))
                };
            };
            table.SelectionChanged += () => {
                Show(table.SelectedRow);
                Doc.SelectRoutes(table.SelectedRows);
            };
            split.AddChild(table);
            split.AddChild(detail);
            detail.Visible = false;
            Subscribe(() => Doc.BaiReplaced += OnReplaced, () => Doc.BaiReplaced -= OnReplaced);
            OnReplaced();
        }

        static VirtualTable Table(int rows, params (string title, Func<int, string> get)[] columns) {
            var t = new VirtualTable { CustomMinimumSize = new Vector2(100, 120) };
            t.AddColumn("Index", 46);
            foreach (var (title, _) in columns) t.AddColumn(title, title.StartsWith("Vert") || title.Length > 6 ? 170 : 80);
            t.RowCount = () => rows;
            t.GetCell = (r, c) => c == 0 ? r.ToString("x") : columns[c - 1].get(r);
            return t;
        }

        static Func<int, string> At(List<float> list) => i => i < list.Count ? BinaryUtil.F(list[i]) : "";
        static Func<int, string> At(List<NVector3> list) => i => i < list.Count ? BinaryUtil.V(list[i]) : "";

        void Show(int index) {
            current = index >= 0 && index < Doc.Bai.Routes.Count ? Doc.Bai.Routes[index] : null;
            int tab = Math.Max(0, detail.CurrentTab);
            foreach (var c in detail.GetChildren()) {
                detail.RemoveChild(c);
                c.QueueFree();
            }
            detail.Visible = current != null;
            if (current == null) return;
            var route = current;
            int n = route.Points;

            var routeBox = new VBoxContainer { Name = "Route" };
            var grid = new PropertyGrid();
            grid.AddHex("ID:", () => route.Id, v => route.Id = v & 0xffff);
            grid.AddHex("Unknown 0:", () => route.Unknown0, v => route.Unknown0 = v & 0xffff, 4, "Flags");
            grid.AddFloat("Unknown 1:", () => route.Unknown1, v => route.Unknown1 = v, "Half width?");
            grid.AddFloat("Unknown 2:", () => route.Unknown2, v => route.Unknown2 = v, "Base speed?");
            grid.Changed += () => table.Refresh();
            routeBox.AddChild(grid);
            var blocks = new ListEditor("Blocks");
            blocks.BindInts(() => route.Blocks, ListEditor.BlockInfo, false, true, "Block");
            blocks.Changed += () => table.Refresh();
            routeBox.AddChild(blocks);
            detail.AddChild(routeBox);

            var center = Table(n, ("Distance", At(route.CentralDistances)), ("Center", At(route.CentralLine)), ("X", At(route.XOrientation)),
                ("Y", At(route.YOrientation)), ("Z", At(route.ZOrientation)), ("W", At(route.WOrientation)));
            foreach (var col in center.Columns.Skip(2)) col.Width = 170;
            center.Name = "Center line";
            detail.AddChild(center);
            detail.AddChild(RoadData("Right", route.Right, n));
            detail.AddChild(RoadData("Left", route.Left, n));
            detail.AddChild(RoadEnd("Start", route.Start));
            detail.AddChild(RoadEnd("End", route.End));
            detail.CurrentTab = Math.Min(tab, detail.GetTabCount() - 1);
        }

        static Control RoadData(string name, BaiRoadData d, int n) {
            var scroll = new ScrollContainer { Name = name, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
            var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            scroll.AddChild(box);
            var grid = new PropertyGrid();
            grid.AddInfo("Lanes:", () => $"{d.Lanes} lanes, {d.Trams} tram rails, {d.Trains} train rails");
            grid.AddHex("Unknown 3:", () => d.Unknown3, v => d.Unknown3 = v & 0xffff);
            grid.AddHex("Unknown 4:", () => d.Unknown4, v => d.Unknown4 = v & 0xffff, 4, "Ambient types?");
            box.AddChild(grid);

            var laneColumns = new List<(string, Func<int, string>)> { ("Distance", At(d.Distances)) };
            for (int k = 0; k < d.Lanes; k++) {
                int lane = k;
                laneColumns.Add(($"Distance {lane}", i => lane * n + i < d.LaneDistances.Count ? BinaryUtil.F(d.LaneDistances[lane * n + i]) : ""));
                laneColumns.Add(($"Vertices {lane}", i => lane * n + i < d.LaneVertices.Count ? BinaryUtil.V(d.LaneVertices[lane * n + i]) : ""));
            }
            box.AddChild(new Label { Text = "Lanes" });
            box.AddChild(Table(n, laneColumns.ToArray()));
            box.AddChild(new Label { Text = "Unknown" });
            box.AddChild(Table(d.Unknown0.Count, ("Unknown", At(d.Unknown0))));
            box.AddChild(new Label { Text = "Sidewalks" });
            box.AddChild(Table(n, ("Inner", At(d.SidewalkInner)), ("Center", At(d.SidewalkCenter)), ("Outer", At(d.SidewalkOuter))));
            if (d.Trams > 0) {
                box.AddChild(new Label { Text = "Trams" });
                var cols = Enumerable.Range(0, d.Trams).Select(k => ($"Vertex {k}", (Func<int, string>)(i => k * n + i < d.TramVertices.Count ? BinaryUtil.V(d.TramVertices[k * n + i]) : ""))).ToArray();
                box.AddChild(Table(n, cols));
            }
            if (d.Trains > 0) {
                box.AddChild(new Label { Text = "Trains" });
                var cols = Enumerable.Range(0, d.Trains).Select(k => ($"Vertex {k}", (Func<int, string>)(i => k * n + i < d.TrainVertices.Count ? BinaryUtil.V(d.TrainVertices[k * n + i]) : ""))).ToArray();
                box.AddChild(Table(n, cols));
            }
            return scroll;
        }

        static Control RoadEnd(string name, BaiRoadEnd e) {
            var grid = new PropertyGrid { Name = name };
            grid.AddHex("Intersection:", () => (int)e.Intersection, v => e.Intersection = (uint)v, 8);
            grid.AddHex("Unknown 0:", () => e.Unknown0, v => e.Unknown0 = v & 0xffff);
            grid.AddHex("Unknown 1:", () => (int)e.Unknown1, v => e.Unknown1 = (uint)v, 8, "Vehicle rule?");
            grid.AddHex("Unknown 2:", () => (int)e.Unknown2, v => e.Unknown2 = (uint)v, 8);
            grid.AddVector("Origin:", () => e.Origin, v => e.Origin = v);
            grid.AddVector("Axis:", () => e.Axis, v => e.Axis = v);
            return grid;
        }

        void OnReplaced() {
            file.Text = string.IsNullOrEmpty(Doc.BaiPath) ? "No BAI file loaded (File > Open BAI)" : $"{Doc.BaiPath}  ({Doc.Bai.Routes.Count} routes, {Doc.Bai.Intersections.Count} intersections)";
            table.Reset();
            Show(-1);
        }
    }
}
