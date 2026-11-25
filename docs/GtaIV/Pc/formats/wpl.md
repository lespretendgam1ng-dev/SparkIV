# GTA IV (PC): Placements (`.wpl`, binary)

Applies only to **GTA IV, PC version** (with TLAD and TBoGT). Overview of all formats: [../data-support.md](../data-support.md).

A `.wpl` defines which model stands where in the world. The loose files are under `pc/data/maps/**`; more are inside the map's `.img` archives.

## Header

| Offset | Type | Content | Source |
|---|---|---|---|
| 0 | uint32 | Version, always 3 | `CutsceneLocation.ReadPlacements` |
| 4 | uint32 | Number of instances | ibid. |
| 68 | – | Start of the instances, 48 bytes each | ibid. |

## Instance (48 bytes)

| Offset | Type | Content | Status | Source |
|---|---|---|---|---|
| 0 | 3 × float | Position x, y, z | used | |
| 12 | 4 × float | Rotation as quaternion x, y, z, w. The game reads it **inverted**: the model's rotation is the conjugate. | used | Comparison with game frames |
| 28 | uint32 | Hash of the model name (`GxtFile.Hash`, Jenkins one-at-a-time, lower case) | used | |
| 32 | uint32 | **Instance flags**, see below | partly used | GTAIV.exe 0x9D76C0 (field 8 of the entry) |
| 36 | int32 | Index of the distant version (LOD) in the loose `.wpl` of the same name; −1 = none | used | |
| 40, 44 | – | unknown | **unclear** | |

### Instance flags (offset 32)

When creating the object, the game copies three bits into its flag field (+0x28). Found in `Entity_CreateFromInstance` (GTAIV.exe 0x9D76C0, name from `ApplyCutsceneFindings.java`):

| Instance bit | Object +0x28 | Meaning | Status |
|---|---|---|---|
| 0x80 | \|= 4 | The lamp flag 0x4000 of the model lights takes effect. The exact effect is open. | ignored |
| 0x100 | \|= 8 | **Lamps of this object may cast shadows.** Only together with the lamp flag 0x8000 (see [2dfx-lights.md](2dfx-lights.md)) is a lamp drawn as a shadow lamp. | used (since 2026-10-22) |
| 0x200 | \|= 2 | unknown | **unclear** |
| 1, 4, 0x20, 0x40 | Bits in the object field +0x24 (0x1000000, 0x100000, clears 0x20000000, clears 0x80000000) | unknown | **unclear** |

Passing to the lights: `Entity_AddModelLights` (0xC1DB60) passes bits 1, 2 and 3 of +0x28 on to `Light_FromEffect` (0xC1E200).

**Evidence in the image:** In bc_3, the wall lamp at the pier has the lamp flag 0x8000, but its instance does not have 0x100. In the game video it has the orange glow of the normal lamp. With the shadow program it disappeared in SparkIV; with the instance condition it is back.

In SparkIV: `MapIndex.LampShadows`, `LocationObject.LampShadows`, in the light record `shadows`.

## How far a distant version is drawn

The game draws a distant version (LOD, its own LOD in turn, a district's `slod…`) as far as its `.ide` distance (the
third field of its `.ide` line) **times the LOD view distance** of the graphics settings:

- **Setting:** `[QUALITY SETTINGS] Set LOD view distance (0-99)` (command line `viewdistance`), stored at GTAIV.exe
  0x01160E94.
- **Factor:** 0x59EEF0 and 0x59F200 set 0x0118D838 = setting / 99 × 1.275 + 0.725. That is 0.725 at the lowest
  setting and **2.0 at the highest** (99, shown as 100 in the menu).
- **Not found:** the code that reads the factor; it lies in code Ghidra hasn't disassembled.
- The detail distance (0x01160E98) gives another value, setting / 99 × 100 + 10 (0x0103F714, via 0xAEBD70); what it
  scales is not checked.

**Evidence:** trace `GTAIV.1.trace` (bc_3), section 2, frame 786.
- The game shows the roofs of the tower blocks 752 m off across the water. Their distant version `lod_bs_ten01` has
  an `.ide` distance of 550 m, so it is drawn only with a factor of at least 1.37.
- With the factor 2.0, SparkIV draws them too. The reference's difference drops from 11.00 to 10.66; the other 22 of
  bc_3 and et01_ba stay within 0.03.
- The traces were recorded at the highest view distance (the user's setting), so the factor is 2.0.
- **Open:** in the same frame `LOD_mhb_cablebas` (also 550 m) 843 m off is not drawn, its district's super LOD
  instead. With 2.0 it would be in reach (1100 m), so another rule decides there (possibly streaming).

### Timed distant versions (the lit windows at night) without the view distance (#138, 2026-11-10)

The night windows' distant versions (`tobj`: `LOD_22_00_NL_MH1`, `LOD_21C_NL_MH1`, `LOD_N13_NL_MH1`, … 700 m,
`gta_emissivenight` with `emissiveMultiplier` 16, textures of `mh_emissive_lods.wtd`) and their district's
`SLOD_NL_03/04/07_MH7` (3000 m, multiplier 9, one 128 × 128 DXT1 of flat colour blocks) - in `GTAIV.3.trace` intro
s2 f352.75 (the frame ending at call 19415728) the game draws the SLODs 780 m and more off and none of the LOD_*_NL
there (its night glow draws, PS 0x2c472260 from 19412294 to 19414679: multipliers 7 to 30, never 16; the bound
texture of the windows at 1600,352 is SLOD_NL's, its mean 0.634 / 0.602 / 0.307 against 0.630 / 0.599 / 0.304).
With the factor 2.0 (1400 m) SparkIV drew the LOD_*_NL there: lit windows of photos with alpha (mean alpha 0.56), so the
skyline's windows came out at half the game's light (1600,352 before the fog: game 4.54 / 5.15 / 3.52, SparkIV 1.98 /
2.62 / 2.57 in the game's units). SparkIV now takes a timed distant version only up to its `.ide` distance; the
window there is 3.59 / 4.52 / 3.55 (after the fog; game 3.41 / 4.29 / 3.96). Why the game leaves the factor off for
them is not found in GTAIV.exe (the rule is from this frame); the distance is from the location's origin, as for the
others. Status: **used** (`CutsceneLocation`, `TimedFarWithViewDistance` / cutgen `D3D12TIMEDFARVIEW=1` for the old way).

### What the cutscene camera cuts off, and which version of a group is drawn

Found by leaving out draws of the same frame (`d3dretrace --ignore-calls`): the buildings behind the cranes that
SparkIV missed are the district super LODs `SupLod_BkN_03` and `SupLod_BkN_08` (draws 136285579 and 136285573).
Their centres are 844 and 779 m off.

- **Far plane:** the cutscene's draw distance is the camera's far plane. The trace's projection gives 780 m there:
  P22 = −1.0000641, P32 = −0.05, near 0.05 m. A far plane cuts by **depth along the view**, not by distance. At the
  picture's sides that reaches up to a quarter farther.
- **Groups:** a distant version counts if any of it (its `.ide` bounding sphere) lies within the draw distance, not
  only its centre.
- **Parents:** a parent whose children are only partly in reach stays drawn, and only the children in reach are
  drawn with it.

**In SparkIV:**
- `CutsceneLocation.LodViewDistance` = 2.0, the highest setting, on the distant versions and on the models
  themselves (a model up to its `.ide` distance × 2.0; the former 150 m limit is gone).
- `Wanted` and `Choose` (groups and parents) and the page's `drawReach` (the depth along the view) follow the rules
  above.
- bc_3 s2 f786 10.66 → 10.59, s1 f78.75 −0.03, s3 f357.25 +0.09 (only the sea's mirror); et01_ba unchanged.
- With the factor on the models: bc_3's pier roof is back (s2 f436.75 −0.06, s3 f506.5 −0.07), s2 f786 −0.10;
  et01_ba f1292.25 −0.16, the rest within 0.05.
- **Pitfall found on the way:** a model whose texture isn't in its own dictionaries makes SparkIV search all
  dictionaries of its archive's folder. Those were kept for the other models by name alone, but the same name can
  stand for different dictionaries in different archives: GTA IV's `amenitie.img` (searched for a telegraph pole,
  `CJ_telgrphpole`, 361 m off) and TLAD's `e1_xref.img` both hold a `bm_tollbooth.wtd`, with a different `bm_glass`.
  et01_ba's booth then took GTA IV's glass and its panes lost their tint. The dictionaries found by searching are now
  kept by archive folder and name (`ModelScene.SharedTextures.Searched`).
