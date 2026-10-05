using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ModuleForge
{
    // Coexistence bridge for the SLOW DEBUFF - the same shape as
    // ModuleForgeCritCompat, and needed for a sharper reason than crit's.
    //
    // Crit duplicated because two prefixes would each ROLL DICE on one hit -
    // wasteful and doubled, but self-correcting the moment one stops. **Two
    // slows are worse than doubled: they are permanent.** Both mods' live
    // components capture the victim's rigidbody values as "the original" and
    // write `original / factor`, so whichever captures second records the
    // already-slowed number and restores to it. The enemy is left at half speed
    // and a third of its mass for the rest of the run, with both mods' logs
    // reporting a correct restore. See the worked example at the top of
    // ModuleForgeSlow.
    //
    // So there is EXACTLY ONE OWNER of a unit's rigidbody:
    //
    //   * Weapon Forge PRESENT -> IT owns the debuff (its `ForgeSlowed` is the
    //     component that exists), and a module's slow is handed to
    //     `ForgeSlow.Apply` with its settings mirrored onto that mod's own
    //     Config. One component, one captured base.
    //   * Weapon Forge ABSENT -> this mod runs `ModuleForgeSlowed` and owns
    //     everything, because a slow module must work on its own.
    //
    // **THE TIME BEND DOES NOT COME THROUGH HERE AT ALL**, and that asymmetry
    // is the interesting half of this feature. `TimeManager.Update` writes the
    // MINIMUM across every live modifier, so two mods each registering one is
    // the stronger of the two rather than a compounding error - the engine
    // arbitrates already. `ModuleForgeSlow.BendTime` therefore runs whether or
    // not Weapon Forge is installed. One JSON block, two halves, two different
    // answers to "does this need cross-mod ownership?".
    public static class ModuleForgeSlowCompat
    {
        private static readonly ManualLogSource Log =
            ModuleForgeLog.Source("ModuleForge.Slow");

        private static bool _init;
        private static bool _weaponForgePresent;
        private static bool _bridgeReady;
        private static bool _announced;

        private static Type _configType;
        private static MethodInfo _apply;       // ForgeSlow.Apply(Unit, Config)

        // Their field set, resolved once. The three in the first group are
        // required; everything else is applied when present.
        private static FieldInfo _fHasSlow, _fAmount, _fDuration;
        private static FieldInfo _fMode, _fCooldown, _fMaxStacks, _fFloor;
        private static FieldInfo _fSlowMove, _fSlowTurn, _fSlowFire;
        private static FieldInfo _fSlowKnockback, _fSlowShots;
        private static FieldInfo _fHasTint, _fTint, _fTintStrength, _fTintMode;

        // Their mode / tint-mode constants, read by NAME. A renumber over there
        // would otherwise turn "stack" into "ignore repeats" silently.
        //
        // **These are best-effort rather than bridge-blocking, unlike
        // ModuleForgeWeaponStats' stat IDs, and the difference is what a wrong
        // answer costs.** A mis-mapped stat id routes DAMAGE into the FIRE RATE
        // - a different number changing a different field, invisible in both
        // logs. A mis-mapped stack mode picks the wrong repeat behaviour, which
        // is visible the first time you shoot something twice. Proportion the
        // guard to the damage.
        private static int _theirRefresh = 0, _theirStack = 1, _theirIgnore = 2;
        private static int _theirMultiply = 0, _theirReplace = 1;

        // True when THIS mod owns the live debuff component.
        public static bool OwnsDebuff
        {
            get
            {
                EnsureInit();
                return !_weaponForgePresent;
            }
        }

        private static void EnsureInit()
        {
            if (_init)
                return;

            _init = true;

            Type slow = FindTypeQuietly("WeaponForge.ForgeSlow");

            _weaponForgePresent = slow != null;

            if (!_weaponForgePresent)
            {
                Log.LogInfo(
                    "Weapon Forge not detected - Module Forge runs its own " +
                    "slow engine, so a slow module works on its own.");
                return;
            }

            try
            {
                _configType = slow.GetNestedType("Config", BindingFlags.Public);
                _apply = AccessTools.Method(
                    slow, "Apply", new[] { typeof(Unit), _configType });

                if (_configType != null)
                {
                    _fHasSlow = AccessTools.Field(_configType, "hasSlow");
                    _fAmount = AccessTools.Field(_configType, "amount");
                    _fDuration = AccessTools.Field(_configType, "duration");
                    _fMode = AccessTools.Field(_configType, "mode");
                    _fCooldown = AccessTools.Field(_configType, "cooldown");
                    _fMaxStacks = AccessTools.Field(_configType, "maxStacks");
                    _fFloor = AccessTools.Field(_configType, "floor");
                    _fSlowMove = AccessTools.Field(_configType, "slowMove");
                    _fSlowTurn = AccessTools.Field(_configType, "slowTurn");
                    _fSlowFire = AccessTools.Field(_configType, "slowFire");
                    _fSlowKnockback =
                        AccessTools.Field(_configType, "slowKnockback");
                    _fSlowShots = AccessTools.Field(_configType, "slowShots");
                    _fHasTint = AccessTools.Field(_configType, "hasTint");
                    _fTint = AccessTools.Field(_configType, "tint");
                    _fTintStrength =
                        AccessTools.Field(_configType, "tintStrength");
                    _fTintMode = AccessTools.Field(_configType, "tintMode");
                }

                ReadConstants(slow);

                _bridgeReady =
                    _configType != null && _apply != null &&
                    _fHasSlow != null && _fAmount != null && _fDuration != null;

                if (_bridgeReady)
                {
                    Log.LogInfo(
                        "Weapon Forge detected - it owns a unit's rigidbody " +
                        "while slowed, and Module Forge's slow modules are " +
                        "applied through its engine so the two can never each " +
                        "capture the same \"original\" speed. The TIME SLOW " +
                        "half still runs here: the game's TimeManager takes " +
                        "the strongest live modifier, so both mods bending " +
                        "time is the stronger bend rather than a conflict.");
                }
                else
                {
                    Log.LogWarning(
                        "Weapon Forge detected but its slow API could not be " +
                        "resolved, so Module Forge's slow modules will not " +
                        "apply their DEBUFF (two mods each remembering a " +
                        "unit's original speed would make the slow permanent). " +
                        "Their \"timeSlow\" half still works. Update both mods " +
                        "to matching versions.");
                }
            }
            catch (Exception e)
            {
                _bridgeReady = false;
                Log.LogWarning("Weapon Forge slow bridge failed: " + e);
            }
        }

        private static void ReadConstants(Type slow)
        {
            _theirRefresh = ConstOr(slow, "Refresh", ModuleForgeSlow.Refresh);
            _theirStack = ConstOr(slow, "Stack", ModuleForgeSlow.Stack);
            _theirIgnore = ConstOr(slow, "Ignore", ModuleForgeSlow.Ignore);

            Type tint = FindTypeQuietly("WeaponForge.ForgeUnitTint");

            if (tint == null)
                return;

            _theirMultiply =
                ConstOr(tint, "Multiply", ModuleForgeUnitTint.Multiply);
            _theirReplace =
                ConstOr(tint, "Replace", ModuleForgeUnitTint.Replace);
        }

        private static int ConstOr(Type t, string name, int mine)
        {
            try
            {
                FieldInfo f = AccessTools.Field(t, name);

                if (f != null && f.FieldType == typeof(int))
                    return (int)f.GetValue(null);
            }
            catch (Exception)
            {
            }

            return mine;
        }

        // ------------------------------------------------------------------
        // Applying
        // ------------------------------------------------------------------

        public static void Apply(Unit victim, ModuleForgeSlow.Options o)
        {
            EnsureInit();

            if (victim == null || o == null)
                return;

            if (!_weaponForgePresent)
            {
                ModuleForgeSlow.ApplyLocally(victim, o);
                return;
            }

            if (!_bridgeReady)
                return;

            try
            {
                object cfg = Mirror(o);

                if (cfg == null)
                    return;

                Announce();
                _apply.Invoke(null, new object[] { victim, cfg });
            }
            catch (Exception e)
            {
                Log.LogWarning("Pushing a slow to Weapon Forge failed: " + e);
            }
        }

        // ------------------------------------------------------------------
        // The mirrored config
        // ------------------------------------------------------------------
        //
        // ONE MIRROR PER Options INSTANCE, REBUILT ONLY WHEN THE OPTIONS MOVE.
        // Sixteen reflected field writes on every hit in the game is not a
        // price worth paying for a debuff whose numbers change when a module is
        // installed or levelled and at no other time. `Options.revision` is
        // bumped by the effect each time it writes those fields, so the
        // comparison is one int.
        private class Mirrored
        {
            public object config;
            public int revision = -1;
        }

        private static readonly Dictionary<ModuleForgeSlow.Options, Mirrored>
            _mirrors = new Dictionary<ModuleForgeSlow.Options, Mirrored>();

        private static object Mirror(ModuleForgeSlow.Options o)
        {
            Mirrored m;

            if (!_mirrors.TryGetValue(o, out m))
            {
                m = new Mirrored { config = Activator.CreateInstance(_configType) };
                _mirrors[o] = m;
            }

            if (m.revision == o.revision)
                return m.config;

            object cfg = m.config;

            _fHasSlow.SetValue(cfg, true);
            _fAmount.SetValue(cfg, o.amount);
            _fDuration.SetValue(cfg, o.duration);

            Put(_fMode, cfg, TheirMode(o.mode));
            Put(_fCooldown, cfg, o.cooldown);
            Put(_fMaxStacks, cfg, o.maxStacks);
            Put(_fFloor, cfg, o.floor);
            Put(_fSlowMove, cfg, o.slowMove);
            Put(_fSlowTurn, cfg, o.slowTurn);
            Put(_fSlowFire, cfg, o.slowFire);
            Put(_fSlowKnockback, cfg, o.slowKnockback);
            Put(_fSlowShots, cfg, o.slowShots);
            Put(_fHasTint, cfg, o.hasTint);
            Put(_fTint, cfg, o.tint);
            Put(_fTintStrength, cfg, o.tintStrength);
            Put(_fTintMode, cfg, TheirTintMode(o.tintMode));

            m.revision = o.revision;
            return cfg;
        }

        private static void Put(FieldInfo f, object target, object value)
        {
            if (f == null)
                return;

            try
            {
                f.SetValue(target, value);
            }
            catch (Exception)
            {
                // A field this mod knows and that mod has renamed is a settings
                // loss, never a crash.
            }
        }

        private static int TheirMode(int mine)
        {
            if (mine == ModuleForgeSlow.Stack) return _theirStack;
            if (mine == ModuleForgeSlow.Ignore) return _theirIgnore;
            return _theirRefresh;
        }

        private static int TheirTintMode(int mine)
        {
            return (mine == ModuleForgeUnitTint.Replace)
                ? _theirReplace : _theirMultiply;
        }

        // Which mod is holding the rigidbody is invisible from in game, so it
        // is said once - the same rule crit's Announce follows.
        private static void Announce()
        {
            if (_announced)
                return;

            _announced = true;

            Log.LogInfo(
                "A slow module fired with Weapon Forge installed, so the " +
                "DEBUFF is applied through that mod's engine (one owner of " +
                "the victim's rigidbody - two would make the slow permanent). " +
                "Every setting crosses over except the trigger, which stays " +
                "this module's: \"onHit\" and \"onKill\" are decided here. " +
                "Said once per run.");
        }

        public static void Reset()
        {
            // The mirrors hold Options objects belonging to effects that are
            // rebuilt on install, so they are per-run rather than build-time.
            _mirrors.Clear();
            _announced = false;
        }

        // Scanned rather than asked for by AccessTools.TypeByName, which LOGS A
        // WARNING when the type is absent. Weapon Forge being uninstalled is a
        // completely normal state and must be silent.
        private static Type FindTypeQuietly(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t;

                try
                {
                    t = asm.GetType(fullName, false);
                }
                catch (Exception)
                {
                    continue;
                }

                if (t != null)
                    return t;
            }

            return null;
        }
    }
}
