using System;
using System.Collections.Generic;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ModuleForge
{
    // THE BRIDGE: given a Unit.Data, find the Unit MonoBehaviour.
    //
    // Why this needs to exist at all. A ModuleEffect is handed
    // `OnRecalculateUnitStats(Unit.Data)` - the DATA, never the component - so
    // it cannot reach ShipMovement, DamagableResource, or anything else that
    // lives on the ship's GameObject. That is the single reason no module in
    // this game can change how the ship handles.
    //
    // The three routes that look like answers and are not:
    //
    //   IWeaponModifier.Modify(weaponBase)   no Unit at all, and
    //                                        weaponBase.Owner is NULL there -
    //                                        WeaponFactory runs the modifiers
    //                                        BEFORE Equip sets Owner.
    //   ModuleEffect.ModifyWeapon(Unit, ...) a DEAD HOOK. Declared on
    //                                        ModuleEffect, called from nowhere
    //                                        in the game. Looks exactly right.
    //   Unit.CreateData()                    real, but only fires on a fresh
    //                                        spawn. A save reload replays
    //                                        mementos instead, so the map
    //                                        would be empty after a load.
    //
    // What DOES work: `SavableComponent<T>.Bind(T)` is public virtual and its
    // whole body is `ComponentData = data`. It runs on a fresh spawn AND on
    // every reload bind, which is exactly the lifetime we need.
    public static class ModuleForgeUnits
    {
        private static readonly ManualLogSource Log =
            ModuleForgeLog.Source("ModuleForge.Units");

        // Data -> Unit. Keyed by reference: Unit.Data is a class, so this is
        // identity, which is what we want.
        private static readonly Dictionary<Unit.Data, Unit> _map =
            new Dictionary<Unit.Data, Unit>();

        private static int _sinceSweep;

        public static void Register(Unit.Data data, Unit unit)
        {
            if (data == null || unit == null)
                return;

            _map[data] = unit;

            // Units die constantly, and a destroyed Unity object compares equal
            // to null without ever being removed from a dictionary. Sweep
            // occasionally rather than on a timer - this is only called on
            // bind, which is rare.
            if (++_sinceSweep >= 64)
            {
                _sinceSweep = 0;
                Sweep();
            }
        }

        private static void Sweep()
        {
            List<Unit.Data> dead = null;

            foreach (var kv in _map)
            {
                // `== null` on a Unity object is the destroyed check; it is not
                // the same as ReferenceEquals(null) and that is the point.
                if (kv.Value == null)
                {
                    if (dead == null)
                        dead = new List<Unit.Data>();

                    dead.Add(kv.Key);
                }
            }

            if (dead == null)
                return;

            for (int i = 0; i < dead.Count; i++)
                _map.Remove(dead[i]);
        }

        // The lookup an effect actually calls.
        public static Unit Find(Unit.Data data)
        {
            if (data == null)
                return null;

            Unit unit;

            if (_map.TryGetValue(data, out unit) && unit != null)
                return unit;

            // Fallback, because a silent null here would look like the whole
            // feature is broken. FindObjectsByType is expensive, but this only
            // runs when the patch did not fire, and a stat recalculation is a
            // rare event (a module changed) rather than a per-frame one.
            var all = UnityEngine.Object.FindObjectsByType<Unit>(
                FindObjectsSortMode.None);

            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != null && all[i].ComponentData == data)
                {
                    _map[data] = all[i];
                    return all[i];
                }
            }

            return null;
        }

        // Convenience: the components the ship-stat effects actually want.
        public static T Component<T>(Unit.Data data) where T : Component
        {
            Unit unit = Find(data);

            if (unit == null)
                return null;

            return unit.GetComponentInChildren<T>(true);
        }

        public static T[] Components<T>(Unit.Data data) where T : Component
        {
            Unit unit = Find(data);

            if (unit == null)
                return new T[0];

            return unit.GetComponentsInChildren<T>(true);
        }

        // ---- the patch ---------------------------------------------------
        //
        // Patching a CLOSED generic: SavableComponent<Unit.Data> only, so this
        // never touches the dozens of other components sharing that base.
        public static void Patch(Harmony harmony)
        {
            try
            {
                var target = AccessTools.Method(
                    typeof(SavableComponent<Unit.Data>),
                    "Bind",
                    new[] { typeof(Unit.Data) });

                if (target == null)
                {
                    Log.LogWarning(
                        "Could not find SavableComponent<Unit.Data>.Bind - " +
                        "ship-stat modules will fall back to a scan, which " +
                        "still works but is slower.");
                    return;
                }

                harmony.Patch(
                    target,
                    postfix: new HarmonyMethod(
                        typeof(ModuleForgeUnits).GetMethod(
                            "OnBind",
                            System.Reflection.BindingFlags.NonPublic |
                            System.Reflection.BindingFlags.Static)));
            }
            catch (Exception e)
            {
                // Never take the mod down over this: without the map, Find
                // still resolves by scanning.
                Log.LogWarning(
                    "Unit bridge patch failed, falling back to scanning: " + e);
            }
        }

        private static void OnBind(
            SavableComponent<Unit.Data> __instance, Unit.Data data)
        {
            try
            {
                Register(data, __instance as Unit);
            }
            catch
            {
                // A binding failure must never stop a unit from spawning.
            }
        }
    }
}
