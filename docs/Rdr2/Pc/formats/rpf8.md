# RPF8 archive (Red Dead Redemption 2, PC)

Status: **reading verified against the game's archives** (header, table of contents, names, stored entries, archives
inside archives). Oodle-compressed entries: implemented, **not yet verified** (the test install's Oodle DLL was still
downloading). Writing: not supported (read-only).
Author: reditec. Specific to RDR2 on PC; unrelated to the RPF2/RPF3 format GTA IV uses.

SparkIV never ships keys or cipher tables. They are read from the user's own running game once and cached on the
user's disk; SparkIV itself contains only fingerprints (length + two hashes) to recognise them.
Sources: librdr3 (MIT), rage-cli (Unlicense), rpf-archive-rs (reference reader, does not decrypt), OpenIV (observed
read-only), and the archives of the game itself (common_0, data_0, textures_0, levels_1, x64\audio, 2026-11-23).

## Header (first 0x110 bytes, not encrypted)
- `0x00` u32 magic `RPF8` (`0x52504638`, bytes `8FPR`).
- `0x04` u32 entry count.
- `0x08` u32 names length (unused: RDR2's archives store no names).
- `0x0C` u16 encryption tag (which key decrypts the table of contents; `0xFF` = not encrypted).
- `0x0E` u16 platform (`'y'` = `0x79` for PC).
- `0x10` 256 bytes RSA signature.
- An archive the Rockstar Games Launcher has not downloaded yet is all zeros (also RDR2.exe and the Oodle DLL); SparkIV
  says so instead of failing.

## Table of contents
- Starts at `0x110`, `count * 24` bytes, a **flat** array: there are no folder entries in the game's archives (extension
  id `0xFE` = directory exists in the format, but none of 2,600 entries checked used it).
- CBC-encrypted with the TFIT2 cipher using the key named by the header tag and the cipher's fixed IV.
- Entry (24 bytes = three u64 `a`,`b`,`c`):
  - name hash = `(uint)a` (low 32 bits): **joaat of the entry's path relative to the archive**, lower case, usually
    without extension (`anim/animscenes/cuts@bou1`, `data/levels/rdr3/heightmap.dat`). Verified: every name of the
    strings database hashes to a hash the game uses in that archive (OpenIV's dumped `.hashes`).
  - encryption config = `(a >> 32) & 0xFF` (which byte ranges of a payload are enciphered); per-entry key tag =
    `(a >> 40) & 0xFF` (`0xFF` = not encrypted); extension id = `(a >> 48) & 0xFF`; resource flag = `a & (1<<56)`;
    trailing 0x100-byte signature = `a & (1<<57)`.
  - disk size = `(b & 0xFFFFFFF) << 4`; offset = `((b >> 28) & 0x7FFFFFFF) << 4`; compressor = `b >> 59`
    (0 stored, 1 deflate, 2 Oodle).
  - size: resource → `(c & 0xFFFFFFF0) + ((c>>32) & 0xFFFFFFF0)`, else `c`.
- Observed: in common_0 every entry is Oodle (1,586/1,586), each with its own key tag; archives inside archives are
  stored (compressor 0) and not encrypted, so they can be read in place.
- Validation: every entry's `offset + diskSize` must stay within the archive (catches a wrong key / corrupt table).

## Names and folders
The archive has no names. SparkIV maps the hashes back with the strings database of
[reditec/RAGE-StringsDatabase](https://github.com/reditec/RAGE-StringsDatabase) (fork of OpenIV-Team's), compiled with
its `build-sparkiv-database` tool (format: that repository's `docs/SparkIV-database-format.md`) and shipped Brotli-
compressed as `src/SparkIV/Data/RDR2.sivs.br`. The folders are made from the paths; names without an extension get the
entry type's. Coverage: 98 % of common_0, 99 % of data_0, all of levels_1; 86.8 % of all archive hashes the database
knows. Unknown entries stay as `<HASH>.<ext>`.

The **scope** of an archive (for the 9 hashes two real names share) is its path relative to the game folder without
`.rpf`, continued by the in-archive path of each nested archive (`data_0/data/ui/value_conversion`).

## TFIT2 cipher
A 16-byte white-box block cipher (table-based), run in CBC mode: init table 4096 u64; 17 round tables 4096 u64 each;
272 mask blocks of 16 u64; 272 u32 xor indices; 128 u64 final masks; 4096-byte end table; 16-byte final xor; 16-byte IV
(`src/RageLib/Rdr2/Pc/Tfit2Cipher.cs`, verified against the librdr3/Swage vector and real archives).
The tables and the IV are in a complete RDR2.exe in plaintext; SparkIV also takes them from the running game's memory
during the setup, so it does not depend on that.

## Keys (solved)
- 165 per-archive keys of 288 bytes (36 u64, TFIT2 round-key schedules), by tag; the last three list positions map to
  tags 0xC0 (master), 0xC5, 0xC6 (0xC6 has no key in the game's set).
- They are not in RDR2.exe in a readable form; the running game holds them decrypted. OpenIV instead embeds them
  AES-256-ECB-encrypted with a key that is in RDR2.exe (night of 2026-11-22/23; not used by SparkIV).
- SparkIV reads them once from the running game (`Rdr2GameMemory`, ReadProcessMemory, offline in story mode: the game
  notices the access and stops with an error) and caches them with the cipher tables
  (`%LOCALAPPDATA%\SparkIV\rdr2keys.cache`, format `RKC3`). Verified: 165/165 keys from the live game, identical by
  SHA-256 to the ones OpenIV uses. The keys are stable across game versions.

## Oodle
Compressor 2. SparkIV calls `OodleLZ_Decompress` of the game's own `oo2core_5_win64.dll` (next to RDR2.exe) through
`NativeLibrary`; Oodle is not shipped. Not yet run against real data (see status).

## SparkIV code
`src/RageLib/Rdr2/Pc/`: `Rpf8Archive.cs` (stream reader, in-place nested archives), `Rpf8FileSystem.cs` (folders from
names), `Rdr2Names.cs` (database reader), `Rdr2Oodle.cs`, `Tfit2Cipher.cs`, `Rdr2Secrets.cs` (fingerprint scan, cache),
`Rdr2GameMemory.cs` (reading the running game), `Rdr2KeyIds.txt`/`Rdr2ContextIds.txt` (fingerprints only).
`src/SparkIV/`: `Services/Rdr2Support.cs`, `Rdr2SetupWindow.xaml(.cs)` (setup assistant), `Services/Volumes.cs`
(RPF8 by header). RDR2 is internally "RDR3" in Rockstar's and OpenIV's names.
