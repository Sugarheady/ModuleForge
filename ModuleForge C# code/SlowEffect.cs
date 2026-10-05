using System;
using System.Collections.Generic;
using UnityEngine;

namespace ModuleForge
{
    // Your hits slow what they land on, and optionally bend time for a moment.
    //
    // SHIP grid vs WEAPON grid falls out of the game's own architecture rather
    // than needing a flag, exactly as it does for ExecuteEffect, CritEffect and
    // KillRewardEffect. `WeaponFactory.Create` walks only the modules in a
    // WEAPON's cluster and calls `IWeaponModifier.Modify` on their effects, so:
    //
    //     Modify() was called  ->  a weapon's grid; `_weapon` records which.
    //                              Only that gun slows.
    //     Modify() never came  ->  the ship grid; `_weapon` stays null and
    //                              EVERY weapon slows.
    //
    // The card line is IHasDescriptionForUnit and NOT IHasDescriptionForWeapon:
    // `HoveredModuleInfo` calls BOTH if an effect implements both, so
    // implementing both prints every line twice. ForWeapon is also not the "am
    // I on a weapon?" test it looks like - it falls back to the primary weapon
    // for a ship-grid module. `_weapon` is that test.
    [Serializable]
    public class SlowEffect
        : ModuleEffect, IWeaponModifier, IHasDescriptionForUnit,
          ModuleForgeLive.IGated
    {
        // ---- the debuff -----------------------------------------------------
        public bool hasSlow = true;

        // The target FRACTION of normal speed. LOWER IS STRONGER, so a per-level
        // `change` that strengthens this is NEGATIVE - said in the builder page
        // and the how-to, because every other series in this mod grows.
        public FloatSeries amount;

        public FloatSeries duration;

        public int mode = ModuleForgeSlow.Refresh;
        public float cooldown;
        public int maxStacks = 3;
        public float floor = 0.2f;

        public bool slowMove = true;
        public bool slowTurn = true;
        public bool slowFire = true;
        public bool slowKnockback = true;
        public bool slowShots = true;

        public bool hasTint;
        public Color tint = Color.white;
        public float tintStrength = 1f;
        public int tintMode = ModuleForgeUnitTint.Multiply;

        // ---- bending time ---------------------------------------------------
        public bool hasTimeSlow;
        public FloatSeries timeScale;
        public float timeDuration = 0.25f;
        public float timeCooldown;
        public int timeMode = ModuleForgeSlow.Refresh;
        public bool timeEase = true;
        public bool timeUnscaled = true;

        // ---- triggers -------------------------------------------------------
        public bool onHit = true;
        public bool onKill;

        // ---- runtime --------------------------------------------------------
        [NonSerialized] private WeaponBase _weapon;
        [NonSerialized] private bool _installed;
        [NonSerialized] private ModuleForgeSlow.Options _options;

        private int Level
        {
            get
            {
                int level = (base.Module != null) ? base.Module.Level : 1;
                return (level < 1) ? 1 : level;
            }
        }

        // FloatSeries is a STRUCT in this game, so there is no null to check
        // for - an unspecified one is all zeroes, and EffectBuilder supplies the
        // non-zero default.
        private float Amount
        {
            get { return Mathf.Clamp(amount.GetElement(Level - 1), 0.01f, 1f); }
        }

        private float Duration
        {
            get { return Mathf.Max(0.05f, duration.GetElement(Level - 1)); }
        }

        private float TimeScale
        {
            get
            {
                return Mathf.Clamp(timeScale.GetElement(Level - 1), 0.01f, 1f);
            }
        }

        // Rebuilt on each Push rather than cached, because the thresholds are
        // per-level and the engine reads this object at hit time rather than
        // being handed fresh numbers. A cached one would freeze the slow at the
        // level the module was placed - the invisible-booster trap from inside.
        private ModuleForgeSlow.Options BuildOptions()
        {
            if (_options == null)
                _options = new ModuleForgeSlow.Options();

            _options.hasSlow = hasSlow;
            _options.amount = Amount;
            _options.duration = Duration;
            _options.mode = mode;
            _options.cooldown = cooldown;
            _options.maxStacks = maxStacks;
            _options.floor = floor;

            _options.slowMove = slowMove;
            _options.slowTurn = slowTurn;
            _options.slowFire = slowFire;
            _options.slowKnockback = slowKnockback;
            _options.slowShots = slowShots;

            _options.hasTint = hasTint;
            _options.tint = tint;
            _options.tintStrength = tintStrength;
            _options.tintMode = tintMode;

            _options.hasTimeSlow = hasTimeSlow;
            _options.timeScale = TimeScale;
            _options.timeDuration = timeDuration;
            _options.timeCooldown = timeCooldown;
            _options.timeMode = timeMode;
            _options.timeEase = timeEase;
            _options.timeUnscaled = timeUnscaled;

            _options.onHit = onHit;
            _options.onKill = onKill;

            // The cross-mod mirror compares this rather than sixteen reflected
            // fields on every hit. Bumped HERE, beside the writes it describes,
            // so a field added above without a bump is visibly wrong.
            _options.revision++;

            return _options;
        }

        // ---- lifecycle -------------------------------------------------------

        public override void OnInstalled(Unit.Data unit)
        {
            // Recorded before anything is pushed: a ship-grid contribution
            // reaches only the guns of the unit carrying it.
            ModuleForgeScope.SetOwner(this, unit);
            Engage();
            ModuleForgeLive.Track(this, unit);
        }

        public override void OnUninstalled(Unit.Data unit)
        {
            ModuleForgeLive.Untrack(this);
            Disengage();
        }

        // Unpowered or disconnected is the same thing as not being there, which
        // is what the game does with every stock module - see ModuleForgeLive.
        // `OnInstalled` fires for a card dropped anywhere in the grid, powered
        // or not, while every seam that RUNS a module takes
        // ConnectedAndPoweredModules.
        public void OnModuleLiveChanged(Unit.Data unit, bool live)
        {
            ModuleForgeScope.SetOwner(this, unit);

            if (live)
                Engage();
            else
                Disengage();
        }

        private void Engage()
        {
            _installed = true;
            Push();
        }

        private void Disengage()
        {
            _installed = false;

            // Dropped on the way out on purpose: an unpowered module is no
            // longer in ConnectedAndPoweredModules, so the weapon the cluster
            // rebuilds without it never calls Modify again and the capture
            // would be a WeaponBase that no longer exists.
            _weapon = null;
            ModuleForgeSlow.Clear(this);

            // A DEBUFF ALREADY ON A UNIT IS LEFT TO EXPIRE, and that is the
            // right reading rather than laziness. The live component restores
            // the rigidbody exactly when its own timer runs out, so pulling the
            // card stops NEW slows and lets the last one wear off - which is
            // what a player expects from a timed effect. Tearing it off early
            // is what `contactDamage` has to do, and only because a contact
            // grant has no expiry at all.
        }

        // Only ever called for a module in a WEAPON's cluster - that is the
        // whole scoping mechanism. Called again whenever the weapon is rebuilt
        // (a fresh WeaponBase instance), so it re-captures rather than assuming
        // the first one holds.
        public void Modify(WeaponBase weaponBase)
        {
            _weapon = weaponBase;
            Push();
        }

        // A BoosterCore beside this module raises Level with NO install or
        // uninstall, so OnInstalled never re-runs and the numbers would be
        // frozen at the level it was placed.
        public override void OnRecalculateUnitStats(Unit.Data unit)
        {
            if (!_installed)
                return;

            ModuleForgeScope.SetOwner(this, unit);
            Push();
        }

        // Keyed on `this`, so pushing again is idempotent - which it has to be,
        // because OnRecalculateUnitStats runs on every stat recalculation.
        private void Push()
        {
            ModuleForgeSlow.Set(_weapon, this, BuildOptions());
        }

        // ---- the module's own card -------------------------------------------

        public void GetPropertyList(
            Unit unit, bool isInstalled, List<DisplayableProperty> properties)
        {
            if (properties == null)
                return;

            if (hasSlow)
            {
                properties.Add(new DisplayableProperty(
                    TextFormatter.ColoredText(TextFormatter.capsColor, "SLOW"),
                    "to " + Pct(Amount) + " speed for " +
                    Fmt(Duration) + "s"));

                properties.Add(new DisplayableProperty(
                    "Slows", Parts()));

                if (mode == ModuleForgeSlow.Stack)
                {
                    properties.Add(new DisplayableProperty(
                        "Repeat hits",
                        "stack, down to " + Pct(floor) + " (max " +
                        maxStacks + ")"));
                }
                else
                {
                    properties.Add(new DisplayableProperty(
                        "Repeat hits", ModuleForgeSlow.ModeWord(mode)));
                }
            }

            if (hasTimeSlow)
            {
                properties.Add(new DisplayableProperty(
                    TextFormatter.ColoredText(
                        TextFormatter.capsColor, "TIME SLOW"),
                    "x" + Fmt(TimeScale) + " for " + Fmt(timeDuration) +
                    "s" + (onKill && !onHit ? " on a kill" : "")));
            }

            // Which grid a module sits in is the whole difference between "this
            // gun" and "all of them", and it is not guessable from the card.
            properties.Add(new DisplayableProperty(
                "Applies to",
                (_weapon != null) ? "this weapon" : "every weapon"));
        }

        private string Parts()
        {
            var bits = new List<string>();

            if (slowMove) bits.Add("movement");
            if (slowTurn) bits.Add("turning");
            if (slowFire) bits.Add("fire rate");
            if (slowShots) bits.Add("their shots");
            if (slowKnockback) bits.Add("weight");

            return (bits.Count == 0) ? "nothing" : string.Join(", ", bits.ToArray());
        }

        private static string Pct(float v)
        {
            return Mathf.RoundToInt(v * 100f) + "%";
        }

        private static string Fmt(float v)
        {
            return v.ToString("0.##");
        }

        // Every field, because `Module`'s constructor Clones each effect off the
        // ModuleData - so THIS is the copy the game actually runs, and anything
        // missing here silently reverts to the class default on every real
        // instance. `buffIndicator` was lost exactly that way once.
        public override ModuleEffect Clone()
        {
            return new SlowEffect
            {
                hasSlow = this.hasSlow,
                amount = this.amount,
                duration = this.duration,
                mode = this.mode,
                cooldown = this.cooldown,
                maxStacks = this.maxStacks,
                floor = this.floor,
                slowMove = this.slowMove,
                slowTurn = this.slowTurn,
                slowFire = this.slowFire,
                slowKnockback = this.slowKnockback,
                slowShots = this.slowShots,
                hasTint = this.hasTint,
                tint = this.tint,
                tintStrength = this.tintStrength,
                tintMode = this.tintMode,
                hasTimeSlow = this.hasTimeSlow,
                timeScale = this.timeScale,
                timeDuration = this.timeDuration,
                timeCooldown = this.timeCooldown,
                timeMode = this.timeMode,
                timeEase = this.timeEase,
                timeUnscaled = this.timeUnscaled,
                onHit = this.onHit,
                onKill = this.onKill
            };
        }
    }
}
