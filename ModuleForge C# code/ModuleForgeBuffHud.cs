using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.UI;

namespace ModuleForge
{
    // "Show me on screen when a module is buffing me."
    //
    // A DELIBERATE COPY of Weapon Forge's ForgeBuffHud, not a borrow. Same call
    // as the loot pools: each mod has to work with the other absent, and a
    // display layer is not a decoder worth a dependency. The two coordinate
    // through exactly ONE shared string - the row name below - so a player with
    // both mods gets one row of icons rather than two. Rename it in one mod only
    // and you reintroduce exactly that bug.
    //
    // The log ID block is the other half of that coordination: Weapon Forge owns
    // 9100-9159 and this owns 9200-9259, so neither mod can clear the other's
    // entries (or the game's, which are 0-5). That is also what makes a weapon
    // buffed by BOTH mods show TWO lines, one per source, each wearing its own
    // icon - which is what was asked for, so you can see which module is
    // actually pulling its weight.
    //
    // Why the game's status line is the right home for this: it already carries
    // PERSISTENT status, not just messages. "Fuel low" is logged with no
    // duration (the expiry test is `duration > 0f && ...`, so zero never times
    // out), coloured and flashing, then cleared when it stops applying. A buff
    // is the same shape.
    public static class ModuleForgeBuffHud
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("ModuleForge.BuffHud");

        public enum Style { Off, Feed, Icons, Both }
        public enum Labels { EffectAmount, Seconds, Stacks, IconOnly }

        public static Style style = Style.Off;
        public static Labels labels = Labels.EffectAmount;
        public static bool showTimedStates;
        public static bool showDebuffs;

        // See the long note on Feed(): the feed must never be allowed to grow
        // over the ammo / fuel / health readouts.
        public static int maxLines = 3;

        // The icon widget is BORROWED from the minion counter, so it arrives at
        // whatever size that prefab was authored with. This is what actually
        // decides how big it looks in our row.
        public static float iconSize = 28f;

        private const int IdBase = 9200;
        private const int IdLimit = 9260;

        // Shared with Weapon Forge on purpose - see the note above.
        public const string RowName = "Forge Buff Indicators";

        public static Style ParseStyle(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "off":
                case "none":
                case "false": return Style.Off;
                case "icons":
                case "icon":
                case "row":   return Style.Icons;
                case "both":  return Style.Both;
                default:      return Style.Feed;
            }
        }

        public static Labels ParseLabels(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "seconds":
                case "time":
                case "countdown": return Labels.Seconds;
                case "stacks":
                case "count":     return Labels.Stacks;
                case "icon":
                case "icononly":
                case "none":      return Labels.IconOnly;
                default:          return Labels.EffectAmount;
            }
        }

        private class Entry
        {
            public int id;
            public Sprite icon;
            public Color color;
            public string text;
            public float endsAt;
            public bool debuff;
            public GameObject widget;
            public object textComponent;
        }

        private static readonly Dictionary<object, Entry> _live =
            new Dictionary<object, Entry>();

        private static int _nextId = IdBase;

        private static ShipHud _hud;
        private static Ship _ship;
        private static Transform _row;
        private static MinionCountWidget _widgetPrefab;
        private static bool _prefabLooked;

        public static void Bind(ShipHud hud, Ship ship)
        {
            _hud = hud;
            _ship = ship;
            _row = null;

            foreach (var e in _live.Values)
            {
                e.widget = null;
                e.textComponent = null;
            }

            _live.Clear();
            _logged.Clear();
        }

        // THE TRAP: ShipLogOutput.Clear and Update invoke LogRemoved WITHOUT a
        // null check, unlike Log, which guards it carefully. Clearing an entry
        // while no ShipLogDisplay is subscribed therefore throws from inside the
        // game's own code. A live ShipHud is what guarantees a subscriber
        // (ShipLogDisplay.Assign wires it), so everything goes through this.
        private static bool FeedReady
        {
            get
            {
                return _ship != null && _hud != null && _ship.LogOutput != null;
            }
        }

        public static void Show(
            object key, Sprite icon, Color color, string effect,
            float amount, int stacks, float endsAt, bool debuff)
        {
            if (style == Style.Off || key == null)
                return;

            if (debuff && !showDebuffs)
                return;

            try
            {
                Entry e;

                if (!_live.TryGetValue(key, out e))
                {
                    e = new Entry();
                    e.id = NextId();
                    _live[key] = e;
                }

                e.icon = icon;
                e.color = color;
                e.endsAt = endsAt;
                e.debuff = debuff;

                string text = Compose(effect, amount, stacks, endsAt);

                // Only repaint when the words change. ShipLogDisplay plays its
                // new-message sound on EVERY entry it shows, so re-logging each
                // frame would chirp continuously.
                if (text == e.text && e.widget != null)
                    return;

                e.text = text;

                if (style == Style.Feed || style == Style.Both)
                    Feed(e);

                if (style == Style.Icons || style == Style.Both)
                    Icons(e);
            }
            catch (Exception ex)
            {
                Log.LogError("Buff indicator failed: " + ex);
            }
        }

        public static void Clear(object key)
        {
            if (key == null)
                return;

            Entry e;

            if (!_live.TryGetValue(key, out e))
                return;

            _live.Remove(key);

            try
            {
                if (FeedReady)
                    _ship.LogOutput.Clear(e.id);
            }
            catch (Exception ex)
            {
                Log.LogError("Clearing a buff indicator failed: " + ex);
            }

            _logged.Remove(e.id);

            if (e.widget != null)
                UnityEngine.Object.Destroy(e.widget);
        }

        private static int NextId()
        {
            int id = _nextId++;

            if (_nextId >= IdLimit)
                _nextId = IdBase;

            return id;
        }

        private static string Compose(
            string effect, float amount, int stacks, float endsAt)
        {
            switch (labels)
            {
                case Labels.IconOnly:
                    return "";

                case Labels.Stacks:
                    return stacks > 1 ? (effect + " x" + stacks) : effect;

                case Labels.Seconds:
                    float left = Mathf.Max(0f, endsAt - Time.time);
                    return effect + " " + left.ToString("0.#") + "s";

                default:
                    string s = effect + " +" +
                               Mathf.RoundToInt(amount * 100f) + "%";
                    return stacks > 1 ? (s + " x" + stacks) : s;
            }
        }

        // Our own ShipLogEntry per id, captured from LogAdded, because
        // ShipLogOutput.Log does not hand the entry back and the display keys
        // its rows on the instance.
        private static readonly Dictionary<int, ShipLogEntry> _logged =
            new Dictionary<int, ShipLogEntry>();

        private static ShipLogOutput _subscribed;
        private static FieldInfo _rowsField;

        private static void Feed(Entry e)
        {
            if (!FeedReady)
                return;

            Watch();

            // UPDATE THE EXISTING ROW rather than logging again.
            //
            // Re-logging an id does NOT replace its row. ShipLogOutput.Log
            // always APPENDS a new ShipLogEntry; ShipLogDisplay.Show then
            // INSTANTIATES a fresh row and plays newMessageSfx; and the Clear()
            // meant to remove the old one calls LogEntry.Hide(), which is an
            // async void that runs a close animation and destroys the row
            // LATER. So every text change stacked a row on top of one still
            // fading out, and chirped while doing it. In a group fight that
            // filled the whole left side of the screen.
            if (TryUpdateRow(e))
                return;

            if (_logged.Count >= Mathf.Max(1, maxLines) &&
                !_logged.ContainsKey(e.id))
            {
                return;
            }

            ColorAsset tint = Tint(e.color);

            // duration 0 = never auto-expires, so the entry lives exactly as
            // long as the buff and we own its removal.
            _ship.LogOutput.Clear(e.id);
            _logged.Remove(e.id);

            _ship.LogOutput.Log(
                e.id,
                string.IsNullOrEmpty(e.text) ? " " : e.text,
                e.icon, 0f, tint, tint, e.debuff);
        }

        private static void Watch()
        {
            if (ReferenceEquals(_subscribed, _ship.LogOutput))
                return;

            if (_subscribed != null)
                _subscribed.LogAdded -= OnLogAdded;

            _subscribed = _ship.LogOutput;
            _subscribed.LogAdded += OnLogAdded;
        }

        private static void OnLogAdded(ShipLogEntry entry)
        {
            if (entry == null || entry.id < IdBase || entry.id >= IdLimit)
                return;

            _logged[entry.id] = entry;
        }

        private static bool TryUpdateRow(Entry e)
        {
            try
            {
                ShipLogEntry logged;

                if (!_logged.TryGetValue(e.id, out logged) || logged == null)
                    return false;

                if (_hud == null || _hud.logDisplay == null)
                    return false;

                if (_rowsField == null)
                {
                    _rowsField = HarmonyLib.AccessTools.Field(
                        typeof(ShipLogDisplay), "logEntries");
                }

                if (_rowsField == null)
                    return false;

                var rows = _rowsField.GetValue(_hud.logDisplay) as IDictionary;

                if (rows == null || !rows.Contains(logged))
                {
                    _logged.Remove(e.id);
                    return false;
                }

                var row = rows[logged] as Component;

                if (row == null)
                    return false;

                SetTextOn(row.gameObject, e.text);
                return true;
            }
            catch (Exception ex)
            {
                Log.LogWarning(
                    "Could not update a buff line in place (" + ex.Message +
                    ") - falling back to re-logging it.");
                return false;
            }
        }

        // The game wraps its own message in uppercase tags; matching that keeps
        // a re-written line looking like every other one.
        private static void SetTextOn(GameObject go, string text)
        {
            if (go == null)
                return;

            foreach (Component c in go.GetComponentsInChildren<Component>(true))
            {
                if (c == null)
                    continue;

                string n = c.GetType().Name;

                if (n != "TextMeshProUGUI" && n != "TMP_Text" && !(c is Text))
                    continue;

                PropertyInfo prop = c.GetType().GetProperty("text");

                if (prop == null)
                    continue;

                prop.SetValue(
                    c, "<uppercase>" + (text ?? "") + "</uppercase>", null);
                return;
            }
        }

        private static readonly Dictionary<int, ColorAsset> _tints =
            new Dictionary<int, ColorAsset>();

        private static ColorAsset Tint(Color c)
        {
            Color32 c32 = c;
            int key = c32.r << 16 | c32.g << 8 | c32.b;

            ColorAsset asset;

            if (_tints.TryGetValue(key, out asset) && asset != null)
                return asset;

            asset = ScriptableObject.CreateInstance<ColorAsset>();
            asset.color = c;
            UnityEngine.Object.DontDestroyOnLoad(asset);
            _tints[key] = asset;
            return asset;
        }

        // Deliberately NOT parented into resourceBarParent: ShipHud's
        // RefreshResourcePanelSize rewrites the anchoredPosition of every child
        // of that transform into a vertical stack, so a guest there gets
        // shuffled about by the game.
        private static Transform Row()
        {
            if (_row != null)
                return _row;

            if (_hud == null || _hud.minionsWidget == null)
                return null;

            Transform parent = _hud.minionsWidget.transform.parent;

            if (parent == null)
                return null;

            // Weapon Forge may already have made it. Finding it by name is the
            // whole coordination mechanism.
            Transform existing = parent.Find(RowName);

            if (existing != null)
            {
                _row = existing;
                return _row;
            }

            var go = new GameObject(RowName, typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var layout = go.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 4f;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;

            // childControl* must be ON or a child LayoutElement is ignored and
            // every widget keeps its authored prefab size - which is why the
            // borrowed icon arrived enormous.
            layout.childControlWidth = true;
            layout.childControlHeight = true;

            _row = go.transform;
            return _row;
        }

        private static MinionCountWidget WidgetPrefab()
        {
            if (_prefabLooked)
                return _widgetPrefab;

            _prefabLooked = true;

            // Borrowed rather than built: already an icon + count text +
            // animator, styled to match the HUD.
            try
            {
                if (_hud != null && _hud.minionsWidget != null)
                {
                    FieldInfo f = HarmonyLib.AccessTools.Field(
                        typeof(MinionsWidget), "minionCountWidgetPrefab");

                    if (f != null)
                    {
                        _widgetPrefab =
                            f.GetValue(_hud.minionsWidget) as MinionCountWidget;
                    }
                }
            }
            catch (Exception e)
            {
                Log.LogWarning(
                    "Could not borrow the minion count widget (" + e.Message +
                    ") - the icon row is unavailable, so buff indicators will " +
                    "use the status feed instead.");
            }

            return _widgetPrefab;
        }

        private static void Icons(Entry e)
        {
            Transform row = Row();
            MinionCountWidget prefab = WidgetPrefab();

            if (row == null || prefab == null)
            {
                if (style == Style.Icons)
                    Feed(e);

                return;
            }

            if (e.widget == null)
            {
                MinionCountWidget w =
                    UnityEngine.Object.Instantiate(prefab, row);

                e.widget = w.gameObject;
                e.widget.SetActive(true);

                Size(e.widget);
            }

            Image image = FindIcon(e.widget);

            if (image != null)
            {
                if (e.icon != null)
                    image.sprite = e.icon;

                image.color = e.color;
            }

            SetText(e, e.text);
        }

        // Pin one borrowed widget to a sane size. Both halves matter: the
        // LayoutElement is what the layout group reads, the RectTransform is
        // the fallback if the group is ever missing.
        private static void Size(GameObject go)
        {
            float side = Mathf.Clamp(iconSize, 8f, 128f);

            var le = go.GetComponent<LayoutElement>();

            if (le == null)
                le = go.AddComponent<LayoutElement>();

            le.preferredWidth = side;
            le.preferredHeight = side;
            le.minWidth = side;
            le.minHeight = side;
            le.flexibleWidth = 0f;
            le.flexibleHeight = 0f;

            var rt = go.transform as RectTransform;

            if (rt != null)
                rt.sizeDelta = new Vector2(side, side);
        }

        private static Image FindIcon(GameObject go)
        {
            Image[] all = go.GetComponentsInChildren<Image>(true);

            for (int i = 0; i < all.Length; i++)
                if (all[i] != null && all[i].gameObject != go)
                    return all[i];

            return all.Length > 0 ? all[0] : null;
        }

        // TMP_Text lives in an assembly this project does not reference, so the
        // label is set through reflection on whatever text component is there.
        private static void SetText(Entry e, string text)
        {
            try
            {
                if (e.textComponent == null)
                {
                    foreach (Component c in
                             e.widget.GetComponentsInChildren<Component>(true))
                    {
                        if (c == null)
                            continue;

                        if (c.GetType().Name == "TextMeshProUGUI" ||
                            c.GetType().Name == "TMP_Text" ||
                            c is Text)
                        {
                            e.textComponent = c;
                            break;
                        }
                    }
                }

                if (e.textComponent == null)
                    return;

                PropertyInfo p = e.textComponent.GetType().GetProperty("text");

                if (p != null)
                    p.SetValue(e.textComponent, text ?? "", null);
            }
            catch { }
        }
    }
}
