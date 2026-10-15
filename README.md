# purpleEdit

A level viewer (and future editor) for the Krome Merkury 2 engine games **The Legend of Spyro: A New Beginning** and **The Eternal Night**, targeting the PS2 versions first. It's written in C# with Dear ImGui (Hexa.NET.ImGui) and OpenTK.

![layout](https://img.shields.io/badge/.NET-8.0-blue)

## Projects

| project | what it is |
|---|---|
| `Mk2.Core` | Format library with no dependencies: RKV2/BPK, BNI, MDL3/MDG3, TEX, MIN, CGR, ANM3/ANG, BBI, levels.ini, lc3 layouts, model resolution, and a glTF (.glb) exporter |
| `Mk2.Viewer` | ImGui + OpenTK 4 level viewer |
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

* **File**: open a game folder, or open a single `.RKV` / `.BPK` directly (nothing needs extracting first). **Extract RKV…** writes an archive's files to a folder. The export items save the visible scene or the selected model to `.glb`, scene textures to PNG, decompiled layouts to text, and screenshots.
* **Levels** / **Packs** menus: load a whole level, or view everything in one `.BPK` (level geometry stays in place, props are laid out on a grid). A level opens at the first checkpoint of its start chunk.
* **Browser**: tick chunks, groups, categories and single items to show or hide them, and filter objects by class, ID name or ID number. Click an object, overlay or ground mesh to select it and fly to it.
* **Render**: Geometry (overlays, objects, sky dome, ground and object collision), Lighting (textures, vertex colours, transparency, fog, brightness) and Volumes (triggers, spawns, checkpoints, paths, volumes, cameras, sounds, logic, critters). Volumes start hidden.
* **Properties**: the level summary when nothing is selected; otherwise the decoded transform, class/alias chain, model candidates, shapes, materials and textures, the full lc3 entry and its scripts. **References** and **Referenced by** list the objects linked by ID in fields or ScriptCommands; click one to jump to it. Right-click a section to copy it.
* **Model Viewer**: pick a model, then one of its animations and a clip from the `.bbi`. Play/pause (or Space), loop and scrub the frame. **Skeleton** shows the bones and a bone tree; click a bone to see it in Properties.

Controls: right-drag to look, WASD to move, Q/E for down/up, Shift/Ctrl for faster/slower, the mouse wheel sets fly speed, middle-drag pans, left-click selects, F focuses the selection, and Esc deselects.

## CLI

```
mk2cli levels   <gameRoot>
mk2cli objects  <gameRoot> <level> [chunk]
mk2cli level    <gameRoot> <level> <out.glb> [--chunks a,b] [--collision] [--no-objects]
mk2cli model    <gameRoot> <model> <out.glb> [--collision]
mk2cli tex      <file.tex> <out.png>
mk2cli bni      <file.bni> [out.txt]
mk2cli anim     <gameRoot> <name> [--keys]          # skeleton, clips, events and matched model
mk2cli skin     <gameRoot> <model> [anim] [clip|frame]  # bone each part follows, and the vertices that move furthest
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
* Animations (ANM3/ANG) with BBI clip lists and events, skinned and rigid-part playback, and skeleton display.

**Not yet:**
* Placing animated props in levels in a chosen clip, and exporting animations to glTF.
* GameCube (big-endian) MDL3/TEX.
* Editing and saving layouts. The BNI reader keeps the full tree, so writing it back is the next step (see the INI editor's compiler).
