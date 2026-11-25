/**********************************************************************\

 RageLib - SparkIV
 GTA VI texture pools (.ttpf, data\ttdb\...).

 Ported from rpf9-explorer's TtpfReader. A pool is an "RDS5" container: a
 table of chunks (stored raw, zlib or Xbox BCPack) that together form one
 virtual address space; at its start an embedded "ETPF" catalog describes
 the textures, each an AMD tiled allocation inside that space. Read-only.

 This program is free software: you can redistribute it and/or modify
 it under the terms of the GNU General Public License as published by
 the Free Software Foundation, either version 3 of the License, or
 (at your option) any later version.

 This program is distributed in the hope that it will be useful,
 but WITHOUT ANY WARRANTY; without even the implied warranty of
 MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 GNU General Public License for more details.

\**********************************************************************/

#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using File = System.IO.File;

namespace RageLib.GtaVI.Textures
{
  /// <summary>One texture of a texture pool.</summary>
  public sealed class TtpfTexture
  {
    public int Index { get; init; }
    /// <summary>A generated name (ordinal + opaque id): the pool stores no file names.</summary>
    public string Name { get; init; } = "";
    public int Width { get; init; }
    public int Height { get; init; }
    public int Depth { get; init; } = 1;
    public int Dimension { get; init; } = 1;
    public int DxgiFormat { get; init; }
    public string FormatName { get; init; } = "";
    /// <summary>The mip levels stored in the pool.</summary>
    public int SourceMips { get; init; }
    /// <summary>The first mip level that can be decoded (higher ones may need an unsupported BCPack codec).</summary>
    public int BaseMipLevel { get; init; }
    /// <summary>How many levels from <see cref="BaseMipLevel"/> on can be decoded.</summary>
    public int Mips { get; init; }
    public ulong AssetKey { get; init; }
    public string Id { get; init; } = "";
    public long StoredSize { get; init; }
    public long DdsSize { get; init; }
    /// <summary>Why the texture can't be read, or null.</summary>
    public string? Limitation { get; init; }
    public bool CanExtract => Limitation == null && DdsSize > 0;
    internal long VirtualOffset { get; init; }
  }

  /// <summary>A GTA VI texture pool file, read on demand.</summary>
  public sealed class TtpfPool
  {
    private const int MaxOutput = 256 * 1024 * 1024;
    private const int MaxIndexBytes = 64 * 1024 * 1024;
    private const int MaxTextures = 250_000;

    private sealed record Chunk(long Source, int Packed, long Virtual, int Size, int Codec, int Padding);
    private sealed record Mip(int Width, int Height, int PaddedWidth, int Offset, int RegionSize, int TailX, int TailY);

    private sealed record Layout(int Size, int BlockWidth, int BlockHeight, int FirstTail, int ElementBytes, IReadOnlyList<Mip> Mips)
    {
      public int TileBytes { get; init; } = 65536;
      public int SliceBytes { get; init; }
      public int DataOffset { get; init; }
      public int BlockDepth { get; init; } = 1;
      public int ZBlockStride { get; init; }
      public bool MipMajorSlices { get; init; }
      public bool Volume { get; init; }
    }

    private static readonly int[][] Patterns =
    {
      new[] { 1, 2, 3, -2, -1, -3, 4, -4, -5, 5, -6, 6, -7, 7, -8, 8 },
      new[] { 0, 1, 2, 3, -1, -2, -3, 4, -4, 5, -5, 6, -6, 7, -7, 8 },
      new[] { 0, 0, 1, 2, -1, -2, 3, -3, -4, 4, -5, 5, -6, 6, -7, 7 },
      new[] { 0, 0, 0, 1, -1, 2, 3, -2, -3, 4, -4, 5, -5, 6, -6, 7 },
      new[] { 0, 0, 0, 0, 1, -1, 2, -2, -3, 3, -4, 4, -5, 5, -6, 6 },
    };
    private static readonly int[][] FourKbPatterns =
    {
      new[] { 1, 2, 3, 4, -1, -2, -3, -4, -5, 5, -6, 6 },
      new[] { 0, 1, 2, 3, -1, -2, -3, 4, -4, 5, -5, 6 },
      new[] { 0, 0, 1, 2, -1, -2, -3, 3, -4, 4, -5, 5 },
      new[] { 0, 0, 0, 1, -1, -2, 2, 3, -3, 4, -4, 5 },
      new[] { 0, 0, 0, 0, -1, -2, 1, 2, -3, 3, -4, 4 },
    };
    private static readonly int[][] VolumePatterns =
    {
      new[] { 1, 2, 101, -1, 102, -2, 3, 103, -3, 4, 104, -4 },
      new[] { 0, 1, 101, -1, 102, -2, 2, 103, -3, 3, 104, -4 },
      new[] { 0, 0, 1, -1, 101, -2, 2, 102, -3, 3, 103, -4 },
      new[] { 0, 0, 0, 1, 101, -1, 2, 102, -2, 3, 103, -3 },
      new[] { 0, 0, 0, 0, 101, -1, 1, 102, -2, 2, 103, -3 },
    };

    private readonly string _path;
    private readonly long _length;
    private readonly List<Chunk> _chunks;
    private readonly Layout?[] _layouts;

    private TtpfPool(string path, long length, List<Chunk> chunks, List<TtpfTexture> textures, Layout?[] layouts)
    {
      _path = path;
      _length = length;
      _chunks = chunks;
      Textures = textures;
      _layouts = layouts;
    }

    public IReadOnlyList<TtpfTexture> Textures { get; }

    public static bool IsTtpfFile(string path)
    {
      try
      {
        using var fs = File.OpenRead(path);
        Span<byte> magic = stackalloc byte[4];
        return fs.Read(magic) == 4 && magic.SequenceEqual("RDS5"u8);
      }
      catch (IOException) { return false; }
      catch (UnauthorizedAccessException) { return false; }
    }

    // ── catalog ─────────────────────────────────────────────────────────

    public static TtpfPool Open(string path)
    {
      using var input = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Write | FileShare.Delete);
      long fileSize = input.Length;
      if (fileSize < 256) throw new InvalidDataException("Truncated RDS5 header.");
      byte[] header = new byte[24];
      input.ReadExactly(header);
      if (!header.AsSpan(0, 4).SequenceEqual("RDS5"u8)) throw new InvalidDataException("Not a GTA VI texture pool (RDS5).");
      uint count = U32(header, 4);
      ulong sizeWord = U64(header, 8);
      long virtualSize = checked((long)(sizeWord & 0x00FFFFFFFFFFFFFFUL));
      if (count < 3 || count > 1_000_002 || (sizeWord >> 56) != 22 || virtualSize < 4096 || virtualSize > 1L << 34 ||
          U32(header, 16) < (count + 31) / 32 || U32(header, 16) > (count + 31) / 32 + 1)
        throw new InvalidDataException("Unsupported RDS5 index layout.");

      long tableEnd = checked(24L + (count - 1) * 8L), etpf = Align(tableEnd, 256);
      if (tableEnd > fileSize - 16 || (count - 1) * 8L > MaxIndexBytes) throw new InvalidDataException("RDS5 index exceeds its file.");
      byte[] table = new byte[checked(((int)count - 1) * 8)];
      input.ReadExactly(table);
      var chunks = new List<Chunk>(checked((int)count - 2));
      long sentinelSource = SourceOffset(U64(table, table.Length - 8)), sentinelVirtual = VirtualOffset(U64(table, table.Length - 8));
      if (sentinelSource != fileSize || sentinelVirtual != virtualSize) throw new InvalidDataException("RDS5 sentinel does not match the file.");
      for (int i = 0; i < count - 2; i++)
      {
        ulong value = U64(table, i * 8), next = U64(table, (i + 1) * 8);
        long start = SourceOffset(value), end = SourceOffset(next), v = VirtualOffset(value), ve = VirtualOffset(next);
        int pad = (int)(value >> 52), codec = (int)((value >> 26) & 15);
        long packed = end - start - pad, size = ve - v;
        if (start < etpf + 16 || end > fileSize || packed < 1 || packed > MaxOutput || size < 1 || size > MaxOutput ||
            ve > virtualSize || (codec == 0 && packed != size))
          throw new InvalidDataException("RDS5 chunk range is invalid.");
        chunks.Add(new Chunk(start, (int)packed, v, (int)size, codec, pad));
      }

      input.Position = etpf;
      byte[] eh = new byte[16];
      input.ReadExactly(eh);
      if (!eh.AsSpan(0, 4).SequenceEqual("ETPF"u8) || U32(eh, 4) != 634) throw new InvalidDataException("Unsupported ETPF catalog version.");
      int textures = checked((int)U32(eh, 8)), area = checked((int)U32(eh, 12));
      long metadataBytes = checked(16L + area + textures * 32L), paddedMetadata = Align(metadataBytes, 4096);
      uint alignmentWord = U32(header, 20);
      long sourcePadding = (alignmentWord >> 28) * 256L;
      if (textures < 1 || textures > MaxTextures || area < textures * 16L || metadataBytes > MaxIndexBytes ||
          (alignmentWord & 0x0FFFFFFF) != 0 || paddedMetadata != chunks[0].Virtual ||
          checked(etpf + paddedMetadata + sourcePadding) != chunks[0].Source)
        throw new InvalidDataException("ETPF tables do not match the pool's data.");
      byte[] records = new byte[area];
      input.ReadExactly(records);
      byte[] ids = new byte[checked(textures * 32)];
      input.ReadExactly(ids);

      var list = new List<TtpfTexture>(textures);
      var layouts = new Layout?[textures];
      for (int i = 0; i < textures; i++)
      {
        int p = i * 16;
        uint a = U32(records, p), b = U32(records, p + 4), c = U32(records, p + 8), d = U32(records, p + 12);
        int nonTail = (int)(b & 7), packedMips = (int)(a >> 28), mode = (int)(b >> 23);
        int width = (int)(a & 0x3FFF) + 1, height = (int)((a >> 14) & 0x3FFF) + 1, mips = packedMips + nonTail,
            format = (int)((b >> 3) & 0x7F), depth = (int)((b >> 10) & 0x7FF) + 1, dimension = (int)((b >> 21) & 3);
        long baseOffset = (long)(c & 0x1FFFFFUL) * 4096;
        string id = Convert.ToHexString(ids.AsSpan(i * 32, 32));
        int keyAt = checked(textures * 16 + i * 8);
        ulong assetKey = keyAt + 8 <= records.Length ? U64(records, keyAt)
          : keyAt >= records.Length ? U64(ids, keyAt - records.Length) : 0;
        bool auxiliaryMapping = (c >> 21) != 0 || d != 0;
        string? limitation = null;
        Layout? layout = null;
        int exported = 0, baseMip = 0;
        long stored = 0, ddsSize = 0;
        try
        {
          Rpf9TextureDictionary.ElementBytes(format);  // a format the decoder knows
          if (baseOffset == 0 || (c & 0x1FFFFF) == 0x1FFFFF) throw new NotSupportedException("This texture has no data in this pool.");
          if (baseOffset < paddedMetadata || baseOffset >= virtualSize) throw new InvalidDataException("Texture lies outside the pool data.");
          long nextAllocation = virtualSize;
          for (int following = i + 1; following < textures; following++)
          {
            long candidate = (long)(U32(records, following * 16 + 8) & 0x1FFFFF) * 4096;
            if (candidate < baseOffset) throw new InvalidDataException("Texture allocations are not ordered.");
            if (candidate > baseOffset) { nextAllocation = candidate; break; }
          }
          long allocationSpan = nextAllocation - baseOffset;
          if (allocationSpan < 1 || allocationSpan > MaxOutput) throw new NotSupportedException("Texture exceeds the 256 MB limit.");
          if (dimension == 1 && depth == 1)
          {
            bool supportedMode = auxiliaryMapping ? mode is 2 or 4 or 5 or 6 or 7 or 36 or 38 : mode is 0 or 1 or 2 or 32 or 34;
            if (!supportedMode) throw new NotSupportedException("This mip storage layout is not supported yet.");
            layout = SurfaceLayout(width, height, format, mips, (mode & 6) == 0 ? 65536 : 4096);
            if (layout.Size != allocationSpan) throw new InvalidDataException("Texture size does not match its allocation.");
            if (auxiliaryMapping && !ValidateCompactStreamingChunks(chunks, baseOffset, layout.Size, mode, nonTail, c >> 21, d))
              throw new NotSupportedException("This streamed mip layout is not supported yet.");
          }
          else if (dimension == 1 && depth > 1)
          {
            if (mode != 1 || auxiliaryMapping) throw new NotSupportedException("This texture array layout is not supported yet.");
            layout = ArrayLayout(width, height, depth, format, mips);
            if (layout.Size != allocationSpan) throw new InvalidDataException("Texture array size does not match its allocation.");
          }
          else if (dimension == 2)
          {
            if (depth != 6 || width != height || nonTail > 3 || mode != 18 + 2 * nonTail)
              throw new NotSupportedException("This cube map layout is not supported yet.");
            var faceLayout = SurfaceLayout(width, height, format, mips, 4096);
            int facesBytes = checked(faceLayout.Size * depth);
            int allocBytes = checked((int)allocationSpan);
            if (!auxiliaryMapping || !ValidateCubemapStreamingChunks(chunks, baseOffset, allocBytes, nonTail, c >> 21, d, facesBytes, out int dataOffset))
              throw new NotSupportedException("This cube map layout is not supported yet.");
            layout = faceLayout with { Size = allocBytes, SliceBytes = faceLayout.Size, DataOffset = dataOffset };
          }
          else if (dimension == 3)
          {
            if (mode != 2 || auxiliaryMapping || depth < 2 || mips != 1)
              throw new NotSupportedException("This volume texture layout is not supported yet.");
            layout = VolumeLayout(width, height, depth, format);
            if (layout.Size != allocationSpan) throw new InvalidDataException("Volume texture size does not match its allocation.");
          }
          else throw new NotSupportedException("This texture layout is not supported yet.");

          var available = new bool[mips];
          var unsupportedCodecs = new SortedSet<int>();
          for (int mip = 0; mip < mips; mip++)
          {
            var level = layout.Mips[mip];
            int slices = layout.Volume ? Math.Max(1, depth >> mip) : depth;
            int step = layout.Volume ? layout.BlockDepth : 1;
            bool ok = true;
            for (int s = 0; s < slices; s += step)
              foreach (var chunk in ChunksFor(chunks, MipSliceOffset(layout, baseOffset, level, s), level.RegionSize))
                if (!IsDecodableCodec(chunk.Codec)) { ok = false; unsupportedCodecs.Add(chunk.Codec); }
            available[mip] = ok;
          }
          baseMip = Array.FindIndex(available, usable => usable);
          if (baseMip < 0)
            throw new NotSupportedException("Every mip level needs an Xbox BCPack codec that is not supported yet (" +
                                            string.Join("/", unsupportedCodecs.Select(BcpackDecoder.GetCodecName)) + ").");
          while (baseMip + exported < mips && available[baseMip + exported]) exported++;
          var used = new HashSet<Chunk>();
          ddsSize = 148;
          for (int mip = baseMip; mip < baseMip + exported; mip++)
          {
            var level = layout.Mips[mip];
            int slices = layout.Volume ? Math.Max(1, depth >> mip) : depth;
            ddsSize = checked(ddsSize + (long)LinearBytes(level, layout.ElementBytes) * slices);
            int step = layout.Volume ? layout.BlockDepth : 1;
            for (int s = 0; s < slices; s += step)
              foreach (var chunk in ChunksFor(chunks, MipSliceOffset(layout, baseOffset, level, s), level.RegionSize)) used.Add(chunk);
          }
          stored = used.Sum(x => (long)x.Packed);
          if (ddsSize > MaxOutput) throw new NotSupportedException("The DDS would exceed the 256 MB limit.");
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ArgumentException or OverflowException)
        {
          limitation = ex.Message;
          exported = baseMip = 0;
          stored = ddsSize = 0;
          layout = null;
        }
        layouts[i] = layout;
        string formatName = Rpf9TextureDictionary.FormatName(format);
        list.Add(new TtpfTexture
        {
          Index = i, Name = $"texture_{i:D6}_{id[..16]}", Width = width, Height = height, Depth = depth, Dimension = dimension,
          DxgiFormat = format, FormatName = formatName == "Unknown" ? $"DXGI {format}" : formatName,
          SourceMips = mips, BaseMipLevel = baseMip, Mips = exported, AssetKey = assetKey, Id = id,
          StoredSize = stored, DdsSize = ddsSize, Limitation = limitation, VirtualOffset = baseOffset,
        });
      }
      return new TtpfPool(path, fileSize, chunks, list, layouts);
    }

    // ── reading ─────────────────────────────────────────────────────────

    private (TtpfTexture Info, Layout Layout) Select(int index)
    {
      var t = Textures[index];
      var layout = _layouts[index];
      if (!t.CanExtract || layout == null) throw new NotSupportedException(t.Limitation ?? "This texture can't be read.");
      return (t, layout);
    }

    private FileStream OpenInput()
    {
      var input = File.Open(_path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Write | FileShare.Delete);
      if (input.Length != _length)
      {
        input.Dispose();
        throw new IOException("The texture pool changed since it was opened.");
      }
      return input;
    }

    /// <summary>A DDS (DX10 header) with every decodable mip level and slice, detiled.</summary>
    public byte[] CreateDds(int index)
    {
      var (t, layout) = Select(index);
      using var input = OpenInput();
      var cache = new Dictionary<int, byte[]>();
      int exportWidth = Math.Max(1, t.Width >> t.BaseMipLevel), exportHeight = Math.Max(1, t.Height >> t.BaseMipLevel);
      int exportDepth = t.Dimension == 3 ? Math.Max(1, t.Depth >> t.BaseMipLevel) : t.Depth;
      using var output = new MemoryStream(checked((int)t.DdsSize));
      using var writer = new BinaryWriter(output, Encoding.UTF8, true);
      bool bc = Rpf9TextureDictionary.IsBc(t.DxgiFormat), volume = t.Dimension == 3, cube = t.Dimension == 2;
      writer.Write("DDS "u8); writer.Write(124); writer.Write((bc ? 0xA1007 : 0x2100F) | (volume ? 0x800000 : 0));
      writer.Write(exportHeight); writer.Write(exportWidth);
      writer.Write(bc ? LinearBytes(layout.Mips[t.BaseMipLevel], layout.ElementBytes) : exportWidth * layout.ElementBytes);
      writer.Write(volume ? exportDepth : 0); writer.Write(t.Mips); writer.Write(new byte[44]);
      writer.Write(32); writer.Write(4); writer.Write(0x30315844); writer.Write(new byte[20]);
      writer.Write((t.Mips > 1 ? 0x401008 : 0x1000) | (volume || cube || t.Depth > 1 ? 8 : 0));
      writer.Write(volume ? 0x200000 : cube ? 0xFE00 : 0); writer.Write(new byte[12]);
      writer.Write(t.DxgiFormat); writer.Write(volume ? 4 : 3); writer.Write(cube ? 4 : 0);
      writer.Write(volume ? 1 : cube ? t.Depth / 6 : t.Depth); writer.Write(0);
      if (volume)
      {
        for (int mip = t.BaseMipLevel; mip < t.BaseMipLevel + t.Mips; mip++)
          for (int z = 0; z < Math.Max(1, t.Depth >> mip); z++)
            writer.Write(ReadMip(input, t, layout, mip, z, cache));
      }
      else
      {
        for (int slice = 0; slice < t.Depth; slice++)
          for (int mip = t.BaseMipLevel; mip < t.BaseMipLevel + t.Mips; mip++)
            writer.Write(ReadMip(input, t, layout, mip, slice, cache));
      }
      writer.Flush();
      if (output.Length != t.DdsSize) throw new InvalidDataException("The DDS size disagrees with the catalog.");
      return output.ToArray();
    }

    private byte[] ReadMip(FileStream input, TtpfTexture t, Layout layout, int mip, int slice, Dictionary<int, byte[]> cache)
    {
      var m = layout.Mips[mip];
      long start = MipSliceOffset(layout, t.VirtualOffset, m, slice);
      byte[] tiled = new byte[m.RegionSize];
      foreach (var chunk in ChunksFor(_chunks, start, m.RegionSize))
      {
        int key = IndexOf(chunk);
        if (!cache.TryGetValue(key, out var decoded))
        {
          decoded = DecodeChunk(input, chunk);
          cache.Add(key, decoded);
        }
        long from = Math.Max(start, chunk.Virtual), to = Math.Min(start + m.RegionSize, chunk.Virtual + chunk.Size);
        decoded.AsSpan((int)(from - chunk.Virtual), (int)(to - from)).CopyTo(tiled.AsSpan((int)(from - start)));
      }
      byte[] linear = new byte[LinearBytes(m, layout.ElementBytes)];
      for (int y = 0; y < m.Height; y++)
        for (int x = 0; x < m.Width; x++)
        {
          int offset = TiledOffset(layout, m, x, y, slice);
          if (offset > tiled.Length - layout.ElementBytes) throw new InvalidDataException("Tiled address exceeds its source range.");
          tiled.AsSpan(offset, layout.ElementBytes).CopyTo(linear.AsSpan((y * m.Width + x) * layout.ElementBytes));
        }
      return linear;
    }

    private int IndexOf(Chunk chunk)
    {
      int lo = 0, hi = _chunks.Count - 1;
      while (lo <= hi)
      {
        int m = (lo + hi) / 2;
        if (_chunks[m].Source == chunk.Source) return m;
        if (_chunks[m].Source < chunk.Source) lo = m + 1; else hi = m - 1;
      }
      throw new InvalidDataException("Chunk is not part of the pool index.");
    }

    private static bool IsDecodableCodec(int codec) => codec is 0 or 8 || BcpackDecoder.IsSupportedCodec(codec);

    private static byte[] DecodeChunk(FileStream input, Chunk chunk)
    {
      if (!IsDecodableCodec(chunk.Codec))
        throw new NotSupportedException($"Xbox BCPack codec {chunk.Codec} ({BcpackDecoder.GetCodecName(chunk.Codec)}) is not supported.");
      byte[] packed = new byte[chunk.Packed];
      input.Position = chunk.Source;
      input.ReadExactly(packed);
      if (chunk.Codec == 0) return packed;
      if (BcpackDecoder.IsSupportedCodec(chunk.Codec))
        return BcpackDecoder.Decode(packed, chunk.Codec, chunk.Size, false, null, CancellationToken.None);
      if (packed.Length < 6) throw new InvalidDataException("Truncated zlib chunk.");
      if (packed.AsSpan().IndexOfAnyExcept((byte)0) < 0)
        throw new InvalidDataException($"Texture chunk at 0x{chunk.Source:X} is empty: its data is missing from this install.");
      using var data = new MemoryStream(packed, false);
      using var zlib = new ZLibStream(data, CompressionMode.Decompress);
      byte[] decoded = new byte[chunk.Size];
      int copied = 0;
      while (copied < decoded.Length)
      {
        int n = zlib.Read(decoded.AsSpan(copied, decoded.Length - copied));
        if (n == 0) throw new InvalidDataException("A zlib chunk is shorter than its size.");
        copied += n;
      }
      return decoded;
    }

    // ── layouts ─────────────────────────────────────────────────────────

    private static bool TryDecodeChunkPages(uint meta, uint streamFlags, Span<int> expectedPages, out int count)
    {
      count = 0;
      if (((streamFlags >> 9) & 0x3FF) != 0) return false;
      ulong packed = ((ulong)streamFlags << 11) | meta;
      bool ended = false;
      for (int k = 0; k < 4; k++)
      {
        int code = (int)((packed >> (5 * k)) & 0x1F);
        if (code == 0) { ended = true; continue; }
        if (ended) return false;
        int pages = ChunkCodePages(code);
        if (pages <= 0) return false;
        expectedPages[count++] = pages;
      }
      return count > 0;
    }

    private static bool TryValidateChunkSizes(IReadOnlyList<Chunk> chunks, long baseOffset, int allocationSize,
                                              ReadOnlySpan<int> expectedPages, out List<Chunk> mapped)
    {
      mapped = ChunksFor(chunks, baseOffset, allocationSize);
      if (mapped.Count != expectedPages.Length || mapped[0].Virtual != baseOffset) return false;
      long totalBytes = 0;
      for (int k = 0; k < expectedPages.Length; k++)
      {
        int expectedBytes = checked(expectedPages[k] * 4096);
        if (mapped[k].Size != expectedBytes) return false;
        totalBytes += expectedBytes;
      }
      return totalBytes == allocationSize;
    }

    private static bool ValidateCompactStreamingChunks(IReadOnlyList<Chunk> chunks, long baseOffset, int allocationSize,
                                                       int mode, int nonTail, uint meta, uint streamFlags)
    {
      Span<int> expectedPages = stackalloc int[4];
      if (!TryDecodeChunkPages(meta, streamFlags, expectedPages, out int count)) return false;
      int alignGroup = (int)((streamFlags >> 19) & 3);
      int padPages = (int)(streamFlags >> 21);
      if (nonTail == 1)
      {
        if (mode is not (4 or 5 or 36) || alignGroup != 0 || padPages != 0) return false;
      }
      else if (nonTail == 3)
      {
        bool alignOk = alignGroup switch
        {
          1 => mode is 2 or 4 && count == 3 && expectedPages[0] + padPages == 16,
          2 => mode is 4 or 5 or 36 && count >= 2 && expectedPages[0] + expectedPages[1] + padPages == 16,
          3 => mode is 6 or 7 or 38 && count >= 2 && expectedPages[0] + expectedPages[1] + padPages == 8,
          _ => false,
        };
        if (!alignOk) return false;
      }
      else return false;
      return TryValidateChunkSizes(chunks, baseOffset, allocationSize, expectedPages[..count], out _);
    }

    private static bool ValidateCubemapStreamingChunks(IReadOnlyList<Chunk> chunks, long baseOffset, int allocationSize,
                                                       int nonTail, uint meta, uint streamFlags, int facesBytes, out int dataOffset)
    {
      dataOffset = 0;
      if ((streamFlags >> 9) != 0) return false;
      Span<int> expectedPages = stackalloc int[4];
      if (!TryDecodeChunkPages(meta, streamFlags, expectedPages, out int count) || count != nonTail + 1) return false;
      if (!TryValidateChunkSizes(chunks, baseOffset, allocationSize, expectedPages[..count], out var mapped)) return false;
      int expectedLastSize = nonTail > 0 && facesBytes >= 36 * 4096 ? checked((int)Align(facesBytes, 65536)) : facesBytes;
      if (mapped[^1].Size != expectedLastSize) return false;
      dataOffset = checked((int)(mapped[^1].Virtual - baseOffset));
      return true;
    }

    private static int ChunkCodePages(int code) => code switch
    {
      >= 1 and <= 8 => code,
      10 => 12, 11 => 16, 12 => 24, 13 => 32, 14 => 48, 15 => 64, 16 => 128,
      21 => 18, 22 => 80, 23 => 96, 24 => 144, 26 => 272, 27 => 384, 28 => 528,
      _ => 0,
    };

    private static List<Chunk> ChunksFor(IReadOnlyList<Chunk> chunks, long start, int count)
    {
      long end = checked(start + count), covered = start;
      var result = new List<Chunk>();
      int lo = 0, hi = chunks.Count;
      while (lo < hi)
      {
        int mid = (lo + hi) / 2;
        if (chunks[mid].Virtual + chunks[mid].Size <= start) lo = mid + 1; else hi = mid;
      }
      for (int i = lo; i < chunks.Count && chunks[i].Virtual < end; i++)
      {
        var c = chunks[i];
        if (c.Virtual > covered) throw new InvalidDataException("Texture crosses an unmapped range.");
        result.Add(c);
        covered = Math.Min(end, c.Virtual + c.Size);
      }
      if (covered != end) throw new InvalidDataException("Texture range is not completely mapped.");
      return result;
    }

    private static long MipSliceOffset(Layout layout, long baseOffset, Mip m, int slice) =>
      layout.Volume ? checked(baseOffset + (long)(slice / layout.BlockDepth) * layout.ZBlockStride + m.Offset)
      : layout.MipMajorSlices ? checked(baseOffset + m.Offset + (long)slice * m.RegionSize)
      : checked(baseOffset + layout.DataOffset + (long)slice * layout.SliceBytes + m.Offset);

    private static int TiledOffset(Layout layout, Mip mip, int x, int y, int slice = 0)
    {
      int offset = checked(((y / layout.BlockHeight) * (mip.PaddedWidth / layout.BlockWidth) + x / layout.BlockWidth) * layout.TileBytes);
      var pattern = (layout.Volume ? VolumePatterns : layout.TileBytes == 4096 ? FourKbPatterns : Patterns)[Log(layout.ElementBytes)];
      for (int bit = 0; bit < pattern.Length; bit++)
      {
        int s = pattern[bit];
        if (s >= 101) offset |= ((slice >> (s - 101)) & 1) << bit;
        else if (s > 0) offset |= (((x + mip.TailX) >> (s - 1)) & 1) << bit;
        else if (s < 0) offset |= (((y + mip.TailY) >> (-s - 1)) & 1) << bit;
      }
      return offset;
    }

    private static Layout ArrayLayout(int w, int h, int depth, int format, int mips)
    {
      var slice = SurfaceLayout(w, h, format, mips, 65536);
      var ms = slice.Mips.ToArray();
      int off = slice.FirstTail < mips ? checked(depth * 65536) : 0;
      for (int i = slice.FirstTail - 1; i >= 0; i--)
      {
        ms[i] = ms[i] with { Offset = off };
        off = checked(off + depth * ms[i].RegionSize);
      }
      if (off != checked(slice.Size * depth) || off > MaxOutput) throw new NotSupportedException("Texture exceeds the 256 MB limit.");
      return slice with { Size = off, Mips = ms, MipMajorSlices = true };
    }

    private static Layout VolumeLayout(int w, int h, int depth, int format)
    {
      int eb = Rpf9TextureDictionary.ElementBytes(format), log = Log(eb), px = Rpf9TextureDictionary.IsBc(format) ? 4 : 1;
      (int bw, int bh, int bd) = log switch { 0 => (16, 16, 16), 1 => (8, 16, 16), 2 => (8, 16, 8), 3 => (8, 8, 8), _ => (4, 8, 8) };
      int ew = (w + px - 1) / px, eh = (h + px - 1) / px;
      int pw = checked((int)Align(ew, bw)), ph = checked((int)Align(eh, bh));
      int zBlockStride = checked(pw * ph * eb * bd);
      int zBlocks = checked((int)Align(depth, bd)) / bd;
      int sizeBytes = checked(zBlockStride * zBlocks);
      if (sizeBytes < 1 || sizeBytes > MaxOutput) throw new NotSupportedException("Texture exceeds the 256 MB limit.");
      return new Layout(sizeBytes, bw, bh, 1, eb, new[] { new Mip(ew, eh, pw, 0, zBlockStride, 0, 0) })
      { TileBytes = 4096, Volume = true, BlockDepth = bd, ZBlockStride = zBlockStride };
    }

    private static Layout SurfaceLayout(int w, int h, int format, int mips, int tileBytes = 65536)
    {
      int levels = 1;
      for (int v = Math.Max(w, h); v > 1; v >>= 1) levels++;
      if (mips < 1 || mips > levels) throw new InvalidDataException("Invalid mip count.");
      int eb = Rpf9TextureDictionary.ElementBytes(format), log = Log(eb), px = Rpf9TextureDictionary.IsBc(format) ? 4 : 1;
      int halfBlockBits = tileBytes == 4096 ? 6 : 8, maximumTailLevels = tileBytes == 4096 ? 8 : 12;
      int bw = 1 << (halfBlockBits - log / 2), bh = 1 << (halfBlockBits - (log + 1) / 2);
      (int mw, int mh) = log switch { 0 => (16, 16), 1 => (16, 8), 2 => (8, 8), 3 => (8, 4), _ => (4, 4) };
      int ew = (w + px - 1) / px, eh = (h + px - 1) / px, tail = mips;
      var sizes = new List<int>();
      var ms = new List<Mip>();
      for (int i = 0; i < mips; i++)
      {
        int wc = Math.Max(1, (ew + (1 << i) - 1) >> i), hc = Math.Max(1, (eh + (1 << i) - 1) >> i);
        if (mips > 1 && wc <= bw / 2 && hc <= bh && mips - i <= maximumTailLevels) { tail = i; break; }
        int pw = (int)Align(wc, bw), ph = (int)Align(hc, bh), size = checked(pw * ph * eb);
        sizes.Add(size);
        ms.Add(new Mip(Elements(w, i, px), Elements(h, i, px), pw, 0, size, 0, 0));
      }
      int off = tail < mips ? tileBytes : 0;
      for (int i = tail - 1; i >= 0; i--) { ms[i] = ms[i] with { Offset = off }; off = checked(off + sizes[i]); }
      for (int i = tail; i < mips; i++)
      {
        int entry = maximumTailLevels - 1 - (i - tail), moff = entry > 6 ? 16 << entry : entry << 8, x = 0, y = 0;
        for (int bit = 0; bit < 6; bit++) { x |= ((moff >> (9 + 2 * bit)) & 1) << bit; y |= ((moff >> (8 + 2 * bit)) & 1) << bit; }
        ms.Add(new Mip(Elements(w, i, px), Elements(h, i, px), bw, 0, tileBytes, x * mw, y * mh));
      }
      if (off < 1 || off > MaxOutput) throw new NotSupportedException("Texture exceeds the 256 MB limit.");
      return new Layout(off, bw, bh, tail, eb, ms) { TileBytes = tileBytes };
    }

    private static int LinearBytes(Mip mip, int eb) => checked(mip.Width * mip.Height * eb);
    private static int Elements(int pixels, int mip, int px) => (Math.Max(1, pixels >> mip) + px - 1) / px;
    private static int Log(int eb) => eb switch { 1 => 0, 2 => 1, 4 => 2, 8 => 3, 16 => 4, _ => throw new InvalidDataException("Invalid element size.") };
    private static long Align(long value, int boundary) => checked((value + boundary - 1) / boundary * boundary);
    private static long SourceOffset(ulong value) => (long)(value & 0x3FFFFFF) * 256;
    private static long VirtualOffset(ulong value) => (long)((value >> 30) & 0x3FFFFF) * 4096;
    private static uint U32(ReadOnlySpan<byte> data, int at) => BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(at, 4));
    private static ulong U64(ReadOnlySpan<byte> data, int at) => BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(at, 8));
  }
}
