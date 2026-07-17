using System.Collections.Generic;
using System.Linq;
using Stellar.Abstractions.Domain.Inventory;
using Xunit;

namespace Stellar.ModuleOptimizer.Tests;

/// <summary>
/// Unit tests for <c>Plugin.ComputeRetrySteps</c> — the pure diff at the heart
/// of the Apply verify+retry pass (owner report, live 3.7: "it equips too
/// fast and the server sometimes ignores the equip command" — a dropped
/// install RPC can leave a slot silently un-equipped even though the step's
/// own EquipResult reported success/no-op). The async pacing/retry driver
/// around this function is owner-verified in-game, not unit-tested here.
/// </summary>
public class ApplyRetryTests
{
    private static List<ModuleInfo> Combo(int count)
        => Enumerable.Range(0, count).Select(_ => TestModules.Mod()).ToList();

    // Equipped set that exactly matches `targets`, slot = index + 1 (mirrors
    // BuildPlan / ComputeRetrySteps's own slot mapping). Tests mutate the
    // returned map to introduce the mismatch they want to exercise.
    private static Dictionary<int, long> EquippedFrom(IReadOnlyList<ModuleInfo> targets)
    {
        var map = new Dictionary<int, long>();
        for (var i = 0; i < targets.Count; i++) map[i + 1] = targets[i].Uuid;
        return map;
    }

    [Fact]
    public void All_slots_matching_returns_no_retry_steps()
    {
        var targets = Combo(5);
        var equipped = EquippedFrom(targets);

        var steps = Plugin.ComputeRetrySteps(targets, equipped);

        Assert.Empty(steps);
    }

    [Fact]
    public void One_wrong_slot_returns_exactly_that_install_step()
    {
        var targets = Combo(5);
        var equipped = EquippedFrom(targets);
        equipped[3] = 999_999L;   // slot 3 (index 2) holds some other module

        var steps = Plugin.ComputeRetrySteps(targets, equipped);

        var step = Assert.Single(steps);
        Assert.Equal(3, step.Slot);
        Assert.Equal(Plugin.StepKind.Install, step.Kind);
        Assert.Equal(targets[2].Uuid, step.ModuleUuid);
    }

    [Fact]
    public void Missing_slot_counts_as_mismatched()
    {
        var targets = Combo(5);
        var equipped = EquippedFrom(targets);
        equipped.Remove(2);   // slot 2 has nothing equipped at all

        var steps = Plugin.ComputeRetrySteps(targets, equipped);

        var step = Assert.Single(steps);
        Assert.Equal(2, step.Slot);
        Assert.Equal(targets[1].Uuid, step.ModuleUuid);
    }

    [Fact]
    public void Empty_equipped_map_returns_every_slot_as_an_install_step()
    {
        var targets = Combo(5);

        var steps = Plugin.ComputeRetrySteps(targets, new Dictionary<int, long>());

        Assert.Equal(5, steps.Count);
        Assert.All(steps, s => Assert.Equal(Plugin.StepKind.Install, s.Kind));
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, steps.Select(s => s.Slot).ToArray());
        Assert.Equal(targets.Select(m => m.Uuid).ToArray(), steps.Select(s => s.ModuleUuid).ToArray());
    }

    [Fact]
    public void Extra_unexpected_slot_beyond_target_count_is_ignored()
    {
        // Contract: ComputeRetrySteps only reconciles the slots the combo
        // cares about (slots 1..targetModules.Count). Content sitting in a
        // slot beyond that range never produces a mismatch/step here — the
        // function only installs what the combo asks for, it never evicts
        // anything extra.
        var targets = Combo(5);
        var equipped = EquippedFrom(targets);
        equipped[6] = 424242L;   // untouched extra slot outside the combo

        var steps = Plugin.ComputeRetrySteps(targets, equipped);

        Assert.Empty(steps);
    }

    [Fact]
    public void Multiple_wrong_slots_return_steps_in_ascending_slot_order()
    {
        var targets = Combo(5);
        var equipped = EquippedFrom(targets);
        equipped[5] = 111L;
        equipped[2] = 222L;

        var steps = Plugin.ComputeRetrySteps(targets, equipped);

        Assert.Equal(new[] { 2, 5 }, steps.Select(s => s.Slot).ToArray());
    }
}
