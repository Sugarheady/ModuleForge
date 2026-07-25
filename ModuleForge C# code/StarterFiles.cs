using System.IO;

namespace ModuleForge
{
    public static class StarterFiles
    {
        public static void Write(string folder)
        {
            File.WriteAllText(
                Path.Combine(folder, "ExampleGlassCannon.json"),
@"{
  ""name"": ""GlassCannon"",
  ""displayName"": ""GLASS CANNON"",
  ""description"": ""+1 projectile, but a slower fire rate."",
  ""target"": ""weapon"",
  ""icon"": ""HUD_GridTiles_10"",
  ""color"": ""#ff5555"",
  ""source"": ""both"",
  ""shopPrice"": 150,
  ""shopUnlockLevel"": 2,
  ""lootWeight"": 10,
  ""effects"": [
    { ""type"": ""ModifyWeaponProperty"", ""targetProperty"": ""ProjectileCount"",
      ""deltaCalculationMode"": ""Constant"", ""value"": 1 },
    { ""type"": ""ModifyWeaponProperty"", ""targetProperty"": ""FireRate"",
      ""deltaCalculationMode"": ""FromOriginal"", ""value"": -0.2 }
  ]
}
");

            File.WriteAllText(
                Path.Combine(folder, "ExampleTankPlating.json"),
@"{
  ""name"": ""TankPlating"",
  ""displayName"": ""TANK PLATING"",
  ""description"": ""More health, but slowly drains fuel."",
  ""target"": ""ship"",
  ""icon"": ""HUD_GridTiles_12"",
  ""color"": ""ColorRed"",
  ""source"": ""loot"",
  ""lootWeight"": 8,
  ""effects"": [
    { ""type"": ""ModifyResourceCapacity"", ""resource"": ""Health"", ""amount"": 6 },
    { ""type"": ""DrainResourceEffect"", ""resource"": ""Fuel"", ""amount"": 0.5 }
  ]
}
");

            File.WriteAllText(
                Path.Combine(folder, "README.txt"),
@"MODULE FORGE - custom module definitions
=========================================

Every *.json file in this folder becomes a custom MODULE - the small
upgrades you attach in the grid (like Health Up, or +Firerate). They
can drop from crates/bosses and/or be bought in the shop. Modules are
built + registered at game startup, so save/continue works.

Edits are read at startup - restart the game to see changes.

TOP-LEVEL KEYS
--------------
name         (required) unique id, letters/numbers, no spaces
displayName  card title
description  card text
target       ""ship""   = attaches to the SHIP BODY (stat/regen/shield
                        style upgrades), OR
             ""weapon"" = attaches to a WEAPON/gadget (fire rate,
                        projectiles, etc.). Default ""ship"".
             (The game routes it automatically by this choice.)
icon         module icon sprite name (see ICONS). e.g. ""HUD_GridTiles_12""
color        icon tint: a game color (ColorWhite/ColorOrange/ColorPurple/
             ColorBlue/ColorRed/ColorYellow/Color Tech/ColorPower) OR a
             hex like ""#ff5555""
source       ""loot"" (crates/bosses, default), ""shop"", or ""both""
shopPrice    (shop) cost in money (default 100)
shopUnlockLevel (shop) stations to unlock before it appears (default 1;
             0 = from the first shop)
lootWeight   (loot) drop chance vs other modules (default 10)
repeatInShop (optional bool) can reappear in shop after buying
canBeBoosted (optional bool, default true)
effects      (required) an array of effect objects (see EFFECTS). You
             can mix several, including opposite signs, on one module.

VALUES / SCALING
----------------
A magnitude can be a plain number (flat, e.g. ""value"": 1) OR an object
that scales with the module's level:
    { ""baseValue"": 2, ""increaseMethod"": ""Add"", ""change"": 1 }
Flat numbers are the simplest and are level-independent. Negative
numbers are allowed and produce the opposite effect (e.g. -0.2 fire
rate = -20%).

EFFECTS - SHIP (use with target ""ship"")
----------------------------------------
{ ""type"": ""ModifyResourceCapacity"", ""resource"": ""Health"", ""amount"": 6 }
    +/- max of a resource tank (Health/Stamina/Caps/Electron/Fuel/Gel/Tech)
{ ""type"": ""ResourceAutoChargeEffect"", ""resource"": ""Health"", ""amount"": 0.2 }
    passive regen per second (a.k.a. ""regen"")
{ ""type"": ""DrainResourceEffect"", ""resource"": ""Fuel"", ""amount"": 0.5 }
    continuously drains a tank (negative = a gain)
{ ""type"": ""AddShieldEffect"", ""resource"": ""Caps"", ""amount"": 0.5 }
    damage shield of an element; amount = fraction (0.5 = 50%)

EFFECTS - WEAPON (use with target ""weapon"")
--------------------------------------------
{ ""type"": ""ModifyWeaponProperty"", ""targetProperty"": ""FireRate"",
  ""deltaCalculationMode"": ""FromOriginal"", ""value"": 0.25 }
    THE universal stat mod. targetProperty is one of:
      FireRate, BurstSize, BurstDelay, ProjectileCount, Spread,
      AngleVariance, AngleOffset, KnockbackForce, Cost, Range, Speed
      (map: extra projectile=ProjectileCount, projectile speed=Speed,
       +1 burst=BurstSize). ""Damage"" is intentionally NOT allowed (a
       game bug double-adds it - use an explosion/burn effect instead).
    deltaCalculationMode:
      ""Constant""     flat +value (e.g. +1 projectile)
      ""FromOriginal"" +value * the weapon's base stat (0.25 = +25%)
      ""FromCurrent""  +value * current stat
    value: number or scaling object. Negative allowed.
    Keep sane: ProjectileCount/BurstSize should end >=1, FireRate >0,
    Spread >=0 (it clamps).
{ ""type"": ""IncreaseExplosionRadiusEffect"", ""increaseAmount"": 1 }
{ ""type"": ""AddImpactExplosionEffect"" }
    makes projectiles explode on impact (projectile weapons only)
{ ""type"": ""AddBurnEffect"", ""amount"": 5,
  ""costPerProjectile"": 0.25, ""costResource"": ""Caps"" }
{ ""type"": ""AddExplosionEffect"", ""damageType"": ""Caps"", ""damageAmount"": 2,
  ""costPerProjectile"": 1, ""costResource"": ""Caps"",
  ""addImpactExplosion"": true, ""explosionRadiusIncrement"": 1, ""burn"": 0 }
{ ""type"": ""AddDischargeEffect"", ""chainLengthIncrement"": 1,
  ""damageIncrement"": 3, ""impact"": true,
  ""costPerProjectile"": 1, ""costResource"": ""Electron"" }
    the ""spark""/chain-lightning arc
(The burn/explosion/discharge effects only fire on PROJECTILE weapons.)

ICONS
-----
Module icons are ""HUD_GridTiles_NN"" sprites. Handy ones:
  Up 12   Regen 17   Shield 34   Burn 40   Explosion 39   Spark 41
  Extra Projectile 10   Fire Rate 4   Spread 5   Proj Speed 15
  Range 11   Power Core 18   Burst (HUD_Modules_17)
The Module Builder web page has a dropdown for these.

NOTES
-----
- Resource names: Health, Stamina, Caps, Electron, Fuel, Gel, Tech
  (Money exists but is currency - don't use it as a resource here).
- Ship effects only work on ""ship"" modules; weapon effects only on
  ""weapon"" modules (they attach to different parts of the grid).
- Errors are logged to BepInEx/LogOutput.log with the file name.
");
        }
    }
}
