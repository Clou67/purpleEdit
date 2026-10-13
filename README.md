# purpleEdit

A level viewer (and future editor) for the Krome Merkury 2 engine games **The Legend of Spyro: A New Beginning** and **The Eternal Night**, targeting the PS2 versions first. It's written in C# with WinForms and OpenTK.

![layout](https://img.shields.io/badge/.NET-8.0-blue)

## Projects

| project | what it is |
|---|---|
| `Mk2.Core` | Format library with no dependencies: RKV2/BPK, BNI, MDL3/MDG3, TEX, MIN, CGR, levels.ini, lc3 layouts, model resolution, and a glTF (.glb) exporter |
| `Mk2.Viewer` | WinForms + OpenTK 4 (GLControl) level viewer |
| `Mk2.Cli` | Command-line tool for batch export, validation and dumps |

## Build & run

You'll need the .NET 8 SDK on Windows.

```
cd purpleEdit
dotnet build -c Release
dotnet run -c Release --project Mk2.Viewer -- "C:\path\to\GAME FILES"
```

Or open `purpleEdit.sln` in Visual Studio 2022 and start `Mk2.Viewer`.

The game folder can be any folder that contains the extracted files and/or the archives, in any layout. The viewer indexes everything underneath it:
* loose extracted files (`extracted/BNI/levels.ini.bni`, `extracted/MDL/*.mdl` …)
* chunk packs (`*.BPK`)
* full archives (`DATA_P2.RKV`). BPKs nested inside these are opened on demand.

`levels.ini.bni` and `global.model.bni` are needed for full levels. Without them you can still browse packs and models.

## Viewer

* **Levels tab**: double-click a level to load all of its chunks, or a chunk to load just that chunk plus the env chunk.
* **Scene tab**: tick chunks and categories to show or hide them, and filter objects by class, ID name or ID number. Double-click an object to fly to it.
* **Packs tab**: view everything in one `.BPK`. Level geometry stays in place; props are laid out on a grid.
* **Models tab**: view any single model.
* **Properties**: shows the full lc3 entry, decoded transform, class/alias chain, model candidates, materials/textures, and script commands. It also lists "referenced by" (other objects whose fields or scripts mention this object's ID).
* **Overlays** you can turn on or off: textures, prelit vertex colours (brightness ×1–2), lighting, wireframe, fog from levels.ini, ground and prop collision (coloured per collision material), `c_*` collision components and `f_*` fragments inside models, and trigger boxes/spheres/polygons, paths, volumes, cameras, spawns, sounds and logic objects.
* **Export**: the current scene (visible chunks) to `.glb` with embedded textures and object transforms, the selected model to `.glb`, all scene textures to PNG, decompiled layouts to text, and screenshots.

Controls: right-drag to look, WASD to move, Q/E for down/up, Shift/Ctrl for faster/slower, the mouse wheel sets fly speed, middle-drag pans, left-click selects, F focuses the selection, and Esc deselects.

## CLI

```
mk2cli levels   <gameRoot>
mk2cli objects  <gameRoot> <level> [chunk]
mk2cli level    <gameRoot> <level> <out.glb> [--chunks a,b] [--collision] [--no-objects]
mk2cli model    <gameRoot> <model> <out.glb> [--collision]
mk2cli tex      <file.tex> <out.png>
mk2cli bni      <file.bni> [out.txt]
mk2cli cgr      <file.cgr>
mk2cli extract  <archive> <outDir> [--group]
mk2cli validate <gameRoot>          # parses every asset in every archive and checks counts
```

## Status

Format details are in [FORMATS.md](FORMATS.md).

**Working:**
* Full level assembly from levels.ini: chunks, overlays, env dome and ground collision.
* All PS2 model vertex layouts (static and skinned), PS2 textures (4/8-bit CLUT, RGB24, RGBA32), materials (texture override, alpha test, additive, double-sided, UV scroll), and collision grids.
* Object placement using the game's own rotation order (taken from `Matrix::SetRotationPYR` in the GC ELF).
* Triggers, volumes, paths, cameras and other logic shapes.

**Not yet:**
* Animations (ANM3/ANG) and skeleton display.
* GameCube (big-endian) MDL3/TEX.
* Editing and saving layouts. The BNI reader keeps the full tree, so writing it back is the next step (see the INI editor's compiler).
