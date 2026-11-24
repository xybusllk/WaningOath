namespace WaningOath.Core.Battle;

using WaningOath.Core.Data;

/// <summary>一次伤害计算的输入。</summary>
public sealed class DamageRequest
{
    public required BattleUnit Source { get; init; }
    public required BattleUnit Target { get; init; }
    public required Element Element { get; init; }

    /// <summary>产生这次伤害的技能（钩子按 `kind` 分流时需要）。召唤物与异常伤害为 `null`。</summary>
    public SkillDef? Skill { get; init; }

    /// <summary>倍率。**百分数写成小数**（1125% → 11.25）。</summary>
    public double Multiplier { get; init; }

    /// <summary>`atk` 或 `hp` —— 面板基数（`01 §2.2 ②`）。</summary>
    public string ScalingBase { get; init; } = "atk";

    public bool CanCrit { get; init; } = true;
    public bool ForceCrit { get; init; }
    public bool ForceNoCrit { get; init; }
    public bool IgnoreDefense { get; init; }

    public DamageCategory Category { get; init; } = DamageCategory.Direct;

    /// <summary>
    /// **以"已结算伤害"为基数的附加伤害**（破冰，`01 §2.4` 的唯一例外）。
    /// 一旦填写，就直接用它当基数，并**跳过 ③ 防御区与 ⑤⑥⑦ 增伤/易伤/抗性区**，只再过 ⑧⑨⑩。
    /// </summary>
    public double? PrecomputedBase { get; init; }

    /// <summary>是否走越级区（`01 §2.2 ⑨`）。破冰"不视为任何单位造成的伤害"，但越级区仍按施加者等级走 —— 见下面的说明。</summary>
    public bool ApplyLevelPenalty { get; init; } = true;

    /// <summary>是否积蓄异常进度。附加伤害默认积蓄，技能可写明不积蓄（`01 §2.4`）。</summary>
    public bool AccumulateAnomaly { get; init; } = true;

    /// <summary>
    /// **`01 §3.2.3` 的特例开关**（`100` #72）：本次命中若触发「可燃」，**不结算**该条内置的
    /// 「叠加即时伤害」。归属：贝尔兰德（`07 §8.3`）。
    /// </summary>
    public bool SuppressCombustStackDamage { get; init; }

    /// <summary>日志用的来源描述（技能名 / 异常名）。</summary>
    public string Label { get; init; } = "";
}

/// <summary>一次伤害计算的结果，含**用于自测的逐乘区明细**。</summary>
public sealed class DamageResult
{
    public double Amount { get; set; }
    public bool Crit { get; set; }

    // 逐乘区（01 §2.2 的 ①~⑪），供 Sim verify 打印与断言。
    public double PanelBase { get; set; }
    public double Multiplier { get; set; }
    public double EffectiveDefense { get; set; }
    public double ReductionRate { get; set; }
    public double AfterDefense { get; set; }
    public double ZoneBoost { get; set; } = 1.0;
    public double ZoneVuln { get; set; } = 1.0;
    public double ZoneResist { get; set; } = 1.0;
    public double ZoneCrit { get; set; } = 1.0;
    public double ZoneLevel { get; set; } = 1.0;
    public double ZoneEnvironment { get; set; } = 1.0;
    public double ZoneFinal { get; set; } = 1.0;
}

/// <summary>
/// `01 §2.2` 的完整伤害管线 —— **全作唯一公式**。
/// </summary>
/// <remarks>
/// <code>
/// ① 技能倍率 = 技能基础倍率 × 等级修正系数
/// ② 面板基数 = 攻击力 或 生命上限
/// ③ 防御区   有效防御 = 防御 × (1 − 穿透)；R = D / (D + DEF_K)；乘数 = 1 − R
/// ④ 基础伤害 = 面板基数 × 技能倍率 × (1 − R)
/// ⑤ 增伤区   × (1 + 属性增伤 + 伤害加深)
/// ⑥ 易伤区   × (1 + 目标受到的全部易伤 / 脆弱层数加成)
/// ⑦ 抗性区   × (1 − 目标该属性抗性 + 我方该属性穿透 + 伤害加深的穿透部分)
/// ⑧ 暴击区   暴击时 × 面板暴击伤害；未暴击 × 1.0
/// ⑨ 越级区   等级低者 × (1 − 等级差 × 1%)，仅单向，上限 −30%
/// ⑩ 环境区   场地级修正，独立乘区，不属于增减益
/// ⑪ 最终伤害 极其稀有的独立增伤/减伤乘区
/// </code>
/// <para>
/// ⚠️ **`⑪` 的落地口径是 M1 的一处实现决策**：`01 §2.3` 把「减伤」列为词条（"受到伤害降低，取最高不叠加"），
/// 但 `§2.2` 的十一区里**没有为它单列一区**。M1 把它放在 `⑪`，理由是 `⑪` 本来就写作"独立增伤/减伤乘区"。
/// 这条需要作者裁一次（已记入 `100`）。**M1 的验收用例里没有任何减伤来源**，所以它不影响验收数字。
/// </para>
/// </remarks>
public static class DamagePipeline
{
    /// <summary>纯计算，不改变任何状态。</summary>
    public static DamageResult Compute(BattleState state, DamageRequest req)
    {
        BattleUnit src = req.Source;
        BattleUnit tgt = req.Target;
        ComputedStats ss = src.Stats;
        ComputedStats ts = tgt.Stats;
        Element e = req.Element;

        DamageResult r = new()
        {
            Multiplier = req.Multiplier,
        };

        double penetration = req.IgnoreDefense ? 1.0 : src.PenFor(e);

        if (req.PrecomputedBase is double pre)
        {
            // 以"已结算伤害"为基数的附加伤害：跳过 ③⑤⑥⑦（01 §2.4 的唯一例外）。
            r.PanelBase = pre;
            r.EffectiveDefense = 0.0;
            r.ReductionRate = 0.0;
            r.AfterDefense = pre;
            r.ZoneBoost = 1.0;
            r.ZoneVuln = 1.0;
            r.ZoneResist = 1.0;
        }
        else
        {
            r.PanelBase = req.ScalingBase == "hp" ? src.MaxHp : ss.AtkValue;

            // ③ 防御区：比率减伤。
            r.EffectiveDefense = ts.DefValue * (1.0 - penetration);
            r.ReductionRate = r.EffectiveDefense / (r.EffectiveDefense + BattleConstants.DefK);

            // ④ 基础伤害。
            r.AfterDefense = r.PanelBase * req.Multiplier * (1.0 - r.ReductionRate);

            // ⑤⑥⑦
            r.ZoneBoost = 1.0 + src.DmgBonusFor(e);
            r.ZoneVuln = 1.0 + tgt.VulnFor(e);
            r.ZoneResist = Math.Max(0.0, 1.0 - tgt.ResistFor(e) + src.PenFor(e));
        }

        // ⑧ 暴击区。面板暴击伤害 = 暴击总倍率；未暴击乘 1.0（不是乘 0，也不是 (1+x)）。
        double critRate = Math.Clamp(ss.CritRateValue, 0.0, 1.0);
        bool crit = req.ForceCrit
                    || (!req.ForceNoCrit && req.CanCrit && state.Rng.Chance(critRate));
        r.Crit = crit;
        r.ZoneCrit = crit ? Math.Min(ss.CritDmgValue, BattleConstants.CritDmgCap) : 1.0;

        // ⑨ 越级区。仅单向：只有等级低的一方降伤。
        if (req.ApplyLevelPenalty && src.Level < tgt.Level)
        {
            double diff = (tgt.Level - src.Level) * BattleConstants.LevelPenaltyPer;
            r.ZoneLevel = 1.0 - Math.Min(BattleConstants.LevelPenaltyCap, diff);
        }

        // ⑩ 环境区。
        r.ZoneEnvironment = state.EnvironmentFactor(e, req.Category);

        // ⑪ 最终伤害区。两条一起乘：
        //   ① 「减伤取最高」（M1 口径，01 §2.2 关键裁定）；
        //   ② 「对某类敌方单位的最终伤害加成」（01 §7 的 category · 100 #87，爱丽丝「异常特攻」）。
        //      ⚠️ 它是**全队生效**的，所以按"同阵营存活单位里取最高"结算，**不叠加**
        //      （与「减伤取最高」同一条纪律 —— 避免带两个同类角色就翻倍）。
        double maxCut = Math.Max(0.0, 1.0 - ts.DamageReductionMax);
        r.ZoneFinal = maxCut * (1.0 + TeamCategoryAmp(state, src, tgt.Category))
                      * (1.0 + TeamShieldedAmp(state, src, tgt));

        r.Amount = r.AfterDefense
                   * r.ZoneBoost
                   * r.ZoneVuln
                   * r.ZoneResist
                   * r.ZoneCrit
                   * r.ZoneLevel
                   * r.ZoneEnvironment
                   * r.ZoneFinal;

        return r;
    }

    /// <summary>同阵营里对该类别敌人的最终伤害加成 —— **取最高，不叠加**。</summary>
    private static double TeamCategoryAmp(BattleState state, BattleUnit src, string category)
    {
        double best = 0.0;
        foreach (BattleUnit u in state.Units)
        {
            if (!u.IsAlive || u.Disposition != src.Disposition) continue;
            if (u.FinalDamageVsCategory.TryGetValue(category, out double v) && v > best) best = v;
        }
        return best;
    }

    /// <summary>
    /// 同阵营里对「持盾 / 召唤物」目标的最终伤害加成 —— 与类别特攻同一条「取最高、不叠加」纪律。
    /// </summary>
    private static double TeamShieldedAmp(BattleState state, BattleUnit src, BattleUnit tgt)
    {
        if (tgt.Shield <= 0.0 && !tgt.IsSummon) return 0.0;
        double best = 0.0;
        foreach (BattleUnit u in state.Units)
        {
            if (!u.IsAlive || u.Disposition != src.Disposition) continue;
            double v = u.FinalDamageVsShielded + u.EidolonKnobs.GetValueOrDefault("shieldedDamageBonus");
            if (v > best) best = v;
        }
        return best;
    }
}
