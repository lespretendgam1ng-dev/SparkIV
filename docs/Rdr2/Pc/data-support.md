# Red Dead Redemption 2 (PC): Which game data SparkIV supports

This file applies only to **Red Dead Redemption 2, PC version** (GTA IV: `docs/GtaIV/Pc/data-support.md`).
Status words as there: **used**, **ignored**, **unclear**, **game logic only**; plus **not yet** for data SparkIV does
not read at all yet.

## Archives

| Data | Status | Notes / source |
|---|---|---|
| RPF8 header, table of contents | used | [formats/rpf8.md](formats/rpf8.md); verified on the game's archives |
| Entry name hashes | used | resolved with the strings database (86.8 % of known hashes named) |
| Stored and deflate entries | used | verified (stored); deflate not seen in the game's archives so far |
| Oodle entries (compressor 2) | used | the game's oo2core DLL; implemented, not yet verified |
| Resource entries (RSC8 header rebuilt) | used | header rebuilt from `c`; needs Oodle verification |
| Per-entry signature (`a` bit 57), names length | ignored | not needed for reading |
| Folder entries (`0xFE`) | unclear | in the format, not seen in the game's archives |
| Writing archives | not yet | RDR2 archives are read-only in SparkIV |

## File formats inside the archives

| Format | Status | Notes |
|---|---|---|
| `.meta`, `.xml` | used | text, XML viewer (once Oodle is verified) |
| `.rpf` (nested) | used | opened in place |
| `.ytd`, `.ydr`, `.yft`, `.ydd`, `.ymap`, `.ytyp`, `.ycd`, `.awc`, `.ymt`, `.ysc`, ... | not yet | listed with type and icon; open in the hex view only |
