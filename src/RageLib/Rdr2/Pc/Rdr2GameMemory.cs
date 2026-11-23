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
          var scan = new Rdr2Secrets.Scan();
          ScanProcess(proc.Id, scan, log, progress, cancel);
          log($"keys found: {scan.KeysFound} of {scan.KeyTotal}; cipher tables found: {scan.CipherFound}");
          if (scan.KeysFound == 0) continue;
          if (scan.KeysFound < scan.KeyTotal)
            throw new InvalidDataException($"Only {scan.KeysFound} of {scan.KeyTotal} keys were found in the game's memory. Wait until the story mode has fully loaded, then try again.");
          byte[] exe = null;
          if (!scan.CipherFound)
          {
            if (exePath == null || !File.Exists(exePath))
              throw new InvalidDataException("The cipher tables were not found in the game's memory, and RDR2.exe is not available.");
            exe = File.ReadAllBytes(exePath);
          }
          return scan.Build(exe);
        }
      }
      finally
      {
        foreach (var p in procs) p.Dispose();
      }
      throw new InvalidDataException("The game is running, but its keys were not found in its memory. Wait until the story mode has fully loaded, then try again.");
    }

    private static void ScanProcess(int pid, Rdr2Secrets.Scan scan, Action<string> log, Action<double> progress, CancellationToken cancel)
    {
      IntPtr h = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, pid);
      if (h == IntPtr.Zero)
        throw new InvalidOperationException($"Could not read the game's memory (error {Marshal.GetLastWin32Error()}). Try running SparkIV as administrator.");

      const int ChunkSize = 16 * 1024 * 1024;      // read big regions in 16 MB pieces
      int overlap = scan.MaxSecretLength - 1;      // so a table straddling a piece boundary is still seen
      long maxRegion = 1L << 31;                   // skip absurdly large single regions (reserved address space)

      try
      {
        // The readable regions first, so the progress can be shown.
        var regions = new List<(long start, long size)>();
        long total = 0;
        IntPtr addr = IntPtr.Zero;
        while (VirtualQueryEx(h, addr, out var mbi, (IntPtr)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>()) != IntPtr.Zero)
        {
          long regionSize = (long)mbi.RegionSize;
          if (regionSize <= 0) break;
          long baseAddr = (long)mbi.BaseAddress;
          bool readable = mbi.State == MEM_COMMIT && (mbi.Protect & READABLE) != 0 &&
                          (mbi.Protect & PAGE_GUARD) == 0 && (mbi.Protect & PAGE_NOACCESS) == 0;
          if (readable && regionSize <= maxRegion) { regions.Add((baseAddr, regionSize)); total += regionSize; }
          long next = baseAddr + regionSize;
          if (next <= baseAddr) break;         // overflow / end of address space
          addr = (IntPtr)next;
        }
        log($"readable memory: {total / (1024 * 1024)} MB in {regions.Count} regions");

        var buffer = new byte[ChunkSize + overlap];
        long done = 0, scanned = 0;
        foreach (var (baseAddr, regionSize) in regions)
        {
          for (long pos = 0; pos < regionSize; pos += ChunkSize)
          {
            cancel.ThrowIfCancellationRequested();
            if (scan.Complete) break;
            int want = (int)Math.Min(ChunkSize + overlap, regionSize - pos);
            // A partly readable piece (ERROR_PARTIAL_COPY) still counts with what was read.
            ReadProcessMemory(h, (IntPtr)(baseAddr + pos), buffer, (IntPtr)want, out var read);
            if ((long)read > 0)
            {
              scan.Add(buffer, (int)read);
              scanned += (long)read;
            }
            progress?.Invoke(total == 0 ? 1 : Math.Min(1.0, (done + pos + ChunkSize) / (double)total));
          }
          done += regionSize;
          if (scan.Complete) break;
        }
        progress?.Invoke(1);
        log($"searched {scanned / (1024 * 1024)} MB");
      }
      finally
      {
        CloseHandle(h);
      }
    }
  }
}
