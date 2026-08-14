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
                Path.Combine(folder, "ExampleRapidBurn.json"),
@"{
  ""name"": ""RapidBurn"",
  ""displayName"": ""RAPID BURN"",
  ""description"": ""Adds burn, and makes burn tick faster on enemies."",
  ""target"": ""weapon"",
  ""icon"": ""HUD_GridTiles_40"",
  ""color"": ""ColorOrange"",
  ""source"": ""both"",
  ""shopPrice"": 175,
  ""shopUnlockLevel"": 2,
  ""lootWeight"": 8,
  ""effects"": [
    { ""type"": ""AddBurnEffect"", ""amount"": 4,
      ""costPerProjectile"": 0.2, ""costResource"": ""Caps"" },
    { ""type"": ""BurnTickRateEffect"", ""ticksPerSecond"": 0.1 }
  ]
}
");

            File.WriteAllText(
                Path.Combine(folder, "ExampleRainbowBurn.json"),
@"{
  ""name"": ""RainbowBurn"",
  ""displayName"": ""RAINBOW BURN"",
  ""description"": ""Adds burn, and sets enemy flames to cycle RGB."",
  ""target"": ""weapon"",
  ""icon"": ""HUD_GridTiles_40"",
  ""color"": ""ColorPurple"",
  ""source"": ""both"",
  ""shopPrice"": 150,
  ""shopUnlockLevel"": 1,
  ""lootWeight"": 8,
  ""effects"": [
    { ""type"": ""AddBurnEffect"", ""amount"": 4,
      ""costPerProjectile"": 0.2, ""costResource"": ""Caps"" },
    { ""type"": ""BurnColorEffect"", ""rgb"": true, ""rgbSpeed"": 0.5 }
  ]
}
");

            File.WriteAllText(
                Path.Combine(folder, "ExamplePhaseRounds.json"),
@"{
  ""name"": ""PhaseRounds"",
  ""displayName"": ""PHASE ROUNDS"",
  ""description"": ""Your shots pass through walls but still hit enemies."",
  ""target"": ""weapon"",
  ""icon"": ""HUD_GridTiles_15"",
  ""color"": ""ColorBlue"",
  ""source"": ""both"",
  ""shopPrice"": 150,
  ""shopUnlockLevel"": 2,
  ""lootWeight"": 8,
  ""effects"": [
    { ""type"": ""Phasing"" }
  ]
}
");

            File.WriteAllText(
                Path.Combine(folder, "ExamplePiercingRounds.json"),
@"{
  ""name"": ""PiercingRounds"",
  ""displayName"": ""PIERCING ROUNDS"",
  ""description"": ""Shots pierce through enemies (capped), weaker each hit."",
  ""target"": ""weapon"",
  ""icon"": ""HUD_GridTiles_10"",
  ""color"": ""ColorRed"",
  ""source"": ""both"",
  ""shopPrice"": 150,
  ""shopUnlockLevel"": 2,
  ""lootWeight"": 8,
  ""effects"": [
    { ""type"": ""PierceCap"", ""pierceCap"": 3, ""falloff"": 0.15 }
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
icon         module icon: a game sprite name like ""HUD_GridTiles_12"", or
             the name of YOUR OWN art (see ICONS)
color        icon tint: a game color (ColorWhite/ColorOrange/ColorPurple/
             ColorBlue/ColorRed/ColorYellow/Color Tech/ColorPower) OR a
             hex like ""#ff5555""
source       ""loot"" (crates/bosses, default), ""shop"", or ""both""
shopPrice    (shop) cost in money (default 100)
shopUnlockLevel (shop) stations to unlock before it appears (default 1;
             0 = from the first shop)
lootWeight   (loot) drop chance vs other modules (default 10)
lootFrom     (loot) WHICH crates it drops from (see LOOT). One name or a
             list. Left out = all of them.
lootRepeat   (loot) may it drop more than once per run? true/false, or a
             number 0..1. Default is the game's ""once only"".
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

LEVELS AND GREEN CRATES. Green crates drop a BoosterCore, and placing one
next to a module on the grid raises that module's LEVEL. For an effect to
answer to that it needs BOTH:
  * ""canBeBoosted"": true on the module (it is true by default), and
  * a magnitude written as the scaling object above, with a
    ""change"" that is NOT zero.
A flat number - or a change of 0 - returns the same value at every level,
so the booster appears to do nothing. That is the single most common
""my booster is broken"" cause, and it isn't a bug.

The Module Builder page has a ""<param> - per level"" box beside every
magnitude that supports this; filling it writes the scaling object for
you. Levels update live: a boost applies the moment the crate module is
placed, without re-equipping anything.

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
{ ""type"": ""BurnTickRateEffect"", ""ticksPerSecond"": 0.1 }
    Speeds up how fast BURN ticks on ENEMIES while equipped: ticksPerSecond
    is ADDED to the burn tick frequency and stacks additively across
    equipped copies (e.g. burn ~1/sec + two +0.1 modules => ~1.2/sec). Burn
    that enemies inflict on YOU is unaffected. It does NOT add burn itself -
    pair with a burn source. Hard-capped (default 20/sec, see below).
{ ""type"": ""BurnColorEffect"", ""color"": ""ColorBlue"" }
{ ""type"": ""BurnColorEffect"", ""rgb"": true, ""rgbSpeed"": 0.5 }
{ ""type"": ""BurnColorEffect"", ""rgb"": true, ""includeTerrain"": true }
    Recolors the BURN FLAMES on ENEMIES while equipped. Give a ""color""
    (game color name or #hex), OR set ""rgb"": true for a rainbow that
    cycles (rgbSpeed = hue cycles/sec, default 0.5). Add
    ""includeTerrain"": true to ALSO recolor burning terrain/world fire.
    Your own on-fire flames are NEVER recolored. Does NOT add burn - pair
    with a burn source. Most recently equipped color wins.
{ ""type"": ""AddExplosionEffect"", ""damageType"": ""Caps"", ""damageAmount"": 2,
  ""costPerProjectile"": 1, ""costResource"": ""Caps"",
  ""addImpactExplosion"": true, ""explosionRadiusIncrement"": 1, ""burn"": 0 }
{ ""type"": ""AddDischargeEffect"", ""chainLengthIncrement"": 1,
  ""damageIncrement"": 3, ""impact"": true,
  ""costPerProjectile"": 1, ""costResource"": ""Electron"" }
    the ""spark""/chain-lightning arc
{ ""type"": ""Phasing"" }
    While equipped, your PROJECTILES pass through terrain (walls) but
    still hit enemies. (Projectile weapons; for a phasing laser use
    Weapon Forge's phasing flag.)
{ ""type"": ""PierceCap"", ""pierceCap"": 3, ""falloff"": 0.15,
  ""explodeOnLimit"": false }
    Turns piercing ON and caps it: shots pierce THROUGH pierceCap enemies
    then vanish on the next contact. falloff = damage lost per pierce
    (clamped 0-1). Caps stack across equipped pierce modules.
    (Projectile weapons only.)

    FRACTIONS WORK. pierceCap is a float, so half a pierce is a real
    thing: 1.5 pierces once always and TWICE half the time. The coin is
    flipped once per shot when it is fired, not per enemy - so a given
    bullet keeps whatever it rolled for its whole flight.
    Fractions from several modules are summed BEFORE the roll, so 0.5 and
    0.5 make a guaranteed pierce rather than two flips that usually lose.

    BOTH pierceCap AND falloff take the scaling object, so pierce can
    grow with a green-crate BoosterCore. This gives +0.5 of a pierce per
    level - level 1 = 1, level 2 = 1.5, level 3 = 2:
      { ""type"": ""PierceCap"",
        ""pierceCap"": { ""baseValue"": 1, ""increaseMethod"": ""Add"",
                     ""change"": 0.5 } }
    A negative change on falloff is a nice upgrade curve: the module
    loses less damage per pierce as it levels.
(The burn/explosion/discharge effects only fire on PROJECTILE weapons.)

ICONS
-----
The game's own icons are ""HUD_GridTiles_NN"" sprites. Handy ones:
  Up 12   Regen 17   Shield 34   Burn 40   Explosion 39   Spark 41
  Extra Projectile 10   Fire Rate 4   Spread 5   Proj Speed 15
  Range 11   Power Core 18   Burst (HUD_Modules_17)
The Module Builder web page has a dropdown for these.

YOUR OWN ICON ART
  ""icon"" also takes the name of a picture you drew yourself. Drop a PNG
  into Weapon Forge's ""sprites"" folder (BepInEx\plugins\sprites) and name
  it in ""icon"" without the .png:

      myicon.png    ->    ""icon"": ""myicon""

  Weapon Forge owns that folder and this mod borrows it, so there is one
  folder and one set of names across both mods - which does mean custom
  icons need Weapon Forge installed. Stock sprites still work without it.

  Four things worth knowing:
  * DRAW IT SQUARE. The stock icons are 24x24. The card stretches art to
    a square, so a tall or wide picture looks squashed there while the
    pickup on the ground keeps its shape. Any size works - it is scaled
    to match the stock icons - but the shape is yours to get right.
  * IT IS TINTED by ""color"". Draw in white or grey and let color do the
    work, or set ""color"": ""#ffffff"" to see your art's own colours.
  * PNG only. The engine cannot read GIF.
  * The name is the PNG's file name, or the name inside a sheet .json if
    you sliced one with Weapon Forge's Sprite Sheet Builder.

  A game sprite always wins a name clash, and the log says so.

LOOT - WHICH CRATE DROPS IT
---------------------------
By default a loot module can come out of every crate that rolls modules.
""lootFrom"" narrows that to the crates you name:

    ""lootFrom"": ""tech""
    ""lootFrom"": [ ""white"", ""caps"" ]

Names: white (aka stamina), caps (aka orange), purple (aka gel), tech,
queen (the Queen's own generic pool), money, level2, or ""all"".

WHICH ONE TO PICK. The pools are very different sizes, so this is the
single biggest lever on how often your module actually turns up:
  tech    ~5 stock entries  -> BEST odds for a custom module
  purple / caps             -> middling
  white   ~18 stock entries -> worst odds
Only 8 of the game's 64 drop tables can produce a module at all, and 5
pools are ever really rolled, so ""all"" is not as broad as it sounds.

MONEY AND LEVEL2 ARE SPECIAL. Neither crate rolls a module in the base
game. Naming one makes the mod ADD a module roll to that crate. This is
additive - the crate keeps every normal drop and gains a module on top -
and it only happens if a file asks for it by name, never from ""all"".
  money   nothing else is in that pool, so YOUR module always drops
  level2  also revives 5 stock regen/generator modules the game never
          rolls, so you compete with them

DROPPING MORE THAN ONCE. The game zeroes a module's own drop weight once
you own one, which is why duplicates never appear. ""lootRepeat"" changes
that for your module only:
    ""lootRepeat"": false   once per run (the default, stock behaviour)
    ""lootRepeat"": true    full chance every time
    ""lootRepeat"": 0.25    each copy you own makes the next 1/4 as likely
It edits your module's own clone, so it can never make a stock module
start repeating.

NOTES
-----
- Resource names: Health, Stamina, Caps, Electron, Fuel, Gel, Tech
  (Money exists but is currency - don't use it as a resource here).
- Ship effects only work on ""ship"" modules; weapon effects only on
  ""weapon"" modules (they attach to different parts of the grid).
- BurnTickRateEffect is capped: burn can never tick faster than
  MaxTicksPerSecond (default 100) in BepInEx\config\com.sugarheady.moduleforge.cfg.
  (The game ticks burn at most once per frame, so a cap above your frame
  rate just means 'every frame'.)
- Errors are logged to BepInEx/LogOutput.log with the file name.
");
        }
    }
}
