using System;
using System.Collections.Generic;

namespace ModuleForge
{
    // While equipped, the ship's projectiles phase through terrain (but
    // still hit enemies). Save/load safe like the burn effects: rebuilt
    // from the registry via Clone() on continue, and OnInstalled re-fires
    // so it re-registers. (Projectile weapons; a phasing laser is better
    // done with WeaponForge's per-weapon phasing flag.)
    //
    // IHasDescriptionForUnit makes this module's own card say what it does
    // ("PHASING ON"); the game calls it for any effect implementing the
    // interface, so no Harmony patch is needed.
    [Serializable]
    public class PhasingModuleEffect
        : ModuleEffect, IHasDescriptionForUnit, ModuleForgeLive.IGated
    {
        private bool _registered;
        private Unit.Data _owner;

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
        public void OnModuleLiveChanged(Unit.Data unit, bool live)
        {
            if (live)
                Engage(unit);
            else
                Disengage(unit);
        }

        // The one owner of each direction, so install, uninstall and the
        // powered gate cannot drift apart. `_registered` makes both idempotent.
        private void Engage(Unit.Data unit)
        {
            if (_registered) return;
            _owner = unit;
            ModuleForgeProjectile.AddPhasing(unit);
            _registered = true;
        }

        private void Disengage(Unit.Data unit)
        {
            if (!_registered) return;
            ModuleForgeProjectile.RemovePhasing(_owner ?? unit);
            _registered = false;
            _owner = null;
        }

        public void GetPropertyList(
            Unit unit, bool isInstalled, List<DisplayableProperty> properties)
        {
            if (properties == null)
                return;

            properties.Add(new DisplayableProperty(
                TextFormatter.ColoredText(TextFormatter.electronColor, "PHASING"),
                "ON"));
            properties.Add(new DisplayableProperty(
                "Shots pass through terrain"));
        }

        public override ModuleEffect Clone()
        {
            return new PhasingModuleEffect();
        }
    }
}
