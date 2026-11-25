/**********************************************************************\

 RageLib - SparkIV
 RDR2 (PC) TFIT2 table-based block cipher.

 Cipher algorithm reference: librdr3 (MIT) / rage-cli (Unlicense). The tables
 themselves are never shipped; they are read from the user's own RDR2.exe at
 runtime (see Rdr2Secrets).

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

namespace RageLib.Rdr2.Pc
{
  /// <summary>
  /// The table-based ("white-box") block cipher RDR2 (PC) uses to encrypt the table of contents and some
  /// payloads of its RPF8 archives: a 16-byte block cipher run in CBC mode. This class holds the tables for a
  /// session and does the raw per-block transform; the tables are read from the user's own executable by
  /// <see cref="Rdr2Secrets"/> and never stored by SparkIV. This is specific to RDR2 on PC.
  /// </summary>
  public sealed class Tfit2Cipher
  {
    // Which 8-byte half each of the 16 lanes reads from in the middle rounds. The first two rounds and the
    // last round read by lane position (i / 8); the rounds in between follow this table.
    private static readonly int[] Mix = { 0, 1, 1, 0, 0, 0, 1, 1, 1, 0, 0, 1, 1, 1, 0, 0 };

    public ulong[] Init = Array.Empty<ulong>();        // 4096 u64 (16 lanes x 256)
    public ulong[][] Lookups = Array.Empty<ulong[]>(); // 17 x 4096 u64
    public ulong[][] Masks = Array.Empty<ulong[]>();   // 272 x 16 u64 (17 rounds x 16 lanes)
    public int[] Xors = Array.Empty<int>();            // 272
    public ulong[] FinalMasks = Array.Empty<ulong>();  // 128 u64 (16 lanes x 8)
    public byte[] EndTable = Array.Empty<byte>();      // 4096 (16 lanes x 256)
    public byte[] XorTable = Array.Empty<byte>();      // 16

    private static ulong[] Splat(ReadOnlySpan<byte> bytes)
    {
      var r = new ulong[8];
      for (int i = 0; i < 8; i++)
        r[i] = 0x0101010101010101UL * bytes[7 - i];
      return r;
    }

    /// <summary>Transforms one 16-byte block (the raw cipher, without CBC chaining).</summary>
    public byte[] DecryptBlock(ulong[] key, ReadOnlySpan<byte> ct)
    {
      ulong s0 = 0, s1 = 0;
      for (int i = 0; i < 16; i++)
      {
        if (i < 8) s0 ^= Init[i * 256 + ct[i]];
        else s1 ^= Init[i * 256 + ct[i]];
      }

      Span<byte> sb = stackalloc byte[16];
      for (int ri = 0; ri < 17; ri++)
      {
        BinaryPrimitives.WriteUInt64LittleEndian(sb, s0);
        BinaryPrimitives.WriteUInt64LittleEndian(sb[8..], s1);
        var h0 = Splat(sb[..8]);
        var h1 = Splat(sb[8..]);

        s0 = key[(ri + 1) * 2];
        s1 = key[(ri + 1) * 2 + 1];

        for (int i = 0; i < 16; i++)
        {
          int half = (ri < 2 || ri == 16) ? (i / 8) : Mix[i];
          var hv = half == 0 ? h0 : h1;
          int mi = ri * 16 + i;

          ulong lo = 0, hi = 0;
          for (int j = 0; j < 8; j++)
          {
            lo ^= Masks[mi][8 + j] & hv[j];
            hi ^= Masks[mi][j] & hv[j];
          }

          lo ^= lo >> 1; lo ^= lo >> 2; lo ^= lo >> 4;
          lo &= 0x0101010101010101UL;
          lo |= lo >> 7; lo |= lo >> 14; lo |= lo >> 28;

          hi ^= hi << 1; hi ^= hi >> 2; hi ^= hi >> 4;
          hi &= 0x0202020202020202UL;
          hi |= hi << 7; hi |= hi >> 14;

          int idx = ((int)(lo & 0xFF) ^ (int)(hi & 0xF00)) ^ Xors[mi];
          if (i < 8) s0 ^= Lookups[ri][idx];
          else s1 ^= Lookups[ri][idx];
        }
      }

      Span<byte> fb = stackalloc byte[16];
      BinaryPrimitives.WriteUInt64LittleEndian(fb, s0);
      BinaryPrimitives.WriteUInt64LittleEndian(fb[8..], s1);
      var fh0 = Splat(fb[..8]);
      var fh1 = Splat(fb[8..]);

      var output = new byte[16];
      for (int i = 0; i < 16; i++)
      {
        var fh = i < 8 ? fh0 : fh1;
        ulong v = 0;
        for (int j = 0; j < 8; j++)
          v ^= FinalMasks[i * 8 + j] & fh[j];
        v ^= v >> 1; v ^= v >> 2; v ^= v >> 4;
        v &= 0x0101010101010101UL;
        v |= v >> 7; v |= v >> 14; v |= v >> 28;
        output[i] = EndTable[i * 256 + ((int)(v & 0xFF) ^ XorTable[i])];
      }
      return output;
    }
  }
}
