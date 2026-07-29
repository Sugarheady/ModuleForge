using System;

namespace ModuleForge
{
    // While equipped, the ship's projectiles phase through terrain (but
    // still hit enemies). Save/load safe like the burn effects: rebuilt
    // from the registry via Clone() on continue, and OnInstalled re-fires
    // so it re-registers. (Projectile weapons; a phasing laser is better
    // done with WeaponForge's per-weapon phasing flag.)
    [Serializable]
    public class PhasingModuleEffect : ModuleEffect
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

        public override ModuleEffect Clone()
        {
            return new PhasingModuleEffect();
        }
    }
}
