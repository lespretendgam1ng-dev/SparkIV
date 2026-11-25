/**********************************************************************\

 RageLib - SparkIV
 GTA VI texture pools (.ttpf) as a read-only folder of DDS textures.

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
using RageLib.GtaVI.Textures;
using Directory = RageLib.FileSystem.Common.Directory;
using File = RageLib.FileSystem.Common.File;

namespace RageLib.FileSystem
{
  /// <summary>
  /// A GTA VI texture pool (data\ttdb\...\*.ttpf): every texture it holds as a .dds file, detiled and
  /// decompressed when it is opened. The pool stores no names, so they are generated (ordinal + id).
  /// Textures SparkIV can't decode yet are in a folder of their own.
  /// </summary>
  public class TtpfFileSystem : Common.FileSystem
  {
    public const string UnreadableFolder = "(not readable yet)";

    public TtpfPool Pool { get; private set; }

    public override bool SupportsRebuild => false;
    public override bool HasDirectoryStructure => true;

    public static bool IsTtpfFile(string path) => TtpfPool.IsTtpfFile(path);

    public override void Open(string filename)
    {
      Pool = TtpfPool.Open(filename);
      var root = new Directory { Name = "/" };
      Directory unreadable = null;
      foreach (var t in Pool.Textures)
      {
        Directory parent = root;
        if (!t.CanExtract)
        {
          if (unreadable == null)
          {
            unreadable = new Directory { Name = UnreadableFolder, ParentDirectory = root };
            root.AddObject(unreadable);
          }
          parent = unreadable;
        }

        int index = t.Index;
        string shape = t.Dimension == 3 ? $" · volume ({t.Depth})" : t.Dimension == 2 ? " · cube map" : t.Depth > 1 ? $" · array ({t.Depth})" : "";
        string mips = t.CanExtract
          ? (t.BaseMipLevel > 0 || t.Mips < t.SourceMips ? $"{t.Mips} of {t.SourceMips} mips" : $"{t.Mips} mips")
          : t.Limitation;
        var file = new File(() => Pool.CreateDds(index))
        {
          Name = t.Name + ".dds",
          Size = t.DdsSize,
          CompressedSize = t.StoredSize,
          IsCompressed = t.StoredSize != t.DdsSize,
          Offset = 0,
          Codec = "texture",
          Detail = $"{t.Width} × {t.Height} · {t.FormatName}{shape} · {mips}",
          DetailLong = $"texture {t.Index} of the pool · {t.Width} × {t.Height} · {t.FormatName}{shape} · {mips}" +
                       $" · asset key {t.AssetKey:X16} · id {t.Id}" +
                       (t.BaseMipLevel > 0 ? $" · starts at mip {t.BaseMipLevel} (the larger ones need an unsupported Xbox BCPack codec)" : "") +
                       " · the pool stores no names; this one is generated",
          ParentDirectory = parent,
        };
        parent.AddObject(file);
      }
      RootDirectory = root;
    }

    public override void Save() => throw new NotSupportedException("Texture pools are read-only.");
    public override void Rebuild() => throw new NotSupportedException("Texture pools are read-only.");
    public override void Close() { }
  }
}
