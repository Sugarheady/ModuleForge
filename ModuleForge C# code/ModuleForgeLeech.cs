using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace ModuleForge
{
    // Leech: your hits heal you, contributed by a grid module.
    //
    // A SECOND COPY of a mechanic Weapon Forge also has, for the reason he
    // approved for crit: a leech module has to work with that mod uninstalled,
    // and a damage-path patch is not something two mods can share. So there are
    // two engines and EXACTLY ONE OWNER at a time - Weapon Forge, which holds
    // the per-weapon `leech` blocks from the weapon files. See
    // ModuleForgeLeechCompat.
    //
    // THE FINDING THIS RESTS ON, and it is not deducible from the field names:
    // `DamagableResource.Damage(float)` refuses damage outright, three silent
    // ways, before it ever touches a tank -
    //
    //     if (IsDamageBlocked()) return;                    // damageBlockers
    //     if (amount > 0 && Unit.HasTank(resource) &&        // WRONG ELEMENT
    //         Time.time - lastDamageTime >= iFrameDuration)  // i-frames
    //
    // - and the middle one is how the damage-type table lets a Crawler resist
    // nearly everything. So this measures the victim's own tank before and after
    // rather than reading the damage number, or it would pay out for hits that
    // did nothing at all.
    //
    // WHICH WEAPON is the same free lookup the crit modules use:
    // `ModuleForgeKills.Credit`, live for all three damage routes and null for a
    // burn tick or a cell collision.
    public static class ModuleForgeLeech
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("ModuleForge.Leech");

        public enum HealOn
        {
            // On the absorb orb reaching your ship. His default: it reads as
            // absorption rather than as a number changing.
            Arrival,

            // The instant the hit lands. Predictable and never lost.
            Hit
        }

        // How a leech BEHAVES and how it LOOKS. Separate from the numbers
        // because the numbers add up across modules and these cannot.
        public class Options
        {
            public HealOn healOn = HealOn.Arrival;

            public bool orb = true;
            public string orbSprite = "area_zero_particle_tuff";
            public float orbFps = 14f;
            public float orbScale = 1f;
            public float orbSpeed = 22f;
            public float orbArc = 0.7f;
            public float orbSpin;
            public float orbLifetime = 2.5f;
            public bool orbHasColor;
            public Color orbColor = Color.white;
            public int maxOrbs = 14;

            public string sfxGuid = "";

            [NonSerialized] public Sprite[] orbFrames;

            // `healOn: Arrival` with no orb to arrive would heal NEVER.
            public HealOn EffectiveHealOn
            {
                get
                {
                    return (orb && orbFrames != null && orbFrames.Length > 0)
                        ? healOn
                        : HealOn.Hit;
                }
            }

            public bool IsPlain
            {
                get
                {
                    return healOn == HealOn.Arrival && orb &&
                           orbSprite == "area_zero_particle_tuff" &&
                           orbFps == 14f && orbScale == 1f &&
                           orbSpeed == 22f && orbArc == 0.7f &&
                           orbSpin == 0f && orbLifetime == 2.5f &&
                           !orbHasColor && maxOrbs == 14 &&
                           string.IsNullOrEmpty(sfxGuid);
                }
            }

            // Compared by VALUE. Every Module CLONES its effects, so two copies
            // of the SAME module carry two distinct Options objects - a
            // reference comparison would report them as two modules disagreeing
            // and log a clash that is not one.
            public bool SameAs(Options o)
            {
                return o != null &&
                       healOn == o.healOn && orb == o.orb &&
                       orbSprite == o.orbSprite && orbFps == o.orbFps &&
                       orbScale == o.orbScale && orbSpeed == o.orbSpeed &&
                       orbArc == o.orbArc && orbSpin == o.orbSpin &&
                       orbLifetime == o.orbLifetime &&
                       orbHasColor == o.orbHasColor && orbColor == o.orbColor &&
                       maxOrbs == o.maxOrbs &&
                       (sfxGuid ?? "") == (o.sfxGuid ?? "");
            }
        }

        private static readonly Options _plain = new Options();

        public static Options Plain { get { return _plain; } }

        // ---- the registry ---------------------------------------------------

        private class Contribution
        {
            public float fraction;
            public float flat;
            public float maxPerSecond;
            public float perEnemyDelay;
            public Resource resource;
            public Options options;
        }

        // Weapon -> contributor -> its numbers. KEYED, because an effect
        // re-pushes on every stat recalculation (that is how it answers to a
        // BoosterCore) and an ADD-style API would drift a weapon upward forever.
        private static readonly
            Dictionary<WeaponBase, Dictionary<object, Contribution>> _byWeapon =
                new Dictionary<WeaponBase, Dictionary<object, Contribution>>();

        // A module in the SHIP grid never gets `IWeaponModifier.Modify`, so it
        // has no weapon to key on - and "all your guns drain" is what it should
        // mean.
        private static readonly Dictionary<object, Contribution> _global =
            new Dictionary<object, Contribution>();

        public static bool Any { get; private set; }

        // `weapon` may be NULL, meaning every weapon. Withdrawn from wherever it
        // was first: a module can move grids, and a weapon is a fresh
        // WeaponBase instance whenever its cluster is rebuilt.
        public static void Set(
            WeaponBase weapon, object key, float fraction, float flat,
            float maxPerSecond, float perEnemyDelay, Resource resource,
            Options options)
        {
            if (key == null)
                return;

            Clear(key);

            Dictionary<object, Contribution> byKey;

            if (weapon == null)
            {
                byKey = _global;
            }
            else if (!_byWeapon.TryGetValue(weapon, out byKey))
            {
                byKey = new Dictionary<object, Contribution>();
                _byWeapon[weapon] = byKey;
            }

            byKey[key] = new Contribution
            {
                fraction = fraction,
                flat = flat,
                maxPerSecond = maxPerSecond,
                perEnemyDelay = perEnemyDelay,
                resource = resource,
                options = options ?? _plain
            };

            Any = true;
        }

        public static void Clear(object key)
        {
            if (key == null)
                return;

            _global.Remove(key);

            List<WeaponBase> emptied = null;

            foreach (var pair in _byWeapon)
            {
                if (!pair.Value.Remove(key) || pair.Value.Count > 0)
                    continue;

                if (emptied == null)
                    emptied = new List<WeaponBase>();

                emptied.Add(pair.Key);
            }

            if (emptied != null)
            {
                for (int i = 0; i < emptied.Count; i++)
                    _byWeapon.Remove(emptied[i]);
            }

            if (_global.Count == 0 && _byWeapon.Count == 0)
                Any = false;
        }

        // ---- resolving one weapon -------------------------------------------

        public static bool Resolve(
            WeaponBase weapon, out float fraction, out float flat,
            out float cap, out float perEnemyDelay, out Resource resource,
            out Options options)
        {
            fraction = 0f;
            flat = 0f;
            cap = 0f;
            perEnemyDelay = 0f;
            resource = null;
            options = _plain;

            bool found = false;

            foreach (Contribution c in _global.Values)
            {
                Fold(c, ref fraction, ref flat, ref cap, ref perEnemyDelay,
                     ref resource);
                found = true;
            }

            Dictionary<object, Contribution> byKey;

            if (weapon != null && _byWeapon.TryGetValue(weapon, out byKey))
            {
                foreach (Contribution c in byKey.Values)
                {
                    Fold(c, ref fraction, ref flat, ref cap,
                         ref perEnemyDelay, ref resource);
                    found = true;
                }
            }

            if (!found)
                return false;

            options = ResolveLook(weapon);
            return fraction > 0f || flat > 0f;
        }

        private static void Fold(
            Contribution c, ref float fraction, ref float flat,
            ref float cap, ref float perEnemyDelay, ref Resource resource)
        {
            fraction += c.fraction;
            flat += c.flat;

            // THE TIGHTEST CAP WINS, treating 0 as "no cap of my own". A cap is
            // a safety limit, so the strict direction is the right one: a module
            // that ships one always protects the player. The converse would let
            // a generous module quietly undo another module's limit.
            cap = Tighter(cap, c.maxPerSecond);

            // Same direction, same reason: the LONGEST wait wins, because a
            // per-enemy delay is a limit too.
            perEnemyDelay = Mathf.Max(perEnemyDelay, c.perEnemyDelay);

            // First named pool wins. Two modules healing different pools is not
            // expressible in one payout, and picking the first is at least
            // stable; the card says which.
            if (resource == null)
                resource = c.resource;
        }

        public static float Tighter(float a, float b)
        {
            if (a <= 0f) return b;
            if (b <= 0f) return a;
            return Mathf.Min(a, b);
        }

        // Numbers add; a look cannot. First contributor that asked for anything
        // wins, weapon-wired ahead of ship-grid, and a disagreement is said out
        // loud once rather than one module's colour silently vanishing.
        private static bool _saidLookClash;

        private static Options ResolveLook(WeaponBase weapon)
        {
            Options chosen = null;
            bool clash = false;

            Dictionary<object, Contribution> byKey;

            if (weapon != null && _byWeapon.TryGetValue(weapon, out byKey))
                Consider(byKey, ref chosen, ref clash);

            Consider(_global, ref chosen, ref clash);

            if (clash && !_saidLookClash)
            {
                _saidLookClash = true;
                Log.LogInfo(
                    "Two leech modules ask for a different LOOK (when the heal " +
                    "lands, the orb art, its colour or sound). Shares and " +
                    "flat amounts add up, but there can only be one look, so " +
                    "the first wins - a module wired to the weapon ahead of one " +
                    "on the ship grid. Said once per run.");
            }

            return chosen ?? _plain;
        }

        private static void Consider(
            Dictionary<object, Contribution> byKey,
            ref Options chosen, ref bool clash)
        {
            foreach (Contribution c in byKey.Values)
            {
                if (c.options == null || c.options.IsPlain)
                    continue;

                if (chosen == null)
                    chosen = c.options;
                else if (!chosen.SameAs(c.options))
                    clash = true;
            }
        }

        // ---- the per-second budget and the per-enemy delay ------------------
        //
        // PER WEAPON, matching what Weapon Forge does with a module-granted
        // leech (it clones a config per weapon). So a ship-grid module with a
        // cap of 5 allows 5 a second on each gun rather than 5 shared - worth
        // knowing, and the alternative would make two weapons interfere in a way
        // nothing on screen explains.
        private class State
        {
            public float windowStartedAt;
            public float healedThisSecond;
            public Dictionary<Unit, float> lastFromEnemy;
        }

        private static readonly Dictionary<WeaponBase, State> _state =
            new Dictionary<WeaponBase, State>();

        private static State StateFor(WeaponBase weapon)
        {
            State s;

            if (_state.TryGetValue(weapon, out s))
                return s;

            if (_state.Count > 64)
                _state.Clear();

            s = new State();
            _state[weapon] = s;
            return s;
        }

        // Partial on purpose: a hit that wants 3 with 2 left in the budget heals
        // 2. An all-or-nothing cap makes a weapon feel randomly dead.
        public static float Take(WeaponBase weapon, float wanted, float cap)
        {
            if (wanted <= 0f)
                return 0f;

            if (cap <= 0f || weapon == null)
                return wanted;

            State s = StateFor(weapon);

            if (Time.time - s.windowStartedAt >= 1f)
            {
                s.windowStartedAt = Time.time;
                s.healedThisSecond = 0f;
            }

            float left = cap - s.healedThisSecond;

            if (left <= 0f)
                return 0f;

            float given = Mathf.Min(wanted, left);
            s.healedThisSecond += given;
            return given;
        }

        public static bool EnemyReady(
            WeaponBase weapon, Unit victim, float delay)
        {
            if (delay <= 0f || victim == null || weapon == null)
                return true;

            State s = StateFor(weapon);

            if (s.lastFromEnemy == null)
                s.lastFromEnemy = new Dictionary<Unit, float>();

            float last;

            if (s.lastFromEnemy.TryGetValue(victim, out last) &&
                Time.time - last < delay)
            {
                return false;
            }

            if (s.lastFromEnemy.Count > 128)
                Prune(s.lastFromEnemy);

            s.lastFromEnemy[victim] = Time.time;
            return true;
        }

        private static void Prune(Dictionary<Unit, float> map)
        {
            var dead = new List<Unit>();

            foreach (var pair in map)
            {
                if (pair.Key == null || Time.time - pair.Value > 10f)
                    dead.Add(pair.Key);
            }

            for (int i = 0; i < dead.Count; i++)
                map.Remove(dead[i]);

            if (map.Count > 128)
                map.Clear();
        }

        // ---- which pool gets healed ----------------------------------------
        //
        // Default is the player's OWN health pool, read off their
        // DamagableResource rather than resolved from the string "Health" -
        // correct by construction, with no name to spell wrong.
        private static readonly Dictionary<Unit, Resource> _ownHealth =
            new Dictionary<Unit, Resource>();

        public static Resource HealthOf(Unit unit)
        {
            if (unit == null)
                return null;

            Resource found;

            if (_ownHealth.TryGetValue(unit, out found) && found != null)
                return found;

            try
            {
                var dr = unit.GetComponent<DamagableResource>();

                if (dr == null)
                    dr = unit.GetComponentInChildren<DamagableResource>(true);

                found = (dr != null) ? dr.resource : null;

                if (found == null)
                    found = ForgeAssets.ResolveResource("Health");

                if (_ownHealth.Count > 64)
                    _ownHealth.Clear();

                _ownHealth[unit] = found;
                return found;
            }
            catch (Exception e)
            {
                Log.LogWarning("Finding the ship's health pool failed: " + e);
                return null;
            }
        }

        // ---- paying out -----------------------------------------------------

        public static float Heal(Unit player, Resource resource, float amount)
        {
            if (player == null || resource == null || amount <= 0f)
                return 0f;

            try
            {
                if (!player.HasTank(resource))
                    return 0f;

                ResourceTank tank = player.GetTank(resource);

                if (tank == null)
                    return 0f;

                float before = tank.Value;

                // Charge() clamps to [0, Capacity], which is right for health
                // and wrong for an infinite tank whose Capacity is meaningless -
                // there the Value setter refuses decreases on its own. Same rule
                // as the on-kill payout.
                if (tank.isInfinite)
                    tank.Value = tank.Value + amount;
                else
                    tank.Charge(amount);

                return tank.Value - before;
            }
            catch (Exception e)
            {
                Log.LogWarning("Healing failed: " + e);
                return 0f;
            }
        }

        // ---- orb bookkeeping ------------------------------------------------

        private static int _orbs;

        public static bool ClaimOrb(int max)
        {
            if (max > 0 && _orbs >= max)
                return false;

            _orbs++;
            return true;
        }

        public static void ReleaseOrb()
        {
            if (_orbs > 0)
                _orbs--;
        }

        // Modules are NOT uninstalled when the ship is destroyed, so a run
        // boundary drops everything and lets them re-register - the same reason
        // ModuleForgeBurn resets there.
        public static void Reset()
        {
            _byWeapon.Clear();
            _global.Clear();
            _state.Clear();
            _ownHealth.Clear();
            _orbs = 0;
            _saidLookClash = false;
            Any = false;
        }
    }
}
