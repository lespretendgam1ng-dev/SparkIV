<img src="assets/logo/sparkiv-icon-256.png" alt="SparkIV logo" width="96" align="right">

# SparkIV

> [!IMPORTANT]
> **SparkIV only works together with your own, legally owned copy of the games.**
> The keys and cipher data needed to read the game archives are **not** part of SparkIV. They are read at run time from the game's own executable on your PC (for Red Dead Redemption 2, once from the running game).
> This repository and the program contain **no keys, cipher tables, IVs or other data taken from the games**. They contain only hashes that are used to find and verify this data in your own game files. Without the game's executable, the archives can't be opened, even with the full source code.

A file browser, viewer and editor for **Grand Theft Auto IV** and **Episodes from Liberty City** (PC), with experimental, read-only support for **Red Dead Redemption 2** (PC) and **Grand Theft Auto VI** (Xbox Series).

This fork rebuilds SparkIV as a **WinUI 3 app on .NET 10**. Its viewers render the game's data the way the game does. The cutscene viewer, for example, reproduces the game's lighting, shadows and post effects. Findings about the file formats and the renderer are documented under [`docs/`](docs/GtaIV/Pc/data-support.md).

## Features

- Browse the game's IMG and RPF archives of GTA IV, The Lost and Damned and The Ballad of Gay Tony.
- Viewers:
  - **textures** (`.wtd`);
  - **models** (`.wdr`, `.wdd`, `.wft`), in 3D;
  - **animations** (`.wad`);
  - **audio banks** (`.ivaud`), with playback and export;
  - **scripts** (`.sco`), decompiled or disassembled;
  - **cutscenes** (`.cut`), with the cast, camera, lights, sound and their location;
  - **collision** (`.wbn`, `.wbd`);
  - text and data files, XML and a hex view.
- An edit mode for changing game files. It has to be switched on after a warning. The original of every file is backed up before SparkIV first writes to it.
- Experimental: browse the archives of **Red Dead Redemption 2** (PC), read-only, with the file names of [reditec/RAGE-StringsDatabase](https://github.com/reditec/RAGE-StringsDatabase). A one-time setup assistant reads the keys of the game's archives from the running game.
- Experimental: browse the archives (RPF9) and texture pools (`.ttpf`) of **Grand Theft Auto VI** (Xbox Series), read-only. Textures (`.ztd`, `.ttpf`) and audio (`.awc`) open in the same viewers as GTA IV's. The archives' cipher is read from the game's own executable.

## Requirements

- Windows 10 (1809) or later, x64.
- A **legally owned copy** of at least one of the supported games:
  - Grand Theft Auto IV and/or Episodes from Liberty City (PC);
  - Red Dead Redemption 2 (PC);
  - Grand Theft Auto VI (Xbox Series).
- SparkIV reads the game's own files:
  - GTA IV and Episodes from Liberty City: the key the game uses to encrypt its archives is read at run time from your game's executable.
  - Red Dead Redemption 2: the keys are read once from the running game and kept on your PC; its own Oodle library decompresses the files.
  - Grand Theft Auto VI: the archives' cipher tables are read from the game's executable next to them. File names are recovered from the game's own strings; an optional name list adds more.
  - No game data and no key is part of this repository.

## Installing

Download `reditec.SparkIV-win-Setup.exe` from the [releases](https://github.com/reditec/SparkIV/releases) and run it. SparkIV checks for new versions at start and updates itself with one click.

## Building

1. Install the [.NET 10 SDK](https://dotnet.microsoft.com/download). Visual Studio with the Windows App SDK (WinUI) tools is optional.
2. Build the solution:

   ```
   dotnet build SparkIV.sln
   ```

   or open `SparkIV.sln` in Visual Studio and run the `SparkIV` project (x64).

## History and credits

- **aru** (Arushan) wrote SparkIV and RageLib in 2008–2009.
- **Ahmed Walid** ([ahmed605](https://github.com/ahmed605/SparkIV)) continued it.
- **Silent Spud** ([SilentSpud](https://github.com/SilentSpud/SparkIV)) moved it to modern .NET.
- **reditec** has rebuilt it as a WinUI app with new viewers since 2026 and added support for RDR2 and GTA VI.
- **PB07** helped with the Grand Theft Auto VI support, above all with the file names and the decryption.

Third-party components:
- [SharpZipLib](https://github.com/icsharpcode/SharpZipLib) (MIT)
- [Ookii.Dialogs](https://github.com/ookii-dialogs/ookii-dialogs-winforms) (BSD-3-Clause)
- [Windows App SDK](https://github.com/microsoft/WindowsAppSDK) (MIT)
- [Velopack](https://github.com/velopack/velopack) (MIT), installer and updates
- [BCnEncoder.Net](https://github.com/Nominom/BCnEncoder.NET) (MIT or Unlicense), BC texture decoding
- [Concentus](https://github.com/lostromb/concentus) (BSD-3-Clause), Opus audio decoding
- zlib, through .NET's `System.IO.Compression`

## Changelog

What changed in each version: [CHANGELOG.md](CHANGELOG.md). SparkIV also shows it in Settings → About.

## License

SparkIV is free software under the **GNU General Public License, version 3 or (at your option) any later version**. See [`LICENSE`](LICENSE).

The original copyright notices are kept in the source files. This version was **modified by reditec in 2026**; every change and its date is recorded in the Git history of this repository.

## Disclaimer

> [!CAUTION]
> **This tool may only be used for OFFLINE MODDING.**
> We expressly distance ourselves from any use of SparkIV for online modding, that is, for changing or gaining an advantage in the online modes of the games (such as GTA Online or Red Dead Online), for cheating, or for anything else that affects other players or the games' online services.
>
> Rockstar Games tolerates single-player mods. In 2017, after Take-Two had sent a cease-and-desist to the modding tool OpenIV, Rockstar published a clarification on its support site: Take-Two generally will not take legal action against third-party projects for Rockstar's PC games that are single-player, non-commercial and respect the intellectual property rights of others. Rockstar added that this is not a license or an endorsement ([report with the statement, PC Gamer](https://www.pcgamer.com/rockstar-is-talking-with-openiv-developer-issues-new-statement-on-mods); [Rockstar Support, Single-Player mods](https://support.rockstargames.com/articles/5NVOAYjcTomO8v6SX2k76k/pc-single-player-mods)). Earlier, Rockstar had stated that no one has been banned for single-player mods and that its focus is on protecting the online modes ([Rockstar Newswire, "Asked & Answered"](https://www.rockstargames.com/newswire/article/25o2411812k472/asked-answered-the-rockstar-editor-gta-online-updates)). Rockstar's current rules are in its [Mod Guidelines](https://www.rockstargames.com/community-resources/mod-guidelines).

SparkIV is an unofficial fan project. It is not affiliated with, endorsed or sponsored by Rockstar Games or Take-Two Interactive. Grand Theft Auto, GTA and related names are trademarks of Take-Two Interactive Software, Inc.

SparkIV is provided without any warranty. Back up your game before you change its files.
