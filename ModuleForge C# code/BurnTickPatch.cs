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
            BepInEx.Logging.Logger.CreateLogSource("ModuleForge");

        static void Prefix(DamagableResource __instance)
        {
            // Skip all work until a booster has actually been used this
            // session; keep running afterwards to restore values when the
            // boost is removed.
            if (ModuleForgeBurn.Delta <= 0f && !ModuleForgeBurn.EverModified)
                return;

            try
            {
                // DamagableResource is [RequireComponent(typeof(Unit))], so
                // the Unit is on the same GameObject.
                Unit unit = __instance.GetComponent<Unit>();
                if (unit == null)
                    return;

                ModuleForgeBurn.ApplyTo(unit.ComponentData);
            }
            catch (Exception e)
            {
                Log.LogError("Burn tick patch failed: " + e);
            }
        }
    }
}
