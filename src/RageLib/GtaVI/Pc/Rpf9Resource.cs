/**********************************************************************\

 RageLib - SparkIV
 GTA VI RPF9 paged resources and member types.

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
using ICSharpCode.SharpZipLib.Zip.Compression;

namespace RageLib.GtaVI.Pc
{
  /// <summary>
  /// The member types of an RPF9 archive. Each entry carries an extension code
  /// (entry byte 5, resource byte 16). Codes 0..18 are the base table; 64..115
  /// are the executable's extension table (the Scarlett "z" platform row). These
  /// are file-type names, the same kind of knowledge as ".wtd" for GTA IV.
  /// </summary>
  public static class Rpf9Extensions
  {
    private static readonly string[] Base =
    {
      ".rpf", ".zmf", ".zdr", ".zft", ".zdd", ".ztd", ".zbn", ".zbd", ".zpd", ".zbs",
      ".zsd", ".zmt", ".zsc", ".zcs", ".zdatb", ".zbdr", ".zbdd", ".zbft", ".zvert",
    };

    private static readonly string[] Extended =
    {
      ".mrf", ".cut", ".gfx", ".zcd", ".zld", ".zpmd", ".zpm", ".zed", ".zpt", ".zmap", ".ztyp", ".zch", ".zldb",
      ".zjd", ".zad", ".znv", ".znvs", ".zpl", ".znd", ".zvr", ".zwr", ".znh", ".zfd", ".zas", ".zsm", ".zscript",
      ".zascript", ".zsma", ".zasf", ".zfc", ".zlfc", ".zcg", ".zasd", ".rdb", ".zfbs", ".zcps", ".zsp", ".ztsl", ".zmmd",
      ".zfid", ".zmsk", ".zhdat", ".znmd", ".zsmm", ".zascd", ".zrd", ".zsmam", ".zldbmt", ".znmd", ".zkit", ".zcgc", ".zcov",
    };

    /// <summary>The extension for a code, or null if the code names no known type.</summary>
    public static string Name(byte code) =>
      code < Base.Length ? Base[code] : code >= 64 && code < 64 + Extended.Length ? Extended[code - 64] : null;

    /// <summary>Texture dictionary (".ztd").</summary>
    public const byte TextureDictionary = 5;
  }

  /// <summary>
  /// RPF9 paged resources. A resource member is a 16-byte header followed by a
  /// run of zlib "pages" (each starting on a 16-byte boundary) and possibly a
  /// tail of raw bytes; decoded and joined they are the system section followed by
  /// the graphics section (sizes from the resource record). Pointers inside use
  /// the usual RAGE bases: 0x50000000 for system, 0x60000000 for graphics.
  ///
  /// SparkIV serves a decoded resource in a small container so viewers can split
  /// it again: "RSC9", u32 extension code, u32 system size, u32 graphics size,
  /// then the system and graphics bytes.
  /// </summary>
  public static class Rpf9Resource
  {
    public const int HeaderSize = 16;
    public const uint Magic = 0x39435352; // "RSC9"
    public const ulong SystemBase = 0x50000000, GraphicsBase = 0x60000000;

    public static bool IsZlibHeader(byte cmf, byte flg) =>
      (cmf & 0x0F) == 8 && (cmf >> 4) <= 7 && ((cmf << 8) | flg) % 31 == 0 && (flg & 0x20) == 0;

    /// <summary>
    /// Decodes a stored resource member into system + graphics bytes, or null if
    /// the pages do not decode to the expected size.
    /// </summary>
    public static byte[] Unpack(byte[] stored, long systemSize, long graphicsSize)
    {
      long size = systemSize + graphicsSize;
      if (stored == null || size <= 0 || size > int.MaxValue)
        return null;

      int start;
      if (stored.Length > 18 && !IsZlibHeader(stored[0], stored[1]) && IsZlibHeader(stored[16], stored[17]))
        start = 16;
      else if (stored.Length > 2 && IsZlibHeader(stored[0], stored[1]))
        start = 0;
      else
      {
        // Stored without compression: header + data, or the data alone.
        if (stored.Length >= 16 + size && stored.AsSpan(0, 16).IndexOfAnyExcept((byte)0) < 0)
          return stored.AsSpan(16, (int)size).ToArray();
        return stored.Length >= size ? stored.AsSpan(0, (int)size).ToArray() : null;
      }

      var result = new byte[size];
      int output = 0, pos = start;
      var inflater = new Inflater(false);
      while (output < size && pos + 2 <= stored.Length && IsZlibHeader(stored[pos], stored[pos + 1]))
      {
        inflater.Reset();
        inflater.SetInput(stored, pos, stored.Length - pos);
        try
        {
          int n;
          do
          {
            n = inflater.Inflate(result, output, (int)size - output);
            output += n;
          } while (n > 0 && !inflater.IsFinished && output < size);
        }
        catch (Exception ex) when (ex is ICSharpCode.SharpZipLib.SharpZipBaseException or InvalidOperationException)
        {
          return null;
        }
        if (!inflater.IsFinished)
          break;
        // The next page (or a raw tail) starts on the next 16-byte boundary.
        int end = pos + ((stored.Length - pos) - inflater.RemainingInput);
        int aligned = (end + 15) & ~15;
        bool pageAtEnd = end + 2 <= stored.Length && IsZlibHeader(stored[end], stored[end + 1]);
        bool pageAligned = aligned + 2 <= stored.Length && IsZlibHeader(stored[aligned], stored[aligned + 1]);
        pos = pageAligned || !pageAtEnd ? aligned : end;
      }

      // The remaining bytes may follow the zlib pages uncompressed.
      long tail = size - output;
      if (tail > 0 && pos >= 0 && pos + tail <= stored.Length)
      {
        Array.Copy(stored, pos, result, output, tail);
        output += (int)tail;
      }
      return output == size ? result : null;
    }

    /// <summary>Wraps decoded system + graphics bytes in SparkIV's resource container.</summary>
    public static byte[] Wrap(byte[] body, byte extensionCode, long systemSize, long graphicsSize)
    {
      var data = new byte[HeaderSize + body.Length];
      BinaryPrimitives.WriteUInt32LittleEndian(data, Magic);
      BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), extensionCode);
      BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), (uint)systemSize);
      BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), (uint)graphicsSize);
      body.CopyTo(data, HeaderSize);
      return data;
    }

    /// <summary>Reads SparkIV's resource container: the extension code and the two sections.</summary>
    public static bool TryOpen(byte[] data, out byte extensionCode, out ReadOnlyMemory<byte> system, out ReadOnlyMemory<byte> graphics)
    {
      extensionCode = 0;
      system = graphics = ReadOnlyMemory<byte>.Empty;
      if (data == null || data.Length < HeaderSize || BinaryPrimitives.ReadUInt32LittleEndian(data) != Magic)
        return false;
      long sys = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8));
      long gfx = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12));
      if (HeaderSize + sys + gfx > data.Length)
        return false;
      extensionCode = (byte)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
      system = data.AsMemory(HeaderSize, (int)sys);
      graphics = data.AsMemory(HeaderSize + (int)sys, (int)gfx);
      return true;
    }
  }
}
