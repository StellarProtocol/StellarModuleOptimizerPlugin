using System;
using System.Globalization;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.ModuleOptimizer;

/// <summary>
/// Results-window footer — the uGUI element tree for the Apply state machine
/// (Idle / Confirm / Running / Done / Failed, plus the foreign-change substate of
/// Running). Each state is a <see cref="ConditionalElement"/> gated on the live
/// <c>_applyState</c>; the state-transition logic + async apply flow stay in
/// <see cref="Plugin"/>'s Apply partial. Built ONCE; Funcs re-pull live state.
/// </summary>
public sealed partial class Plugin
{
    private HudElement BuildFooter() => new ColumnElement(new HudElement[]
    {
        BuildIdleFooter(),
        BuildConfirmFooter(),
        BuildRunningFooter(),
        BuildForeignPromptFooter(),
        BuildDoneFooter(),
        BuildFailedFooter(),
    });

    private HudElement BuildIdleFooter() => new ConditionalElement(
        () => _applyState == ApplyState.Idle && _haveOptimized && _combos.Count > 0,
        new ColumnElement(new HudElement[]
        {
            new ConditionalElement(() => _invAvailable && _targetIds.Count > 0, new ColumnElement(new HudElement[]
            {
                new TextElement(() => $"Currently equipped:  Score {ComputeEquippedScore().ToString(CultureInfo.InvariantCulture)}"),
                new TextElement(BestDeltaLine, BestDeltaColor),
            })),
            new ConditionalElement(() => !(_invAvailable && _targetIds.Count > 0),
                new TextElement(() => "Currently equipped:  —", Muted)),
        }));

    private string BestDeltaLine()
    {
        var equipped = ComputeEquippedScore();
        var best = _combos.Count > 0 ? _combos[0].Score : equipped;
        var delta = best - equipped;
        return $"Best delta: {(delta >= 0 ? "+" : "")}{delta.ToString(CultureInfo.InvariantCulture)} (#1 above)";
    }

    private ColorRgba? BestDeltaColor()
    {
        var best = _combos.Count > 0 ? _combos[0].Score : ComputeEquippedScore();
        return best - ComputeEquippedScore() >= 0 ? DeltaPosColor : DeltaNegColor;
    }

    private HudElement BuildConfirmFooter()
    {
        var lines = new HudElement[MaxPlanLines];
        for (var i = 0; i < MaxPlanLines; i++)
        {
            var idx = i;
            lines[i] = new TextElement(() => idx < _confirmStepLines.Count ? _confirmStepLines[idx] : "");
        }
        return new ConditionalElement(() => _applyState == ApplyState.Confirm, new ColumnElement(new HudElement[]
        {
            new TextElement(() => $"Apply combo #{_applyComboIndex + 1} — will change {_confirmSlotCount} slots:"),
            new ListElement(() => _confirmStepLines.Count, lines),
            new RowElement(new HudElement[]
            {
                new TextElement(() => $"(auto-cancels in {Countdown(ConfirmTimeoutS, _applyStateChangedAt):0.0} s)", Muted),
                new SpacerElement(),
                new ButtonElement(() => "Cancel", ResetApplyState, Width: 70f),
            }, Gap: 6f),
        }));
    }

    private HudElement BuildRunningFooter() => new ConditionalElement(
        () => _applyState == ApplyState.Running && !_foreignPrompt,
        new ColumnElement(new HudElement[]
        {
            new TextElement(() => $"Applying… ({RunningStep()} / {_applyPlan.Count})"),
            new TextElement(RunningStepLine),
            new BarElement(RunningFraction, ProgressFill),
            new RowElement(new HudElement[]
            {
                new SpacerElement(),
                new ButtonElement(() => "Cancel", CancelApply, Width: 70f),
            }),
        }));

    private int RunningStep()
        => Math.Clamp(_applyStepIndex, 1, Math.Max(1, _applyPlan.Count));

    private string RunningStepLine()
    {
        if (_applyStepIndex < 1 || _applyStepIndex > _applyPlan.Count) return "";
        var step = _applyPlan[_applyStepIndex - 1];
        var sub = step.Kind == StepKind.Install ? step.ModuleName : "(uninstall current)";
        return $"Slot {step.Slot}: {sub}";
    }

    private float RunningFraction()
    {
        var total = _applyPlan.Count;
        return total == 0 ? 0f : (float)RunningStep() / total;
    }

    private HudElement BuildForeignPromptFooter() => new ConditionalElement(
        () => _applyState == ApplyState.Running && _foreignPrompt,
        new ColumnElement(new HudElement[]
        {
            new TextElement(() => "⚠ Inventory changed mid-apply.", () => WarnColor),
            new TextElement(() => $"(Foreign change detected after step {_applyStepIndex - 1}.)", Muted),
            new RowElement(new HudElement[]
            {
                new ButtonElement(() => "Continue", () => ResolveForeignPrompt(true), Width: 80f),
                new ButtonElement(() => "Cancel", () => ResolveForeignPrompt(false), Width: 70f),
                new SpacerElement(),
                new TextElement(() => $"(auto-cancels in {Countdown(ForeignChangeTimeoutS, _foreignPromptAt):0.0} s)", Muted),
            }, Gap: 6f),
        }));

    private HudElement BuildDoneFooter() => new ConditionalElement(
        () => _applyState == ApplyState.Done,
        new ColumnElement(new HudElement[]
        {
            new TextElement(() => $"✓ Applied {CountSlots(_applyPlan)} changes.", () => SuccessColor),
            new TextElement(DoneScoreLine),
        }));

    private string DoneScoreLine()
    {
        var score = ComputeEquippedScore();
        var delta = score - _preApplyEquippedScore;
        return $"Currently equipped:  Score {score.ToString(CultureInfo.InvariantCulture)}"
            + $"  ({(delta >= 0 ? "+" : "")}{delta.ToString(CultureInfo.InvariantCulture)})";
    }

    private HudElement BuildFailedFooter() => new ConditionalElement(
        () => _applyState == ApplyState.Failed,
        new ColumnElement(new HudElement[]
        {
            new TextElement(() => $"⚠ Step {_failedStepNumber} failed:  {_failedResult}", () => ErrorColor),
            new TextElement(() => _failedSubLine, Muted),
            new RowElement(new HudElement[]
            {
                new ButtonElement(() => "Dismiss", ResetApplyState, Width: 90f),
                new ButtonElement(() => "Re-optimize", ReoptimizeFromFailed, Width: 110f),
            }, Gap: 6f),
        }));

    private float Countdown(float window, float since) => Math.Max(0f, window - (SafeTimeNow() - since));
}
