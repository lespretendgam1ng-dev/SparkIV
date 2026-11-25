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
using System.Numerics;
using SparkIV.WinUI.Rendering.D3D12;

namespace SparkIV.WinUI.Viewers.GtaIV.Pc
{
  /// <summary>
  /// GTA IV (PC): the map's particle effects of a location that always play (#118: the plumes over et01_ba's toll booths,
  /// the intro ship's plume of #137): each source's effect rule from the particle libraries (ParticleLibrary, the tag
  /// through common/data/effects/entityFx.dat), simulated as GTAIV.exe's ptxSprite update (0x64D3D0, see
  /// docs/GtaIV/Pc/formats/wpfl.md) and lit as its gta_rmptfx_litsprite. The state at a time is a pure function of it:
  /// particles spawn on a fixed grid of 1/30 s, each with its own random numbers, and are stepped from their spawn on
  /// (the effects have played long before the cutscene: every particle alive at the time is there); the states are kept
  /// from frame to frame, going on in time only the new steps are made. Not thread-safe (one drawing thread).
  /// The motion and the size's time are fitted to the game's traced particles (see VelocityShare); not done yet: the
  /// game's wind itself, the emitter domains' shapes and turns (a box round the place), collisions, the sun's shadow on
  /// the particles (their shadow share 1), soft edges against the scene, model particles.
  /// </summary>
  public sealed class CutsceneParticles
  {
    private const double Step = 1.0 / 30;

    // The lighting the game's draws used (GTAIV.3.trace intro s2 f863, draw 34241242, VS c65-c75, c128, PS c66): not the
    // sprite rule's own shader values (its WrapScale 1, AmbientAmount 0.9, gScaleFade 8 are not what the draw got).
    private const float WrapScale = 0.6f, WrapBias = 0.3f, Diffuse = 0.9f, IntensityClamp = 0.5f, AmbientAmount = 0.35f,
                        AmbientShadow = 0.5f, SuperAlpha = 2, ScaleFade = 25, NearPlane = 0.4f, UseShadows = 0.4f;
    // The vertices' HDR multiplier in that draw (v6.y, 1.6; the event's HDRIntensityMultiplier curve says 1.0-1.1). The
    // lighting numbers and this are the same in GTAIV.2.trace et01_ba (by day, draws 224301426-224301543).
    private const float HdrMultiplier = 1.6f;

    // Fitted, not derived (2026-11-22): the motion of amb_tower_plume_sm's particles in both traces (GTAIV.3 intro s2
    // f863, the 2dfx at scale 0.5; GTAIV.2 et01_ba f1394.75, scale 1) is the same, mean offsets per life (0.8-1.0:
    // intro 12.3 / -20.2 / 9.6, et01_ba 12.4 / -21.9 / 12.3): the motion isn't scaled with the effect, and it is carried
    // in one direction in both scenes. With the update's rules as read (0x64D3D0) the rule's own motion drifted along
    // +x; fitted over both: the velocity domain x 0.15, the acceleration x 0.25 and a field of (3, -26, 13) m/s carried
    // at WindInfluenceKF (as the update's wind term). The size: SizeKF at 0.36 of the life (the drawn sizes grow slower
    // than the curve at the life, which GTAIV.exe's vertex builder 0x650430 reads at the life as well: open).
    private const float VelocityShare = 0.15f, AccelerationShare = 0.25f, SizeTime = 0.36f;
    private static readonly Vector3 Wind = new Vector3(3, -26, 13);

    private sealed class Source
    {
      public Vector3 At;
      public Quaternion Turn;
      public float Scale;
      public ParticleEffect Effect = null!;
      public int Seed;
    }

    private readonly List<Source> _sources = new List<Source>();
    private readonly Dictionary<string, byte[]> _textures = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The effects found (tag: effect rule) and those not drawn (why), for the load log.</summary>
    public List<string> Report { get; } = new List<string>();

    public int Count => _sources.Count;

    /// <summary>The textures the effects need, by name, as ModelScene.Raw makes them (for MeshRenderer.PrepareTexture).</summary>
    public IReadOnlyDictionary<string, byte[]> Textures => _textures;

    public CutsceneParticles(CutsceneLocation location, string folder, int hour)
    {
      if (location.Particles.Count == 0)
      {
        return;
      }
      var libraries = Libraries(folder);
      var tags = EntityFx(folder);
      int seed = 0;
      foreach (var p in location.Particles)
      {
        seed++;
        if (!tags.TryGetValue(p.Tag, out var fx))
        {
          Report.Add(p.Tag + ": not in entityFx.dat");
          continue;
        }
        // Only those that play whatever the weather (no rain or wind needed) at this hour (TIME_MIN - TIME_MAX, 0 0: always).
        if (fx.RainThreshold >= 0 || fx.WindThreshold >= 0)
        {
          Report.Add(p.Tag + ": needs rain or wind (not drawn)");
          continue;
        }
        if (fx.HourMin != fx.HourMax && !(fx.HourMin < fx.HourMax ? hour >= fx.HourMin && hour < fx.HourMax : hour >= fx.HourMin || hour < fx.HourMax))
        {
          continue;
        }
        var lib = libraries.FirstOrDefault(l => l.Has(fx.Effect));
        var effect = lib?.Effect(fx.Effect);
        if (lib == null || effect == null || effect.Emitters.Count == 0)
        {
          Report.Add(p.Tag + ": " + fx.Effect + " not found");
          continue;
        }
        // Only sprites with a texture of the library (model particles aren't drawn).
        if (effect.Emitters.Any(e => e.Sprite.Texture == null))
        {
          Report.Add(p.Tag + ": " + fx.Effect + " has a model or untextured rule (not drawn)");
          continue;
        }
        foreach (var e in effect.Emitters)
        {
          if (!_textures.ContainsKey(e.Sprite.Texture!) && lib.Texture(e.Sprite.Texture!) is RageLib.Textures.Texture t)
          {
            _textures[e.Sprite.Texture!] = ModelScene.Raw(t);
          }
        }
        _sources.Add(new Source { At = p.At, Turn = p.Turn, Scale = p.Scale > 0 ? p.Scale : 1, Effect = effect, Seed = seed });
        Report.Add(p.Tag + ": " + fx.Effect + " at " + p.At.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
      }
    }

    /// <summary>
    /// The particles at <paramref name="seconds"/>, a batch per emitter (textures: the prepared numbers by name; those not
    /// prepared are left out), sorted far to near.
    /// </summary>
    public List<ParticleBatch> At(double seconds, Vector3 eye, IReadOnlyDictionary<string, int> textures)
    {
      var batches = new List<ParticleBatch>();
      foreach (var s in _sources)
      {
        for (int e = 0; e < s.Effect.Emitters.Count; e++)
        {
          var emitter = s.Effect.Emitters[e];
          if (!textures.TryGetValue(emitter.Sprite.Texture!, out int slot) || slot == 0)
          {
            continue;
          }
          var particles = Simulate(s, emitter, e, seconds);
          if (particles.Count == 0)
          {
            continue;
          }
          particles.Sort((a, b) => Vector3.DistanceSquared(new Vector3(b.M11, b.M12, b.M13), eye).CompareTo(Vector3.DistanceSquared(new Vector3(a.M11, a.M12, a.M13), eye)));
          batches.Add(new ParticleBatch
          {
            Texture = slot,
            TilesX = emitter.Sprite.TilesX,
            TilesY = emitter.Sprite.TilesY,
            Particles = particles.ToArray(),
            Count = particles.Count,
            WrapScale = WrapScale,
            WrapBias = WrapBias,
            Diffuse = Diffuse,
            IntensityClamp = IntensityClamp,
            Shadow = UseShadows,
            AmbientAmount = AmbientAmount,
            AmbientShadow = AmbientShadow,
            HdrMultiplier = HdrMultiplier,
            SuperAlpha = SuperAlpha,
            NearPlane = NearPlane,
            ScaleFade = ScaleFade,
          });
        }
      }
      return batches;
    }

    // A particle: its state after its last whole step of the grid, and its own random numbers.
    private sealed class Particle
    {
      public long Spawn;
      public long Steps;
      public float Life;
      public Vector3 Position, Velocity, WindTerm;
      public float Theta, FrameTime, T;
      public int Frame;
      public float RAccX, RAccY, RAccZ, RDamp, RPlay, RRot, RSize, RAlpha, RColour, RAnim, RWind;

      public Particle Copy() => (Particle)MemberwiseClone();
    }

    // An emitter's particles kept from frame to frame (the last whole step of the grid they are at): going on in time,
    // only the steps since are made (playing), else they are made afresh from the time (the same state either way).
    private sealed class EmitterState
    {
      public long Last = long.MinValue;
      public readonly List<Particle> Alive = new List<Particle>();
    }

    private readonly Dictionary<(Source, int), EmitterState> _states = new Dictionary<(Source, int), EmitterState>();

    // The emitter's particles alive at the time: spawned on the grid (rate x time scale at the emitter's time, its own
    // Duration looping), each stepped from its spawn by the update's rules, whole steps up to the grid's last before the
    // time (kept), then the part of a step to the time (shown only).
    private List<Matrix4x4> Simulate(Source s, ParticleEmitter e, int emitterIndex, double seconds)
    {
      var result = new List<Matrix4x4>();
      var sprite = e.Sprite;
      float lifeMax = Math.Max(e.SpawnLife.At(0).Y, e.SpawnLife.At(0).X);
      if (lifeMax <= 0)
      {
        return result;
      }
      float duration = e.Duration > 0 ? e.Duration : 1;
      long last = (long)Math.Floor(seconds / Step);
      long span = (long)Math.Ceiling(lifeMax * 1.6 / Step) + 1;
      int cells = Math.Max(1, sprite.TilesX * sprite.TilesY);
      if (!_states.TryGetValue((s, emitterIndex), out var state))
      {
        _states[(s, emitterIndex)] = state = new EmitterState();
      }
      if (state.Last == long.MinValue || last < state.Last || last - state.Last > span)
      {
        state.Alive.Clear();
        state.Last = last - span - 1;
      }
      for (long k = state.Last + 1; k <= last; k++)
      {
        double spawnAt = k * Step;
        float et = (float)(((spawnAt % duration) + duration) % duration / duration);
        var rate = e.SpawnRate.At(et);
        float perStep = (rate.X + rate.Y) * 0.5f * Math.Max(e.TimeScale.At(et).X, 0) * (float)Step;
        int count = (int)(Math.Floor((k + 1) * (double)perStep) - Math.Floor(k * (double)perStep));
        for (int j = 0; j < count; j++)
        {
          if (Spawn(s, e, emitterIndex, k, j, et, cells) is Particle p)
          {
            state.Alive.Add(p);
          }
        }
      }
      // Whole steps up to the grid's last.
      for (int i = state.Alive.Count - 1; i >= 0; i--)
      {
        var p = state.Alive[i];
        bool alive = true;
        while (p.Steps < last - p.Spawn && alive)
        {
          alive = Advance(p, s, sprite, (float)Step, cells);
          p.Steps++;
        }
        if (!alive)
        {
          state.Alive.RemoveAt(i);
        }
      }
      state.Last = last;
      // Shown: the part of a step to the time.
      float part = (float)(seconds - last * Step);
      foreach (var kept in state.Alive)
      {
        var p = part > 1e-6f ? kept.Copy() : kept;
        if (part > 1e-6f && !Advance(p, s, sprite, part, cells))
        {
          continue;
        }
        var size = sprite.Size.At(p.T * SizeTime);
        float half = Lerp(size.X, size.Z, p.RSize) * s.Scale;
        var alpha = sprite.Alpha.At(p.T);
        var c0 = sprite.Color.At(p.T);
        var c1 = sprite.ColorMax.At(p.T);
        var colour = Vector4.Lerp(c0, c1 == Vector4.Zero ? c0 : c1, p.RColour);
        float a = Lerp(alpha.X, alpha.Y, p.RAlpha) * (colour.W > 0 ? colour.W : 1);
        if (half <= 0 || a <= 0)
        {
          continue;
        }
        result.Add(new Matrix4x4(p.Position.X, p.Position.Y, p.Position.Z, half,
                                 colour.X, colour.Y, colour.Z, a,
                                 p.Theta, p.Frame, (p.Frame + 1) % cells, 0,
                                 0, 0, 0, 0));
      }
      return result;
    }

    // A particle spawned at step k (its j-th): its place in the emitter domain (a box of its size round its place), its
    // velocity the velocity domain's place plus its spread x the speed (both turned with the effect), its own randoms.
    private static Particle? Spawn(Source s, ParticleEmitter e, int emitterIndex, long k, int j, float et, int cells)
    {
      var rng = new Rng(s.Seed, emitterIndex, k, j);
      float life = Lerp(e.SpawnLife.At(et).X, e.SpawnLife.At(et).Y, rng.Next());
      if (life <= 0)
      {
        return null;
      }
      var edAt = e.EmitterDomain.Position.At(et);
      var edSize = e.EmitterDomain.Size.At(et);
      var local = new Vector3(edAt.X + (rng.Next() - 0.5f) * 2 * edSize.X, edAt.Y + (rng.Next() - 0.5f) * 2 * edSize.Y, edAt.Z + (rng.Next() - 0.5f) * 2 * edSize.Z);
      var vAt = e.VelocityDomain.Position.At(et);
      var vSize = e.VelocityDomain.Size.At(et);
      var speed = e.Speed.At(et);
      var v0 = new Vector3(vAt.X + (rng.Next() - 0.5f) * vSize.X, vAt.Y + (rng.Next() - 0.5f) * vSize.Y, vAt.Z + (rng.Next() - 0.5f) * vSize.Z) * Lerp(speed.X, speed.Y, rng.Next());
      var p = new Particle
      {
        Spawn = k,
        Life = life,
        Position = s.At + Vector3.Transform(local, s.Turn),
        Velocity = Vector3.Transform(v0, s.Turn) * VelocityShare,
        RWind = rng.Next(),
        RAccX = rng.Next(),
        RAccY = rng.Next(),
        RAccZ = rng.Next(),
        RDamp = rng.Next(),
        RPlay = rng.Next(),
        RRot = rng.Next(),
        RSize = rng.Next(),
        RAlpha = rng.Next(),
        RColour = rng.Next(),
        RAnim = rng.Next(),
      };
      var theta0 = e.Sprite.InitTheta.At(0);
      p.Theta = Lerp(theta0.X, theta0.Y, rng.Next()) * MathF.PI / 180;
      p.Frame = (int)(rng.Next() * cells) % cells;
      return p;
    }

    // A step of the update (GTAIV.exe 0x64D3D0, with the fitted shares; see VelocityShare): false once its life is over.
    private static bool Advance(Particle p, Source s, ParticleSprite sprite, float dt, int cells)
    {
      float t = p.T;
      var play = sprite.PlaybackRate.At(t);
      float playback = play == Vector4.Zero ? 1 : Lerp(play.X, play.Y, p.RPlay);
      var amin = sprite.AccelerationMin.At(t);
      var amax = sprite.AccelerationMax.At(t);
      var acc = new Vector3(Lerp(amin.X, amax.X, p.RAccX), Lerp(amin.Y, amax.Y, p.RAccY), Lerp(amin.Z, amax.Z, p.RAccZ));
      p.Velocity += Vector3.Transform(acc, s.Turn) * AccelerationShare * dt;
      float damp = Lerp(sprite.DampeningMin.At(t).X, sprite.DampeningMax.At(t).X, p.RDamp);
      p.Velocity *= Math.Max(0, 1 - damp * dt);
      // The wind term (the update's: the last one taken off, the new one added).
      var wi = sprite.WindInfluence.At(t);
      var newWind = Wind * Lerp(wi.X, wi.Y, p.RWind);
      p.Velocity += newWind - p.WindTerm;
      p.WindTerm = newWind;
      p.Position += p.Velocity * dt;
      var rot = sprite.RotateVel.At(t);
      p.Theta += Lerp(rot.X, rot.Y, p.RRot) * dt;
      var anim = sprite.TextureAnimRate.At(t);
      p.FrameTime += Lerp(anim.X, anim.Y, p.RAnim) * dt;
      while (p.FrameTime >= 1)
      {
        p.FrameTime -= 1;
        p.Frame = (p.Frame + 1) % cells;
      }
      p.T += playback * dt / p.Life;
      return p.T <= 1;
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    // A particle's own random numbers (SplitMix64 of the source, emitter, spawn step and number).
    private struct Rng
    {
      private ulong _state;

      public Rng(int source, int emitter, long step, int index)
      {
        _state = (ulong)source * 0x9E3779B97F4A7C15UL ^ (ulong)emitter * 0xC2B2AE3D27D4EB4FUL ^ (ulong)step * 0x165667B19E3779F9UL ^ (ulong)index * 0x27D4EB2F165667C5UL;
      }

      public float Next()
      {
        ulong z = _state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        z ^= z >> 31;
        return (z >> 40) / (float)(1UL << 24);
      }
    }

    // The particle libraries: the episode's first (its folder's pc\data\effects), then GTA IV's (the folder above an
    // episode's), each once while SparkIV runs.
    private static readonly Dictionary<string, List<ParticleLibrary>> s_libraries = new Dictionary<string, List<ParticleLibrary>>(StringComparer.OrdinalIgnoreCase);

    private static List<ParticleLibrary> Libraries(string folder)
    {
      lock (s_libraries)
      {
        if (s_libraries.TryGetValue(folder, out var known))
        {
          return known;
        }
        var list = new List<ParticleLibrary>();
        foreach (var root in Roots(folder))
        {
          string dir = Path.Combine(root, "pc", "data", "effects");
          if (!Directory.Exists(dir))
          {
            continue;
          }
          foreach (var file in Directory.GetFiles(dir, "*.wpfl").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
          {
            if (ParticleLibrary.Load(file) is ParticleLibrary lib)
            {
              list.Add(lib);
            }
          }
        }
        s_libraries[folder] = list;
        return list;
      }
    }

    // The folder and, for an episode's, GTA IV's above it.
    private static IEnumerable<string> Roots(string folder)
    {
      yield return folder;
      string? parent = Path.GetDirectoryName(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar));
      if (parent != null && Directory.Exists(Path.Combine(parent, "pc", "data", "effects")))
      {
        yield return parent;
      }
    }

    private sealed record EntityEffect(string Effect, float RainThreshold, float WindThreshold, int HourMin, int HourMax);

    // common/data/effects/entityFx.dat's ambient entries (ENTITYFX_AMBIENT_START ... END): TAG, FX_NAME, TYPE, ROT,
    // ALWAYS, RAIN_THRESH, MAX_WIND_EVO, WIND_THRESH, TEMPERATURE MIN MAX, TIME OF DAY MIN MAX, ...; the episode's first.
    private static Dictionary<string, EntityEffect> EntityFx(string folder)
    {
      var result = new Dictionary<string, EntityEffect>(StringComparer.OrdinalIgnoreCase);
      var inv = System.Globalization.CultureInfo.InvariantCulture;
      foreach (var root in Roots(folder))
      {
        string path = Path.Combine(root, "common", "data", "effects", "entityFx.dat");
        if (!File.Exists(path))
        {
          continue;
        }
        foreach (var raw in File.ReadLines(path))
        {
          var line = raw.Trim();
          if (line.Length == 0 || line.StartsWith('#'))
          {
            continue;
          }
          var f = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
          if (f.Length < 12 || result.ContainsKey(f[0]))
          {
            continue;
          }
          float F(int k) => float.TryParse(f[k], System.Globalization.NumberStyles.Float, inv, out float v) ? v : 0;
          result[f[0]] = new EntityEffect(f[1], F(5), F(7), (int)F(10), (int)F(11));
        }
      }
      return result;
    }
  }
}
