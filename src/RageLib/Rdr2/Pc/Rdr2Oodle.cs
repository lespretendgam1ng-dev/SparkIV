/**********************************************************************\

 RageLib - SparkIV
 Oodle decompression for RDR2 (PC) archive entries, through the game's own DLL.

 Author: reditec

 This program is free software: you can redistribute it and/or modify
 it under the terms of the GNU General Public License as published by
 the Free Software Foundation, either version 3 of the License, or
 (at your option) any later version.

\**********************************************************************/

using System;
using System.IO;
using System.Runtime.InteropServices;

namespace RageLib.Rdr2.Pc
{
  /// <summary>
  /// Most RDR2 (PC) archive entries are Oodle-compressed (RPF8 compressor 2). Oodle is proprietary, so SparkIV does
  /// not ship it: the game installs its own copy next to RDR2.exe (<c>oo2core_5_win64.dll</c>), and this loads that
  /// one. <see cref="Load"/> is called with the game folder before an archive is read.
  /// </summary>
  public static class Rdr2Oodle
  {
    // int OodleLZ_Decompress(const void* comp, intptr compLen, void* raw, intptr rawLen, int fuzzSafe, int checkCRC,
    //   int verbosity, void* decBufBase, intptr decBufSize, void* callback, void* callbackData, void* decoderMemory,
    //   intptr decoderMemorySize, int threadPhase) - returns the number of bytes decoded, 0 on failure.
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate long DecompressFn(IntPtr comp, long compLen, IntPtr raw, long rawLen, int fuzzSafe, int checkCrc,
      int verbosity, IntPtr decBufBase, long decBufSize, IntPtr callback, IntPtr callbackData, IntPtr decoderMemory,
      long decoderMemorySize, int threadPhase);

    private static readonly object Sync = new object();
    private static DecompressFn _decompress;
    private static string _path;

    /// <summary>The Oodle DLL in use, or null if none is loaded.</summary>
    public static string LoadedFrom => _path;

    public static bool IsLoaded => _decompress != null;

    /// <summary>
    /// Loads the game's Oodle DLL from <paramref name="gameFolder"/> (the folder with RDR2.exe). Returns false if
    /// there is none. Loading it again (another folder) is skipped: one copy serves all archives.
    /// </summary>
    public static bool Load(string gameFolder)
    {
      lock (Sync)
      {
        if (_decompress != null) return true;
        if (string.IsNullOrEmpty(gameFolder) || !System.IO.Directory.Exists(gameFolder)) return false;
        var candidates = System.IO.Directory.GetFiles(gameFolder, "oo2core_*_win64.dll");
        if (candidates.Length == 0) return false;
        Array.Sort(candidates, StringComparer.OrdinalIgnoreCase);
        var dll = candidates[^1];
        if (!NativeLibrary.TryLoad(dll, out var handle)) return false;
        if (!NativeLibrary.TryGetExport(handle, "OodleLZ_Decompress", out var fn))
        {
          NativeLibrary.Free(handle);
          return false;
        }
        _decompress = Marshal.GetDelegateForFunctionPointer<DecompressFn>(fn);
        _path = dll;
        return true;
      }
    }

    /// <summary>Decompresses <paramref name="input"/> to exactly <paramref name="size"/> bytes.</summary>
    public static byte[] Decompress(byte[] input, int size)
    {
      var fn = _decompress ?? throw new InvalidOperationException(
        "This RDR2 file is Oodle-compressed. SparkIV uses the Oodle library of the game (oo2core_*_win64.dll next to RDR2.exe), which was not found.");
      var output = new byte[size];
      long got;
      var src = GCHandle.Alloc(input, GCHandleType.Pinned);
      var dst = GCHandle.Alloc(output, GCHandleType.Pinned);
      try
      {
        // fuzzSafe 0, as the tools that read RDR2 with this DLL call it; threadPhase 3 = unthreaded.
        got = fn(src.AddrOfPinnedObject(), input.Length, dst.AddrOfPinnedObject(), size, 0, 0, 0, IntPtr.Zero, 0,
                 IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, 3);
      }
      finally
      {
        src.Free();
        dst.Free();
      }
      if (got != size) throw new InvalidDataException($"Oodle decompression failed ({got} of {size} bytes).");
      return output;
    }
  }
}
