using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace ModuleForge
{
    // The timed half of a kill reward: a few seconds of extra fire rate or
    // damage, refreshed or stacked by the next kill.
    //
    // Weapon stats are not simply "set and forget". `WeaponFactory.Create`
    // rebuilds a WeaponBase from its TemplateData and re-runs every
    // IWeaponModifier whenever the weapon's module cluster changes - so a buff
    // that pokes FireRate directly is silently wiped by the next rebuild, and a
    // buff that tries to restore an old value after a rebuild writes back a
    // number that is no longer the baseline.
    //
    // So the baseline is re-captured whenever it changes underneath us, and the
    // buff is always expressed as baseline x multiplier rather than as a
    // remembered delta. That way a rebuild costs at most one frame of buff, and
    // never corrupts the weapon's real stats.
    public static class ModuleForgeKillBuff
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("ModuleForge.KillBuff");

        private class State
        {
            public WeaponBase weapon;
            public float baseFireRate;
            public float baseDamage;
            public float appliedFireRate;   // what we last wrote
            public float appliedDamage;
            public readonly List<float> fireStacks = new List<float>();
            public readonly List<float> dmgStacks = new List<float>();
            public readonly List<float> expiry = new List<float>();
            public int maxStacks = 1;
        }

        private static readonly List<State> _live = new List<State>();
        private static GameObject _ticker;

        // The on-screen half, tracked SEPARATELY from the per-weapon States.
        //
        // A ship-grid module buffs every weapon the ship is holding, so it owns
        // several States at once - and keying the indicator on those would put
        // one identical line on screen per weapon. The player thinks in terms of
        // "that module is buffing me", so the indicator is keyed on the SOURCE
        // (the effect instance) and carries its own stack clock.
        private class Source
        {
            public Sprite icon;
            public Color color = Color.white;
            public float fire;
            public float dmg;
            public int maxStacks = 1;
            public readonly List<float> expiry = new List<float>();
        }

        private static readonly Dictionary<object, Source> _sources =
            new Dictionary<object, Source>();

        public static void Grant(
            Unit killer,
            WeaponBase scoped,
            float fireRateBonus,
            float damageBonus,
            float duration,
            int maxStacks)
        {
            Grant(killer, scoped, fireRateBonus, damageBonus, duration,
                  maxStacks, null, null, Color.white, false);
        }

        public static void Grant(
            Unit killer,
            WeaponBase scoped,
            float fireRateBonus,
            float damageBonus,
            float duration,
            int maxStacks,
            object indicatorKey,
            Sprite icon,
            Color color,
            bool indicate)
        {
            if (killer == null || duration <= 0f)
                return;

            EnsureTicker();

            if (indicate && indicatorKey != null)
            {
                TrackSource(
                    indicatorKey, icon, color, fireRateBonus, damageBonus,
                    duration, maxStacks);
            }

            // A ship-grid module buffs every weapon the ship is holding; a
            // weapon-grid one buffs only its own.
            if (scoped != null)
            {
                Apply(scoped, fireRateBonus, damageBonus, duration, maxStacks);
                return;
            }

            foreach (Shooter shooter in
                     killer.GetComponentsInChildren<Shooter>(true))
            {
                if (shooter != null && shooter.Weapon != null)
                    Apply(shooter.Weapon, fireRateBonus, damageBonus,
                          duration, maxStacks);
            }
        }

        private static void Apply(
            WeaponBase weapon, float fire, float dmg,
            float duration, int maxStacks)
        {
            State s = Find(weapon);

            if (s == null)
            {
                s = new State { weapon = weapon };
                Rebase(s);
                _live.Add(s);
            }

            s.maxStacks = Mathf.Max(1, maxStacks);

            if (s.expiry.Count >= s.maxStacks)
            {
                // At the cap: refresh the soonest-expiring stack rather than
                // adding another, so "stacks 1" behaves as a plain refresh.
                int soonest = 0;

                for (int i = 1; i < s.expiry.Count; i++)
                    if (s.expiry[i] < s.expiry[soonest])
                        soonest = i;

                s.expiry[soonest] = Time.time + duration;
                s.fireStacks[soonest] = fire;
                s.dmgStacks[soonest] = dmg;
            }
            else
            {
                s.expiry.Add(Time.time + duration);
                s.fireStacks.Add(fire);
                s.dmgStacks.Add(dmg);
            }

            Push(s);
        }

        private static State Find(WeaponBase w)
        {
            for (int i = 0; i < _live.Count; i++)
                if (_live[i].weapon == w)
                    return _live[i];

            return null;
        }

        private static void Rebase(State s)
        {
            s.baseFireRate = s.weapon.FireRate;
            s.baseDamage = s.weapon.Damage.amount;
            s.appliedFireRate = s.baseFireRate;
            s.appliedDamage = s.baseDamage;
        }

        // Writes baseline x (1 + sum of stacks). Re-captures the baseline first
        // if something else has changed the weapon since we last wrote - that is
        // the rebuild case, and treating the new value as the baseline is the
        // only correct reading of it.
        private static void Push(State s)
        {
            if (s.weapon == null)
                return;

            if (!Nearly(s.weapon.FireRate, s.appliedFireRate) ||
                !Nearly(s.weapon.Damage.amount, s.appliedDamage))
            {
                Rebase(s);
            }

            float fire = 0f, dmg = 0f;

            for (int i = 0; i < s.expiry.Count; i++)
            {
                fire += s.fireStacks[i];
                dmg += s.dmgStacks[i];
            }

            s.weapon.FireRate = s.baseFireRate * (1f + fire);

            Damage d = s.weapon.Damage;
            d.amount = s.baseDamage * (1f + dmg);
            s.weapon.Damage = d;

            s.appliedFireRate = s.weapon.FireRate;
            s.appliedDamage = s.weapon.Damage.amount;
        }

        private static bool Nearly(float a, float b)
        {
            return Mathf.Abs(a - b) < 0.0001f;
        }

        private static void TrackSource(
            object key, Sprite icon, Color color,
            float fire, float dmg, float duration, int maxStacks)
        {
            Source src;

            if (!_sources.TryGetValue(key, out src))
            {
                src = new Source();
                _sources[key] = src;
            }

            src.icon = icon;
            src.color = color;
            src.fire = fire;
            src.dmg = dmg;
            src.maxStacks = Mathf.Max(1, maxStacks);

            // Same cap rule the weapon stacks use: at the ceiling, refresh the
            // soonest-expiring one rather than adding another.
            if (src.expiry.Count >= src.maxStacks)
            {
                int soonest = 0;

                for (int i = 1; i < src.expiry.Count; i++)
                    if (src.expiry[i] < src.expiry[soonest])
                        soonest = i;

                src.expiry[soonest] = Time.time + duration;
            }
            else
            {
                src.expiry.Add(Time.time + duration);
            }

            Publish(key, src);
        }

        private static void Publish(object key, Source src)
        {
            // Fire rate wins a tie because it is the one you feel.
            string effect = (src.fire > 0f) ? "FIRE RATE" : "DAMAGE";
            float amount = (src.fire > 0f) ? src.fire : src.dmg;

            if (amount <= 0f)
                return;

            float latest = 0f;

            for (int i = 0; i < src.expiry.Count; i++)
                if (src.expiry[i] > latest)
                    latest = src.expiry[i];

            ModuleForgeBuffHud.Show(
                key, src.icon, src.color, effect, amount,
                src.expiry.Count, latest, false);
        }

        // Expire the on-screen sources. Separate from the weapon-stack sweep
        // below because a source outlives any one weapon's State.
        private static void TickSources()
        {
            if (_sources.Count == 0)
                return;

            List<object> dead = null;

            foreach (var kv in _sources)
            {
                Source src = kv.Value;

                for (int i = src.expiry.Count - 1; i >= 0; i--)
                    if (Time.time >= src.expiry[i])
                        src.expiry.RemoveAt(i);

                if (src.expiry.Count == 0)
                {
                    if (dead == null)
                        dead = new List<object>();

                    dead.Add(kv.Key);
                    continue;
                }

                // Repaint for a seconds-remaining label. Show() only repaints
                // when the composed text actually differs, so this settles at
                // about once a second rather than every frame.
                Publish(kv.Key, src);
            }

            if (dead == null)
                return;

            for (int i = 0; i < dead.Count; i++)
            {
                ModuleForgeBuffHud.Clear(dead[i]);
                _sources.Remove(dead[i]);
            }
        }

        private static void Tick()
        {
            TickSources();

            for (int i = _live.Count - 1; i >= 0; i--)
            {
                State s = _live[i];

                if (s.weapon == null)
                {
                    _live.RemoveAt(i);
                    continue;
                }

                bool changed = false;

                for (int j = s.expiry.Count - 1; j >= 0; j--)
                {
                    if (Time.time < s.expiry[j])
                        continue;

                    s.expiry.RemoveAt(j);
                    s.fireStacks.RemoveAt(j);
                    s.dmgStacks.RemoveAt(j);
                    changed = true;
                }

                if (changed)
                    Push(s);

                if (s.expiry.Count == 0)
                {
                    // Fully expired: hand the weapon back exactly as found.
                    s.weapon.FireRate = s.baseFireRate;

                    Damage d = s.weapon.Damage;
                    d.amount = s.baseDamage;
                    s.weapon.Damage = d;

                    _live.RemoveAt(i);
                }
            }
        }

        private static void EnsureTicker()
        {
            if (_ticker != null)
                return;

            _ticker = new GameObject("ModuleForge Kill Buffs");
            UnityEngine.Object.DontDestroyOnLoad(_ticker);
            _ticker.AddComponent<Ticker>();
        }

        private class Ticker : MonoBehaviour
        {
            private void Update()
            {
                try
                {
                    Tick();
                }
                catch (Exception e)
                {
                    Log.LogError("Kill buff tick failed: " + e);
                    _live.Clear();
                }
            }
        }
    }
}
