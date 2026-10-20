using System;
using Godot;

namespace PsdlViewer.UI {
    /// <summary>A panel shown as a tab in one of the dock areas (the original viewer's internal frames).</summary>
    public partial class DockPanel : VBoxContainer {
        /// <summary>Kind used for numbering ("Rooms 1", "Rooms 2", ...).</summary>
        public virtual string Kind => "Panel";
        /// <summary>Subscriptions to app wide events, attached while in the tree.</summary>
        protected event Action Attach;
        protected event Action Detach;

        public DockPanel() {
            SizeFlagsHorizontal = SizeFlags.ExpandFill;
            SizeFlagsVertical = SizeFlags.ExpandFill;
        }

        public override void _EnterTree() => Attach?.Invoke();
        public override void _ExitTree() => Detach?.Invoke();

        /// <summary>Registers a handler that is only active while the panel is in the tree.</summary>
        protected void Subscribe(Action add, Action remove) {
            Attach += add;
            Detach += remove;
            if (IsInsideTree()) add();
        }

        protected static Label Title(string text) {
            var l = new Label { Text = text };
            l.AddThemeColorOverride("font_color", new Color(0.75f, 0.8f, 0.9f));
            return l;
        }

        protected static Button ToolButton(string text, Action pressed, string tooltip = null) {
            var b = new Button { Text = text, TooltipText = tooltip ?? "", FocusMode = FocusModeEnum.None };
            b.Pressed += pressed;
            return b;
        }
    }
}
