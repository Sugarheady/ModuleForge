using System;
using System.Collections.Generic;
using BepInEx.Logging;
using HarmonyLib;

namespace ModuleForge
{
    // "IS THIS CARD ACTUALLY DOING ANYTHING?" - the connected-and-powered gate.
    //
    // His 2026-09-11 ask, in his own words: *"The modules in general need to be
    // powered and connected to the grid to work otherwise they dont do
    // anything."* He is describing the game's rule, and this mod was not
    // keeping it.
    //
    // THE GAME'S RULE, read off the decompile rather than assumed:
    //
    //   ModuleGrid.OnUpdate           -> cluster.ConnectedAndPoweredModules
    //   ModuleGrid.OnRecalculateStats -> cluster.ConnectedAndPoweredModules
    //   WeaponFactory.Create          -> cluster.ConnectedAndPoweredModules
    //
    // ...but:
    //
    //   ModuleGrid.Install -> ModuleInstalled -> module.OnInstalled(unit)
    //
    // fires for ANY module dropped anywhere in the grid, powered or not,
    // connected or not. So an effect whose whole job is done inside one of the
    // gated seams is correct for free - `CellConvertEffect`,
    // `ExtraWeaponStatEffect` and `WeaponResourceEffect` are pure
    // `IWeaponModifier.Modify`, which only the cluster walk reaches, and they
    // need nothing from this file.
    //
    // **Every effect that REGISTERS something in `OnInstalled` was wrong**, and
    // there are twelve of them. A kill-reward module lying unpowered in a
    // corner still paid out; a ship-stat module still moved the stat; a pierce
    // or burn-rate contribution still counted. Worse for the kill rewards
    // specifically: never having been handed a weapon, an unpowered one in a
    // weapon's cluster paid out for EVERY gun - the opposite of what its
    // position says.
    //
    // WHAT THIS DOES. One postfix on `Unit.Data.RecalculateStats(IModuleGrid)`,
    // which is the game's own "the grid changed" tick and is handed the grid.
    // After the game has run its live modules, this sweeps the effects THIS mod
    // has registered on that unit and tells any whose live-ness changed. The
    // effect then runs its own push or release path - the same two methods its
    // install and uninstall already call, so there is one owner per action and
    // nothing new to keep in step.
    //
    // WHY THE GRID IS ASKED RATHER THAN THE CALLBACK COUNTED. The obvious
    // alternative is "an effect that got `OnRecalculateUnitStats` this
    // generation is live", and it is wrong in a way that would have shipped
    // quietly: `ModuleGrid.OnRecalculateStats` has a branch that, for a cluster
    // whose MainModule is a `SpawnMinionModule`, calls the MAIN module only and
    // **skips every augmentation in that cluster**. So every module augmenting
    // a drone gadget would have read as dead while the game was happily running
    // its `OnUpdate`. Reading `ConnectedAndPoweredModules` has no such hole.
    //
    // WHEN WE CANNOT TELL, THE ANSWER IS LIVE. That is the standing rule about
    // silent fallbacks pointed the useful way round: this gate can only ever
    // take a working module away from someone, so every uncertainty resolves in
    // favour of leaving it alone. A grid we were not handed, a grid with no
    // main module in any cluster (not set up yet - the restore path calls
    // `RecalculateStats` early), an effect with no `Module`, or a throw
    // anywhere: state is left exactly as installed.
    //
    // Works on both grid implementations because it only ever touches
    // `IModuleGrid` / `IModuleCluster`. `SimpleModuleCluster` (enemies and
    // minions) returns every module it holds from `ConnectedAndPoweredModules`,
    // which has no power geometry - so a minion's modules are always live,
    // which is right.
    public static class ModuleForgeLive
    {
        private static readonly ManualLogSource Log =
            ModuleForgeLog.Source("ModuleForge.Live");

        // Implemented by any effect that registers something outside the
        // game's gated seams.
        //
        // BOTH DIRECTIONS MUST BE IDEMPOTENT. This is called on a CHANGE, but a
        // re-install, a reset or a save-load can repeat one, and every
        // implementation here already guards itself because its install and
        // uninstall did.
        public interface IGated
        {
            void OnModuleLiveChanged(Unit.Data unit, bool live);
        }

        private class Entry
        {
            public IGated gated;
            public ModuleEffect effect;
            public Unit.Data owner;

            // What we last TOLD it, not what we last measured - so a release
            // is never sent twice and an effect that was never gated off is
            // never redundantly pushed.
            public bool live;
        }

        private static readonly List<Entry> _tracked = new List<Entry>();

        // Reused across sweeps; this runs on a grid change, not per frame, but
        // allocating a set per call for something the ship does while you drag
        // a card around is still wasteful.
        private static readonly HashSet<Module> _powered = new HashSet<Module>();

        // ------------------------------------------------------------------
        // Tracking
        // ------------------------------------------------------------------

        // Called from an effect's OnInstalled, AFTER it has pushed. The effect
        // is therefore recorded as live and the first sweep corrects it if it
        // is not - at most one frame of a stat being applied, against the
        // alternative of re-plumbing every install path in the mod.
        public static void Track(ModuleEffect effect, Unit.Data owner)
        {
            IGated gated = effect as IGated;

            if (gated == null || owner == null)
                return;

            for (int i = 0; i < _tracked.Count; i++)
            {
                if (ReferenceEquals(_tracked[i].gated, gated))
                {
                    _tracked[i].owner = owner;
                    _tracked[i].live = true;
                    return;
                }
            }

            _tracked.Add(new Entry
            {
                gated = gated,
                effect = effect,
                owner = owner,
                live = true
            });
        }

        public static void Untrack(ModuleEffect effect)
        {
            IGated gated = effect as IGated;

            if (gated == null)
                return;

            for (int i = _tracked.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(_tracked[i].gated, gated))
                    _tracked.RemoveAt(i);
            }
        }

        // Modules are NOT uninstalled when a run ends, so without this the list
        // would carry a run's worth of dead effects into the next one forever.
        // Safe to clear at a run entry point for the same reason the burn totals
        // are: both reset points run BEFORE that run's modules install, and an
        // untracked effect is simply ungated, which is the safe state.
        public static void Reset()
        {
            _tracked.Clear();
            _powered.Clear();
        }

        // For a diagnostic line; not used for any decision.
        public static int TrackedCount { get { return _tracked.Count; } }

        // ------------------------------------------------------------------
        // The sweep
        // ------------------------------------------------------------------

        public static void Sweep(Unit.Data unit, IModuleGrid grid)
        {
            if (unit == null || grid == null || _tracked.Count == 0)
                return;

            // Most units in a level are not ours. Check before building
            // anything.
            bool mine = false;

            for (int i = 0; i < _tracked.Count; i++)
            {
                if (ReferenceEquals(_tracked[i].owner, unit))
                {
                    mine = true;
                    break;
                }
            }

            if (!mine)
                return;

            _powered.Clear();
            bool ready = false;

            foreach (IModuleCluster cluster in grid.GetAllClusters())
            {
                if (cluster == null)
                    continue;

                // A grid with a main module somewhere is a grid that has been
                // set up. Deliberately NOT "the powered set is non-empty": a
                // cluster root need not sit in its own powered slots, so an
                // empty set is a plausible reading of a real ship and would
                // have made this guard argue for the wrong answer.
                if (cluster.HasMainModule)
                    ready = true;

                IEnumerable<Module> modules = cluster.ConnectedAndPoweredModules;

                if (modules == null)
                    continue;

                foreach (Module m in modules)
                {
                    if (m != null)
                        _powered.Add(m);
                }
            }

            if (!ready)
            {
                SayNotReady();
                return;
            }

            for (int i = 0; i < _tracked.Count; i++)
            {
                Entry e = _tracked[i];

                if (!ReferenceEquals(e.owner, unit))
                    continue;

                Module module = (e.effect != null) ? e.effect.Module : null;

                // No module to ask about - leave it alone rather than guessing.
                bool live = (module == null) || _powered.Contains(module);

                if (live == e.live)
                    continue;

                e.live = live;

                try
                {
                    e.gated.OnModuleLiveChanged(unit, live);
                    Say(e, live);
                }
                catch (Exception ex)
                {
                    // A gate must never be able to break the stat recalculation
                    // it is riding on. Roll the record back so the next sweep
                    // tries again rather than believing a change that did not
                    // happen.
                    e.live = !live;

                    Log.LogError(
                        "a module live-gate threw for " +
                        e.effect.GetType().Name + ": " + ex);
                }
            }
        }

        // ------------------------------------------------------------------
        // Diagnostics
        // ------------------------------------------------------------------
        //
        // Named `_said*` so ModuleForgeDiagnosticGates sweeps them by
        // convention at each run entry - what these describe is per RUN, and a
        // one-shot bool describing a per-run thing is silent in exactly the run
        // you go looking for it.

        private static bool _saidGatedOff;
        private static bool _saidGatedOn;
        private static bool _saidNotReady;

        private static void Say(Entry e, bool live)
        {
            string name = (e.effect != null)
                ? e.effect.GetType().Name : "a module effect";

            string card = (e.effect != null && e.effect.Module != null &&
                           e.effect.Module.Data != null)
                ? e.effect.Module.Data.displayName : "a module";

            if (!live)
            {
                if (_saidGatedOff)
                    return;

                _saidGatedOff = true;

                Log.LogInfo(
                    "\"" + card + "\" (" + name + ") is installed but NOT " +
                    "connected and powered, so its effect has been withdrawn " +
                    "until it is - which is the same rule the game applies to " +
                    "every stock module. Connect it to its cluster's power " +
                    "core and it comes straight back. Said once per run.");

                return;
            }

            if (_saidGatedOn)
                return;

            _saidGatedOn = true;

            Log.LogInfo(
                "\"" + card + "\" (" + name + ") is connected and powered " +
                "again, so its effect is back on. Said once per run.");
        }

        private static void SayNotReady()
        {
            if (_saidNotReady)
                return;

            _saidNotReady = true;

            Log.LogInfo(
                "a stat recalculation arrived before any cluster had a main " +
                "module, so the powered-module gate left everything as " +
                "installed rather than guessing. Normal during a save restore. " +
                "Said once per run.");
        }

        // ------------------------------------------------------------------
        // The hook
        // ------------------------------------------------------------------
        //
        // `Unit.Data.RecalculateStats(IModuleGrid)` is the game's own "the grid
        // changed" tick: `ModuleGridOwner.Data.OnUpdate` calls it on the frame
        // after any install, uninstall, move or swap, and it is handed the grid
        // rather than having to be walked back to. A POSTFIX, so the live
        // modules have already re-pushed by the time the dead ones are
        // withdrawn.
        [HarmonyPatch(typeof(Unit.Data), "RecalculateStats")]
        public class OnRecalculateStats
        {
            static void Postfix(Unit.Data __instance, IModuleGrid moduleGrid)
            {
                try
                {
                    Sweep(__instance, moduleGrid);
                }
                catch (Exception e)
                {
                    Log.LogError("the powered-module gate failed: " + e);
                }
            }
        }
    }
}
