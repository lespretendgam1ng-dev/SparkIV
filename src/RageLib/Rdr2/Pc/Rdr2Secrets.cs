/**********************************************************************\

 RageLib - SparkIV
 RDR2 (PC) TFIT2 secret discovery and decryption.

 Method and public secret identifiers: librdr3 (MIT) / rage-cli (Unlicense).
 The identifiers are fingerprints (length + two hashes), NOT key material;
 SparkIV ships only these fingerprints and reads the actual keys and cipher
 tables from the user's own RDR2.exe at runtime.

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
using System.Reflection;

namespace RageLib.Rdr2.Pc
{
  /// <summary>
  /// The TFIT2 cipher, per-archive keys and IV recovered from the user's own RDR2.exe. SparkIV never ships any
  /// of this material: it ships only the public <b>fingerprints</b> of each secret (a length and two hashes),
  /// and locates the matching bytes inside the executable the user already owns. A secret is accepted only if
  /// its content hashes to a known fingerprint, so the recovery is exact and survives game updates that move
  /// the data around (as long as the key material itself is unchanged).
  ///
  /// Specific to Red Dead Redemption 2 on PC; it does not apply to other RAGE titles.
  /// </summary>
  public sealed class Rdr2Secrets
  {
    public const int NoEncryption = 0xFF;

    private readonly struct Fp : IEquatable<Fp>
    {
      public readonly uint Len; public readonly uint Hash; public readonly ulong NotCrc;
      public Fp(uint len, uint hash, ulong notCrc) { Len = len; Hash = hash; NotCrc = notCrc; }
      public bool Equals(Fp o) => Len == o.Len && Hash == o.Hash && NotCrc == o.NotCrc;
      public override bool Equals(object o) => o is Fp f && Equals(f);
      public override int GetHashCode() => HashCode.Combine(Len, Hash, NotCrc);
    }

    public Tfit2Cipher Cipher { get; }
    private readonly Dictionary<int, ulong[]> _keys;
    private readonly byte[] _iv;
    private readonly byte[][] _context;   // the raw cipher tables in Rdr2ContextIds.txt order, for the cache

    public int KeyCount => _keys.Count;
    public bool HasKey(int tag) => tag == NoEncryption || _keys.ContainsKey(tag);
    public byte[] Iv => (byte[])_iv.Clone();
    public IReadOnlyDictionary<int, ulong[]> Keys => _keys;

    private Rdr2Secrets(Tfit2Cipher cipher, Dictionary<int, ulong[]> keys, byte[] iv, byte[][] context)
    {
      Cipher = cipher; _keys = keys; _iv = iv; _context = context;
    }

    // -------------------------------------------------------------- fingerprints

    private const uint Factor = 0x9E3779B1u;
    private const string Alphabet =
      "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz!#$%&()*+-;<=>?@^_`{|}~";

    /// <summary>The 16-byte fingerprint of a secret: its length, a multiplicative hash and a CRC-64.</summary>
    private static Fp Fingerprint(ReadOnlySpan<byte> bytes)
    {
      uint hash = 0;
      unchecked { for (int i = 0; i < bytes.Length; i++) hash = (hash + bytes[i]) * Factor; }
      ulong crc = ~0UL;
      for (int i = 0; i < bytes.Length; i++)
      {
        crc ^= bytes[i];
        for (int k = 0; k < 8; k++)
          crc = (crc >> 1) ^ (0xC96C5795D7870F42UL & (0UL - (crc & 1)));
      }
      return new Fp((uint)bytes.Length, hash, ~crc);
    }

    /// <summary>Decodes a base-85 identifier string into the same fingerprint Fingerprint() produces.</summary>
    private static Fp DecodeId(string id)
    {
      Span<byte> raw = stackalloc byte[16];
      for (int c = 0; c * 5 < id.Length && c < 4; c++)
      {
        uint n = 0;
        unchecked { for (int k = 0; k < 5; k++) n = n * 85 + (uint)Alphabet.IndexOf(id[c * 5 + k]); }
        BinaryPrimitives.WriteUInt32BigEndian(raw[(c * 4)..], n);
      }
      return new Fp(
        BinaryPrimitives.ReadUInt32LittleEndian(raw),
        BinaryPrimitives.ReadUInt32LittleEndian(raw[4..]),
        BinaryPrimitives.ReadUInt64LittleEndian(raw[8..]));
    }

    // -------------------------------------------------------------- scan

    /// <summary>
    /// Scans <paramref name="exe"/> for every wanted secret. For each distinct secret length it runs a single
    /// rolling-hash pass; the cheap rolling hash is a pre-filter, and the full fingerprint (CRC and length) is
    /// confirmed only where the hash matches a wanted value, so a 32 KB table costs one pass, not one per byte.
    /// </summary>
    private static Dictionary<Fp, byte[]> FindSecrets(byte[] exe, HashSet<Fp> wanted)
      => FindSecrets(exe, exe.Length, wanted);

    /// <summary>As above, over the first <paramref name="length"/> bytes of <paramref name="data"/>.</summary>
    private static Dictionary<Fp, byte[]> FindSecrets(byte[] data, int length, HashSet<Fp> wanted)
    {
      var exe = new ReadOnlySpan<byte>(data, 0, length);
      var found = new Dictionary<Fp, byte[]>();
      // Group wanted fingerprints by length, then by their rolling-hash value.
      var bySize = new Dictionary<int, Dictionary<uint, List<Fp>>>();
      foreach (var fp in wanted)
      {
        if (!bySize.TryGetValue((int)fp.Len, out var byHash)) bySize[(int)fp.Len] = byHash = new();
        if (!byHash.TryGetValue(fp.Hash, out var list)) byHash[fp.Hash] = list = new();
        list.Add(fp);
      }

      foreach (var (size, byHash) in bySize)
      {
        if (size <= 0 || size > exe.Length) continue;
        uint factor = 1; unchecked { for (int i = 0; i < size; i++) factor *= Factor; }
        uint rolling = 0; unchecked { for (int i = 0; i < size; i++) rolling = (rolling + exe[i]) * Factor; }
        for (int start = 0; start + size <= exe.Length; start++)
        {
          if (byHash.TryGetValue(rolling, out var candidates))
          {
            var fp = Fingerprint(exe.Slice(start, size));
            if (candidates.Contains(fp) && !found.ContainsKey(fp))
              found[fp] = exe.Slice(start, size).ToArray();
          }
          if (start + size < exe.Length)
            unchecked { rolling = (rolling - exe[start] * factor + exe[start + size]) * Factor; }
        }
      }
      return found;
    }

    private static ulong[] Words(byte[] data)
    {
      var a = new ulong[data.Length / 8];
      for (int i = 0; i < a.Length; i++) a[i] = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(i * 8));
      return a;
    }

    // -------------------------------------------------------------- assembly

    private const string IvId = "5C8xGcF{L^AxqTQd!ofA";

    /// <summary>Recovers the secrets from the bytes of RDR2.exe using the embedded fingerprint lists.</summary>
    public static Rdr2Secrets Extract(byte[] exe)
      => Extract(exe, LoadIds("Rdr2KeyIds.txt"), LoadIds("Rdr2ContextIds.txt"), IvId);

    /// <summary>Checks the fingerprint implementation against librdr3's published vector for "123456789".</summary>
    public static bool FingerprintSelfTest()
    {
      var fp = Fingerprint(System.Text.Encoding.ASCII.GetBytes("123456789"));
      return fp.NotCrc == 0x995DC9BBDF1939FAUL;
    }

    /// <summary>Length a fingerprint id decodes to (for diagnostics/tests).</summary>
    public static int DecodedLength(string id) => (int)DecodeId(id).Len;

    /// <summary>Diagnostics: the (keyIndex, fileOffset) of every key fingerprint found in the executable.</summary>
    public static List<(int index, long offset)> FindKeyOffsets(byte[] exe, string[] keyIds)
    {
      var want = new Dictionary<Fp, int>();
      for (int i = 0; i < keyIds.Length; i++) want[DecodeId(keyIds[i])] = i;
      var result = new List<(int, long)>();
      int size = (int)DecodeId(keyIds[0]).Len; // all keys are 288 bytes
      uint factor = 1; unchecked { for (int i = 0; i < size; i++) factor *= Factor; }
      uint rolling = 0; unchecked { for (int i = 0; i < size; i++) rolling = (rolling + exe[i]) * Factor; }
      var byHash = new HashSet<uint>();
      foreach (var fp in want.Keys) byHash.Add(fp.Hash);
      for (int start = 0; start + size <= exe.Length; start++)
      {
        if (byHash.Contains(rolling))
        {
          var fp = Fingerprint(exe.AsSpan(start, size));
          if (want.TryGetValue(fp, out int idx)) result.Add((idx, start));
        }
        if (start + size < exe.Length)
          unchecked { rolling = (rolling - exe[start] * factor + exe[start + size]) * Factor; }
      }
      return result;
    }

    /// <summary>Maps a key fingerprint's list position to its TOC tag (the last entries use fixed tags).</summary>
    private static int TagForIndex(int i) => i switch { 163 => 0xC0, 164 => 0xC5, 165 => 0xC6, _ => i };

    /// <summary>
    /// Scans an arbitrary buffer (the executable, or a region of the running game's memory) for the per-archive
    /// keys, returning them indexed by TOC tag. Only the content that hashes to a known key fingerprint is kept,
    /// so a scan of unrelated memory simply yields nothing.
    /// </summary>
    public static Dictionary<int, ulong[]> FindKeys(byte[] data, string[] keyIds = null)
    {
      keyIds ??= LoadIds("Rdr2KeyIds.txt");
      var wanted = new HashSet<Fp>();
      foreach (var id in keyIds) wanted.Add(DecodeId(id));
      var store = FindSecrets(data, wanted);
      var keys = new Dictionary<int, ulong[]>();
      for (int i = 0; i < keyIds.Length; i++)
        if (store.TryGetValue(DecodeId(keyIds[i]), out var d))
          keys[TagForIndex(i)] = Words(d);
      return keys;
    }

    /// <summary>The raw cipher tables (in id order) and IV located in <paramref name="store"/>; throws if one is missing.</summary>
    private static (byte[][] context, byte[] iv) ContextFromStore(Dictionary<Fp, byte[]> store, string[] contextIds, string ivId)
    {
      var context = new byte[contextIds.Length][];
      for (int i = 0; i < contextIds.Length; i++)
        if (!store.TryGetValue(DecodeId(contextIds[i]), out context[i]))
          throw new InvalidDataException("The RDR2 cipher tables were not found in RDR2.exe (an unexpected or incomplete file).");
      var iv = store.TryGetValue(DecodeId(ivId), out var v) ? v : throw new InvalidDataException("RDR2 cipher IV not found in the executable.");
      if (iv.Length != 16) throw new InvalidDataException("RDR2 cipher IV has the wrong size.");
      return (context, iv);
    }

    /// <summary>Builds the TFIT2 cipher from its raw tables (in Rdr2ContextIds.txt order).</summary>
    private static Tfit2Cipher BuildCipher(byte[][] context)
    {
      int ci = 0;
      byte[] NextCtx(string what)
      {
        if (ci >= context.Length) throw new InvalidDataException($"RDR2 cipher table list exhausted at {what}.");
        return context[ci++] ?? throw new InvalidDataException($"RDR2 cipher table '{what}' is missing.");
      }

      var cipher = new Tfit2Cipher
      {
        Init = Words(NextCtx("init")),
        Lookups = new ulong[17][],
        Masks = new ulong[272][],
        Xors = new int[272],
      };
      for (int r = 0; r < 17; r++)
      {
        cipher.Lookups[r] = Words(NextCtx($"lookup[{r}]"));
        for (int b = 0; b < 16; b++)
        {
          cipher.Masks[r * 16 + b] = Words(NextCtx($"masks[{r},{b}]"));
          var xb = NextCtx($"xor[{r},{b}]");
          int x = (int)BinaryPrimitives.ReadUInt32LittleEndian(xb);
          if (x >= 4096) throw new InvalidDataException("Invalid TFIT2 round xor index.");
          cipher.Xors[r * 16 + b] = x;
        }
      }
      cipher.FinalMasks = Words(NextCtx("final masks"));
      cipher.EndTable = NextCtx("end table");
      cipher.XorTable = NextCtx("final xor");
      return cipher;
    }

    /// <summary>Core recovery with explicit fingerprint lists (used by tests).</summary>
    public static Rdr2Secrets Extract(byte[] exe, string[] keyIds, string[] contextIds, string ivId, Action<string> log = null)
    {
      log ??= _ => { };
      var wanted = new HashSet<Fp>();
      foreach (var id in keyIds) wanted.Add(DecodeId(id));
      foreach (var id in contextIds) wanted.Add(DecodeId(id));
      wanted.Add(DecodeId(ivId));

      var store = FindSecrets(exe, wanted);
      log($"secrets found: {store.Count} / {wanted.Count}");

      var keys = new Dictionary<int, ulong[]>();
      for (int i = 0; i < keyIds.Length; i++)
        if (store.TryGetValue(DecodeId(keyIds[i]), out var d))
          keys[TagForIndex(i)] = Words(d);
      log($"keys found: {keys.Count} / {keyIds.Length}");
      if (keys.Count == 0) throw new InvalidDataException("No RDR2 TFIT2 keys found in the executable (packed or unexpected build?).");

      var (context, iv) = ContextFromStore(store, contextIds, ivId);
      return new Rdr2Secrets(BuildCipher(context), keys, iv, context);
    }

    /// <summary>
    /// Builds a usable <see cref="Rdr2Secrets"/> from the cipher tables and IV in the user's own RDR2.exe plus a
    /// set of per-archive keys obtained elsewhere (from the running game's memory, or a local cache). The retail
    /// executable is protector-packed, so the full keys are not statically present; only the cipher tables and IV
    /// are, and those are read here from <paramref name="exe"/>.
    /// </summary>
    public static Rdr2Secrets Create(byte[] exe, Dictionary<int, ulong[]> keys)
    {
      if (keys == null || keys.Count == 0)
        throw new InvalidDataException("No RDR2 TFIT2 keys supplied (run the one-time key extraction with the game open).");
      var contextIds = LoadIds("Rdr2ContextIds.txt");
      var wanted = new HashSet<Fp>();
      foreach (var id in contextIds) wanted.Add(DecodeId(id));
      wanted.Add(DecodeId(IvId));
      var store = FindSecrets(exe, wanted);
      var (context, iv) = ContextFromStore(store, contextIds, IvId);
      return new Rdr2Secrets(BuildCipher(context), new Dictionary<int, ulong[]>(keys), iv, context);
    }

    /// <summary>
    /// Collects the keys and the cipher tables from any number of buffers (the regions of the running game's
    /// memory, or the executable), so the game's memory can be read piece by piece. Only content that hashes to a
    /// known fingerprint is kept; unrelated memory yields nothing.
    /// </summary>
    public sealed class Scan
    {
      private readonly string[] _keyIds = LoadIds("Rdr2KeyIds.txt");
      private readonly string[] _contextIds = LoadIds("Rdr2ContextIds.txt");
      private readonly HashSet<Fp> _wanted = new();
      private readonly Dictionary<Fp, byte[]> _store = new();
      private readonly HashSet<Fp> _keyFps = new();
      private readonly HashSet<Fp> _cipherFps = new();

      public Scan()
      {
        foreach (var id in _keyIds) { var fp = DecodeId(id); _wanted.Add(fp); _keyFps.Add(fp); }
        foreach (var id in _contextIds) { var fp = DecodeId(id); _wanted.Add(fp); _cipherFps.Add(fp); }
        var ivFp = DecodeId(IvId); _wanted.Add(ivFp); _cipherFps.Add(ivFp);
        foreach (var fp in _wanted) MaxSecretLength = Math.Max(MaxSecretLength, (int)fp.Len);
      }

      /// <summary>The longest secret; buffers read piece by piece must overlap by this much less one byte.</summary>
      public int MaxSecretLength { get; }

      /// <summary>Number of the 165 per-archive keys found so far.</summary>
      public int KeysFound
      {
        get { lock (_store) { int n = 0; foreach (var fp in _keyFps) if (_store.ContainsKey(fp)) n++; return n; } }
      }

      public int KeyTotal => _keyFps.Count;

      /// <summary>Whether all cipher tables and the IV were found.</summary>
      public bool CipherFound
      {
        get { lock (_store) { foreach (var fp in _cipherFps) if (!_store.ContainsKey(fp)) return false; return true; } }
      }

      public bool Complete { get { lock (_store) return _wanted.Count == 0; } }

      /// <summary>Whether all 165 keys were found (the cipher tables may still be missing).</summary>
      public bool KeysComplete => KeysFound == KeyTotal;

      /// <summary>
      /// Searches <paramref name="buffer"/> (its first <paramref name="length"/> bytes) for what is still missing:
      /// the keys, the cipher tables, or both. Each length needs its own pass over the buffer, so a search for only
      /// the keys (one length) is several times faster than one for everything. Safe to call from several threads.
      /// </summary>
      public void Add(byte[] buffer, int length, bool keys = true, bool cipher = true)
      {
        if (length <= 0) return;
        var wanted = new HashSet<Fp>();
        lock (_store)
        {
          foreach (var fp in _wanted)
            if (keys && _keyFps.Contains(fp) || cipher && _cipherFps.Contains(fp)) wanted.Add(fp);
        }
        if (wanted.Count == 0) return;
        var found = FindSecrets(buffer, length, wanted);
        lock (_store)
        {
          foreach (var (fp, bytes) in found)
          {
            _store[fp] = bytes;
            _wanted.Remove(fp);
          }
        }
      }

      /// <summary>The keys found, by archive tag.</summary>
      public Dictionary<int, ulong[]> Keys
      {
        get
        {
          var keys = new Dictionary<int, ulong[]>();
          for (int i = 0; i < _keyIds.Length; i++)
            if (_store.TryGetValue(DecodeId(_keyIds[i]), out var d)) keys[TagForIndex(i)] = Words(d);
          return keys;
        }
      }

      /// <summary>
      /// The secrets: the keys found and the cipher tables found, or, if those were not found, the ones in
      /// <paramref name="exe"/> (the user's RDR2.exe; may be null).
      /// </summary>
      public Rdr2Secrets Build(byte[] exe = null)
      {
        var keys = Keys;
        if (keys.Count == 0) throw new InvalidDataException("No RDR2 keys were found.");
        if (CipherFound)
        {
          var (context, iv) = ContextFromStore(_store, _contextIds, IvId);
          return new Rdr2Secrets(BuildCipher(context), keys, iv, context);
        }
        if (exe == null) throw new InvalidDataException("The RDR2 cipher tables were not found.");
        return Create(exe, keys);
      }
    }

    // -------------------------------------------------------------- key cache

    private const uint CacheMagic = 0x32434B52;     // "RKC2": keys only (the cipher tables come from RDR2.exe)
    private const uint CacheMagicFull = 0x33434B52; // "RKC3": keys, then the cipher tables and IV
    private const int KeyWords = 288 / 8;           // a TFIT2 key is 288 bytes = 36 little-endian words

    /// <summary>
    /// Writes everything needed to read the archives - the keys, the cipher tables and the IV - to a local cache
    /// file (never shipped; it lives on the user's disk), so neither the game nor RDR2.exe is needed again.
    /// </summary>
    public void SaveCache(string path)
    {
      var dir = Path.GetDirectoryName(path);
      if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
      var tmp = path + ".tmp";
      using (var w = new BinaryWriter(File.Create(tmp)))
      {
        w.Write(CacheMagicFull);
        WriteKeys(w, _keys);
        w.Write(_context.Length);
        foreach (var c in _context) { w.Write(c.Length); w.Write(c); }
        w.Write(_iv);
      }
      File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// Reads a cache written by <see cref="SaveCache"/>. An older cache with only the keys needs the cipher tables
    /// from <paramref name="exe"/> (RDR2.exe, loaded only then). Null if there is no usable cache.
    /// </summary>
    public static Rdr2Secrets LoadCache(string path, Func<byte[]> exe = null)
    {
      if (!File.Exists(path)) return null;
      try
      {
        using var r = new BinaryReader(File.OpenRead(path));
        uint magic = r.ReadUInt32();
        if (magic != CacheMagic && magic != CacheMagicFull) return null;
        var keys = ReadKeys(r);
        if (keys == null) return null;
        if (magic == CacheMagicFull)
        {
          int count = r.ReadInt32();
          if (count != LoadIds("Rdr2ContextIds.txt").Length) return null;
          var context = new byte[count][];
          for (int i = 0; i < count; i++)
          {
            int len = r.ReadInt32();
            if (len <= 0 || len > 1 << 20) return null;
            context[i] = r.ReadBytes(len);
            if (context[i].Length != len) return null;
          }
          var iv = r.ReadBytes(16);
          if (iv.Length != 16) return null;
          return new Rdr2Secrets(BuildCipher(context), keys, iv, context);
        }
        var bytes = exe?.Invoke();
        return bytes == null ? null : Create(bytes, keys);
      }
      // A truncated or damaged cache, an unreadable one, or (old cache) an exe without the tables: set up again.
      catch (IOException) { return null; }   // includes EndOfStreamException
      catch (InvalidDataException) { return null; }
      catch (UnauthorizedAccessException) { return null; }
    }

    private static void WriteKeys(BinaryWriter w, IReadOnlyDictionary<int, ulong[]> keys)
    {
      w.Write(keys.Count);
      foreach (var (tag, words) in keys)
      {
        if (words.Length != KeyWords) throw new InvalidDataException("RDR2 key has the wrong size for the cache.");
        w.Write(tag);
        foreach (var word in words) w.Write(word);
      }
    }

    private static Dictionary<int, ulong[]> ReadKeys(BinaryReader r)
    {
      int count = r.ReadInt32();
      if (count <= 0 || count > 4096) return null;
      var keys = new Dictionary<int, ulong[]>(count);
      for (int i = 0; i < count; i++)
      {
        int tag = r.ReadInt32();
        var words = new ulong[KeyWords];
        for (int k = 0; k < KeyWords; k++) words[k] = r.ReadUInt64();
        keys[tag] = words;
      }
      return keys;
    }

    /// <summary>Writes the recovered per-archive keys to a local cache file (never shipped; lives on the user's disk).</summary>
    public static void SaveKeyCache(string path, IReadOnlyDictionary<int, ulong[]> keys)
    {
      var dir = Path.GetDirectoryName(path);
      if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
      using var w = new BinaryWriter(File.Create(path));
      w.Write(CacheMagic);
      WriteKeys(w, keys);
    }

    /// <summary>Reads only the keys of a cache (either version), or returns null if it is missing or unreadable.</summary>
    public static Dictionary<int, ulong[]> LoadKeyCache(string path)
    {
      if (!File.Exists(path)) return null;
      try
      {
        using var r = new BinaryReader(File.OpenRead(path));
        uint magic = r.ReadUInt32();
        return magic == CacheMagic || magic == CacheMagicFull ? ReadKeys(r) : null;
      }
      catch (IOException) { return null; }   // includes EndOfStreamException for a truncated cache
    }

    private static string[] LoadIds(string resourceLeaf)
    {
      var asm = Assembly.GetExecutingAssembly();
      using var s = asm.GetManifestResourceStream("RageLib.Rdr2.Pc." + resourceLeaf)
        ?? throw new InvalidDataException($"Embedded RDR2 id list '{resourceLeaf}' is missing.");
      using var r = new StreamReader(s);
      var list = new List<string>();
      string line;
      while ((line = r.ReadLine()) != null)
        if (line.Length > 0) list.Add(line);
      return list.ToArray();
    }

    // -------------------------------------------------------------- decrypt

    /// <summary>Decrypts <paramref name="data"/> in place with the key for <paramref name="tag"/> (CBC).</summary>
    public void Decrypt(int tag, byte[] data) => DecryptRanges(tag, data, new[] { (0, data.Length) });

    /// <summary>
    /// Decrypts the given byte ranges of <paramref name="data"/> in place. CBC chaining continues across the
    /// ranges; bytes outside them are left as stored, and a trailing partial 16-byte block is left unchanged.
    /// </summary>
    public void DecryptRanges(int tag, byte[] data, IReadOnlyList<(int start, int end)> ranges)
    {
      if (tag == NoEncryption) return;
      if (!_keys.TryGetValue(tag, out var key))
        throw new InvalidDataException($"RDR2 TFIT2 key 0x{tag:X2} is not present.");
      var iv = (byte[])_iv.Clone();
      var ct = new byte[16];
      foreach (var (start, rangeEnd) in ranges)
      {
        int end = Math.Min(rangeEnd, data.Length);
        for (int pos = start; pos + 16 <= end; pos += 16)
        {
          new ReadOnlySpan<byte>(data, pos, 16).CopyTo(ct);
          var pt = Cipher.DecryptBlock(key, ct);
          for (int i = 0; i < 16; i++) data[pos + i] = (byte)(pt[i] ^ iv[i]);
          (iv, ct) = (ct, iv);
        }
      }
    }
  }
}
