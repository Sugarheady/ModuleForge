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
    //
    // ONE LIST, TWO ENTRY POINTS. This held two copies of the list, byte for
    // byte, one per patch - and anything added to a duplicated list is one
    // paste away from being half-wired, which then fails only on the entry
    // point nobody tested. (Weapon Forge's twin had exactly that shape and was
    // deduplicated on 2026-09-03.) Add to `ResetAll` and both paths get it.
    public static class BurnResetPatch
    {
        private static void ResetAll()
        {
            ModuleForgeBurn.Reset();
            ModuleForgeProjectile.Reset();
            ModuleForgeCrit.Reset();
            ModuleForgeLeech.Reset();

            // Both hold live `WeaponBase` keys plus contributions from modules
            // that are NOT uninstalled on teardown, so without this a run's
            // worth of dead entries carries into the next one forever - and an
            // execute keyed to a weapon that no longer exists is the shape that
            // fires on something it should not. The live growth components need
            // no sweep: they are MonoBehaviours on projectiles and die with the
            // scene for free.
            ModuleForgeExecute.Reset();
            ModuleForgeGrow.Reset();

            // Holds `Unit.Data` keys from modules that are not uninstalled on
            // teardown. Its controllers are MonoBehaviours and die with the
            // scene for free, but a stale armed entry would have the reconciler
            // re-attach one to the next run's ship - a spiked hull nobody
            // installed, which is precisely the bug the Weapon Forge twin had
            // arriving by a different road.
            ModuleForgeContact.Reset();

            // The slow registry, the cross-mod config mirrors - and, unlike
            // everything else on this list, a call that reaches OUTSIDE this
            // mod's own state: `ModuleForgeSlow.Reset` also removes any live
            // `TimeManager` modifier we own. A run that begins while a hit-stop
            // is still running would otherwise start in slow motion, and a
            // modifier with a non-positive duration never expires at all. The
            // twin makes the same call from its own run reset.
            ModuleForgeSlow.Reset();

            // The windup registry and its once-per-weapon gadget warning. The
            // meters are MonoBehaviours on Shooters and die with the scene for
            // free; the WARNING gate is the part that needs the seam, because
            // it describes a per-RUN situation (this loadout, this grid) while
            // a one-shot bool is per session - the trap the diagnostic sweep
            // exists for.
            ModuleForgeWindup.Reset();

            // Holds live `WeaponBase` keys, which do not survive a run.
            ModuleForgeWeaponStats.Reset();

            // The gas clouds' live list, their per-target claim table (keyed by
            // HealthBase objects that a teardown destroys) and the `Crediting`
            // latch, which a teardown landing mid-credit could otherwise leave
            // true for the rest of the session and silently disable every
            // chain. The CONFIGS are deliberately not touched - those are built
            // once from the module files and clearing them would unbuild every
            // gas module until the game is restarted.
            ModuleForgeGasCloud.Reset();

            // Holds the installed effects the connected-and-powered gate is
            // watching. Modules are not uninstalled on teardown (the whole
            // reason this file exists), so these would otherwise pile up one
            // run's worth at a time. An untracked effect is simply ungated,
            // which is the safe state, and every module re-tracks as it
            // installs after this runs.
            ModuleForgeLive.Reset();

            // The once-only log gates. Without this the second run of a
            // session reports none of its own warnings - so the run where you
            // go looking for a log line is the run that cannot produce it.
            ModuleForgeDiagnosticGates.Reset();

            // Only a FAILED cross-mod lookup, and only so it is asked again
            // rather than answered once and forever from whenever it first
            // happened to be asked.
            ForgeInterop.ResetHeatLookup();
            ForgeInterop.ResetOwnerLookup();
        }

        [HarmonyPatch(typeof(RunData), "Initialize")]
        public class OnNewRun
        {
            static void Prefix()
            {
                ResetAll();
            }
        }

        [HarmonyPatch(typeof(Punk.SaveLoad.GameSaver), "Load")]
        public class OnContinue
        {
            static void Prefix()
            {
                ResetAll();
            }
        }
    }
}
