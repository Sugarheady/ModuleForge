using System;
using System.Collections.Generic;
using UnityEngine;

namespace ModuleForge
{
    // Changes WHAT AMMO a weapon spends.
    //
    // `WeaponBase.ResourceUsed` is a settable Resource and no stock effect
    // touches it - ModifyWeaponProperty's table is twelve floats, and a
    // resource is not a number, which is why it needs its own class rather
    // than a thirteenth row in ExtraWeaponStatEffect.
    //
    // It is a different KIND of module from "+10% fire rate": it changes what
    // a build is made of. A Caps gun converted to Electron stops competing with
    // your other Caps weapons for the same tank.
    //
    // THE TRAP, and it is the whole reason this needs a warning:
    //
    //     Shooter.OwnerHasResource =
    //         unit.HasInfiniteResource || weapon.Cost == 0 ||
    //         unit.GetResource(weapon.ResourceUsed) >= weapon.Cost
    //
    // Point a weapon at a resource the ship has no tank of and GetResource
    // returns 0, so the gate is never satisfied and THE WEAPON SIMPLY WILL NOT
    // FIRE - silently, with no error, looking for all the world like the module
    // broke the gun. The one exception is a weapon whose Cost is 0, which
    // bypasses the check entirely and is therefore always safe.
    //
    // The card line names the resource for exactly that reason: it is the only
    // place a player can find out why their gun stopped working.
    [Serializable]
    public class WeaponResourceEffect
        : ModuleEffect, IWeaponModifier, IHasDescriptionForWeapon
    {
        public Resource resource;

        // Zero the weapon's cost as well. The honest escape hatch from the trap
        // above: a free weapon never consults its tank, so it fires whether or
        // not the ship carries that resource. Off by default because "free
        // ammo" is a much bigger balance change than "different ammo", and it
        // should be asked for rather than inherited.
        public bool free;

        public void Modify(WeaponBase weaponBase)
        {
            if (weaponBase == null || resource == null)
                return;

            weaponBase.ResourceUsed = resource;

            if (free)
                weaponBase.Cost = 0f;
        }

        // Every field - see the note in ExtraWeaponStatEffect. `resource` is a
        // reference to a shared ScriptableObject, which is correct to copy by
        // reference: we point at the game's asset, we never modify it.
        public override ModuleEffect Clone()
        {
            return new WeaponResourceEffect
            {
                resource = resource,
                free = free
            };
        }

        public void GetDescription(
            WeaponBase weapon,
            bool isInstalled,
            List<DisplayableProperty> properties)
        {
            if (properties == null)
                return;

            string label =
                TextFormatter.ColoredText(TextFormatter.capsColor, "AMMO");

            string name = (resource != null)
                ? (string.IsNullOrEmpty(resource.SpriteTag)
                       ? resource.name
                       : resource.SpriteTag)
                : "-";

            if (free)
                name += " (FREE)";

            if (isInstalled && weapon != null && weapon.TemplateData != null)
            {
                Resource was = weapon.TemplateData.resourceUsed;

                properties.Add(new DisplayableProperty(
                    label,
                    name,
                    (was != null) ? was.name : "-",
                    (resource != null) ? resource.name : "-"));

                return;
            }

            properties.Add(new DisplayableProperty(label, name));
        }
    }
}
