# GTA IV (PC): Particle effects (`.wpfl`, the 2dfx particle entries)

Applies only to **GTA IV, PC version** (with TLAD and TBoGT). Overview of all formats: [../data-support.md](../data-support.md).
Status: **used** for the map's always-playing sprite effects (#118, 2026-11-22; see the end).
Findings from 2026-11-22 (#118, #137, #144).

## Where the map's effects come from

- **The .ide files' `2dfx` section, type 1** (2378 lines in GTA IV): `model, x, y, z, 1, qx, qy, qz, qw, TAG, trigger,
  attach, scale, distance, flags, r, g, b`. The offset and turn are the effect's on the model. Seen:
  - et01_ba (TLAD): `wii_plume_dummy ... AMB_INDUST_PLUME_SM,0,0,1,100,0,255,255,255` three times (616.2/789.1/44.8,
    670.3/648.5/41.1, 614.8/686.7/41.1): the smoke plumes over the toll booths (#118, #144).
  - intro s2: `ET_ShipIn62` offset (−0.93, −27.49, 20.52) `AMB_INDUST_PLUME_SM,0,0,0.5,100,1,48,64,64`: the ship's
    plume (−343.7/−1503.5/34.1), at half scale and tinted (48, 64, 64) - the "glow" of #137.
  - bc_3: `AMB_STEAM_CHIMNEY` on BM_vent*, `AMB_STEAM_WALL_VENT`, `AMB_FLIES_CIRCLING`.
  - trigger 0 = always (ambient); 3 / 4 = on breaking or shattering (`BRK_*`, `DST_*`).
- **The tag to the effect:** `common/data/effects/entityFx.dat` (`AMB_INDUST_PLUME_SM` → `ambient_tower_plume_small`,
  `AMB_STEAM_CHIMNEY` → `ambient_steam_chimney`, …) with creation thresholds (rain, wind, temperature, hours) and an
  optional light.

## The library (`pc/data/effects/gta_entity.wpfl`, `gta_core.wpfl`, `gta_script.wpfl`; episodes `gta_ep1/2.wpfl`)

A RAGE resource (type 0x24 / 0x1B), a `ptxFxList`. Pointers 0x5xxxxxxx are into the system part. At its start:

| Offset | What | gta_entity |
|---|---|---|
| +0x00 | texture dictionary (`pgDictionary<grcTexture>`) | 62 (`smoke_chimney_sheet`, `smoke_puff`, …) |
| +0x08 | model dictionary | 37 |
| +0x0C | emit rules (`pgDictionary<ptxEmitRule>`) | 178 |
| +0x14 | particle rules (`ptxRule`: `ptxSprite` / `ptxModel`) | 172 |
| +0x18 | effect rules (`pgDictionary<ptxEffectRule>`), name at +0x08 | 133 |

A dictionary: +0x10 hashes, +0x14 count, +0x18 entries (pointers).

**Curves** (`rmPtfxKeyframe`, 0x28 bytes): +0x18 entries, +0x1C count. An entry is 0x30 bytes: time (+0x00), value vec4
(+0x10), slope to the next (+0x20): piecewise linear. Values are (min…, max…) pairs; a particle takes its own random
point between them (a byte per curve, `DAT_018b7588` table).

**Effect rule** (`ptxEffectRule`): name, five effect curves, a time line (`ptxTimeLine`, +0xF0) with events; the event
emitter (`ptxEventEmitter`) names the emit rule and the particle rule (`amb_tower_plume_sm` for both) and holds the
evolutions (`HybridAddRatio`, `HDRIntensityMultiplier` as keyframed shader variables).

**Emit rule** (`ptxEmitRuleStd`): +0x10 the emitter domain, the velocity domain; ten curves, the parser's names
(GTAIV.exe strings, `rage::ptxEmitRuleStd::stdEmitterData`) in memory order: `SpawnRateKF`, `SpawnDistKF`,
`TimeScaleKF`, `SpawnLifeKF`, `SpeedKF`, `SizeKFOT`, `AccelerationKFOT`, `DampeningKFOT`, `MatrixWeightKFOT`,
`InheritVelKFOT`; then its name and `Duration`.

**Domain** (`ptxDomain`; type +0x08: box, sphere, cylinder, vortex): `PositionKF`, `DirectionKF` (degrees),
`SizeKF`, `InnerSizeKF` over the emitter's time.

**Particle rule** (`ptxSprite`): its property list at +0x160, the curves one after another (0x34 bytes each, the id at
+0x28). The ids by the parser's registration (`ptxRulePropList` 0x669AD0, `ptxSpriteRulePropList` 0x669DA0, member
records name +0 / offset +8; the constructors 0x63ACB0, 0x64B0A0 set the ids):

| id | curve | id | curve | id | curve |
|---|---|---|---|---|---|
| 0 | ColorKF | 8 | RotateVelKF | 16 | TextureAnimRateKF |
| 1 | ColorMaxKF | 9 | AccelerationMinKF | 17 | PlaybackRateKF |
| 2 | CollisionVelDmpKF | 10 | AccelerationMaxKF | 18 | WindInfluenceKF |
| 3 | CollisionImpVarKF | 11 | DampeningMinKF | 19 | AlphaKF |
| 4 | SizeKF | 12 | DampeningMaxKF | 20 | PositionNoiseKF |
| 5 | ThetaKF | 13 | MatrixWeightKF | 21 | VelocityNoiseKF |
| 6 | InitThetaKFOT | 14 | DirectionalKF | 31 | VortexPropsKF (not in GTA IV's files) |
| 7 | InitRotateVelKFOT | 15 | DirectionalVelKF | 33 | TrailPropsKF |

After the curves: texture tiles x, y (2, 2), the shader (`rmPtfxShader`: name `gta_rmptfx_litsprite`, technique
`draw_soft`, its variables: `DiffuseTex2` = `smoke_chimney_sheet`, `gSoftness` 0.2, `Diffuse` 0.9, `AmbientAmount` 0.9,
`SuperAlpha` 2, `NormalArc` 0.2, `gScaleFade` 8, `WrapScale` 1, `WrapBias` 0, `RimScale` 0).

### `ambient_tower_plume_small` (gta_entity)

| | |
|---|---|
| emit | 12–14 a second, time scale 0.65, life 7.5–10.5 s, speed 1; emitter domain a cylinder r 0.09 m; velocity domain centre (−2…6…−2, 0, 6) over the emitter's 4 s, spread (0.16, 1.2, 0.16); both turned 90° about x |
| size | (1.19–1.67) → 3.97–6.04 at 0.26 → 6.99–12.6 at 0.88 of life |
| alpha | 0.59–0.73 → 0.40–0.63 at 0.21 → 0.09–0.31 at 0.40 → 0 at the end |
| acceleration | (2.5–3, −0.6–0.6, −0.5–1) |
| dampening | 0.40–0.49 / s |
| wind influence | 0 → 0.2–0.22 at 0.25 → 0.3–0.35 at 0.6 |
| turn | start −70…70°, −0.1…−0.2 / s |
| texture | `smoke_chimney_sheet` 2 × 2, animation rate 0 → 0.5–0.8 from 0.2 to 0.55 of life |

## The update (GTAIV.exe 0x64D3D0, ptxSprite vtable +0x30)

Per particle and frame: life += PlaybackRate × (1 / life length) × dt; acceleration = lerp(AccelerationMin,
AccelerationMax) at life, in the effect's axes (world axes with flag bit 8); velocity += acceleration × dt, then
× (1 − lerp(DampeningMin, DampeningMax) × dt); wind × WindInfluence; position += velocity × dt; the size from SizeKF
at life × the effect's and the emitter's scale. Runtime curve offsets of the rule: 0x31C PlaybackRate, 0x4BC Size,
0x4F0 Theta, 0x280 / 0x2B4 Dampening min / max, 0x454 WindInfluence, 0x58C RotateVel.

## Drawing (`gta_rmptfx_litsprite.fxc`, GTAIV.3.trace intro s2 f863, draw 34241242: VS 0x2d30d4a0, PS 0x2d006e20)

- 68 quads (6 vertices of 108 bytes each, rewritten every frame): centre, the corner (± half size) and the turn,
  colour (D3DCOLOR), the two atlas frames' UVs, and (v6) the hybrid-add share and the HDR multiplier (1.6 here).
- VS: the quad turned and spread in clip space (× gAspectRatio), faded within gScaleFade (8 m) of the near plane;
  lit per vertex: the sun with a 9-tap shadow map lookup, the normal bent by `gNormalArc`, wrap (`gWrapScale`,
  `gWrapBias`) × `gDiffuse`; ambient above / below (`gLightAmbient0/1` by the normal's y) × `gAmbientAmount`; up to
  four lamps (`gLightPos/Dir/Col`, the lamps' falloff and cone); fog and the depth effect.
- PS: soft edge against the scene's depth ((scene − particle depth)² × gSoftness), the two atlas frames blended,
  × the vertex colour × the HDR multiplier, the depth effect's desaturation and gamma, the fog; `HybridAdd`.
- The vertex declaration (GTAIV.3.trace call 200308): position, normal (the corner ± half size, z the turn), colour
  (D3DCOLOR), TEXCOORD0 the two atlas cells' UVs, TEXCOORD1, TEXCOORD2 (two numbers no program reads, then 0), TEXCOORD3
  (the hybrid-add share 0, the HDR multiplier 1.6), TEXCOORD4.
- **The lighting numbers are fixed**, not the rule's own shader values: in both traces (GTAIV.3 intro s2 by night,
  GTAIV.2 et01_ba by day, draws 224301426 / 224301524 / 224301543) the draws get gWrapScale 0.6, gWrapBias 0.3,
  gDiffuse 0.9, gExtraLights 0 (no lamps), gAmbientAmount 0.35, gSuperAlpha 2, gNormalArc 0, gScaleFade 25,
  gUseShadows 0.4, gAmbientShadow 0.5, gLightIntensityClamp 0.5, PS gSoftness 6, and the vertices' HDR multiplier 1.6
  (the event's HDRIntensityMultiplier curve says 1.0–1.1). The sun and the light from around are the scene's
  (gDirectionalColour.x × .w: 16.8 at et01_ba, = SparkIV's sun / exposure; gLightAmbient0/1 = SparkIV's above,
  below − above). The vertex colour is white (the 2dfx's tint 48, 64, 64 on the ship isn't in it).
- **The sun's shadow:** the VS's 9 taps count the map's depths nearer the sun than the corner (`slt (stored − z), 0`),
  and the share is 1 + gUseShadows × (count / 9 − 1): a particle in the open sun gets 0.6, one fully in shadow 1 (the
  light from around × max(that, gAmbientShadow 0.5)). Measured: GTAIV.2.trace et01_ba f1394.75, d3dretrace before and
  after the plume draws (224301420 / 224301545): over the 1439 changed samples (x 656–944, y 264–458) the sky 7.29 /
  9.52 / 11.74 becomes 6.50 / 7.30 / 8.10 (a grey smoke of 6.06 at alpha 0.64), which 0.6 gives (SparkIV with it 7.11 /
  7.89 / 8.66; with 1: 9.52 / 10.16 / 10.80).

## What the traced particles show, and SparkIV's fitted motion (2026-11-22)

The traced particles of `amb_tower_plume_small` (68 at the intro ship, 62 + 72 + 68 at et01_ba) against their
emitters, by the second number of TEXCOORD2 (0–0.97, taken as the life: AlphaKF at it gives the vertex alphas):

| life | intro (scale 0.5): mean offset, half size | et01_ba (scale 1) |
|---|---|---|
| 0–0.2 | −0.1 / −0.5 / 1.5, 0.9 | −0.4 / −0.7 / 2.3, 1.7 |
| 0.2–0.4 | 2.4 / −3.4 / 3.1, 1.4 | 1.8 / −3.9 / 5.5, 2.7 |
| 0.4–0.6 | 6.2 / −8.5 / 4.3, 1.9 | 5.8 / −9.4 / 6.4, 3.7 |
| 0.6–0.8 | 8.9 / −14.1 / 5.7, 2.7 | 8.1 / −15.0 / 8.3, 4.8 |
| 0.8–1.0 | 12.3 / −20.2 / 9.6, 2.8 | 12.4 / −21.9 / 12.3, 5.6 |

- The sizes scale with the 2dfx's scale, the motion doesn't, and both scenes drift the same way.
- The rule's own motion as read (the velocity domain's (−2…6, 0, 6), acceleration (2.5–3, ±0.6, …)) carries the
  particles 40 m along +x; with the game's wind field unknown, SparkIV takes a fit over both traces: the velocity
  domain × 0.15, the acceleration × 0.25, a field of (3, −26, 13) m/s carried at WindInfluenceKF (the update's wind
  term), mean offsets within 1–4 m of both.
- The drawn sizes grow slower than SizeKF at the life (et01_ba: 0.56 × the curve's mean), though the vertex builder
  (0x650430) reads SizeKF at the life (its render copy +0x50 = the particle's +0xF0; 0x64AF90): SparkIV takes SizeKF at
  0.36 of the life (fitted, rms 0.9 m over both). Open.
- In SparkIV: `ParticleLibrary` (the reader), `CutsceneParticles` (sources, simulation, batches; the fitted numbers),
  `MeshRenderer.DrawParticles` (VSParticle / PSParticle, the game's lighting per corner with the sun's shadow map).
  Not yet: the game's wind, collisions, the domains' shapes and turns, soft edges against the scene, model particles,
  effects that need rain or wind (entityFx.dat thresholds), effects beyond 250 m of the scene's origin.
- Visual test (2026-11-22, full run): intro s2 f863.00 1.92 → 1.87, et01_ba s1 f1394.75 3.81 → 3.76, the other 70
  unchanged. Status: **used**.
