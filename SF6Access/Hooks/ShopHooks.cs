using System;
using System.Collections.Generic;
using REFrameworkNET;
using SF6Access.Services;
using SF6Access.Services.Ui;

namespace SF6Access.Hooks;

/// <summary>
/// World Tour / Avatar Arcade shop (app.UIFlowShop.*): the top menu, the buy
/// (general + apparel), sell, enhance (StrengthTargetList) and dye
/// (ColorStainingList) item lists, the dye detail window, and a G / R3
/// shortcut that reads the current Zenny. The list params all inherit
/// app.UIFlowShop.ItemListBaseParam: a `_categoryTab` (UIPartsScrollList, with
/// the current category name mirrored in the `_categoryText` gui text), an
/// `_itemGrid` / `_itemGrid_PickUp` (UIPartsScrollGrid) whose cells only carry
/// numbers (price, counters), and the selected item's name/description/effect
/// rendered in the shared ShopItemList GUI. The buy/sell mode line
/// (e_text_stateName, "Get - Takeout" / "Sell - All") is announced on toggle.
/// The zenny price caption is an icon, so the number gets a localized label.
/// The buy/sell confirm popup is its own flow param (app.UIFlowShop.DialogUI.
/// BuyDialog/SellDialog *Param*): quantity + labeled total render in the shared
/// ShopBuyPopup GUI, and the Decide/Return buttons live in the param's `Group`.
/// Enhance (StrengthTarget) and dye (ColorStaining) screens are NOT covered yet.
/// </summary>
public sealed class ShopHooks : ScreenAdapter
{
    private const string TOP = "app.UIFlowShop.WTTopMenu.Param";
    private const string BUY_GENERAL = "app.UIFlowShop.BuyItemList.ParamGeneral";
    private const string BUY_APPAREL = "app.UIFlowShop.BuyItemList.ParamApparel";
    private const string SELL = "app.UIFlowShop.SellItemList.Param";
    private const string STRENGTH_LIST = "app.UIFlowShop.StrengthTargetList.Param";
    private const string STRENGTH_MATERIAL = "app.UIFlowShop.StrengthMaterialList.Param";
    private const string COLOR_LIST = "app.UIFlowShop.ColorStainingList.Param";
    private const string COLOR_DETAIL = "app.UIFlowShop.ColorStainingDetail.Param";
    // The Battle Hub goods shop (reached from the in-game store) is the same
    // ItemListBaseParam family with its own GUI owner (ShopOnlineItemList).
    private const string HUB_BUY = "app.UIFlowShop.BuyItemList.ParamOnline";
    // Side panes that show the focused gear's stats in the enhance flow —
    // tracked for reading but never own the screen (their lists do).
    private const string STRENGTH_TARGET = "app.UIFlowShop.StrengthTarget.Param";
    private const string STRENGTH_RESULT = "app.UIFlowShop.StrengthResult.Param";
    internal static readonly string[] Types =
        { TOP, BUY_GENERAL, BUY_APPAREL, SELL, STRENGTH_LIST, STRENGTH_MATERIAL, COLOR_LIST,
          COLOR_DETAIL, HUB_BUY, STRENGTH_TARGET, STRENGTH_RESULT };

    // All buy/sell confirm dialog variants (BuyParam_Single/_Mul/_Online,
    // SellParam_Single/_Mul) share this flow-param prefix and the popup GUI.
    private const string DIALOG_PREFIX = "app.UIFlowShop.DialogUI.";
    private static readonly string[] DialogPrefixes = { DIALOG_PREFIX };

    private const string GUI_OWNER = "ShopItemList";
    private const string HUB_GUI_OWNER = "ShopOnlineItemList";
    private const string POPUP_GUI_OWNER = "ShopBuyPopup";

    /// <summary>GUI owner of the active item list (the hub goods shop has its own).</summary>
    private string ActiveGuiOwner => _activeType == HUB_BUY ? HUB_GUI_OWNER : GUI_OWNER;

    public override string[] OwnedTypes => Types;

    public ShopHooks()
    {
        SearchInterval = 30;
        ReadInterval = 1;   // the G/R3 shortcut needs per-frame edge detection
    }

    // Menu/GUI reads still run at the old cadence; only the shortcut is per-frame.
    private const int MENU_POLL_EVERY = 5;
    private int _frame;
    private readonly ReadoutShortcut _currencyShortcut = new();

    private ManagedObject _top, _itemList, _colorDetail, _dialog;
    private ManagedObject _strengthTarget, _strengthResult;
    private string _activeType;
    private string _lastActive;
    private ulong _lastActiveAddr;

    // A list param can host several grids (normal / pick-up / hub "Group View");
    // the inactive ones keep a stale SelectedIndex, so each is tracked and the
    // one that CHANGED owns the announcement.
    private static readonly string[] GridFields = { "_itemGrid", "_itemGrid_PickUp", "_itemGridView" };
    private readonly int[] _lastGridIdx = { int.MinValue, int.MinValue, int.MinValue };

    // Set on a tab change: the re-laid grid's item announcement must QUEUE
    // behind the tab name instead of cutting it off.
    private bool _queueNextItemAnnounce;
    private int _lastTabIdx = int.MinValue;
    private string _lastStateName;

    private ulong _dialogAddr;
    private bool _dialogOpenPending;
    private string _lastDialogLine;
    private readonly GroupFocusPoller _dialogPoller = new(
        "Shop dialog", announceFirst: false, new GroupFocusPoller.Source(null, "Group"));

    protected override bool Locate()
    {
        // Handle order decides which screen is active: backed-out shop screens
        // linger in _Handles (RestoreFlow), so a fixed priority went stale — the
        // first watched type in handle order (newest) owns the screen. The dye
        // detail window is arbitrated separately (its param coexists with the
        // gear list and only counts while IsShow).
        var ordered = FlowHelper.FindFlowParamsOrdered(Types);
        _top = _itemList = _colorDetail = _strengthTarget = _strengthResult = null;
        _activeType = null;
        foreach (var (name, param) in ordered)
        {
            if (name == COLOR_DETAIL) { _colorDetail = param; continue; }
            if (name == STRENGTH_TARGET) { _strengthTarget = param; continue; }
            if (name == STRENGTH_RESULT) { _strengthResult = param; continue; }
            if (_activeType != null) continue;
            _activeType = name;
            if (name == TOP) _top = param;
            else _itemList = param;
        }
        var dialogs = FlowHelper.FindFirstFlowParamsByPrefixes(DialogPrefixes);
        _dialog = dialogs.TryGetValue(DIALOG_PREFIX, out var d) ? d.param : null;
        return _activeType != null || _colorDetail != null || _dialog != null;
    }

    protected override void OnActivate() => ResetState();

    protected override void OnDeactivate()
    {
        _top = _itemList = _colorDetail = _dialog = null;
        _strengthTarget = _strengthResult = null;
        _activeType = null;
        ResetState();
    }

    private void ResetState()
    {
        _lastActive = null;
        _lastActiveAddr = 0;
        _lastTabIdx = int.MinValue;
        for (int g = 0; g < _lastGridIdx.Length; g++) _lastGridIdx[g] = int.MinValue;
        _queueNextItemAnnounce = false;
        _lastStateName = null;
        _dialogAddr = 0;
        _dialogOpenPending = false;
        _lastDialogLine = null;
        _dialogPoller.Reset();
        _lastColorRowIdx = int.MinValue;
    }

    protected override void OnPoll()
    {
        // G / Start: read the current Zenny anywhere in the shop (per-frame so
        // short presses aren't missed; everything else keeps the old cadence).
        // In the hub goods shop OnlineShopHooks announces ALL the balances
        // (tickets + coins + zenny) — two interrupting readouts would race.
        if (_currencyShortcut.Pressed() && _activeType != HUB_BUY)
            CurrencyReader.AnnounceZenny("ShopBg");
        if (++_frame % MENU_POLL_EVERY != 0) return;

        // Buy/sell confirm popup: modal over the item list — it wins.
        ulong dialogAddr = 0;
        try { dialogAddr = _dialog?.GetAddress() ?? 0; } catch { }
        if (dialogAddr != _dialogAddr)
        {
            _dialogAddr = dialogAddr;
            if (dialogAddr != 0)
            {
                _dialogOpenPending = true;
                _lastDialogLine = null;
                _dialogPoller.Reset();
            }
        }
        if (_dialog != null) { PollDialog(); return; }

        // Dye variant window: a persistent SIDE PANE — IsShow stays true for the
        // whole Color section, so gating the section on it muted the gear list.
        // Poll it concurrently: it only speaks when its own row selection
        // changes (focus jumps into the pane when a gear piece is confirmed).
        if (_colorDetail != null && FlowHelper.ReadByteField(_colorDetail, "IsShow") != 0)
            PollColorDetail();

        // Reset the cursors when the active screen OR its param instance changes
        // (a re-entered list can come back as a new instance on the same index)
        ulong activeAddr = 0;
        try { activeAddr = (_itemList ?? _top)?.GetAddress() ?? 0; } catch { }
        if (_activeType != _lastActive || activeAddr != _lastActiveAddr)
        {
            _lastActive = _activeType;
            _lastActiveAddr = activeAddr;
            _lastTabIdx = int.MinValue;
            for (int g = 0; g < _lastGridIdx.Length; g++) _lastGridIdx[g] = int.MinValue;
            _lastStateName = null;
        }

        if (_itemList != null) PollItemList(_itemList);
        // The top menu (Buy / Sell / Enhance...) needs NO reading here: the
        // generic focus reader already announces its rows and the guide watcher
        // its tooltips — a duplicate announcement from this adapter arrived
        // after the queued tooltip and kept cutting it off. TOP stays in Types
        // only so it wins the screen arbitration over backed-out lists.
    }

    private int _lastColorRowIdx = int.MinValue;

    /// <summary>Dye variant pane: announce the focused variant row of its
    /// _scrollList (gear variant name + the dyes it needs + the dye cost).
    /// The first sighting only records the index — while the user browses the
    /// gear LIST the pane keeps a resting selection that must stay silent.</summary>
    private void PollColorDetail()
    {
        var list = FlowHelper.GetObjectField(_colorDetail, "_scrollList");
        int idx = FlowHelper.CallInt(list, "get_SelectedIndex");
        if (idx < 0 || idx == _lastColorRowIdx) return;
        bool first = _lastColorRowIdx == int.MinValue;
        _lastColorRowIdx = idx;
        if (first) return;

        string row = FlowHelper.ReadSelectedItemText(list);
        if (string.IsNullOrEmpty(row)) return;
        // The dye cost caption is an icon — read the pane's own price text
        string price = FlowHelper.ReadGuiText(FlowHelper.GetObjectField(_colorDetail, "_priceText"));
        string msg = string.IsNullOrEmpty(price) ? row : $"{row}. {PriceWord()} {price}";
        API.LogInfo($"[SF6Access] Shop dye row [{idx}]: {msg}");
        Speak(msg);
    }

    /// <summary>
    /// Buy/sell confirm popup: announce title + quantity + total on open, the new
    /// quantity/total when the spin changes, and the focused Decide/Return button
    /// via the group poller. The first e_text_num of the popup GUI is the
    /// quantity and its first e_text_total is the already-labeled "Total: N"
    /// (the second one is the player's money).
    /// </summary>
    private void PollDialog()
    {
        string title = null, qty = null, total = null;
        foreach (var t in GuiTextReader.ReadTextsByOwner(POPUP_GUI_OWNER))
        {
            if (string.IsNullOrWhiteSpace(t.Text)) continue;
            string s = t.Text.Trim();
            switch (t.Name)
            {
                case "e_text_title": title ??= s; break;
                case "e_text_num": qty ??= s; break;
                case "e_text_total": total ??= s; break;
            }
        }

        string line = qty != null && total != null ? $"{qty}. {total}" : total ?? qty;
        if (_dialogOpenPending)
        {
            if (title == null && line == null) return;   // GUI not populated yet — retry
            _dialogOpenPending = false;
            _lastDialogLine = line;
            string msg = title != null ? (line != null ? $"{title}. {line}" : title) : line;
            API.LogInfo($"[SF6Access] Shop dialog: {msg}");
            Speak(msg, interrupt: true);
        }
        else if (line != null && line != _lastDialogLine)
        {
            _lastDialogLine = line;
            API.LogInfo($"[SF6Access] Shop dialog qty: {line}");
            Speak(line, interrupt: true);
        }

        _dialogPoller.Poll(_dialog);
    }

    private void PollItemList(ManagedObject param)
    {
        PollStateName();

        // Category tab: announce the localized category name on change (the
        // param mirrors it into the `_categoryText` gui text; the hub "Group
        // View" mode has its own tab/text pair)
        var tab = FlowHelper.GetObjectField(param, "_categoryTab");
        int tabIdx = FlowHelper.CallInt(tab, "get_SelectedIndex");
        if (tabIdx < 0)
        {
            tab = FlowHelper.GetObjectField(param, "_categoryTabView");
            tabIdx = FlowHelper.CallInt(tab, "get_SelectedIndex");
        }
        if (tabIdx >= 0 && tabIdx != _lastTabIdx)
        {
            bool first = _lastTabIdx == int.MinValue;
            _lastTabIdx = tabIdx;
            for (int g = 0; g < _lastGridIdx.Length; g++)
                _lastGridIdx[g] = int.MinValue;   // tab switch re-lays the grid
            if (!first)
            {
                string category = FlowHelper.ReadGuiText(FlowHelper.GetObjectField(param, "_categoryText"))
                    ?? FlowHelper.ReadGuiText(FlowHelper.GetObjectField(param, "_categoryTextView"))
                    ?? FlowHelper.ReadSelectedItemText(tab);
                if (!string.IsNullOrEmpty(category))
                {
                    API.LogInfo($"[SF6Access] Shop category [{tabIdx}]: {category}");
                    Speak(category);
                    // The re-laid grid announces its item a tick later — queue
                    // it so it doesn't cut this tab name off
                    _queueNextItemAnnounce = true;
                }
                return;
            }
        }

        // Several grids can coexist (normal / pick-up / hub view) and the
        // inactive ones keep a stale index — announce for the grid that CHANGED
        // (polling "the first grid with an index" froze on the hub's normal
        // grid and only the entry item was ever announced).
        ManagedObject changedGrid = null;
        int changedIdx = -1, changedSlot = -1;
        for (int g = 0; g < GridFields.Length; g++)
        {
            var grid = FlowHelper.GetObjectField(param, GridFields[g]);
            if (grid == null) continue;
            int idx = FlowHelper.CallInt(grid, "get_SelectedIndex");
            if (idx < 0) continue;
            if (idx != _lastGridIdx[g] && changedGrid == null)
            {
                changedGrid = grid;
                changedIdx = idx;
                changedSlot = g;
            }
            _lastGridIdx[g] = idx;
        }
        if (changedGrid == null) return;

        string msg = ReadSelectedProduct(param, changedGrid, ActiveGuiOwner);
        if (string.IsNullOrEmpty(msg))
        {
            _lastGridIdx[changedSlot] = int.MinValue;   // GUI not populated yet — retry
            return;
        }
        API.LogInfo($"[SF6Access] Shop item [{changedIdx}]: {msg}");
        Speak(msg, interrupt: !_queueNextItemAnnounce);
        _queueNextItemAnnounce = false;
    }

    /// <summary>Buy/sell mode line ("Get - Takeout", "Sell - All"): announce when
    /// the user toggles it (not on entry — the screen context covers that).</summary>
    private void PollStateName()
    {
        string state = null;
        foreach (var t in GuiTextReader.ReadTextsByOwner(ActiveGuiOwner))
        {
            if (t.Name == "e_text_stateName" && !string.IsNullOrWhiteSpace(t.Text))
            {
                state = t.Text.Trim();
                break;
            }
        }
        if (state == null || state == _lastStateName) return;
        bool first = _lastStateName == null;
        _lastStateName = state;
        if (first) return;

        API.LogInfo($"[SF6Access] Shop mode: {state}");
        Speak(state);
    }

    /// <summary>
    /// Selected product: name/description from the param's own detail widget
    /// (_itemDetail, hidden texts included — the "Toggle Item Detail Display"
    /// option hides the panel), the first effect from _itemEffectList (an
    /// e_text_value followed by its e_text_name), and the price from the
    /// selected grid cell's own e_text_price (cells carry only numbers).
    /// Do NOT scan the whole ShopItemList GUI for the name: the player
    /// equip-status compare panel renders its stat labels first, and the gear
    /// lists announced "Defense" as the item name.
    /// </summary>
    private string ReadSelectedProduct(ManagedObject param, ManagedObject grid, string guiOwner)
    {
        string name = null, detail = null;
        foreach (var t in GuiTextReader.ReadControlTexts(
                     ControlOf(FlowHelper.GetObjectField(param, "_itemDetail")), visibleOnly: false))
        {
            if (string.IsNullOrWhiteSpace(t.Text) || t.Text.Contains('{')) continue;
            string s = t.Text.Replace('\n', ' ').Trim();
            if (t.Name == "e_text_name") name ??= s;
            else if (t.Name == "e_text_detail") detail ??= s;
        }

        // Effect: the first name of the effect widget, with its value only when
        // it renders DIRECTLY before it ("Recover Vitality +10000"). Loose
        // pairing grabbed unrelated numbers ("Drive Time 377" — a granted perk
        // paired with a player stat).
        string effectName = null, effectValue = null;
        string prevEffectElem = null, prevEffectText = null;
        foreach (var t in GuiTextReader.ReadControlTexts(
                     ControlOf(FlowHelper.GetObjectField(param, "_itemEffectList")), visibleOnly: false))
        {
            if (string.IsNullOrWhiteSpace(t.Text) || t.Text.Contains('{')) continue;
            string s = t.Text.Replace('\n', ' ').Trim();
            if (t.Name == "e_text_name" && effectName == null)
            {
                effectName = s;
                effectValue = prevEffectElem == "e_text_value" ? prevEffectText : null;
            }
            prevEffectElem = t.Name;
            prevEffectText = s;
        }

        // Fallback when the detail widget yields nothing (its control/element
        // layout is unverified on some lists — trusting it alone MUTED the whole
        // shop): the flat owner scan, skipping the equip-status panel's stat
        // labels, which are the e_text_name entries preceded by an
        // e_text_current value ("Defense" was announced as the item name).
        if (string.IsNullOrEmpty(name))
        {
            string previousElement = null;
            foreach (var t in GuiTextReader.ReadTextsByOwner(guiOwner))
            {
                if (string.IsNullOrWhiteSpace(t.Text)) continue;
                string s = t.Text.Replace('\n', ' ').Trim();
                switch (t.Name)
                {
                    case "e_text_name":
                        if (previousElement == "e_text_current") break;   // equip-panel stat label
                        name ??= s;
                        break;
                    case "e_text_detail":
                        detail ??= s;
                        break;
                }
                previousElement = t.Name;
            }
        }

        // Enhance material list: the detail pane keeps the text of ONE row while
        // the cursor moves, so every material was announced under the same name.
        // The product the cursor is on is authoritative (see ReadProductName).
        if (_activeType == STRENGTH_MATERIAL)
        {
            string productName = ReadProductName(param);
            if (!string.IsNullOrEmpty(productName))
            {
                API.LogInfo($"[SF6Access] Shop material name: product '{productName}' vs gui '{name}'");
                name = productName;
            }
        }

        if (string.IsNullOrEmpty(name)) return null;

        string price = null;
        var cell = FlowHelper.Call(grid, "get_SelectedItem") as ManagedObject;
        if (cell != null)
        {
            foreach (var t in GuiTextReader.ReadControlTexts(cell))
            {
                if (t.Name == "e_text_price" && !string.IsNullOrWhiteSpace(t.Text))
                {
                    price = t.Text.Trim();
                    break;
                }
            }
        }

        var parts = new List<string> { name };
        // The zenny caption next to the price is an icon — label the bare number
        if (!string.IsNullOrEmpty(price)) parts.Add($"{PriceWord()} {price}");
        string stats = ReadStatsForSelection(param, name, guiOwner);
        if (!string.IsNullOrEmpty(stats)) parts.Add(stats);
        if (effectName != null) parts.Add(effectValue != null ? $"{effectName} {effectValue}" : effectName);
        if (!string.IsNullOrEmpty(detail)) parts.Add(detail);
        return string.Join(". ", parts);
    }

    /// <summary>
    /// The focused gear's stats, per screen. First choice everywhere is the
    /// product the cursor is on (see <see cref="ReadProductStats"/>). Enhance
    /// target/material lists then fall back to the side pane's
    /// UIPartsPlayerEquipStatus (its stat captions are textures — mLabelList
    /// gives StatusType + value). Buy lists: the item-vs-equipped compare
    /// triplets in the list GUI; the product data as last resort.
    /// <para>The pane used to come FIRST, and in the material list it does not
    /// follow the cursor: it is the enhance RESULT pane, so it kept reporting
    /// the numbers of the gear being enhanced (every material announced the
    /// stats of a hat). Product data is the only source that tracks the
    /// cursor, so it wins; the pane is a fallback.</para>
    /// </summary>
    private string ReadStatsForSelection(ManagedObject param, string name, string guiOwner)
    {
        // 1) The product the cursor is on, straight from the game's own
        // "which product is current" answer (ItemListBaseParam.
        // GetCurrentProduct) — one product, its item params, and each param's
        // own granted stats. No GUI, no index arithmetic: the compare panel
        // recycles three row widgets and the two the focused piece does not
        // fill keep whatever the PREVIOUS piece left in them, which is why
        // every item announced the same "Defense 8, Kick 0".
        string fromData = ReadProductStats(param);
        if (fromData != null) return fromData;

        // 2) The gear the shop itself is holding as "to equip" for the focused
        // row (it previews the piece on the avatar).
        fromData = ReadTryOnStats(param);
        if (fromData != null) return fromData;

        // 3) Enhance target / material side panes.
        var pane = _activeType == STRENGTH_LIST
            ? FlowHelper.GetObjectField(_strengthTarget, "_targetInfo")
            : _activeType == STRENGTH_MATERIAL
                ? FlowHelper.GetObjectField(_strengthResult, "_materialInfo")
                : null;
        if (pane != null)
        {
            string fromPane = AvatarStatsReader.FormatStats(AvatarStatsReader.ReadStatsFromEquipStatusWidget(
                FlowHelper.GetObjectField(pane, "_playerEquipStatus")));
            if (fromPane != null) return fromPane;
        }

        // 4) The on-screen compare rows (weaker: stale rows — see above).
        return ReadCompareStats(guiOwner) ?? ReadItemDataStats(param, name);
    }

    /// <summary>
    /// The stats of the product the cursor is on, read from the game's own
    /// objects: <c>ItemListBaseParam.GetCurrentProduct()</c> → the product's
    /// <c>_itemParamList</c> → <c>WTItemParam.GetEquipStatus()</c>. A set (a
    /// costume sold as one product) is several item params and their values add
    /// up, which is what the player sees when they put it on.
    /// <para>This is the ONLY source that follows the cursor exactly. The
    /// compare panel keeps three row widgets and refreshes only the ones the
    /// focused piece actually uses, so a piece with one stat leaves two rows
    /// holding the previous piece's numbers — announced, that reads as "every
    /// item is the same".</para>
    /// </summary>
    private static string ReadProductStats(ManagedObject param)
    {
        try
        {
            var product = FlowHelper.Call(param, "GetCurrentProduct") as ManagedObject;
            if (product == null) return null;

            var items = FlowHelper.GetObjectField(product, "_itemParamList");
            int count = FlowHelper.GetListCount(items);

            var list = new List<ManagedObject>();
            for (int i = 0; i < count; i++)
            {
                var itemParam = FlowHelper.GetListItem(items, i);
                if (itemParam != null) list.Add(itemParam);
            }
            if (list.Count == 0) return null;

            string stats = AvatarStatsReader.FormatStats(AvatarStatsReader.ReadStatsOfItemParams(list));
            if (string.IsNullOrEmpty(stats)) return null;

            var names = new List<string>();
            foreach (var item in list)
                names.Add(FlowHelper.CleanTags(FlowHelper.Call(item, "GetNameWithLevel") as string) ?? "?");
            API.LogInfo($"[SF6Access] Shop stats from GetCurrentProduct " +
                        $"({list.Count} of {count} item param(s)): {string.Join(" + ", names)} -> {stats}");
            return stats;
        }
        catch (Exception ex)
        {
            API.LogInfo($"[SF6Access] Shop product stats failed: {ex.Message}");
        }
        return null;
    }

    /// <summary>
    /// The name of the product the cursor is on, from the same objects the stats
    /// come from: <c>WTItemParam.GetNameWithLevel()</c> for a single-piece
    /// product, the product's own <c>_productName</c> for a set.
    /// <para>Used where the GUI name goes stale. In the enhance MATERIAL list
    /// every row announced the same name: the detail pane keeps the text of one
    /// row while the cursor moves. The level suffix the game puts in
    /// GetNameWithLevel is also the only thing that tells two stacks of the same
    /// material apart — they are identical items at different levels.</para>
    /// </summary>
    private static string ReadProductName(ManagedObject param)
    {
        try
        {
            var product = FlowHelper.Call(param, "GetCurrentProduct") as ManagedObject;
            if (product == null) return null;

            string productName = FlowHelper.CleanTags(FlowHelper.ReadStringField(product, "_productName"));
            var items = FlowHelper.GetObjectField(product, "_itemParamList");
            int count = FlowHelper.GetListCount(items);

            if (count == 1)
            {
                var itemParam = FlowHelper.GetListItem(items, 0);
                string itemName = FlowHelper.CleanTags(FlowHelper.Call(itemParam, "GetNameWithLevel") as string)
                                  ?? FlowHelper.CleanTags(FlowHelper.Call(itemParam, "GetName") as string);
                if (!string.IsNullOrWhiteSpace(itemName)) return itemName.Trim();
            }
            return string.IsNullOrWhiteSpace(productName) ? null : productName.Trim();
        }
        catch { }
        return null;
    }

    /// <summary>
    /// The stats of the gear piece the shop is currently previewing, read from
    /// the game's OWN item objects instead of the GUI: the param keeps the
    /// WTItemParams it wants to equip in `_nowWantEquipItems_TryingOn` (and the
    /// dressing-up twin). A set is several params — their values add up, which
    /// is exactly what the GUI renders as "the same stat twice".
    /// </summary>
    private static string ReadTryOnStats(ManagedObject param)
    {
        try
        {
            // 1) The single piece the shop is previewing ("dressing up").
            string stats = StatsOfItems(param, "_nowWantEquipItems_DressingUp", -1);
            if (stats != null) return stats;

            // 2) The whole previewed outfit — narrowed down to the slot the
            // cursor is on. The array holds EVERY slot (seven of them), so
            // summing it would announce the outfit, not the piece.
            int slot = FlowHelper.ReadIntField(param, "_focusItem_EquipSlot", -1);
            if (slot >= 0)
            {
                stats = StatsOfItems(param, "_nowWantEquipItems_TryingOn", slot);
                if (stats != null) return stats;
            }
        }
        catch (Exception ex)
        {
            API.LogInfo($"[SF6Access] Shop try-on stats failed: {ex.Message}");
        }
        return null;
    }

    /// <summary>
    /// The stats of the item params in one of the shop's "want to equip" arrays.
    /// With <paramref name="slotFilter"/> &gt;= 0 only the pieces that belong to
    /// that equip slot count (that is how the focused piece is picked out of the
    /// full outfit); when a piece's slot cannot be read the array is refused
    /// rather than summed whole — the outfit total is not what a shopper asked
    /// for. A set (a costume sold as one product) fills several slots, so more
    /// than one piece may pass the filter and their values are added.
    /// </summary>
    private static string StatsOfItems(ManagedObject param, string field, int slotFilter)
    {
        var items = FlowHelper.GetObjectField(param, field);
        int count = FlowHelper.GetListCount(items);
        if (count <= 0) return null;

        var list = new List<ManagedObject>();
        for (int i = 0; i < count; i++)
        {
            var item = FlowHelper.GetListItem(items, i);
            if (item == null) continue;
            if (slotFilter >= 0)
            {
                int slot = EquipSlotOf(item);
                if (slot < 0) return null;      // cannot tell — refuse the array
                if (slot != slotFilter) continue;
            }
            list.Add(item);
        }
        if (list.Count == 0) return null;

        string stats = AvatarStatsReader.FormatStats(AvatarStatsReader.ReadStatsOfItemParams(list));
        if (string.IsNullOrEmpty(stats)) return null;

        var names = new List<string>();
        foreach (var item in list)
            names.Add(FlowHelper.CleanTags(FlowHelper.Call(item, "GetNameWithLevel") as string)
                      ?? FlowHelper.Call(item, "GetName") as string ?? "?");
        API.LogInfo($"[SF6Access] Shop stats from {field} ({list.Count} of {count} item(s)), " +
                    $"slot {slotFilter}: {string.Join(" + ", names)} -> {stats}");
        return stats;
    }

    /// <summary>The equip slot a WTItemParam belongs to, -1 when the game does
    /// not expose it (then callers must not guess).</summary>
    private static int EquipSlotOf(ManagedObject itemParam)
    {
        foreach (var getter in new[] { "GetEquipSlot", "get_EquipSlot", "GetSlot", "get_Slot" })
        {
            int v = FlowHelper.CallInt(itemParam, getter, -1);
            if (v >= 0) return v;
        }
        foreach (var fieldName in new[] { "EquipSlot", "_equipSlot", "Slot", "_slot", "mEquipSlot" })
        {
            int v = FlowHelper.ReadIntField(itemParam, fieldName, -1);
            if (v >= 0) return v;
        }
        return -1;
    }

    /// <summary>
    /// The item-vs-equipped compare block of a buy list: it renders STRICT
    /// triplets in tree order — e_text_value (the gear's value) directly
    /// followed by e_text_current (the equipped one) then e_text_name (the
    /// localized label) → "Defense 5". Adjacency is required: loose value/name
    /// pairing picked up the player-status panel's totals instead and announced
    /// "Defense 377" on every item.
    /// </summary>
    /// <summary>
    /// The item-vs-equipped compare block of a buy list: it renders STRICT
    /// triplets in tree order — e_text_value (the gear currently worn) directly
    /// followed by e_text_current (what the FOCUSED piece grants) then
    /// e_text_name (the localized label) → "Defense 5". Adjacency is required:
    /// loose value/name pairing picked up the player-status panel's totals
    /// instead and announced "Defense 377" on every item.
    /// <para>The value that belongs to the focused row is <b>e_text_current</b>.
    /// e_text_value is the piece the avatar is wearing, and the shop re-dresses
    /// the avatar as the cursor moves, so it differed between two visits to the
    /// SAME row ("the same item reads differently every time").</para>
    /// </summary>
    private static string ReadCompareStats(string guiOwner)
    {
        try
        {
            var stats = new List<string>();
            var seen = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            string prevName1 = null, prevName2 = null, prevText2 = null, prevText1 = null;
            foreach (var t in GuiTextReader.ReadTextsByOwner(guiOwner))
            {
                if (string.IsNullOrWhiteSpace(t.Text)) continue;
                string s = t.Text.Trim();
                if (t.Name == "e_text_name" && prevName1 == "e_text_current" && prevName2 == "e_text_value")
                {
                    // The same caption can render twice (a set's pieces each get
                    // their own row) — one gear piece, one number per stat.
                    if (seen.Add(s)) stats.Add($"{s} {prevText1}");
                }
                prevName2 = prevName1;
                prevText2 = prevText1;
                prevName1 = t.Name;
                prevText1 = s;
            }
            return stats.Count > 0 ? string.Join(", ", stats) : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// The selected gear's own granted stats from the product data: the product
    /// matching the announced name in ProductList → _itemParamList[0] →
    /// WTItemParam.GetEquipStatus (via AvatarStatsReader — same source as the
    /// Status menu's per-item stats). Null for non-gear items (no stats).
    /// </summary>
    private static string ReadItemDataStats(ManagedObject param, string name)
    {
        try
        {
            var products = FlowHelper.GetObjectField(param, "ProductList");
            int count = FlowHelper.GetListCount(products);
            for (int i = 0; i < count; i++)
            {
                var product = FlowHelper.GetListItem(products, i);
                string productName = FlowHelper.ReadStringField(product, "_productName")
                    ?? FlowHelper.Call(product, "GetName") as string;
                if (string.IsNullOrWhiteSpace(productName)) continue;
                if (!name.Contains(productName) && !productName.Contains(name)) continue;

                var itemParam = FlowHelper.GetListItem(
                    FlowHelper.GetObjectField(product, "_itemParamList"), 0);
                return AvatarStatsReader.FormatStats(AvatarStatsReader.ReadStatsOfItem(itemParam));
            }
        }
        catch { }
        return null;
    }

    private static ManagedObject ControlOf(ManagedObject parts)
        => FlowHelper.GetObjectField(parts, "Control")
           ?? FlowHelper.Call(parts, "get_Control") as ManagedObject;

    private static string PriceWord() => LocalizedText.Price();
}
