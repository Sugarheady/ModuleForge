using System;
using System.Collections.Generic;
using BepInEx.Logging;
using HarmonyLib;

namespace ModuleForge
{
    // Adds a "BURN FREQ +x/s" line to a weapon's stat card showing the
    // total active burn-tick-rate boost (sum of every equipped burn-rate
    // module). The boost is global, so it's shown on any weapon that
    // actually inflicts burn - a non-burn weapon gets nothing, to avoid
    // implying it burns.
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
            try
            {
                float delta = ModuleForgeBurn.Delta;
                if (delta <= 0f || results == null)
                    return;

                // Only on weapons that inflict burn (base or augmented).
                if (__instance.GetBurnWithAllAugmentations().Max <= 0f)
                    return;

                string label = TextFormatter.ColoredText(
                    TextFormatter.capsColor, "BURN FREQ");

                results.Add(new DisplayableProperty(
                    label, "+" + delta.ToString("0.##") + "/S"));
            }
            catch (Exception e)
            {
                Log.LogError("Weapon burn-freq stat failed: " + e);
            }
        }
    }
}
