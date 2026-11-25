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
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using RageLib.Audio;
using RageLib.GtaVI.Audio;
using SparkIV.WinUI.Model;
using VirtualKey = Windows.System.VirtualKey;

namespace SparkIV.WinUI.Viewers
{
  /// <summary>
  /// An audio bank of any game SparkIV reads (GTA IV's .ivaud, GTA VI's .awc): its tracks, playback and
  /// export. In GTA IV waves named NAME_LEFT / NAME_RIGHT (/ _CENTRE / _LS / _RS) also play as one stereo,
  /// LCR or 5.0 track.
  /// </summary>
  public sealed partial class AudioView : UserControl, IDisposable
  {
    // Remembered while SparkIV runs, like in the old audio viewer.
    private static bool s_looped;
    private static bool s_downmix = true;
    private static double s_volume = 100;

    private readonly ViewerContext _context;
    private readonly IAudioBank _bank;
    private readonly DispatcherQueueTimer _timer;
    private readonly List<AudioTrackGroup> _groups = new List<AudioTrackGroup>();

    private bool _started;
    private bool _isPlaying;
    private bool _seeking;
    private bool _updatingSlider;
    private string? _busyNote;  // "Decoding ..." while a track is being prepared
    private int _loadVersion;
    private string _sortKey = "";
    private bool _ascending = true;

    internal AudioView(ViewerContext context, IAudioBank bank)
    {
      InitializeComponent();
      _context = context;
      _bank = bank;

      LoopButton.IsChecked = s_looped;
      DownmixButton.IsChecked = s_downmix;
      VolumeSlider.Value = s_volume;
      _bank.Looped = s_looped;
      _bank.Downmix = s_downmix;
      _bank.Volume = (float)(s_volume / 100);

      ExportAllButton.IsEnabled = bank.SupportsMultichannelExport;
      ExportAllButton.Visibility = bank.SupportsMultichannelExport || bank is IvaudBank ? Visibility.Visible : Visibility.Collapsed;
      ExportRawButton.Visibility = bank.SupportsRawExport ? Visibility.Visible : Visibility.Collapsed;

      // The slider seeks when the mouse lets go of it, not when it is pressed or on every step of a drag.
      // The slider moves its value on the press itself, before a handler on the slider hears of it, so the
      // press is caught on the root of its template, which sees it first.
      PositionSlider.Loaded += (s, e) =>
      {
        if (Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(PositionSlider) > 0 &&
            Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(PositionSlider, 0) is UIElement templateRoot)
        {
          templateRoot.AddHandler(PointerPressedEvent, new PointerEventHandler((sender, args) => _seeking = true), true);
        }
      };
      PositionSlider.AddHandler(PointerPressedEvent, new PointerEventHandler((s, e) => _seeking = true), true);
      PositionSlider.AddHandler(PointerReleasedEvent, new PointerEventHandler((s, e) => EndSeek()), true);
      PositionSlider.AddHandler(PointerCaptureLostEvent, new PointerEventHandler((s, e) => EndSeek()), true);

      _timer = DispatcherQueue.CreateTimer();
      _timer.Interval = TimeSpan.FromMilliseconds(100);
      _timer.Tick += (s, e) =>
      {
        if (_bank.HasEnded)
        {
          _bank.Stop();
        }
        UpdatePlayback();
      };

      BuildTracks();
      Loaded += AudioView_Loaded;
    }

    // Opening a bank plays its first track right away.
    private void AudioView_Loaded(object sender, RoutedEventArgs e)
    {
      if (_started)
      {
        return;
      }
      _started = true;
      _timer.Start();

      var rows = _groups.SelectMany(g => g).ToList();
      var first = rows.FirstOrDefault(r => r.Track.NotPlayable == null) ?? rows.FirstOrDefault();
      if (first != null)
      {
        TrackList.SelectedItem = first;
        if (first.Track.NotPlayable == null)
        {
          _ = PlayTrackAsync(first.Track);
        }
      }
      UpdatePlayback();
      TrackList.Focus(FocusState.Programmatic);
    }

    #region Track list

    private void BuildTracks()
    {
      _groups.Clear();
      _groups.AddRange(_bank.BuildGroups());
      ShowGroups();
    }

    private void ShowGroups()
    {
      TrackSource.Source = _groups;
      TrackList.ItemsSource = TrackSource.View;
    }

    private void SortColumn_Click(object sender, RoutedEventArgs e)
    {
      string key = (string)((Button)sender).Tag;
      _ascending = key != _sortKey || !_ascending;
      _sortKey = key;

      Func<AudioTrackRow, IComparable> by = key switch
      {
        "Channels" => r => r.Channels,
        "Length" => r => r.Track.Length,
        "Rate" => r => r.Track.SamplesPerSecond,
        _ => r => r.Name,
      };

      var selected = TrackList.SelectedItem;

      // Multichannel groups keep their members in speaker order and are sorted by their track;
      // the mono tracks are sorted among themselves.
      var multichannel = _groups.Where(g => g.IsMultichannel);
      multichannel = _ascending ? multichannel.OrderBy(g => by(g[0])) : multichannel.OrderByDescending(g => by(g[0]));
      var sorted = multichannel.ToList();
      foreach (var group in _groups.Where(g => !g.IsMultichannel))
      {
        var rows = _ascending ? group.OrderBy(by).ToList() : group.OrderByDescending(by).ToList();
        group.Clear();
        group.AddRange(rows);
        sorted.Add(group);
      }
      _groups.Clear();
      _groups.AddRange(sorted);

      ShowGroups();
      TrackList.SelectedItem = selected;
    }

    private IAudioTrack? SelectedTrack => (TrackList.SelectedItem as AudioTrackRow)?.Track;

    // A single click only selects. Playback continues; when nothing is playing, Play starts the selected track.
    private async void TrackList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      var track = SelectedTrack;
      if (track != null && track != _bank.Track && !_bank.IsPlaying && !_bank.IsPaused && track.NotPlayable == null &&
          _bank is IvaudBank)
      {
        // Ready to play right away (GTA IV's waves decode as they play; others are decoded on Play).
        await LoadAsync(track);
      }
      UpdatePlayback();
    }

    // Double-click / Enter: play that track from the start.
    private void TrackList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
      if (SelectedTrack is IAudioTrack track)
      {
        _ = PlayTrackAsync(track);
      }
    }

    private void TrackList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
      if (e.Key == VirtualKey.Enter && SelectedTrack is IAudioTrack track)
      {
        e.Handled = true;
        _ = PlayTrackAsync(track);
      }
    }

    #endregion

    #region Playback

    private void Root_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
      // Space plays and pauses, except on a button, which Space presses.
      if (e.Key == VirtualKey.Space && !(FocusManager.GetFocusedElement(XamlRoot) is ButtonBase))
      {
        e.Handled = true;
        TogglePlay();
      }
    }

    private void Play_Click(object sender, RoutedEventArgs e) => TogglePlay();

    private async void TogglePlay()
    {
      if (_bank.IsPlaying)
      {
        _bank.Pause();
        UpdatePlayback();
        return;
      }

      // Stopped: start the selected track (paused playback just resumes).
      if (!_bank.IsPaused && SelectedTrack is IAudioTrack selected && selected != _bank.Track)
      {
        if (!await LoadAsync(selected))
        {
          return;
        }
      }
      StartPlayback();
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
      _bank.Stop();
      UpdatePlayback();
    }

    private async Task PlayTrackAsync(IAudioTrack track)
    {
      if (await LoadAsync(track))
      {
        StartPlayback();
      }
    }

    /// <summary>Makes the track the one played; false if it can't be (shown in the transport).</summary>
    private async Task<bool> LoadAsync(IAudioTrack track)
    {
      if (track.NotPlayable != null)
      {
        _busyNote = null;
        NowPlaying.Text = track.NotPlayable;
        return false;
      }
      int version = ++_loadVersion;
      _bank.Stop();
      _busyNote = "Decoding " + track.Name + " …";
      UpdatePlayback();
      try
      {
        await _bank.LoadAsync(track);
        return version == _loadVersion;  // Another track was chosen meanwhile.
      }
      catch (Exception ex)
      {
        if (version == _loadVersion)
        {
          ShowPlayError(ex);
        }
        return false;
      }
      finally
      {
        if (version == _loadVersion)
        {
          _busyNote = null;
          UpdatePlayback();
        }
      }
    }

    private void StartPlayback()
    {
      try
      {
        _bank.Play();
      }
      catch (Exception ex)
      {
        ShowPlayError(ex);
      }
      UpdatePlayback();
    }

    private void PositionSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
      if (_updatingSlider)
      {
        return;
      }
      PositionText.Text = FormatTime(TimeSpan.FromMilliseconds(e.NewValue));
      // Keyboard changes seek right away, mouse clicks and drags when the button is released.
      if (IsLeftMouseButtonDown())
      {
        _seeking = true;
      }
      if (!_seeking)
      {
        Seek();
      }
    }

    private static bool IsLeftMouseButtonDown() => (GetKeyState(0x01) & 0x8000) != 0;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetKeyState(int virtualKey);

    private void EndSeek()
    {
      if (_seeking)
      {
        _seeking = false;
        Seek();
      }
    }

    private void Seek()
    {
      if (_bank.Track == null)
      {
        return;
      }
      try
      {
        _bank.Seek(TimeSpan.FromMilliseconds(PositionSlider.Value));
      }
      catch (Exception ex)
      {
        ShowPlayError(ex);
      }
      UpdatePlayback();
    }

    private void Loop_Click(object sender, RoutedEventArgs e)
    {
      s_looped = LoopButton.IsChecked == true;
      _bank.Looped = s_looped;
    }

    private void Downmix_Click(object sender, RoutedEventArgs e)
    {
      s_downmix = DownmixButton.IsChecked == true;
      try
      {
        _bank.Downmix = s_downmix;
      }
      catch (Exception ex)
      {
        ShowPlayError(ex);
      }
      UpdatePlayback();
    }

    private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
      s_volume = e.NewValue;
      _bank.Volume = (float)(e.NewValue / 100);
      ToolTipService.SetToolTip(VolumeSlider, "Volume: " + (int)e.NewValue + " %");
    }

    private void UpdatePlayback()
    {
      var track = _bank.Track;
      TimeSpan length = _bank.Length;
      TimeSpan position = _bank.Position;

      _updatingSlider = true;
      double max = Math.Max(1, length.TotalMilliseconds);
      if (PositionSlider.Maximum != max)
      {
        PositionSlider.Maximum = max;
        LengthText.Text = FormatTime(length);
      }
      if (!_seeking)
      {
        PositionSlider.Value = Math.Min(max, Math.Max(0, position.TotalMilliseconds));
        PositionText.Text = FormatTime(position);
      }
      _updatingSlider = false;

      bool playing = _bank.IsPlaying;
      if (playing != _isPlaying)
      {
        _isPlaying = playing;
        PlayIcon.Glyph = playing ? "" : "";
        ToolTipService.SetToolTip(PlayButton, playing ? "Pause (Space)" : "Play (Space)");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(PlayButton, playing ? "Pause" : "Play");
      }

      DownmixButton.IsEnabled = track != null && track.ChannelCount > 2 && _bank is IvaudBank;
      if (_busyNote != null)
      {
        NowPlaying.Text = _busyNote;
      }
      else if (track != null)
      {
        NowPlaying.Text = track.Name + "   ·   " + track.LayoutName + ", " + track.SamplesPerSecond + " Hz"
                          + (playing ? "" : _bank.IsPaused ? "   ·   paused" : "");
      }
      else
      {
        NowPlaying.Text = SelectedTrack?.NotPlayable ?? "";
      }
    }

    private void ShowPlayError(Exception ex)
    {
      _context.ShowMessage("Could not play the audio", ex.Message, true);
    }

    private static string FormatTime(TimeSpan time)
    {
      return time.TotalHours >= 1
               ? string.Format("{0}:{1:00}:{2:00}", (int)time.TotalHours, time.Minutes, time.Seconds)
               : string.Format("{0}:{1:00}", (int)time.TotalMinutes, time.Seconds);
    }

    #endregion

    #region Export

    private async void ExportWave_Click(object sender, RoutedEventArgs e)
    {
      if (SelectedTrack is not IAudioTrack track)
      {
        _context.ShowMessage("Nothing selected", "Select the track to export.", true);
        return;
      }
      if (track.NotPlayable != null)
      {
        _context.ShowMessage("Can't convert " + track.Name, track.NotPlayable, true);
        return;
      }

      string? path = await ViewerPages.PickSaveFileAsync(_context.Window, SafeFileName(track.Name) + ".wav", ".wav");
      if (path == null)
      {
        return;
      }

      await ExportAsync(path, stream => _bank.ExportWave(track, stream));
    }

    private async void ExportAll_Click(object sender, RoutedEventArgs e)
    {
      string? path = await ViewerPages.PickSaveFileAsync(_context.Window, Path.GetFileNameWithoutExtension(_context.File.Name) + ".wav", ".wav");
      if (path != null)
      {
        await ExportAsync(path, stream => _bank.ExportAll(stream));
      }
    }

    private async void ExportRaw_Click(object sender, RoutedEventArgs e)
    {
      if (SelectedTrack is not IAudioTrack track)
      {
        _context.ShowMessage("Nothing selected", "Select the track to export.", true);
        return;
      }
      string? path = await ViewerPages.PickSaveFileAsync(_context.Window, SafeFileName(track.Name) + ".bin", ".bin");
      if (path == null)
      {
        return;
      }
      try
      {
        await System.IO.File.WriteAllBytesAsync(path, _bank.ExportRaw(track));
        _context.ShowMessage("Exported", path + "   ·   the audio data as it is stored", false);
      }
      catch (Exception ex)
      {
        _context.ShowMessage("Could not export the track", ex.Message, true);
      }
    }

    private async Task ExportAsync(string path, Action<Stream> write)
    {
      // Playback and export may read the same sound bank stream.
      if (_bank is IvaudBank)
      {
        _bank.Stop();
        UpdatePlayback();
      }
      try
      {
        await Task.Run(() =>
        {
          using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
          write(stream);
        });
        _context.ShowMessage("Exported", path, false);
      }
      catch (Exception ex)
      {
        _context.ShowMessage("Could not export the audio", ex.Message, true);
      }
    }

    private async void ExportOriginal_Click(object sender, RoutedEventArgs e)
    {
      string name = _context.File.Name;
      string? path = await ViewerPages.PickSaveFileAsync(_context.Window, name, Path.GetExtension(name));
      if (path != null)
      {
        await System.IO.File.WriteAllBytesAsync(path, _context.Data);
        _context.ShowMessage("Exported", path + (_bank is IvaudBank
          ? ". To put an edited version back, use Import in the main window; the file name must stay \"" + name + "\"."
          : ""), false);
      }
    }

    private static string SafeFileName(string name)
    {
      foreach (char c in Path.GetInvalidFileNameChars())
      {
        name = name.Replace(c, '_');
      }
      return name;
    }

    #endregion

    public void Dispose()
    {
      _loadVersion++;
      _timer.Stop();
      _bank.Dispose();
    }
  }

  /// <summary>Audio banks: GTA IV's .ivaud, GTA VI's .awc.</summary>
  internal sealed class AudioViewer : IViewer
  {
    public object Load(RageLib.FileSystem.Common.File file, byte[] data)
    {
      // GTA VI audio wave containers start with "ADAT".
      if (data.Length >= 4 && data[0] == 'A' && data[1] == 'D' && data[2] == 'A' && data[3] == 'T')
      {
        return new AwcBank(AwcFile.Parse(data), data);
      }
      var audio = new AudioFile();
      audio.Open(new MemoryStream(data));
      return new IvaudBank(audio);
    }

    public FrameworkElement CreateView(ViewerContext context, object loaded) => new AudioView(context, (IAudioBank)loaded);
  }
}
