/**********************************************************************\

 RageLib - SparkIV
 GTA VI RPF9 table-of-contents parser.

 Parses the decrypted RPF9 TOC (see Rpf9Cipher) into entries. Layout of the
 decoded TOC (verified against every standalone archive of the game):

   0x000  header (version, key descriptors, ...)
   0x160  list of the storage-codec ids used by the archive (count + ids)
   0x170  table directory: 0x18-byte descriptors
            { u64 typeHash, u32 count, u32 offset, u16 recordSize, u16 flags, u32 0 }
          It ends with the slot table (type 0xB6F5BDB2C66DA2E9). Tables with
          flags == 1 (the slot table among them) are lookup tables the game
          builds at run time; they take no space in the TOC.
   tableBase = 0x170 + tables * 0x18
          member rows: one row per member (row count = the slot table's
          count), one index per stored (flags == 0) table, in directory
          order. Indices are u8 when every stored table has at most 255
          records, otherwise u16; the all-ones value means "no record".
   base2 = tableBase + align8(rows * columns * index width)
          every stored table lives at base2 + its descriptor offset.

 Tables by type hash:
   0x33F70DFAF2C50989  entry (0x18): q0 = offset/16 (bits 0..31) | resource
                       bit (32) | storage codec id (bits 40..47);
                       q8 = stored size / 16; q10 = decoded size.
   0x73BCB72DCE74F11F  64-bit path hash (8): MurmurHash3 x64 (low 64 bits) of
                       the lower-case relative path, with a recognised
                       extension removed (see Rpf9NameDictionary).
   0xBA180AFB232D1B4B  resource (0x14): u32 system size, u16 flags, u16 graphics
                       64 KiB pages, u32 stored/16, u32 offset/16, u32 version.
                       The member is a 16-byte clear prefix plus its chunks.
   0x286030A358A789FB  payload encryption descriptor (0x40, type 17 / flag 4).
   0xE0C12D7A5D4884C5  chunk info for transformed members (8).
   0xB7076B6245D35FB5  per-member value for transformed members (4).

 RPF9 stores no directory table and no plain names: a member's folder is part
 of its (hashed) path. Names and folders are recovered by hashing candidate
 paths (Rpf9NameDictionary), exactly as the game looks members up.

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
  /// <summary>One RPF9 member (one row of the slot table).</summary>
  public sealed class Rpf9Entry
  {
    /// <summary>Row in the slot table (stable member number).</summary>
    public int Slot;

    /// <summary>64-bit path hash (0 if the member has none).</summary>
    public ulong NameHash;

    /// <summary>Absolute file offset of the member data (bytes).</summary>
    public long Offset;

    /// <summary>Stored (on-disk) length in bytes, exact from the entry record.</summary>
    public long StoredSize;

    /// <summary>Decoded length in bytes (the stored length for resources).</summary>
    public long UncompressedSize;

    /// <summary>Storage codec id (q0 byte 5): 0x00 = stored as-is, others are transforms.</summary>
    public byte Codec;

    /// <summary>The member is a resource (q0 bit 32).</summary>
    public bool IsResource;

    /// <summary>The member has a payload encryption descriptor.</summary>
    public bool IsEncrypted;

    /// <summary>The 0x40-byte payload encryption descriptor, when encrypted.</summary>
    public byte[] EncDescriptor;

    /// <summary>The member has a chunk-info record (it is chunked/compressed).</summary>
    public bool HasChunks;

    /// <summary>Raw upper 32 bits of the entry's first word (flags).</summary>
    public uint Flags;

    /// <summary>Resource type/version (resource-table members only, else 0).</summary>
    public int ResourceVersion;

    /// <summary>The member's type as an index into the archive's extension table
    /// (entry byte 5 / resource byte 16); see <see cref="Rpf9Extensions"/>.</summary>
    public byte ExtensionCode;

    /// <summary>Decoded system / graphics section sizes (resource-table members only).</summary>
    public long SystemSize, GraphicsSize;
  }

  /// <summary>Parses a decrypted RPF9 table of contents into its members.</summary>
  public static class Rpf9Toc
  {
    private const int DefaultDirectoryOffset = 0x170;
    public const int DescriptorSize = 0x18;
    private const int MaxTables = 32;

    public const ulong EntryTableHash = 0x33F70DFAF2C50989;
    public const ulong ResourceTableHash = 0xBA180AFB232D1B4B;
    public const ulong PathHash64TableHash = 0x73BCB72DCE74F11F;
    public const ulong EncryptionTableHash = 0x286030A358A789FB;
    public const ulong ChunkInfoTableHash = 0xE0C12D7A5D4884C5;
    public const ulong SlotTableHash = 0xB6F5BDB2C66DA2E9;

    private sealed class Table
    {
      public ulong Hash;
      public int Count, Offset, RecordSize, Flags, At;
    }

    public static List<Rpf9Entry> Parse(ReadOnlySpan<byte> decoded, long memberBase, long archiveSize)
    {
      var entries = new List<Rpf9Entry>();
      int DirectoryOffset = LocateDirectory(decoded);

      // 1) Table directory. It ends with the slot (lookup) table; tables with
      //    flags == 1 are built by the game at run time (hash lookups) and take
      //    no space here, only the flags == 0 tables are stored.
      var tables = new List<Table>();
      Table slotTable = null;
      for (int k = 0; k < MaxTables; k++)
      {
        int d = DirectoryOffset + k * DescriptorSize;
        if (d + DescriptorSize > decoded.Length)
          break;
        var t = new Table
        {
          Hash = BinaryPrimitives.ReadUInt64LittleEndian(decoded.Slice(d, 8)),
          Count = (int)BinaryPrimitives.ReadUInt32LittleEndian(decoded.Slice(d + 8, 4)),
          Offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(decoded.Slice(d + 12, 4)),
          RecordSize = BinaryPrimitives.ReadUInt16LittleEndian(decoded.Slice(d + 16, 2)),
          Flags = BinaryPrimitives.ReadUInt16LittleEndian(decoded.Slice(d + 18, 2)),
        };
        uint pad = BinaryPrimitives.ReadUInt32LittleEndian(decoded.Slice(d + 20, 4));
        if (t.Hash == 0 || t.Count < 0 || t.Count > 4_000_000 || t.Offset < 0 || t.Offset > decoded.Length ||
            t.RecordSize <= 0 || t.RecordSize > 0x400 || t.Flags > 1 || pad != 0)
          break; // past the directory (the member rows follow it)
        tables.Add(t);
        if (t.Hash == SlotTableHash) slotTable = t;
      }
      // The slot table gives the row count; fall back to the last run-time table.
      slotTable ??= tables.FindLast(t => t.Flags == 1);
      if (slotTable == null)
        return entries;
      var columns = tables.FindAll(t => t.Flags == 0);
      if (columns.Count == 0)
        return entries;
      int tableBase = DirectoryOffset + tables.Count * DescriptorSize;

      // 2) Slot table: index width, then base of the other tables.
      int maxCount = 0;
      foreach (var t in columns) maxCount = Math.Max(maxCount, t.Count);
      int width = maxCount <= 0xFF ? 1 : 2;
      if (!LayoutFits(decoded, tableBase, slotTable, columns, width) &&
          LayoutFits(decoded, tableBase, slotTable, columns, 3 - width))
        width = 3 - width;
      int base2 = TableArea(tableBase, slotTable.Count, columns.Count, width);
      foreach (var t in columns)
        t.At = base2 + t.Offset;

      int entryCol = columns.FindIndex(t => t.Hash == EntryTableHash && t.RecordSize >= 0x18);
      int resCol = columns.FindIndex(t => t.Hash == ResourceTableHash && t.RecordSize >= 0x14);
      if (entryCol < 0 && resCol < 0)
        return entries;
      foreach (int c in new[] { entryCol, resCol })
        if (c >= 0 && columns[c].At + (long)columns[c].Count * columns[c].RecordSize > decoded.Length)
          return entries;
      int hashCol = columns.FindIndex(t => t.Hash == PathHash64TableHash && t.RecordSize >= 8);
      int encCol = columns.FindIndex(t => t.Hash == EncryptionTableHash);
      int chunkCol = columns.FindIndex(t => t.Hash == ChunkInfoTableHash);

      // 3) One member per slot row: a plain member (entry table) or a resource
      //    (resource table).
      for (int s = 0; s < slotTable.Count; s++)
      {
        int entryIndex = entryCol < 0 ? -1 : SlotIndex(decoded, tableBase, s, entryCol, columns.Count, width);
        int resIndex = resCol < 0 ? -1 : SlotIndex(decoded, tableBase, s, resCol, columns.Count, width);

        long offset, stored, decodedSize, systemSize = 0, graphicsSize = 0;
        bool isResource;
        byte codec;
        uint flags;
        int version = 0;
        if (entryIndex >= 0 && entryIndex < columns[entryCol].Count)
        {
          Table et = columns[entryCol];
          int r = et.At + entryIndex * et.RecordSize;
          ulong q0 = BinaryPrimitives.ReadUInt64LittleEndian(decoded.Slice(r, 8));
          ulong q8 = BinaryPrimitives.ReadUInt64LittleEndian(decoded.Slice(r + 8, 8));
          ulong q10 = BinaryPrimitives.ReadUInt64LittleEndian(decoded.Slice(r + 16, 8));
          // Entry record: 40-bit offset/16, byte 5 = extension code; 40-bit
          // stored/16; 44-bit decoded size. (Plain members; resources are in the
          // resource table.)
          offset = (long)(q0 & 0xFFFFFFFFFF) << 4;
          stored = (long)(q8 & 0xFFFFFFFFFF) << 4;
          isResource = false;
          codec = (byte)((q0 >> 40) & 0xFF);
          flags = (uint)(q0 >> 32);
          decodedSize = (long)(q10 & 0xFFFFFFFFFFF);
          if (decodedSize <= 0 || decodedSize > (1L << 40))
            decodedSize = stored;
        }
        else if (resIndex >= 0 && resIndex < columns[resCol].Count)
        {
          // Resource record (0x14): u32 system size (low 4 bits/top bit are
          // flags), u32 graphics size, u32 stored/16, u32 offset/16, byte 16 =
          // extension code. The member is a 16-byte header followed by zlib
          // pages that decode to system + graphics (see Rpf9Resource).
          Table rt = columns[resCol];
          int r = rt.At + resIndex * rt.RecordSize;
          systemSize = BinaryPrimitives.ReadUInt32LittleEndian(decoded.Slice(r, 4)) & 0x7FFFFFF0;
          graphicsSize = BinaryPrimitives.ReadUInt32LittleEndian(decoded.Slice(r + 4, 4)) & 0xFFFFFFF0;
          stored = (long)BinaryPrimitives.ReadUInt32LittleEndian(decoded.Slice(r + 8, 4)) << 4;
          offset = (long)BinaryPrimitives.ReadUInt32LittleEndian(decoded.Slice(r + 12, 4)) << 4;
          codec = decoded[r + 16];
          version = codec;
          flags = BinaryPrimitives.ReadUInt32LittleEndian(decoded.Slice(r, 4)) & 0x8000000F;
          isResource = true;
          decodedSize = systemSize + graphicsSize;
        }
        else
          continue;

        if (offset < Math.Max(0x40, memberBase) || offset >= archiveSize)
          continue;
        stored = Math.Min(stored, archiveSize - offset);

        ulong hash = 0;
        if (hashCol >= 0)
        {
          int hi = SlotIndex(decoded, tableBase, s, hashCol, columns.Count, width);
          var ht = columns[hashCol];
          if (hi >= 0 && hi < ht.Count && ht.At + (hi + 1) * ht.RecordSize <= decoded.Length)
            hash = BinaryPrimitives.ReadUInt64LittleEndian(decoded.Slice(ht.At + hi * ht.RecordSize, 8));
        }

        // The 0x40-byte payload encryption descriptor, when this member has one.
        byte[] encDesc = null;
        if (encCol >= 0)
        {
          int ei = SlotIndex(decoded, tableBase, s, encCol, columns.Count, width);
          Table ect = columns[encCol];
          if (ei >= 0 && ei < ect.Count && ect.RecordSize >= 0x40 &&
              ect.At + (ei + 1) * ect.RecordSize <= decoded.Length)
            encDesc = decoded.Slice(ect.At + ei * ect.RecordSize, 0x40).ToArray();
        }

        entries.Add(new Rpf9Entry
        {
          Slot = s,
          NameHash = hash,
          Offset = offset,
          StoredSize = stored,
          UncompressedSize = decodedSize,
          Codec = codec,
          IsResource = isResource,
          IsEncrypted = encDesc != null,
          EncDescriptor = encDesc,
          ExtensionCode = codec,
          HasChunks = chunkCol >= 0 && SlotIndex(decoded, tableBase, s, chunkCol, columns.Count, width) >= 0,
          Flags = flags,
          ResourceVersion = version,
          SystemSize = systemSize,
          GraphicsSize = graphicsSize,
        });
      }
      return entries;
    }

    /// <summary>
    /// Offset of the table directory in a decoded TOC. The header holds a list of
    /// sections at 0x18 as (u32 type 0x0001xxxx, u32 offset) pairs; the table
    /// directory is type 0x10009 at (its pair's position + 0x18 + offset).
    /// Most archives have only that section (directory at 0x170); some add a
    /// sorted hash list (type 0x10004) first.
    /// </summary>
    public static int FindDirectory(ReadOnlySpan<byte> d)
    {
      foreach (var (type, at) in Sections(d))
        if (type == 0x00010009)
          // 16-byte aligned (verified on the 1- and 2-section header layouts).
          return at > 0 && at < int.MaxValue - 16 ? (int)((at + 15) & ~15L) : DefaultDirectoryOffset;
      return DefaultDirectoryOffset;
    }

    /// <summary>
    /// The header's section records: from 0x08, (u32 type, u32 offset) pairs up
    /// to the first key descriptor (u32 0x10xxxxxx). For 0x0001xxxx sections the
    /// target is (record position + 0x18 + offset).
    /// </summary>
    public static List<(uint Type, long Target)> Sections(ReadOnlySpan<byte> d)
    {
      var list = new List<(uint, long)>();
      for (int o = 0x08; o + 8 <= d.Length && o < 0x100; o += 8)
      {
        uint type = BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(o, 4));
        if ((type >> 24) == 0x10 || type == 0) break;
        if ((type & 0xFFFF0000) == 0x00010000)
          list.Add((type, o + 0x18L + BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(o + 4, 4))));
      }
      return list;
    }

    /// <summary>
    /// Directory offset in a decoded TOC: the section-list position when it holds
    /// a valid directory, otherwise found via the slot table descriptor (walking
    /// back over the preceding descriptors). Headers with many sections put
    /// extra structures before the directory.
    /// </summary>
    public static int LocateDirectory(ReadOnlySpan<byte> d)
    {
      int dir = FindDirectory(d);
      if (LooksLikeDirectory(d, dir))
        return dir;
      if (LooksLikeDirectory(d, DefaultDirectoryOffset))
        return DefaultDirectoryOffset;
      int slot = FindSlotDescriptor(d, 0, d.Length);
      if (slot < 0)
        return dir;
      while (slot - DescriptorSize >= 0 && LooksLikeDescriptor(d, slot - DescriptorSize))
        slot -= DescriptorSize;
      return slot;
    }

    /// <summary>Position (8-aligned) of the slot table's descriptor in [from, to), or -1.</summary>
    public static int FindSlotDescriptor(ReadOnlySpan<byte> d, int from, int to)
    {
      to = Math.Min(to, d.Length);
      for (int p = (from + 7) & ~7; p + DescriptorSize <= to; p += 8)
        if (BinaryPrimitives.ReadUInt64LittleEndian(d.Slice(p, 8)) == SlotTableHash && LooksLikeDescriptor(d, p))
          return p;
      return -1;
    }

    private static bool LooksLikeDirectory(ReadOnlySpan<byte> d, int at)
    {
      // A directory is a run of descriptors that contains the slot table.
      for (int k = 0; k < MaxTables && LooksLikeDescriptor(d, at + k * DescriptorSize); k++)
        if (BinaryPrimitives.ReadUInt64LittleEndian(d.Slice(at + k * DescriptorSize, 8)) == SlotTableHash)
          return true;
      return false;
    }

    /// <summary>
    /// Offset of the data-span IV in a decoded TOC header: 6 bytes into the first
    /// key descriptor (u32 0x10xxxxxx, u16 0x0010, IV...), which follows the
    /// section list.
    /// </summary>
    public static int FindDataIv(ReadOnlySpan<byte> d)
    {
      for (int o = 0x18; o + 0x16 <= d.Length && o < 0x80; o += 8)
        if (d[o + 3] == 0x10 && d[o + 2] == 0 && d[o + 1] == 0 &&
            BinaryPrimitives.ReadUInt16LittleEndian(d.Slice(o + 4, 2)) == 0x0010)
          return o + 6;
      return 0x26;
    }

    /// <summary>Whether <paramref name="d"/> at <paramref name="at"/> looks like a table descriptor.</summary>
    public static bool LooksLikeDescriptor(ReadOnlySpan<byte> d, int at)
    {
      if (at < 0 || at + DescriptorSize > d.Length) return false;
      ulong hash = BinaryPrimitives.ReadUInt64LittleEndian(d.Slice(at, 8));
      uint count = BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(at + 8, 4));
      ushort rsz = BinaryPrimitives.ReadUInt16LittleEndian(d.Slice(at + 16, 2));
      ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(d.Slice(at + 18, 2));
      uint pad = BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(at + 20, 4));
      return hash != 0 && count <= 4_000_000 && rsz > 0 && rsz <= 0x400 && flags <= 1 && pad == 0;
    }

    private static int TableArea(int tableBase, int slots, int columns, int width)
    {
      long size = (long)slots * columns * width;
      return tableBase + (int)((size + 7) & ~7L);
    }

    /// <summary>Reads one slot-table index; -1 for the "no record" sentinel.</summary>
    private static int SlotIndex(ReadOnlySpan<byte> d, int tableBase, int slot, int column, int columns, int width)
    {
      int o = tableBase + (slot * columns + column) * width;
      if (o + width > d.Length) return -1;
      if (width == 1)
        return d[o] == 0xFF ? -1 : d[o];
      int v = BinaryPrimitives.ReadUInt16LittleEndian(d.Slice(o, 2));
      return v == 0xFFFF ? -1 : v;
    }

    /// <summary>Whether a slot index width yields a layout whose indices and tables are in range.</summary>
    private static bool LayoutFits(ReadOnlySpan<byte> d, int tableBase, Table slotTable, List<Table> columns, int width)
    {
      int base2 = TableArea(tableBase, slotTable.Count, columns.Count, width);
      foreach (var t in columns)
        if (base2 + t.Offset + (long)t.Count * t.RecordSize > d.Length)
          return false;
      int sample = Math.Min(slotTable.Count, 512);
      for (int s = 0; s < sample; s++)
        for (int c = 0; c < columns.Count; c++)
        {
          int v = SlotIndex(d, tableBase, s, c, columns.Count, width);
          if (v >= columns[c].Count)
            return false;
        }
      return true;
    }
  }
}
