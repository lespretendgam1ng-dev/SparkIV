/**********************************************************************\

 RageLib - SparkIV
 An RDR2 (PC) RPF8 archive as a browsable file system.

 Author: reditec

 This program is free software: you can redistribute it and/or modify
 it under the terms of the GNU General Public License as published by
 the Free Software Foundation, either version 3 of the License, or
 (at your option) any later version.

\**********************************************************************/

using System;
using System.Collections.Generic;
using RageLib.FileSystem.Common;
using Directory = RageLib.FileSystem.Common.Directory;
using File = RageLib.FileSystem.Common.File;

namespace RageLib.Rdr2.Pc
{
  /// <summary>
  /// Shows an RDR2 (PC) RPF8 archive as folders and files. The archive itself is flat and stores only the hash of
  /// each entry's path; <see cref="Rdr2Names"/> turns the hashes back into paths, and the folders are made from
  /// them. Entries whose path is not known are listed in the top folder as <c>&lt;HASH&gt;.&lt;ext&gt;</c>.
  /// Read-only. Specific to RDR2 on PC.
  /// </summary>
  public sealed class Rpf8FileSystem : FileSystem.Common.FileSystem
  {
    private readonly Rdr2Secrets _secrets;
    private readonly Rdr2Names _names;
    private Rpf8Archive _archive;
    private readonly Dictionary<File, int> _entryOf = new();
    private readonly Dictionary<File, string> _pathOf = new();

    /// <param name="secrets">Keys and cipher of the game.</param>
    /// <param name="names">The names database, or null to show hashes only.</param>
    /// <param name="scope">
    /// The archive's path relative to the game folder, without ".rpf" and with '/' (for an archive inside an
    /// archive: continued by its path inside); it picks the right name for the few hashes two names share.
    /// </param>
    public Rpf8FileSystem(Rdr2Secrets secrets, Rdr2Names names, string scope)
    {
      _secrets = secrets;
      _names = names;
      Scope = scope;
    }

    public string Scope { get; }

    /// <summary>Number of entries whose name was found in the names database.</summary>
    public int NamedEntries { get; private set; }

    public int Entries => _archive?.Entries.Count ?? 0;

    public override void Open(string filename)
    {
      _archive = Rpf8Archive.Open(filename, _secrets);
      BuildFS();
    }

    /// <summary>Whether <paramref name="file"/> is an archive this one can open in place (no copy).</summary>
    public bool CanOpenInPlace(File file)
      => _entryOf.TryGetValue(file, out int index) && _archive.CanOpenInPlace(index);

    /// <summary>Opens the archive <paramref name="file"/> inside this one, reading it in place.</summary>
    public Rpf8FileSystem OpenNested(File file)
    {
      var nested = new Rpf8FileSystem(_secrets, _names, ChildScope(file));
      nested._archive = _archive.OpenNested(_entryOf[file]);
      nested.BuildFS();
      return nested;
    }

    /// <summary>The scope of an archive inside this one (see the constructor).</summary>
    public string ChildScope(File file)
    {
      string path = _pathOf.TryGetValue(file, out var p) ? p : _entryOf.TryGetValue(file, out int i) ? $"{_archive.Entries[i].Hash:X8}" : file.Name;
      if (path.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase)) path = path[..^4];
      return string.IsNullOrEmpty(Scope) ? path : Scope + "/" + path;
    }

    public override void Save() => throw new NotSupportedException("RDR2 archives are read-only in SparkIV.");

    public override void Rebuild() => throw new NotSupportedException("RDR2 archives are read-only in SparkIV.");

    public override void Close()
    {
      _archive?.Dispose();
      _archive = null;
    }

    public override bool SupportsRebuild => false;

    public override bool HasDirectoryStructure => true;

    private void BuildFS()
    {
      RootDirectory = new Directory { Name = "" };
      NamedEntries = 0;
      var folders = new Dictionary<string, Directory>(StringComparer.OrdinalIgnoreCase);

      for (int i = 0; i < _archive.Entries.Count; i++)
      {
        var entry = _archive.Entries[i];
        if (entry.Directory) continue;

        string path = _names?.Lookup(Rdr2Names.Kind.ArchiveItems, entry.Hash, Scope);
        Directory parent = RootDirectory;
        string name;
        if (string.IsNullOrEmpty(path))
        {
          name = entry.Name;
        }
        else
        {
          NamedEntries++;
          path = path.Replace('\\', '/').Trim('/');
          int slash = path.LastIndexOf('/');
          if (slash >= 0) parent = Folder(path[..slash], folders);
          name = path[(slash + 1)..];
          // Most names have no extension; the entry's type gives it. Names that have one keep it (".meta" of a
          // "bin" entry).
          if (name.IndexOf('.') < 0 && entry.Extension.Length > 0) name += "." + entry.Extension;
        }

        if (parent.FindByName(name) != null)
        {
          // Two entries with the same name (a hash collision, or a name the database got wrong): keep both.
          int dot = name.LastIndexOf('.');
          name = dot > 0 ? $"{name[..dot]}~{entry.Hash:X8}{name[dot..]}" : $"{name}~{entry.Hash:X8}";
        }

        int index = i;
        var file = new File(() => _archive.Extract(index))
        {
          Name = name,
          Size = (int)Math.Min(entry.Size, int.MaxValue),
          CompressedSize = (int)Math.Min(entry.DiskSize, int.MaxValue),
          IsCompressed = entry.Compressor != 0,
          ParentDirectory = parent,
        };
        parent.AddObject(file);
        _entryOf[file] = index;
        if (!string.IsNullOrEmpty(path)) _pathOf[file] = path;
      }
    }

    private Directory Folder(string path, Dictionary<string, Directory> folders)
    {
      if (folders.TryGetValue(path, out var dir)) return dir;
      int slash = path.LastIndexOf('/');
      var parent = slash >= 0 ? Folder(path[..slash], folders) : RootDirectory;
      string name = path[(slash + 1)..];
      if (parent.FindByName(name) is Directory existing)
      {
        folders[path] = existing;
        return existing;
      }
      if (parent.FindByName(name) != null) name += "~folder"; // a file of the same name: very unlikely
      dir = new Directory { Name = name, ParentDirectory = parent };
      parent.AddObject(dir);
      folders[path] = dir;
      return dir;
    }
  }
}
