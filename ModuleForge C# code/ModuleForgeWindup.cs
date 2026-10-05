using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ModuleForge
{
    // WINDUP - sustained fire rewards you. The port of Weapon Forge's `spinUp`.
    //
    // ---------------------------------------------------------------------
    // ★ WHY IT IS CALLED "windup" HERE AND "spinUp" THERE
    // ---------------------------------------------------------------------
    //
    // A deliberate exception to the one-word-across-both-mods rule, decided by
    // the author 2026-09-18. **This mod already uses `spinup` for something
    // else**: it is a live alias on `ExtraWeaponStatEffect` pointing at the
    // game's own `WarmupTime`, a flat delay before a gun starts firing - one
    // number, applied once, no meter and no reward. Files exist against it, and
    // *the mod rejecting or silently re-interpreting JSON its own page wrote*
    // has cost two test rounds.
    //
    // The alternative was to reclaim the word and retire that alias, which was
    // cheap (`warmuptime` already names that stat correctly). He chose to leave
    // it alone, so **the confusion hazard survives and has to be answered out
    // loud instead**: `EffectBuilder` warns whenever a module says `"spinup"`,
    // naming what it actually gets and pointing at `windup`. A hazard nobody
    // documents is the one that costs a round.
    //
    // ---------------------------------------------------------------------
    // WHAT MAKES IT WORK, and none of it is new
    // ---------------------------------------------------------------------
    //
    //   - `Shooter.FireRatePassed` is `Time.time > lastShootTime + 1f/FireRate`,
    //     computed FRESH every frame. So raising the rate mid-hold shortens the
    //     gap you are already waiting through; there is nothing to re-arm.
    //   - `AngleVariance` and `Spread` are read inside `GetDirections`, walked
    //     by `DoShoot` for the damage AND by `HitscanWeapon.OnBarrelMoved` for a
    //     beam's visual, so one write steadies both.
    //   - Every reward is published to `ModuleForgeWeaponStats`, never written
    //     onto the weapon - one captured base, named contributors, multiplied.
    //
    // IT IS AUTHORED AS TIMES, NOT AS A METER. Heat needs five numbers because
    // the interesting question is where the ceiling is; a wind-up has no
    // ceiling to discover - it is at 0 or on its way to 1. So `windUpTime` /
    // `windDownTime` / `grace` are the numbers an author actually has in mind.
    //
    // ---------------------------------------------------------------------
    // ★ NO CROSS-MOD OWNERSHIP, AND IT COMES FREE FROM THE STAT TABLE
    // ---------------------------------------------------------------------
    //
    // Asked per mechanism, which is the rule `slow` established. A weapon-side
    // `spinUp` and a module-side `windup` on one gun both publish into **one**
    // table - `ModuleForgeWeaponStats` bridges into `WeaponForge
    // .ForgeWeaponStats` when that mod is present - with **one captured base
    // and two named contributors**, which the table multiplies. That is correct
    // composition, not corruption: there is no second capture to go stale and
    // no rate to double behind anyone's back. Same answer as `execute` and as
    // slow's time bend, for the same underlying reason - **the engine already
    // arbitrates**.
    //
    // **The one thing that had to be got right is the KEY.** Contributions
    // under the same key OVERWRITE; under different keys they MULTIPLY. So this
    // publishes under `"windup"` and Weapon Forge under `"spinUp"`, and the
    // rename the author picked makes that distinction free rather than
    // something to remember. (`ModuleForgeSlowed` had to choose its key by hand
    // for exactly this reason.)
    public static class ModuleForgeWindup
    {
        private static readonly ManualLogSource Log =
            ModuleForgeLog.Source("ModuleForge.Windup");

        // The contributor key. Deliberately NOT Weapon Forge's "spinUp" - see
        // the note above; identical keys overwrite, different keys multiply.
        public const string Key = "windup";

        // How the reward arrives between empty and full.
        public const int Smooth = 0;   // every frame is slightly better
        public const int Stepped = 1;  // discrete stages, each announced
        public const int Click = 2;    // smooth, but full has a moment

        public static int ParseRamp(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "stepped":
                case "steps":
                case "tiers":
                case "tiered":
                    return Stepped;

                case "click":
                case "clicked":
                case "snap":
                    return Click;

                default:
                    return Smooth;
            }
        }

        public static string RampWord(int r, int steps)
        {
            switch (r)
            {
                case Stepped: return "in " + steps + " steps";
                case Click: return "smoothly, with a click at full";
                default: return "smoothly";
            }
        }

        public class Options
        {
            // The clocks. `grace` is the trigger-discipline knob: with the
            // wind-down starting the instant you let go, tapping would keep the
            // bonus for free.
            public float windUpTime = 1.5f;
            public float windDownTime = 1f;
            public float grace = 0.25f;

            public int ramp = Smooth;
            public int steps = 3;

            public ModuleForgeRewards rewards = new ModuleForgeRewards();

            // ---- feedback ----
            //
            // NO MUZZLE TINT, and that is a stated omission rather than a
            // forgotten one - see the effect's header. This mod owns a unit
            // tint table and a buff HUD; it has no muzzle-flash layer table,
            // and building one is its own job with its own traps.
            public bool showOnHud = true;
            public string readySfx = "";
            public string stepSfx = "";
            public string lostSfx = "";
        }

        // ------------------------------------------------------------------
        // The registry - global (ship grid) plus per weapon (weapon grid)
        // ------------------------------------------------------------------
        private static readonly Dictionary<object, Options> _global =
            new Dictionary<object, Options>();

        private static readonly Dictionary<WeaponBase, Dictionary<object, Options>>
            _byWeapon = new Dictionary<WeaponBase, Dictionary<object, Options>>();

        public static bool Any { get; private set; }

        private static void Sync()
        {
            Any = _global.Count > 0 || _byWeapon.Count > 0;
        }

        public static void Set(WeaponBase weapon, object key, Options options)
        {
            if (key == null || options == null)
                return;

            // Withdraw from every other bucket first: a module can be moved
            // between grids, and a weapon is a fresh `WeaponBase` instance
            // whenever its cluster is rebuilt.
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

        // ONE RAMP APPLIES, and the weapon's own grid wins over the ship grid.
        //
        // Copied from `ModuleForgeGrow.Resolve` deliberately, because it is the
        // same problem shape and it already has an answer here: **two ramps
        // cannot be combined in any way a player could predict.** Two
        // wind-ups multiplied would be a third weapon that is neither, and
        // averaging their times gives a ramp neither card describes. So the
        // nearer card wins and the log says so once.
        //
        // Note this is NOT slow's "strongest wins": a slow is one number, so
        // "strongest" is meaningful. A ramp is a clock plus seventeen optional
        // rewards, and there is no ordering over that.
        public static Options Resolve(WeaponBase weapon)
        {
            Dictionary<object, Options> byKey;

            if (weapon != null && _byWeapon.TryGetValue(weapon, out byKey))
            {
                foreach (var pair in byKey)
                {
                    if (pair.Value != null)
                        return pair.Value;
                }
            }

            // ★ SHIP-GRID MEANS THE SHIP'S OWN GUNS. Without the owner test
            // every ENEMY Shooter got a meter too - enemies wound up and fired
            // faster the longer they held on, and each of their meters posted
            // its own line to your status feed, which is the chirp storm of
            // R19 test 246. See ModuleForgeScope.
            foreach (var pair in _global)
            {
                if (pair.Value != null && ModuleForgeScope.Applies(pair.Key, weapon))
                    return pair.Value;
            }

            return null;
        }

        public static int CountFor(WeaponBase weapon)
        {
            int n = _global.Count;

            Dictionary<object, Options> byKey;

            if (weapon != null && _byWeapon.TryGetValue(weapon, out byKey))
                n += byKey.Count;

            return n;
        }

        // ------------------------------------------------------------------
        // Is this Shooter blocked?
        // ------------------------------------------------------------------
        //
        // `Block(object)` / `Unblock(object)` are public but there is no
        // `IsBlocked`, and `blockers` is a private `HashSet<object>`. Worth one
        // reflected read: a jam or a freeze should cost you the wind-up, and
        // asking those features directly would mean naming them here and
        // missing the next one.
        //
        // House rule on reflection - resolve once, latch either way, degrade to
        // doing nothing. A gun that keeps winding up while jammed is a small
        // wrongness; a gun that throws every frame is not.
        private static FieldInfo _blockers;
        private static bool _blockersLooked;

        public static bool IsBlocked(Shooter shooter)
        {
            if (shooter == null)
                return false;

            if (!_blockersLooked)
            {
                _blockersLooked = true;
                _blockers = AccessTools.Field(typeof(Shooter), "blockers");

                if (_blockers == null)
                {
                    Log.LogInfo(
                        "Could not reach Shooter.blockers, so a jammed or " +
                        "stunned weapon will keep its wind-up instead of " +
                        "losing it. Everything else about windup works.");
                }
            }

            if (_blockers == null)
                return false;

            try
            {
                var set = _blockers.GetValue(shooter) as ICollection;
                return set != null && set.Count > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ------------------------------------------------------------------
        // ★ THE GADGET WARNING, said once, at the moment it becomes true
        // ------------------------------------------------------------------
        //
        // A MODULE APPLIES TO EVERY GUN IT IS CONNECTED TO, and the player
        // chose the slot - so unlike the weapon-side version, this cannot
        // REFUSE a gadget at build time and say so. Gadget weapons call
        // `weapon.Fire(...)` directly and never touch `Shooter`, so a wind-up
        // module beside one is silently inert.
        //
        // `ModuleForgeKills.ShotFired` fires from the `WeaponBase.DoShoot`
        // prefix, which is on the path of EVERY shot including a gadget's - so
        // "this weapon fired and has no Shooter meter" is answerable at runtime
        // even though it is not answerable at build time. That is the whole
        // reason this hangs off a shot rather than being a build warning.
        private static readonly HashSet<string> _saidGadget =
            new HashSet<string>();

        public static void NoteShotWithoutShooter(WeaponBase weapon)
        {
            if (weapon == null || weapon.TemplateData == null)
                return;

            string name = weapon.TemplateData.name ?? "?";

            if (!_saidGadget.Add(name))
                return;

            Log.LogWarning(
                "\"" + name + "\" fired without going through a Shooter, which " +
                "is how GADGET weapons fire - they call the weapon directly. A " +
                "windup module cannot reach one: the meter is driven by the " +
                "held trigger, and a gadget has no held trigger to read. The " +
                "module still works on your primary and secondary. Said once " +
                "per weapon per run.");
        }

        // Per-RUN state: live `WeaponBase` keys and contributions from modules
        // that are not uninstalled on teardown. The meters themselves are
        // MonoBehaviours on Shooters and die with the scene for free.
        public static void Reset()
        {
            _global.Clear();
            _byWeapon.Clear();
            _saidGadget.Clear();
            Sync();
        }
    }
}
