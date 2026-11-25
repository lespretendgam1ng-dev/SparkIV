/**********************************************************************\

 RageLib - SparkIV
 Names for the hashes RDR2 (PC) stores instead of names.

 Author: reditec

 This program is free software: you can redistribute it and/or modify
 it under the terms of the GNU General Public License as published by
 the Free Software Foundation, either version 3 of the License, or
 (at your option) any later version.

\**********************************************************************/

using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;

namespace RageLib.Rdr2.Pc
{
  /// <summary>
  /// Reads a compiled strings database (<c>.sivs</c>, built by reditec/RAGE-StringsDatabase with
  /// <c>build-sparkiv-database</c>; the format is described in that repository's
  /// <c>docs/SparkIV-database-format.md</c>). It maps the hashes RDR2 stores to the names they were made from:
  /// archive entry paths, audio track names and text keys. Lookups are a binary search in the memory-mapped file.
  /// </summary>
  public sealed class Rdr2Names : IDisposable
  {
    public enum Kind : uint { ArchiveItems = 1, AudioTracks = 2, TextKeys = 3 }

    private const uint Magic = 0x53564953; // "SIVS"
    private const uint UnconfirmedFlag = 0x80000000u;

    private readonly struct Section
    {
      public readonly uint HashType; public readonly long Count; public readonly long Records; public readonly long Strings;
      public Section(uint hashType, long count, long records, long strings) { HashType = hashType; Count = count; Records = records; Strings = strings; }
    }

    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;
    private readonly Section?[] _sections = new Section?[4];

    /// <summary>The info text stored in the file (where it was built from).</summary>
    public string Info { get; }

    private Rdr2Names(MemoryMappedFile file, MemoryMappedViewAccessor view)
    {
      _file = file; _view = view;
      if (view.ReadUInt32(0) != Magic) throw new InvalidDataException("Not a SparkIV strings database.");
      if (view.ReadUInt32(4) != 1) throw new InvalidDataException("Unsupported strings database version.");
      uint sections = view.ReadUInt32(8);
      uint infoOffset = view.ReadUInt32(12), infoLength = view.ReadUInt32(16);
      var info = new byte[infoLength];
      view.ReadArray(infoOffset, info, 0, info.Length);
      Info = Encoding.UTF8.GetString(info);
      for (uint i = 0; i < sections; i++)
      {
        long e = 32 + i * 32;
        uint kind = view.ReadUInt32(e);
        if (kind < _sections.Length)
          _sections[kind] = new Section(view.ReadUInt32(e + 4), view.ReadUInt32(e + 8), view.ReadUInt32(e + 12), view.ReadUInt32(e + 20));
      }
    }

    /// <summary>Opens a database file; it stays mapped until disposed.</summary>
    public static Rdr2Names Open(string path)
    {
      var file = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
      try
      {
        var view = file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        try { return new Rdr2Names(file, view); }
        catch { view.Dispose(); throw; }
      }
      catch { file.Dispose(); throw; }
    }

    /// <summary>joaat (one-at-a-time) over the lower-case string, as RAGE hashes names.</summary>
    public static uint Hash(string s)
    {
      uint h = 0;
      foreach (char ch in s)
      {
        h += char.ToLowerInvariant(ch); h += h << 10; h ^= h >> 6;
      }
      h += h << 3; h ^= h >> 11; h += h << 15;
      return h;
    }

    /// <summary>
    /// The name of <paramref name="hash"/>, or null if it is not known. <paramref name="scope"/> is the path of the
    /// name list the hash belongs to (for an archive entry: the archive's path relative to the game folder without
    /// ".rpf", continued by the paths of archives inside it); it only matters for the few hashes two real names share.
    /// <paramref name="confirmed"/> is false if the database could not confirm the name with a hash the game uses.
    /// </summary>
    public string Lookup(Kind kind, uint hash, string scope, out bool confirmed)
    {
      confirmed = false;
      if ((uint)kind >= _sections.Length || _sections[(int)kind] is not Section s) return null;
      long lo = 0, hi = s.Count;
      while (lo < hi)
      {
        long mid = (lo + hi) >> 1;
        if (_view.ReadUInt32(s.Records + mid * 12) < hash) lo = mid + 1; else hi = mid;
      }
      if (lo >= s.Count || _view.ReadUInt32(s.Records + lo * 12) != hash) return null;

      long pick = lo;
      if (scope != null && lo + 1 < s.Count && _view.ReadUInt32(s.Records + (lo + 1) * 12) == hash)
      {
        uint scopeHash = Hash(scope);
        for (long i = lo; i < s.Count && _view.ReadUInt32(s.Records + i * 12) == hash; i++)
          if (_view.ReadUInt32(s.Records + i * 12 + 4) == scopeHash) { pick = i; break; }
      }
      uint off = _view.ReadUInt32(s.Records + pick * 12 + 8);
      confirmed = (off & UnconfirmedFlag) == 0;
      long at = s.Strings + (off & ~UnconfirmedFlag);
      int len = _view.ReadUInt16(at);
      var bytes = new byte[len];
      _view.ReadArray(at + 2, bytes, 0, len);
      return Encoding.UTF8.GetString(bytes);
    }

    public string Lookup(Kind kind, uint hash, string scope = null) => Lookup(kind, hash, scope, out _);

    public void Dispose()
    {
      _view.Dispose();
      _file.Dispose();
    }
  }
}
