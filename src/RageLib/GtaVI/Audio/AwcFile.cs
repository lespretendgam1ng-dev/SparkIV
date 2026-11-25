/**********************************************************************\

 RageLib - SparkIV
 GTA VI (RPF9) audio wave container (.awc) parser.

 An AWC ("ADAT") packs several audio streams plus metadata. Its layout is the
 RAGE audio-wave-container format (the same bit-packed stream/chunk tables that
 CodeWalker reads for GTA5/RDR2), but GTA VI's variant differs in a few places:

   * the header's flag word carries two bits CodeWalker never saw - an optional
     4-byte pad after the 16-byte header, and an opaque "(count+1) x u32" prefix
     table - and both must be skipped to reach the stream-info table;
   * the per-stream format chunk is 32 bytes (CodeWalker's is 20/24) and now
     stores the codec at byte 28 and the channel count at byte 29;
   * new codecs appear: 5 = XMA2 and 0x0B = Opus (CodeWalker only had PCM/ADPCM).

 The chunk tag bit-packing (type<<56 | size<<28 | offset) and the stream-info
 bit-packing (tagCount<<29 | id) are unchanged from CodeWalker, and were verified
 byte-for-byte against real GTA VI members (sfx/*.rpf audio banks in audio.rpf).

 This parser is read-only: it decodes the container into a flat list of streams
 (name/hash, codec, sample rate, channels, samples, and the raw audio data's
 offset+size inside the AWC) for browsing and raw export. It never writes AWCs.

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

namespace RageLib.GtaVI.Audio
{
  /// <summary>The audio codec a stream is stored in (the format chunk's codec byte).</summary>
  public enum AwcCodec : byte
  {
    Pcm = 0,
    Adpcm = 4,
    Xma2 = 5,
    Opus = 0x0B,
  }

  /// <summary>The kind of a chunk, named by the last byte of its name hash (same
  /// values CodeWalker uses, except GTA VI's format chunk is 0x76, not 0xFA).</summary>
  public enum AwcChunkType : byte
  {
    Data = 0x55,
    Format = 0x76,
    StreamFormat = 0x48,
    SeekTable = 0xA3,
    Animation = 0x5C,
    GranularGrains = 0x5A,
    GranularLoops = 0xD9,
    Midi = 0x68,
    Markers = 0xBD,
    Peak = 0x36,
    Gesture = 0x2B,
  }

  /// <summary>One bit-packed chunk descriptor: its type, byte size and byte offset
  /// (relative to the start of the AWC).</summary>
  public readonly struct AwcChunk
  {
    public AwcChunk(AwcChunkType type, int size, int offset) { Type = type; Size = size; Offset = offset; }
    public AwcChunkType Type { get; }
    public int Size { get; }
    public int Offset { get; }
    public override string ToString() => $"{Type} @0x{Offset:X} ({Size} B)";
  }

  /// <summary>One audio stream in the container, flattened for display and export.
  /// For a multichannel ("music") bank each playable channel is also a stream row,
  /// referring to the shared data chunk by <see cref="MusicChannel"/>.</summary>
  public sealed class AwcStream
  {
    /// <summary>The stream's 29-bit name hash (0 for the multichannel source stream).</summary>
    public uint Id { get; init; }
    public int Index { get; init; }
    public AwcCodec Codec { get; init; }
    public int SampleRate { get; init; }
    public long Samples { get; init; }
    public int Channels { get; init; }

    /// <summary>Byte offset of the stream's raw audio data inside the AWC (-1 if none).</summary>
    public long DataOffset { get; init; } = -1;
    public long DataSize { get; init; }

    /// <summary>True for a block-interleaved multichannel ("music") channel.</summary>
    public bool IsStreamed { get; init; }
    /// <summary>For a streamed channel: its index within the interleaved blocks; else -1.</summary>
    public int MusicChannel { get; init; } = -1;
    /// <summary>For a streamed channel: how many channels share the interleaved blocks.</summary>
    public int MusicChannelCount { get; init; }
    public int BlockCount { get; init; }
    public int BlockSize { get; init; }

    /// <summary>True for the container row that owns the multichannel data (the id==0 source).</summary>
    public bool IsMultichannelSource { get; init; }

    public IReadOnlyList<AwcChunk> Chunks { get; init; } = Array.Empty<AwcChunk>();

    /// <summary>The stream's name: a recovered label if one is known, else its hex hash.</summary>
    public string Name => string.IsNullOrEmpty(KnownName) ? (Id == 0 ? "(multichannel source)" : $"0x{Id:X8}") : KnownName;
    public string KnownName { get; set; }

    public string CodecName => Codec switch
    {
      AwcCodec.Pcm => "PCM",
      AwcCodec.Adpcm => "ADPCM",
      AwcCodec.Xma2 => "XMA2",
      AwcCodec.Opus => "Opus",
      _ => $"codec 0x{(byte)Codec:X2}",
    };

    /// <summary>Opus always decodes at 48 kHz regardless of the stored sample rate.</summary>
    public int EffectiveSampleRate => Codec == AwcCodec.Opus ? 48000 : SampleRate;

    /// <summary>Whether this stream can be decoded to PCM for playback/WAV export
    /// (Opus or PCM16, 1-2 channels, with data). The multichannel source row is not
    /// itself decodable - its channels are.</summary>
    public bool IsDecodable =>
      !IsMultichannelSource && DataOffset >= 0 && DataSize > 0 &&
      (Codec == AwcCodec.Opus || Codec == AwcCodec.Pcm) && Channels >= 1 && Channels <= 2;

    public double DurationSeconds => EffectiveSampleRate > 0 ? (double)Samples / EffectiveSampleRate : 0;

    public override string ToString() => $"{Name}  {CodecName}  {SampleRate} Hz  {Channels}ch  {Samples} smp";
  }

  /// <summary>A parsed GTA VI audio wave container.</summary>
  public sealed class AwcFile
  {
    public const uint MagicLittle = 0x54414441; // "ADAT"
    public const uint MagicBig = 0x41444154;    // "TADA" (big-endian container)

    public uint Flags { get; private set; }
    public ushort Version { get; private set; }
    public int DeclaredStreamCount { get; private set; }
    public int HeaderLength { get; private set; }

    public bool HasChunkIndices => (Flags & 0x10000) != 0;
    public bool SingleChannelEncrypt => (Flags & 0x20000) != 0;
    public bool MultiChannel => (Flags & 0x40000) != 0;
    public bool MultiChannelEncrypt => (Flags & 0x80000) != 0;
    public bool HasHeaderPad => (Flags & 0x400000) != 0;
    public bool HasOpaquePrefixTable => (Flags & 0x200000) != 0;

    public IReadOnlyList<AwcStream> Streams { get; private set; } = Array.Empty<AwcStream>();

    /// <summary>Parses the AWC from its (already decompressed) bytes. Throws
    /// <see cref="InvalidDataException"/> when the data is not a recognisable AWC.</summary>
    public static AwcFile Parse(byte[] data)
    {
      if (data == null || data.Length < 16)
        throw new System.IO.InvalidDataException("The data is too short to be an AWC.");

      uint magic = BinaryPrimitives.ReadUInt32LittleEndian(data);
      if (magic != MagicLittle && magic != MagicBig)
        throw new System.IO.InvalidDataException(
          $"Not an AWC: the first bytes are 0x{magic:X8}, not 'ADAT'. (Whole-file-encrypted AWCs are not supported.)");
      if (magic == MagicBig)
        throw new System.IO.InvalidDataException("Big-endian (console) AWCs are not supported.");

      var awc = new AwcFile();
      awc.Read(data);
      return awc;
    }

    private void Read(byte[] data)
    {
      Flags = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
      Version = (ushort)(Flags & 0xFFFF);
      int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8));
      HeaderLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12));
      DeclaredStreamCount = count;

      if (count < 0 || count > 0x100000)
        throw new System.IO.InvalidDataException($"Implausible AWC stream count ({count}).");

      long available = data.Length;
      int p = 16 + (HasHeaderPad ? 4 : 0);
      int indexStart = HasChunkIndices ? p : -1;
      if (indexStart >= 0) p += count * 2;
      if (HasOpaquePrefixTable) p += (count + 1) * 4;
      int infoStart = p;
      p += count * 4;
      if (p > data.Length)
        throw new System.IO.InvalidDataException("The AWC header runs past the end of the data.");

      // Stream-info table: one bit-packed u32 per stream (tagCount<<29 | id).
      var ids = new uint[count];
      var tagCounts = new int[count];
      for (int i = 0; i < count; i++)
      {
        uint packed = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(infoStart + i * 4));
        ids[i] = packed & 0x1FFFFFFF;
        tagCounts[i] = (int)(packed >> 29);
      }

      // Chunk tag table: one bit-packed u64 per chunk (type<<56 | size<<28 | offset).
      int tagCursor = p;
      var streamChunks = new List<AwcChunk>[count];
      for (int i = 0; i < count; i++)
      {
        var chunks = new List<AwcChunk>(tagCounts[i]);
        for (int t = 0; t < tagCounts[i]; t++, tagCursor += 8)
        {
          if (tagCursor + 8 > data.Length)
            throw new System.IO.InvalidDataException("The AWC chunk table runs past the end of the data.");
          ulong tag = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(tagCursor, 8));
          var type = (AwcChunkType)(byte)(tag >> 56);
          int size = (int)((tag >> 28) & 0x0FFFFFFF);
          int offset = (int)(tag & 0x0FFFFFFF);
          chunks.Add(new AwcChunk(type, size, offset));
        }
        streamChunks[i] = chunks;
      }

      // A multichannel source's per-channel ids also appear as bare top-level streams
      // (carrying only Markers): collect those ids so the channel rows replace them.
      var musicChannelIds = new HashSet<uint>();
      for (int i = 0; i < count; i++)
        foreach (var c in streamChunks[i])
          if (c.Type == AwcChunkType.StreamFormat)
            foreach (var ch in ReadMusicChannels(data, c, ids))
              musicChannelIds.Add(ch.id);

      var streams = new List<AwcStream>();
      var music = new List<(int sourceIndex, AwcChunk format, AwcChunk dataChunk)>();

      for (int i = 0; i < count; i++)
      {
        var chunks = streamChunks[i];
        AwcChunk? format = null, dataChunk = null, streamFormat = null;
        foreach (var c in chunks)
        {
          if (c.Type == AwcChunkType.Format) format = c;
          else if (c.Type == AwcChunkType.Data) dataChunk = c;
          else if (c.Type == AwcChunkType.StreamFormat) streamFormat = c;
        }

        if (streamFormat is AwcChunk sf)
        {
          // Multichannel ("music") source: describes the interleaved channels and owns the data.
          streams.Add(new AwcStream
          {
            Index = i,
            Id = ids[i],
            Chunks = chunks,
            IsMultichannelSource = true,
            DataOffset = dataChunk?.Offset ?? -1,
            DataSize = dataChunk?.Size ?? 0,
            Channels = ReadMusicChannelCount(data, sf),
          });
          if (dataChunk is AwcChunk dc)
            music.Add((i, sf, dc));
          continue;
        }

        if (format is AwcChunk fc)
        {
          var (samples, rate, codec, channels) = ReadFormatChunk(data, fc);
          // A "mono" Opus stream whose packets are stereo: the header counts the samples of both channels.
          if (codec == AwcCodec.Opus && channels <= 1 && dataChunk is AwcChunk odc && IsStereoOpusPacket(data, odc.Offset))
          {
            channels = 2;
            samples /= 2;
          }
          streams.Add(new AwcStream
          {
            Index = i,
            Id = ids[i],
            Chunks = chunks,
            Codec = codec,
            SampleRate = rate,
            Samples = samples,
            Channels = channels == 0 ? 1 : channels,
            DataOffset = dataChunk?.Offset ?? -1,
            DataSize = dataChunk?.Size ?? 0,
          });
        }
        else if (!musicChannelIds.Contains(ids[i]))
        {
          // A stream without its own format chunk (e.g. a marker/animation-only
          // stream): list it so nothing is hidden. Bare rows that merely restate a
          // multichannel channel are left out - the channel row below carries it.
          streams.Add(new AwcStream
          {
            Index = i,
            Id = ids[i],
            Chunks = chunks,
            DataOffset = dataChunk?.Offset ?? -1,
            DataSize = dataChunk?.Size ?? 0,
          });
        }
      }

      // Expand each multichannel source's per-channel records into playable channel rows.
      foreach (var (sourceIndex, format, dataChunk) in music)
      {
        var channels = ReadMusicChannels(data, format, ids);
        foreach (var ch in channels)
        {
          // Radio stations store a stereo track as one "channel" of stereo Opus packets; the header then
          // counts the samples of both its channels (the seek table steps by twice a packet's samples).
          bool stereo = (AwcCodec)ch.codec == AwcCodec.Opus &&
                        IsStereoOpusPacket(data, FirstMusicPacket(data, dataChunk, ch.musicChannel, ch.channelCount, ch.blockSize));
          streams.Add(new AwcStream
          {
            Index = ch.index,
            Id = ch.id,
            Codec = (AwcCodec)ch.codec,
            SampleRate = ch.rate,
            Samples = stereo ? ch.samples / 2 : ch.samples,
            Channels = stereo ? 2 : 1,
            IsStreamed = true,
            MusicChannel = ch.musicChannel,
            MusicChannelCount = ch.channelCount,
            BlockCount = ch.blockCount,
            BlockSize = ch.blockSize,
            DataOffset = dataChunk.Offset,
            DataSize = dataChunk.Size,
          });
        }
      }

      Streams = streams;
    }

    /// <summary>Whether the Opus packet at <paramref name="offset"/> (u16 length, then the packet) is coded in stereo.</summary>
    private static bool IsStereoOpusPacket(byte[] data, long offset)
    {
      if (offset < 0 || offset + 3 > data.Length) return false;
      int bytes = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan((int)offset));
      return bytes > 0 && offset + 2 + bytes <= data.Length && (data[offset + 2] & 0x04) != 0;  // TOC byte: stereo flag
    }

    /// <summary>Where the first packet of a music channel is: in the first block, after the block's header.</summary>
    private static long FirstMusicPacket(byte[] data, AwcChunk dataChunk, int which, int channels, int blockSize)
    {
      long at = dataChunk.Offset;
      if (channels <= 0 || which < 0 || which >= channels || at < 0 || at + channels * 24 > data.Length) return -1;
      long before = 0, total = 0;
      for (int c = 0; c < channels; c++)
      {
        int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)at + c * 24 + 4));
        if (count < 0 || count > Math.Max(blockSize, 2048) / 2048) return -1;
        if (c < which) before += count;
        total += count;
      }
      long header = ((long)channels * 24 + total * 4 + 2047) & ~2047L;
      return at + header + before * 2048;
    }

    // Format chunk (type 0x76), 32 bytes on GTA VI: samples at 0, sample rate at 8,
    // codec at 28, channel count at 29. (CodeWalker's GTA5 chunk is 20/24 bytes with
    // the codec at byte 19 and no channel count.)
    private static (long samples, int rate, AwcCodec codec, int channels) ReadFormatChunk(byte[] data, AwcChunk c)
    {
      if (c.Offset < 0 || c.Offset + Math.Min(c.Size, 32) > data.Length || c.Size < 20)
        return (0, 0, AwcCodec.Pcm, 1);
      long samples = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(c.Offset));
      int rate = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(c.Offset + 8));
      // 32-byte GTA VI layout carries codec/channels near the end; the 20/24-byte
      // legacy layout keeps the codec at byte 19 and is mono.
      AwcCodec codec;
      int channels;
      if (c.Size >= 30)
      {
        codec = (AwcCodec)data[c.Offset + 28];
        channels = data[c.Offset + 29];
      }
      else
      {
        codec = (AwcCodec)data[c.Offset + 19];
        channels = 1;
      }
      return (samples, rate, codec, channels);
    }

    // Multichannel / "music" chunk (type 0x48): blockCount, blockSize, channelCount
    // (3 x u32), then one 16-byte record per channel: id, samples, (u16 rate at +10),
    // codec at +12.
    private static int ReadMusicChannelCount(byte[] data, AwcChunk c)
    {
      if (c.Offset < 0 || c.Offset + 12 > data.Length) return 0;
      return (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(c.Offset + 8));
    }

    private static List<(int index, uint id, long samples, int rate, byte codec, int musicChannel, int channelCount, int blockCount, int blockSize)>
      ReadMusicChannels(byte[] data, AwcChunk c, uint[] ids)
    {
      var result = new List<(int, uint, long, int, byte, int, int, int, int)>();
      if (c.Offset < 0 || c.Offset + 12 > data.Length) return result;
      int blockCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(c.Offset));
      int blockSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(c.Offset + 4));
      int channels = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(c.Offset + 8));
      if (channels < 0 || channels > 64) return result;
      for (int ch = 0; ch < channels; ch++)
      {
        int rec = c.Offset + 12 + ch * 16;
        if (rec + 16 > data.Length) break;
        uint id = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(rec)) & 0x1FFFFFFF;
        long samples = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(rec + 4));
        int rate = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(rec + 10));
        byte codec = data[rec + 12];
        // Find which top-level stream index carries this channel's id (for display order).
        int index = -1;
        for (int i = 0; i < ids.Length; i++) if (ids[i] == id) { index = i; break; }
        result.Add((index, id, samples, rate, codec, ch, channels, blockCount, blockSize));
      }
      return result;
    }
  }
}
