using System;
using System.Collections.Generic;
using UnityEngine;

namespace ModuleForge
{
    // Makes the ship invisible to enemy Vision on a trigger.
    //
    // The mechanic is the game's own and is fully implemented - it was simply
    // unreachable outside the debug menu. See ModuleForgeStealth for how it
    // behaves; the short version is that enemies drop you from their target
    // lists within a quarter of a second, so it disengages as well as hides.
    [Serializable]
    public class StealthEffect
        : ModuleEffect, IHasDescriptionForUnit, ModuleForgeKills.IKillListener
    {
        public const string Dash = "dash";
        public const string Sprint = "sprint";
        public const string Idle = "idle";
        public const string Kill = "kill";
        public const string Hit = "hit";
        public const string Always = "always";

        public static bool KnownTrigger(string t)
        {
            switch (t)
            {
                case Dash:
                case Sprint:
                case Idle:
                case Kill:
                case Hit:
                case Always:
                    return true;
                default:
                    return false;
            }
        }

        public string trigger = Dash;
        public FloatSeries seconds;
        public float idleSeconds = 2f;
        public bool revealOnFire = true;
        public float revealSeconds = 1f;

        [NonSerialized] private Unit.Data _owner;
        [NonSerialized] private bool _listening;

        // IKillListener. The feed filters on these before calling OnKill:
        // Owner scopes to this ship, and a NULL ScopedWeapon means "any
        // weapon's kill counts", which is right for a ship-grid module.
        public Unit.Data Owner { get { return _owner; } }
        public WeaponBase ScopedWeapon { get { return null; } }

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
                return (v < 0f) ? 0f : v;
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

            var s = ModuleForgeStealth.For(unit ?? _owner);

            if (s != null)
                s.Release(this);

            if (_listening)
            {
                ModuleForgeKills.Unregister(this);
                _listening = false;
            }

            _owner = null;
        }

        // Also the level-boost path: a BoosterCore changes Module.Level with no
        // install, so re-pushing here is what makes a longer cloak per level
        // actually happen.
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

            var s = ModuleForgeStealth.For(unit);

            if (s == null)
                return;

            s.Request(this, new ModuleForgeStealth.Spec
            {
                trigger = trigger,
                seconds = Seconds,
                idleSeconds = idleSeconds,
                revealOnFire = revealOnFire,
                revealSeconds = revealSeconds
            });

            // Only the kill trigger needs the kill feed, so only it pays for
            // being on the list.
            if (trigger == Kill && !_listening)
            {
                ModuleForgeKills.Register(this);
                _listening = true;
            }
        }

        public void OnKill(Unit killer, Unit victim)
        {
            if (_owner == null || killer == null)
                return;

            // Somebody else's kill must not cloak the player.
            if (killer.ComponentData != _owner)
                return;

            var s = ModuleForgeStealth.For(_owner);

            if (s != null)
                s.NoteKill();
        }

        public override ModuleEffect Clone()
        {
            return new StealthEffect
            {
                trigger = trigger,
                seconds = seconds,
                idleSeconds = idleSeconds,
                revealOnFire = revealOnFire,
                revealSeconds = revealSeconds
            };
        }

        public void GetPropertyList(
            Unit unit, bool isInstalled, List<DisplayableProperty> properties)
        {
            if (properties == null)
                return;

            string label = TextFormatter.ColoredText(
                TextFormatter.capsColor, "STEALTH");

            string when;

            switch (trigger)
            {
                case Sprint: when = "WHILE SPRINTING"; break;
                case Idle:
                    when = "AFTER " + idleSeconds.ToString("0.##") +
                           "S NOT FIRING";
                    break;
                case Kill:
                    when = Seconds.ToString("0.##") + "S ON KILL"; break;
                case Hit:
                    when = Seconds.ToString("0.##") + "S WHEN HIT"; break;
                case Always: when = "ALWAYS"; break;
                default:
                    when = Seconds.ToString("0.##") + "S ON DASH"; break;
            }

            properties.Add(new DisplayableProperty(label, when));

            // The reveal rule is half the mechanic, so it gets its own line
            // rather than being buried in the description.
            if (revealOnFire && trigger != Idle)
            {
                properties.Add(new DisplayableProperty(
                    TextFormatter.ColoredText(
                        TextFormatter.capsColor, "REVEALED"),
                    revealSeconds.ToString("0.##") + "S BY FIRING"));
            }
        }
    }
}
