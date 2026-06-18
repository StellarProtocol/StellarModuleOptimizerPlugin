using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Domain.Inventory;
using Stellar.Abstractions.Services;

namespace Stellar.ModuleOptimizer;

/// <summary>
/// Results window — the uGUI element tree (migrated off IMGUI). A scrollable list
/// of ranked combo rows (header + slot lines + already-equipped marker + per-row
/// Preview/Apply buttons), a SHARED preview panel below the list showing the
/// projected-vs-current attribute deltas of the previewed combo (aligned
/// <see cref="CellElement"/> table), and the Apply state-machine footer. Built
/// ONCE; element Funcs re-pull live state on the framework's capped refresh.
///
/// Layout note: the IMGUI build drew the projected-delta table INLINE inside each
/// combo box. With up to <see cref="TopNMax"/> rows that would build N copies of a
/// ~32-row table once; instead a single shared panel renders the previewed combo's
/// table (snapshotted in <see cref="RebuildPreview"/>) — same content, far fewer
/// GameObjects.
/// </summary>
public sealed partial class Plugin
{
    private const float ComboListHeight = 320f;
    private const float PreviewAttrColW = 150f;
    private const float PreviewValueColW = 96f;
    private const float PreviewDeltaColW = 56f;

    // Whether the last Optimize ran with at least one active min-sum floor.
    private bool _hadMinSumFloors;

    // ---- Equipped-snapshot cache (no per-poll inventory scan) ---------------
    private bool _equippedCacheDirty = true;
    private readonly Dictionary<long, ModuleInfo> _moduleByUuid = new();
    private readonly HashSet<long> _equippedUuids = new();
    private int _cachedEquippedSlotCount;
    private int _cachedEquippedScore;

    // ---- Preview snapshot (built when a combo's Preview is toggled) ----------
    private readonly List<(int Id, int Cur, int Proj)> _previewAttrs = new();

    // ---- Combo display-string snapshot (combos are immutable between optimizes) ----
    // Built once per Optimize so the row Funcs index cached strings instead of re-interpolating /
    // re-building the slot-line StringBuilder every refresh poll.
    private readonly List<string> _comboHeaders = new();
    private readonly List<string[]> _comboSlotLines = new();

    private void RebuildEquippedCacheIfNeeded()
    {
        if (!_equippedCacheDirty) return;
        _equippedCacheDirty = false;

        _moduleByUuid.Clear();
        _equippedUuids.Clear();
        _cachedEquippedSlotCount = 0;
        _cachedEquippedScore = 0;

        var snap = _services.Inventory.GetModules();
        if (snap is not null)
        {
            foreach (var m in snap.Modules) _moduleByUuid[m.Uuid] = m;
        }

        var equipped = _services.Inventory.GetEquipped();
        if (equipped is not null)
        {
            _cachedEquippedSlotCount = equipped.ModuleUuidsBySlot.Count;
            var equippedModules = new List<ModuleInfo>(equipped.ModuleUuidsBySlot.Count);
            foreach (var uuid in equipped.ModuleUuidsBySlot.Values)
            {
                _equippedUuids.Add(uuid);
                if (_moduleByUuid.TryGetValue(uuid, out var mod)) equippedModules.Add(mod);
            }
            _cachedEquippedScore = CombatPower.ScoreCombo(equippedModules);
        }
    }

    private void RunOptimize(ModuleSnapshot snap)
    {
        var start = SafeTimeNow();
        _combos = ModuleOptimizerEngine.Optimize(snap, _targetIds, _categoryMask, _topN, _minSums);
        _lastCandidateCount = CountInMask(snap);
        _hadMinSumFloors = HasActiveMinSums();
        _haveOptimized = true;
        _previewRow = -1;
        _previewAttrs.Clear();
        SnapshotComboStrings();
        ResetApplyState();

        ShowAndPersist(_resultsWindow, "results_visible");

        LogOptimize(snap.Modules.Count, _combos.Count, SafeTimeNow() - start);
    }

    // ---- Element tree -------------------------------------------------------

    private HudElement BuildResultsRoot() => new ColumnElement(new HudElement[]
    {
        new ConditionalElement(() => _haveOptimized && _combos.Count > 0,
            new TextElement(() => $"Top {_combos.Count} combinations  •  {_lastCandidateCount} candidates", Muted)),
        new SeparatorElement(),

        new ConditionalElement(() => !_haveOptimized,
            new TextElement(() => "Click Optimize ▶ in the Targets window.", Muted)),
        new ConditionalElement(() => _haveOptimized && _combos.Count == 0, BuildEmptyState()),
        new ConditionalElement(() => _haveOptimized && _combos.Count > 0,
            new ScrollElement(new ListElement(() => _combos.Count, BuildComboSlots()), ComboListHeight)),

        new ConditionalElement(() => _previewRow >= 0, BuildPreviewPanel()),

        new SeparatorElement(),
        BuildFooter(),
    });

    private HudElement[] BuildComboSlots()
    {
        var slots = new HudElement[MaxComboSlots];
        for (var i = 0; i < MaxComboSlots; i++)
        {
            var idx = i;
            var slotLines = new HudElement[SlotCount];
            for (var s = 0; s < SlotCount; s++)
            {
                var si = s;
                slotLines[s] = new TextElement(() => SlotLine(idx, si));
            }
            slots[i] = new ColumnElement(new HudElement[]
            {
                new RowElement(new HudElement[]
                {
                    new TextElement(() => ComboHeader(idx), Emphasis: true),
                    new SpacerElement(),
                    new ButtonElement(() => _previewRow == idx ? "Preview ▾" : "Preview",
                        () => TogglePreview(idx), Active: () => _previewRow == idx, Width: 84f),
                    BuildApplyButton(idx),
                }, Gap: 6f),
                new ListElement(() => idx < _combos.Count ? _combos[idx].Modules.Count : 0, slotLines),
                new ConditionalElement(() => IsAlreadyEquippedRow(idx),
                    new TextElement(() => "  ✓ Already equipped", () => SuccessColor)),
                new SeparatorElement(),
            }, Gap: 2f);
        }
        return slots;
    }

    // Snapshot the immutable per-combo display strings (header + slot lines) once per Optimize.
    private void SnapshotComboStrings()
    {
        _comboHeaders.Clear();
        _comboSlotLines.Clear();
        foreach (var combo in _combos)
        {
            _comboHeaders.Add($"#{_comboHeaders.Count + 1}  Score: {combo.Score.ToString(CultureInfo.InvariantCulture)}");
            var lines = new string[combo.Modules.Count];
            for (var s = 0; s < combo.Modules.Count; s++)
                lines[s] = $"  Slot {s + 1}: {DescribeModule(combo.Modules[s])}";
            _comboSlotLines.Add(lines);
        }
    }

    private string ComboHeader(int idx) => idx < _comboHeaders.Count ? _comboHeaders[idx] : "";

    private string SlotLine(int idx, int slot)
        => idx < _comboSlotLines.Count && slot < _comboSlotLines[idx].Length ? _comboSlotLines[idx][slot] : "";

    private bool IsAlreadyEquippedRow(int idx)
        => idx < _combos.Count && IsAlreadyEquipped(_combos[idx]);

    // "Atk Mod A (Int×4, Crit×3, DMG×2)" — module name + ALL of its parts.
    private string DescribeModule(ModuleInfo module)
    {
        var sb = new StringBuilder(module.Name);
        var shown = 0;
        foreach (var part in module.Parts)
        {
            sb.Append(shown == 0 ? " (" : ", ");
            sb.Append(ResolveAttrShort(part.AttrId)).Append('×').Append(part.Value);
            shown++;
        }
        if (shown > 0) sb.Append(')');
        return sb.ToString();
    }

    private HudElement BuildEmptyState() => new ColumnElement(new HudElement[]
    {
        new ConditionalElement(EmptyIsFloorProblem, new ColumnElement(new HudElement[]
        {
            new TextElement(() => "No combination meets the minimums —", Muted),
            new TextElement(() => "lower them or widen the category mask.", Muted),
        })),
        new ConditionalElement(() => !EmptyIsFloorProblem(), new ColumnElement(new HudElement[]
        {
            new TextElement(() => "Need at least 4 modules in selected categories.", Muted),
            new TextElement(() => $"({_lastCandidateCount} matching modules in inventory)", Muted),
        })),
        new ButtonElement(() => "Adjust targets", () => ShowAndPersist(_mainWindow, "targets_visible")),
    });

    private bool EmptyIsFloorProblem() => _hadMinSumFloors && _lastCandidateCount >= SlotCount;

    // ---- Shared preview panel ----------------------------------------------

    private void TogglePreview(int idx)
    {
        if (_previewRow == idx) { _previewRow = -1; _previewAttrs.Clear(); return; }
        _previewRow = idx;
        RebuildPreview();
    }

    // Snapshot the previewed combo's projected-vs-current attribute deltas so the
    // preview table Funcs read a cached list (no per-poll inventory aggregation).
    private void RebuildPreview()
    {
        _previewAttrs.Clear();
        if (_previewRow < 0 || _previewRow >= _combos.Count) return;
        var combo = _combos[_previewRow];
        var current = SumAllParts(EquippedModules(_services.Inventory.GetEquipped()));
        var projected = SumAllParts(combo.Modules);
        var ids = new SortedSet<int>(current.Keys);
        foreach (var id in projected.Keys) ids.Add(id);
        foreach (var id in ids)
        {
            var cur = current.TryGetValue(id, out var c) ? c : 0;
            var proj = projected.TryGetValue(id, out var p) ? p : 0;
            _previewAttrs.Add((id, cur, proj));
            if (_previewAttrs.Count >= MaxPreviewAttrs) break;
        }
    }

    private HudElement BuildPreviewPanel()
    {
        var slots = new HudElement[MaxPreviewAttrs];
        for (var k = 0; k < MaxPreviewAttrs; k++)
        {
            var ki = k;
            slots[k] = new RowElement(new HudElement[]
            {
                new CellElement(new TextElement(() => PreviewName(ki)), Width: PreviewAttrColW),
                new CellElement(new TextElement(() => PreviewCurProj(ki)), Width: PreviewValueColW),
                new CellElement(new TextElement(() => PreviewDelta(ki), () => PreviewDeltaColor(ki)), Width: PreviewDeltaColW),
            }, Gap: 6f);
        }
        return new ColumnElement(new HudElement[]
        {
            new SeparatorElement(),
            new RowElement(new HudElement[]
            {
                new CellElement(new TextElement(() => "Attribute", Muted), Width: PreviewAttrColW),
                new CellElement(new TextElement(() => "Current → Proj", Muted), Width: PreviewValueColW),
                new CellElement(new TextElement(() => "Δ", Muted), Width: PreviewDeltaColW),
            }, Gap: 6f),
            new ListElement(() => _previewAttrs.Count, slots),
        });
    }

    private string PreviewName(int k) => k < _previewAttrs.Count ? "  " + ResolveAttrShort(_previewAttrs[k].Id) : "";
    private string PreviewCurProj(int k) => k < _previewAttrs.Count ? $"{_previewAttrs[k].Cur} → {_previewAttrs[k].Proj}" : "";

    private string PreviewDelta(int k)
    {
        if (k >= _previewAttrs.Count) return "";
        var d = _previewAttrs[k].Proj - _previewAttrs[k].Cur;
        return (d >= 0 ? "+" : "") + d.ToString(CultureInfo.InvariantCulture);
    }

    private ColorRgba? PreviewDeltaColor(int k)
    {
        if (k >= _previewAttrs.Count) return null;
        return _previewAttrs[k].Proj - _previewAttrs[k].Cur >= 0 ? DeltaPosColor : DeltaNegColor;
    }

    // ---- Equipped helpers (shared with Apply) -------------------------------

    private List<ModuleInfo> EquippedModules(EquippedSet? equipped)
    {
        var result = new List<ModuleInfo>();
        if (equipped is null) return result;
        RebuildEquippedCacheIfNeeded();
        foreach (var uuid in equipped.ModuleUuidsBySlot.Values)
        {
            if (_moduleByUuid.TryGetValue(uuid, out var mod)) result.Add(mod);
        }
        return result;
    }

    private static Dictionary<int, int> SumAllParts(IEnumerable<ModuleInfo> modules)
    {
        var totals = new Dictionary<int, int>();
        foreach (var module in modules)
        {
            foreach (var part in module.Parts)
            {
                totals[part.AttrId] = totals.TryGetValue(part.AttrId, out var v) ? v + part.Value : part.Value;
            }
        }
        return totals;
    }

    private int ComputeEquippedScore()
    {
        RebuildEquippedCacheIfNeeded();
        return _cachedEquippedScore;
    }

    private bool IsAlreadyEquipped(ModuleCombo combo)
    {
        RebuildEquippedCacheIfNeeded();
        if (_cachedEquippedSlotCount == 0) return false;
        if (_cachedEquippedSlotCount != combo.Modules.Count) return false;
        foreach (var m in combo.Modules)
        {
            if (!_equippedUuids.Contains(m.Uuid)) return false;
        }
        return true;
    }
}
