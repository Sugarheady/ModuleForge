using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using MyBox;          // MinMaxFloat, which is what WeaponBase.Burn is
using UnityEngine;

namespace ModuleForge
{
    // ONE OWNER FOR A LIVE WEAPON'S TUNING, and a bridge to Weapon Forge's copy
    // of the same thing.
    //
    // WHY THIS EXISTS. `ModuleForgeKillBuff` multiplies `WeaponBase.FireRate`
    // and `WeaponBase.Damage.amount` from its own captured baseline, with a
    // heuristic that re-captures when it notices the weapon has moved
    // underneath it. That works against another one-shot writer and fails
    // completely against a PER-FRAME one - and Weapon Forge has two:
    //
    //   * `overheat` with `response: slow` rewrites the fire rate every frame
    //     while any heat remains.
    //   * `spinUp` rewrites it every frame the trigger is held.
    //
    // With either of those on the same weapon, this mod's kill buff was
    // overwritten on the very next frame - so **an onKill fire-rate module did
    // nothing at all on an overheating or spinning-up weapon** - and the
    // re-capture then adopted the other mod's modified number as the weapon's
    // own baseline and wrote it back on expiry. Both mods logged exactly what
    // they intended. There is no error anywhere.
    //
    // EXACTLY ONE OWNER, and it is Weapon Forge when present, for the reason
    // crit is owned there: the per-frame writers live in that mod, so its stack
    // is the one that has to arbitrate. Absent it, this mod runs its own
    // identical table and owns everything - the mods must each work alone.
    //
    // THE STAT IDS ARE READ OUT OF THE OTHER MOD RATHER THAN ASSUMED. Both
    // declare `FireRate` and `Damage` as `const int` and they happen to have
    // the same values today. Hard-coding that agreement across a reflection
    // boundary is exactly the silent fallback this project keeps paying for: a
    // renumber over there would route damage into the fire rate with nothing to
    // read in either log. So the constants are resolved by NAME and the bridge
    // stands down if either is missing.
    public static class ModuleForgeWeaponStats
    {
        private static readonly ManualLogSource Log =
            ModuleForgeLog.Source("ModuleForge.Stats");

        // THE SAME EIGHTEEN WEAPON FORGE CARRIES, AND IN THE SAME ORDER - but
        // the order is a convenience, not a contract. Every id that crosses the
        // bridge is resolved from that mod BY NAME (see TryId), because two
        // tables agreeing today is not the same as them being defined to agree.
        //
        // Widened from two on 2026-09-18 for `windup`. Until then this table
        // held FireRate and Damage because the kill buff was its only customer;
        // a ramp that pays seventeen different rewards needs the rest, and the
        // alternative - letting the effect write those fields directly - is the
        // exact thing this class exists to prevent.
        public const int FireRate = 0;
        public const int Damage = 1;
        public const int AngleVariance = 2;
        public const int Spread = 3;
        public const int Pellets = 4;
        public const int Cost = 5;
        public const int Range = 6;
        public const int Speed = 7;
        public const int BurstSize = 8;
        public const int BurstDelay = 9;
        public const int Burn = 10;
        public const int Push = 11;
        public const int Knockback = 12;
        public const int Size = 13;
        public const int ExplosionRadius = 14;
        public const int ExplosionDamage = 15;
        public const int Warmup = 16;
        public const int Lifetime = 17;

        private const int Count = 18;

        private static readonly string[] Names =
        {
            "fire rate", "damage", "angle variance", "spread", "pellets",
            "cost", "range", "projectile speed", "burst size", "burst delay",
            "burn", "push force", "knockback", "projectile size",
            "explosion radius", "explosion damage", "warmup", "lifetime",
        };

        public static string NameOf(int stat)
        {
            return (stat >= 0 && stat < Count) ? Names[stat] : "?";
        }

        // ------------------------------------------------------------------
        // The bridge
        // ------------------------------------------------------------------

        private static bool _init;
        private static bool _weaponForgePresent;
        private static bool _bridgeReady;
        private static bool _announced;

        private static MethodInfo _set;      // (WeaponBase, int, string, float)
        private static MethodInfo _clearOne; // (WeaponBase, int, string)
        private static MethodInfo _clearAll; // (WeaponBase, string)
        private static MethodInfo _reset;

        private static readonly int[] _theirIds = new int[Count];

        private static void EnsureInit()
        {
            if (_init)
                return;

            _init = true;

            try
            {
                Type t = AccessTools.TypeByName("WeaponForge.ForgeWeaponStats");

                if (t == null)
                    return;

                _weaponForgePresent = true;

                _set = AccessTools.Method(
                    t, "Set",
                    new Type[]
                    {
                        typeof(WeaponBase), typeof(int), typeof(string),
                        typeof(float)
                    });

                _clearOne = AccessTools.Method(
                    t, "Clear",
                    new Type[] { typeof(WeaponBase), typeof(int),
                                 typeof(string) });

                _clearAll = AccessTools.Method(
                    t, "Clear",
                    new Type[] { typeof(WeaponBase), typeof(string) });

                _reset = AccessTools.Method(t, "Reset", new Type[0]);

                // ★ THE TWO CORE IDS GATE THE BRIDGE; THE OTHER SIXTEEN DO NOT,
                // and that asymmetry is deliberate rather than laziness.
                //
                // If a missing id took the whole bridge down, an older Weapon
                // Forge would push this mod onto its LOCAL table - and then
                // BOTH mods would be writing `WeaponBase.FireRate` from two
                // separately captured bases, which is the ratchet this class was
                // written to kill. Losing the bridge is strictly worse than
                // losing a stat.
                //
                // So a stat whose id cannot be resolved is simply NOT PUBLISHED
                // (see Set), with its name said once. Refusing is safe; writing
                // it into our own table while that mod owns the field is not.
                bool core =
                    TryId(t, "FireRate", FireRate) &&
                    TryId(t, "Damage", Damage);

                TryId(t, "AngleVariance", AngleVariance);
                TryId(t, "Spread", Spread);
                TryId(t, "Pellets", Pellets);
                TryId(t, "Cost", Cost);
                TryId(t, "Range", Range);
                TryId(t, "Speed", Speed);
                TryId(t, "BurstSize", BurstSize);
                TryId(t, "BurstDelay", BurstDelay);
                TryId(t, "Burn", Burn);
                TryId(t, "Push", Push);
                TryId(t, "Knockback", Knockback);
                TryId(t, "Size", Size);
                TryId(t, "ExplosionRadius", ExplosionRadius);
                TryId(t, "ExplosionDamage", ExplosionDamage);
                TryId(t, "Warmup", Warmup);
                TryId(t, "Lifetime", Lifetime);

                _bridgeReady =
                    _set != null && _clearOne != null && _clearAll != null &&
                    core;

                if (!_bridgeReady)
                {
                    Log.LogWarning(
                        "Weapon Forge is installed but its weapon-stat table " +
                        "could not be reached, so this mod will keep its own " +
                        "fire-rate and damage buffs in a separate table. If " +
                        "you use an onKill module on a weapon that also has " +
                        "\"overheat\" or \"spinUp\", the two will fight over " +
                        "the same field and the module's half may not show.");
                }
            }
            catch (Exception e)
            {
                Log.LogError("Resolving the weapon-stat bridge failed: " + e);
                _bridgeReady = false;
            }
        }

        // -1 means "that mod has no such stat", which `Set` treats as a refusal
        // rather than as id 0 - the difference between publishing nothing and
        // routing, say, the explosion radius into the FIRE RATE.
        private static bool TryId(Type t, string name, int mine)
        {
            _theirIds[mine] = -1;

            FieldInfo f = AccessTools.Field(t, name);

            if (f == null || f.FieldType != typeof(int))
                return false;

            _theirIds[mine] = (int)f.GetValue(null);
            return true;
        }

        // Said once per stat, because a reward that silently does nothing is
        // indistinguishable from a reward the author mis-typed.
        private static readonly HashSet<int> _saidUnbridged = new HashSet<int>();

        private static void SayUnbridged(int stat)
        {
            if (!_saidUnbridged.Add(stat))
                return;

            Log.LogWarning(
                "Weapon Forge is installed and owns a weapon's live tuning, " +
                "but its stat table has no \"" + NameOf(stat) + "\" - so a " +
                "module asking for that reward does nothing while that mod is " +
                "present. Everything else still works. This means the two mods " +
                "are different versions; update them together. (Writing it " +
                "here instead would mean two mods each remembering the same " +
                "field's original value, which is worse than losing it.)");
        }

        private static bool Bridged
        {
            get
            {
                EnsureInit();
                return _weaponForgePresent && _bridgeReady;
            }
        }

        private static void Announce()
        {
            if (_announced || !_weaponForgePresent)
                return;

            _announced = true;

            Log.LogInfo(
                "Weapon Forge is installed, so IT owns a weapon's live fire " +
                "rate and damage. This mod's timed buffs are published into " +
                "its table and multiply with whatever that mod's own " +
                "\"overheat\" or \"spinUp\" is doing, rather than the two " +
                "overwriting each other.");
        }

        // ------------------------------------------------------------------
        // Publishing
        // ------------------------------------------------------------------

        public static void Set(
            WeaponBase weapon, int stat, string key, float multiplier)
        {
            if (weapon == null || string.IsNullOrEmpty(key) ||
                stat < 0 || stat >= Count)
            {
                return;
            }

            if (Bridged)
            {
                Announce();

                // Their table cannot name this stat - refuse rather than fall
                // through to ours. See the note on TryId.
                if (_theirIds[stat] < 0)
                {
                    SayUnbridged(stat);
                    return;
                }

                try
                {
                    _set.Invoke(
                        null,
                        new object[]
                        {
                            weapon, _theirIds[stat], key, multiplier
                        });

                    return;
                }
                catch (Exception e)
                {
                    Log.LogError("Publishing across the bridge failed: " + e);
                }
            }

            Local(weapon, stat, key, multiplier);
        }

        public static void Clear(WeaponBase weapon, int stat, string key)
        {
            if (weapon == null || string.IsNullOrEmpty(key) ||
                stat < 0 || stat >= Count)
            {
                return;
            }

            if (Bridged)
            {
                if (_theirIds[stat] < 0)
                    return;

                try
                {
                    _clearOne.Invoke(
                        null,
                        new object[] { weapon, _theirIds[stat], key });

                    return;
                }
                catch (Exception e)
                {
                    Log.LogError("Withdrawing across the bridge failed: " + e);
                }
            }

            LocalClear(weapon, stat, key);
        }

        public static void Clear(WeaponBase weapon, string key)
        {
            if (weapon == null || string.IsNullOrEmpty(key))
                return;

            if (Bridged)
            {
                try
                {
                    _clearAll.Invoke(null, new object[] { weapon, key });
                    return;
                }
                catch (Exception e)
                {
                    Log.LogError("Withdrawing across the bridge failed: " + e);
                }
            }

            for (int i = 0; i < Count; i++)
                LocalClear(weapon, i, key);
        }

        public static void Reset()
        {
            _local.Clear();

            // The other mod resets its own table from its own run hook, so this
            // is belt and braces rather than required - and it is guarded
            // because a failure here must never take a run entry down.
            if (Bridged && _reset != null)
            {
                try
                {
                    _reset.Invoke(null, new object[0]);
                }
                catch (Exception)
                {
                }
            }
        }

        // ------------------------------------------------------------------
        // The local table - identical semantics, used when Weapon Forge is
        // absent. See WeaponForge.ForgeWeaponStats for the full account of why
        // it is shaped this way.
        // ------------------------------------------------------------------

        private class Stat
        {
            public bool captured;
            public float baseValue;

            // Two stats are not one number. `Burn` is a MinMaxFloat and both
            // ends scale together; `ExplosionDamage` is a LIST, one entry per
            // damage type. Captured alongside the scalar rather than in a
            // parallel structure, so a stat can never be half-captured.
            public float baseValue2;
            public float[] baseList;

            // The last product written, so a ramp publishing the same number
            // sixty times a second writes once. Cheap for the plain floats and
            // load-bearing for the explosion pair, which rebuilds a struct and
            // a list every time it really writes.
            public float lastProduct = float.NaN;

            public readonly List<string> keys = new List<string>();
            public readonly List<float> mults = new List<float>();

            public float Product()
            {
                float p = 1f;

                for (int i = 0; i < mults.Count; i++)
                    p *= mults[i];

                return p;
            }
        }

        private class Entry
        {
            public readonly Stat[] stats = new Stat[Count];

            public Stat Of(int stat)
            {
                if (stats[stat] == null)
                    stats[stat] = new Stat();

                return stats[stat];
            }

            public bool Idle()
            {
                for (int i = 0; i < Count; i++)
                {
                    if (stats[i] != null && stats[i].keys.Count > 0)
                        return false;
                }

                return true;
            }
        }

        private static readonly Dictionary<WeaponBase, Entry> _local =
            new Dictionary<WeaponBase, Entry>();

        private static void Local(
            WeaponBase weapon, int stat, string key, float multiplier)
        {
            try
            {
                Entry e;

                if (!_local.TryGetValue(weapon, out e))
                {
                    e = new Entry();
                    _local[weapon] = e;
                }

                Stat s = e.Of(stat);

                if (!s.captured)
                {
                    s.captured = true;
                    s.baseValue = Read(weapon, stat);
                    s.baseValue2 = Read2(weapon, stat);
                    s.baseList = ReadList(weapon, stat);
                }

                int at = s.keys.IndexOf(key);

                if (at < 0)
                {
                    s.keys.Add(key);
                    s.mults.Add(multiplier);
                }
                else
                {
                    s.mults[at] = multiplier;
                }

                Write(weapon, stat, s, s.Product());
            }
            catch (Exception e)
            {
                Log.LogError("Publishing a weapon stat failed: " + e);
            }
        }

        private static void LocalClear(WeaponBase weapon, int stat, string key)
        {
            try
            {
                Entry e;

                if (!_local.TryGetValue(weapon, out e))
                    return;

                Stat s = e.stats[stat];

                if (s == null)
                    return;

                int at = s.keys.IndexOf(key);

                if (at < 0)
                    return;

                s.keys.RemoveAt(at);
                s.mults.RemoveAt(at);

                if (s.keys.Count > 0)
                {
                    Write(weapon, stat, s, s.Product());
                }
                else if (s.captured)
                {
                    Write(weapon, stat, s, 1f);
                    s.captured = false;
                    s.baseList = null;
                    s.lastProduct = float.NaN;
                }

                if (e.Idle())
                    _local.Remove(weapon);
            }
            catch (Exception ex)
            {
                Log.LogError("Withdrawing a weapon stat failed: " + ex);
            }
        }

        private static float Read(WeaponBase w, int stat)
        {
            var pw = w as ProjectileWeapon;

            switch (stat)
            {
                case FireRate: return w.FireRate;
                case Damage: return w.Damage.amount;
                case AngleVariance: return w.AngleVariance;
                case Spread: return w.Spread;
                case Pellets: return w.ProjectileCount;
                case Cost: return w.Cost;

                case Range:
                {
                    var r = w as IHasRangeProperty;
                    return (r != null) ? r.Range : 0f;
                }

                case Speed:
                {
                    var s = w as IHasSpeedProperty;
                    return (s != null) ? s.Speed : 0f;
                }

                case BurstSize: return w.BurstSize;
                case BurstDelay: return w.BurstDelay;

                // The MAX end. The Min comes back from Read2.
                case Burn: return w.Burn.Max;

                case Push: return w.PushForce;
                case Knockback: return w.KnockbackForce;

                case Size:
                {
                    if (pw != null)
                        return pw.ProjectileRadius;

                    var hw = w as HitscanWeapon;
                    return (hw != null) ? hw.RayWidth : 0f;
                }

                case ExplosionRadius: return w.Explosion.radius;

                // Not a single number - the LIST is the base. This is here only
                // so the idle check has something to report.
                case ExplosionDamage:
                {
                    var d = w.Explosion.damages;
                    return (d != null && d.Count > 0) ? d[0].amount : 0f;
                }

                case Warmup: return w.WarmupTime;

                case Lifetime:
                    return (pw != null) ? pw.LifetimeData.time : 0f;

                default: return 0f;
            }
        }

        private static float Read2(WeaponBase w, int stat)
        {
            switch (stat)
            {
                case Burn: return w.Burn.Min;
                default: return 0f;
            }
        }

        private static float[] ReadList(WeaponBase w, int stat)
        {
            if (stat != ExplosionDamage)
                return null;

            List<Damage> d = w.Explosion.damages;

            if (d == null || d.Count == 0)
                return null;

            var baseline = new float[d.Count];

            for (int i = 0; i < d.Count; i++)
                baseline[i] = d[i].amount;

            return baseline;
        }

        private static void Write(WeaponBase w, int stat, Stat s, float k)
        {
            // Nothing has moved since the last write.
            if (!float.IsNaN(s.lastProduct) &&
                Mathf.Abs(k - s.lastProduct) < 0.000001f)
            {
                return;
            }

            s.lastProduct = k;

            float value = s.baseValue * k;
            var pw = w as ProjectileWeapon;

            switch (stat)
            {
                case FireRate:
                    // 0 is a division by zero in `Shooter.FireRatePassed`
                    // (`1f / FireRate`), which yields Infinity - a gun that
                    // never fires again.
                    w.FireRate = Mathf.Max(0.01f, value);
                    break;

                case Damage:
                {
                    // `Damage` is a STRUCT, so read-modify-assign.
                    Damage d = w.Damage;
                    d.amount = Mathf.Max(0f, value);
                    w.Damage = d;
                    break;
                }

                case AngleVariance:
                    // `GetDirections` calls `Random.Range(-v, v)`, which is
                    // happy with 0 and nonsense with a negative.
                    w.AngleVariance = Mathf.Max(0f, value);
                    break;

                case Spread:
                    w.Spread = Mathf.Clamp(value, 0f, 360f);
                    break;

                case Pellets:
                    // ROUNDED, because the fan geometry is built from it:
                    // `step = Spread / (ProjectileCount - 1)`, so a count of 2.5
                    // spaces the pellets as though there were 1.5 gaps.
                    //
                    // A WEAPON AT projectileCount 0 FIRES NOTHING BY DESIGN and
                    // must stay that way.
                    if (s.baseValue > 0f)
                        w.ProjectileCount = Mathf.Max(1f, Mathf.Round(value));
                    break;

                case Cost:
                    w.Cost = Mathf.Max(0f, value);
                    break;

                case Range:
                {
                    var r = w as IHasRangeProperty;

                    if (r != null)
                        r.Range = Mathf.Max(0f, value);

                    break;
                }

                case Speed:
                {
                    var sp = w as IHasSpeedProperty;

                    if (sp != null)
                        sp.Speed = Mathf.Max(0f, value);

                    break;
                }

                case BurstSize:
                    // An int, and a burst of 0 is a weapon that fires nothing:
                    // `WeaponBase.Fire` is `for (b = 0; b < BurstSize; b++)`.
                    w.BurstSize = Mathf.Max(1, Mathf.RoundToInt(value));
                    break;

                case BurstDelay:
                    w.BurstDelay = Mathf.Max(0f, value);
                    break;

                case Burn:
                    // Both ends by the same product.
                    w.Burn = new MinMaxFloat(
                        Mathf.Max(0f, s.baseValue2 * k), Mathf.Max(0f, value));
                    break;

                case Push:
                    w.PushForce = value;
                    break;

                case Knockback:
                    w.KnockbackForce = value;
                    break;

                case Size:
                {
                    if (pw != null)
                    {
                        pw.ProjectileRadius = Mathf.Max(0.001f, value);
                        break;
                    }

                    var hw = w as HitscanWeapon;

                    if (hw != null)
                        hw.RayWidth = Mathf.Max(0.001f, value);

                    break;
                }

                case ExplosionRadius:
                {
                    Explosion ex = w.Explosion;
                    ex.radius = Mathf.Max(0f, value);
                    w.Explosion = ex;
                    break;
                }

                case ExplosionDamage:
                {
                    if (s.baseList == null)
                        break;

                    // DUPLICATE, do not edit in place. The weapon owns its own
                    // list, so editing it would not reach the asset - but
                    // `FireSingle` hands the struct to the projectile and the
                    // struct carries the list BY REFERENCE, so editing in place
                    // would re-scale an explosion already in the air.
                    Explosion ex = w.Explosion.Duplicate();

                    if (ex.damages != null &&
                        ex.damages.Count == s.baseList.Length)
                    {
                        for (int i = 0; i < ex.damages.Count; i++)
                        {
                            ex.damages[i] = new Damage(
                                Mathf.Max(0f, s.baseList[i] * k),
                                ex.damages[i].damageType);
                        }
                    }

                    w.Explosion = ex;
                    break;
                }

                case Warmup:
                    // 0 is legitimate and useful: it removes the game's own
                    // minigun gate for the length of a ramp.
                    w.WarmupTime = Mathf.Max(0f, value);
                    break;

                case Lifetime:
                {
                    if (pw == null)
                        break;

                    ProjectileLifetimeData lt = pw.LifetimeData;
                    lt.time = Mathf.Max(0.01f, value);
                    pw.LifetimeData = lt;
                    break;
                }
            }
        }
    }
}
