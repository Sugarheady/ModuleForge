using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace ModuleForge
{
    // Your shots change SIZE as they travel.
    //
    // SHIP grid vs WEAPON grid falls out of the game's own architecture, the
    // same as CritEffect and ExecuteEffect: `Modify` being called means a
    // weapon's cluster, and never being called means the ship.
    //
    // ---------------------------------------------------------------------
    // ★ NO `damageAtFull`, AND THE REASON IS A REAL CONSTRAINT RATHER THAN A
    // SHORTCUT
    // ---------------------------------------------------------------------
    //
    // Weapon Forge's `grow` can multiply damage at full size, and it does it by
    // publishing into `ForgeDamageStack` - one captured base per shot, every
    // contributor multiplied, order-independent by construction. That stack
    // exists because `grow.damageAtFull` and `damageRamp` would otherwise each
    // assign `Damage.amount` from their own captured base and cancel each other
    // out, in whichever order Harmony happened to run them.
    //
    // **This mod has no such stack, and it already has an in-flight damage
    // writer:** `ModuleForgePierceCap` multiplies `Projectile.Damage` by its
    // falloff on every target a piercing shot passes through. A grow damage
    // multiplier writing `base * multiplier` every frame would erase that
    // falloff on the very next frame - so a pierce module and a grow module on
    // one gun would silently disagree, with both logs reporting success.
    //
    // So the size ramp ships and the damage ramp does not. Adding it means
    // porting the damage stack first, which is its own job and would make this
    // the wrong size of change. **Stated here rather than left as an absence**,
    // because the next person to want it needs the reason, not the gap.
    [Serializable]
    public class GrowEffect
        : ModuleEffect, IWeaponModifier, IHasDescriptionForUnit,
          ModuleForgeLive.IGated
    {
        // ---- the ramp ------------------------------------------------------
        // Size multipliers relative to the shot's normal size. Series so a
        // level can buy a bigger finish, which is the dial worth scaling.
        public FloatSeries from;
        public FloatSeries to;

        public bool overTime;
        public float span;
        public bool hitbox = true;
        public float curve = 1f;
        public bool clamp = true;

        // ---- runtime -------------------------------------------------------
        [NonSerialized] private WeaponBase _weapon;
        [NonSerialized] private bool _installed;
        [NonSerialized] private ModuleForgeGrow.Options _options;

        private static readonly ManualLogSource Log =
            ModuleForgeLog.Source("ModuleForge.Grow");

        // ONE-SHOT and named `_said*` so ModuleForgeDiagnosticGates sweeps it at
        // each run entry - a clash that did not happen in one run may well
        // happen in the next, and the run where you go looking for the warning
        // is exactly the run that could not produce it.
        [NonSerialized] private static bool _saidTwoGrows;

        private int Level
        {
            get
            {
                int level = (base.Module != null) ? base.Module.Level : 1;
                return (level < 1) ? 1 : level;
            }
        }

        // FloatSeries is a STRUCT, so an unspecified one is all zeroes and
        // EffectBuilder supplies the non-zero defaults.
        //
        // NEGATIVE IS FLOORED AT ZERO, NOT AT ONE. A multiplier of 0 is a shot
        // with no size at all, which is legitimate as a starting point (it
        // appears from nothing) and is the author's business; a negative one
        // would mirror the sprite and invert the hitbox arithmetic.
        private float From
        {
            get { return Mathf.Max(0f, from.GetElement(Level - 1)); }
        }

        private float To
        {
            get { return Mathf.Max(0f, to.GetElement(Level - 1)); }
        }

        private ModuleForgeGrow.Options BuildOptions()
        {
            if (_options == null)
                _options = new ModuleForgeGrow.Options();

            _options.from = From;
            _options.to = To;
            _options.overTime = overTime;
            _options.span = span;
            _options.hitbox = hitbox;
            _options.curve = curve;
            _options.clamp = clamp;

            return _options;
        }

        // ---- lifecycle -----------------------------------------------------

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

        // Unpowered or disconnected is the same as not being there - the game's
        // own rule for every stock module. See ModuleForgeLive.
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
            _weapon = null;
            ModuleForgeGrow.Clear(this);
        }

        public void Modify(WeaponBase weaponBase)
        {
            _weapon = weaponBase;
            Push();
        }

        // A BoosterCore raises Level with no install or uninstall, so without
        // this the ramp would freeze at the level the card was placed.
        public override void OnRecalculateUnitStats(Unit.Data unit)
        {
            if (!_installed)
                return;

            ModuleForgeScope.SetOwner(this, unit);
            Push();
        }

        private void Push()
        {
            ModuleForgeGrow.Set(_weapon, this, BuildOptions());

            // TWO GROW MODULES ON ONE GUN CANNOT BE COMBINED SENSIBLY, so one
            // wins - and this is said at the moment it becomes true rather than
            // at damage time, where it would print per shot.
            //
            // Multiplying two 0.4->3 ramps gives 0.16->9, which is a different
            // weapon than either; averaging gives neither. There is no answer a
            // player would predict, so the honest thing is to pick one and say
            // that is what happened.
            if (!_saidTwoGrows && ModuleForgeGrow.CountFor(_weapon) > 1)
            {
                _saidTwoGrows = true;

                Log.LogWarning(
                    "more than one GROW module applies to the same weapon. " +
                    "Only ONE ramp is used - they cannot be combined in any " +
                    "way you could predict (two 0.4->3 ramps multiplied would " +
                    "be 0.16->9, a different weapon than either). A module in " +
                    "the WEAPON's own grid wins over one on the ship grid; " +
                    "otherwise it is whichever was registered first. Said once " +
                    "per run.");
            }
        }

        // ---- the module's own card -----------------------------------------

        public void GetPropertyList(
            Unit unit, bool isInstalled, List<DisplayableProperty> properties)
        {
            if (properties == null)
                return;

            float f = From;
            float t = To;

            properties.Add(new DisplayableProperty(
                TextFormatter.ColoredText(
                    TextFormatter.capsColor, t >= f ? "GROWS" : "SHRINKS"),
                "x" + Fmt(f) + " to x" + Fmt(t)));

            properties.Add(new DisplayableProperty(
                "Over",
                span > 0f
                    ? (Fmt(span) + (overTime ? " seconds" : " units"))
                    : (overTime ? "its lifetime" : "its range")));

            // The one that decides whether this is a weapon change or a paint
            // job, and a player cannot see it any other way.
            properties.Add(new DisplayableProperty(
                "Hitbox",
                hitbox ? "grows with it" : "unchanged (visual only)"));

            if (!clamp)
            {
                properties.Add(new DisplayableProperty(
                    "Past full size", "keeps growing"));
            }

            properties.Add(new DisplayableProperty(
                "Applies to",
                (_weapon != null) ? "this weapon" : "every weapon"));
        }

        private static string Fmt(float v)
        {
            return v.ToString("0.##");
        }

        // Every field - `Module`'s constructor Clones each effect off the
        // ModuleData, so this is the copy the game actually runs and anything
        // omitted here silently reverts to the class default.
        public override ModuleEffect Clone()
        {
            return new GrowEffect
            {
                from = this.from,
                to = this.to,
                overTime = this.overTime,
                span = this.span,
                hitbox = this.hitbox,
                curve = this.curve,
                clamp = this.clamp
            };
        }
    }
}
