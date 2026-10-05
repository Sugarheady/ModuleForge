using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace ModuleForge
{
    // SLOW - a debuff on whatever your weapons hit, plus (separately) a bend in
    // time at the moment they connect.
    //
    // A port of Weapon Forge's `ForgeSlow`. Two keys, no shared mechanism:
    //
    //     slow       is a debuff on the thing you hit; you stay at full speed.
    //     timeSlow   bends time for EVERYTHING, you included - a hit-stop punch.
    //
    // ---------------------------------------------------------------------
    // ★ ONE FEATURE, TWO HALVES, TWO DIFFERENT CROSS-MOD ANSWERS
    // ---------------------------------------------------------------------
    //
    // The port list records that "does this need cross-mod ownership?" has
    // three answers - stand down (crit, leech), bridge only (coolHeat), or
    // neither (execute). **Slow is the first feature where the two halves of
    // one JSON block answer it differently**, and the discriminator is the same
    // one every time: *what does running twice actually cost?*
    //
    // **The DEBUFF must stand down, and running it twice is not merely
    // wasteful - it is the permanent-debuff ratchet.** Both mods' live
    // components capture the victim's `linearDamping` / `angularDamping` /
    // `mass` as "the original" and write `original / factor`. Whichever
    // captures second records the ALREADY-SLOWED number as its base:
    //
    //     weapon slow starts   captures damping 1,   writes 2
    //     module slow starts   captures damping 2,   writes 4
    //     weapon slow ends     writes back 1
    //     module slow ends     writes back 2        <- HALF SPEED FOREVER
    //
    // That is the `ForgeUnitTint` bug one subsystem along, and it is why this
    // half goes through `ModuleForgeSlowCompat`: Weapon Forge present means it
    // owns the rigidbody and this mod hands the slow to `ForgeSlow.Apply`.
    //
    // **The TIME BEND needs nothing, because the engine it uses already
    // arbitrates.** `TimeManager.Update` walks every live modifier and writes
    // the MINIMUM to `Time.timeScale`, so two mods each registering one is not
    // two slows - it is the stronger one, which is the correct answer. There is
    // no captured base to corrupt and no rate to double, exactly as with
    // `execute`. So this half stays live with Weapon Forge installed and a
    // weapon-side `timeSlow` and a module-side one simply both apply.
    //
    // ---------------------------------------------------------------------
    // WHY THE RIGIDBODY AND NOT THE MOVEMENT COMPONENTS
    // ---------------------------------------------------------------------
    //
    // This is the load-bearing decision, and the obvious implementation is the
    // trap. A unit carries one `UnitMovement` per AI state, and
    // `MovementAction.OnEnable` SNAPSHOTS that component's current values while
    // `OnDisable` restores the snapshot wholesale. So a slow written into
    // `pushForce` mid-state has two failure modes: it is reverted the moment
    // the AI changes state, and - worse - if a state happens to BEGIN while the
    // unit is slowed, the slowed numbers become that state's "original" and are
    // restored forever after. A temporary debuff becomes permanent, at random,
    // depending on AI timing.
    //
    // Nothing snapshots the rigidbody, so everything goes there instead:
    //
    //   move speed   linearDamping    terminal speed under a constant force is
    //                                 force/damping, so damping/amount gives
    //                                 exactly `amount` times the speed
    //   turn speed   angularDamping   the same trick against AddTorque
    //   knockback    mass             PushMovement multiplies its OWN force by
    //                                 mass, so a unit's self-propulsion is
    //                                 unchanged and only EXTERNAL shoves - your
    //                                 shots, your blasts - move it further
    //
    // and the fire rate goes through `ModuleForgeWeaponStats`, which is the one
    // owner of a live weapon's tuning.
    //
    // It is also the game's own idiom: `DragCellBehaviourTarget.FixedUpdate` is
    // `rigidbody.linearVelocity *= 1f - drag`.
    //
    // ---------------------------------------------------------------------
    // ★ THE WORD "slow" IS ALREADY TAKEN IN THIS MOD, and that is fine
    // ---------------------------------------------------------------------
    //
    // `gas.slow` is a live key on `KillRewardEffect`'s cloud and means a
    // per-tick velocity drag applied to anything standing in it - not a debuff,
    // no duration, no restore. **Both mods spell it that way**, so the two
    // vocabularies already agree and there is nothing to reconcile; this is
    // recorded only because the `spinup` alias (which points at the game's flat
    // `WarmupTime` rather than at Weapon Forge's ramping meter) is the same
    // shape and IS a hazard. A player reading `gas.slow` and `SlowEffect` gets
    // two different mechanisms under one word, so the how-to says so.
    public static class ModuleForgeSlow
    {
        private static readonly ManualLogSource Log =
            ModuleForgeLog.Source("ModuleForge.Slow");

        // How a second hit on an already-slowed target behaves.
        public const int Refresh = 0;   // reset the timer (default)
        public const int Stack = 1;     // slow harder, down to a floor
        public const int Ignore = 2;    // the first hit wins until it expires

        public static int ParseMode(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "stack":
                case "stacking":
                    return Stack;

                case "ignore":
                case "first":
                case "once":
                    return Ignore;

                default:
                    return Refresh;
            }
        }

        public static string ModeWord(int mode)
        {
            switch (mode)
            {
                case Stack: return "stack";
                case Ignore: return "ignore repeats";
                default: return "refresh";
            }
        }

        // ------------------------------------------------------------------
        // What a module asks for
        // ------------------------------------------------------------------
        public class Options
        {
            // ---- the debuff on what you hit ----
            public bool hasSlow = true;
            public float amount = 0.5f;      // target fraction of normal speed
            public float duration = 2f;
            public int mode = Refresh;
            public float cooldown;           // 0 = none, which is the default
            public int maxStacks = 3;
            public float floor = 0.2f;       // strongest a stack can ever get

            public bool slowMove = true;
            public bool slowTurn = true;
            public bool slowFire = true;
            public bool slowKnockback = true;

            // Shots a slowed enemy fires come out slow too.
            //
            // `slowFire` already halves how OFTEN it shoots; this is how fast
            // the bullets travel, which is the half you can see and dodge.
            public bool slowShots = true;

            public bool hasTint;             // off by default, on request
            public Color tint = Color.white;
            public float tintStrength = 1f;
            public int tintMode = ModuleForgeUnitTint.Multiply;

            // ---- bending time itself ----
            public bool hasTimeSlow;
            public float timeScale = 0.35f;
            public float timeDuration = 0.25f;
            public float timeCooldown;       // 0 = none
            public int timeMode = Refresh;
            public bool timeEase = true;     // ride back up instead of snapping
            public bool timeUnscaled = true; // duration in REAL seconds

            // Triggers. Hitting is the default; killing is opt-in.
            public bool onHit = true;
            public bool onKill;

            [NonSerialized] public float lastTimeSlowAt = -999f;

            // Bumped by the effect every time it rewrites the fields above.
            // `ModuleForgeSlowCompat` mirrors this object onto Weapon Forge's
            // own Config by reflection, and it compares this one int rather
            // than re-writing sixteen fields on every hit in the game. The
            // numbers here move when a module is installed or levelled and at
            // no other time, so a revision is exactly the right granularity.
            [NonSerialized] public int revision;

            // The strength used for ranking when several modules apply. Lower
            // is stronger, and it is clamped exactly as the live component
            // clamps it so the ranking cannot disagree with the result.
            public float SlowStrength
            {
                get { return Mathf.Clamp(amount, 0.01f, 1f); }
            }
        }

        // ------------------------------------------------------------------
        // The registry - global (ship grid) plus per weapon (weapon grid)
        // ------------------------------------------------------------------
        //
        // The same two-bucket shape `ModuleForgeExecute` and `ModuleForgeCrit`
        // use, for the same reason: a module in a WEAPON's cluster gets
        // `IWeaponModifier.Modify` and can key on that `WeaponBase`; one on the
        // SHIP grid never does, so it registers under the null key and means
        // "every weapon".
        //
        // Keyed by the CONTRIBUTOR object inside each bucket so a push is
        // idempotent - `OnRecalculateUnitStats` runs on every stat
        // recalculation and an add-style API would ratchet.
        private static readonly Dictionary<object, Options> _global =
            new Dictionary<object, Options>();

        private static readonly Dictionary<WeaponBase, Dictionary<object, Options>>
            _byWeapon = new Dictionary<WeaponBase, Dictionary<object, Options>>();

        // Read on every hit in the game, so it is a field test rather than a
        // dictionary walk.
        public static bool Any { get; private set; }

        private static void Sync()
        {
            Any = _global.Count > 0 || _byWeapon.Count > 0;
        }

        public static void Set(WeaponBase weapon, object key, Options options)
        {
            if (key == null || options == null)
                return;

            // WITHDRAW FROM EVERY OTHER BUCKET FIRST. A module can be moved
            // between grids, and a weapon is a fresh `WeaponBase` instance
            // whenever its cluster is rebuilt - so "set here and nowhere else"
            // is the only semantics that cannot leave a stale contribution
            // slowing on behalf of a weapon that no longer exists.
            Clear(key);

            if (weapon == null)
            {
                _global[key] = options;
            }
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

            Sync();
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

            Sync();
        }

        // ------------------------------------------------------------------
        // Resolving several modules
        // ------------------------------------------------------------------
        //
        // THE STRONGEST WINS, and EACH HALF IS RESOLVED SEPARATELY.
        //
        // "Strongest wins" is the house answer already used by
        // `ModuleForgeContact` (highest damage) and `ModuleForgeExecute` (any
        // module that says finish it, finishes it). Summing would be nonsense -
        // two halving modules are not a 0x slow in any sense a player would
        // predict - and taking the last one iterated would make the answer
        // depend on dictionary order. A tie on the deciding number is broken by
        // the longer duration, so the result is order-independent outright.
        //
        // **The two halves are resolved independently because they share no
        // mechanism.** One module carrying only a debuff and another carrying
        // only a time bend must both apply; a single winner would silently
        // discard whichever half the loser owned. That is the same mistake as
        // reading a feature's two keys as one setting.
        public static bool ResolveSlow(WeaponBase weapon, out Options best)
        {
            best = null;

            // ★ The global bucket answers only for its own ship's guns - to an
            // ENEMY gun you are the other side, so the victim guard could not
            // stop an enemy hit slowing YOU. See ModuleForgeScope.
            PickSlow(_global, weapon, true, ref best);

            Dictionary<object, Options> byKey;

            if (weapon != null && _byWeapon.TryGetValue(weapon, out byKey))
                PickSlow(byKey, weapon, false, ref best);

            return best != null;
        }

        private static void PickSlow(
            Dictionary<object, Options> bucket, WeaponBase weapon, bool global,
            ref Options best)
        {
            foreach (var pair in bucket)
            {
                Options o = pair.Value;

                if (o == null || !o.hasSlow)
                    continue;

                if (global && !ModuleForgeScope.Applies(pair.Key, weapon))
                    continue;

                if (best == null)
                {
                    best = o;
                    continue;
                }

                if (o.SlowStrength < best.SlowStrength)
                    best = o;
                else if (o.SlowStrength == best.SlowStrength &&
                         o.duration > best.duration)
                    best = o;
            }
        }

        public static bool ResolveTime(WeaponBase weapon, out Options best)
        {
            best = null;

            PickTime(_global, weapon, true, ref best);

            Dictionary<object, Options> byKey;

            if (weapon != null && _byWeapon.TryGetValue(weapon, out byKey))
                PickTime(byKey, weapon, false, ref best);

            return best != null;
        }

        private static void PickTime(
            Dictionary<object, Options> bucket, WeaponBase weapon, bool global,
            ref Options best)
        {
            foreach (var pair in bucket)
            {
                Options o = pair.Value;

                if (o == null || !o.hasTimeSlow)
                    continue;

                if (global && !ModuleForgeScope.Applies(pair.Key, weapon))
                    continue;

                if (best == null)
                {
                    best = o;
                    continue;
                }

                if (o.timeScale < best.timeScale)
                    best = o;
                else if (o.timeScale == best.timeScale &&
                         o.timeDuration > best.timeDuration)
                    best = o;
            }
        }

        // How many modules are contributing, for the build log and the card.
        public static int CountFor(WeaponBase weapon)
        {
            int n = _global.Count;

            Dictionary<object, Options> byKey;

            if (weapon != null && _byWeapon.TryGetValue(weapon, out byKey))
                n += byKey.Count;

            return n;
        }

        // ------------------------------------------------------------------
        // Applying the debuff
        // ------------------------------------------------------------------

        // Goes through the compat layer, NOT straight to the component. See the
        // ratchet at the top of this file.
        public static void Apply(Unit victim, Options o)
        {
            if (victim == null || o == null || !o.hasSlow)
                return;

            ModuleForgeSlowCompat.Apply(victim, o);
        }

        // Adds this mod's own live debuff. Called by the compat layer when
        // Weapon Forge is absent, and by nothing else.
        public static void ApplyLocally(Unit victim, Options o)
        {
            if (victim == null || o == null || !o.hasSlow)
                return;

            try
            {
                var live = victim.gameObject.GetComponent<ModuleForgeSlowed>();

                if (live == null)
                {
                    live = victim.gameObject.AddComponent<ModuleForgeSlowed>();
                    live.Begin(victim, o);
                    return;
                }

                live.Again(o);
            }
            catch (Exception e)
            {
                Log.LogError("Slowing a unit failed: " + e);
            }
        }

        // ------------------------------------------------------------------
        // Bending time
        // ------------------------------------------------------------------

        // One shared owner object, so "cancel all of ours" is one call. The
        // game keys modifiers by owner and by nothing else.
        public static readonly object TimeOwner = new object();

        public static void BendTime(Options o)
        {
            if (o == null || !o.hasTimeSlow)
                return;

            try
            {
                if (o.timeCooldown > 0f &&
                    Time.unscaledTime - o.lastTimeSlowAt < o.timeCooldown)
                {
                    return;
                }

                // Ignore-repeats: while a previous one is still running, do
                // nothing at all rather than laying another on top.
                if (o.timeMode == Ignore &&
                    Time.unscaledTime - o.lastTimeSlowAt < o.timeDuration)
                {
                    return;
                }

                TimeManager time;

                if (!ServiceLocator.TryGet<TimeManager>(out time) || time == null)
                    return;

                var modifier = new TimeManager.TimeScaleModifier();

                modifier.timeScale = o.timeScale;

                // DURATION 0 MEANS FOREVER. `TimeScaleModifier.IsIndefinit` is
                // `duration <= 0f`, and an indefinite modifier is never
                // evaluated for expiry - it sits in the list holding the game
                // in slow motion until something calls RemoveAllModifiers. The
                // builder refuses a non-positive duration; this is the second
                // belt.
                modifier.duration = Mathf.Max(0.01f, o.timeDuration);

                // UNSCALED, OR THE SLOW EXTENDS ITSELF. With it off the elapsed
                // check uses `Time.time`, which is the thing we just slowed - a
                // 0.25s bend at scale 0.2 would really last 1.25 seconds, and
                // the heavier the slow the further it overruns.
                modifier.unscaled = o.timeUnscaled;

                if (o.timeEase)
                {
                    // Ride back up to normal across the duration rather than
                    // snapping, which is most of what makes hit-stop feel good.
                    modifier.useCurve = true;
                    modifier.curve =
                        AnimationCurve.EaseInOut(0f, o.timeScale, 1f, 1f);
                }

                time.AddModifier(modifier, TimeOwner);
                o.lastTimeSlowAt = Time.unscaledTime;
            }
            catch (Exception e)
            {
                Log.LogError("Bending time failed: " + e);
            }
        }

        public static void ClearTime()
        {
            try
            {
                TimeManager time;

                if (ServiceLocator.TryGet<TimeManager>(out time) && time != null)
                    time.RemoveAllModifiers(TimeOwner);
            }
            catch (Exception)
            {
            }
        }

        // Per-RUN state: live `WeaponBase` keys and contributions from modules
        // that are not uninstalled on teardown. The Options objects belong to
        // the effects and are rebuilt on install, so there is no build-time
        // config here to protect - unlike `ModuleForgeCrit`'s `_weapons`, which
        // must survive.
        //
        // **`ClearTime` is CALLED here rather than merely existing**, matching
        // the call Weapon Forge's own run reset already makes. That method sat
        // in that mod for weeks describing itself as being for "a future cancel
        // all of ours" with nothing calling it, until the teardown audit wired
        // it - a written teardown is not a wired one, so this copy is wired in
        // the same change that creates it. A run beginning while a bend is live
        // would otherwise start in slow motion, and a modifier with a
        // non-positive duration never expires at all.
        public static void Reset()
        {
            _global.Clear();
            _byWeapon.Clear();
            Sync();
            ClearTime();
            ModuleForgeSlowCompat.Reset();
        }
    }
}
