using System.Collections.Generic;
using System.Linq;
using Stellar.Abstractions.Domain.Inventory;
using Xunit;

namespace Stellar.ModuleOptimizer.Tests;

public class ModuleOptimizerEngineTests
{
    private const int AllCategories = 7;

    private static ModuleInfo[] DistinctPool(int n)
        => Enumerable.Range(0, n)
            .Select(i => TestModules.Mod(parts: (1110, i + 1)))
            .ToArray();

    [Fact]
    public void Optimize_returns_empty_below_slot_count()
    {
        var pool = DistinctPool(ModuleOptimizerEngine.SlotCount - 1);
        var combos = ModuleOptimizerEngine.Optimize(
            TestModules.Snap(pool), new List<int>(), AllCategories, topN: 100);
        Assert.Empty(combos);
    }

    [Fact]
    public void Optimize_enumerates_every_k_combination()
    {
        var pool = DistinctPool(8);
        var combos = ModuleOptimizerEngine.Optimize(
            TestModules.Snap(pool), new List<int>(), AllCategories, topN: 1_000_000);
        Assert.Equal(
            TestModules.Choose(8, ModuleOptimizerEngine.SlotCount), combos.Count);
    }

    [Fact]
    public void Optimize_full_prefilter_pool_enumerates_expected_combo_count()
    {
        // PrefilterCount-sized pool — the real-world worst case, and the
        // complexity canary the spec asks for: C(40,5)=658,008 at k=5.
        // A blow-up here shows up as a slow/failing test.
        var pool = DistinctPool(40);
        var combos = ModuleOptimizerEngine.Optimize(
            TestModules.Snap(pool), new List<int>(), AllCategories, topN: 700_000);
        Assert.Equal(TestModules.Choose(40, ModuleOptimizerEngine.SlotCount), combos.Count);
    }

    [Fact]
    public void Optimize_combos_are_distinct_sets_of_k_distinct_modules()
    {
        var pool = DistinctPool(8);
        var combos = ModuleOptimizerEngine.Optimize(
            TestModules.Snap(pool), new List<int>(), AllCategories, topN: 1_000_000);
        var seen = new HashSet<string>();
        foreach (var combo in combos)
        {
            Assert.Equal(ModuleOptimizerEngine.SlotCount, combo.Modules.Count);
            var key = string.Join(",", combo.Modules.Select(m => m.Uuid).OrderBy(u => u));
            Assert.Equal(ModuleOptimizerEngine.SlotCount, combo.Modules.Select(m => m.Uuid).Distinct().Count());
            Assert.True(seen.Add(key), $"duplicate combo {key}");
        }
    }

    [Fact]
    public void Optimize_pool_exactly_k_returns_single_combo()
    {
        var pool = DistinctPool(ModuleOptimizerEngine.SlotCount);
        var combos = ModuleOptimizerEngine.Optimize(
            TestModules.Snap(pool), new List<int>(), AllCategories, topN: 100);
        Assert.Single(combos);
    }

    [Fact]
    public void Optimize_orders_by_score_descending()
    {
        var pool = DistinctPool(8);
        var combos = ModuleOptimizerEngine.Optimize(
            TestModules.Snap(pool), new List<int>(), AllCategories, topN: 1_000_000);
        for (var i = 1; i < combos.Count; i++)
        {
            Assert.True(combos[i - 1].Score >= combos[i].Score);
        }
    }

    [Fact]
    public void Optimize_top_combo_is_the_known_best_subset()
    {
        // Values 1..8 on the single target attr: above the rung-6 threshold the
        // total-power map is linear, so the k highest values are provably the
        // unique best subset (spec's "known best combo" end-to-end test).
        var pool = DistinctPool(8);
        var combos = ModuleOptimizerEngine.Optimize(
            TestModules.Snap(pool), new List<int> { 1110 }, AllCategories, topN: 5);
        var expected = pool.Skip(8 - ModuleOptimizerEngine.SlotCount)
            .Select(m => m.Uuid).OrderBy(u => u).ToArray();
        var actual = combos[0].Modules.Select(m => m.Uuid).OrderBy(u => u).ToArray();
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Optimize_min_sums_filter_excludes_combos_below_floor()
    {
        // Floor of 26 on attr 1110: only combos summing >= 26 survive.
        var pool = DistinctPool(8);   // values 1..8; max k-sum with k=4 is 8+7+6+5=26
        var floors = new Dictionary<int, int> { [1110] = 26 };
        var combos = ModuleOptimizerEngine.Optimize(
            TestModules.Snap(pool), new List<int> { 1110 }, AllCategories, 1_000_000, floors);
        Assert.All(combos, c => Assert.True(c.ProjectedAttrTotals[1110] >= 26));
        Assert.NotEmpty(combos);
    }

    [Fact]
    public void Optimize_category_mask_filters_candidates()
    {
        var attack = Enumerable.Range(0, ModuleOptimizerEngine.SlotCount)
            .Select(i => TestModules.Mod(ModuleCategory.Attack, (1110, i + 1))).ToArray();
        var defend = Enumerable.Range(0, ModuleOptimizerEngine.SlotCount)
            .Select(i => TestModules.Mod(ModuleCategory.Defend, (1111, i + 1))).ToArray();
        var snap = TestModules.Snap(attack.Concat(defend).ToArray());

        var attackOnly = ModuleOptimizerEngine.Optimize(snap, new List<int>(), 1, 1_000_000);
        Assert.All(attackOnly.SelectMany(c => c.Modules),
            m => Assert.Equal(ModuleCategory.Attack, m.Category));
    }
}
