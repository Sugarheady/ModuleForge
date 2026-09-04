using System;
using System.Collections.Generic;
using UnityEngine;

namespace ModuleForge
{
    // Infinite ammo, and the instant reload that goes with it.
    //
    // Two shapes, both asked for: a BUFF (a window after each kill) and a
    // PASSIVE (always on). The trigger plumbing is the same one stealth and
    // last stand use, which is why this is a small file.
    //
    // The dangerous part lives in ModuleForgeAmmo - read the header there
    // before touching any of this. The short version: the obvious field,
    // `Unit.Data.HasInfiniteResource`, makes the ship INVULNERABLE, because it
    // flags every tank including health and an infinite tank refuses to go
    // down. This never touches it.
    [Serializable]
    public class AmmoEffect
        : ModuleEffect, IHasDescriptionForUnit, ModuleForgeKills.IKillListener
    {
        public const string Kill = "kill";
        public const string Always = "always";

        public string trigger = Kill;

        // How long the window lasts after a kill. A series, so a level can buy
        // a longer buff.
        public FloatSeries seconds;

        // Shots cost nothing while the window is open.
        public bool infinite = true;

        // Top every ammo tank up at the moment of the kill. Instantaneous, so
        // it pairs with a short window or with none at all.
        public bool refill;

        // Off by default and deliberately so: the game's own
        // `RefillResources()` includes health, because health is not a SHARED
        // resource - so "refill on kill" would silently be "full heal on every
        // kill", which is a different and much stronger module.
        public bool includeHealth;

        // Optional: restrict to one resource, so a module can be "infinite
        // White" rather than infinite everything.
        public Resource resource;

        [NonSerialized] private Unit.Data _owner;
        [NonSerialized] private bool _listening;

        private int Level
        {
            get
            {
                int level = (base.Module != null) ? base.Module.Level : 1;
                return (level < 1) ? 1 : level;
            }
        }

        private float Seconds
        {
            get
            {
                float v = seconds.GetElement(Level - 1);
                return (v <= 0f) ? 3f : v;
            }
        }

        private string Trigger
        {
            get
            {
                string t = (trigger ?? "").Trim().ToLowerInvariant();
                return (t == Always) ? Always : Kill;
            }
        }

        public override void OnInstalled(Unit.Data unit)
        {
            base.OnInstalled(unit);
            _owner = unit;
            Push(unit);
        }

        public override void OnUninstalled(Unit.Data unit)
        {
            base.OnUninstalled(unit);

            var a = ModuleForgeAmmo.For(unit ?? _owner);

            if (a != null)
                a.Release(this);

            if (_listening)
            {
                ModuleForgeKills.Unregister(this);
                _listening = false;
            }

            _owner = null;
        }

        // A BoosterCore raises Module.Level with no install/uninstall, so this
        // is the only place a longer window can be noticed.
        public override void OnRecalculateUnitStats(Unit.Data unit)
        {
            base.OnRecalculateUnitStats(unit);
            _owner = unit;
            Push(unit);
        }

        private void Push(Unit.Data unit)
        {
            if (unit == null)
                return;

            var a = ModuleForgeAmmo.For(unit);

            if (a == null)
                return;

            a.Request(this, new ModuleForgeAmmo.Spec
            {
                trigger = Trigger,
                seconds = Seconds,
                infinite = infinite,
                refill = refill,
                includeHealth = includeHealth,
                only = resource
            });

            // Only the kill trigger needs the kill feed, so only it pays for
            // being on the list.
            if (Trigger == Kill && !_listening)
            {
                ModuleForgeKills.Register(this);
                _listening = true;
            }
        }

        // ---- IKillListener --------------------------------------------------
        // Owner scopes to this ship; a NULL ScopedWeapon means "any weapon's
        // kills", and the real scope comes from which grid the module sits in.
        public Unit.Data Owner { get { return _owner; } }

        public WeaponBase ScopedWeapon { get { return null; } }

        public void OnKill(Unit killer, Unit victim)
        {
            if (_owner == null || killer == null)
                return;

            // Somebody else's kill must not reload the player.
            if (killer.ComponentData != _owner)
                return;

            var a = ModuleForgeAmmo.For(_owner);

            if (a != null)
                a.NoteKill();
        }

        // Every field - Module's constructor Clones each effect off the
        // ModuleData, so the clone is what actually runs and a missing line
        // here is an invisible half-configured module.
        public override ModuleEffect Clone()
        {
            return new AmmoEffect
            {
                trigger = trigger,
                seconds = seconds,
                infinite = infinite,
                refill = refill,
                includeHealth = includeHealth,
                resource = resource
            };
        }

        public void GetPropertyList(
            Unit unit, bool isInstalled, List<DisplayableProperty> properties)
        {
            if (properties == null)
                return;

            string what = resource != null
                ? resource.SpriteTag
                : "AMMO";

            if (infinite)
            {
                string label = TextFormatter.ColoredText(
                    TextFormatter.capsColor, "FREE " + what);

                properties.Add(new DisplayableProperty(
                    label,
                    (Trigger == Always)
                        ? "always"
                        : Seconds.ToString("0.##") + "s on kill"));
            }

            if (refill)
            {
                string label = TextFormatter.ColoredText(
                    TextFormatter.capsColor,
                    includeHealth ? "REFILL ALL" : "RELOAD");

                properties.Add(new DisplayableProperty(
                    label, (Trigger == Always) ? "on kill" : "on kill"));
            }
        }
    }
}
