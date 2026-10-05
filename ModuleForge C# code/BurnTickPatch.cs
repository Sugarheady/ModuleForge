using System;
using BepInEx.Logging;
using HarmonyLib;

namespace ModuleForge
{
    // Just before the game runs a unit's burn tick check, rewrite that
    // unit's fireTickRate to the boosted interval (only while a burn-rate
    // module is equipped). The game's own Update then does the tick and
    // cooling with our value. ModuleForgeBurn caches each unit's original
    // interval, gates out the player, and clamps the max rate.
    //
    // The game resets burnProperties from the prefab on spawn/continue, so
    // re-applying every frame here (rather than stamping once) is exactly
    // what keeps the boost correct across reloads.
    [HarmonyPatch(typeof(DamagableResource), "Update")]
    public class BurnTickPatch
    {
        private static readonly ManualLogSource Log =
            ModuleForgeLog.Source("ModuleForge");

        static void Prefix(DamagableResource __instance)
        {
            // Skip all work until a booster has actually been used this
            // session; keep running afterwards to restore values when the
            // boost is removed.
            bool burn =
                ModuleForgeBurn.Delta > 0f || ModuleForgeBurn.EverModified;

            // A FIRE-RESISTANCE module needs the same per-frame treatment, and
            // for the same reason spelled out at the top of this file: the game
            // rebuilds `burnProperties` from the prefab on spawn and on
            // continue, so a value stamped once at install is silently reverted.
            // Both gates are plain bool reads, so a run with neither costs
            // nothing.
            bool resist = ModuleForgeShipStats.AnyBurnOwned;

            if (!burn && !resist)
                return;

            try
            {
                // DamagableResource is [RequireComponent(typeof(Unit))], so
                // the Unit is on the same GameObject.
                Unit unit = __instance.GetComponent<Unit>();
                if (unit == null)
                    return;

                // ORDER MATTERS. Resistance re-asserts the values it owns
                // first; the burn engine then layers its tick-rate boost on
                // top, reading the resisted value as its base. Reversed, the
                // boost would be computed against a number that was about to
                // change.
                if (resist)
                    ModuleForgeShipStats.ReassertBurn(unit.ComponentData);

                if (burn)
                    ModuleForgeBurn.ApplyTo(unit.ComponentData);
            }
            catch (Exception e)
            {
                Log.LogError("Burn tick patch failed: " + e);
            }
        }
    }
}
