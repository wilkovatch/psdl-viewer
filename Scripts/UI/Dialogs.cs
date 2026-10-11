using System;
using System.IO;
using Godot;
using PsdlViewer.Core;

namespace PsdlViewer.UI {
    /// <summary>In-app (embedded) dialogs and Godot file pickers.</summary>
    public static class Dialogs {
        static Node host;

        public static void Init(Node node) {
            host = node;
        }

        static T Show<T>(T dialog) where T : Window {
            host.AddChild(dialog);
            dialog.CloseRequested += () => dialog.QueueFree();
            if (dialog is AcceptDialog accept) {
                accept.Confirmed += () => accept.QueueFree();
                accept.Canceled += () => accept.QueueFree();
            }
            dialog.PopupCentered();
            return dialog;
        }

        public static void Info(string message, string title = "PSDL Viewer") {
            Show(new AcceptDialog { Title = title, DialogText = message, DialogAutowrap = true, MinSize = new Vector2I(360, 0) });
        }

        public static void Error(string message) {
            GD.PushWarning(message);
            Info(message, "Error");
        }

        public static void Confirm(string message, Action yes, string title = "Confirm") {
            var d = new ConfirmationDialog { Title = title, DialogText = message, DialogAutowrap = true, MinSize = new Vector2I(360, 0) };
            d.Confirmed += () => yes();
            Show(d);
        }

        /// <summary>Simple form with one line edit per field.</summary>
        public static void Form(string title, (string label, string value)[] fields, Action<string[]> ok, string text = null) {
            var d = new ConfirmationDialog { Title = title, MinSize = new Vector2I(360, 0) };
            var box = new VBoxContainer();
            if (text != null) box.AddChild(new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(320, 0) });
            var grid = new GridContainer { Columns = 2 };
            var edits = new LineEdit[fields.Length];
            for (int i = 0; i < fields.Length; i++) {
                grid.AddChild(new Label { Text = fields[i].label });
                edits[i] = new LineEdit { Text = fields[i].value, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(180, 0) };
                grid.AddChild(edits[i]);
                if (i == fields.Length - 1) edits[i].TextSubmitted += _ => {
                    d.Hide();
                    d.EmitSignal(AcceptDialog.SignalName.Confirmed);
                };
            }
            box.AddChild(grid);
            d.AddChild(box);
            d.Confirmed += () => ok(Array.ConvertAll(edits, e => e.Text));
            Show(d);
            if (edits.Length > 0) {
                edits[0].GrabFocus();
                edits[0].SelectAll();
            }
        }

        public static void Input(string title, string label, string value, Action<string> ok, string text = null) {
            Form(title, new[] { (label, value) }, v => ok(v[0]), text);
        }

        public static void Choose(string title, string label, string[] options, Action<int> ok) {
            var d = new ConfirmationDialog { Title = title, MinSize = new Vector2I(320, 0) };
            var box = new VBoxContainer();
            box.AddChild(new Label { Text = label });
            var option = new OptionButton();
            foreach (string o in options) option.AddItem(o);
            box.AddChild(option);
            d.AddChild(box);
            d.Confirmed += () => ok(option.Selected);
            Show(d);
        }

        public static void ShowText(string title, string text) {
            var d = new AcceptDialog { Title = title, MinSize = new Vector2I(520, 420) };
            var edit = new TextEdit { Text = text, Editable = false, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
            d.AddChild(edit);
            Show(d);
        }

        // ---- file pickers ----

        static FileDialog MakeFileDialog(string title, FileDialog.FileModeEnum mode, string[] filters, string dir) {
            var d = new FileDialog {
                Title = title,
                FileMode = mode,
                Access = FileDialog.AccessEnum.Filesystem,
                UseNativeDialog = Settings.Current.UseNativeFileDialogs,
                MinSize = new Vector2I(820, 540)
            };
            if (filters != null) d.Filters = filters;
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) d.CurrentDir = dir;
            return d;
        }

        static void ShowFileDialog(FileDialog d) {
            host.AddChild(d);
            d.Canceled += () => d.QueueFree();
            d.CloseRequested += () => d.QueueFree();
            d.PopupCentered();
        }

        public static void OpenFile(string title, string[] filters, Action<string> ok, bool remember = true) {
            var d = MakeFileDialog(title, FileDialog.FileModeEnum.OpenFile, filters, Settings.Current.OpenPath);
            d.FileSelected += path => {
                if (remember) {
                    Settings.Current.OpenPath = Path.GetDirectoryName(path);
                    Settings.Save();
                }
                d.QueueFree();
                ok(path);
            };
            ShowFileDialog(d);
        }

        public static void SaveFile(string title, string[] filters, string defaultName, Action<string> ok) {
            var d = MakeFileDialog(title, FileDialog.FileModeEnum.SaveFile, filters,
                string.IsNullOrEmpty(Settings.Current.SavePath) ? Settings.Current.OpenPath : Settings.Current.SavePath);
            if (!string.IsNullOrEmpty(defaultName)) d.CurrentFile = defaultName;
            d.FileSelected += path => {
                Settings.Current.SavePath = Path.GetDirectoryName(path);
                Settings.Save();
                d.QueueFree();
                ok(path);
            };
            ShowFileDialog(d);
        }

        public static void OpenFolder(string title, string start, Action<string> ok) {
            var d = MakeFileDialog(title, FileDialog.FileModeEnum.OpenDir, null, string.IsNullOrEmpty(start) ? Settings.Current.OpenPath : start);
            d.DirSelected += path => {
                d.QueueFree();
                ok(path);
            };
            ShowFileDialog(d);
        }
    }
}
