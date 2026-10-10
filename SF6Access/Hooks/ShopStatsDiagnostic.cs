using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using REFrameworkNET;
using REFrameworkNET.Attributes;
using REFrameworkNET.Callbacks;
using SF6Access.Services;

namespace SF6Access.Hooks;

/// <summary>
/// DEV TOOL (F3): gear-stat research dump for the shop. While recording is ON,
/// every change of the focused shop item appends a block to
/// reframework/data/sf6access_shopstats_*.txt containing the RAW material the
/// announcement is built from:
///   - the full ShopItemList GUI text sequence (owner | element | text, hidden
///     included) — this is where the "every item reads the same" came from:
///     the compare panel recycles three row widgets and only refreshes the
///     ones the focused piece actually fills,
///   - the product the game says the cursor is on
///     (ItemListBaseParam.GetCurrentProduct) and its item params' granted
///     stats — the source the announcement now uses,
///   - the WTPlayerStatusType enum table (written once, at the top).
/// Recording is ON by default: the player is blind and cannot be asked to press
/// a key at the right moment, and a block is only written when the focus moves.
/// F3 turns it off (and back on).
/// <para>Everything is written LINE BY LINE and flushed: an engine read that
/// kills the callback used to take the whole block down with it, and the file
/// then ended mid-section, which looked like "the list is empty".</para>
/// </summary>
public static class ShopStatsDiagnostic
{
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private const int VK_F3 = 0x72;
    private const string STATUS_TYPE_ENUM = "app.worldtour.WTStatusDefine.WTPlayerStatusType";

    private static bool _lastKey;
    private static bool _recording = true;
    private static StreamWriter _writer;
    private static int _frame;
    private static int _lastIdx = int.MinValue;
    private static string _lastType;
    private static ulong _lastParamAddr;

    private static readonly string[] GridFields = { "_itemGrid", "_itemGrid_PickUp", "_itemGridView" };

    [Callback(typeof(UpdateBehavior), CallbackType.Pre)]
    public static void OnUpdate()
    {
        try
        {
            bool down = (GetAsyncKeyState(VK_F3) & 0x8000) != 0;
            if (down && !_lastKey)
            {
                _recording = !_recording;
                API.LogInfo($"[SF6Access] Shop stat recording {(_recording ? "ON" : "OFF")}");
                ScreenReaderService.Speak(_recording ? "商店属性记录开启" : "商店属性记录关闭");
            }
            _lastKey = down;

            if (!_recording) return;
            if (++_frame % 15 != 0) return;   // index check only — no GUI scan per frame
            Poll();
        }
        catch (Exception ex)
        {
            API.LogError($"[SF6Access] Shop stat diagnostic error: {ex.Message}");
        }
    }

    private static void Poll()
    {
        var ordered = FlowHelper.FindFlowParamsOrdered(ShopHooks.Types);
        foreach (var (typeName, param) in ordered)
        {
            // Every list screen, not only the ones whose type name spells
            // "ItemList": the enhance target / material params are named
            // Strength*List and were never captured, which left the material
            // list's wrong names unexplained. A param without a grid is skipped.
            if (param == null) continue;
            ManagedObject grid = null;
            string gridField = null;
            int idx = -1;
            foreach (var field in GridFields)
            {
                var g = FlowHelper.GetObjectField(param, field);
                if (g == null) continue;
                int i = FlowHelper.CallInt(g, "get_SelectedIndex");
                if (i < 0) continue;
                grid = g;
                gridField = field;
                idx = i;
                break;
            }
            if (grid == null) continue;

            ulong addr = 0;
            try { addr = param.GetAddress(); } catch { }
            if (idx == _lastIdx && typeName == _lastType && addr == _lastParamAddr) continue;
            _lastIdx = idx;
            _lastType = typeName;
            _lastParamAddr = addr;
            Capture(typeName, param, gridField, idx);
            break;
        }
    }

    private static StreamWriter Writer()
    {
        if (_writer != null) return _writer;
        string file = Path.Combine(ObjectDumper.DumpDir, $"sf6access_shopstats_{DateTime.Now:HHmmss}.txt");
        _writer = new StreamWriter(new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.ReadWrite),
                                   new UTF8Encoding(false));
        _writer.AutoFlush = true;
        Write($"=== SF6 SHOP GEAR STATS DIAGNOSTIC - {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
        Write("");
        AppendEnumTable();
        API.LogInfo($"[SF6Access] Shop stat dump file: {file}");
        return _writer;
    }

    private static void Write(string line)
    {
        try { Writer().WriteLine(line); }
        catch (Exception ex) { API.LogError($"[SF6Access] Shop stat write failed: {ex.Message}"); }
    }

    private static void Capture(string typeName, ManagedObject param, string gridField, int idx)
    {
        Writer();
        Write($"######## {DateTime.Now:HH:mm:ss} | {typeName} | {gridField} | idx={idx} ########");

        string guiOwner = typeName.EndsWith("ParamOnline") ? "ShopOnlineItemList" : "ShopItemList";
        Write($"guiOwner = {guiOwner}");

        // 1. THE PATH THE ANNOUNCEMENT USES: the game's own "current product".
        Write("--- GetCurrentProduct ---");
        Write($"  method present: {HasMethod(param, "GetCurrentProduct")}");
        var product = FlowHelper.Call(param, "GetCurrentProduct") as ManagedObject;
        Write($"  product = {(product == null ? "(null)" : product.GetTypeDefinition()?.FullName)}");
        if (product != null)
        {
            Write($"  _productName = {FlowHelper.ReadStringField(product, "_productName") ?? "(null)"}");
            var items = FlowHelper.GetObjectField(product, "_itemParamList");
            Write($"  _itemParamList = {(items == null ? "(null)" : items.GetTypeDefinition()?.FullName)}");
            ProbeList(items, "    ", i => AppendEquipStatus(FlowHelper.GetListItem(items, i)));
        }

        // 2. the whole GUI text sequence of the list screen (hidden included)
        Write("--- GUI texts (owner | element | text) ---");
        try
        {
            int n = 0;
            foreach (var t in GuiTextReader.ReadTextsByOwner(guiOwner, visibleOnly: false))
            {
                if (string.IsNullOrWhiteSpace(t.Text)) continue;
                Write($"  [{n++}] {t.Owner} | {t.Name} | {t.Text.Replace('\n', ' ')}");
            }
        }
        catch (Exception ex) { Write($"  (gui scan failed: {ex.Message})"); }

        // 3. the item params the shop is previewing (the older stat source)
        Write("--- try-on / dressing-up item params ---");
        foreach (var field in new[] { "_nowWantEquipItems_TryingOn", "_nowWantEquipItems_DressingUp",
                                      "_nowTryingOnItems", "_nowDressingUpItems" })
        {
            var items = FlowHelper.GetObjectField(param, field);
            Write($"  {field}: {(items == null ? "(null)" : items.GetTypeDefinition()?.FullName)}");
            ProbeList(items, "    ", i => AppendEquipStatus(FlowHelper.GetListItem(items, i)));
        }
        Write($"  _focusItem_EquipSlot = {FlowHelper.ReadIntField(param, "_focusItem_EquipSlot", -1)}");

        // 4. the product list — where the cursor index and the product index
        // have never been proven to line up
        Write("--- ProductList ---");
        var products = FlowHelper.GetObjectField(param, "ProductList");
        Write($"  ProductList = {(products == null ? "(null)" : products.GetTypeDefinition()?.FullName)}");
        ProbeList(products, "    ", null);

        Write("");
    }

    /// <summary>
    /// Everything that can be learned about one engine list — type, element
    /// type, count, and then every element through every route — written as it
    /// is found so a read that kills the callback leaves the evidence behind.
    /// </summary>
    private static void ProbeList(ManagedObject list, string indent, Action<int> perElement)
    {
        if (list == null) { Write(indent + "(null)"); return; }
        try
        {
            var td = list.GetTypeDefinition();
            var element = td?.ElementType;
            Write($"{indent}type = {td?.FullName}, element = {element?.FullName ?? "(none)"}, " +
                  $"elementIsValueType = {(element == null ? "?" : element.IsValueType().ToString())}");

            ulong addr = FlowHelper.AddressOf(list);
            Write($"{indent}addr = 0x{addr:X}");
            var header = new StringBuilder();
            for (int i = 1; i <= 5; i++) header.Append($"+{(i * 8):X}=0x{FlowHelper.ReadQword(addr + (ulong)(i * 8)):X} ");
            Write($"{indent}header: {header}");

            ulong storage = FlowHelper.ReadQword(addr + 0x10);
            Write($"{indent}storage(+0x10) = 0x{storage:X}");
            if (storage != 0)
            {
                var sb = new StringBuilder();
                for (int i = 0; i < 8; i++) sb.Append(FlowHelper.ReadQword(storage + (ulong)(i * 8)).ToString("X") + " ");
                Write($"{indent}first 8 words at storage: {sb}");
            }

            int count = FlowHelper.GetListCount(list);
            Write($"{indent}count = {count}");
            for (int i = 0; i < count && i < 12; i++)
            {
                var item = FlowHelper.GetListItem(list, i);
                if (item == null) { Write($"{indent}[{i}] = (null)"); continue; }
                string name = FlowHelper.CleanTags(FlowHelper.Call(item, "GetNameWithLevel") as string)
                              ?? FlowHelper.Call(item, "GetName") as string
                              ?? FlowHelper.ReadStringField(item, "_productName");
                Write($"{indent}[{i}] {name} <{item.GetTypeDefinition()?.FullName}>");
                perElement?.Invoke(i);
            }
        }
        catch (Exception ex) { Write($"{indent}(probe failed: {ex.Message})"); }
    }

    private static bool HasMethod(ManagedObject obj, string name)
    {
        try { return MemberAccess.FindMethod(obj.GetTypeDefinition(), name, logMiss: false) != null; }
        catch { return false; }
    }

    private static void AppendEquipStatus(ManagedObject itemParam)
    {
        if (itemParam == null) return;
        try
        {
            var statusData = FlowHelper.Call(itemParam, "GetEquipStatus", false) as ManagedObject;
            Write($"      GetEquipStatus = {(statusData == null ? "(null)" : statusData.GetTypeDefinition()?.FullName)}");
            if (statusData == null) return;

            // The direct answer per stat — no list read needed.
            var sb = new StringBuilder();
            foreach (int type in new[] { 1, 6, 7, 8, 9, 10 })
            {
                float v = FlowHelper.CallFloat(statusData, "GetValue", float.NaN, type);
                if (float.IsNaN(v)) continue;
                sb.Append($"{LocalizedText.StatLabel(type) ?? type.ToString()} {v}; ");
            }
            Write($"      GetValue: {sb}");

            var dataList = FlowHelper.GetObjectField(statusData, "DataList");
            int n = FlowHelper.GetListCount(dataList);
            Write($"      DataList count = {n}");
            for (int i = 0; i < n; i++)
            {
                var data = FlowHelper.GetListItem(dataList, i);
                if (data == null) { Write($"      data[{i}] = (null)"); continue; }
                int type = FlowHelper.ReadIntField(data, "Type");
                float value = FlowHelper.ReadFloatField(data, "Value", 0f);
                Write($"      data[{i}] Type={type} ({FlowHelper.ResolveEnumName(STATUS_TYPE_ENUM, type)}) " +
                      $"label={LocalizedText.StatLabel(type) ?? "-"} Value={value}");
            }
        }
        catch (Exception ex) { Write($"      (GetEquipStatus failed: {ex.Message})"); }
    }

    private static void AppendEnumTable()
    {
        Write("=== WTPlayerStatusType enum ===");
        for (int v = 0; v <= 24; v++)
        {
            string name = FlowHelper.ResolveEnumName(STATUS_TYPE_ENUM, v);
            if (name == null) continue;
            Write($"  {v} = {name}  (label: {LocalizedText.StatLabel(v) ?? "-"})");
        }
        Write("");
    }
}
