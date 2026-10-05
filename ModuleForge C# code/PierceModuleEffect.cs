using System;
using System.Collections.Generic;
using UnityEngine;

namespace ModuleForge
{
    // While equipped, turns piercing ON for the ship's projectiles and
    // gives it a cap of `pierceCap` enemies (caps stack across equipped
    // pierce modules). Optional per-pierce damage falloff and an explosion
    // on the final hit. Projectile weapons only.
    //
    // IHasDescriptionForUnit makes THIS module's own card show what it
    // contributes ("PIERCE +2"). The game's HoveredModuleInfo calls it for
    // any effect implementing the interface - no Harmony patch needed. (The
    // weapon's combined total is a separate line, see WeaponStatsPatch.)
    [Serializable]
    public class PierceModuleEffect
        : ModuleEffect, IHasDescriptionForUnit, ModuleForgeLive.IGated
    {
        // FloatSeries so the cap can scale with the module's level, and FLOAT
        // so a level can buy a FRACTION of a pierce: 1.5 pierces once always
        // and twice half the time. The roll is per projectile, at Shoot.
        public FloatSeries pierceCap;

        // Also a series: "each level loses less damage per pierce" is a
        // natural module, and it costs nothing now the pattern is here.
        public FloatSeries falloff;

        public bool explodeOnLimit;

        private bool _registered;
        private float _applied;
        private Unit.Data _owner;

        private int Level
        {
            get
            {
                int level = (base.Module != null) ? base.Module.Level : 1;
                return (level < 1) ? 1 : level;
            }
        }

        // Never negative: a series with a negative change would otherwise
        // hand a nonsense cap to the roll at high levels.
        private float CurrentCap
        {
            get
            {
                float v = pierceCap.GetElement(Level - 1);
                return (v < 0f) ? 0f : v;
            }
        }

        // Clamped to 0..1: it is used as (1 - falloff) damage multiplier, so
        // a negative value would AMPLIFY damage per pierce and above 1 would
        // go negative.
        private float CurrentFalloff
        {
            get
            {
                float v = falloff.GetElement(Level - 1);
                if (v < 0f) return 0f;
                return (v > 1f) ? 1f : v;
            }
        }

        public override void OnInstalled(Unit.Data unit)
        {
            Engage(unit);
            ModuleForgeLive.Track(this, unit);
        }

        public override void OnUninstalled(Unit.Data unit)
        {
            ModuleForgeLive.Untrack(this);
            Disengage(unit);
        }

        // Unpowered or disconnected is the same thing as not being there, which
        // is what the game does with every stock module - see ModuleForgeLive.
        // The cap is a shared accumulator, so a dead module left in it would
        // hand the whole ship pierce it has not earned.
        public void OnModuleLiveChanged(Unit.Data unit, bool live)
        {
            if (live)
                Engage(unit);
            else
                Disengage(unit);
        }

        // The one owner of each direction, so install, uninstall and the
        // powered gate cannot drift apart. `_registered` makes both idempotent,
        // and re-engaging re-reads the level, which is what makes a boosted
        // module come back at its CURRENT value rather than its old one.
        private void Engage(Unit.Data unit)
        {
            if (_registered) return;
            _owner = unit;
            _applied = CurrentCap;
            ModuleForgeProjectile.AddPierce(
                unit, _applied, CurrentFalloff, explodeOnLimit);
            _registered = true;
        }

        private void Disengage(Unit.Data unit)
        {
            if (!_registered) return;
            ModuleForgeProjectile.RemovePierce(_owner ?? unit, _applied);
            _registered = false;
            _owner = null;
            _applied = 0f;
        }

        // A BoosterCore placed beside this module raises Level without any
        // install/uninstall, so re-sync what we contributed.
        public override void OnRecalculateUnitStats(Unit.Data unit)
        {
            if (!_registered) return;

            float now = CurrentCap;

            if (now == _applied) return;

            ModuleForgeProjectile.AdjustPierce(
                _owner ?? unit, now - _applied);

            _applied = now;
        }

        // Shows what THIS module contributes on its own card. When it's
        // actively installed, it shows the running total as an old > new
        // transition (like the BURN FREQ line), so you can see the module's
        // "+N" and where it takes the ship's total pierce.
        public void GetPropertyList(
            Unit unit, bool isInstalled, List<DisplayableProperty> properties)
        {
            if (properties == null)
                return;

            string label =
                TextFormatter.ColoredText(TextFormatter.capsColor, "PIERCE");

            if (_registered)
            {
                float total = ModuleForgeProjectile.PierceCapTotal;
                float without = total - _applied;
                if (without < 0f) without = 0f;

                properties.Add(new DisplayableProperty(
                    label, "+" + Fmt(_applied), Fmt(without), Fmt(total)));
            }
            else
            {
                properties.Add(new DisplayableProperty(
                    label, "+" + Fmt(CurrentCap)));
            }

            // A fractional cap is worth spelling out - "+1.5 PIERCE" is not
            // self-explanatory, and a player watching one bullet cannot tell
            // a coin flip from a bug.
            float frac = CurrentCap - (int)CurrentCap;

            if (frac > 0f)
            {
                properties.Add(new DisplayableProperty(
                    "Extra pierce chance",
                    Mathf.RoundToInt(frac * 100f) + "%"));
            }

            float fall = CurrentFalloff;

            if (fall > 0f)
            {
                properties.Add(new DisplayableProperty(
                    "Damage per pierce",
                    "-" + (fall * 100f).ToString("0.#") + "%"));
            }

            if (explodeOnLimit)
                properties.Add(new DisplayableProperty("Explodes at the cap"));
        }

        private static string Fmt(float f)
        {
            return f.ToString("0.##");
        }

        public override ModuleEffect Clone()
        {
            return new PierceModuleEffect
            {
                pierceCap = this.pierceCap,
                falloff = this.falloff,
                explodeOnLimit = this.explodeOnLimit
            };
        }
    }
}
