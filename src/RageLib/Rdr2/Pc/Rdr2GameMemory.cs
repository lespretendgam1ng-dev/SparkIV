/**********************************************************************\

 RageLib - SparkIV
 One-time RDR2 (PC) key recovery from the running game.

 The per-archive TFIT2 keys of RDR2 are not stored in RDR2.exe in a readable
 form; the game decrypts them into memory while it runs. This reads them, once,
 from the user's own running game via ReadProcessMemory, together with the
 cipher tables, identifies each by its public fingerprint (a hash, never shipped
 key material), and the caller caches the result on the user's disk. Nothing
 here circumvents the game's protection: it reads data the running process has
 already decrypted for itself.

 Author: reditec

 This program is free software: you can redistribute it and/or modify
 it under the terms of the GNU General Public License as published by
 the Free Software Foundation, either version 3 of the License, or
 (at your option) any later version.

\**********************************************************************/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace RageLib.Rdr2.Pc
{
  /// <summary>
  /// Recovers what is needed to read RDR2 archives - the per-archive keys and the cipher tables - from the running
  /// game's memory, once; the result is cached by the caller (<see cref="Rdr2Secrets.SaveCache"/>) and the game is
  /// never needed again (the keys are stable). SparkIV must run as a 64-bit process to read the 64-bit game.
  /// </summary>
  public static class Rdr2GameMemory
  {
    public const string DefaultProcessName = "RDR2";

    /// <summary>
    /// Default on-disk cache location: per-user application data
    /// (%LOCALAPPDATA%\SparkIV\rdr2keys.cache), not next to the executable, so the app stays writable
    /// when installed read-only and the cache is kept out of the program folder. The directory is
    /// created on first write by <see cref="Rdr2Secrets.SaveCache"/>.
    /// </summary>
    public static string DefaultCachePath =>
      Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SparkIV", "rdr2keys.cache");

    private const int PROCESS_QUERY_INFORMATION = 0x0400;
    private const int PROCESS_VM_READ = 0x0010;
    private const int MEM_COMMIT = 0x1000;
    private const uint PAGE_GUARD = 0x100;
    private const uint PAGE_NOACCESS = 0x01;
    // Protection flags that permit reading.
    private const uint READABLE =
      0x02 /*R*/ | 0x04 /*RW*/ | 0x08 /*WC*/ | 0x20 /*XR*/ | 0x40 /*XRW*/ | 0x80 /*XWC*/;

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_BASIC_INFORMATION
    {
      public IntPtr BaseAddress;
      public IntPtr AllocationBase;
      public uint AllocationProtect;
      public IntPtr RegionSize;
      public uint State;
      public uint Protect;
      public uint Type;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualQueryEx(IntPtr h, IntPtr address, out MEMORY_BASIC_INFORMATION mbi, IntPtr length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(IntPtr h, IntPtr address, byte[] buffer, IntPtr size, out IntPtr read);

    /// <summary>Whether the game is running (its process exists).</summary>
    public static bool IsGameRunning(string processName = DefaultProcessName)
    {
      var procs = Process.GetProcessesByName(processName);
      foreach (var p in procs) p.Dispose();
      return procs.Length > 0;
    }

    /// <summary>
    /// The secrets from the local cache, or null if there is none. A cache from before the cipher tables were
    /// cached needs <paramref name="exePath"/> (RDR2.exe) for them.
    /// </summary>
    public static Rdr2Secrets LoadCached(string exePath, string cachePath = null)
    {
      cachePath ??= DefaultCachePath;
      return Rdr2Secrets.LoadCache(cachePath, () => exePath != null && File.Exists(exePath) ? File.ReadAllBytes(exePath) : null);
    }

    /// <summary>
    /// Reads the keys and cipher tables from the running game and returns them. The cipher tables come from the
    /// game's memory, or else from <paramref name="exePath"/>. <paramref name="progress"/> gets the share of the
    /// game's memory searched (0..1). Throws with a message for the user if the game is not running, cannot be
    /// read, or not everything was found.
    /// </summary>
    public static Rdr2Secrets ExtractFromRunningGame(string exePath, Action<string> log = null, Action<double> progress = null,
                                                     CancellationToken cancel = default, string processName = DefaultProcessName)
    {
      log ??= _ => { };
      if (!Environment.Is64BitProcess)
        throw new InvalidOperationException("Reading the 64-bit RDR2 process requires a 64-bit SparkIV build.");

      var procs = Process.GetProcessesByName(processName);
      if (procs.Length == 0)
        throw new InvalidOperationException("Red Dead Redemption 2 is not running. Start the game and load the story mode, then try again.");

      try
      {
        foreach (var proc in procs)
        {
          var watch = Stopwatch.StartNew();
          IntPtr h = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, proc.Id);
          if (h == IntPtr.Zero)
            throw new InvalidOperationException($"Could not read the game's memory (error {Marshal.GetLastWin32Error()}). Try running SparkIV as administrator.");
          try
          {
            var scan = new Rdr2Secrets.Scan();
            var regions = Regions(h, log);
            // Pass 1: the keys everywhere, the cipher tables only in the mapped modules (RDR2.exe's image): the
            // tables are static data of the exe, and a search for them costs several passes per piece of memory.
            ScanRegions(h, regions, scan, image => (keys: true, cipher: image), progress, 0, 1, cancel);
            log($"keys found: {scan.KeysFound} of {scan.KeyTotal}; cipher tables found: {scan.CipherFound} ({watch.ElapsedMilliseconds} ms)");
            if (scan.KeysFound == 0) continue;
            if (scan.KeysFound < scan.KeyTotal)
              throw new InvalidDataException($"Only {scan.KeysFound} of {scan.KeyTotal} keys were found in the game's memory. Wait until the story mode has fully loaded, then try again.");
            if (scan.CipherFound) return scan.Build();

            // The tables were not in the image: RDR2.exe on disk has them, else the rest of the memory (slow).
            if (exePath != null && File.Exists(exePath))
            {
              try { return scan.Build(File.ReadAllBytes(exePath)); }
              catch (InvalidDataException) { log("RDR2.exe has no cipher tables (incomplete download?)"); }
            }
            ScanRegions(h, regions, scan, image => (keys: false, cipher: !image), progress, 0, 1, cancel);
            log($"cipher tables found: {scan.CipherFound} ({watch.ElapsedMilliseconds} ms)");
            if (!scan.CipherFound)
              throw new InvalidDataException("The cipher tables were found neither in the game's memory nor in RDR2.exe.");
            return scan.Build();
          }
          finally
          {
            CloseHandle(h);
          }
        }
      }
      finally
      {
        foreach (var p in procs) p.Dispose();
      }
      throw new InvalidDataException("The game is running, but its keys were not found in its memory. Wait until the story mode has fully loaded, then try again.");
    }

    private const uint MEM_IMAGE = 0x1000000;

    /// <summary>The readable committed regions of the process (start, size, whether a mapped module).</summary>
    private static List<(long start, long size, bool image)> Regions(IntPtr h, Action<string> log)
    {
      long maxRegion = 1L << 31;                   // skip absurdly large single regions (reserved address space)
      var regions = new List<(long, long, bool)>();
      long total = 0, image = 0;
      IntPtr addr = IntPtr.Zero;
      while (VirtualQueryEx(h, addr, out var mbi, (IntPtr)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>()) != IntPtr.Zero)
      {
        long regionSize = (long)mbi.RegionSize;
        if (regionSize <= 0) break;
        long baseAddr = (long)mbi.BaseAddress;
        bool readable = mbi.State == MEM_COMMIT && (mbi.Protect & READABLE) != 0 &&
                        (mbi.Protect & PAGE_GUARD) == 0 && (mbi.Protect & PAGE_NOACCESS) == 0;
        if (readable && regionSize <= maxRegion)
        {
          bool isImage = mbi.Type == MEM_IMAGE;
          regions.Add((baseAddr, regionSize, isImage));
          total += regionSize;
          if (isImage) image += regionSize;
        }
        long next = baseAddr + regionSize;
        if (next <= baseAddr) break;         // overflow / end of address space
        addr = (IntPtr)next;
      }
      log($"readable memory: {total / (1024 * 1024)} MB in {regions.Count} regions, {image / (1024 * 1024)} MB of them modules");
      return regions;
    }

    /// <summary>
    /// Searches the regions in 16 MB pieces on several threads. <paramref name="what"/> says per region (by whether
    /// it is a mapped module) what to look for; a piece is skipped once that is found.
    /// </summary>
    private static void ScanRegions(IntPtr h, List<(long start, long size, bool image)> regions, Rdr2Secrets.Scan scan,
                                    Func<bool, (bool keys, bool cipher)> what, Action<double> progress,
                                    double from, double to, CancellationToken cancel)
    {
      const int ChunkSize = 16 * 1024 * 1024;
      int overlap = scan.MaxSecretLength - 1;      // so a table straddling a piece boundary is still seen

      var pieces = new List<(long address, int length, bool keys, bool cipher)>();
      long total = 0;
      foreach (var (start, size, image) in regions)
      {
        var (keys, cipher) = what(image);
        if (!keys && !cipher) continue;
        for (long pos = 0; pos < size; pos += ChunkSize)
        {
          int length = (int)Math.Min(ChunkSize + overlap, size - pos);
          pieces.Add((start + pos, length, keys, cipher));
          total += Math.Min(ChunkSize, size - pos);
        }
      }

      long done = 0;
      var options = new System.Threading.Tasks.ParallelOptions
      {
        CancellationToken = cancel,
        // Half the cores: the game keeps running meanwhile.
        MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 8),
      };
      System.Threading.Tasks.Parallel.ForEach(pieces, options,
        () => new byte[ChunkSize + overlap],
        (piece, state, buffer) =>
        {
          bool keys = piece.keys && !scan.KeysComplete, cipher = piece.cipher && !scan.CipherFound;
          if (keys || cipher)
          {
            // A partly readable piece (ERROR_PARTIAL_COPY) still counts with what was read.
            ReadProcessMemory(h, (IntPtr)piece.address, buffer, (IntPtr)piece.length, out var read);
            if ((long)read > 0) scan.Add(buffer, (int)read, keys, cipher);
          }
          long now = Interlocked.Add(ref done, Math.Min(ChunkSize, piece.length));
          progress?.Invoke(from + (to - from) * (total == 0 ? 1 : Math.Min(1.0, now / (double)total)));
          return buffer;
        },
        buffer => { });
      progress?.Invoke(to);
    }
  }
}
