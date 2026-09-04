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

                // Float: a module's cap can be fractional (1.5 = a second
                // pierce half the time), so rounding here would hide what
                // the player actually has.
                float pierce =
                    (isProjectile ? ModuleForgeProjectile.PierceCapTotal : 0f) +
                    ForgeInterop.WeaponBakedPierce(td);
                if (pierce > 0f)
                {
                    results.Add(new DisplayableProperty(
                        TextFormatter.ColoredText(
                            TextFormatter.capsColor, "PIERCE"),
                        pierce.ToString("0.##")));
                }

                // CRIT - ONE line, from whichever mod owns the mechanic. Not a
                // sum of two halves like PIERCE above: with Weapon Forge
                // installed, this mod's crit modules have already pushed their
                // chance into that mod's engine, so its total already contains
                // them and adding ours again would double-count.
                float critChance, critMultiplier;

                bool hasCrit = ForgeInterop.WeaponCritTotal(
                    __instance, out critChance, out critMultiplier);

                if (!hasCrit && !ForgeInterop.HasWeaponCrit)
                {
                    ModuleForgeCrit.Options ignored;

                    hasCrit = ModuleForgeCrit.Resolve(
                        __instance, out critChance, out critMultiplier,
                        out ignored);
                }

                if (hasCrit)
                {
                    results.Add(new DisplayableProperty(
                        TextFormatter.ColoredText(
                            TextFormatter.capsColor, "CRIT"),
                        (critChance * 100f).ToString("0.#") + "% X" +
                        critMultiplier.ToString("0.##")));
                }

                // LEECH - one line, from whichever mod owns the mechanic, for
                // the same reason as CRIT above.
                float share, flatHeal;

                bool hasLeech = ForgeInterop.WeaponLeechTotal(
                    __instance, out share, out flatHeal);

                if (!hasLeech && !ForgeInterop.HasWeaponLeech)
                {
                    float cap, delay;
                    Resource pool;
                    ModuleForgeLeech.Options ignoredOptions;

                    hasLeech = ModuleForgeLeech.Resolve(
                        __instance, out share, out flatHeal, out cap,
                        out delay, out pool, out ignoredOptions);
                }

                if (hasLeech)
                {
                    string text = (share > 0f)
                        ? (share * 100f).ToString("0.#") + "%"
                        : "";

                    if (flatHeal > 0f)
                    {
                        text += (text.Length > 0 ? " +" : "+") +
                                flatHeal.ToString("0.##");
                    }

                    results.Add(new DisplayableProperty(
                        TextFormatter.ColoredText(
                            TextFormatter.capsColor, "LEECH"),
                        text));
                }
            }
            catch (Exception e)
            {
                Log.LogError("Weapon stat card failed: " + e);
            }
        }
    }
}
