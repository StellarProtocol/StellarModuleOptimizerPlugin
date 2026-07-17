using System.Collections.Generic;
using Xunit;

namespace Stellar.ModuleOptimizer.Tests;

public class CombatPowerTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(3, 1)]
    [InlineData(4, 2)]
    [InlineData(19, 5)]
    [InlineData(20, 6)]
    [InlineData(999, 6)]
    public void LevelFor_matches_threshold_ladder(int total, int expected)
        => Assert.Equal(expected, CombatPower.LevelFor(total));

    [Theory]
    [InlineData(9)]
    [InlineData(17)]
    [InlineData(107)]
    [InlineData(112)]
    public void Total_power_is_zero_in_the_intentional_gaps(int total)
    {
        Assert.False(CombatPower.TotalAttrPowerMap.ContainsKey(total));
    }

    [Fact]
    public void Score_at_120_total_includes_total_power_award()
    {
        // 1110 is a basic attr: value 120 crosses rung 6 (award 254),
        // and TotalAttrPowerMap[120] = 699.
        var score = CombatPower.Score(new Dictionary<int, int> { [1110] = 120 });
        Assert.Equal(254 + 699, score);
    }

    [Fact]
    public void Score_above_120_total_drops_total_power_matching_reference_tool()
    {
        // AutoMod's 5-module mode (commit a365df5) deliberately keeps the map
        // capped at 120: totals above it score 0 total_power via lookup miss.
        // We match that reference behavior. See spec + recon/module-3.7-notes.md.
        var score = CombatPower.Score(new Dictionary<int, int> { [1110] = 121 });
        Assert.Equal(254, score);
    }

    [Fact]
    public void Special_attr_uses_special_power_map()
    {
        // 2104 is special: value 4 crosses rung 2 (special award 29);
        // TotalAttrPowerMap[4] = 23.
        var score = CombatPower.Score(new Dictionary<int, int> { [2104] = 4 });
        Assert.Equal(29 + 23, score);
    }
}
