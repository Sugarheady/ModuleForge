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
    // NINE of them ship with the game, and they are PREFABS. Verified against
    // the asset dump 2026-08-26 by looking for the ResourcePickup COMPONENT
    // rather than for a name pattern, which is what an earlier version of this
    // comment did and got wrong:
    //
    //   prefab                    resource          amount   look
    //   ------------------------  ----------------  ------   ---------------
    //   FuelPickup                Resource Fuel       1      blue orb
    //   ResourcePickup Caps       Resource Caps       1      orange orb
    //   ResourcePickup_Purple     Resource Purple     1      pink orb
    //   ResourcePickup_White      Resource White      1      white
    //   ResourcePickup_Red        Resource Health     1      red
    //   ResourcePickup Tech       Resource Tech       1      purple squares
    //   ResourcePickup_Money      Resource Money     10      yellow
    //   ResourcePickup_Money_20   Resource Money     20      yellow, bigger
    //   ResourcePickup_Money_50   Resource Money     50      yellow, bigger
    //
    // **THE ONLY RESOURCE IN THE GAME WITH NO PICKUP IS `Resource Electron`.**
    // Eight resources exist; seven have art, and Money has three denominations.
    //
    // Two naming traps, and both cost a round:
    //
    //   - **Health's pickup is `ResourcePickup_Red`.** `DropTable Box Health`
    //     drops it and its `resource` is `Resource Health`.
    //   - **Fuel's pickup is `FuelPickup`** - it does NOT start with
    //     "ResourcePickup". An earlier comment here claimed the game had no
    //     Fuel pickup at all, because the list had been built by name. The
    //     type scan below always found it; only the documentation was wrong.
    //
    // Fuel, Caps and Purple all share ONE orb sprite and differ only by the
    // SpriteRenderer's tint, which is what makes re-tinting a sensible fallback
    // for a resource with no pickup of its own - see `OrbPickups` below.
    //
    // WHY THIS USED TO SILENTLY STOP WORKING, which is what "unable to spawn
    // health regardless of what i change" was. The template used to be learned
    // only from pickups that had already SPAWNED, via a `Pickup.Start` postfix,
    // with `FindObjectsByType` as a one-shot fallback. Three things wrong with
    // that, and they compound:
    //
    //   - `FindObjectsByType` finds scene objects only. **Prefabs are invisible
    //     to it**, so the fallback could never see any of the eight.
    //   - `_searched` latched to true on the first attempt whether or not it
    //     found anything, so one early kill - before any crate had dropped
    //     anything - killed the feature for the rest of the run.
    //   - The cache held live scene objects, and a pickup DESTROYS itself when
    //     collected. A destroyed Unity object compares equal to null, so the
    //     cached template silently became null the moment the player picked it
    //     up.
    //
    // All three go away by caching the PREFABS instead, found with
    // `Resources.FindObjectsOfTypeAll`, which does include them - they are in
    // memory because the drop tables reference them. A prefab is never
    // destroyed and never needs re-finding.
    //
    // ================================================================
    // AND WHY IT STILL WORE THE WRONG ART AFTER ALL THAT (fixed 2026-08-26)
    // ================================================================
    //
    // The prefab scan above was written, and then gated behind a condition the
    // OLD path satisfies first:
    //
    //     if (_template != null && _byResource.Count > 0) return;
    //
    // `Learn` is called from a `Pickup.Start` postfix on every pickup the game
    // spawns. The first crate to drop anything therefore sets `_template` and
    // puts one entry in `_byResource` - which makes that guard true, so
    // **the prefab scan never ran at all**. The cache went back to holding
    // exactly what it held before the rewrite: whatever had physically spawned
    // in the room.
    //
    // The visible symptom is precise: a Health reward finds no `Resource Health`
    // entry (because `ResourcePickup_Red` never spawned), falls back to
    // `_template` - the first pickup of the run - and every drop for the rest of
    // that run wears THAT one prefab's art while paying Health correctly. Same
    // wrong art all run; a different wrong art next run, because which crate
    // opened first changes.
    //
    // THE LESSON: an early-out written against "have we got anything yet" is not
    // the same as "have we done the expensive thing yet", and another code path
    // filling the same cache is all it takes for them to diverge. The scan now
    // has its OWN flag, set only when the scan actually finds prefabs.
    //
    // Prefabs also now WIN over live scene instances in the cache, rather than
    // merely filling gaps. A live pickup is a worse template in every way: it is
    // destroyed when collected, it carries its own velocity and spawn state, and
    // it is whichever one happened to be lying around.
    public static class ModuleForgeKillPickup
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("ModuleForge.KillPickup");

        // The fallback art for a resource with no pickup of its own, in
        // preference order. All three of these are the SAME orb sprite with a
        // different SpriteRenderer tint - so cloning one and re-tinting it with
        // the resource's own colour produces art the game itself would have
        // drawn, rather than an obviously borrowed one.
        //
        // Pinned rather than "whatever we found first" for the same reason the
        // scan is pinned: an arbitrary fallback varies run to run and is
        // impossible to recognise in a bug report.
        private static readonly string[] OrbPickups =
        {
            "ResourcePickup_Purple",
            "ResourcePickup Caps",
            "FuelPickup",
            "ResourcePickup_White",
        };

        private static int OrbRank(ResourcePickup p)
        {
            if (p == null)
                return int.MaxValue;

            for (int i = 0; i < OrbPickups.Length; i++)
            {
                if (string.Equals(p.name, OrbPickups[i],
                        StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return OrbPickups.Length;
        }

        private static ResourcePickup _template;

        // Set ONLY when the prefab scan actually found something. Never set by
        // the live-learning path, which is the whole bug above.
        private static bool _scanned;
        private static bool _warned;

        private static readonly Dictionary<Resource, ResourcePickup> _byResource =
            new Dictionary<Resource, ResourcePickup>();

        // One log line per resource, so a mismatch is diagnosable without
        // spamming a line per kill.
        private static readonly HashSet<Resource> _reported =
            new HashSet<Resource>();

        // A prefab asset has no scene. A scene object does. This is the only
        // thing separating the two once `FindObjectsOfTypeAll` has handed them
        // to you in one array.
        private static bool IsPrefab(ResourcePickup p)
        {
            return p != null && !p.gameObject.scene.IsValid();
        }

        // Learned from the prefab scan, and from whatever the level spawns - the
        // second still matters for a resource no prefab covers (a Forge resource,
        // say), which the scan cannot teach us.
        public static void Learn(ResourcePickup pickup)
        {
            if (pickup == null)
                return;

            bool isPrefab = IsPrefab(pickup);

            // A prefab REPLACES a live instance here, it does not merely fill a
            // gap. Otherwise the first pickup to spawn owns the slot for the
            // run. Between two prefabs, the better orb wins.
            if (_template == null ||
                (isPrefab && !IsPrefab(_template)) ||
                (isPrefab && OrbRank(pickup) < OrbRank(_template)))
            {
                _template = pickup;
            }

            if (pickup.resource == null)
                return;

            // Overwrite a DEAD entry, don't skip it. A pickup destroys itself
            // when collected, and a destroyed Unity object compares equal to
            // null while still being a perfectly good dictionary key - so
            // `ContainsKey` alone would keep a collected pickup in the cache
            // forever and the drop would silently stop working.
            ResourcePickup had;
            bool have = _byResource.TryGetValue(pickup.resource, out had) &&
                        had != null;

            if (!have || (isPrefab && !IsPrefab(had)))
                _byResource[pickup.resource] = pickup;
        }

        // Returns false when no pickup could be dropped, so the caller can pay
        // the resource straight into the tank instead. The old version said in
        // its own warning that it was "being granted directly instead" and then
        // just returned - which is how a broken pickup became a reward that
        // paid nothing at all rather than a reward that looked slightly wrong.
        public static bool Drop(Vector2 where, Resource resource, float amount)
        {
            if (resource == null || amount <= 0f)
                return false;

            bool exact;
            ResourcePickup template = Template(resource, out exact);

            if (template == null)
            {
                if (!_warned)
                {
                    _warned = true;
                    Log.LogWarning(
                        "Kill reward wanted to drop a pickup and found no " +
                        "ResourcePickup prefab at all to copy for " +
                        (resource.name ?? "that resource") +
                        " - the resource is being granted straight into the " +
                        "tank instead, so the reward still pays. Nine pickup " +
                        "prefabs ship with the game, so seeing this means none " +
                        "of them were loaded yet.");
                }

                return false;
            }

            try
            {
                ResourcePickup drop = UnityEngine.Object.Instantiate(
                    template, where, Quaternion.identity);

                drop.resource = resource;
                drop.amount = amount;

                // Borrowed art gets the resource's OWN colour. An exact match
                // is left alone - its tint is already the right one, and
                // repainting it would flatten the deliberate differences
                // between, say, the three Money denominations.
                if (!exact)
                    Tint(drop, resource);

                drop.gameObject.SetActive(true);

                // A little scatter so several kills at once do not stack into
                // one invisible pile.
                Rigidbody2D rb = drop.Rigidbody;

                if (rb != null)
                {
                    // Cloning a LIVE pickup copies its velocity too, so clear it
                    // before adding the scatter or the copy inherits the flight
                    // of whatever it was cloned from. Harmless on a prefab.
                    rb.linearVelocity = Vector2.zero;
                    rb.angularVelocity = 0f;

                    rb.AddForce(
                        new Vector2(
                            UnityEngine.Random.Range(-1.5f, 1.5f),
                            UnityEngine.Random.Range(0.5f, 2f)),
                        ForceMode2D.Impulse);
                }

                return true;
            }
            catch (Exception e)
            {
                Log.LogError("Could not drop a kill pickup: " + e);
                return false;
            }
        }

        // Paint a borrowed pickup in the resource's own colour, so "no prefab
        // for this resource" still looks like something the game would draw.
        // `Resource.color` is a `ColorAsset` (a ScriptableObject wrapping one
        // Color); `resourceBarUnitColorFull` is a plain Color on the resource
        // itself and covers anything that has no ColorAsset.
        private static void Tint(ResourcePickup drop, Resource resource)
        {
            try
            {
                SpriteRenderer sr =
                    drop.GetComponentInChildren<SpriteRenderer>();

                if (sr == null)
                    return;

                Color c = (resource.color != null)
                    ? resource.color.color
                    : resource.resourceBarUnitColorFull;

                // An all-zero Color is "nobody filled this in", not black - and
                // painting a pickup transparent would be worse than the wrong
                // colour. Leave the prefab's own tint alone in that case.
                if (c.a <= 0f || (c.r <= 0f && c.g <= 0f && c.b <= 0f))
                    return;

                c.a = 1f;
                sr.color = c;
            }
            catch { }
        }

        // Prefer a pickup that already belongs to this resource - its art and
        // sound will be right. Otherwise borrow the orb and re-tint it, and SAY
        // SO: the payout is correct either way, but the art is a substitution
        // and there was previously no way at all to tell.
        private static ResourcePickup Template(Resource resource, out bool exact)
        {
            Search();

            ResourcePickup match;
            exact = _byResource.TryGetValue(resource, out match) &&
                    match != null;

            if (exact)
            {
                if (_reported.Add(resource))
                {
                    Log.LogInfo(
                        "Kill reward for " + resource.name + " drops '" +
                        match.name + "' - the prefab that already belongs to " +
                        "that resource, so the art is right.");
                }

                return match;
            }

            if (_template != null && _reported.Add(resource))
            {
                Log.LogWarning(
                    "Kill reward for " + resource.name + " has no pickup " +
                    "prefab of its own, so it borrows '" + _template.name +
                    "' and re-tints it to that resource's colour. It pays " +
                    resource.name + " correctly; only the SHAPE is borrowed. " +
                    "Every stock resource except Electron has its own pickup, " +
                    "so this normally means a modded resource.");
            }

            return (_template != null) ? _template : null;
        }

        // `Resources.FindObjectsOfTypeAll` rather than `FindObjectsByType`, and
        // that is half the fix: the first includes assets that are loaded but
        // not in the scene - which is what a prefab is - and the second does
        // not. The eight pickup prefabs are in memory because the drop tables
        // reference them, so this finds all eight on the first call, before
        // anything has ever dropped.
        //
        // The other half is `_scanned`, which is set ONLY when this actually
        // finds prefabs. It used to early-out on `_template != null &&
        // _byResource.Count > 0`, and the `Pickup.Start` postfix makes both true
        // the moment any crate drops anything - so the scan never ran and the
        // cache fell back to "whatever spawned in this room", which is exactly
        // the failure this method was written to remove.
        private static void Search()
        {
            if (_scanned)
                return;

            try
            {
                var all =
                    Resources.FindObjectsOfTypeAll<ResourcePickup>();

                int prefabs = 0;

                for (int i = 0; i < all.Length; i++)
                {
                    if (IsPrefab(all[i]))
                        prefabs++;

                    Learn(all[i]);
                }

                // Retried rather than latched: if it genuinely finds nothing (a
                // scene with no loot tables loaded yet), the next kill asks
                // again.
                if (prefabs == 0)
                    return;

                _scanned = true;

                var names = new List<string>();

                foreach (var pair in _byResource)
                {
                    if (pair.Key != null && pair.Value != null)
                        names.Add(pair.Value.name + " = " + pair.Key.name);
                }

                Log.LogInfo(
                    "Kill-reward pickups: scanned " + all.Length +
                    " ResourcePickup(s), " + prefabs + " of them prefabs, " +
                    "covering " + _byResource.Count + " resource(s): " +
                    (names.Count > 0
                        ? string.Join(", ", names.ToArray())
                        : "none named") +
                    ". Anything not in that list borrows '" +
                    (_template != null ? _template.name : "none") +
                    "' re-tinted to its own colour. Expect 9 prefabs and 7 " +
                    "resources - Money has three denominations, and Electron " +
                    "is the one stock resource with no pickup.");
            }
            catch (Exception e)
            {
                Log.LogError("Looking for pickup prefabs failed: " + e);
            }
        }

        // Kept, and still useful: a pickup the game spawns at runtime for a
        // resource no prefab covers (a Forge resource, say) teaches us a shape
        // the prefab scan cannot. It can only ADD to what the scan found - a
        // live instance never displaces a prefab, see `Learn`.
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
