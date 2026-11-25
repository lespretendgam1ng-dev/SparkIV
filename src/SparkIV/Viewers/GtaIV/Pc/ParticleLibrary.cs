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
using System.Numerics;
using System.Text;

namespace SparkIV.WinUI.Viewers.GtaIV.Pc
{
  /// <summary>
  /// GTA IV (PC): a curve of a particle rule (rmPtfxKeyframe): piecewise linear over 0-1 (a particle's life, an
  /// emitter's time), a vec4 a point; a particle takes its own random point between a min and a max pair.
  /// </summary>
  public sealed class ParticleCurve
  {
    public static readonly ParticleCurve Zero = new ParticleCurve(new[] { 0f }, new[] { Vector4.Zero });

    private readonly float[] _times;
    private readonly Vector4[] _values;

    public ParticleCurve(float[] times, Vector4[] values)
    {
      _times = times;
      _values = values;
    }

    public Vector4 At(float t)
    {
      if (_values.Length == 0)
      {
        return Vector4.Zero;
      }
      if (t <= _times[0])
      {
        return _values[0];
      }
      for (int i = 1; i < _times.Length; i++)
      {
        if (t <= _times[i])
        {
          float span = _times[i] - _times[i - 1];
          float f = span > 0 ? (t - _times[i - 1]) / span : 0;
          return Vector4.Lerp(_values[i - 1], _values[i], f);
        }
      }
      return _values[^1];
    }
  }

  /// <summary>GTA IV (PC): a domain of an emit rule (ptxDomain): its place, turn (degrees), size over the emitter's time.</summary>
  public sealed class ParticleDomain
  {
    public int Kind;
    public ParticleCurve Position = ParticleCurve.Zero;
    public ParticleCurve Direction = ParticleCurve.Zero;
    public ParticleCurve Size = ParticleCurve.Zero;
    public ParticleCurve InnerSize = ParticleCurve.Zero;
  }

  /// <summary>GTA IV (PC): a sprite rule (ptxSprite): its curves over a particle's life, texture and atlas.</summary>
  public sealed class ParticleSprite
  {
    public string Name = "";
    public ParticleCurve Color = ParticleCurve.Zero;
    public ParticleCurve ColorMax = ParticleCurve.Zero;
    public ParticleCurve AccelerationMin = ParticleCurve.Zero;
    public ParticleCurve AccelerationMax = ParticleCurve.Zero;
    public ParticleCurve DampeningMin = ParticleCurve.Zero;
    public ParticleCurve DampeningMax = ParticleCurve.Zero;
    public ParticleCurve PlaybackRate = ParticleCurve.Zero;
    public ParticleCurve Alpha = ParticleCurve.Zero;
    public ParticleCurve WindInfluence = ParticleCurve.Zero;
    public ParticleCurve Size = ParticleCurve.Zero;
    public ParticleCurve InitTheta = ParticleCurve.Zero;
    public ParticleCurve RotateVel = ParticleCurve.Zero;
    public ParticleCurve TextureAnimRate = ParticleCurve.Zero;
    public int TilesX = 1;
    public int TilesY = 1;
    public string Shader = "";
    public string? Texture;
  }

  /// <summary>GTA IV (PC): an emit rule (ptxEmitRuleStd) with the sprite rule its event names.</summary>
  public sealed class ParticleEmitter
  {
    public string Name = "";
    public ParticleCurve SpawnRate = ParticleCurve.Zero;
    public ParticleCurve TimeScale = ParticleCurve.Zero;
    public ParticleCurve SpawnLife = ParticleCurve.Zero;
    public ParticleCurve Speed = ParticleCurve.Zero;
    public ParticleDomain EmitterDomain = new ParticleDomain();
    public ParticleDomain VelocityDomain = new ParticleDomain();
    public float Duration;
    public ParticleSprite Sprite = new ParticleSprite();
  }

  /// <summary>GTA IV (PC): an effect rule (ptxEffectRule): its emitters (the time line's emitter events).</summary>
  public sealed class ParticleEffect
  {
    public string Name = "";
    public List<ParticleEmitter> Emitters = new List<ParticleEmitter>();
  }

  /// <summary>
  /// GTA IV (PC): a particle library (pc/data/effects/*.wpfl, a ptxFxList resource; docs/GtaIV/Pc/formats/wpfl.md):
  /// its effect rules by name and its textures. Only what SparkIV draws is read: sprite rules' curves, emit rules'
  /// rates, lives and domains (#118).
  /// </summary>
  public sealed class ParticleLibrary
  {
    private readonly byte[] _sys;
    private readonly byte[] _gfx;
    private readonly Dictionary<string, int> _effects = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ParticleEffect> _parsed = new Dictionary<string, ParticleEffect>(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, RageLib.Textures.Texture>? _textures;

    public string Path { get; }

    private ParticleLibrary(string path, byte[] sys, byte[] gfx)
    {
      Path = path;
      _sys = sys;
      _gfx = gfx;
      // The effect rules' dictionary (the list's +0x18): each rule's name at +0x08.
      int dict = Ptr(0x18);
      if (dict > 0)
      {
        foreach (int rule in Dictionary(dict))
        {
          if (Str(Ptr(rule + 8)) is string name)
          {
            _effects[name] = rule;
          }
        }
      }
    }

    /// <summary>The library read (null if it isn't there or isn't one).</summary>
    public static ParticleLibrary? Load(string path)
    {
      if (!File.Exists(path))
      {
        return null;
      }
      try
      {
        using var rf = new RageLib.Common.Resources.ResourceFile();
        using (var fs = File.OpenRead(path))
        {
          rf.Read(fs);
        }
        return new ParticleLibrary(path, rf.SystemMemData, rf.GraphicsMemData ?? Array.Empty<byte>());
      }
      catch (Exception)
      {
        return null;
      }
    }

    public IEnumerable<string> EffectNames => _effects.Keys;

    public bool Has(string effect) => _effects.ContainsKey(effect);

    /// <summary>The effect rule (null if not in this library).</summary>
    public ParticleEffect? Effect(string name)
    {
      if (_parsed.TryGetValue(name, out var done))
      {
        return done;
      }
      if (!_effects.TryGetValue(name, out int rule))
      {
        return null;
      }
      var effect = new ParticleEffect { Name = name };
      // The time line (+0xF0): its events (+0x14 pointer, +0x18 count). An emitter event names its emit rule (+0x48) and
      // its particle rule (+0x4C).
      int line = rule + 0xF0;
      int events = Ptr(line + 0x14);
      int count = U16(line + 0x18);
      for (int i = 0; i < count && events > 0; i++)
      {
        int ev = Ptr(events + i * 4);
        if (ev <= 0)
        {
          continue;
        }
        int emit = Ptr(ev + 0x48), sprite = Ptr(ev + 0x4C);
        if (emit <= 0 || sprite <= 0)
        {
          continue;
        }
        var emitter = ReadEmitter(emit);
        emitter.Sprite = ReadSprite(sprite);
        effect.Emitters.Add(emitter);
      }
      _parsed[name] = effect;
      return effect;
    }

    /// <summary>A texture of the library's dictionary by name (null if not there).</summary>
    public RageLib.Textures.Texture? Texture(string name)
    {
      if (_textures == null)
      {
        _textures = new Dictionary<string, RageLib.Textures.Texture>(StringComparer.OrdinalIgnoreCase);
        try
        {
          var sys = new MemoryStream(_sys) { Position = Ptr(0) };
          var file = new RageLib.Textures.TextureFile();
          file.Open(sys, new MemoryStream(_gfx));
          foreach (var t in file)
          {
            _textures[t.Name] = t;
          }
        }
        catch (Exception)
        {
          // No textures: the effects aren't drawn.
        }
      }
      return _textures.TryGetValue(name, out var texture) ? texture : null;
    }

    // An emit rule (ptxEmitRuleStd): +0x14 the emitter domain, +0x1C the velocity domain; ten curves from +0x24, 0x28
    // apart (SpawnRate, SpawnDist, TimeScale, SpawnLife, Speed, SizeOT, AccelerationOT, DampeningOT, MatrixWeightOT,
    // InheritVelOT); its name at +0x1E0, Duration at +0x1E4.
    private ParticleEmitter ReadEmitter(int at)
    {
      return new ParticleEmitter
      {
        Name = Str(Ptr(at + 0x1E0)) ?? "",
        EmitterDomain = ReadDomain(Ptr(at + 0x14)),
        VelocityDomain = ReadDomain(Ptr(at + 0x1C)),
        SpawnRate = Curve(at + 0x24),
        TimeScale = Curve(at + 0x24 + 2 * 0x28),
        SpawnLife = Curve(at + 0x24 + 3 * 0x28),
        Speed = Curve(at + 0x24 + 4 * 0x28),
        Duration = F32(at + 0x1E4),
      };
    }

    // A domain (ptxDomain): its kind at +0x08, then Position, Direction, Size and InnerSize from +0x10.
    private ParticleDomain ReadDomain(int at)
    {
      if (at <= 0)
      {
        return new ParticleDomain();
      }
      return new ParticleDomain
      {
        Kind = (int)U32(at + 8),
        Position = Curve(at + 0x10),
        Direction = Curve(at + 0x10 + 0x28),
        Size = Curve(at + 0x10 + 2 * 0x28),
        InnerSize = Curve(at + 0x10 + 3 * 0x28),
      };
    }

    // A sprite rule (ptxSprite): its curves one after another from +0x170 (0x34 bytes each, the id at +0x28: see
    // wpfl.md), then the atlas's tiles x and y, then the shader (its name, technique, variables: the texture by name).
    private ParticleSprite ReadSprite(int rule)
    {
      var s = new ParticleSprite { Name = Str(Ptr(rule + 0x120)) ?? "" };
      int at = rule + 0x170;
      while (at + 0x34 <= _sys.Length && U32(at) == U32(rule + 0x170))
      {
        var curve = Curve(at);
        switch ((int)U32(at + 0x28))
        {
          case 0: s.Color = curve; break;
          case 1: s.ColorMax = curve; break;
          case 4: s.Size = curve; break;
          case 6: s.InitTheta = curve; break;
          case 8: s.RotateVel = curve; break;
          case 9: s.AccelerationMin = curve; break;
          case 10: s.AccelerationMax = curve; break;
          case 11: s.DampeningMin = curve; break;
          case 12: s.DampeningMax = curve; break;
          case 16: s.TextureAnimRate = curve; break;
          case 17: s.PlaybackRate = curve; break;
          case 18: s.WindInfluence = curve; break;
          case 19: s.Alpha = curve; break;
        }
        at += 0x34;
      }
      s.TilesX = Math.Max(1, (int)U32(at));
      s.TilesY = Math.Max(1, (int)U32(at + 4));
      int shader = at + 8;
      s.Shader = Str(Ptr(shader + 4)) ?? "";
      int vars = Ptr(shader + 0x14);
      int varCount = U16(shader + 0x18);
      for (int i = 0; i < varCount && vars > 0; i++)
      {
        int v = Ptr(vars + i * 4);
        if (v > 0 && Str(Ptr(v + 8)) is string name && name.StartsWith("DiffuseTex", StringComparison.OrdinalIgnoreCase))
        {
          s.Texture = Str(Ptr(v + 0x24)) ?? s.Texture;
        }
      }
      return s;
    }

    // A curve (rmPtfxKeyframe): +0x18 its entries, +0x1C their count; an entry 0x30 bytes (time, value at +0x10).
    private ParticleCurve Curve(int at)
    {
      int entries = Ptr(at + 0x18);
      int count = U16(at + 0x1C);
      if (entries <= 0 || count == 0)
      {
        return ParticleCurve.Zero;
      }
      var times = new float[count];
      var values = new Vector4[count];
      for (int i = 0; i < count; i++)
      {
        int e = entries + i * 0x30;
        times[i] = F32(e);
        values[i] = new Vector4(F32(e + 0x10), F32(e + 0x14), F32(e + 0x18), F32(e + 0x1C));
      }
      return new ParticleCurve(times, values);
    }

    // A pgDictionary: +0x14 count, +0x18 entries.
    private IEnumerable<int> Dictionary(int at)
    {
      int count = U16(at + 0x14);
      int entries = Ptr(at + 0x18);
      for (int i = 0; i < count && entries > 0; i++)
      {
        int e = Ptr(entries + i * 4);
        if (e > 0)
        {
          yield return e;
        }
      }
    }

    private uint U32(int at) => at >= 0 && at + 4 <= _sys.Length ? BitConverter.ToUInt32(_sys, at) : 0;
    private int U16(int at) => at >= 0 && at + 2 <= _sys.Length ? BitConverter.ToUInt16(_sys, at) : 0;
    private float F32(int at) => at >= 0 && at + 4 <= _sys.Length ? BitConverter.ToSingle(_sys, at) : 0;

    // A pointer into the system part (0x5xxxxxxx), else -1.
    private int Ptr(int at)
    {
      uint v = U32(at);
      return v >> 28 == 5 ? (int)(v & 0x0FFFFFFF) : -1;
    }

    private string? Str(int at)
    {
      if (at < 0 || at >= _sys.Length)
      {
        return null;
      }
      int end = at;
      while (end < _sys.Length && _sys[end] != 0 && end - at < 128)
      {
        if (_sys[end] < 32 || _sys[end] > 126)
        {
          return null;
        }
        end++;
      }
      return end > at ? Encoding.ASCII.GetString(_sys, at, end - at) : null;
    }
  }
}
