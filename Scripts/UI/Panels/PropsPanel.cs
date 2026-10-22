using System.Collections.Generic;
using Godot;
using PsdlViewer.Core;
using PsdlViewer.Formats;

namespace PsdlViewer.UI.Panels {
    /// <summary>Read-only list of the loaded prop rules (proprules.csv) and prop definitions (propdefs.csv).</summary>
    public partial class PropsPanel : DockPanel {
        public override string Kind => "Props";
        readonly VirtualTable rules = new();
        readonly VirtualTable defs = new();
        readonly Label file = new() { Modulate = new Color(1, 1, 1, 0.6f), ClipText = true };
        /// <summary>Definitions shown in the lower list: those of the selected rule, or all of them.</summary>
        List<PropDef> shown = new();

        public PropsPanel() {
            var bar = new HBoxContainer();
            bar.AddChild(file);
            file.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            bar.AddChild(ToolButton("Open...", () => Main.Instance.OpenProps(), "Open the prop rules and props.pathset of a city folder"));
            bar.AddChild(ToolButton("Re-render", () => Main.Instance.RenderProps(), "Update the roadside props in the 3D view"));
            AddChild(bar);
            var split = new VSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
            AddChild(split);

            rules.AddColumn("Index", 46);
            rules.AddColumn("Prop rule", 80);
            rules.AddColumn("Name", 120);
            rules.AddColumn("Definitions", 320);
            rules.CustomMinimumSize = new Vector2(100, 90);
            rules.RowCount = () => Doc.Props.Rules.Count;
            rules.GetCell = (r, c) => {
                var rule = Doc.Props.Rules[r];
                return c switch {
                    0 => r.ToString("x"),
                    // the room prop rule n uses the lines 2(n - 1) and 2(n - 1) + 1
                    1 => $"{r / 2 + 1:x2} {(r % 2 == 0 ? "left" : "right")}",
                    2 => rule.Name,
                    _ => string.Join(", ", rule.Defs)
                };
            };
            rules.SelectionChanged += ShowDefs;
            split.AddChild(rules);

            defs.AddColumn("Name", 120);
            defs.AddColumn("Start", 50, false, HorizontalAlignment.Right);
            defs.AddColumn("Spacing", 56, false, HorizontalAlignment.Right);
            defs.AddColumn("Max use", 56, false, HorizontalAlignment.Right);
            defs.AddColumn("Min lerp", 60, false, HorizontalAlignment.Right);
            defs.AddColumn("Max lerp", 60, false, HorizontalAlignment.Right);
            defs.AddColumn("Geometry", 300);
            defs.CustomMinimumSize = new Vector2(100, 90);
            defs.RowCount = () => shown.Count;
            defs.GetCell = (r, c) => {
                var d = shown[r];
                return c switch {
                    0 => d.Name,
                    1 => BinaryUtil.F(d.StartDist),
                    2 => BinaryUtil.F(d.Spacing),
                    3 => d.MaxUse.ToString(),
                    4 => BinaryUtil.F(d.MinLerp),
                    5 => BinaryUtil.F(d.MaxLerp),
                    _ => string.Join(", ", d.Models)
                };
            };
            split.AddChild(defs);
            Subscribe(() => Doc.PropsReplaced += OnReplaced, () => Doc.PropsReplaced -= OnReplaced);
            OnReplaced();
        }

        void ShowDefs() {
            int[] sel = rules.SelectedRows;
            if (sel.Length == 0) {
                shown = Doc.Props.Defs;
            } else {
                shown = new List<PropDef>();
                foreach (int r in sel) {
                    foreach (var d in Doc.Props.Rules[r].Defs) {
                        if (!shown.Contains(d)) shown.Add(d);
                    }
                }
            }
            defs.Reset();
        }

        void OnReplaced() {
            file.Text = string.IsNullOrEmpty(Doc.PropsPath) ? "No props loaded (File > Open props folder)"
                : $"{Doc.PropsPath} ({Doc.Props.Rules.Count} rules, {Doc.Props.Defs.Count} definitions, traffic lights {Doc.Props.SingleTrafficLight} / {Doc.Props.DualTrafficLight})";
            rules.Reset();
            ShowDefs();
        }
    }
}
