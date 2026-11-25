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
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SparkIV.WinUI.Services;
using Windows.Storage.Pickers;

namespace SparkIV.WinUI
{
  /// <summary>
  /// Asks which game a file is from, when SparkIV can't tell from where the file is: like the start of
  /// OpenIV, one line per game with its platforms. The game's folder is asked for once and remembered.
  /// </summary>
  public sealed partial class GameChooserWindow : Window
  {
    // Continuations not inline: the window may be closing (X) when the result is set.
    private readonly TaskCompletionSource<GameInstall?> _result = new TaskCompletionSource<GameInstall?>(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _closed;

    /// <param name="path">The file the game is asked for; null when SparkIV starts and asks which game to open.</param>
    private GameChooserWindow(string? path, bool optional)
    {
      InitializeComponent();

      Title = "Select game - SparkIV";
      TitleText.Text = "Select game";
      if (path == null)
      {
        QuestionText.Text = "Which game do you want to open?";
        FileText.Text = "SparkIV shows the game's folder and archives.";
        CancelButton.Content = "Close";
      }
      else
      {
        FileText.Text = Path.GetFileName(path) + " is not in a game folder. " +
                        (optional ? "With its game, the page shows the game's texts and links to the other sites."
                                  : "SparkIV needs the game for the key of the archive.");
        CancelButton.Content = optional ? "Without game" : "Cancel";
      }
      HintText.Text = "The folder of a game is asked for once and remembered; it can be changed in the settings.";

      ExtendsContentIntoTitleBar = true;
      SetTitleBar(AppTitleBar);
      Root.Loaded += async (sender, args) =>
      {
        TitleBarColors.Apply(this, Root);
        // First start: once, over the game selector, that SparkIV is for offline modding only.
        await OfflineNotice.ShowIfNeededAsync(Root.XamlRoot);
      };
      Root.ActualThemeChanged += (sender, args) => TitleBarColors.Apply(this, Root);

      string icon = Path.Combine(AppContext.BaseDirectory, "SparkIV.ico");
      if (System.IO.File.Exists(icon))
      {
        AppWindow.SetIcon(icon);
      }
      if (AppWindow.Presenter is OverlappedPresenter presenter)
      {
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
      }
      AppWindow.Resize(new Windows.Graphics.SizeInt32(560, 520));
      CenterOnScreen();

      // For a file: the games it can be from. An RDR2 archive (RPF8) is only RDR2's; any other file only GTA IV's.
      System.Collections.Generic.IEnumerable<Game> games = path == null ? Games.All
                : RageLib.Rdr2.Pc.Rpf8Archive.IsRpf8File(path) ? new[] { Games.Rdr2 }
                : Games.All.Where(g => g != Games.Rdr2);
      foreach (var game in games)
      {
        GameList.Children.Add(CreateRow(game));
      }

      Closed += (sender, args) =>
      {
        _closed = true;
        _result.TrySetResult(null);
      };
    }

    private async Task<GameInstall?> ResultAsync()
    {
      Activate();
      GameInstall? install = await _result.Task;
      if (!_closed)
      {
        Close();
      }
      return install;
    }

    /// <summary>
    /// Asks for the game of a file. Null if the user cancelled. <paramref name="optional"/>: the file
    /// opens without a game too (a web page), so cancelling is offered as "Without game".
    /// </summary>
    internal static Task<GameInstall?> AskAsync(string path, bool optional) => new GameChooserWindow(path, optional).ResultAsync();

    /// <summary>Asks which game to open, as OpenIV does when it starts. Null if the window was closed.</summary>
    internal static Task<GameInstall?> ChooseGameAsync() => new GameChooserWindow(null, optional: true).ResultAsync();

    private void CenterOnScreen()
    {
      var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
      var size = AppWindow.Size;
      AppWindow.Move(new Windows.Graphics.PointInt32(area.X + (area.Width - size.Width) / 2, area.Y + (area.Height - size.Height) / 2));
    }

    #region Rows

    private FrameworkElement CreateRow(Game game)
    {
      var grid = new Grid { Padding = new Thickness(8), ColumnSpacing = 16 };
      grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
      grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

      var tile = new Border
      {
        Width = 64,
        Height = 64,
        CornerRadius = new CornerRadius(6),
        VerticalAlignment = VerticalAlignment.Top,
        Background = (Brush)Application.Current.Resources["ControlFillColorSecondaryBrush"],
      };
      grid.Children.Add(tile);

      var text = new StackPanel { Spacing = 2 };
      Grid.SetColumn(text, 1);
      grid.Children.Add(text);

      text.Children.Add(new TextBlock { Text = game.Name, Style = (Style)Application.Current.Resources["BodyLargeTextBlockStyle"] });

      // One link per platform, like OpenIV. Each platform has its own folder: asked for on the first click,
      // changed in the settings.
      var platforms = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(-12, 0, 0, 0) };
      foreach (var platform in game.Platforms)
      {
        var button = new HyperlinkButton { Content = platform };
        button.Click += async (sender, args) => await ChooseAsync(game, platform);
        platforms.Children.Add(button);
      }
      text.Children.Add(platforms);

      tile.Child = CreateTileContent(game);
      return grid;
    }

    /// <summary>
    /// The icon of the game's exe when the folder of a platform is known and the exe has one; else (no folder yet, or
    /// an exe without an icon of its own, as GTA VI's Xbox build) a game controller from Windows' own symbol font.
    /// </summary>
    private static UIElement CreateTileContent(Game game)
    {
      string? folder = game.Platforms.Select(p => Games.InstallFolder(game, p)).FirstOrDefault(f => f != null);
      string exe = game.Executable;
      if (folder != null && System.IO.File.Exists(Path.Combine(folder, exe)) && HasOwnIcon(Path.Combine(folder, exe)))
      {
        try
        {
          using var icon = System.Drawing.Icon.ExtractAssociatedIcon(Path.Combine(folder, exe));
          if (icon != null)
          {
            using var bitmap = icon.ToBitmap();
            using var png = new MemoryStream();
            bitmap.Save(png, System.Drawing.Imaging.ImageFormat.Png);
            png.Position = 0;
            var image = new BitmapImage();
            image.SetSource(png.AsRandomAccessStream());
            return new Image { Source = image, Width = 40, Height = 40 };
          }
        }
        catch (Exception)
        {
          // Then the controller.
        }
      }
      return new FontIcon
      {
        Glyph = "",
        FontSize = 32,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
      };
    }

    #endregion

    /// <summary>Whether the file has an icon of its own (ExtractIconEx with index -1 returns the number of icons).</summary>
    private static bool HasOwnIcon(string path)
    {
      try
      {
        return ExtractIconExW(path, -1, IntPtr.Zero, IntPtr.Zero, 0) > 0;
      }
      catch (Exception)
      {
        return true;  // Then as before: whatever Windows gives for it.
      }
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern uint ExtractIconExW(string file, int index, IntPtr large, IntPtr small, uint count);

    private async Task ChooseAsync(Game game, string platform)
    {
      Info.IsOpen = false;
      string? folder = Games.InstallFolder(game, platform);
      if (folder == null)
      {
        var (picked, error) = await GameFolders.PickAsync(this, game, platform);
        if (error != null)
        {
          Info.Title = "Not a folder of " + game.Name;
          Info.Message = error;
          Info.IsOpen = true;
        }
        if (picked == null)
        {
          return;
        }
        folder = picked;
      }
      _result.TrySetResult(new GameInstall(game, folder, platform));
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _result.TrySetResult(null);
  }
}
