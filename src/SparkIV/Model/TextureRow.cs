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

using Microsoft.UI.Xaml.Media;
using SparkIV.WinUI.Viewers;

namespace SparkIV.WinUI.Model
{
  /// <summary>One texture in the list of a texture dictionary.</summary>
  public sealed class TextureRow
  {
    public TextureRow(IViewerTexture texture, ImageSource? thumbnail)
    {
      Texture = texture;
      Thumbnail = thumbnail;
    }

    public IViewerTexture Texture { get; }
    public ImageSource? Thumbnail { get; }

    public string Title => Texture.Title;
    public string Detail => Texture.Width + " × " + Texture.Height + "   ·   " + Texture.FormatName;
  }
}
