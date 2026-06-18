using System;
using System.Collections.Generic;
using System.Globalization;

namespace Stellar.ModuleOptimizer;

public sealed partial class Plugin
{
    private void LoadConfig()
    {
        // Targets — fresh installs have an empty list (UI design §3: no starter list).
        // An older config may still carry a "weights" key; it is silently ignored.
        LoadTargetIds();
        LoadMinSums();
        _categoryMask = _targetsSection.Get<int>("category_mask", CategoryMaskDefault);
        _topN = ClampTopN(_targetsSection.Get<int>("top_n", TopNDefault));
    }

    private void LoadTargetIds()
    {
        var ids = _targetsSection.Get<int[]>("attr_ids", null) ?? Array.Empty<int>();
        _targetIds.Clear();
        foreach (var id in ids)
        {
            if (!_targetIds.Contains(id)) _targetIds.Add(id);
        }
    }

    // Min-sum floors persist as an object keyed by decimal-string attrId.
    // Tolerate absence (→ empty) and any malformed/negative entry (→ skipped).
    private void LoadMinSums()
    {
        _minSums.Clear();
        var stored = _targetsSection.Get<Dictionary<string, int>>("min_sums", null);
        if (stored is null) return;
        foreach (var kv in stored)
        {
            if (kv.Value <= 0) continue;
            if (int.TryParse(kv.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var attrId))
            {
                _minSums[attrId] = kv.Value;
            }
        }
    }

    private void PersistTargets()
    {
        _targetsSection.Set("attr_ids", _targetIds.ToArray());
        _targetsSection.Set("category_mask", _categoryMask);
        _targetsSection.Set("min_sums", BuildMinSumsForPersist());
        _targetsSection.Set("top_n", _topN);
        _targetsSection.SaveQuiet();
    }

    // Project _minSums into a decimal-string-keyed dictionary for JSON (System.Text.Json
    // requires string keys). Only positive floors are written; 0 = no constraint.
    private Dictionary<string, int> BuildMinSumsForPersist()
    {
        var map = new Dictionary<string, int>(_minSums.Count);
        foreach (var kv in _minSums)
        {
            if (kv.Value > 0) map[kv.Key.ToString(CultureInfo.InvariantCulture)] = kv.Value;
        }
        return map;
    }

    private void OnConfigChanged(string sectionName)
    {
        if (sectionName != "targets") return;

        // External edit — rebuild target state from disk (UI design §3
        // reconciliation). An in-flight Apply is NOT cancelled; it runs against
        // its captured combo. A legacy "weights" key, if present, is ignored.
        LoadTargetIds();
        LoadMinSums();
        _categoryMask = _targetsSection.Get<int>("category_mask", _categoryMask);
        _topN = ClampTopN(_targetsSection.Get<int>("top_n", _topN));

        RefreshInventorySnapshot();
        RebuildPickerSource();

        LogReconciliation(_targetIds.Count);
    }
}
