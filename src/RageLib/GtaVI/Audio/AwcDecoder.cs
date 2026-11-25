/**********************************************************************\

 RageLib - SparkIV
 GTA VI (RPF9) audio wave container decoder: turns an AWC stream's stored
 codec data into 16-bit interleaved PCM for playback and WAV export.

 GTA VI AWC audio is Opus (codec 0x0B) at 48 kHz, or occasionally raw PCM16.
 The Opus framing was taken from rpf9-explorer's native OpusPreview.cpp and
 verified against real members:

   * The audio data is a run of 2048-byte blocks. Inside a block, each Opus
     packet is stored as a little-endian u16 byte-length followed by the packet
     bytes; a zero length ends the block (the rest is zero padding). A packet
     never straddles a 2048-byte block boundary.
   * An independent stream (its own 0x76 format chunk + 0x55 data chunk) is one
     such run - the whole data chunk.
   * A streamed "music" channel (from a 0x48 multichannel chunk) is interleaved:
     each big block begins with a per-channel table (24 bytes each) giving that
     channel's sub-block count and a repeat-skip (samples of leading packets to
     drop, used for seamless looping); the channel's 2048-byte sub-blocks follow
     a 2048-aligned header and are concatenated across blocks.

 A single Opus decoder instance is fed every packet in order (its state carries
 across blocks); skipped packets are dropped without decoding, as the reference
 does. PCM streams are copied straight through.

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
using Concentus;
using Concentus.Structs;

namespace RageLib.GtaVI.Audio
{
  /// <summary>Decoded 16-bit interleaved PCM plus the format it plays at.</summary>
  public sealed class AwcDecodedAudio
  {
    public AwcDecodedAudio(short[] pcm, int channels, int sampleRate)
    {
      Pcm = pcm;
      Channels = channels;
      SampleRate = sampleRate;
    }
    public short[] Pcm { get; }
    public int Channels { get; }
    public int SampleRate { get; }
    public long FrameCount => Channels > 0 ? Pcm.Length / Channels : 0;
  }

  public static class AwcDecoder
  {
    private const int OpusRate = 48000;
    private const int MaxFrameSamples = 5760; // 120 ms at 48 kHz, the largest Opus frame

    // Opus carries encoder delay ("pre-skip") at the very start of the decoded signal, and
    // pads the final frame. Dropping ~2.5 ms of pre-skip and any trailing digital-silence pad
    // leaves the real content (the AWC stores no explicit pre-skip, so a safe fixed value is used).
    private const int OpusPreSkip = 120;        // 2.5 ms at 48 kHz
    private const int SilenceThreshold = 6;     // |sample| at or below this is treated as padding silence

    /// <summary>Whether this stream can be decoded to PCM here (Opus or PCM16, 1-2 channels).</summary>
    public static bool CanDecode(AwcStream s) => s != null && s.IsDecodable;

    private readonly struct Span
    {
      public Span(long offset, long size, int skipSamples) { Offset = offset; Size = size; SkipSamples = skipSamples; }
      public long Offset { get; }
      public long Size { get; }
      public int SkipSamples { get; }
    }

    /// <summary>Decodes a stream to interleaved 16-bit PCM. <paramref name="maxSeconds"/> &gt; 0 caps the length.</summary>
    public static AwcDecodedAudio Decode(AwcStream stream, byte[] data, int maxSeconds = 0)
    {
      if (stream == null) throw new ArgumentNullException(nameof(stream));
      if (data == null) throw new ArgumentNullException(nameof(data));
      if (stream.IsMultichannelSource)
        throw new NotSupportedException("The multichannel source row has no single decodable stream; play its channels.");
      int channels = stream.Channels <= 0 ? 1 : stream.Channels;
      if (channels > 2)
        throw new NotSupportedException($"{channels}-channel streams are not supported for playback.");

      var spans = BuildSpans(stream, data);

      return stream.Codec switch
      {
        AwcCodec.Pcm => DecodePcm(spans, data, channels, stream.SampleRate, maxSeconds),
        AwcCodec.Opus => TrimOpus(DecodeOpus(spans, data, channels, maxSeconds)),
        _ => throw new NotSupportedException($"Codec {stream.CodecName} cannot be decoded; export the raw data instead."),
      };
    }

    // Drops the leading Opus pre-skip and the trailing digital-silence padding so the buffer is
    // the real content and never ends on stray pad samples. Loud, abrupt content ends (music loop
    // points) are not silence and are left intact here - the player fades those out on playback.
    private static AwcDecodedAudio TrimOpus(AwcDecodedAudio audio)
    {
      int ch = audio.Channels;
      long frames = audio.FrameCount;
      if (ch <= 0 || frames <= 0) return audio;

      long start = Math.Min(OpusPreSkip, frames);
      long endExclusive = frames;
      // Walk back over trailing frames that are silence (the encoder's final-frame padding).
      while (endExclusive > start)
      {
        bool silent = true;
        for (int c = 0; c < ch; c++)
        {
          if (Math.Abs((int)audio.Pcm[(endExclusive - 1) * ch + c]) > SilenceThreshold) { silent = false; break; }
        }
        if (!silent) break;
        endExclusive--;
      }

      long len = Math.Max(0, endExclusive - start);
      if (start == 0 && len == frames) return audio;
      var trimmed = new short[len * ch];
      Array.Copy(audio.Pcm, start * ch, trimmed, 0, (int)(len * ch));
      return new AwcDecodedAudio(trimmed, ch, audio.SampleRate);
    }

    private static List<Span> BuildSpans(AwcStream stream, byte[] data)
    {
      var spans = new List<Span>();
      if (!stream.IsStreamed)
      {
        // Independent stream: the whole data chunk is one run.
        if (stream.DataOffset >= 0 && stream.DataSize > 0 && stream.DataOffset + stream.DataSize <= data.Length)
          spans.Add(new Span(stream.DataOffset, stream.DataSize, 0));
        return spans;
      }

      // Streamed music channel: gather this channel's sub-blocks from every big block.
      int channels = stream.MusicChannelCount <= 0 ? 1 : stream.MusicChannelCount;
      int which = stream.MusicChannel;
      long chunk = stream.BlockSize;
      long dataOffset = stream.DataOffset;
      long dataSize = stream.DataSize;
      if (which < 0 || which >= channels || chunk < 2048 || dataOffset < 0) return spans;

      for (int b = 0; b < stream.BlockCount; b++)
      {
        long at = dataOffset + (long)b * chunk;
        long remaining = dataSize - (long)b * chunk;
        if (remaining <= 0) break;
        long blockSize = Math.Min(chunk, remaining);
        if (at + channels * 24 > data.Length || blockSize < channels * 24) break;

        long subTotal = 0, before = 0;
        int entries = 0, skip = 0;
        for (int c = 0; c < channels; c++)
        {
          int rec = (int)at + c * 24;
          int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(rec + 4));
          if (count < 0 || count > blockSize / 2048) { count = 0; }
          if (c < which) before += count;
          if (c == which) { entries = count; skip = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(rec + 8)); }
          subTotal += count;
        }
        long header = ((long)channels * 24 + subTotal * 4 + 2047) & ~2047L;
        if (header > blockSize || subTotal * 2048 > blockSize - header) break;
        if (entries > 0)
        {
          long off = at + header + before * 2048;
          long size = (long)entries * 2048;
          if (off >= 0 && off + size <= data.Length)
            spans.Add(new Span(off, size, skip));
        }
      }
      return spans;
    }

    private static AwcDecodedAudio DecodePcm(List<Span> spans, byte[] data, int channels, int sampleRate, int maxSeconds)
    {
      long maxFrames = maxSeconds > 0 ? (long)maxSeconds * Math.Max(1, sampleRate) : long.MaxValue;
      var buffer = new GrowBuffer(0);
      long frames = 0;
      foreach (var span in spans)
      {
        long end = span.Offset + span.Size;
        for (long at = span.Offset; at + 2 <= end && frames < maxFrames; at += 2)
        {
          buffer.Add(BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan((int)at)));
          if (buffer.Count % channels == 0) frames++;
        }
      }
      return new AwcDecodedAudio(buffer.ToArray(), channels, sampleRate <= 0 ? OpusRate : sampleRate);
    }

    private static AwcDecodedAudio DecodeOpus(List<Span> spans, byte[] data, int channels, int maxSeconds)
    {
      IOpusDecoder decoder = OpusCodecFactory.CreateDecoder(OpusRate, channels, null);
      long maxFrames = maxSeconds > 0 ? (long)maxSeconds * OpusRate : long.MaxValue;
      var buffer = new GrowBuffer(OpusRate * channels);
      short[] frame = new short[MaxFrameSamples * channels];
      long frames = 0;

      foreach (var span in spans)
      {
        if (frames >= maxFrames) break;
        int skip = span.SkipSamples;
        long end = span.Offset + span.Size;
        for (long blockStart = span.Offset; blockStart < end && frames < maxFrames; blockStart += 2048)
        {
          int blockLen = (int)Math.Min(2048, end - blockStart);
          int p = 0;
          while (p + 2 <= blockLen)
          {
            int bytes = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan((int)blockStart + p));
            if (bytes == 0) break;                 // padding sentinel: rest of the block is empty
            if (bytes > blockLen - p - 2) break;   // packet would cross the block boundary
            var packet = data.AsSpan((int)blockStart + p + 2, bytes);

            int n = OpusPacketInfo.GetNumSamples(packet, OpusRate);
            if (n <= 0 || n > MaxFrameSamples) break;

            if (skip > 0)
            {
              // Leading repeat-prefix packets are dropped without decoding (as the reference does).
              skip -= n;
            }
            else
            {
              int decoded = decoder.Decode(packet, frame.AsSpan(), MaxFrameSamples, false);
              if (decoded <= 0) break;
              int take = decoded;
              if (frames + take > maxFrames) take = (int)(maxFrames - frames);
              buffer.AddRange(frame, take * channels);
              frames += take;
            }
            p += 2 + bytes;
            if (frames >= maxFrames) break;
          }
        }
      }
      return new AwcDecodedAudio(buffer.ToArray(), channels, OpusRate);
    }

    /// <summary>A simple doubling short[] accumulator, to avoid List&lt;short&gt; churn on long tracks.</summary>
    private sealed class GrowBuffer
    {
      private short[] _data;
      public GrowBuffer(int capacity) { _data = new short[Math.Max(1024, capacity)]; }
      public int Count { get; private set; }

      public void Add(short value)
      {
        if (Count >= _data.Length) Array.Resize(ref _data, _data.Length * 2);
        _data[Count++] = value;
      }

      public void AddRange(short[] source, int count)
      {
        if (Count + count > _data.Length)
        {
          int size = _data.Length;
          while (size < Count + count) size *= 2;
          Array.Resize(ref _data, size);
        }
        Array.Copy(source, 0, _data, Count, count);
        Count += count;
      }

      public short[] ToArray()
      {
        if (Count == _data.Length) return _data;
        var result = new short[Count];
        Array.Copy(_data, result, Count);
        return result;
      }
    }
  }
}
