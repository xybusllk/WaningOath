using WaningOath.Core.Data;

namespace WaningOath.Core.Progression;

/// <summary>一个单位在某个投入档位下的**目标面板**。</summary>
public sealed record TierPanel(
    double Atk,
    double Hp,
    double Def,
    double Speed,
    double CritRate,
    double CritDmg)
{
    public override string ToString()
        => $"攻 {Atk:0} · 生命 {Hp:0} · 防 {Def:0} · 速 {Speed:0} · 暴击 {CritRate:P0} / 面板暴伤 {CritDmg:0.00}";
}

/// <summary>
/// 把「投入档位 × 定位锚点行 × 角色基础值」折算成一份具体面板。
/// </summary>
/// <remarks>
/// <b>为什么不是"装上装备算出来"</b>：`02 §8.1.1` 定义档位时给的是**结果**（暴击 / 面板暴伤 + 完成度），
/// 不是**配装**；而武器与装备进战斗属 `M7`。`M2` 的任务是"用可信的面板量出 DPR"，
/// 所以这里按**锚点插值**给出面板，`M7` 再把"真实配装 → 面板"那条链路接上并替换本类。
/// <para>
/// 两条独立的插值轴（都用 `02` 的原文当锚点，没有自由参数）：
/// <list type="number">
///   <item><b>面板完成度</b> <see cref="InvestmentTierDef.PanelK"/>：攻 / 生 / 防 / 速 在「80 级裸装 → 满配」之间线性插值。</item>
///   <item><b>双暴完成度</b> <see cref="InvestmentTierDef.CritRateK"/> / <see cref="InvestmentTierDef.CritDmgK"/>：
///         由 `02 §8.1.1` 的 50/200 · 60/240 · 65/270 四行**反解**出来，再按各行自己的满配双暴缩放。</item>
/// </list>
/// 主C 在「0+1 低配」档于是**正好**落在 §8.1.1 的 50% / 200% —— 这是这套折算唯一能自我验证的地方。
/// </para>
/// </remarks>
public static class TierResolver
{
    /// <summary>80 级裸装防御：`100 × 1.01^79 ≈ 219.5`（`02 §1.2` 的「其他基础属性」表）。</summary>
    public const double NakedDefense = 219.0;

    /// <summary>裸装暴击率 / 面板暴击伤害（`02 §10` 的 `CRIT_RATE_BASE` / `CRIT_DMG_BASE`）。</summary>
    public const double NakedCritRate = 0.05;
    public const double NakedCritDmg = 1.50;

    public static double Lerp(double from, double to, double k) => from + (to - from) * k;

    /// <summary>折算一个角色的档位面板。`row` 缺省时返回 `null`（调用点负责报错或回退）。</summary>
    public static TierPanel? Resolve(InvestmentTierDef tier, TierRowDef? row, CharacterDef character)
    {
        if (row is null) return null;

        double k = Math.Clamp(tier.PanelK, 0.0, 1.0);
        return new TierPanel(
            Atk: Lerp(row.AtkNaked, row.AtkFull, k),
            Hp: Lerp(row.HpNaked, row.HpFull, k),
            Def: Lerp(NakedDefense, row.DefFull, k),
            Speed: Lerp(character.Base.Speed, row.SpeedFull, k),
            CritRate: NakedCritRate + (row.CritRateFull - NakedCritRate) * Math.Clamp(tier.CritRateK, 0.0, 1.0),
            CritDmg: NakedCritDmg + (row.CritDmgFull - NakedCritDmg) * Math.Clamp(tier.CritDmgK, 0.0, 1.0));
    }

    /// <summary>档位面板 → 一份可直接喂给 <c>BattleUnit.Base</c> 的静态面板（`PanelIsFinal = true`）。</summary>
    public static BaseStatsDef ToBaseStats(TierPanel panel, CharacterDef character) => new()
    {
        Atk = panel.Atk,
        Hp = panel.Hp,
        Def = panel.Def,
        Speed = panel.Speed,
        CritRate = panel.CritRate,
        CritDmg = panel.CritDmg,
        EnergyMax = character.Base.EnergyMax,
        EnergyOverflowCap = character.Base.EnergyOverflowCap,
        Threat = character.Base.Threat,
    };
}
