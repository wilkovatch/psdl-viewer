using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using PsdlViewer.Core;
using PsdlViewer.Formats;

namespace PsdlViewer.UI.Panels {
    /// <summary>Pathset list with path and point editors (PathsetListView/PathsetView/PathView).</summary>
    public partial class PathsetsPanel : DockPanel {
        public override string Kind => "Pathsets";
        readonly VirtualTable sets = new();
        readonly VirtualTable paths = new();
        readonly VirtualTable points = new();
        readonly PropertyGrid pathGrid = new();
        readonly Control pathBox, pathDetail;
        Pathset currentSet;
        PropPath currentPath;

        public PathsetsPanel() {
            var bar = new HBoxContainer();
            bar.AddChild(ToolButton("Open pathset...", () => Main.Instance.OpenPathset()));
            bar.AddChild(ToolButton("Save pathset...", SaveSelected, "Save the selected pathset"));
            bar.AddChild(ToolButton("Re-render", () => Main.Instance.RenderPathsets(), "Update the props in the 3D view"));
            AddChild(bar);
            var split = new VSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
            AddChild(split);

            sets.AddColumn("Index", 46);
            sets.AddColumn("File", 220);
            sets.AddColumn("Paths", 50, false, HorizontalAlignment.Right);
            sets.AddColumn("Unknown 0", 76, true);
            sets.CustomMinimumSize = new Vector2(100, 70);
            sets.RowCount = () => Doc.Pathsets.Count;
            sets.GetCell = (r, c) => {
                var s = Doc.Pathsets[r];
                return c switch {
                    0 => r.ToString("x"),
                    1 => s.DisplayName,
                    2 => s.Paths.Count.ToString(),
                    _ => BinaryUtil.Hex((int)s.CurrentPath)
                };
            };
            sets.SetCell = (r, c, text) => {
                if (!BinaryUtil.TryParseHex(text, out int v)) return "Enter a hexadecimal number.";
                Doc.Pathsets[r].CurrentPath = (uint)v;
                return null;
            };
            sets.SelectionChanged += () => ShowSet(sets.SelectedRow);
            sets.ContextMenu = () => {
                int[] sel = sets.SelectedRows;
                return new List<(string, Action, bool)> {
                    ("Open pathset...", () => Main.Instance.OpenPathset(), true),
                    ("Save pathset...", SaveSelected, sel.Length == 1),
                    ("Remove", () => {
                        foreach (int r in sel.OrderByDescending(r => r)) Doc.Pathsets.RemoveAt(r);
                        sets.ClearSelection();
                        Doc.NotifyChanged(DataKind.Pathsets);
                        Main.Instance.RenderPathsets();
                    }, sel.Length > 0)
                };
            };
            split.AddChild(sets);

            var lower = new VSplitContainer();
            pathBox = lower;
            paths.AddColumn("Index", 46);
            paths.AddColumn("Name", 200, true);
            paths.AddColumn("Type", 100);
            paths.AddColumn("Points", 50, false, HorizontalAlignment.Right);
            paths.AddColumn("Spacing", 60, false, HorizontalAlignment.Right);
            paths.CustomMinimumSize = new Vector2(100, 70);
            paths.RowCount = () => currentSet?.Paths.Count ?? 0;
            paths.GetCell = (r, c) => {
                var p = currentSet.Paths[r];
                return c switch {
                    0 => r.ToString("x"),
                    1 => p.Name,
                    2 => p.Type < PropPath.TypeNames.Length ? PropPath.TypeNames[p.Type] : p.Type.ToString(),
                    3 => p.Points.Count.ToString(),
                    _ => BinaryUtil.F(p.Spacing)
                };
            };
            paths.SetCell = (r, c, text) => {
                currentSet.Paths[r].Name = text.Trim();
                pathGrid.Refresh();
                return null;
            };
            paths.SelectionChanged += () => ShowPath(paths.SelectedRow);
            paths.ContextMenu = () => {
                int[] sel = paths.SelectedRows;
                bool ok = currentSet != null;
                return new List<(string, Action, bool)> {
                    ("Add last", () => {
                        currentSet.Paths.Add(new PropPath());
                        paths.Refresh();
                        sets.Refresh();
                    }, ok),
                    ("Remove", () => {
                        foreach (int r in sel.OrderByDescending(r => r)) currentSet.Paths.RemoveAt(r);
                        paths.ClearSelection();
                        paths.Refresh();
                        sets.Refresh();
                    }, ok && sel.Length > 0)
                };
            };
            lower.AddChild(paths);

            var pathScroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
            var detailBox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
            pathScroll.AddChild(detailBox);
            pathDetail = pathScroll;
            detailBox.AddChild(pathGrid);
            pathGrid.Changed += () => paths.Refresh();
            detailBox.AddChild(Title("Points"));
            BuildPoints();
            detailBox.AddChild(points);
            lower.AddChild(pathScroll);
            split.AddChild(lower);
            pathBox.Visible = false;
            pathDetail.Visible = false;

            Subscribe(() => Doc.DataChanged += OnDataChanged, () => Doc.DataChanged -= OnDataChanged);
        }

        void BuildPoints() {
            points.AddColumn("Index", 46);
            points.AddColumn("Room / misc", 90, true);
            points.AddColumn("X", 80, true, HorizontalAlignment.Right);
            points.AddColumn("Y", 80, true, HorizontalAlignment.Right);
            points.AddColumn("Z", 80, true, HorizontalAlignment.Right);
            points.CustomMinimumSize = new Vector2(100, 160);
            points.RowCount = () => currentPath?.Points.Count ?? 0;
            points.GetCell = (r, c) => {
                var p = currentPath.Points[r];
                return c switch {
                    0 => r.ToString("x"),
                    1 => BinaryUtil.Hex((int)p.Misc, 8),
                    2 => BinaryUtil.F(p.Position.X),
                    3 => BinaryUtil.F(p.Position.Y),
                    _ => BinaryUtil.F(p.Position.Z)
                };
            };
            points.SetCell = (r, c, text) => {
                var p = currentPath.Points[r];
                if (c == 1) {
                    if (!BinaryUtil.TryParseHex(text, out int v)) return "Enter a hexadecimal number.";
                    p.Misc = (uint)v;
                    return null;
                }
                if (!BinaryUtil.TryParseFloat(text, out float f)) return "Enter a number.";
                var pos = p.Position;
                if (c == 2) pos.X = f;
                else if (c == 3) pos.Y = f;
                else pos.Z = f;
                p.Position = pos;
                return null;
            };
            points.ContextMenu = () => {
                int[] sel = points.SelectedRows;
                bool ok = currentPath != null;
                return new List<(string, Action, bool)> {
                    ("Add last", () => {
                        var last = currentPath.Points.Count > 0 ? currentPath.Points[^1].Clone() : new PathPoint();
                        currentPath.Points.Add(last);
                        points.Refresh();
                        paths.Refresh();
                    }, ok),
                    ("Remove", () => {
                        foreach (int r in sel.OrderByDescending(r => r)) currentPath.Points.RemoveAt(r);
                        points.ClearSelection();
                        points.Refresh();
                        paths.Refresh();
                    }, ok && sel.Length > 0)
                };
            };
        }

        void ShowSet(int index) {
            currentSet = index >= 0 && index < Doc.Pathsets.Count ? Doc.Pathsets[index] : null;
            pathBox.Visible = currentSet != null;
            paths.Reset();
            ShowPath(-1);
        }

        void ShowPath(int index) {
            currentPath = currentSet != null && index >= 0 && index < currentSet.Paths.Count ? currentSet.Paths[index] : null;
            pathDetail.Visible = currentPath != null;
            pathGrid.Clear();
            points.Reset();
            if (currentPath == null) return;
            var p = currentPath;
            pathGrid.AddText("Name:", () => p.Name, v => p.Name = v.Trim(), "PKG (prop) or texture (decal) name");
            pathGrid.AddHex("Unknown 1:", () => (int)p.Unknown1, v => p.Unknown1 = (uint)v, 8);
            pathGrid.AddOption("Type", PropPath.TypeNames, () => Math.Clamp(p.Type, 0, 2), v => p.Type = v);
            pathGrid.AddFloat("Spacing:", () => p.Spacing, v => p.Spacing = v, "Metres between props on line strips");
            pathGrid.AddHex("Unknown 3:", () => p.Unknown3, v => p.Unknown3 = v & 0xffff);
        }

        void SaveSelected() {
            var set = currentSet;
            if (set == null) {
                Dialogs.Info("Select a pathset first.");
                return;
            }
            Dialogs.SaveFile("Save pathset file", new[] { "*.pathset ; Pathset files" }, System.IO.Path.GetFileName(set.FileName), path => {
                try {
                    set.Save(path);
                    sets.Refresh();
                    Doc.Status("Wrote " + path);
                } catch (Exception e) {
                    Dialogs.Error("Save pathset: " + e.Message);
                }
            });
        }

        void OnDataChanged(DataKind kind) {
            if (kind != DataKind.Pathsets) return;
            sets.Refresh();
            if (currentSet != null && !Doc.Pathsets.Contains(currentSet)) ShowSet(-1);
        }
    }
}
