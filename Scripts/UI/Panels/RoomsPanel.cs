using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using PsdlViewer.Core;
using PsdlViewer.Formats;
using PsdlViewer.Formats.Psdl;

namespace PsdlViewer.UI.Panels {
    /// <summary>Room list with the perimeter and attribute editors (BlockListView/BlockView).</summary>
    public partial class RoomsPanel : DockPanel {
        public override string Kind => "Rooms";

        readonly VirtualTable table = new();
        readonly VirtualTable perimeter = new();
        readonly VirtualTable attributes = new();
        readonly ScrollContainer attributeScroll = new() { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        readonly PropertyGrid typeGrid = new();
        readonly LineEdit findAttribute = new() { PlaceholderText = "attribute id (hex)", CustomMinimumSize = new Vector2(120, 0) };
        readonly LineEdit findTexture = new() { PlaceholderText = "texture index (hex)", CustomMinimumSize = new Vector2(120, 0) };
        readonly TabContainer detail = new() { SizeFlagsVertical = SizeFlags.ExpandFill };
        Room current;
        int currentIndex = -1;
        bool syncing, syncingAttributes;

        static PsdlFile Psdl => Doc.Psdl;

        public RoomsPanel() {
            var find = new HBoxContainer();
            find.AddChild(new Label { Text = "Find:" });
            find.AddChild(findAttribute);
            find.AddChild(findTexture);
            find.AddChild(ToolButton("Find", () => FindNext(true), "Find the first room containing the attribute or texture"));
            find.AddChild(ToolButton("Find next", () => FindNext(false)));
            findAttribute.TextSubmitted += _ => FindNext(true);
            findTexture.TextSubmitted += _ => FindNext(true);
            AddChild(find);

            var split = new VSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
            AddChild(split);
            BuildTable();
            split.AddChild(table);
            split.AddChild(detail);
            table.SizeFlagsVertical = SizeFlags.Fill;
            table.CustomMinimumSize = new Vector2(120, 230);

            var typeBox = new VBoxContainer { Name = "Room" };
            typeBox.AddChild(typeGrid);
            detail.AddChild(typeBox);
            var perimeterBox = new VBoxContainer { Name = "Perimeter" };
            perimeterBox.AddChild(new Label { Text = "Vertex, connected room (drag vertices here from a vertex list)", Modulate = new Color(1, 1, 1, 0.7f) });
            BuildPerimeter();
            perimeterBox.AddChild(perimeter);
            detail.AddChild(perimeterBox);
            var attributeSplit = new VSplitContainer { Name = "Attributes" };
            BuildAttributes();
            attributeSplit.AddChild(attributes);
            attributeSplit.AddChild(attributeScroll);
            attributes.SizeFlagsVertical = SizeFlags.Fill;
            attributes.CustomMinimumSize = new Vector2(120, 150);
            detail.AddChild(attributeSplit);
            detail.CurrentTab = 2;
            detail.Visible = false;

            Subscribe(() => {
                Doc.PsdlReplaced += OnReplaced;
                Doc.DataChanged += OnDataChanged;
                Doc.RoomsSelected += OnRoomsSelected;
                Doc.RoomAttributesSelected += OnRoomAttributesSelected;
            }, () => {
                Doc.PsdlReplaced -= OnReplaced;
                Doc.DataChanged -= OnDataChanged;
                Doc.RoomsSelected -= OnRoomsSelected;
                Doc.RoomAttributesSelected -= OnRoomAttributesSelected;
            });
        }

        void BuildTable() {
            table.AddColumn("Index", 50);
            table.AddColumn("Flags", 46, true);
            table.AddColumn("Type", 130);
            table.AddColumn("Prop rule", 64, true);
            table.AddColumn("Perimeter", 64, false, HorizontalAlignment.Right);
            table.AddColumn("Attributes", 70, false, HorizontalAlignment.Right);
            table.RowCount = () => Psdl.Rooms.Count;
            table.GetCell = (r, c) => {
                var b = Psdl.Rooms[r];
                return c switch {
                    0 => r.ToString("x"),
                    1 => b.Flags.ToString("x2"),
                    2 => b.FlagsText(),
                    3 => b.PropRule.ToString("x2"),
                    4 => b.Perimeter.Count.ToString(),
                    _ => b.Attributes.Count.ToString()
                };
            };
            table.SetCell = (r, c, text) => {
                if (!BinaryUtil.TryParseHex(text, out int v) || v < 0 || v > 255) return "Enter a hexadecimal value between 0 and ff.";
                if (c == 1) Psdl.Rooms[r].Flags = v;
                else Psdl.Rooms[r].PropRule = v;
                typeGrid.Refresh();
                return null;
            };
            table.SelectionChanged += () => {
                ShowRoom(table.SelectedRow);
                if (syncing) return;
                syncing = true;
                Doc.SelectRooms(table.SelectedRows, this);
                syncing = false;
            };
            table.ContextMenu = () => {
                int[] sel = table.SelectedRows;
                return new List<(string, Action, bool)> {
                    ("Add last", () => AddRoom(Psdl.Rooms.Count), true),
                    ("Add before", () => AddRoom(sel.Length > 0 ? sel[0] : 0), true),
                    ("Add after", () => AddRoom(sel.Length > 0 ? sel[^1] + 1 : 0), true),
                    ("Remove", () => Dialogs.Confirm($"Remove {sel.Length} room(s)? References to them are cleared and their INST objects are removed.", () => {
                        foreach (int r in sel.OrderByDescending(r => r)) Doc.RemoveRoom(r);
                        table.ClearSelection();
                        Changed(DataKind.Rooms, DataKind.Inst);
                    }), sel.Length > 0),
                    ("-", null, true),
                    ("Move up", () => Move(sel, -1), sel.Length == 1 && sel[0] > 0),
                    ("Move down", () => Move(sel, 1), sel.Length == 1 && sel[0] < Psdl.Rooms.Count - 1),
                    ("Find rooms...", () => findAttribute.GrabFocus(), true)
                };
            };
        }

        void AddRoom(int index) {
            Doc.InsertRoom(Math.Clamp(index, 0, Psdl.Rooms.Count), new Room());
            Changed(DataKind.Rooms, DataKind.Inst);
            table.Select(new[] { index });
        }

        void Move(int[] sel, int delta) {
            int to = sel[0] + delta;
            Doc.MoveRoom(sel[0], to);
            Changed(DataKind.Rooms, DataKind.Inst);
            table.Select(new[] { to });
        }

        void Changed(params DataKind[] kinds) {
            foreach (var k in kinds) Doc.NotifyChanged(k);
            Doc.Status("Room data changed. Use View > Refresh rooms (Ctrl+R) to update the 3D view.");
        }

        void BuildPerimeter() {
            perimeter.AddColumn("Index", 50);
            perimeter.AddColumn("Vertex", 64, true);
            perimeter.AddColumn("Room", 64, true);
            perimeter.AddColumn("Position", 200);
            perimeter.RowCount = () => current?.Perimeter.Count ?? 0;
            perimeter.GetCell = (r, c) => {
                var p = current.Perimeter[r];
                return c switch {
                    0 => r.ToString("x"),
                    1 => BinaryUtil.Hex(p.Vertex),
                    2 => p.Room < 0 ? "-" : BinaryUtil.Hex(p.Room),
                    _ => ListEditor.VertexInfo(p.Vertex)
                };
            };
            perimeter.SetCell = (r, c, text) => {
                var p = current.Perimeter[r];
                if (c == 2 && (text.Trim() == "-" || text.Trim() == "")) {
                    p.Room = -1;
                } else {
                    if (!BinaryUtil.TryParseHex(text, out int v)) return $"\"{text}\" is not a hexadecimal number.";
                    if (c == 1) p.Vertex = v;
                    else p.Room = v >= 0xffff ? -1 : v;
                }
                Changed(DataKind.Rooms);
                return null;
            };
            perimeter.SelectionChanged += () => {
                if (current == null) return;
                int[] refs = perimeter.SelectedRows.Select(r => current.Perimeter[r].Vertex).ToArray();
                Doc.SelectVertexRefs(refs);
                Doc.SelectVertices(refs);
            };
            perimeter.ContextMenu = () => {
                int[] sel = perimeter.SelectedRows;
                bool ok = current != null;
                return new List<(string, Action, bool)> {
                    ("Add last", () => InsertPerimeter(current.Perimeter.Count), ok),
                    ("Add before", () => InsertPerimeter(sel.Length > 0 ? sel[0] : 0), ok),
                    ("Add after", () => InsertPerimeter(sel.Length > 0 ? sel[^1] + 1 : 0), ok),
                    ("Remove", () => {
                        foreach (int r in sel.OrderByDescending(r => r)) current.Perimeter.RemoveAt(r);
                        perimeter.ClearSelection(false);
                        perimeter.Refresh();
                        Changed(DataKind.Rooms);
                    }, ok && sel.Length > 0)
                };
            };
            perimeter.CanDrop = p => current != null && p.Kind == "vertices";
            perimeter.Drop = (p, row) => {
                int at = Math.Min(row, current.Perimeter.Count);
                foreach (int v in (int[])p.Data) current.Perimeter.Insert(at++, new PerimeterPoint(v, -1));
                perimeter.Refresh();
                Changed(DataKind.Rooms);
            };
        }

        void InsertPerimeter(int index) {
            current.Perimeter.Insert(Math.Clamp(index, 0, current.Perimeter.Count), new PerimeterPoint(0, -1));
            perimeter.Refresh();
            Changed(DataKind.Rooms);
        }

        void BuildAttributes() {
            attributes.AddColumn("Index", 46);
            attributes.AddColumn("Last", 36, false, HorizontalAlignment.Center);
            attributes.AddColumn("Attribute", 320);
            attributes.RowCount = () => current?.Attributes.Count ?? 0;
            attributes.GetCell = (r, c) => {
                var a = current.Attributes[r];
                return c switch {
                    0 => r.ToString("x"),
                    1 => a.Last ? "✓" : "",
                    _ => a.ToString()
                };
            };
            attributes.RowColor = r => current.Attributes[r] is TextureReferenceAttribute ? new Color(0.75f, 0.85f, 1f) : null;
            attributes.SelectionChanged += ShowAttribute;
            attributes.ContextMenu = () => {
                int[] sel = attributes.SelectedRows;
                bool ok = current != null;
                return new List<(string, Action, bool)> {
                    ("Add last", () => AddAttribute(current.Attributes.Count), ok),
                    ("Add before", () => AddAttribute(sel.Length > 0 ? sel[0] : 0), ok),
                    ("Add after", () => AddAttribute(sel.Length > 0 ? sel[^1] + 1 : 0), ok),
                    ("Remove", () => {
                        foreach (int r in sel.OrderByDescending(r => r)) current.Attributes.RemoveAt(r);
                        attributes.ClearSelection();
                        attributes.Refresh();
                        Changed(DataKind.Rooms);
                    }, ok && sel.Length > 0),
                    ("Duplicate", () => {
                        int at = sel[^1] + 1;
                        foreach (int r in sel.Reverse()) current.Attributes.Insert(at, current.Attributes[r].Clone());
                        attributes.Refresh();
                        Changed(DataKind.Rooms);
                    }, ok && sel.Length > 0),
                    ("-", null, true),
                    ("Move up", () => MoveAttribute(sel[0], -1), ok && sel.Length == 1 && sel[0] > 0),
                    ("Move down", () => MoveAttribute(sel[0], 1), ok && sel.Length == 1 && sel[0] < current.Attributes.Count - 1)
                };
            };
        }

        void AddAttribute(int index) {
            var room = current;
            Dialogs.Input("Add room attribute", "Attribute ID (hex):", "50", text => {
                if (!BinaryUtil.TryParseHex(text, out int id) || id < 0 || id > 0x7f) {
                    Dialogs.Error("Enter an attribute id between 0 and 7f.");
                    return;
                }
                if (room != current) return;
                var a = RoomAttribute.Create(id);
                int at = Math.Clamp(index, 0, room.Attributes.Count);
                room.Attributes.Insert(at, a);
                attributes.Refresh();
                attributes.Select(new[] { at });
                Changed(DataKind.Rooms);
            }, RoomAttribute.KnownAttributesText());
        }

        void MoveAttribute(int index, int delta) {
            var a = current.Attributes[index];
            current.Attributes.RemoveAt(index);
            current.Attributes.Insert(index + delta, a);
            attributes.Select(new[] { index + delta });
            attributes.Refresh();
            Changed(DataKind.Rooms);
        }

        void ShowRoom(int index) {
            current = index >= 0 && index < Psdl.Rooms.Count ? Psdl.Rooms[index] : null;
            currentIndex = current == null ? -1 : index;
            detail.Visible = current != null;
            typeGrid.Clear();
            if (current != null) {
                var room = current;
                typeGrid.AddInfo("Room:", () => $"{currentIndex:x}  ({Psdl.RoomCenter(room).X:0.#}, {Psdl.RoomCenter(room).Y:0.#}, {Psdl.RoomCenter(room).Z:0.#})");
                for (int i = 0; i < 8; i++) {
                    int bit = 1 << i;
                    typeGrid.AddCheck(Room.FlagNames[i], () => room.HasFlag(bit), v => room.SetFlag(bit, v));
                }
                typeGrid.AddHex("Prop rule:", () => room.PropRule, v => room.PropRule = v & 0xff, 2, "Line in proprules.csv (0 = none)");
                typeGrid.Changed += () => {
                    table.Refresh();
                    Doc.NotifyChanged(DataKind.Rooms);
                };
            }
            perimeter.Reset();
            attributes.Reset();
            ShowAttribute();
        }

        void ShowAttribute() {
            foreach (var c in attributeScroll.GetChildren()) {
                attributeScroll.RemoveChild(c);
                c.QueueFree();
            }
            var room = current;
            if (!syncingAttributes) Doc.SelectRoomAttributes(room, room == null ? null : attributes.SelectedRows.Where(r => r < room.Attributes.Count).Select(r => room.Attributes[r]).ToArray(), this);
            int row = attributes.SelectedRow;
            if (current == null || row < 0 || row >= current.Attributes.Count) return;
            var editor = new AttributeEditor(current.Attributes[row]);
            editor.Changed += () => {
                attributes.Refresh();
                Changed(DataKind.Rooms);
            };
            attributeScroll.AddChild(editor);
        }

        void FindNext(bool fromStart) {
            bool hasAttr = BinaryUtil.TryParseHex(findAttribute.Text, out int attrId) && findAttribute.Text.Trim() != "";
            bool hasTex = BinaryUtil.TryParseHex(findTexture.Text, out int texIndex) && findTexture.Text.Trim() != "";
            if (!hasAttr && !hasTex) {
                Doc.Status("Enter an attribute id and/or a texture index (hexadecimal).");
                return;
            }
            int start = fromStart ? 0 : table.LastSelectedRow + 1;
            for (int i = start; i < Psdl.Rooms.Count; i++) {
                foreach (var a in Psdl.Rooms[i].Attributes) {
                    bool match = hasAttr && a.Id == attrId || hasTex && a is TextureReferenceAttribute t && t.Index == texIndex;
                    if (!match) continue;
                    table.Select(new[] { i });
                    return;
                }
            }
            Dialogs.Info("No match found.");
        }

        void OnReplaced() {
            current = null;
            table.Reset();
            ShowRoom(-1);
        }

        void OnDataChanged(DataKind kind) {
            if (kind == DataKind.Rooms) {
                table.Refresh();
                if (current != null && !Psdl.Rooms.Contains(current)) ShowRoom(table.SelectedRow);
                else {
                    perimeter.Refresh();
                    attributes.Refresh();
                    typeGrid.Refresh();
                }
            } else if (kind == DataKind.Vertices || kind == DataKind.Heights || kind == DataKind.Textures) {
                perimeter.Refresh();
                attributes.Refresh();
            }
        }

        /// <summary>Attributes clicked in the 3D view: show them in the attribute list.</summary>
        void OnRoomAttributesSelected(object source) {
            if (source == this || syncingAttributes || current == null || Doc.AttributeRoom != current) return;
            syncingAttributes = true;
            detail.CurrentTab = 2;
            attributes.Select(Doc.SelectedAttributes.Select(a => current.Attributes.IndexOf(a)).Where(i => i >= 0));
            syncingAttributes = false;
        }

        void OnRoomsSelected(int[] rooms, object source) {
            if (source == this || syncing) return;
            syncing = true;
            table.Select(rooms, true, true);
            syncing = false;
        }
    }
}
