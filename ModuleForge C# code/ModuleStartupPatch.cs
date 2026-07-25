using System;
using HarmonyLib;
using BepInEx.Logging;

namespace ModuleForge
{
    // Build + register custom modules at startup, before any save loads.
    // The game restores an equipped/owned module by id via
    // ModuleRegistry.Get(id).DeepCopy(), so an unregistered module makes
    // loading a save that owns it throw. Registering here (not only when
    // a shop/crate needs it) keeps "Continue" safe.
    [HarmonyPatch(typeof(ServiceContainer), "InstallServices")]
    public class ModuleStartupPatch
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("ModuleForge");

        static void Postfix()
        {
            try
            {
                ModuleRegistry registry;

                if (!ServiceLocator.TryGet<ModuleRegistry>(out registry) ||
                    registry == null)
                {
                    return;
                }

                ModuleForgeRegistry.BuildAll();
                ModuleForgeRegistry.RegisterInto(registry);
            }
            catch (Exception e)
            {
                Log.LogError(
                    "Module Forge startup registration failed: " + e);
            }
        }
    }
}
