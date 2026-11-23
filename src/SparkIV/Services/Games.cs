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
using RageLib.Common;

namespace SparkIV.WinUI.Services
{
  /// <summary>
  /// A game SparkIV can open the files of. More RAGE games (and their platforms) are added here.
  /// </summary>
  internal sealed class Game
  {
    /// <param name="executable">The game's exe in its folder ("GTAIV.exe"), for the icon.</param>
    /// <param name="isInstall">Whether a folder is the game's.</param>
    /// <param name="check">The key util of a folder of a GTA IV-era game (null for a game without one, as RDR2).</param>
    public Game(string id, string name, string shortName, string[] platforms, string[] contentFolders, string executable,
                Func<string, bool> isInstall, Func<string, KeyUtil?> check, Func<string?> find)
    {
      Id = id;
      Name = name;
      ShortName = shortName;
      Platforms = platforms;
      ContentFolders = contentFolders;
      Executable = executable;
      _isInstall = isInstall;
      _check = check;
      _find = find;
    }

    private readonly Func<string, bool> _isInstall;
    private readonly Func<string, KeyUtil?> _check;
    private readonly Func<string?> _find;

    /// <summary>The game's exe in its folder ("GTAIV.exe").</summary>
    public string Executable { get; }

    /// <summary>Whether <paramref name="folder"/> is an installation of this game.</summary>
    public bool IsInstall(string folder) => System.IO.Directory.Exists(folder) && _isInstall(folder);

    /// <summary>The name in the settings ("gtaiv").</summary>
    public string Id { get; }

    public string Name { get; }

    /// <summary>For the tile when there is no icon ("IV").</summary>
    public string ShortName { get; }

    /// <summary>The platforms SparkIV reads this game's files of.</summary>
    public IReadOnlyList<string> Platforms { get; }

    /// <summary>
    /// The folders of the install that hold the game's content (pc\html, common\text), nearest first:
    /// the episodes, then the folder of the install itself ("").
    /// </summary>
    public IReadOnlyList<string> ContentFolders { get; }

    /// <summary>
    /// The key util for a folder of this game, or null if the folder isn't the game. Only GTA IV and the episodes
    /// have one (their archives' AES key); RDR2's archives use <see cref="Rdr2Support"/>.
    /// </summary>
    public KeyUtil? Check(string folder) => System.IO.Directory.Exists(folder) ? _check(folder) : null;

    /// <summary>The install found without asking (working directory, path file, registry), or null.</summary>
    public string? FindInstall() => _find();

    public override string ToString() => Name;
  }

  /// <summary>A game and where it is installed.</summary>
  internal sealed record GameInstall(Game Game, string Folder, string Platform)
  {
    public KeyUtil KeyUtil => Game.Check(Folder)!;
  }

  internal static class Games
  {
    public static Game GtaIV { get; } = new Game(
      "gtaiv", "Grand Theft Auto IV", "IV", new[] { "Windows" }, new[] { "" }, "GTAIV.exe",
      folder => System.IO.File.Exists(Path.Combine(folder, "GTAIV.exe")),
      folder => System.IO.File.Exists(Path.Combine(folder, "GTAIV.exe")) ? new KeyUtilGTAIV() : null,
      () => GameLocator.FindGameDirectory(episodes: false, out _));

    public static Game Episodes { get; } = new Game(
      "eflc", "Episodes from Liberty City", "EFLC", new[] { "Windows" }, new[] { "TBoGT", "TLAD", "" }, "EFLC.exe",
      folder => GameLocator.Check(folder, episodes: true) != null,
      folder => GameLocator.Check(folder, episodes: true),
      () => GameLocator.FindGameDirectory(episodes: true, out _));

    /// <summary>Red Dead Redemption 2 (PC): RPF8 archives, keys from the game itself (see <see cref="Rdr2Support"/>).</summary>
    public static Game Rdr2 { get; } = new Game(
      "rdr2", "Red Dead Redemption 2", "RDR2", new[] { "Windows" }, new[] { "" }, "RDR2.exe",
      folder => System.IO.File.Exists(Path.Combine(folder, "RDR2.exe")),
      folder => null,
      GameLocator.FindRdr2Directory);

    /// <summary>In the order the choice offers them.</summary>
    public static IReadOnlyList<Game> All { get; } = new[] { GtaIV, Episodes, Rdr2 };

    /// <summary>
    /// The game of what is open: models find their textures through its .ide files, web pages copied out
    /// of it its texts and other sites. Null when that isn't known (an archive on its own).
    /// </summary>
    public static GameInstall? Current { get; private set; }

    public static void SetCurrent(GameInstall? install)
    {
      Current = install;
      GameTextures.SetGameFolder(install?.Folder);
    }

    /// <summary>
    /// Where the game is installed for a platform: the folder set in the settings, else (Windows) the one
    /// found. Null if neither.
    /// </summary>
    public static string? InstallFolder(Game game, string platform)
    {
      string? stored = AppSettings.GetGamePath(game.Id, platform);
      if (stored != null && game.IsInstall(stored))
      {
        return stored;
      }
      return platform == "Windows" ? game.FindInstall() : null;
    }

    /// <summary>
    /// The game a file on disk belongs to, from the folders above it: an install of a game, or a folder
    /// chosen for one before. In the Complete Edition (one GTAIV.exe) a file in TBoGT or TLAD is of the
    /// episodes. Null if the file isn't in a game.
    /// </summary>
    public static GameInstall? ForFile(string path)
    {
      string full = Path.GetFullPath(path);
      for (var dir = new DirectoryInfo(Path.GetDirectoryName(full)!); dir != null; dir = dir.Parent)
      {
        string folder = dir.FullName;
        if (System.IO.File.Exists(Path.Combine(folder, "RDR2.exe")))
        {
          return new GameInstall(Rdr2, folder, "Windows");
        }
        if (System.IO.File.Exists(Path.Combine(folder, "EFLC.exe")))
        {
          return new GameInstall(Episodes, folder, "Windows");
        }
        if (System.IO.File.Exists(Path.Combine(folder, "GTAIV.exe")))
        {
          string first = Path.GetRelativePath(folder, full).Split(Path.DirectorySeparatorChar)[0];
          bool episode = Episodes.ContentFolders.Any(f => f.Length > 0 && f.Equals(first, StringComparison.OrdinalIgnoreCase)) &&
                         GameLocator.IsCompleteEdition(folder);
          return new GameInstall(episode ? Episodes : GtaIV, folder, "Windows");
        }
      }
      return null;
    }

    /// <summary>Whether a file can't be opened without knowing its game: archives (their key), web pages (the game's texts).</summary>
    public static bool NeedsGame(string fileName)
    {
      return Volumes.IsArchiveName(fileName) || Path.GetExtension(fileName).Equals(".whm", StringComparison.OrdinalIgnoreCase);
    }
  }
}
