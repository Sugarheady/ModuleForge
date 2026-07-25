using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using UnityEngine;

namespace ModuleForge
{
    // One built custom module + its availability settings.
    public class ModuleEntry
    {
        public string name;
        public ModuleData module;
        public bool inLoot;
        public bool inShop;
        public float lootWeight;
        public float shopPrice;
        public int shopUnlockLevel;
    }

    // Builds custom modules from the "modules" folder and registers them
    // into the game's ModuleRegistry (required for drop/shop content to
    // rehydrate on save/load). Mirrors WeaponForge's ForgeRegistry.
    public static class ModuleForgeRegistry
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("ModuleForge");

        private static readonly List<ModuleEntry> _entries =
            new List<ModuleEntry>();

        private static readonly HashSet<string> _builtNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static IEnumerable<ModuleEntry> Entries
        {
            get { return _entries; }
        }

        public static string ModulesFolder()
        {
            return Path.Combine(
                Path.GetDirectoryName(
                    Assembly.GetExecutingAssembly().Location),
                "modules");
        }

        // Scan + build any module not built yet. Idempotent.
        public static void BuildAll()
        {
            string folder = ModulesFolder();

            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
                StarterFiles.Write(folder);
            }

            string[] files =
                Directory.GetFiles(folder, "*.json")
                    .OrderBy(x => x)
                    .ToArray();

            foreach (string file in files)
            {
                try
                {
                    ModuleEntry entry =
                        ModuleBuilder.BuildModule(file, _builtNames);

                    if (entry != null)
                    {
                        _entries.Add(entry);
                        _builtNames.Add(entry.name);
                    }
                }
                catch (Exception e)
                {
                    Log.LogError(
                        "Failed to build module from " +
                        Path.GetFileName(file) + ": " + e);
                }
            }
        }

        // Ensure every built module is in the ModuleRegistry the save
        // system reads. Cheap; safe to call repeatedly.
        public static void RegisterInto(ModuleRegistry registry)
        {
            if (registry == null)
                return;

            FieldInfo itemListField =
                typeof(ScriptableObjectRegistry<ModuleData, string>)
                    .GetField(
                        "itemList",
                        BindingFlags.NonPublic | BindingFlags.Instance);

            if (itemListField == null)
            {
                Log.LogError("ModuleRegistry itemList field not found.");
                return;
            }

            IList itemList =
                itemListField.GetValue(registry) as IList;

            if (itemList == null)
                return;

            var present =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (object existing in itemList)
            {
                var md = existing as ModuleData;

                if (md != null && md.Id != null)
                    present.Add(md.Id);
            }

            bool changed = false;

            foreach (ModuleEntry entry in _entries)
            {
                if (entry.module == null)
                    continue;

                if (present.Contains(entry.module.Id))
                    continue;

                itemList.Add(entry.module);
                present.Add(entry.module.Id);
                changed = true;

                Log.LogInfo(
                    "Registered module '" + entry.module.Id + "'.");
            }

            if (changed)
                registry.Initialize();
        }
    }
}
