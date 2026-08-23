using System;
using BepInEx.Logging;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.Mono;
using HarmonyLib;
using UnityEngine;

namespace ModuleForge
{
    [BepInPlugin(
        "com.sugarheady.moduleforge",
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

            // ---- the on-screen buff indicator -----------------------------
            //
            // Deliberately INDEPENDENT of Weapon Forge's identical settings, so
            // this mod works and is configurable with the other absent. The
            // mismatch check below is the price of that: two configs can
            // disagree, and a screen that mixes styles should say why.
            ConfigEntry<string> buffStyle = Config.Bind(
                "Buff indicator",
                "Style",
                "off",
                "How an active module buff is shown: \"off\" (the default), " +
                "\"feed\" (the game's own status line, where \"Fuel low\" " +
                "appears), \"icons\" (a row of icons beside the minion " +
                "counter), or \"both\". OFF by default deliberately - the " +
                "status area sits next to the ammo / fuel / health readouts, " +
                "so this is an opt-in overlay on top of them. A module can " +
                "still ask for an indicator per-file with \"buffIndicator\".");

            ConfigEntry<string> buffLabel = Config.Bind(
                "Buff indicator",
                "Label",
                "effect",
                "What the indicator reads: \"effect\" (FIRE RATE +30%), " +
                "\"seconds\" (a countdown), \"stacks\" (x3), or \"icon\" " +
                "(no text at all).");

            ConfigEntry<bool> buffStates = Config.Bind(
                "Buff indicator",
                "ShowTimedStates",
                false,
                "Reserved: also indicate other timed states as they gain " +
                "support. Nothing reports one yet, so this does nothing today.");

            ConfigEntry<bool> buffDebuffs = Config.Bind(
                "Buff indicator",
                "ShowDebuffs",
                false,
                "Reserved: also indicate things working against you. Nothing " +
                "reports one yet, so this does nothing today.");

            ConfigEntry<int> buffMaxLines = Config.Bind(
                "Buff indicator",
                "MaxFeedLines",
                3,
                "The most status lines this mod may occupy at once. A backstop " +
                "so the feed can never grow over the ammo / fuel / health " +
                "readouts, whatever happens upstream.");

            ConfigEntry<float> buffIconSize = Config.Bind(
                "Buff indicator",
                "IconSize",
                28f,
                "Side of one icon in the icon row, in UI pixels. The widget is " +
                "borrowed from the minion counter and brings its own authored " +
                "size, so this is what actually decides how big it looks.");

            ModuleForgeBuffHud.maxLines =
                Mathf.Clamp(buffMaxLines.Value, 1, 12);
            ModuleForgeBuffHud.iconSize =
                Mathf.Clamp(buffIconSize.Value, 8f, 128f);

            ModuleForgeBuffHud.style =
                ModuleForgeBuffHud.ParseStyle(buffStyle.Value);
            ModuleForgeBuffHud.labels =
                ModuleForgeBuffHud.ParseLabels(buffLabel.Value);
            ModuleForgeBuffHud.showTimedStates = buffStates.Value;
            ModuleForgeBuffHud.showDebuffs = buffDebuffs.Value;

            // Deferred, NOT run here. Weapon Forge loads AFTER this mod, so at
            // plugin-load time its types genuinely do not exist yet - the check
            // could never fire, and asking for the type printed a HarmonyX
            // warning that looked like a real failure in the log. The first HUD
            // bind is after every plugin is up.
            _myStyle = buffStyle.Value;
            _myLabel = buffLabel.Value;

            var harmony =
                new Harmony("com.sugarheady.moduleforge");

            harmony.PatchAll();

            Logger.LogInfo("Module Forge patches applied");
        }

        // The two mods keep separate indicator settings on purpose - either has
        // to work alone. The cost is that they can disagree, and a screen where
        // modules draw icons while weapons write status lines looks like a bug
        // rather than a choice. So say so, once, at startup.
        //
        // Read by type name only, never an assembly reference - the same
        // one-way bridge rule the rest of the interop follows. If Weapon Forge
        // is absent there is nothing to disagree with and this does nothing.
        private static readonly ManualLogSource MismatchLog =
            BepInEx.Logging.Logger.CreateLogSource("ModuleForge");

        private static string _myStyle = "";
        private static string _myLabel = "";
        private static bool _mismatchChecked;

        public static void CheckIndicatorMismatch()
        {
            if (_mismatchChecked)
                return;

            _mismatchChecked = true;

            string style = _myStyle;
            string label = _myLabel;

            try
            {
                // Found by scanning loaded assemblies rather than
                // AccessTools.TypeByName, which LOGS A WARNING when the type is
                // absent. Weapon Forge being uninstalled is a completely normal
                // state and must be silent.
                Type other = FindTypeQuietly("WeaponForge.ForgeBuffHud");

                if (other == null)
                    return;

                FieldInfo styleField = AccessTools.Field(other, "style");
                FieldInfo labelField = AccessTools.Field(other, "labels");

                if (styleField == null || labelField == null)
                    return;

                // Compared as text: the two enums are separate types with the
                // same member names, so the names are the only common ground.
                string theirStyle = styleField.GetValue(null).ToString();
                string theirLabel = labelField.GetValue(null).ToString();

                string mineStyle =
                    ModuleForgeBuffHud.ParseStyle(style).ToString();
                string mineLabel =
                    ModuleForgeBuffHud.ParseLabels(label).ToString();

                if (theirStyle == mineStyle && theirLabel == mineLabel)
                    return;

                MismatchLog.LogWarning(
                    "Buff indicator settings differ between the two mods - " +
                    "Module Forge is " + mineStyle + "/" + mineLabel +
                    ", Weapon Forge is " + theirStyle + "/" + theirLabel +
                    ". Both will work; module buffs and weapon buffs will " +
                    "just look different on screen. Match the [Buff indicator] " +
                    "blocks in the two BepInEx config files if that is not " +
                    "what you wanted.");
            }
            catch
            {
                // Never let a cosmetic check affect startup.
            }
        }

        private static Type FindTypeQuietly(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t;

                try
                {
                    t = asm.GetType(fullName, false);
                }
                catch (Exception)
                {
                    continue;
                }

                if (t != null)
                    return t;
            }

            return null;
        }
    }
}
