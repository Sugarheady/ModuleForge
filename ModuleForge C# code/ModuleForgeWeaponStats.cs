using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
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
            BepInEx.Logging.Logger.CreateLogSource("ModuleForge.Stats");

        public const int FireRate = 0;
        public const int Damage = 1;

        private const int Count = 2;

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

                bool ids =
                    TryId(t, "FireRate", FireRate) &&
                    TryId(t, "Damage", Damage);

                _bridgeReady =
                    _set != null && _clearOne != null && _clearAll != null &&
                    ids;

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

        private static bool TryId(Type t, string name, int mine)
        {
            FieldInfo f = AccessTools.Field(t, name);

            if (f == null || f.FieldType != typeof(int))
                return false;

            _theirIds[mine] = (int)f.GetValue(null);
            return true;
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

                Write(weapon, stat, s.baseValue * s.Product());
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
                    Write(weapon, stat, s.baseValue * s.Product());
                }
                else if (s.captured)
                {
                    Write(weapon, stat, s.baseValue);
                    s.captured = false;
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
            switch (stat)
            {
                case FireRate: return w.FireRate;
                case Damage: return w.Damage.amount;
                default: return 0f;
            }
        }

        private static void Write(WeaponBase w, int stat, float value)
        {
            switch (stat)
            {
                case FireRate:
                    // 0 is a division by zero in `Shooter.FireRatePassed`
                    // (`1f / FireRate`), which yields Infinity - a gun that
                    // never fires again.
                    w.FireRate = Mathf.Max(0.01f, value);
                    break;

                case Damage:
                    // `Damage` is a STRUCT, so read-modify-assign.
                    Damage d = w.Damage;
                    d.amount = Mathf.Max(0f, value);
                    w.Damage = d;
                    break;
            }
        }
    }
}
