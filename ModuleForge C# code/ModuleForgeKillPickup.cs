using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace ModuleForge
{
    // Drops a resource pickup on the corpse instead of granting the resource
    // straight into the tank.
    //
    // `ResourcePickup` does all the work already and its `resource`, `amount`
    // and `pickupSfx` are all public - it charges the collector's tank on
    // pickup and refuses collectors with no tank for that resource. So this only
    // has to find one to clone.
    //
    // Nine of them ship with the game (one per resource plus Fuel and three
    // Money denominations). Rather than hard-code a guid, the first one seen at
    // runtime is remembered and re-used, with the resource overwritten - which
    // also means a Forge-only resource still gets a working pickup.
    public static class ModuleForgeKillPickup
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("ModuleForge.KillPickup");

        private static ResourcePickup _template;
        private static bool _searched;
        private static bool _warned;

        private static readonly Dictionary<Resource, ResourcePickup> _byResource =
            new Dictionary<Resource, ResourcePickup>();

        // Learned from whatever the level already contains. Called from the
        // pickup patch below, so by the time anything dies there is normally a
        // template to hand.
        public static void Learn(ResourcePickup pickup)
        {
            if (pickup == null)
                return;

            if (_template == null)
                _template = pickup;

            if (pickup.resource != null && !_byResource.ContainsKey(pickup.resource))
                _byResource[pickup.resource] = pickup;
        }

        public static void Drop(Vector2 where, Resource resource, float amount)
        {
            if (resource == null || amount <= 0f)
                return;

            ResourcePickup template = Template(resource);

            if (template == null)
            {
                if (!_warned)
                {
                    _warned = true;
                    Log.LogWarning(
                        "Kill reward wanted to drop a pickup, but no " +
                        "ResourcePickup has been seen yet to copy - the " +
                        "resource is being granted directly instead. This " +
                        "should sort itself out once any pickup has spawned " +
                        "in the run.");
                }

                return;
            }

            try
            {
                ResourcePickup drop = UnityEngine.Object.Instantiate(
                    template, where, Quaternion.identity);

                drop.resource = resource;
                drop.amount = amount;
                drop.gameObject.SetActive(true);

                // A little scatter so several kills at once do not stack into
                // one invisible pile.
                Rigidbody2D rb = drop.Rigidbody;

                if (rb != null)
                {
                    rb.AddForce(
                        new Vector2(
                            UnityEngine.Random.Range(-1.5f, 1.5f),
                            UnityEngine.Random.Range(0.5f, 2f)),
                        ForceMode2D.Impulse);
                }
            }
            catch (Exception e)
            {
                Log.LogError("Could not drop a kill pickup: " + e);
            }
        }

        // Prefer a pickup that already belongs to this resource - its art and
        // sound will be right. Fall back to any pickup with the resource
        // overwritten.
        private static ResourcePickup Template(Resource resource)
        {
            ResourcePickup exact;

            if (_byResource.TryGetValue(resource, out exact) && exact != null)
                return exact;

            if (_template != null)
                return _template;

            if (!_searched)
            {
                _searched = true;

                // Last resort: anything already in the scene.
                ResourcePickup[] all =
                    UnityEngine.Object.FindObjectsByType<ResourcePickup>(
                        FindObjectsSortMode.None);

                for (int i = 0; i < all.Length; i++)
                    Learn(all[i]);
            }

            return _template;
        }

        // Every pickup that spawns teaches us its prefab shape. Cheaper and far
        // more robust than resolving a guid, and it picks up Forge resources
        // too.
        //
        // Patched on `Pickup.Start`, NOT on ResourcePickup - ResourcePickup has
        // no lifecycle method of its own, and Harmony throws at PatchAll when
        // asked for a method that does not exist. `Start` is private on the
        // base, which Harmony patches happily.
        [HarmonyLib.HarmonyPatch(typeof(Pickup), "Start")]
        public class OnPickupStart
        {
            static void Postfix(Pickup __instance)
            {
                try
                {
                    Learn(__instance as ResourcePickup);
                }
                catch { }
            }
        }
    }
}
