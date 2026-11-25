/**********************************************************************\

 RageLib - SparkIV
 GTA VI RPF9 path-hash dictionary.

 RPF9 archives store no names, only a 64-bit hash of each member's relative
 path: MurmurHash3 x64 (low 64 bits, seed 0) of the lower-case path with '/'
 separators and a recognised extension removed (e.g. "sfx/core.props" for the
 nested archive sfx/core.props.rpf). The game never lists members; it hashes a
 path it already knows and looks it up. SparkIV does the same: it collects
 candidate paths from the user's own files (strings in the game executable and
 in decoded text members of the archives), hashes them and keeps the ones
 that match. A member's folder is simply the directory part of its path.
 Nothing is shipped; the dictionary is rebuilt from the local game files and
 cached per executable.

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
using System.Text;

namespace RageLib.GtaVI.Pc
{
  /// <summary>A recovered member path; <see cref="ExtensionHint"/> is the extension the
  /// source string carried when the hashed path had it removed.</summary>
  public readonly struct Rpf9Name
  {
    public Rpf9Name(string path, string extensionHint) { Path = path; ExtensionHint = extensionHint; }
    public string Path { get; }
    public string ExtensionHint { get; }
  }

  /// <summary>Process-wide path-hash dictionary for RPF9 members.</summary>
  public static class Rpf9NameDictionary
  {
    private static readonly object Lock = new object();
    private static readonly Dictionary<ulong, Rpf9Name> Names = new Dictionary<ulong, Rpf9Name>();
    private static readonly HashSet<string> SeenTokens = new HashSet<string>(StringComparer.Ordinal);

    // Folders the game fills in at run time ("%ssfx/%s/" -> "sfx") and the bare
    // names found in archive text (name lists); combined on demand for members
    // whose path is not a literal string anywhere.
    private static readonly HashSet<string> FormatDirs = new HashSet<string>(StringComparer.Ordinal);
    private static readonly HashSet<string> Leaves = new HashSet<string>(StringComparer.Ordinal);
    private static readonly System.Text.RegularExpressions.Regex Placeholder =
      new System.Text.RegularExpressions.Regex(@"%[-+ #0-9.]*[a-z]", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Names added since the last <see cref="Save"/>.</summary>
    public static bool Dirty { get; private set; }

    private const int MinToken = 3, MaxToken = 200;

    public static int Count { get { lock (Lock) return Names.Count; } }
    public static int LeafCount { get { lock (Lock) return Leaves.Count; } }
    public static int FormatDirCount { get { lock (Lock) return FormatDirs.Count; } }
    public static bool HasLeaf(string leaf) { lock (Lock) return Leaves.Contains(leaf); }

    /// <summary>The RPF9 path hash of <paramref name="path"/>.</summary>
    public static ulong Hash(string path)
    {
      string p = path.ToLowerInvariant().Replace('\\', '/');
      return MurmurLow64(Encoding.ASCII.GetBytes(p));
    }

    public static bool TryGet(ulong hash, out Rpf9Name name)
    {
      lock (Lock) return Names.TryGetValue(hash, out name);
    }

    /// <summary>Adds every path-like token found in <paramref name="data"/> (ASCII and UTF-16LE).
    /// With <paramref name="asLeaves"/>, tokens without a folder are also kept as leaf names
    /// for <see cref="ResolveWithFormatDirs"/> (use it for archive text, not for the executable).</summary>
    public static void AddText(ReadOnlySpan<byte> data, bool asLeaves = false)
    {
      var tokens = new List<string>();
      Tokenize(data, tokens);
      List<string> list = asLeaves ? NameList(data) : null;
      lock (Lock)
      {
        foreach (string t in tokens)
          AddTokenLocked(t);
        if (list != null)
          foreach (string leaf in list)
            if (Leaves.Add(leaf)) Dirty = true;
      }
    }

    /// <summary>The entries of a name list (one name per line or per NUL-terminated
    /// string), or null if <paramref name="data"/> is not one. Such lists are where
    /// the game keeps names it later fills into a run-time folder.</summary>
    private static List<string> NameList(ReadOnlySpan<byte> data)
    {
      var names = new List<string>();
      int lines = 0, bad = 0, start = 0;
      for (int i = 0; i <= data.Length; i++)
      {
        bool end = i == data.Length || data[i] == (byte)'\n' || data[i] == (byte)'\r' || data[i] == 0;
        if (!end) continue;
        int len = i - start;
        if (len > 0)
        {
          lines++;
          ReadOnlySpan<byte> line = data.Slice(start, len);
          bool ok = len >= MinToken && len <= 120;
          for (int k = 0; ok && k < line.Length; k++)
            ok = IsPathChar(line[k]) && line[k] != (byte)'/' && line[k] != (byte)'%' && line[k] != (byte)':';
          if (ok) names.Add(Encoding.ASCII.GetString(line).ToLowerInvariant().Trim('.'));
          else bad++;
          if (lines >= 64 && bad * 5 > lines) return null; // mostly not a list
        }
        start = i + 1;
      }
      return names.Count >= 2 && bad * 5 <= lines ? names : null;
    }

    /// <summary>
    /// Tries every run-time folder ("%s<dir>/%s") with every known leaf name for
    /// the given unresolved hashes; matches become names. Returns the number found.
    /// </summary>
    public static int ResolveWithFormatDirs(HashSet<ulong> unresolved)
    {
      if (unresolved.Count == 0) return 0;
      string[] dirs, leaves;
      lock (Lock) { dirs = new string[FormatDirs.Count]; FormatDirs.CopyTo(dirs); leaves = new string[Leaves.Count]; Leaves.CopyTo(leaves); }
      var found = new List<(ulong, string, string)>();
      foreach (string dir in dirs)
        foreach (string leaf in leaves)
        {
          if (leaf.Length == 0) continue;
          string p = dir + "/" + leaf;
          ulong h = MurmurLow64(Encoding.ASCII.GetBytes(p));
          if (unresolved.Contains(h)) { found.Add((h, p, null)); unresolved.Remove(h); continue; }
          int dot = leaf.LastIndexOf('.');
          if (dot > 0)
          {
            string q = p.Substring(0, p.Length - (leaf.Length - dot));
            h = MurmurLow64(Encoding.ASCII.GetBytes(q));
            if (unresolved.Contains(h)) { found.Add((h, q, leaf.Substring(dot))); unresolved.Remove(h); }
          }
        }
      lock (Lock)
        foreach (var (h, p, ext) in found)
        {
          Names[h] = new Rpf9Name(p, ext);
          Dirty = true;
        }
      return found.Count;
    }

    /// <summary>Adds a single candidate (any form: "mount:/dir/file.ext", "dir\\file", ...).</summary>
    public static void AddCandidate(string token)
    {
      lock (Lock) AddTokenLocked(token.ToLowerInvariant().Replace('\\', '/'));
    }

    /// <summary>Streams a (large) file and adds its tokens.</summary>
    public static void AddFile(string path)
    {
      const int chunk = 8 << 20, carry = 1024;
      using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
      byte[] buf = new byte[chunk + carry];
      int kept = 0;
      while (true)
      {
        int read = fs.Read(buf, kept, chunk);
        if (read <= 0) break;
        int len = kept + read;
        // keep the tail so tokens crossing a chunk boundary are not cut
        int cut = Math.Max(0, len - carry);
        AddText(buf.AsSpan(0, len));
        Buffer.BlockCopy(buf, cut, buf, 0, len - cut);
        kept = len - cut;
      }
    }

    // ── cache ────────────────────────────────────────────────────────────

    public static void Save(string file)
    {
      lock (Lock)
      {
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file));
        using var w = new StreamWriter(file, false, new UTF8Encoding(false));
        foreach (var kv in Names)
          w.WriteLine(kv.Key.ToString("x16") + "\t" + kv.Value.Path + "\t" + (kv.Value.ExtensionHint ?? ""));
        foreach (string dir in FormatDirs)
          w.WriteLine("D\t" + dir);
        foreach (string leaf in Leaves)
          w.WriteLine("L\t" + leaf);
        Dirty = false;
      }
    }

    public static bool Load(string file)
    {
      if (!File.Exists(file)) return false;
      lock (Lock)
      {
        foreach (string line in File.ReadLines(file))
        {
          string[] f = line.Split('\t');
          if (f.Length == 2 && f[0] == "D") { FormatDirs.Add(f[1]); continue; }
          if (f.Length == 2 && f[0] == "L") { Leaves.Add(f[1]); continue; }
          if (f.Length < 2 || !ulong.TryParse(f[0], System.Globalization.NumberStyles.HexNumber, null, out ulong h))
            continue;
          if (!Names.ContainsKey(h))
            Names[h] = new Rpf9Name(f[1], f.Length > 2 && f[2].Length > 0 ? f[2] : null);
        }
      }
      return true;
    }

    // ── external name list ────────────────────────────────────────────────

    /// <summary>
    /// Loads a ground-truth hash→path list and overrides harvested guesses with
    /// it. Accepts the GTA VI name lists' <c>file_list.tsv</c> ("path, name hash,
    /// named 1/0, size") and the <c>*_murmur64.tsv</c> form ("hash, name"). In
    /// <c>file_list.tsv</c> the hash is a member's own 64-bit path hash and the
    /// path's part after the last "&lt;archive&gt;.rpf\" is its path within its
    /// immediate archive. Returns how many names were added.
    /// </summary>
    public static int LoadNameList(string file)
    {
      if (string.IsNullOrEmpty(file) || !File.Exists(file)) return 0;
      int added = 0;
      lock (Lock)
      {
        foreach (string line in File.ReadLines(file))
        {
          string[] f = line.Split('\t');
          string pathField;
          ulong h;
          if (f.Length >= 3 &&
              ulong.TryParse(f[1], System.Globalization.NumberStyles.HexNumber, null, out h))
          {
            if (f[2] != "1") continue;              // skip unnamed rows (name is the hash)
            pathField = f[0];
          }
          else if (f.Length == 2 &&
              ulong.TryParse(f[0], System.Globalization.NumberStyles.HexNumber, null, out h))
          {
            pathField = f[1];
          }
          else continue;                            // header or malformed line

          string rel = RelativeInArchive(pathField);
          if (rel.Length == 0) continue;
          SplitLeafExtension(rel, out string stem, out string ext);
          Names[h] = new Rpf9Name(stem, ext);       // ground truth wins over guesses
          added++;
        }
        if (added > 0) Dirty = true;
      }
      return added;
    }

    /// <summary>The path within its immediate archive: the part after the last
    /// ".rpf\" (or ".rpf/"), lowercased with '/' separators.</summary>
    private static string RelativeInArchive(string full)
    {
      string s = full.Replace('\\', '/');
      int at = s.LastIndexOf(".rpf/", StringComparison.OrdinalIgnoreCase);
      if (at >= 0) s = s.Substring(at + 5);
      return s.Trim('/').ToLowerInvariant();
    }

    /// <summary>Splits a relative path into its stem and the leaf's extension.</summary>
    private static void SplitLeafExtension(string rel, out string stem, out string ext)
    {
      int slash = rel.LastIndexOf('/');
      int dot = rel.LastIndexOf('.');
      if (dot > slash && dot > 0 && dot < rel.Length - 1)
      {
        stem = rel.Substring(0, dot);
        ext = rel.Substring(dot);
      }
      else { stem = rel; ext = null; }
    }

    // ── internals ────────────────────────────────────────────────────────

    private static bool IsPathChar(byte b) =>
      (b >= (byte)'a' && b <= (byte)'z') || (b >= (byte)'A' && b <= (byte)'Z') || (b >= (byte)'0' && b <= (byte)'9') ||
      b == (byte)'_' || b == (byte)'-' || b == (byte)'.' || b == (byte)'/' || b == (byte)':' || b == (byte)'\\' || b == (byte)'%';

    private static void Tokenize(ReadOnlySpan<byte> d, List<string> tokens)
    {
      var sb = new StringBuilder(MaxToken);
      // ASCII runs
      for (int i = 0; i < d.Length; i++)
      {
        byte b = d[i];
        if (IsPathChar(b) && sb.Length < MaxToken + 1) { sb.Append((char)b); continue; }
        Flush(sb, tokens);
      }
      Flush(sb, tokens);
      // UTF-16LE runs (char, 0x00, char, 0x00, ...)
      for (int i = 0; i + 1 < d.Length; )
      {
        if (IsPathChar(d[i]) && d[i + 1] == 0)
        {
          int j = i;
          while (j + 1 < d.Length && IsPathChar(d[j]) && d[j + 1] == 0 && sb.Length <= MaxToken) { sb.Append((char)d[j]); j += 2; }
          if (sb.Length >= MinToken) Flush(sb, tokens); else sb.Clear();
          i = j;
        }
        else i++;
      }
    }

    private static void Flush(StringBuilder sb, List<string> tokens)
    {
      if (sb.Length >= MinToken && sb.Length <= MaxToken)
        tokens.Add(sb.ToString().ToLowerInvariant().Replace('\\', '/'));
      sb.Clear();
    }

    private static void AddTokenLocked(string s)
    {
      if (!SeenTokens.Add(s)) return;
      if (s.IndexOf('%') >= 0)
      {
        // A format string: the pieces between placeholders are literal paths,
        // and a piece ending in '/' is a folder the game fills at run time.
        foreach (string piece in Placeholder.Split(s))
        {
          string literal = piece.Replace("%", "");
          if (literal.Length == 0) continue;
          if (literal.EndsWith("/"))
          {
            string dir = literal;
            int mount = dir.IndexOf(":/", StringComparison.Ordinal);
            if (mount >= 0) dir = dir.Substring(mount + 2);
            dir = dir.Trim('/', '.', ' ');
            if (dir.Length > 0 && dir.Length <= 120 && FormatDirs.Add(dir)) Dirty = true;
          }
          if (literal.Length >= MinToken) AddTokenLocked(literal);
        }
        return;
      }
      int colon = s.IndexOf(":/", StringComparison.Ordinal);
      if (colon >= 0) s = s.Substring(colon + 2);
      s = s.Trim('/', '.', ' ');
      if (s.Length == 0) return;
      string[] parts = s.Split('/', StringSplitOptions.RemoveEmptyEntries);
      if (parts.Length == 0) return;
      string leaf = parts[^1];
      int dot = leaf.LastIndexOf('.');
      string ext = dot > 0 ? leaf.Substring(dot) : null;
      for (int i = 0; i < parts.Length; i++)
      {
        string p = string.Join("/", parts, i, parts.Length - i);
        Put(p, null);
        if (ext != null) Put(p.Substring(0, p.Length - ext.Length), ext);
      }
    }

    private static void Put(string path, string extHint)
    {
      if (path.Length == 0 || path.Length > MaxToken) return;
      ulong h = MurmurLow64(Encoding.ASCII.GetBytes(path));
      if (Names.TryGetValue(h, out var old))
      {
        if (old.ExtensionHint == null && extHint != null && old.Path == path)
          Names[h] = new Rpf9Name(path, extHint);
        return;
      }
      Names[h] = new Rpf9Name(path, extHint);
    }

    private static ulong Rotate(ulong n, int bits) => (n << bits) | (n >> (64 - bits));

    private static ulong Finalize(ulong n)
    {
      unchecked { n ^= n >> 33; n *= 0xff51afd7ed558ccd; n ^= n >> 33; n *= 0xc4ceb9fe1a85ec53; return n ^ (n >> 33); }
    }

    /// <summary>MurmurHash3 x64 128 (seed 0), low 64 bits.</summary>
    private static ulong MurmurLow64(ReadOnlySpan<byte> bytes)
    {
      unchecked
      {
        const ulong c1 = 0x87c37b91114253d5, c2 = 0x4cf5ad432745937f;
        ulong h1 = 0, h2 = 0;
        int full = bytes.Length & ~15;
        for (int at = 0; at < full; at += 16)
        {
          ulong k1 = BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(at, 8));
          ulong k2 = BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(at + 8, 8));
          h1 ^= Rotate(k1 * c1, 31) * c2; h1 = Rotate(h1, 27) + h2; h1 = h1 * 5 + 0x52dce729;
          h2 ^= Rotate(k2 * c2, 33) * c1; h2 = Rotate(h2, 31) + h1; h2 = h2 * 5 + 0x38495ab5;
        }
        ulong t1 = 0, t2 = 0;
        for (int i = full; i < bytes.Length; i++)
          if (i - full < 8) t1 |= (ulong)bytes[i] << ((i - full) * 8);
          else t2 |= (ulong)bytes[i] << ((i - full - 8) * 8);
        if (bytes.Length - full > 8) h2 ^= Rotate(t2 * c2, 33) * c1;
        if (bytes.Length > full) h1 ^= Rotate(t1 * c1, 31) * c2;
        h1 ^= (ulong)bytes.Length; h2 ^= (ulong)bytes.Length;
        h1 += h2; h2 += h1;
        h1 = Finalize(h1); h2 = Finalize(h2);
        return h1 + h2;
      }
    }
  }
}
