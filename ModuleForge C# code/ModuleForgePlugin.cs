using BepInEx;
using BepInEx.Configuration;
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

            // The burn-tick-rate cap: burn-rate modules can never make a
            // burn tick faster than this many times per second, no matter
            // how many are stacked. Editable in the BepInEx config file.
            ConfigEntry<float> maxBurnTicks = Config.Bind(
                "Burn",
                "MaxTicksPerSecond",
                100f,
                "Cap on how fast burn can tick when using burn-rate " +
                "modules (ticks per second). Stacking booster modules can " +
                "approach but never exceed this. Note: the game can only " +
                "tick burn once per frame, so values above your frame rate " +
                "just mean 'every frame'. Must be > 0.");

            if (maxBurnTicks.Value > 0f)
                ModuleForgeBurn.MaxTicksPerSecond = maxBurnTicks.Value;

            var harmony =
                new Harmony("com.andy.moduleforge");

            harmony.PatchAll();

            Logger.LogInfo("Module Forge patches applied");
        }
    }
}
