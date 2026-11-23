# RPF8 archive (Red Dead Redemption 2, PC)

Status: **decryption understood and verified; per-archive key derivation still open.**
Author: reditec. Specific to RDR2 on PC; unrelated to the RPF2/RPF3 format GTA IV uses.

SparkIV never ships keys or cipher tables. Everything here is either read from the user's own `RDR2.exe` at
runtime or kept as a validation hash. Source: librdr3 (MIT), rage-cli (Unlicense), and verification against a
running OpenIV instance (read-only). Nothing game-derived is committed.

## Header (first 0x110 bytes, not encrypted)
- `0x00` u32 magic `RPF8` (`0x52504638`).
- `0x04` u32 entry count.
- `0x0C` u16 encryption tag (which key decrypts the TOC; `0xFF` = not encrypted).
- `0x0E` u16 platform (`'y'` = `0x79` for PC).

## Table of contents
- Starts at `0x110`, is `count * 24` bytes, a flat array of 24-byte entries.
- CBC-encrypted with the TFIT2 cipher (below) using the key named by the header tag and the cipher's fixed IV.
- Entry (24 bytes = three u64 `a`,`b`,`c`):
  - name hash = `(uint)a` (low 32 bits), displayed as `<HEX>.<ext>`; the hash is **joaat** (RAGE
    one-at-a-time, lowercase) of the original name. OpenIV reverses it via its own name list; SparkIV shows the hash.
  - extension id = `(a >> 48) & 0xFF` (`0xFE` = directory).
  - resource flag = `a & (1<<56)`; per-entry key tag = `(a >> 40) & 0xFF` (`0xFF` = not encrypted).
  - disk size = `(b & 0xFFFFFFF) << 4`; offset = `((b >> 28) & 0x7FFFFFFF) << 4`; compressor = `b >> 59`
    (0 stored, 1 deflate, 2 Oodle).
  - size: resource → `(c & 0xFFFFFFF0) + ((c>>32) & 0xFFFFFFF0)`, else `c`.
- Validation: every entry's `offset + diskSize` must stay within the archive (catches a wrong key / corrupt TOC).

## TFIT2 cipher (TOC and some payloads)
A 16-byte block cipher ("white-box", table-based), run in CBC mode. VERIFIED: with the cipher tables and IV read
from `RDR2.exe` and the correct 288-byte key, SparkIV's `Tfit2Cipher` decrypts a real TOC exactly (confirmed
against a running OpenIV, 14/14 entries of `update_3.rpf`).

The cipher **tables and the IV are present in `RDR2.exe` in plaintext** and are located by content fingerprint
(librdr3 identifiers = length + two hashes; only hashes, never values, live in SparkIV):
- init table 4096 u64; 17 round tables 4096 u64 each; 272 mask blocks (17x16) of 16 u64; 272 u32 xor indices;
  128 u64 final masks; 4096-byte end table; 16-byte final xor; 16-byte IV.
- Block/round math: see `src/RageLib/Rdr2/Pc/Tfit2Cipher.cs` (verified against the librdr3/Swage reference vector).

## Keys (OPEN: derivation not yet reproduced)
- The per-archive keys are 288-byte TFIT2 round-key schedules (36 u64). There are 166 (indices 0..165; the last
  three map to tags 0xC0/0xC5/0xC6).
- **Only one key (index 163 = tag 0xC0, the "master") is present in `RDR2.exe` in plaintext.** The other 165 are
  **not** in `RDR2.exe` and **not** in `OpenIV.exe`; OpenIV computes them at runtime by an as-yet-unreproduced
  derivation. Established by capturing the real keys from a running OpenIV (read-only) and confirming:
  - all 165 share the master's first 32 bytes exactly, then a per-key 256-byte remainder;
  - the remainder is cryptographically random per key (no XOR/chain/exe-slice relationship found);
  - OpenIV's 165 embedded 32-byte constants are exactly `SHA-256(key)` of the real keys (verification set, 165/165).
- So SparkIV can verify any candidate key via SHA-256 against those constants, but cannot yet generate them. Keys
  are stable across game versions (OpenIV 4.1 from 2022 opens the 2024 build), so once the derivation is reproduced
  it is permanent. This is the one remaining piece for exe-only RDR2 support.

## SparkIV code
`src/RageLib/Rdr2/Pc/`: `Tfit2Cipher.cs` (cipher), `Rpf8Archive.cs` (header/TOC/entries, extract for
stored+deflate; Oodle TODO), `Rdr2Secrets.cs` (fingerprint scan of the exe for tables/IV/keys),
`Rdr2KeyIds.txt`/`Rdr2ContextIds.txt` (fingerprint hashes only). RDR2 is internally "RDR3" in Rockstar/OpenIV.
