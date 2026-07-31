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
    public class PhasingModuleEffect : ModuleEffect, IHasDescriptionForUnit
    {
        private bool _registered;
        private Unit.Data _owner;

        public override void OnInstalled(Unit.Data unit)
        {
            if (_registered) return;
            _owner = unit;
            ModuleForgeProjectile.AddPhasing(unit);
            _registered = true;
        }

        public override void OnUninstalled(Unit.Data unit)
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
