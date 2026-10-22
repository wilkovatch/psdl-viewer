using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using PsdlViewer.Core;
using PsdlViewer.Formats;

namespace PsdlViewer.UI.Panels {
    /// <summary>INST component list (INSTComponentListView/INSTComponentView).</summary>
    public partial class InstPanel : DockPanel {
        public override string Kind => "INST";
        readonly VirtualTable table = new();
        readonly PropertyGrid grid = new();
        readonly Label file = new() { Modulate = new Color(1, 1, 1, 0.6f), ClipText = true };
        readonly Control detail;
        InstComponent current;
        bool syncing;

        public InstPanel() {
            var bar = new HBoxContainer();
            bar.AddChild(file);
            file.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            bar.AddChild(ToolButton("Re-render", () => Main.Instance.RenderInst(), "Update the INST objects in the 3D view"));
            AddChild(bar);
            var split = new VSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
            AddChild(split);
            table.AddColumn("Index", 50);
            table.AddColumn("Room", 56, true);
            table.AddColumn("Type", 46);
            table.AddColumn("Geometry", 230, true);
            table.AddColumn("Modifiers", 70, true);
            table.CustomMinimumSize = new Vector2(100, 90);
            table.RowCount = () => Doc.Inst.Components.Count;
            table.GetCell = (r, c) => {
                var x = Doc.Inst.Components[r];
                return c switch {
                    0 => r.ToString("x"),
                    1 => x.Room.ToString("x"),
                    2 => x.IsSimple ? "Simple" : "Coord",
                    3 => x.Pkg,
                    _ => BinaryUtil.Hex(x.Modifiers)
                };
            };
            table.SetCell = (r, c, text) => {
                var x = Doc.Inst.Components[r];
                if (c == 3) x.Pkg = text.Trim();
                else {
                    if (!BinaryUtil.TryParseHex(text, out int v)) return $"\"{text}\" is not a hexadecimal number.";
                    if (c == 1) x.Room = v;
                    else x.Modifiers = v & 0xffff;
                }
                grid.Refresh();
                Doc.NotifyChanged(DataKind.Inst);
                return null;
            };
            table.SelectionChanged += () => {
                Show(table.SelectedRow);
                if (syncing) return;
                syncing = true;
                Doc.SelectInst(table.SelectedRows, this);
                if (current != null && current.Room >= 0) Doc.SelectRooms(new[] { current.Room }, this);
                syncing = false;
            };
            table.ContextMenu = () => {
                int[] sel = table.SelectedRows;
                return new List<(string, Action, bool)> {
                    ("Add last", () => Add(Doc.Inst.Components.Count), true),
                    ("Add before", () => Add(sel.Length > 0 ? sel[0] : 0), true),
                    ("Add after", () => Add(sel.Length > 0 ? sel[^1] + 1 : 0), true),
                    ("Remove", () => {
                        foreach (int r in sel.OrderByDescending(r => r)) Doc.Inst.Components.RemoveAt(r);
                        table.ClearSelection();
                        Doc.NotifyChanged(DataKind.Inst);
                    }, sel.Length > 0),
                    ("Duplicate", () => {
                        foreach (int r in sel) Doc.Inst.Components.Add(Doc.Inst.Components[r].Clone());
                        Doc.NotifyChanged(DataKind.Inst);
                    }, sel.Length > 0)
                };
            };
            split.AddChild(table);
            var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
            scroll.AddChild(grid);
            detail = scroll;
            detail.Visible = false;
            grid.Changed += () => {
                grid.Refresh();
                table.Refresh();
                Doc.NotifyChanged(DataKind.Inst);
            };
            split.AddChild(scroll);
            Subscribe(() => {
                Doc.InstReplaced += OnReplaced;
                Doc.DataChanged += OnDataChanged;
                Doc.InstSelected += OnInstSelected;
            }, () => {
                Doc.InstReplaced -= OnReplaced;
                Doc.DataChanged -= OnDataChanged;
                Doc.InstSelected -= OnInstSelected;
            });
            OnReplaced();
        }

        void Add(int index) {
            Dialogs.Choose("Add INST component", "Select component type", new[] { "Simple", "Coordinate system" }, type => {
                InstComponent c = type == 0 ? new InstSimple() : new InstCoordinate();
                index = Math.Clamp(index, 0, Doc.Inst.Components.Count);
                Doc.Inst.Components.Insert(index, c);
                Doc.NotifyChanged(DataKind.Inst);
                table.Select(new[] { index });
            });
        }

        void Show(int index) {
            current = index >= 0 && index < Doc.Inst.Components.Count ? Doc.Inst.Components[index] : null;
            detail.Visible = current != null;
            grid.Clear();
            if (current == null) return;
            var c = current;
            grid.AddText("Geometry:", () => c.Pkg, v => c.Pkg = v.Trim(), "PKG name without extension");
            grid.AddHex("Room:", () => c.Room, v => c.Room = v);
            grid.AddHex("Modifiers:", () => c.Modifiers, v => c.Modifiers = v & 0xffff, 4, "Paint job in the low byte, flags in the high byte");
            grid.AddInt("Paint job:", () => c.PaintJob, v => c.Modifiers = (c.Modifiers & 0xff00) | Math.Clamp(v, 0, 0xff));
            foreach (var (flag, name, description) in InstComponent.Flags) {
                grid.AddCheck(name, () => (c.Modifiers & flag) != 0, v => c.Modifiers = v ? c.Modifiers | flag : c.Modifiers & ~flag, description);
            }
            grid.AddVector("Origin:", () => c.Origin, v => c.Origin = v);
            if (c is InstSimple s) {
                grid.AddFloat("Scale:", () => s.Scale, v => s.SetOrientationScale(v, s.Angle));
                grid.AddFloat("Angle:", () => Mathf.RadToDeg(s.Angle), v => s.SetOrientationScale(s.Scale, Mathf.DegToRad(v)), "Degrees");
            } else if (c is InstCoordinate cs) {
                grid.AddVector("X axis:", () => cs.XAxis, v => cs.XAxis = v);
                grid.AddVector("Y axis:", () => cs.YAxis, v => cs.YAxis = v);
                grid.AddVector("Z axis:", () => cs.ZAxis, v => cs.ZAxis = v);
            }
        }

        void OnReplaced() {
            file.Text = string.IsNullOrEmpty(Doc.InstPath) ? "No INST file loaded (File > Open INST)" : Doc.InstPath;
            table.Reset();
            Show(-1);
        }

        void OnDataChanged(DataKind kind) {
            if (kind != DataKind.Inst) return;
            table.Refresh();
            if (current != null && !Doc.Inst.Components.Contains(current)) Show(-1);
        }

        void OnInstSelected(int[] components, object source) {
            if (source == this || syncing) return;
            syncing = true;
            table.Select(components, true, true);
            syncing = false;
        }
    }
}
