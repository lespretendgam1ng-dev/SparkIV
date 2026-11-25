/**********************************************************************\

 RageLib - SparkIV
 GTA VI (RPF9) file system.

 Opens a GTA VI .rpf archive: it reads the cipher data from the user's own game
 executable (found next to the archive), decrypts the table of contents and
 parses it into members (see Rpf9Toc). RPF9 stores each member's relative path
 only as a hash; the folder a member lives in is part of that path. Names and
 folders are recovered by hashing candidate paths collected from the user's
 own game files (Rpf9NameDictionary) - the same lookup the game performs.
 Members whose path is not recovered are listed under "(unnamed)" by hash.
 Archives are read-only. zlib members are decompressed on read; other
 transformed (encrypted/chunked) members are served as stored.

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
using RageLib.GtaVI.Pc;
using Directory = RageLib.FileSystem.Common.Directory;
using File = RageLib.FileSystem.Common.File;

namespace RageLib.FileSystem
{
  /// <summary>Reads GTA VI (RPF9) archives (read-only).</summary>
  public class GtaVIFileSystem : Common.FileSystem
  {
    private const uint Rpf9Magic = 0x52504639; // "9FPR"
    public const string ExecutableName = "game_scarlett_master.exe";
    public const string UnnamedFolder = "(unnamed)";

    // One decoded cipher per executable is reused across archives (the exe is large).
    private static readonly object CipherLock = new object();
    private static string _cipherExe;
    private static Rpf9Cipher _cipher;

    // The name dictionary is filled once per executable (and cached on disk).
    private static readonly object NamesLock = new object();
    private static string _namesExe;

    private const long MaxHarvestMember = 8L << 20;      // stored bytes per text member
    private const long MaxHarvestDecoded = 16L << 20;    // decoded bytes per text member
    private const long MaxHarvestPerArchive = 48L << 20; // decoded bytes per archive (incl. its nested ones)

    private string _path;

    /// <summary>The file this archive's data lives in (the real archive for a
    /// nested one opened in place).</summary>
    public string ArchivePath => _path;

    public override bool SupportsRebuild => false;
    public override bool HasDirectoryStructure => true;

    /// <summary>Whether <paramref name="path"/> begins with the RPF9 magic.</summary>
    public static bool IsRpf9File(string path)
    {
      try
      {
        using var fs = System.IO.File.OpenRead(path);
        Span<byte> magic = stackalloc byte[4];
        return fs.Length >= 4 && fs.Read(magic) == 4 &&
               BinaryPrimitives.ReadUInt32LittleEndian(magic) == Rpf9Magic;
      }
      catch (IOException) { return false; }
      catch (UnauthorizedAccessException) { return false; }
    }

    public override void Open(string filename)
    {
      using var fs = System.IO.File.OpenRead(filename);
      OpenCore(filename, 0, fs.Length);
    }

    /// <summary>Opens a GTA VI archive nested in <paramref name="parentPath"/> IN
    /// PLACE (no copy): its TOC is at <paramref name="baseOffset"/> and its member
    /// data stays in the parent file. This is how archives too large to copy into
    /// a byte[] (the multi-GB audio packs, e.g. core.score.rpf at 2 GB,
    /// core.speech.ped.rpf at ~14 GB) are opened.</summary>
    public void OpenNested(string parentPath, long baseOffset, long size)
    {
      OpenCore(parentPath, baseOffset, size);
    }

    private void OpenCore(string path, long baseOffset, long size)
    {
      _path = path;
      GetCipher(path); // also locates the executable for the name dictionary
      List<Rpf9Entry> entries;
      using (var fs = System.IO.File.OpenRead(path))
        entries = ReadEntries(fs, baseOffset, baseOffset == 0 ? fs.Length : size);
      // In place: make every member offset absolute in the parent file so the
      // data reads (LoadData, SniffMember) need no base of their own.
      if (baseOffset != 0)
        foreach (var e in entries) e.Offset += baseOffset;
      EnsureNames(_cipherExe);
      // The game's own archives were read for names when the dictionary was
      // built (and are cached); any OTHER top-level archive contributes its own
      // text members now. Nested game archives are already covered.
      bool gameArchive = _cipherExe != null && string.Equals(
        Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetDirectoryName(_cipherExe), StringComparison.OrdinalIgnoreCase);
      if (!gameArchive && baseOffset == 0)
        using (var fs = System.IO.File.OpenRead(path))
          HarvestText(fs, entries, 0, 1);
      Build(entries);
    }

    /// <summary>Decrypts and parses the TOC of the archive at <paramref name="baseOffset"/>
    /// (0 for a file; the member offset for an archive nested in another). Member
    /// offsets are relative to that base.</summary>
    private static List<Rpf9Entry> ReadEntries(Stream fs, long baseOffset, long archiveSize)
    {
      if (archiveSize < 64)
        throw new InvalidDataException("The archive is too short to be an RPF9 file.");
      byte[] header = new byte[64];
      fs.Seek(baseOffset, SeekOrigin.Begin);
      fs.ReadExactly(header, 0, 64);
      if (BitConverter.ToUInt32(header, 0) != Rpf9Magic)
        throw new InvalidDataException("Not an RPF9 archive.");
      uint size = BitConverter.ToUInt32(header, 4);
      int tocLen = (int)size - 0x40;
      if (tocLen <= 0 || tocLen % 16 != 0 || 0x40 + (long)tocLen > archiveSize)
        throw new InvalidDataException("Invalid RPF9 table-of-contents size.");
      byte[] toc = new byte[tocLen];
      fs.Seek(baseOffset + 0x40, SeekOrigin.Begin);
      fs.ReadExactly(toc, 0, tocLen);
      Rpf9Cipher cipher;
      lock (CipherLock) cipher = _cipher;
      if (cipher == null)
        throw new FileNotFoundException($"The GTA VI executable ({ExecutableName}) is needed to read RPF9 archives.");
      cipher.DecodeToc(header, toc);
      return Rpf9Toc.Parse(toc, size, archiveSize);
    }

    private static Rpf9Cipher GetCipher(string archivePath)
    {
      string exe = FindExecutable(archivePath);
      lock (CipherLock)
      {
        if (exe != null && (_cipher == null || !string.Equals(_cipherExe, exe, StringComparison.OrdinalIgnoreCase)))
        {
          _cipher = new Rpf9Secrets().Load(exe);
          _cipherExe = exe;
        }
        // Nested archives are opened from a temp file with no executable beside
        // them; they reuse the cipher already loaded for the parent.
        if (_cipher == null)
          throw new FileNotFoundException(
            $"The GTA VI executable ({ExecutableName}) was not found next to the archive. " +
            "RPF9 archives need it to read their index; SparkIV ships no keys.");
        return _cipher;
      }
    }

    private static string FindExecutable(string archivePath)
    {
      string env = Environment.GetEnvironmentVariable("RPF9_EXE");
      if (!string.IsNullOrEmpty(env) && System.IO.File.Exists(env))
        return env;
      string dirOfArchive = Path.GetDirectoryName(Path.GetFullPath(archivePath));
      for (var dir = dirOfArchive == null ? null : new DirectoryInfo(dirOfArchive); dir != null; dir = dir.Parent)
      {
        string candidate = Path.Combine(dir.FullName, ExecutableName);
        if (System.IO.File.Exists(candidate))
          return candidate;
      }
      return null;
    }

    // ── names ────────────────────────────────────────────────────────────

    /// <summary>
    /// Fills the path dictionary once per executable: strings of the executable
    /// plus the decoded text members of every archive next to it. The result is
    /// cached under %LOCALAPPDATA%\SparkIV so later sessions start instantly.
    /// </summary>
    private static void EnsureNames(string exe)
    {
      if (exe == null) return;
      lock (NamesLock)
      {
        if (string.Equals(_namesExe, exe, StringComparison.OrdinalIgnoreCase)) return;
        var info = new FileInfo(exe);
        string gameDir = Path.GetDirectoryName(exe);
        // A ground-truth name list (file_list.tsv), if the user placed one; its
        // length is folded into the cache key so updating it rebuilds the cache.
        string nameList = FindNameList(gameDir);
        long listLen = nameList != null ? new FileInfo(nameList).Length : 0;
        string cache = Path.Combine(
          Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SparkIV",
          $"rpf9-names-v6-{info.Length:x}-{info.LastWriteTimeUtc.Ticks:x}-{listLen:x}.tsv");
        _namesCache = cache;
        if (!Rpf9NameDictionary.Load(cache))
        {
          Rpf9NameDictionary.AddFile(exe);
          foreach (string archive in System.IO.Directory.GetFiles(gameDir, "*.rpf"))
          {
            try
            {
              using var fs = System.IO.File.OpenRead(archive);
              HarvestText(fs, ReadEntries(fs, 0, fs.Length), 0, 0);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or EndOfStreamException) { }
          }
          // Ground-truth names (if present) override the harvested guesses.
          if (nameList != null)
          {
            try { Rpf9NameDictionary.LoadNameList(nameList); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
          }
          SaveNames();
        }
        _namesExe = exe;
      }
    }

    private static string _namesCache;

    private static void SaveNames()
    {
      if (_namesCache == null) return;
      try { Rpf9NameDictionary.Save(_namesCache); }
      catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Locates an optional ground-truth name list (<c>file_list.tsv</c>):
    /// the <c>SPARKIV_NAMELIST</c> variable, then the game directory, the user's
    /// <c>Downloads\GTA6_NameLists</c>, and <c>%LOCALAPPDATA%\SparkIV</c>.</summary>
    private static string FindNameList(string gameDir)
    {
      var candidates = new List<string>();
      string env = Environment.GetEnvironmentVariable("SPARKIV_NAMELIST");
      if (!string.IsNullOrEmpty(env)) candidates.Add(env);
      string downloads = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "GTA6_NameLists");
      string local = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SparkIV");
      foreach (string dir in new[] { gameDir, Path.Combine(gameDir ?? "", "GTA6_NameLists"), downloads, local })
      {
        if (string.IsNullOrEmpty(dir)) continue;
        candidates.Add(Path.Combine(dir, "file_list.tsv"));
        candidates.Add(Path.Combine(dir, "file_names_murmur64.tsv"));
      }
      foreach (string c in candidates)
        try { if (System.IO.File.Exists(c)) return c; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
      return null;
    }

    private const int MaxNestedArchives = 64;            // nested archives read per archive (smallest first)
    private const long MaxNestedMember = 512L << 10;     // stored bytes per text member inside a nested archive
    private const int MaxNestedMembers = 128;            // text members read per nested archive

    /// <summary>Adds the path-like strings of an archive's zlib text members to the
    /// dictionary, and (one level deep) those of its nested archives - name lists
    /// such as audio's core.metadata live there. Only TOCs and zlib text members
    /// are read, never whole nested archives; one decoded-byte budget covers all.</summary>
    private static void HarvestText(Stream fs, List<Rpf9Entry> entries, long baseOffset, int depth)
    {
      long budget = MaxHarvestPerArchive;
      HarvestText(fs, entries, baseOffset, depth, ref budget);
    }

    private static void HarvestText(Stream fs, List<Rpf9Entry> entries, long baseOffset, int depth, ref long budget)
    {
      byte[] head = new byte[4];
      var nestedArchives = new List<Rpf9Entry>();
      long memberLimit = depth == 0 ? MaxHarvestMember : MaxNestedMember;
      int members = 0;
      foreach (Rpf9Entry e in entries)
      {
        if (budget <= 0) return;
        if (e.StoredSize < 6) continue;
        if (depth > 0 && members >= MaxNestedMembers) break;
        if (e.IsResource) continue;                 // resources carry no name text
        bool large = e.StoredSize > memberLimit;
        if (large && depth > 0) continue;           // inside nested archives: small members only
        long at = baseOffset + e.Offset;
        fs.Seek(at, SeekOrigin.Begin);
        if (fs.Read(head, 0, 4) != 4) continue;

        // Encrypted members decrypt to normal (usually zlib) content full of
        // asset paths; decrypt the head so the zlib check sees the real bytes.
        bool enc = e.IsEncrypted && e.EncDescriptor != null && _cipher != null && _cipher.PayloadReady;
        if (enc)
          _cipher.DecryptPayload(head.AsSpan(0, 4), e.UncompressedSize, e.EncDescriptor);

        if (BinaryPrimitives.ReadUInt32LittleEndian(head) == Rpf9Magic)
        {
          if (depth == 0) nestedArchives.Add(e);
          continue;
        }

        if (large || !IsZlibHeader(head[0], head[1])) continue;
        members++;
        byte[] raw = new byte[e.StoredSize];
        fs.Seek(at, SeekOrigin.Begin);
        int got = fs.Read(raw, 0, raw.Length);
        if (enc)
          _cipher.DecryptPayload(raw.AsSpan(0, got), e.UncompressedSize, e.EncDescriptor);
        byte[] text = Inflate(raw.AsSpan(0, got), MaxHarvestDecoded);
        if (text == null) continue;
        budget -= text.Length;
        Rpf9NameDictionary.AddText(text, asLeaves: true);
      }

      // Smaller nested archives first: metadata/name-list archives tend to be small.
      nestedArchives.Sort((a, b) => a.StoredSize.CompareTo(b.StoredSize));
      int count = 0;
      foreach (Rpf9Entry e in nestedArchives)
      {
        if (budget <= 0 || count++ >= MaxNestedArchives) break;
        long at = baseOffset + e.Offset;
        try { HarvestText(fs, ReadEntries(fs, at, e.StoredSize), at, depth + 1, ref budget); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or EndOfStreamException) { }
      }
    }

    // ── tree ─────────────────────────────────────────────────────────────

    private void Build(List<Rpf9Entry> entries)
    {
      var root = new Directory { Name = "/" };
      var dirs = new Dictionary<string, Directory>(StringComparer.OrdinalIgnoreCase) { [""] = root };
      var used = new Dictionary<Directory, HashSet<string>>();
      Directory unnamed = null;

      // Members whose path is no literal string anywhere: try the run-time
      // folders ("%s<dir>/%s") with the names found in archive text.
      var unresolved = new HashSet<ulong>();
      foreach (var e in entries)
        if (e.NameHash != 0 && !Rpf9NameDictionary.TryGet(e.NameHash, out _))
          unresolved.Add(e.NameHash);
      if (Rpf9NameDictionary.ResolveWithFormatDirs(unresolved) > 0)
        lock (NamesLock) SaveNames();

      // Recover every member's path first, so "(unnamed)" exists only when needed.
      var named = new Rpf9Name?[entries.Count];
      int namedCount = 0;
      for (int i = 0; i < entries.Count; i++)
        if (entries[i].NameHash != 0 && Rpf9NameDictionary.TryGet(entries[i].NameHash, out var n))
        { named[i] = n; namedCount++; }

      using var fs = System.IO.File.OpenRead(_path);
      for (int i = 0; i < entries.Count; i++)
      {
        Rpf9Entry e = entries[i];
        Sniff sniff = SniffMember(fs, e);

        Directory parent;
        string leaf;
        if (named[i] is Rpf9Name name)
        {
          string path = name.Path;
          int slash = path.LastIndexOf('/');
          parent = GetDirectory(dirs, used, slash < 0 ? "" : path.Substring(0, slash));
          leaf = slash < 0 ? path : path.Substring(slash + 1);
          leaf = WithExtension(leaf, name.ExtensionHint, sniff);
        }
        else
        {
          if (namedCount > 0)
            unnamed ??= GetDirectory(dirs, used, UnnamedFolder);
          parent = namedCount > 0 ? unnamed : root;
          leaf = (e.NameHash != 0 ? e.NameHash.ToString("X16") : $"slot{e.Slot:D5}") + sniff.Extension;
        }

        Rpf9Entry entry = e;
        var file = new File(() => LoadData(entry))
        {
          Name = Unique(used, parent, leaf),
          Size = e.UncompressedSize,
          CompressedSize = e.StoredSize,
          IsCompressed = e.StoredSize != e.UncompressedSize,
          IsResource = false,
          Offset = e.Offset,
          Codec = sniff.Kind,
          Detail = $"@0x{e.Offset:X} · {sniff.Kind} · {FriendlyBytes(e.StoredSize)}",
          DetailLong = (named[i] is Rpf9Name nm
                         ? $"path {nm.Path}{(nm.ExtensionHint ?? "")} (recovered from the path hash) · "
                         : $"name not recovered: 64-bit path hash {e.NameHash:X16} (RPF9 stores no plain names) · ")
                     + $"offset 0x{e.Offset:X} · {sniff.Kind} · "
                     + $"{e.StoredSize:N0} bytes stored · {e.UncompressedSize:N0} bytes decoded"
                     + (e.IsEncrypted ? " · encrypted" : "")
                     + (e.IsResource ? " · resource" : ""),
          ParentDirectory = parent,
        };
        parent.AddObject(file);
      }
      RootDirectory = root;
    }

    private static Directory GetDirectory(Dictionary<string, Directory> dirs, Dictionary<Directory, HashSet<string>> used, string path)
    {
      if (dirs.TryGetValue(path, out var dir))
        return dir;
      int slash = path.LastIndexOf('/');
      Directory parent = GetDirectory(dirs, used, slash < 0 ? "" : path.Substring(0, slash));
      string name = slash < 0 ? path : path.Substring(slash + 1);
      // A file may already use this name in the parent; keep folder names unique too.
      dir = new Directory { Name = Unique(used, parent, name), ParentDirectory = parent };
      parent.AddObject(dir);
      dirs[path] = dir;
      return dir;
    }

    private static string Unique(Dictionary<Directory, HashSet<string>> used, Directory dir, string name)
    {
      if (!used.TryGetValue(dir, out var set))
        used[dir] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      if (set.Add(name))
        return name;
      for (int i = 1; ; i++)
      {
        string candidate = $"{name}~{i}";
        if (set.Add(candidate))
          return candidate;
      }
    }

    /// <summary>The display name of a recovered leaf: the extension the path or its
    /// source string carried, else the one the content identifies.</summary>
    private static string WithExtension(string leaf, string hint, Sniff sniff)
    {
      if (sniff.Confident && !leaf.EndsWith(sniff.Extension, StringComparison.OrdinalIgnoreCase))
        return hint != null && hint.Equals(sniff.Extension, StringComparison.OrdinalIgnoreCase) ? leaf + hint : leaf + sniff.Extension;
      if (hint != null)
        return leaf + hint;
      int dot = leaf.LastIndexOf('.');
      return dot > 0 ? leaf : leaf + sniff.Extension;
    }

    // ── data ─────────────────────────────────────────────────────────────

    private byte[] LoadData(Rpf9Entry e)
    {
      if (e.StoredSize <= 0 || e.StoredSize > int.MaxValue)
        return Array.Empty<byte>();
      byte[] raw = new byte[e.StoredSize];
      using (var fs = System.IO.File.OpenRead(_path))
      {
        fs.Seek(e.Offset, SeekOrigin.Begin);
        int read = fs.Read(raw, 0, raw.Length);
        if (read != raw.Length)
          Array.Resize(ref raw, read);
      }

      // Encrypted members: decrypt the payload (CTR over its encrypted runs) to
      // the real member bytes (usually a zlib stream), using the static key read
      // from the user's executable.
      if (e.IsEncrypted && e.EncDescriptor != null && _cipher != null && _cipher.PayloadReady)
        _cipher.DecryptPayload(raw, e.UncompressedSize, e.EncDescriptor);

      // Resources: join their zlib pages into the system + graphics sections and
      // serve them in SparkIV's "RSC9" container, which the viewers split again
      // (texture dictionaries, 0DRP text databases, ...).
      if (e.IsResource)
      {
        byte[] body = Rpf9Resource.Unpack(raw, e.SystemSize, e.GraphicsSize);
        if (body != null)
          return Rpf9Resource.Wrap(body, e.ExtensionCode, e.SystemSize, e.GraphicsSize);
      }

      // zlib members decompress to the real content.
      if (raw.Length >= 2 && IsZlibHeader(raw[0], raw[1]))
      {
        byte[] inflated = Inflate(raw, Math.Max(e.UncompressedSize, 1) + 16);
        // Large members are a run of zlib streams (64 KB each, decoded); the first one alone is short.
        if (inflated != null && inflated.Length < e.UncompressedSize)
        {
          byte[] joined = Rpf9Resource.Unpack(raw, e.UncompressedSize, 0);
          if (joined != null)
            return joined;
        }
        if (inflated != null && inflated.Length > 0)
          return inflated;
      }


      // Stored members are padded to 16 bytes on disk; return their exact size.
      if (e.UncompressedSize > 0 && e.UncompressedSize < raw.Length && raw.Length - e.UncompressedSize < 16)
        Array.Resize(ref raw, (int)e.UncompressedSize);
      return raw;
    }

    private static bool IsZlibHeader(byte cmf, byte flg) =>
      (cmf & 0x0F) == 8 && (cmf >> 4) <= 7 && ((cmf << 8) | flg) % 31 == 0 && (flg & 0x20) == 0;

    private static byte[] Inflate(ReadOnlySpan<byte> raw, long limit)
    {
      try
      {
        using var zin = new ZLibStream(new MemoryStream(raw.ToArray(), writable: false), CompressionMode.Decompress);
        using var output = new MemoryStream();
        byte[] buf = new byte[81920];
        int n;
        while ((n = zin.Read(buf, 0, buf.Length)) > 0)
        {
          output.Write(buf, 0, n);
          if (output.Length > limit) break;
        }
        return output.ToArray();
      }
      // A bad header throws InvalidDataException; corrupt data a ZLibException (an
      // IOException). The source is in memory, so either means "not clean zlib".
      catch (Exception ex) when (ex is InvalidDataException or IOException) { return null; }
    }

    // ── classification ───────────────────────────────────────────────────

    private readonly struct Sniff
    {
      public Sniff(string ext, string kind, bool confident) { Extension = ext; Kind = kind; Confident = confident; }
      public string Extension { get; }
      public string Kind { get; }
      /// <summary>The content proves the extension (archive, video, audio bank).</summary>
      public bool Confident { get; }
    }

    /// <summary>Classifies a member from its first bytes (cheap: a few KB read).</summary>
    private static Sniff SniffMember(FileStream fs, Rpf9Entry e)
    {
      if (e.StoredSize < 4 || e.Offset + 4 > fs.Length)
        return new Sniff(".bin", "empty", false);
      int n = (int)Math.Min(e.StoredSize, 4096);
      byte[] head = new byte[n];
      fs.Seek(e.Offset, SeekOrigin.Begin);
      int got = fs.Read(head, 0, n);
      if (got < 4)
        return new Sniff(".bin", "empty", false);

      // Encrypted members: decrypt the head so it is classified by its real
      // content (the per-byte keystream only depends on the byte's offset, so a
      // prefix decrypts correctly on its own).
      if (e.IsEncrypted && e.EncDescriptor != null && _cipher != null && _cipher.PayloadReady)
        _cipher.DecryptPayload(head.AsSpan(0, got), e.UncompressedSize, e.EncDescriptor);

      uint magic = BinaryPrimitives.ReadUInt32LittleEndian(head);
      if (magic == Rpf9Magic) return new Sniff(".rpf", "RPF9 archive", true);
      if (head[0] == 'A' && head[1] == 'D' && head[2] == 'A' && head[3] == 'T') return new Sniff(".awc", "audio bank (ADAT)", true);
      if (head[0] == 'K' && head[1] == 'B' && head[2] == '2') return new Sniff(".bk2", "Bink 2 video", true);
      if (head[0] == 'B' && head[1] == 'I' && head[2] == 'K') return new Sniff(".bik", "Bink video", true);
      if (head[0] == 'S' && head[1] == 'G' && head[2] == 'D' && head[3] == '5') return new Sniff(".sgd5", "shader database (SGD5)", true);

      if (!e.IsResource && IsZlibHeader(head[0], head[1]))
      {
        byte[] peek = Inflate(head.AsSpan(0, got), 64);
        if (peek != null && peek.Length >= 4)
        {
          if (peek[0] == 'P' && peek[1] == 'S' && peek[2] == 'I' && peek[3] == 'N') return new Sniff(".psin", "zlib · PSIN metadata", false);
          if (LooksLikeXml(peek)) return new Sniff(".xml", "zlib · XML", false);
        }
        return new Sniff(".bin", "zlib", false);
      }

      // Resources: the record's extension code names the type (".ztd", ".zldbmt", ...).
      if (e.IsResource)
      {
        string ext = Rpf9Extensions.Name(e.ExtensionCode);
        return ext != null
          ? new Sniff(ext, ResourceKind(ext), true)
          : new Sniff(".r9resource", $"resource (type {e.ExtensionCode})", false);
      }
      if (e.StoredSize < e.UncompressedSize)
        return new Sniff(".bin", e.IsEncrypted || e.HasChunks ? "encrypted/chunked (not yet decoded)" : "transformed (not yet decoded)", false);

      bool allZero = true;
      for (int i = 0; i < Math.Min(got, 32); i++) if (head[i] != 0) { allZero = false; break; }
      return new Sniff(".bin", allZero ? "padding / empty" : "raw", false);
    }

    private static string ResourceKind(string ext) => ext switch
    {
      ".ztd" => "resource · texture dictionary",
      ".zdr" => "resource · drawable",
      ".zdd" => "resource · drawable dictionary",
      ".zft" => "resource · fragment",
      ".zldbmt" => "resource · text database (0DRP)",
      ".zmt" => "resource · metadata",
      ".zbn" => "resource · collision bounds",
      ".zcd" => "resource · clip dictionary",
      ".znv" => "resource · navmesh",
      _ => "resource · " + ext.TrimStart('.'),
    };

    private static bool LooksLikeXml(ReadOnlySpan<byte> s)
    {
      int i = 0;
      while (i < s.Length && (s[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or 0xEF or 0xBB or 0xBF))
        i++;
      return i < s.Length && s[i] == (byte)'<';
    }

    /// <summary>Formats a byte count as B / KB / MB / GB for display.</summary>
    private static string FriendlyBytes(long bytes)
    {
      if (bytes < 1024) return $"{bytes} B";
      double v = bytes;
      string[] units = { "KB", "MB", "GB", "TB" };
      int u = -1;
      do { v /= 1024; u++; } while (v >= 1024 && u < units.Length - 1);
      return $"{v:0.#} {units[u]}";
    }

    public override void Save() => throw new NotSupportedException("RPF9 archives are read-only.");
    public override void Rebuild() => throw new NotSupportedException("RPF9 archives are read-only.");
    public override void Close() { }
  }
}
