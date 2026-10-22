# PSDL Viewer

PSDL Viewer is a viewer and editor for the PSDL files of Midtown Madness 2. PSDL files define the terrain, roads and simple buildings of a city, and the viewer shows them in 3D together with the related INST (instances), BAI (AI routes) and pathset files, and lets you inspect and edit their contents.

This is a Godot 4.7 port (with scripts written in C#) of the original PSDL Viewer, a Java 3D application created by Fredrik Bergholtz (a.k.a. fre_ber), whose source code is published at [ApanLoon/PSLDViewer](https://github.com/ApanLoon/PSLDViewer).

> [!NOTE]
> Claude Code was used during the development of this project and for writing its documentation, including this README.

## Features

- **Supported files**: PSDL files (open, save, append another PSDL file, import vertices, export a text dump), INST and BAI files (open and save), and pathsets (open). Files can also be opened by dropping them onto the window. The INST and BAI files next to a PSDL file with the same name (`sf.psdl` → `sf.inst`, `sf.bai`) can be loaded with it automatically, which you can turn on in the open dialog. The `texture` and `geometry` folders next to the PSDL file's folder are also searched while that file is open, after the folders set in the options.
- **3D view** of the city, including tunnels and railings: fill or line drawing, lit or unlit, with grey, random, texture index or texture map colouring, plus optional room indices, room bounds, city bounds, INST meshes, pathsets and BAI routes.
- **Editing panels** for rooms and their attributes, vertices, textures, heights, room paths, INST entries, BAI routes and pathsets. Each panel can be moved to the left, center or bottom dock and renamed.
- **Tools** (*Tools* menu): automatic neighbours, last attribute flags, room flags, room sorting, dimensions and facade bounds; generating a basic CPVS file from the perimeters; and a random city generator.
- **Console** (*View→Views→Console*) with the messages and errors of the program.

## Setup

Download the latest build for Windows or Linux from the releases page and extract it, or run the project from the Godot editor (see [Building](#building)).

The textures and meshes of a city are not stored in the PSDL file, so the viewer looks them up in the folders set in *File→Options*:

- **Texture paths**: folders containing the textures, in .tex, .gif, .jpg, .tga, .png or .bmp format.
- **Geometry paths**: folders containing the .pkg meshes used by the INST files.

The settings are saved in `settings.json` in the Godot user data folder of the program.

## Controls

The 3D view has two camera control schemes, which you can switch in *File→Options*:

| Action | Scheme A | Scheme B |
|---|---|---|
| Look / orbit | Right drag | Middle drag (orbits around the center of the view) |
| Pan | Middle drag, or Home + mouse | Shift + middle drag, Home + mouse, or Ctrl / Shift + wheel |
| Zoom | Wheel, PgUp / PgDn | Ctrl + middle drag, wheel, PgUp / PgDn |
| Zoom in on a point | Middle click | Middle click |

With *Emulate 3-button mouse* turned on, Alt + left button acts as the middle button.

The main menu commands have keyboard shortcuts: Ctrl+N / Ctrl+O / Ctrl+S to create, open and save a PSDL file, Ctrl+P for the options, Ctrl+R to refresh the rooms, and Ctrl+Alt+B / V / T / F / U / I / 3 to open the room, vertex, texture, height, room path, INST and terrain views.

## Building

The project requires [Godot 4.7.1 .NET](https://godotengine.org/download) and the .NET 9 SDK. Open `project.godot` in the Godot editor to run it, or export it with the *Windows Desktop* and *Linux* presets.

Every push to `master` that changes the program builds both versions and publishes them as a GitHub release. The workflow is in [.github/workflows/release.yml](.github/workflows/release.yml), and the version number is taken from [PSDL Viewer.csproj](PSDL%20Viewer.csproj).

## Code structure

All the code is under `Scripts/`:

| Folder | Purpose |
|---|---|
| `Scripts/Formats` | Readers and writers for the file formats: PSDL (and its XML form), INST, BAI, pathsets, PKG meshes and TEX textures. |
| `Scripts/Core` | The open document, the settings, the log, the editing tools and the random city generator. |
| `Scripts/Render` | Generation of the 3D city, with its materials, textures and meshes. |
| `Scripts/UI` | The 3D view, the docks, the panels and the dialogs. |
| `Scripts/Main.cs` | The main window, its menu and the commands. |

A description for the PSDL format can be found in [the angel-file-formats repository by Dummiesman](https://github.com/Dummiesman/angel-file-formats/blob/master/Midtown%20Madness%202/PSDL.md).

The generation of tunnels, railings, slivers and the ground texture mapping, as well as the interpretation of several fields the specification lists as unknown, are based on [mmclone_v2 by Dummiesman](https://github.com/Dummiesman/mmclone_v2).

## License

PSDL Viewer is released under the MIT license, see [License.txt](License.txt).

It is a port of the original PSDL Viewer by Fredrik Bergholtz ([ApanLoon/PSLDViewer](https://github.com/ApanLoon/PSLDViewer) also MIT-licensed). The icons in `Assets/Icons` come from the original program.
