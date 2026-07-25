using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;

namespace ModuleForge
{
    // Adds loot-enabled custom modules to crate drop pools, the same way
    // WeaponForge does for weapons: just before a table-based loot roll,
    // inject the module into that table's MODULE groups (only groups that
    // already contain module entries, so resource/prefab drops are left
    // alone). Hooking SelectLoot guarantees the groups are loaded.
    [HarmonyPatch(typeof(LootSelector), "SelectLoot")]
    public class ModuleLootPatch
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("ModuleForge");

        private static readonly HashSet<DropTableWeightedGroup> _done =
            new HashSet<DropTableWeightedGroup>();

        private static FieldInfo _groupField;

        static void Prefix(DropTable dropTable)
        {
            try
            {
                if (dropTable == null || dropTable.items == null)
                    return;

                if (_groupField == null)
                {
                    _groupField =
                        typeof(DropTableItem).GetField(
                            "group",
                            BindingFlags.NonPublic |
                            BindingFlags.Instance);
                }

                if (_groupField == null)
                    return;

                foreach (DropTableItem item in dropTable.items)
                {
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

        private static void Augment(DropTableWeightedGroup group)
        {
            if (!_done.Add(group))
                return;

            var dist = group.itemDistribution;

            if (dist == null)
                return;

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

            if (!hasModuleEntry)
                return;

            int added = 0;

            foreach (ModuleEntry entry in ModuleForgeRegistry.Entries)
            {
                if (!entry.inLoot || entry.module == null)
                    continue;

                if (present.Contains(entry.module.Id))
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
