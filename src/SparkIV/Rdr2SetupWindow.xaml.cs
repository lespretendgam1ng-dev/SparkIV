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
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using RageLib.Rdr2.Pc;
using SparkIV.WinUI.Services;

namespace SparkIV.WinUI
{
  /// <summary>
  /// Guides through the one-time setup of Red Dead Redemption 2, step by step like an installer: SparkIV reads the
  /// keys of the game's archives from the running game's memory (they are not stored readably anywhere else) and
  /// keeps them on this PC. The game notices another program reading its memory and stops with an error, so this
  /// is done offline in story mode. Afterwards the game is not needed again.
  /// </summary>
  public sealed partial class Rdr2SetupWindow : Window
  {
    private readonly TaskCompletionSource<Rdr2Secrets?> _result = new TaskCompletionSource<Rdr2Secrets?>();
    private readonly string _gameFolder;
    private readonly List<(string Title, FrameworkElement Page)> _steps = new List<(string, FrameworkElement)>();
    private readonly List<(Border Number, TextBlock Title)> _stepRows = new List<(Border, TextBlock)>();
    private readonly DispatcherQueueTimerWrapper _timer;
    private int _step;
    private Rdr2Secrets? _secrets;
    private CancellationTokenSource? _extracting;

    // Live state shown on the pages.
    private TextBlock _onlineText1 = null!, _onlineText6 = null!, _gameText2 = null!, _gameText5 = null!, _extractText = null!;
    private ProgressBar _extractProgress = null!;
    private Button _extractButton = null!;
    private Storyboard? _windowedAnimation;

    private Rdr2SetupWindow(string gameFolder)
    {
      _gameFolder = gameFolder;
      InitializeComponent();

      Title = "Set up Red Dead Redemption 2 - SparkIV";
      ExtendsContentIntoTitleBar = true;
      SetTitleBar(AppTitleBar);
      Root.Loaded += (sender, args) => TitleBarColors.Apply(this, Root);
      Root.ActualThemeChanged += (sender, args) => TitleBarColors.Apply(this, Root);
      string icon = Path.Combine(AppContext.BaseDirectory, "SparkIV.ico");
      if (System.IO.File.Exists(icon))
      {
        AppWindow.SetIcon(icon);
      }
      if (AppWindow.Presenter is OverlappedPresenter presenter)
      {
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = true;
      }
      AppWindow.Resize(new Windows.Graphics.SizeInt32(900, 620));
      CenterOnScreen();

      BuildSteps();
      foreach (var (title, page) in _steps)
      {
        page.Visibility = Visibility.Collapsed;
        Pages.Children.Add(page);
        AddStepRow(title);
      }
      ShowStep(0);

      // Whether the PC is online and the game runs, shown live.
      _timer = new DispatcherQueueTimerWrapper(DispatcherQueue, TimeSpan.FromSeconds(1), UpdateStatus);
      UpdateStatus();

      Closed += (sender, args) =>
      {
        _timer.Stop();
        _extracting?.Cancel();
        // Once read, the keys are saved: the setup is done even if the window is closed early.
        _result.TrySetResult(_secrets);
      };
    }

    /// <summary>
    /// Runs the setup for the install in <paramref name="gameFolder"/>. Returns the keys read and saved, or null if it
    /// was cancelled before they were read.
    /// </summary>
    internal static async Task<Rdr2Secrets?> RunAsync(string gameFolder)
    {
      var window = new Rdr2SetupWindow(gameFolder);
      window.Activate();
      var secrets = await window._result.Task;
      window.Close();
      return secrets;
    }

    private void CenterOnScreen()
    {
      var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
      var size = AppWindow.Size;
      AppWindow.Move(new Windows.Graphics.PointInt32(area.X + (area.Width - size.Width) / 2, area.Y + (area.Height - size.Height) / 2));
    }

    #region Steps

    private void BuildSteps()
    {
      // 1
      _onlineText1 = StatusText();
      _steps.Add(("Go offline", Page("Turn off the internet",
        Para("Turn off this PC's internet connection: switch off Wi-Fi or unplug the network cable."),
        Para("The Rockstar Games Launcher then switches to offline mode, and Red Dead Redemption 2 starts without going online."),
        Note("Why: SparkIV reads the keys of the game's archives from the game's memory, once. The game notices that " +
             "another program reads its memory and stops with an error message. So this is done offline, in story mode."),
        _onlineText1)));

      // 2
      _gameText2 = StatusText();
      _steps.Add(("Start the game", Page("Start Red Dead Redemption 2",
        Para("Start the game from the Rockstar Games Launcher (now offline), as you always do."),
        Para("SparkIV notices when it is running."),
        _gameText2)));

      // 3
      _steps.Add(("Load the story mode", Page("Load the story mode",
        Para("Choose Story Mode and wait until the game has loaded completely: when you can move your character."),
        Para("Then press Alt+Enter. The game leaves full screen and shows in a window, so you can switch back to SparkIV " +
             "(Alt+Tab works too)."),
        WindowedAnimation())));

      // 4
      _extractText = StatusText();
      _extractProgress = new ProgressBar { Minimum = 0, Maximum = 1, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 0) };
      _extractButton = new Button { Content = "Read the keys", Style = (Style)Application.Current.Resources["AccentButtonStyle"], Margin = new Thickness(0, 8, 0, 0) };
      _extractButton.Click += async (sender, args) => await ExtractAsync();
      _steps.Add(("Read the keys", Page("Read the keys",
        Para("With the story mode loaded, click the button. SparkIV searches the game's memory for the keys of its " +
             "archives and the tables of their cipher. This takes up to a minute."),
        _extractButton, _extractProgress, _extractText)));

      // 5
      _gameText5 = StatusText();
      _steps.Add(("Close the game", Page("Close the game",
        Para("Red Dead Redemption 2 now shows an error message, because another program read its memory. This is expected."),
        Para("Click OK in the game's message. The game then closes."),
        _gameText5)));

      // 6
      _onlineText6 = StatusText();
      _steps.Add(("Go online again", Page("Turn the internet on again",
        Para("Switch Wi-Fi back on or plug the network cable in again."),
        _onlineText6)));

      // 7
      _steps.Add(("Finish", Page("Done",
        Para("SparkIV can now read Red Dead Redemption 2's archives."),
        Para("The keys are kept on this PC (" + Rdr2GameMemory.DefaultCachePath + "). They do not change, so this setup is " +
             "not needed again."),
        Note("Click Finish to open the game's folder."))));
    }

    private void ShowStep(int index)
    {
      _step = index;
      for (int i = 0; i < _steps.Count; i++)
      {
        _steps[i].Page.Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
        var (number, title) = _stepRows[i];
        bool done = i < index, current = i == index;
        number.Background = (Brush)Application.Current.Resources[current ? "AccentFillColorDefaultBrush" : done ? "AccentFillColorSecondaryBrush" : "ControlFillColorSecondaryBrush"];
        ((TextBlock)number.Child).Foreground = (Brush)Application.Current.Resources[current || done ? "TextOnAccentFillColorPrimaryBrush" : "TextFillColorSecondaryBrush"];
        ((TextBlock)number.Child).Text = done ? "✓" : (i + 1).ToString();
        title.FontWeight = current ? FontWeights.SemiBold : FontWeights.Normal;
        title.Foreground = (Brush)Application.Current.Resources[current ? "TextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush"];
      }

      bool last = index == _steps.Count - 1;
      BackButton.IsEnabled = index > 0 && !last;
      NextButton.Content = last ? "Finish" : "Next";
      CancelButton.IsEnabled = !last;
      if (index == 2) _windowedAnimation?.Begin(); else _windowedAnimation?.Stop();
      UpdateStatus();
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
      if (_step == _steps.Count - 1)
      {
        _result.TrySetResult(_secrets);
        return;
      }
      ShowStep(_step + 1);
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
      if (_step > 0)
      {
        ShowStep(_step - 1);
      }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
      _extracting?.Cancel();
      _result.TrySetResult(_secrets);
    }

    #endregion

    #region Status

    private void UpdateStatus()
    {
      bool? online = IsOnline();
      string onlineText = online == null ? "Internet: unknown" : online.Value ? "⚠  This PC is online." : "✓  This PC is offline.";
      _onlineText1.Text = onlineText;
      _onlineText6.Text = online == null ? "Internet: unknown" : online.Value ? "✓  This PC is online again." : "This PC is still offline.";

      bool running = Rdr2GameMemory.IsGameRunning();
      _gameText2.Text = running ? "✓  Red Dead Redemption 2 is running." : "Waiting for Red Dead Redemption 2 to start ...";
      _gameText5.Text = running ? "The game is still running." : "✓  The game has closed.";

      // Reading the keys needs the running game; leaving that step needs the keys.
      _extractButton.IsEnabled = running && _extracting == null && _secrets == null;
      NextButton.IsEnabled = _step switch
      {
        1 => running,
        3 => _secrets != null,
        _ => true,
      };
    }

    /// <summary>Whether Windows has a connection to the internet (null if it can't tell).</summary>
    private static bool? IsOnline()
    {
      try
      {
        var profile = Windows.Networking.Connectivity.NetworkInformation.GetInternetConnectionProfile();
        return profile != null &&
               profile.GetNetworkConnectivityLevel() == Windows.Networking.Connectivity.NetworkConnectivityLevel.InternetAccess;
      }
      catch (Exception)
      {
        return null;
      }
    }

    private async Task ExtractAsync()
    {
      _extracting = new CancellationTokenSource();
      var token = _extracting.Token;
      _extractButton.IsEnabled = false;
      _extractProgress.Visibility = Visibility.Visible;
      _extractProgress.Value = 0;
      _extractText.Text = "Searching the game's memory ...";
      try
      {
        string exe = Rdr2Support.ExePath(_gameFolder);
        var secrets = await Task.Run(() => Rdr2GameMemory.ExtractFromRunningGame(exe,
          progress: p => DispatcherQueue.TryEnqueue(() => _extractProgress.Value = p), cancel: token));
        await Task.Run(() => secrets.SaveCache(Rdr2GameMemory.DefaultCachePath));
        _secrets = secrets;
        _extractText.Text = "✓  " + secrets.KeyCount + " keys and the cipher tables were found and saved. Click Next.";
      }
      catch (OperationCanceledException)
      {
        _extractText.Text = "Cancelled.";
      }
      catch (Exception ex)
      {
        _extractText.Text = "⚠  " + ex.Message;
      }
      finally
      {
        _extracting = null;
        _extractProgress.Visibility = Visibility.Collapsed;
        UpdateStatus();
      }
    }

    #endregion

    #region Page building

    private void AddStepRow(string title)
    {
      var grid = new Grid { ColumnSpacing = 10, Padding = new Thickness(0, 4, 0, 4) };
      grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
      grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
      var number = new Border
      {
        Width = 24,
        Height = 24,
        CornerRadius = new CornerRadius(12),
        Child = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 },
      };
      var text = new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
      Grid.SetColumn(text, 1);
      grid.Children.Add(number);
      grid.Children.Add(text);
      StepList.Children.Add(grid);
      _stepRows.Add((number, text));
    }

    private static FrameworkElement Page(string heading, params UIElement[] content)
    {
      var panel = new StackPanel { Spacing = 12 };
      panel.Children.Add(new TextBlock { Text = heading, Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"], Margin = new Thickness(0, 0, 0, 4) });
      foreach (var c in content)
      {
        panel.Children.Add(c);
      }
      return panel;
    }

    private static TextBlock Para(string text) => new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };

    private static UIElement Note(string text) => new Border
    {
      Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
      BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
      BorderThickness = new Thickness(1),
      CornerRadius = new CornerRadius(6),
      Padding = new Thickness(12, 10, 12, 10),
      Child = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] },
    };

    private static TextBlock StatusText() => new TextBlock
    {
      TextWrapping = TextWrapping.Wrap,
      Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
      Margin = new Thickness(0, 4, 0, 0),
    };

    /// <summary>
    /// A small animation of Alt+Enter: the game fills the screen, the two keys are pressed, and it shrinks into a
    /// window with a title bar, over and over.
    /// </summary>
    private UIElement WindowedAnimation()
    {
      const double ScreenW = 320, ScreenH = 180;
      var accent = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
      var stroke = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

      var canvas = new Canvas { Width = ScreenW + 20, Height = ScreenH + 90, HorizontalAlignment = HorizontalAlignment.Left };

      // The monitor and its stand.
      var monitor = new Border { Width = ScreenW + 12, Height = ScreenH + 12, CornerRadius = new CornerRadius(8), BorderBrush = stroke, BorderThickness = new Thickness(2), Background = new SolidColorBrush(Colors.Black) };
      canvas.Children.Add(monitor);
      var stand = new Border { Width = 60, Height = 10, Background = stroke, CornerRadius = new CornerRadius(2) };
      Canvas.SetLeft(stand, (ScreenW + 12 - 60) / 2);
      Canvas.SetTop(stand, ScreenH + 14);
      canvas.Children.Add(stand);

      // The game: full screen at first; a window with a title bar afterwards.
      var title = new Border { Height = 14, Background = (Brush)Application.Current.Resources["ControlFillColorDefaultBrush"], Opacity = 0 };
      title.Child = new TextBlock { Text = "Red Dead Redemption 2", FontSize = 9, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
      var picture = new Border
      {
        Background = new LinearGradientBrush
        {
          StartPoint = new Windows.Foundation.Point(0, 0),
          EndPoint = new Windows.Foundation.Point(0, 1),
          GradientStops =
          {
            new GradientStop { Color = ColorHelper.FromArgb(255, 214, 120, 60), Offset = 0 },
            new GradientStop { Color = ColorHelper.FromArgb(255, 120, 50, 30), Offset = 0.6 },
            new GradientStop { Color = ColorHelper.FromArgb(255, 40, 25, 20), Offset = 1 },
          },
        },
      };
      picture.Child = new TextBlock { Text = "RDR2", FontWeight = FontWeights.Bold, FontSize = 28, Foreground = new SolidColorBrush(Colors.White), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.85 };
      var game = new Grid { Width = ScreenW, Height = ScreenH };
      game.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
      game.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
      game.Children.Add(title);
      Grid.SetRow(picture, 1);
      game.Children.Add(picture);
      var scale = new ScaleTransform { ScaleX = 1, ScaleY = 1, CenterX = ScreenW / 2, CenterY = ScreenH / 2 };
      game.RenderTransform = scale;
      Canvas.SetLeft(game, 8);
      Canvas.SetTop(game, 8);
      canvas.Children.Add(game);

      // The keys: Alt + Enter.
      var keys = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
      var alt = Key("Alt", 52);
      var enter = Key("Enter", 72);
      keys.Children.Add(alt);
      keys.Children.Add(new TextBlock { Text = "+", VerticalAlignment = VerticalAlignment.Center, FontSize = 18 });
      keys.Children.Add(enter);
      Canvas.SetLeft(keys, (ScreenW + 12 - 52 - 72 - 8 * 2 - 12) / 2);
      Canvas.SetTop(keys, ScreenH + 36);
      canvas.Children.Add(keys);

      // One cycle of 4 s: full screen, keys pressed at 1.0-1.4 s, window from 1.4 s, back to full screen at 3.6 s.
      var sb = new Storyboard { RepeatBehavior = RepeatBehavior.Forever, Duration = TimeSpan.FromSeconds(4) };
      void Keys(DependencyObject target, string property, params (double t, double v)[] frames)
      {
        var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(4) };
        foreach (var (t, v) in frames)
        {
          a.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = TimeSpan.FromSeconds(t), Value = v });
        }
        Storyboard.SetTarget(a, target);
        Storyboard.SetTargetProperty(a, property);
        sb.Children.Add(a);
      }
      Keys(scale, "ScaleX", (0, 1), (1.4, 1), (1.8, 0.66), (3.6, 0.66), (3.9, 1), (4, 1));
      Keys(scale, "ScaleY", (0, 1), (1.4, 1), (1.8, 0.66), (3.6, 0.66), (3.9, 1), (4, 1));
      Keys(title, "Opacity", (0, 0), (1.6, 0), (1.8, 1), (3.6, 1), (3.7, 0), (4, 0));
      foreach (var key in new[] { alt, enter })
      {
        var light = (Border)((Grid)key).Children[1];
        Keys(light, "Opacity", (0, 0), (0.95, 0), (1.0, 1), (1.4, 1), (1.5, 0), (4, 0));
      }
      _windowedAnimation = sb;

      var host = new StackPanel { Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
      host.Children.Add(canvas);
      return host;

      Grid Key(string label, double width)
      {
        var g = new Grid { Width = width, Height = 36 };
        g.Children.Add(new Border { CornerRadius = new CornerRadius(6), BorderBrush = stroke, BorderThickness = new Thickness(1.5), Background = (Brush)Application.Current.Resources["ControlFillColorDefaultBrush"] });
        g.Children.Add(new Border { CornerRadius = new CornerRadius(6), Background = accent, Opacity = 0 });
        g.Children.Add(new TextBlock { Text = label, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold });
        return g;
      }
    }

    #endregion

    /// <summary>A repeating timer on the window's thread.</summary>
    private sealed class DispatcherQueueTimerWrapper
    {
      private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _timer;

      public DispatcherQueueTimerWrapper(Microsoft.UI.Dispatching.DispatcherQueue queue, TimeSpan interval, Action tick)
      {
        _timer = queue.CreateTimer();
        _timer.Interval = interval;
        _timer.IsRepeating = true;
        _timer.Tick += (sender, args) => tick();
        _timer.Start();
      }

      public void Stop() => _timer.Stop();
    }
  }
}
