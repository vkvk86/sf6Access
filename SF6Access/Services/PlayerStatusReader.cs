using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using REFrameworkNET;

namespace SF6Access.Services;

/// <summary>
/// "My status": the numbers a blind player cannot see — health, Zenny, level,
/// experience, skill points, the five combat stats and the two gauges — read
/// straight out of the game's own World Tour player data and spoken on demand
/// (L3 on the pad, H on the keyboard).
///
/// <para><b>The member names here are MEASURED, not guessed.</b> The field dump
/// of <c>app.worldtour.WTPlayerData</c> (2026-10-06) shows where each number
/// really lives: <c>Status.VitalNow</c> is the current health and status-array
/// entry 1 (Vitality) its upper bound — entry 6 is PunchPower, so a loose
/// "contains hp" test picks up "Punc(hP)ower" and speaks 386 as health, which
/// is exactly what the first build did; <c>Status.Level</c> and
/// <c>Status.Exp</c> sit inside Status rather than on the player data itself;
/// and the skill points are Style's total minus used.</para>
///
/// <para>Every read is defensive: fields are read through the type definition,
/// and a getter is only ever invoked once its RETURN TYPE has been checked —
/// numeric for a value, <c>app.</c> for a record — so no call here can hand
/// back a reference type (the c0000005 rule that took the plugin down twice).</para>
///
/// <para>A value that cannot be read is left out of the sentence instead of
/// being spoken as zero. "0 Zenny" is a lie a player would act on, and so is
/// "health 0".</para>
/// </summary>
public static class PlayerStatusReader
{
    private const string PLAYER_MANAGER = "app.worldtour.WTPlayerManager";
    private const string STATUS_TYPE_ENUM = "app.worldtour.WTStatusDefine.WTPlayerStatusType";

    // Status-array indices, confirmed against the enum names the game reports.
    private const int TYPE_VITALITY = 1;         // upper bound of health
    private const int TYPE_FOCUS_LEVEL = 2;      // Focus / Drive gauge level
    private const int TYPE_SA_LEVEL = 4;         // Super Art gauge level
    private static readonly int[] CombatTypes = { 6, 7, 8, 9, 10 };   // punch, kick, throw, special, defense

    private const string GET_VALUE_SIG = "GetValue(app.worldtour.WTStatusDefine.WTPlayerStatusType)";

    /// <summary>Speak the status line. <paramref name="zenny"/> and
    /// <paramref name="mile"/> are the values the hook sampled across many
    /// frames (pass <see cref="int.MinValue"/> to read them once here, which is
    /// what the keyboard diagnostic does).</summary>
    public static void Announce(int zenny = int.MinValue, int mile = int.MinValue, bool deep = false)
    {
        try
        {
            var data = LocalPlayerData();
            var status = Record(data, "Status");
            var parts = new List<string>();

            // 1. Health: current from the live field, upper bound from the
            //    computed status array (Vitality).
            float now = Number(status, "VitalNow");
            float max = StatusValue(data, TYPE_VITALITY);
            if (!float.IsNaN(now))
            {
                parts.Add(float.IsNaN(max) || max <= 0f
                    ? string.Concat(LocalizedText.StatusHp(), " ", Num(now))
                    : string.Format(LocalizedText.StatusHpFull(), Num(now), Num(max)));
            }

            // 3. Miles — the "里程" the HUD hands out (CityHud_ItemGet /
            //    MileNotice). Not documented anywhere, so the wallet is asked
            //    first and, failing that, the whole player record is searched
            //    for a member whose name says "mile".
            if (mile == int.MinValue) mile = ReadMile();
            if (mile != int.MinValue) parts.Add(string.Concat(LocalizedText.StatusMile(), " ", Num(mile)));

            // 4. Zenny.
            if (zenny == int.MinValue) zenny = ReadZennyStable();
            if (zenny != int.MinValue)
                parts.Add(string.Concat(LocalizedText.StatusZenny(), " ", Num(zenny)));

            // 3. Level and experience — both live inside Status.
            float level = Number(status, "Level");
            if (!float.IsNaN(level)) parts.Add(string.Concat(LocalizedText.StatusLevel(), " ", Num(level)));

            float exp = Number(status, "Exp");
            if (!float.IsNaN(exp)) parts.Add(string.Concat(LocalizedText.StatusExp(), " ", Num(exp)));

            // 4. Skill points left to spend: total minus already used.
            var style = Record(data, "Style");
            float total = Number(style, "SkillPointTotal");
            float used = Number(style, "SkillPointUsed");
            if (!float.IsNaN(total) && !float.IsNaN(used))
                parts.Add(string.Concat(LocalizedText.StatusSkillPoint(), " ", Num(total - used)));

            // 5. The five combat stats, already localized for the Status menu.
            foreach (int type in CombatTypes)
            {
                float v = StatusValue(data, type);
                if (float.IsNaN(v)) continue;
                parts.Add(string.Concat(LocalizedText.StatLabel(type) ?? ("stat " + type), " ", Num(v)));
            }

            // 6. The two gauges.
            float focus = StatusValue(data, TYPE_FOCUS_LEVEL);
            if (!float.IsNaN(focus)) parts.Add(string.Concat(LocalizedText.StatusFocus(), " ", Num(focus)));
            float sa = StatusValue(data, TYPE_SA_LEVEL);
            if (!float.IsNaN(sa)) parts.Add(string.Concat(LocalizedText.StatusSuperArt(), " ", Num(sa)));

            // 7. Story chapter.
            float chapter = Number(Record(data, "Mission"), "mChapterNo");
            if (!float.IsNaN(chapter) && chapter > 0f)
                parts.Add(string.Format(LocalizedText.StatusChapter(), Num(chapter)));

            string line = parts.Count > 0
                ? string.Join(LocalizedText.StatusJoin(), parts)
                : LocalizedText.StatusNone();

            API.LogInfo($"[SF6Access] Status: {line}");
            ScreenReaderService.Speak(line, interrupt: true);

            // Written on every press: the field list is what tells us which
            // member really holds each number.
            string path = Dump(data, status, zenny, deep);
            if (path != null) API.LogInfo($"[SF6Access] Status dump: {path}");
        }
        catch (Exception ex)
        {
            API.LogInfo($"[SF6Access] Status readout failed: {ex.Message}");
            ScreenReaderService.Speak(LocalizedText.StatusNone(), interrupt: true);
        }
    }

    /// <summary>
    /// The wallet's real amounts. <c>get_Money()</c> / <c>get_Mile()</c> are
    /// convenience accessors that read 0 whenever the wallet is not the game's
    /// current context — measured 2026-10-06: out of twelve consecutive frames
    /// only one or two gave the real 125252, the rest gave 0 along with
    /// <c>get_SaveDataVersion()</c>, so the whole object was empty.
    ///
    /// <para>The amounts that never move are in <c>Wallet.WalletParamList</c>: a
    /// list of <c>WTWalletParam</c> records, each carrying a <c>WalletId</c>
    /// (a <c>app.worldtour.WTWalletID</c> enum value) and a <c>Num</c> (a
    /// <c>via.rds.RoseMary&lt;UInt32&gt;</c>). The id is resolved through the
    /// enum's own names rather than a guessed number, and the number is dug out
    /// of the Rose container.</para>
    /// </summary>
    /// <param name="enumNames">Enum member names that mean this currency, tried
    /// in order (e.g. Money/Zenny). Falls back to the plain wallet id when the
    /// enum cannot be resolved.</param>
    private static int WalletNum(string[] enumNames, int fallbackId)
    {
        ResolveWalletIds();

        foreach (string wanted in enumNames)
        {
            foreach (var (name, id) in WalletIds)
                if (name == wanted)
                {
                    int v = WalletNum(id);
                    if (v != int.MinValue) return v;
                }
        }
        return WalletNum(fallbackId);
    }

    /// <summary>The wallet's amount with a raw id, or MinValue.</summary>
    private static int WalletNum(int walletId)
    {
        if (walletId <= 0) return int.MinValue;
        try
        {
            var list = Record(Record(LocalPlayerData(), "Wallet"), "WalletParamList");
            int count = FlowHelper.GetListCount(list);
            for (int i = 0; i < count; i++)
            {
                var item = FlowHelper.GetListItem(list, i);
                if (item == null) continue;
                float id = Number(item, "WalletId");
                if (float.IsNaN(id) || (int)MathF.Round(id) != walletId) continue;
                float v = RoseNumber(Record(item, "Num"));
                if (float.IsNaN(v)) return int.MinValue;
                return (int)MathF.Round(v);
            }
        }
        catch { }
        return int.MinValue;
    }

    // The WTWalletID enum, resolved once: (member name, value). Its names are
    // the game's own words for what each wallet entry holds.
    private static readonly List<(string name, int id)> WalletIds = new();
    private static bool walletIdsResolved;

    /// <summary>Resolve <c>app.worldtour.WTWalletID</c> once (0..15) and log it,
    /// so the mapping is in the log even before the first press of a key.</summary>
    private static void ResolveWalletIds()
    {
        if (walletIdsResolved) return;
        walletIdsResolved = true;
        try
        {
            for (int i = 0; i < 16; i++)
            {
                string name = FlowHelper.ResolveEnumName(WALLET_ID_ENUM, i);
                if (name == null) continue;
                WalletIds.Add((name, i));
            }
            API.LogInfo($"[SF6Access] WTWalletID enum: {string.Join(", ", WalletIds)}");
        }
        catch { }
    }

    private const string WALLET_ID_ENUM = "app.worldtour.WTWalletID";

    /// <summary>The number inside a <c>via.rds.RoseMary</c> wrapper. The wrapper
    /// has no managed fields at all (measured: GetFields() is empty), so the
    /// amount is read from the raw struct memory at the offset calibrated from
    /// the Zenny entry — see <see cref="CalibrateRose"/>.</summary>
    private static float RoseNumber(ManagedObject num)
    {
        if (num == null) return float.NaN;

        // Calibrated direct read. The wallet LIST is stable save data — it is
        // there every single frame with its ids and type codes — so a number
        // read from its memory is the real amount, and a real zero reads as
        // zero here. Only the getter flickers, not this.
        if (_roseBaseKind != 0)
        {
            var baseObj = _roseBaseKind == 1 ? Record(num, "_Rose") : num;
            ulong addr = baseObj?.GetAddress() ?? 0;
            if (addr != 0)
            {
                uint? v = ReadU32At(addr + (ulong)_roseOffset);
                if (v.HasValue) return v.Value;
            }
        }

        // Uncalibrated: legacy attempts, kept for the log until calibration.
        foreach (string name in new[] { "Value", "Num", "mValue", "_Value", "Amount", "Count", "Data" })
        {
            float v = Number(num, name);
            if (!float.IsNaN(v)) return v;
        }

        var rose = Record(num, "_Rose");
        if (rose != null && rose != num)
        {
            foreach (string name in new[] { "Value", "Data", "Num", "_value", "_data", "mValue", "Buffer" })
            {
                float v = Number(rose, name);
                if (!float.IsNaN(v)) return v;
            }
            if (TryFirstGetterNumber(rose, out float fromRose)) return fromRose;
        }

        return TryFirstGetterNumber(num, out float v2) ? v2 : float.NaN;
    }

    // === raw-memory read of the wallet list amounts (v7) ===
    // via.rds.Rose carries no managed fields, so the amount only exists as
    // bytes. Where in those bytes? The Zenny getter occasionally tells the
    // truth (128252 on a good frame), and that known amount is searched for
    // in the Zenny entry's raw memory: wherever it sits is the value's fixed
    // offset for every entry, because the list is one homogeneous type.
    private static int _roseBaseKind;   // 0 = not calibrated, 1 = _Rose address, 2 = RoseMary address
    private static int _roseOffset;     // byte offset of the value inside the base

    /// <summary>Find the amount's byte offset once, using a Zenny amount the
    /// getter actually returned. Only ever called with a real positive
    /// amount, and only once — after this every wallet read goes straight to
    /// memory.</summary>
    private static void CalibrateRose(ManagedObject wallet, int knownZenny)
    {
        if (_roseBaseKind != 0 || wallet == null || knownZenny <= 0) return;
        try
        {
            var list = Record(wallet, "WalletParamList");
            int count = FlowHelper.GetListCount(list);
            for (int i = 0; i < count; i++)
            {
                var item = FlowHelper.GetListItem(list, i);
                if (item == null) continue;
                float id = Number(item, "WalletId");
                if (float.IsNaN(id) || (int)MathF.Round(id) != 1) continue;   // the Zenny entry
                var num = Record(item, "Num");
                if (num == null) continue;
                var rose = Record(num, "_Rose");
                if (rose != null && ScanRoseBytes(rose.GetAddress(), knownZenny, 1)) return;
                if (ScanRoseBytes(num.GetAddress(), knownZenny, 2)) return;
            }
            API.LogInfo($"[SF6Access] Rose calibration: no offset matched {knownZenny}");
        }
        catch (Exception ex)
        {
            API.LogInfo($"[SF6Access] Rose calibration failed: {ex.Message}");
        }
    }

    /// <summary>Scan the first bytes at <paramref name="addr"/> for the known
    /// amount; the first 4-byte-aligned match becomes the calibrated
    /// offset.</summary>
    private static bool ScanRoseBytes(ulong addr, int value, int kind)
    {
        if (addr == 0) return false;
        var buf = new byte[24];
        try { Marshal.Copy((IntPtr)(long)addr, buf, 0, buf.Length); }
        catch { return false; }
        for (int off = 0; off + 4 <= buf.Length; off += 4)
        {
            if (BitConverter.ToUInt32(buf, off) != (uint)value) continue;
            _roseBaseKind = kind;
            _roseOffset = off;
            API.LogInfo($"[SF6Access] Rose calibrated: base={(kind == 1 ? "_Rose" : "RoseMary")} offset=+{off} (value {value})");
            return true;
        }
        return false;
    }

    private static uint? ReadU32At(ulong addr)
    {
        try
        {
            var buf = new byte[4];
            Marshal.Copy((IntPtr)(long)addr, buf, 0, 4);
            return BitConverter.ToUInt32(buf, 0);
        }
        catch { return null; }
    }

    /// <summary>Whatever numeric getter the object offers, first one that
    /// returns a value.</summary>
    private static bool TryFirstGetterNumber(ManagedObject obj, out float value)
    {
        value = float.NaN;
        var td = obj?.GetTypeDefinition();
        if (td == null) return false;
        try
        {
            foreach (var m in td.GetMethods())
            {
                string name = m.Name ?? "";
                if (!name.StartsWith("get_", StringComparison.Ordinal)) continue;
                if (!IsNumeric(m.ReturnType?.FullName ?? "")) continue;
                try
                {
                    var r = m.InvokeBoxed(typeof(object), obj, Array.Empty<object>());
                    if (r == null) continue;
                    value = Convert.ToSingle(r);
                    if (!float.IsNaN(value)) return true;
                }
                catch { }
            }
        }
        catch { }
        return false;
    }

    /// <summary>Zenny from the wallet's own list — the source that does not
    /// flicker. A 0 from the old getter path means the read failed (empty
    /// wallet), not an empty purse, so it is reported as unreadable rather than
    /// spoken as zero.</summary>
    public static int ReadZennyStable()
    {
        ResolveWalletIds();
        int v = WalletNum(new[] { "Money", "Zenny" }, 1);
        if (v != int.MinValue) return v;

        int old = CurrencyReader.ReadZenny();
        if (old > 0)
        {
            // A truth the getter told: use it to locate the amount's offset in
            // the wallet list's raw memory, so future reads need no getter.
            CalibrateRose(Record(LocalPlayerData(), "Wallet"), old);
            return old;
        }
        return int.MinValue;
    }

    /// <summary>Miles ("里程") straight from the wallet — cheap enough to poll
    /// every frame.
    ///
    /// <para>MEASURED 2026-10-06: the wallet getters do not fail as "unavailable"
    /// — they return an EMPTY WALLET, so <c>get_Money()</c>, <c>get_Mile()</c>,
    /// <c>get_ContinueTicket()</c> and even <c>get_SaveDataVersion()</c> all come
    /// back 0 together, at random, roughly six frames out of ten, while the real
    /// amounts (125252 Zenny, 8897 Miles) never change. So a single read is a
    /// coin toss and 0 is a read that failed, not an empty purse.</para></summary>
    public static int ReadMileFast()
    {
        ResolveWalletIds();
        int stable = WalletNum(new[] { "Mile", "Miles", "Mileage" }, 2);
        if (stable != int.MinValue) return stable;
        try
        {
            var wallet = Record(LocalPlayerData(), "Wallet");
            foreach (string name in MileNames)
            {
                float v = Number(wallet, name);
                // 0 from the wallet getters is the empty-wallet read, not a
                // mileage of zero — treat it as unreadable.
                if (!float.IsNaN(v) && v > 0f) return (int)MathF.Round(v);
            }
        }
        catch { }
        return int.MinValue;
    }

    /// <summary>Miles with the whole player record searched as a last resort.
    /// The search walks the field tree, so it is for once per press, never for
    /// a per-frame poll.</summary>
    public static int ReadMile()
    {
        int fast = ReadMileFast();
        if (fast != int.MinValue) return fast;

        float scanned = ScanNumber(LocalPlayerData(), MileWords, 3, out string where);
        if (float.IsNaN(scanned)) return int.MinValue;
        API.LogInfo($"[SF6Access] Miles found at {where} = {Num(scanned)}");
        return (int)MathF.Round(scanned);
    }
    /// <summary>Names the Miles counter might carry in the wallet.</summary>
    private static readonly string[] MileNames = { "Mile", "Miles", "Mileage", "MilePoint", "MileNum" };

    /// <summary>What a "miles" member's name contains — the search fallback.</summary>
    private static readonly string[] MileWords = { "mile" };

    /// <summary>First numeric member, anywhere in the record down to
    /// <paramref name="maxDepth"/>, whose name contains one of the words. Used
    /// when a number the player can see has no known address.</summary>
    private static float ScanNumber(ManagedObject obj, string[] words, int maxDepth, out string where)
        => ScanNumber(obj, words, 0, maxDepth, "", out where);

    private static float ScanNumber(ManagedObject obj, string[] words, int depth, int maxDepth,
                                    string path, out string where)
    {
        where = null;
        if (obj == null || depth > maxDepth) return float.NaN;
        var td = obj.GetTypeDefinition();
        if (td == null) return float.NaN;

        try
        {
            foreach (var field in td.GetFields())
            {
                string name = StripBacking(field.Name ?? "?");
                string typeName = field.Type?.FullName ?? "?";
                string here = path.Length == 0 ? name : path + "." + name;
                try
                {
                    if (IsNumeric(typeName))
                    {
                        if (!ContainsAny(name, words)) continue;
                        object raw = field.GetDataBoxed(typeof(object), obj.GetAddress(), field.IsStatic());
                        if (raw == null) continue;
                        where = here;
                        return Convert.ToSingle(raw);
                    }
                    if (typeName.StartsWith("app.", StringComparison.Ordinal) &&
                        !typeName.EndsWith("[]", StringComparison.Ordinal) && depth < maxDepth)
                    {
                        var child = Record(obj, name);
                        if (child == null) continue;
                        float v = ScanNumber(child, words, depth + 1, maxDepth, here, out where);
                        if (!float.IsNaN(v)) return v;
                    }
                }
                catch { }
            }
        }
        catch { }
        return float.NaN;
    }

    /// <summary>The avatar's own player data record, or null outside World Tour.</summary>
    private static ManagedObject LocalPlayerData()
    {
        try
        {
            var mgr = API.GetManagedSingleton(PLAYER_MANAGER) as ManagedObject;
            return mgr == null ? null : Record(mgr, "LocalPlayerData");
        }
        catch { return null; }
    }

    // ==================== reads ====================

    /// <summary>A managed record held by <paramref name="owner"/>: the backing
    /// field when there is one, otherwise the getter — and the getter is only
    /// invoked when its declared return type is a managed reference we can
    /// hold (never an arbitrary one).</summary>
    private static ManagedObject Record(ManagedObject owner, string name)
    {
        if (owner == null) return null;
        try
        {
            var td = owner.GetTypeDefinition();
            var field = td?.GetField($"<{name}>k__BackingField") ?? td?.GetField(name);
            if (field != null)
                return field.GetDataBoxed(typeof(object), owner.GetAddress(), false) as ManagedObject;

            var getter = td?.GetMethod("get_" + name);
            if (getter != null && (getter.ReturnType?.FullName ?? "").StartsWith("app.", StringComparison.Ordinal))
                return getter.InvokeBoxed(typeof(object), owner, Array.Empty<object>()) as ManagedObject;
        }
        catch { }
        return null;
    }

    /// <summary>A numeric member: the field first, then the getter — and the
    /// getter is only invoked when its declared return type is a number.</summary>
    private static float Number(ManagedObject obj, string name)
    {
        if (obj == null) return float.NaN;
        try
        {
            var td = obj.GetTypeDefinition();
            var field = td?.GetField($"<{name}>k__BackingField") ?? td?.GetField(name);
            if (field != null && IsNumeric(field.Type?.FullName ?? ""))
                return Convert.ToSingle(field.GetDataBoxed(typeof(object), obj.GetAddress(), false));

            var getter = td?.GetMethod("get_" + name);
            if (getter != null && IsNumeric(getter.ReturnType?.FullName ?? ""))
            {
                var r = getter.InvokeBoxed(typeof(object), obj, Array.Empty<object>());
                if (r != null) return Convert.ToSingle(r);
            }
        }
        catch { }
        return float.NaN;
    }

    /// <summary>One entry of the computed status array by index
    /// (<c>Status.CalcStatus.GetValue(type)</c>), NaN when unavailable.</summary>
    private static float StatusValue(ManagedObject data, int type)
    {
        var calc = CalcStatus(data);
        if (calc == null) return float.NaN;
        try
        {
            var method = calc.GetTypeDefinition()?.GetMethod(GET_VALUE_SIG);
            if (method == null) return float.NaN;
            var r = method.InvokeBoxed(typeof(float), calc, new object[] { type });
            return r == null ? float.NaN : Convert.ToSingle(r);
        }
        catch { return float.NaN; }
    }

    private static ManagedObject CalcStatus(ManagedObject data)
    {
        var status = Record(data, "Status");
        return status == null ? null : Record(status, "CalcStatus");
    }

    /// <summary>Every entry of the computed status array as (enum name, value) —
    /// for the dump, where seeing all of them is the point.</summary>
    private static List<(string name, float value)> StatusEntries(ManagedObject data)
    {
        var result = new List<(string, float)>();
        var calc = CalcStatus(data);
        if (calc == null) return result;
        try
        {
            var method = calc.GetTypeDefinition()?.GetMethod(GET_VALUE_SIG);
            if (method == null) return result;
            for (int type = 0; type < 24; type++)
            {
                float v;
                try
                {
                    var r = method.InvokeBoxed(typeof(float), calc, new object[] { type });
                    if (r == null) continue;
                    v = Convert.ToSingle(r);
                }
                catch { continue; }
                result.Add((FlowHelper.ResolveEnumName(STATUS_TYPE_ENUM, type) ?? $"type{type}", v));
            }
        }
        catch { }
        return result;
    }

    private static bool IsNumeric(string typeName) =>
        typeName == "System.Int32" || typeName == "System.UInt32" ||
        typeName == "System.Int16" || typeName == "System.UInt16" ||
        typeName == "System.Byte" || typeName == "System.SByte" ||
        typeName == "System.Single" || typeName == "System.Double" ||
        typeName == "System.Int64" || typeName == "System.UInt64";

    private static string Num(float v) =>
        ((int)MathF.Round(v)).ToString(CultureInfo.InvariantCulture);

    // ==================== diagnostic ====================

    /// <summary>Write what the player data actually contains, so the spoken line
    /// can be checked against the source — including every numeric getter of the
    /// wallet, which is where the money really is.</summary>
    private static string Dump(ManagedObject data, ManagedObject status, int zenny, bool deep)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"=== SF6 MY STATUS - {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
            sb.AppendLine($"LocalPlayerData: {(data == null ? "null" : data.GetTypeDefinition()?.FullName)}");
            sb.AppendLine($"Status:          {(status == null ? "null" : status.GetTypeDefinition()?.FullName)}");
            sb.AppendLine();

            if (status != null)
            {
                sb.AppendLine("--- STATUS FIELDS (numbers) ---");
                DumpFields(sb, status, "  ", 0, 1);
                sb.AppendLine();
            }

            sb.AppendLine("--- STATUS ARRAY ---");
            foreach (var (name, value) in StatusEntries(data))
                sb.AppendLine($"  {name} = {value:0.##}");
            sb.AppendLine();

            // The wallet has no fields at all — the money is only reachable
            // through its getters, so those are listed every time.
            var wallet = Record(data, "Wallet");
            sb.AppendLine("--- WALLET ---");
            sb.AppendLine($"  object: {(wallet == null ? "null" : wallet.GetTypeDefinition()?.FullName)}");
            if (wallet != null)
            {
                // Fields too, not only the getters: if the amounts have a
                // backing field anywhere, reading it beats polling a getter
                // that hands back an empty wallet six frames in ten.
                DumpAny(sb, wallet, "  ", 0, 1);
                DumpGetters(sb, wallet, "  ");
                DumpWalletList(sb, wallet, "  ");
            }
            sb.AppendLine();

            if (data != null)
            {
                sb.AppendLine("--- PLAYER DATA FIELDS (numbers) ---");
                DumpFields(sb, data, "  ", 0, 2);
                sb.AppendLine();

                if (deep)
                {
                    sb.AppendLine("--- NUMERIC GETTERS OF PLAYER DATA ---");
                    DumpGetters(sb, data, "  ");
                    sb.AppendLine();

                    // What the game CALLS the thing that revives you. The
                    // player asked whether the number we speak is really the one
                    // a revive spends, and type names are the only witness that
                    // is available without letting the avatar die: the flow that
                    // handles reviving, and the wallet's own spend methods.
                    sb.AppendLine("--- TYPES: CONTINUE / WALLET ---");
                    DumpTdbTypes(sb, new[] { "WorldTourContinue", "WTPlayerDataWallet", "WTWallet", "WTContinue" });
                    sb.AppendLine();

                    // The wallet amount lives in a via.rds.RoseMary wrapper whose
                    // value sits inside a via.rds.Rose — its member list is what
                    // the next deep press needs to read the number directly
                    // instead of through the flickering getter.
                    sb.AppendLine("--- TYPES: via.rds.Rose (wallet Num container) ---");
                    DumpTdbTypes(sb, new[] { "via.rds.Rose" });
                    sb.AppendLine();

                    sb.AppendLine("--- ON-SCREEN TEXTS (revive / mileage) ---");
                    DumpScreenWords(sb, new[] { "里程", "复活", "继续", "券", "mile", "continue" });
                    sb.AppendLine();
                }
            }

            sb.AppendLine("--- TRAVEL / DISTANCE PROBE ---");
            if (data != null) DumpNamed(sb, data, TravelWords, "  ", 0, 3, "");
            else sb.AppendLine("  [no player data]");
            sb.AppendLine();

            // The player also asked for "the thing used to revive", and no field
            // name for it is known either — same probe, different words.
            sb.AppendLine("--- REVIVE / HEAL PROBE ---");
            if (data != null) DumpNamed(sb, data, ReviveWords, "  ", 0, 3, "");
            else sb.AppendLine("  [no player data]");
            sb.AppendLine();

            sb.AppendLine("--- ITEM RECORD ---");
            var item = data == null ? null : Record(data, "Item");
            if (item != null) DumpAny(sb, item, "  ", 0, 2);
            else sb.AppendLine("  [no item record]");
            sb.AppendLine();

            sb.AppendLine("--- CANDIDATE READS ---");
            sb.AppendLine($"  health  = {(float.IsNaN(Number(status, "VitalNow")) ? "(none)" : Num(Number(status, "VitalNow")))}" +
                          $" / max {(float.IsNaN(StatusValue(data, TYPE_VITALITY)) ? "(none)" : Num(StatusValue(data, TYPE_VITALITY)))}");
            sb.AppendLine($"  zenny   = {(zenny == int.MinValue ? "(none)" : zenny.ToString(CultureInfo.InvariantCulture))}" +
                          $"   (fresh read: {(CurrencyReader.ReadZenny() == int.MinValue ? "(none)" : CurrencyReader.ReadZenny().ToString(CultureInfo.InvariantCulture))})");
            sb.AppendLine($"  level   = {(float.IsNaN(Number(status, "Level")) ? "(none)" : Num(Number(status, "Level")))}");
            sb.AppendLine($"  exp     = {(float.IsNaN(Number(status, "Exp")) ? "(none)" : Num(Number(status, "Exp")))}");
            sb.AppendLine();

            string path = Path.Combine(ObjectDumper.DumpDir, "sf6access_playerstatus.txt");
            File.WriteAllText(path, sb.ToString());
            return path;
        }
        catch (Exception ex)
        {
            API.LogInfo($"[SF6Access] Status dump failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Words that mean "how far the avatar has travelled" — the player
    /// asked for the mileage, and no field name for it is known, so every
    /// member whose name contains one of these is listed with its value.</summary>
    private static readonly string[] TravelWords =
        { "distance", "mile", "walk", "travel", "move", "step", "run", "meter", "metre", "km" };

    /// <summary>Words that mean "the thing used to revive" — the player asked
    /// for it by that description, not by a name, so every member whose name
    /// contains one of these is listed with its value.</summary>
    private static readonly string[] ReviveWords =
        { "revive", "revival", "resurrect", "heal", "cure", "recover", "medical",
          "medicine", "kit", "rebirth", "revival", "continue", "drug", "potion" };

    /// <summary>Every wallet entry with its id and the number inside it, so the
    /// id-to-currency mapping (1 Zenny, 2 Miles, 3 continue tickets) can be
    /// checked against what the player actually has.</summary>
    private static void DumpWalletList(StringBuilder sb, ManagedObject wallet, string indent)
    {
        try
        {
            var list = Record(wallet, "WalletParamList");
            int count = FlowHelper.GetListCount(list);
            sb.AppendLine($"{indent}WalletParamList = {count}");
            for (int i = 0; i < count && i < 8; i++)
            {
                var item = FlowHelper.GetListItem(list, i);
                if (item == null) continue;
                float id = Number(item, "WalletId");
                var num = Record(item, "Num");
                sb.AppendLine($"{indent}  [{i}] WalletId = {(float.IsNaN(id) ? "?" : Num(id))}" +
                              $"  Num = {(num == null ? "null" : (float.IsNaN(RoseNumber(num)) ? "(unreadable)" : Num(RoseNumber(num))))}" +
                              $"  ({num?.GetTypeDefinition()?.FullName ?? "?"})");
                if (num != null)
                {
                    // Every field of the wrapper: its value is behind a type the
                    // numeric-only dump refuses to walk into.
                    foreach (var f in num.GetTypeDefinition()?.GetFields())
                    {
                        try
                        {
                            object raw = f.GetDataBoxed(typeof(object), num.GetAddress(), f.IsStatic());
                            sb.AppendLine($"{indent}      {f.Type?.FullName} {StripBacking(f.Name)} = {raw?.ToString() ?? "null"}");
                        }
                        catch { }
                    }
                    DumpGetters(sb, num, indent + "      ");

                    // The ground truth: the raw bytes of the wrapper and of its
                    // Rose container. The list is stable save data, so these
                    // bytes hold the real amounts — laid out per 4 bytes here,
                    // three entries side by side, the id-to-currency mapping
                    // reads itself off this section.
                    AppendRaw(sb, $"{indent}      RoseMary bytes:", num.GetAddress(), 32);
                    var rose = Record(num, "_Rose");
                    if (rose != null)
                        AppendRaw(sb, $"{indent}      _Rose bytes:", rose.GetAddress(), 24);
                    else
                        sb.AppendLine($"{indent}      _Rose: null");
                }
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"{indent}[wallet list error: {ex.Message}]");
        }
    }

    /// <summary>Raw memory at <paramref name="addr"/> as 4-byte words, address
    /// and all — the bytes ARE the wallet's amounts, so they go into every
    /// dump for checking against what was spoken.</summary>
    private static void AppendRaw(StringBuilder sb, string title, ulong addr, int len)
    {
        sb.AppendLine(title);
        if (addr == 0) { sb.AppendLine("        [null address]"); return; }
        try
        {
            var buf = new byte[len];
            Marshal.Copy((IntPtr)(long)addr, buf, 0, len);
            for (int i = 0; i + 4 <= len; i += 4)
            {
                uint w = BitConverter.ToUInt32(buf, i);
                sb.AppendLine($"        +{i,2} = {w,12} (0x{w:X8})");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"        [read failed: {ex.Message}]");
        }
    }

    /// <summary>Every type whose name matches, with its field and method
    /// SIGNATURES — never invoked, so this is safe to run anywhere. Names are
    /// how the game tells us what a number is for: the revive flow and the
    /// wallet's spend methods name the currency they consume.</summary>
    private static void DumpTdbTypes(StringBuilder sb, string[] keywords)
    {
        try
        {
            var tdb = TDB.Get();
            uint total = tdb.GetNumTypes();
            int printed = 0;
            for (uint i = 0; i < total && printed < 24; i++)
            {
                var td = tdb.GetType(i);
                string full = td?.FullName;
                if (string.IsNullOrEmpty(full) || !ContainsAny(full, keywords)) continue;
                printed++;
                sb.AppendLine($"  ## {full}");
                try
                {
                    foreach (var f in td.GetFields())
                        sb.AppendLine($"      {f.Type?.FullName} {StripBacking(f.Name ?? "?")}");
                }
                catch { }
                try
                {
                    int methods = 0;
                    foreach (var m in td.GetMethods())
                    {
                        if (methods++ > 60) { sb.AppendLine("      ..."); break; }
                        var ps = m.GetParameters();
                        var p = new StringBuilder();
                        if (ps != null)
                            for (int k = 0; k < ps.Count; k++)
                            {
                                if (k > 0) p.Append(", ");
                                p.Append($"{ps[k].Type?.FullName ?? "?"}");
                            }
                        sb.AppendLine($"      {m.ReturnType?.FullName ?? "void"} {m.Name}({p})");
                    }
                }
                catch { }
                sb.AppendLine();
            }
            if (printed == 0) sb.AppendLine("  [no type matched]");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  [TDB scan failed: {ex.Message}]");
        }
    }

    /// <summary>On-screen texts that mention mileage or reviving — the game's
    /// own wording for whatever this number is, read where the player can see
    /// it.</summary>
    private static void DumpScreenWords(StringBuilder sb, string[] words)
    {
        try
        {
            foreach (var t in GuiTextReader.ReadSceneTexts(visibleOnly: false))
            {
                string text = t.Text ?? t.Raw ?? "";
                if (!ContainsAny(t.Name ?? "", words) && !ContainsAny(text, words)) continue;
                sb.AppendLine($"  [{t.Owner ?? "?"}] {t.Name} = {text}");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  [screen read failed: {ex.Message}]");
        }
    }

    /// <summary>Every field of a record, whatever its type, with lists reported
    /// as their length — for records whose shape is completely unknown (the
    /// inventory), where a numeric-only dump shows nothing at all.</summary>
    private static void DumpAny(StringBuilder sb, ManagedObject obj, string indent, int depth, int maxDepth)
    {
        if (obj == null || depth > maxDepth) return;
        var td = obj.GetTypeDefinition();
        if (td == null) return;

        try
        {
            foreach (var field in td.GetFields())
            {
                string name = StripBacking(field.Name ?? "?");
                string typeName = field.Type?.FullName ?? "?";
                try
                {
                    if (field.IsStatic()) continue;
                    object raw = field.GetDataBoxed(typeof(object), obj.GetAddress(), false);
                    int count = FlowHelper.GetListCount(raw as ManagedObject);
                    if (count > 0)
                    {
                        sb.AppendLine($"{indent}{typeName} {name} = list of {count}");
                        var first = FlowHelper.GetListItem(raw as ManagedObject, 0);
                        if (first != null)
                            DumpAny(sb, first, indent + "  ", depth + 1, maxDepth);
                        continue;
                    }
                    string value = raw?.ToString() ?? "null";
                    if (value.Length > 80) value = value.Substring(0, 80) + "...";
                    if (raw is ManagedObject child)
                    {
                        sb.AppendLine($"{indent}{typeName} {name}:");
                        if (depth < maxDepth && typeName.StartsWith("app.", StringComparison.Ordinal))
                            DumpAny(sb, child, indent + "  ", depth + 1, maxDepth);
                    }
                    else
                    {
                        sb.AppendLine($"{indent}{typeName} {name} = {value}");
                    }
                }
                catch { }
            }
        }
        catch { }
    }

    /// <summary>List every member, at any depth, whose NAME contains one of the
    /// given words, with the path to it and its value. This is how a number the
    /// player can see but we cannot locate gets found.</summary>
    private static void DumpNamed(StringBuilder sb, ManagedObject obj, string[] words,
                                  string indent, int depth, int maxDepth, string path)
    {
        if (obj == null || depth > maxDepth) return;
        var td = obj.GetTypeDefinition();
        if (td == null) return;

        try
        {
            foreach (var field in td.GetFields())
            {
                string name = StripBacking(field.Name ?? "?");
                string typeName = field.Type?.FullName ?? "?";
                string here = path.Length == 0 ? name : path + "." + name;
                try
                {
                    if (IsNumeric(typeName))
                    {
                        if (!ContainsAny(name, words)) continue;
                        object raw = field.GetDataBoxed(typeof(object), obj.GetAddress(), field.IsStatic());
                        sb.AppendLine($"{indent}{here} ({typeName}) = {raw?.ToString() ?? "null"}");
                    }
                    else if (typeName.StartsWith("app.", StringComparison.Ordinal) &&
                             !typeName.EndsWith("[]", StringComparison.Ordinal) && depth < maxDepth)
                    {
                        var child = Record(obj, name);
                        if (child == null) continue;
                        // Descend always — the interesting field may be two
                        // records down and only ITS name carries the keyword.
                        DumpNamed(sb, child, words, indent, depth + 1, maxDepth, here);
                    }
                }
                catch { }
            }
        }
        catch { }
    }

    /// <summary>Numeric fields of an object and, a level or two down, of the
    /// managed records it owns.</summary>
    private static void DumpFields(StringBuilder sb, ManagedObject obj, string indent, int depth, int maxDepth)
    {
        if (obj == null || depth > maxDepth) return;
        var td = obj.GetTypeDefinition();
        if (td == null) return;

        try
        {
            foreach (var field in td.GetFields())
            {
                string name = field.Name ?? "?";
                string typeName = field.Type?.FullName ?? "?";
                try
                {
                    if (IsNumeric(typeName))
                    {
                        object raw = field.GetDataBoxed(typeof(object), obj.GetAddress(), field.IsStatic());
                        sb.AppendLine($"{indent}{typeName} {StripBacking(name)} = {raw?.ToString() ?? "null"}");
                    }
                    else if (typeName.StartsWith("app.", StringComparison.Ordinal) &&
                             !typeName.EndsWith("[]", StringComparison.Ordinal) && depth < maxDepth)
                    {
                        var child = Record(obj, StripBacking(name));
                        if (child == null) continue;
                        sb.AppendLine($"{indent}{typeName} {StripBacking(name)}:");
                        DumpFields(sb, child, indent + "  ", depth + 1, maxDepth);
                    }
                }
                catch { }
            }
        }
        catch { }
    }

    /// <summary>Every getter whose declared return type is a number, with what it
    /// returns — the safe half of a member scan, and the only way into the
    /// getter-only properties (the wallet's money).</summary>
    private static void DumpGetters(StringBuilder sb, ManagedObject obj, string indent)
    {
        var td = obj?.GetTypeDefinition();
        if (td == null) return;
        try
        {
            foreach (var m in td.GetMethods())
            {
                string name = m.Name ?? "";
                if (!name.StartsWith("get_", StringComparison.Ordinal)) continue;
                if (!IsNumeric(m.ReturnType?.FullName ?? "")) continue;
                try
                {
                    var r = m.InvokeBoxed(typeof(object), obj, Array.Empty<object>());
                    sb.AppendLine($"{indent}{name}() = {r?.ToString() ?? "null"}");
                }
                catch { }
            }
        }
        catch { }
    }

    private static bool ContainsAny(string name, string[] words)
    {
        foreach (string w in words)
            if (name.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    /// <summary><c>&lt;Hp&gt;k__BackingField</c> → <c>Hp</c>; other names unchanged.</summary>
    private static string StripBacking(string name)
    {
        if (name.StartsWith("<", StringComparison.Ordinal) && name.EndsWith(">k__BackingField", StringComparison.Ordinal))
            return name.Substring(1, name.Length - 17);
        return name;
    }
}
