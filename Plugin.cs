using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Domain.Inventory;
using Stellar.Abstractions.Plugins;
using Stellar.Abstractions.Services;
using UnityEngine;

namespace Stellar.ModuleOptimizer;

/// <summary>
/// Two-window module equipment optimizer. The Targets window lets the user pick
/// attribute goals and a category mask; Optimize then enumerates every 4-combination
/// of the filtered <see cref="IInventory"/> pool, scores each with the
/// <see cref="CombatPower"/> model, and shows the top-N ranked combos in a Results
/// window. Apply drives a confirm → run → done/failed state machine that equips the
/// chosen combo through the game's own RPC dispatcher (<see cref="IModuleEquip"/>).
/// Demonstrates interactive multi-window uGUI layouts, inventory change events, and
/// a non-trivial game-API action flow with user confirmation.
///
/// Hotkey: <b>F5</b> (toggles the Targets window).
///
/// Persistence keys (single config file
/// <c>&lt;plugin-dir&gt;/stellar.moduleoptimizer.config.json</c>):
///   targets.attr_ids       int[]                 selected target attribute IDs (ordered)
///   targets.category_mask  int                   Attack=1 | Assist=2 | Defend=4 (default 7)
///   targets.min_sums       {string:int}          per-attrId min TOTAL the final combo must reach
///   targets.top_n          int                   number of result combos to show (default 10, clamped 1..50)
///   window.targets_visible bool
///   window.results_visible bool
/// </summary>
public sealed partial class Plugin : IStellarPlugin
{
    public string Name => "ModuleOptimizer";

    // ---- Window constants ---------------------------------------------------
    private const float TargetsWidth = 340f;
    private const float ResultsWidth = 560f;

    // Element-tree slot caps (built once; the visible count drives SetActive). The
    // module-attribute namespace has ~21 entries, so 32 covers every target row /
    // picker entry with headroom. Combo slots cover the top-N max.
    private const int MaxAttrSlots = 32;
    private const int MaxComboSlots = TopNMax;
    private const int MaxPreviewAttrs = 32;
    private const int MaxPlanLines = SlotCount;

    // Apply state timings.
    private const float ConfirmTimeoutS       = 3.0f;
    private const float SuccessFlashS         = 2.0f;
    private const float ForeignChangeTimeoutS = 5.0f;

    private const int CategoryMaskDefault = 7;   // Attack | Assist | Defend

    // Equip slots — alias of the optimizer engine's constant so the UI footer /
    // empty-state read a single source of truth.
    private const int SlotCount = ModuleOptimizerEngine.SlotCount;

    // Result-count bounds. AutoMod returns a configurable top-N; the Results
    // window is scrollable, so we clamp to a sane range. Default 10.
    private const int TopNDefault = 10;
    private const int TopNMin = 1;
    private const int TopNMax = 50;

    // ---- Semantic colours (carried over from the IMGUI build) ---------------
    private static readonly ColorRgba SuccessColor  = new(0.30f, 0.85f, 0.40f, 1f);
    private static readonly ColorRgba WarnColor     = new(0.95f, 0.78f, 0.30f, 1f);
    private static readonly ColorRgba ErrorColor    = new(0.95f, 0.40f, 0.40f, 1f);
    private static readonly ColorRgba DeltaPosColor = new(0.30f, 0.85f, 0.40f, 1f);
    private static readonly ColorRgba DeltaNegColor = new(0.95f, 0.40f, 0.40f, 1f);
    private static readonly ColorRgba ProgressFill  = new(0.40f, 0.70f, 0.95f, 1f);

    // ---- Services + config --------------------------------------------------
    private readonly IPluginServices _services;
    private readonly IConfigSection _targetsSection;
    private readonly IConfigSection _windowSection;

    // ---- Framework-managed windows + hotkey ---------------------------------
    private IWindowControl _mainWindow = null!;
    private IWindowControl _resultsWindow = null!;
    private IHotkeyAction _toggleAction = null!;
    private IDisposable _launcherEntry = null!;

    // ---- Target state (mirrors targets section) -----------------------------
    private readonly List<int> _targetIds = new();
    private int _categoryMask = CategoryMaskDefault;

    // Optional hard min-attr-sum floors (AutoMod's -mas), keyed by AttrId.
    private readonly Dictionary<int, int> _minSums = new();

    // Result-combo count the user wants (clamped to [TopNMin, TopNMax]).
    private int _topN = TopNDefault;

    // ---- Picker popup state -------------------------------------------------
    private bool _pickerOpen;
    private string _pickerSearch = string.Empty;

    // Picker visible list: inventory AttrIds minus current targets, name-sorted,
    // filtered by the search box. Rebuilt on inventory change / target edit /
    // search keystroke so the picker List Funcs index a cached list rather than
    // scanning + filtering the inventory each poll.
    private readonly List<int> _pickerVisible = new();

    // ---- Optimizer results --------------------------------------------------
    private List<ModuleCombo> _combos = new();
    private int _lastCandidateCount;
    private int _previewRow = -1;           // -1 = no preview open
    private int _preApplyEquippedScore;
    private bool _haveOptimized;

    // ---- Inventory-derived display snapshot (no per-poll inventory scan) -----
    private bool _invAvailable;
    private int _invModuleCount;
    private int _invEquippedSlotCount;
    private int _maskCount;

    // ---- One-shot orphan-attr log set ---------------------------------------
    private readonly HashSet<int> _loggedOrphans = new();

    public Plugin(IPluginServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));

        _targetsSection = _services.Config.GetSection("targets");
        _windowSection = _services.Config.GetSection("window");

        _services.Log.Info("[ModuleOptimizer] plugin constructed");

        LoadConfig();
        RefreshInventorySnapshot();
        RebuildPickerSource();

        BuildWindows();

        _services.Inventory.InventoryChanged += OnInventoryChanged;
        _services.Framework.Update += OnUpdate;
        _services.Config.SectionChanged += OnConfigChanged;
    }

    private void BuildWindows()
    {
        var targetsVisibleAtBoot = _windowSection.Get<bool>("targets_visible", true);
        var resultsVisibleAtBoot = _windowSection.Get<bool>("results_visible", false);

        _mainWindow = _services.Windows.Register(new WindowRegistration(
            new WindowSpec(
                Id:          "moduleoptimizer.main",
                Title:       "Module Optimizer",
                DefaultRect: new WindowRect(1218f, 514f, TargetsWidth, 0f),
                Category:    WindowCategory.Tools,
                Style:       WindowPanelStyle.GlassMenu)
            { StartVisible = targetsVisibleAtBoot, HideUntilInWorld = true, Closable = true, Draggable = true },
            BuildTargetsRoot(),
            OnClose: () => HideAndPersist(_mainWindow!, "targets_visible")));

        _resultsWindow = _services.Windows.Register(new WindowRegistration(
            new WindowSpec(
                Id:          "moduleoptimizer.results",
                Title:       "Module Optimizer — Results",
                DefaultRect: new WindowRect(655f, 618f, ResultsWidth, 0f),
                Category:    WindowCategory.Tools,
                Style:       WindowPanelStyle.GlassMenu)
            { StartVisible = resultsVisibleAtBoot, HideUntilInWorld = true, Closable = true, Draggable = true },
            BuildResultsRoot(),
            OnClose: () =>
            {
                HideAndPersist(_resultsWindow!, "results_visible");
                CancelApplyOnWindowClose();
            }));

        _toggleAction = _services.Hotkeys.DeclareAction(
            new HotkeyAction(
                Id:              "moduleoptimizer.toggle",
                Description:     "Toggle ModuleOptimizer",
                SuggestedDefault: new KeyBinding(StellarKeyCode.F5)),
            callback: ToggleAndPersistTargets);

        _launcherEntry = _services.Launcher.Register(new LauncherEntry(
            "Module Optimizer", LoadIconPng(), IconKey: null, OnOpen: ToggleAndPersistTargets));
    }

    private static byte[]? LoadIconPng()
    {
        try
        {
            using var s = typeof(Plugin).Assembly
                .GetManifestResourceStream("Stellar.ModuleOptimizer.modopt-icon.png");
            if (s == null) return null;
            using var ms = new System.IO.MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
        catch { return null; }
    }

    public void Dispose()
    {
        _services.Config.SectionChanged -= OnConfigChanged;
        _services.Framework.Update -= OnUpdate;
        _services.Inventory.InventoryChanged -= OnInventoryChanged;

        _launcherEntry.Dispose();
        _toggleAction.Dispose();
        _resultsWindow.Remove();
        _mainWindow.Remove();
    }

    private void ToggleAndPersistTargets()
    {
        _mainWindow.SetVisible(!_mainWindow.IsShown);
        _windowSection.Set("targets_visible", _mainWindow.IsShown);
        _windowSection.Save();
        _services.Log.Info($"[ModuleOptimizer] Targets window {(_mainWindow.IsShown ? "shown" : "hidden")}");
    }

    private void HideAndPersist(IWindowControl window, string key)
    {
        window.SetVisible(false);
        _windowSection.Set(key, false);
        _windowSection.Save();
    }

    private void ShowAndPersist(IWindowControl window, string key)
    {
        window.SetVisible(true);
        _windowSection.Set(key, true);
        _windowSection.Save();
    }

    private void OnUpdate(float deltaTime)
    {
        var now = SafeTimeNow();
        AdvanceApplyState(now);

        // Lazy availability flip: IInventory has no "became available" event, so
        // cheaply watch the bool and re-snapshot when it changes (no per-frame scan).
        if (_invAvailable != _services.Inventory.IsAvailable)
        {
            RefreshInventorySnapshot();
            RebuildPickerSource();
        }
    }

    private void OnInventoryChanged()
    {
        _equippedCacheDirty = true;
        RefreshInventorySnapshot();
        RebuildPickerSource();
        if (_previewRow >= 0) RebuildPreview();
    }

    // Cache the inventory-derived display counts so the Targets footer Funcs read
    // cached ints instead of scanning the snapshot each poll.
    private void RefreshInventorySnapshot()
    {
        _invAvailable = _services.Inventory.IsAvailable;
        var snap = _services.Inventory.GetModules();
        _invModuleCount = snap?.Modules.Count ?? 0;
        _invEquippedSlotCount = _services.Inventory.GetEquipped()?.ModuleUuidsBySlot.Count ?? 0;
        _maskCount = (_invAvailable && snap is not null) ? CountInMask(snap) : 0;
    }

    private bool CanOptimize => _invAvailable && _targetIds.Count > 0 && _maskCount >= SlotCount;

    private static int ClampTopN(int value) => Mathf.Clamp(value, TopNMin, TopNMax);

    private void SetTopN(int value)
    {
        var next = ClampTopN(value);
        if (next == _topN) return;
        _topN = next;
        PersistTargets();
    }

    // Commit a min-sum floor for an attr. 0 (or negative) removes the floor.
    private void SetMinSum(int attrId, int value)
    {
        if (value <= 0) _minSums.Remove(attrId);
        else _minSums[attrId] = value;
        PersistTargets();
    }

    private bool HasActiveMinSums()
    {
        foreach (var kv in _minSums)
        {
            if (kv.Value > 0) return true;
        }
        return false;
    }

    private void AddTarget(int attrId)
    {
        if (_targetIds.Contains(attrId)) return;
        _targetIds.Add(attrId);
        PersistTargets();
        RefreshInventorySnapshot();
        RebuildPickerSource();
    }

    private void RemoveTarget(int attrId)
    {
        if (!_targetIds.Remove(attrId)) return;
        _minSums.Remove(attrId);
        PersistTargets();
        RefreshInventorySnapshot();
        RebuildPickerSource();
    }

    private void SetCategoryMaskBit(ModuleCategory category, bool on)
    {
        var bit = 1 << ((int)category - 1);
        var next = on ? (_categoryMask | bit) : (_categoryMask & ~bit);
        if (next == _categoryMask) return;
        _categoryMask = next;
        PersistTargets();
        RefreshInventorySnapshot();
    }

    // ---- Shared helpers -----------------------------------------------------

    private string? ResolveAttrName(int attrId)
    {
        var name = _services.GameData.Combat.GetAttribute(attrId)?.Name;
        if (!string.IsNullOrEmpty(name)) return name;
        name = _services.GameData.Combat.GetAttributeProfile(attrId)?.Name;
        if (!string.IsNullOrEmpty(name)) return name;
        return ModuleAttrName(attrId);
    }

    // Compact attribute label for combo slot lines — short name if available.
    private string ResolveAttrShort(int attrId)
    {
        var info = _services.GameData.Combat.GetAttribute(attrId);
        if (info is { ShortName: { Length: > 0 } sn }) return sn;
        var name = info?.Name ?? _services.GameData.Combat.GetAttributeProfile(attrId)?.Name;
        if (string.IsNullOrEmpty(name)) name = ModuleAttrName(attrId);
        return string.IsNullOrEmpty(name) ? "#" + attrId.ToString(CultureInfo.InvariantCulture) : name!;
    }

    private string ResolveAttrNameOrId(int attrId)
    {
        var name = ResolveAttrName(attrId);
        if (!string.IsNullOrEmpty(name)) return name!;
        LogOrphanAttr(attrId);
        return "#" + attrId.ToString(CultureInfo.InvariantCulture);
    }

    private static float SafeTimeNow()
    {
        try { return Time.realtimeSinceStartup; } catch { return 0f; }
    }

    // Union of all AttrIds in the inventory snapshot minus current targets, sorted
    // by resolved name and filtered by the search box. Cached in _pickerVisible;
    // rebuilt on inventory/target change + each search keystroke.
    private void RebuildPickerSource()
    {
        _pickerVisible.Clear();
        var seen = new HashSet<int>();
        var snap = _services.Inventory.GetModules();
        if (snap is not null)
        {
            foreach (var m in snap.Modules)
            {
                foreach (var p in m.Parts) seen.Add(p.AttrId);
            }
        }
        if (seen.Count == 0)
        {
            foreach (var id in _targetIds) seen.Add(id);
        }
        var trimmed = (_pickerSearch ?? string.Empty).Trim();
        foreach (var id in seen)
        {
            if (_targetIds.Contains(id)) continue;   // already a target
            if (trimmed.Length > 0)
            {
                var name = ResolveAttrName(id) ?? "#" + id.ToString(CultureInfo.InvariantCulture);
                if (name.IndexOf(trimmed, StringComparison.OrdinalIgnoreCase) < 0) continue;
            }
            _pickerVisible.Add(id);
        }
        _pickerVisible.Sort((a, b) => string.Compare(
            ResolveAttrName(a) ?? "#" + a, ResolveAttrName(b) ?? "#" + b,
            StringComparison.OrdinalIgnoreCase));
    }
}
