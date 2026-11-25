/**********************************************************************\

 RageLib - SparkIV
 Plays a ready-decoded 16-bit interleaved PCM buffer through the Windows
 waveOut device, with the same transport as AudioPlayer (play/pause/stop,
 seek, position, length, volume, loop). Used by the GTA VI AWC viewer, whose
 Opus/PCM streams are decoded up front into one buffer.

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
using System.Runtime.InteropServices;

namespace RageLib.Audio.Playback
{
  /// <summary>Plays a fixed, already-decoded 16-bit PCM buffer (1 or 2 channels).</summary>
  public sealed class PcmBufferPlayer : IDisposable
  {
    private const int FramesPerBuffer = 4096;
    private const int BufferCount = 3;

    private short[] _pcm;         // interleaved
    private int _channels = 1;
    private int _rate;
    private int _totalFrames;

    private WaveFormat _format;
    private WaveOutPlayer _player;
    private short[] _samples;     // scratch for one device buffer

    private int _startFrame;      // base for the device position after a (re)open
    private int _fillFrame;       // the Filler's own read cursor
    private int _fadeFrames;      // length of the end fade-out, in frames
    private bool _paused;
    private volatile bool _looped;
    private volatile float _volume = 1.0f;

    /// <summary>Loads a buffer to play. Stops any current playback.</summary>
    public void Load(short[] pcm, int channels, int sampleRate)
    {
      Stop();
      _pcm = pcm ?? Array.Empty<short>();
      _channels = Math.Max(1, channels);
      _rate = sampleRate > 0 ? sampleRate : 48000;
      _totalFrames = _pcm.Length / _channels;
      _format = new WaveFormat(_rate, 16, _channels);
      _samples = new short[FramesPerBuffer * _channels];
      _startFrame = 0;
      // ~8 ms end fade-out, so a track that ends on a loud sample (a music loop point)
      // does not click when playback stops. Never longer than half the track.
      _fadeFrames = Math.Clamp(_rate / 125, 1, Math.Max(1, _totalFrames / 2));
    }

    public bool HasAudio => _pcm != null && _totalFrames > 0;

    public bool Looped
    {
      get { return _looped; }
      set { _looped = value; }
    }

    /// <summary>Output volume from 0 to 1.</summary>
    public float Volume
    {
      get { return _volume; }
      set { _volume = Math.Max(0f, Math.Min(1f, value)); }
    }

    public bool IsPlaying => _player != null && !_paused;
    public bool IsPaused => _player != null && _paused;

    public TimeSpan Length => TimeSpan.FromSeconds(_rate > 0 ? (double)_totalFrames / _rate : 0);

    private long RawPositionFrames => _startFrame + (_player == null ? 0 : _player.GetPlayedFrames());

    private int PositionFrames
    {
      get
      {
        if (_totalFrames == 0) return 0;
        long position = RawPositionFrames;
        return (int)(_looped ? position % _totalFrames : Math.Min(position, _totalFrames));
      }
    }

    public TimeSpan Position => TimeSpan.FromSeconds(_rate > 0 ? (double)PositionFrames / _rate : 0);

    /// <summary>True once a non-looped buffer has been played to the end.</summary>
    public bool HasEnded => _player != null && !_looped && RawPositionFrames >= _totalFrames;

    private void Filler(IntPtr data, int size)
    {
      int frames = size / (2 * _channels);
      float volume = _volume;
      short[] pcm = _pcm;
      int total = _totalFrames;

      for (int f = 0; f < frames; f++)
      {
        int srcFrame = _fillFrame++;
        if (_looped && total > 0) srcFrame %= total;

        int di = f * _channels;
        // Never read past the buffer: beyond the last frame the device gets silence.
        if (srcFrame >= 0 && srcFrame < total)
        {
          float gain = volume;
          // Fade the last few milliseconds to zero (not when looping, where the end joins the start).
          if (!_looped)
          {
            int remain = total - srcFrame;
            if (remain < _fadeFrames) gain *= (float)remain / _fadeFrames;
          }
          int si = srcFrame * _channels;
          for (int c = 0; c < _channels; c++)
            _samples[di + c] = (short)(pcm[si + c] * gain);
        }
        else
        {
          for (int c = 0; c < _channels; c++)
            _samples[di + c] = 0;
        }
      }

      Marshal.Copy(_samples, 0, data, frames * _channels);
    }

    public void Play()
    {
      if (!HasAudio) return;

      if (IsPaused)
      {
        _player.Resume();
        _paused = false;
        return;
      }
      if (IsPlaying) return;

      if (_startFrame >= _totalFrames) _startFrame = 0;
      _fillFrame = _startFrame;
      _paused = false;
      _player = new WaveOutPlayer(-1, _format, FramesPerBuffer * _format.nBlockAlign, BufferCount, Filler);
    }

    public void Pause()
    {
      if (IsPlaying)
      {
        _player.Pause();
        _paused = true;
      }
    }

    public void Seek(TimeSpan position)
    {
      if (!HasAudio) return;
      bool wasPlaying = IsPlaying;
      CloseDevice();
      _startFrame = Math.Max(0, Math.Min(_totalFrames, (int)(position.TotalSeconds * _rate)));
      if (wasPlaying) Play();
    }

    public void Stop()
    {
      CloseDevice();
      _startFrame = 0;
    }

    private void CloseDevice()
    {
      if (_player != null)
      {
        _startFrame = PositionFrames; // keep where it was, for a following Seek/Play
        _player.Dispose();
        _player = null;
      }
      _paused = false;
    }

    public void Dispose() => Stop();
  }
}
