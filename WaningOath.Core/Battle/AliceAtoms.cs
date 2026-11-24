using WaningOath.Core.Data;

namespace WaningOath.Core.Battle;

/// <summary>
/// 爱丽丝（`07 §9`）带进来的新规则与新原子。
/// </summary>
/// <remarks>
/// 单独成文件是因为它们**都不是某一个角色的私有特例**，而是这一整套体系需要补的通用能力：
/// <list type="bullet">
///   <item><b>异常的伤害基数按"施加者集合"取</b>（`01 §3.2.5` · `100` #92）——
///         这条改的是**所有 dot 的基数规则**，只是恰好由她第一个用上。</item>
///   <item><b>立即结算一次异常伤害</b>（`tickAnomalyNow`）—— "提前走一格 dot"，不是引爆。</item>
///   <item><b>协同攻击</b>（`coopAttack` · `01 §5.5`）。</item>
///   <item><b>驱散</b>（`dispel`）—— 与"复制"（`§6.4`）和"净化"（`§6.5`）都不同的第三个动作。</item>
///   <item><b>异常层数上限覆盖</b>（`anomalyCapOverride`）—— 承载「风化」。</item>
/// </list>
/// </remarks>
public static class AliceAtoms
{
    // ── 01 §3.2.5：每层的伤害基数取谁 ────────────────────────────────

    /// <summary>
    /// 某条异常在某个目标身上的**伤害基数来源**。
    /// </summary>
    /// <remarks>
    /// `01 §3.2.5` · `100` #92：**由「队内"施加过这条异常"且"生命上限最高"的那一位」**算。
    /// <para>
    /// ⇒ 这让"**低基数的副C铺层 + 高基数的主C提供伤害**"成为一套正式配合：
    /// 守约人的风蚀模态（生命 8,000）负责快速堆层，爱丽丝（生命 15,000）提供基数。
    /// </para>
    /// ⚠️ 找不到任何施加者时退回 <see cref="AnomalyState.Source"/>（数据异常，但不应崩）。
    /// </remarks>
    public static BattleUnit? BaseSourceFor(BattleUnit target, AnomalyFamily family)
    {
        AnomalyState a = AnomalySystem.Get(target, family);
        BattleUnit? best = null;
        foreach (BattleUnit u in a.Appliers)
        {
            if (!u.IsAlive) continue;
            // "队内" = 与目标敌对的那一方（即施加者阵营）
            if (u.Disposition == target.Disposition) continue;
            if (best is null
                || u.MaxHp > best.MaxHp
                || (Math.Abs(u.MaxHp - best.MaxHp) < 1e-9 && u.Serial < best.Serial))
            {
                best = u;
            }
        }
        return best ?? a.Source;
    }

    /// <summary>某条异常当前的层数上限：有覆盖值就用覆盖值（「风化」= 风蚀的上限覆盖）。</summary>
    public static int CapFor(AnomalyState a, int fallback) => a.CapOverride > 0 ? a.CapOverride : fallback;

    // ── tickAnomalyNow：立即结算一次异常持续伤害 ─────────────────────

    /// <summary>
    /// **立即结算一次某属性的异常持续伤害**（`01 §3.2.5` 的 dot 走一格）。
    /// </summary>
    /// <remarks>
    /// ⚠️ **它不是引爆**：**不消耗层数、不改变持续时间** —— 只是把"这一回合的 dot"提前打出来。
    /// 与贝尔兰德的「爆燃」（`detonateCombust`，**消耗概率换一次爆发**）是**两条不同的出路**。
    /// <para>
    /// 目前只登记了「风蚀 / 风化」的每层口径（`01 §3.2.5`：施加者面板生命的 10%）。
    /// 其余家族的"立即结算"口径**还没有定义**（可燃是引爆式、引雷是延迟式、复伤是回放式），
    /// ⇒ 遇到未登记的家族**记一条 <c>Note</c> 并跳过**，不抛异常。
    /// </para>
    /// </remarks>
    public static void TickAnomalyNow(BattleState state, EffectDef e, EffectContext ctx, int depth)
    {
        Element element = string.IsNullOrEmpty(e.Element) ? Element.Wind : ElementInfo.Parse(e.Element);
        AnomalyFamily family = ElementInfo.FamilyOf(element);

        double perStack = family switch
        {
            AnomalyFamily.Wind => BattleConstants.WindErosionHpPctPerStack,
            _ => 0.0,
        };
        if (perStack <= 0.0)
        {
            state.Note($"原子 tickAnomalyNow：家族「{ElementInfo.FamilyToChinese(family)}」还没有「每层伤害」的口径，已跳过。");
            return;
        }

        foreach (BattleUnit t in EffectResolver.Targets(state, e.Target, ctx, beneficial: false))
        {
            AnomalyState a = AnomalySystem.Get(t, family);
            // 层数的三种口径：
            //   * `countAsFullCap` = **按这条异常当前的层数上限结算**（爱丽丝二段终结技的"按满层"）。
            //   * `countAsLayer > 0` = 按一个**写死的**层数结算。
            //   * 都没有 = 按实际层数。
            //
            // ⚠️ 为什么"按满层"不能用写死的数字：风化（`01 §3.2.5` · `100` #88）会把风蚀的上限
            //    从 20 抬到 30（权阶Ⅵ）。写死 `countAsLayer: 20` 会让"按满层结算"在权阶Ⅵ 下**少算 1/3** ——
            //    而且它不会报错，只会让数字偏小。所以这里读的是**当时那一刻的上限**。
            int cap = CapFor(a, BattleConstants.WindErosionStackCap);
            int stacks = e.CountAsFullCap ? cap
                       : e.CountAsLayer > 0 ? e.CountAsLayer
                       : a.Stacks;
            if (stacks <= 0)
            {
                state.Say($"· 立即结算：{t.Name} 身上没有{ElementInfo.FamilyToChinese(family)}层数，跳过", 3);
                continue;
            }

            BattleUnit src = BaseSourceFor(t, family) ?? ctx.Actor;
            double mult = stacks * perStack * (e.Multiplier > 0 ? e.Multiplier : 1.0);
            string how = e.CountAsFullCap ? $"按满层 {stacks} 层结算（实际 {a.Stacks} 层）"
                       : e.CountAsLayer > 0 ? $"按指定层数 {stacks} 层结算（实际 {a.Stacks} 层）"
                       : $"{stacks} 层";

            state.Say($"▸ {t.Name} 的【{ElementInfo.FamilyToChinese(family)}】**立即结算**" +
                      $"（{how} × 基数 {src.Name} 生命上限 {src.MaxHp:0} —— **不消耗层数**）", 3);

            Combat.DealDamage(state, new DamageRequest
            {
                Source = src,
                Target = t,
                Element = element,
                ScalingBase = "hp",
                Multiplier = mult,
                CanCrit = false,
                ForceNoCrit = true,
                Category = DamageCategory.Anomaly,
                AccumulateAnomaly = false,
                SuppressCombustStackDamage = true,
                Skill = ctx.Skill,
                Label = $"{ElementInfo.FamilyToChinese(family)}立即结算",
            }, depth);
        }
    }

    // ── transferStack：把某个资源整堆转成另一个 ──────────────────────

    /// <summary>
    /// **把资源 A 的整堆按 1:1 转成资源 B**（`07 §9.3` 的「**期望 → 誓言**」）。
    /// </summary>
    /// <remarks>
    /// `100` #94（作者口径）：**转化是全部转、且不残留** —— 转化本身没有次数与层数上限。
    /// ⇒ 一段终结技释放时，把当前全部「期望」倒进「誓言」，然后「期望」清零。
    /// ⚠️ `cap` 仍然约束**转入方**（誓言上限 15 / 权阶Ⅳ 20）。
    /// </remarks>
    public static void TransferStack(BattleState state, EffectDef e, EffectContext ctx)
    {
        if (string.IsNullOrEmpty(e.Stack) || string.IsNullOrEmpty(e.From)) return;
        int moved = ctx.Actor.StackOf(e.From);
        if (moved <= 0)
        {
            state.Say($"· 「{e.From}」没有层数可转化", 3);
            return;
        }
        ctx.Actor.SetStack(e.From, 0, 0);
        int now = ctx.Actor.GainStack(e.Stack, moved, e.Cap);
        state.Say($"◆ 「{e.From}」×{moved} **全部转化为**「{e.Stack}」→ {now}" +
                  (e.Cap > 0 ? $"/{e.Cap}" : ""), 2);
    }

    // ── anomalyCapOverride：层数上限覆盖（「风化」） ──────────────────

    /// <summary>
    /// **覆盖某属性异常的层数上限**（`01 §3.2.5` 的「风化」· `100` #88）。
    /// </summary>
    /// <remarks>
    /// 使用者：爱丽丝权阶Ⅵ「代你驶向的黎明」—— "爱丽丝在场时，**所有的风蚀均转化为风化**"，
    /// 上限 **20 → 30**。⚠️ **该转化是全局的**：连守约人风蚀模态挂上去的层也会变成风化。
    /// ⇒ 实现上就是给**全场敌对单位**的该家族盖一个上限，而不是新增一个异常家族。
    /// </remarks>
    public static void SetCapOverride(BattleState state, EffectDef e, EffectContext ctx)
    {
        Element element = string.IsNullOrEmpty(e.Element) ? Element.Wind : ElementInfo.Parse(e.Element);
        AnomalyFamily family = ElementInfo.FamilyOf(element);
        int cap = e.Cap;
        if (cap <= 0) return;

        int n = 0;
        foreach (BattleUnit u in state.Units)
        {
            if (!u.IsAlive || u.Disposition == ctx.Actor.Disposition) continue;
            AnomalySystem.Get(u, family).CapOverride = cap;
            n++;
        }
        state.Say($"◆ 「{ElementInfo.FamilyToChinese(family)}」的层数上限 → **{cap}**（覆盖 {n} 个敌方单位；" +
                  $"与风蚀同一家族、同一进度条）", 2);
    }

    // ── coopAttack：协同攻击 ─────────────────────────────────────────

    /// <summary>
    /// **协同攻击**（`01 §5.5`）：让被选中的我方角色**无视一切条件发动一次元素共鸣**。
    /// </summary>
    /// <remarks>
    /// 选人规则【已定 · `100` #97】：**由玩家选**；`Sim` 的默认策略 = **存活友方中"生命上限最高"的输出位**。
    /// 理由：被选中者放的那一次元素共鸣是"免费的"，选谁直接影响收益；而生命上限最高者同时也在
    /// `01 §3.2.5` 的基数规则里最值钱（两者指向同一个人，是刻意的）。
    /// </remarks>
    public static void CoopAttack(BattleState state, EffectDef e, EffectContext ctx, int depth)
    {
        List<BattleUnit> pool = [.. state.Units.Where(u =>
            u.IsAlive && u.IsCharacter && u.Disposition == ctx.Actor.Disposition)];
        if (pool.Count == 0)
        {
            state.Say("· 协同攻击：没有可选的队友，跳过", 3);
            return;
        }

        BattleUnit pick = pool
            .OrderByDescending(static u => u.MaxHp)
            .ThenBy(static u => u.Serial)
            .First();

        state.Say($"· ◆【协同攻击】{ctx.Actor.Name} 选中了 {pick.Name}", 2);
        state.CoopTarget = pick;
        Runner.CoopResonance(state, pick, depth);

        // 剩余的 e.Hits 段数留作将来的"多段协同"（数据里不填就是 1 次）
        _ = e;
    }

    // ── dispel：移除敌方增益 ────────────────────────────────────────

    /// <summary>
    /// **驱散**：移除敌方身上的增益。⚠️ 与 <c>copyBuff</c>（拿过来）和 <c>cleanse</c>（清我方负面）**方向都不同**。
    /// </summary>
    public static void Dispel(BattleState state, EffectDef e, EffectContext ctx, int depth)
    {
        _ = depth;
        int want = e.Count > 0 ? e.Count : int.MaxValue;

        foreach (BattleUnit t in EffectResolver.Targets(state, e.Target, ctx, beneficial: false))
        {
            List<StatusInstance> buffs = [.. t.Statuses.Where(s => s.Def.Kind == "buff" && ReferenceEquals(s.Owner, t))];
            if (buffs.Count == 0)
            {
                state.Say($"· 驱散：{t.Name} 身上没有可移除的增益", 3);
                continue;
            }

            // `random: true` = 等概率取 N 个；否则按"最新挂上的先掉"（更符合玩家的直觉预期）
            if (e.Random) state.Rng.Shuffle(buffs);
            else buffs.Reverse();

            int removed = 0;
            foreach (StatusInstance s in buffs)
            {
                if (removed >= want) break;
                AnomalySystem.ExpireStatus(state, t, s, expired: false);
                removed++;
            }
            state.Say($"· 驱散：移除了 {t.Name} 的 {removed} 个增益", 2);
        }
    }
}
