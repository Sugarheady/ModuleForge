using System;
using BepInEx.Logging;
using HarmonyLib;

namespace ModuleForge
{
    // Finishes a victim an execute module has brought below its threshold.
    // See ModuleForgeExecute for the seam, why the kill credits itself, and why
    // this one deliberately does NOT stand down when Weapon Forge is installed.
    public static class ModuleForgeExecutePatch
    {
        private static readonly ManualLogSource Log =
            ModuleForgeLog.Source("ModuleForge.Execute");

        // Carried prefix -> postfix through Harmony's `__state`, NEVER a static.
        //
        // `Damage` NESTS: it can call `Die()`, whose spawn-on-death explosion
        // reaches `OnExplosion` and back into `Damage` before the outer call has
        // returned. A static would be overwritten by the inner hit and the outer
        // one would measure the wrong tank. `__state` is per-invocation and
        // cannot be. Same reasoning as ModuleForgeLeechPatch, which shares this
        // method.
        private class Scope
        {
            public WeaponBase weapon;
            public float before;
        }

        [HarmonyPatch(typeof(DamagableResource), "Damage", new[] { typeof(float) })]
        public class OnDamage
        {
            static void Prefix(
                DamagableResource __instance, float amount, ref object __state)
            {
                __state = null;

                if (!ModuleForgeExecute.Any || __instance == null || amount <= 0f)
                    return;

                try
                {
                    // GUARD 1 - NO WEAPON BEHIND THIS DAMAGE.
                    //
                    // `ModuleForgeKills.Credit` is set in a prefix on each of
                    // the three real damage routes (`ProjectileCollided`,
                    // `OnHitByHitscanWeapon`, `OnExplosion`) and cleared after,
                    // so it is live exactly when a weapon is responsible. A
                    // burn tick or a cell collision reaches `Damage` by neither
                    // route and reads null, so it can never execute - that
                    // falls out rather than being a rule, and it is the same
                    // property `ModuleForgeCritPatch` relies on.
                    //
                    // **It is also what keeps an execute module off its own gas
                    // cloud's ticks.** `ModuleForgeGasCloud` calls
                    // `hb.TakeDamage(...)` directly, which is not one of the
                    // three routes, so `Credit` is null throughout - the cloud
                    // sets it only around its `RegisterKill`, long after this.
                    // So gas damage can kill but can never *execute*, which is
                    // the right answer: a threshold meant for the gun you are
                    // aiming should not fire on a cloud sitting on a corpse.
                    WeaponBase source = ModuleForgeKills.Credit;

                    if (source == null)
                        return;

                    // GUARD 2 - ALREADY DEAD, OR INVINCIBLE.
                    if (__instance.IsDead || __instance.IsInvincible)
                        return;

                    __state = new Scope
                    {
                        weapon = source,
                        before = __instance.CurrentHealth
                    };
                }
                catch (Exception e)
                {
                    Log.LogError("Execute prefix failed: " + e);
                }
            }

            static void Postfix(DamagableResource __instance, object __state)
            {
                var scope = __state as Scope;

                if (scope == null || __instance == null)
                    return;

                try
                {
                    // GUARD 2 AGAIN, and it is doing real work rather than
                    // being paranoia. The game's own death branch
                    // (`if (!IsDead && tank.Value <= 0f) Die();`) runs INSIDE
                    // Damage and returns early - but **a postfix still runs
                    // after an early return**, so without this a shot that
                    // already killed something would "execute" the corpse.
                    //
                    // It is also what makes two mods' executes idempotent: with
                    // Weapon Forge installed, whichever postfix Harmony ordered
                    // second finds IsDead true and stops here.
                    if (__instance.IsDead || __instance.IsInvincible)
                        return;

                    float now = __instance.CurrentHealth;

                    // GUARD 3 - THE HIT HAS TO HAVE LANDED.
                    //
                    // `Damage` refuses damage three silent ways - a damage
                    // blocker, no tank of that damage type, or i-frames - and on
                    // any of them the tank is untouched. Without this test a
                    // module could finish something it never hurt, which reads
                    // as the threshold firing at random. The middle case is not
                    // a corner: it is how the damage-type table lets a Crawler
                    // resist nearly everything.
                    if (now >= scope.before)
                        return;

                    float max = __instance.MaxHealth;

                    // GUARD 4 - NO TANK OF THIS RESOURCE. **The dangerous one.**
                    //
                    // MaxHealth is 0 when the unit has no tank of this type, and
                    // CurrentHealth reads 0 too - which looks exactly like
                    // "already nearly dead" and would execute everything the
                    // module touched.
                    if (max <= 0f)
                        return;

                    ModuleForgeExecute.Options fired;

                    if (!ModuleForgeExecute.ShouldFinish(
                            scope.weapon, now, max, out fired))
                    {
                        return;
                    }

                    Unit victim = __instance.GetComponentInParent<Unit>();
                    Unit owner = (scope.weapon != null) ? scope.weapon.Owner : null;

                    // GUARD 5 - NEVER YOURSELF, NEVER YOUR OWN SIDE.
                    //
                    // A minion of yours caught in your own blast must not be
                    // finished by your own module. `IsFriendsWith` is the game's
                    // own question, the same one the targeting finders ask.
                    if (victim != null && owner != null)
                    {
                        if (ReferenceEquals(victim, owner) ||
                            owner.IsFriendsWith(victim))
                        {
                            return;
                        }
                    }

                    ModuleForgeExecute.Feedback(fired, __instance);

                    // Through the game's OWN Die(), so `IsDead` is set before
                    // control returns to ProjectileCollided /
                    // OnHitByHitscanWeapon / OnExplosion - each of which then
                    // credits the kill itself. That is what makes on-kill
                    // rewards, leech and the music controller all see it.
                    __instance.Die();
                }
                catch (Exception e)
                {
                    Log.LogError("Execute postfix failed: " + e);
                }
            }
        }
    }
}
