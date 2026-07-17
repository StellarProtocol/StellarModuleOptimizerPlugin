using System.Collections.Generic;
using System.Linq;
using Stellar.Abstractions.Domain.Inventory;
using Xunit;

namespace Stellar.ModuleOptimizer.Tests;

public class CombinationParityTests
{
    // Reference k-combination enumerator (recursive, obviously correct).
    private static IEnumerable<long[]> RefCombos(long[] items, int k)
    {
        if (k == 0) { yield return System.Array.Empty<long>(); yield break; }
        for (var i = 0; i <= items.Length - k; i++)
        {
            foreach (var rest in RefCombos(items.Skip(i + 1).ToArray(), k - 1))
            {
                yield return new[] { items[i] }.Concat(rest).ToArray();
            }
        }
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(9)]
    public void Engine_enumerates_exactly_the_reference_combination_set(int n)
    {
        if (n < ModuleOptimizerEngine.SlotCount) return;
        var pool = Enumerable.Range(0, n)
            .Select(i => TestModules.Mod(parts: (1110, i + 1)))
            .ToArray();

        var combos = ModuleOptimizerEngine.Optimize(
            TestModules.Snap(pool), new List<int>(), 7, topN: 1_000_000);
        var actual = combos
            .Select(c => string.Join(",", c.Modules.Select(m => m.Uuid).OrderBy(u => u)))
            .OrderBy(s => s)
            .ToList();

        var expected = RefCombos(pool.Select(m => m.Uuid).ToArray(), ModuleOptimizerEngine.SlotCount)
            .Select(ids => string.Join(",", ids.OrderBy(u => u)))
            .OrderBy(s => s)
            .ToList();

        Assert.Equal(expected, actual);
    }
}
