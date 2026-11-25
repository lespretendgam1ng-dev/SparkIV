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
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace SparkIV.WinUI.Services
{
  /// <summary>
  /// The notice of the first start: SparkIV may only be used for offline modding (NOTICE.txt). Shown once, over the
  /// first window SparkIV opens (the game selector, or the window of a file SparkIV was started with).
  /// </summary>
  internal static class OfflineNotice
  {
    private static bool s_showing;

    public static async Task ShowIfNeededAsync(XamlRoot root)
    {
      if (s_showing || AppSettings.OfflineNoticeShown)
      {
        return;
      }
      s_showing = true;
      try
      {
        var dialog = new ContentDialog
        {
          XamlRoot = root,
          Title = "Offline modding only",
          Content = Message(),
          PrimaryButtonText = "I understand",
          DefaultButton = ContentDialogButton.Primary,
        };
        // Only "I understand" counts: a dialog that couldn't show (the window closing, another dialog open)
        // returns None, and the notice comes again next time.
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
          AppSettings.OfflineNoticeShown = true;
        }
      }
      catch (Exception)
      {
        // Shown again at the next start.
      }
      finally
      {
        s_showing = false;
      }
    }

    private static UIElement Message()
    {
      var grid = new Grid { ColumnSpacing = 12 };
      grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
      grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
      grid.Children.Add(new FontIcon
      {
        Glyph = "",
        FontSize = 28,
        VerticalAlignment = VerticalAlignment.Top,
        Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCautionBrush"],
      });

      var text = new StackPanel { Spacing = 10 };
      text.Children.Add(new TextBlock
      {
        Text = "This tool may only be used for offline modding.",
        TextWrapping = TextWrapping.Wrap,
        Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
      });
      text.Children.Add(new TextBlock
      {
        Text = "We expressly distance ourselves from any use of SparkIV for online modding: for changing or gaining an " +
               "advantage in the online modes of the games (such as GTA Online or Red Dead Online), for cheating, or for " +
               "anything else that affects other players or the games' online services.",
        TextWrapping = TextWrapping.Wrap,
      });
      text.Children.Add(new TextBlock
      {
        Text = "SparkIV only works with your own, legally owned copy of the games.",
        TextWrapping = TextWrapping.Wrap,
      });
      text.Children.Add(new TextBlock
      {
        Text = "You find this notice again in Settings → About (\"Notice on use\").",
        TextWrapping = TextWrapping.Wrap,
        Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
        Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
      });
      Grid.SetColumn(text, 1);
      grid.Children.Add(text);
      return grid;
    }
  }
}
