using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ModuleForge
{
    // Recolors the burn aura a unit emits while on fire. StatusEffectPar-
    // ticleManager.EmitForUnit is the ONLY path for unit burn particles
    // (world/cell fire goes through a different method), so tinting here
    // colors enemy burn without touching burning terrain. We re-emit the
    // single particle with an explicit EmitParams.startColor and skip the
    // original - matching its position/shape exactly, just colored.
    //
    // Gated: only when a burn-color module is equipped, and never for an
    // excluded owner (the player), so enemy-inflicted burn on you stays
    // the normal color.
    [HarmonyPatch(typeof(StatusEffectParticleManager), "EmitForUnit")]
    public class BurnColorPatch
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("ModuleForge");

        private static AccessTools.FieldRef<
            StatusEffectParticleManager, ParticleSystem[]> _psRef;
        private static bool _psReady;

        static bool Prefix(StatusEffectParticleManager __instance, Unit.Data unit)
        {
            // No color module equipped -> let the game emit normally.
            if (!ModuleForgeBurn.HasColor)
                return true;

            if (unit == null || unit.entity == null)
                return true;

            // The player (owner of the color module) burns normally.
            if (ModuleForgeBurn.IsExcluded(unit))
                return true;

            ParticleSystem[] systems;
            Color c;
            Vector3 pos;

            // Setup can fail before anything is emitted - in that case fall
            // back to the original (return true), no double emit.
            try
            {
                systems = GetSystems(__instance);
                if (systems == null || systems.Length == 0)
                    return true;

                c = ModuleForgeBurn.GetEmitColor();
                pos = unit.entity.position;
            }
            catch (Exception e)
            {
                Log.LogError("Burn color setup failed: " + e);
                return true;
            }

            // Committed to the colored emit now: always skip the original
            // afterwards, even if a single system throws, so a unit never
            // gets our colored particle AND the game's normal one.
            var ep = default(ParticleSystem.EmitParams);
            ep.position = new Vector3(pos.x, pos.y, 0f);
            ep.applyShapeToPosition = true;
            ep.startColor = c;

            for (int i = 0; i < systems.Length; i++)
            {
                if (systems[i] == null)
                    continue;

                try
                {
                    systems[i].Emit(ep, 1);
                }
                catch (Exception e)
                {
                    Log.LogError("Burn color emit failed: " + e);
                }
            }

            return false;
        }

        internal static ParticleSystem[] GetSystems(
            StatusEffectParticleManager m)
        {
            if (!_psReady)
            {
                try
                {
                    _psRef = AccessTools.FieldRefAccess<
                        StatusEffectParticleManager, ParticleSystem[]>(
                        "particleSystems");
                }
                catch
                {
                    _psRef = null;
                }
                _psReady = true;
            }

            return _psRef != null ? _psRef(m) : null;
        }
    }
}
