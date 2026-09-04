using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ModuleForge
{
    // The hooks for a crit module, live ONLY when this mod owns the mechanic -
    // i.e. Weapon Forge is not installed. See ModuleForgeCritCompat for why
    // there can be exactly one owner.
    //
    // Every prefix here early-returns on two cheap tests before it does any
    // work: `OwnsPatches` (one latched bool) and `ModuleForgeCrit.Any` (another
    // one). That matters more than usual, because the damage prefix runs on
    // EVERY point of damage anything in the game takes.
    public static class ModuleForgeCritPatch
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("ModuleForge.Crit");

        // The shot currently delivering damage, so the damage prefix can reach
        // it for Roll.Shot. Set in a prefix and cleared in a FINALIZER on the
        // same method - Harmony guarantees the finalizer runs even if the
        // original throws, so a stale value can never leak into the next hit.
        // The same contract ModuleForgeKills uses for its credit.
        private static IProjectile _shot;

        // Only logged once: this sits on every point of damage in the game and
        // a per-hit line would bury the log.
        private static bool _saidNoImpact;

        private static bool Live
        {
            get
            {
                return ModuleForgeCritCompat.OwnsPatches && ModuleForgeCrit.Any;
            }
        }

        // ---- publish the shot ----------------------------------------------

        [HarmonyPatch(typeof(DamagableResource), "ProjectileCollided")]
        public class ShotScope
        {
            static void Prefix(IProjectile projectile)
            {
                if (Live)
                    _shot = projectile;
            }

            static void Finalizer()
            {
                _shot = null;
            }
        }

        // ---- the crit itself -----------------------------------------------

        // Priority.First for the same reason Weapon Forge's copy uses it: it
        // leaves room for anything that READS `amount` after us to see the crit
        // rather than the base hit. Nothing in this mod reads it today, so this
        // is about not having to remember later.
        [HarmonyPatch(typeof(DamagableResource), "Damage", new[] { typeof(float) })]
        [HarmonyPriority(Priority.First)]
        public class CritDamage
        {
            static void Prefix(DamagableResource __instance, ref float amount)
            {
                if (!Live || amount <= 0f)
                    return;

                try
                {
                    Apply(__instance, ref amount);
                }
                catch (Exception e)
                {
                    // A crit must never be the thing that stops damage landing.
                    Log.LogWarning("crit skipped: " + e.Message);
                }
            }
        }

        private static void Apply(DamagableResource victim, ref float amount)
        {
            // Which weapon is dealing this? ModuleForgeKills sets its credit in
            // a prefix on each of the three damage routes (projectile, beam,
            // explosion) and clears it in a finalizer - and it is NULL for a
            // burn tick or a cell collision, which is exactly the gate we want,
            // so damage-over-time never crits.
            WeaponBase weapon = ModuleForgeKills.Credit;

            if (weapon == null)
                return;

            float chance, multiplier;
            ModuleForgeCrit.Options options;

            if (!ModuleForgeCrit.Resolve(
                    weapon, out chance, out multiplier, out options))
                return;

            int stacks = Decide(options, weapon, chance);

            if (stacks <= 0)
                return;

            float factor = ModuleForgeCrit.Factor(stacks, multiplier);

            if (factor <= 1f)
                return;

            amount *= factor;

            ModuleForgeCrit.Feedback(options, weapon, victim, stacks);

            Projectile plain = _shot as Projectile;

            if (!options.impact)
                return;

            if (plain != null)
            {
                ModuleForgeCrit.BigImpact(
                    options, plain, victim.transform.position);
            }
            else if (!_saidNoImpact)
            {
                _saidNoImpact = true;
                Log.LogInfo(
                    "A crit module's \"impact\" cue is a bigger copy of the " +
                    "weapon's own impact burst, which only exists on a " +
                    "PROJECTILE hit - a beam's spark and an explosion's flash " +
                    "are not per-hit prefabs. This crit landed by another " +
                    "route, so use \"sfx\", \"color\" or \"shake\" to see " +
                    "those. Said once per run.");
            }
        }

        // Which roll mode, and what did it say?
        private static int Decide(
            ModuleForgeCrit.Options options, WeaponBase weapon, float chance)
        {
            if (options.roll == ModuleForgeCrit.Roll.Pull)
            {
                int pull;

                if (ModuleForgeCrit.TryGetPull(weapon, out pull))
                    return pull;

                // No pull on record - an explosion or a mine going off long
                // after the trigger. Roll rather than silently never critting.
                return ModuleForgeCrit.Stacks(chance, options.overflow);
            }

            if (options.roll == ModuleForgeCrit.Roll.Shot)
            {
                var plain = _shot as Component;

                if (plain != null)
                {
                    var mark = plain.gameObject
                        .GetComponent<ModuleForgeCrit.ShotMark>();

                    if (mark == null)
                    {
                        mark = plain.gameObject
                            .AddComponent<ModuleForgeCrit.ShotMark>();
                    }

                    if (!mark.rolled)
                    {
                        mark.rolled = true;
                        mark.stacks =
                            ModuleForgeCrit.Stacks(chance, options.overflow);
                    }

                    return mark.stacks;
                }

                // No shot to hang the decision on (a beam, an explosion). Per
                // hit is the only sane fallback.
                return ModuleForgeCrit.Stacks(chance, options.overflow);
            }

            return ModuleForgeCrit.Stacks(chance, options.overflow);
        }

        // ---- the per-pull roll ----------------------------------------------
        //
        // DoShoot is the one method provably on the path of every shot - the
        // shoot SFX and the muzzle flash both fire from it - and it covers
        // gadget weapons, which bypass Shooter entirely.
        [HarmonyPatch(typeof(WeaponBase), "DoShoot")]
        public class PullRoll
        {
            static void Prefix(WeaponBase __instance)
            {
                if (!Live || __instance == null)
                    return;

                try
                {
                    float chance, multiplier;
                    ModuleForgeCrit.Options options;

                    if (!ModuleForgeCrit.Resolve(
                            __instance, out chance, out multiplier,
                            out options))
                        return;

                    if (options.roll != ModuleForgeCrit.Roll.Pull)
                        return;

                    ModuleForgeCrit.RollPull(
                        __instance,
                        ModuleForgeCrit.Stacks(chance, options.overflow));
                }
                catch (Exception e)
                {
                    Log.LogWarning("crit pull roll skipped: " + e.Message);
                }
            }
        }
    }
}
