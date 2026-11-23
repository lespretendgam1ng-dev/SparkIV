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

using System.IO;
using RageLib.Common;

namespace SparkIV.WinUI.Services
{
  /// <summary>
  /// Finds a game installation and the key for its archives.
  /// </summary>
  internal static class GameLocator
  {
    /// <summary>
    /// GTA IV: The Complete Edition: one GTAIV.exe, the episodes in the TLAD and TBoGT subfolders.
    /// </summary>
    public static bool IsCompleteEdition(string gamePath)
    {
      return System.IO.File.Exists(Path.Combine(gamePath, "GTAIV.exe")) &&
             (System.IO.Directory.Exists(Path.Combine(gamePath, "TBoGT")) || System.IO.Directory.Exists(Path.Combine(gamePath, "TLAD")));
    }

    /// <summary>
    /// The installation found automatically (working directory, path file, registry), or null.
    /// For the episodes the Complete Edition counts too: it has no EFLC.exe and only registers GTA IV.
    /// </summary>
    public static string? FindGameDirectory(bool episodes, out KeyUtil keyUtil)
    {
      keyUtil = episodes ? new KeyUtilEFLC() : new KeyUtilGTAIV();
      string? path = keyUtil.TryFindGameDirectory();
      if (path != null && System.IO.File.Exists(Path.Combine(path, keyUtil.ExecutableName)))
      {
        return path;
      }

      if (episodes)
      {
        var iv = new KeyUtilGTAIV();
        string? ivPath = iv.TryFindGameDirectory();
        if (ivPath != null && IsCompleteEdition(ivPath))
        {
          keyUtil = iv;
          return ivPath;
        }
      }

      return null;
    }

    /// <summary>
    /// Red Dead Redemption 2 found without asking: the Rockstar Games Launcher's registry entry, else Steam's
    /// libraries (app 1174180), else the Epic Games Launcher's manifests. The folder must contain RDR2.exe.
    /// </summary>
    public static string? FindRdr2Directory()
    {
      static bool IsRdr2(string? folder) => !string.IsNullOrEmpty(folder) && System.IO.File.Exists(Path.Combine(folder, "RDR2.exe"));

      foreach (var key in new[] { @"SOFTWARE\WOW6432Node\Rockstar Games\Red Dead Redemption 2", @"SOFTWARE\Rockstar Games\Red Dead Redemption 2" })
      {
        try
        {
          using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(key);
          if (k?.GetValue("InstallFolder") is string folder && IsRdr2(folder))
          {
            return folder.TrimEnd('\\');
          }
        }
        catch (System.Exception)
        {
          // Not readable: try the next place.
        }
      }

      try
      {
        using var steam = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
        if (steam?.GetValue("InstallPath") is string steamPath)
        {
          var libraries = new System.Collections.Generic.List<string> { steamPath };
          string vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
          if (System.IO.File.Exists(vdf))
          {
            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(System.IO.File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
            {
              libraries.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
            }
          }
          foreach (var library in libraries)
          {
            string folder = Path.Combine(library, "steamapps", "common", "Red Dead Redemption 2");
            if (IsRdr2(folder))
            {
              return folder;
            }
          }
        }
      }
      catch (System.Exception)
      {
        // No Steam, or its files are not readable.
      }

      try
      {
        string manifests = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.CommonApplicationData),
                                        "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (System.IO.Directory.Exists(manifests))
        {
          foreach (var item in System.IO.Directory.GetFiles(manifests, "*.item"))
          {
            using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(item));
            if (doc.RootElement.TryGetProperty("InstallLocation", out var location) && IsRdr2(location.GetString()))
            {
              return location.GetString();
            }
          }
        }
      }
      catch (System.Exception)
      {
        // No Epic Games Launcher, or a manifest it is still writing.
      }

      return null;
    }

    /// <summary>
    /// Checks a folder the user picked. Returns the key util to use, or null if it isn't the game.
    /// </summary>
    public static KeyUtil? Check(string path, bool episodes)
    {
      KeyUtil keyUtil = episodes ? new KeyUtilEFLC() : new KeyUtilGTAIV();
      if (System.IO.File.Exists(Path.Combine(path, keyUtil.ExecutableName)))
      {
        return keyUtil;
      }
      if (episodes && IsCompleteEdition(path))
      {
        return new KeyUtilGTAIV();
      }
      return null;
    }
  }
}
