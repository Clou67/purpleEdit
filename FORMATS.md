# Krome MK2 formats — Legend of Spyro (PS2)

Everything below was decoded from the game files in `GAME FILES/` (The Eternal Night, PS2, little endian) and checked against every file in 15 chunk packs: 490 models, 465 textures, 311 collision files, 705 materials and 34 BNI files all parse, and every model's vertex and triangle counts match its header exactly.
Fields marked **(?)** are inferred, not proven.

Coordinate system: **+Y is up** in model space. Level geometry is stored directly in world space. The rotation convention comes from the GameCube ELF (`Matrix::SetRotationPYR`, see "Object transforms" below).

---

## How a level is put together

```
levels.ini.bni
 └ level temple
     startChunk temple_01 / envChunk temple_env / fogPlanes / fogCol / enemySpawnPool …
     chunk temple_env            → temple_env.BPK + temple_env.lc3.bni
        envDome Env_Temple       → sky model (follows the camera)
        ground  A07_c_01         → A07_c_01_all.cgr  (collision only, no visual)
     chunk temple_07             → temple_07.BPK + temple_07.lc3.bni
        Overlay A04_07           → A04_07.mdl/.mdg (visual level geometry, world space)
        Overlay A04_l_08 1       → optional second value (flag, meaning unknown)
```

* `<chunk>.BPK` is an RKV2 pack holding that chunk's models, textures, materials, animations, BBIs and collision.
* `<chunk>.lc3.bni` (NOT inside the BPK; it's in the main RKV) is the object layout for that chunk.
* `<chunk>.pkg` (text) is the pack's file list; `+name.min` lines are materials listed before their texture.
* Visual ground = the chunk *overlays*. Ground collision = the env chunk's `ground` list.
* `PropGroupVolume` / `Connector` objects in the layout control which overlays/chunks stream in.

## RKV2 archive (`.RKV`, `.BPK`)

| off | type | meaning |
|---|---|---|
| 0x00 | char[4] | `RKV2` |
| 0x04 | int | entry count |
| 0x08 | int | name table size |
| 0x0C | int | addendum (full path alias) count, 0 in BPKs |
| 0x14 | int | entry table offset (0x80 in BPKs) |

Entry, 0x14 bytes: `nameOffset, groupingRef, size, dataOffset, crc32`. The name table follows the entry table, and name offsets are relative to it.

## BNI: compiled Krome INI (`.ini.bni`, `.lc3.bni`, `.model.bni`, `.add.bni`, `.sc2.bni`, `.ui.bni` …)

Header 0x44 bytes: `char[32] path; int 0x64; int lineCount; int dataLen; int stringTableOff; int shortTableOff; int hashOff; int hashDivisor; int bsearchOff; int sectionCount`. The offsets are relative to 0x44.

Lines are 0x10 each: `u16 valueCount (0xFFFF = end of block), u16 sectionName, u16 fieldName, u16 firstValue, u16 childLine, u16 mask, pad[4]`. Names are in 4-byte units into the string table. Values come from the short table: `stringTable + shortTable[firstValue+k] * 4`.

> ⚠ The short-table entries are **unsigned**. The original INI editor reads them as signed, which corrupts values in large layout files (e.g. `temple_env.lc3.bni`, 900 KB).

There are two styles of section:
* lc3: `name P1008_ColumnBase` with **indented** children (`pos`, `rot`, `scale`, `color`, `ID`, …).
* model.bni: `name X` followed by **sibling** fields until the next `name`.

## Layout objects (`.lc3.bni`)

Each section is one object. The section name is the class (or prop) name. Common fields:

* `pos x y z`, `rot x y z` (radians: pitch, yaw, roll), `scale x y z`, `color r g b`
* `ID <number> <name>`: other objects and scripts reference the number
* `zone <bitmask>`, `SaveGroup`, `bEnabled`
* Event blocks such as `OnEnter`, `OnExit`, `OnShatter`, `OnDestruction` and `OnRespawn` hold `ScriptCommand <text>`, e.g. `send FunctionOn 8174 4176; delay 2 (send TeleportHeroToHere 582)`

Geometry carried by logic objects:

| class (examples) | field | shape |
|---|---|---|
| TriggerBoxProp | `box { pos, rot, scale }` | oriented box; scale = full size **(?)** |
| TriggerSphereProp | `sphere { pos, radius }` | sphere |
| SoundProp, Checkpoint | `dummySphere { pos, radius }` | sphere |
| TriggerPolyProp, EnemyRegion, EnemyExclusionRegion | `waypoints { waypoint … }` | polygon (floor outline) |
| SplinePath, SparxPath, ElementalSpline, WaterTrickleEmitter | `waypoints` | polyline |
| SpawnPoint | `waypoints`, `wallpoints` | polylines |
| PropGroupVolume, Connector | `region { waypoint … }` + `overlay1..12` / `chunkTerrainN` | streaming volumes |
| EventCamera, Checkpoint | `camera { cameraSource, cameraTarget, FOV, near, far }` | camera |
| CollideBox, CameraCollideProp | `pos`, `rot`, `scale` | box; base size of 100 units is a guess **(?)** |
| Critters (C0xx_*) | `scale` holds the **swarm volume size**, `count` holds the number of critters | box |

### Model resolution (`global.model.bni`)
For each class, the viewer walks the `alias` chain and tries each section's `model` (`%s` = class name) and `badfile` (animated model; same base name as the `.mdl`), then falls back to the class name itself. Classes with `type … draw-` are invisible. For example, `DragonBustAnimatingDoor → badfile P1977_DragonDoor`, and `C002_TempleFlicker → badfile C_Temple_Flicker`.

### Object transforms
From `Matrix::SetRotationPYR` in the GameCube ELF (`spyro06.elf`):
`M = Pitch(rot.x) · Yaw(rot.y) · Roll(rot.z)`, using row vectors (`v' = v·M`, translation in the last row).
Each axis matrix is the transpose of the usual right-handed one:
`Pitch rows (1,0,0)(0,c,-s)(0,s,c)`, `Yaw rows (c,0,s)(0,1,0)(-s,0,c)`, `Roll rows (c,-s,0)(s,c,0)(0,0,1)`.
In System.Numerics that's `CreateRotationX(-x) * CreateRotationY(-y) * CreateRotationZ(-z)`, followed by scale first and translation last.

## MDL3 + MDG3 models (PS2)

The `.mdl` is the header; the `.mdg` ("guts") holds the geometry as PS2 VIF packets.

MDL3 header:

| off | type | meaning |
|---|---|---|
| 0x00 | char[4] | `MDL3` |
| 0x04 | u16 | component (sub-object) count |
| 0x06 | u16 | material count |
| 0x08 | u16 | anim node (bone) count |
| 0x0A | u16 | ref point count |
| 0x0E | u16 | mesh count |
| 0x10 | u16 | anim node list count |
| 0x14 | u32 | total vertex count |
| 0x18 | u32 | total triangle count (= verts − 2·strips) |
| 0x1E | u16 | strip count |
| 0x30 | vec4 | bounds min (w = radius) |
| 0x40 | vec4 | bounds size |
| 0x50 | u32 | component table (0x40 each: vec4 min, vec4 size, …, +0x30 name, +0x34 extra, +0x38 u16 bone count) |
| 0x54 | u32 | material name table (u32 string offsets; names are `.min` material names) |
| 0x58 | u32 | ref point table (u32 offsets → vec4 pos, +0x10 name offset) e.g. `r_A024_weapon01` |
| 0x5C | u32 | anim node bind positions (vec4 each) |
| 0x64 | u32 | anim node lists (128-byte bone remap tables) |
| 0x68 | u32 | mesh lookup table `[material][component]` → MDG offset of the first mesh (0 = none) |

Component names: `c_*` / `C_Collide` = collision proxy (hidden), `f_*` = shatter fragment, `m_*` = main mesh.

MDG3 mesh header (0x10): `u16 ?, u16 ?, u16 ?, u16 stripCount, u16 animNodeList (0xFFFF = none), u16 ?, u32 nextMeshOffset (0 = last)`. It's followed by a DMA chain (`cnt` tags) of VIF codes. Each strip is one VU1 batch:

```
UNPACK V4-32 @0  (2 qw)  [vertexCount, f32 ?, 0xCDCDCDCD, flags] [bounding sphere xyz r]
STCYCL cl=4 wl=1          (vertex = 4 qwords: pos, normal, uv, colour)
UNPACK V3-32 @2           positions (float)
UNPACK V3-8  @3           normals (s8 / 127)                       — static
       V4-8  @3           normal xyz + bone0·2                      — skinned
UNPACK V2-16 @4           uv (s16 / 4096)                           — static
       V4-16 @4           u, v, weight0·4096, bone1·4               — skinned (weight1 = 1 − weight0)
UNPACK V4-8  @5           RGBA prelit colour (0x80 = 1.0)
MSCAL
```
Each batch is a plain triangle strip (no restart flags). Skinned vertices are stored in bind pose, model space. Bone indices go through the mesh's anim node list when it isn't 0xFFFF.

## MIN materials (0x1EC bytes)

| off | meaning |
|---|---|
| 0x000 | char[32] name |
| 0x020 | u32 hash |
| 0x024 | u32 blend (0 normal, 1 additive, 2 subtractive) **(?)** |
| 0x02C | f32 alpha-test reference (0.5 on cut-out foliage) **(?)** |
| 0x030 | f32[4] colour multiplier **(?)** |
| 0x080 | char[32] collision surface (`Wall`, `Mud`, `Wood` …) for `CM_*` materials |
| 0x0A0 | char[32] grass type (`grass`) |
| 0x0C0 | char[40] grass / scatter material (e.g. `G101_MushiesMoss`) |
| 0x0E8 | char[32] **texture name override** (empty = use the material name) |
| 0x140 | f32 u-scroll, 0x144 f32 v-scroll |
| 0x1E0 | u32 flags: `0x1` collision mat, `0x60` grass, `0x100` double-sided (`_NC`, `_DS`), `0x2000` alpha test, `0x8000` has texture override, `0x80000` uv scroll, `0x40000000` glow/HDR pass **(?)**, `0x80000000` env map **(?)** |

## TEX textures

Header 0x20: `"TEX\0", u16 w, u16 h, u32 palOff/dataSize, u32 dataSize, u8 format, u8 (8 for CLUT), u8 log2w, u8 log2h, …, u32 flags @0x18`.
Pixels start at 0x20 and are linear (not swizzled). The CLUT follows the pixels. Alpha is PS2 style (0x80 = opaque).

| fmt | meaning |
|---|---|
| 0x00 | RGBA32 |
| 0x01 | RGB24 |
| 0x13 | 8-bit indexed, 256-entry CLUT in CSM1 order (swap entries 8–15 / 16–23 of each 32-entry block) |
| 0x14 | 4-bit indexed (low nibble first), 16 colours stored as an 8×2 block in a 16-wide CLUT (entries 0–7 then 16–23) |

## CGR collision

| off | meaning |
|---|---|
| 0x00 | u32 version (2) |
| 0x04 | u32 cellsX, cellsY, cellsZ |
| 0x10 | vec4 cell size |
| 0x20 | vec4 bounds min |
| 0x30 | vec4 bounds max |
| 0x40 | u32 material count |
| 0x44 | u32 vertex data size (bytes) |
| 0x48–0xC3 | runtime pointers (garbage on disk) |
| 0xC8 | u32 cell table size (16-aligned) |
| 0xCC | u32 triangle data size (16-aligned) |
| 0xD0 | cells: `u16 firstTri, u16 triCount` × (X·Y·Z), padded with `X` |
| … | triangles, 14 bytes: `u8[6] ? , u16 a, u16 b, u16 c, u16 flags` (low byte = material index) |
| … | vertices, 16 bytes: `f32 x, y, z, u8 r, g, b, a` |
| … | materials: `char[32]` each (`CM_Wall`, `CM_Rock`, …) |

Triangles are stored per cell, so the same face can appear in several cells (the viewer de-duplicates them). Level ground uses `<ground>_all.cgr`; props use `<model>_00.cgr`, `_01` and so on.

## Still unknown / next steps
* The 6 leading bytes of each collision triangle (maybe edge or neighbour data) and the high bits of the triangle flags.
* What the overlay flag means (e.g. `Overlay A04_l_08 1`) and the 2nd/3rd/6th u16 of the MDG mesh header.
* ANM3/ANG animation decoding. The notes in `notes/mk2-mk1 formats msg.txt` describe it, and `Decompress_Vector` is in the GC ELF.
* BBI (animation descriptor), MIN fields 0x24–0x3C and 0x140+, and the PS2-vs-GameCube differences (GameCube files are big endian; the readers detect the RKV/BNI/CGR byte order, but MDL3/TEX GameCube variants are not implemented).
