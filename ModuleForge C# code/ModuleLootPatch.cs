using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ModuleForge
{
    // Makes loot-enabled custom modules drop from crates. There is no global
    // "droppable" flag in the game: a module only drops if it is a member of a
    // DropTableWeightedGroup that a crate's DropTable references. So just
    // before any table-based loot roll, our modules are added into that
    // table's MODULE groups (the crate module pools). Only groups that already
    // contain module entries are touched, so resource/prefab-only tables (e.g.
    // enemy drops) are left alone.
    //
    // Hooking SelectLoot (rather than editing group assets at startup)
    // guarantees the groups are loaded - they arrive as the argument.
    [HarmonyPatch(typeof(LootSelector), "SelectLoot")]
    public class ModuleLootPatch
    {
        private static readonly ManualLogSource Log =
            ModuleForgeLog.Source("ModuleForge");

        // Groups we've already augmented, so we add our modules once.
        private static readonly HashSet<DropTableWeightedGroup> _done =
            new HashSet<DropTableWeightedGroup>();

        private static FieldInfo _groupField;
        private static FieldInfo _useGroupField;

        static void Prefix(DropTable dropTable)
        {
            try
            {
                if (dropTable == null || dropTable.items == null)
                    return;

                // Crates the game gives no module roll at all (Money, Level 2)
                // gain one here - but only if a module actually asked for that
                // crate, so an untargeted crate is left exactly as the game
                // shipped it. Must run BEFORE the loop below: it appends to the
                // very list that loop walks.
                Graft(dropTable);

                if (_groupField == null)
                {
                    _groupField =
                        typeof(DropTableItem).GetField(
                            "group",
                            BindingFlags.NonPublic |
                            BindingFlags.Instance);

                    _useGroupField =
                        typeof(DropTableItem).GetField(
                            "useGroup",
                            BindingFlags.NonPublic |
                            BindingFlags.Instance);
                }

                if (_groupField == null)
                    return;

                foreach (DropTableItem item in dropTable.items)
                {
                    // useGroup is the flag that decides whether the group is
                    // ROLLED. Unity serializes the group reference even when it
                    // is off (it is a ConditionalField), so plenty of tables
                    // point at a pool they never draw from - the Level 2 crate,
                    // every Box, and ten enemy tables all do. Injecting into
                    // those achieves nothing while the log claims otherwise,
                    // which is exactly how this mod used to behave.
                    if (_useGroupField != null)
                    {
                        object flag = _useGroupField.GetValue(item);

                        if (flag is bool && !(bool)flag)
                            continue;
                    }

                    var group =
                        _groupField.GetValue(item)
                            as DropTableWeightedGroup;

                    if (group != null)
                        Augment(group);
                }
            }
            catch (Exception e)
            {
                Log.LogError("Module loot injection failed: " + e);
            }
        }

        // Tables we have already extended, and the pools we created.
        private static readonly HashSet<DropTable> _grafted =
            new HashSet<DropTable>();

        private static readonly Dictionary<string, DropTableWeightedGroup>
            _madePools =
                new Dictionary<string, DropTableWeightedGroup>(
                    StringComparer.OrdinalIgnoreCase);

        // Give a crate a module roll it does not normally have.
        //
        // ADDITIVE ON PURPOSE. Crate Money is four fixed prefab drops with no
        // group anywhere, and Crate Level 2's one group slot is already spent
        // on an Ingredient - so there is nothing to simply "switch on" in
        // either. Rather than repurposing an existing entry (which would take
        // something away), a brand new DropTableItem is appended: every normal
        // drop survives and the crate just also yields a module.
        private static void Graft(DropTable dropTable)
        {
            if (_grafted.Contains(dropTable))
                return;

            string pool = ModuleLootPools.GraftPoolFor(dropTable.name);

            if (pool == null)
                return;

            // EXPLICIT opt-in only. Targets() treats "no lootFrom" as "every
            // pool", which is right for injecting but wrong here: it would mean
            // any loot module at all silently altered Money and Level 2 crates
            // for the whole run. Grafting has to be something the file asked
            // for by name.
            bool wanted = false;

            foreach (ModuleEntry entry in ModuleForgeRegistry.Entries)
            {
                if (!entry.inLoot || entry.module == null ||
                    entry.lootGroups == null || entry.lootGroups.Length == 0)
                {
                    continue;
                }

                if (Targets(entry, pool))
                {
                    wanted = true;
                    break;
                }
            }

            if (!wanted)
                return;

            _grafted.Add(dropTable);

            DropTableWeightedGroup group = ResolvePool(pool);

            if (group == null)
                return;

            // With WeaponForge installed, IT may have grafted this very table
            // already - both mods target the same pool names on purpose. A
            // second DropTableItem would make the crate roll modules TWICE,
            // which reads as a duplicate-drop bug. Share the existing roll
            // instead; Augment below still adds our modules to the same pool.
            if (TableAlreadyRolls(dropTable, group))
            {
                Log.LogInfo(
                    "'" + dropTable.name + "' already has a module roll from '" +
                    group.name + "' (Weapon Forge got there first) - sharing it " +
                    "rather than adding a second one.");
                return;
            }

            if (!AppendGroupRoll(dropTable, group))
                return;

            Log.LogInfo(
                "Gave '" + dropTable.name + "' a module roll from '" +
                group.name + "' - the crate keeps everything it normally " +
                "drops and gains a module on top." +
                (pool == ModuleLootPools.Level2
                    ? " Side effect: this also revives the 5 stock " +
                      "regen/generator modules in that pool, which the game " +
                      "otherwise never rolls."
                    : string.Empty));
        }

        // Does this table already roll the given pool? Covers both a stock
        // entry and one the other mod appended a moment ago.
        private static bool TableAlreadyRolls(
            DropTable dropTable, DropTableWeightedGroup group)
        {
            if (_groupField == null)
            {
                _groupField =
                    typeof(DropTableItem).GetField(
                        "group",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                _useGroupField =
                    typeof(DropTableItem).GetField(
                        "useGroup",
                        BindingFlags.NonPublic | BindingFlags.Instance);
            }

            if (_groupField == null)
                return false;

            foreach (DropTableItem item in dropTable.items)
            {
                if (_useGroupField != null)
                {
                    object flag = _useGroupField.GetValue(item);

                    if (flag is bool && !(bool)flag)
                        continue;
                }

                var existing =
                    _groupField.GetValue(item) as DropTableWeightedGroup;

                if (existing == group)
                    return true;
            }

            return false;
        }

        // The existing asset where there is one, otherwise a pool of our own.
        //
        // FindObjectsOfTypeAll sees HideAndDontSave objects, so if WeaponForge
        // created the Money pool first this finds THAT instance and both mods
        // end up filling one pool - which is the whole point of sharing the
        // name.
        private static DropTableWeightedGroup ResolvePool(string pool)
        {
            var existing =
                ForgeAssets.FindAsset(
                    typeof(DropTableWeightedGroup), pool)
                    as DropTableWeightedGroup;

            if (existing != null)
                return existing;

            DropTableWeightedGroup made;

            if (_madePools.TryGetValue(pool, out made) && made != null)
                return made;

            made = ScriptableObject.CreateInstance<DropTableWeightedGroup>();
            made.name = pool;
            made.hideFlags = HideFlags.HideAndDontSave;

            _madePools[pool] = made;
            return made;
        }

        // Build and append the DropTableItem. Its fields are private
        // [SerializeField] on a STRUCT, so it is boxed, filled, then unboxed
        // back into the list.
        private static bool AppendGroupRoll(
            DropTable dropTable, DropTableWeightedGroup group)
        {
            try
            {
                var t = typeof(DropTableItem);
                var f = BindingFlags.NonPublic | BindingFlags.Instance;

                object boxed = new DropTableItem();

                // Probability + 1 = "always exactly one", which is how every
                // crate that DOES roll modules is set up.
                t.GetField("countSource", f)
                    .SetValue(boxed, DropTableItemCountSource.Probability);
                t.GetField("probability", f).SetValue(boxed, 1f);
                t.GetField("useGroup", f).SetValue(boxed, true);
                t.GetField("group", f).SetValue(boxed, group);

                dropTable.items.Add((DropTableItem)boxed);
                return true;
            }
            catch (Exception e)
            {
                Log.LogError(
                    "Could not add a module roll to '" + dropTable.name +
                    "': " + e);
                return false;
            }
        }

        private static bool Targets(ModuleEntry entry, string groupName)
        {
            if (entry.lootGroups == null || entry.lootGroups.Length == 0)
                return true;

            for (int i = 0; i < entry.lootGroups.Length; i++)
            {
                if (string.Equals(
                        entry.lootGroups[i],
                        groupName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static void Augment(DropTableWeightedGroup group)
        {
            if (!_done.Add(group))
                return;   // already processed this group

            var dist = group.itemDistribution;

            if (dist == null)
                return;

            // Only inject into groups that are MODULE pools, and skip any of
            // our modules already present.
            bool hasModuleEntry = false;
            var present =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var distItem in dist.Items)
            {
                if (distItem.Value.droppableType !=
                    DroppabbleType.Module)
                {
                    continue;
                }

                hasModuleEntry = true;

                if (distItem.Value.module != null &&
                    distItem.Value.module.Id != null)
                {
                    present.Add(distItem.Value.module.Id);
                }
            }

            // A grafted pool starts EMPTY, so it has no module entry to
            // recognise yet - it exists precisely to be filled here. Asked by
            // pool name rather than "did I create it", because with both mods
            // installed the other one may have created it. Every other
            // empty-of-modules pool is a resource/prefab one and is left alone.
            if (!hasModuleEntry && !ModuleLootPools.IsGraftPool(group.name))
                return;   // resource/prefab pool - leave it alone

            int added = 0;

            foreach (ModuleEntry entry in ModuleForgeRegistry.Entries)
            {
                if (!entry.inLoot || entry.module == null)
                    continue;

                if (present.Contains(entry.module.Id))
                    continue;

                // No list = every pool, which is what "loot" meant before
                // lootFrom existed.
                if (!Targets(entry, group.name))
                    continue;

                dist.Add(
                    new DroppabbleItem
                    {
                        droppableType = DroppabbleType.Module,
                        module = entry.module
                    },
                    entry.lootWeight);

                added++;
            }

            if (added > 0)
            {
                Log.LogInfo(
                    "Added " + added +
                    " custom module(s) to drop group '" +
                    group.name + "'.");
            }
        }
    }
}
