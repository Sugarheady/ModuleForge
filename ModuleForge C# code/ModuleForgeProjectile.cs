using System.Collections.Generic;

namespace ModuleForge
{
    // Owner-keyed registries for the projectile-modifying modules
    // (phasing + capped piercing). A module registers the ship it's
    // installed on; ModuleForgeProjectilePatch then applies the effect to
    // that ship's projectiles at spawn. Mirrors ModuleForgeBurn's
    // owner-refcount + per-run reset approach (modules aren't uninstalled
    // on death, so state is reset at each run entry).
    public static class ModuleForgeProjectile
    {
        // ---- Phasing (pass through terrain) ----
        private static readonly Dictionary<Unit.Data, int> _phasing =
            new Dictionary<Unit.Data, int>();

        public static void AddPhasing(Unit.Data owner)
        {
            if (owner == null) return;
            int n;
            _phasing.TryGetValue(owner, out n);
            _phasing[owner] = n + 1;
        }

        public static void RemovePhasing(Unit.Data owner)
        {
            if (owner == null) return;
            int n;
            if (_phasing.TryGetValue(owner, out n))
            {
                if (n <= 1) _phasing.Remove(owner);
                else _phasing[owner] = n - 1;
            }
        }

        public static bool IsPhasing(Unit.Data owner)
        {
            return owner != null && _phasing.ContainsKey(owner);
        }

        // ---- Capped piercing ----
        private class PierceInfo
        {
            public int cap;       // summed across equipped pierce modules
            public float falloff; // max across modules
            public bool explode;  // any module wants it
            public int refs;
        }

        private static readonly Dictionary<Unit.Data, PierceInfo> _pierce =
            new Dictionary<Unit.Data, PierceInfo>();

        public static void AddPierce(
            Unit.Data owner, int cap, float falloff, bool explode)
        {
            if (owner == null) return;

            PierceInfo info;
            if (_pierce.TryGetValue(owner, out info))
            {
                info.cap += cap;
                info.refs++;
                if (falloff > info.falloff) info.falloff = falloff;
                info.explode = info.explode || explode;
            }
            else
            {
                _pierce[owner] = new PierceInfo
                {
                    cap = cap, falloff = falloff, explode = explode, refs = 1
                };
            }
        }

        public static void RemovePierce(Unit.Data owner, int cap)
        {
            if (owner == null) return;

            PierceInfo info;
            if (_pierce.TryGetValue(owner, out info))
            {
                info.cap -= cap;
                info.refs--;
                if (info.refs <= 0)
                    _pierce.Remove(owner);
            }
        }

        public static bool TryGetPierce(
            Unit.Data owner, out int cap, out float falloff, out bool explode)
        {
            cap = 0; falloff = 0f; explode = false;

            PierceInfo info;
            if (owner != null && _pierce.TryGetValue(owner, out info))
            {
                cap = info.cap;
                falloff = info.falloff;
                explode = info.explode;
                return true;
            }
            return false;
        }

        // ---- Aggregates for the weapon stat card ----
        // Only the player installs these modules, so a global view == the
        // player's total (same assumption ModuleForgeBurn.Delta relies on).
        public static bool AnyPhasing
        {
            get { return _phasing.Count > 0; }
        }

        public static int PierceCapTotal
        {
            get
            {
                int total = 0;
                foreach (var kv in _pierce)
                    total += kv.Value.cap;
                return total;
            }
        }

        public static void Reset()
        {
            _phasing.Clear();
            _pierce.Clear();
        }
    }
}
