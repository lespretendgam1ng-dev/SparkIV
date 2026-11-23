/**********************************************************************\

 RageLib - SparkIV
 RDR2 (PC) RPF8 archive reader.

 Format reference: librdr3 (MIT) / rage-cli (Unlicense), verified against the game's archives. Specific to RDR2
 on PC.

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

namespace RageLib.Rdr2.Pc
{
  /// <summary>
  /// Reads a Red Dead Redemption 2 (PC) RPF8 archive. The table of contents starts at 0x110, is a flat array of
  /// 24-byte entries, and is CBC-encrypted with the TFIT2 key named by the header's tag (see
  /// <see cref="Rdr2Secrets"/>); an unencrypted archive uses tag 0xFF. There are no folders and no names in the
  /// archive: each entry carries the joaat hash of its path relative to the archive (see <see cref="Rdr2Names"/>).
  ///
  /// The archive is read from a stream: only the header and the table of contents are read when it is opened, an
  /// entry when it is extracted (the game's archives are up to 8 GB). Specific to RDR2 on PC; RPF8 is unrelated to
  /// the RPF2/RPF3 format GTA IV uses.
  /// </summary>
  public sealed class Rpf8Archive : IDisposable
  {
    public const uint Magic = 0x52504638; // 'RPF8'
    private const int HeaderSize = 0x110;

    public sealed class Entry
    {
      /// <summary>joaat of the entry's path relative to the archive (lower case; usually without extension).</summary>
      public uint Hash;
      /// <summary><c>&lt;HASH&gt;.&lt;ext&gt;</c>, the name when the path is not known.</summary>
      public string Name;
      /// <summary>The extension of the entry's type ("ydr", "rpf", "bin", ...), without the dot.</summary>
      public string Extension;
      public long Offset;       // byte offset of the stored payload in the archive
      public long DiskSize;     // bytes stored (compressed/encrypted) in the archive
      public long Size;         // uncompressed size
      public bool Resource;
      public bool Directory;
      public bool Encrypted;
      public int Tag;           // per-entry encryption key tag (0xFF = none)
      public int Compressor;    // 0 = stored, 1 = deflate, 2 = Oodle
      internal ulong A, B, C;
    }

    public IReadOnlyList<Entry> Entries => _entries;
    private readonly List<Entry> _entries = new();
    private readonly Rdr2Secrets _secrets;
    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private readonly object _sync;

    private Rpf8Archive(Rdr2Secrets secrets, Stream stream, bool ownsStream, object sync)
    {
      _secrets = secrets; _stream = stream; _ownsStream = ownsStream; _sync = sync ?? new object();
    }

    public static bool IsRpf8(ReadOnlySpan<byte> header)
      => header.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(header) == Magic;

    /// <summary>Whether the file at <paramref name="path"/> starts like an RPF8 archive.</summary>
    public static bool IsRpf8File(string path)
    {
      try
      {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        Span<byte> head = stackalloc byte[4];
        return fs.Read(head) == 4 && IsRpf8(head);
      }
      catch (IOException) { return false; }
      catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>Opens an archive file. The file stays open (shared for reading) until the archive is disposed.</summary>
    public static Rpf8Archive Open(string path, Rdr2Secrets secrets)
    {
      var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.RandomAccess);
      try { return Open(fs, secrets, ownsStream: true); }
      catch { fs.Dispose(); throw; }
    }

    /// <summary>
    /// Reads the header and table of contents from <paramref name="stream"/> (seekable, the archive starting at
    /// position 0). Throws if it is not an RDR2 PC RPF8 archive or if the decrypted entries do not stay within the
    /// archive (which is how a wrong key or corrupt table is caught).
    /// </summary>
    public static Rpf8Archive Open(Stream stream, Rdr2Secrets secrets, bool ownsStream = false)
      => Open(stream, secrets, ownsStream, null);

    private static Rpf8Archive Open(Stream stream, Rdr2Secrets secrets, bool ownsStream, object sync)
    {
      var header = new byte[HeaderSize];
      stream.Position = 0;
      if (ReadFully(stream, header, 0, HeaderSize) < HeaderSize) throw new InvalidDataException("RPF8 header is truncated.");
      if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic)
      {
        if (Array.TrueForAll(header, b => b == 0))
          throw new InvalidDataException("This archive is empty (all zeros): the game has not finished downloading it. Let the Rockstar Games Launcher complete the download.");
        throw new InvalidDataException("Not an RPF8 archive.");
      }
      ushort platform = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(14));
      if (platform != 'y') throw new InvalidDataException($"RPF8 platform 0x{platform:X4} is not RDR2 PC ('y').");
      int tag = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(12));
      long count = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
      long length = count * 24;
      if (length > int.MaxValue || HeaderSize + length > stream.Length) throw new InvalidDataException("RPF8 entry table is truncated.");

      var toc = new byte[length];
      if (ReadFully(stream, toc, 0, toc.Length) < toc.Length) throw new InvalidDataException("RPF8 entry table is truncated.");
      if (tag != Rdr2Secrets.NoEncryption)
      {
        if (secrets == null || !secrets.HasKey(tag))
          throw new InvalidDataException($"The RDR2 key 0x{tag:X2} for this archive is not available.");
        secrets.Decrypt(tag, toc);
      }

      var archive = new Rpf8Archive(secrets, stream, ownsStream, sync);
      for (int i = 0; i < count; i++)
      {
        int p = i * 24;
        ulong a = BinaryPrimitives.ReadUInt64LittleEndian(toc.AsSpan(p));
        ulong b = BinaryPrimitives.ReadUInt64LittleEndian(toc.AsSpan(p + 8));
        ulong c = BinaryPrimitives.ReadUInt64LittleEndian(toc.AsSpan(p + 16));

        int extId = (int)((a >> 48) & 0xFF);
        bool directory = extId == 0xFE;
        bool resource = (a & (1UL << 56)) != 0;
        long diskSize = (long)((b & 0xFFFFFFF) << 4);
        long offset = (long)(((b >> 28) & 0x7FFFFFFF) << 4);
        int compressor = (int)(b >> 59);
        long size = resource ? (long)((c & 0xFFFFFFF0) + ((c >> 32) & 0xFFFFFFF0)) : (long)c;
        int entTag = (int)((a >> 40) & 0xFF);
        string ext = directory ? "" : Extension(extId);

        var e = new Entry
        {
          Hash = (uint)a,
          Name = directory ? $"{(uint)a:X8}" : $"{(uint)a:X8}.{ext}",
          Extension = ext,
          Offset = offset,
          DiskSize = diskSize,
          Size = size,
          Resource = resource,
          Directory = directory,
          Encrypted = entTag != Rdr2Secrets.NoEncryption,
          Tag = entTag,
          Compressor = compressor,
          A = a, B = b, C = c,
        };
        if (!directory && offset + diskSize > stream.Length)
          throw new InvalidDataException($"RPF8 entry {e.Name} lies outside the archive (wrong keys or corrupt table of contents).");
        archive._entries.Add(e);
      }
      return archive;
    }

    /// <summary>Parses an archive held in memory.</summary>
    public static Rpf8Archive Parse(byte[] data, Rdr2Secrets secrets)
      => Open(new MemoryStream(data, writable: false), secrets, ownsStream: true);

    private static int ReadFully(Stream s, byte[] buffer, int offset, int count)
    {
      int total = 0;
      while (total < count)
      {
        int n = s.Read(buffer, offset + total, count - total);
        if (n <= 0) break;
        total += n;
      }
      return total;
    }

    /// <summary>
    /// Whether <see cref="OpenNested"/> can open the entry in place: an archive stored without compression and
    /// encryption, which is how the game keeps the archives inside its archives.
    /// </summary>
    public bool CanOpenInPlace(int index)
    {
      var e = _entries[index];
      return !e.Directory && !e.Resource && e.Compressor == 0 && !e.Encrypted && e.Size >= HeaderSize && e.Size <= e.DiskSize;
    }

    /// <summary>Opens an archive inside this one in place, reading through this archive's stream (no copy).</summary>
    public Rpf8Archive OpenNested(int index)
    {
      if (!CanOpenInPlace(index)) throw new InvalidOperationException("This entry cannot be opened in place.");
      var e = _entries[index];
      var window = new WindowStream(_stream, e.Offset, e.Size, _sync);
      return Open(window, _secrets, ownsStream: true, _sync);
    }

    /// <summary>Reads and decrypts/decompresses one entry's payload.</summary>
    public byte[] Extract(int index)
    {
      var e = _entries[index];
      if (e.Directory) throw new InvalidOperationException("Directory entries have no payload.");
      long start = e.Offset;
      long raw = e.DiskSize;
      if ((e.A & (1UL << 57)) != 0) raw -= 0x100;     // trailing signature
      if (e.Resource) { start += 16; raw -= 16; }      // resource header
      if (raw < 0) throw new InvalidDataException("RPF8 entry payload is truncated.");
      long size = e.Size;
      long length = e.Compressor == 0 ? size : raw;
      if (start + length > _stream.Length) throw new InvalidDataException("RPF8 entry payload is out of range.");
      if (length > int.MaxValue || size > int.MaxValue) throw new InvalidDataException("RPF8 entry is too large.");

      var input = new byte[length];
      lock (_sync)
      {
        _stream.Position = start;
        if (ReadFully(_stream, input, 0, input.Length) < input.Length) throw new InvalidDataException("RPF8 entry payload is truncated.");
      }
      if (e.Tag != Rdr2Secrets.NoEncryption)
      {
        if (_secrets == null || !_secrets.HasKey(e.Tag))
          throw new InvalidDataException($"The RDR2 key 0x{e.Tag:X2} for this file is not available.");
        int chunk = e.Resource ? (e.Compressor != 0 ? 0x80000 : (int)size)
                               : (e.Compressor != 0 ? 0x2000 : 0x1000);
        _secrets.DecryptRanges(e.Tag, input, CipherRanges((int)((e.A >> 32) & 0xFF), (int)raw, chunk));
      }

      byte[] payload = Decompress(e.Compressor, input, (int)size);
      if (!e.Resource) return payload;

      // Rebuild the RSC8 resource header the game strips when packing.
      uint version = (((uint)e.C & 0xF) << 4) | (((uint)(e.C >> 32)) & 0xF);
      var outBuf = new byte[16 + payload.Length];
      BinaryPrimitives.WriteUInt32LittleEndian(outBuf.AsSpan(0), 0x38435352); // 'RSC8'
      BinaryPrimitives.WriteUInt32LittleEndian(outBuf.AsSpan(4), version | 0x1F00);
      BinaryPrimitives.WriteUInt32LittleEndian(outBuf.AsSpan(8), (uint)e.C);
      BinaryPrimitives.WriteUInt32LittleEndian(outBuf.AsSpan(12), (uint)(e.C >> 32));
      Array.Copy(payload, 0, outBuf, 16, payload.Length);
      return outBuf;
    }

    public void Dispose()
    {
      if (_ownsStream) _stream.Dispose();
    }

    private static byte[] Decompress(int compressor, byte[] input, int size)
    {
      switch (compressor)
      {
        case 0:
          if (input.Length < size) throw new InvalidDataException("RDR2 stored payload is truncated.");
          return input.Length == size ? input : input[..size];
        case 1:
          using (var ds = new DeflateStream(new MemoryStream(input), CompressionMode.Decompress))
          {
            var outBuf = new byte[size];
            int read = 0;
            while (read < size)
            {
              int n = ds.Read(outBuf, read, size - read);
              if (n == 0) break;
              read += n;
            }
            if (read != size) throw new InvalidDataException("RDR2 deflate size mismatch.");
            return outBuf;
          }
        case 2:
          return Rdr2Oodle.Decompress(input, size);
        default:
          throw new NotSupportedException($"RDR2 compressor {compressor} is not supported.");
      }
    }

    // The game encrypts only parts of larger payloads; this reproduces which byte ranges are enciphered.
    private static IReadOnlyList<(int start, int end)> CipherRanges(int config, int size, int chunk)
    {
      int head = (config & 3) == 0 ? 0 : 0x400 << ((config & 3) * 2);
      int lenBits = (config >> 2) & 7;
      int length = lenBits == 0 ? 0 : 0x400 << lenBits;
      int stride = lenBits == 0 ? 0 : (((config >> 5) & 7) + 1) << 16;
      int tail = Math.Max(0, size - 0x400);
      var result = new List<(int, int)>();
      int offset = Math.Min(head, size);
      if (offset > 0) result.Add((0, offset));
      if (head < tail)
      {
        if (stride != 0)
        {
          if (stride == chunk && tail < stride && stride < size)
          {
            offset = stride;
          }
          else
          {
            int block = offset != 0 ? stride : 0;
            while (block + length <= tail)
            {
              int s = Math.Max(block, offset);
              int en = Math.Min(block + length, size);
              if (s < en) { result.Add((s, en)); offset = en; }
              block += stride;
            }
          }
        }
        int start = Math.Max(tail, offset);
        if (start < size) result.Add((start, size));
      }
      return result;
    }

    private static readonly string[] BaseExt =
    {
      "rpf", "ymf", "ydr", "yft", "ydd", "ytd", "ybn", "ybd", "ypd", "ybs", "ysd", "ymt", "ysc", "ycs",
    };
    private static readonly string[] ExtraExt =
    {
      "mrf", "cut", "gfx", "ycd", "yld", "ypmd", "ypm", "yed", "ypt", "ymap", "ytyp", "ych",
      "yldb", "yjd", "yad", "ynv", "yhn", "ypl", "ynd", "yvr", "ywr", "ynh", "yfd", "yas",
    };

    private static string Extension(int id)
    {
      if (id < BaseExt.Length) return BaseExt[id];
      int x = id - 64;
      if (x >= 0 && x < ExtraExt.Length) return ExtraExt[x];
      return "bin";
    }

    /// <summary>A read-only window of another stream (an archive inside an archive), sharing its lock.</summary>
    private sealed class WindowStream : Stream
    {
      private readonly Stream _base;
      private readonly long _start, _length;
      private readonly object _sync;
      private long _position;

      public WindowStream(Stream baseStream, long start, long length, object sync)
      {
        _base = baseStream; _start = start; _length = length; _sync = sync;
      }

      public override bool CanRead => true;
      public override bool CanSeek => true;
      public override bool CanWrite => false;
      public override long Length => _length;
      public override long Position { get => _position; set => _position = value; }

      public override int Read(byte[] buffer, int offset, int count)
      {
        long left = _length - _position;
        if (left <= 0) return 0;
        if (count > left) count = (int)left;
        int n;
        lock (_sync)
        {
          _base.Position = _start + _position;
          n = _base.Read(buffer, offset, count);
        }
        _position += n;
        return n;
      }

      public override long Seek(long offset, SeekOrigin origin)
      {
        _position = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => _position + offset, _ => _length + offset };
        return _position;
      }

      public override void Flush() { }
      public override void SetLength(long value) => throw new NotSupportedException();
      public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
  }
}
