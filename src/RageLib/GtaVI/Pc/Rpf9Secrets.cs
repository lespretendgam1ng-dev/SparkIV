/**********************************************************************\

 RageLib - SparkIV
 GTA VI RPF9 cipher data loader.

 Reads the RPF9 block-cipher lookup tables, the 8 crypto contexts and the header
 IV from the user's OWN game executable at runtime. SparkIV embeds NO table or
 key bytes: only fingerprints (region length, a cheap prefilter sum and a
 SHA-256) used to locate and validate the regions inside the executable the user
 owns. This mirrors KeyUtilGTAIV, which ships a key hash and scans the exe for it.

 This program is free software: you can redistribute it and/or modify
 it under the terms of the GNU General Public License as published by
 the Free Software Foundation, either version 3 of the License, or
 (at your option) any later version.

 This program is distributed in the hope that it will be useful,
 but WITHOUT ANY WARRANTY; without even the implied warranty of
 MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 GNU General Public License for more details.

\**********************************************************************/

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace RageLib.GtaVI.Pc
{
  /// <summary>
  /// Locates and validates the RPF9 cipher data inside the user's game
  /// executable and builds a ready <see cref="Rpf9Cipher"/>. Nothing secret is
  /// stored in SparkIV; the executable is the sole source of the tables, the 8
  /// contexts and the header IV. The user must own the game.
  /// </summary>
  public sealed class Rpf9Secrets
  {
    // --- Fingerprints of the static regions (game_scarlett_master.exe) ---
    // Only hashes + lengths live here; no table, key or IV bytes are embedded, and
    // nothing computed with them (no test vectors). A new game build needs new
    // fingerprints: the SHA-256 of each region in that build's executable.

    // Tables block: 16 round-1 byte tables (0x1000) + 16 middle-round T-tables (0x4000).
    private const long TablesOffsetHint = 0x6035D30;
    private const int TablesLength = 0x5000;
    private const uint TablesPrefilter = 0x00001FE0;
    private const string TablesSha256 = "39dff6172873bac1b93b0aa26d2e31146404f3f40c2261492c9c45b313f1e38e";

    // Context array: 8 contexts, each 0x1100 (round keys 0x00..0xE0, S-boxes
    // 0xF0..0x10F0, per-context IV 0x10F0..0x1100). context[6] is the index context.
    private const long ContextsOffsetHint = 0x66E6B64;
    private const int ContextStride = 0x1100;
    private const int ContextCount = 8;
    private const int ContextsLength = ContextStride * ContextCount; // 0x8800
    private const uint ContextsPrefilter = 0x00001BDF;
    private const string ContextsSha256 = "1fbfd828c039ebce4ab372b3dea13943b6933cbb03cb8cf3a2ca844ac26e4968";

    // The global header-span IV (16 bytes).
    private const long HeaderIvOffsetHint = 0x62FC270;
    private const int HeaderIvLength = 0x10;
    private const uint HeaderIvPrefilter = 0x000008AD;
    private const string HeaderIvSha256 = "70ad3893679946abe55fa553f19856c1f81401bee02380885084a418c64ff2e3";

    private const int PrefilterLen = 64;

    /// <summary>The GTA VI executable the data is read from.</summary>
    public string ExecutableName => "game_scarlett_master.exe";

    /// <summary>
    /// Reads the cipher tables, the 8 contexts and the header IV from the given
    /// game executable and returns a ready cipher. Throws if a region cannot be
    /// located or fails validation (e.g. an unsupported build).
    /// </summary>
    public Rpf9Cipher Load(string executablePath)
    {
      if (string.IsNullOrEmpty(executablePath) || !File.Exists(executablePath))
        throw new FileNotFoundException("GTA VI executable not found.", executablePath);

      byte[] exe = File.ReadAllBytes(executablePath);
      byte[] tables = Locate(exe, TablesOffsetHint, TablesLength, TablesPrefilter, TablesSha256, "tables");
      byte[] contexts = Locate(exe, ContextsOffsetHint, ContextsLength, ContextsPrefilter, ContextsSha256, "contexts");
      byte[] headerIv = Locate(exe, HeaderIvOffsetHint, HeaderIvLength, HeaderIvPrefilter, HeaderIvSha256, "header IV");

      Rpf9Cipher cipher = BuildCipher(tables, contexts, headerIv);
      Validate(cipher);
      LoadPayload(exe, cipher); // member-payload cipher data (optional; same exe)
      return cipher;
    }

    // --- Member-payload cipher data ---
    // The encrypted members use the same table cipher as the TOC with a separate
    // table set (payload Input/Mixed) and per-key material reached through a small
    // descriptor array. All of it is static .rdata in the user's executable; we
    // read it by RVA. On any mismatch the payload path stays off and members keep
    // their "encrypted" label (nothing is embedded in SparkIV).
    private const int PayloadInputRva = 0x6040f30;   // 16 x 256 byte tables
    private const int PayloadMixedRva = 0x6041f30;   // 16 x 256 u32 T-tables
    private const int KeyDescriptorRva = 0x66e5900;  // 24 x 24-byte descriptors
    private const int KeyBlobSize = 0x10f4;
    private const uint PayloadKeyKind = 0x2718ce5d;

    private static void LoadPayload(byte[] exe, Rpf9Cipher cipher)
    {
      try
      {
        int pe = BitConverter.ToInt32(exe, 0x3C);
        if (BitConverter.ToUInt32(exe, pe) != 0x00004550) return; // "PE\0\0"
        int numSec = BitConverter.ToUInt16(exe, pe + 6);
        int optSize = BitConverter.ToUInt16(exe, pe + 20);
        ulong imageBase = BitConverter.ToUInt64(exe, pe + 24 + 24); // PE32+ ImageBase
        int sh = pe + 24 + optSize;
        var secs = new (long va, long end, long raw)[numSec];
        for (int i = 0; i < numSec; i++)
        {
          int o = sh + i * 40;
          long va = BitConverter.ToUInt32(exe, o + 12);
          long vsize = BitConverter.ToUInt32(exe, o + 8);
          long rsize = BitConverter.ToUInt32(exe, o + 16);
          secs[i] = (va, va + Math.Max(vsize, rsize), BitConverter.ToUInt32(exe, o + 20));
        }

        byte[] Read(long rva, int size)
        {
          foreach (var s in secs)
            if (rva >= s.va && rva < s.end)
            {
              long raw = rva - s.va + s.raw;
              if (raw < 0 || raw + size > exe.Length) return null;
              var b = new byte[size];
              Array.Copy(exe, raw, b, 0, size);
              return b;
            }
          return null;
        }

        byte[] input = Read(PayloadInputRva, 0x1000);
        byte[] mixedBytes = Read(PayloadMixedRva, 0x1000 * 4);
        if (input == null || mixedBytes == null) return;

        // The round-1 lane 0 must be a byte permutation (cheap sanity check).
        var seen = new bool[256];
        for (int i = 0; i < 256; i++) seen[input[i]] = true;
        for (int v = 0; v < 256; v++) if (!seen[v]) return;

        var mixed = new uint[0x1000];
        for (int i = 0; i < 0x1000; i++) mixed[i] = BitConverter.ToUInt32(mixedBytes, i * 4);

        var keys = new Dictionary<uint, Rpf9Cipher.PayloadKey>();
        byte[] desc = Read(KeyDescriptorRva, 24 * 24);
        if (desc != null)
          for (int o = 0; o + 24 <= desc.Length; o += 24)
          {
            if (BitConverter.ToUInt32(desc, o) != 0x02000000) continue;         // descriptor kind
            if (BitConverter.ToUInt64(desc, o + 16) != KeyBlobSize) continue;   // blob size
            uint id = BitConverter.ToUInt32(desc, o + 4);
            ulong addr = BitConverter.ToUInt64(desc, o + 8);
            byte[] blob = Read((long)(addr - imageBase), KeyBlobSize);
            if (blob == null) continue;
            var words = new uint[60];
            for (int i = 0; i < 60; i++) words[i] = BitConverter.ToUInt32(blob, 4 + i * 4);
            var output = new byte[0x1000];
            Array.Copy(blob, 244, output, 0, 0x1000);
            keys[id] = new Rpf9Cipher.PayloadKey
            {
              Kind = BitConverter.ToUInt32(blob, 0),
              Words = words,
              Output = output,
            };
          }

        bool anyPayload = false;
        foreach (var k in keys.Values) if (k.Kind == PayloadKeyKind) { anyPayload = true; break; }
        if (!anyPayload) return;

        cipher.PayloadInput = input;
        cipher.PayloadMixed = mixed;
        cipher.PayloadKeys = keys;
      }
      catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or OverflowException)
      {
        // Unsupported build layout: payload decryption stays unavailable.
      }
    }

    private static Rpf9Cipher BuildCipher(byte[] tables, byte[] contexts, byte[] headerIv)
    {
      var c = new Rpf9Cipher();

      // Round-1 byte tables: first 0x1000 bytes (16 lanes x 256), used flat.
      c.ByteTables = new byte[0x1000];
      Array.Copy(tables, 0, c.ByteTables, 0, 0x1000);

      // Middle-round T-tables: next 0x4000 bytes = 16 tables of 256 LE u32.
      c.T = new uint[16][];
      for (int k = 0; k < 16; k++)
      {
        var t = new uint[256];
        int baseOff = 0x1000 + k * 0x400;
        for (int i = 0; i < 256; i++)
          t[i] = BitConverter.ToUInt32(tables, baseOff + i * 4);
        c.T[k] = t;
      }

      // The 8 contexts, each 0x1100 bytes.
      c.Contexts = new Rpf9Cipher.Context[ContextCount];
      for (int n = 0; n < ContextCount; n++)
      {
        int off = n * ContextStride;
        var ctx = new Rpf9Cipher.Context();
        for (int i = 0; i < 56; i++)
          ctx.RoundKeys[i] = BitConverter.ToUInt32(contexts, off + i * 4);
        Array.Copy(contexts, off + 0xF0, ctx.SBoxes, 0, 0x1000);
        Array.Copy(contexts, off + 0x10F0, ctx.Iv, 0, 16);
        c.Contexts[n] = ctx;
      }

      c.HeaderIv = new byte[16];
      Array.Copy(headerIv, 0, c.HeaderIv, 0, 16);
      return c;
    }

    private static void Validate(Rpf9Cipher c)
    {
      // Each of the 16 S-boxes of every context must be a permutation of 0..255.
      var seen = new bool[256];
      for (int n = 0; n < c.Contexts.Length; n++)
      {
        byte[] sb = c.Contexts[n].SBoxes;
        for (int k = 0; k < 16; k++)
        {
          Array.Clear(seen);
          for (int i = 0; i < 256; i++)
            seen[sb[k * 256 + i]] = true;
          for (int v = 0; v < 256; v++)
            if (!seen[v])
              throw new InvalidDataException(
                $"RPF9 context {n} S-box {k} is not a permutation — wrong data located in the executable.");
        }
      }
      // No known-answer test: its expected outputs would be data derived from the
      // executable's secrets. The SHA-256 fingerprints in Locate prove the regions,
      // and opening an archive proves the cipher (its TOC must decode).
    }

    /// <summary>
    /// Returns the bytes of one region, located inside <paramref name="exe"/>.
    /// Tries the known build offset first, then scans the whole file (prefilter
    /// sum, verified by SHA-256) so a relocated/updated build still validates.
    /// </summary>
    private static byte[] Locate(byte[] exe, long hint, int length, uint prefilter, string sha256Hex, string what)
    {
      if (hint >= 0 && hint + length <= exe.Length
          && HashHex(exe.AsSpan((int)hint, length)) == sha256Hex)
        return exe.AsSpan((int)hint, length).ToArray();

      if (exe.Length >= length)
      {
        int pfLen = Math.Min(PrefilterLen, length);
        long limit = exe.Length - length;
        uint sum = 0;
        for (int i = 0; i < pfLen; i++)
          sum += exe[i];

        for (long off = 0; off <= limit; off++)
        {
          if (sum == prefilter && HashHex(exe.AsSpan((int)off, length)) == sha256Hex)
            return exe.AsSpan((int)off, length).ToArray();

          sum -= exe[off];
          long next = off + pfLen;
          if (next < exe.Length)
            sum += exe[next];
        }
      }

      throw new InvalidDataException(
        $"Could not locate the RPF9 {what} region in the game executable — this build may be unsupported.");
    }

    private static string HashHex(ReadOnlySpan<byte> data) =>
      Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
  }
}
