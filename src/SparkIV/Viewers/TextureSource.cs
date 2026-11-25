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
using System.Drawing.Imaging;
using RageLib.GtaVI.Textures;
using RageLib.Textures;
using Bitmap = System.Drawing.Bitmap;

namespace SparkIV.WinUI.Viewers
{
  /// <summary>
  /// A texture as the texture viewer shows it, whatever game or platform it comes from: its name, size,
  /// format and mipmap levels, and its pixels.
  /// </summary>
  public interface IViewerTexture
  {
    string Title { get; }
    int Width { get; }
    int Height { get; }
    int Levels { get; }

    /// <summary>The pixel format as the game stores it ("DXT5", "BC7_UNORM_SRGB").</summary>
    string FormatName { get; }

    int GetWidth(int level);
    int GetHeight(int level);

    /// <summary>One mipmap level as 32-bit BGRA, top row first (not premultiplied).</summary>
    byte[] DecodeBgra(int level);

    /// <summary>The full-size texture with its alpha channel, as a PNG.</summary>
    void SavePng(string path);
  }

  /// <summary>A texture of GTA IV and its episodes (a .wtd, or embedded in a model or web page).</summary>
  internal sealed class GtaIVTexture : IViewerTexture
  {
    private readonly object _lock;

    public GtaIVTexture(Texture texture, object owner)
    {
      Texture = texture;
      _lock = owner;
    }

    public Texture Texture { get; }

    public string Title => Texture.TitleName;
    public int Width => (int)Texture.Width;
    public int Height => (int)Texture.Height;
    public int Levels => Texture.Levels;
    public string FormatName => Texture.TextureType.ToString();
    public int GetWidth(int level) => (int)Texture.GetWidth(level);
    public int GetHeight(int level) => (int)Texture.GetHeight(level);

    public byte[] DecodeBgra(int level)
    {
      using Bitmap bitmap = (Bitmap)Texture.Decode(level);
      return TextureBitmaps.ToBgra(bitmap);
    }

    public void SavePng(string path)
    {
      lock (_lock)
      {
        using var image = Texture.Decode();
        image.Save(path, ImageFormat.Png);
      }
    }
  }

  /// <summary>A texture of GTA VI (a .ztd texture dictionary resource).</summary>
  internal sealed class GtaVITexture : IViewerTexture
  {
    private readonly Rpf9TextureDictionary _dictionary;
    private readonly Rpf9Texture _texture;

    public GtaVITexture(Rpf9TextureDictionary dictionary, Rpf9Texture texture)
    {
      _dictionary = dictionary;
      _texture = texture;
    }

    public string Title => _texture.Name;
    public int Width => _texture.Width;
    public int Height => _texture.Height;
    public int Levels => _texture.MipCount;
    public string FormatName => _texture.FormatName +
      (_texture.Dimension == 2 ? " cube" : _texture.Dimension == 3 ? " volume" : _texture.Depth > 1 ? " array" : "");
    public int GetWidth(int level) => _texture.GetWidth(level);
    public int GetHeight(int level) => _texture.GetHeight(level);

    public byte[] DecodeBgra(int level) => _dictionary.DecodeBgra(_texture.Index, level);

    public void SavePng(string path)
    {
      using var bitmap = TextureBitmaps.FromBgra(DecodeBgra(0), Width, Height);
      bitmap.Save(path, ImageFormat.Png);
    }
  }

  /// <summary>A DDS image (exported, or a texture of a GTA VI texture pool).</summary>
  internal sealed class DdsTexture : IViewerTexture
  {
    private readonly DdsImage _image;

    public DdsTexture(string title, DdsImage image)
    {
      Title = title;
      _image = image;
    }

    public string Title { get; }
    public int Width => _image.Width;
    public int Height => _image.Height;
    public int Levels => _image.Mips;
    public string FormatName => _image.FormatName +
      (_image.IsCube ? " cube" : _image.IsVolume ? " volume" : _image.Depth > 1 ? " array" : "");
    public int GetWidth(int level) => _image.GetWidth(level);
    public int GetHeight(int level) => _image.GetHeight(level);
    public byte[] DecodeBgra(int level) => _image.DecodeBgra(level);

    public void SavePng(string path)
    {
      using var bitmap = TextureBitmaps.FromBgra(DecodeBgra(0), Width, Height);
      bitmap.Save(path, ImageFormat.Png);
    }
  }

  internal static class TextureBitmaps
  {
    public static byte[] ToBgra(Bitmap bitmap)
    {
      var rect = new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height);
      var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
      try
      {
        var bgra = new byte[bitmap.Width * bitmap.Height * 4];
        for (int y = 0; y < bitmap.Height; y++)
        {
          System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, bgra, y * bitmap.Width * 4, bitmap.Width * 4);
        }
        return bgra;
      }
      finally
      {
        bitmap.UnlockBits(data);
      }
    }

    public static Bitmap FromBgra(byte[] bgra, int width, int height)
    {
      var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
      var data = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
      try
      {
        for (int y = 0; y < height; y++)
        {
          System.Runtime.InteropServices.Marshal.Copy(bgra, y * width * 4, data.Scan0 + y * data.Stride, width * 4);
        }
      }
      finally
      {
        bitmap.UnlockBits(data);
      }
      return bitmap;
    }
  }
}
