using System.Globalization;
using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Domain.Inventory;

namespace Stellar.ModuleOptimizer;

/// <summary>
/// Diagnostic-mode logging for <see cref="Plugin"/>. All entry points
/// short-circuit on <see cref="StellarDiagnostics.IsEnabled"/> so production
/// partials call them unconditionally — keeps the production code clean of
/// inline gates (coding-standards § Diagnostics; same pattern as
/// <c>StatInspector.Plugin.Diagnostics.cs</c>).
/// </summary>
public sealed partial class Plugin
{
    private void LogOptimize(int moduleCount, int comboCount, float elapsedS)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _services.Log.Info(
            $"[ModuleOptimizer] optimize: {moduleCount} modules → {comboCount} combos in "
            + $"{(elapsedS * 1000f).ToString("F1", CultureInfo.InvariantCulture)}ms");
    }

    private void LogApplyStart(int comboIndex, int stepCount)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _services.Log.Info(
            $"[ModuleOptimizer] apply start: combo #{comboIndex + 1}, {stepCount} steps");
    }

    private void LogApplyStep(int stepNumber, string kind, int slot, long uuid)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _services.Log.Info(
            $"[ModuleOptimizer] apply step {stepNumber}: {kind} slot={slot} uuid={uuid} dispatched");
    }

    private void LogApplyResult(int stepNumber, EquipResult result)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _services.Log.Info($"[ModuleOptimizer] apply step {stepNumber}: result={result}");
    }

    private void LogApplyTransition(string state)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _services.Log.Info($"[ModuleOptimizer] apply state → {state}");
    }

    private void LogOrphanAttr(int attrId)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        if (!_loggedOrphans.Add(attrId)) return;
        _services.Log.Info($"[ModuleOptimizer] orphan attr in attr_ids: #{attrId}");
    }

    private void LogReconciliation(int targetCount)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _services.Log.Info($"[ModuleOptimizer] reconciliation: {targetCount} targets");
    }
}
