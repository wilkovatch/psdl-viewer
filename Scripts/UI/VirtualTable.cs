using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace PsdlViewer.UI {
    /// <summary>Wraps any object for Godot's drag and drop.</summary>
    public partial class DragPayload : RefCounted {
        public object Data;
        public string Kind;
    }

    /// <summary>
    /// Virtualised multi-column list (the Swing JTable of the original viewer): only visible rows are drawn,
    /// supports multi-selection, inline editing, a context menu and drag and drop.
    /// </summary>
    public partial class VirtualTable : Control {
        public class Column {
            public string Title;
            public float Width;
            public bool Editable;
            public HorizontalAlignment Align = HorizontalAlignment.Left;
        }

        public readonly List<Column> Columns = new();
        public Func<int> RowCount = () => 0;
        public Func<int, int, string> GetCell = (r, c) => "";
        /// <summary>Applies an edit; return an error message or null.</summary>
        public Func<int, int, string, string> SetCell;
        public Func<int, Color?> RowColor;
        /// <summary>Context menu entries; a null action adds a separator, a null label is skipped.</summary>
        public Func<List<(string label, Action action, bool enabled)>> ContextMenu;
        public Func<DragPayload> DragSource;
        public Func<DragPayload, bool> CanDrop;
        public Action<DragPayload, int> Drop;

        public event Action SelectionChanged;
        public event Action<int> RowActivated;

        readonly SortedSet<int> selected = new();
        int anchor = -1, lead = -1;
        float rowHeight = 20, headerHeight = 22;
        float scrollY, scrollX;
        readonly VScrollBar vbar = new();
        readonly HScrollBar hbar = new();
        readonly LineEdit editor = new() { Visible = false };
        readonly PopupMenu menu = new();
        List<Action> menuActions = new();
        int editRow = -1, editColumn = -1;
        int resizeColumn = -1;
        float resizeStart, resizeWidth;
        Font font;
        int fontSize;
        int dropRow = -1;

        public VirtualTable() {
            ClipContents = true;
            FocusMode = FocusModeEnum.All;
            SizeFlagsHorizontal = SizeFlags.ExpandFill;
            SizeFlagsVertical = SizeFlags.ExpandFill;
            CustomMinimumSize = new Vector2(80, 60);
        }

        public override void _Ready() {
            AddChild(vbar);
            AddChild(hbar);
            vbar.ValueChanged += v => {
                scrollY = (float)v;
                QueueRedraw();
            };
            hbar.ValueChanged += v => {
                scrollX = (float)v;
                QueueRedraw();
            };
            editor.Visible = false;
            editor.TextSubmitted += _ => CommitEdit();
            editor.FocusExited += () => {
                if (editor.Visible) CommitEdit();
            };
            editor.GuiInput += e => {
                if (e is InputEventKey { Pressed: true, Keycode: Key.Escape }) {
                    CancelEdit();
                    AcceptEvent();
                }
            };
            AddChild(editor);
            AddChild(menu);
            menu.IdPressed += id => {
                if (id >= 0 && id < menuActions.Count) menuActions[(int)id]?.Invoke();
            };
            Resized += Layout;
            ThemeChanged += UpdateMetrics;
            UpdateMetrics();
        }

        void UpdateMetrics() {
            font = GetThemeDefaultFont();
            fontSize = GetThemeDefaultFontSize();
            rowHeight = Mathf.Ceil(font.GetHeight(fontSize) + 4);
            headerHeight = rowHeight + 2;
            Layout();
        }

        float TotalWidth => Columns.Sum(c => c.Width);
        int Count => Math.Max(0, RowCount());
        float ViewHeight => Size.Y - headerHeight - (hbar.Visible ? hbar.Size.Y : 0);
        float ViewWidth => Size.X - (vbar.Visible ? vbar.Size.X : 0);

        void Layout() {
            if (!IsInsideTree()) return;
            float vbw = vbar.GetCombinedMinimumSize().X;
            float hbh = hbar.GetCombinedMinimumSize().Y;
            bool needV = Count * rowHeight > Size.Y - headerHeight - hbh;
            bool needH = TotalWidth > Size.X - (needV ? vbw : 0);
            vbar.Visible = needV;
            hbar.Visible = needH;
            vbar.Position = new Vector2(Size.X - vbw, headerHeight);
            vbar.Size = new Vector2(vbw, Math.Max(0, Size.Y - headerHeight - (needH ? hbh : 0)));
            hbar.Position = new Vector2(0, Size.Y - hbh);
            hbar.Size = new Vector2(Math.Max(0, Size.X - (needV ? vbw : 0)), hbh);
            vbar.MaxValue = Count * rowHeight;
            vbar.Page = Math.Max(1, ViewHeight);
            vbar.Step = 1;
            hbar.MaxValue = TotalWidth;
            hbar.Page = Math.Max(1, ViewWidth);
            if (!needV) vbar.Value = 0;
            if (!needH) hbar.Value = 0;
            scrollY = (float)vbar.Value;
            scrollX = (float)hbar.Value;
            if (editor.Visible) PlaceEditor();
            QueueRedraw();
        }

        /// <summary>Call after rows were added/removed or values changed.</summary>
        public void Refresh() {
            int n = Count;
            selected.RemoveWhere(r => r >= n);
            if (lead >= n) lead = n - 1;
            if (anchor >= n) anchor = n - 1;
            Layout();
        }

        public void Reset() {
            CancelEdit();
            selected.Clear();
            anchor = lead = -1;
            vbar.Value = 0;
            Refresh();
        }

        public int[] SelectedRows => selected.ToArray();
        public int SelectedRow => selected.Count > 0 ? selected.Min : -1;
        public int LastSelectedRow => selected.Count > 0 ? selected.Max : -1;
        public bool IsSelected(int row) => selected.Contains(row);

        public void Select(IEnumerable<int> rows, bool scroll = true, bool notify = true) {
            selected.Clear();
            int n = Count;
            foreach (int r in rows) {
                if (r >= 0 && r < n) selected.Add(r);
            }
            lead = anchor = selected.Count > 0 ? selected.Min : -1;
            if (scroll && lead >= 0) EnsureVisible(lead);
            QueueRedraw();
            if (notify) SelectionChanged?.Invoke();
        }

        public void ClearSelection(bool notify = true) {
            if (selected.Count == 0) return;
            selected.Clear();
            QueueRedraw();
            if (notify) SelectionChanged?.Invoke();
        }

        public void EnsureVisible(int row) {
            float top = row * rowHeight;
            if (top < scrollY) vbar.Value = top;
            else if (top + rowHeight > scrollY + ViewHeight) vbar.Value = top + rowHeight - ViewHeight;
        }

        int RowAt(float y) {
            if (y < headerHeight) return -1;
            int r = (int)((y - headerHeight + scrollY) / rowHeight);
            return r >= 0 && r < Count ? r : -1;
        }

        int ColumnAt(float x) {
            float cx = -scrollX;
            for (int i = 0; i < Columns.Count; i++) {
                if (x >= cx && x < cx + Columns[i].Width) return i;
                cx += Columns[i].Width;
            }
            return -1;
        }

        float ColumnX(int column) {
            float x = -scrollX;
            for (int i = 0; i < column; i++) x += Columns[i].Width;
            return x;
        }

        int ResizeHandleAt(Vector2 p) {
            if (p.Y > headerHeight) return -1;
            float x = -scrollX;
            for (int i = 0; i < Columns.Count; i++) {
                x += Columns[i].Width;
                if (Math.Abs(p.X - x) <= 4) return i;
            }
            return -1;
        }

        public override void _Draw() {
            var style = GetThemeStylebox("panel", "Tree");
            DrawStyleBox(style, new Rect2(Vector2.Zero, Size));
            var fontColor = GetThemeColor("font_color", "Tree");
            var selColor = GetThemeColor("font_selected_color", "Tree");
            var accent = new Color(0.36f, 0.56f, 0.86f);
            var selBg = new Color(accent, 0.45f);
            var altBg = new Color(1, 1, 1, 0.03f);
            var gridColor = new Color(1, 1, 1, 0.08f);
            float width = Math.Max(ViewWidth, 0);
            int n = Count;
            int first = (int)(scrollY / rowHeight);
            int last = Math.Min(n - 1, (int)((scrollY + ViewHeight) / rowHeight) + 1);
            float ascent = font.GetAscent(fontSize);
            for (int r = first; r <= last; r++) {
                float y = headerHeight + r * rowHeight - scrollY;
                var rect = new Rect2(0, y, width, rowHeight);
                if (selected.Contains(r)) DrawRect(rect, selBg);
                else if ((r & 1) == 1) DrawRect(rect, altBg);
                var rowColor = RowColor?.Invoke(r);
                float x = -scrollX;
                for (int c = 0; c < Columns.Count; c++) {
                    var col = Columns[c];
                    if (x + col.Width > 0 && x < width) {
                        string text = GetCell(r, c) ?? "";
                        DrawCell(text, x + 4, y + 2 + ascent, col.Width - 8, col.Align, selected.Contains(r) ? selColor : rowColor ?? fontColor);
                    }
                    x += col.Width;
                }
                if (r == dropRow) DrawLine(new Vector2(0, y), new Vector2(width, y), accent, 2);
            }
            if (dropRow == n && n > 0) {
                float y = headerHeight + n * rowHeight - scrollY;
                DrawLine(new Vector2(0, y), new Vector2(width, y), accent, 2);
            }
            // header
            DrawRect(new Rect2(0, 0, Size.X, headerHeight), new Color(0, 0, 0, 0.25f));
            float hx = -scrollX;
            for (int c = 0; c < Columns.Count; c++) {
                var col = Columns[c];
                DrawCell(col.Title, hx + 4, 2 + ascent, col.Width - 8, HorizontalAlignment.Left, fontColor);
                hx += col.Width;
                DrawLine(new Vector2(hx, 0), new Vector2(hx, Math.Min(Size.Y, headerHeight + (n - first) * rowHeight)), gridColor);
            }
            DrawLine(new Vector2(0, headerHeight), new Vector2(Size.X, headerHeight), gridColor);
            if (HasFocus()) DrawRect(new Rect2(Vector2.One, Size - Vector2.One * 2), new Color(accent, 0.6f), false);
        }

        void DrawCell(string text, float x, float baseline, float width, HorizontalAlignment align, Color color) {
            if (width <= 4) return;
            float w = font.GetStringSize(text, HorizontalAlignment.Left, -1, fontSize).X;
            if (w > width) {
                int lo = 0, hi = text.Length;
                while (lo < hi) {
                    int mid = (lo + hi + 1) / 2;
                    if (font.GetStringSize(text.Substring(0, mid) + "…", HorizontalAlignment.Left, -1, fontSize).X <= width) lo = mid;
                    else hi = mid - 1;
                }
                text = text.Substring(0, lo) + "…";
                w = width;
            }
            if (align == HorizontalAlignment.Right) x += width - w;
            else if (align == HorizontalAlignment.Center) x += (width - w) / 2;
            DrawString(font, new Vector2(x, baseline), text, HorizontalAlignment.Left, -1, fontSize, color);
        }

        public override void _GuiInput(InputEvent e) {
            if (e is InputEventMouseButton mb) {
                if (mb.Pressed && (mb.ButtonIndex == MouseButton.WheelUp || mb.ButtonIndex == MouseButton.WheelDown)) {
                    float d = rowHeight * 3 * (mb.ButtonIndex == MouseButton.WheelUp ? -1 : 1);
                    if (mb.ShiftPressed) hbar.Value += d;
                    else vbar.Value += d;
                    AcceptEvent();
                    return;
                }
                if (mb.ButtonIndex == MouseButton.Left && !mb.Pressed && resizeColumn >= 0) {
                    resizeColumn = -1;
                    AcceptEvent();
                    return;
                }
                if (!mb.Pressed) return;
                GrabFocus();
                if (mb.ButtonIndex == MouseButton.Left) {
                    int handle = ResizeHandleAt(mb.Position);
                    if (handle >= 0) {
                        resizeColumn = handle;
                        resizeStart = mb.Position.X;
                        resizeWidth = Columns[handle].Width;
                        AcceptEvent();
                        return;
                    }
                }
                int row = RowAt(mb.Position.Y);
                if (mb.ButtonIndex == MouseButton.Left) {
                    if (row < 0) {
                        AcceptEvent();
                        return;
                    }
                    if (mb.DoubleClick) {
                        int c = ColumnAt(mb.Position.X);
                        if (c >= 0 && Columns[c].Editable && SetCell != null) BeginEdit(row, c);
                        else RowActivated?.Invoke(row);
                    } else {
                        ClickRow(row, mb.ShiftPressed, mb.CtrlPressed || mb.MetaPressed);
                    }
                    AcceptEvent();
                } else if (mb.ButtonIndex == MouseButton.Right) {
                    if (row >= 0 && !selected.Contains(row)) ClickRow(row, false, false);
                    ShowMenu(GetGlobalTransformWithCanvas() * mb.Position);
                    AcceptEvent();
                }
            } else if (e is InputEventMouseMotion mm) {
                if (resizeColumn >= 0) {
                    Columns[resizeColumn].Width = Math.Max(24, resizeWidth + mm.Position.X - resizeStart);
                    Layout();
                    AcceptEvent();
                }
                MouseDefaultCursorShape = ResizeHandleAt(mm.Position) >= 0 || resizeColumn >= 0 ? CursorShape.Hsize : CursorShape.Arrow;
            } else if (e is InputEventKey { Pressed: true } key) {
                HandleKey(key);
            }
        }

        void HandleKey(InputEventKey key) {
            int n = Count;
            if (n == 0) return;
            int page = Math.Max(1, (int)(ViewHeight / rowHeight) - 1);
            int target = -1;
            switch (key.Keycode) {
                case Key.Up: target = Math.Max(0, lead - 1); break;
                case Key.Down: target = Math.Min(n - 1, lead + 1); break;
                case Key.Pageup: target = Math.Max(0, lead - page); break;
                case Key.Pagedown: target = Math.Min(n - 1, lead + page); break;
                case Key.Home: target = 0; break;
                case Key.End: target = n - 1; break;
                case Key.A when key.CtrlPressed:
                    Select(Enumerable.Range(0, n), false);
                    AcceptEvent();
                    return;
                case Key.F2:
                case Key.Enter:
                case Key.KpEnter:
                    if (lead >= 0 && SetCell != null) {
                        int c = Columns.FindIndex(col => col.Editable);
                        if (c >= 0) BeginEdit(lead, c);
                    }
                    AcceptEvent();
                    return;
                case Key.Menu:
                    ShowMenu(GetGlobalTransformWithCanvas() * new Vector2(20, headerHeight + (lead + 1) * rowHeight - scrollY));
                    AcceptEvent();
                    return;
            }
            if (target < 0) return;
            ClickRow(target, key.ShiftPressed, false);
            EnsureVisible(target);
            AcceptEvent();
        }

        void ClickRow(int row, bool shift, bool ctrl) {
            if (shift && anchor >= 0) {
                if (!ctrl) selected.Clear();
                for (int i = Math.Min(anchor, row); i <= Math.Max(anchor, row); i++) selected.Add(i);
            } else if (ctrl) {
                if (!selected.Remove(row)) selected.Add(row);
                anchor = row;
            } else {
                selected.Clear();
                selected.Add(row);
                anchor = row;
            }
            lead = row;
            QueueRedraw();
            SelectionChanged?.Invoke();
        }

        void ShowMenu(Vector2 screenPos) {
            var items = ContextMenu?.Invoke();
            if (items == null || items.Count == 0) return;
            menu.Clear();
            menuActions = new List<Action>();
            foreach (var (label, action, enabled) in items) {
                if (label == null) continue;
                if (action == null && label == "-") {
                    menu.AddSeparator();
                    menuActions.Add(null);
                    continue;
                }
                menu.AddItem(label, menuActions.Count);
                menu.SetItemDisabled(menu.ItemCount - 1, !enabled);
                menuActions.Add(action);
            }
            menu.Position = (Vector2I)screenPos;
            menu.ResetSize();
            menu.Popup();
        }

        // ---- inline editing ----

        public void BeginEdit(int row, int column) {
            if (SetCell == null || row < 0 || row >= Count) return;
            editRow = row;
            editColumn = column;
            EnsureVisible(row);
            editor.Text = GetCell(row, column) ?? "";
            editor.Visible = true;
            PlaceEditor();
            editor.GrabFocus();
            editor.SelectAll();
        }

        void PlaceEditor() {
            float x = ColumnX(editColumn);
            editor.Position = new Vector2(x, headerHeight + editRow * rowHeight - scrollY);
            editor.Size = new Vector2(Columns[editColumn].Width, rowHeight);
        }

        void CommitEdit() {
            if (!editor.Visible) return;
            editor.Visible = false;
            int row = editRow, column = editColumn;
            editRow = editColumn = -1;
            string error = SetCell?.Invoke(row, column, editor.Text);
            if (error != null) Dialogs.Error(error);
            QueueRedraw();
            if (IsInsideTree()) GrabFocus();
        }

        void CancelEdit() {
            if (!editor.Visible) return;
            editor.Visible = false;
            editRow = editColumn = -1;
            if (IsInsideTree()) GrabFocus();
        }

        // ---- drag and drop ----

        public override Variant _GetDragData(Vector2 atPosition) {
            if (DragSource == null || selected.Count == 0 || editor.Visible || resizeColumn >= 0) return default;
            var payload = DragSource();
            if (payload == null) return default;
            SetDragPreview(new Label { Text = $"{selected.Count} {payload.Kind}" });
            return payload;
        }

        public override bool _CanDropData(Vector2 atPosition, Variant data) {
            if (CanDrop == null || data.Obj is not DragPayload payload || !CanDrop(payload)) {
                SetDropRow(-1);
                return false;
            }
            int row = RowAt(atPosition.Y);
            SetDropRow(row < 0 ? Count : row);
            return true;
        }

        public override void _DropData(Vector2 atPosition, Variant data) {
            SetDropRow(-1);
            if (data.Obj is not DragPayload payload) return;
            int row = RowAt(atPosition.Y);
            Drop?.Invoke(payload, row < 0 ? Count : row);
        }

        public override void _Notification(int what) {
            if (what == NotificationDragEnd || what == NotificationMouseExit) SetDropRow(-1);
        }

        void SetDropRow(int row) {
            if (dropRow == row) return;
            dropRow = row;
            QueueRedraw();
        }

        public void AddColumn(string title, float width, bool editable = false, HorizontalAlignment align = HorizontalAlignment.Left) {
            Columns.Add(new Column { Title = title, Width = width, Editable = editable, Align = align });
        }
    }
}
