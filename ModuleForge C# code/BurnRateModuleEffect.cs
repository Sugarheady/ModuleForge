using System;
using System.Collections.Generic;

namespace ModuleForge
{
    // A weapon-module effect that speeds up the burn your weapons inflict.
    // While equipped it adds ticksPerSecond to the burn tick FREQUENCY on
    // enemies (additive across equipped copies), clamped globally to
    // ModuleForgeBurn.MaxTicksPerSecond. The player is excluded, so burn
    // that enemies inflict on you keeps ticking at the normal rate.
    //
    // Implements IHasDescriptionForWeapon so the module card shows a
    // "BURN FREQ +x/s" line (with the running total when active), the same
    // way AddBurnEffect shows "BURN +x".
    //
    // Save/load safe: modules persist by Id only; on continue the effect
    // list is rebuilt from our registry via Clone(), and the restore path
    // re-fires OnInstalled - so the boost re-registers automatically.
    [Serializable]
    public class BurnRateModuleEffect : ModuleEffect, IHasDescriptionForWeapon
    {
        // Ticks/sec this module adds per equipped copy (a FloatSeries so
        // it can optionally scale with the module's level).
        public FloatSeries ticksPerSecond;

        private bool _registered;
        private float _applied;     // exact amount added, for exact removal
        private Unit.Data _owner;

        private float CurrentValue
        {
            get
            {
                int level = (base.Module != null) ? base.Module.Level : 1;
                if (level < 1)
                    level = 1;

                return ticksPerSecond.GetElement(level - 1);
            }
        }

        public override void OnInstalled(Unit.Data unit)
        {
            if (_registered)
                return;

            _applied = CurrentValue;
            _owner = unit;
            ModuleForgeBurn.AddBooster(unit, _applied);
            _registered = true;
        }

        public override void OnUninstalled(Unit.Data unit)
        {
            if (!_registered)
                return;

            ModuleForgeBurn.RemoveBooster(_owner ?? unit, _applied);
            _registered = false;
            _owner = null;
            _applied = 0f;
        }

        // If the module's level (hence per-copy value) changed while it
        // stayed installed, re-sync the summed delta.
        public override void OnRecalculateUnitStats(Unit.Data unit)
        {
            if (!_registered)
                return;

            float now = CurrentValue;
            if (now != _applied)
            {
                ModuleForgeBurn.AdjustDelta(now - _applied);
                _applied = now;
            }
        }

        public override ModuleEffect Clone()
        {
            // Fresh (unregistered) copy - each installed Module gets its
            // own instance and registers itself on install.
            return new BurnRateModuleEffect
            {
                ticksPerSecond = this.ticksPerSecond
            };
        }

        // Shows the burn-frequency stat on the module card. When this
        // module is actively boosting, it shows the running total (sum of
        // every equipped burn-rate module) as an old > new transition,
        // like the game's own BURN line does for total burn.
        public void GetDescription(
            WeaponBase weapon,
            bool isInstalled,
            List<DisplayableProperty> properties)
        {
            string label =
                TextFormatter.ColoredText(TextFormatter.capsColor, "BURN FREQ");

            if (_registered)
            {
                float total = ModuleForgeBurn.Delta;
                float without = total - _applied;
                if (without < 0f)
                    without = 0f;

                properties.Add(new DisplayableProperty(
                    label,
                    "+" + Fmt(_applied) + "/S",
                    "+" + Fmt(without) + "/S",
                    "+" + Fmt(total) + "/S"));
            }
            else
            {
                properties.Add(new DisplayableProperty(
                    label, "+" + Fmt(CurrentValue) + "/S"));
            }
        }

        private static string Fmt(float f)
        {
            return f.ToString("0.##");
        }
    }
}
