using System.Collections.Generic;
using System.Linq;
using Stellar.Abstractions.Domain.Inventory;
using Xunit;

namespace Stellar.ModuleOptimizer.Tests;

/// <summary>
/// Regression coverage for the floor-aware prefilter fix (task-8): a
/// combined-sum-only prefilter can starve a scarce floored target attr out of
/// the candidate pool when an abundant/high-rolling target attr dominates the
/// ranking. See ModuleOptimizerEngine.Prefilter for the reservation algorithm.
/// </summary>
public class PrefilterFloorTests
{
    private const int AllCategories = 7;

    // --- Test 1: RED repro -------------------------------------------------
    // 60 abundant modules (EliteStrike-like attr, value 10 each) combined-sum
    // rank strictly above 8 scarce modules (LifeWave-like attr, value 9 each),
    // so a plain combined-sum prefilter fills its entire 40-slot pool from the
    // abundant attr alone and the LifeWave floor becomes unsatisfiable even
    // though 8 genuinely-qualifying modules exist. Pre-fix: Assert.NotEmpty
    // FAILS here. Post-fix: non-empty, and every combo clears the floor.
    [Fact]
    public void Optimize_floor_on_scarce_attr_survives_combined_sum_starvation()
    {
        const int eliteStrikeId = 2104; // abundant target attr — dominates the combined-sum ranking
        const int lifeWaveId = 1407;    // scarce target attr, carries the floor

        var abundantEliteStrike = Enumerable.Range(0, 60)
            .Select(_ => TestModules.Mod(parts: (eliteStrikeId, 10)))
            .ToArray();
        var scarceLifeWave = Enumerable.Range(0, 8)
            .Select(_ => TestModules.Mod(parts: (lifeWaveId, 9)))
            .ToArray();
        var snap = TestModules.Snap(abundantEliteStrike.Concat(scarceLifeWave).ToArray());

        var targets = new List<int> { eliteStrikeId, lifeWaveId };
        var floors = new Dictionary<int, int> { [lifeWaveId] = 20 };

        var combos = ModuleOptimizerEngine.Optimize(snap, targets, AllCategories, topN: 100, floors);

        Assert.NotEmpty(combos);
        Assert.All(combos, c => Assert.True(c.ProjectedAttrTotals[lifeWaveId] >= 20));
    }

    // --- Test 2: no-floor parity --------------------------------------------
    // With no floored attrs, the reservation step must contribute nothing, so
    // the pool must be byte-identical to the pre-fix "combined-sum desc,
    // Uuid desc, take PrefilterCount" ranking — for BOTH null and all-zero
    // minSums maps. n=41 is the minimal pool exceeding the (private)
    // PrefilterCount=40, so the Take(40) boundary is actually exercised: the
    // expected pool, computed by hand from the ranking rule, is every module
    // except the single lowest-value one (index 0, value 1).
    [Fact]
    public void Optimize_no_floors_matches_hand_computed_prefix_pool_for_null_and_allzero()
    {
        const int attrA = 8101;
        const int attrB = 8102;
        const int n = 41; // PrefilterCount(40) + 1: exercises the Take(40) boundary drop

        var modules = Enumerable.Range(0, n)
            .Select(i => TestModules.Mod(parts: (i % 2 == 0 ? attrA : attrB, i + 1)))
            .ToArray();
        // Hand-computed expected pool: top 40 by (own) value, i.e. every module
        // except the single lowest-value one (index 0, value 1) — no ties, so
        // the Uuid tiebreak is never exercised here.
        var expectedPoolUuids = modules.Skip(1).Select(m => m.Uuid).ToHashSet();

        var targets = new List<int> { attrA, attrB };
        var snap = TestModules.Snap(modules);
        var expectedComboCount = TestModules.Choose(40, ModuleOptimizerEngine.SlotCount);

        var combosNull = ModuleOptimizerEngine.Optimize(snap, targets, AllCategories, topN: 700_000, minSums: null);
        var combosAllZero = ModuleOptimizerEngine.Optimize(
            snap, targets, AllCategories, topN: 700_000,
            minSums: new Dictionary<int, int> { [attrA] = 0, [attrB] = 0 });

        Assert.Equal(expectedComboCount, combosNull.Count);
        Assert.Equal(expectedComboCount, combosAllZero.Count);

        var actualPoolFromNull = combosNull.SelectMany(c => c.Modules).Select(m => m.Uuid).ToHashSet();
        var actualPoolFromAllZero = combosAllZero.SelectMany(c => c.Modules).Select(m => m.Uuid).ToHashSet();
        Assert.Equal(expectedPoolUuids, actualPoolFromNull);
        Assert.Equal(expectedPoolUuids, actualPoolFromAllZero);

        // Same pool + same (untouched) scoring code => identical order too.
        var orderedKeysNull = combosNull
            .Select(c => string.Join(",", c.Modules.Select(m => m.Uuid))).ToList();
        var orderedKeysAllZero = combosAllZero
            .Select(c => string.Join(",", c.Modules.Select(m => m.Uuid))).ToList();
        Assert.Equal(orderedKeysNull, orderedKeysAllZero);
    }

    // --- Test 3: two floored attrs ------------------------------------------
    // A dominant, high-value target attr starves BOTH floorA and floorB out
    // of a plain combined-sum prefilter (same shape of bug as test 1, just
    // with two scarce attrs instead of one). Both must get reserved, and
    // returned combos must satisfy both floors simultaneously.
    [Fact]
    public void Optimize_two_floored_attrs_both_reserved_and_satisfied()
    {
        const int dominantId = 9001;
        const int floorAId = 9002;
        const int floorBId = 9003;

        var dominant = Enumerable.Range(0, 60)
            .Select(_ => TestModules.Mod(parts: (dominantId, 100)))
            .ToArray();
        var floorACarriers = Enumerable.Range(0, 8)
            .Select(_ => TestModules.Mod(parts: (floorAId, 10)))
            .ToArray();
        var floorBCarriers = Enumerable.Range(0, 8)
            .Select(_ => TestModules.Mod(parts: (floorBId, 10)))
            .ToArray();
        var snap = TestModules.Snap(dominant.Concat(floorACarriers).Concat(floorBCarriers).ToArray());

        var targets = new List<int> { dominantId, floorAId, floorBId };
        var floors = new Dictionary<int, int> { [floorAId] = 20, [floorBId] = 20 };

        var combos = ModuleOptimizerEngine.Optimize(snap, targets, AllCategories, topN: 100, floors);

        Assert.NotEmpty(combos);
        Assert.All(combos, c =>
        {
            Assert.True(c.ProjectedAttrTotals[floorAId] >= 20);
            Assert.True(c.ProjectedAttrTotals[floorBId] >= 20);
        });
    }

    // --- Test 4: unsatisfiable floor -----------------------------------------
    // The inventory genuinely cannot reach the floor (max any single module
    // contributes is 10, so 5 slots cap out at 50, nowhere near 1000) — the
    // fix must not manufacture false positives; empty stays reachable.
    [Fact]
    public void Optimize_unsatisfiable_floor_still_returns_empty()
    {
        const int eliteStrikeId = 2104;
        const int lifeWaveId = 1407;

        var abundantEliteStrike = Enumerable.Range(0, 60)
            .Select(_ => TestModules.Mod(parts: (eliteStrikeId, 10)))
            .ToArray();
        var scarceLifeWave = Enumerable.Range(0, 8)
            .Select(_ => TestModules.Mod(parts: (lifeWaveId, 9)))
            .ToArray();
        var snap = TestModules.Snap(abundantEliteStrike.Concat(scarceLifeWave).ToArray());

        var targets = new List<int> { eliteStrikeId, lifeWaveId };
        var floors = new Dictionary<int, int> { [lifeWaveId] = 1000 };

        var combos = ModuleOptimizerEngine.Optimize(snap, targets, AllCategories, topN: 100, floors);

        Assert.Empty(combos);
    }

    // --- Test 5: reservation overflow guard ----------------------------------
    // 4 floored attrs x FloorReserveCount(10) reserves = 40 = the whole
    // PrefilterCount pool, leaving zero fill slots. A 5th, unfloored target
    // attr carries an enormous value on 5 "decoy" modules that would dominate
    // the ordinary combined-sum fill ranking if any room were left. Asserts:
    // no exception/overflow, floors still satisfied, and the decoys never
    // make it into the pool (because there is no room left to fill).
    [Fact]
    public void Optimize_many_floors_never_overflow_prefilter_pool()
    {
        var floorIds = new[] { 9101, 9102, 9103, 9104 };
        var carrierGroups = floorIds
            .Select(id => Enumerable.Range(0, 10)
                .Select(i => TestModules.Mod(parts: (id, i + 1))) // values 1..10 per group
                .ToArray())
            .ToArray();

        const int decoyId = 9105; // unfloored — would dominate the ordinary fill ranking if given room
        var decoys = Enumerable.Range(0, 5)
            .Select(_ => TestModules.Mod(parts: (decoyId, 100_000)))
            .ToArray();

        var allModules = carrierGroups.SelectMany(g => g).Concat(decoys).ToArray();
        var snap = TestModules.Snap(allModules);

        var targets = new List<int>(floorIds) { decoyId };
        var floors = floorIds.ToDictionary(id => id, _ => 10); // each satisfied by its single value=10 carrier

        var combos = ModuleOptimizerEngine.Optimize(snap, targets, AllCategories, topN: 1000, floors);

        Assert.NotEmpty(combos);
        var decoyUuids = decoys.Select(m => m.Uuid).ToHashSet();
        Assert.All(combos, c => Assert.True(c.Modules.All(m => !decoyUuids.Contains(m.Uuid))));
        Assert.All(combos, c =>
        {
            foreach (var id in floorIds)
            {
                Assert.True(c.ProjectedAttrTotals[id] >= 10);
            }
        });
    }

    // --- Test 6: five-floored overflow + determinism -------------------------
    // 5 floored attrs with 11 carriers each (55 total > PrefilterCount 40), so
    // ReserveFloorCandidates will overflow: truncation order depends on which
    // attr is processed last when the cap is hit. Without canonical iteration
    // (ascending attrId), different insertion orders of the minSums dict would
    // truncate different attrs, yielding different pools and combos. With
    // canonical order, the same attr is always truncated, yielding identical
    // results. Asserts: (a) no exception on both insertion orders; (b) returned
    // combos (if any) satisfy all floors they can satisfy (attrs in the pool);
    // (c) determinism: both insertion orders (ascending then descending attrId)
    // yield identical combo uuid-set sequences.
    [Fact]
    public void Optimize_five_floored_attrs_overflow_and_deterministic()
    {
        var floorIds = new[] { 9201, 9202, 9203, 9204, 9205 }; // 5 attrs
        var carrierGroups = floorIds
            .Select(id => Enumerable.Range(0, 11) // 11 carriers per attr, total 55 > 40 cap
                .Select(i => TestModules.Mod(parts: (id, i + 1))) // values 1..11 per group
                .ToArray())
            .ToArray();

        var allModules = carrierGroups.SelectMany(g => g).ToArray();
        var snap = TestModules.Snap(allModules);
        var targets = new List<int>(floorIds);

        // Build floors dict with attrs in ASCENDING order.
        var floorsAscending = new Dictionary<int, int>();
        foreach (var id in floorIds.OrderBy(x => x))
        {
            floorsAscending[id] = 5; // satisfiable per-attr if that attr is in the pool
        }

        // Build floors dict with attrs in DESCENDING order.
        var floorsDescending = new Dictionary<int, int>();
        foreach (var id in floorIds.OrderByDescending(x => x))
        {
            floorsDescending[id] = 5;
        }

        // Run both.
        var combosAscending = ModuleOptimizerEngine.Optimize(snap, targets, AllCategories, topN: 1000, floorsAscending);
        var combosDescending = ModuleOptimizerEngine.Optimize(snap, targets, AllCategories, topN: 1000, floorsDescending);

        // (a) Both should complete without exception.
        // (b) All returned combos satisfy all floors they can satisfy (attrs in their module set).
        var poolAttrIdsAscending = combosAscending.SelectMany(c => c.Modules).Select(m => m.Parts.Select(p => p.AttrId)).SelectMany(x => x).Distinct().ToHashSet();
        Assert.All(combosAscending, c =>
        {
            foreach (var (id, floor) in floorsAscending)
            {
                if (poolAttrIdsAscending.Contains(id))
                {
                    Assert.True(c.ProjectedAttrTotals[id] >= floor, $"Ascending: attr {id} in pool but failed floor");
                }
            }
            Assert.Equal(ModuleOptimizerEngine.SlotCount, c.Modules.Count);
        });

        var poolAttrIdsDescending = combosDescending.SelectMany(c => c.Modules).Select(m => m.Parts.Select(p => p.AttrId)).SelectMany(x => x).Distinct().ToHashSet();
        Assert.All(combosDescending, c =>
        {
            foreach (var (id, floor) in floorsDescending)
            {
                if (poolAttrIdsDescending.Contains(id))
                {
                    Assert.True(c.ProjectedAttrTotals[id] >= floor, $"Descending: attr {id} in pool but failed floor");
                }
            }
            Assert.Equal(ModuleOptimizerEngine.SlotCount, c.Modules.Count);
        });

        // (c) Determinism: both insertion orders yield identical combo sequences
        // (same pool → same combos, regardless of dict insertion order).
        Assert.Equal(combosAscending.Count, combosDescending.Count);
        var keysAscending = combosAscending
            .Select(c => string.Join(",", c.Modules.OrderBy(m => m.Uuid).Select(m => m.Uuid)))
            .ToList();
        var keysDescending = combosDescending
            .Select(c => string.Join(",", c.Modules.OrderBy(m => m.Uuid).Select(m => m.Uuid)))
            .ToList();
        Assert.Equal(keysAscending, keysDescending);
    }
}
