using System;
using System.Reflection;
using BepInEx.Logging;

namespace ModuleForge
{
    // THE ONE OWNER of "have I already said this?".
    //
    // A DELIBERATE COPY of `WeaponForge.ForgeDiagnosticGates`, and the same
    // call as `ModuleForgeUnitTint` and `ModuleForgeBuffHud`: resetting this
    // mod's own diagnostics is not a decoder worth a dependency, and each mod
    // has to work with the other absent. The two sweep their own assemblies and
    // can never collide.
    //
    // THE PROBLEM: a warning about a misconfigured module is gated so it is
    // said once rather than once per hit - and that gate is per SESSION, while
    // the thing it describes is per RUN. Die, start again, and the warning is
    // already spent, so the run where you go looking for the log line is
    // exactly the run that cannot produce it. There are four of them here
    // (`ModuleForgeCrit`, `ModuleForgeCritPatch`, `ModuleForgeKillPickup`,
    // `ModuleForgeLeech`) and none had a reset.
    //
    // Swept by NAMING CONVENTION rather than by a list, because the
    // hand-written version is what drifts: a per-file reset only helps if
    // someone remembers to write it AND to add the call. A gate added next
    // month is covered the moment it is named.
    //
    // It deliberately OVER-RESETS. `_saidLookClash` describes a cross-mod
    // ownership announcement that cannot change within a session, so re-saying
    // it costs one repeated line per run. Under-resetting costs a diagnostic at
    // the moment it is wanted, on a project where several runs per session is
    // the normal way to test.
    public static class ModuleForgeDiagnosticGates
    {
        private static readonly ManualLogSource Log =
            ModuleForgeLog.Source("ModuleForge");

        private static FieldInfo[] _gates;

        // Resetting diagnostics must never be able to cost a run, so every
        // layer is guarded separately: a bad type cannot stop the sweep, and a
        // failed sweep cannot stop the reset list it is called from.
        public static void Reset()
        {
            try
            {
                Ensure();

                if (_gates == null)
                    return;

                for (int i = 0; i < _gates.Length; i++)
                    Clear(_gates[i]);
            }
            catch (Exception e)
            {
                Log.LogWarning(
                    "Could not reset the once-only log gates (" + e.Message +
                    "). Harmless in itself, but warnings already said this " +
                    "session will stay silent for the rest of it.");
            }
        }

        private static void Clear(FieldInfo f)
        {
            try
            {
                if (f.FieldType == typeof(bool))
                {
                    f.SetValue(null, false);
                    return;
                }

                object set = f.GetValue(null);

                if (set == null)
                    return;

                MethodInfo clear = ClearMethod(f.FieldType);

                if (clear != null)
                    clear.Invoke(set, null);
            }
            catch
            {
                // One unreachable gate is not worth a line of its own; the
                // others still reset.
            }
        }

        private static void Ensure()
        {
            if (_gates != null)
                return;

            var found =
                new System.Collections.Generic.List<FieldInfo>();

            Type[] types;

            try
            {
                types = typeof(ModuleForgeDiagnosticGates).Assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                // Partial answer beats none: whatever loaded still gets swept.
                types = e.Types ?? new Type[0];
            }

            for (int i = 0; i < types.Length; i++)
            {
                Type t = types[i];

                if (t == null)
                    continue;

                FieldInfo[] fields;

                try
                {
                    fields = t.GetFields(
                        BindingFlags.Static |
                        BindingFlags.NonPublic |
                        BindingFlags.Public |
                        BindingFlags.DeclaredOnly);
                }
                catch
                {
                    continue;
                }

                for (int j = 0; j < fields.Length; j++)
                {
                    if (IsGate(fields[j]))
                        found.Add(fields[j]);
                }
            }

            _gates = found.ToArray();

            int bools = 0;

            for (int i = 0; i < _gates.Length; i++)
                if (_gates[i].FieldType == typeof(bool))
                    bools++;

            // Both kinds counted separately, because a single total is not
            // checkable: Weapon Forge's copy of this class printed "20" while
            // the source held 26, and the six it dropped were every one of the
            // keyed sets. A count is evidence only when it can be compared with
            // a count derived another way - here, a grep of the declarations.
            Log.LogInfo(
                "Once-only log gates found: " + _gates.Length +
                " (" + bools + " one-shot, " + (_gates.Length - bools) +
                " keyed) - reset at each run entry, so a second run in one " +
                "session still reports its own warnings.");
        }

        private static bool IsGate(FieldInfo f)
        {
            if (f.IsLiteral)
                return false;

            string n = f.Name;

            if (!(n.StartsWith("_said") ||
                  n.StartsWith("_warned") ||
                  n.StartsWith("_told")))
            {
                return false;
            }

            // A readonly bool would throw on assignment; a readonly SET is
            // fine because it is emptied in place.
            if (f.FieldType == typeof(bool))
                return !f.IsInitOnly;

            return ClearMethod(f.FieldType) != null;
        }

        // `HashSet<T>` DOES NOT IMPLEMENT THE NON-GENERIC `ICollection`.
        //
        // Weapon Forge's first cut of this class tested
        // `typeof(ICollection).IsAssignableFrom(fieldType)`, which reads as a
        // safely conservative "is this a collection" and passes `List<T>` and
        // `Dictionary<K,V>` - but not `HashSet<T>`, which is what every keyed
        // gate in either mod actually is. Test the capability being used
        // instead: a public parameterless instance `Clear()`. It is also the
        // method the reset then calls, so acceptance and action cannot
        // disagree - which is precisely how the first version went wrong.
        private static MethodInfo ClearMethod(Type t)
        {
            if (t == null || t.IsValueType)
                return null;

            try
            {
                MethodInfo m = t.GetMethod(
                    "Clear",
                    BindingFlags.Instance | BindingFlags.Public,
                    null,
                    Type.EmptyTypes,
                    null);

                return (m != null && m.ReturnType == typeof(void)) ? m : null;
            }
            catch
            {
                return null;
            }
        }
    }
}
