/**********************************************************************\

 RageLib - SparkIV
 GTA VI (PC port target, Xbox Series "Scarlett" build) RPF9 table cipher.

 The cipher algorithm and the multi-span TOC layout were recovered by reverse
 engineering (the game's own decrypt loader). The tables, the 8 crypto contexts
 and the header IV are NEVER shipped by SparkIV; they are read from the user's
 own game executable at runtime (see Rpf9Secrets). A user must own the game.

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
using System.Buffers.Binary;
using System.Collections.Generic;

namespace RageLib.GtaVI.Pc
{
  /// <summary>
  /// The custom table-based block cipher GTA VI uses to encrypt the table of
  /// contents of its RPF9 archives, together with the multi-span CBC layout the
  /// game's loader applies. The 16-byte block cipher is AES-like (round 1 byte
  /// substitution, 13 middle T-table rounds, final byte substitution). A TOC is
  /// decrypted in spans: a fixed header + a stage-1 descriptor with one context,
  /// then the data spans with a second context and an IV read from the
  /// descriptor. All tables, the 8 contexts and the header IV are read from the
  /// user's own executable (see <see cref="Rpf9Secrets"/>) and never stored.
  /// </summary>
  public sealed class Rpf9Cipher
  {
    /// <summary>One crypto context: round keys, final S-boxes and a seed IV.</summary>
    public sealed class Context
    {
      public uint[] RoundKeys = new uint[56];   // 14 rounds x 4 LE u32 (0x00..0xE0)
      public byte[] SBoxes = new byte[0x1000];  // 16 lanes x 256 (0xF0..0x10F0)
      public byte[] Iv = new byte[16];          // 0x10F0..0x1100 (per-context seed)
    }

    /// <summary>Round-1 byte substitution tables: 16 lanes x 256 bytes (shared).</summary>
    public byte[] ByteTables = Array.Empty<byte>();

    /// <summary>Middle-round T-tables: 16 tables of 256 u32, executable order (shared).</summary>
    public uint[][] T = Array.Empty<uint[]>();

    /// <summary>The 8 crypto contexts (static array in the executable).</summary>
    public Context[] Contexts = Array.Empty<Context>();

    /// <summary>The global header-span IV (static in the executable).</summary>
    public byte[] HeaderIv = new byte[16];

    // ── member payload cipher ──────────────────────────────────────────────
    // Encrypted members use the same 14-round table cipher as the TOC, but with
    // a SEPARATE table set and per-key material, applied in CTR mode over only
    // the encrypted runs of the member. All data is static in the executable.

    /// <summary>A member-payload key: round keys and the final S-box, from the
    /// key blob a descriptor points to.</summary>
    public sealed class PayloadKey
    {
      public uint Kind;
      public uint[] Words = Array.Empty<uint>();   // round keys (big-endian words)
      public byte[] Output = Array.Empty<byte>();  // 16 x 256 final S-box
    }

    /// <summary>Payload round-1 byte tables (16 x 256) and middle-round T-tables
    /// (16 x 256 u32), in natural order; the round/output permutations differ
    /// from the TOC cipher.</summary>
    public byte[] PayloadInput = Array.Empty<byte>();
    public uint[] PayloadMixed = Array.Empty<uint>();
    public static readonly int[] PayloadRoundOrder = { 0, 5, 10, 15, 3, 4, 9, 14, 2, 7, 8, 13, 1, 6, 11, 12 };
    public static readonly int[] PayloadOutputOrder = { 0, 5, 10, 15, 4, 9, 14, 3, 8, 13, 2, 7, 12, 1, 6, 11 };

    /// <summary>Payload keys by id (the encryption descriptor's first dword).</summary>
    public Dictionary<uint, PayloadKey> PayloadKeys = new Dictionary<uint, PayloadKey>();

    /// <summary>Whether the member-payload cipher data loaded and validated.</summary>
    public bool PayloadReady => PayloadInput.Length == 0x1000 && PayloadMixed.Length == 0x1000 && PayloadKeys.Count > 0;

    /// <summary>One payload block transform (big-endian word arithmetic).</summary>
    private void PayloadTransform(ReadOnlySpan<byte> input, Span<byte> output, PayloadKey key)
    {
      Span<byte> state = stackalloc byte[16];
      Span<uint> mixed = stackalloc uint[16];
      for (int i = 0; i < 16; i++)
        state[i] = PayloadInput[i * 256 + input[i]];
      for (int g = 0; g < 4; g++)
      {
        uint w = BinaryPrimitives.ReadUInt32BigEndian(state.Slice(g * 4, 4)) ^ key.Words[g];
        BinaryPrimitives.WriteUInt32BigEndian(state.Slice(g * 4, 4), w);
      }
      for (int round = 1; round <= 13; round++)
      {
        for (int i = 0; i < 16; i++)
          mixed[i] = PayloadMixed[i * 256 + state[i]];
        for (int g = 0; g < 4; g++)
        {
          int j = g * 4;
          uint w = mixed[PayloadRoundOrder[j]] ^ mixed[PayloadRoundOrder[j + 1]] ^
                   mixed[PayloadRoundOrder[j + 2]] ^ mixed[PayloadRoundOrder[j + 3]] ^
                   key.Words[round * 4 + g];
          BinaryPrimitives.WriteUInt32BigEndian(state.Slice(j, 4), w);
        }
      }
      for (int i = 0; i < 16; i++)
        output[i] = key.Output[i * 256 + state[PayloadOutputOrder[i]]];
    }

    /// <summary>CTR keystream over <paramref name="bytes"/> starting at member
    /// offset <paramref name="start"/>: counter = big-endian IV + block index.</summary>
    private void PayloadKeystream(Span<byte> bytes, int start, ReadOnlySpan<byte> iv, PayloadKey key)
    {
      Span<byte> counter = stackalloc byte[16];
      Span<byte> mask = stackalloc byte[16];
      UInt128 initial = BinaryPrimitives.ReadUInt128BigEndian(iv);
      for (int pos = 0; pos < bytes.Length;)
      {
        int offset = start + pos;
        int inBlock = offset & 0xF;
        BinaryPrimitives.WriteUInt128BigEndian(counter, initial + (uint)(offset / 16));
        PayloadTransform(counter, mask, key);
        int count = Math.Min(16 - inBlock, bytes.Length - pos);
        for (int i = 0; i < count; i++)
          bytes[pos + i] ^= mask[inBlock + i];
        pos += count;
      }
    }

    /// <summary>
    /// Decrypts an encrypted member in place. <paramref name="descriptor"/> is the
    /// 0x40-byte encryption record (id, mode 17, 16-byte IV, then prefix/encrypted/
    /// clear/suffix/flags): a clear prefix, repeating encrypted/clear runs, clear
    /// suffix. Returns false if the member is not a supported encrypted member.
    /// </summary>
    public bool DecryptPayload(Span<byte> bytes, long decodedSize, ReadOnlySpan<byte> descriptor)
    {
      if (!PayloadReady || descriptor.Length < 0x40)
        return false;
      uint id = BinaryPrimitives.ReadUInt32LittleEndian(descriptor);
      int mode = BinaryPrimitives.ReadUInt16LittleEndian(descriptor.Slice(4, 2));
      if (mode != 17 || !PayloadKeys.TryGetValue(id, out var key) || key.Kind != 0x2718ce5d)
        return false;

      ReadOnlySpan<byte> iv = descriptor.Slice(6, 16);
      ulong prefix = BinaryPrimitives.ReadUInt64LittleEndian(descriptor.Slice(24, 8));
      ulong encrypted = BinaryPrimitives.ReadUInt64LittleEndian(descriptor.Slice(32, 8));
      ulong clear = BinaryPrimitives.ReadUInt64LittleEndian(descriptor.Slice(40, 8));
      ulong suffix = BinaryPrimitives.ReadUInt64LittleEndian(descriptor.Slice(48, 8));
      ulong flags = BinaryPrimitives.ReadUInt64LittleEndian(descriptor.Slice(56, 8));
      ulong length = (flags & 4) != 0 ? (ulong)decodedSize : (ulong)bytes.Length;
      int total = (int)Math.Min(length, (ulong)bytes.Length);
      if (total <= 0) return true;
      int first = (int)Math.Min(prefix, (ulong)total);
      int last = (int)Math.Min((ulong)total, Math.Max(prefix, length - Math.Min(suffix, length)));

      PayloadKeystream(bytes.Slice(0, first), 0, iv, key);
      for (int pos = first; encrypted > 0 && pos < last;)
      {
        int run = (int)Math.Min(encrypted, (ulong)(last - pos));
        PayloadKeystream(bytes.Slice(pos, run), pos, iv, key);
        pos += run;
        pos += (int)Math.Min(clear, (ulong)(last - pos));
      }
      PayloadKeystream(bytes.Slice(last, total - last), last, iv, key);
      return true;
    }

    /// <summary>Diagnostics: the data context index chosen by the last DecodeToc.</summary>
    public int LastDataContext { get; private set; }

    private const int MiddleRounds = 13;

    // Span layout: a 0x20 header block then a 0x140 stage-1 descriptor, both
    // under the index context; the data spans follow.
    private const int HeaderStage1End = 0x160;
    private const int IndexContext = 6;      // context[6] decrypts header + stage-1
    private const int DataIvOffset = 0x26;   // the data IV sits here in stage-1
    private const int DataSpanStart = 0x160;

    /// <summary>Transforms one 16-byte block with the given context (no chaining).</summary>
    public byte[] DecryptBlock(ReadOnlySpan<byte> ct, Context c)
    {
      uint[] rk = c.RoundKeys;
      byte[] sboxes = c.SBoxes;

      // Round 1: byte substitution + add round key.
      Span<uint> w = stackalloc uint[4];
      for (int j = 0; j < 4; j++)
      {
        uint v = ((uint)ByteTables[(j * 4 + 0) * 256 + ct[j * 4 + 0]] << 24)
               ^ ((uint)ByteTables[(j * 4 + 1) * 256 + ct[j * 4 + 1]] << 16)
               ^ ((uint)ByteTables[(j * 4 + 2) * 256 + ct[j * 4 + 2]] << 8)
               ^  (uint)ByteTables[(j * 4 + 3) * 256 + ct[j * 4 + 3]];
        w[j] = v ^ rk[j];
      }

      // Middle rounds: AES-style T-table round with a fixed byte permutation.
      for (int r = 1; r <= MiddleRounds; r++)
      {
        uint w0 = w[0], w1 = w[1], w2 = w[2], w3 = w[3];
        w[0] = T[10][(w2 >> 8) & 0xFF] ^ T[13][(w3 >> 16) & 0xFF] ^ T[0][(w0 >> 24) & 0xFF] ^ T[7][w1 & 0xFF] ^ rk[r * 4 + 0];
        w[1] = T[1][(w0 >> 16) & 0xFF] ^ T[14][(w3 >> 8) & 0xFF] ^ T[4][(w1 >> 24) & 0xFF] ^ T[11][w2 & 0xFF] ^ rk[r * 4 + 1];
        w[2] = T[5][(w1 >> 16) & 0xFF] ^ T[2][(w0 >> 8) & 0xFF] ^ T[8][(w2 >> 24) & 0xFF] ^ T[15][w3 & 0xFF] ^ rk[r * 4 + 2];
        w[3] = T[9][(w2 >> 16) & 0xFF] ^ T[6][(w1 >> 8) & 0xFF] ^ T[12][(w3 >> 24) & 0xFF] ^ T[3][w0 & 0xFF] ^ rk[r * 4 + 3];
      }

      // Final round: 16 S-box lookups, bytes taken in inverse-ShiftRows order.
      uint f0 = w[0], f1 = w[1], f2 = w[2], f3 = w[3];
      ReadOnlySpan<int> sel = stackalloc int[16]
      {
        (int)((f0 >> 24) & 0xFF), (int)((f3 >> 16) & 0xFF), (int)((f2 >> 8) & 0xFF), (int)(f1 & 0xFF),
        (int)((f1 >> 24) & 0xFF), (int)((f0 >> 16) & 0xFF), (int)((f3 >> 8) & 0xFF), (int)(f2 & 0xFF),
        (int)((f2 >> 24) & 0xFF), (int)((f1 >> 16) & 0xFF), (int)((f0 >> 8) & 0xFF), (int)(f3 & 0xFF),
        (int)((f3 >> 24) & 0xFF), (int)((f2 >> 16) & 0xFF), (int)((f1 >> 8) & 0xFF), (int)(f0 & 0xFF),
      };
      var output = new byte[16];
      for (int i = 0; i < 16; i++)
        output[i] = sboxes[i * 256 + sel[i]];
      return output;
    }

    /// <summary>Decrypts a single CBC span in place with the given context + IV.</summary>
    public void DecryptCbc(Span<byte> buf, Context c, ReadOnlySpan<byte> iv)
    {
      Span<byte> prev = stackalloc byte[16];
      iv.CopyTo(prev);
      Span<byte> ctBlock = stackalloc byte[16];
      int blocks = buf.Length / 16;
      for (int b = 0; b < blocks; b++)
      {
        var block = buf.Slice(b * 16, 16);
        block.CopyTo(ctBlock);
        var pt = DecryptBlock(block, c);
        for (int i = 0; i < 16; i++)
          block[i] = (byte)(pt[i] ^ prev[i]);
        ctBlock.CopyTo(prev);
      }
    }

    /// <summary>
    /// Decrypts a whole RPF9 table of contents in place. <paramref name="header64"/>
    /// is the archive's 64-byte unencrypted header; <paramref name="toc"/> is the
    /// encrypted TOC (the bytes from file offset 0x40). The result is the decoded
    /// header + stage-1 descriptor + data records.
    /// </summary>
    public void DecodeToc(ReadOnlySpan<byte> header64, Span<byte> toc)
    {
      // Header + stage-1 descriptor: context[6], the global header IV.
      int hs1 = Math.Min(HeaderStage1End, toc.Length);
      DecryptCbc(toc[..hs1], Contexts[IndexContext], HeaderIv);
      if (toc.Length <= DataSpanStart)
        return;

      // The data IV is embedded in the decoded stage-1 descriptor.
      // It is 6 bytes into the key descriptor that follows the section list.
      Span<byte> dataIv = stackalloc byte[16];
      int ivAt = Rpf9Toc.FindDataIv(toc[..hs1]);
      if (ivAt + 16 > hs1) ivAt = DataIvOffset;
      toc.Slice(ivAt, 16).CopyTo(dataIv);

      // The last data span starts at the file offset in unenc header +0x08.
      int boundary = (int)BinaryPrimitives.ReadUInt32LittleEndian(header64.Slice(8, 4)) - 0x40;

      // The data context is one of the 8. Prefer the one that turns the table
      // directory (located via the decoded section list) into a valid table
      // descriptor; fall back to the most-structured probe.
      int sel = SelectByDirectory(toc, toc[..hs1]);
      if (sel < 0)
        sel = SelectDataContext(toc, dataIv);
      LastDataContext = sel;
      Context data = Contexts[sel];

      if (boundary > DataSpanStart && boundary < toc.Length)
      {
        DecryptCbc(toc[DataSpanStart..boundary], data, dataIv);
        DecryptCbc(toc[boundary..], data, dataIv);
      }
      else
      {
        DecryptCbc(toc[DataSpanStart..], data, dataIv);
      }
    }

    /// <summary>
    /// Picks the data context: decrypts the first data chunk with each of the 8
    /// contexts and keeps the one whose output is the most structured (the most
    /// zero bytes). Wrong contexts yield high-entropy noise; the right one yields
    /// the record tables. Verified to select correctly across every GTA VI archive.
    /// </summary>
    /// <summary>
    /// Picks the data context whose decryption puts the slot table's descriptor
    /// near the sections named in the decoded header. CBC lets each window be
    /// decrypted in isolation (the IV is the preceding ciphertext block); a span
    /// boundary inside the window only garbles one block.
    /// </summary>
    private int SelectByDirectory(ReadOnlySpan<byte> toc, ReadOnlySpan<byte> header)
    {
      var sections = Rpf9Toc.Sections(header);
      long lo = long.MaxValue, hi = 0;
      foreach (var (_, target) in sections)
      {
        if (target <= DataSpanStart || target >= toc.Length) continue;
        lo = Math.Min(lo, target);
        hi = Math.Max(hi, target);
      }
      if (hi == 0)
      {
        lo = DataSpanStart + 0x10;
        hi = DataSpanStart + 0x10;
      }

      // The directory follows the last section's structure; allow a few KB.
      int first = (int)Math.Max(DataSpanStart + 16, (hi - 0x40) & ~0xFL);
      int last = (int)Math.Min(toc.Length, ((hi + 0x2000) + 15) & ~0xFL) & ~0xF;
      if (last - first < 0x30)
        return -1;

      var buf = new byte[last - first];
      int found = -1;
      for (int c = 0; c < Contexts.Length; c++)
      {
        toc[first..last].CopyTo(buf);
        DecryptCbc(buf, Contexts[c], toc.Slice(first - 16, 16));
        if (Rpf9Toc.FindSlotDescriptor(buf, 0, buf.Length) >= 0)
        {
          if (found >= 0) return -1; // ambiguous
          found = c;
        }
      }
      return found;
    }

    private int SelectDataContext(ReadOnlySpan<byte> toc, ReadOnlySpan<byte> dataIv)
    {
      int probeLen = Math.Min(0x200, toc.Length - DataSpanStart) & ~0xF;
      if (probeLen <= 0)
        return IndexContext;
      var probe = new byte[probeLen];
      int best = IndexContext, bestZeros = -1;
      for (int c = 0; c < Contexts.Length; c++)
      {
        toc.Slice(DataSpanStart, probeLen).CopyTo(probe);
        DecryptCbc(probe, Contexts[c], dataIv);
        int zeros = 0;
        foreach (byte b in probe)
          if (b == 0) zeros++;
        if (zeros > bestZeros) { bestZeros = zeros; best = c; }
      }
      return best;
    }
  }
}
