using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stellar.Abstractions.Domain.Inventory;
using UnityEngine;

namespace Stellar.ModuleOptimizer;

/// <summary>
/// Post-plan verify + one-retry pass for the Apply flow (see
/// <see cref="Plugin"/>'s Apply partial for the main state machine). Owner
/// report from live 3.7 play: "it equips too fast and the server sometimes
/// ignores the equip command" — the server can silently drop an equip RPC
/// while the client-side poll in <see cref="Stellar.Abstractions.Services.IModuleEquip"/>
/// still resolves as success, because the game's local ModSlots map updates
/// optimistically ahead of the server ack. <see cref="Plugin.ExecuteStepAsync"/>
/// has no way to see that drop from inside a single step, so once the whole
/// plan has run we let the last step's ack settle, diff the live equipped set
/// against the target combo, and retry exactly the slots that didn't stick.
/// </summary>
public sealed partial class Plugin
{
    // Returns false once EnterFailed has been called (caller should stop);
    // true when the equipped set matches the combo (whether or not a retry
    // pass was needed).
    private async Task<bool> VerifyAndRetryAsync(IReadOnlyList<ModuleInfo> targetModules, CancellationToken ct)
    {
        await Task.Delay(InterStepDelayMs * 2, ct).ConfigureAwait(true);
        var mismatched = ComputeRetrySteps(targetModules, CurrentEquippedBySlot());
        if (mismatched.Count == 0) return true;

        // Re-run just the mismatched slots — same pacing, same per-step
        // EquipResult handling (RunStepsAsync/ExecuteStepAsync both read the
        // currently-assigned _applyPlan, so repointing it here is what makes
        // the Running footer track this retry batch's own progress).
        _applyPlan = mismatched;
        _applyStepIndex = 0;
        if (!await RunStepsAsync(ct).ConfigureAwait(true))
            return false;

        await Task.Delay(InterStepDelayMs * 2, ct).ConfigureAwait(true);
        var stillMismatched = ComputeRetrySteps(targetModules, CurrentEquippedBySlot());
        if (stillMismatched.Count == 0) return true;

        var slotWord = stillMismatched.Count == 1 ? "slot" : "slots";
        var slots = string.Join(",", stillMismatched.Select(s => s.Slot));
        EnterFailed(Mathf.Max(1, _applyStepIndex), EquipResult.RpcError,
            $"{slotWord} {slots} did not stick (server ignored the equip) — Apply again");
        return false;
    }

    private IReadOnlyDictionary<int, long> CurrentEquippedBySlot()
        => _services.Inventory.GetEquipped()?.ModuleUuidsBySlot ?? EmptyEquippedBySlot;

    private static readonly IReadOnlyDictionary<int, long> EmptyEquippedBySlot = new Dictionary<int, long>();

    // Compute the Install steps needed to reconcile `equippedBySlot` with
    // `targetModules` (slot = index + 1, mirroring Plugin.BuildPlan). A slot
    // counts as mismatched when it's missing from the map entirely OR holds a
    // uuid other than the target's — both read as "this slot didn't stick"
    // from the owner's dropped-RPC report. Slots present in `equippedBySlot`
    // beyond `targetModules.Count` are ignored: this reconciles only the
    // slots the combo cares about, it does not evict anything extra. Unlike
    // BuildPlan this never emits an Uninstall step — a retry only fires for
    // slots the main plan already ran an uninstall+install pair against, so
    // any leftover mismatch is attributed to a dropped install RPC. If a
    // retried InstallAsync instead hits a real conflict (e.g. the uninstall
    // was ALSO dropped and the slot is still occupied), ExecuteStepAsync's
    // existing EquipResult handling reports that failure through the normal
    // EnterFailed path — this function doesn't need to special-case it.
    //
    // A pure static function (no _services access) so it's unit-testable
    // without a live IInventory/IModuleEquip — see ApplyRetryTests.
    internal static List<ApplyStep> ComputeRetrySteps(
        IReadOnlyList<ModuleInfo> targetModules,
        IReadOnlyDictionary<int, long> equippedBySlot)
    {
        var steps = new List<ApplyStep>();
        for (var s = 0; s < targetModules.Count; s++)
        {
            var slot = s + 1;
            var module = targetModules[s];
            if (equippedBySlot.TryGetValue(slot, out var curUuid) && curUuid == module.Uuid)
                continue;   // matches — nothing to retry for this slot

            var label = string.IsNullOrEmpty(module.Name) ? $"module {module.Uuid}" : module.Name;
            steps.Add(new ApplyStep(slot, StepKind.Install, module.Uuid, label));
        }
        return steps;
    }
}
