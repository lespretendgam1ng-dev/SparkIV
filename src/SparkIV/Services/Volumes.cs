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
using RageLib.FileSystem;
using RageLib.Rdr2.Pc;
using SparkIV.WinUI.Model;
using FileSystem = RageLib.FileSystem.Common.FileSystem;

namespace SparkIV.WinUI.Services
{
  /// <summary>
  /// The open file systems. Opening an archive twice returns the volume opened the first time.
  /// </summary>
  internal sealed class Volumes : IDisposable
  {
    private readonly List<Volume> _open = new List<Volume>();
    private readonly List<string> _tempFiles = new List<string>();

    public Volume OpenFolder(string path)
    {
      var fs = new RealFileSystem();
      fs.Open(path);
      return Add(new Volume(fs, new DirectoryInfo(path).Name, null, null));
    }

    public Volume OpenArchive(string path)
    {
      var fs = CreateArchive(path, Rdr2ScopeOf(path));
      fs.Open(path);
      return Add(new Volume(fs, System.IO.Path.GetFileName(path), null, null, path));
    }

    private static string Rdr2ScopeOf(string path) =>
      Rdr2Support.Current?.ScopeOf(path) ?? System.IO.Path.GetFileNameWithoutExtension(path);

    /// <summary>Opens an .rpf or .img file that lies in an open volume.</summary>
    public Volume OpenNested(Location host, RageLib.FileSystem.Common.File file)
    {
      foreach (var v in _open)
      {
        if (ReferenceEquals(v.HostFile, file))
        {
          return v;
        }
      }

      // RDR2: an archive inside an RDR2 archive is read in place (the game stores them uncompressed).
      if (host.Volume.FileSystem is Rpf8FileSystem rpf8 && rpf8.CanOpenInPlace(file))
      {
        return Add(new Volume(rpf8.OpenNested(file), file.Name, host, file, null));
      }

      string path;
      string? archivePath = null;
      string scope;
      if (host.Volume.FileSystem is RealFileSystem real)
      {
        // FullName starts with the name of the root folder, so it is relative to the folder above it.
        string parent = new DirectoryInfo(real.RealDirectory).Parent?.FullName ?? real.RealDirectory;
        path = System.IO.Path.Combine(parent, file.FullName);
        archivePath = path;
        scope = Rdr2ScopeOf(path);
      }
      else
      {
        // An archive inside an archive: the readers need a file.
        path = NewTempFile(System.IO.Path.GetExtension(file.Name));
        System.IO.File.WriteAllBytes(path, file.GetData());
        scope = host.Volume.FileSystem is Rpf8FileSystem outer ? outer.ChildScope(file) : System.IO.Path.GetFileNameWithoutExtension(file.Name);
      }

      var fs = CreateArchive(path, scope);
      fs.Open(path);
      return Add(new Volume(fs, file.Name, host, file, archivePath));
    }

    /// <summary>A path for a temporary file, removed when the volumes are closed.</summary>
    public string NewTempFile(string extension)
    {
      string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SparkIV-" + Guid.NewGuid().ToString("N") + extension);
      lock (_tempFiles)
      {
        _tempFiles.Add(path);
      }
      return path;
    }

    public static bool IsArchiveName(string name)
    {
      string ext = System.IO.Path.GetExtension(name);
      return ext.Equals(".rpf", StringComparison.OrdinalIgnoreCase) || ext.Equals(".img", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The reader for an archive file. GTA IV and RDR2 both name their archives .rpf, so an .rpf is told apart by
    /// its first bytes: RPF8 is RDR2's (<paramref name="rdr2Scope"/>: its scope for the names database).
    /// </summary>
    private static FileSystem CreateArchive(string path, string rdr2Scope)
    {
      if (System.IO.Path.GetExtension(path).Equals(".img", StringComparison.OrdinalIgnoreCase))
      {
        return new IMGFileSystem();
      }
      // An archive in the RDR2 folder that is not RPF8 is one the launcher has not downloaded yet (zeros): the RDR2
      // reader says so, where the GTA IV one could only fail.
      if (Rpf8Archive.IsRpf8File(path) || Rdr2Support.Current?.Contains(path) == true)
      {
        var rdr2 = Rdr2Support.Current ?? throw new InvalidOperationException(
          "This is a Red Dead Redemption 2 archive. Open Red Dead Redemption 2 first (its keys are set up there).");
        return new Rpf8FileSystem(rdr2.Secrets, rdr2.Names, rdr2Scope);
      }
      return new RPFFileSystem();
    }

    /// <summary>Whether <paramref name="volume"/> is in an archive SparkIV can't change (RDR2's).</summary>
    public static bool IsReadOnly(Volume volume)
    {
      for (Volume? v = volume; v != null; v = v.Host?.Volume)
      {
        if (v.FileSystem is Rpf8FileSystem)
        {
          return true;
        }
      }
      return false;
    }

    private Volume Add(Volume volume)
    {
      _open.Add(volume);
      return volume;
    }

    public void CloseAll()
    {
      foreach (var v in _open)
      {
        try
        {
          v.FileSystem.Close();
        }
        catch (Exception)
        {
          // Closing is best effort; the process is going away or a new game is opened.
        }
      }
      _open.Clear();

      foreach (var f in _tempFiles)
      {
        try
        {
          System.IO.File.Delete(f);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
      }
      _tempFiles.Clear();
    }

    public void Dispose() => CloseAll();
  }
}
