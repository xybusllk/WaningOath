namespace WaningOath.Core.Battle;

/// <summary>标量属性。依据 `01 §2.3 属性词条全表`。</summary>
public enum StatKind
{
    Atk = 0,
    Hp = 1,
    Def = 2,
    Speed = 3,
    CritRate = 4,
    CritDmg = 5,
    EnergyMax = 6,
    Threat = 7,
    EnergyRegen = 8,
    HealBonus = 9,
    ShieldBonus = 10,
    EffectHit = 11,
    EffectRes = 12,
    /// <summary>**通用**属性异常积蓄效率（装备 / 武器上的那一层，`01 §2.3`）。</summary>
    AnomalyBuildUp = 13,

    /// <summary>
    /// 「受到的伤害降低」。**多来源取最高、不叠加、不设上限**（`01 §2.2`）。
    /// ⚠️ 它**不是** `Add`/`Pct` 累加器意义上的词条 —— 取值走 <see cref="ComputedStats.DamageReductionMax"/>，
    /// 不走 <see cref="ScalarAccumulator"/>。
    /// </summary>
    DamageReduction = 14,
}

/// <summary>按属性分别记录的词条。依据 `01 §2.3`。</summary>
public enum ElemStat
{
    /// <summary>属性增伤（增伤区 ⑤）。</summary>
    DmgBonus = 0,
    /// <summary>穿透（抗性区 ⑦ 的加项）。</summary>
    Pen = 1,
    /// <summary>属性抗性（抗性区 ⑦ 的减项，敌方为主）。</summary>
    Resist = 2,
    /// <summary>易伤（易伤区 ⑥）。</summary>
    Vuln = 3,
    /// <summary>
    /// **伤害加深**（`01 §2.3`）：**同数值同时计入增伤区与穿透区**。
    /// 这是本作**唯一**一个"一个数值进两个乘区"的词条，因此单列，不能并进 <see cref="DmgBonus"/>。
    /// </summary>
    DmgAmp = 4,
    /// <summary>**限定属性**的异常积蓄效率（技能 / 增益给的那一层，`01 §2.3`）。</summary>
    AnomalyBuildUp = 5,
}

/// <summary>修正的运算方式。<see cref="Add"/> → <see cref="Pct"/> → <see cref="Mul"/> 依次结算。</summary>
public enum ModOp
{
    /// <summary>固定值加算（例：攻击力 +520）。</summary>
    Add = 0,
    /// <summary>百分比加算（例：攻击力 +30%）——**同类相加**，不是连乘。</summary>
    Pct = 1,
    /// <summary>独立乘区（例：某效果"最终伤害 ×1.2"）。用得越少越好。</summary>
    Mul = 2,
}

/// <summary>一条标量修正。带 <see cref="Source"/> 便于日志与调试，不参与结算。</summary>
public sealed record StatMod(StatKind Kind, ModOp Op, double Value, string Source);

/// <summary>一条按属性记录的修正。</summary>
public sealed record ElemMod(ElemStat Kind, Element Element, ModOp Op, double Value, string Source);

/// <summary>标量属性的取值容器。<see cref="ModOp.Add"/> 与 <see cref="ModOp.Pct"/> 用累加器，<see cref="ModOp.Mul"/> 用连乘。</summary>
public sealed class ScalarAccumulator
{
    public double Add;
    public double Pct;
    public double Mul = 1.0;

    public void Apply(ModOp op, double value)
    {
        switch (op)
        {
            case ModOp.Add: Add += value; break;
            case ModOp.Pct: Pct += value; break;
            case ModOp.Mul: Mul *= value; break;
        }
    }
}

/// <summary>
/// 按属性记录的累加器集合。**只有 8 个下标**（八属性），且"离谒 / 咏叹"在取值时会被解析到基础属性，
/// 所以写入时也要先 <see cref="ElementInfo.Resolve"/>。
/// </summary>
public sealed class ElemAccumulator
{
    private readonly ScalarAccumulator[] _cells = new ScalarAccumulator[ElementInfo.Count * 6];

    public ElemAccumulator()
    {
        for (int i = 0; i < _cells.Length; i++) _cells[i] = new ScalarAccumulator();
    }

    private static int Index(ElemStat kind, Element element) => (int)element * 6 + (int)kind;

    public void Apply(ElemStat kind, Element element, ModOp op, double value)
        => _cells[Index(kind, ElementInfo.Resolve(element))].Apply(op, value);

    /// <summary>取某属性某词条的最终值。</summary>
    public double Get(ElemStat kind, Element element)
    {
        ScalarAccumulator c = _cells[Index(kind, ElementInfo.Resolve(element))];
        return (0.0 + c.Add) * (1.0 + c.Pct) * c.Mul;
    }

    /// <summary>取某属性某词条的百分比部分——用于"加算"语义的词条（增伤 / 易伤 / 抗性 / 穿透 / 加深 / 积蓄效率）。</summary>
    public double GetPct(ElemStat kind, Element element) => Get(kind, element);
}

/// <summary>
/// 一个单位在**当前这一刻**的实战面板。
/// </summary>
/// <remarks>
/// 由 <see cref="Builder"/> 从「基础面板 + 全部生效中的状态 + 权阶 / 武器被动」即时算出。
/// **不做缓存**：单位数量与状态数量都很小（≤ 10 单位 × 数十条状态），
/// 而"速度变化立即重算 AV""攻击力取结算瞬间的实时值"（`01 §1.2` / `§3.2.4`）都要求读到的必须是实时值。
/// 缓存只会带来一处忘记失效的 bug。
/// </remarks>
public sealed class ComputedStats
{
    public double BaseAtk;
    public double BaseHp;
    public double BaseDef;
    public double BaseSpeed;
    public readonly ScalarAccumulator Atk = new();
    public readonly ScalarAccumulator Hp = new();
    public readonly ScalarAccumulator Def = new();
    public readonly ScalarAccumulator Speed = new();
    public readonly ScalarAccumulator CritRate = new();
    public readonly ScalarAccumulator CritDmg = new();
    public readonly ScalarAccumulator EnergyMax = new();
    public readonly ScalarAccumulator EnergyRegen = new();
    public readonly ScalarAccumulator HealBonus = new();
    public readonly ScalarAccumulator ShieldBonus = new();
    public readonly ScalarAccumulator EffectHit = new();
    public readonly ScalarAccumulator EffectRes = new();
    public readonly ScalarAccumulator Threat = new();
    public readonly ElemAccumulator Elem = new();

    /// <summary>通用异常积蓄效率（装备 / 武器层）。</summary>
    public readonly ScalarAccumulator AnomalyBuildUp = new();

    /// <summary>"受到的伤害降低"（**取最高，不叠加** —— `01 §2.2` 关键裁定）。**不设上限**。</summary>
    public double DamageReductionMax;

    public double AtkValue => Flat(BaseAtk, Atk);
    public double HpValue => Flat(BaseHp, Hp);
    public double DefValue => Flat(BaseDef, Def);
    public double SpeedValue => Flat(BaseSpeed, Speed);
    public double CritRateValue => Flat(BattleConstants.CritRateBase, CritRate);
    public double CritDmgValue => Flat(BattleConstants.CritDmgBase, CritDmg);
    public double EnergyMaxValue => Flat(0.0, EnergyMax);
    public double EnergyRegenValue => Flat(1.0, EnergyRegen);
    public double HealBonusValue => Flat(0.0, HealBonus);
    public double ShieldBonusValue => Flat(0.0, ShieldBonus);
    public double EffectHitValue => Flat(0.0, EffectHit);
    public double EffectResValue => Flat(0.0, EffectRes);
    public double ThreatValue => Flat(BattleConstants.DefaultThreat, Threat);
    public double AnomalyBuildUpValue => Flat(0.0, AnomalyBuildUp);

    /// <summary>`(基础 + 固定值) × (1 + 百分比之和) × 独立乘区连乘`。</summary>
    public static double Flat(double baseValue, ScalarAccumulator acc)
        => (baseValue + acc.Add) * (1.0 + acc.Pct) * acc.Mul;

    /// <summary>某属性的某条词条（已按 `01 §2.1` 解析过离谒 / 咏叹）。</summary>
    public double ElemValue(ElemStat kind, Element element) => Elem.GetPct(kind, element);

    /// <summary>攻击者的"某属性异常积蓄效率"总额：通用层 + 该属性的限定层（`01 §2.3` 的两层口径）。</summary>
    public double BuildUpFor(Element element)
        => AnomalyBuildUpValue + ElemValue(ElemStat.AnomalyBuildUp, element);
}
