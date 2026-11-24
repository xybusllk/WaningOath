using System.Text.Json;
using WaningOath.Core.Data;

namespace WaningOath.Core.Progression;

/// <summary>一件装备的实例（只含**养成状态**，静态规则在 `data/tables/gear.json`）。</summary>
public sealed record GearPiece(
    string Uid,
    string SetId,
    string Part,
    int Level,
    string MainStat,
    IReadOnlyList<(string Stat, double Value)> SubStats);

/// <summary>
/// 一个角色**身上穿戴的全部东西**。
/// </summary>
/// <remarks>
/// 这是 `M2` 的 <see cref="TierPanel"/>（锚点插值出来的目标面板）在 `M7` 的替代品：
/// 同一件事（"这个角色现在是什么面板"）从"按投入档位猜"变成"按实际配装算"。
/// </remarks>
public sealed record Loadout(
    int Level = 1,
    int Breakthrough = 0,
    int Eidolon = 0,
    string? WeaponId = null,
    int WeaponLevel = 80,
    int WeaponOverwrite = 1,
    IReadOnlyList<GearPiece>? Gears = null)
{
    public IReadOnlyList<GearPiece> Pieces => Gears ?? [];
}

/// <summary>面板的逐项来源（给界面显示与自测断言用）。</summary>
public sealed record PanelLine(string Source, string Stat, string Op, double Value)
{
    public override string ToString() => $"{Source,-26} {Stat,-10} {Op,-4} {Value,10:0.####}";
}

/// <summary>折算结果：最终面板 + 逐项明细 + 这一轮**没有折算进面板**的特效键。</summary>
public sealed record PanelResult(BaseStatsDef Panel, IReadOnlyList<PanelLine> Breakdown, IReadOnlyList<string> Unfolded)
{
    public override string ToString()
        => $"攻 {Panel.Atk:0} · 生 {Panel.Hp:0} · 防 {Panel.Def:0} · 速 {Panel.Speed:0} · " +
           $"暴击 {Panel.CritRate:P1} / 面板暴伤 {Panel.CritDmg:0.00} · 能量上限 {Panel.EnergyMax:0}";
}

/// <summary>
/// **把配装折成面板**（`02 §1.1` / `§5.3` / `§6.3` / `§6.4`）。
/// </summary>
/// <remarks>
/// <b>这是 `M7` 的核心那一条链</b>：存档里只有"几级 / 哪把武器 / 哪几件装备"，
/// 面板必须由规则**算出来**，不能存进存档 —— 否则玩家改一个数字就能得到任意面板，
/// 而且服务器重放战斗时会与客户端分叉（`06 §2.3`）。
/// <para>
/// 口径（全部来自 `02`）：
/// <list type="number">
///   <item><b>角色基础</b>：1 级裸面板 × `1.01^(等级-1)`（`§1.1`）；**速度不随等级成长**（`§1.2`）。</item>
///   <item><b>突破</b>：每次突破给 `growth.breakthroughBonus` 里的百分比（按定位差异化，`§3`）。</item>
///   <item><b>武器</b>：主属性（%）与副属性（固定值）**都随武器等级成长**，满级值见 `§5.3`；
///         特效只随权限覆写成长、且**仅当角色适用武器类型 == 武器类型**时生效（"类型契合"）。</item>
///   <item><b>装备</b>：主属性按 `§6.3` 的 +15 值线性插值；副属性是**已经 roll 好的值**（存档存结果）。</item>
///   <item><b>合成</b>：`(基础 + 全部固定值) × (1 + 全部百分比之和)`。</item>
/// </list>
/// </para>
/// <para>
/// ⚠️ <b>只折算"面板类"属性。</b> 武器的五段特效里有一大半是**进战斗才生效**的东西
/// （属性增伤、异常积蓄效率、`oathCap` 这类机制旋钮）。它们**不进面板**，
/// 这里把它们收集到 <see cref="PanelResult.Unfolded"/> 里报出来，而不是悄悄丢掉 ——
/// 「写得进数据、算不出来、还不报错」正是本项目最贵的一类 bug。
/// </para>
/// </remarks>
public static class PanelCalculator
{
    /// <summary>每级 +1% 复利（`02 §1.1`）：`1.01^(n-1)`。</summary>
    public static double LevelGrowth(int level) => Math.Pow(1.01, Math.Max(0, level - 1));

    /// <summary>
    /// 面板类属性键 → 归到哪一个面板项、是不是百分比。
    /// </summary>
    /// <remarks>
    /// 这是**数据里允许出现的键**的白名单。武器特效的段内键名由各武器自定
    /// （`101`：`critRateBonus` / `windDmgBonus` …），所以必须有一层"语义名 → 面板项"的翻译，
    /// 否则"写了却没生效"永远不会有人发现。
    /// </remarks>
    private static readonly Dictionary<string, (PanelKey Key, bool IsPct)> PanelKeys = new(StringComparer.Ordinal)
    {
        ["atk"] = (PanelKey.Atk, false),
        ["atkFlat"] = (PanelKey.Atk, false),
        ["atkPct"] = (PanelKey.Atk, true),
        ["hp"] = (PanelKey.Hp, false),
        ["hpFlat"] = (PanelKey.Hp, false),
        ["hpPct"] = (PanelKey.Hp, true),
        ["def"] = (PanelKey.Def, false),
        ["defFlat"] = (PanelKey.Def, false),
        ["defPct"] = (PanelKey.Def, true),
        ["speed"] = (PanelKey.Speed, false),
        ["critRate"] = (PanelKey.CritRate, true),
        ["critRateBonus"] = (PanelKey.CritRate, true),
        ["critDmg"] = (PanelKey.CritDmg, true),
        ["critDmgBonus"] = (PanelKey.CritDmg, true),
        ["energyRegen"] = (PanelKey.EnergyRegen, true),
        ["energyRegenBonus"] = (PanelKey.EnergyRegen, true),
        ["healBonus"] = (PanelKey.HealBonus, true),
        ["shieldBonus"] = (PanelKey.ShieldBonus, true),
    };

    private enum PanelKey { None = 0, Atk, Hp, Def, Speed, CritRate, CritDmg, EnergyRegen, HealBonus, ShieldBonus }

    public static PanelResult Compute(GameData data, CharacterDef def, Loadout loadout)
    {
        List<PanelLine> lines = [];
        List<string> unfolded = [];
        Dictionary<PanelKey, double> flat = [];
        Dictionary<PanelKey, double> pct = [];

        void Apply(string key, double value, string source)
        {
            if (!PanelKeys.TryGetValue(key, out (PanelKey Key, bool IsPct) info))
            {
                unfolded.Add($"{source} · {key} = {value:0.####}");
                return;
            }
            Dictionary<PanelKey, double> box = info.IsPct ? pct : flat;
            box[info.Key] = box.GetValueOrDefault(info.Key) + value;
            lines.Add(new PanelLine(source, info.Key.ToString(), info.IsPct ? "Pct" : "Add", value));
        }

        // ── ① 角色基础（等级成长 = 额外 +100%×(g-1) 的固定值，等价于 1 级值 × g）──
        double g = LevelGrowth(loadout.Level);
        Apply("atk", def.Base.Atk * (g - 1.0), "角色等级成长");
        Apply("hp", def.Base.Hp * (g - 1.0), "角色等级成长");
        Apply("def", def.Base.Def * (g - 1.0), "角色等级成长");

        // ── ② 突破 ────────────────────────────────────────────────────
        int bt = Math.Max(0, loadout.Breakthrough);
        if (bt > 0)
        {
            foreach (KeyValuePair<string, double> kv in def.Growth.BreakthroughBonus)
                Apply(NormalizeStatName(kv.Key), kv.Value * bt, $"突破 ×{bt}");
        }

        // ── ③ 武器 ────────────────────────────────────────────────────
        if (loadout.WeaponId is not null && data.Weapon(loadout.WeaponId) is { } w)
        {
            // 主/副属性随武器等级成长：满级值 × (1.01^(L-1) / 1.01^79)。
            double wg = LevelGrowth(loadout.WeaponLevel) / LevelGrowth(80);
            if (w.MainStat is { } main)
                Apply(NormalizeStatName(main.Kind) + (main.Op == "Pct" ? "Pct" : "Flat"), main.Value * wg, $"武器主属性·{w.Name}");
            if (w.SubStat is { } sub)
                Apply(NormalizeStatName(sub.Kind) + (sub.Op == "Pct" ? "Pct" : "Flat"), sub.Value * wg, $"武器副属性·{w.Name}");

            // 特效：**只有标记为"常驻"的那一部分进面板**。
            //
            // ⚠️ 五段特效（`tiers`）**一律不折进面板** —— 它们全是**有条件的战斗内增益**
            //    （守约人"释放终结技后 2 回合"、星遥"释放追加攻击后"、爱丽丝"召唤共生灵后"）。
            //    把它们当常驻面板会让"大毕业攻击"凭空多出两三成，而正确的归属是
            //    M7 的 `modifyStat` 原子在战斗里挂状态。⇒ 这里**报出来但不计入**，
            //    因为"写得进数据、算不出来、还不报错"正是本项目最贵的一类 bug。
            if (w.Effect is { } eff)
            {
                if (!TypeMatches(def, w))
                {
                    unfolded.Add($"武器特效·{w.Name}：**类型不契合**（角色 {def.WeaponType} ≠ 武器 {w.WeaponType}）⇒ 未生效");
                }
                else
                {
                    int rank = Math.Clamp(loadout.WeaponOverwrite, 1, Math.Max(1, eff.Tiers.Count));
                    WeaponTierDef? tier = eff.Tiers.FirstOrDefault(t => t.Rank == rank);
                    if (tier is not null)
                    {
                        foreach (KeyValuePair<string, JsonElement> kv in tier.Values)
                        {
                            if (kv.Value.ValueKind != JsonValueKind.Number) continue;
                            unfolded.Add($"武器特效·{w.Name}@覆写{rank} · {kv.Key} = {kv.Value.GetDouble():0.####}（战斗内生效）");
                        }
                    }
                    // 常驻部分（例：不落星辰的能量恢复效率 +20%）**是面板项**。
                    foreach (KeyValuePair<string, double> kv in eff.Constant)
                        Apply(kv.Key, kv.Value, $"武器特效常驻·{w.Name}");
                }
            }
        }

        // ── ④ 装备 ────────────────────────────────────────────────────
        GearTable gear = data.Gear;
        foreach (GearPiece piece in loadout.Pieces)
        {
            string label = $"{piece.Part}·{piece.SetId}";
            if (!string.IsNullOrEmpty(piece.MainStat)
                && gear.MainStats.TryGetValue(piece.MainStat, out GearMainStatDef? msDef))
            {
                string k = msDef.Kind == "pct" ? piece.MainStat : piece.MainStat;
                Apply(k, gear.MainStatAt(piece.MainStat, piece.Level), $"{label} 主属性+{piece.Level}");
            }
            foreach ((string stat, double value) in piece.SubStats)
                Apply(stat, value, label + " 副属性");
        }

        // ── ⑤ 合成：`(基础 + 固定值) × (1 + 百分比之和)` ──────────────
        BaseStatsDef panel = new()
        {
            Atk = Mix(def.Base.Atk, PanelKey.Atk),
            Hp = Mix(def.Base.Hp, PanelKey.Hp),
            Def = Mix(def.Base.Def, PanelKey.Def),
            // 速度不随等级成长（02 §1.2），只吃固定值。
            Speed = def.Base.Speed + flat.GetValueOrDefault(PanelKey.Speed),
            CritRate = BattleConstants.CritRateBase + pct.GetValueOrDefault(PanelKey.CritRate),
            CritDmg = BattleConstants.CritDmgBase + pct.GetValueOrDefault(PanelKey.CritDmg),
            EnergyMax = def.Base.EnergyMax,
            EnergyOverflowCap = def.Base.EnergyOverflowCap,
            Threat = def.Base.Threat,
        };

        double Mix(double baseValue, PanelKey k)
            => (baseValue + flat.GetValueOrDefault(k)) * (1.0 + pct.GetValueOrDefault(k));

        return new PanelResult(panel, lines, unfolded);
    }

    /// <summary>武器数据里的 `kind` 写成 `Atk` / `Hp` / `CritRate`，装备池写成 `atkPct` / `critRate`。这里统一。</summary>
    private static string NormalizeStatName(string key) => key.Trim() switch
    {
        "Atk" => "atk",
        "Hp" => "hp",
        "Def" => "def",
        "Speed" => "speed",
        "CritRate" => "critRate",
        "CritDmg" => "critDmg",
        "EnergyRegen" => "energyRegen",
        "HealBonus" => "healBonus",
        "ShieldBonus" => "shieldBonus",
        _ => key.Trim(),
    };

    /// <summary>武器特效的"类型契合"（`02 §5.3`）：角色适用武器类型 == 武器类型。</summary>
    public static bool TypeMatches(CharacterDef def, WeaponDef weapon)
        => string.Equals(def.WeaponType?.Trim(), weapon.WeaponType?.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>这个键会不会被折进面板（供 `M3` 校验判断"写了却没生效"）。</summary>
    public static bool IsPanelKey(string key) => PanelKeys.ContainsKey(key);
}
