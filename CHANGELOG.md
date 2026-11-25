# Changelog

Versions follow [Semantic Versioning](docs/versioning.md); 1.0 will be the first version that fully supports GTA IV (PC). SparkIV shows this file in Settings → About, and after an update the part of the new version.

## v0.9.0-alpha.8 – 2026-11-25

- GTA VI: the shader databases in `shaders.rpf` (`.sgd5`) open in a new viewer: every shader with its stage, shader model, size and hash, and the effects with their techniques and the shaders of each pass. Shaders export as `.dxbc` files (DXBC containers with DXIL, e.g. for `dxc -dumpbin`), one, several or all at once, with the effects as a text file.
- The hex view opens large files (hundreds of MB) right away: it loads the part shown.
- GTA VI: large compressed files (such as the shader pack in `shaders.rpf`) unpack completely; only their first 64 KB came before.

## v0.9.0-alpha.7 – 2026-11-25

- New (experimental, read-only): **Red Dead Redemption 2** (PC). SparkIV opens its RPF8 archives with the file names of the RAGE-StringsDatabase; a one-time setup assistant reads the archives' keys from the running game and keeps them on your PC.
- New (experimental, read-only): **Grand Theft Auto VI** (Xbox Series). SparkIV opens its RPF9 archives, also archives inside archives (multi-GB ones read in place). The cipher is read from the game's own executable; nothing secret is part of SparkIV.
- GTA VI: encrypted files are decrypted and compressed ones unpacked, so XML, metadata (PSIN) and resources open as their real content.
- GTA VI: file and folder names are recovered from the paths' hashes, using the game's own strings; an optional name list (`file_list.tsv` in the game folder or in `%LocalAppData%\SparkIV`) adds more. Files whose name is not known yet are in an "(unnamed)" folder, with their type as extension.
- GTA VI: textures (`.ztd`) open in the texture viewer, with all mipmap levels, zoom, channels and PNG export.
- GTA VI: the texture pools (`data\ttdb\*.ttpf`) open like a folder: every texture as a `.dds` file that opens in the texture viewer. Pool textures stored in some Xbox BCPack formats (some BC7) can't be read yet; they are in a folder of their own.
- GTA VI: audio (`.awc`) opens in the audio viewer: Opus and PCM tracks play and export as WAV, also the radio stations in stereo; the stored data of every track can be exported.
- GTA VI: subtitle and text databases (`.zldbmt`) are unpacked, so their text can be read in the hex view.
- The texture viewer and the audio viewer are the same for every game: GTA IV's `.wtd` and `.ivaud`, GTA VI's `.ztd`, `.ttpf` textures and `.awc`. The texture viewer also opens `.dds` files.
- Edit mode is off for Red Dead Redemption 2 and GTA VI: writing their archives is not tested yet.
- SparkIV may only be used for offline modding. It says so once at the first start, in NOTICE.txt and in Settings → About ("Notice on use").
- The main window has one "Choose Game" button, which opens the game selector, instead of one button per game.
- New setting "Texture filtering" for the 3D views, like the game's (Tri-Linear, Anisotropic x2 to x16; default Anisotropic x16).
- New setting "Smooth edges" for the 3D views (on by default).
- Lots of bug fixes, mainly in the cutscene viewer:
  - Street lamps (also the ones that can be knocked over) have their glow and light, and traffic lights show the glow of their current colour, switched by the game's clock.
  - The map's smoke plumes and steam are shown and lit as in the game.
  - Vehicle headlamps are drawn as in the game.
  - Lamps: 8 lamp shadow maps as in the game, all of a section's cutscene lights are used (not only the 8 strongest), map spotlights are as wide as in the game, and floodlights no longer appear twice.
  - Glass lamp globes no longer show speckles and are lit by the lamps' cones of light as in the game.
  - Characters: the inner faces of clothes no longer show (hi-vis vest), sleeves, arms and necks twist as in the game, hair fades into the skin as in the game, and their materials no longer mirror the surroundings wrongly.
  - Lamp back plates and other mirroring materials are no longer too bright.
  - Surfaces without a texture are black, as in the game.
  - The morning and night sky are no longer too dark, and the clouds look as in the game.
  - The water's mirror fades into the distance, and the swell moves with the game's clock as strongly as in the game.
  - Depth of field and the fog's colour are closer to the game; far versions of buildings no longer light the scene.
  - Glows that blend over the picture (the credits, night windows) fade into the distance as in the game, and far night windows show from the same distance as in the game.

## v0.9.0-alpha.6 – 2026-11-06

- Cutscenes, models and animations are now drawn by SparkIV's own Direct3D 12 renderer instead of a web page: much smoother playback (also in busy scenes like the intro's harbour), faster loading, and a picture closer to the game.
- Cutscenes: playback no longer jumps back and forth after pausing and resuming; the picture follows the sound smoothly.
- Cutscenes: a cutscene's .wad file opens in the cutscene view with just its section, with its models, place, sound and subtitles.
- Cutscenes: the night sky has its stars and the galaxy, and the clouds drift with the time of day.
- Cutscenes: the cutscene's own lights and the lamps around light the scene as in the game; lamps inside buildings only shine out where a door or window is near.
- Cutscenes: glowing signs fade into the haze in the distance, car and ped glass is lit as in the game, and worn props such as glasses always show.
- Cutscene view: in the free view the cutscene's camera is shown again, the background choice works again, and the Fit button is gone.
- New "Open file" button (Ctrl+O) opens a single file from disk.
- Models: vehicles that showed nothing (e.g. dukes.wft) open again.

## v0.9.0-alpha.5 – 2026-11-04

- Cutscenes: the intro starts at 05:15 and its clock stands still, as in the game.
- Cutscenes inside interiors are lit as in the game: room lighting blends through doors and windows, lamps light only from visible rooms, and glowing lamps, posters and rugs are lit like the walls under them.
- Cutscenes: taxis whose lights the mission turns on show their red tail light.
- Cutscenes: scenes with many lamps draw a little faster.
- The cutscene view shows the frames per second while playing. SparkIV-fps.log (next to SparkIV.exe) records where a cutscene's frame rate drops below 30, with section and frame, and its lowest and highest rate.

## v0.9.0-alpha.4 – 2026-11-02

- Cutscenes (highly experimental): the viewer shows a running time of day that follows playback, also when rewinding. The start time can be set from the top bar, and "Lock time" keeps the clock and the clouds at it. So far cutscenes were tested mainly at noon, so other times of day can look noticeably wrong and will take a lot more work.
- Cutscenes: switching between sections and seeking no longer stutter or flash, loading is much faster and uses far less memory.
- Cutscenes: busy scenes (the intro's harbour) play much more smoothly.
- Cutscenes: scenes whose sections play in different places (the intro: the ship, then the harbour) show each section in its own place.
- Cutscenes: scenes that start inside but are filmed outside (the intro on the ship's deck) show the city's skyline.
- Cutscenes: subtitles of cutscenes made of several parts (the intro) appear at the right moment.
- Cutscenes: faces move like in the game (expressions, smiles, lip shapes).
- Cutscenes: shoulders, forearms and necks twist as in the game.
- Cutscenes: the sky and the sea are as bright as in the game after noon; water, sky and sun shadows are closer to the game.
- Cutscenes: clouds in The Lost and Damned's cutscenes, and the haze's colour as in the game.
- Cutscenes: lamps have their soft glow (coronas), lamp glass glows as in the game, and windows mirror their surroundings.
- Cutscenes: the pier lamp globes no longer show as dark balls against the sky, and the glow of a lamp right next to the camera shows.
- Cutscenes: more surfaces shine in the sun as in the game, and walls and floors show their bumps.
- Cutscenes: surfaces facing away from the camera are lit as in the game.
- Cutscenes: buildings and props are drawn as far as in the game at the highest view distance, and more of the distant city shows in the background.
- Cutscenes: one-sided map surfaces (plant leaves) are no longer drawn from behind; lattice railings stay solid from afar.
- Cutscenes: car and boat interiors no longer show a grey shine on their seats.
- Cutscenes fade in and out as long as in the game.
- Models: vertex colours no longer have red and blue swapped (shading of buildings, vehicles and characters).
- Textures: decoded at their full brightness (they were up to 3 % too dark).
- The changelog shows each version's release date.

## v0.9.0-alpha.3 – 2026-10-28

- Cutscenes: the water mirrors as in the game – far buildings only, no ground, broken up by its waves.
- Cutscenes: the water's waves are there from the start, breaking up its mirror as in the game.
- Cutscenes: the sky is no longer too white and the clouds no longer pink.
- Cutscenes: depth of field also blurs the foreground, and the picture is softened as in the game.
- Cutscenes: lamps light walls and rooms as in the game (bc_3's granite wall, Bernie's flat).
- Cutscenes: see-through glass shows its reflection as in the game (Bernie's glass table).
- Cutscenes: cutscene lights light faces as brightly as in the game.
- After an update, SparkIV shows what's new right away.

## v0.9.0-alpha.2 – 2026-10-26

- Cutscenes: vehicle windows are no longer drawn twice (bright streaks on car glass).
- Cutscenes: lamps of other floors no longer light through the floor.
- Cutscenes: see-through lamp shades no longer glow.
- Cutscenes: shiny surfaces outdoors no longer mirror too much sky.
- Cutscenes: ceiling spots' lenses light up whole, as in the game.
- Cutscenes: lamps show their cones of light in the air, as in the game.
- Cutscenes: lamp shadows on peds' clothes follow their folds, as in the game.
- Cutscenes: peds' normal maps work again (folds and marks on clothes, as in the game).
- Settings → About shows this changelog; after an update SparkIV offers to show what's new.

## v0.9.0-alpha.1 – 2026-10-24 (WinUI version, reditec)

- New WinUI 3 app on .NET 10.
- Installer; SparkIV updates itself from the GitHub releases.
- New logo, replacing the GTA IV icon.
- New viewers for models, animations, cutscenes and collision; the audio viewer can play and export multichannel banks.
- Removed components that were closed or licensed incompatibly with the GPL (ragezip.dll, Model3DViewer, the old WaveOut player).

## v0.8.56 – 2020-04-08 (Beta)

- Fix Update checker returning old version.

## v0.8.52 – 2020-04-05 (Beta)

- Added the support for GTA IV (Complete Edition) v1.2.0.32 (for Steam and non-Steam versions).

## v0.8.1 – 2019-01-02 (Beta)

- Added partial Darg & Drop support.
- New update system.
- Improved support for **GTA IV** ***1.0.8.0*** & **EFLC** ***1.1.3.0***.
- Added fallback folder browser dialog for selecting game folder if the ***registry path*** was deleted.
- Added file deleting ability by clicking (***Delete Key***) in the keyboard.

## v0.8.0 – 2019-01-01 (Beta)

- New icons
- Support for GTA IV 1.0.8.0 & GTA EFLC 1.1.3.0
- Windows 10+ Support
- New style
- Fixed some bugs
- Initial drag & drop support (WIP)
- Fix checking for updates not working
