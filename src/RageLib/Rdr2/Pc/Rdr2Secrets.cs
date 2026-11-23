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

    public int KeyCount => _keys.Count;
    public bool HasKey(int tag) => tag == NoEncryption || _keys.ContainsKey(tag);
    public byte[] Iv => (byte[])_iv.Clone();

    private Rdr2Secrets(Tfit2Cipher cipher, Dictionary<int, ulong[]> keys, byte[] iv)
    {
      Cipher = cipher; _keys = keys; _iv = iv;
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
    {
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
            var fp = Fingerprint(exe.AsSpan(start, size));
            if (candidates.Contains(fp) && !found.ContainsKey(fp))
              found[fp] = exe[start..(start + size)];
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

    /// <summary>Core recovery with explicit fingerprint lists (used by tests).</summary>
    public static Rdr2Secrets Extract(byte[] exe, string[] keyIds, string[] contextIds, string ivId, Action<string> log = null)
    {
      log ??= _ => { };
      // Decode every wanted fingerprint; FindSecrets derives the distinct secret sizes from them.
      var wanted = new HashSet<Fp>();
      foreach (var id in keyIds) wanted.Add(DecodeId(id));
      foreach (var id in contextIds) wanted.Add(DecodeId(id));
      wanted.Add(DecodeId(ivId));

      var store = FindSecrets(exe, wanted);
      byte[] Get(string id) => store.TryGetValue(DecodeId(id), out var b) ? b : null;
      log($"secrets found: {store.Count} / {wanted.Count}");

      // Per-archive keys, indexed by their TOC tag (the last three use fixed tags).
      var keys = new Dictionary<int, ulong[]>();
      int keysFound = 0;
      for (int i = 0; i < keyIds.Length; i++)
      {
        var data = Get(keyIds[i]);
        if (data == null) continue;
        keysFound++;
        int tag = i switch { 163 => 0xC0, 164 => 0xC5, 165 => 0xC6, _ => i };
        keys[tag] = Words(data);
      }
      log($"keys found: {keysFound} / {keyIds.Length}");
      if (keys.Count == 0) throw new InvalidDataException("No RDR2 TFIT2 keys found in the executable (packed or unexpected build?).");

      // Cipher tables, consumed from the context list in order:
      //   init, 17 x (lookup, 16 x (masks, xor)), final masks, end table, final xor.
      int ci = 0;
      byte[] NextCtx(string what)
      {
        if (ci >= contextIds.Length) throw new InvalidDataException($"RDR2 cipher table list exhausted at {what}.");
        var d = Get(contextIds[ci++]);
        if (d == null) throw new InvalidDataException($"RDR2 cipher table '{what}' not found in the executable.");
        return d;
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

      var iv = Get(ivId) ?? throw new InvalidDataException("RDR2 cipher IV not found in the executable.");
      if (iv.Length != 16) throw new InvalidDataException("RDR2 cipher IV has the wrong size.");

      return new Rdr2Secrets(cipher, keys, iv);
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
      foreach (var (start, end) in ranges)
      {
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
