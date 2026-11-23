/**********************************************************************\

 RageLib - SparkIV
 One-time RDR2 (PC) per-archive key recovery from the running game.

 The retail RDR2.exe is protector-packed, so the 165 per-archive TFIT2
 keys are not present in the file; they are decrypted into memory only
 while the game runs. This reads them, once, from the user's own running
 game via ReadProcessMemory, identifies each by its public fingerprint
 (a hash, never shipped key material), and the caller caches the result
 on the user's disk. Nothing here circumvents the game's protection: it
 reads data the running process has already decrypted for itself.

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

namespace RageLib.Rdr2.Pc
{
  /// <summary>
  /// Recovers the RDR2 per-archive keys from the running game's memory (a one-time operation, since the keys are
  /// stable). SparkIV must run as a 64-bit process to read the 64-bit game. The result is meant to be cached by
  /// the caller (see <see cref="Rdr2Secrets.SaveKeyCache"/>); the keys themselves are never written into SparkIV.
  /// </summary>
  public static class Rdr2GameMemory
  {
    public const string DefaultProcessName = "RDR2";

    /// <summary>Default on-disk cache location: next to the running SparkIV executable.</summary>
    public static string DefaultCachePath =>
      Path.Combine(AppContext.BaseDirectory, "rdr2keys.cache");

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

    /// <summary>
    /// Convenience end-to-end provisioning: returns a usable <see cref="Rdr2Secrets"/> built from the cipher tables
    /// and IV in <paramref name="exe"/> (the user's RDR2.exe) plus the per-archive keys, which come from the local
    /// cache when present and otherwise from the running game (and are then cached). Throws a descriptive error if
    /// no cache exists and the game is not running.
    /// </summary>
    public static Rdr2Secrets ProvisionSecrets(byte[] exe, string cachePath = null, Action<string> log = null)
    {
      log ??= _ => { };
      cachePath ??= DefaultCachePath;

      var keys = Rdr2Secrets.LoadKeyCache(cachePath);
      if (keys != null)
      {
        log($"RDR2 keys loaded from cache: {keys.Count} ({cachePath})");
      }
      else
      {
        log("RDR2 key cache not found; scanning the running game...");
        keys = ExtractKeysFromRunningGame(log: log);
        Rdr2Secrets.SaveKeyCache(cachePath, keys);
        log($"RDR2 keys extracted and cached: {keys.Count} ({cachePath})");
      }
      return Rdr2Secrets.Create(exe, keys);
    }

    /// <summary>
    /// Finds the running RDR2 process and recovers every per-archive key present in its memory. Throws if the game
    /// is not running or cannot be opened. If it returns an incomplete set, the needed archive's key may not have
    /// been touched yet by the game &#8212; open the matching content once in-game and retry.
    /// </summary>
    public static Dictionary<int, ulong[]> ExtractKeysFromRunningGame(string processName = DefaultProcessName, Action<string> log = null)
    {
      log ??= _ => { };
      if (!Environment.Is64BitProcess)
        throw new InvalidOperationException("Reading the 64-bit RDR2 process requires a 64-bit SparkIV build.");

      var procs = Process.GetProcessesByName(processName);
      if (procs.Length == 0)
        throw new InvalidOperationException($"Game process '{processName}.exe' is not running. Start RDR2, then run the key extraction.");

      try
      {
        foreach (var proc in procs)
        {
          var keys = ScanProcess(proc.Id, log);
          if (keys.Count > 0) return keys;
        }
      }
      finally
      {
        foreach (var p in procs) p.Dispose();
      }
      throw new InvalidDataException("Found the RDR2 process but no keys in its memory (not yet loaded, or an unexpected build).");
    }

    private static Dictionary<int, ulong[]> ScanProcess(int pid, Action<string> log)
    {
      var result = new Dictionary<int, ulong[]>();
      IntPtr h = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, pid);
      if (h == IntPtr.Zero)
        throw new InvalidOperationException($"Could not open the RDR2 process (error {Marshal.GetLastWin32Error()}). Try running SparkIV as administrator.");

      const long KeyLen = 288;
      const int ChunkSize = 16 * 1024 * 1024;      // read big regions in 16 MB pieces
      const int Overlap = (int)KeyLen - 1;         // so a key straddling a chunk boundary is still seen
      long maxRegion = 1L << 31;                    // skip absurdly large single regions (reserved address space)

      try
      {
        var buffer = new byte[ChunkSize + Overlap];
        IntPtr addr = IntPtr.Zero;
        long scanned = 0;
        while (true)
        {
          if (VirtualQueryEx(h, addr, out var mbi, (IntPtr)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>()) == IntPtr.Zero)
            break;

          long regionSize = (long)mbi.RegionSize;
          if (regionSize <= 0) break;
          long baseAddr = (long)mbi.BaseAddress;

          bool readable =
            mbi.State == MEM_COMMIT &&
            (mbi.Protect & READABLE) != 0 &&
            (mbi.Protect & PAGE_GUARD) == 0 &&
            (mbi.Protect & PAGE_NOACCESS) == 0;

          if (readable && regionSize <= maxRegion)
          {
            long pos = 0;
            while (pos < regionSize)
            {
              int want = (int)Math.Min(ChunkSize + Overlap, regionSize - pos);
              if (ReadProcessMemory(h, (IntPtr)(baseAddr + pos), buffer, (IntPtr)want, out var read) && (long)read >= KeyLen)
              {
                int got = (int)read;
                byte[] slice = got == buffer.Length ? buffer : buffer[..got];
                var found = Rdr2Secrets.FindKeys(slice);
                foreach (var (tag, words) in found)
                  result[tag] = words;        // later regions simply confirm the same key
                scanned += got;
              }
              pos += ChunkSize;               // advance by ChunkSize; the Overlap bytes are re-read next pass
            }
          }

          long next = baseAddr + regionSize;
          if (next <= baseAddr) break;         // overflow / end of address space
          addr = (IntPtr)next;
        }
        log($"scanned {scanned / (1024 * 1024)} MB; keys found: {result.Count}");
      }
      finally
      {
        CloseHandle(h);
      }
      return result;
    }
  }
}
