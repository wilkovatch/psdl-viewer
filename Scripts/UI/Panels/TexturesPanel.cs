using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using PsdlViewer.Core;
using PsdlViewer.Formats.Psdl;
using PsdlViewer.Render;

namespace PsdlViewer.UI.Panels {
    /// <summary>Texture list with image previews (TextureListView/TextureView). Follows the room selection.</summary>
    public partial class TexturesPanel : DockPanel {
        public override string Kind => "Textures";
        readonly VirtualTable table = new();
        readonly HFlowContainer images = new() { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        readonly Dictionary<string, string> status = new(StringComparer.OrdinalIgnoreCase);

        public TexturesPanel() {
            var split = new VSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
            AddChild(split);
            table.AddColumn("Index", 50);
            table.AddColumn("Name", 220, true);
            table.AddColumn("File", 110);
            table.RowCount = () => Doc.Psdl.Textures.Count;
            table.GetCell = (r, c) => {
                string name = Doc.Psdl.Textures[r];
                return c switch {
                    0 => r.ToString("x"),
                    1 => name ?? "",
                    _ => Status(name)
                };
            };
            table.RowColor = r => Status(Doc.Psdl.Textures[r]) == "missing" ? new Color(1, 0.55f, 0.5f) : null;
            table.SetCell = (r, c, text) => {
                Doc.Psdl.Textures[r] = text.Trim();
                status.Remove(text.Trim());
                Doc.NotifyChanged(DataKind.Textures);
                ShowImages();
                return null;
            };
            table.SelectionChanged += ShowImages;
            table.ContextMenu = () => {
                int[] sel = table.SelectedRows;
                return new List<(string, Action, bool)> {
                    ("Add last", () => {
                        Doc.Psdl.Textures.Add("");
                        Doc.NotifyChanged(DataKind.Textures);
                        table.Select(new[] { Doc.Psdl.Textures.Count - 1 });
                    }, true),
                    ("Remove", () => {
                        foreach (int r in sel.OrderByDescending(r => r)) Doc.Psdl.Textures.RemoveAt(r);
                        table.ClearSelection();
                        Doc.NotifyChanged(DataKind.Textures);
                    }, sel.Length > 0)
                };
            };
            split.AddChild(table);
            var imageBox = new VBoxContainer();
            imageBox.AddChild(Title("Images"));
            var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, CustomMinimumSize = new Vector2(0, 120) };
            scroll.AddChild(images);
            imageBox.AddChild(scroll);
            split.AddChild(imageBox);
            Subscribe(() => {
                Doc.PsdlReplaced += OnReplaced;
                Doc.DataChanged += OnDataChanged;
                Doc.RoomsSelected += OnRoomsSelected;
                Settings.Changed += OnReplaced;
            }, () => {
                Doc.PsdlReplaced -= OnReplaced;
                Doc.DataChanged -= OnDataChanged;
                Doc.RoomsSelected -= OnRoomsSelected;
                Settings.Changed -= OnReplaced;
            });
        }

        string Status(string name) {
            if (string.IsNullOrEmpty(name)) return "";
            if (status.TryGetValue(name, out string s)) return s;
            string file = TextureStore.FindFile(name, Settings.Current.TexturePaths, new[] { "tex", "gif", "jpg", "tga", "png", "bmp" });
            s = file == null ? "missing" : System.IO.Path.GetExtension(file).TrimStart('.');
            status[name] = s;
            return s;
        }

        void ShowImages() {
            foreach (var c in images.GetChildren()) {
                images.RemoveChild(c);
                c.QueueFree();
            }
            foreach (int r in table.SelectedRows.Take(48)) {
                string name = Doc.Psdl.Textures[r];
                var box = new VBoxContainer();
                var entry = TextureStore.Get(name);
                var rect = new TextureRect {
                    Texture = entry?.DisplayTexture,
                    ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                    StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                    CustomMinimumSize = new Vector2(128, 128),
                    TooltipText = entry?.File ?? "No image"
                };
                box.AddChild(rect);
                box.AddChild(new Label { Text = $"{r:x}: {name}", CustomMinimumSize = new Vector2(128, 0), ClipText = true, TooltipText = entry?.Status ?? "" });
                images.AddChild(box);
            }
        }

        void OnReplaced() {
            status.Clear();
            table.Reset();
            ShowImages();
        }

        void OnDataChanged(DataKind kind) {
            if (kind == DataKind.Textures) table.Refresh();
        }

        /// <summary>Selects the textures referenced by the first selected room.</summary>
        void OnRoomsSelected(int[] rooms, object source) {
            if (rooms.Length == 0 || rooms[0] >= Doc.Psdl.Rooms.Count) return;
            var rows = new List<int>();
            foreach (var a in Doc.Psdl.Rooms[rooms[0]].Attributes) {
                if (a is TextureReferenceAttribute t && t.Index >= 0 && t.Index < Doc.Psdl.Textures.Count) rows.Add(t.Index);
                if (a is DividedRoadAttribute d && d.DividerTexture >= 0 && d.DividerTexture < Doc.Psdl.Textures.Count) rows.Add(d.DividerTexture);
            }
            table.Select(rows.Distinct());
        }
    }
}
