using System.Collections.Generic;
using Stellar.Abstractions.Domain.Inventory;

namespace Stellar.ModuleOptimizer;

/// <summary>
/// Pure, UnityEngine-free port of StarResonanceAutoMod's combat-power model
/// (<c>module_optimizer.py: _restore_original_scores</c> +
/// <c>module_types.py</c> maps). A module set's score is the sum of two terms:
///
///   threshold_power — per attribute, the summed part value crosses a ladder of
///     thresholds (<see cref="AttrThresholds"/>); the highest crossed rung maps
///     to a power award via the basic or special power map.
///   total_power     — the grand total of ALL part values maps directly through
///     <see cref="TotalAttrPowerMap"/> (a sparse table with intentional gaps
///     that score 0 via lookup-miss).
///
/// Kept pure + internal-but-IVT'd so the fiddly threshold boundaries and the
/// 9–17 / 107–112 total-map gaps can be unit-tested without a live IL2CPP host.
/// </summary>
internal static class CombatPower
{
    /// <summary>Per-attribute value thresholds; index i (0-based) is rung i+1.</summary>
    internal static readonly int[] AttrThresholds = { 1, 4, 8, 12, 16, 20 };

    /// <summary>Power award per crossed rung for a basic attribute.</summary>
    internal static readonly IReadOnlyDictionary<int, int> BasicAttrPowerMap =
        new Dictionary<int, int> { [1] = 7, [2] = 14, [3] = 29, [4] = 44, [5] = 167, [6] = 254 };

    /// <summary>Power award per crossed rung for a special ("极") attribute.</summary>
    internal static readonly IReadOnlyDictionary<int, int> SpecialAttrPowerMap =
        new Dictionary<int, int> { [1] = 14, [2] = 29, [3] = 59, [4] = 89, [5] = 298, [6] = 448 };

    /// <summary>Special-attribute IDs (the "极-…" stacks). Everything else is basic.</summary>
    internal static readonly HashSet<int> SpecialAttrIds = new()
    {
        2104, 2105, 2204, 2205, 2304, 2404, 2405, 2406,
    };

    /// <summary>Basic-attribute IDs (reference parity with AutoMod's BASIC_ATTR_IDS).</summary>
    internal static readonly HashSet<int> BasicAttrIds = new()
    {
        1110, 1111, 1112, 1113, 1114, 1205, 1206, 1307, 1308, 1407, 1408, 1409, 1410,
    };

    /// <summary>
    /// Total-attr-value → power. Sparse on purpose: 9–17 and 107–112 are absent
    /// so a lookup miss yields 0 (mirrors AutoMod's <c>TOTAL_ATTR_POWER_MAP.get</c>).
    /// </summary>
    internal static readonly IReadOnlyDictionary<int, int> TotalAttrPowerMap = BuildTotalMap();

    /// <summary>
    /// Combat-power score of a module set: <c>thresholdPower + totalPower</c>.
    /// Sums each part value into a per-AttrId breakdown, awards threshold power
    /// for the highest crossed rung of each attribute, and adds the direct
    /// total-value award.
    /// </summary>
    internal static int ScoreCombo(IEnumerable<ModuleInfo> modules)
    {
        var attrBreakdown = new Dictionary<int, int>();
        foreach (var module in modules)
        {
            foreach (var part in module.Parts)
            {
                attrBreakdown.TryGetValue(part.AttrId, out var prev);
                attrBreakdown[part.AttrId] = prev + part.Value;
            }
        }
        return Score(attrBreakdown);
    }

    /// <summary>Combat-power score from an already-computed per-AttrId total map.</summary>
    internal static int Score(IReadOnlyDictionary<int, int> attrBreakdown)
    {
        var thresholdPower = 0;
        var totalAttrValue = 0;

        foreach (var kv in attrBreakdown)
        {
            totalAttrValue += kv.Value;

            var level = LevelFor(kv.Value);
            if (level <= 0) continue;

            var map = SpecialAttrIds.Contains(kv.Key) ? SpecialAttrPowerMap : BasicAttrPowerMap;
            if (map.TryGetValue(level, out var award)) thresholdPower += award;
        }

        var totalPower = TotalAttrPowerMap.TryGetValue(totalAttrValue, out var tp) ? tp : 0;
        return thresholdPower + totalPower;
    }

    /// <summary>
    /// Highest 1-based rung whose threshold <paramref name="total"/> meets, stopping
    /// at the first unmet rung (AutoMod's break-on-miss loop). 0 if total &lt; 1.
    /// </summary>
    internal static int LevelFor(int total)
    {
        var level = 0;
        for (var i = 0; i < AttrThresholds.Length; i++)
        {
            if (total >= AttrThresholds[i]) level = i + 1;
            else break;
        }
        return level;
    }

    private static Dictionary<int, int> BuildTotalMap()
    {
        // Verbatim from module_types.py TOTAL_ATTR_POWER_MAP. The 9–17 and
        // 107–112 ranges are intentionally absent (score 0 via lookup miss).
        return new Dictionary<int, int>
        {
            [0] = 0, [1] = 5, [2] = 11, [3] = 17, [4] = 23, [5] = 29, [6] = 34, [7] = 40, [8] = 46,
            [18] = 104, [19] = 110, [20] = 116, [21] = 122, [22] = 128, [23] = 133, [24] = 139, [25] = 145,
            [26] = 151, [27] = 157, [28] = 163, [29] = 168, [30] = 174, [31] = 180, [32] = 186, [33] = 192,
            [34] = 198, [35] = 203, [36] = 209, [37] = 215, [38] = 221, [39] = 227, [40] = 233, [41] = 238,
            [42] = 244, [43] = 250, [44] = 256, [45] = 262, [46] = 267, [47] = 273, [48] = 279, [49] = 285,
            [50] = 291, [51] = 297, [52] = 302, [53] = 308, [54] = 314, [55] = 320, [56] = 326, [57] = 332,
            [58] = 337, [59] = 343, [60] = 349, [61] = 355, [62] = 361, [63] = 366, [64] = 372, [65] = 378,
            [66] = 384, [67] = 390, [68] = 396, [69] = 401, [70] = 407, [71] = 413, [72] = 419, [73] = 425,
            [74] = 431, [75] = 436, [76] = 442, [77] = 448, [78] = 454, [79] = 460, [80] = 466, [81] = 471,
            [82] = 477, [83] = 483, [84] = 489, [85] = 495, [86] = 500, [87] = 506, [88] = 512, [89] = 518,
            [90] = 524, [91] = 530, [92] = 535, [93] = 541, [94] = 547, [95] = 553, [96] = 559, [97] = 565,
            [98] = 570, [99] = 576, [100] = 582, [101] = 588, [102] = 594, [103] = 599, [104] = 605, [105] = 611,
            [106] = 617, [113] = 658, [114] = 664, [115] = 669, [116] = 675, [117] = 681, [118] = 687, [119] = 693, [120] = 699,
        };
    }
}
