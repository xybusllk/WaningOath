using WaningOath.Core.Data;

namespace WaningOath.Core.Battle;

/// <summary>单位种类。决定"算不算我方角色"（`01 §6.3`）。</summary>
public enum UnitKind
{
    Character = 0,
    Enemy = 1,
    Summon = 2,
}

/// <summary>
/// 一个生效中的状态实例（buff / debuff / 硬控 / 召唤物载体）。
/// </summary>
/// <remarks>
/// <b>必须显式携带三个引用</b>（`01 §1.3` 的实现要求）：
/// <list type="bullet">
///   <item><see cref="Owner"/> —— 挂给谁（决定"这个状态在谁身上"）。</item>
///   <item><see cref="Source"/> —— 谁施加的（风蚀 dot 按施加者生命、引雷按施加者实时攻击力）。</item>
///   <item><see cref="TickSource"/> —— **按谁的行动计时**（自身增益 = 被施加者；光环 = 施加者）。</item>
/// </list>
/// 这三者**可以互不相同**，把它们合并成一个字段是本作最容易埋下的一个坑。
/// </remarks>
public sealed class StatusInstance
{
    public required StatusDef Def { get; init; }
    public required BattleUnit Owner { get; init; }

    /// <summary>施加者。风蚀 / 引雷 / 可燃的伤害基数取自它，且取**结算瞬间的实时面板**。</summary>
    public BattleUnit? Source { get; init; }

    /// <summary>计时依据。省略 = 同 <see cref="Owner"/>。</summary>
    public BattleUnit? TickSource { get; init; }

    public int Stacks { get; set; } = 1;

    /// <summary>剩余时间。单位由 <see cref="StatusDef.DurationUnit"/> 决定（`turn` 数或 `round` 数）。</summary>
    public double Remaining { get; set; }

    /// <summary>施加时记录的"施加者等级"，供越级区使用。</summary>
    public int SourceLevel { get; init; }

    public BattleUnit EffectiveTickSource => TickSource ?? Owner;

    public string Label => Stacks > 1 ? $"{Def.Name}×{Stacks}" : Def.Name;

    public override string ToString() => $"{Def.Id}({Label}) on {Owner.Name} 剩余 {Remaining:0.#}";
}

/// <summary>
/// 某一条异常在某个单位身上的状态。
/// </summary>
/// <remarks>
/// 依据 `01 §3.2`。注意本类**不是** <see cref="StatusInstance"/> 的特例——
/// 异常的层数规则、结算时点、时长单位都各写各的（脆弱按单位回合、可燃 / 风蚀按自然回合），
/// 硬套 buff 那套三分法只会得到四不像。
/// </remarks>
public sealed class AnomalyState
{
    public AnomalyFamily Family { get; init; }

    /// <summary>层数。冻结 / 晕眩这类"无层数"的异常恒为 0。</summary>
    public int Stacks { get; set; }

    /// <summary>剩余时间。语义按家族：脆弱 / 冻结按单位回合；可燃 / 风蚀 / 复伤按自然回合。</summary>
    public double Remaining { get; set; }

    /// <summary>施加者（dot 的基数由它决定）。**每次叠加时更新为最新的施加者**（`01 §3.2.3` 可燃的明文口径）。</summary>
    public BattleUnit? Source { get; set; }

    public bool Active => Stacks > 0 || Remaining > 0.0;

    /// <summary>冻结期间累计承受的伤害之和 —— 破冰的基数（`01 §3.2.2`）。</summary>
    public double AccumulatedDamage { get; set; }

    /// <summary>冻结：正在冻结中（`01 §3.2.2`）。这是 <see cref="AnomalyFamily.Ice"/> 的专属标志。</summary>
    public bool Frozen { get; set; }

    /// <summary>晕眩：无法行动 1 回合（引雷的附带效果，`01 §3.2.4`）。</summary>
    public bool Stunned { get; set; }

    /// <summary>畏缩：脆弱满 10 层时进入（**脆弱的派生状态，不是独立异常**，`01 §3.2.1`）。</summary>
    public bool Cower { get; set; }

    /// <summary>引雷：伤害已标记、待目标**下个回合开始**时结算（`01 §3.2.4`）。</summary>
    public bool PendingDelivery { get; set; }

    /// <summary>复伤：待回放的最近一次伤害（`01 §3.2.6`）。</summary>
    public DamageRecord? PendingReplay { get; set; }

    /// <summary>是否存在任何生效中的部分（层数 / 时长 / 硬控 / 待结算）。</summary>
    public bool AnyActive
        => Stacks > 0 || Remaining > 0.0 || Frozen || Stunned || Cower || PendingDelivery;

    /// <summary>
    /// **施加过这条异常的敌方单位集合**（`01 §3.2.5` · `100` #92）。
    /// </summary>
    /// <remarks>
    /// 为什么不能只留一个"最新施加者"：风蚀每层的伤害基数现在取
    /// **「队内施加过风蚀且生命上限最高」的那一位** —— 而"最新"与"生命上限最高"**不是同一个人**。
    /// 配合实例：守约人风蚀模态先铺 20 层（生命 8,000），爱丽丝再叠一次（生命 15,000）
    /// ⇒ **整堆按爱丽丝算**。这就是"低基数副C铺层 + 高基数主C提供伤害"的机制底座。
    /// </remarks>
    public List<BattleUnit> Appliers { get; } = [];

    /// <summary>
    /// **层数上限覆盖**（`01 §3.2.5` 的「风化」· `100` #88）。`0` = 用通行常量。
    /// </summary>
    public int CapOverride { get; set; }
}

/// <summary>一笔已经过完管线的伤害记录。复伤与"忆库回溯"类效果都读它。</summary>
public sealed class DamageRecord
{
    public required BattleUnit Target { get; init; }
    public BattleUnit? Source { get; init; }
    public required Element Element { get; init; }
    public double Amount { get; init; }
    public bool WasCrit { get; init; }
    public int SourceLevel { get; init; }

    /// <summary>伤害分类。复伤**不复制**持续伤害 / 附加伤害 / 属性异常伤害 / 另一份复伤（`01 §3.2.6`）。</summary>
    public DamageCategory Category { get; init; }
}

public enum DamageCategory
{
    /// <summary>技能直伤。</summary>
    Direct = 0,
    /// <summary>属性异常本身的伤害（风蚀 dot、引雷延迟、可燃引爆、破冰）。</summary>
    Anomaly = 1,
    /// <summary>附加伤害（`01 §2.4`）。</summary>
    Additional = 2,
    /// <summary>元素紊乱真实伤害（`01 §3.3`）。</summary>
    Disorder = 3,
    /// <summary>复伤回放。</summary>
    Replay = 4,
}

/// <summary>领域（Field）。全场同名唯一，默认不可并存（`01 §1.3`）。</summary>
public sealed class FieldInstance
{
    public required StatusDef Def { get; init; }
    public required BattleUnit Caster { get; init; }
    public double Remaining { get; set; }
    public double DurationUnit { get; init; } = 100.0;
}

/// <summary>
/// 战斗中的一个单位。角色 / 敌人 / 召唤物共用这一个类型。
/// </summary>
/// <remarks>
/// ⚠️ **不许在本类型里出现"如果是我方角色则…"这种分叉**（`06 §8.3` 第 4 条）。
/// 敌我差异一律走 <see cref="Kind"/> 与 <see cref="Disposition"/> 的**数据**判定，
/// 这样"我方超过 4 个单位"（召唤物）与"敌方也能吃我方的增益复制"才不需要改内核。
/// </remarks>
public sealed class BattleUnit
{
    /// <summary>战斗内唯一序号。用整数而不是引用，是为了让日志、重放、哈希都不依赖对象标识。</summary>
    public int Serial { get; init; }

    /// <summary>静态数据 id（`char-*` / `anom-*` / `summon-*`）。</summary>
    public required string DefId { get; init; }

    /// <summary>
    /// 所属战斗。**光环扩散必须靠它**：`appliesTo: partyAll` 的状态只挂在施加者身上，
    /// 但数值要对全队生效，所以读面板时得能看到同场其他单位的状态（`StatusesAffecting`）。
    /// </summary>
    public BattleState? Battle { get; init; }

    /// <summary>守护召唤物：该单位存活时，本角色受到的伤害由它承担（`01 §1.5` 的扩展）。</summary>
    public BattleUnit? GuardianSummon { get; set; }

    /// <summary>守护召唤物承担伤害的倍率（薇拉 = 3 倍）。</summary>
    public double GuardianMultiplier { get; set; } = 3.0;

    public required string Name { get; init; }
    public UnitKind Kind { get; init; }

    /// <summary>单位态度（`01 §1.6`）。与阵营是两条独立的轴。</summary>
    public Disposition Disposition { get; set; }

    /// <summary>精英级及以上 → 免疫秒杀（`01 §6.2` 通则 6）。</summary>
    public bool IsEliteOrAbove { get; init; }

    /// <summary>
    /// **敌方单位类别**（`01 §7` · `100` #87）：`human` / `anomaly` / `machine`。
    /// 我方单位恒为 `human`（它只用于"对某类敌人额外生效"的特效判定）。
    /// </summary>
    public string Category { get; init; } = "anomaly";

    /// <summary>对某类敌方单位的**最终伤害加成**（见 `CharacterDef.FinalDamageVsCategory`）。</summary>
    public Dictionary<string, double> FinalDamageVsCategory { get; init; } = new(StringComparer.Ordinal);

    /// <summary>我方全体对护盾 / 召唤物目标的最终伤害加成（见 `CharacterDef.FinalDamageVsShielded`）。</summary>
    public double FinalDamageVsShielded { get; init; }

    /// <summary>本场战斗是否已经触发过致命伤害保护（每个单位一次）。</summary>
    public bool LethalGuardUsed { get; set; }

    /// <summary>本场战斗选定的升格模态（`01 §5.4`）。**战斗内不可更改。**</summary>
    public string? ModeId { get; init; }

    public int Level { get; init; }
    public Rarity Rarity { get; init; } = Rarity.Five;

    /// <summary>`true` = <see cref="Base"/> 已经是最终面板，不再乘等级成长（预设面板 / 敌人表都用这条路径）。</summary>
    public bool PanelIsFinal { get; set; }

    public required BaseStatsDef Base { get; set; }

    /// <summary>
    /// **整体替换面板**（`M2` 分档实测用：同一个关卡只换面板，技能与关卡都不动）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 它只改 <see cref="Base"/> 与 <see cref="PanelIsFinal"/>，**不碰技能的 `scalingBase`** ——
    /// 所以生命倍率角色（爱丽丝）换档后依然按生命上限结算，不会因为换面板而改变倍率口径。
    /// 调用后必须重设 <see cref="Hp"/> 为新的上限（旧上限下的血量百分比在新面板上没有意义）。
    /// </remarks>
    public void OverridePanel(BaseStatsDef panel)
    {
        Base = panel;
        PanelIsFinal = true;
        Hp = MaxHp;
    }

    /// <summary>角色拥有的属性。双属性角色有两个（`01 §2.1`）。</summary>
    public Element[] Elements { get; init; } = [Element.Physical];

    public List<string> Factions { get; init; } = [];

    /// <summary>已按模态展开的实际技能表。</summary>
    public Dictionary<string, SkillDef> Skills { get; init; } = new(StringComparer.Ordinal);

    /// <summary>天赋 / 权阶带来的触发钩子。</summary>
    public List<TriggerDef> Triggers { get; init; } = [];

    /// <summary>站位序号。**一号位（最左）= 0**；部分技能按它判定阵营（`01 §1.1`）。</summary>
    public int SlotIndex { get; set; }

    /// <summary>召唤物的生成者。非召唤物为 <c>null</c>。</summary>
    public BattleUnit? Summoner { get; set; }

    // ── 运行时状态 ──────────────────────────────────────────────────
    public double Hp { get; set; }
    public double SummonHpMax { get; set; }
    public double Av { get; set; }
    public double Energy { get; set; }
    public double EnergyOverflowCap { get; init; }
    public double Shield { get; set; }
    public bool ShieldConsumed { get; set; }
    public bool DamageReductionMaxSet { get; set; }

    public List<StatusInstance> Statuses { get; } = [];

    /// <summary>八条异常进度条（离谒与咏叹各占一条，**不与基础属性共用**）。</summary>
    public Dictionary<Element, double> AnomalyBars { get; } = [];

    /// <summary>六条异常状态。</summary>
    public Dictionary<AnomalyFamily, AnomalyState> Anomalies { get; } = [];

    /// <summary>按异常家族记录的抗性（"积蓄更少 / 直接免疫某属性异常"，`01 §7`）。</summary>
    public Dictionary<AnomalyFamily, double> AnomalyResist { get; } = [];

    /// <summary>静态数据里写死的属性抗性（八属性）。敌人数值表 `02 §8` 的抗性列走这里。</summary>
    public double[] BaseResist { get; } = new double[ElementInfo.Count];

    /// <summary>技能等级（键 = 技能位）。缺省走 <see cref="DefaultSkillLevel"/>。</summary>
    public Dictionary<string, int> SkillLevels { get; } = new(StringComparer.Ordinal);

    /// <summary>`01 §5.2`：0 权阶、未突破时的默认技能等级上限是 10 级；教学关从 1 级起步。</summary>
    public int DefaultSkillLevel { get; set; } = 1;

    /// <summary>本场生效的权阶（0~6）。权阶数据由 `BattleConfig.EidolonRanks` 传入。</summary>
    public int EidolonRank { get; init; }

    /// <summary>
    /// 权阶效果里暂时没有专门实现落点的声明性旋钮（例如 `starMemCap` / `guimingCap`）。
    /// **它不是"已生效"**：只有读取它的机制才生效；放在这里是为了让数据与后续实现有同一个入口。
    /// </summary>
    public Dictionary<string, double> EidolonKnobs { get; } = new(StringComparer.Ordinal);

    /// <summary>上一次记录的实时速度。速度变化时必须按 `AV = 原AV × 原速度 / 新速度` 重算（`01 §1.2`）。</summary>
    public double LastSpeed { get; set; }

    /// <summary>资源型堆叠（风涌 / 星忆 / 镜印 / 弹药 …）。**不是 buff**：它们没有持续时间，只有层数（`05 §3.2` 的 `gainStack`）。</summary>
    public Dictionary<string, int> Stacks { get; } = new(StringComparer.Ordinal);

    /// <summary>钩子的触发计数（用于 `oncePerTurn` / `oncePerBattle`）。键 = `triggerIndex@hook`。</summary>
    public Dictionary<string, int> TriggerCounts { get; } = new(StringComparer.Ordinal);

    /// <summary>`oncePerTurn` 的计数。**在每个单位回合开始时清零**。</summary>
    public Dictionary<string, int> TriggerCountsTurn { get; } = new(StringComparer.Ordinal);

    /// <summary>本回合已经通过 `recastSelf` 重发过的技能 id（防止同一技能无限重发）。</summary>
    public HashSet<string> RecastsThisTurn { get; } = new(StringComparer.Ordinal);

    /// <summary>最近一次**受到的**伤害（复伤读它）。</summary>
    public DamageRecord? LastDamageTaken { get; set; }

    /// <summary>
    /// 最近一次**可被复伤复制**的伤害。复伤**无法复制**持续伤害 / 附加伤害 / 属性异常伤害 / 另一份复伤
    /// （`01 §3.2.6` 的"复制限制"），因此它只记录 <see cref="DamageCategory.Direct"/> 那一类。
    /// </summary>
    public DamageRecord? LastCopyableDamage { get; set; }

    /// <summary>
    /// 本场战斗是否**释放过终结技**。供 `Conditions` 的 `usedUltimate` 用 ——
    /// 守约人风蚀模态的强化元素共鸣有一个"本场释放过终结技"的**不可逆前提**（`07 §1.3`）。
    /// </summary>
    public bool HasCastUltimate { get; set; }

    /// <summary>仇恨加成（`01 §7` 的仇恨操控）。最终仇恨 = <see cref="ComputedStats.ThreatValue"/> + 本值。</summary>
    public double ThreatBonus { get; set; }

    /// <summary>本回合是否已被硬控消耗掉行动（冻结 / 晕眩）。</summary>
    public bool ActionConsumedByControl { get; set; }

    public bool IsSummon => Kind == UnitKind.Summon;

    /// <summary>`common` / `symbiont`（`01 §1.5` 的召唤物子类）。</summary>
    public string SummonKind { get; init; } = "common";

    /// <summary>是共生灵（有技能、靠额外回合行动）。</summary>
    public bool IsSymbiont => IsSummon && string.Equals(SummonKind, "symbiont", StringComparison.Ordinal);
    public bool IsCharacter => Kind == UnitKind.Character;
    public bool IsEnemy => Kind == UnitKind.Enemy;
    public bool IsAlive => Hp > 0.0;

    public double MaxHp => IsSummon ? SummonHpMax : Stats.HpValue;

    /// <summary>当前生命占上限的比例（日志与"生命低于 X%"类条件用）。</summary>
    public double HpRatio { get { double m = MaxHp; return m <= 0 ? 0 : Math.Clamp(Hp / m, 0, 1); } }

    /// <summary>AI 选敌权重（`01 §7`）：**下限 1**，不设上限。</summary>
    public double ThreatWeight => Math.Max(BattleConstants.ThreatMin, Stats.ThreatValue + ThreatBonus);

    /// <summary>
    /// 实时面板。**每次访问都重算** —— "速度变化立即重算 AV"（`01 §1.2`）与
    /// "攻击力取结算瞬间的实时面板"（`01 §3.2.4`）都要求读到的必须是当前值。
    /// </summary>
    public ComputedStats Stats
    {
        get
        {
            ComputedStats s = new()
            {
                BaseAtk = Base.Atk,
                BaseHp = Base.Hp,
                BaseDef = Base.Def,
                BaseSpeed = Base.Speed,
            };

            if (!PanelIsFinal)
            {
                double g = Math.Pow(1.01, Math.Max(0, Level - 1));
                s.BaseAtk *= g;
                s.BaseHp *= g;
                s.BaseDef *= g;
            }

            s.CritRate.Apply(ModOp.Add, Base.CritRate - BattleConstants.CritRateBase);
            s.CritDmg.Apply(ModOp.Add, Base.CritDmg - BattleConstants.CritDmgBase);
            s.EnergyMax.Apply(ModOp.Add, Base.EnergyMax);
            s.Threat.Apply(ModOp.Add, Base.Threat);

            foreach (StatusInstance st in AffectingStatuses())
            {
                if (st.Remaining <= 0.0 && st.Def.Duration > 0.0) continue;
                // 层数型修正按层数等比放大（层数型 == 数值 × 层数，01 §6.2 通则 1 的"多来源层数相加"的层内版本）。
                double k = st.Def.Stackable ? Math.Max(1, st.Stacks) : 1;
                foreach (StatMod m in st.Def.Mods) s.Apply(m, k);
                foreach (ElemMod m in st.Def.ElemMods) s.Elem.Apply(m.Kind, m.Element, m.Op, m.Value * k);
            }

            // 异常状态对面板的贡献（脆弱 = 全属性易伤；可燃 = 火属性易伤；畏缩 = 攻击 −20%）。
            if (Anomalies.TryGetValue(AnomalyFamily.Physical, out AnomalyState? fragile) && fragile.Stacks > 0)
            {
                double v = fragile.Stacks * BattleConstants.FragileVulnPerStack;
                for (int i = 0; i < ElementInfo.Count; i++)
                    s.Elem.Apply(ElemStat.Vuln, (Element)i, ModOp.Add, v);
            }
            if (Anomalies.TryGetValue(AnomalyFamily.Fire, out AnomalyState? burn) && burn.Stacks > 0)
            {
                s.Elem.Apply(ElemStat.Vuln, Element.Fire, ModOp.Add, burn.Stacks * BattleConstants.BurnVulnPerStack);
            }
            if (Anomalies.TryGetValue(AnomalyFamily.Physical, out AnomalyState? cower) && cower.Cower)
            {
                // 畏缩是脆弱的派生状态（不是独立异常，且**不参与紊乱** —— 01 §3.3）。
                s.Atk.Apply(ModOp.Pct, -BattleConstants.CowerAtkDown);
            }

            // 「受到的伤害降低」**多来源取最高，不叠加**（01 §2.2 关键裁定），且**不设上限**。
            double maxCut = 0.0;
            foreach (StatusInstance st in AffectingStatuses())
            {
                double cell = 0.0;
                int k = st.Def.Stackable ? Math.Max(1, st.Stacks) : 1;
                foreach (StatMod m in st.Def.Mods)
                {
                    if (m.Kind != StatKind.DamageReduction) continue;
                    cell += m.Value * k;
                }
                if (cell > maxCut) maxCut = cell;
            }
            s.DamageReductionMax = maxCut;
            return s;
        }
    }

    public void NormalizeHp()
    {
        double max = MaxHp;
        if (Hp > max) Hp = max;
    }

    public int StackOf(string name) => Stacks.TryGetValue(name, out int v) ? v : 0;

    /// <summary>
    /// **每自然回合一次**类技能的发动台账（键 = 技能位名，值 = 最近一次发动的自然回合号）。
    /// </summary>
    /// <remarks>
    /// 记"回合号"而不是布尔值：翻到下一个自然回合时**不需要任何人去清它**，
    /// 于是也就不会出现"忘了在回合开始时重置"这一类 bug。
    /// </remarks>
    private readonly Dictionary<string, int> _oncePerRoundRound = new(StringComparer.Ordinal);

    /// <summary>该技能位最近一次发动的自然回合号（`-1` = 从未发动）。</summary>
    public int OncePerRoundUsed(string skillKey)
        => _oncePerRoundRound.TryGetValue(skillKey, out int round) ? round : -1;

    public void MarkOncePerRound(string skillKey, int round) => _oncePerRoundRound[skillKey] = round;

    /// <summary>
    /// **真正作用在本单位身上的状态**：自己身上的 + 队友挂的光环（`appliesTo: partyAll`）。
    /// </summary>
    /// <remarks>
    /// 光环只挂一份在施加者身上，若读面板时只看 `Statuses`，队友就拿不到它。
    /// `Battle` 为空（脱离战斗构造的单位，例如数据校验/面板试算）时退回只看自己。
    /// </remarks>
    private IEnumerable<StatusInstance> AffectingStatuses()
        => Battle is null ? Statuses : Battle.StatusesAffecting(this);

    /// <summary>
    /// 某**状态名**的当前层数（`StatusInstance.Stacks`；同名多个实例时取最大）。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="StackOf"/> **不是同一张表**：`StackOf` 读的是资源型计数（风涌 / 誓言 / 余火），
    /// 而 `applyBuff`/`applyDebuff` 落的层数只在 <see cref="Statuses"/> 里。
    /// 「注目」「视觉模糊」「代偿」都是后者 —— 条件表达式 `statusAtLeast:` / `actorStatusAtLeast:` 走这里。
    /// 按 `Def.Name` 匹配（数据里的中文名）而不是 id：条件表达式是人读的，
    /// 写 `statusAtLeast:代偿:3` 比写 `statusAtLeast:buff-compensation:3` 更贴近文档。
    /// </remarks>
    public int StatusStacks(string name)
    {
        int best = 0;
        foreach (StatusInstance st in AffectingStatuses())
        {
            if (st.Stacks > best && string.Equals(st.Def.Name, name, StringComparison.Ordinal)) best = st.Stacks;
        }
        return best;
    }

    /// <summary>
    /// 某条**机制旋钮**的当前总额（`M7`）：把所有生效中状态上的同名旋钮加起来。
    /// </summary>
    /// <remarks>
    /// <b>为什么旋钮要挂在状态上而不是直接写进单位</b>：武器的"爆燃伤害 +8%"是有时长的
    /// （挂在 `onCombustDetonate` 上、持续 N 回合）。写进单位就没有到期这回事了。
    /// 汇总而不是取最高，是因为它们本来就是可叠的加成（`01 §6.2` 通则 1）。
    /// </remarks>
    public double KnobValue(string name)
    {
        double sum = EidolonKnobs.GetValueOrDefault(name);
        foreach (StatusInstance st in AffectingStatuses())
        {
            if (st.Remaining <= 0.0 && st.Def.Duration > 0.0) continue;
            if (st.Def.Knobs.TryGetValue(name, out double v)) sum += v;
        }
        return sum;
    }

    public int GainStack(string name, int amount, int cap)
    {
        int cur = StackOf(name);
        int next = Math.Max(0, cur + amount);
        if (cap > 0) next = Math.Min(next, cap);
        Stacks[name] = next;
        return next;
    }

    public void SetStack(string name, int value, int cap)
    {
        int next = cap > 0 ? Math.Min(value, cap) : value;
        Stacks[name] = next;
    }

    /// <summary>取某属性在**我方视角**下的攻击性取值：用于"该吃哪份增益"。见 `01 §2.1` 的离谒 / 咏叹口径。</summary>
    public double DmgBonusFor(Element e)
    {
        // 薇拉权阶Ⅳ「枪口指在已逝的昔日」：任何属性的增伤 / 加深都转化为全属性。
        // 它不改变状态本身，只在取值时把六个基础属性的 DmgBonus + DmgAmp 求和；
        // 离谒 / 咏叹是变体，不参与求和（否则冰 / 共振会被算两遍）。
        if (EidolonKnobs.GetValueOrDefault("allElemConversion") > 0.0)
        {
            double total = 0.0;
            for (int i = 0; i < 6; i++)
            {
                Element baseElement = (Element)i;
                total += Stats.ElemValue(ElemStat.DmgBonus, baseElement)
                         + Stats.ElemValue(ElemStat.DmgAmp, baseElement);
            }
            return total;
        }

        return Stats.ElemValue(ElemStat.DmgBonus, e) + Stats.ElemValue(ElemStat.DmgAmp, e);
    }

    public double PenFor(Element e) => Stats.ElemValue(ElemStat.Pen, e) + Stats.ElemValue(ElemStat.DmgAmp, e);

    /// <summary>该属性的总抗性 = 静态抗性（`02 §8`）+ 状态给的抗性。</summary>
    public double ResistFor(Element e) => BaseResist[(int)ElementInfo.Resolve(e)] + Stats.ElemValue(ElemStat.Resist, e);

    public double VulnFor(Element e) => Stats.ElemValue(ElemStat.Vuln, e);

    /// <summary>该技能位的实际等级（`01 §5.2`：实际倍率 = 基础倍率 × 等级修正系数）。</summary>
    public int SkillLevel(string key) => SkillLevels.TryGetValue(key, out int lv) ? lv : DefaultSkillLevel;

    public override string ToString() => $"{Name}#{Serial}";
}

/// <summary><see cref="ComputedStats"/> 的写入助手。放在这里是为了让"面板怎么算"只有一处。</summary>
public static class ComputedStatsExtensions
{
    public static void Apply(this ComputedStats s, StatMod m, double scale = 1.0)
    {
        double v = m.Value * scale;
        switch (m.Kind)
        {
            case StatKind.Atk: s.Atk.Apply(m.Op, v); break;
            case StatKind.Hp: s.Hp.Apply(m.Op, v); break;
            case StatKind.Def: s.Def.Apply(m.Op, v); break;
            case StatKind.Speed: s.Speed.Apply(m.Op, v); break;
            case StatKind.CritRate: s.CritRate.Apply(m.Op, v); break;
            case StatKind.CritDmg: s.CritDmg.Apply(m.Op, v); break;
            case StatKind.EnergyMax: s.EnergyMax.Apply(m.Op, v); break;
            case StatKind.Threat: s.Threat.Apply(m.Op, v); break;
            case StatKind.EnergyRegen: s.EnergyRegen.Apply(m.Op, v); break;
            case StatKind.HealBonus: s.HealBonus.Apply(m.Op, v); break;
            case StatKind.ShieldBonus: s.ShieldBonus.Apply(m.Op, v); break;
            case StatKind.EffectHit: s.EffectHit.Apply(m.Op, v); break;
            case StatKind.EffectRes: s.EffectRes.Apply(m.Op, v); break;
            case StatKind.AnomalyBuildUp: s.AnomalyBuildUp.Apply(m.Op, v); break;
        }
    }
}
