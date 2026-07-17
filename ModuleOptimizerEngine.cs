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
/// per-attribute SUMS, so it is NOT separable — the best 5-subset is not the 5
/// best individual modules. We therefore prefilter to a small candidate pool
/// (mirroring AutoMod's <c>_prefilter_modules_by_total_scores</c>, extended with
/// per-floor reservation — see <see cref="Prefilter"/> — so a scarce floored
/// attr (AutoMod's <c>-mas</c>; see <see cref="MeetsMinSums"/>) can't be starved
/// out by an unrelated target attr dominating the combined ranking) and then
/// brute-force every SlotCount-combination of that pool, scoring each by combat
/// power. C(<see cref="PrefilterCount"/>=40, 5) = 658,008 combos — still trivial
/// on a click.
///
/// A static, UnityEngine-free type (not a <c>Plugin</c> partial) so the algorithm
/// is independently unit-testable.
/// </summary>
internal static class ModuleOptimizerEngine
{
    internal const int SlotCount = 5;

    // Candidate pool size after the total-score prefilter (AutoMod uses a tunable
    // enumeration count). 40 keeps C(40,5)=658,008 cheap (~sub-second). NOTE: a
    // combined-sum-only ranking is NOT sufficient on its own to satisfy floors —
    // an abundant/high-rolling target attr can dominate the combined ranking and
    // starve a scarcer floored attr out of the pool entirely (task-8). Prefilter
    // therefore reserves room per floored attr (see FloorReserveCount) BEFORE
    // filling the remaining slots by combined-sum, so the pool can satisfy any
    // floor the inventory can actually reach.
    private const int PrefilterCount = 40;

    // Per floored attr, the top this-many candidates ranked by THAT attr alone
    // are reserved into the pool ahead of the ordinary combined-sum fill. A
    // floor can be cleared by at most SlotCount modules, so reserving 10 leaves
    // slack even after ties/near-misses, while capping worst-case reservation
    // at FloorReserveCount * (number of floored attrs).
    private const int FloorReserveCount = 10;

    /// <summary>
    /// Runs the optimizer: filter by category mask, prefilter to the top
    /// <see cref="PrefilterCount"/> — reserving room per floored attr in
    /// <paramref name="minSums"/> before filling the rest by summed target-attr
    /// value (see <see cref="Prefilter"/>) — enumerate all SlotCount-combinations
    /// of that pool, drop any that fail the min-attr-sum floors, score the
    /// survivors by combat power, and return the top <paramref name="topN"/> by
    /// score (desc, stable). An empty list means either too few candidates or no
    /// combo cleared the floors.
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

        var pool = Prefilter(candidates, targetIds, PrefilterCount, minSums);
        return EnumerateTopCombos(pool, targetIds, topN, minSums);
    }

    private static bool CategoryInMask(ModuleCategory category, int mask)
        => (mask & (1 << ((int)category - 1))) != 0;

    // Two-phase pool build: (1) reserve up to FloorReserveCount candidates per
    // floored attr — ranked by THAT attr alone — so a scarce floor can never be
    // starved out by an unrelated target attr dominating the combined ranking;
    // (2) fill the remaining slots (up to `count` total) by the ordinary
    // combined-sum-of-target-attrs ranking (or all parts when no targets are
    // selected — AutoMod's no-target fallback), skipping anything already
    // reserved. With no floored attrs, step 1 reserves nothing and this is
    // byte-identical to the pre-fix single-phase ranking. Stable tiebreak by
    // Uuid throughout so the pool is deterministic.
    private static List<ModuleInfo> Prefilter(
        List<ModuleInfo> candidates, IReadOnlyList<int> targetIds, int count,
        IReadOnlyDictionary<int, int>? minSums)
    {
        var hasTargets = targetIds.Count > 0;
        var targetSet = hasTargets ? new HashSet<int>(targetIds) : null;

        var pool = ReserveFloorCandidates(candidates, minSums, count);
        var remaining = count - pool.Count;
        if (remaining <= 0) return pool;

        var reservedUuids = pool.Count > 0 ? new HashSet<long>(pool.Select(m => m.Uuid)) : null;
        pool.AddRange(candidates
            .Where(m => reservedUuids is null || !reservedUuids.Contains(m.Uuid))
            .OrderByDescending(m => SumForPrefilter(m, targetSet))
            .ThenByDescending(m => m.Uuid)
            .Take(remaining));

        return pool;
    }

    // Reserves, per attr with a floor (minSums value > 0), the top
    // FloorReserveCount candidates carrying a non-zero value for that attr
    // (ranked desc by that attr's own value, Uuid-desc tiebreak). A module
    // reserved by two floors counts once. Never returns more than `cap`
    // entries — defensive against pathological inputs with more floored
    // attrs than the pool can hold (worst case exercised by task-8's
    // overflow-guard test: 4 floors * 10 reserves = 40 = the whole pool).
    private static List<ModuleInfo> ReserveFloorCandidates(
        List<ModuleInfo> candidates, IReadOnlyDictionary<int, int>? minSums, int cap)
    {
        var reserved = new List<ModuleInfo>();
        if (minSums is null) return reserved;

        var seenUuids = new HashSet<long>();
        foreach (var kv in minSums)
        {
            if (reserved.Count >= cap) break;
            if (kv.Value <= 0) continue;

            var topForAttr = candidates
                .Where(m => AttrValue(m, kv.Key) > 0)
                .OrderByDescending(m => AttrValue(m, kv.Key))
                .ThenByDescending(m => m.Uuid)
                .Take(FloorReserveCount);
            foreach (var module in topForAttr)
            {
                if (reserved.Count >= cap) break;
                if (seenUuids.Add(module.Uuid)) reserved.Add(module);
            }
        }
        return reserved;
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

    private static int AttrValue(ModuleInfo module, int attrId)
    {
        var sum = 0;
        foreach (var part in module.Parts)
        {
            if (part.AttrId == attrId) sum += part.Value;
        }
        return sum;
    }

    // Enumerate every SlotCount-combination of the prefiltered pool via an
    // iterative lexicographic index array (replaces the former literal 4-deep
    // nested loops, so the slot count lives in exactly one constant). Attr sums
    // are accumulated into two REUSED dictionaries per candidate; scoring and
    // ModuleCombo materialization happen only for combos that clear the
    // min-attr-sum floors — not for all C(n,k) candidates. OrderByDescending is
    // stable, so ties preserve enumeration order (index-ascending over the pool).
    private static List<ModuleCombo> EnumerateTopCombos(
        List<ModuleInfo> pool,
        IReadOnlyList<int> targetIds,
        int topN,
        IReadOnlyDictionary<int, int>? minSums)
    {
        var combos = new List<ModuleCombo>();
        var n = pool.Count;
        var k = SlotCount;
        var idx = new int[k];
        for (var i = 0; i < k; i++) idx[i] = i;
        var breakdown = new Dictionary<int, int>();              // all attrs — combat-power input
        var totals = new Dictionary<int, int>(targetIds.Count);  // target attrs — floor gate + preview

        while (true)
        {
            AccumulateSums(pool, idx, targetIds, breakdown, totals);
            if (MeetsMinSums(totals, minSums))
            {
                var modules = new ModuleInfo[k];
                for (var i = 0; i < k; i++) modules[i] = pool[idx[i]];
                combos.Add(new ModuleCombo(
                    modules, CombatPower.Score(breakdown), new Dictionary<int, int>(totals)));
            }

            // Advance to the next lexicographic combination; done when the
            // leftmost index can no longer move.
            var pos = k - 1;
            while (pos >= 0 && idx[pos] == n - k + pos) pos--;
            if (pos < 0) break;
            idx[pos]++;
            for (var i = pos + 1; i < k; i++) idx[i] = idx[i - 1] + 1;
        }

        return combos
            .OrderByDescending(combo => combo.Score)
            .Take(topN)
            .ToList();
    }

    // Recompute the per-attr sums for the candidate at `idx`: `breakdown` gets
    // every attr (combat-power input), `totals` only the target attrs (floor
    // gate + ProjectedAttrTotals). Both are cleared and refilled — reused
    // across candidates to avoid per-candidate garbage.
    private static void AccumulateSums(
        List<ModuleInfo> pool, int[] idx, IReadOnlyList<int> targetIds,
        Dictionary<int, int> breakdown, Dictionary<int, int> totals)
    {
        breakdown.Clear();
        totals.Clear();
        foreach (var id in targetIds) totals[id] = 0;
        for (var i = 0; i < idx.Length; i++)
        {
            foreach (var part in pool[idx[i]].Parts)
            {
                breakdown.TryGetValue(part.AttrId, out var prev);
                breakdown[part.AttrId] = prev + part.Value;
                if (totals.ContainsKey(part.AttrId)) totals[part.AttrId] += part.Value;
            }
        }
    }

    /// <summary>
    /// Hard min-attr-sum gate (AutoMod's <c>-mas</c> / <c>_filter_by_min_attr</c>):
    /// the combo passes iff, for EVERY attr with a floor &gt; 0, its summed total
    /// across the SlotCount picked modules is &gt;= the floor. Floors are read
    /// against the TARGET-attr totals (an attr absent from the current targets
    /// counts as 0 — unchanged semantics). A null/empty map, or all-zero floors,
    /// means no constraint. This is a FILTER only — the score is unaffected.
    /// </summary>
    internal static bool MeetsMinSums(
        IReadOnlyDictionary<int, int> totals, IReadOnlyDictionary<int, int>? minSums)
    {
        if (minSums is null) return true;
        foreach (var kv in minSums)
        {
            if (kv.Value <= 0) continue;
            totals.TryGetValue(kv.Key, out var total);
            if (total < kv.Value) return false;
        }
        return true;
    }
}
