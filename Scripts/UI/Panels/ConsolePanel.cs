using System.Linq;
using System.Text;
using Godot;
using PsdlViewer.Core;

namespace PsdlViewer.UI.Panels {
    /// <summary>Messages, warnings and errors printed to the terminal (the original viewer's System.out/System.err).</summary>
    public partial class ConsolePanel : DockPanel {
        public override string Kind => "Console";
        static readonly string[] LevelNames = { "Messages", "Warnings", "Errors" };
        static readonly Color TimeColor = new(0.55f, 0.58f, 0.62f);
        static readonly Color WarningColor = new(1, 0.85f, 0.4f);
        static readonly Color ErrorColor = new(1, 0.55f, 0.5f);

        readonly RichTextLabel text;
        readonly CheckBox[] filters = new CheckBox[3];
        long lastId;
        int generation = -1;
        int shown;

        public ConsolePanel() {
            var bar = new HBoxContainer();
            AddChild(bar);
            for (int i = 0; i < filters.Length; i++) {
                filters[i] = new CheckBox { ButtonPressed = true, FocusMode = FocusModeEnum.None };
                filters[i].Toggled += _ => Rebuild();
                bar.AddChild(filters[i]);
            }
            bar.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
            bar.AddChild(ToolButton("Copy", Copy, "Copy the shown messages to the clipboard"));
            bar.AddChild(ToolButton("Clear", AppLog.Clear, "Remove all messages"));

            text = new RichTextLabel {
                SizeFlagsVertical = SizeFlags.ExpandFill,
                ScrollFollowing = true,
                SelectionEnabled = true,
                ContextMenuEnabled = true,
                FocusMode = FocusModeEnum.Click
            };
            text.AddThemeFontOverride("normal_font", new SystemFont { FontNames = new[] { "Consolas", "DejaVu Sans Mono", "Monospace" } });
            AddChild(text);
            UpdateCounts();
        }

        public override void _Process(double delta) {
            if (generation != AppLog.Generation) {
                Rebuild();
                return;
            }
            if (AppLog.LastId == lastId) return;
            foreach (var e in AppLog.Since(lastId)) Append(e);
            if (shown > AppLog.Capacity * 6 / 5) Rebuild();
            UpdateCounts();
        }

        bool Shows(LogLevel level) => filters[(int)level].ButtonPressed;

        void Rebuild() {
            generation = AppLog.Generation;
            lastId = 0;
            shown = 0;
            text.Clear();
            foreach (var e in AppLog.Since(0)) Append(e);
            UpdateCounts();
        }

        void Append(LogEntry e) {
            lastId = e.Id;
            if (!Shows(e.Level)) return;
            if (shown++ > 0) text.Newline();
            text.PushColor(TimeColor);
            text.AddText(e.Time.ToString("HH:mm:ss") + "  ");
            text.Pop();
            if (e.Level == LogLevel.Info) {
                text.AddText(e.Text);
                return;
            }
            text.PushColor(e.Level == LogLevel.Warning ? WarningColor : ErrorColor);
            text.AddText(e.Text);
            text.Pop();
        }

        void UpdateCounts() {
            for (int i = 0; i < filters.Length; i++) filters[i].Text = $"{LevelNames[i]} ({AppLog.Count((LogLevel)i)})";
        }

        void Copy() {
            var sb = new StringBuilder();
            foreach (var e in AppLog.Since(0).Where(e => Shows(e.Level))) sb.Append(e.Time.ToString("HH:mm:ss")).Append("  ").AppendLine(e.Text);
            DisplayServer.ClipboardSet(sb.ToString());
        }
    }
}
