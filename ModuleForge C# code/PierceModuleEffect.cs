using System;

namespace ModuleForge
{
    // While equipped, turns piercing ON for the ship's projectiles and
    // gives it a cap of `pierceCap` enemies (caps stack across equipped
    // pierce modules). Optional per-pierce damage falloff and an explosion
    // on the final hit. Projectile weapons only.
    [Serializable]
    public class PierceModuleEffect : ModuleEffect
    {
        public int pierceCap = 2;
        public float falloff;
        public bool explodeOnLimit;

        private bool _registered;
        private int _applied;
        private Unit.Data _owner;

        public override void OnInstalled(Unit.Data unit)
        {
            if (_registered) return;
            _owner = unit;
            _applied = pierceCap;
            ModuleForgeProjectile.AddPierce(unit, _applied, falloff, explodeOnLimit);
            _registered = true;
        }

        public override void OnUninstalled(Unit.Data unit)
        {
            if (!_registered) return;
            ModuleForgeProjectile.RemovePierce(_owner ?? unit, _applied);
            _registered = false;
            _owner = null;
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
