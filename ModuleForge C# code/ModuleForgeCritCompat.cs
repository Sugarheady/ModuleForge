using System;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;

namespace ModuleForge
{
    // Coexistence bridge for CRITICAL HITS - the mirror image of Weapon Forge's
    // ForgeBurnCompat, and for the same reason.
    //
    // Both mods can crit, and a crit is a prefix on `DamagableResource.Damage`.
    // Two prefixes on that method would each roll their own dice on the same
    // hit, so a player with both mods would get roughly double the crit rate
    // they asked for and two impact bursts on top of each other. So there is
    // EXACTLY ONE OWNER:
    //
    //   * Weapon Forge PRESENT -> IT owns the patch (it has the per-weapon crit
    //     blocks from the weapon files, and its engine already sums module
    //     contributions). This mod stops patching (see OwnsPatches) and pushes
    //     its chance and multiplier into that engine by reflection, so the two
    //     add into ONE number - the way pierce caps already do.
    //   * Weapon Forge ABSENT -> this mod runs its own bundled engine
    //     (ModuleForgeCrit + ModuleForgeCritPatch) and owns everything.
    //
    // Which half of a crit module is in force therefore depends on which mod is
    // installed, and that must never be silent: `Announce` says it once at the
    // first push, and names exactly which fields the other mod decides.
    //
    // The direction of ownership is the OPPOSITE of burn (where this mod owns
    // and Weapon Forge stands down) and that is deliberate rather than
    // inconsistent: burn's engine lives here, crit's lives there, and in both
    // cases the mod that owns the mechanic keeps it.
    public static class ModuleForgeCritCompat
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("ModuleForge.Crit");

        private static bool _init;
        private static bool _weaponForgePresent;
        private static bool _bridgeReady;

        private static MethodInfo _setBonus;    // (WeaponBase, object, float, float)
        private static MethodInfo _clearBonus;  // (object)

        // True when THIS mod should run its own crit Harmony patches.
        public static bool OwnsPatches
        {
            get
            {
                EnsureInit();
                return !_weaponForgePresent;
            }
        }

        // True when a contribution can actually reach somebody. False only in
        // the one bad case: Weapon Forge is installed but its API could not be
        // resolved, where doing nothing is safer than two mods patching.
        public static bool Live
        {
            get
            {
                EnsureInit();
                return !_weaponForgePresent || _bridgeReady;
            }
        }

        private static void EnsureInit()
        {
            if (_init)
                return;

            _init = true;

            Type crit = FindTypeQuietly("WeaponForge.ForgeCrit");

            _weaponForgePresent = crit != null;

            if (!_weaponForgePresent)
            {
                Log.LogInfo(
                    "Weapon Forge not detected - Module Forge runs its own " +
                    "crit engine, so a crit module works on its own.");
                return;
            }

            try
            {
                _setBonus = AccessTools.Method(
                    crit, "SetBonus",
                    new[] { typeof(WeaponBase), typeof(object),
                            typeof(float), typeof(float) });

                _clearBonus = AccessTools.Method(
                    crit, "ClearBonus", new[] { typeof(object) });

                _bridgeReady = _setBonus != null && _clearBonus != null;

                if (_bridgeReady)
                {
                    Log.LogInfo(
                        "Weapon Forge detected - it owns the crit damage " +
                        "patch, and Module Forge's crit modules feed its " +
                        "engine so the two add into one chance (no conflict).");
                }
                else
                {
                    Log.LogWarning(
                        "Weapon Forge detected but its crit API couldn't be " +
                        "resolved - Module Forge's crit modules are disabled " +
                        "to avoid two mods patching the same damage method. " +
                        "(Update both mods to matching versions.)");
                }
            }
            catch (Exception e)
            {
                _bridgeReady = false;
                Log.LogWarning("Weapon Forge crit bridge failed: " + e);
            }
        }

        // `weapon` may be null, meaning every weapon (a ship-grid module).
        public static void Set(
            WeaponBase weapon, object key, float chance, float multiplierBonus,
            ModuleForgeCrit.Options options)
        {
            EnsureInit();

            if (key == null)
                return;

            if (!_weaponForgePresent)
            {
                ModuleForgeCrit.Set(
                    weapon, key, chance, multiplierBonus, options);
                return;
            }

            if (!_bridgeReady)
                return;

            Announce(options);

            try
            {
                _setBonus.Invoke(
                    null, new object[] { weapon, key, chance, multiplierBonus });
            }
            catch (Exception e)
            {
                Log.LogWarning("Pushing crit to Weapon Forge failed: " + e);
            }
        }

        public static void Clear(object key)
        {
            EnsureInit();

            if (key == null)
                return;

            if (!_weaponForgePresent)
            {
                ModuleForgeCrit.Clear(key);
                return;
            }

            if (!_bridgeReady)
                return;

            try
            {
                _clearBonus.Invoke(null, new object[] { key });
            }
            catch (Exception e)
            {
                Log.LogWarning(
                    "Withdrawing crit from Weapon Forge failed: " + e);
            }
        }

        // Only the NUMBERS cross the bridge. Roll mode and the four cues belong
        // to whichever engine is running, exactly like the burn engine's tick
        // rate - and a module that set them would otherwise look broken to its
        // author with no explanation anywhere.
        private static bool _announced;

        private static void Announce(ModuleForgeCrit.Options options)
        {
            if (_announced || options == null || options.IsPlain)
                return;

            _announced = true;

            Log.LogInfo(
                "A crit module asked for a roll mode or a cue (colour, " +
                "sound, shake, bigger impact). Weapon Forge is installed and " +
                "owns the crit damage patch, so the CHANCE and the " +
                "MULTIPLIER from this module are added to the weapon's, but " +
                "the LOOK comes from that weapon's own \"crit\" block (or " +
                "its defaults: a bigger impact burst, rolled per hit). " +
                "Nothing is lost - set the look on the weapon instead. Said " +
                "once per run.");
        }

        // Scanned rather than asked for by AccessTools.TypeByName, which LOGS A
        // WARNING when the type is absent. Weapon Forge being uninstalled is a
        // completely normal state and must be silent.
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
