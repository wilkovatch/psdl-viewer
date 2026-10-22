using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using PsdlViewer.Core;
using PsdlViewer.Formats;
using PsdlViewer.Formats.Psdl;

namespace PsdlViewer.UI.Panels {
    /// <summary>The last PSDL section, room paths (the original viewer's RoadListView/RoadView), used by the game as prop paths.</summary>
    public partial class RoomPathsPanel : DockPanel {
        public override string Kind => "Room paths";
        readonly VirtualTable table = new();
        readonly PropertyGrid grid = new();
        readonly ListEditor forward = new("Forward lane positions");
        readonly ListEditor backward = new("Backward lane positions");
        readonly ListEditor start = new("Start cross section");
        readonly ListEditor end = new("End cross section");
        readonly ListEditor rooms = new("Rooms");
        readonly Control detail;
        RoomPath current;

        public RoomPathsPanel() {
            AddChild(new Label { Text = "Room paths (prop paths: the props of the first room's prop rule are placed along the roads of all the rooms)", Modulate = new Color(1, 1, 1, 0.6f) });
            var split = new VSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
            AddChild(split);
            table.AddColumn("Index", 50);
            table.AddColumn("Flags", 84);
            table.AddColumn("Lanes", 56);
            table.AddColumn("Unknown 6", 76);
            table.AddColumn("Rooms", 160);
            table.CustomMinimumSize = new Vector2(100, 90);
            table.RowCount = () => Doc.Psdl.RoomPaths.Count;
            table.GetCell = (r, c) => {
                var path = Doc.Psdl.RoomPaths[r];
                return c switch {
                    0 => r.ToString("x"),
                    1 => BinaryUtil.Hex((int)path.Flags, 8),
                    2 => $"{path.ForwardLanes.Count}+{path.BackwardLanes.Count}",
                    3 => BinaryUtil.Hex(path.Unknown6),
                    _ => (path.NegativeRooms ? "(-) " : "") + string.Join(" ", path.Rooms.Select(b => b.ToString("x")))
                };
            };
            table.SelectionChanged += () => Show(table.SelectedRow);
            table.ContextMenu = () => {
                int[] sel = table.SelectedRows;
                return new List<(string, Action, bool)> {
                    ("Add last", () => Add(Doc.Psdl.RoomPaths.Count), true),
                    ("Add before", () => Add(sel.Length > 0 ? sel[0] : 0), true),
                    ("Add after", () => Add(sel.Length > 0 ? sel[^1] + 1 : 0), true),
                    ("Remove", () => {
                        foreach (int r in sel.OrderByDescending(r => r)) Doc.Psdl.RoomPaths.RemoveAt(r);
                        table.ClearSelection();
                        Doc.NotifyChanged(DataKind.RoomPaths);
                    }, sel.Length > 0)
                };
            };
            split.AddChild(table);

            var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
            var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            scroll.AddChild(box);
            detail = scroll;
            box.AddChild(grid);
            grid.Changed += Edited;
            var lanes = new HBoxContainer();
            lanes.AddChild(forward);
            lanes.AddChild(backward);
            box.AddChild(lanes);
            var ends = new HBoxContainer();
            ends.AddChild(start);
            ends.AddChild(end);
            box.AddChild(ends);
            box.AddChild(rooms);
            foreach (var l in new[] { forward, backward, start, end, rooms }) l.Changed += Edited;
            split.AddChild(scroll);
            detail.Visible = false;

            Subscribe(() => {
                Doc.PsdlReplaced += OnReplaced;
                Doc.DataChanged += OnDataChanged;
            }, () => {
                Doc.PsdlReplaced -= OnReplaced;
                Doc.DataChanged -= OnDataChanged;
            });
        }

        void Add(int index) {
            index = Math.Clamp(index, 0, Doc.Psdl.RoomPaths.Count);
            Doc.Psdl.RoomPaths.Insert(index, new RoomPath());
            Doc.NotifyChanged(DataKind.RoomPaths);
            table.Select(new[] { index });
        }

        void Edited() {
            table.Refresh();
            Doc.NotifyChanged(DataKind.RoomPaths);
        }

        void Show(int index) {
            current = index >= 0 && index < Doc.Psdl.RoomPaths.Count ? Doc.Psdl.RoomPaths[index] : null;
            detail.Visible = current != null;
            grid.Clear();
            if (current == null) return;
            var path = current;
            grid.AddHex("Flags:", () => (int)path.Flags, v => path.Flags = (uint)v, 8,
                "Unknown bits, possibly: low word nonzero = place props, high word nonzero = invert the placement");
            grid.AddHex("Unknown 6:", () => path.Unknown6, v => path.Unknown6 = v & 0xffff, 4, "Looks like a bit field");
            grid.AddCheck("Negative room numbers", () => path.NegativeRooms, v => path.NegativeRooms = v, "The room numbers are stored negated in the file (meaning unknown)");
            forward.BindFloats(() => path.ForwardLanes, true, "Position");
            backward.BindFloats(() => path.BackwardLanes, true, "Position");
            start.BindInts(() => path.StartCrossroads, ListEditor.VertexInfo, true, false, "Vertex");
            end.BindInts(() => path.EndCrossroads, ListEditor.VertexInfo, true, false, "Vertex");
            rooms.BindInts(() => path.Rooms, ListEditor.RoomInfo, false, true, "Room");
        }

        void OnReplaced() {
            table.Reset();
            Show(-1);
        }

        void OnDataChanged(DataKind kind) {
            if (kind == DataKind.RoomPaths || kind == DataKind.Rooms) {
                table.Refresh();
                if (current != null && !Doc.Psdl.RoomPaths.Contains(current)) Show(-1);
            }
        }
    }
}
