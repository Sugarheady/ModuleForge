using BepInEx;
using BepInEx.Unity.Mono;
using HarmonyLib;

namespace ModuleForge
{
    [BepInPlugin(
        "com.andy.moduleforge",
        "Module Forge",
        "1.0.0")]
    public class ModuleForgePlugin : BaseUnityPlugin
    {
        private void Awake()
        {
            Logger.LogInfo("Module Forge loaded");

            var harmony =
                new Harmony("com.andy.moduleforge");

            harmony.PatchAll();

            Logger.LogInfo("Module Forge patches applied");
        }
    }
}
