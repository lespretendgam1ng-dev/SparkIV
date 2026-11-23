/**********************************************************************\

 RageLib - SparkIV
 RDR2 (PC) RPF8 archive reader.

 Format reference: librdr3 (MIT) / rage-cli (Unlicense). Specific to RDR2 on PC.

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
using System.IO;
using System.IO.Compression;

namespace RageLib.Rdr2.Pc
{
  /// <summary>
  /// Reads the directory of a Red Dead Redemption 2 (PC) RPF8 archive. The table of contents starts at 0x110,
  /// is a flat array of 24-byte entries, and is CBC-encrypted with the TFIT2 key named by the header's tag
  /// (see <see cref="Rdr2Secrets"/>); an unencrypted archive uses tag 0xFF. Original file names are not stored
  /// in the archive - entries carry a name hash, so SparkIV shows <c>&lt;hash&gt;.&lt;ext&gt;</c> names.
  ///
  /// Specific to RDR2 on PC; RPF8 is unrelated to the RPF2/RPF3 format GTA IV uses.
  /// </summary>
  public sealed class Rpf8Archive
  {
    public const uint Magic = 0x52504638; // 'RPF8'

    public sealed class Entry
    {
      public string Name;
      public long Offset;       // byte offset of the stored payload in the archive
      public long DiskSize;     // bytes stored (compressed/encrypted) in the archive
      public long Size;         // uncompressed size
      public bool Resource;
      public bool Directory;
      public bool Encrypted;
      public int Tag;           // per-entry encryption key tag (0xFF = none)
      public int Compressor;    // 0 = stored, 1 = deflate, 2 = Oodle
      internal ulong A, B, C;
    }

    public IReadOnlyList<Entry> Entries => _entries;
    private readonly List<Entry> _entries = new();
    private readonly Rdr2Secrets _secrets;

    private Rpf8Archive(Rdr2Secrets secrets) { _secrets = secrets; }

    public static bool IsRpf8(byte[] data)
      => data != null && data.Length >= 16 && BinaryPrimitives.ReadUInt32LittleEndian(data) == Magic;

    /// <summary>
    /// Parses the directory of an RPF8 archive. Throws if the data is not RPF8, not the PC platform, or if the
    /// decrypted entries do not stay within the archive (which is how a wrong key or corrupt TOC is caught).
    /// </summary>
    public static Rpf8Archive Parse(byte[] data, Rdr2Secrets secrets)
    {
      if (data == null || data.Length < 0x110) throw new InvalidDataException("RPF8 header is truncated.");
      if (BinaryPrimitives.ReadUInt32LittleEndian(data) != Magic) throw new InvalidDataException("Not an RPF8 archive.");
      ushort platform = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(14));
      if (platform != 'y') throw new InvalidDataException($"RPF8 platform 0x{platform:X4} is not RDR2 PC ('y').");
      int tag = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(12));
      int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
      long length = (long)count * 24;
      long end = 0x110 + length;
      if (count < 0 || end > data.Length) throw new InvalidDataException("RPF8 entry table is truncated.");

      var toc = data[0x110..(int)end];
      if (tag != Rdr2Secrets.NoEncryption)
        secrets.Decrypt(tag, toc);

      var archive = new Rpf8Archive(secrets);
      for (int i = 0; i < count; i++)
      {
        int p = i * 24;
        ulong a = BinaryPrimitives.ReadUInt64LittleEndian(toc.AsSpan(p));
        ulong b = BinaryPrimitives.ReadUInt64LittleEndian(toc.AsSpan(p + 8));
        ulong c = BinaryPrimitives.ReadUInt64LittleEndian(toc.AsSpan(p + 16));

        int extId = (int)((a >> 48) & 0xFF);
        bool directory = extId == 0xFE;
        bool resource = (a & (1UL << 56)) != 0;
        long diskSize = (long)((b & 0xFFFFFFF) << 4);
        long offset = (long)(((b >> 28) & 0x7FFFFFFF) << 4);
        int compressor = (int)(b >> 59);
        long size = resource ? (long)((c & 0xFFFFFFF0) + ((c >> 32) & 0xFFFFFFF0)) : (long)c;
        int entTag = (int)((a >> 40) & 0xFF);

        var e = new Entry
        {
          Name = directory ? $"{(uint)a:X8}" : $"{(uint)a:X8}.{Extension(extId)}",
          Offset = offset,
          DiskSize = diskSize,
          Size = size,
          Resource = resource,
          Directory = directory,
          Encrypted = entTag != Rdr2Secrets.NoEncryption,
          Tag = entTag,
          Compressor = compressor,
          A = a, B = b, C = c,
        };
        if (!directory)
        {
          long storedEnd = offset + diskSize;
          if (storedEnd > data.Length)
            throw new InvalidDataException($"RPF8 entry {e.Name} lies outside the archive (wrong keys or corrupt TOC).");
        }
        archive._entries.Add(e);
      }
      return archive;
    }

    /// <summary>Reads and decrypts/decompresses one entry's payload. Oodle-compressed entries are not yet supported.</summary>
    public byte[] Extract(byte[] data, int index)
    {
      var e = _entries[index];
      if (e.Directory) throw new InvalidOperationException("Directory entries have no payload.");
      long start = e.Offset;
      long raw = e.DiskSize;
      if ((e.A & (1UL << 57)) != 0) raw -= 0x100;     // trailing signature
      if (e.Resource) { start += 16; raw -= 16; }      // resource header
      if (raw < 0) throw new InvalidDataException("RPF8 entry payload is truncated.");
      long size = e.Size;
      long length = e.Compressor == 0 ? size : raw;
      if (start + length > data.Length) throw new InvalidDataException("RPF8 entry payload is out of range.");

      var input = data[(int)start..(int)(start + length)];
      if (e.Tag != Rdr2Secrets.NoEncryption)
      {
        int chunk = e.Resource ? (e.Compressor != 0 ? 0x80000 : (int)size)
                               : (e.Compressor != 0 ? 0x2000 : 0x1000);
        _secrets.DecryptRanges(e.Tag, input, CipherRanges((int)((e.A >> 32) & 0xFF), (int)raw, chunk));
      }

      byte[] payload = Decompress(e.Compressor, input, (int)size);
      if (!e.Resource) return payload;

      // Rebuild the RSC8 resource header the game strips when packing.
      uint version = (((uint)e.C & 0xF) << 4) | (((uint)(e.C >> 32)) & 0xF);
      var outBuf = new byte[16 + payload.Length];
      BinaryPrimitives.WriteUInt32LittleEndian(outBuf.AsSpan(0), 0x38435352); // 'RSC8'
      BinaryPrimitives.WriteUInt32LittleEndian(outBuf.AsSpan(4), version | 0x1F00);
      BinaryPrimitives.WriteUInt32LittleEndian(outBuf.AsSpan(8), (uint)e.C);
      BinaryPrimitives.WriteUInt32LittleEndian(outBuf.AsSpan(12), (uint)(e.C >> 32));
      Array.Copy(payload, 0, outBuf, 16, payload.Length);
      return outBuf;
    }

    private static byte[] Decompress(int compressor, byte[] input, int size)
    {
      switch (compressor)
      {
        case 0:
          if (input.Length < size) throw new InvalidDataException("RDR2 stored payload is truncated.");
          return input[..size];
        case 1:
          using (var ds = new DeflateStream(new MemoryStream(input), CompressionMode.Decompress))
          {
            var outBuf = new byte[size];
            int read = 0;
            while (read < size)
            {
              int n = ds.Read(outBuf, read, size - read);
              if (n == 0) break;
              read += n;
            }
            if (read != size) throw new InvalidDataException("RDR2 deflate size mismatch.");
            return outBuf;
          }
        case 2:
          throw new NotSupportedException("RDR2 Oodle-compressed entries are not supported yet.");
        default:
          throw new NotSupportedException($"RDR2 compressor {compressor} is not supported.");
      }
    }

    // The game encrypts only parts of larger payloads; this reproduces which byte ranges are enciphered.
    private static IReadOnlyList<(int start, int end)> CipherRanges(int config, int size, int chunk)
    {
      int head = (config & 3) == 0 ? 0 : 0x400 << ((config & 3) * 2);
      int lenBits = (config >> 2) & 7;
      int length = lenBits == 0 ? 0 : 0x400 << lenBits;
      int stride = lenBits == 0 ? 0 : (((config >> 5) & 7) + 1) << 16;
      int tail = Math.Max(0, size - 0x400);
      var result = new List<(int, int)>();
      int offset = Math.Min(head, size);
      if (offset > 0) result.Add((0, offset));
      if (head < tail)
      {
        if (stride != 0)
        {
          if (stride == chunk && tail < stride && stride < size)
          {
            offset = stride;
          }
          else
          {
            int block = offset != 0 ? stride : 0;
            while (block + length <= tail)
            {
              int s = Math.Max(block, offset);
              int en = Math.Min(block + length, size);
              if (s < en) { result.Add((s, en)); offset = en; }
              block += stride;
            }
          }
        }
        int start = Math.Max(tail, offset);
        if (start < size) result.Add((start, size));
      }
      return result;
    }

    private static readonly string[] BaseExt =
    {
      "rpf", "ymf", "ydr", "yft", "ydd", "ytd", "ybn", "ybd", "ypd", "ybs", "ysd", "ymt", "ysc", "ycs",
    };
    private static readonly string[] ExtraExt =
    {
      "mrf", "cut", "gfx", "ycd", "yld", "ypmd", "ypm", "yed", "ypt", "ymap", "ytyp", "ych",
      "yldb", "yjd", "yad", "ynv", "yhn", "ypl", "ynd", "yvr", "ywr", "ynh", "yfd", "yas",
    };

    private static string Extension(int id)
    {
      if (id < BaseExt.Length) return BaseExt[id];
      int x = id - 64;
      if (x >= 0 && x < ExtraExt.Length) return ExtraExt[x];
      return "bin";
    }
  }
}
