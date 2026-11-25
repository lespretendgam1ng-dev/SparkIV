/**********************************************************************\

 SparkIV
 Copyright (C) 2026  reditec

 This program is free software: you can redistribute it and/or modify
 it under the terms of the GNU General Public License as published by
 the Free Software Foundation, either version 3 of the License, or
 (at your option) any later version.

 This program is distributed in the hope that it will be useful,
 but WITHOUT ANY WARRANTY; without even the implied warranty of
 MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 GNU General Public License for more details.

 You should have received a copy of the GNU General Public License
 along with this program.  If not, see <http://www.gnu.org/licenses/>.

\**********************************************************************/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using RageLib.Audio;
using RageLib.Audio.Playback;
using RageLib.Audio.WaveFile;
using RageLib.GtaVI.Audio;
using SparkIV.WinUI.Model;

namespace SparkIV.WinUI.Viewers
{
  /// <summary>A track as the audio viewer lists and plays it, whatever game it comes from.</summary>
  public interface IAudioTrack
  {
    string Name { get; }
    TimeSpan Length { get; }
    int SamplesPerSecond { get; }

    /// <summary>"Mono", "Stereo", "5.0", ...</summary>
    string LayoutName { get; }

    /// <summary>How many channels it plays with.</summary>
    int ChannelCount { get; }

    /// <summary>Null if it can be played and exported as WAV, else why not.</summary>
    string? NotPlayable { get; }
  }

  /// <summary>
  /// An audio bank as the audio viewer shows it: its tracks in groups, the player for them, and the
  /// exports. GTA IV's .ivaud and GTA VI's .awc each have one.
  /// </summary>
  internal interface IAudioBank : IDisposable
  {
    List<AudioTrackGroup> BuildGroups();

    /// <summary>Whether the whole bank exports as one multichannel WAV.</summary>
    bool SupportsMultichannelExport { get; }

    /// <summary>Whether a track's stored data can be exported as it is (not converted).</summary>
    bool SupportsRawExport { get; }

    // Playback of one track at a time.
    IAudioTrack? Track { get; }

    /// <summary>Makes <paramref name="track"/> the one played (may decode it first).</summary>
    Task LoadAsync(IAudioTrack track);

    void Play();
    void Pause();
    void Stop();
    void Seek(TimeSpan position);
    TimeSpan Position { get; }
    TimeSpan Length { get; }
    bool IsPlaying { get; }
    bool IsPaused { get; }
    bool HasEnded { get; }
    bool Looped { set; }
    float Volume { set; }
    bool Downmix { set; }

    void ExportWave(IAudioTrack track, Stream output);
    void ExportAll(Stream output);
    byte[] ExportRaw(IAudioTrack track);
  }

  #region GTA IV (.ivaud)

  internal sealed class IvaudTrack : IAudioTrack
  {
    public IvaudTrack(AudioTrack track) => Track = track;

    public AudioTrack Track { get; }
    public string Name => Track.Name;
    public TimeSpan Length => Track.Length;
    public int SamplesPerSecond => Track.SamplesPerSecond;
    public string LayoutName => Track.LayoutName;
    public int ChannelCount => Track.Waves.Length;
    public string? NotPlayable => null;
  }

  /// <summary>A GTA IV audio bank. Waves named NAME_LEFT / NAME_RIGHT (/ _CENTRE / _LS / _RS) also play as one track.</summary>
  internal sealed class IvaudBank : IAudioBank
  {
    private readonly AudioFile _file;
    private readonly AudioPlayer _player = new AudioPlayer();
    private readonly Dictionary<AudioTrack, IvaudTrack> _tracks = new Dictionary<AudioTrack, IvaudTrack>();

    public IvaudBank(AudioFile file) => _file = file;

    private IvaudTrack Wrap(AudioTrack track)
    {
      if (!_tracks.TryGetValue(track, out var wrapped))
      {
        _tracks[track] = wrapped = new IvaudTrack(track);
      }
      return wrapped;
    }

    public List<AudioTrackGroup> BuildGroups()
    {
      var groupTracks = AudioTrack.FindChannelGroups(_file);
      var monoTracks = _file.Select(AudioTrack.FromMono).ToList();
      var monoOfWave = monoTracks.ToDictionary(t => t.Waves[0]);
      var grouped = new HashSet<AudioTrack>();
      var groups = new List<AudioTrackGroup>();

      foreach (var track in groupTracks)
      {
        int count = track.Waves.Length;
        var group = new AudioTrackGroup(track.Name + "  –  " + track.LayoutName + ", stored as " + count + " mono tracks", isMultichannel: true);
        group.Add(new AudioTrackRow(Wrap(track), track.LayoutName + " (" + count + " × Mono)", AudioRowKind.Multichannel));
        // Members in speaker order: L, R, C, LS, RS
        for (int i = 0; i < count; i++)
        {
          if (monoOfWave.TryGetValue(track.Waves[i], out var mono))
          {
            group.Add(new AudioTrackRow(Wrap(mono), "Mono (" + AudioTrack.GetChannelName(track.Channels[i]) + ")", AudioRowKind.Member));
            grouped.Add(mono);
          }
        }
        groups.Add(group);
      }

      var rest = new AudioTrackGroup(groupTracks.Count > 0 ? "Mono tracks" : "Tracks", isMultichannel: false);
      foreach (var track in monoTracks.Where(t => !grouped.Contains(t)))
      {
        rest.Add(new AudioTrackRow(Wrap(track), "Mono", AudioRowKind.Mono));
      }
      if (rest.Count > 0)
      {
        groups.Add(rest);
      }
      return groups;
    }

    public bool SupportsMultichannelExport => _file.SupportsMultichannelExport;
    public bool SupportsRawExport => false;

    public IAudioTrack? Track => _player.Track == null ? null : Wrap(_player.Track);

    public Task LoadAsync(IAudioTrack track)
    {
      _player.Initialize(_file, ((IvaudTrack)track).Track);
      return Task.CompletedTask;
    }

    public void Play() => _player.Play();
    public void Pause() => _player.Pause();
    public void Stop() => _player.Stop();
    public void Seek(TimeSpan position) => _player.Seek(position);
    public TimeSpan Position => _player.Position;
    public TimeSpan Length => _player.Length;
    public bool IsPlaying => _player.IsPlaying;
    public bool IsPaused => _player.IsPaused;
    public bool HasEnded => _player.HasEnded;
    public bool Looped { set => _player.Looped = value; }
    public float Volume { set => _player.Volume = value; }
    public bool Downmix { set => _player.Downmix = value; }

    public void ExportWave(IAudioTrack track, Stream output)
    {
      var t = ((IvaudTrack)track).Track;
      if (t.IsMultichannel)
      {
        WaveExport.ExportChannels(_file, t, output);
      }
      else
      {
        WaveExport.Export(_file, t.Waves[0], output);
      }
    }

    public void ExportAll(Stream output) => WaveExport.ExportMultichannel(_file, output);

    public byte[] ExportRaw(IAudioTrack track) => throw new NotSupportedException();

    public void Dispose()
    {
      _player.Stop();
      _file.Dispose();
    }
  }

  #endregion

  #region GTA VI (.awc)

  internal sealed class AwcTrack : IAudioTrack
  {
    public AwcTrack(AwcStream stream) => Stream = stream;

    public AwcStream Stream { get; }
    public string Name => Stream.Name;
    public TimeSpan Length => TimeSpan.FromSeconds(Math.Max(0, Stream.DurationSeconds));
    public int SamplesPerSecond => Stream.EffectiveSampleRate;
    public int ChannelCount => Math.Max(1, Stream.Channels);
    public string LayoutName => ChannelCount switch { 1 => "Mono", 2 => "Stereo", _ => ChannelCount + " channels" };

    public string? NotPlayable =>
      Stream.IsDecodable ? null
      : Stream.IsMultichannelSource ? "The multichannel source holds the shared data; play its channels instead."
      : Stream.DataOffset < 0 || Stream.DataSize == 0 ? Name + " has no audio data."
      : Stream.Channels > 2 ? Name + ": " + Stream.Channels + "-channel audio can't be played yet (export the stored data)."
      : Name + ": " + Stream.CodecName + " can't be played yet (export the stored data).";
  }

  /// <summary>A GTA VI audio wave container: its streams decode (Opus, PCM) to PCM that is played from memory.</summary>
  internal sealed class AwcBank : IAudioBank
  {
    private readonly AwcFile _awc;
    private readonly byte[] _data;
    private readonly PcmBufferPlayer _player = new PcmBufferPlayer();
    private readonly Dictionary<AwcStream, AwcDecodedAudio> _decoded = new Dictionary<AwcStream, AwcDecodedAudio>();

    public AwcBank(AwcFile awc, byte[] data)
    {
      _awc = awc;
      _data = data;
    }

    public List<AudioTrackGroup> BuildGroups()
    {
      // The multichannel "source" stream is the raw block-interleaved container; its channels stand for it.
      bool hasChannels = _awc.Streams.Any(s => s.IsStreamed);
      var music = new AudioTrackGroup("Music channels", isMultichannel: false);
      var sounds = new AudioTrackGroup(hasChannels ? "Streams" : "Tracks", isMultichannel: false);
      foreach (var s in _awc.Streams)
      {
        if (s.IsMultichannelSource && hasChannels)
        {
          continue;
        }
        var track = new AwcTrack(s);
        var row = new AudioTrackRow(track, track.LayoutName + "  ·  " + s.CodecName, AudioRowKind.Mono);
        (s.IsStreamed ? music : sounds).Add(row);
      }
      return new[] { music, sounds }.Where(g => g.Count > 0).ToList();
    }

    public bool SupportsMultichannelExport => false;
    public bool SupportsRawExport => true;

    public IAudioTrack? Track { get; private set; }

    public async Task LoadAsync(IAudioTrack track)
    {
      var stream = ((AwcTrack)track).Stream;
      if (track.NotPlayable != null)
      {
        throw new NotSupportedException(track.NotPlayable);
      }
      AwcDecodedAudio? audio;
      lock (_decoded)
      {
        _decoded.TryGetValue(stream, out audio);
      }
      if (audio == null)
      {
        audio = await Task.Run(() => AwcDecoder.Decode(stream, _data));
        lock (_decoded)
        {
          _decoded[stream] = audio;
        }
      }
      _player.Stop();
      _player.Load(audio.Pcm, audio.Channels, audio.SampleRate);
      Track = track;
    }

    public void Play() => _player.Play();
    public void Pause() => _player.Pause();
    public void Stop() => _player.Stop();
    public void Seek(TimeSpan position) => _player.Seek(position);
    public TimeSpan Position => _player.Position;
    public TimeSpan Length => _player.Length;
    public bool IsPlaying => _player.IsPlaying;
    public bool IsPaused => _player.IsPaused;
    public bool HasEnded => _player.HasEnded;
    public bool Looped { set => _player.Looped = value; }
    public float Volume { set => _player.Volume = value; }
    public bool Downmix { set { } }  // At most two channels are played.

    public void ExportWave(IAudioTrack track, Stream output)
    {
      var stream = ((AwcTrack)track).Stream;
      if (track.NotPlayable != null)
      {
        throw new NotSupportedException(track.NotPlayable);
      }
      AwcDecodedAudio audio;
      lock (_decoded)
      {
        if (!_decoded.TryGetValue(stream, out audio!))
        {
          audio = AwcDecoder.Decode(stream, _data);
        }
      }
      WriteWav(output, audio);
    }

    public void ExportAll(Stream output) => throw new NotSupportedException();

    public byte[] ExportRaw(IAudioTrack track)
    {
      var s = ((AwcTrack)track).Stream;
      if (s.DataOffset < 0 || s.DataSize <= 0 || s.DataOffset + s.DataSize > _data.Length)
      {
        throw new InvalidDataException("This stream carries no stored audio data.");
      }
      return _data.AsSpan((int)s.DataOffset, (int)s.DataSize).ToArray();
    }

    private static void WriteWav(Stream stream, AwcDecodedAudio audio)
    {
      int channels = audio.Channels;
      int rate = audio.SampleRate;
      int dataBytes = audio.Pcm.Length * 2;
      var w = new BinaryWriter(stream);
      w.Write(0x46464952);                 // "RIFF"
      w.Write(36 + dataBytes);
      w.Write(0x45564157);                 // "WAVE"
      w.Write(0x20746D66);                 // "fmt "
      w.Write(16);
      w.Write((short)1);                   // PCM
      w.Write((short)channels);
      w.Write(rate);
      w.Write(rate * channels * 2);        // byte rate
      w.Write((short)(channels * 2));      // block align
      w.Write((short)16);                  // bits per sample
      w.Write(0x61746164);                 // "data"
      w.Write(dataBytes);
      byte[] bytes = new byte[dataBytes];
      Buffer.BlockCopy(audio.Pcm, 0, bytes, 0, dataBytes);
      w.Write(bytes);
      w.Flush();
    }

    public void Dispose() => _player.Dispose();
  }

  #endregion
}
