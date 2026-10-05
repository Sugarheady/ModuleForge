using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ModuleForge
{
    // The hook for a leech module, live ONLY when this mod owns the mechanic -
    // i.e. Weapon Forge is not installed. See ModuleForgeLeechCompat.
    //
    // One prefix/postfix pair on `DamagableResource.Damage(float)` covers
    // projectiles, lobbed shots, beams, explosions and missiles at once, because
    // every damage route in the game funnels into that one method.
    //
    // WHY A PREFIX AND A POSTFIX rather than just reading the damage: `Damage`
    // can refuse the hit three silent ways (invincibility, no tank of that
    // resource, i-frames), so the only honest measure of what a hit ACHIEVED is
    // the victim's tank before and after. See ModuleForgeLeech's header.
    public static class ModuleForgeLeechPatch
    {
        private static readonly ManualLogSource Log =
            ModuleForgeLog.Source("ModuleForge.Leech");

        // Carried prefix -> postfix through Harmony's __state, NOT a static.
        // `Damage` NESTS: it can call `Die()`, whose spawn-on-death explosion
        // reaches `OnExplosion` and back into `Damage` before the outer call
        // returns. A static snapshot would be overwritten by the inner hit and
        // the outer one would measure the wrong delta.
        private class Scope
        {
            public WeaponBase weapon;
            public float fraction;
            public float flat;
            public float cap;
            public float perEnemyDelay;
            public Resource resource;
            public ModuleForgeLeech.Options options;
            public ResourceTank tank;
            public Unit victim;
            public float before;
        }

        private static bool Live
        {
            get
            {
                return ModuleForgeLeechCompat.OwnsPatches &&
                       ModuleForgeLeech.Any;
            }
        }

        [HarmonyPatch(typeof(DamagableResource), "Damage", new[] { typeof(float) })]
        public class LeechDamage
        {
            static void Prefix(
                DamagableResource __instance, float amount, ref object __state)
            {
                __state = null;

                if (!Live || amount <= 0f)
                    return;

                try
                {
                    __state = Open(__instance);
                }
                catch (Exception e)
                {
                    Log.LogWarning("leech snapshot skipped: " + e.Message);
                }
            }

            static void Postfix(DamagableResource __instance, object __state)
            {
                var scope = __state as Scope;

                if (scope == null)
                    return;

                try
                {
                    Close(__instance, scope);
                }
                catch (Exception e)
                {
                    // A leech must never be the thing that breaks damage.
                    Log.LogWarning("leech skipped: " + e.Message);
                }
            }
        }

        private static Scope Open(DamagableResource victim)
        {
            // ModuleForgeKills sets its credit in a prefix on each of the three
            // damage routes and clears it in a finalizer - and it is NULL for a
            // burn tick or a cell collision, which is exactly the gate we want.
            WeaponBase weapon = ModuleForgeKills.Credit;

            if (weapon == null)
                return null;

            float fraction, flat, cap, perEnemyDelay;
            Resource resource;
            ModuleForgeLeech.Options options;

            if (!ModuleForgeLeech.Resolve(
                    weapon, out fraction, out flat, out cap,
                    out perEnemyDelay, out resource, out options))
            {
                return null;
            }

            // You cannot leech off yourself.
            Unit owner = weapon.Owner;

            if (owner == null)
                return null;

            Unit hurt = victim.GetComponentInParent<Unit>();

            // ★★ AND YOU CANNOT LEECH OFF YOUR OWN SIDE EITHER. The hull test
            // above was right and too narrow - audit one mod, audit its twin:
            // Weapon Forge's `ForgeDamageDealt.Open` had the identical guard
            // and the identical hole. A minion, a charmed unit or a wingman
            // drone body is not `owner`, so damage to one was measured as
            // damage dealt and paid for: deploy, shoot your own companions,
            // heal - a straight resource-to-health converter with no cap.
            //
            // > A guard written for the exceptional path is not a guard for the
            // > ordinary one. "Your own hull" is the exceptional self-hit;
            // > "anything on your side" is the ordinary one.
            //
            // The hull test is KEPT as well as widened: self-friendship is a
            // property of the faction asset and only the Player one was checked
            // (`Player.asset` ships `allies: [ {fileID: 11400000} ]`, a
            // self-reference), so an enemy faction that does not list itself
            // must not start leeching off its own hull.
            if (hurt == null || hurt == owner || owner.IsFriendsWith(hurt))
                return null;

            // `DamagableResource.Tank` resolves through Unit.GetTank, which is
            // not guarded - ask HasTank first, exactly as Damage() does before
            // touching the tank.
            if (!hurt.HasTank(victim.resource))
                return null;

            ResourceTank tank = hurt.GetTank(victim.resource);

            if (tank == null)
                return null;

            return new Scope
            {
                weapon = weapon,
                fraction = fraction,
                flat = flat,
                cap = cap,
                perEnemyDelay = perEnemyDelay,
                resource = resource,
                options = options,
                tank = tank,
                victim = hurt,
                before = tank.Value
            };
        }

        private static void Close(DamagableResource victim, Scope scope)
        {
            if (scope.tank == null)
                return;

            float dealt = scope.before - scope.tank.Value;

            // Nothing landed: blocked, i-framed, or the wrong element entirely.
            if (dealt <= 0f)
                return;

            // OVERKILL DOES NOT PAY. `ResourceTank.Value` has no floor - the
            // game writes `tank.Value -= amount` - so a 200-damage rocket into a
            // 5-health fly leaves the tank at -195 and the raw delta reads 200.
            if (scope.before > 0f)
                dealt = Mathf.Min(dealt, scope.before);

            if (!ModuleForgeLeech.EnemyReady(
                    scope.weapon, scope.victim, scope.perEnemyDelay))
            {
                return;
            }

            float wanted = dealt * scope.fraction + scope.flat;

            if (wanted <= 0f)
                return;

            float allowed = ModuleForgeLeech.Take(
                scope.weapon, wanted, scope.cap);

            if (allowed <= 0f)
                return;

            Unit player = scope.weapon.Owner;

            Resource resource = (scope.resource != null)
                ? scope.resource
                : ModuleForgeLeech.HealthOf(player);

            if (resource == null)
                return;

            ModuleForgeLeech.Options options = scope.options;

            bool onArrival =
                options.EffectiveHealOn == ModuleForgeLeech.HealOn.Arrival;

            // The orb, when there is room. Over the cap the healing is granted
            // immediately rather than dropped: a firehose should cost the
            // flourish, never the health.
            bool flew = false;

            if (options.orb && ModuleForgeLeech.ClaimOrb(options.maxOrbs))
            {
                var orb = ModuleForgeLeechOrb.Spawn(
                    victim.transform.position,
                    player,
                    options,
                    resource,
                    onArrival ? allowed : 0f,
                    victim);

                if (orb != null)
                    flew = true;
                else
                    ModuleForgeLeech.ReleaseOrb();
            }

            if (!flew || !onArrival)
            {
                ModuleForgeLeech.Heal(player, resource, allowed);

                // With no orb to carry it the sound belongs to the hit; when an
                // orb IS flying the orb plays it on arrival, so the cue lands
                // with the health rather than ahead of it.
                if (!flew && !string.IsNullOrEmpty(options.sfxGuid))
                {
                    try
                    {
                        AudioManager.PlaySfx(
                            options.sfxGuid, victim.transform.position);
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        }
    }
}
