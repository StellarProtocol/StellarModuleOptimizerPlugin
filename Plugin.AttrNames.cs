using System.Collections.Generic;

namespace Stellar.ModuleOptimizer;

/// <summary>
/// Display names for the module-attribute ID namespace (the IDs carried in
/// <c>ModNewAttr.ModParts</c> / <c>Mod.ModInfos[*].PartIds</c>). These IDs are
/// not present in the game's combat-attribute table (<c>IGameDataCombat</c>),
/// so the optimizer carries its own labels — mirroring the reference tool
/// StarResonanceAutoMod (<c>module_types.py</c> <c>MODULE_ATTR_NAMES_EN</c>).
/// <see cref="ResolveAttrName"/>/<see cref="ResolveAttrShort"/> consult the
/// game-data lookups first and fall back here; unmapped IDs render as
/// <c>#&lt;id&gt;</c>.
/// </summary>
public sealed partial class Plugin
{
    private static readonly IReadOnlyDictionary<int, string> ModuleAttrNames = new Dictionary<int, string>
    {
        [1110] = "Strength Boost",
        [1111] = "Agility Boost",
        [1112] = "Intellect Boost",
        [1113] = "Special Attack",
        [1114] = "Elite Strike",
        [1205] = "Healing Boost",
        [1206] = "Healing Enhance",
        [1307] = "Resistance",
        [1308] = "Armor",
        [1407] = "Cast Focus",
        [1408] = "Attack SPD",
        [1409] = "Crit Focus",
        [1410] = "Luck Focus",
        [2104] = "DMG Stack",
        [2105] = "Agile",
        [2204] = "Life Condense",
        [2205] = "First Aid",
        [2304] = "Final Protection",
        [2404] = "Life Wave",
        [2405] = "Life Steal",
        [2406] = "Team Luck&Crit",
    };

    // Module-attribute display name, or null if the ID is not a known module attr.
    private static string? ModuleAttrName(int attrId)
        => ModuleAttrNames.TryGetValue(attrId, out var name) ? name : null;
}
