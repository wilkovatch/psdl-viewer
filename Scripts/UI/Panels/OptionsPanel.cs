using System;
using System.Collections.Generic;
using Godot;
using PsdlViewer.Core;

namespace PsdlViewer.UI.Panels {
    /// <summary>Options (OptionDialog with its File and Terrain tabs), edited on a copy until applied.</summary>
    public partial class OptionsPanel : DockPanel {
        public override string Kind => "Options";
        Settings edit;
        readonly TabContainer tabs = new() { SizeFlagsVertical = SizeFlags.ExpandFill };

        public OptionsPanel() {
            AddChild(tabs);
            var buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
            buttons.AddChild(ToolButton("Save", () => {
                Apply();
                Settings.Save();
                Doc.Status("Options saved.");
            }, "Apply and remember the options"));
            buttons.AddChild(ToolButton("OK", () => {
                Apply();
                Settings.Save();
                Close();
            }));
            buttons.AddChild(ToolButton("Apply", Apply));
            buttons.AddChild(ToolButton("Cancel", Close));
            AddChild(buttons);
            Build();
        }

        void Close() {
            GetParent()?.RemoveChild(this);
            QueueFree();
        }

        void Apply() {
            Main.Instance.ApplySettings(edit.Clone());
        }

        void Build() {
            edit = Settings.Current.Clone();
            foreach (var c in tabs.GetChildren()) {
                tabs.RemoveChild(c);
                c.QueueFree();
            }
            tabs.AddChild(FileTab());
            tabs.AddChild(TerrainTab());
        }

        Control FileTab() {
            var scroll = new ScrollContainer { Name = "File", HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
            var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            scroll.AddChild(box);
            var grid = new PropertyGrid();
            grid.AddText("Open folder:", () => edit.OpenPath, v => edit.OpenPath = v);
            grid.AddText("Save folder:", () => edit.SavePath, v => edit.SavePath = v);
            grid.AddCheck("Native file dialogs", () => edit.UseNativeFileDialogs, v => edit.UseNativeFileDialogs = v, "Use the operating system's file picker instead of Godot's");
            box.AddChild(grid);
            box.AddChild(PathList("Texture folders (searched for .tex, .gif, .jpg, .tga, .png, .bmp)", edit.TexturePaths));
            box.AddChild(PathList("Geometry folders (searched for .pkg)", edit.GeometryPaths));
            return scroll;
        }

        static Control PathList(string title, List<string> paths) {
            var box = new VBoxContainer();
            box.AddChild(Title(title));
            var list = new ItemList { CustomMinimumSize = new Vector2(0, 110), SelectMode = ItemList.SelectModeEnum.Single };
            void Fill() {
                list.Clear();
                foreach (string p in paths) list.AddItem(p);
            }
            Fill();
            box.AddChild(list);
            var buttons = new HBoxContainer();
            buttons.AddChild(ToolButton("Add...", () => Dialogs.OpenFolder("Select a folder", null, dir => {
                if (!paths.Contains(dir)) paths.Add(dir);
                Fill();
            })));
            buttons.AddChild(ToolButton("Remove", () => {
                var sel = list.GetSelectedItems();
                if (sel.Length == 0) return;
                paths.RemoveAt(sel[0]);
                Fill();
            }));
            buttons.AddChild(ToolButton("Up", () => Swap(list, paths, -1, Fill)));
            buttons.AddChild(ToolButton("Down", () => Swap(list, paths, 1, Fill)));
            box.AddChild(buttons);
            return box;
        }

        static void Swap(ItemList list, List<string> paths, int delta, Action fill) {
            var sel = list.GetSelectedItems();
            if (sel.Length == 0) return;
            int i = sel[0], j = i + delta;
            if (j < 0 || j >= paths.Count) return;
            (paths[i], paths[j]) = (paths[j], paths[i]);
            fill();
            list.Select(j);
        }

        Control TerrainTab() {
            var scroll = new ScrollContainer { Name = "Terrain", HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
            var grid = new PropertyGrid();
            scroll.AddChild(grid);
            grid.AddOption("Drawing mode", Settings.DrawingNames, () => edit.DrawingMode, v => edit.DrawingMode = v);
            grid.AddOption("Colouring mode", Settings.ColouringNames, () => edit.ColouringMode, v => edit.ColouringMode = v);
            grid.AddOption("Lighting mode", Settings.LightingNames, () => edit.LightingMode, v => edit.LightingMode = v);
            grid.AddCheck("Show block indices", () => edit.ShowBlockIndices, v => edit.ShowBlockIndices = v);
            grid.AddCheck("Render block attributes", () => edit.RenderBlockAttributes, v => edit.RenderBlockAttributes = v, "Off: draw each block's perimeter only");
            grid.AddCheck("Show block bounds", () => edit.ShowBlockBounds, v => edit.ShowBlockBounds = v, "Outline the selected blocks");
            grid.AddCheck("Show city bounds", () => edit.ShowCityBounds, v => edit.ShowCityBounds = v, "Bounding sphere from the PSDL dimensions");
            grid.AddCheck("Render INST", () => edit.RenderInst, v => edit.RenderInst = v);
            grid.AddCheck("Show pathsets", () => edit.ShowPathsets, v => edit.ShowPathsets = v);
            grid.AddCheck("Show BAI", () => edit.ShowBai, v => edit.ShowBai = v);
            grid.AddFloat("Block label size", () => edit.LabelSize, v => edit.LabelSize = Math.Clamp(v, 0.1f, 100), "Height of the block index labels in metres");
            grid.AddFloat("Field of view", () => edit.CameraFov, v => edit.CameraFov = Math.Clamp(v, 5, 150), "Degrees");
            grid.AddFloat("Background brightness", () => edit.BackgroundBrightness, v => edit.BackgroundBrightness = Math.Clamp(v, 0, 1));
            return scroll;
        }
    }

    /// <summary>The Random City wizard (RandomCityWizard): init, seed, smooth, water, flatten, roads, buildings.</summary>
    public partial class RandomCityPanel : DockPanel {
        public override string Kind => "Random city";
        readonly RandomCity city = new();
        readonly VBoxContainer content = new() { SizeFlagsVertical = SizeFlags.ExpandFill };
        readonly Label title = new();
        readonly TextureRect icon = new() { StretchMode = TextureRect.StretchModeEnum.KeepCentered };
        readonly Button prev = new() { Text = "<<" }, next = new() { Text = ">>" }, close = new() { Text = "Close" };
        int step;
        static readonly string[] Steps = { "Init", "Seed", "Smooth", "Water", "Flatten", "Road", "Building" };
        static readonly string[] Icons = { "RandomCity_Init", "RandomCity_Seed", "RandomCity_Smooth", "RandomCity_Water", "RandomCity_Flatten", "RandomCity_Roads", "RandomCity_Buildings" };

        public RandomCityPanel() {
            var header = new HBoxContainer();
            header.AddChild(title);
            title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            title.AddThemeFontSizeOverride("font_size", 18);
            header.AddChild(icon);
            AddChild(header);
            AddChild(new HSeparator());
            AddChild(content);
            var buttons = new HBoxContainer();
            buttons.AddChild(prev);
            buttons.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
            buttons.AddChild(close);
            buttons.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
            buttons.AddChild(next);
            AddChild(buttons);
            prev.Pressed += () => SetStep(step - 1);
            next.Pressed += Next;
            close.Pressed += () => {
                CityScene().ClearLevel();
                GetParent()?.RemoveChild(this);
                QueueFree();
            };
            SetStep(0);
        }

        static Render.CityScene CityScene() => Render.CityScene.Instance;

        async void Next() {
            switch (step) {
                case 0:
                    await Main.Instance.ReplacePsdl(city.Init(), "Initializing surface", "");
                    break;
                case 2:
                    if (!city.Initialized) break;
                    city.Smooth();
                    await Main.Instance.RefreshBlocks("Generating surface");
                    break;
                case 3:
                    if (!city.Initialized) break;
                    CityScene().ShowEdges(city.Water());
                    break;
            }
            if (step == Steps.Length - 1) {
                CityScene().ClearLevel();
                GetParent()?.RemoveChild(this);
                QueueFree();
                return;
            }
            SetStep(step + 1);
        }

        void SetStep(int s) {
            step = Math.Clamp(s, 0, Steps.Length - 1);
            title.Text = $"Step {step + 1} of {Steps.Length}: {Steps[step]}";
            icon.Texture = GD.Load<Texture2D>($"res://Assets/Icons/{Icons[step]}.png");
            prev.Disabled = step == 0;
            next.Text = step == Steps.Length - 1 ? "Finish" : ">>";
            foreach (var c in content.GetChildren()) {
                content.RemoveChild(c);
                c.QueueFree();
            }
            var grid = new PropertyGrid();
            switch (step) {
                case 0:
                    content.AddChild(Info("Creates a new city: a grid of size x size vertices, divided into blocks of 3 x 3 vertices. This replaces the current PSDL."));
                    grid.AddInt("Size", () => city.Size, v => city.Size = Math.Clamp(v, 3, 257), "Vertices per side; 2^n + 1 works best");
                    grid.AddFloat("Side length", () => city.SideLength, v => city.SideLength = v, "Metres");
                    grid.AddFloat("Initial height", () => city.InitialHeight, v => city.InitialHeight = v);
                    break;
                case 1:
                    content.AddChild(Info("Seeding is not implemented (it wasn't in the original viewer either)."));
                    break;
                case 2:
                    content.AddChild(Info("Generates random heights with the diamond-square algorithm when you press >>."));
                    grid.AddFloat("Smoothness", () => city.Smoothness, v => city.Smoothness = v);
                    grid.AddFloat("Height scale", () => city.HeightScale, v => city.HeightScale = v);
                    break;
                case 3: {
                    content.AddChild(Info("Preview shows the water plane; >> computes the shore lines."));
                    grid.AddFloat("Water height", () => city.WaterHeight, v => city.WaterHeight = v);
                    var preview = new Button { Text = "Preview" };
                    preview.Pressed += () => {
                        if (!city.Initialized) return;
                        var (min, max) = city.Extent();
                        CityScene().ShowLevel(new Vector3(min.X, min.Y, min.Z), new Vector3(max.X, max.Y, max.Z), city.WaterHeight);
                    };
                    content.AddChild(grid);
                    content.AddChild(preview);
                    return;
                }
                default:
                    content.AddChild(Info($"The {Steps[step].ToLowerInvariant()} step is not implemented (it wasn't in the original viewer either)."));
                    break;
            }
            content.AddChild(grid);
        }

        static Label Info(string text) => new() { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(260, 0) };
    }
}
