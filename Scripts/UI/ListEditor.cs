using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using PsdlViewer.Core;
using PsdlViewer.Formats;

namespace PsdlViewer.UI {
    /// <summary>Titled editable list of numbers (vertex references, room references, lanes, raw words...).</summary>
    public partial class ListEditor : VBoxContainer {
        public readonly VirtualTable Table = new();
        public event Action Changed;
        Func<int> count = () => 0;
        Action<int> insert;
        Action<int> remove;
        bool vertexRefs;
        Func<IList<int>> intList;

        public ListEditor(string title) {
            SizeFlagsHorizontal = SizeFlags.ExpandFill;
            SizeFlagsVertical = SizeFlags.ExpandFill;
            AddChild(new Label { Text = title, Modulate = new Color(0.75f, 0.8f, 0.9f) });
            Table.CustomMinimumSize = new Vector2(120, 110);
            AddChild(Table);
            Table.RowCount = () => count();
            Table.ContextMenu = Menu;
            Table.SelectionChanged += OnSelection;
        }

        void OnSelection() {
            if (!vertexRefs) return;
            var l = intList?.Invoke();
            if (l == null) return;
            int[] refs = Table.SelectedRows.Where(r => r < l.Count).Select(r => l[r]).ToArray();
            Doc.SelectVertexRefs(refs);
            Doc.SelectVertices(refs);
        }

        List<(string, Action, bool)> Menu() {
            bool resizable = insert != null;
            int[] sel = Table.SelectedRows;
            return new List<(string, Action, bool)> {
                ("Add last", () => Insert(count()), resizable),
                ("Add before", () => Insert(sel.Length > 0 ? sel[0] : 0), resizable),
                ("Add after", () => Insert(sel.Length > 0 ? sel[^1] + 1 : 0), resizable),
                ("Remove", () => {
                    foreach (int r in sel.OrderByDescending(r => r)) remove(r);
                    Table.ClearSelection(false);
                    Done();
                }, resizable && sel.Length > 0)
            };
        }

        void Insert(int index) {
            insert(Math.Clamp(index, 0, count()));
            Table.Select(new[] { Math.Clamp(index, 0, count() - 1) }, true, false);
            Done();
        }

        void Done() {
            Table.Refresh();
            Changed?.Invoke();
        }

        /// <summary>Integer list shown in hex. Vertex reference lists link with the vertex views and accept dropped vertices.</summary>
        public void BindInts(Func<IList<int>> list, Func<int, string> info = null, bool vertexReferences = false, bool resizable = true, string valueTitle = "Value", bool hex = true) {
            intList = list;
            vertexRefs = vertexReferences;
            Table.Columns.Clear();
            Table.AddColumn("Index", 50);
            Table.AddColumn(valueTitle, 70, true);
            if (info != null) Table.AddColumn(vertexReferences ? "Position" : "Info", 180);
            count = () => list()?.Count ?? 0;
            Table.GetCell = (r, c) => {
                var l = list();
                if (l == null || r >= l.Count) return "";
                return c switch {
                    0 => r.ToString("x"),
                    1 => hex ? BinaryUtil.Hex(l[r]) : l[r].ToString(),
                    _ => info?.Invoke(l[r]) ?? ""
                };
            };
            Table.SetCell = (r, c, text) => {
                int v;
                if (hex ? !BinaryUtil.TryParseHex(text, out v) : !int.TryParse(text, out v)) return $"\"{text}\" is not a valid number.";
                list()[r] = v;
                Changed?.Invoke();
                return null;
            };
            if (resizable) {
                insert = i => list().Insert(i, 0);
                remove = i => list().RemoveAt(i);
            } else {
                insert = null;
                remove = null;
            }
            Table.CanDrop = null;
            Table.Drop = null;
            if (vertexReferences) {
                if (resizable) {
                    Table.CanDrop = p => p.Kind == "vertices";
                    Table.Drop = (p, row) => {
                        var l = list();
                        int at = Math.Min(row, l.Count);
                        foreach (int v in (int[])p.Data) l.Insert(at++, v);
                        Done();
                    };
                }
            }
            Table.Reset();
        }

        public void BindFloats(Func<IList<float>> list, bool resizable = true, string valueTitle = "Value") {
            vertexRefs = false;
            intList = null;
            Table.Columns.Clear();
            Table.AddColumn("Index", 50);
            Table.AddColumn(valueTitle, 110, true);
            count = () => list()?.Count ?? 0;
            Table.GetCell = (r, c) => {
                var l = list();
                if (l == null || r >= l.Count) return "";
                return c == 0 ? r.ToString("x") : BinaryUtil.F(l[r]);
            };
            Table.SetCell = (r, c, text) => {
                if (!BinaryUtil.TryParseFloat(text, out float v)) return $"\"{text}\" is not a valid number.";
                list()[r] = v;
                Changed?.Invoke();
                return null;
            };
            insert = resizable ? i => list().Insert(i, 0f) : null;
            remove = resizable ? i => list().RemoveAt(i) : null;
            Table.Reset();
        }

        public void Refresh() => Table.Refresh();

        public static string VertexInfo(int v) {
            var verts = Doc.Psdl.Vertices;
            return v >= 0 && v < verts.Count ? BinaryUtil.V(verts[v]) : "missing";
        }

        public static string HeightInfo(int h) {
            var heights = Doc.Psdl.Heights;
            return h >= 0 && h < heights.Count ? BinaryUtil.F(heights[h]) + " m" : "missing";
        }

        public static string TextureInfo(int t) {
            if (t < 0) return "none (hides the geometry)";
            return Doc.Psdl.TextureAt(t) ?? (t < Doc.Psdl.Textures.Count ? "(empty)" : "missing");
        }

        public static string RoomInfo(int b) => b < 0 ? "-" : b < Doc.Psdl.Rooms.Count ? Doc.Psdl.Rooms[b].FlagsText() : "missing";
    }
}
