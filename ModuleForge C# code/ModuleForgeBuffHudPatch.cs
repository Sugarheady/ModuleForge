using System;
using BepInEx.Logging;
using HarmonyLib;

namespace ModuleForge
{
    // Catches the live ship HUD so ModuleForgeBuffHud has something to draw into.
    //
    // ShipHud.AssignShip is the right hook and not merely a convenient one: it is
    // where the game wires up every part of the HUD that needs the ship,
    // including ShipLogDisplay.Assign - which is what subscribes to the log
    // events. By the time this postfix runs the log has a subscriber, and
    // "has a subscriber" is not a detail: ShipLogOutput.Clear invokes LogRemoved
    // with no null check.
    //
    // Both mods patching the same method is fine - Harmony composes postfixes,
    // and each mod only ever binds its own HUD state.
    public static class ModuleForgeBuffHudPatch
    {
        private static readonly ManualLogSource Log =
            ModuleForgeLog.Source("ModuleForge.BuffHud");

        [HarmonyPatch(typeof(ShipHud), "AssignShip")]
        public class OnAssignShip
        {
            static void Postfix(ShipHud __instance, Ship ship)
            {
                try
                {
                    if (ship != null)
                        ModuleForgeBuffHud.Bind(__instance, ship);

                    // Both mods are certainly loaded by now, which is not true
                    // at plugin-load time - this mod loads FIRST.
                    ModuleForgePlugin.CheckIndicatorMismatch();

                    // Resolve who owns CRIT here too, for the same reason and
                    // one of its own: that check is lazy, so without this the
                    // "who owns it" line would not appear until something in
                    // the world took damage - which makes it useless as the
                    // first thing to look for in a log. It also gets the
                    // reflection out of the way before combat rather than on
                    // the first hit. Reading the property is the whole call.
                    bool ignored = ModuleForgeCritCompat.OwnsPatches;
                    bool ignoredToo = ModuleForgeLeechCompat.OwnsPatches;
                }
                catch (Exception e)
                {
                    Log.LogError("Binding the buff HUD failed: " + e);
                }
            }
        }

        // NOTE: deliberately no ShipHud.OnDestroy hook - **ShipHud does not
        // declare one**, and Harmony throws at PatchAll for a missing method,
        // which would take the whole mod down rather than just this feature.
        // It is not needed: Unity reports a destroyed object as null, so
        // FeedReady stops being true on its own and the next AssignShip rebinds.
    }
}
