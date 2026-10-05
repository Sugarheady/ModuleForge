using UnityEngine;

namespace ModuleForge
{
    // ONE REWARD SET for anything that makes a weapon temporarily better.
    //
    // A deliberate copy of Weapon Forge's `ForgeRewards` - the duplicate-by-
    // design rule, same call as the buff HUD, the tint table and the loot
    // pools: a module must pay out with that mod absent, and this is a table of
    // multipliers rather than a decoder worth a cross-mod dependency.
    //
    // Today its only customer is `windup`. It is a class rather than fields on
    // that effect for the reason the twin records: Weapon Forge had `spinUp`
    // and `surge` with the same seventeen pairs, pasted twice, and the second
    // copy is the one that goes stale. If this mod ever gains a second ramping
    // feature - the module half of `surge` is on the list - it inherits every
    // reward and every warning by pointing at this.
    //
    // WHAT A FEATURE STILL OWNS: its ramp, its clock, its feedback, and the
    // `InitializeVisuals()` call that `pellets` forces (that needs per-instance
    // state - the last rounded count seen - so it cannot live here). `Publish`
    // reports whether the count is in play so the caller knows to make it.
    public class ModuleForgeRewards
    {
        // Fire rate and accuracy default ON - the two the weapon-side version
        // was asked for. Everything else waits to be named.
        public bool rate = true;
        public float fireRateAtFull = 2f;

        public bool accuracy = true;
        public float accuracyAtFull = 1f;   // the FRACTION of scatter removed
        public float spreadAtFull = 1f;     // multiplier on the fan, 1 = keep

        public bool damage;
        public float damageAtFull = 1.5f;

        public bool pellets;
        public float pelletsAtFull = 2f;

        public bool cheaper;
        public float costAtFull = 0.5f;

        public bool speed;
        public float speedAtFull = 1.5f;

        public bool range;
        public float rangeAtFull = 1.5f;

        public bool size;
        public float sizeAtFull = 1.5f;

        public bool burst;
        public float burstAtFull = 2f;
        public float burstDelayAtFull = 1f;

        public bool burn;
        public float burnAtFull = 2f;

        public bool explosion;
        public float explosionAtFull = 1.5f;

        public bool push;
        public float pushAtFull = 2f;
        public float knockbackAtFull = 1f;

        public bool warmup;
        public float warmupAtFull;          // 0 removes the minigun gate

        public bool lifetime;
        public float lifetimeAtFull = 1.5f;

        // Does anything here actually move a number? A ramp with nothing in it
        // is a valid file and a dead module.
        public bool PaysAnything
        {
            get
            {
                return rate || accuracy || damage || pellets || cheaper ||
                       speed || range || size || burst || burn || explosion ||
                       push || warmup || lifetime;
            }
        }

        // Publish every live reward at ramp position `f` (0..1).
        //
        // Returns TRUE when the projectile count is among them, which is the
        // caller's cue to re-run `InitializeVisuals()` if the ROUNDED count has
        // moved. That is not fastidiousness: `HitscanWeapon.OnBarrelMoved`
        // indexes `visualsInstances[num]` with no bounds guard, every frame the
        // barrel moves, so a beam whose count rose behind its back throws sixty
        // times a second - and orphaned visuals left ABOVE the count on the way
        // back down are a beam frozen in mid-air with `Firing` stuck true.
        public bool Publish(WeaponBase weapon, string key, float f)
        {
            if (weapon == null || string.IsNullOrEmpty(key))
                return false;

            if (rate)
                One(weapon, key, ModuleForgeWeaponStats.FireRate,
                    fireRateAtFull, f);

            if (accuracy)
            {
                // `accuracyAtFull` is the FRACTION OF THE SCATTER REMOVED, so 1
                // is pinpoint and 0.5 halves it. Expressed that way round
                // because "how much better does it get" is the question an
                // author is asking; the multiplier is the inverse.
                One(weapon, key, ModuleForgeWeaponStats.AngleVariance,
                    Mathf.Max(0f, 1f - accuracyAtFull), f);

                // Spread is the DELIBERATE fan, left alone unless asked -
                // `spreadAtFull` defaults to 1 and `One` withdraws rather than
                // pinning a base at a multiplier of 1. Tightening a shotgun's
                // spread to nothing turns it into a slug gun, which is a fine
                // weapon and a terrible surprise.
                One(weapon, key, ModuleForgeWeaponStats.Spread, spreadAtFull, f);
            }

            if (damage)
                One(weapon, key, ModuleForgeWeaponStats.Damage, damageAtFull, f);

            if (pellets)
                One(weapon, key, ModuleForgeWeaponStats.Pellets,
                    pelletsAtFull, f);

            if (cheaper)
                One(weapon, key, ModuleForgeWeaponStats.Cost, costAtFull, f);

            if (speed)
                One(weapon, key, ModuleForgeWeaponStats.Speed, speedAtFull, f);

            if (range)
                One(weapon, key, ModuleForgeWeaponStats.Range, rangeAtFull, f);

            if (size)
                One(weapon, key, ModuleForgeWeaponStats.Size, sizeAtFull, f);

            if (burst)
            {
                One(weapon, key, ModuleForgeWeaponStats.BurstSize,
                    burstAtFull, f);
                One(weapon, key, ModuleForgeWeaponStats.BurstDelay,
                    burstDelayAtFull, f);
            }

            if (burn)
                One(weapon, key, ModuleForgeWeaponStats.Burn, burnAtFull, f);

            if (explosion)
            {
                One(weapon, key, ModuleForgeWeaponStats.ExplosionRadius,
                    explosionAtFull, f);
                One(weapon, key, ModuleForgeWeaponStats.ExplosionDamage,
                    explosionAtFull, f);
            }

            if (push)
            {
                One(weapon, key, ModuleForgeWeaponStats.Push, pushAtFull, f);
                One(weapon, key, ModuleForgeWeaponStats.Knockback,
                    knockbackAtFull, f);
            }

            if (warmup)
                One(weapon, key, ModuleForgeWeaponStats.Warmup, warmupAtFull, f);

            if (lifetime)
                One(weapon, key, ModuleForgeWeaponStats.Lifetime,
                    lifetimeAtFull, f);

            return pellets;
        }

        // A MULTIPLIER OF 1 WITHDRAWS RATHER THAN BEING PUBLISHED. It matters
        // for two configurations that would otherwise pin a captured base for
        // nothing - a reward switched on and set to its no-op value, and the
        // default `spreadAtFull: 1`. A pinned base is invisible in play and
        // stops a legitimate change to the weapon (a module augmentation
        // between engagements) from being picked up.
        private static void One(
            WeaponBase weapon, string key, int stat, float atFull, float f)
        {
            float m = Mathf.Lerp(1f, atFull, f);

            if (Mathf.Abs(m - 1f) > 0.0001f)
                ModuleForgeWeaponStats.Set(weapon, stat, key, m);
            else
                ModuleForgeWeaponStats.Clear(weapon, stat, key);
        }

        public ModuleForgeRewards Copy()
        {
            return (ModuleForgeRewards)MemberwiseClone();
        }
    }
}
