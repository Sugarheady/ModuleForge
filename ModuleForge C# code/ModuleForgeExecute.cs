using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace ModuleForge
{
    // EXECUTE - finish anything a hit leaves under a threshold.
    //
    // A port of Weapon Forge's `ForgeExecute`, and the cheapest of the thirteen
    // behaviours on the port list for a reason that held up when measured: the
    // seam it needs (`DamagableResource.Damage`) is already prefixed for crit
    // and wrapped for leech in this mod, so this is one more postfix on a call
    // site that is already ours.
    //
    // ---------------------------------------------------------------------
    // THE KILL CREDITS ITSELF, and that is the whole reason this is cheap
    // ---------------------------------------------------------------------
    //
    // All three damage routes end the same way:
    //
    //     base.ProjectileCollided(...)        // -> ... -> Damage(float)
    //     if (this.IsDead && projectile.Owner != null)
    //         projectile.Owner.RegisterKill(this.Unit);
    //
    // **The `IsDead` test runs AFTER the damage call returns.** So a postfix on
    // `Damage` that calls `Die()` is seen by the game's own credit line, and an
    // execute pays out on-kill rewards, leech, this mod's own
    // `KillRewardEffect` - including its gas cloud - and the music controller
    // **with no code from us**.
    //
    // Killing it any other way, e.g. `HealthBase.TakeDamage` behind the game's
    // back, credits nobody. That is exactly the mistake `ModuleForgeKills
    // .CreditKill` exists to undo for the gas cloud.
    //
    // ---------------------------------------------------------------------
    // ★ NO CROSS-MOD OWNERSHIP, AND THAT IS A REAL DIFFERENCE FROM CRIT/LEECH
    // ---------------------------------------------------------------------
    //
    // Crit stands down when Weapon Forge is present because two prefixes would
    // each **roll their own dice** on one hit. Leech stands down because two
    // pairs would each **measure the same hit and each pay out**. Both are
    // accumulating or random, so running twice is running wrong.
    //
    // **Execute is idempotent.** Two postfixes both calling `Die()` is not two
    // deaths: `Die()` sets `IsDead` synchronously before it returns, and the
    // second postfix's own first guard is `if (IsDead) return;`. So the pair
    // costs one dead enemy and one feedback flash, which is the correct answer.
    // There is no shared state to corrupt and no rate to double.
    //
    // So this mod keeps its patch running even with Weapon Forge installed, and
    // a weapon-side `execute` block and a module-side one simply both apply -
    // "either source says finish it" - which is what a player would expect from
    // two independent sources.
    //
    // **The one cost, stated rather than hidden:** which mod's colour and sound
    // play is decided by Harmony's postfix order, which is arbitrary. Both are
    // announcing the same event, so the flash is right either way; it may just
    // not be the one whose file you were editing.
    public static class ModuleForgeExecute
    {
        private static readonly ManualLogSource Log =
            ModuleForgeLog.Source("ModuleForge.Execute");

        public class Options
        {
            // Below this FRACTION of the tank's capacity, finish it. 0 disables
            // the percentage test and leaves only `flat`.
            public float percent = 0.15f;

            // Below this ABSOLUTE value, finish it. 0 = unused. Whichever of
            // the two is satisfied first wins, so a module can say "under 15%
            // OR under 20 health" and catch the chaff and the big one.
            public float flat;

            // Skip anything whose tank CAPACITY is above this - the boss guard.
            // 0 = no limit. Without it a 15% execute on a 2000-health boss is
            // 300 free damage in one hit.
            public float maxTargetHealth;

            // ---- stacking (R20 252, his design) --------------------------
            // "one initial percent value, one value when there's another
            // module added to the grid to increase the cap." Copies of the
            // SAME module file share a `group`; each copy past the first adds
            // these on top of the strongest copy's own threshold. 0 = copies
            // do not stack, which is how every file written before this reads.
            public string group;
            public float stackPercent;
            public float stackFlat;

            // ---- cues, because the game announces nothing --------------
            public string sfxGuid = "";

            public bool hasColor;
            public Color color = new Color(1f, 0.3f, 0.3f, 1f);
            public float colorSeconds = 0.25f;
            public float colorPulse;
            public float tintStrength = 1f;
            public int tintMode = ModuleForgeUnitTint.Multiply;
        }

        // ------------------------------------------------------------------
        // The registry - global (ship grid) plus per weapon (weapon grid)
        // ------------------------------------------------------------------
        //
        // The same two-bucket shape `ModuleForgeCrit` uses, and for the same
        // reason: a module in a WEAPON's cluster gets `IWeaponModifier.Modify`
        // and can key on that `WeaponBase`; one on the SHIP grid never does, so
        // it registers under the null key and means "every weapon".
        //
        // Keyed by the CONTRIBUTOR object inside each bucket so a push is
        // idempotent - `OnRecalculateUnitStats` runs on every stat
        // recalculation, and an add-style API would ratchet.
        private static readonly Dictionary<object, Options> _global =
            new Dictionary<object, Options>();

        private static readonly Dictionary<WeaponBase, Dictionary<object, Options>>
            _byWeapon = new Dictionary<WeaponBase, Dictionary<object, Options>>();

        // Read on EVERY damage call in the game, so it has to be a field test
        // rather than a dictionary walk.
        public static bool Any { get; private set; }

        // Does any installed contribution stack? Decides whether a hit takes
        // the grouping path at all - see ShouldFinish.
        private static bool _anyStacks;

        private static void Refresh()
        {
            Any = _global.Count > 0 || _byWeapon.Count > 0;

            bool stacks = false;

            foreach (var pair in _global)
            {
                if (Stacks(pair.Value))
                {
                    stacks = true;
                    break;
                }
            }

            if (!stacks)
            {
                foreach (var weapon in _byWeapon)
                {
                    foreach (var pair in weapon.Value)
                    {
                        if (Stacks(pair.Value))
                        {
                            stacks = true;
                            break;
                        }
                    }

                    if (stacks)
                        break;
                }
            }

            _anyStacks = stacks;
        }

        private static bool Stacks(Options o)
        {
            return o != null && (o.stackPercent > 0f || o.stackFlat > 0f);
        }

        public static void Set(WeaponBase weapon, object key, Options options)
        {
            if (key == null || options == null)
                return;

            // WITHDRAW FROM EVERY OTHER BUCKET FIRST. A module can be moved
            // between grids, and a weapon is a fresh `WeaponBase` instance
            // whenever its cluster is rebuilt - so "set here and nowhere else"
            // is the only semantics that cannot leave a stale contribution
            // executing on behalf of a weapon that no longer exists. Same
            // reasoning as ForgeCrit.SetBonus.
            Clear(key);

            if (weapon == null)
                _global[key] = options;
            else
            {
                Dictionary<object, Options> byKey;

                if (!_byWeapon.TryGetValue(weapon, out byKey))
                {
                    byKey = new Dictionary<object, Options>();
                    _byWeapon[weapon] = byKey;
                }

                byKey[key] = options;
            }

            Refresh();
        }

        public static void Clear(object key)
        {
            if (key == null)
                return;

            _global.Remove(key);

            List<WeaponBase> emptied = null;

            foreach (var pair in _byWeapon)
            {
                if (!pair.Value.Remove(key))
                    continue;

                if (pair.Value.Count == 0)
                {
                    if (emptied == null)
                        emptied = new List<WeaponBase>();

                    emptied.Add(pair.Key);
                }
            }

            if (emptied != null)
            {
                for (int i = 0; i < emptied.Count; i++)
                    _byWeapon.Remove(emptied[i]);
            }

            Refresh();
        }

        // BETWEEN DIFFERENT MODULES THE STRONGEST THRESHOLD WINS, and that is a
        // deliberate reading rather than an accident of iteration.
        //
        // Execute is not a rate and not a pool - it is a question with a yes or
        // no answer, asked once per hit. Summing two DIFFERENT modules'
        // thresholds would be nonsense (a 15% card and a 20% card are not a 35%
        // execute in any sense a player would predict), and taking the last one
        // iterated would make the answer depend on dictionary order. "Any
        // module that says finish it, finishes it" is order-independent and
        // guessable.
        //
        // ★ COPIES OF ONE MODULE STACK BY THE FILE'S OWN RULE (R20 252, his
        // design): the strongest copy's threshold, plus `stackPercent` /
        // `stackFlat` for every copy past the first. A file with neither key
        // stacks by 0, which is exactly the strongest-wins it always had.
        //
        // The BOSS GUARD is evaluated per module rather than globally, so a
        // module with a cap cannot be dragged past its own limit by a second
        // module that has none - and within a group the TIGHTEST cap holds.
        public static bool ShouldFinish(
            WeaponBase weapon, float current, float max, out Options fired)
        {
            fired = null;

            if (max <= 0f)
                return false;

            // THE COMMON CASE ALLOCATES NOTHING. This runs on every damage
            // call in the game, so while no installed copy stacks, the answer
            // is the old one-pass strongest-wins over the two buckets.
            if (!_anyStacks)
            {
                if (Qualifies(_global, weapon, true, current, max, ref fired))
                    return true;

                Dictionary<object, Options> bucket;

                if (weapon != null && _byWeapon.TryGetValue(weapon, out bucket))
                    return Qualifies(bucket, weapon, false, current, max, ref fired);

                return false;
            }

            List<Options> live = Applicable(weapon);

            if (live.Count == 0)
                return false;

            foreach (Group g in Groups(live))
            {
                // A capacity above the cap means "too big to execute".
                if (g.cap > 0f && max > g.cap)
                    continue;

                if (g.flat > 0f && current <= g.flat)
                {
                    fired = g.first;
                    return true;
                }

                if (g.percent > 0f && current <= g.percent * max)
                {
                    fired = g.first;
                    return true;
                }
            }

            return false;
        }

        private static bool Qualifies(
            Dictionary<object, Options> bucket, WeaponBase weapon, bool global,
            float current, float max, ref Options fired)
        {
            foreach (var pair in bucket)
            {
                Options o = pair.Value;

                if (o == null)
                    continue;

                if (global && !ModuleForgeScope.Applies(pair.Key, weapon))
                    continue;

                // A capacity above the cap means "too big to execute".
                if (o.maxTargetHealth > 0f && max > o.maxTargetHealth)
                    continue;

                if (o.flat > 0f && current <= o.flat)
                {
                    fired = o;
                    return true;
                }

                if (o.percent > 0f && current <= o.percent * max)
                {
                    fired = o;
                    return true;
                }
            }

            return false;
        }

        // Every contribution that answers for this weapon.
        //
        // ★ THE GLOBAL BUCKET ONLY ANSWERS FOR ITS OWN SHIP'S GUNS. Before R19
        // it answered for every gun in the game, and the victim guard ("never
        // your own side") guards the other direction - so an ENEMY shot could
        // execute YOU under 20%. See ModuleForgeScope.
        private static List<Options> Applicable(WeaponBase weapon)
        {
            var live = new List<Options>();

            foreach (var pair in _global)
            {
                if (pair.Value != null && ModuleForgeScope.Applies(pair.Key, weapon))
                    live.Add(pair.Value);
            }

            Dictionary<object, Options> byKey;

            if (weapon != null && _byWeapon.TryGetValue(weapon, out byKey))
            {
                foreach (var pair in byKey)
                {
                    if (pair.Value != null)
                        live.Add(pair.Value);
                }
            }

            return live;
        }

        public class Group
        {
            public Options first;
            public int copies;
            public float percent;
            public float flat;
            public float cap;
        }

        // Copies of one file fold into one group; a contribution with no
        // group (none today, but a null must not merge strangers) is its own.
        public static List<Group> Groups(List<Options> live)
        {
            var byName = new Dictionary<string, Group>();
            var order = new List<Group>();

            foreach (Options o in live)
            {
                Group g;
                string name = o.group;

                if (string.IsNullOrEmpty(name) || !byName.TryGetValue(name, out g))
                {
                    g = new Group { first = o };
                    order.Add(g);

                    if (!string.IsNullOrEmpty(name))
                        byName[name] = g;
                }

                g.copies++;

                // The STRONGEST copy is the base - a boosted copy's level
                // raises its own threshold - and the stack is added on top.
                g.percent = Mathf.Max(g.percent, o.percent);
                g.flat = Mathf.Max(g.flat, o.flat);

                if (o.maxTargetHealth > 0f)
                    g.cap = (g.cap > 0f) ? Mathf.Min(g.cap, o.maxTargetHealth) : o.maxTargetHealth;

                if (o.stackPercent > g.first.stackPercent || o.stackFlat > g.first.stackFlat)
                    g.first = o;
            }

            foreach (Group g in order)
            {
                int extra = g.copies - 1;

                if (extra <= 0)
                    continue;

                if (g.percent > 0f || g.first.stackPercent > 0f)
                    g.percent = Mathf.Min(1f, g.percent + g.first.stackPercent * extra);

                if (g.flat > 0f || g.first.stackFlat > 0f)
                    g.flat = g.flat + g.first.stackFlat * extra;
            }

            return order;
        }

        // How many copies of `group` this unit is carrying where they are
        // live - for the card, which should say what the stack is worth now.
        public static int CopiesOn(string group, Unit.Data owner)
        {
            if (string.IsNullOrEmpty(group) || owner == null)
                return 0;

            int n = 0;

            foreach (var pair in _global)
            {
                if (pair.Value != null && pair.Value.group == group &&
                    ReferenceEquals(ModuleForgeScope.OwnerOf(pair.Key), owner))
                {
                    n++;
                }
            }

            foreach (var weapon in _byWeapon)
            {
                foreach (var pair in weapon.Value)
                {
                    if (pair.Value != null && pair.Value.group == group &&
                        ReferenceEquals(ModuleForgeScope.OwnerOf(pair.Key), owner))
                    {
                        n++;
                    }
                }
            }

            return n;
        }

        public static void Feedback(Options o, Component victim)
        {
            if (o == null || victim == null)
                return;

            try
            {
                if (!string.IsNullOrEmpty(o.sfxGuid))
                    AudioManager.PlaySfx(o.sfxGuid, victim.transform.position);

                if (o.hasColor)
                {
                    Unit hurt = victim.GetComponentInParent<Unit>();

                    if (hurt != null)
                    {
                        // Its own layer key, so an execute flash and a crit
                        // flash on the same frame compose instead of one
                        // restoring over the other - the tint owner keeps one
                        // captured base per renderer for exactly this.
                        ModuleForgeUnitTint.Set(
                            hurt, "execute", o.color,
                            Mathf.Max(0.05f, o.colorSeconds),
                            o.colorPulse, o.tintStrength, o.tintMode);
                    }
                }
            }
            catch (Exception e)
            {
                // Decoration must never cost the kill that earned it.
                Log.LogError("Execute feedback failed: " + e);
            }
        }

        // Per-RUN state: these hold live `WeaponBase` keys and contributions
        // from modules that are not uninstalled on teardown. The Options
        // objects themselves belong to the effects and are rebuilt on install,
        // so there is no build-time config here to protect - unlike
        // ModuleForgeCrit's `_weapons`, which must survive.
        public static void Reset()
        {
            _global.Clear();
            _byWeapon.Clear();
            Refresh();
        }
    }
}
