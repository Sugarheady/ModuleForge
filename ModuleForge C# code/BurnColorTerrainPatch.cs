using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ModuleForge
{
    // Optional: also recolor burning TERRAIN/world fire (opt-in per
    // module via "includeTerrain"). World fire is emitted through
    // StatusEffectParticleManager.Emit(Vector2Int) - a different path from
    // unit burn (EmitForUnit) - so this never affects the player's own
    // burn, only the terrain.
    //
    // Rather than re-implement the game's per-cell emission throttling, we
    // briefly set the shared particle systems' MainModule.startColor
    // before the original emit and restore it right after (in a Finalizer,
    // so it's restored even if the original throws). The game's emit uses
    // the main module's start color when EmitParams doesn't override it,
    // so this tints exactly the terrain particles emitted in this call.
    [HarmonyPatch(typeof(StatusEffectParticleManager), "Emit",
        new Type[] { typeof(Vector2Int) })]
    public class BurnColorTerrainPatch
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("ModuleForge");

        // Per-call save state (Emit(Vector2Int) is single-threaded and
        // non-reentrant, so plain statics are safe). _tintedCount is the
        // number of leading systems actually retinted, so the Finalizer
        // reverts EXACTLY those - even if the set loop throws part way,
        // world fire is never left recolored.
        private static ParticleSystem.MinMaxGradient[] _saved;
        private static ParticleSystem[] _tintedSystems;
        private static int _tintedCount;

        static void Prefix(StatusEffectParticleManager __instance)
        {
            _tintedSystems = null;
            _tintedCount = 0;

            if (!ModuleForgeBurn.HasColor || !ModuleForgeBurn.ColorTerrain)
                return;

            try
            {
                ParticleSystem[] systems =
                    BurnColorPatch.GetSystems(__instance);

                if (systems == null || systems.Length == 0)
                    return;

                Color c = ModuleForgeBurn.GetEmitColor();

                if (_saved == null || _saved.Length != systems.Length)
                    _saved = new ParticleSystem.MinMaxGradient[systems.Length];

                // Publish the target BEFORE mutating, so the Finalizer can
                // undo whatever we managed to apply if a set throws.
                _tintedSystems = systems;

                for (int i = 0; i < systems.Length; i++)
                {
                    if (systems[i] == null)
                        continue;

                    var main = systems[i].main;
                    _saved[i] = main.startColor;
                    main.startColor = new ParticleSystem.MinMaxGradient(c);
                    _tintedCount = i + 1;
                }
            }
            catch (Exception e)
            {
                // Leave _tintedSystems/_tintedCount set so the Finalizer
                // reverts the systems we already retinted.
                Log.LogError("Burn color terrain setup failed: " + e);
            }
        }

        // Runs after the original even if it threw - guarantees the fire
        // color is put back so world fire is never left recolored.
        static void Finalizer()
        {
            if (_tintedSystems == null)
                return;

            try
            {
                int n = _tintedCount;
                if (n > _tintedSystems.Length) n = _tintedSystems.Length;
                if (_saved != null && n > _saved.Length) n = _saved.Length;

                for (int i = 0; i < n; i++)
                {
                    if (_tintedSystems[i] == null)
                        continue;

                    var main = _tintedSystems[i].main;
                    main.startColor = _saved[i];
                }
            }
            catch (Exception e)
            {
                Log.LogError("Burn color terrain restore failed: " + e);
            }
            finally
            {
                _tintedSystems = null;
                _tintedCount = 0;
            }
        }
    }
}
