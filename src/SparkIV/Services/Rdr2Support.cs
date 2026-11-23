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
using System.IO.Compression;
using System.Security.Cryptography;
using RageLib.Rdr2.Pc;

namespace SparkIV.WinUI.Services
{
  /// <summary>What reading an RDR2 install's archives needs, once it is set up.</summary>
  internal sealed class Rdr2Context
  {
    public Rdr2Context(string gameFolder, Rdr2Secrets secrets, Rdr2Names? names)
    {
      GameFolder = gameFolder;
      Secrets = secrets;
      Names = names;
    }

    public string GameFolder { get; }
    public Rdr2Secrets Secrets { get; }
    public Rdr2Names? Names { get; }

    /// <summary>Whether <paramref name="path"/> is in the game's folder.</summary>
    public bool Contains(string path) =>
      Path.GetFullPath(path).StartsWith(Path.GetFullPath(GameFolder).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The scope of an archive on disk for the names database: its path relative to the game folder, without
    /// ".rpf", with '/' (e.g. "x64/dlcpacks/mp009/dlc"); its file name for an archive outside the game folder.
    /// </summary>
    public string ScopeOf(string archivePath)
    {
      string full = Path.GetFullPath(archivePath);
      string root = Path.GetFullPath(GameFolder).TrimEnd('\\') + "\\";
      string relative = full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full[root.Length..] : Path.GetFileName(full);
      if (relative.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase))
      {
        relative = relative[..^4];
      }
      return relative.Replace('\\', '/');
    }
  }

  /// <summary>
  /// Red Dead Redemption 2 (PC): its archives need three things SparkIV does not ship.
  /// <list type="bullet">
  /// <item>The keys and cipher tables: read once from the running game by the setup assistant
  /// (<see cref="Rdr2SetupWindow"/>) and kept in a local cache (<see cref="Rdr2GameMemory.DefaultCachePath"/>).</item>
  /// <item>Oodle, for most entries: the game's own oo2core DLL next to RDR2.exe.</item>
  /// <item>Names: the archives store only hashes; the names come from the strings database SparkIV ships
  /// (reditec/RAGE-StringsDatabase, compiled and compressed), unpacked once into the local app data.</item>
  /// </list>
  /// </summary>
  internal static class Rdr2Support
  {
    private static readonly object Sync = new object();
    private static Rdr2Names? _names;
    private static bool _namesTried;

    /// <summary>The RDR2 install whose archives are being read, or null.</summary>
    public static Rdr2Context? Current { get; private set; }

    public static string ExePath(string gameFolder) => Path.Combine(gameFolder, "RDR2.exe");

    /// <summary>Whether the keys were set up already (the cache exists).</summary>
    public static bool IsSetUp => System.IO.File.Exists(Rdr2GameMemory.DefaultCachePath);

    /// <summary>The keys and cipher from the cache, or null when the setup assistant has not been run yet.</summary>
    public static Rdr2Secrets? LoadSecrets(string gameFolder) => Rdr2GameMemory.LoadCached(ExePath(gameFolder));

    /// <summary>
    /// Makes <paramref name="gameFolder"/> the RDR2 install archives are read from. Returns a warning for the
    /// user when something is missing that only some files need (Oodle), else null.
    /// </summary>
    public static string? Activate(string gameFolder, Rdr2Secrets secrets)
    {
      bool oodle = Rdr2Oodle.Load(gameFolder);
      Current = new Rdr2Context(gameFolder, secrets, Names);
      if (!oodle)
      {
        return "The Oodle library of the game (oo2core_*_win64.dll next to RDR2.exe) is missing or not downloaded completely; most files can't be opened without it.";
      }
      if (Current.Names == null)
      {
        return "The names database is missing; files are shown with their hashes.";
      }
      return null;
    }

    /// <summary>The names database, unpacked from SparkIV's folder on first use (null if it isn't there).</summary>
    public static Rdr2Names? Names
    {
      get
      {
        lock (Sync)
        {
          if (!_namesTried)
          {
            _namesTried = true;
            try
            {
              _names = OpenNames();
            }
            catch (Exception)
            {
              _names = null;
            }
          }
          return _names;
        }
      }
    }

    private static Rdr2Names? OpenNames()
    {
      string source = Path.Combine(AppContext.BaseDirectory, "Data", "RDR2.sivs.br");
      string folder = Path.GetDirectoryName(Rdr2GameMemory.DefaultCachePath)!;
      string target = Path.Combine(folder, "RDR2.sivs");
      string stamp = target + ".source";
      if (!System.IO.File.Exists(source))
      {
        return System.IO.File.Exists(target) ? Rdr2Names.Open(target) : null;
      }

      // Unpacked again when SparkIV brings a different database (an update).
      string id;
      using (var s = System.IO.File.OpenRead(source))
      {
        id = Convert.ToHexString(SHA256.HashData(s));
      }
      bool current = System.IO.File.Exists(target) && System.IO.File.Exists(stamp) && System.IO.File.ReadAllText(stamp) == id;
      if (!current)
      {
        System.IO.Directory.CreateDirectory(folder);
        string temp = target + ".tmp";
        using (var input = new BrotliStream(System.IO.File.OpenRead(source), CompressionMode.Decompress))
        using (var output = System.IO.File.Create(temp))
        {
          input.CopyTo(output);
        }
        try
        {
          System.IO.File.Move(temp, target, true);
          System.IO.File.WriteAllText(stamp, id);
        }
        catch (IOException)
        {
          // Another SparkIV has the old one open: use that until next time.
          System.IO.File.Delete(temp);
        }
      }
      return Rdr2Names.Open(target);
    }
  }
}
