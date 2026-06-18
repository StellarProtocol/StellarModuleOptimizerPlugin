using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stellar.Abstractions.Domain.Inventory;
using Stellar.Abstractions.Services;
using UnityEngine;

namespace Stellar.ModuleOptimizer;

/// <summary>
/// The Apply state machine (UI design §2.4 / D6): Idle → Confirm[3s] → Running →
/// Done[2s] / Failed, with a foreign-inventory-change substate of Running. The
/// flow diffs the chosen combo against the currently equipped set and applies
/// the per-slot uninstall→install steps serially through
/// <see cref="Stellar.Abstractions.Services.IModuleEquip"/>; each await of an
/// <c>EquipResult</c> is the per-step completion gate (B2 polling resolves the
/// Task once the game's ModSlots actually changes).
/// </summary>
public sealed partial class Plugin
{
    private enum ApplyState { Idle, Confirm, Running, Done, Failed }

    private enum StepKind { Uninstall, Install }

    private readonly record struct ApplyStep(int Slot, StepKind Kind, long ModuleUuid, string ModuleName);

    private ApplyState _applyState = ApplyState.Idle;
    private int _applyComboIndex = -1;
    private float _applyStateChangedAt;

    private List<ApplyStep> _applyPlan = new();
    private int _applyStepIndex;                 // 1-based index of the dispatched step
    private CancellationTokenSource? _applyCts;

    // Failed-state display.
    private int _failedStepNumber;
    private EquipResult _failedResult;
    private string _failedSubLine = string.Empty;

    // Foreign-change substate.
    private bool _foreignPrompt;
    private float _foreignPromptAt;
    private TaskCompletionSource<bool>? _foreignContinue;

    // Confirm-footer snapshot (taken on Idle→Confirm so the footer Funcs read a
    // cached plan rather than re-diffing the combo each poll).
    private readonly List<string> _confirmStepLines = new(SlotCount);
    private int _confirmSlotCount;

    // ---- Per-row Apply button (element tree, built in BuildComboSlots) -------

    // The Apply button's label + enablement track the live state machine: a row
    // that's already equipped shows a disabled "─────"; the row mid-confirm shows
    // "Confirm"; every row freezes during Confirm/Running on any row.
    private HudElement BuildApplyButton(int idx) => new ButtonElement(
        () => ApplyButtonLabel(idx),
        () => { if (idx < _combos.Count) OnApplyClicked(idx, _combos[idx]); },
        Enabled: () => ApplyButtonEnabled(idx),
        Width: 70f);

    private bool IsConfirmRow(int idx) => _applyState == ApplyState.Confirm && _applyComboIndex == idx;

    private string ApplyButtonLabel(int idx)
    {
        if (idx >= _combos.Count) return "Apply";
        if (IsAlreadyEquipped(_combos[idx]) && !IsConfirmRow(idx)) return "─────";
        return IsConfirmRow(idx) ? "Confirm" : "Apply";
    }

    private bool ApplyButtonEnabled(int idx)
    {
        if (idx >= _combos.Count) return false;
        if (IsConfirmRow(idx)) return true;
        if (IsAlreadyEquipped(_combos[idx])) return false;
        var applyActive = _applyState is ApplyState.Confirm or ApplyState.Running;
        return !applyActive && _services.ModuleEquip.IsAvailable && _services.Inventory.IsAvailable;
    }

    private void OnApplyClicked(int index, ModuleCombo combo)
    {
        var now = SafeTimeNow();
        switch (_applyState)
        {
            case ApplyState.Idle:
                _applyComboIndex = index;
                _applyState = ApplyState.Confirm;
                _applyStateChangedAt = now;
                SnapshotConfirmPlan(combo);
                break;
            case ApplyState.Confirm when _applyComboIndex == index:
                StartApply(combo);
                break;
        }
    }

    // Cache the install-step lines + slot count for the Confirm footer display.
    private void SnapshotConfirmPlan(ModuleCombo combo)
    {
        var plan = BuildPlan(combo);
        _confirmSlotCount = CountSlots(plan);
        _confirmStepLines.Clear();
        foreach (var step in plan)
        {
            if (step.Kind == StepKind.Install)
                _confirmStepLines.Add($"   Slot {step.Slot}: → {step.ModuleName}");
        }
    }

    private void ReoptimizeFromFailed()
    {
        ResetApplyState();
        var snap = _services.Inventory.GetModules();
        if (snap is not null) RunOptimize(snap);
    }

    // ---- State transitions --------------------------------------------------

    private void AdvanceApplyState(float now)
    {
        switch (_applyState)
        {
            case ApplyState.Confirm when now - _applyStateChangedAt > ConfirmTimeoutS:
                ResetApplyState();
                break;
            case ApplyState.Done when now - _applyStateChangedAt > SuccessFlashS:
                ResetApplyState();
                break;
            case ApplyState.Running when _foreignPrompt
                && now - _foreignPromptAt > ForeignChangeTimeoutS:
                ResolveForeignPrompt(false);
                break;
        }
    }

    private void ResetApplyState()
    {
        // Unblock any flow parked at PromptForeignChange before discarding the
        // TaskCompletionSource — otherwise the awaiting RunApplyFlow continuation
        // never runs and the captured combo/plan stay rooted (latent leak).
        ResolveForeignPrompt(false);
        _applyState = ApplyState.Idle;
        _applyComboIndex = -1;
        _applyStepIndex = 0;
        _applyCts?.Dispose();
        _applyCts = null;
    }

    private void CancelApply()
    {
        // The flow may be parked awaiting the foreign-change prompt rather than a
        // cancellable game call; resolving the prompt with `false` lets it exit
        // via its own `!go → EnterFailed` branch.
        ResolveForeignPrompt(false);
        try { _applyCts?.Cancel(); } catch { /* already disposed */ }
    }

    private void CancelApplyOnWindowClose()
    {
        if (_applyState is not (ApplyState.Confirm or ApplyState.Running)) return;

        // If the flow is parked at the foreign-change prompt, unblock it and let
        // its own `!go → EnterFailed` terminate the flow — calling EnterFailed
        // here too would double-fire (and clobber the flow's terminal message).
        if (_foreignPrompt)
        {
            CancelApply();
            return;
        }

        CancelApply();
        EnterFailed(Mathf.Max(1, _applyStepIndex), EquipResult.Cancelled,
            "Window closed during apply.");
    }

    private void ResolveForeignPrompt(bool cont)
    {
        _foreignPrompt = false;
        var tcs = _foreignContinue;
        _foreignContinue = null;
        tcs?.TrySetResult(cont);
    }

    // ---- The async apply flow -----------------------------------------------

    private void StartApply(ModuleCombo combo)
    {
        _applyPlan = BuildPlan(combo);
        if (_applyPlan.Count == 0)
        {
            // Nothing to change — treat as instant success flash.
            _applyState = ApplyState.Done;
            _applyStateChangedAt = SafeTimeNow();
            return;
        }

        _preApplyEquippedScore = ComputeEquippedScore();
        _applyStepIndex = 0;
        _applyState = ApplyState.Running;
        _applyStateChangedAt = SafeTimeNow();
        _applyCts = new CancellationTokenSource();
        LogApplyStart(_applyComboIndex, _applyPlan.Count);
        _ = RunApplyFlow(_applyCts.Token);
    }

    private async Task RunApplyFlow(CancellationToken ct)
    {
        try
        {
            for (var i = 0; i < _applyPlan.Count; i++)
            {
                if (ct.IsCancellationRequested)
                {
                    EnterFailed(i, EquipResult.Cancelled,
                        $"Cancelled ({i} / {_applyPlan.Count} steps completed).");
                    return;
                }

                _applyStepIndex = i + 1;
                var step = _applyPlan[i];
                if (!await ExecuteStepAsync(step, i, ct).ConfigureAwait(true))
                    return;
            }

            EnterDone();
        }
        catch (Exception ex)
        {
            EnterFailed(Mathf.Max(1, _applyStepIndex), EquipResult.RpcError,
                $"Apply flow error: {ex.Message}");
        }
    }

    // Execute one apply step (uninstall or install), perform the post-step
    // checks, and handle the foreign-change prompt when needed. Returns true to
    // continue to the next step, false when the flow should stop (caller returns).
    private async Task<bool> ExecuteStepAsync(ApplyStep step, int stepIndex, CancellationToken ct)
    {
        var beforeHash = EquippedHash();

        LogApplyStep(_applyStepIndex, step.Kind.ToString(), step.Slot, step.ModuleUuid);
        var result = step.Kind == StepKind.Uninstall
            ? await _services.ModuleEquip.UninstallAsync(step.Slot, ct).ConfigureAwait(true)
            : await _services.ModuleEquip.InstallAsync(step.Slot, step.ModuleUuid, ct).ConfigureAwait(true);
        LogApplyResult(_applyStepIndex, result);

        if (result == EquipResult.Cancelled)
        {
            EnterFailed(_applyStepIndex, result,
                $"Cancelled ({stepIndex} / {_applyPlan.Count} steps completed).");
            return false;
        }
        if (result != EquipResult.Success && result != EquipResult.SlotEmpty)
        {
            EnterFailed(_applyStepIndex, result, ExplainResult(result, step.Slot));
            return false;
        }

        // Foreign-change check: if the equipped set changed in a way our
        // step didn't intend, pause for confirmation (UI design D12).
        if (stepIndex + 1 < _applyPlan.Count && DetectForeignChange(beforeHash, step))
        {
            var go = await PromptForeignChange().ConfigureAwait(true);
            if (!go)
            {
                EnterFailed(_applyStepIndex, EquipResult.Cancelled,
                    $"Foreign inventory change after step {_applyStepIndex} — flow halted.");
                return false;
            }
        }
        return true;
    }

    private Task<bool> PromptForeignChange()
    {
        _foreignPrompt = true;
        _foreignPromptAt = SafeTimeNow();
        _foreignContinue = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        return _foreignContinue.Task;
    }

    private void EnterDone()
    {
        // A successful apply mutates the equipped set; invalidate the Results
        // window's equipped-snapshot cache so the Done footer's delta + any
        // "already equipped" markers reflect the new state.
        _equippedCacheDirty = true;
        _applyState = ApplyState.Done;
        _applyStateChangedAt = SafeTimeNow();
        LogApplyTransition("Done");
    }

    private void EnterFailed(int stepNumber, EquipResult result, string subLine)
    {
        _failedStepNumber = stepNumber;
        _failedResult = result;
        _failedSubLine = subLine;
        _applyState = ApplyState.Failed;
        _applyStateChangedAt = SafeTimeNow();
        LogApplyTransition($"Failed ({result})");
    }

    // ---- Plan + diff helpers ------------------------------------------------

    // Diff the combo vs the currently equipped set. For each combo slot:
    //   equipped uuid == combo uuid  → no-op (silent)
    //   equipped slot empty          → Install
    //   equipped slot occupied       → Uninstall current, then Install new
    private List<ApplyStep> BuildPlan(ModuleCombo combo)
    {
        var plan = new List<ApplyStep>(SlotCount * 2);
        var equipped = _services.Inventory.GetEquipped();
        var bySlot = equipped?.ModuleUuidsBySlot;

        for (var s = 0; s < combo.Modules.Count; s++)
        {
            var slot = s + 1;
            var module = combo.Modules[s];
            var hasCurrent = bySlot is not null && bySlot.TryGetValue(slot, out var curUuid);
            var current = hasCurrent ? bySlot![slot] : 0L;

            if (hasCurrent && current == module.Uuid) continue;   // no-op
            if (hasCurrent)
            {
                plan.Add(new ApplyStep(slot, StepKind.Uninstall, current, "(uninstall current)"));
            }
            // Game modules carry an empty Name — fall back to the parts description so the Confirm/Running
            // footers show a meaningful label ("Slot 1: → (Agility Boost×7, …)") instead of "Slot 1: →".
            var label = string.IsNullOrEmpty(module.Name) ? DescribeModule(module) : module.Name;
            plan.Add(new ApplyStep(slot, StepKind.Install, module.Uuid, label));
        }
        return plan;
    }

    // Slot count (not step count): a replace is 2 steps (uninstall+install) but
    // 1 changed slot. Every changed slot gets exactly one Install step in
    // BuildPlan (no-op slots are skipped), so the Install count == distinct
    // slots changed (UI design — Confirm/Done counts are slot-based).
    private static int CountSlots(List<ApplyStep> plan)
    {
        var n = 0;
        foreach (var step in plan)
        {
            if (step.Kind == StepKind.Install) n++;
        }
        return n;
    }

    private long EquippedHash()
    {
        var equipped = _services.Inventory.GetEquipped();
        if (equipped is null) return 0L;
        long h = 0;
        foreach (var kv in equipped.ModuleUuidsBySlot)
        {
            h ^= unchecked(((long)kv.Key) * 0xDEADBEEFL + kv.Value);
        }
        return h;
    }

    // A foreign change is one where the equipped set after a step changed in a
    // slot OTHER than the one our step targeted.
    private bool DetectForeignChange(long beforeHash, ApplyStep step)
    {
        var equipped = _services.Inventory.GetEquipped();
        if (equipped is null) return false;
        var afterHash = EquippedHash();
        if (afterHash == beforeHash) return false;   // our own step may still be settling

        // Our step legitimately changes `step.Slot`. If any OTHER slot's content
        // also moved we can't easily prove it here without a full snapshot diff;
        // conservatively, treat the simple "still matches plan target for this
        // slot" as fine and only prompt when the targeted slot does NOT hold the
        // expected uuid (i.e. something else interfered).
        if (step.Kind != StepKind.Install) return false;
        return equipped.ModuleUuidsBySlot.TryGetValue(step.Slot, out var uuid)
            && uuid != step.ModuleUuid;
    }

    // Per-EquipResult human-friendly sub-line (UI design §2.4 Failed table).
    private static string ExplainResult(EquipResult result, int slot) => result switch
    {
        EquipResult.SlotLocked => $"Slot {slot} is currently locked in-game.",
        EquipResult.SlotConflict => "Equipping this module exceeds the category max.",
        EquipResult.SlotEmpty => $"Slot {slot} was already empty (no uninstall needed).",
        EquipResult.ModuleNotInInventory => "Target module is no longer in inventory.",
        EquipResult.Timeout => "Game did not respond within 6 s.",
        EquipResult.Cancelled => "Cancelled by user.",
        EquipResult.RpcError => "Game-server error — see log.",
        EquipResult.GameApiUnavailable => "Equip API not available — wait until you're in-world.",
        EquipResult.PlayerNotInWorld => "Character not in-world — try again after a zone load.",
        _ => "Unknown error — see log.",
    };
}
