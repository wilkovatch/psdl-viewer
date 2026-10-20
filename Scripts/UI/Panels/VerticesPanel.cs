using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using PsdlViewer.Core;
using PsdlViewer.Formats;
using NVector3 = System.Numerics.Vector3;

namespace PsdlViewer.UI.Panels {
    /// <summary>Vertex list (VertexListView): editing, translation and drag source for vertex references.</summary>
    public partial class VerticesPanel : DockPanel {
        public override string Kind => "Vertices";
        readonly VirtualTable table = new();
        bool syncing;

        public VerticesPanel() {
            AddChild(new Label { Text = "Drag selected vertices into a perimeter or attribute vertex list.", Modulate = new Color(1, 1, 1, 0.6f) });
            table.AddColumn("Index", 56);
            table.AddColumn("X", 90, true, HorizontalAlignment.Right);
            table.AddColumn("Y", 90, true, HorizontalAlignment.Right);
            table.AddColumn("Z", 90, true, HorizontalAlignment.Right);
            table.RowCount = () => Doc.Psdl.Vertices.Count;
            table.GetCell = (r, c) => {
                var v = Doc.Psdl.Vertices[r];
                return c switch {
                    0 => r.ToString("x"),
                    1 => BinaryUtil.F(v.X),
                    2 => BinaryUtil.F(v.Y),
                    _ => BinaryUtil.F(v.Z)
                };
            };
            table.SetCell = (r, c, text) => {
                if (!BinaryUtil.TryParseFloat(text, out float f)) return $"\"{text}\" is not a number.";
                var v = Doc.Psdl.Vertices[r];
                if (c == 1) v.X = f;
                else if (c == 2) v.Y = f;
                else v.Z = f;
                Doc.Psdl.Vertices[r] = v;
                Doc.NotifyChanged(DataKind.Vertices);
                return null;
            };
            table.SelectionChanged += () => {
                if (syncing) return;
                Doc.SelectVertices(table.SelectedRows);
            };
            table.DragSource = () => new DragPayload { Kind = "vertices", Data = table.SelectedRows };
            table.ContextMenu = () => {
                int[] sel = table.SelectedRows;
                return new List<(string, Action, bool)> {
                    ("Add last", () => {
                        Doc.Psdl.Vertices.Add(NVector3.Zero);
                        Doc.NotifyChanged(DataKind.Vertices);
                        table.Select(new[] { Doc.Psdl.Vertices.Count - 1 });
                    }, true),
                    ("Remove", () => Dialogs.Confirm($"Remove {sel.Length} vertices? References to later vertices are NOT renumbered (as in the original viewer).", () => {
                        foreach (int r in sel.OrderByDescending(r => r)) Doc.Psdl.Vertices.RemoveAt(r);
                        table.ClearSelection();
                        Doc.NotifyChanged(DataKind.Vertices);
                    }), sel.Length > 0),
                    ("-", null, true),
                    ("Translate...", () => Translate(sel), sel.Length > 0)
                };
            };
            AddChild(table);
            Subscribe(() => {
                Doc.PsdlReplaced += table.Reset;
                Doc.DataChanged += OnDataChanged;
                Doc.VertexRefsSelected += OnVertexRefs;
            }, () => {
                Doc.PsdlReplaced -= table.Reset;
                Doc.DataChanged -= OnDataChanged;
                Doc.VertexRefsSelected -= OnVertexRefs;
            });
        }

        void Translate(int[] rows) {
            Dialogs.Form("Translate selected vertices", new[] { ("X:", "0"), ("Y:", "0"), ("Z:", "0") }, values => {
                if (!BinaryUtil.TryParseFloat(values[0], out float x) || !BinaryUtil.TryParseFloat(values[1], out float y) || !BinaryUtil.TryParseFloat(values[2], out float z)) {
                    Dialogs.Error("Enter three numbers.");
                    return;
                }
                var d = new NVector3(x, y, z);
                foreach (int r in rows) Doc.Psdl.Vertices[r] += d;
                Doc.NotifyChanged(DataKind.Vertices);
                Doc.Status($"Translated {rows.Length} vertices. Use View > Refresh rooms to update the 3D view.");
            });
        }

        void OnDataChanged(DataKind kind) {
            if (kind == DataKind.Vertices) table.Refresh();
        }

        void OnVertexRefs(int[] refs) {
            syncing = true;
            table.Select(refs, true, false);
            syncing = false;
        }
    }

    /// <summary>Height list (HeightListView).</summary>
    public partial class HeightsPanel : DockPanel {
        public override string Kind => "Heights";
        readonly VirtualTable table = new();

        public HeightsPanel() {
            table.AddColumn("Index", 56);
            table.AddColumn("Height", 110, true, HorizontalAlignment.Right);
            table.RowCount = () => Doc.Psdl.Heights.Count;
            table.GetCell = (r, c) => c == 0 ? r.ToString("x") : BinaryUtil.F(Doc.Psdl.Heights[r]) + " m";
            table.SetCell = (r, c, text) => {
                if (!BinaryUtil.TryParseFloat(text.Replace("m", ""), out float f)) return $"\"{text}\" is not a number.";
                Doc.Psdl.Heights[r] = f;
                Doc.NotifyChanged(DataKind.Heights);
                return null;
            };
            table.ContextMenu = () => {
                int[] sel = table.SelectedRows;
                return new List<(string, Action, bool)> {
                    ("Add last", () => {
                        Doc.Psdl.Heights.Add(0);
                        Doc.NotifyChanged(DataKind.Heights);
                        table.Select(new[] { Doc.Psdl.Heights.Count - 1 });
                    }, true),
                    ("Remove", () => {
                        foreach (int r in sel.OrderByDescending(r => r)) Doc.Psdl.Heights.RemoveAt(r);
                        table.ClearSelection();
                        Doc.NotifyChanged(DataKind.Heights);
                    }, sel.Length > 0),
                    ("-", null, true),
                    ("Adjust...", () => Dialogs.Input("Adjust selected heights", "Add:", "0", text => {
                        if (!BinaryUtil.TryParseFloat(text, out float d)) {
                            Dialogs.Error("Enter a number.");
                            return;
                        }
                        foreach (int r in sel) Doc.Psdl.Heights[r] += d;
                        Doc.NotifyChanged(DataKind.Heights);
                    }), sel.Length > 0)
                };
            };
            AddChild(table);
            Subscribe(() => {
                Doc.PsdlReplaced += table.Reset;
                Doc.DataChanged += OnDataChanged;
            }, () => {
                Doc.PsdlReplaced -= table.Reset;
                Doc.DataChanged -= OnDataChanged;
            });
        }

        void OnDataChanged(DataKind kind) {
            if (kind == DataKind.Heights) table.Refresh();
        }
    }
}
