using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using PsdlViewer.Core;
using PsdlViewer.Formats;
using PsdlViewer.Formats.Psdl;
using PsdlViewer.Render;
using PsdlViewer.UI;
using PsdlViewer.UI.Panels;

namespace PsdlViewer {
    /// <summary>Main window (MainWindow): menus, dock areas, status bar and the long running operations.</summary>
    public partial class Main : Control {
        public static Main Instance { get; private set; }

        CityScene scene;
        TabContainer leftDock, centerDock, bottomDock;
        HSplitContainer mainSplit;
        VSplitContainer centerSplit;
        Label statusLabel, countsLabel;
        Control overlay;
        Label overlayTitle, overlayNote;
        ProgressBar overlayBar;
        Progress progress;
        bool busy;
        readonly Dictionary<string, int> panelCounters = new();
        PopupMenu tabMenu;
        TabContainer tabMenuDock;
        int tabMenuIndex;

        public override void _Ready() {
            Instance = this;
            AppLog.Install();
            FileLog.Output = text => GD.Print(text);
            RenderingServer.SetDebugGenerateWireframes(true);
            Settings.Load();
            TextureStore.LoadPlaceholders();
            SetAnchorsPreset(LayoutPreset.FullRect);
            Dialogs.Init(this);

            scene = new CityScene { Name = "CityScene" };
            AddChild(scene);

            var background = new Panel();
            background.SetAnchorsPreset(LayoutPreset.FullRect);
            AddChild(background);

            var root = new VBoxContainer();
            root.SetAnchorsPreset(LayoutPreset.FullRect);
            root.AddThemeConstantOverride("separation", 0);
            AddChild(root);
            root.AddChild(BuildMenu());

            mainSplit = new HSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
            root.AddChild(mainSplit);
            leftDock = MakeDock("LeftDock");
            mainSplit.AddChild(leftDock);
            centerSplit = new VSplitContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            mainSplit.AddChild(centerSplit);
            centerDock = MakeDock("CenterDock");
            bottomDock = MakeDock("BottomDock");
            centerSplit.AddChild(centerDock);
            centerSplit.AddChild(bottomDock);
            leftDock.SizeFlagsHorizontal = SizeFlags.Fill;
            leftDock.CustomMinimumSize = new Vector2(540, 120);
            bottomDock.SizeFlagsVertical = SizeFlags.Fill;
            bottomDock.CustomMinimumSize = new Vector2(200, 260);
            root.AddChild(BuildStatusBar());
            BuildOverlay();
            BuildTabMenu();

            OpenDefaultPanels();

            Doc.StatusMessage += SetStatus;
            Doc.PsdlReplaced += UpdateCounts;
            Doc.DataChanged += _ => UpdateCounts();
            UpdateCounts();
            GetWindow().Title = "PSDL Viewer";
            GetWindow().FilesDropped += OnFilesDropped;
            SetStatus("Ready. Open a PSDL file with File > Open (Ctrl+O), or drop files on the window.");
            CallDeferred(MethodName.HandleCommandLine);
        }

        public override void _ExitTree() => AppLog.Uninstall();

        public override void _UnhandledInput(InputEvent e) {
            if (e is not InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape } || busy) return;
            DeselectAll();
            GetViewport().SetInputAsHandled();
        }

        /// <summary>Escape: clears the selection of every list and of the 3D view.</summary>
        void DeselectAll() {
            foreach (var table in FindChildren("*", "", true, false).OfType<VirtualTable>()) table.ClearSelection();
            Doc.DeselectAll();
            SetStatus("Selection cleared.");
        }

        // ---- layout ----

        TabContainer MakeDock(string name) {
            var dock = new TabContainer {
                Name = name,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                SizeFlagsVertical = SizeFlags.ExpandFill,
                DragToRearrangeEnabled = true,
                TabsRearrangeGroup = 1,
                CustomMinimumSize = new Vector2(200, 120)
            };
            var bar = dock.GetTabBar();
            bar.TabCloseDisplayPolicy = TabBar.CloseButtonDisplayPolicy.ShowActiveOnly;
            bar.TabClosePressed += index => ClosePanel(dock, (int)index);
            bar.TabRmbClicked += index => ShowTabMenu(dock, (int)index);
            return dock;
        }

        void OpenDefaultPanels() {
            OpenPanel(new RoomsPanel(), leftDock);
            OpenPanel(new RoomPathsPanel(), leftDock, false);
            OpenPanel(new PathsetsPanel(), leftDock, false);
            OpenPanel(new TerrainView(), centerDock);
            OpenPanel(new VerticesPanel(), bottomDock);
            OpenPanel(new TexturesPanel(), bottomDock, false);
            OpenPanel(new HeightsPanel(), bottomDock, false);
            OpenPanel(new InstPanel(), bottomDock, false);
            OpenPanel(new RoutesPanel(), bottomDock, false);
            OpenPanel(new ConsolePanel(), bottomDock, false);
        }

        /// <summary>Closes every panel and restores the docks, split positions and panels of a fresh start.</summary>
        void ResetLayout() {
            foreach (var dock in new[] { leftDock, centerDock, bottomDock }) {
                while (dock.GetTabCount() > 0) ClosePanel(dock, 0);
                dock.Visible = true;
            }
            panelCounters.Clear();
            mainSplit.SplitOffsets = new[] { 0 };
            centerSplit.SplitOffsets = new[] { 0 };
            OpenDefaultPanels();
        }

        string NextName(string kind) {
            panelCounters.TryGetValue(kind, out int n);
            panelCounters[kind] = ++n;
            return n == 1 ? kind : $"{kind} {n}";
        }

        public void OpenPanel(DockPanel panel, TabContainer dock = null, bool select = true) {
            dock ??= panel is TerrainView ? centerDock : leftDock;
            panel.Name = NextName(panel.Kind);
            dock.AddChild(panel);
            if (select) dock.CurrentTab = dock.GetTabIdxFromControl(panel);
        }

        void ClosePanel(TabContainer dock, int index) {
            var panel = dock.GetTabControl(index);
            if (panel == null) return;
            dock.RemoveChild(panel);
            panel.QueueFree();
        }

        void BuildTabMenu() {
            tabMenu = new PopupMenu();
            AddChild(tabMenu);
            tabMenu.AddItem("Move to left dock", 0);
            tabMenu.AddItem("Move to center dock", 1);
            tabMenu.AddItem("Move to bottom dock", 2);
            tabMenu.AddSeparator();
            tabMenu.AddItem("Rename...", 3);
            tabMenu.AddItem("Close", 4);
            tabMenu.IdPressed += id => {
                var panel = tabMenuDock?.GetTabControl(tabMenuIndex);
                if (panel == null) return;
                switch (id) {
                    case 0:
                    case 1:
                    case 2:
                        var target = id == 0 ? leftDock : id == 1 ? centerDock : bottomDock;
                        if (target == tabMenuDock) return;
                        panel.Reparent(target);
                        target.CurrentTab = target.GetTabIdxFromControl(panel);
                        break;
                    case 3:
                        Dialogs.Input("Rename view", "Name:", panel.Name, name => {
                            name = name.Trim().Replace(".", " ").Replace(":", " ").Replace("/", " ");
                            if (name != "") panel.Name = name;
                        });
                        break;
                    case 4:
                        ClosePanel(tabMenuDock, tabMenuIndex);
                        break;
                }
            };
        }

        void ShowTabMenu(TabContainer dock, int index) {
            tabMenuDock = dock;
            tabMenuIndex = index;
            tabMenu.Position = (Vector2I)GetGlobalMousePosition();
            tabMenu.ResetSize();
            tabMenu.Popup();
        }

        Control BuildStatusBar() {
            var panel = new PanelContainer();
            var bar = new HBoxContainer();
            panel.AddChild(bar);
            statusLabel = new Label { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipText = true };
            countsLabel = new Label { Modulate = new Color(1, 1, 1, 0.7f) };
            bar.AddChild(statusLabel);
            bar.AddChild(countsLabel);
            return panel;
        }

        void BuildOverlay() {
            overlay = new Control { Visible = false, MouseFilter = MouseFilterEnum.Stop };
            overlay.SetAnchorsPreset(LayoutPreset.FullRect);
            var dim = new ColorRect { Color = new Color(0, 0, 0, 0.45f), MouseFilter = MouseFilterEnum.Ignore };
            dim.SetAnchorsPreset(LayoutPreset.FullRect);
            overlay.AddChild(dim);
            var center = new CenterContainer();
            center.SetAnchorsPreset(LayoutPreset.FullRect);
            overlay.AddChild(center);
            var box = new PanelContainer { CustomMinimumSize = new Vector2(420, 0) };
            center.AddChild(box);
            var v = new VBoxContainer();
            v.AddThemeConstantOverride("separation", 8);
            var margin = new MarginContainer();
            foreach (string side in new[] { "left", "right", "top", "bottom" }) margin.AddThemeConstantOverride("margin_" + side, 14);
            box.AddChild(margin);
            margin.AddChild(v);
            overlayTitle = new Label();
            overlayTitle.AddThemeFontSizeOverride("font_size", 18);
            overlayNote = new Label();
            overlayBar = new ProgressBar { MinValue = 0, MaxValue = 1, Step = 0.001, CustomMinimumSize = new Vector2(0, 22) };
            v.AddChild(overlayTitle);
            v.AddChild(overlayNote);
            v.AddChild(overlayBar);
            AddChild(overlay);
        }

        public override void _Process(double delta) {
            if (!overlay.Visible || progress == null) return;
            overlayNote.Text = progress.Note;
            overlayBar.Value = progress.Fraction;
        }

        void SetStatus(string text) {
            statusLabel.Text = text;
            statusLabel.TooltipText = text;
        }

        void UpdateCounts() {
            var p = Doc.Psdl;
            string name = string.IsNullOrEmpty(Doc.PsdlPath) ? "untitled" : Path.GetFileName(Doc.PsdlPath);
            countsLabel.Text = $"{name}   {p.Vertices.Count} vertices   {p.Heights.Count} heights   {p.Textures.Count} textures   {p.Rooms.Count} rooms   {p.RoomPaths.Count} room paths   {Doc.Inst.Components.Count} INST   {Doc.Bai.Routes.Count} routes";
            GetWindow().Title = $"PSDL Viewer - {name}";
        }

        /// <summary>Runs a long operation with the progress overlay; exceptions are reported in a dialog.</summary>
        public async Task Run(string title, Func<Progress, Task> work) {
            if (busy) {
                SetStatus("Busy, please wait for the current operation to finish.");
                return;
            }
            busy = true;
            progress = new Progress();
            overlayTitle.Text = title;
            overlayNote.Text = "";
            overlayBar.Value = 0;
            overlay.Visible = true;
            try {
                await work(progress);
            } catch (Exception e) {
                GD.PrintErr(e.ToString());
                Dialogs.Error($"{title}: {e.Message}");
            } finally {
                overlay.Visible = false;
                busy = false;
                UpdateCounts();
            }
        }

        // ---- menu ----

        enum Cmd {
            New, Open, Save, OpenInst, SaveInst, OpenBai, SaveBai, OpenPathset, ImportVertices, AppendPsdl, ExportText, Options, Exit,
            ViewRooms, ViewVertices, ViewTextures, ViewHeights, ViewRoomPaths, ViewInst, ViewRoutes, ViewTerrain, ViewPathsets, ViewConsole, RefreshRooms, FlushTextures, ResetLayout,
            AutoNeighbours, AutoLastAttr, AutoRoomFlags, AutoRoomSort, AutoDimensions, RandomTerrain, AutoFacadeBounds, GenerateCpvs,
            ListAttributes, About
        }

        Control BuildMenu() {
            var bar = new MenuBar();
            var file = Menu(bar, "File");
            Item(file, "New", Cmd.New, Key.N);
            Item(file, "Open...", Cmd.Open, Key.O);
            Item(file, "Save...", Cmd.Save, Key.S);
            file.AddSeparator();
            Item(file, "Open INST...", Cmd.OpenInst);
            Item(file, "Save INST...", Cmd.SaveInst);
            file.AddSeparator();
            Item(file, "Open BAI...", Cmd.OpenBai);
            Item(file, "Save BAI...", Cmd.SaveBai);
            file.AddSeparator();
            Item(file, "Open pathset...", Cmd.OpenPathset);
            file.AddSeparator();
            Item(file, "Import vertices...", Cmd.ImportVertices);
            Item(file, "Append PSDL file...", Cmd.AppendPsdl);
            Item(file, "Export PSDL text dump...", Cmd.ExportText);
            file.AddSeparator();
            Item(file, "Options...", Cmd.Options, Key.P);
            file.AddSeparator();
            Item(file, "Exit", Cmd.Exit, Key.Q);

            var view = Menu(bar, "View");
            var views = new PopupMenu { Name = "Views" };
            views.IdPressed += id => Execute((Cmd)(int)id);
            Item(views, "Room list", Cmd.ViewRooms, Key.B, true);
            Item(views, "Vertex list", Cmd.ViewVertices, Key.V, true);
            Item(views, "Texture list", Cmd.ViewTextures, Key.T, true);
            Item(views, "Height list", Cmd.ViewHeights, Key.F, true);
            Item(views, "Room path list", Cmd.ViewRoomPaths, Key.U, true);
            Item(views, "INST list", Cmd.ViewInst, Key.I, true);
            Item(views, "BAI route list", Cmd.ViewRoutes, Key.None);
            Item(views, "Terrain view", Cmd.ViewTerrain, Key.Key3, true);
            Item(views, "Pathset list", Cmd.ViewPathsets, Key.None);
            Item(views, "Console", Cmd.ViewConsole, Key.None);
            view.AddChild(views);
            view.AddSubmenuNodeItem("Views", views);
            Item(view, "Refresh rooms", Cmd.RefreshRooms, Key.R);
            Item(view, "Flush textures", Cmd.FlushTextures);
            view.AddSeparator();
            Item(view, "Reset layout", Cmd.ResetLayout);

            var tools = Menu(bar, "Tools");
            Item(tools, "Auto neighbours", Cmd.AutoNeighbours);
            Item(tools, "Auto last attribute", Cmd.AutoLastAttr);
            Item(tools, "Auto room flags", Cmd.AutoRoomFlags);
            Item(tools, "Auto room sort", Cmd.AutoRoomSort);
            Item(tools, "Auto dimension", Cmd.AutoDimensions);
            Item(tools, "Random terrain...", Cmd.RandomTerrain);
            Item(tools, "Auto facade bounds", Cmd.AutoFacadeBounds);
            Item(tools, "Generate CPVS...", Cmd.GenerateCpvs);
            tools.SetItemTooltip(0, "Sets up all neighbour references of all room perimeters");
            tools.SetItemTooltip(1, "Sets up the last attribute flag in all room attributes");
            tools.SetItemTooltip(2, "Sets up the road and intersection flags of all rooms based on the contained attributes");
            tools.SetItemTooltip(3, "Sorts all rooms by type: plain, road, intersection");
            tools.SetItemTooltip(4, "Sets the min and max vectors and the bounding sphere");
            tools.SetItemTooltip(6, "Adds a facade bound for every facade that doesn't have one");
            tools.SetItemTooltip(7, "Generates the simplest possible CPVS file from the perimeters");

            var help = Menu(bar, "Help");
            Item(help, "List room attributes...", Cmd.ListAttributes);
            Item(help, "About...", Cmd.About);
            return bar;
        }

        PopupMenu Menu(MenuBar bar, string title) {
            var menu = new PopupMenu { Name = title };
            menu.IdPressed += id => Execute((Cmd)(int)id);
            bar.AddChild(menu);
            return menu;
        }

        static void Item(PopupMenu menu, string label, Cmd cmd, Key key = Key.None, bool alt = false) {
            menu.AddItem(label, (int)cmd);
            if (key == Key.None) return;
            var shortcut = new InputEventKey { Keycode = key, CtrlPressed = true, AltPressed = alt };
            menu.SetItemShortcut(menu.ItemCount - 1, new Shortcut { Events = new Godot.Collections.Array { shortcut } }, true);
        }

        void Execute(Cmd cmd) {
            switch (cmd) {
                case Cmd.New:
                    Dialogs.Confirm("Discard the current PSDL and start a new one?", () => _ = ReplacePsdl(new PsdlFile(), "New PSDL", ""));
                    break;
                case Cmd.Open:
                    Dialogs.OpenFile("Open PSDL file", new[] { "*.psdl ; PSDL files", "*.xml ; PSDL-ML files" },
                        new[] { ("Load sibling INST", Settings.Current.LoadSiblingInst), ("Load sibling BAI", Settings.Current.LoadSiblingBai) },
                        (path, options) => {
                            Settings.Current.LoadSiblingInst = options[0];
                            Settings.Current.LoadSiblingBai = options[1];
                            Settings.Save();
                            OpenPsdl(path);
                        });
                    break;
                case Cmd.Save:
                    Dialogs.SaveFile("Save PSDL file", new[] { "*.psdl ; PSDL files" }, Path.GetFileName(Doc.PsdlPath), path => _ = Run("Saving PSDL file", async p => {
                        await Task.Run(() => Doc.Psdl.Save(path, p));
                        Doc.PsdlPath = path;
                        SetStatus("Wrote " + path);
                    }));
                    break;
                case Cmd.OpenInst:
                    Dialogs.OpenFile("Open INST file", new[] { "*.inst ; INST files" }, path => _ = Run("Opening INST file", async p => {
                        var inst = await Task.Run(() => InstFile.Load(path, p));
                        Doc.SetInst(inst, path);
                        await scene.RebuildInst(p);
                        SetStatus($"Loaded {inst.Components.Count} INST components from {path}");
                    }));
                    break;
                case Cmd.SaveInst:
                    Dialogs.SaveFile("Save INST file", new[] { "*.inst ; INST files" }, Path.GetFileName(Doc.InstPath), path => {
                        try {
                            Doc.Inst.Save(path);
                            Doc.InstPath = path;
                            Dialogs.Info($"Wrote file \"{path}\"");
                        } catch (Exception e) {
                            Dialogs.Error("Save INST: " + e.Message);
                        }
                    });
                    break;
                case Cmd.OpenBai:
                    Dialogs.OpenFile("Open BAI file", new[] { "*.bai ; BAI files" }, path => _ = Run("Opening BAI file", async p => {
                        var bai = await Task.Run(() => BaiFile.Load(path, p));
                        Doc.SetBai(bai, path);
                        await scene.RebuildBai(p);
                        SetStatus($"Loaded {bai.Routes.Count} routes from {path}");
                    }));
                    break;
                case Cmd.SaveBai:
                    Dialogs.SaveFile("Save BAI file", new[] { "*.bai ; BAI files" }, Path.GetFileName(Doc.BaiPath), path => {
                        try {
                            Doc.Bai.Save(path);
                            Doc.BaiPath = path;
                            Dialogs.Info($"Wrote file \"{path}\"");
                        } catch (Exception e) {
                            Dialogs.Error("Save BAI: " + e.Message);
                        }
                    });
                    break;
                case Cmd.OpenPathset:
                    OpenPathset();
                    break;
                case Cmd.ImportVertices:
                    Dialogs.OpenFile("Import vertices (one \"x,y,z\" per line)", new[] { "*.txt, *.csv ; Text files", "* ; All files" }, path => {
                        try {
                            int n = Tools.ImportVertices(Doc.Psdl, path);
                            Doc.NotifyChanged(DataKind.Vertices);
                            SetStatus($"Imported {n} vertices.");
                        } catch (Exception e) {
                            Dialogs.Error("Import vertices: " + e.Message);
                        }
                    });
                    break;
                case Cmd.AppendPsdl:
                    Dialogs.OpenFile("Append PSDL file", new[] { "*.psdl ; PSDL files" }, path => _ = Run("Appending PSDL file", async p => {
                        var psdl = Doc.Psdl;
                        await Task.Run(() => psdl.Append(path, p));
                        Doc.SetPsdl(psdl, Doc.PsdlPath);
                        await RenderAll(p);
                        ReportLoadError(psdl, path);
                    }));
                    break;
                case Cmd.ExportText:
                    Dialogs.SaveFile("Export PSDL text dump", new[] { "*.txt ; Text files" }, "psdl.txt", path => {
                        try {
                            PsdlMl.ExportText(Doc.Psdl, path);
                            SetStatus("Wrote " + path);
                        } catch (Exception e) {
                            Dialogs.Error("Export: " + e.Message);
                        }
                    });
                    break;
                case Cmd.Options:
                    ShowSingleton<OptionsPanel>(() => new OptionsPanel(), leftDock);
                    break;
                case Cmd.Exit:
                    GetTree().Quit();
                    break;
                case Cmd.ViewRooms: OpenPanel(new RoomsPanel()); break;
                case Cmd.ViewVertices: OpenPanel(new VerticesPanel(), bottomDock); break;
                case Cmd.ViewTextures: OpenPanel(new TexturesPanel(), bottomDock); break;
                case Cmd.ViewHeights: OpenPanel(new HeightsPanel(), bottomDock); break;
                case Cmd.ViewRoomPaths: OpenPanel(new RoomPathsPanel()); break;
                case Cmd.ViewInst: OpenPanel(new InstPanel(), bottomDock); break;
                case Cmd.ViewRoutes: OpenPanel(new RoutesPanel(), bottomDock); break;
                case Cmd.ViewTerrain: OpenPanel(new TerrainView(), centerDock); break;
                case Cmd.ViewPathsets: OpenPanel(new PathsetsPanel()); break;
                case Cmd.ViewConsole: ShowSingleton<ConsolePanel>(() => new ConsolePanel(), bottomDock); break;
                case Cmd.RefreshRooms: _ = RefreshRooms("Rendering terrain"); break;
                case Cmd.FlushTextures:
                    _ = Run("Rendering terrain", async p => {
                        TextureStore.Flush();
                        PkgStore.Flush();
                        scene.ApplySettings();
                        await RenderAll(p);
                    });
                    break;
                case Cmd.ResetLayout: ResetLayout(); break;
                case Cmd.AutoNeighbours: RunTool("Computing perimeters", p => Tools.AutoNeighbours(Doc.Psdl, p), DataKind.Rooms); break;
                case Cmd.AutoLastAttr: RunTool("Computing last attribute flags", p => Tools.AutoLastAttribute(Doc.Psdl, p), DataKind.Rooms); break;
                case Cmd.AutoRoomFlags: RunTool("Computing room flags", p => Tools.AutoRoomFlags(Doc.Psdl, p), DataKind.Rooms); break;
                case Cmd.AutoRoomSort: RunTool("Sorting rooms", p => Tools.AutoRoomSort(Doc.Psdl, p), DataKind.Rooms, DataKind.Inst, DataKind.RoomPaths); break;
                case Cmd.AutoDimensions:
                    Tools.AutoDimensions(Doc.Psdl);
                    scene.RebuildCityBound();
                    Doc.NotifyChanged(DataKind.Dimensions);
                    var d = Doc.Psdl.Dimensions;
                    SetStatus($"Dimensions: min {BinaryUtil.V(d.Min)}, max {BinaryUtil.V(d.Max)}, center {BinaryUtil.V(d.Center)}, radius {BinaryUtil.F(d.Radius)}");
                    break;
                case Cmd.RandomTerrain:
                    ShowSingleton<RandomCityPanel>(() => new RandomCityPanel(), leftDock);
                    break;
                case Cmd.AutoFacadeBounds:
                    RunTool("Computing facade bounds", p => {
                        int n = Tools.AutoFacadeBounds(Doc.Psdl, p);
                        CallDeferred(MethodName.SetStatus, $"Added {n} facade bounds.");
                    }, DataKind.Rooms);
                    break;
                case Cmd.GenerateCpvs:
                    Dialogs.Input("Generate CPVS", "Maximum radial distance (metres):", "200", text => {
                        if (!BinaryUtil.TryParseFloat(text, out float radius)) {
                            Dialogs.Error("Enter a number.");
                            return;
                        }
                        Dialogs.SaveFile("Save CPVS file", new[] { "*.cpvs ; CPVS files" }, Path.GetFileNameWithoutExtension(Doc.PsdlPath) + ".cpvs",
                            path => _ = Run("Generating CPVS file", p => Task.Run(() => Tools.GenerateCpvs(Doc.Psdl, radius, path, p))));
                    });
                    break;
                case Cmd.ListAttributes:
                    Dialogs.ShowText("Known room attributes", RoomAttribute.KnownAttributesText());
                    break;
                case Cmd.About:
                    ShowAbout();
                    break;
            }
        }

        void RunTool(string title, Action<Progress> tool, params DataKind[] changed) {
            _ = Run(title, async p => {
                await Task.Run(() => tool(p));
                foreach (var k in changed) Doc.NotifyChanged(k);
                SetStatus(title.TrimEnd('.') + ": done. Use View > Refresh rooms to update the 3D view.");
            });
        }

        void ShowSingleton<T>(Func<T> create, TabContainer dock) where T : DockPanel {
            foreach (var d in new[] { leftDock, centerDock, bottomDock }) {
                foreach (var c in d.GetChildren()) {
                    if (c is not T existing) continue;
                    d.CurrentTab = d.GetTabIdxFromControl(existing);
                    return;
                }
            }
            OpenPanel(create(), dock);
        }

        void ShowAbout() {
            var d = new AcceptDialog { Title = "About PSDL Viewer" };
            var row = new HBoxContainer();
            row.AddChild(new Label {
                Text = "PSDL Viewer\n\n" +
                       "Analyzer/editor for PSDL files.\n" +
                       "PSDL files define terrain, roads and simple\n" +
                       "buildings for a city in the Microsoft game\n" +
                       "Midtown Madness 2.\n\n" +
                       "The original PSDL Viewer (a Java 3D application)\n" +
                       "was created by Fredrik Bergholtz, a.k.a. fre_ber. \n" +
                       "This version is made with Godot " + Engine.GetVersionInfo()["string"]
            });
            d.AddChild(row);
            AddChild(d);
            d.Confirmed += d.QueueFree;
            d.Canceled += d.QueueFree;
            d.PopupCentered();
        }

        // ---- documents ----

        void OnFilesDropped(string[] files) {
            foreach (string f in files) {
                switch (Path.GetExtension(f).ToLowerInvariant()) {
                    case ".psdl":
                    case ".xml":
                        OpenPsdl(f);
                        break;
                    case ".inst":
                        _ = Run("Opening INST file", async p => {
                            Doc.SetInst(await Task.Run(() => InstFile.Load(f, p)), f);
                            await scene.RebuildInst(p);
                        });
                        break;
                    case ".bai":
                        _ = Run("Opening BAI file", async p => {
                            Doc.SetBai(await Task.Run(() => BaiFile.Load(f, p)), f);
                            await scene.RebuildBai(p);
                        });
                        break;
                    case ".pathset":
                        _ = LoadPathset(f);
                        break;
                }
            }
        }

        public void OpenPsdl(string path) {
            if (path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) {
                Dialogs.Input("Import psdl-ml", "Welding range (metres):", "0.1", text => {
                    if (!BinaryUtil.TryParseFloat(text, out float weld)) {
                        Dialogs.Error("Enter a number.");
                        return;
                    }
                    _ = Run("Opening PSDL-ML file", async p => {
                        await UnloadSiblings(p);
                        var psdl = await Task.Run(() => PsdlMl.Import(path, weld, p));
                        UseFileFolders(path);
                        Doc.SetPsdl(psdl, path);
                        if (Settings.Current.LoadSiblingInst) await LoadSiblingInst(path, p);
                        if (Settings.Current.LoadSiblingBai) await LoadSiblingBai(path, p);
                        await RenderAll(p);
                    });
                });
                return;
            }
            _ = Run("Opening PSDL file", async p => {
                await UnloadSiblings(p);
                var psdl = await Task.Run(() => PsdlFile.Load(path, p));
                UseFileFolders(path);
                Doc.SetPsdl(psdl, path);
                string inst = Settings.Current.LoadSiblingInst ? await LoadSiblingInst(path, p) : null;
                string bai = Settings.Current.LoadSiblingBai ? await LoadSiblingBai(path, p) : null;
                await RenderAll(p);
                string status = $"{(psdl.LoadError == null ? "Loaded" : "Partially loaded")} {path}";
                if (inst != null) status += $", {Doc.Inst.Components.Count} INST components from {inst}";
                if (bai != null) status += $", {Doc.Bai.Routes.Count} routes from {bai}";
                SetStatus(status);
                ReportLoadError(psdl, path);
            });
        }

        /// <summary>Warns that a damaged PSDL file could only be read up to the point where the problem was found.</summary>
        static void ReportLoadError(PsdlFile psdl, string path) {
            if (psdl.LoadError == null) return;
            Dialogs.Warning($"{Path.GetFileName(path)} is damaged: {psdl.LoadError}\nShowing the data that was read before that point.");
        }

        /// <summary>Drops the current INST and BAI files so they do not stay attached to a newly opened PSDL.</summary>
        async Task UnloadSiblings(Progress p) {
            Doc.SetInst(new InstFile(), null);
            Doc.SetBai(new BaiFile(), null);
            await scene.RebuildInst(p);
            await scene.RebuildBai(p);
        }

        /// <summary>Loads the INST file next to a PSDL file with the same base name (sf.psdl -> sf.inst), if there is one. Returns its path, or null.</summary>
        Task<string> LoadSiblingInst(string psdlPath, Progress p) =>
            LoadSibling(psdlPath, ".inst", "INST", path => InstFile.Load(path, p), Doc.SetInst);

        /// <summary>Loads the BAI file next to a PSDL file with the same base name (sf.psdl -> sf.bai), if there is one. Returns its path, or null.</summary>
        Task<string> LoadSiblingBai(string psdlPath, Progress p) =>
            LoadSibling(psdlPath, ".bai", "BAI", path => BaiFile.Load(path, p), Doc.SetBai);

        static async Task<string> LoadSibling<T>(string psdlPath, string extension, string kind, Func<string, T> load, Action<T, string> set) {
            string path = Path.ChangeExtension(psdlPath, extension);
            if (!File.Exists(path)) return null;
            try {
                set(await Task.Run(() => load(path)), path);
                return path;
            } catch (Exception e) {
                GD.PrintErr(e.ToString());
                Dialogs.Error($"Opening {kind} file {path}: {e.Message}");
                return null;
            }
        }

        /// <summary>Also searches the texture and geometry folders next to the city folder (MM2 layout) while this PSDL file is open; they are not saved.</summary>
        static void UseFileFolders(string psdlPath) {
            if (!Settings.SetFileFolders(psdlPath)) return;
            TextureStore.Flush();
            PkgStore.Flush();
        }

        public async Task ReplacePsdl(PsdlFile psdl, string title, string path = null) {
            await Run(title, async p => {
                Doc.SetPsdl(psdl, path ?? Doc.PsdlPath);
                await scene.RebuildPsdl(p);
            });
        }

        public Task RefreshRooms(string title) => Run(title, p => scene.RebuildPsdl(p));

        async Task RenderAll(Progress p) {
            await scene.RebuildPsdl(p);
            await scene.RebuildInst(p);
            await scene.RebuildBai(p);
            await scene.RebuildPathsets(p);
        }

        public void RenderInst() => _ = Run("Rendering INST", p => scene.RebuildInst(p));
        public void RenderPathsets() => _ = Run("Rendering pathsets", p => scene.RebuildPathsets(p));

        public void OpenPathset() {
            Dialogs.OpenFile("Open pathset file", new[] { "*.pathset ; Pathset files" }, path => _ = LoadPathset(path));
        }

        Task LoadPathset(string path) {
            return Run("Opening pathset", async p => {
                var set = await Task.Run(() => Pathset.Load(path));
                Doc.Pathsets.Add(set);
                Doc.NotifyChanged(DataKind.Pathsets);
                await scene.RebuildPathsets(p);
                SetStatus($"Loaded {set.Paths.Count} paths from {path}");
            });
        }

        /// <summary>Applies new options and redraws what they affect (TerrainOptionTab.apply -> terrain.redraw).</summary>
        public void ApplySettings(Settings next) {
            var old = Settings.Current;
            bool paths = !old.TexturePaths.SequenceEqual(next.TexturePaths) || !old.GeometryPaths.SequenceEqual(next.GeometryPaths);
            bool city = paths || old.ColouringMode != next.ColouringMode || old.LightingMode != next.LightingMode || old.RenderRoomAttributes != next.RenderRoomAttributes;
            bool props = paths || old.LightingMode != next.LightingMode;
            bool labels = old.ShowRoomIndices != next.ShowRoomIndices || Math.Abs(old.LabelSize - next.LabelSize) > 1e-4f;
            Settings.Replace(next);
            scene.ApplySettings();
            if (labels) scene.RebuildLabels();
            if (!city && !props) return;
            _ = Run("Rendering terrain", async p => {
                if (paths) {
                    TextureStore.Flush();
                    PkgStore.Flush();
                }
                if (city) await scene.RebuildPsdl(p);
                if (props) {
                    await scene.RebuildInst(p);
                    await scene.RebuildPathsets(p);
                    await scene.RebuildBai(p);
                }
            });
        }

        static IEnumerable<T> Descendants<T>(Node node) where T : Node {
            foreach (var child in node.GetChildren()) {
                if (child is T t) yield return t;
                foreach (var d in Descendants<T>(child)) yield return d;
            }
        }

        // ---- command line: --psdl, --inst, --bai, --sibling-inst, --sibling-bai, --pathset, --screenshot <png>, --camera x,y,z,pitch,yaw ----

        async void HandleCommandLine() {
            var args = OS.GetCmdlineUserArgs();
            string Arg(string name) {
                int i = Array.IndexOf(args, name);
                return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
            }
            string psdl = Arg("--psdl");
            if (psdl == null) {
                OnFilesDropped(args.Where(a => !a.StartsWith("--") && File.Exists(a)).ToArray());
                return;
            }
            if (Arg("--colouring") is string colouring && int.TryParse(colouring, out int cm)) Settings.Current.ColouringMode = cm;
            if (Arg("--lighting") is string lighting && int.TryParse(lighting, out int lm)) Settings.Current.LightingMode = lm;
            if (args.Contains("--labels")) Settings.Current.ShowRoomIndices = true;
            scene.ApplySettings();
            await Run("Opening PSDL file", async p => {
                UseFileFolders(psdl);
                var file = await Task.Run(() => PsdlFile.Load(psdl, p));
                Doc.SetPsdl(file, psdl);
                await scene.RebuildPsdl(p);
                ReportLoadError(file, psdl);
                if (Arg("--inst") is string inst) {
                    Doc.SetInst(await Task.Run(() => InstFile.Load(inst, p)), inst);
                    await scene.RebuildInst(p);
                } else if (args.Contains("--sibling-inst") && await LoadSiblingInst(psdl, p) != null) {
                    await scene.RebuildInst(p);
                }
                if (Arg("--bai") is string bai) {
                    Doc.SetBai(await Task.Run(() => BaiFile.Load(bai, p)), bai);
                    await scene.RebuildBai(p);
                } else if (args.Contains("--sibling-bai") && await LoadSiblingBai(psdl, p) != null) {
                    await scene.RebuildBai(p);
                }
                if (Arg("--pathset") is string ps) {
                    Doc.Pathsets.Add(await Task.Run(() => Pathset.Load(ps)));
                    Doc.NotifyChanged(DataKind.Pathsets);
                    await scene.RebuildPathsets(p);
                }
            });
            if (Arg("--select") is string sel && BinaryUtil.TryParseHex(sel, out int room)) Doc.SelectRooms(new[] { room }, null);
            if (Arg("--select-inst") is string selInst && BinaryUtil.TryParseHex(selInst, out int component)) Doc.SelectInst(new[] { component }, null);
            if (Arg("--camera") is string cam) {
                float[] c = cam.Split(',').Select(s => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                foreach (var view in Descendants<TerrainView>(this)) view.SetCamera(new Vector3(c[0], c[1], c[2]), new Vector3(c[3], c[4], 0));
            }
            string shot = Arg("--screenshot");
            if (shot == null) return;
            for (int i = 0; i < 30; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var image = GetViewport().GetTexture().GetImage();
            image.SavePng(shot);
            GD.Print("Saved screenshot " + shot);
            GetTree().Quit();
        }
    }
}
