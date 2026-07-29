using System;
using System.Collections.Generic;
using UnityEngine;

namespace ModuleForge
{
    // Recolors the burn animation your weapons inflict on enemies - a
    // solid tint, or an RGB rainbow that cycles over time (the same look
    // as WeaponForge's RGB projectiles/beams). While equipped, enemy burn
    // particles are emitted with this color; burn that enemies inflict on
    // YOU is left the game's normal color (the owner is excluded).
    //
    // Global-while-equipped, like the burn tick-rate effect, because PUNK
    // renders all burn through one shared particle system.
    //
    // Save/load safe: rebuilt from the registry via Clone() on continue,
    // and OnInstalled re-fires so it re-registers.
    [Serializable]
    public class BurnColorEffect : ModuleEffect, IHasDescriptionForWeapon
    {
        public bool rgb;
        public Color color = Color.white;   // resolved solid tint
        public string colorLabel;           // original text, for the card
        public float rgbSpeed = 0.5f;       // hue cycles per second
        public float saturation = 1f;
        public float brightness = 1f;

        // Also recolor burning TERRAIN/world fire (not just units). The
        // player's own burn is never recolored either way. Opt-in.
        public bool includeTerrain;

        private bool _registered;
        private Unit.Data _owner;

        // The tint to emit right now: a fixed color, or a time-based hue
        // for the rainbow. Uses Time.time so every burning enemy shares
        // the same phase and it cycles smoothly frame to frame.
        public Color GetEmitColor()
        {
            if (!rgb)
                return color;

            float hue = Time.time * rgbSpeed;
            hue -= Mathf.Floor(hue);
            return Color.HSVToRGB(hue, saturation, brightness);
        }

        public override void OnInstalled(Unit.Data unit)
        {
            if (_registered)
                return;

            _owner = unit;
            ModuleForgeBurn.AddColor(unit, this);
            _registered = true;
        }

        public override void OnUninstalled(Unit.Data unit)
        {
            if (!_registered)
                return;

            ModuleForgeBurn.RemoveColor(_owner ?? unit, this);
            _registered = false;
            _owner = null;
        }

        public override ModuleEffect Clone()
        {
            return new BurnColorEffect
            {
                rgb = this.rgb,
                color = this.color,
                colorLabel = this.colorLabel,
                rgbSpeed = this.rgbSpeed,
                saturation = this.saturation,
                brightness = this.brightness,
                includeTerrain = this.includeTerrain
            };
        }

        public void GetDescription(
            WeaponBase weapon,
            bool isInstalled,
            List<DisplayableProperty> properties)
        {
            string label =
                TextFormatter.ColoredText(TextFormatter.capsColor, "BURN COLOR");

            string val = rgb
                ? "RGB"
                : (string.IsNullOrEmpty(colorLabel)
                    ? "CUSTOM"
                    : colorLabel.ToUpperInvariant());

            if (includeTerrain)
                val += " +TERRAIN";

            properties.Add(new DisplayableProperty(label, val));
        }
    }
}
