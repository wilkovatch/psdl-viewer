using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using PsdlViewer.Core;
using PsdlViewer.Formats;
using PsdlViewer.Formats.Psdl;

namespace PsdlViewer.UI.Panels {
    /// <summary>The last PSDL section (RoadListView/RoadView). Known today to be prop paths; names kept from the original.</summary>
    public partial class RoadsPanel : DockPanel {
        public override string Kind => "Roads";
        readonly VirtualTable table = new();
        readonly PropertyGrid grid = new();
        readonly ListEditor forward = new("Forward lanes");
        readonly ListEditor backward = new("Backward lanes");
        readonly ListEditor start = new("Start vertices");
        readonly ListEditor end = new("End vertices");
        readonly ListEditor blocks = new("Blocks");
        readonly Control detail;
        RoadPath current;

        public RoadsPanel() {
            AddChild(new Label { Text = "Roads (prop paths: the meaning of most fields is unknown)", Modulate = new Color(1, 1, 1, 0.6f) });
            var split = new VSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
            AddChild(split);
            table.AddColumn("Index", 50);
            table.AddColumn("Unknown 0", 76);
            table.AddColumn("Unknown 1", 76);
            table.AddColumn("Lanes", 56);
            table.AddColumn("Unknown 2", 76);
            table.AddColumn("Blocks", 160);
            table.CustomMinimumSize = new Vector2(100, 90);
            table.RowCount = () => Doc.Psdl.Roads.Count;
            table.GetCell = (r, c) => {
                var road = Doc.Psdl.Roads[r];
                return c switch {
                    0 => r.ToString("x"),
                    1 => BinaryUtil.Hex(road.Unknown0),
                    2 => BinaryUtil.Hex(road.Unknown1),
                    3 => $"{road.ForwardLanes.Count}+{road.BackwardLanes.Count}",
                    4 => BinaryUtil.Hex(road.Unknown2),
                    _ => string.Join(" ", road.Blocks.Select(b => b.ToString("x")))
                };
            };
            table.SelectionChanged += () => Show(table.SelectedRow);
            table.ContextMenu = () => {
                int[] sel = table.SelectedRows;
                return new List<(string, Action, bool)> {
                    ("Add last", () => Add(Doc.Psdl.Roads.Count), true),
                    ("Add before", () => Add(sel.Length > 0 ? sel[0] : 0), true),
                    ("Add after", () => Add(sel.Length > 0 ? sel[^1] + 1 : 0), true),
                    ("Remove", () => {
                        foreach (int r in sel.OrderByDescending(r => r)) Doc.Psdl.Roads.RemoveAt(r);
                        table.ClearSelection();
                        Doc.NotifyChanged(DataKind.Roads);
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
            box.AddChild(blocks);
            foreach (var l in new[] { forward, backward, start, end, blocks }) l.Changed += Edited;
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
            index = Math.Clamp(index, 0, Doc.Psdl.Roads.Count);
            Doc.Psdl.Roads.Insert(index, new RoadPath());
            Doc.NotifyChanged(DataKind.Roads);
            table.Select(new[] { index });
        }

        void Edited() {
            table.Refresh();
            Doc.NotifyChanged(DataKind.Roads);
        }

        void Show(int index) {
            current = index >= 0 && index < Doc.Psdl.Roads.Count ? Doc.Psdl.Roads[index] : null;
            detail.Visible = current != null;
            grid.Clear();
            if (current == null) return;
            var road = current;
            grid.AddHex("Unknown 0:", () => road.Unknown0, v => road.Unknown0 = v & 0xffff);
            grid.AddHex("Unknown 1:", () => road.Unknown1, v => road.Unknown1 = v & 0xffff);
            grid.AddHex("Unknown 2:", () => road.Unknown2, v => road.Unknown2 = v & 0xffff, 4, "Looks like flags");
            forward.BindFloats(() => road.ForwardLanes, true, "Density?");
            backward.BindFloats(() => road.BackwardLanes, true, "Density?");
            start.BindInts(() => road.StartVertices, ListEditor.VertexInfo, true, false, "Vertex");
            end.BindInts(() => road.EndVertices, ListEditor.VertexInfo, true, false, "Vertex");
            blocks.BindInts(() => road.Blocks, ListEditor.BlockInfo, false, true, "Block");
        }

        void OnReplaced() {
            table.Reset();
            Show(-1);
        }

        void OnDataChanged(DataKind kind) {
            if (kind == DataKind.Roads || kind == DataKind.Blocks) {
                table.Refresh();
                if (current != null && !Doc.Psdl.Roads.Contains(current)) Show(-1);
            }
        }
    }
}
