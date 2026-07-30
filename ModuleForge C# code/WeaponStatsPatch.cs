using System;
using System.Collections.Generic;
using BepInEx.Logging;
using HarmonyLib;

namespace ModuleForge
{
    // Adds module-driven lines to a weapon's stat card:
    //   "BURN FREQ +x/s" - total burn-tick-rate boost, on burning weapons.
    //   "PHASING ON"     - a phasing module is installed (projectile weapons).
    //   "PIERCE n"       - total pierce cap from pierce modules (projectiles).
    // These boosts are global (they affect all the player's projectiles), so
    // they show on any relevant weapon; a weapon they don't affect (e.g. a
    // non-burn weapon, or a laser for phasing/pierce) gets nothing, to avoid
    // implying an effect it doesn't have.
    //
    // WeaponBase.GetPropertyList is the single stat-list builder for both
    // the equipped weapon and the weapon-module preview (WeaponModule
    // delegates to baseWeapon.GetPropertyList), so one postfix covers both.
    [HarmonyPatch(typeof(WeaponBase), "GetPropertyList")]
    public class WeaponStatsPatch
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("ModuleForge");

        static void Postfix(WeaponBase __instance, List<DisplayableProperty> results)
        {
            if (results == null)
                return;

            try
            {
                // BURN FREQ - only on weapons that inflict burn.
                float delta = ModuleForgeBurn.Delta;
                if (delta > 0f && __instance.GetBurnWithAllAugmentations().Max > 0f)
                {
                    results.Add(new DisplayableProperty(
                        TextFormatter.ColoredText(TextFormatter.capsColor, "BURN FREQ"),
                        "+" + delta.ToString("0.##") + "/S"));
                }

                // PHASING / PIERCE - one COMBINED line each, summing this
                // mod's modules with the weapon's OWN baked WeaponForge value
                // (read via ForgeInterop). Module phasing/pierce apply to all
                // the player's PROJECTILES, so they only count on a projectile
                // weapon; a weapon's baked phasing can also be a hitscan/laser.
                WeaponData td = __instance.TemplateData;
                bool isProjectile = __instance is ProjectileWeapon;

                bool phasing =
                    (isProjectile && ModuleForgeProjectile.AnyPhasing) ||
                    ForgeInterop.WeaponBakedPhasing(td);
                if (phasing)
                {
                    results.Add(new DisplayableProperty(
                        TextFormatter.ColoredText(
                            TextFormatter.electronColor, "PHASING"),
                        "ON"));
                }

                int pierce =
                    (isProjectile ? ModuleForgeProjectile.PierceCapTotal : 0) +
                    ForgeInterop.WeaponBakedPierce(td);
                if (pierce > 0)
                {
                    results.Add(new DisplayableProperty(
                        TextFormatter.ColoredText(
                            TextFormatter.capsColor, "PIERCE"),
                        pierce.ToString()));
                }
            }
            catch (Exception e)
            {
                Log.LogError("Weapon stat card failed: " + e);
            }
        }
    }
}
