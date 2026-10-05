using System;
using System.Collections.Generic;
using UnityEngine;

namespace ModuleForge
{
    // "You cannot die for a moment." Two triggers, both optional, at least one
    // required.
    //
    // Uses DamagableResource.IsInvincible, which FLOORS the tank at 1 rather
    // than blocking damage - so this is a safety net, not a shield, and it is a
    // genuinely different card from the `invuln` window even though the titles
    // sound the same.
    [Serializable]
    public class LastStandEffect
        : ModuleEffect, IHasDescriptionForUnit, ModuleForgeKills.IKillListener,
          ModuleForgeLive.IGated
    {
        public bool onThreshold = true;
        public float threshold = 0.25f;
        public bool onKill;

        public FloatSeries seconds;
        public float cooldown = 15f;

        [NonSerialized] private Unit.Data _owner;
        [NonSerialized] private bool _listening;

        // IKillListener - see the note in StealthEffect. Null weapon scope
        // means any of the ship's kills arm it.
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
            ModuleForgeLive.Track(this, unit);
        }

        public override void OnUninstalled(Unit.Data unit)
        {
            base.OnUninstalled(unit);
            ModuleForgeLive.Untrack(this);
            Release(unit);
        }

        // Unpowered or disconnected is the same thing as not being there, which
        // is what the game does with every stock module - see ModuleForgeLive.
        // This one is worth getting right on its own: a last stand that fires
        // off a card the player can see is unpowered reads as the mod cheating.
        public void OnModuleLiveChanged(Unit.Data unit, bool live)
        {
            if (live)
            {
                _owner = unit;
                Push(unit);
            }
            else
            {
                Release(unit);
            }
        }

        // The one owner of the teardown, so uninstall and the powered gate
        // cannot drift apart. Idempotent: every step is already guarded.
        private void Release(Unit.Data unit)
        {
            var s = ModuleForgeLastStand.For(unit ?? _owner);

            if (s != null)
                s.Release(this);

            if (_listening)
            {
                ModuleForgeKills.Unregister(this);
                _listening = false;
            }

            _owner = null;
        }

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

            var s = ModuleForgeLastStand.For(unit);

            if (s == null)
                return;

            s.Request(this, new ModuleForgeLastStand.Spec
            {
                onThreshold = onThreshold,
                threshold = threshold,
                onKill = onKill,
                seconds = Seconds,
                cooldown = cooldown
            });

            if (onKill && !_listening)
            {
                ModuleForgeKills.Register(this);
                _listening = true;
            }
        }

        public void OnKill(Unit killer, Unit victim)
        {
            if (_owner == null || killer == null ||
                killer.ComponentData != _owner)
                return;

            var s = ModuleForgeLastStand.For(_owner);

            if (s != null)
                s.NoteKill();
        }

        public override ModuleEffect Clone()
        {
            return new LastStandEffect
            {
                onThreshold = onThreshold,
                threshold = threshold,
                onKill = onKill,
                seconds = seconds,
                cooldown = cooldown
            };
        }

        public void GetPropertyList(
            Unit unit, bool isInstalled, List<DisplayableProperty> properties)
        {
            if (properties == null)
                return;

            string label = TextFormatter.ColoredText(
                TextFormatter.capsColor, "LAST STAND");

            string when;

            if (onThreshold && onKill)
                when = "UNDER " + (threshold * 100f).ToString("0") + "% OR ON KILL";
            else if (onKill)
                when = "ON KILL";
            else
                when = "UNDER " + (threshold * 100f).ToString("0") + "% HEALTH";

            properties.Add(new DisplayableProperty(
                label, Seconds.ToString("0.##") + "S " + when));

            properties.Add(new DisplayableProperty(
                TextFormatter.ColoredText(TextFormatter.capsColor, "COOLDOWN"),
                cooldown.ToString("0.##") + "S"));
        }
    }
}
