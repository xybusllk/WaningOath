using WaningOath.Core.Data;

namespace WaningOath.Core.Battle;

public enum BattleOutcome
{
    None = 0,
    AllyWin = 1,
    AllyLose = 2,
}

/// <summary>
/// 一场战斗的全部可变状态。
/// </summary>
/// <remarks>
/// <b>确定性纪律</b>（`06 §1` / 裁决 #59）：本类里**不允许**出现
/// <see cref="DateTime"/>、<see cref="System.Random"/>、环境变量、区域性设置、并行遍历。
/// 所有随机都来自 <see cref="Rng"/>；所有遍历都走 <see cref="List{T}"/> 的固定顺序。
/// </remarks>
public sealed class BattleState
{
    public required GameData Data { get; init; }
    public required Rng Rng { get; init; }
    public required StageDef Stage { get; init; }

    /// <summary>
    /// 全部单位。**顺序固定**：先我方角色（按站位），再敌方（按站位），最后按生成顺序追加召唤物。
    /// 行动值相同时按本列表的顺序决定先后（`01 §1.2`：我方优先于敌方，我方内部一号位优先）。
    /// </summary>
    public List<BattleUnit> Units { get; } = [];

    public List<BattleUnit> Allies { get; } = [];
    public List<BattleUnit> Enemies { get; } = [];
    public List<BattleUnit> Summons { get; } = [];
    public List<FieldInstance> Fields { get; } = [];

    /// <summary>战斗日志。**这是 M1 的验收产物本身**，所以格式稳定、可逐行 diff。</summary>
    public List<string> Log { get; } = [];

    /// <summary>
    /// 量化台账（`M2` 的输入）。日志回答"发生了什么"，台账回答"打出多少、从哪来"。
    /// **只累加、不参与判定**，因此不影响确定性。
    /// </summary>
    public BattleMetrics Metrics { get; } = new();

    /// <summary>未实现原子的记录。**不算错误**（`06 §8.3` 第 4 条：接口层已就位、实现层待 Mx）。</summary>
    public List<string> NotImplementedNotes { get; } = [];

    // ── 资源 ──────────────────────────────────────────────────────────
    public int ResonanceIndex { get; set; } = BattleConstants.ResonanceStart;

    /// <summary>共鸣指数上限的扩容（千语镜星天赋 +2 / 权阶Ⅱ +3，`05 §3.2` 的 `changeResonanceCap`）。</summary>
    public int ResonanceCapBonus { get; set; }

    public int ResonanceCap => BattleConstants.ResonanceCap + ResonanceCapBonus;

    /// <summary>本场战斗累计的普攻次数与元素共鸣次数 —— `107` 的产出账要核对它们。</summary>
    public int BasicAttackCount { get; set; }
    public int ResonanceCastCount { get; set; }
    public int OverdrawCount { get; set; }

    // ── 时间 ──────────────────────────────────────────────────────────
    public double ElapsedAv { get; set; }
    public int Round { get; private set; }

    /// <summary>处决扫射期间屏蔽「击杀额外回合」（避免终结技内无限连动）。</summary>
    public bool SuppressExtraTurns { get; set; }

    /// <summary>本自然回合每个单位已触发的「额外回合」次数（`extraTurnCap` 用）。</summary>
    public Dictionary<int, int> ExtraTurnsThisRound { get; } = [];

    /// <summary>紊乱节流：每自然回合仅能触发一次（`01 §3.3`）。</summary>
    public bool DisorderUsedThisRound { get; set; }

    public int TurnCounter { get; set; }
    public int MaxRound { get; init; } = BattleConstants.MaxRoundPerStage;

    /// <summary>
    /// **本次协同攻击选中的我方角色**（`01 §5.5`）。由 `coopAttack` 原子写入，
    /// 供紧随其后的 `applyBuff` / `heal` 用 `target: "coopTarget"` 定位。
    /// </summary>
    public BattleUnit? CoopTarget { get; set; }

    // ── 状态 ──────────────────────────────────────────────────────────
    public BattleOutcome Outcome { get; set; } = BattleOutcome.None;
    public bool IsOver => Outcome != BattleOutcome.None;

    /// <summary>本场战斗中**最近一次被任意我方目标造成伤害的敌方单位**（`01 §6.4`）。</summary>
    public BattleUnit? MostRecentlyDamagedEnemy { get; set; }

    /// <summary>
    /// 紊乱的**窗口**（`01 §3.3` 的实现备忘）。按目标分组，记录本次窗口内已触发的异常及其自身伤害。
    /// </summary>
    /// <remarks>
    /// 口径（`01 §3.3` 的实现备忘原文）：**由同一个动作直接引发的多属性触发才计入同一窗口；
    /// 延迟结算的 dot 各自视为独立窗口**。因此 `Runner` 会在"每次技能结算"与"每个延迟结算"前后
    /// 各调一次 <see cref="AnomalySystem.OpenWindow"/>。
    /// </remarks>
    public Dictionary<BattleUnit, List<AnomalyWindowEntry>> AnomalyWindow { get; } = [];

    public int NextSerial { get; set; } = 1;

    // ── 查询 ──────────────────────────────────────────────────────────

    public IEnumerable<BattleUnit> AliveUnits => Units.Where(static u => u.IsAlive);
    public List<BattleUnit> AliveAllies => [.. Allies.Where(static u => u.IsAlive)];
    public List<BattleUnit> AliveEnemies => [.. Enemies.Where(static u => u.IsAlive)];
    public List<BattleUnit> AliveCharacters => [.. Allies.Where(static u => u.IsAlive)];

    // ── 阵营（**相对施法者**） ────────────────────────────────────────
    //
    // ⚠️ 这一组方法是本轮补的，起因是一个真 bug：`Runner.ResolveHitTargets` 的
    //    `case "all"` 直接取 `state.AliveEnemies`，而那是**从玩家视角**写死的
    //    「对面那一边」⇒ **敌方单位放群攻会打自己的队友**。
    //    `Combat.SelectAdjacent` 同理（写死 `state.Enemies`，敌方用「相邻」会静默失效）。
    //
    // 口径：所有"我方 / 敌方"一律**相对动作发出者**解释。
    //   * 玩家侧数据的行为**完全不变**（施法者是 Friendly 时，下面的 OwnSide 就是 Allies + 友方召唤物，
    //     与旧实现逐元素相同）—— `Sim battle` 的日志逐行 diff 可以证明。
    //   * 敌方单位从此能正确表达"打我方全体 / 相邻"。
    //
    // ⚠️ `Allies` / `Enemies` 这两个**固定列表**保留：它们是"玩家侧"的语义，
    //    用于站位、`PartyWiped`、策略选敌等**本来就只属于玩家**的地方。

    /// <summary>某一阵营的全部存活单位（含该阵营的召唤物），顺序 = <see cref="Units"/> 的顺序。</summary>
    public List<BattleUnit> AliveOnSide(Disposition side)
        => [.. Units.Where(u => u.IsAlive && u.Disposition == side)];

    /// <summary>
    /// 某一阵营的**全部**单位（**含已阵亡**），顺序 = <see cref="Units"/> 的顺序。
    /// </summary>
    /// <remarks>
    /// 给「相邻」用：`01 §1.1` 的相邻是**站位**序号 ±1，所以中间那个位置死了就是"没人"，
    /// 不该顺延到下一个活着的（<see cref="Combat.SelectAdjacent"/>）。
    /// </remarks>
    public List<BattleUnit> AllOnSide(Disposition side)
        => [.. Units.Where(u => u.Disposition == side)];

    /// <summary>施法者**自己那一边**（含其召唤物）。</summary>
    public List<BattleUnit> OwnSide(BattleUnit actor) => AliveOnSide(actor.Disposition);

    /// <summary>施法者的**对面**。</summary>
    public List<BattleUnit> OpposingSide(BattleUnit actor) => AliveOnSide(Opposite(actor.Disposition));

    /// <summary>施法者自己那边的**角色**（不含召唤物）——`01 §6.3` 的「我方角色」。</summary>
    public List<BattleUnit> OwnCharacters(BattleUnit actor)
        => [.. Units.Where(u => u.IsAlive && u.Disposition == actor.Disposition && u.Kind == UnitKind.Character)];

    /// <summary>施法者自己那边的召唤物。</summary>
    public List<BattleUnit> OwnSummonsOf(BattleUnit actor)
        => [.. Summons.Where(s => s.IsAlive && s.Disposition == actor.Disposition)];

    public static Disposition Opposite(Disposition side)
        => side == Disposition.Friendly ? Disposition.Hostile : Disposition.Friendly;

    /// <summary>敌方 AI 的**合法目标**：4 名存活角色 + 所有召唤物（`01 §7`）。召唤物是合法目标。</summary>
    public List<BattleUnit> LegalTargetsForEnemy => [.. Units.Where(static u => u.IsAlive && u.Disposition == Disposition.Friendly)];

    /// <summary>我方一号位（最左）。部分技能按它的阵营判定（星遥元素共鸣，`07 §2.2`）。</summary>
    public BattleUnit? PartySlot1 => Allies.Count > 0 ? Allies[0] : null;

    /// <summary>我方是否已经"全员无法行动"——**召唤物不计入**（`01 §1.4` / `§1.5`）。</summary>
    public bool PartyWiped => Allies.All(static u => !u.IsAlive);

    /// <summary>某单位身上"对我方/敌方生效"的状态集合（含光环扩散）。</summary>
    public IEnumerable<StatusInstance> StatusesAffecting(BattleUnit target)
    {
        foreach (BattleUnit owner in Units)
        {
            foreach (StatusInstance st in owner.Statuses)
            {
                if (ReferenceEquals(st.Owner, target))
                {
                    yield return st;
                }
                else if (st.Def.AppliesTo == TargetSpec.PartyAll
                         && owner.IsAlive
                         && target.Disposition == Disposition.Friendly
                         && owner.Disposition == Disposition.Friendly)
                {
                    // 光环：挂在施加者身上，按施加者的行动次数计时（01 §1.3）。
                    yield return st;
                }
            }
        }
    }

    // ── 日志 ──────────────────────────────────────────────────────────

    public void Say(string text) => Log.Add(text);

    public void Say(string text, int indent) => Log.Add(new string(' ', indent * 2) + text);

    public void Note(string text)
    {
        if (!NotImplementedNotes.Contains(text, StringComparer.Ordinal)) NotImplementedNotes.Add(text);
    }

    // ── 自然回合 ──────────────────────────────────────────────────────

    /// <summary>推进时间并返回本次是否跨过了自然回合边界。</summary>
    public bool AdvanceClock(double delta)
    {
        ElapsedAv += delta;
        int round = (int)Math.Floor(ElapsedAv / BattleConstants.RoundAv);
        if (round != Round)
        {
            Round = round;
            DisorderUsedThisRound = false;
            ExtraTurnsThisRound.Clear();
            return true;
        }
        return false;
    }

    // ── 环境区（01 §2.2 ⑩） ───────────────────────────────────────────

    /// <summary>
    /// 环境乘区。**独立乘区，不属于增减益** —— 这也是"紊乱真伤不受常规增减益影响、却可被环境修正"能同时成立的原因（`01 §3.3`）。
    /// </summary>
    public double EnvironmentFactor(Element element, DamageCategory category)
    {
        EnvironmentDef env = Stage.Environment;
        double f = env.All;
        if (env.Element.TryGetValue(ElementInfo.ToJson(ElementInfo.Resolve(element)), out double v)) f *= v;
        if (category is DamageCategory.Anomaly or DamageCategory.Replay) f *= env.Anomaly;
        if (category == DamageCategory.Disorder) f *= env.Disorder;
        return f;
    }

    /// <summary>给日志用的紧凑战场快照：`守约人 8000/8000 · 犬型异常 8000/8000`。</summary>
    public string Snapshot() => string.Join(" | ", Units.Where(static u => u.IsAlive)
        .Select(static u => $"{u.Name} {u.Hp:0}/{u.MaxHp:0}"));
}
