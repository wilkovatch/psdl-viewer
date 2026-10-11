using System;
using System.Collections.Generic;
using Godot;
using PsdlViewer.Core;
using PsdlViewer.Formats;
using NVector3 = System.Numerics.Vector3;

namespace PsdlViewer.UI {
    /// <summary>Two column label/editor grid bound to getters and setters.</summary>
    public partial class PropertyGrid : GridContainer {
        readonly List<Action> refreshers = new();
        /// <summary>Raised after any value was changed through the grid.</summary>
        public event Action Changed;

        public PropertyGrid() {
            Columns = 2;
            SizeFlagsHorizontal = SizeFlags.ExpandFill;
            AddThemeConstantOverride("h_separation", 8);
        }

        public void Refresh() {
            foreach (var r in refreshers) r();
        }

        public void Clear() {
            foreach (var c in GetChildren()) {
                RemoveChild(c);
                c.QueueFree();
            }
            refreshers.Clear();
        }

        Label AddLabel(string text) {
            var l = new Label { Text = text, VerticalAlignment = VerticalAlignment.Center };
            AddChild(l);
            return l;
        }

        LineEdit AddLine(string label, Func<string> get, Func<string, bool> set, string tooltip = null) {
            AddLabel(label);
            var edit = new LineEdit { SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(90, 0), TooltipText = tooltip ?? "" };
            AddChild(edit);
            string current = get();
            edit.Text = current;
            void Commit() {
                if (edit.Text == get()) return;
                if (set(edit.Text)) {
                    Changed?.Invoke();
                } else {
                    Doc.Status($"Invalid value for {label.TrimEnd(':', ' ')}: \"{edit.Text}\"");
                }
                edit.Text = get();
            }
            edit.TextSubmitted += _ => Commit();
            edit.FocusExited += Commit;
            refreshers.Add(() => {
                if (!edit.HasFocus()) edit.Text = get();
            });
            return edit;
        }

        public LineEdit AddText(string label, Func<string> get, Action<string> set, string tooltip = null) {
            return AddLine(label, get, s => {
                set(s);
                return true;
            }, tooltip);
        }

        public LineEdit AddHex(string label, Func<int> get, Action<int> set, int digits = 4, string tooltip = null) {
            return AddLine(label, () => BinaryUtil.Hex(get(), digits), s => {
                if (!BinaryUtil.TryParseHex(s, out int v)) return false;
                set(v);
                return true;
            }, tooltip ?? "Hexadecimal");
        }

        public LineEdit AddInt(string label, Func<int> get, Action<int> set, string tooltip = null) {
            return AddLine(label, () => get().ToString(), s => {
                if (!int.TryParse(s.Trim(), out int v)) return false;
                set(v);
                return true;
            }, tooltip);
        }

        public LineEdit AddFloat(string label, Func<float> get, Action<float> set, string tooltip = null) {
            return AddLine(label, () => BinaryUtil.F(get()), s => {
                if (!BinaryUtil.TryParseFloat(s, out float v)) return false;
                set(v);
                return true;
            }, tooltip);
        }

        public LineEdit AddVector(string label, Func<NVector3> get, Action<NVector3> set) {
            return AddLine(label, () => BinaryUtil.V(get()), s => {
                if (!BinaryUtil.TryParseVec3(s, out var v)) return false;
                set(v);
                return true;
            }, "x, y, z");
        }

        public CheckBox AddCheck(string label, Func<bool> get, Action<bool> set, string tooltip = null) {
            AddLabel(label);
            var check = new CheckBox { ButtonPressed = get(), TooltipText = tooltip ?? "" };
            AddChild(check);
            check.Toggled += on => {
                if (on == get()) return;
                set(on);
                Changed?.Invoke();
            };
            refreshers.Add(() => check.SetPressedNoSignal(get()));
            return check;
        }

        public OptionButton AddOption(string label, string[] items, Func<int> get, Action<int> set) {
            AddLabel(label);
            var option = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            foreach (string i in items) option.AddItem(i);
            option.Selected = get();
            AddChild(option);
            option.ItemSelected += i => {
                if (i == get()) return;
                set((int)i);
                Changed?.Invoke();
            };
            refreshers.Add(() => option.Selected = get());
            return option;
        }

        public Label AddInfo(string label, Func<string> get) {
            AddLabel(label);
            var value = new Label { Text = get(), SizeFlagsHorizontal = SizeFlags.ExpandFill, AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = new Color(1, 1, 1, 0.75f) };
            AddChild(value);
            refreshers.Add(() => value.Text = get());
            return value;
        }
    }
}
