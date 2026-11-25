/**********************************************************************\

 RageLib - SparkIV
 DDS images (DX10 header and the common legacy formats), for any game.

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
using System.IO;
using RageLib.GtaVI.Textures;

namespace RageLib.Textures
{
  /// <summary>
  /// A DDS file: its size, DXGI format, mip levels and slices, and each level decoded to BGRA. Legacy
  /// headers (DXT1/3/5, ATI1/2, 32-bit RGBA) are mapped to their DXGI format.
  /// </summary>
  public sealed class DdsImage
  {
    private readonly byte[] _data;
    private readonly int _dataOffset;

    private DdsImage(byte[] data, int dataOffset)
    {
      _data = data;
      _dataOffset = dataOffset;
    }

    public int Width { get; private set; }
    public int Height { get; private set; }
    /// <summary>Volume depth, or the number of array slices / cube faces.</summary>
    public int Depth { get; private set; } = 1;
    public bool IsVolume { get; private set; }
    public bool IsCube { get; private set; }
    public int Mips { get; private set; } = 1;
    public int DxgiFormat { get; private set; }
    public string FormatName => Rpf9TextureDictionary.FormatName(DxgiFormat);

    public int GetWidth(int level) => Math.Max(1, Width >> level);
    public int GetHeight(int level) => Math.Max(1, Height >> level);

    public static bool IsDds(byte[] data) =>
      data != null && data.Length >= 128 && data[0] == 'D' && data[1] == 'D' && data[2] == 'S' && data[3] == ' ';

    public static DdsImage Open(byte[] data)
    {
      if (!IsDds(data)) throw new InvalidDataException("Not a DDS file.");
      int flags = I32(data, 8);
      var image = new DdsImage(data, 128)
      {
        Height = I32(data, 12),
        Width = I32(data, 16),
      };
      int depth = I32(data, 24);
      image.Mips = Math.Max(1, I32(data, 28));
      int pfFlags = I32(data, 80);
      uint fourCc = (uint)I32(data, 84);
      int caps2 = I32(data, 112);
      int dataOffset = 128;

      if ((pfFlags & 4) != 0 && fourCc == 0x30315844)  // "DX10"
      {
        if (data.Length < 148) throw new InvalidDataException("Truncated DDS DX10 header.");
        image.DxgiFormat = I32(data, 128);
        int dimension = I32(data, 132);
        int misc = I32(data, 136);
        int arraySize = Math.Max(1, I32(data, 140));
        image.IsVolume = dimension == 4;
        image.IsCube = (misc & 4) != 0;
        image.Depth = image.IsVolume ? Math.Max(1, depth) : arraySize * (image.IsCube ? 6 : 1);
        dataOffset = 148;
      }
      else
      {
        image.DxgiFormat = (pfFlags & 4) != 0 ? fourCc switch
        {
          0x31545844 => 71,  // DXT1
          0x32545844 or 0x33545844 => 74,  // DXT2/3
          0x34545844 or 0x35545844 => 77,  // DXT4/5
          0x31495441 or 0x55344342 => 80,  // ATI1 / BC4U
          0x32495441 or 0x55354342 => 83,  // ATI2 / BC5U
          _ => throw new NotSupportedException("This DDS format is not supported."),
        } : (pfFlags & 0x40) != 0 && I32(data, 88) == 32
          ? (I32(data, 92) == 0x00FF0000 ? 87 : 28)  // BGRA / RGBA
          : throw new NotSupportedException("This DDS format is not supported.");
        image.IsVolume = (caps2 & 0x200000) != 0 && (flags & 0x800000) != 0;
        image.IsCube = (caps2 & 0x200) != 0;
        image.Depth = image.IsVolume ? Math.Max(1, depth) : image.IsCube ? 6 : 1;
      }
      Rpf9TextureDictionary.ElementBytes(image.DxgiFormat);  // throws for formats with no decoder
      return new DdsImage(data, dataOffset)
      {
        Width = image.Width, Height = image.Height, Depth = image.Depth, IsVolume = image.IsVolume,
        IsCube = image.IsCube, Mips = image.Mips, DxgiFormat = image.DxgiFormat,
      };
    }

    private int LevelBytes(int level)
    {
      int eb = Rpf9TextureDictionary.ElementBytes(DxgiFormat);
      int w = GetWidth(level), h = GetHeight(level);
      return Rpf9TextureDictionary.IsBc(DxgiFormat) ? checked(((w + 3) / 4) * ((h + 3) / 4) * eb) : checked(w * h * eb);
    }

    /// <summary>One mip level (and slice) decoded to 32-bit BGRA, top row first.</summary>
    public byte[] DecodeBgra(int level, int slice = 0)
    {
      if ((uint)level >= Mips) throw new ArgumentOutOfRangeException(nameof(level));
      long offset = _dataOffset;
      if (IsVolume)
      {
        // Mip-major: every slice of level 0, then of level 1, ...
        for (int m = 0; m < level; m++) offset += (long)LevelBytes(m) * Math.Max(1, Depth >> m);
        offset += (long)LevelBytes(level) * Math.Min(slice, Math.Max(1, Depth >> level) - 1);
      }
      else
      {
        long sliceBytes = 0;
        for (int m = 0; m < Mips; m++) sliceBytes += LevelBytes(m);
        offset += sliceBytes * slice;
        for (int m = 0; m < level; m++) offset += LevelBytes(m);
      }
      int bytes = LevelBytes(level);
      if (offset + bytes > _data.Length) throw new InvalidDataException("The DDS is shorter than its header says.");
      byte[] linear = _data.AsSpan((int)offset, bytes).ToArray();
      return Rpf9TextureDictionary.DecodeLinear(linear, GetWidth(level), GetHeight(level), DxgiFormat);
    }

    private static int I32(byte[] data, int at) => BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at, 4));
  }
}
