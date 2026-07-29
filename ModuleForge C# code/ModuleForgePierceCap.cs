using System.Collections.Generic;
using UnityEngine;

namespace ModuleForge
{
    // Added at spawn to a projectile owned by a ship carrying a pierce
    // module. Caps its piercing: after passing through `limit` enemies it
    // vanishes on the next contact. ModuleForgeProjectilePatch counts.
    public class ModuleForgePierceCap : MonoBehaviour
    {
        public int limit = 2;
        public float falloff;
        public bool explodeOnLimit;

        public readonly HashSet<object> seen = new HashSet<object>();
    }
}
