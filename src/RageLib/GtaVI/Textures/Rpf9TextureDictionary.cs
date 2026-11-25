/**********************************************************************\

 RageLib - SparkIV
 GTA VI (Scarlett) texture dictionaries.

 Ported from rpf9-explorer's RageTextureDecoder: the dictionary layout, the
 AMD tiled mip layout of the graphics section, and the pixel decoding.

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
using System.Linq;
using System.Text;
using BCnEncoder.Decoder;
using BCnEncoder.Shared;

namespace RageLib.GtaVI.Textures
{
  /// <summary>One texture of a GTA VI texture dictionary.</summary>
  public sealed class Rpf9Texture
  {
    public int Index { get; init; }
    public string Name { get; init; }
    public uint NameHash { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int Depth { get; init; } = 1;
    /// <summary>1 = 2D (or 2D array), 2 = cube (array), 3 = volume.</summary>
    public int Dimension { get; init; } = 1;
    public int DxgiFormat { get; init; }
    public string FormatName { get; init; }
    public int MipCount { get; init; }
    internal int GraphicsOffset { get; init; }
    internal int AllocationBytes { get; init; }

    public int GetWidth(int level) => Math.Max(1, Width >> level);
    public int GetHeight(int level) => Math.Max(1, Height >> level);
  }

  /// <summary>
  /// A GTA VI texture dictionary: the textures described in the resource's system
  /// section, their pixels in the graphics section (AMD tiled layout).
  /// </summary>
  public sealed class Rpf9TextureDictionary
  {
    public const int MaxSystemBytes = 16 * 1024 * 1024;
    public const int MaxGraphicsBytes = 256 * 1024 * 1024;
    private const int MaxDecodePixels = 64 * 1024 * 1024;

    private readonly byte[] _graphics;

    private Rpf9TextureDictionary(byte[] graphics, IReadOnlyList<Rpf9Texture> textures)
    {
      _graphics = graphics;
      Textures = textures;
    }

    public IReadOnlyList<Rpf9Texture> Textures { get; }

    /// <summary>Opens a dictionary from a resource's two sections, or returns null if it is none.</summary>
    public static Rpf9TextureDictionary TryOpen(ReadOnlySpan<byte> system, ReadOnlySpan<byte> graphics)
    {
      try
      {
        var textures = Parse(system, graphics.Length);
        return new Rpf9TextureDictionary(graphics.ToArray(), textures);
      }
      catch (Exception ex) when (ex is InvalidDataException or ArgumentException or OverflowException or DecoderFallbackException)
      {
        return null;
      }
    }

    /// <summary>Whether a system section starts with a texture dictionary.</summary>
    public static bool LooksLikeDictionary(ReadOnlySpan<byte> system)
    {
      if (system.Length < 64) return false;
      int count = BinaryPrimitives.ReadUInt16LittleEndian(system.Slice(0x38));
      return count is > 0 and <= 4096 && BinaryPrimitives.ReadUInt16LittleEndian(system.Slice(0x3A)) >= count &&
             BinaryPrimitives.ReadUInt16LittleEndian(system.Slice(0x28)) == count &&
             IsSystemPointer(BinaryPrimitives.ReadUInt64LittleEndian(system.Slice(0x20))) &&
             IsSystemPointer(BinaryPrimitives.ReadUInt64LittleEndian(system.Slice(0x30)));
    }

    /// <summary>Decodes one mip level (and slice) to 32-bit BGRA, top row first.</summary>
    public byte[] DecodeBgra(int index, int level, int slice = 0)
    {
      var info = Textures[index];
      if ((uint)level >= info.MipCount) throw new ArgumentOutOfRangeException(nameof(level));
      if ((uint)slice >= info.Depth) throw new ArgumentOutOfRangeException(nameof(slice));
      int width = info.GetWidth(level), height = info.GetHeight(level);
      if ((long)width * height > MaxDecodePixels)
        throw new NotSupportedException("This mip level is too large to decode; use a smaller level or export as DDS.");
      int eb = ElementBytes(info.DxgiFormat);
      var layout = TextureLayout(info);
      ReadOnlySpan<byte> allocation = _graphics.AsSpan(info.GraphicsOffset, info.AllocationBytes);
      if (info.Dimension != 3) allocation = allocation.Slice(checked(slice * layout.Size), layout.Size);
      int sourceSlice = info.Dimension == 3 ? Math.Min(Math.Max(1, info.Depth >> level) - 1, slice >> level) : 0;
      byte[] linear = Detile(allocation, layout, eb, level, sourceSlice);
      return DecodeLinear(linear, width, height, info.DxgiFormat);
    }

    /// <summary>A standard DDS (DX10 header) with every mip level and slice, detiled.</summary>
    public byte[] CreateDds(int index)
    {
      var info = Textures[index];
      int eb = ElementBytes(info.DxgiFormat);
      var layout = TextureLayout(info);
      using var output = new MemoryStream();
      using var writer = new BinaryWriter(output, Encoding.UTF8, true);
      bool volume = info.Dimension == 3, cube = info.Dimension == 2;
      writer.Write("DDS "u8); writer.Write(124); writer.Write((IsBc(info.DxgiFormat) ? 0x000A1007 : 0x0002100F) | (volume ? 0x800000 : 0));
      writer.Write(info.Height); writer.Write(info.Width);
      writer.Write(IsBc(info.DxgiFormat) ? layout.Mips[0].Width * layout.Mips[0].Height * eb : info.Width * eb);
      writer.Write(volume ? info.Depth : 0); writer.Write(info.MipCount); writer.Write(new byte[44]);
      writer.Write(32); writer.Write(4); writer.Write(0x30315844); writer.Write(new byte[20]);
      writer.Write((info.MipCount > 1 ? 0x401008 : 0x1000) | (volume || cube || info.Depth > 1 ? 8 : 0));
      writer.Write(volume ? 0x200000 : cube ? 0xFE00 : 0); writer.Write(new byte[12]);
      writer.Write(info.DxgiFormat); writer.Write(volume ? 4 : 3); writer.Write(cube ? 4 : 0);
      writer.Write(volume ? 1 : cube ? info.Depth / 6 : info.Depth); writer.Write(0);
      ReadOnlySpan<byte> allocation = _graphics.AsSpan(info.GraphicsOffset, info.AllocationBytes);
      if (volume)
      {
        for (int mip = 0; mip < info.MipCount; mip++)
          for (int z = 0; z < Math.Max(1, info.Depth >> mip); z++)
            writer.Write(Detile(allocation, layout, eb, mip, z));
      }
      else
      {
        for (int s = 0; s < info.Depth; s++)
          for (int mip = 0; mip < info.MipCount; mip++)
            writer.Write(Detile(allocation.Slice(s * layout.Size, layout.Size), layout, eb, mip));
      }
      writer.Flush();
      return output.ToArray();
    }

    // ── layout ──────────────────────────────────────────────────────────

    private static readonly int[] Pattern8 = { 0, 0, 0, 1, -1, -2, 2, 3, -3, 4, -4, 5 };
    private static readonly int[] Pattern16 = { 0, 0, 0, 0, -1, -2, 1, 2, -3, 3, -4, 4 };
    private static readonly int[] Pattern1 = { 1, 2, 3, 4, -1, -2, -3, -4, -5, 5, -6, 6 };
    private static readonly int[] Pattern2 = { 0, 1, 2, 3, -1, -2, -3, 4, -4, 5, -5, 6 };
    private static readonly int[] Pattern4 = { 0, 0, 1, 2, -1, -2, -3, 3, -4, 4, -5, 5 };
    private static readonly int[][] VolumePatterns =
    {
      new[] { 1, 2, 101, -1, 102, -2, 3, 103, -3, 4, 104, -4 },
      new[] { 0, 1, 101, -1, 102, -2, 2, 103, -3, 3, 104, -4 },
      new[] { 0, 0, 1, -1, 101, -2, 2, 102, -3, 3, 103, -4 },
      new[] { 0, 0, 0, 1, 101, -1, 2, 102, -2, 3, 103, -3 },
      new[] { 0, 0, 0, 0, 101, -1, 1, 102, -2, 2, 103, -3 },
    };

    private sealed record Mip(int Width, int Height, int PaddedWidth, int Offset, int TailX, int TailY);

    private sealed record Layout(int Size, int BlockWidth, int BlockHeight, IReadOnlyList<Mip> Mips)
    {
      public int BlockDepth { get; init; } = 1;
      public int ZBlockStride { get; init; }
      public bool Volume { get; init; }
    }

    private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

    private static IReadOnlyList<Rpf9Texture> Parse(ReadOnlySpan<byte> system, int graphicsLength)
    {
      if (system.Length > MaxSystemBytes || graphicsLength > MaxGraphicsBytes || graphicsLength < 0 || !LooksLikeDictionary(system))
        throw new InvalidDataException("Not a supported texture dictionary.");
      int count = U16(system, 0x38);
      int hashes = Pointer(system, 0x20), pointers = Pointer(system, 0x30);
      Check(hashes, checked(count * 4), system.Length);
      Check(pointers, checked(count * 8), system.Length);
      var result = new List<Rpf9Texture>(count);
      var ranges = new List<(int Start, int End)>();
      for (int i = 0; i < count; i++)
      {
        int t = Pointer(system, pointers + i * 8);
        Check(t, 0x70, system.Length);
        int descriptor = Pointer(system, t + 0x30);
        Check(descriptor, 8, system.Length);
        string name;
        if (U64(system, descriptor) == 0)
        {
          int nameStart = checked(descriptor + 8);
          Check(nameStart, 1, system.Length);
          var nameSpan = system.Slice(nameStart, Math.Min(1024, system.Length - nameStart));
          int end = nameSpan.IndexOf((byte)0);
          if (end <= 0) throw new InvalidDataException("Invalid texture name.");
          name = StrictUtf8.GetString(nameSpan[..end]);
          if (name.Any(char.IsControl)) throw new InvalidDataException("Invalid texture name characters.");
        }
        else
        {
          // A texture known only by a 64-bit asset key.
          Check(descriptor, 16, system.Length);
          ulong marker = U64(system, descriptor), key = U64(system, descriptor + 8);
          if (descriptor < 16 || marker != 0x50000000UL + (ulong)descriptor - 11 || key == 0 ||
              U32(system, descriptor - 16) != 6 || U32(system, descriptor - 12) != 0 || U32(system, descriptor - 4) != 0)
            throw new InvalidDataException("Unsupported keyed texture descriptor.");
          name = $"texture_{key:X16}";
        }
        int width = U16(system, t + 0x38), height = U16(system, t + 0x3A);
        int format = system[t + 0x3F], eb = ElementBytes(format), mips = system[t + 0x42];
        int depth = U16(system, t + 0x3C), dimension = system[t + 0x3E];
        if (width is < 1 or > 16384 || height is < 1 or > 16384 || depth is < 1 or > 2048 ||
            dimension is < 1 or > 3 || (dimension == 2 && depth % 6 != 0) ||
            system[t + 0x40] != 5 || U16(system, t + 0x0C) != eb ||
            U64(system, t + 0x28) != 0x50000000UL + (ulong)t + 0x50)
          throw new InvalidDataException("Unsupported texture descriptor.");
        ulong gfxPointer = U64(system, t + 0x18);
        if (gfxPointer < 0x60000000UL || gfxPointer - 0x60000000UL > int.MaxValue)
          throw new InvalidDataException("Invalid graphics pointer.");
        int gfx = (int)(gfxPointer - 0x60000000UL), length = checked((int)U32(system, t + 8) * eb);
        Check(gfx, length, graphicsLength);
        var info = new Rpf9Texture
        {
          Index = i, Name = name, NameHash = U32(system, hashes + i * 4),
          Width = width, Height = height, Depth = depth, Dimension = dimension,
          DxgiFormat = format, FormatName = FormatName(format), MipCount = mips,
          GraphicsOffset = gfx, AllocationBytes = length,
        };
        var layout = TextureLayout(info);
        if (checked(layout.Size * (dimension == 3 ? 1 : depth)) != length)
          throw new InvalidDataException("Descriptor allocation disagrees with the tiled mip layout.");
        if (ranges.Any(r => gfx < r.End && r.Start < gfx + length))
          throw new InvalidDataException("Overlapping texture allocations.");
        ranges.Add((gfx, gfx + length));
        result.Add(info);
      }
      return result;
    }

    private static Layout TextureLayout(Rpf9Texture info) => ComputeLayout(info.Width, info.Height,
      ElementBytes(info.DxgiFormat), info.MipCount, IsBc(info.DxgiFormat) ? 4 : 1, info.Depth, info.Dimension == 3);

    private static Layout ComputeLayout(int w, int h, int eb, int mips, int pixelsPerElement, int depth, bool volume)
    {
      int maxMips = 1;
      for (int size = Math.Max(Math.Max(w, h), volume ? depth : 1); size > 1; size >>= 1) maxMips++;
      if (mips < 1 || mips > maxMips) throw new InvalidDataException("Invalid mip count.");
      int log = ElementLog2(eb);
      (int bw, int bh, int bd) = volume
        ? log switch { 0 => (16, 16, 16), 1 => (8, 16, 16), 2 => (8, 16, 8), 3 => (8, 8, 8), _ => (4, 8, 8) }
        : log switch { 0 => (64, 64, 1), 1 => (64, 32, 1), 2 => (32, 32, 1), 3 => (32, 16, 1), _ => (16, 16, 1) };
      (int mw, int mh) = volume
        ? log switch { 0 => (8, 4), 1 or 2 => (4, 4), 3 => (4, 2), _ => (2, 2) }
        : log switch { 0 => (16, 16), 1 => (16, 8), 2 => (8, 8), 3 => (8, 4), _ => (4, 4) };
      int ew = (w + pixelsPerElement - 1) / pixelsPerElement, eh = (h + pixelsPerElement - 1) / pixelsPerElement, tail = mips;
      int tailW = volume ? bw : bw / 2, tailH = volume ? bh / 2 : bh, maxTail = volume ? 5 : 8;
      var layouts = new List<Mip>();
      var sizes = new List<int>();
      for (int m = 0; m < mips; m++)
      {
        int wc = Math.Max(1, (ew + (1 << m) - 1) >> m), hc = Math.Max(1, (eh + (1 << m) - 1) >> m);
        if (mips > 1 && wc <= tailW && hc <= tailH && mips - m <= maxTail) { tail = m; break; }
        int pw = Align(wc, bw), ph = Align(hc, bh);
        sizes.Add(checked(pw * ph * eb));
        layouts.Add(new Mip(Elements(w, m, pixelsPerElement), Elements(h, m, pixelsPerElement), pw, 0, 0, 0));
      }
      int off = tail < mips ? 4096 : 0;
      for (int m = tail - 1; m >= 0; m--) { layouts[m] = layouts[m] with { Offset = off }; off = checked(off + sizes[m] * bd); }
      for (int m = tail; m < mips; m++)
      {
        int entry = maxTail - 1 - (m - tail), moff = entry > 6 ? 16 << entry : entry << 8, mx = 0, my = 0;
        for (int bit = 0; bit < 6; bit++) { mx |= ((moff >> (9 + 2 * bit)) & 1) << bit; my |= ((moff >> (8 + 2 * bit)) & 1) << bit; }
        layouts.Add(new Mip(Elements(w, m, pixelsPerElement), Elements(h, m, pixelsPerElement), bw, 0, mx * mw, my * mh));
      }
      int sizeBytes = volume ? checked(off / bd * Align(depth, bd)) : off;
      if (sizeBytes > MaxGraphicsBytes) throw new InvalidDataException("Texture layout exceeds the limit.");
      return new Layout(sizeBytes, bw, bh, layouts) { Volume = volume, BlockDepth = bd, ZBlockStride = volume ? off : 0 };
    }

    private static byte[] Detile(ReadOnlySpan<byte> allocation, Layout layout, int eb, int level, int slice = 0)
    {
      var mip = layout.Mips[level];
      byte[] linear = new byte[checked(mip.Width * mip.Height * eb)];
      for (int y = 0; y < mip.Height; y++)
        for (int x = 0; x < mip.Width; x++)
        {
          int offset = TiledOffset(layout, mip, eb, x, y, slice);
          Check(offset, eb, allocation.Length);
          allocation.Slice(offset, eb).CopyTo(linear.AsSpan((y * mip.Width + x) * eb, eb));
        }
      return linear;
    }

    private static int TiledOffset(Layout layout, Mip mip, int eb, int x, int y, int slice)
    {
      int[] pattern = layout.Volume ? VolumePatterns[ElementLog2(eb)]
        : eb switch { 1 => Pattern1, 2 => Pattern2, 4 => Pattern4, 8 => Pattern8, _ => Pattern16 };
      int block = (y / layout.BlockHeight) * (mip.PaddedWidth / layout.BlockWidth) + x / layout.BlockWidth;
      int address = 0, tx = x + mip.TailX, ty = y + mip.TailY;
      for (int bit = 0; bit < pattern.Length; bit++)
      {
        int s = pattern[bit];
        if (s >= 101) address |= ((slice >> (s - 101)) & 1) << bit;
        else if (s > 0) address |= ((tx >> (s - 1)) & 1) << bit;
        else if (s < 0) address |= ((ty >> (-s - 1)) & 1) << bit;
      }
      return checked((slice / layout.BlockDepth) * layout.ZBlockStride + mip.Offset + block * 4096 + address);
    }

    // ── pixels ──────────────────────────────────────────────────────────

    /// <summary>Decodes one linear (detiled) mip level to 32-bit BGRA, top row first.</summary>
    public static byte[] DecodeLinear(byte[] linear, int width, int height, int dxgiFormat)
    {
      if (!IsBc(dxgiFormat)) return DecodeRawPixels(linear, dxgiFormat, width * height);
      var decoder = new BcDecoder();
      if (dxgiFormat is 95 or 96)
      {
        var hdr = decoder.DecodeRawHdr(linear, width, height, dxgiFormat == 95 ? CompressionFormat.Bc6U : CompressionFormat.Bc6S);
        byte[] mapped = new byte[checked(width * height * 4)];
        for (int i = 0; i < hdr.Length; i++)
        {
          mapped[i * 4] = Quantize(hdr[i].b); mapped[i * 4 + 1] = Quantize(hdr[i].g);
          mapped[i * 4 + 2] = Quantize(hdr[i].r); mapped[i * 4 + 3] = 255;
        }
        return mapped;
      }
      CompressionFormat format = dxgiFormat switch
      {
        71 or 72 => CompressionFormat.Bc1, 74 or 75 => CompressionFormat.Bc2,
        77 or 78 => CompressionFormat.Bc3, 80 or 81 => CompressionFormat.Bc4,
        83 or 84 => CompressionFormat.Bc5, 98 or 99 => CompressionFormat.Bc7,
        _ => throw new NotSupportedException("This BC format has no 8-bit decoder; export it as DDS."),
      };
      var pixels = decoder.DecodeRaw(linear, width, height, format);
      byte[] bgra = new byte[checked(width * height * 4)];
      for (int i = 0; i < pixels.Length; i++)
      {
        bgra[i * 4] = pixels[i].b; bgra[i * 4 + 1] = pixels[i].g;
        bgra[i * 4 + 2] = pixels[i].r; bgra[i * 4 + 3] = pixels[i].a;
      }
      return bgra;
    }

    private static byte[] DecodeRawPixels(ReadOnlySpan<byte> data, int format, int count)
    {
      int eb = ElementBytes(format);
      byte[] output = new byte[checked(count * 4)];
      for (int i = 0; i < count; i++)
      {
        int p = i * eb;
        byte r = 0, g = 0, b = 0, a = 255;
        switch (format)
        {
          case 28: case 29: r = data[p]; g = data[p + 1]; b = data[p + 2]; a = data[p + 3]; break;
          case 87: case 91: b = data[p]; g = data[p + 1]; r = data[p + 2]; a = data[p + 3]; break;
          case 88: case 93: b = data[p]; g = data[p + 1]; r = data[p + 2]; break;
          case 61: case 65: r = g = b = data[p]; break;
          case 49: r = data[p]; g = data[p + 1]; break;
          case 56: r = g = b = Quantize(U16(data, p) / 65535.0); break;
          case 54: r = g = b = Quantize((double)BitConverter.UInt16BitsToHalf(U16(data, p))); break;
          case 41: r = g = b = Quantize(BitConverter.Int32BitsToSingle((int)U32(data, p))); break;
          case 34:
            r = Quantize((double)BitConverter.UInt16BitsToHalf(U16(data, p)));
            g = Quantize((double)BitConverter.UInt16BitsToHalf(U16(data, p + 2)));
            break;
          case 10:
            r = Quantize((double)BitConverter.UInt16BitsToHalf(U16(data, p)));
            g = Quantize((double)BitConverter.UInt16BitsToHalf(U16(data, p + 2)));
            b = Quantize((double)BitConverter.UInt16BitsToHalf(U16(data, p + 4)));
            a = Quantize((double)BitConverter.UInt16BitsToHalf(U16(data, p + 6)));
            break;
          case 11:
            r = Quantize(U16(data, p) / 65535.0); g = Quantize(U16(data, p + 2) / 65535.0);
            b = Quantize(U16(data, p + 4) / 65535.0); a = Quantize(U16(data, p + 6) / 65535.0);
            break;
          case 24:
            uint packed = U32(data, p);
            r = Quantize((packed & 1023) / 1023.0); g = Quantize(((packed >> 10) & 1023) / 1023.0);
            b = Quantize(((packed >> 20) & 1023) / 1023.0); a = (byte)((packed >> 30) * 85);
            break;
          default: throw new NotSupportedException("This raw format has no pixel decoder.");
        }
        output[i * 4] = b; output[i * 4 + 1] = g; output[i * 4 + 2] = r; output[i * 4 + 3] = a;
      }
      return output;
    }

    // ── helpers ─────────────────────────────────────────────────────────

    private static int Elements(int pixels, int mip, int blockPixels) => (Math.Max(1, pixels >> mip) + blockPixels - 1) / blockPixels;
    private static int ElementLog2(int eb) => eb switch { 1 => 0, 2 => 1, 4 => 2, 8 => 3, 16 => 4, _ => throw new InvalidDataException("Invalid element size.") };
    public static bool IsBc(int format) => format is >= 70 and <= 84 or >= 94 and <= 99;
    private static int Align(int value, int boundary) => checked((value + boundary - 1) / boundary * boundary);

    public static int ElementBytes(int format) => format switch
    {
      61 or 65 => 1, 49 or 54 or 56 => 2, 24 or 28 or 29 or 34 or 41 or 87 or 88 or 91 or 93 => 4,
      10 or 11 or 71 or 72 or 80 or 81 => 8, 74 or 75 or 77 or 78 or 83 or 84 or 95 or 96 or 98 or 99 => 16,
      _ => throw new InvalidDataException("Unsupported texture format."),
    };

    public static string FormatName(int format) => format switch
    {
      10 => "R16G16B16A16_FLOAT", 11 => "R16G16B16A16_UNORM", 24 => "R10G10B10A2_UNORM", 28 => "R8G8B8A8_UNORM", 29 => "R8G8B8A8_UNORM_SRGB",
      34 => "R16G16_FLOAT", 41 => "R32_FLOAT", 49 => "R8G8_UNORM", 54 => "R16_FLOAT", 56 => "R16_UNORM",
      61 => "R8_UNORM", 65 => "A8_UNORM", 87 => "B8G8R8A8_UNORM", 88 => "B8G8R8X8_UNORM", 91 => "B8G8R8A8_UNORM_SRGB", 93 => "B8G8R8X8_UNORM_SRGB",
      71 => "BC1_UNORM", 72 => "BC1_UNORM_SRGB", 74 => "BC2_UNORM", 75 => "BC2_UNORM_SRGB",
      77 => "BC3_UNORM", 78 => "BC3_UNORM_SRGB", 80 => "BC4_UNORM", 81 => "BC4_SNORM",
      83 => "BC5_UNORM", 84 => "BC5_SNORM", 95 => "BC6H_UF16", 96 => "BC6H_SF16",
      98 => "BC7_UNORM", 99 => "BC7_UNORM_SRGB", _ => "Unknown",
    };

    private static byte Quantize(double value) => double.IsNaN(value) ? (byte)0 : (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);
    private static bool IsSystemPointer(ulong value) => value >= 0x50000000UL && value < 0x51000000UL;

    private static int Pointer(ReadOnlySpan<byte> data, int at)
    {
      ulong value = U64(data, at);
      if (!IsSystemPointer(value)) throw new InvalidDataException("Invalid system pointer.");
      int offset = checked((int)(value - 0x50000000UL));
      Check(offset, 1, data.Length);
      return offset;
    }

    private static void Check(int offset, int count, int length)
    {
      if (offset < 0 || count < 0 || offset > length || count > length - offset)
        throw new InvalidDataException("Resource range exceeds its section.");
    }

    private static ushort U16(ReadOnlySpan<byte> data, int at) { Check(at, 2, data.Length); return BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(at, 2)); }
    private static uint U32(ReadOnlySpan<byte> data, int at) { Check(at, 4, data.Length); return BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(at, 4)); }
    private static ulong U64(ReadOnlySpan<byte> data, int at) { Check(at, 8, data.Length); return BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(at, 8)); }
  }
}
