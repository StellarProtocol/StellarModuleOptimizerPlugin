using System.Collections.Generic;
using System.Globalization;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Domain.Inventory;
using Stellar.Abstractions.Services;

namespace Stellar.ModuleOptimizer;

/// <summary>
/// Targets window — the uGUI element tree (migrated off IMGUI). Category toggles,
/// the target-attribute list (name + per-target min-sum floor + remove ✕, no
/// weights — mirroring StarResonanceAutoMod's unweighted target SET), an inline
/// "+ Add attribute…" picker popup sourced from the inventory's AttrIds, the
/// inventory/equipped count lines, the show-top-N stepper, and the Optimize button
/// (gated by the empty-state rules). Built ONCE; element Funcs re-pull live state.
/// </summary>
public sealed partial class Plugin
{
    private const float AttrColWidth   = 150f;
    private const float MinSumInputW   = 50f;
    private const float PickerHeight   = 220f;

    private ColorRgba? Muted() => _services.Theme.Colors.MenuMuted;

    private HudElement BuildTargetsRoot() => new ColumnElement(new HudElement[]
    {
        // Categories
        new TextElement(() => "Categories:", Emphasis: true),
        new RowElement(new HudElement[]
        {
            CategoryToggle("Attack", ModuleCategory.Attack),
            CategoryToggle("Assist", ModuleCategory.Assistant),
            CategoryToggle("Defend", ModuleCategory.Defend),
        }, Gap: 8f),
        new SeparatorElement(),

        // Target attributes (or empty-state)
        new ConditionalElement(() => !_invAvailable,
            new TextElement(() => "Not in-world — inventory unavailable.", Muted)),
        new ConditionalElement(() => _invAvailable && _invModuleCount == 0,
            new ColumnElement(new HudElement[]
            {
                new TextElement(() => "No modules in inventory.", Muted),
                new TextElement(() => "(Pick some up in-world to get started.)", Muted),
            })),
        new ConditionalElement(() => _invAvailable && _invModuleCount > 0, BuildTargetSection()),

        new SeparatorElement(),
        BuildTargetsFooter(),
    });

    // The toggle capsule carries no text of its own (BuildToggle ignores ToggleElement.Label — settings rows
    // supply a sibling Text), so pair each with its category label, matching the IMGUI "Attack/Assist/Defend".
    private HudElement CategoryToggle(string label, ModuleCategory category)
    {
        var bit = 1 << ((int)category - 1);
        return new RowElement(new HudElement[]
        {
            new ToggleElement(() => "", () => (_categoryMask & bit) != 0, on => SetCategoryMaskBit(category, on)),
            new TextElement(() => label),
        }, Gap: 4f);
    }

    private HudElement BuildTargetSection() => new ColumnElement(new HudElement[]
    {
        new TextElement(() => "Target attributes:", Emphasis: true),
        new ConditionalElement(() => _targetIds.Count == 0,
            new TextElement(() => "  ⓘ Pick at least one target attribute.", Muted)),
        new ConditionalElement(() => _targetIds.Count > 0, new ColumnElement(new HudElement[]
        {
            // Header: the per-row "≥" field is a minimum TOTAL the final combo must reach.
            new RowElement(new HudElement[]
            {
                new CellElement(new TextElement(() => "Attribute", Muted), Weight: 1f),
                new TextElement(() => "min ≥", Muted),
                new SpacerElement(MinSumInputW + 28f),
            }, Gap: 6f),
            BuildTargetList(),
        })),
        BuildAddAttributeBlock(),
    });

    private HudElement BuildTargetList()
    {
        var slots = new HudElement[MaxAttrSlots];
        for (var i = 0; i < MaxAttrSlots; i++)
        {
            var idx = i;
            slots[i] = new RowElement(new HudElement[]
            {
                new CellElement(new TextElement(() => idx < _targetIds.Count ? ResolveAttrNameOrId(_targetIds[idx]) : ""), Weight: 1f),
                new TextElement(() => "≥", Muted),
                new InputElement(
                    () => idx < _targetIds.Count ? MinSumDisplay(_targetIds[idx]) : "",
                    _ => { },
                    MinSumInputW,
                    OnChange: s => { if (idx < _targetIds.Count) SetMinSum(_targetIds[idx], ParseNonNegative(s)); }),
                new ButtonElement(() => "✕", () => { if (idx < _targetIds.Count) RemoveTarget(_targetIds[idx]); }, Width: 26f),
            }, Gap: 6f);
        }
        return new ListElement(() => _targetIds.Count, slots);
    }

    // Current floor as display text ("" when none) — seeds the min-sum field.
    private string MinSumDisplay(int attrId)
        => _minSums.TryGetValue(attrId, out var v) && v > 0
            ? v.ToString(CultureInfo.InvariantCulture)
            : string.Empty;

    // Keep digits only; empty (or all-non-digit) → 0 (clears the floor).
    private static int ParseNonNegative(string s)
    {
        long value = 0;
        var any = false;
        foreach (var ch in s)
        {
            if (ch < '0' || ch > '9') continue;
            any = true;
            value = value * 10 + (ch - '0');
            if (value > int.MaxValue) return int.MaxValue;
        }
        return any ? (int)value : 0;
    }

    private HudElement BuildAddAttributeBlock() => new ColumnElement(new HudElement[]
    {
        new ButtonElement(
            () => _pickerOpen ? "− Add attribute…" : "+ Add attribute…",
            TogglePicker,
            Active: () => _pickerOpen),
        new ConditionalElement(() => _pickerOpen, BuildPickerPanel()),
    });

    private void TogglePicker()
    {
        _pickerOpen = !_pickerOpen;
        if (_pickerOpen) _pickerSearch = string.Empty;
        RebuildPickerSource();
    }

    private HudElement BuildPickerPanel()
    {
        var slots = new HudElement[MaxAttrSlots];
        for (var i = 0; i < MaxAttrSlots; i++)
        {
            var idx = i;
            slots[i] = new ButtonElement(
                () => idx < _pickerVisible.Count ? ResolveAttrNameOrId(_pickerVisible[idx]) : "",
                () => { if (idx < _pickerVisible.Count) AddTarget(_pickerVisible[idx]); });
        }

        return new ColumnElement(new HudElement[]
        {
            new RowElement(new HudElement[]
            {
                new TextElement(() => "Search:", Muted, Width: 52f),
                new InputElement(() => _pickerSearch, _ => { }, 180f,
                    OnChange: s => { _pickerSearch = s; RebuildPickerSource(); }),
            }, Gap: 4f),
            new ConditionalElement(() => !_services.GameData.IsAvailable,
                new TextElement(() => "Loading attributes…", Muted)),
            new ConditionalElement(() => _services.GameData.IsAvailable,
                new ScrollElement(new ListElement(() => _pickerVisible.Count, slots), PickerHeight)),
            new ConditionalElement(() => _services.GameData.IsAvailable && _pickerVisible.Count == 0,
                new TextElement(() => _pickerSearch.Trim().Length > 0
                    ? $"No attributes match '{_pickerSearch.Trim()}'"
                    : "No attributes available.", Muted)),
        });
    }

    private HudElement BuildTargetsFooter() => new ColumnElement(new HudElement[]
    {
        new TextElement(() => _invAvailable ? $"Inventory: {_invModuleCount} modules" : "Inventory: —", Emphasis: true),
        new TextElement(() => _invAvailable ? $"Equipped:  {_invEquippedSlotCount} / {SlotCount} slots" : "Equipped:  —", Emphasis: true),

        // Show-top-N stepper.
        new RowElement(new HudElement[]
        {
            new TextElement(() => "Show top", Muted),
            new ButtonElement(() => "−", () => SetTopN(_topN - 1), Width: 26f),
            new TextElement(() => _topN.ToString(CultureInfo.InvariantCulture), Width: 30f),
            new ButtonElement(() => "+", () => SetTopN(_topN + 1), Width: 26f),
            new TextElement(() => "combos", Muted),
            new SpacerElement(),
        }, Gap: 4f),

        // Optimize + gating hint.
        new RowElement(new HudElement[]
        {
            new ButtonElement(() => "Optimize ▶", RunOptimizeFromTargets,
                Enabled: () => CanOptimize, Style: MenuButtonStyle.Filled),
            new ConditionalElement(() => !CanOptimize, new TextElement(OptimizeHint, Muted)),
        }, Gap: 6f),
    });

    private string OptimizeHint()
    {
        if (!_invAvailable) return "(not in-world)";
        if (_targetIds.Count == 0) return "(pick an attribute first)";
        return "(need 4+ modules in mask)";
    }

    private void RunOptimizeFromTargets()
    {
        if (!CanOptimize) return;
        var snap = _services.Inventory.GetModules();
        if (snap is not null) RunOptimize(snap);
    }

    private int CountInMask(ModuleSnapshot snap)
    {
        var n = 0;
        foreach (var m in snap.Modules)
        {
            if ((_categoryMask & (1 << ((int)m.Category - 1))) != 0) n++;
        }
        return n;
    }
}
