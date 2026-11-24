using System.Text.Json;
using System.Text.Json.Serialization;
using WaningOath.Core.Battle;
using ElementType = WaningOath.Core.Element;
using ElementUtil = WaningOath.Core.ElementInfo;

namespace WaningOath.Core.Data;

/// <summary>
/// 知道自己是从哪个文件读出来的定义。**只给校验器用**（`05 §7` 校验 1 / 11 要求
/// "`id` 与文件名一致"——不知道来源文件名就没法查这一条）。
/// </summary>
public interface ISourcedDef
{
    /// <summary>来源文件的主名（不含扩展名），例如 `char-shouyueren`。</summary>
    string SourceFile { get; set; }
}

/// <summary>
/// `data/**/*.json` 的反序列化模型。字段名与 `05 §3` 的 Schema 一一对应。
/// </summary>
/// <remarks>
/// <b>设计纪律</b>（`06 §8.3` 第 4 条的"接口层"要求）：
/// <list type="bullet">
///   <item>本文件的**类型必须覆盖文档里出现过的全部字段**，即使当前里程碑不实现它的行为 —— 否则数据文件无法声明它，
///         实现层就必然要回去改 Schema，那正是"先写死特例再回头抽象"。</item>
///   <item>未实现的原子**不报错**：解析通过、校验通过，只在真正执行时记一条"接口层已就位、实现层待 Mx"的日志。
///         判据见 `06 §8.3` 第 4 条。</item>
/// </list>
/// </remarks>
public sealed class BaseStatsDef
{
    public double Atk { get; set; }
    public double Hp { get; set; }
    public double Def { get; set; }
    public double Speed { get; set; } = 100.0;
    public double CritRate { get; set; } = BattleConstants.CritRateBase;
    public double CritDmg { get; set; } = BattleConstants.CritDmgBase;
    public double EnergyMax { get; set; }
    public double EnergyOverflowCap { get; set; } = BattleConstants.EnergyOverflowCapDefault;
    public double Threat { get; set; } = BattleConstants.DefaultThreat;
}

public sealed class GrowthDef
{
    public double PanelGrowth { get; set; } = 1.01;
    public Dictionary<string, double> BreakthroughBonus { get; set; } = [];
}

/// <summary>技能的伤害段。`01 §1.1` 的四种攻击形态在这里用 <see cref="Target"/> 表达。</summary>
public sealed class HitDef
{
    /// <summary>基础倍率。**百分数写成小数**（1125% → 11.25）。</summary>
    public double Multiplier { get; set; }

    /// <summary>本段倍率按目标已损失生命比例追加：`mult += lostHp * 本值`。</summary>
    public double LostHpMultiplier { get; set; }

    /// <summary>上述追加倍率的上限（加算部分）。</summary>
    public double LostHpCap { get; set; }

    /// <summary>`main` 主目标 · `adjacent` 相邻 · `all` 全体 · `random` 随机敌方单位。</summary>
    public string Target { get; set; } = "main";

    /// <summary>本段的属性。留空 → 用技能的属性。</summary>
    public string? Element { get; set; }

    /// <summary>重复段数（弹射 / 多段终结技）。`random` 目标时逐段独立随机。</summary>
    public int Times { get; set; } = 1;

    /// <summary>是否积蓄异常进度。默认 `true` —— 异常积蓄是"每次命中"（`01 §2.5`）。</summary>
    public bool AccumulateAnomaly { get; set; } = true;

    /// <summary>是否可暴击。默认 `true`（破冰不可暴击，见 `01 §3.2.2`）。</summary>
    public bool CanCrit { get; set; } = true;

    /// <summary>
    /// 该段是否**无视防御**（`01 §2.4`：附加伤害可写明"无视防御"）。
    /// 注意"降防 / 穿透"类效果必须按**实际增伤**标注，不得按"减了多少防御"标注（`01 §2.2`）。
    /// </summary>
    public bool IgnoreDefense { get; set; }

    /// <summary>
    /// 本段的**附加条件**（条件表达式，语法同 `05 §3.2` 的 `Conditions`）。不满足时本段按"朴素版"结算。
    /// </summary>
    /// <remarks>
    /// 首个使用者：守约人**风蚀模态**的强化元素共鸣 —— 它的"优先弹射未被本次技能攻击过的目标"
    /// **只在敌人 ≥ 3 人时启用**（`07 §1.3`）。写在段上而不是技能上，因为条件影响的是**一段**的行为。
    /// </remarks>
    public string? Condition { get; set; }

    /// <summary>
    /// 本段命中的**每个目标**各自追加执行一次的效果（例：为该目标施加若干层「可燃」/「风蚀」）。
    /// </summary>
    /// <remarks>
    /// **为什么不能只靠技能级的效果列表**：贝尔兰德的普攻是 2 段（主目标 + 相邻），
    /// 而"100% 基础概率为**命中的敌人**施加 1 层可燃"要求**每个被打到的人都吃一层**。
    /// 技能级效果只会给主目标上，段数与上层层数就对不上了。守约人风蚀模态同理。
    /// </remarks>
    public List<EffectDef> OnHit { get; set; } = [];

    /// <summary>
    /// **`01 §3.2.3` 的特例开关**（`100` #72）：本段施加「可燃」时，**不触发**该条内置的
    /// 「叠加即时伤害」（`现有层数 × 最新施加者攻击力`）。
    /// </summary>
    /// <remarks>
    /// 归属：**贝尔兰德**（`07 §8`）。理由：她的普攻 / 元素共鸣 / 专武特效**每一次都在施加可燃**，
    /// 若照通用规则走，她每回合都白送一次该伤害，而且它与她的「爆燃」**是同一个公式**
    /// ⇒ 两条同时生效等于每次普攻打 19× 攻击力。**她的引爆全部交给「爆燃」承担。**
    /// <para>⚠️ 这个开关**只给她**；`§3.2.3` 对其他人（含未来的火属性角色）**原样有效**。</para>
    /// </remarks>
    public bool SuppressCombustStackDamage { get; set; }
}

public sealed class SkillCostDef
{
    public double Energy { get; set; }
    public int ResonanceIndex { get; set; }

    /// <summary>额外的**资源型堆叠**消耗（例：强化元素共鸣要消耗 1 层「风涌」）。</summary>
    public string? Stack { get; set; }

    public int StackAmount { get; set; } = 1;

    /// <summary>
    /// **条件免消耗**：这条条件成立时**不扣** <see cref="Stack"/>，但技能照常释放。
    /// </summary>
    /// <remarks>
    /// 首个使用者：瓦列里娅的「冰原不灭的意志」——「意志」满 5 层时，**下一次元素共鸣不消耗「火种」**。
    /// <para>
    /// ⚠️ 它和 <see cref="Requires"/> 是**两件事**：`Requires` 决定"能不能放"（不满足就取消），
    /// `FreeWhen` 决定"要不要付钱"（不满足也照放）。
    /// </para>
    /// </remarks>
    public string? FreeWhen { get; set; }

    /// <summary>
    /// **每自然回合只能发动一次**（瓦列里娅的终结技）。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="Requires"/> / 资源消耗都不同：它约束的是**频率**。
    /// 没有它的话，"行动提前 / 额外回合 / 协同攻击"里任何一条路径
    /// 都能把同一个终结技在一回合内再放一次。
    /// </remarks>
    public bool OncePerRound { get; set; }

    /// <summary>
    /// **发动前提**（条件表达式，语法同 `05 §3.2` 的 `Conditions`）。不满足时**这个技能不能选**。
    /// </summary>
    /// <remarks>
    /// 首个使用者：守约人**风蚀模态**的强化元素共鸣 —— 它的开启条件是
    /// `usedUltimate` **且** `anomalyStacksAtLeast:wind:9`，与风涌模态的"消耗 1 层风涌"**完全不是一回事**。
    /// 放在 `cost` 而不是 `triggers` 里，是因为它决定的是"**能不能放**"，不是"放了之后触发什么"。
    /// </remarks>
    public string? Requires { get; set; }
}

public sealed class SkillGainDef
{
    public int ResonanceIndex { get; set; }
    public double Energy { get; set; }
}

/// <summary>
/// 效果原子。`05 §3.2` 的**全部** type 都用这一个类型承载（各取所需字段）。
/// </summary>
/// <remarks>
/// 用一个"宽"类型而不是二十个多态子类，是刻意的取舍：Schema 会随策划案持续长大，
/// 多态反序列化每加一个原子就要动一次 <c>JsonDerivedType</c> 注册表，而宽类型的代价只是几个可空字段。
/// 代价是**没有编译期字段检查**，因此 `tools` 的数据校验器（`M3`）是必需的，不是可选的。
/// </remarks>
public sealed class EffectDef
{
    public string Type { get; set; } = "";

    // ── 数值（按需取用） ──
    public double Amount { get; set; }
    public double Value { get; set; }
    public double Delta { get; set; }
    public double Duration { get; set; }
    public double BaseChance { get; set; }
    public double Intensity { get; set; }
    public double Multiplier { get; set; }
    public double PctMaxHp { get; set; }
    public double PctCurrentHp { get; set; }
    public double Flat { get; set; }
    public double HpPctOfCaster { get; set; }
    public double ThreatBonus { get; set; }
    public double Mult { get; set; } = 1.0;

    /// <summary>
    /// **机制旋钮乘区**：本条伤害的倍率再乘 `(1 + 施法者身上该旋钮的总和)`。
    /// </summary>
    /// <remarks>
    /// 首个使用者：瓦列里娅「燎原」期间无人机伤害 +20/30/50% —— 那个百分比是**状态上的旋钮**
    /// （<see cref="StatusDef.Knobs"/>），而"无人机攻击"是数据里的一个 `additionalDamage`。
    /// 有了这个字段，两者才接得上，而**不需要为某个角色写一个专用原子**
    /// （`06 §8.3`：不允许把任何一条写死成特例）。
    /// <para>
    /// 它是**通用**的：任何"某个伤害按身上某个开关量放大"的机制都可以用它，
    /// 用法与 `perStack`（按层数放大）同源，只是一个读旋钮、一个读层数。
    /// </para>
    /// </remarks>
    public string? Knob { get; set; }

    /// <summary>
    /// **按「目标身上某状态的层数」放大本条伤害的倍率**。
    /// </summary>
    /// <remarks>
    /// ⚠️ 与 <see cref="PerStack"/> 不是一回事，别混：`perStack` 读的是**施法者自己的资源计数**
    /// （风涌 / 誓言 / 余火），而这里要的是**被打的那个人身上挂了几层**。
    /// <para>
    /// 首个使用者：瓦列里娅「可爆火种」的引爆 —— 每层造成她 30% 攻击力的火伤，
    /// 而层数在敌人身上、不在她身上。用 `perStack` 会读到 0、然后被 `Math.Max(1, 0)` 兜成 1 层，
    /// 于是"每层 30%"会静默退化成"固定 30%"。
    /// </para>
    /// </remarks>
    public string? PerTargetStatus { get; set; }

    /// <summary>触发概率。**留空 = 必触发**（`null` 与 `0` 是两件事）。</summary>
    public double? Chance { get; set; }

    /// <summary>
    /// **概率也走旋钮**：写一个旋钮名，`chanceCheck` 会从施法者身上读它的当前总值。
    /// </summary>
    /// <remarks>
    /// 首个使用者：瓦列里娅的权阶Ⅵ「协同攻击消耗无人机时 50% 返还 1 架」——
    /// 概率本身是权阶给的（0 权阶没有这个旋钮），而 `chance: 0.5` 会把数字抄进数据两遍。
    /// 写成旋钮后，**加不加、加多少、叠几层**都只由权阶/装备那一侧决定。
    /// </remarks>
    public string? ChanceKnob { get; set; }

    // ── 层数 / 次数 / 上限 ──
    public int Stacks { get; set; } = 1;
    public int Cap { get; set; }
    public int Count { get; set; }
    public int Hits { get; set; } = 1;

    /// <summary>
    /// `perStack` 指向资源时，把总段数乘以「资源层数 × 本值」。0 = 不按层数加段。
    /// 首个使用者：罗兰达终结技 / 反击「段数 = 转速层数」。
    /// </summary>
    public int HitsPerStack { get; set; }

    /// <summary>`perStack` 指向的资源名 → 倍率按该资源的层数放大（`05 §3.2`）。</summary>
    public string? PerStack { get; set; }

    /// <summary>
    /// **「每次攻击」还是「每次命中」**（`01 §2.5` · 裁决 `100` #105/1）。
    /// </summary>
    /// <remarks>
    /// 取值只有两个：`perAttack`（一次技能算一次，**与命中段数无关**）与 `perHit`（按伤害实例逐段计）。
    /// <para>
    /// ⚠️ **默认是 `perAttack`**，且**缺失时校验器只报警告、不报错**（`05 §7` 校验 17）——
    /// 因为"沉默地按 `perAttack` 处理"本身是正确行为；`perHit` 必须**显式写**，
    /// 否则"多段弹射会不会叠 N 层"就会变成一个每次都要重新推的坑。
    /// </para>
    /// </remarks>
    public string? StackCounter { get; set; }

    /// <summary>
    /// **额外叠层也走旋钮**：`applyAnomalyStack` 的层数 = `stacks` + 该旋钮的当前值。
    /// </summary>
    /// <remarks>
    /// 首个使用者：瓦列里娅的「燎原」与权阶Ⅵ都写"无人机攻击额外叠 N 层可燃"——
    /// 额外层数一半来自状态（燎原三档 1/1/2 层），一半来自权阶（Ⅵ +1 层）。
    /// 两处都用同一个旋钮名 `droneExtraCombust`：<see cref="BattleUnit.KnobValue"/> 汇总状态那部分，
    /// <see cref="BattleUnit.EidolonKnobs"/> 提供权阶那部分，**数据只写一遍旋钮名**。
    /// </remarks>
    public string? ExtraStacksKnob { get; set; }

    // ── 新角色（薇拉 / 卡尔）带进来的通用字段 ──
    /// <summary>召唤物的攻击力 = 召唤者实时攻击力 × 本值（0 = 不继承）。</summary>
    public double SummonAtkPctOfCaster { get; set; }

    /// <summary>召唤物是否继承召唤者的暴击率 / 暴击伤害。</summary>
    public bool SummonInheritCrit { get; set; }

    /// <summary>召唤物是否为「守护召唤物」：召唤者受到的伤害改由它承担。</summary>
    public bool SummonGuardian { get; set; }

    /// <summary>本条伤害的倍率按目标**已损失生命比例**追加：`mult += lostHp * 本值`，再受 <see cref="LostHpCap"/> 限制。</summary>
    public double LostHpMultiplier { get; set; }

    /// <summary>上述追加倍率的**上限**（加算部分，不是最终倍率）。</summary>
    public double LostHpCap { get; set; }

    /// <summary>`executionSweep` / `stackDetonate` 要执行的技能键（例：`bloom`）。</summary>
    public string? SkillKey { get; set; }

    /// <summary>处决扫射：目标需要持有的状态名与层数门槛。</summary>
    public string? SweepStatus { get; set; }
    public int SweepStatusAtLeast { get; set; }

    /// <summary>处决扫射：目标生命比例门槛（0 = 不按血量判定）。</summary>
    public double SweepHpBelow { get; set; }

    /// <summary>门槛也可由权阶旋钮提供（例：`executeHpPct` / `verdictThreshold`）。</summary>
    public string? SweepHpBelowKnob { get; set; }
    public string? SweepStatusAtLeastKnob { get; set; }

    /// <summary>若该旋钮 &gt; 0，则扫射目标扩大为全体敌人（权阶Ⅵ）。</summary>
    public string? SweepAllTargetsKnob { get; set; }

    /// <summary>扫射期间是否屏蔽「击杀额外回合」（终结技内连续处决用）。</summary>
    public bool SuppressExtraTurns { get; set; }

    /// <summary>每处决 1 人后对施法者执行的效果（例：暴伤 +15%）。</summary>
    public List<EffectDef>? OnEach { get; set; }

    /// <summary>至少处决 1 人后执行的效果（例：全队「秩序」）。</summary>
    public List<EffectDef>? OnAny { get; set; }

    /// <summary>印记结算：达到该层数时追加一段全体伤害。</summary>
    public int BonusAtStacks { get; set; }
    public double BonusMultiplier { get; set; }
    public string? BonusTarget { get; set; }

    /// <summary>印记结算后是否清空该资源。</summary>
    public bool SpendAll { get; set; }

    // ── 引用 ──
    public string? Stack { get; set; }
    public string? BuffId { get; set; }
    public string? DebuffId { get; set; }

    /// <summary>
    /// 泛指的**状态 id**（`decayStatus` 用）。`applyBuff` / `applyDebuff` 仍走上面两个字段
    /// —— 那两个字段名本身就在告诉作者"这一条是好是坏"，保留它们能让 `05 §3.2` 的表格继续自解释。
    /// </summary>
    public string? StatusId { get; set; }

    /// <summary>
    /// **全场唯一**：施加状态时先把它从**其他所有单位**身上摘掉；**目标已持有则整个效果跳过**。
    /// </summary>
    /// <remarks>
    /// 首个使用者：瓦列里娅的「可爆火种」——档案原话是"**场上同时只能存在一个**被施加该状态的目标"，
    /// 而且它的驱散效果发生在**施加那一刻** ⇒ "目标已持有"时必须**什么都不做**
    /// （既不再驱散一次，也不叠层）。这两件事合起来正好是这一个布尔值。
    /// </remarks>
    public bool Unique { get; set; }

    public string? FieldId { get; set; }
    public string? SummonId { get; set; }
    public string? Element { get; set; }
    public string? Stat { get; set; }
    public string? SourceSummon { get; set; }
    public string? From { get; set; }
    public string? Condition { get; set; }

    /// <summary>
    /// **这条效果挂在哪个钩子上**（`M7` 新增 · `01 §2.5`）。
    /// </summary>
    /// <remarks>
    /// 留空 = **立刻结算**（绝大多数效果都是这样，例："两件套：风属性伤害 +10%"）。
    /// 填了钩子名 = 这条效果是**条件触发**的（例：忆库所藏的四件套是"**释放追加攻击后**我方全体暴击伤害 +15%"）。
    /// <para>
    /// ⚠️ 没有这个字段时，整套装备效果只能"建场时一次性挂上"——
    /// 于是"释放追加攻击后"会被错误地当成"开局就有"，而且 `Duration` 会从第 0 回合就开始走。
    /// </para>
    /// </remarks>
    public string? TriggerHook { get; set; }

    /// <summary>作用对象。见 <see cref="TargetSpec"/>。</summary>
    public string? Target { get; set; }

    /// <summary>`atk` 或 `hp` —— 生命倍率技能靠它表达（`05 §3` 明令不新增 `lifeScaling` 字段）。</summary>
    public string? ScalingBase { get; set; }

    // ── 开关 ──
    public bool Random { get; set; }
    public bool AccumulateAnomaly { get; set; } = true;
    public bool CountsAsUltimateDamage { get; set; }
    public bool IgnoreAdditionalDamage { get; set; }
    public bool ShieldOneHit { get; set; }
    public bool SourceSummonRemainingHp { get; set; }
    public bool IgnoreDefense { get; set; }
    public bool CanCrit { get; set; } = true;
    public bool IsUltimateDamage { get; set; }

    /// <summary>
    /// **`01 §3.2.3` 的特例开关**（`100` #72）：本次施加的「可燃」**不触发**内置的「叠加即时伤害」。
    /// 归属：贝尔兰德。见 <see cref="HitDef.SuppressCombustStackDamage"/>。
    /// </summary>
    public bool SuppressCombustStackDamage { get; set; }

    // ── 「可燃引爆」族（`detonateCombust` 原子专用，05 §3.2） ──────────────
    /// <summary>每层可燃提供的触发概率（例：`0.015` = 每层 1.5%）。</summary>
    public double ChancePerStack { get; set; }

    /// <summary>触发概率上限（例：`0.30` = 30%）。</summary>
    public double ChanceCap { get; set; }

    /// <summary>满足条件时**无视概率必定触发**所依据的资源名（例：`ember` = 余火）。</summary>
    public string? ForceStack { get; set; }

    /// <summary>`ForceStack` 达到该层数时必定触发（例：`6` = 余火满层）。</summary>
    public int ForceCount { get; set; }

    /// <summary>
    /// `tickAnomalyNow` 专用：**无视实际层数、按这个层数结算**（`0` = 用实际层数）。
    /// 使用者：爱丽丝二段终结技"**无视实际风蚀层数，按满层结算一次**"（`07 §9.3`）。
    /// </summary>
    public int CountAsLayer { get; set; }

    /// <summary>
    /// `tickAnomalyNow` 专用：**按"当时那一刻的层数上限"结算**。
    /// </summary>
    /// <remarks>
    /// ⚠️ 与 <see cref="CountAsLayer"/> 的区别是**要不要跟着上限走**：
    /// 风化（`01 §3.2.5` · `100` #88）会把风蚀上限 20 抬到 30（权阶Ⅵ），
    /// 所以"按满层结算"必须读上限，不能读一个写死的 20 —— 后者不会报错，只会安静地少算。
    /// </remarks>
    public bool CountAsFullCap { get; set; }

    /// <summary>必定触发时额外附加的伤害系数（例：`0.40` = +40%）。</summary>
    public double ForceBonus { get; set; }

    // ── `05 §3.2` 里写着、但早期 DTO 漏掉的字段（M3 补齐声明层） ──────────
    // 判据是 06 §8.3 第 4 条：**类型必须覆盖文档里出现过的全部字段**，
    // 即使当前里程碑不实现它的行为 —— 否则数据文件无法声明它，实现层就必然要回去改 Schema。

    /// <summary>
    /// 层数 **按谁的行动计时**：`self`（挂载者，默认）· `source`（施加者）。
    /// `applyBuff` / `applyDebuff` / `summon` 的计时依据（`05 §3.2`）。
    /// </summary>
    public string? TickSource { get; set; }

    /// <summary>`summon` 专用：召唤物生命的基数取**当前**还是**最大**（`current` / `max`）。</summary>
    public string? HpBase { get; set; }

    /// <summary>`summon` 专用：召唤物子类 `common` / `symbiont`（与 <see cref="StatusDef.SummonKind"/> 同义）。</summary>
    public string? SummonKind { get; set; }

    /// <summary>`coopAttack` 专用：协同攻击要选几名我方角色（默认 1）。</summary>
    public int TargetCount { get; set; } = 1;

    /// <summary>`modifyStat` 专用：`add` / `mul` —— 即时面板修正的运算方式（`05 §3.2`）。</summary>
    public string? Mode { get; set; }

    /// <summary>`changeEnergyCost` 专用：直接把消耗设成这个值（与 `delta` 二选一）。</summary>
    public double SetTo { get; set; }

    /// <summary>`advanceAction` / `delayAction` / `revive` 专用：比例（行动提前 X% / 复活生命 X%）。</summary>
    public double Pct { get; set; }

    // ── 列表 ──
    public List<string>? Targets { get; set; }
    public List<string>? Types { get; set; }
    public List<string>? ExclusiveTo { get; set; }
    public List<string>? Skills { get; set; }

    // ── 分支 ──
    public List<EffectDef>? OnSuccess { get; set; }
    public List<EffectDef>? OnFail { get; set; }
    public List<EffectDef>? Then { get; set; }
    public List<EffectDef>? OnDestroy { get; set; }
    public List<EffectDef>? OnExpire { get; set; }

    /// <summary>本原子是否带概率。`null` = 必定执行。</summary>
    [JsonIgnore]
    public bool HasChance => Chance.HasValue;
}

/// <summary>触发钩子。`05 §3.3` 的 hook 名原样使用。</summary>
public sealed class TriggerDef
{
    /// <summary>`onAttack` / `onHit` / `onCrit` / `onUltimateCast` / `onAllyResonanceCast` …（`05 §3.3`）。</summary>
    public string Hook { get; set; } = "";

    /// <summary>触发概率。**留空 = 必触发**。</summary>
    public double? Chance { get; set; }

    /// <summary>`self`（默认，仅自己触发）· `ally`（我方任意单位触发）· `enemy`（敌方单位触发）。</summary>
    public string Scope { get; set; } = "self";

    /// <summary>条件表达式（`M1` 支持 `always` / `hasStack:X` / `targetIsMinion` / `targetIsElitePlus`）。</summary>
    public string? Condition { get; set; }

    /// <summary>限定技能种类（`basic` / `resonance` / `ultimate`）。留空 = 不限。</summary>
    public string? SkillKind { get; set; }

    /// <summary>每个单位回合最多触发次数。0 = 不限。</summary>
    public int OncePerTurn { get; set; }

    /// <summary>
    /// 每个单位回合最多触发次数改由权阶旋钮给（例：薇拉「龙裔，永不腐朽」= `talentCap`）。
    /// 旋钮缺失时退回 <see cref="OncePerTurn"/>。
    /// </summary>
    public string? OncePerTurnKnob { get; set; }

    /// <summary>每场战斗最多触发次数。0 = 不限。</summary>
    public int OncePerBattle { get; set; }

    public List<EffectDef> Effects { get; set; } = [];
    public List<EffectDef>? OnSuccess { get; set; }
    public List<EffectDef>? OnFail { get; set; }

    [JsonIgnore]
    public bool HasChance => Chance.HasValue;
}

public sealed class SkillDef
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Name { get; set; } = "";
    public string? TargetType { get; set; }
    public string ScalingBase { get; set; } = "atk";
    public string ScalingArray { get; set; } = "basic";
    public SkillCostDef Cost { get; set; } = new();
    public SkillGainDef Gain { get; set; } = new();
    public List<HitDef> Hits { get; set; } = [];
    public List<EffectDef> OnCast { get; set; } = [];

    /// <summary>该技能自带的触发钩子（天赋 / 部分被动）。</summary>
    public List<TriggerDef> Triggers { get; set; } = [];

    public string Description { get; set; } = "";
    public string RawText { get; set; } = "";

    /// <summary>
    /// 战斗内克隆：**只深拷贝会被权阶改写的 Cost**，其余引用共享。
    /// 不这么做的话，“终结技 130→110”会把 `CharacterDef` 里的原始技能一起改掉，
    /// 下一场战斗 / 模拟器 / 服务端重放就会读到被污染的全局数据。
    /// </summary>
    public SkillDef CloneForBattle()
    {
        SkillDef copy = (SkillDef)MemberwiseClone();
        copy.Cost = new SkillCostDef
        {
            Energy = Cost.Energy,
            ResonanceIndex = Cost.ResonanceIndex,
            Stack = Cost.Stack,
            StackAmount = Cost.StackAmount,
            FreeWhen = Cost.FreeWhen,
            OncePerRound = Cost.OncePerRound,
            Requires = Cost.Requires,
        };
        return copy;
    }
}

/// <summary>
/// 升格模态（`01 §5.4`）。**只有双模态角色才写 `modes`**；单模态角色写单一 `skills`。
/// </summary>
/// <remarks>
/// ⚠️ **写法与 `05 §3` 的 schema 一致：模态 id 与 `default` 平级**，不是包在 `entries` 里：
/// <code>
/// "modes": { "default": "windsurge", "windsurge": { ... }, "winderosion": { ... } }
/// </code>
/// 因此模态 id 是**任意字符串**，只能用 <see cref="JsonExtensionDataAttribute"/> 接。
/// （早期版本写成了 `entries`，结果模态表永远是空的 —— 而 `default` 又能正常读到，
/// 表现为"默认模态对了、但技能表是空的"，是个很安静的坑。）
/// </remarks>
public sealed class ModesDef
{
    public string Default { get; set; } = "";

    /// <summary>未被 <see cref="Default"/> 接住的那些键 —— 也就是模态 id → 模态定义。</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement> RawModes { get; set; } = new(StringComparer.Ordinal);

    private Dictionary<string, ModeDef>? _entries;

    /// <summary>模态 id → 模态定义。首次访问时惰性解析。</summary>
    [JsonIgnore]
    public Dictionary<string, ModeDef> Entries
    {
        get
        {
            if (_entries is not null) return _entries;
            Dictionary<string, ModeDef> result = new(StringComparer.Ordinal);
            foreach (KeyValuePair<string, JsonElement> kv in RawModes)
            {
                if (string.Equals(kv.Key, "default", StringComparison.Ordinal)) continue;
                // **顺序固定**：字典遍历顺序不保证，所以按键排序后再插入，
                // 让"按插入顺序遍历模态"这种代码在不同机器上得到同样的结果（06 §1 的确定性纪律）。
                ModeDef? mode = kv.Value.Deserialize<ModeDef>(GameData.JsonOptions);
                if (mode is not null) result[kv.Key] = mode;
            }
            _entries = result;
            return result;
        }
    }
}

public sealed class ModeDef
{
    public string Name { get; set; } = "";
    public string? Element { get; set; }
    public Dictionary<string, SkillDef> Skills { get; set; } = [];

    /// <summary>本模态**有意不带的技能位**（05 §7 校验 20 的“确实该少就登记为差异”）。</summary>
    public List<string> SkillDifferences { get; set; } = [];
}

public sealed class EidolonDef
{
    public int Rank { get; set; }
    public string Name { get; set; } = "";
    public bool Shared { get; set; }
    public List<EffectDef> Effects { get; set; } = [];
    public List<TriggerDef> Triggers { get; set; } = [];
}

public sealed class LoreDef
{
    public string Quote { get; set; } = "";
    public List<string> NarrativeSkills { get; set; } = [];
    public List<string> FieldSkills { get; set; } = [];
    public List<string> Secrets { get; set; } = [];
    public string SpeechStyle { get; set; } = "";
    public Dictionary<string, string> Relationships { get; set; } = [];
}

/// <summary>`data/characters/&lt;id&gt;.json`。</summary>
public sealed class CharacterDef : ISourcedDef
{
    public string Id { get; set; } = "";

    /// <inheritdoc/>
    [JsonIgnore]
    public string SourceFile { get; set; } = "";
    public string Name { get; set; } = "";
    public string Title { get; set; } = "";
    public int Rarity { get; set; } = 5;

    /// <summary>单属性角色的属性。双属性角色改用 <see cref="Elements"/>。</summary>
    public string? Element { get; set; }

    /// <summary>双属性角色（例：群星誓约 = 物理 / 冰）。`01 §2.1`。</summary>
    public List<string>? Elements { get; set; }

    public string Path { get; set; } = "dps";

    /// <summary>
    /// **面板锚点行**（`02 §1.2` 的一行 · `data/tables/tiers.json` 的 `rows` 键）。
    /// </summary>
    /// <remarks>
    /// 为什么不直接用 <see cref="Path"/> 推：**定位（path）与面板锚点是两条轴**。
    /// 首个反例是爱丽丝 —— 她的 `path` 是 `dps`（生命倍率也属于输出），
    /// 但面板锚点必须走「生命主C」行（攻击 2,000 / 生命 15,000），
    /// 否则 `M2` 会把她按「常规主C 3,000 攻」跑，得到一份完全错误的 DPR。
    /// </remarks>
    public string AnchorRow { get; set; } = "";

    public string WeaponType { get; set; } = "dao";
    public List<string> Faction { get; set; } = [];
    public string Birthday { get; set; } = "";
    public bool GenderVariant { get; set; }
    public BaseStatsDef Base { get; set; } = new();
    public GrowthDef Growth { get; set; } = new();
    public Dictionary<string, SkillDef> Skills { get; set; } = [];
    public ModesDef? Modes { get; set; }
    public List<EidolonDef> Eidolons { get; set; } = [];
    public string? SignatureWeapon { get; set; }
    public string? GearSet { get; set; }

    /// <summary>有该角色在队时允许的编队人数上限（`null` = 不限制）。</summary>
    public int? TeamLimit { get; set; }
    public LoreDef Lore { get; set; } = new();

    /// <summary>
    /// **对某类敌方单位的最终伤害加成**（键 = `category`，值 = 加成比例）。
    /// </summary>
    /// <remarks>
    /// 首个使用者：爱丽丝的「**异常特攻**」（`07 §9.2`）—— 敌方单位类型为 `anomaly` 时，
    /// **我方全队**对该敌方单位的**最终伤害 +10%**。
    /// <para>
    /// 走 `01 §2.2 ⑪ 最终伤害区`（比增伤区更贵），且是**全队生效**的 ——
    /// 所以实现上按"同阵营存活单位里取最高"结算，**不叠加**（与「减伤取最高」同一条纪律）。
    /// </para>
    /// ⚠️ 没有 `01 §7` 的敌方单位类别字段，这条就会退化成"无条件全队最终伤害 +10%"（`100` #87）。
    /// </remarks>
    public Dictionary<string, double> FinalDamageVsCategory { get; set; } = [];

    /// <summary>
    /// **我方全体对「持有护盾 / 召唤物」目标的最终伤害加成**（取全队最高，不叠加）。
    /// 首个使用者：罗兰达天赋「外源驱动加特林」的拆垒特攻。
    /// </summary>
    public double FinalDamageVsShielded { get; set; }

    public Rarity RarityEnum => (Rarity)Rarity;

    /// <summary>角色拥有的全部属性（双属性角色有两个）。</summary>
    public ElementType[] ResolveElements()
    {
        if (Elements is { Count: > 0 }) return [.. Elements.Select(ElementUtil.Parse)];
        return Element is null ? [ElementType.Physical] : [ElementUtil.Parse(Element)];
    }

    /// <summary>
    /// 展开实际生效的技能表（`01 §5.4` 的模态展开）。
    /// **单模态角色没有 <see cref="Modes"/>，直接返回 <see cref="Skills"/>** —— 这就是向后兼容的那条分支。
    /// </summary>
    public IReadOnlyDictionary<string, SkillDef> ResolveSkills(string? modeId)
    {
        if (Modes is null || Modes.Entries.Count == 0) return Skills;
        string key = modeId is not null && Modes.Entries.ContainsKey(modeId) ? modeId : Modes.Default;
        if (!Modes.Entries.TryGetValue(key, out ModeDef? mode)) return Skills;
        return mode.Skills;
    }
}

/// <summary>`data/enemies/&lt;id&gt;.json`。</summary>
public sealed class EnemyDef : ISourcedDef
{
    public string Id { get; set; } = "";

    /// <inheritdoc/>
    [JsonIgnore]
    public string SourceFile { get; set; } = "";
    public string Name { get; set; } = "";
    public string CcrfCode { get; set; } = "";

    /// <summary>`minion` 杂兵 · `normal` 普通 · `elite` 精英 · `boss` 强敌 · `highboss` 首领。决定秒杀是否生效（`01 §6.2` 通则 6）。</summary>
    public string Tier { get; set; } = "minion";

    /// <summary>
    /// **敌方单位类别**（`01 §7` · `100` #87）：`human`（人形）/ `anomaly`（异常）/ `machine`（机械）。
    /// </summary>
    /// <remarks>
    /// ⚠️ **没有这个字段的话，"敌方单位类型为异常"这句话在本作里恒为真** —— 因为所有敌人都是异常体。
    /// 一条写着条件的特效（爱丽丝的「异常特攻」：对 `anomaly` 类敌人全队最终伤害 +10%）会变成
    /// **无条件的全队最终伤害 +10%**。
    /// </remarks>
    public string Category { get; set; } = "anomaly";

    public int Level { get; set; } = 85;
    public BaseStatsDef Base { get; set; } = new();

    /// <summary>全属性抗性。</summary>
    public double ResistAll { get; set; }

    /// <summary>按属性记录的额外抗性（留空 = 只吃 <see cref="ResistAll"/>）。</summary>
    public Dictionary<string, double> Resist { get; set; } = [];

    /// <summary>按异常家族的抗性（键 = 家族名，如 `ice`）——用于"积蓄更少 / 直接免疫"（`01 §7`）。</summary>
    public Dictionary<string, double> AnomalyResist { get; set; } = [];

    public List<SkillDef> Skills { get; set; } = [];
    public string AiProfile { get; set; } = "default";

    /// <summary>固有技能 / 起始效果。`04 §2.3` 的"只对伤害过它的个体做出永久反应"由行为脚本表达，不进 M1 数值内核。</summary>
    public List<EffectDef> OnBattleStart { get; set; } = [];

    [JsonIgnore]
    public bool IsEliteOrAbove => Tier is "elite" or "boss" or "highboss";
}

/// <summary>`data/stages/&lt;id&gt;.json`。**这张表同时是服务端关卡白名单**（`100` 戊10）。</summary>
public sealed class StageDef : ISourcedDef
{
    public string Id { get; set; } = "";

    /// <inheritdoc/>
    [JsonIgnore]
    public string SourceFile { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "main";

    /// <summary>
    /// **同一类刷本里的"哪一版"**：属性刷本写属性名（`physical` / `ice` / …），
    /// 武器素材本写武器类型（`dao` / `sword` / `focus` / `gun`）。
    /// </summary>
    /// <remarks>
    /// 主界面的「定期清剿」按 <see cref="Kind"/> 分组、按本字段排序与显示后缀 ——
    /// 「阶跃核心每个属性一关」这件事必须能**从数据里看出来**，而不是靠关卡名的中文去猜。
    /// </remarks>
    public string? Variant { get; set; }
    public List<string> Enemies { get; set; } = [];
    public int MaxRound { get; set; } = BattleConstants.MaxRoundPerStage;
    public EnvironmentDef Environment { get; set; } = new();

    /// <summary>我方上场角色（按站位，一号位在最前）。</summary>
    public List<string> Party { get; set; } = [];

    /// <summary>
    /// **每个上场角色选定的升格模态**（`01 §5.4`）。键 = 角色 id，值 = 模态 id。
    /// 缺省时用角色数据里的 `modes.default`；单模态角色忽略本字段。
    /// </summary>
    /// <remarks>
    /// ⚠️ **它必须挂在关卡 / 队伍上，不能挂在角色上** —— `01 §5.4` 定死了
    /// "进战斗前选定、**战斗内不可更改**"，所以"选哪个模态"是一份**编队决策**，与角色的静态数据无关。
    /// （存档里记"上次选的那个"作为下次默认，是客户端的便利行为，不影响服务器的战斗重放。）
    /// </remarks>
    public Dictionary<string, string> PartyModes { get; set; } = [];

    /// <summary>浅拷贝一份关卡，用于"玩家编队覆盖 stage.Party"而不污染 `GameData` 缓存。</summary>
    public StageDef Clone()
    {
        StageDef copy = (StageDef)MemberwiseClone();
        copy.Party = [.. Party];
        copy.PartyModes = new Dictionary<string, string>(PartyModes, StringComparer.Ordinal);
        return copy;
    }

    /// <summary>教学关用固定预设面板，不受账号养成影响（`04 §2.3`）。键 = 角色 id。</summary>
    public bool FixedPanel { get; set; }
    public int PresetLevel { get; set; } = 80;

    /// <summary>本关的技能等级（0 权阶、未突破时为 1 级）。</summary>
    public int SkillLevel { get; set; } = 1;

    /// <summary>教学关开场即满能量 —— 否则"演示终结技"这条教学目标在一场杂兵战里根本达不成。</summary>
    public bool InitialEnergyFull { get; set; }

    /// <summary>
    /// **关卡产出**（`04 §5` · `100` 戊10）。**这张表同时是服务端关卡白名单**：
    /// `stage/clear` 只接受表内 id，奖励由表驱动、不再硬编码。
    /// </summary>
    /// <remarks>
    /// ⚠️ `kind == "sim"` 的靶场（`data/stages/stage-sim-*.json`）**不发任何奖励** ——
    /// 它们是 `M2` 的平衡实测工具，不是游戏内容。服务端对它们直接拒绝结算。
    /// </remarks>
    public StageRewardDef Reward { get; set; } = new();
}

/// <summary>一关的产出定义。</summary>
public sealed class StageRewardDef
{
    /// <summary>说明这条产出的依据（写在数据里，便于人核对）。</summary>
    public string Note { get; set; } = "";

    /// <summary>是否可重复结算。**主线关卡不可重复刷取**（`04 §5.1`：防刷原码，重刷只给经验）。</summary>
    public bool Repeatable { get; set; }

    /// <summary>每次挑战消耗的运维配额（`02 §2.3` 的经验本是 40）。</summary>
    public int StaminaCost { get; set; }

    /// <summary>解锁所需的协议等级（`02 §2.3` / `04 §1`）。`0` = 无门槛。</summary>
    public int UnlockProtocolLevel { get; set; }

    /// <summary>首通奖励。</summary>
    public RewardBundle FirstClear { get; set; } = new();

    /// <summary>重复奖励（<see cref="Repeatable"/> 为 false 时忽略）。</summary>
    public RewardBundle Repeat { get; set; } = new();
}

/// <summary>一包奖励。</summary>
public sealed class RewardBundle
{
    /// <summary>协议原码。</summary>
    public int ProtocolCode { get; set; }

    /// <summary>授信额度。</summary>
    public int Credit { get; set; }

    /// <summary>材料 / 道具（键 = `data/tables/items.json` 的 id，值 = 个数）。</summary>
    public Dictionary<string, int> Items { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>环境区（`01 §2.2 ⑩`）——独立乘区，不属于增减益。</summary>
public sealed class EnvironmentDef
{
    public double All { get; set; } = 1.0;
    public double Anomaly { get; set; } = 1.0;
    public double Disorder { get; set; } = 1.0;
    public Dictionary<string, double> Element { get; set; } = [];
}

/// <summary>`data/effects/&lt;id&gt;.json` —— buff / debuff / 领域 / 召唤物的定义。</summary>
public sealed class StatusDef : ISourcedDef
{
    public string Id { get; set; } = "";

    /// <inheritdoc/>
    [JsonIgnore]
    public string SourceFile { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>`buff` · `debuff` · `field` · `summon`。</summary>
    public string Kind { get; set; } = "buff";

    /// <summary>
    /// **召唤物子类**（`01 §1.5` · `100` #90）：`common`（普通召唤物，默认不行动）/ **`symbiont`（共生灵）**。
    /// </summary>
    /// <remarks>
    /// 「召唤物是一个大类，共生灵是它下面的子类」—— 忆灵是一种召唤物，但召唤物并不全是忆灵；
    /// 同理静止镜像没有独立天赋和共生灵技，只是个普通的召唤物。
    /// **共生灵有共生灵技与共生灵天赋，靠"关联角色行动后插入额外回合"来行动。**
    /// </remarks>
    public string SummonKind { get; set; } = "common";

    /// <summary>召唤物自带的技能表（键 = 技能位名）。**只有共生灵才有**。</summary>
    public Dictionary<string, SkillDef> Skills { get; set; } = [];

    /// <summary>层数上限。</summary>
    public int StackCap { get; set; } = 1;

    /// <summary>是否可叠层（`false` 时重复施加只刷新时间）。</summary>
    public bool Stackable { get; set; } = true;

    /// <summary>`turn`（单位回合，默认）· `round`（自然回合）。依据 `01 §1.3` 的 buff 三分法。</summary>
    public string DurationUnit { get; set; } = "turn";

    /// <summary>缺省持续时间。技能可覆盖。</summary>
    public double Duration { get; set; } = 2.0;

    /// <summary>
    /// 作用范围。`null` = 只对挂载对象生效（自身增益）；`partyAll` = **光环**（`01 §1.3` 三分法的第二类：
    /// **挂在施加者身上、按施加者的行动次数计时、效果覆盖我方全体**）。
    /// </summary>
    public string? AppliesTo { get; set; }

    public List<StatMod> Mods { get; set; } = [];
    public List<ElemMod> ElemMods { get; set; } = [];

    /// <summary>
    /// **机制旋钮**（`M7`）：不是面板上的一个数，而是"引擎某处会去读的一个开关量"。
    /// </summary>
    /// <remarks>
    /// 例：`extraCombustChance` 改的是爆燃判定里的概率曲线，`detonateDamageBonus` 改的是它的伤害。
    /// 它们必须**跟着状态的生命周期走**（挂了才有、到期就没），所以挂在 <see cref="StatusDef"/> 上，
    /// 由 <see cref="BattleUnit.KnobValue"/> 汇总。
    /// </remarks>
    public Dictionary<string, double> Knobs { get; set; } = new(StringComparer.Ordinal);

    // ── 硬控 / 软控标记（01 §6.1） ──
    /// <summary>冻结：限制行动 1 回合；解除时结算破冰。</summary>
    public bool Freeze { get; set; }

    /// <summary>晕眩：1 回合无法行动。</summary>
    public bool Stun { get; set; }

    /// <summary>畏缩：打断当前动作 + 攻击力 −20%（脆弱的派生状态，不是独立异常）。</summary>
    public bool Cower { get; set; }

    /// <summary>该护盾**只吸收一次伤害实例**、剩余不结转（`05 §3.2` 的 `shieldOneHit`）。</summary>
    public bool ShieldOneHit { get; set; }

    /// <summary>
    /// 嘲讽：持有者成为敌方单体攻击的**优先目标**（`01 §7` 的强制仇恨）。
    /// 首个使用者：罗兰达「区域封锁」。
    /// </summary>
    public bool Taunt { get; set; }

    /// <summary>
    /// 致命伤害保护：本次伤害若会使生命降到 0，则免疫本次伤害并恢复「生命上限 × 本值」。
    /// 0 = 不启用。首个使用者：罗兰达战斗固有「黑林特制急救注射剂」。
    /// </summary>
    public double LethalGuardHealPct { get; set; }

    /// <summary>致命保护触发后给**我方全体**施加的状态 id（留空 = 不给）。</summary>
    public string? LethalGuardBuffId { get; set; }

    /// <summary>该状态消失时触发的效果引用（例：召唤物的 `onExpire`）。</summary>
    public List<EffectDef> OnEnemyEnter { get; set; } = [];

    /// <summary>状态**自然到期**时执行的效果（被驱散/净化不触发）。</summary>
    public List<EffectDef> OnExpire { get; set; } = [];

    /// <summary>持有该状态时，下一次自身元素绽放复制的增益持续时长 / 层数翻倍（薇拉「借你羽梢一瞬」）。</summary>
    public bool DoubleNextBloom { get; set; }

    /// <summary>持有该状态时，行动被替换为这个技能键（例：卡尔的「处决」）。</summary>
    public string? ForcedSkill { get; set; }

    /// <summary>强制行动的目标规则：`lowestHpEnemy`（默认）/ `self`。</summary>
    public string? ForcedTarget { get; set; }

    /// <summary>专拐标注（`02 §0` 宪法 5）：只对这些角色 id 生效；留空 = 通用拐。</summary>
    public List<string>? ExclusiveTo { get; set; }

    public string Description { get; set; } = "";

    /// <summary>给作者 / 实现者看的备注（不参与任何结算）。数据文件里的 `note`。</summary>
    public string Note { get; set; } = "";
}

// ────────────────────────────────────────────────────────────────────────
//  武器 · 装备 · 场外技能
//  这三类当前**不进战斗**（武器 / 装备属 M7），但它们是 M3 数据校验器的输入
//  （`05 §7` 校验 8 / 19、`100` 戊10、`104` 的"武器部件数按星级分级"）。
// ────────────────────────────────────────────────────────────────────────

/// <summary>武器的一条属性（主属性 / 副属性）。`101 §0` 的"白值 = 主 + 副"。</summary>
public sealed class WeaponStatLine
{
    /// <summary>属性名：`Atk` / `Hp` / `Def` / `CritRate` / `CritDmg` / `Speed`。</summary>
    public string Kind { get; set; } = "";

    /// <summary>`Pct`（百分比）或 `Add`（固定值）。</summary>
    public string Op { get; set; } = "";

    /// <summary>满级值。**百分比写成小数**（30% → `0.30`）。</summary>
    public double Value { get; set; }

    public string? Note { get; set; }
}

/// <summary>武器特效的一段（共五段，依次对应权限覆写 1~5 阶）。段内键名由各武器自定。</summary>
public sealed class WeaponTierDef
{
    public int Rank { get; set; }

    /// <summary>该段的全部数值键。**故意用扩展数据接**：五段里写什么由武器自己决定。</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement> Values { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>武器特效的触发声明（`01 §2.5` 的钩子名 + 持续与作用对象）。</summary>
public sealed class WeaponTriggerDef
{
    /// <summary>钩子名，取值见 `05 §3.3`（例：`onUltimateCast` / `onFollowUp` / `onAnomalyTrigger`）。</summary>
    public string Hook { get; set; } = "";

    /// <summary>持续几个回合；**`0` = 本场永久**（`05 §3.2` 的 `duration: 0` 口径）。</summary>
    public double Duration { get; set; }

    /// <summary>作用对象，取值见 <see cref="TargetSpec"/>（例：`self` / `partyAll`）。</summary>
    public string Target { get; set; } = "self";

    /// <summary>可选触发条件（与角色 TriggerDef.Condition 同一套语法）。</summary>
    public string? Condition { get; set; }

    /// <summary>`07` / `101` 的出处，供人核对。</summary>
    public string Note { get; set; } = "";

    /// <summary>
    /// **只把这些键挂到这个钩子上**；留空 = 该段的全部键都走这个钩子。
    /// </summary>
    /// <remarks>
    /// ⚠️ 需要它是因为**同一段特效里的键可以有不同时机**：
    /// 「斯瓦迪尔法利之焰剑」的 `extraCombustChance`（施加可燃时 +35% 额外叠一层）
    /// 与 `detonateDamageBonus`（引爆爆燃时 +8% 伤害）本来就是两件事。
    /// 共用一个钩子的话，前者要等到第一次爆燃之后才生效 —— 一个不会报错的偏差。
    /// </remarks>
    public List<string> Keys { get; set; } = [];
}

/// <summary>武器特效整块。</summary>
public sealed class WeaponEffectDef
{
    /// <summary>类型契合条件（例：`{"weaponType": "sword"}` —— 只有单刀角色能吃到）。</summary>
    public Dictionary<string, string> AppliesWhen { get; set; } = [];

    /// <summary>初始权限覆写档位（`101 §0.3`）。</summary>
    public int InitialOverrideRank { get; set; }

    /// <summary>
    /// **特效什么时候生效**（`M7` 新增）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 这一层在本轮之前是**缺的**：`tiers` 只给了数值，没给时机，"释放终结技后 2 回合"这句话
    /// 只存在于 `description` 的中文里 ⇒ 代码无从下手，特效就只能永远不生效。
    /// 补上它之后，五段特效才能被翻译成"挂一个带时长的状态"。
    /// </remarks>
    public WeaponTriggerDef? Trigger { get; set; }

    /// <summary>
    /// **多条触发声明**（同一段特效里的键分属不同时机时用；留空则退回 <see cref="Trigger"/>）。
    /// </summary>
    /// <remarks>
    /// 每条可以用 <see cref="WeaponTriggerDef.Keys"/> 限定自己负责哪些键。
    /// </remarks>
    public List<WeaponTriggerDef> Triggers { get; set; } = [];

    /// <summary>
    /// **不随权限覆写成长**的常驻部分（键 = 效果名，值 = 数值）。
    /// </summary>
    /// <remarks>
    /// `101 §5` 的"甲"路线把原稿里被挤出的**百分比副属性**（暴击率 / 能量恢复效率 / 暴击伤害）
    /// 并入特效 —— 其中「不落星辰」的能量恢复效率 +20% 是**常驻**的（五段里每一段都一样，
    /// 写五遍没有意义）。另外「晨星咏叹调」的固定生命 +1,200 也走这里
    /// （`101 §4.6`：它"不随等级成长、只随覆写成长"，而实际上它连覆写都不随）。
    /// </remarks>
    public Dictionary<string, double> Constant { get; set; } = [];

    public List<WeaponTierDef> Tiers { get; set; } = [];
    public string Description { get; set; } = "";
}

/// <summary>武器成长规则。`02 §6.2`：主 / 副属性吃等级曲线，特效只吃权限覆写。</summary>
public sealed class WeaponGrowthDef
{
    public bool MainAndSubGrowWithLevel { get; set; } = true;
    public double LevelFactor { get; set; } = 2.195;
    public bool EffectGrowsWithOverrideOnly { get; set; } = true;
}

/// <summary>`data/weapons/&lt;id&gt;.json`。</summary>
public sealed class WeaponDef : ISourcedDef
{
    public string Id { get; set; } = "";

    /// <inheritdoc/>
    [JsonIgnore]
    public string SourceFile { get; set; } = "";

    public string Name { get; set; } = "";
    public int Rarity { get; set; } = 5;
    public string WeaponType { get; set; } = "dao";

    /// <summary>专属角色 id。**留空 = 通用武器**。</summary>
    public string? Owner { get; set; }

    /// <summary>主属性。**每一把武器都必须有**（`101 §0.1`：三 / 两 / 一件里都含主属性）。</summary>
    public WeaponStatLine? MainStat { get; set; }

    /// <summary>副属性。**只有 5★ 有**（`101 §0.1` · 校验 8）。</summary>
    public WeaponStatLine? SubStat { get; set; }

    /// <summary>特效。**只有 5★ / 4★ 有**；3★ 只有主属性一件。</summary>
    public WeaponEffectDef? Effect { get; set; }

    public WeaponGrowthDef Growth { get; set; } = new();
    public string Note { get; set; } = "";
    public string Flavor { get; set; } = "";

    /// <summary>`101 §0.1` 的**部件数**：5★ = 主 + 副 + 特效（三件）/ 4★ = 主 + 特效（两件）/ 3★ = 主（一件）。</summary>
    [JsonIgnore]
    public int PartCount => (MainStat is null ? 0 : 1) + (SubStat is null ? 0 : 1) + (Effect is null ? 0 : 1);
}

/// <summary>`data/gears/sets/&lt;id&gt;.json` —— 装备套装（原"遗器"，术语已改，`100` #40）。</summary>
/// <remarks>
/// ⚠️ 目前**尚未实装**（属 M7）。角色数据里的 `gearSet` 现在指向的是**规划中的 id**，
/// 因此校验器对"套装未定义"只报警告、不报错 —— 报错会让 M3 永远无法通过，
/// 而那两个 id 是否落地是 M7 的事。
/// </remarks>
public sealed class GearSetDef : ISourcedDef
{
    public string Id { get; set; } = "";

    /// <inheritdoc/>
    [JsonIgnore]
    public string SourceFile { get; set; } = "";

    public string Name { get; set; } = "";
    public int Rarity { get; set; } = 5;

    /// <summary>套装件数阈值 → 效果（例：`{"2": ..., "4": ...}`）。</summary>
    public List<GearSetTierDef> Tiers { get; set; } = [];
    public string Description { get; set; } = "";
}

public sealed class GearSetTierDef
{
    /// <summary>需要的件数（2 / 4）。</summary>
    public int Pieces { get; set; }

    public List<EffectDef> Effects { get; set; } = [];
    public string Description { get; set; } = "";
}

/// <summary>
/// `data/tables/field-skills.json` 里的一条**场外技能登记**（`01 §8.1`）。
/// </summary>
/// <remarks>
/// **为什么必须有这张表**：`05 §7` 校验 19 要求"`fieldSkills` 必须同时登记在 `03` 的产出模型里"。
/// `03` 是 markdown，机器读不了 ⇒ 把 `03 §2` 那张表**镜像成 JSON**，
/// 校验器只认这一份；`03` 改了而这里没改，就是一条真实的错误。
/// </remarks>
public sealed class FieldSkillRegistry
{
    public string Note { get; set; } = "";
    public List<FieldSkillDef> Skills { get; set; } = [];
}

public sealed class FieldSkillDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Owner { get; set; } = "";

    /// <summary>生效区域（例：`safeZone` 安全区）。</summary>
    public string Zone { get; set; } = "";

    /// <summary>真实时间口径（例：`perHour` 每小时判定一次）。</summary>
    public string Cadence { get; set; } = "";

    /// <summary>`03 §2` 里的登记位置，供人核对。</summary>
    public string RegisteredIn { get; set; } = "";

    public string Description { get; set; } = "";
}

// ────────────────────────────────────────────────────────────────────────
// 投入档位（`data/tables/tiers.json`）
// ────────────────────────────────────────────────────────────────────

/// <summary>一个**定位行**的面板锚点（`02 §1.2` 的一行）。</summary>
public sealed class TierRowDef
{
    public string Name { get; set; } = "";

    /// <summary>80 级**裸装**攻击 = 1 级攻击 × 1.01^79。</summary>
    public double AtkNaked { get; set; }

    /// <summary>80 级**裸装**生命 = 1 级生命 × 1.01^79。</summary>
    public double HpNaked { get; set; }

    public double AtkFull { get; set; }
    public double HpFull { get; set; }
    public double DefFull { get; set; } = 2000.0;
    public double SpeedFull { get; set; } = 105.0;
    public double CritRateFull { get; set; } = 0.05;
    public double CritDmgFull { get; set; } = 1.50;
}

/// <summary>一个投入档位（`02 §8.1.1` 的一行）。</summary>
public sealed class InvestmentTierDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>面板完成度：插值系数 `裸装 → 满配`（0 / 0.35 / 0.70 / 1.00）。</summary>
    public double PanelK { get; set; }

    /// <summary>暴击率的完成度（由 `02 §8.1.1` 的四行锚点反解）。</summary>
    public double CritRateK { get; set; }

    /// <summary>面板暴击伤害的完成度。</summary>
    public double CritDmgK { get; set; }

    /// <summary>是否带专属武器。**M2 只用于标注**（面板已由 <see cref="PanelK"/> 表达）。</summary>
    public bool HasSignatureWeapon { get; set; }

    public string Note { get; set; } = "";
}

/// <summary>`data/tables/tiers.json`。</summary>
public sealed class TierTable
{
    public int Version { get; set; } = 1;
    public string Source { get; set; } = "";

    /// <summary>四档共用的技能等级。**0 权阶 → 10 级**（`02 §4`）。</summary>
    public int SkillLevel { get; set; } = 10;

    public Dictionary<string, TierRowDef> Rows { get; set; } = new(StringComparer.Ordinal);
    public List<InvestmentTierDef> Tiers { get; set; } = [];

    /// <summary>按 id 取档位（大小写敏感，与其它 id 一致）。</summary>
    public InvestmentTierDef? Find(string id)
        => Tiers.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.Ordinal));

    /// <summary>按 id 取定位行。</summary>
    public TierRowDef? Row(string? key)
        => key is not null && Rows.TryGetValue(key, out TierRowDef? r) ? r : null;
}

// ────────────────────────────────────────────────────────────────────────
// 养成规则（`data/tables/growth.json`）
// ────────────────────────────────────────────────────────────────────

/// <summary>等级曲线的一段增长率。</summary>
public sealed class LevelRateDef
{
    public int FromLevel { get; set; }
    public int ToLevel { get; set; }
    public double Rate { get; set; } = 1.10;
}

/// <summary>`data/tables/growth.json` 的 `level` 段。</summary>
public sealed class LevelRuleDef
{
    public int Cap { get; set; } = 80;
    public List<int> BreakthroughNodes { get; set; } = [20, 40, 60, 80];
    public double ExpBase { get; set; } = 200;
    public List<LevelRateDef> RateTiers { get; set; } = [];
    public double TotalExp { get; set; } = 2928936;
}

/// <summary>一条突破消耗。</summary>
public sealed class BreakthroughCostDef
{
    public int Node { get; set; }
    public int Count { get; set; }
}

/// <summary>一级技能升级消耗（**注意与技能的释放消耗 <see cref="SkillCostDef"/> 是两回事**）。</summary>
public sealed class SkillUpgradeCostDef
{
    public int ToLevel { get; set; }
    public string Material { get; set; } = "";
    public int Count { get; set; }
}

/// <summary>一个道具 / 材料的定义。</summary>
public sealed class ItemDef
{
    public string Name { get; set; } = "";

    /// <summary>`exp`（经验道具）/ `ascension`（突破）/ `skill`（技能材料）/ `gear` / `weapon`。</summary>
    public string Kind { get; set; } = "";

    /// <summary>经验道具提供的经验；非经验道具为 0。</summary>
    public double Exp { get; set; }
}

/// <summary>`data/tables/growth.json` 的 `breakthrough` 段。</summary>
public sealed class BreakthroughRuleDef
{
    public string Material { get; set; } = "jumpCore";
    public List<BreakthroughCostDef> Costs { get; set; } = [];
}

/// <summary>`data/tables/growth.json` 的 `skill` 段。</summary>
public sealed class SkillRuleDef
{
    /// <summary>键 = 星级（`"5"` / `"4"`），值 = 权阶 0~6 各自的技能等级上限。</summary>
    public Dictionary<string, List<int>> CapsByEidolon { get; set; } = new(StringComparer.Ordinal);

    public List<SkillUpgradeCostDef> Costs { get; set; } = [];
}

/// <summary>`data/tables/growth.json` 的 `eidolon` 段。</summary>
public sealed class EidolonRuleDef
{
    public int Cap { get; set; } = 6;
}

/// <summary>`data/tables/growth.json`。</summary>
public sealed class GrowthTable
{
    public int Version { get; set; } = 1;
    public LevelRuleDef Level { get; set; } = new();
    public Dictionary<string, double> ExpItems { get; set; } = new(StringComparer.Ordinal);
    public BreakthroughRuleDef Breakthrough { get; set; } = new();
    public SkillRuleDef Skill { get; set; } = new();
    public EidolonRuleDef Eidolon { get; set; } = new();
    public Dictionary<string, ItemDef> Items { get; set; } = new(StringComparer.Ordinal);

    // ── 兼容别名：数据是嵌套的，而调用点写成扁平的可读性更好。 ──
    public string BreakthroughMaterial => Breakthrough.Material;
    public List<BreakthroughCostDef> BreakthroughCosts => Breakthrough.Costs;
    public Dictionary<string, List<int>> SkillCapsByEidolon => Skill.CapsByEidolon;
    public List<SkillUpgradeCostDef> SkillCosts => Skill.Costs;
    public int EidolonCap => Eidolon.Cap;

    /// <summary>
    /// 道具 id → **显示名**（查不到就退回 id 本身）。
    /// </summary>
    /// <remarks>
    /// 存在的理由很朴素：材料不足的提示里写 <c>jumpCore×2</c> 玩家看不懂，
    /// 而"阶跃核心×2"是数据里本来就有的 <c>items.jumpCore.name</c>。**不要在各处硬编码中文名。**
    /// </remarks>
    public string ItemName(string id)
        => Items.TryGetValue(id, out ItemDef? def) && def.Name.Length > 0 ? def.Name : id;
}

// ────────────────────────────────────────────────────────────────────────
// 抽卡配置（`data/tables/gacha.json`）
// ────────────────────────────────────────────────────────────────────

public sealed class GachaRateDef
{
    public double Five { get; set; } = 0.006;
    public double Four { get; set; } = 0.051;
    public int SoftPityStart { get; set; } = 55;
    public double SoftPityStep { get; set; } = 0.06;
    public int HardPity { get; set; } = 80;
    public int FourPityHits { get; set; } = 10;
}

public sealed class GachaCostDef
{
    public int ProtocolCodePerPull { get; set; } = 160;
}

/// <summary>一个卡池。</summary>
public sealed class GachaPoolDef
{
    public string Name { get; set; } = "";

    /// <summary>存档里记五星保底计数的键（`standardPity` / `limitedPity` / `weaponPity`）。</summary>
    public string PityKey { get; set; } = "";

    /// <summary>存档里记"下次必定是当期限定"的键；`null` = 该池没有大保底。</summary>
    public string? GuaranteedKey { get; set; }

    public List<string> Five { get; set; } = [];
    public List<string> Four { get; set; } = [];
    public List<string> ThreeWeapons { get; set; } = [];
}

/// <summary>`data/tables/gacha.json`。</summary>
public sealed class GachaTable
{
    public int Version { get; set; } = 1;
    public GachaCostDef Cost { get; set; } = new();
    public GachaRateDef Rates { get; set; } = new();
    public Dictionary<string, GachaPoolDef> Pools { get; set; } = new(StringComparer.Ordinal);
}

// ────────────────────────────────────────────────────────────────────────
// 装备规则（`data/tables/gear.json`）
// ────────────────────────────────────────────────────────────────────

/// <summary>一个装备部位。</summary>
public sealed class GearSlotDef
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>1~6，决定它属于四件套还是两件套（`02 §6.1`）。</summary>
    public int Order { get; set; }

    /// <summary>该部位**能出的主属性**。身份铭牌与执行芯片是固定池（只有一条）。</summary>
    public List<string> MainStats { get; set; } = [];
}

/// <summary>一条主属性的上限。</summary>
public sealed class GearMainStatDef
{
    public string Name { get; set; } = "";

    /// <summary>`pct` 或 `flat`。</summary>
    public string Kind { get; set; } = "pct";

    /// <summary>**+15 的值**（`02 §6.3`）。</summary>
    public double Max15 { get; set; }
}

/// <summary>一条副属性的上限。</summary>
public sealed class GearSubStatDef
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "pct";

    /// <summary>**满级上限**（= 一条全中 5 次）；单次强化 = <see cref="Max"/> ÷ 5（`02 §6.4`）。</summary>
    public double Max { get; set; }

    /// <summary>单次强化的值。</summary>
    public double PerRoll => Max / 5.0;
}

/// <summary>`data/tables/gear.json` 的 `rules` 段。</summary>
public sealed class GearRulesDef
{
    public int MaxLevel { get; set; } = 15;
    public int SubStatInitCount { get; set; } = 3;
    public int SubStatFullCount { get; set; } = 4;
    public int SubStatFullAtLevel { get; set; } = 3;
    public int EnhanceHits { get; set; } = 5;
    public List<int> EnhanceHitLevels { get; set; } = [3, 6, 9, 12, 15];
    public List<string> FourPieceSlots { get; set; } = [];
    public List<string> TwoPieceSlots { get; set; } = [];
}

// ────────────────────────────────────────────────────────────────────────
// 剧情表（`data/tables/stories.json`）
// ────────────────────────────────────────────────────────────────────

/// <summary>
/// 一段剧情（章）。
/// </summary>
/// <remarks>
/// <b>它存在的理由是防刷</b>：`06 §2.2` 的 `POST /api/game/story/clear` 原先接受**任意
/// `storyId` 字符串**，每个新字符串都算一次首通 ⇒ 一次 `POST` 就能白拿 160 原码（`100` 戊10）。
/// 要拦住它，服务端就必须有一张"剧情白名单" —— 而在此之前它**根本不存在**。
/// </remarks>
public sealed class StoryDef : ISourcedDef
{
    public string Id { get; set; } = "";

    /// <inheritdoc/>
    [JsonIgnore]
    public string SourceFile { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>`main`（主线）/ `event`（活动）。</summary>
    public string Kind { get; set; } = "main";

    /// <summary>
    /// **通关哪一关之后算这一章结束**（必须是一个已登记的 `stage-` id）。
    /// </summary>
    /// <remarks>
    /// 它把"章节"与"关卡"连起来：`04 §3.1` 的 S9 是结算，而 S7 才是战斗，
    /// 所以"这一章通关了没有"必须有一个可判定的锚点。
    /// </remarks>
    public string AnchorStage { get; set; } = "";

    public RewardBundle Reward { get; set; } = new();

    /// <summary>是否可重复领取。**主线章节不可**（`03 §4.1`）。</summary>
    public bool Repeatable { get; set; }

    public string Note { get; set; } = "";
}

/// <summary>`data/tables/stories.json`。</summary>
public sealed class StoryTable
{
    public int Version { get; set; } = 1;
    public List<StoryDef> Stories { get; set; } = [];
}
/// <summary>`data/tables/gear.json`。</summary>
public sealed class GearTable
{
    public int Version { get; set; } = 1;
    public List<GearSlotDef> Slots { get; set; } = [];
    public Dictionary<string, GearMainStatDef> MainStats { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, GearSubStatDef> SubStats { get; set; } = new(StringComparer.Ordinal);
    public GearRulesDef Rules { get; set; } = new();

    /// <summary>`+0` 的主属性占 +15 的比例（`02 §6.2` 没给，本文件里标了【我提议】）。</summary>
    public double MainStatAtZeroPct { get; set; } = 0.25;

    /// <summary>
    /// **掉落表**：关卡 `kind` → 该类型关卡会掉的套装 id 列表（`M7`）。
    /// </summary>
    /// <remarks>
    /// ⚠️ `02 §6.5` 写的是"套装基本为角色专属，一个角色一套"，**没有给掉落来源**。
    /// 所以这张表是【我提议】：按关卡类型分发到已落盘的 7 套上，便于 M7 的闭环跑通。
    /// 没有配的类型（`sim` / `exp`）就是**不掉装备**。
    /// </remarks>
    public Dictionary<string, List<string>> Drop { get; set; } = new(StringComparer.Ordinal);

    /// <summary>首通 / 重复各掉几件。</summary>
    public Dictionary<string, int> DropCount { get; set; } = new(StringComparer.Ordinal);

    public GearSlotDef? Slot(string? key)
        => key is null ? null : Slots.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.Ordinal));

    /// <summary>主属性在 <paramref name="level"/> 级的值（线性插值到 +15 = 满级）。</summary>
    public double MainStatAt(string statKey, int level)
    {
        if (!MainStats.TryGetValue(statKey, out GearMainStatDef? def)) return 0.0;
        int max = Math.Max(1, Rules.MaxLevel);
        double t = Math.Clamp((double)level / max, 0.0, 1.0);
        return def.Max15 * (MainStatAtZeroPct + (1.0 - MainStatAtZeroPct) * t);
    }
}
