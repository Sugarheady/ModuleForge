using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace ModuleForge
{
    // ONE OWNER FOR A UNIT'S SPRITE COLOUR.
    //
    // A DELIBERATE COPY of Weapon Forge's ForgeUnitTint, for the same reason
    // ModuleForgeBuffHud and ModuleLootPools are copies: each mod has to work
    // with the other absent, and this is a display layer rather than a decoder
    // worth a cross-mod dependency. When Weapon Forge IS installed it owns the
    // crit patch outright (see ModuleForgeCritCompat), so this class and its
    // twin are never live at the same time and cannot fight.
    //
    // WHY IT IS SHAPED THIS WAY, rather than "remember sr.color and write it
    // back". That is correct for exactly one effect at a time and silently
    // wrong for two:
    //
    //     effect A starts   remembers ORIGINAL,        writes ORIGINAL * blue
    //     effect B starts   remembers ORIGINAL*blue,   writes ORIGINAL * blue * red
    //     effect A ends     writes back ORIGINAL        <- the red is gone
    //     effect B ends     writes back ORIGINAL*blue   <- THE BLUE IS PERMANENT
    //
    // The unit stays blue for the rest of the run. So instead: ONE captured base
    // per renderer, captured once and never from a tinted state, and every
    // feature publishes a named LAYER. The result is the base times the product
    // of the live layers. Multiplication is commutative, so there is no ordering
    // to get wrong and no restore path that can bake somebody else's colour in.
    //
    // ALPHA IS NEVER TOUCHED. A renderer's alpha is a visibility decision, not a
    // colour: every Larva and Maggot carries a full-body `HighlightSprite` held
    // at alpha 0 that the damage flash drives, and painting that opaque makes a
    // unit wear its own outline. So layers carry RGB only, and each write keeps
    // whatever alpha the renderer has AT THAT MOMENT.
    public class ModuleForgeUnitTint : MonoBehaviour
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("ModuleForge.Tint");

        // Keys used by this mod's own features. Strings rather than an enum so a
        // new feature needs no change here.
        public const string Crit = "crit";

        private class Layer
        {
            public string key;
            public Color color;
            public float endsAt;      // 0 = until explicitly cleared
            public float pulseHz;     // 0 = steady

            // HOW HARD THE COLOUR COMES THROUGH, 0..1.
            //
            // His R13 ask, and the same change is in Weapon Forge's own copy of
            // this class - the two mods duplicate by design so either works
            // alone. Cheap here because `Product` was already lerping from "no
            // tint" toward the colour; the pulse was just the only thing
            // allowed to drive that lerp. 1 is exactly the old look.
            public float strength = 1f;
        }

        private readonly List<Layer> _layers = new List<Layer>();

        private readonly List<SpriteRenderer> _renderers =
            new List<SpriteRenderer>();

        private readonly List<Color> _base = new List<Color>();

        private bool _captured;

        // ------------------------------------------------------------------
        // Publishing a layer
        // ------------------------------------------------------------------

        // `seconds` of 0 or less means "until Clear", which is what a held
        // effect wants. Calling this again with the same key REPLACES that layer
        // rather than adding another - being crit twice in a row should refresh
        // the flash, not double the colour.
        public static void Set(
            Unit unit, string key, Color color, float seconds, float pulseHz)
        {
            Set(unit, key, color, seconds, pulseHz, 1f);
        }

        // `strength` is 0..1: 0 leaves the unit alone, 1 is the full colour.
        public static void Set(
            Unit unit, string key, Color color, float seconds, float pulseHz,
            float strength)
        {
            if (unit == null || string.IsNullOrEmpty(key))
                return;

            try
            {
                ModuleForgeUnitTint t =
                    unit.gameObject.GetComponent<ModuleForgeUnitTint>();

                if (t == null)
                    t = unit.gameObject.AddComponent<ModuleForgeUnitTint>();

                t.Capture();

                Layer layer = t.Find(key);

                if (layer == null)
                {
                    layer = new Layer { key = key };
                    t._layers.Add(layer);
                }

                layer.color = color;
                layer.pulseHz = Mathf.Max(0f, pulseHz);
                layer.endsAt = (seconds > 0f) ? Time.time + seconds : 0f;
                layer.strength = Mathf.Clamp01(strength);

                t.Write();
            }
            catch (Exception e)
            {
                Log.LogError("Tinting a unit failed: " + e);
            }
        }

        public static void Clear(Unit unit, string key)
        {
            if (unit == null || string.IsNullOrEmpty(key))
                return;

            try
            {
                var t = unit.gameObject.GetComponent<ModuleForgeUnitTint>();

                if (t == null)
                    return;

                Layer layer = t.Find(key);

                if (layer == null)
                    return;

                t._layers.Remove(layer);
                t.Write();

                if (t._layers.Count == 0)
                    Destroy(t);
            }
            catch (Exception e)
            {
                Log.LogError("Clearing a unit tint failed: " + e);
            }
        }

        private Layer Find(string key)
        {
            for (int i = 0; i < _layers.Count; i++)
            {
                if (_layers[i].key == key)
                    return _layers[i];
            }

            return null;
        }

        // ------------------------------------------------------------------
        // The renderers
        // ------------------------------------------------------------------

        // Scoped to the prefab's `Visual` subtree: a unit's AI state indicators
        // are SpriteRenderers too and are deliberately colour coded, so tinting
        // everything paints over the AI's own tells.
        private void Capture()
        {
            if (_captured)
                return;

            _captured = true;

            Transform visual = FindVisual(transform);

            SpriteRenderer[] found = (visual != null)
                ? visual.GetComponentsInChildren<SpriteRenderer>(true)
                : GetComponentsInChildren<SpriteRenderer>(true);

            for (int i = 0; i < found.Length; i++)
            {
                SpriteRenderer sr = found[i];

                if (sr == null)
                    continue;

                // Never the damage-flash silhouette, and - when there is no
                // Visual subtree to scope us - never an AI state sprite.
                if (IsHighlight(sr.transform))
                    continue;

                if (visual == null && IsStateSprite(sr.transform))
                    continue;

                _renderers.Add(sr);
                _base.Add(sr.color);
            }
        }

        // ------------------------------------------------------------------
        private void Update()
        {
            bool dropped = false;

            for (int i = _layers.Count - 1; i >= 0; i--)
            {
                Layer l = _layers[i];

                if (l.endsAt > 0f && Time.time >= l.endsAt)
                {
                    _layers.RemoveAt(i);
                    dropped = true;
                }
            }

            if (_layers.Count == 0)
            {
                if (dropped)
                    Write();          // puts the base colours back

                Destroy(this);
                return;
            }

            // Re-asserted every frame rather than written once. A unit's
            // renderers are few, and something else touching a colour (an
            // animator, a damage flash) would otherwise silently drop the tint
            // until the next Set - the kind of bug that looks like the feature
            // never ran.
            Write();
        }

        private void OnDestroy()
        {
            // Whatever happens - the unit dying, the component being removed,
            // an exception upstream - the base colours go back.
            try
            {
                _layers.Clear();
                Write();
            }
            catch (Exception) { }
        }

        private void Write()
        {
            Color product = Product();

            for (int i = 0; i < _renderers.Count; i++)
            {
                SpriteRenderer sr = _renderers[i];

                if (sr == null)
                    continue;

                Color b = _base[i];

                // The renderer's CURRENT alpha, not the captured one - so
                // anything driving alpha at the same time is preserved rather
                // than reverted every frame.
                sr.color = new Color(
                    b.r * product.r,
                    b.g * product.g,
                    b.b * product.b,
                    sr.color.a);
            }
        }

        // Every live layer multiplied together. Commutative, so no ordering and
        // no priorities - two features cannot fight over who wins.
        private Color Product()
        {
            float r = 1f, g = 1f, b = 1f;

            for (int i = 0; i < _layers.Count; i++)
            {
                Layer l = _layers[i];

                float strength = l.strength;

                if (l.pulseHz > 0f)
                {
                    // 0..1, so the pulse runs between "no tint at all" and the
                    // full colour rather than between two tints.
                    // Multiplies rather than replaces: a half-strength
                    // pulsing tint should still pulse, between nothing and
                    // half. Setting one must never switch the other off.
                    strength *= 0.5f + 0.5f *
                        Mathf.Sin(Time.time * l.pulseHz * Mathf.PI * 2f);
                }

                r *= Mathf.Lerp(1f, l.color.r, strength);
                g *= Mathf.Lerp(1f, l.color.g, strength);
                b *= Mathf.Lerp(1f, l.color.b, strength);
            }

            return new Color(r, g, b, 1f);
        }

        // ------------------------------------------------------------------
        // The two scoping rules, inlined from Weapon Forge's ForgeMinionLook so
        // this file stands alone. Keep them identical in both mods: they encode
        // which renderers a unit's prefab means to be recoloured, which is a
        // fact about the GAME, not about either mod.
        // ------------------------------------------------------------------

        private static readonly HashSet<string> StateNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "idle", "move", "wait", "aggro", "agro", "seek", "regroup",
                "flee", "search", "charge", "entry", "aim move", "move away",
                "move_dodge", "combat", "investigate",
                "gottolastknownposition"
            };

        private static bool IsHighlight(Transform t)
        {
            return t != null &&
                   t.name.IndexOf(
                       "highlight",
                       StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static Transform FindVisual(Transform root)
        {
            // Direct children first - that is where it always is - then a full
            // walk, so an unusual prefab still resolves.
            for (int i = 0; i < root.childCount; i++)
            {
                Transform c = root.GetChild(i);

                if (c != null && c.name != null &&
                    c.name.StartsWith("Visual",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return c;
                }
            }

            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t != null && t.name != null &&
                    t.name.StartsWith("Visual",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return t;
                }
            }

            return null;
        }

        private static bool IsStateSprite(Transform t)
        {
            for (Transform p = t; p != null; p = p.parent)
            {
                if (p.name == null)
                    continue;

                if (StateNames.Contains(p.name.Trim()))
                    return true;

                if (p.name.StartsWith("AI_", StringComparison.OrdinalIgnoreCase) ||
                    p.name.StartsWith("AiS ", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
