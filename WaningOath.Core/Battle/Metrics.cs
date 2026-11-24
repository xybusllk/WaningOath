namespace WaningOath.Core.Battle;

/// <summary>
/// 一场战斗的**量化台账**。
/// </summary>
/// <remarks>
/// <b>为什么要有它</b>：`M1` 的产物是日志（可读、可逐行 diff），但日志**数不出 DPR**。
/// `M2` 要回答的是"这套倍率打出多少伤害、伤害从哪来、几轮打完"，那是统计问题，不是叙事问题。
/// 于是把每次落地的伤害在**落地点**顺手累加一份 —— 而不是回头去解析日志文本
/// （解析文本会随日志格式变动而失效，而且"护盾吸收了多少""溢出多少"在文本里是丢掉的）。
/// <para>
/// ⚠️ <b>确定性不受影响</b>：这里只有累加，不参与任何分支判定，也不读时间 / 环境。
/// </para>
/// <para>
/// 口径：
/// <list type="bullet">
///   <item><see cref="RawDealt"/> —— 管线算出来的原始伤害额（**含溢出**）。</item>
///   <item><see cref="HpRemoved"/> —— 真正从目标生命里扣掉的部分。**DPR 用这一列**：
///         拿原始伤害算 DPR 会让"最后一击的斩杀溢出"按怪的血量上限虚增成绩。</item>
///   <item><see cref="Overkill"/> —— <see cref="RawDealt"/> − <see cref="HpRemoved"/> − <see cref="Absorbed"/>。</item>
/// </list>
/// </para>
/// </remarks>
public sealed class BattleMetrics
{
    // ── 我方 → 敌方 ──────────────────────────────────────────────────
    public double RawDealt { get; set; }
    public double HpRemoved { get; set; }
    public double Absorbed { get; set; }
    public double Overkill { get; set; }

    // ── 敌方 → 我方 ──────────────────────────────────────────────────
    public double RawTaken { get; set; }
    public double HpLost { get; set; }
    public double AbsorbedByAllies { get; set; }

    // ── 治疗与护盾 ────────────────────────────────────────────────────
    public double Healed { get; set; }
    public double Overheal { get; set; }
    public double ShieldGranted { get; set; }

    // ── 计数 ──────────────────────────────────────────────────────────
    public int HitInstances { get; set; }

    /// <summary>
    /// **可暴击**的伤害实例数。它是"实测暴击率"的正确分母 ——
    /// 风蚀 dot 写着 `CanCrit = false`，把它算进分母会把实测暴击率从 65% 压到 15%，
    /// 从而伪装成"面板暴击没生效"。
    /// </summary>
    public int CritEligibleInstances { get; set; }

    public int CritInstances { get; set; }
    public int AnomalyTriggers { get; set; }
    public int DisorderTriggers { get; set; }

    /// <summary>按异常家族分开计数 —— "哪条异常在真的出伤"这个问题只有分开数才回答得了（`100` 甲7）。</summary>
    public Dictionary<AnomalyFamily, int> AnomalyByFamily { get; } = [];

    /// <summary>紊乱真伤总额（独立记一份，便于直接和"直伤"对比）。</summary>
    public double DisorderDamage { get; set; }

    /// <summary>
    /// 各**属性异常自身**的伤害（不含直伤、不含紊乱）。`100` 甲7 要的正是这张表：
    /// 技能倍率被削之后，"异常"到底占了多少。按属性分开记（风蚀 / 可燃 / 破冰 / 引雷各有各的倍率）。
    /// </summary>
    public Dictionary<Element, double> AnomalyDamageByElement { get; } = [];
    public int CowerTriggers { get; set; }
    public int EnemyDeaths { get; set; }
    public int AllyDeaths { get; set; }

    /// <summary>输出明细：伤害来自哪个单位（键 = 战斗内序号）。</summary>
    public Dictionary<int, SourceRow> BySource { get; } = [];

    /// <summary>输出明细：伤害属于哪一类（直伤 / 异常 / 紊乱 / 附加 / 复伤）。</summary>
    public Dictionary<DamageCategory, double> ByCategory { get; } = [];

    /// <summary>输出明细：伤害按属性。</summary>
    public Dictionary<Element, double> ByElement { get; } = [];

    /// <summary>承受明细：我方谁掉了多少血。</summary>
    public Dictionary<int, double> TakenByAlly { get; } = [];

    /// <summary>我方技能位的使用次数（键 = `技能位`，用于确认"循环"里到底放了什么）。</summary>
    public Dictionary<string, int> SkillUses { get; } = new(StringComparer.Ordinal);

    /// <summary>某个单位的伤害贡献。</summary>
    public sealed class SourceRow
    {
        public required string Name { get; init; }
        public required string DefId { get; init; }
        public double HpRemoved { get; set; }
        public double Raw { get; set; }
        public int Hits { get; set; }
        public int Eligible { get; set; }
        public int Crits { get; set; }
    }

    /// <summary>记一次落地伤害。<paramref name="friendlyToHostile"/> 由落地点判定后传入。</summary>
    public void RecordDamage(
        BattleUnit source,
        BattleUnit target,
        Element element,
        DamageCategory category,
        double raw,
        double absorbed,
        double hpRemoved,
        bool critEligible = true)
    {
        bool friendlyToHostile = source.Disposition == Disposition.Friendly
                                 && target.Disposition == Disposition.Hostile;

        if (friendlyToHostile)
        {
            RawDealt += raw;
            HpRemoved += hpRemoved;
            Absorbed += absorbed;
            Overkill += Math.Max(0.0, raw - absorbed - hpRemoved);
            HitInstances++;
            if (critEligible) CritEligibleInstances++;

            if (!BySource.TryGetValue(source.Serial, out SourceRow? row))
            {
                row = new SourceRow { Name = source.Name, DefId = source.DefId };
                BySource[source.Serial] = row;
            }
            row.HpRemoved += hpRemoved;
            row.Raw += raw;
            row.Hits++;
            if (critEligible) row.Eligible++;
            if (!ByCategory.TryGetValue(category, out double c)) c = 0.0;
            ByCategory[category] = c + hpRemoved;
            if (!ByElement.TryGetValue(element, out double e)) e = 0.0;
            ByElement[element] = e + hpRemoved;

            if (category == DamageCategory.Disorder) DisorderDamage += hpRemoved;
            if (category == DamageCategory.Anomaly)
            {
                if (!AnomalyDamageByElement.TryGetValue(element, out double av)) av = 0.0;
                AnomalyDamageByElement[element] = av + hpRemoved;
            }
        }
        else if (source.Disposition == Disposition.Hostile && target.Disposition == Disposition.Friendly)
        {
            RawTaken += raw;
            HpLost += hpRemoved;
            AbsorbedByAllies += absorbed;
            if (!TakenByAlly.TryGetValue(target.Serial, out double v)) v = 0.0;
            TakenByAlly[target.Serial] = v + hpRemoved;
        }
    }

    /// <summary>记一次暴击（只有"我方打敌方"才计数，避免敌方的暴击污染"我方暴击率"这一列）。</summary>
    public void RecordCrit(BattleUnit source, BattleUnit target)
    {
        if (source.Disposition != Disposition.Friendly || target.Disposition != Disposition.Hostile) return;
        CritInstances++;
        if (BySource.TryGetValue(source.Serial, out SourceRow? row)) row.Crits++;
    }

    public void RecordHeal(double requested, double effective)
    {
        Healed += effective;
        Overheal += Math.Max(0.0, requested - effective);
    }

    /// <summary>某个单位打出的伤害（键缺失时返回 0）。</summary>
    public double DealtBy(int serial) => BySource.TryGetValue(serial, out SourceRow? r) ? r.HpRemoved : 0.0;

    public double CategoryOf(DamageCategory category) => ByCategory.TryGetValue(category, out double v) ? v : 0.0;

    /// <summary>直伤以外的全部伤害（异常 + 紊乱 + 附加 + 复伤）。</summary>
    public double NonDirect => HpRemoved - CategoryOf(DamageCategory.Direct);

    /// <summary>按输出从高到低排列的贡献表。</summary>
    public IEnumerable<SourceRow> Ranked()
        => BySource.Values.OrderByDescending(static r => r.HpRemoved).ThenBy(static r => r.DefId, StringComparer.Ordinal);
}
