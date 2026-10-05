using System;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;

namespace ModuleForge
{
    // Coexistence bridge for LEECH, the same shape as ModuleForgeCritCompat and
    // for the same reason.
    //
    // Both mods can leech, and a leech is a prefix/postfix pair on
    // `DamagableResource.Damage`. Two of them would each measure the victim's
    // tank around the same hit and each pay out - so a player with both mods
    // would heal roughly twice what they asked for and see two orbs per hit. So
    // there is EXACTLY ONE OWNER:
    //
    //   * Weapon Forge PRESENT -> IT owns the patch (it holds the per-weapon
    //     `leech` blocks from the weapon files). This mod stops patching and
    //     pushes its share and flat amount into that engine, so the two ADD.
    //   * Weapon Forge ABSENT  -> this mod runs its own bundled engine.
    //
    // Ownership goes the same way as crit and the opposite way from burn, and
    // that is the rule rather than an inconsistency: THE MOD THAT OWNS A
    // MECHANIC KEEPS IT. Burn's engine lives here; crit's and leech's live
    // there.
    public static class ModuleForgeLeechCompat
    {
        private static readonly ManualLogSource Log =
            ModuleForgeLog.Source("ModuleForge.Leech");

        private static bool _init;
        private static bool _weaponForgePresent;
        private static bool _bridgeReady;

        // (WeaponBase, object, float, float, float)
        private static MethodInfo _setBonus;
        private static MethodInfo _clearBonus;   // (object)

        public static bool OwnsPatches
        {
            get
            {
                EnsureInit();
                return !_weaponForgePresent;
            }
        }

        // False only in the one bad case: Weapon Forge installed but its API
        // unresolvable, where doing nothing beats two mods patching.
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

            Type leech = FindTypeQuietly("WeaponForge.ForgeLeech");

            _weaponForgePresent = leech != null;

            if (!_weaponForgePresent)
            {
                Log.LogInfo(
                    "Weapon Forge not detected - Module Forge runs its own " +
                    "leech engine, so a leech module works on its own.");
                return;
            }

            try
            {
                _setBonus = AccessTools.Method(
                    leech, "SetBonus",
                    new[] { typeof(WeaponBase), typeof(object),
                            typeof(float), typeof(float), typeof(float),
                            typeof(float) });

                _clearBonus = AccessTools.Method(
                    leech, "ClearBonus", new[] { typeof(object) });

                _bridgeReady = _setBonus != null && _clearBonus != null;

                if (_bridgeReady)
                {
                    Log.LogInfo(
                        "Weapon Forge detected - it owns the leech damage " +
                        "patch, and Module Forge's leech modules feed its " +
                        "engine so the two add into one drain (no conflict).");
                }
                else
                {
                    Log.LogWarning(
                        "Weapon Forge detected but its leech API couldn't be " +
                        "resolved - Module Forge's leech modules are disabled " +
                        "to avoid two mods measuring the same hit. (Update " +
                        "both mods to matching versions.)");
                }
            }
            catch (Exception e)
            {
                _bridgeReady = false;
                Log.LogWarning("Weapon Forge leech bridge failed: " + e);
            }
        }

        // `weapon` may be null, meaning every weapon (a ship-grid module).
        public static void Set(
            WeaponBase weapon, object key, float fraction, float flat,
            float maxPerSecond, float perEnemyDelay, Resource resource,
            ModuleForgeLeech.Options options)
        {
            EnsureInit();

            if (key == null)
                return;

            if (!_weaponForgePresent)
            {
                ModuleForgeLeech.Set(
                    weapon, key, fraction, flat, maxPerSecond, perEnemyDelay,
                    resource, options);
                return;
            }

            if (!_bridgeReady)
                return;

            Announce(resource, options);

            // A ship-grid module (null weapon) must reach only its OWN ship's
            // guns in Weapon Forge's engine too - tell it who that is first.
            if (weapon == null)
                ForgeInterop.TellWeaponForgeOwner(key);

            try
            {
                _setBonus.Invoke(
                    null,
                    new object[] { weapon, key, fraction, flat, maxPerSecond,
                                   perEnemyDelay });
            }
            catch (Exception e)
            {
                Log.LogWarning("Pushing leech to Weapon Forge failed: " + e);
            }
        }

        public static void Clear(object key)
        {
            EnsureInit();

            if (key == null)
                return;

            if (!_weaponForgePresent)
            {
                ModuleForgeLeech.Clear(key);
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
                    "Withdrawing leech from Weapon Forge failed: " + e);
            }
        }

        // The SHARE, the FLAT amount and the CAP cross the bridge. When the heal
        // lands, the orb's art and which pool refills belong to whichever engine
        // is running - and a module that set them would otherwise look broken to
        // its author with no explanation anywhere.
        //
        // The cap crossing is deliberate and it is the safety valve: Weapon
        // Forge takes the TIGHTEST of the weapon's cap and ours, so a module
        // that ships a cap protects the player even on a weapon whose author
        // left it uncapped.
        private static bool _announced;

        private static void Announce(
            Resource resource, ModuleForgeLeech.Options options)
        {
            if (_announced)
                return;

            bool saidLook = options != null && !options.IsPlain;
            bool saidPool = resource != null;

            if (!saidLook && !saidPool)
                return;

            _announced = true;

            Log.LogInfo(
                "A leech module asked for " +
                (saidLook ? "a heal timing or an orb setting" : "") +
                (saidLook && saidPool ? " and " : "") +
                (saidPool ? "a specific resource" : "") +
                ". Weapon Forge is installed and owns the leech damage patch, " +
                "so the SHARE, the FLAT amount and the CAP from this module " +
                "are combined with the weapon's, but WHEN the heal lands, the " +
                "orb's look and which pool refills come from that weapon's own " +
                "\"leech\" block (or its defaults: the tuff bubble, arriving " +
                "on the orb, into your ship's health). Nothing is lost - set " +
                "those on the weapon instead. Said once per run.");
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
