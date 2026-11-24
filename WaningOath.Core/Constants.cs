namespace WaningOath.Core;

/// <summary>
/// 战斗内核的全部常量。**唯一来源是 `02 §10 常数列`** —— 这里不允许出现文档里没有的数字。
/// </summary>
/// <remarks>
/// 纪律（`06 §1`）：Core 是**纯 C#、零 Godot 依赖、逐位确定**的。
/// 因此本文件里只能有编译期常量与只读表，不允许读时间、环境变量、区域性设置。
/// </remarks>
public static class BattleConstants
{
    // ── 行动条（01 §1.2） ────────────────────────────────────────────────
    /// <summary>行动值公式分子：`AV = AV_BASE / 速度`。</summary>
    public const double AvBase = 10000.0;

    /// <summary>一个自然回合的行动值窗口（01 §1.3）。</summary>
    public const double RoundAv = 100.0;

    // ── 伤害管线（01 §2.2） ─────────────────────────────────────────────
    /// <summary>比率减伤常数：`R = D / (D + DEF_K)`。取代已作废的减法防御与防御下限保护。</summary>
    public const double DefK = 2400.0;

    /// <summary>基础暴击率。</summary>
    public const double CritRateBase = 0.05;

    /// <summary>基础**面板暴击伤害 = 暴击总倍率**（不是"额外加成"）。</summary>
    public const double CritDmgBase = 1.50;

    /// <summary>面板暴击伤害上限（卷王）。</summary>
    public const double CritDmgCap = 2.99;

    /// <summary>越级惩罚：每差 1 级 −1%。**仅单向**——只有等级低的一方降伤。</summary>
    public const double LevelPenaltyPer = 0.01;

    /// <summary>越级惩罚上限（−30%）。</summary>
    public const double LevelPenaltyCap = 0.30;

    // ── 共鸣指数（01 §4.1 / 107） ───────────────────────────────────────
    public const int ResonanceStart = 3;
    public const int ResonanceCap = 5;

    /// <summary>透支：点数为 0 时强行释放元素共鸣，扣除释放者 25% 生命上限。</summary>
    public const double ResonanceOverdrawHpPct = 0.25;

    // ── 能量（01 §4.2） ────────────────────────────────────────────────
    /// <summary>受击统一回复 5 点能量。</summary>
    public const double EnergyOnHit = 5.0;

    /// <summary>能量溢出储存的默认值。有效上限 = 能量上限 + 本值。</summary>
    public const double EnergyOverflowCapDefault = 0.0;

    // ── 关卡（02 §10） ─────────────────────────────────────────────────
    public const int MaxRoundPerStage = 15;

    // ── 属性异常（01 §3） ──────────────────────────────────────────────
    /// <summary>进度条满值。</summary>
    public const double AnomalyBarMax = 100.0;
    /// <summary>每次**命中**（不是每次攻击，见 01 §2.5）的积蓄量。</summary>
    public const double AnomalyGainPerHit = 10.0;

    /// <summary>进度条每触发一次施加的层数。</summary>
    public const int AnomalyStackPerTrigger = 1;

    /// <summary>抗性超过此值且无积蓄效率加成 → 完全无法积蓄。</summary>
    public const double AnomalyResistBlock = 0.10;

    /// <summary>脆弱：每层 10% 全属性易伤，上限 10 层，持续 3 回合（单位回合）。</summary>
    public const double FragileVulnPerStack = 0.10;
    public const int FragileStackCap = 10;
    public const double FragileDurationTurns = 3.0;

    /// <summary>畏缩：脆弱满 10 层时进入，脆弱延长至 8 回合，攻击力 −20%。</summary>
    public const double CowerDurationTurns = 8.0;
    public const double CowerAtkDown = 0.20;

    /// <summary>冻结：限制 1 回合行动；解除时结算破冰。</summary>
    public const double FreezeDurationTurns = 1.0;

    /// <summary>破冰：冻结期间承受的任何伤害之和 × 30%，**不可暴击**，不视为任何单位造成的伤害。</summary>
    public const double BurstIceRatio = 0.30;

    /// <summary>可燃：每层受到的火属性伤害 +5%，上限 20 层，持续 2 **自然回合**。</summary>
    public const double BurnVulnPerStack = 0.05;
    public const int BurnStackCap = 20;
    public const double BurnDurationRounds = 2.0;

    /// <summary>引雷：1 回合后造成施加者实时攻击力 200% 的雷伤 + 晕眩 1 回合。</summary>
    public const double LightningDelayMult = 2.00;
    public const double StunDurationTurns = 1.0;

    /// <summary>风蚀：每层每自然回合造成施加者面板生命 10% 的风伤，上限 20 层，持续 2 回合。</summary>
    public const double WindErosionHpPctPerStack = 0.10;
    public const int WindErosionStackCap = 20;
    public const double WindErosionDurationRounds = 2.0;

    /// <summary>复伤：2 回合后回放被施加者受到的最近一次伤害；暴击时二次伤害按 50% 概率以 100% 基础暴伤暴击。</summary>
    public const double ReplayDelayRounds = 2.0;
    public const double ReplayCritChance = 0.50;
    public const double ReplayCritBonus = 1.00;

    /// <summary>紊乱：`这两次异常效果本身造成的伤害之和 × 250%` 真实伤害；每自然回合限 1 次。</summary>
    public const double DisorderMult = 2.50;
    public const int DisorderPerRound = 1;

    /// <summary>无伤害异常计入紊乱基数时的系数（× 施加者攻击力）。</summary>
    public const double DisorderNoDamageBase = 1.00;

    // ── 敌方 AI（01 §7） ───────────────────────────────────────────────
    public const double DefaultThreat = 50.0;

    /// <summary>仇恨权重下限。仇恨值不会降到 0 而被完全无视。</summary>
    public const double ThreatMin = 1.0;

    // ── 召唤物（01 §1.5） ──────────────────────────────────────────────
    /// <summary>我方召唤物同时存在的数量上限。</summary>
    public const int SummonFieldCap = 3;

    // ── 秒杀（01 §6.2 通则 6） ─────────────────────────────────────────
    /// <summary>一切直接秒杀 / 处决对**精英级及以上**无效，只对杂兵生效。</summary>
    public const bool ExecuteEliteImmune = true;

    // ── 会话 / 经济（不参与战斗，但与上面同出一处：`02 §10` 的常数列） ──

    /// <summary>`STAMINA_MAX`：运维配额上限。</summary>
    public const int StaminaMax = 240;

    /// <summary>`STAMINA_REGEN_SEC`：每点配额恢复秒数（6 分钟）。</summary>
    public const double StaminaRegenSec = 360.0;

    /// <summary>`RESET_HOUR`：每日 / 每周重置时刻（服务器本地时间 **04:00**；周重置为周一 04:00）。</summary>
    public const int ResetHour = 4;

    /// <summary>`GACHA_COST`：一抽消耗的协议原码（`03 §4.2`）。</summary>
    public const int GachaCostPerPull = 160;

    /// <summary>`AUDIT_CAP`：审计日志保留的最近条数（`02 §10`）。</summary>
    public const int AuditCap = 4000;

    // ── 技能等级修正系数数组（01 §5.2） ────────────────────────────────
    /// <summary>索引 0 = 1 级，索引 13 = 14 级。</summary>
    /// <summary>
    /// 技能等级修正数组（`01 §5.2`）。索引 0 = 1 级，索引 13 = 14 级。
    /// </summary>
    /// <remarks>
    /// ⚠️ **这是"相对成长曲线"，不是绝对值**（口径裁决 #62 方案 B）：
    /// 每个数组的 1 级值只表示"这条曲线从哪里起步"，`LevelMult` 会**除以它**归一化，
    /// 因此 **1 级恒等于 ×1.00**。倍率数值本体写在 `07` 的角色技能表里，且那个值**就是 1 级的最终倍率**。
    /// <para>
    /// 换句话说：`01 §5.2` 管的是"这个技能从 1 级升到 14 级能涨多少倍"，
    /// `07` 管的是"1 级是多少"。两处职责不再重叠。
    /// </para>
    /// </remarks>
    public static readonly double[] BasicLevelMult =
        [0.50, 0.54, 0.58, 0.62, 0.66, 0.70, 0.74, 0.78, 0.82, 0.86, 0.90, 0.94, 0.98, 1.02];

    public static readonly double[] ResonanceLevelMult =
        [1.00, 1.08, 1.16, 1.24, 1.32, 1.40, 1.48, 1.56, 1.64, 1.72, 1.80, 1.88, 1.96, 2.04];

    public static readonly double[] ResonanceSpreadLevelMult =
        [0.70, 0.76, 0.82, 0.88, 0.94, 1.00, 1.06, 1.12, 1.18, 1.24, 1.30, 1.36, 1.42, 1.48];

    public static readonly double[] UltimateLevelMult =
        [3.80, 4.10, 4.40, 4.70, 5.00, 5.40, 5.70, 6.00, 6.30, 6.60, 6.90, 7.20, 7.50, 7.80];

    /// <summary>不修正（敌人技能 / 召唤物 / 附加伤害）。</summary>
    private static readonly double[] NoneLevelMult = [1.0];

    /// <summary>按倍率数组名取等级修正系数。<paramref name="level"/> 是 1 起的技能等级。</summary>
    public static double LevelMult(string scalingArray, int level)
    {
        double[] table = scalingArray switch
        {
            "basic" => BasicLevelMult,
            "resonance" => ResonanceLevelMult,
            "resonance_spread" => ResonanceSpreadLevelMult,
            "ultimate" => UltimateLevelMult,
            // "none" = 不修正。**敌人技能与召唤物**用它 —— 它们的倍率是最终值，
            // 不经过 01 §5.2 的角色技能等级数组。
            "none" => NoneLevelMult,
            // 未登记数组名：按"不修正"处理并留痕（数据校验器 M3 会报错）。
            _ => NoneLevelMult,
        };
        int idx = Math.Clamp(level, 1, table.Length) - 1;

        // ★ 口径裁决 #62（方案 B）：`07` 的「倍率（基础）」**就是该技能 1 级的最终倍率**，
        //   而 01 §5.2 的这张数组是**相对成长曲线**，1 级 = ×1.00。
        //   ⇒ 因此必须**除以该数组的 1 级值**再返回。
        //   这条不成立时的后果（实测过）：守约人 80 级裸装的终结技会从 6,000 变成 26,000，
        //   而 02 §8.2 给 Lv85 杂兵的血量只有 8,000 —— 教学关第一发就把目标打超 3 倍。
        return table[idx] / table[0];
    }

    /// <summary>技能等级上限按星级分档（01 §5.2）：5★ = 14，4★ = 12。</summary>
    public static int SkillLevelCap(Rarity rarity) => rarity == Rarity.Five ? 14 : 12;

    /// <summary>权阶 Ⅲ / Ⅴ 的技能等级 +N 也按星级分档。</summary>
    public static int SkillLevelBonusPerRank(Rarity rarity) => rarity == Rarity.Five ? 2 : 1;
}

/// <summary>
/// 属性异常的**家族**（进度条与异常状态的实际归属）。
/// </summary>
/// <remarks>
/// 依据 `01 §2.1`：八种属性只有**六条异常进度条**。
/// <list type="bullet">
///   <item><see cref="Element.Liye"/> 有**自己的进度条**，但**共用冰异常状态**。</item>
///   <item><see cref="Element.Aria"/> 有**自己的进度条**，但**共用共振异常状态**（复伤）。</item>
/// </list>
/// 所以"进度条"按 <see cref="Element"/>（8 条）索引，"异常状态"按本枚举（6 种）索引 —— 两者**不是一回事**。
/// </remarks>
public enum AnomalyFamily
{
    Physical = 0,
    Ice = 1,
    Fire = 2,
    Lightning = 3,
    Wind = 4,
    Resonance = 5,
}

/// <summary>属性的解析工具。所有"属性 → 该吃哪份增益 / 抗性"的换算都只走这里。</summary>
public static class ElementInfo
{
    /// <summary>全部八种属性，顺序固定（用于数组下标与稳定的遍历顺序）。</summary>
    public static readonly Element[] All =
        [Element.Physical, Element.Ice, Element.Fire, Element.Lightning, Element.Wind, Element.Resonance, Element.Liye, Element.Aria];

    /// <summary>六种基础攻击属性。</summary>
    public static readonly Element[] BaseSix =
        [Element.Physical, Element.Ice, Element.Fire, Element.Lightning, Element.Wind, Element.Resonance];

    public const int Count = 8;
    public const int FamilyCount = 6;

    /// <summary>
    /// 属性 → 结算用的基础属性。**离谒按冰结算、咏叹按共振结算**（`01 §2.1` 两段口径）：
    /// 增伤、伤害加深、抗性、穿透、异常积蓄效率全部改用基础属性的那一份；只有**进度条**是独立的。
    /// </summary>
    public static Element Resolve(Element e) => e switch
    {
        Element.Liye => Element.Ice,
        Element.Aria => Element.Resonance,
        _ => e,
    };

    /// <summary>属性 → 异常家族。</summary>
    public static AnomalyFamily FamilyOf(Element e) => e switch
    {
        Element.Liye => AnomalyFamily.Ice,
        Element.Aria => AnomalyFamily.Resonance,
        Element.Physical => AnomalyFamily.Physical,
        Element.Ice => AnomalyFamily.Ice,
        Element.Fire => AnomalyFamily.Fire,
        Element.Lightning => AnomalyFamily.Lightning,
        Element.Wind => AnomalyFamily.Wind,
        Element.Resonance => AnomalyFamily.Resonance,
        _ => AnomalyFamily.Physical,
    };

    /// <summary>字符串 → 属性。JSON 里的 element 字段用这个解析。</summary>
    public static Element Parse(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "physical" or "phys" => Element.Physical,
        "ice" => Element.Ice,
        "fire" => Element.Fire,
        "lightning" or "thunder" or "elec" => Element.Lightning,
        "wind" => Element.Wind,
        "resonance" => Element.Resonance,
        "liye" => Element.Liye,
        "aria" => Element.Aria,
        _ => throw new ArgumentException($"未登记的属性名：'{name}'。合法值见 01 §2.1。", nameof(name)),
    };

    public static string ToJson(Element e) => e switch
    {
        Element.Physical => "physical",
        Element.Ice => "ice",
        Element.Fire => "fire",
        Element.Lightning => "lightning",
        Element.Wind => "wind",
        Element.Resonance => "resonance",
        Element.Liye => "liye",
        Element.Aria => "aria",
        _ => throw new ArgumentOutOfRangeException(nameof(e)),
    };

    /// <summary>中文名（战斗日志用）。</summary>
    public static string ToChinese(Element e) => e switch
    {
        Element.Physical => "物理",
        Element.Ice => "冰",
        Element.Fire => "火",
        Element.Lightning => "雷",
        Element.Wind => "风",
        Element.Resonance => "共振",
        Element.Liye => "离谒",
        Element.Aria => "咏叹",
        _ => "?",
    };

    public static string FamilyToChinese(AnomalyFamily f) => f switch
    {
        AnomalyFamily.Physical => "脆弱",
        AnomalyFamily.Ice => "冻结",
        AnomalyFamily.Fire => "可燃",
        AnomalyFamily.Lightning => "引雷",
        AnomalyFamily.Wind => "风蚀",
        AnomalyFamily.Resonance => "复伤",
        _ => "?",
    };
}
