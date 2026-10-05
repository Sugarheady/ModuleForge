using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ModuleForge
{
    // Fires "slow" and "timeSlow" at the moment one of your weapons connects.
    //
    // WHY THESE THREE HOOKS AND NOT `Damage`. "On hit" is three different code
    // paths in this game and they arrive with different information: a
    // projectile knows its own weapon, a hitscan beam is handed the weapon
    // directly, and an explosion is a struct with no route back to anything.
    // `ModuleForgeKills` already solved all three - including the awkward one,
    // where the only place to learn the weapon is while the projectile that
    // owns the explosion is still on the stack - so this reads its answer
    // rather than building a fourth copy of the same plumbing.
    //
    // `execute` hooks `Damage(float)` instead because it needs the health
    // DELTA. A slow needs only "a hit landed", and `Damage` NESTS (a death
    // explosion re-enters it before the outer call returns), so hooking the
    // routes means one slow per hit rather than one per nested damage call.
    //
    // THESE ARE POSTFIXES ON PURPOSE. `ModuleForgeKills` sets `Credit` in a
    // prefix on these same three methods and clears it in a finalizer, and
    // Harmony runs prefixes, then the original, then postfixes, then
    // finalizers. A postfix is inside that window; a prefix would be racing it.
    //
    // Postfix also means the damage has resolved, so `victim.IsDead` is
    // meaningful and an `onKill` trigger is free.
    //
    // ★ A REFUSED HIT STILL SLOWS, AND THAT IS DELIBERATE PARITY RATHER THAN AN
    // OVERSIGHT. `Damage` refuses damage three silent ways (a damage blocker,
    // no tank of that type, i-frames), and a postfix on these routes cannot
    // tell. Weapon Forge's `slow` has always behaved this way, and the two mods
    // bridge into one engine - so a divergence here would mean the same JSON
    // behaving differently depending on which mods are installed, which is a
    // worse bug than the one it fixes. "The shot connected" is a defensible
    // reading; `execute` needs the stricter one and pays for it with a
    // prefix/postfix pair.
    public static class ModuleForgeSlowPatch
    {
        private static readonly ManualLogSource Log =
            ModuleForgeLog.Source("ModuleForge.Slow");

        [HarmonyPatch(typeof(DamagableResource), "ProjectileCollided")]
        public class OnProjectile
        {
            static void Postfix(DamagableResource __instance)
            {
                Hit(__instance);
            }
        }

        [HarmonyPatch(typeof(DamagableResource), "OnHitByHitscanWeapon")]
        public class OnHitscan
        {
            static void Postfix(DamagableResource __instance)
            {
                Hit(__instance);
            }
        }

        [HarmonyPatch(typeof(DamagableResource), "OnExplosion")]
        public class OnExplosion
        {
            static void Postfix(DamagableResource __instance)
            {
                Hit(__instance);
            }
        }

        private static void Hit(DamagableResource victim)
        {
            if (!ModuleForgeSlow.Any || victim == null)
                return;

            try
            {
                // NO WEAPON BEHIND THIS DAMAGE, NO SLOW. `Credit` is live for
                // the whole of all three routes and null for a burn tick, a
                // cell collision or a gas cloud - so damage over time can never
                // slow, which falls out rather than being a rule. It is also
                // how a weapon-grid module knows the hit was its own gun's.
                WeaponBase source = ModuleForgeKills.Credit;

                if (source == null)
                    return;

                bool dead = victim.IsDead;

                ModuleForgeSlow.Options timing;

                // onKill and onHit are independent, so a module can do a small
                // debuff on every hit and a big time-bend only on a kill.
                if (ModuleForgeSlow.ResolveTime(source, out timing) &&
                    (dead ? timing.onKill : timing.onHit))
                {
                    ModuleForgeSlow.BendTime(timing);
                }

                // No point debuffing something that just died, and its
                // rigidbody may already be on its way out.
                if (dead)
                    return;

                ModuleForgeSlow.Options debuff;

                if (!ModuleForgeSlow.ResolveSlow(source, out debuff) ||
                    !debuff.onHit)
                {
                    return;
                }

                Slow(victim, source, debuff);
            }
            catch (Exception e)
            {
                Log.LogError("Slow-on-hit failed: " + e);
            }
        }

        private static void Slow(
            DamagableResource victim, WeaponBase source,
            ModuleForgeSlow.Options o)
        {
            // DamagableResource keeps its own Unit reference private, and on
            // several prefabs health sits on a child - so walk up.
            Unit unit = victim.GetComponentInParent<Unit>();

            if (unit == null)
                return;

            Unit owner = source.Owner;

            // NEVER YOURSELF, NEVER YOUR OWN SIDE.
            //
            // The self case is reachable in practice: explosions in this game
            // carry no owner filter at all, so your own blast reaches you like
            // anyone else's. The friendly case is a MODULE problem specifically
            // - a card on the ship grid applies to every gun you carry,
            // including the wide ones, and a minion or a wingman caught in your
            // own blast should not be crippled by your own module.
            // `IsFriendsWith` is the game's own question, the same one the
            // targeting finders and `ModuleForgeExecutePatch` ask.
            //
            // Worth knowing that the movement half would have been a no-op on
            // the player anyway - `ShipMovement` rewrites `rb.linearDamping`
            // every frame in three branches, so a damping-based slow cannot
            // stick to the ship. The MASS and FIRE RATE halves would have
            // landed, which is exactly the half-working shape that costs a
            // round to work out.
            if (owner != null)
            {
                if (ReferenceEquals(unit, owner) || owner.IsFriendsWith(unit))
                    return;
            }

            ModuleForgeSlow.Apply(unit, o);
        }
    }

    // A slowed enemy's SHOTS come out slow too.
    //
    // WHY THIS HOOKS `Shoot` AND NOT THE PROJECTILE IN FLIGHT. Scaling a live
    // bullet's Velocity looks like the obvious move and is wrong twice over.
    // `Projectile.FixedUpdate` rebuilds Velocity from the private `startSpeed`
    // whenever `rangeData.slowDown` is on - the most-copied template in the
    // game ships that - so the change would be undone on the next physics step.
    // And `HandleRange` compares elapsed time against a `timeToReachRange`
    // computed at Shoot from the ORIGINAL speed, so a bullet slowed afterwards
    // would despawn well short of its range for no visible reason.
    //
    // `Shoot()` derives all three of those, so a prefix that scales Velocity
    // first gets every one of them consistent for free.
    //
    // Shots ALREADY in the air when the slow lands keep their speed. That is a
    // deliberate line rather than a limitation: a bullet is a thing that has
    // left, and reaching back into flight is where both traps above live.
    //
    // ★ GATED ON OWNING THE DEBUFF. With Weapon Forge installed there is no
    // `ModuleForgeSlowed` component to read - its `ForgeSlowed` holds the
    // debuff and its own shot patch does this half - so the check saves a
    // GetComponent per shot AND states out loud that the two cannot both scale.
    public static class ModuleForgeSlowShotPatch
    {
        private static readonly ManualLogSource Log =
            ModuleForgeLog.Source("ModuleForge.Slow");

        private static bool Live
        {
            get
            {
                return ModuleForgeSlow.Any && ModuleForgeSlowCompat.OwnsDebuff;
            }
        }

        [HarmonyPatch(typeof(Projectile), "Shoot")]
        public class OnPlainShot
        {
            static void Prefix(Projectile __instance)
            {
                if (!Live || __instance == null)
                    return;

                try
                {
                    Slow(__instance.Owner, __instance);
                }
                catch (Exception e)
                {
                    Log.LogError("Slowing a shot failed: " + e);
                }
            }
        }

        [HarmonyPatch(typeof(PhysicsProjectile), "Shoot")]
        public class OnLobbedShot
        {
            static void Prefix(PhysicsProjectile __instance)
            {
                if (!Live || __instance == null)
                    return;

                try
                {
                    // `PhysicsProjectile.Shoot` is called TWICE and the first
                    // call is against a BLANK - no Owner, no velocity, because
                    // the early call sits inside the `if (UsePhysics)` branch
                    // before Damage, Owner and Velocity are assigned. The test
                    // is both together: either alone would reject a legitimate
                    // speed-0 dropped mine.
                    if (__instance.Owner == null &&
                        __instance.Velocity.sqrMagnitude <= 0.000001f)
                    {
                        return;
                    }

                    Slow(__instance.Owner, __instance);
                }
                catch (Exception e)
                {
                    Log.LogError("Slowing a lobbed shot failed: " + e);
                }
            }
        }

        private static void Slow(Unit owner, IProjectile shot)
        {
            if (owner == null || shot == null)
                return;

            float f = ModuleForgeSlowed.FactorFor(owner);

            if (f >= 0.999f)
                return;

            shot.Velocity = shot.Velocity * f;
        }
    }
}
