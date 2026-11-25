/**********************************************************************\

 RageLib - SparkIV
 GTA VI shader databases ("SGD5").

 The layout follows VIctory's victory-game/src/sgd5.rs (reditec/VIctory).
 Little-endian; str = u8 length (with the NUL), the bytes, a NUL:

   file:    "SGD5", u64, u32 n, n x { u32 size, size bytes }  shared parameter blocks
            u32 size, size bytes                              string table
            6 x { u32 n, n x shader }                         one list per stage slot
            u32 n, n x effect
   shader:  str name, u8 kind, u32 n, n bytes record,
            u64 hash, 0x90 bytes, u32 m, m bytes
   record:  u32 0x000DDCE7, u32 n, u32 unpacked size, raw deflate (n - 12 bytes)
   effect:  str name, u32 size once loaded, 6 x { u32 n, n x u32 shader index },
            0x50 bytes, rasterizer/depth-stencil/blend/sampler tables, techniques,
            passes, ..., technique names (see Effect below)

 Each record unpacks to a standard DXBC container holding DXIL (shader model 6.6).
 A file whose tables don't parse falls back to finding the records by marker.

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

namespace RageLib.GtaVI.Shaders
{
  /// <summary>One shader object of a database: a packed DXBC/DXIL container.</summary>
  public sealed class Sgd5Shader
  {
    internal Sgd5Shader(byte[] file, int stage, int index, string name, byte kind, ulong hash, int packedOffset, int packedLength, int unpackedSize)
    {
      _file = file;
      Stage = stage;
      Index = index;
      Name = name;
      Kind = kind;
      Hash = hash;
      PackedOffset = packedOffset;
      PackedLength = packedLength;
      UnpackedSize = unpackedSize;
    }

    private readonly byte[] _file;

    /// <summary>Index into <see cref="Sgd5Database.Stages"/>.</summary>
    public int Stage { get; }
    public string StageName => Sgd5Database.Stages[Stage];
    /// <summary>Position in its stage's list.</summary>
    public int Index { get; }
    /// <summary>Like "PS_Blit_8b8e4721_Wrapped".</summary>
    public string Name { get; }
    /// <summary>Apparently the shader model the effect asks for, x 10 (0x32 = 5.0, 0x41 = 6.5).</summary>
    public byte Kind { get; }
    public string ModelName => Kind == 0 ? "" : $"{Kind / 10}.{Kind % 10}";
    /// <summary>The engine's identity of the shader (0 when found by marker).</summary>
    public ulong Hash { get; }
    public int PackedOffset { get; }
    public int PackedLength { get; }
    public int UnpackedSize { get; }

    /// <summary>The DXBC container (with DXIL), or null if the stream is corrupt.</summary>
    public byte[]? Unpack()
    {
      try
      {
        using var input = new MemoryStream(_file, PackedOffset, PackedLength, writable: false);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        var output = new byte[UnpackedSize];
        int read = 0;
        while (read < output.Length)
        {
          int n = deflate.Read(output, read, output.Length - read);
          if (n == 0) break;
          read += n;
        }
        return read == UnpackedSize ? output : null;
      }
      catch (InvalidDataException)
      {
        return null;
      }
    }
  }

  /// <summary>A pass of a technique: the shader of each stage slot it uses.</summary>
  public sealed class Sgd5Pass
  {
    /// <summary>Per stage slot, the shader (null: none).</summary>
    public Sgd5Shader?[] Shaders { get; } = new Sgd5Shader?[6];
    public bool SetsRasterizer { get; init; }
    public bool SetsDepthStencil { get; init; }
    public bool SetsBlend { get; init; }
    public byte? StencilRef { get; init; }
  }

  public sealed class Sgd5Technique
  {
    public string Name { get; init; } = "";
    public IReadOnlyList<Sgd5Pass> Passes { get; init; } = Array.Empty<Sgd5Pass>();
  }

  /// <summary>An effect: techniques whose passes combine the database's shaders.</summary>
  public sealed class Sgd5Effect
  {
    public string Name { get; init; } = "";
    public IReadOnlyList<Sgd5Technique> Techniques { get; init; } = Array.Empty<Sgd5Technique>();
    public int RasterizerStates { get; init; }
    public int DepthStencilStates { get; init; }
    public int BlendStates { get; init; }
  }

  /// <summary>A GTA VI shader database (SGD5): shader objects per stage slot, and effects.</summary>
  public sealed class Sgd5Database
  {
    public static readonly string[] Stages = { "vertex", "pixel", "geometry", "slot3", "slot4", "compute" };
    private const uint Marker = 0x000DDCE7;
    private const uint Shared = 0x80000000;
    private const int MaxUnpacked = 64 << 20;

    private Sgd5Database(IReadOnlyList<Sgd5Shader> shaders, IReadOnlyList<Sgd5Effect> effects, bool fromTables)
    {
      Shaders = shaders;
      Effects = effects;
      FromTables = fromTables;
    }

    public IReadOnlyList<Sgd5Shader> Shaders { get; }
    public IReadOnlyList<Sgd5Effect> Effects { get; }
    /// <summary>False when the tables did not parse and the shaders were found by their marker.</summary>
    public bool FromTables { get; }

    public static bool IsSgd5(ReadOnlySpan<byte> data) => data.Length >= 4 && data[..4].SequenceEqual("SGD5"u8);

    public static Sgd5Database Open(byte[] file)
    {
      if (!IsSgd5(file)) throw new InvalidDataException("Not a shader database (SGD5).");
      var parsed = TryParse(file);
      if (parsed != null) return parsed;
      // The tables don't parse: find the records by their marker.
      var shaders = new List<Sgd5Shader>();
      for (int at = 0; (at = IndexOf(file, at)) >= 0; at += 4)
      {
        var s = RecordAt(file, at, shaders.Count);
        if (s != null) shaders.Add(s);
      }
      return new Sgd5Database(shaders, Array.Empty<Sgd5Effect>(), fromTables: false);
    }

    // ── tables ──────────────────────────────────────────────────────────

    private sealed class Cursor
    {
      private readonly byte[] _f;
      public int At;
      public Cursor(byte[] f, int at) { _f = f; At = at; }
      public int Take(long n)
      {
        if (n < 0 || At + n > _f.Length) throw new InvalidDataException("Past the end of the database.");
        int start = At; At += (int)n; return start;
      }
      public byte U8() => _f[Take(1)];
      public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(_f.AsSpan(Take(4), 4));
      public ulong U64() => BinaryPrimitives.ReadUInt64LittleEndian(_f.AsSpan(Take(8), 8));
      public string Str()
      {
        int n = U8();
        int start = Take(n);
        if (n == 0 || _f[start + n - 1] != 0) throw new InvalidDataException("Bad string.");
        return Encoding.UTF8.GetString(_f, start, n - 1);
      }
      /// <summary>u32 n, then n items of size bytes: returns n.</summary>
      public int Items(int size) { uint n = U32(); Take((long)n * size); return (int)n; }
      public List<uint> U32s() { uint n = U32(); var list = new List<uint>((int)Math.Min(n, 1 << 16)); for (uint i = 0; i < n; i++) list.Add(U32()); return list; }
      public byte[] File => _f;
    }

    private static Sgd5Database? TryParse(byte[] file)
    {
      try
      {
        var c = new Cursor(file, 4);
        c.U64();
        uint blocks = c.U32();
        for (uint i = 0; i < blocks; i++) c.Take(c.U32());
        c.Take(c.U32());
        var lists = new List<Sgd5Shader>[6];
        var all = new List<Sgd5Shader>();
        for (int stage = 0; stage < 6; stage++)
        {
          lists[stage] = new List<Sgd5Shader>();
          uint n = c.U32();
          for (uint i = 0; i < n; i++)
          {
            var s = Shader(c, stage, (int)i);
            lists[stage].Add(s);
            all.Add(s);
          }
        }
        var effects = new List<Sgd5Effect>();
        uint count = c.U32();
        for (uint i = 0; i < count; i++) effects.Add(Effect(c, lists));
        return new Sgd5Database(all, effects, fromTables: true);
      }
      catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException or OverflowException)
      {
        return null;
      }
    }

    private static Sgd5Shader Shader(Cursor c, int stage, int index)
    {
      string name = c.Str();
      byte kind = c.U8();
      uint n = c.U32();
      int record = c.Take(n);
      var f = c.File;
      if (n < 12 || BinaryPrimitives.ReadUInt32LittleEndian(f.AsSpan(record)) != Marker || BinaryPrimitives.ReadUInt32LittleEndian(f.AsSpan(record + 4)) != n)
        throw new InvalidDataException("Bad shader record.");
      int unpacked = (int)BinaryPrimitives.ReadUInt32LittleEndian(f.AsSpan(record + 8));
      if (unpacked < 0 || unpacked > MaxUnpacked) throw new InvalidDataException("Absurd shader size.");
      ulong hash = c.U64();
      c.Take(0x90);
      c.Take(c.U32());
      return new Sgd5Shader(f, stage, index, name, kind, hash, record + 12, (int)n - 12, unpacked);
    }

    private static Sgd5Effect Effect(Cursor c, List<Sgd5Shader>[] lists)
    {
      string name = c.Str();
      uint size = c.U32();
      var stages = new List<uint>[6];
      for (int s = 0; s < 6; s++)
      {
        stages[s] = c.U32s();
        if (stages[s].Any(i => i >= lists[s].Count)) throw new InvalidDataException("Bad shader index.");
      }
      c.Take(0x50);
      int rasterizers = c.Items(16), depthStencils = c.Items(24), blends = c.Items(0x44);
      c.Items(0x24);
      var techniques = c.U32s();
      int passCount = (int)c.U32();
      int passesAt = c.Take((long)passCount * 24);
      var extra = c.U32s();
      long loaded = (stages.Sum(s => (long)s.Count + 1) * 8) + techniques.Count * 8L + passCount * 32L + extra.Count * 4L;
      uint strings = c.U32();
      if (strings != Shared) { c.Take(strings); loaded += strings; }
      for (int k = 0; k < 2; k++) { uint n = c.U32(); c.Take(n); loaded += n; }
      uint blockCount = c.U32() & 0x00FFFFFF;
      if (blockCount != 0) loaded = (loaded + 7) / 8 * 8 + blockCount * 8L;
      for (uint b = 0; b < blockCount; b++)
      {
        uint n = c.U32();
        if (n == Shared) c.U32(); else { c.Take(n); loaded += n; }
      }
      uint namesLength = c.U32();
      int namesAt = c.Take(namesLength);
      if (loaded + namesLength != size) throw new InvalidDataException("Effect size disagrees with its tables.");

      var f = c.File;
      var list = new List<Sgd5Technique>();
      foreach (uint t in techniques)
      {
        int at = (int)(t & 0x7FFF);
        if (at >= namesLength) throw new InvalidDataException("Bad technique name.");
        int end = Array.IndexOf(f, (byte)0, namesAt + at, (int)namesLength - at);
        if (end < 0) throw new InvalidDataException("Bad technique name.");
        string techniqueName = Encoding.UTF8.GetString(f, namesAt + at, end - namesAt - at);
        int first = (int)((t >> 15) & 0x1FF), count = (int)(t >> 24);
        if (first + count > passCount) throw new InvalidDataException("Bad pass range.");
        var passes = new List<Sgd5Pass>();
        for (int p = first; p < first + count; p++)
        {
          int o = passesAt + p * 24;
          byte flags = f[o + 10];
          var pass = new Sgd5Pass
          {
            SetsRasterizer = (flags & 1) != 0,
            SetsDepthStencil = (flags & 2) != 0,
            SetsBlend = (flags & 4) != 0,
            StencilRef = (flags & 8) != 0 ? f[o + 9] : null,
          };
          for (int slot = 0; slot < 6; slot++)
          {
            int local = f[o + slot];
            if (local == 0) continue;
            if (local - 1 >= stages[slot].Count) throw new InvalidDataException("Bad pass shader.");
            pass.Shaders[slot] = lists[slot][(int)stages[slot][local - 1]];
          }
          passes.Add(pass);
        }
        list.Add(new Sgd5Technique { Name = techniqueName, Passes = passes });
      }
      return new Sgd5Effect
      {
        Name = name, Techniques = list,
        RasterizerStates = rasterizers, DepthStencilStates = depthStencils, BlendStates = blends,
      };
    }

    // ── fallback: records by marker ─────────────────────────────────────

    private static int IndexOf(byte[] f, int from)
    {
      ReadOnlySpan<byte> marker = stackalloc byte[] { 0xE7, 0xDC, 0x0D, 0x00 };
      int i = f.AsSpan(from).IndexOf(marker);
      return i < 0 ? -1 : from + i;
    }

    private static Sgd5Shader? RecordAt(byte[] f, int at, int index)
    {
      if (at < 6 || at + 12 > f.Length) return null;
      uint n = BinaryPrimitives.ReadUInt32LittleEndian(f.AsSpan(at - 4));
      uint unpacked = BinaryPrimitives.ReadUInt32LittleEndian(f.AsSpan(at + 8));
      if (BinaryPrimitives.ReadUInt32LittleEndian(f.AsSpan(at + 4)) != n || n < 12 || unpacked > MaxUnpacked || at + n > f.Length) return null;
      int nul = at - 6;
      string name = f[nul] == 0 ? NameBefore(f, nul) ?? $"shader_{index:D5}" : $"shader_{index:D5}";
      byte kind = f[at - 5];
      int stage = name.StartsWith("PS_") ? 1 : name.StartsWith("GS_") ? 2 : name.StartsWith("CS_") || name.Contains(":CS_") ? 5 : 0;
      return new Sgd5Shader(f, stage, index, name, kind, 0, at + 12, (int)n - 12, (int)unpacked);
    }

    /// <summary>The printable name that ends at nul and follows its length byte.</summary>
    private static string? NameBefore(byte[] f, int nul)
    {
      int run = nul;
      while (run > 0 && f[run - 1] > 0x20 && f[run - 1] < 0x7F) run--;
      for (int l = Math.Max(0, run - 1); l < nul; l++)
        if (f[l] == nul - l) return Encoding.ASCII.GetString(f, l + 1, nul - l - 1);
      return null;
    }
  }
}
