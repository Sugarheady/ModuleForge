using HarmonyLib;

namespace ModuleForge
{
    // Modules are NOT uninstalled when the ship is destroyed (death, or a
    // scene teardown on quit-to-menu), so a running total of burn-rate
    // boosts would otherwise leak into the next run. Reset at the two
    // run-entry points, each of which runs BEFORE that run's ship modules
    // install and re-register the boost:
    //
    //   new run  -> RunData.Initialize (ship spawns afterwards)
    //   continue -> GameSaver.Load, whose LoadEntities pass restores the
    //               ship and fires OnInstalled AFTER Load begins; reset in
    //               a Load prefix so the boost rebuilds cleanly. (Resetting
    //               on RunData.RestoreFromMemento would be too late - it
    //               runs after LoadEntities, GameSaver.Load lines 121-122.)
    public static class BurnResetPatch
    {
        [HarmonyPatch(typeof(RunData), "Initialize")]
        public class OnNewRun
        {
            static void Prefix()
            {
                ModuleForgeBurn.Reset();
                ModuleForgeProjectile.Reset();
            }
        }

        [HarmonyPatch(typeof(Punk.SaveLoad.GameSaver), "Load")]
        public class OnContinue
        {
            static void Prefix()
            {
                ModuleForgeBurn.Reset();
                ModuleForgeProjectile.Reset();
            }
        }
    }
}
