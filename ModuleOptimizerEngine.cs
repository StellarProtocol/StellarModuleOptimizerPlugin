using System.Collections.Generic;
using System.Linq;
using Stellar.Abstractions.Domain.Inventory;

namespace Stellar.ModuleOptimizer;

/// <summary>
/// A candidate equip combination: the picked modules, the combat-power score,
/// and the per-target-attr projected totals (summed across the picked modules,
/// used by the Preview overlay and the min-attr-sum gate). Top-level + free of
/// UnityEngine so it (and <see cref="ModuleOptimizerEngine"/>) can be unit-tested
/// without a live IL2CPP host — the same isolation <see cref="CombatPower"/> has.
/// </summary>
internal sealed class ModuleCombo
{
    public ModuleCombo(IReadOnlyList<ModuleInfo> modules, int score,
        IReadOnlyDictionary<int, int> projectedAttrTotals)
    {
        Modules = modules;
        Score = score;
        ProjectedAttrTotals = projectedAttrTotals;
    }

    public IReadOnlyList<ModuleInfo> Modules { get; }
    public int Score { get; }
    public IReadOnlyDictionary<int, int> ProjectedAttrTotals { get; }
}

/// <summary>
/// Selection logic for the optimizer, ported from StarResonanceAutoMod. Scoring
/// is the combat-power model in <see cref="CombatPower"/>: threshold-based on
/// per-attribute SUMS, so it is NOT separable — the best 4-subset is not the 4
/// best individual modules. We therefore prefilter to a small candidate pool
/// (mirroring AutoMod's <c>_prefilter_modules_by_total_scores</c>) and then
/// brute-force every 4-combination of that pool, scoring each by combat power.
/// C(<see cref="PrefilterCount"/>=40, 4) = 91390 combos — still trivial on a
/// click, with enough headroom for strict min-attr-sum constraints (AutoMod's
/// <c>-mas</c>; see <see cref="MeetsMinSums"/>) to be satisfiable.
///
/// A static, UnityEngine-free type (not a <c>Plugin</c> partial) so the algorithm
/// is independently unit-testable.
/// </summary>
internal static class ModuleOptimizerEngine
{
    internal const int SlotCount = 4;

    // Candidate pool size after the total-score prefilter (AutoMod uses a tunable
    // enumeration count). 40 keeps C(40,4)=91390 cheap while giving headroom for
    // strict min-attr-sum floors: the prefilter ranks target-rich modules first,
    // which is exactly the pool a min-sum needs, so a deeper pool widens the set
    // of combos that can clear the floors.
    private const int PrefilterCount = 40;

    /// <summary>
    /// Runs the optimizer: filter by category mask, prefilter to the top
    /// <see cref="PrefilterCount"/> by summed target-attr value, enumerate all
    /// 4-combinations of that pool, drop any that fail the min-attr-sum floors
    /// in <paramref name="minSums"/>, score the survivors by combat power, and
    /// return the top <paramref name="topN"/> by score (desc, stable). An empty
    /// list means either too few candidates or no combo cleared the floors.
    /// </summary>
    internal static List<ModuleCombo> Optimize(
        ModuleSnapshot inventory,
        IReadOnlyList<int> targetIds,
        int categoryMask,
        int topN,
        IReadOnlyDictionary<int, int>? minSums = null)
    {
        var candidates = inventory.Modules
            .Where(m => CategoryInMask(m.Category, categoryMask))
            .ToList();
        if (candidates.Count < SlotCount)
        {
            return new List<ModuleCombo>();
        }

        var pool = Prefilter(candidates, targetIds, PrefilterCount);
        return EnumerateTopCombos(pool, targetIds, topN, minSums);
    }

    private static bool CategoryInMask(ModuleCategory category, int mask)
        => (mask & (1 << ((int)category - 1))) != 0;

    // Rank each module by the SUM of its part values for the target attrs (or all
    // parts when no targets are selected — AutoMod's no-target fallback), and keep
    // the top `count`. Stable tiebreak by Uuid so the pool is deterministic.
    private static List<ModuleInfo> Prefilter(
        List<ModuleInfo> candidates, IReadOnlyList<int> targetIds, int count)
    {
        var hasTargets = targetIds.Count > 0;
        var targetSet = hasTargets ? new HashSet<int>(targetIds) : null;

        return candidates
            .OrderByDescending(m => SumForPrefilter(m, targetSet))
            .ThenByDescending(m => m.Uuid)
            .Take(count)
            .ToList();
    }

    private static int SumForPrefilter(ModuleInfo module, HashSet<int>? targetSet)
    {
        var sum = 0;
        foreach (var part in module.Parts)
        {
            if (targetSet is null || targetSet.Contains(part.AttrId)) sum += part.Value;
        }
        return sum;
    }

    // Enumerate every 4-combination of the prefiltered pool, score by combat power,
    // drop combos that miss any min-attr-sum floor, keep the top `topN` by score
    // (desc). OrderByDescending is stable, so ties preserve enumeration order
    // (which is index-ascending over the pool).
    private static List<ModuleCombo> EnumerateTopCombos(
        List<ModuleInfo> pool,
        IReadOnlyList<int> targetIds,
        int topN,
        IReadOnlyDictionary<int, int>? minSums)
    {
        var combos = new List<ModuleCombo>();
        var n = pool.Count;
        for (var a = 0; a < n - 3; a++)
        for (var b = a + 1; b < n - 2; b++)
        for (var c = b + 1; c < n - 1; c++)
        for (var d = c + 1; d < n; d++)
        {
            var modules = new List<ModuleInfo> { pool[a], pool[b], pool[c], pool[d] };
            var combo = BuildCombo(modules, targetIds);
            if (MeetsMinSums(combo, minSums)) combos.Add(combo);
        }

        return combos
            .OrderByDescending(combo => combo.Score)
            .Take(topN)
            .ToList();
    }

    /// <summary>
    /// Hard min-attr-sum gate (AutoMod's <c>-mas</c> / <c>_filter_by_min_attr</c>):
    /// the combo passes iff, for EVERY attr with a floor &gt; 0, its summed total
    /// across the 4 modules is &gt;= the floor. A null/empty map, or all-zero
    /// floors, means no constraint (everything passes). This is a FILTER only —
    /// the combat-power score is unaffected.
    /// </summary>
    internal static bool MeetsMinSums(ModuleCombo combo, IReadOnlyDictionary<int, int>? minSums)
    {
        if (minSums is null) return true;
        foreach (var kv in minSums)
        {
            if (kv.Value <= 0) continue;
            combo.ProjectedAttrTotals.TryGetValue(kv.Key, out var total);
            if (total < kv.Value) return false;
        }
        return true;
    }

    private static ModuleCombo BuildCombo(List<ModuleInfo> modules, IReadOnlyList<int> targetIds)
    {
        var totals = new Dictionary<int, int>(targetIds.Count);
        foreach (var id in targetIds) totals[id] = 0;

        foreach (var m in modules)
        {
            foreach (var part in m.Parts)
            {
                if (totals.ContainsKey(part.AttrId)) totals[part.AttrId] += part.Value;
            }
        }

        var score = CombatPower.ScoreCombo(modules);
        return new ModuleCombo(modules, score, totals);
    }
}
