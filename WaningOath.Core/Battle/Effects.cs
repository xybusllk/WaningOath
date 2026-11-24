using WaningOath.Core.Data;

namespace WaningOath.Core.Battle;

/// <summary>触发钩子的一次派发上下文。</summary>
public sealed class HookContext
{
    /// <summary>做出动作的单位。</summary>
    public BattleUnit? Actor { get; init; }

    /// <summary>动作的主目标。</summary>
    public BattleUnit? Target { get; init; }

    public SkillDef? Skill { get; init; }

    public DamageResult? Damage { get; init; }
}

/// <summary>效果原子的执行上下文。</summary>
public sealed class EffectContext
{
    public required BattleUnit Actor { get; init; }
    public BattleUnit? Target { get; init; }
    public SkillDef? Skill { get; init; }

    /// <summary>主目标；`null` 时按"攻击类默认敌对、增益类默认友好"补全（`01 §1.6`）。</summary>
    public BattleUnit? MainTarget => Target;
}

/// <summary>
/// 触发钩子的派发。依据 `05 §3.3` 的 hook 名单。
/// </summary>
public static class HookBus
{
    private const int MaxDepth = 8;

    public static void Fire(BattleState state, string hook, HookContext ctx, int depth = 0)
    {
        if (depth > MaxDepth)
        {
            state.Note($"[护栏] 钩子 '{hook}' 的递归深度超过 {MaxDepth}，已中止。检查是否有互相触发的天赋。");
            return;
        }

        // 遍历顺序固定（state.Units 是 List），保证确定性。
        // 用快照：`onBattleStart` 里的召唤会往 state.Units 追加单位，直接枚举会抛
        // "Collection was modified"（薇拉权阶Ⅱ 的开局天龙就撞到过）。
        foreach (BattleUnit unit in state.Units.ToList())
        {
            if (!unit.IsAlive) continue;
            for (int i = 0; i < unit.Triggers.Count; i++)
            {
                TriggerDef t = unit.Triggers[i];
                if (!string.Equals(t.Hook, hook, StringComparison.Ordinal)) continue;
                if (!ScopeMatches(t.Scope, unit, ctx)) continue;
                if (!SkillKindMatches(t.SkillKind, ctx)) continue;
                if (!ConditionOk(state, t.Condition, unit, ctx)) continue;

                string key = $"{hook}#{i}";
                if (t.OncePerBattle > 0 && unit.TriggerCounts.GetValueOrDefault(key) >= t.OncePerBattle) continue;
                int turnLimit = t.OncePerTurn;
                if (!string.IsNullOrEmpty(t.OncePerTurnKnob)
                    && unit.EidolonKnobs.TryGetValue(t.OncePerTurnKnob, out double knobLimit))
                {
                    turnLimit = (int)knobLimit;
                }
                if (turnLimit > 0 && unit.TriggerCountsTurn.GetValueOrDefault(key) >= turnLimit) continue;

                bool ok = t.Chance is null || state.Rng.Chance(t.Chance.Value);
                List<EffectDef> effects = ok
                    ? t.OnSuccess ?? t.Effects
                    : t.OnFail ?? [];

                if (ok) unit.TriggerCounts[key] = unit.TriggerCounts.GetValueOrDefault(key) + 1;
                unit.TriggerCountsTurn[key] = unit.TriggerCountsTurn.GetValueOrDefault(key) + 1;

                state.Say($"· 天赋触发：{unit.Name} 的 [{hook}]{(ok ? "" : "（未命中概率分支）")}", 2);
                EffectResolver.Run(state, effects, new EffectContext
                {
                    Actor = unit,
                    Target = ctx.Target,
                    Skill = ctx.Skill,
                }, depth + 1);
            }
        }
    }

    private static bool ScopeMatches(string scope, BattleUnit owner, HookContext ctx)
    {
        BattleUnit? actor = ctx.Actor;
        // 全局钩子（onBattleStart / onRoundStart / onRoundEnd）没有动作发起者；
        // 这时 `self` = 每个单位自己的触发器都该跑，不能因为 Actor 为 null 就整批跳过。
        // 首个被此 bug 影响的是薇拉权阶Ⅱ的「开局召唤余烬天龙」。
        if (actor is null)
            return string.Equals(scope, "self", StringComparison.Ordinal)
                   || string.Equals(scope, "ally", StringComparison.Ordinal);
        return scope switch
        {
            "self" => ReferenceEquals(actor, owner),
            "ally" => actor.Disposition == owner.Disposition,
            // 「**别的**队友」—— `ally` 把 owner 自己也算进去，而很多机制写的是"队友"、
            // 不含自己（首个使用者：瓦列里娅的协同攻击，档案原话是"**队友**攻击被斗争标记的敌人时"）。
            // 用 `ally` 再在条件里排自己做不到（条件读不到"是不是本体"），所以单列一档。
            "allyOther" => actor.Disposition == owner.Disposition && !ReferenceEquals(actor, owner),
            "enemy" => actor.Disposition != owner.Disposition,
            "any" => true,
            _ => ReferenceEquals(actor, owner),
        };
    }

    private static bool SkillKindMatches(string? required, HookContext ctx)
    {
        if (string.IsNullOrEmpty(required)) return true;
        return ctx.Skill is not null && string.Equals(ctx.Skill.Kind, required, StringComparison.Ordinal);
    }

    /// <summary>
    /// 条件表达式。**M1 只支持 <see cref="Conditions"/> 里登记的那几条**；
    /// 未识别的条件一律返回 `false`（保守：宁可天赋不触发，也不要它错误地触发）。
    /// </summary>
    private static bool ConditionOk(BattleState state, string? condition, BattleUnit owner, HookContext ctx)
        => Conditions.Evaluate(state, condition, owner, ctx.Target, ctx.Actor);
}

/// <summary>
/// 条件表达式的**唯一**求值处。触发表与 `conditional` 原子共用它 ——
/// 两处各写一份 <c>switch</c> 是这类系统最常见的分叉来源。
/// </summary>
public static class Conditions
{
    /// <param name="owner">触发钩子的**所属单位**（也是 `hasStack:` / `stackAtLeast:` / `hpBelow:` 的读表对象）。</param>
    /// <param name="target">事件的目标（`ctx.Target`）。</param>
    /// <param name="actor">
    /// 事件的**行动/承受单位**（`ctx.Actor`）。只有 `actorStatusAtLeast:` 会读它 ——
    /// 见该分支上的说明：**状态挂在谁身上，不一定等于钩子挂在谁身上**。
    /// </param>
    public static bool Evaluate(BattleState state, string? condition, BattleUnit owner, BattleUnit? target,
                               BattleUnit? actor = null)
    {
        if (string.IsNullOrWhiteSpace(condition) || condition == "always") return true;
        string c = condition.Trim();

        // **复合条件**：`A AND B`。用最朴素的分割而不是写一个小解析器 ——
        // 条件表达式目前只需要"与"，没有"或"与括号；等真的需要时再升级（M3 的校验器会拦住非法语法）。
        if (c.Contains(" AND ", StringComparison.OrdinalIgnoreCase))
        {
            foreach (string part in c.Split(" AND ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!Evaluate(state, part, owner, target, actor)) return false;
            }
            return true;
        }

        if (c.StartsWith("hasStack:", StringComparison.Ordinal))
            return owner.StackOf(c["hasStack:".Length..]) > 0;

        // 「某**状态**的层数」—— 与 `hasStack`/`stackAtLeast` 是**两套东西**：
        // `StackOf` 读的是 `BattleUnit.Stacks` 那张资源计数表（风涌、誓言…），
        // 而 `ApplyStatus` 落的 `StatusInstance.Stacks` 在这张表里**看不见**。
        // 「注目」「视觉模糊」「代偿」都是后者 ⇒ 必须有这一条，否则
        // `stackAtLeast:代偿:3` 会**永远为假**（M1 只登记了前者，本轮补上）。
        if (c.StartsWith("statusAtLeast:", StringComparison.Ordinal))
        {
            string[] parts = c["statusAtLeast:".Length..].Split(':', 2);
            return parts.Length == 2 && int.TryParse(parts[1], out int need) && owner.StatusStacks(parts[0]) >= need;
        }

        // 同上，但读**事件行动者**。**为什么需要它**：「失焦者」的钩子挂在敌人身上，
        // 而被叠「视觉模糊」的是**我方角色** —— `owner`（钩子所属单位）身上什么也没有。
        // 没有 `actor` 时退回 `owner`，这样 `conditional` 原子（此时二者同为施法者）不必区分。
        if (c.StartsWith("actorStatusAtLeast:", StringComparison.Ordinal))
        {
            string[] parts = c["actorStatusAtLeast:".Length..].Split(':', 2);
            BattleUnit subject = actor ?? owner;
            return parts.Length == 2 && int.TryParse(parts[1], out int need2) && subject.StatusStacks(parts[0]) >= need2;
        }

        // 「身上的**权阶旋钮** ≥ N」—— 权阶进战斗后的通用闸门。
        // 首个使用者：瓦列里娅的权阶Ⅱ「才解锁可爆火种引爆」、Ⅳ「惊慌减防加深」、Ⅵ「返还/额外可燃」。
        // 读的是 `EidolonKnobs`（`Policy.ApplyEidolons` 把未登记为面板键的 modifyStat 收进这里），
        // **不是** `KnobValue`（那是状态上的旋钮袋）——两者生命周期不同，混读会出错。
        if (c.StartsWith("knobAtLeast:", StringComparison.Ordinal))
        {
            string[] parts = c["knobAtLeast:".Length..].Split(':', 2);
            return parts.Length == 2 && TryNumber(parts[1], out double knobNeed)
                   && owner.EidolonKnobs.TryGetValue(parts[0], out double knobHave) && knobHave >= knobNeed;
        }

        // 「权阶旋钮 < N」—— 用于「基础形态 / 权阶解锁形态」二选一（避免两条同时命中）。
        if (c.StartsWith("knobBelow:", StringComparison.Ordinal))
        {
            string[] parts = c["knobBelow:".Length..].Split(':', 2);
            return parts.Length == 2 && TryNumber(parts[1], out double knobLimit)
                   && (!owner.EidolonKnobs.TryGetValue(parts[0], out double knobValue) || knobValue < knobLimit);
        }

        // 「目标生命比例低于 P」与「按权阶旋钮给阈值」——处决/斩杀类机制用。
        if (c.StartsWith("targetHpBelow:", StringComparison.Ordinal))
            return target is not null && TryNumber(c["targetHpBelow:".Length..], out double tPct)
                   && target.HpRatio < tPct;

        if (c.StartsWith("targetHpBelowKnob:", StringComparison.Ordinal))
        {
            string key = c["targetHpBelowKnob:".Length..];
            return target is not null
                   && owner.EidolonKnobs.TryGetValue(key, out double tKnob)
                   && target.HpRatio < tKnob;
        }

        if (c.StartsWith("stackAtLeast:", StringComparison.Ordinal))
        {
            string[] parts = c["stackAtLeast:".Length..].Split(':', 2);
            return parts.Length == 2 && int.TryParse(parts[1], out int need) && owner.StackOf(parts[0]) >= need;
        }

        // 「层数**恰好**等于 N」。
        // **为什么不能只靠 `stackAtLeast` 拼**：终结技按 1/2/3 发火种分三档，而三档是**互斥**的。
        // 只用 `stackAtLeast` 的话，3 层会让"≥3""≥2""≥1"三条同时判真 ⇒ 三档效果一起结算
        // （全队火伤会叠成 10% + 20%）。有了这一条，三档写成 `≥3` / `==2` / `==1` 就天然互斥。
        if (c.StartsWith("stackExactly:", StringComparison.Ordinal))
        {
            string[] parts = c["stackExactly:".Length..].Split(':', 2);
            return parts.Length == 2 && int.TryParse(parts[1], out int exact) && owner.StackOf(parts[0]) == exact;
        }

        // 「身上**没有**某个状态」——条件表达式里唯一的否定形式。
        // 首个使用者：「燎原期间无人机攻击不消耗无人机」要把 `spendStack` 包进 `notStatus:燎原`。
        // ⚠️ 它只回答"有没有这个状态"，**不做通用取反**（`!A AND B` 那种要写小解析器，
        //    而条件表一旦长成一门语言就再也收不回来了 —— 真有需要时再加）。
        if (c.StartsWith("notStatus:", StringComparison.Ordinal))
            return owner.StatusStacks(c["notStatus:".Length..]) == 0;

        // 「某状态的剩余时长 < N」——用于"火枪手剩余不到 200 行动值时追发强化普攻"这类时机判断。
        // 首个使用者：薇拉「狩猎，从不会止步」的追加条件（数据侧用 `<2 回合` 近似 `<200 AV`）。
        if (c.StartsWith("statusRemainingBelow:", StringComparison.Ordinal))
        {
            string[] parts = c["statusRemainingBelow:".Length..].Split(':', 2);
            return parts.Length == 2 && TryNumber(parts[1], out double remainingLimit)
                   && owner.Statuses.Any(st => string.Equals(st.Def.Name, parts[0], StringComparison.Ordinal)
                                               && st.Remaining < remainingLimit);
        }

        // 读**事件目标**的两条（与 `owner` / `actor` 那两组并列）。
        // 首个使用者：瓦列里娅 —— 「普攻时若**目标**未持有可爆火种，则驱散它一个增益」、
        // 「无人机攻击**持有**可爆火种的敌人时为其叠层」。这两件事看的是被打的那个人，
        // 既不是她自己（owner），也不是行动者（actor）。
        if (c.StartsWith("targetStatusAtLeast:", StringComparison.Ordinal))
        {
            string[] parts = c["targetStatusAtLeast:".Length..].Split(':', 2);
            return parts.Length == 2 && int.TryParse(parts[1], out int tNeed)
                   && target is not null && target.StatusStacks(parts[0]) >= tNeed;
        }

        if (c.StartsWith("targetLacksStatus:", StringComparison.Ordinal))
            return target is null || target.StatusStacks(c["targetLacksStatus:".Length..]) == 0;

        // 「场上**没有任何敌人**持有某状态」。
        // 首个使用者：瓦列里娅的天赋「若**未标记任何目标**，行动结束后获得 1 架无人机」——
        // 这句话问的是**对面全体**，既不是 owner 也不是某一个 target，前几条都表达不了。
        // 写法与 `anomalyStacksAtLeast` 同源（同样遍历 `OpposingSide`），只是条件相反。
        if (c.StartsWith("noEnemyHasStatus:", StringComparison.Ordinal))
        {
            string name = c["noEnemyHasStatus:".Length..];
            return !state.OpposingSide(owner).Exists(u => u.StatusStacks(name) > 0);
        }

        // 「场上**至少有一个敌人**持有某状态」—— `noEnemyHasStatus` 的正向补集。
        // 首个使用者：瓦列里娅权阶Ⅱ「每回合必定获得 1 架无人机」：
        // 原天赋是"**未标记**时 +1"，Ⅱ 改成"**必定** +1" ⇒ 需要一条"有标记时才补"的触发，
        // 否则两条会在"未标记"时同时命中、叠成 +2。
        if (c.StartsWith("enemyHasStatus:", StringComparison.Ordinal))
        {
            string name = c["enemyHasStatus:".Length..];
            return state.OpposingSide(owner).Exists(u => u.StatusStacks(name) > 0);
        }

        if (c.StartsWith("hpBelow:", StringComparison.Ordinal))
            return TryNumber(c["hpBelow:".Length..], out double p) && owner.HpRatio < p;

        // 「场上还有几个敌人」= **相对施法者的对面**（敌方单位问这句话时问的是"我方还有几个人"）。
        if (c.StartsWith("enemyCountLE:", StringComparison.Ordinal))
            return TryNumber(c["enemyCountLE:".Length..], out double n) && state.OpposingSide(owner).Count <= n;

        if (c.StartsWith("enemyCountGE:", StringComparison.Ordinal))
            return TryNumber(c["enemyCountGE:".Length..], out double n) && state.OpposingSide(owner).Count >= n;

        // 「场上任意敌人持有的某属性异常层数 ≥ N」——守约人风蚀模态的强化共鸣开启条件（07 §1.3）。
        // ⚠️ 看的是**层数**（Stacks），不是进度条。
        if (c.StartsWith("anomalyStacksAtLeast:", StringComparison.Ordinal))
        {
            string[] parts = c["anomalyStacksAtLeast:".Length..].Split(':', 2);
            if (parts.Length != 2 || !TryNumber(parts[1], out double need)) return false;
            AnomalyFamily family = ElementInfo.FamilyOf(ElementInfo.Parse(parts[0]));
            return state.OpposingSide(owner).Exists(u => u.Anomalies.TryGetValue(family, out AnomalyState? a) && a.Stacks >= need);
        }

        // 「本场战斗释放过终结技」。与上一条合起来就是风蚀模态强化共鸣的**动态**开启条件。
        // 「目标属于某个敌方单位类别」—— 爱丽丝「大家伙的遗赠」的触发条件（07 §9.2）。
        if (c.StartsWith("targetCategoryIs:", StringComparison.Ordinal))
        {
            string cat = c["targetCategoryIs:".Length..];
            return target is not null && string.Equals(target.Category, cat, StringComparison.Ordinal);
        }

        if (c == "usedUltimate") return owner.HasCastUltimate;

        // 「场上存在某 id 的召唤物」—— 爱丽丝风蚀… 不，是她的强化元素共鸣的开启条件（07 §9.4）。
        if (c.StartsWith("hasSummon:", StringComparison.Ordinal))
        {
            string id = c["hasSummon:".Length..];
            return state.Units.Exists(u => u.IsAlive && u.IsSummon && string.Equals(u.DefId, id, StringComparison.Ordinal));
        }

        return c switch
        {
            "targetIsElitePlus" => target?.IsEliteOrAbove == true,
            "targetIsMinion" => target is { IsEnemy: true, IsEliteOrAbove: false },
            "targetAlive" => target?.IsAlive == true,
            _ => false,
        };
    }

    private static bool TryNumber(string text, out double value)
        => double.TryParse(text, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out value);
}



/// <summary>
/// 效果原子（`05 §3.2`）的解释器。
/// </summary>
/// <remarks>
/// <b>接口层 vs 实现层</b>（`06 §8.3` 第 4 条）：
/// 本类的 <c>switch</c> **必须列出全部原子**（接口层），其中 M1 不需要的走
/// <see cref="NotYet"/> 分支 —— **记一条日志而不是抛异常**。这样"数据文件可以先声明、
/// 实现层按里程碑补齐"，且不会在验收用例里炸掉。
/// </remarks>
public static class EffectResolver
{
    public static void Run(BattleState state, IReadOnlyList<EffectDef> effects, EffectContext ctx, int depth)
    {
        if (depth > 8)
        {
            state.Note("[护栏] 效果原子递归超过 8 层，已中止。");
            return;
        }
        foreach (EffectDef e in effects) RunOne(state, e, ctx, depth);
    }

    private static void RunOne(BattleState state, EffectDef e, EffectContext ctx, int depth)
    {
        BattleUnit actor = ctx.Actor;

        switch (e.Type)
        {
            // ── 资源 ──────────────────────────────────────────────────
            case "gainStack":
                GainStack(state, e, ctx, actor, +1);
                break;

            case "spendStack":
                GainStack(state, e, ctx, actor, -1);
                break;

            // 「把某个资源整堆转成另一个资源」——爱丽丝的「期望 → 誓言」（07 §9.3）。
            case "transferStack":
                AliceAtoms.TransferStack(state, e, ctx);
                break;

            case "gainResonance":
                ChangeResonance(state, (int)e.Amount, actor);
                break;

            case "gainEnergy":
                Combat.GainEnergy(actor, e.Amount);
                break;

            case "changeEnergyCost":
                state.Note("原子 changeEnergyCost 属实现层（M7），M1 只在权阶数据里声明。");
                break;

            case "changeResonanceCap":
                state.ResonanceCapBonus += (int)e.Delta;
                state.Say($"· 共鸣指数上限 +{(int)e.Delta} → {state.ResonanceCap}", 3);
                break;

            // ── 行动 ──────────────────────────────────────────────────
            case "extraTurn":
                if (state.SuppressExtraTurns)
                {
                    state.Say("· 处决扫射期间不触发额外回合", 3);
                    break;
                }
                foreach (BattleUnit u in Targets(state, e.Target, ctx, beneficial: true))
                {
                    int cap = (int)u.EidolonKnobs.GetValueOrDefault("extraTurnCap");
                    if (cap > 0)
                    {
                        int used = state.ExtraTurnsThisRound.GetValueOrDefault(u.Serial);
                        if (used >= cap)
                        {
                            state.Say($"· {u.Name} 本自然回合的额外回合已达上限 {cap} 次", 3);
                            continue;
                        }
                        state.ExtraTurnsThisRound[u.Serial] = used + 1;
                    }
                    u.Av = 0.0;
                    state.Say($"· {u.Name} 立即行动", 3);
                }
                break;

            case "advanceAction":
                foreach (BattleUnit u in Targets(state, e.Target, ctx, beneficial: true)) u.Av *= 1.0 - e.Value;
                break;

            case "delayAction":
                foreach (BattleUnit u in Targets(state, e.Target, ctx, beneficial: false)) u.Av *= 1.0 + e.Value;
                break;

            // ── 自己掏钱 / 自己挨打（M7） ─────────────────────────────
            case "selfHpCost":
                SelfHpCost(state, e, ctx);
                break;

            // ── 伤害 ──────────────────────────────────────────────────
            case "dealDamage":
                DealDamageAtom(state, e, ctx, depth);
                break;

            case "trueDamage":
                DealTrueDamage(state, e, ctx, depth);
                break;

            case "followUpAttack":
                FollowUp(state, e, ctx, depth);
                break;

            case "additionalDamage":
                AdditionalDamage(state, e, ctx, depth);
                break;

            // ── 治疗 / 护盾 ───────────────────────────────────────────
            case "heal":
                Heal(state, e, ctx);
                break;

            case "shield":
                Shield(state, e, ctx);
                break;

            // ── 增益 / 减益 ───────────────────────────────────────────
            case "applyBuff":
                ApplyStatus(state, e, ctx, e.BuffId, beneficial: true, depth);
                break;

            case "applyDebuff":
                ApplyStatus(state, e, ctx, e.DebuffId, beneficial: false, depth);
                break;

            // 「状态层数 −N」—— 与 `spendStack` 的区别同 `statusAtLeast:` 与 `stackAtLeast:`：
            // 它动的是 `StatusInstance.Stacks`。CCRF-411「注目」的破绽（任何非攻击行动 −1 层）用它。
            case "decayStatus":
                DecayStatus(state, e, ctx);
                break;

            // ── 异常 ──────────────────────────────────────────────────
            case "applyAnomalyStack":
                foreach (BattleUnit t in Targets(state, e.Target, ctx, beneficial: false))
                {
                    // 「基础概率」（01 §2.3）：受攻击者的效果命中与目标的效果抵抗修正。
                    double chance = e.Chance ?? 1.0;
                    chance = Math.Clamp(chance * (1.0 + actor.Stats.EffectHitValue) * (1.0 - t.Stats.EffectResValue), 0.0, 1.0);
                    if (!state.Rng.Chance(chance))
                    {
                        state.Say($"· 基础概率 {(e.Chance ?? 1.0):P0} → {chance:P0}（命中/抵抗修正后）未命中，{t.Name} 未获得异常层数", 3);
                        continue;
                    }
                    // 额外叠层：`extraStacksKnob` 同时读状态旋钮（燎原的 1/1/2 层）与权阶旋钮（Ⅵ +1 层）。
                    int stacks = Math.Max(1, e.Stacks);
                    if (!string.IsNullOrEmpty(e.ExtraStacksKnob))
                    {
                        double extra = actor.KnobValue(e.ExtraStacksKnob)
                                       + actor.EidolonKnobs.GetValueOrDefault(e.ExtraStacksKnob);
                        stacks = Math.Max(1, stacks + (int)Math.Round(extra));
                    }

                    // 处决扫射里同一个绽放技能会连续调用：`ignoreCost` 会绕过 OncePerRound，
                    // 但仍应受目标存活与日志约束（由 Runner.ExecuteSkill 负责）。
                    AnomalySystem.ApplyDirectStacks(state, actor, t, ParseElement(e.Element, actor), stacks, depth,
                        e.SuppressCombustStackDamage);
                }
                break;

            // ── 可燃引爆族（`01 §3.2.3` 的专属爆发点，05 §3.2） ─────────
            case "detonateCombust":
                DetonateCombust(state, e, ctx, depth);
                break;

            // ── 爱丽丝（07 §9）带进来的四个原子，实现见 AliceAtoms.cs ──
            case "tickAnomalyNow":
                AliceAtoms.TickAnomalyNow(state, e, ctx, depth);
                break;

            case "coopAttack":
                AliceAtoms.CoopAttack(state, e, ctx, depth);
                break;

            case "dispel":
                AliceAtoms.Dispel(state, e, ctx, depth);
                break;

            case "anomalyCapOverride":
                AliceAtoms.SetCapOverride(state, e, ctx);
                break;

            case "applyAnomaly":
                state.Note("原子 applyAnomaly（走进度条）—— M1 的进度条由 §3.1 的命中积蓄直接驱动，本原子保留给技能显式指定属性时使用。");
                foreach (BattleUnit t in Targets(state, e.Target, ctx, beneficial: false))
                {
                    AnomalySystem.Accumulate(state, actor, t, ParseElement(e.Element, actor), depth);
                }
                break;

            // ── 领域 / 召唤物 ─────────────────────────────────────────
            case "deployField":
                DeployField(state, e, ctx);
                break;

            case "summon":
                Summon(state, e, ctx, depth);
                break;

            // ── 判定 ──────────────────────────────────────────────────
            case "chanceCheck":
                RunChanceCheck(state, e, ctx, depth);
                break;

            case "conditional":
                RunConditional(state, e, ctx, depth);
                break;

            case "execute":
                Execute(state, e, ctx);
                break;

            // ── 新角色（薇拉 / 卡尔）的通用原子，实现见 NewAtoms.cs ──
            case "stackDetonate":
                NewAtoms.StackDetonate(state, e, ctx, depth);
                break;

            case "executionSweep":
                NewAtoms.ExecutionSweep(state, e, ctx, depth);
                break;

            case "recastSelf":
                NewAtoms.RecastSelf(state, ctx, depth);
                break;

            // ── 特殊 ──────────────────────────────────────────────────
            case "cleanse":
                Cleanse(state, e, ctx);
                break;

            case "revive":
                NotYet(state, e, "M7（复活）");
                break;

            case "copyBuff":
                CopyBuff(state, e, ctx, depth);
                break;

            case "threatChange":
                foreach (BattleUnit u in Targets(state, e.Target, ctx, beneficial: true))
                {
                    u.ThreatBonus += e.Delta;
                    state.Say($"· {u.Name} 仇恨 {(e.Delta >= 0 ? "+" : "")}{e.Delta:0.#} → {u.ThreatWeight:0.#}", 3);
                }
                break;

            case "dodge":
                NotYet(state, e, "M7（守约人权阶Ⅳ 的闪避）");
                break;

            // ── 修正 ──────────────────────────────────────────────────
            case "modifyStat":
                ModifyStat(state, e, ctx);
                break;

            default:
                state.Note($"未登记的原子类型 '{e.Type}'（05 §3.2 无此项）。已忽略。");
                break;
        }
    }

    private static void NotYet(BattleState state, EffectDef e, string milestone)
        => state.Note($"原子 '{e.Type}' 的**接口层已就位、实现层待 {milestone}**（06 §8.3 第 4 条）。本次未产生效果。");

    // ────────────────────────────────────────────────────────────────────

    private static void GainStack(BattleState state, EffectDef e, EffectContext ctx, BattleUnit actor, int sign)
    {
        if (string.IsNullOrEmpty(e.Stack)) return;
        int amount = (int)(e.Amount <= 0 ? e.Stacks : e.Amount) * sign;
        int cap = e.Cap;

        foreach (BattleUnit u in Targets(state, e.Target ?? TargetSpec.Self, ctx, beneficial: sign > 0))
        {
            int actualCap = cap;
            string capKey = StackCapKey(e.Stack);
            if (u.EidolonKnobs.TryGetValue(capKey, out double capBonus) && Math.Abs(capBonus) > 1e-9)
                actualCap = Math.Max(0, actualCap + (int)capBonus);
            int now = u.GainStack(e.Stack, amount, actualCap);
            state.Say($"· {u.Name} 的「{e.Stack}」{(amount >= 0 ? "+" : "")}{amount} → {now}" +
                      (actualCap > 0 ? $"/{actualCap}" : ""), 3);
            HookBus.Fire(state, "onStackReach", new HookContext { Actor = u }, 0);
        }
    }

    /// <summary>共鸣指数变动。**上限 ≠ 产出**（`107`）：本方法只做点数增减，不判定"是否允许产点"。</summary>
    public static void ChangeResonance(BattleState state, int delta, BattleUnit actor)
    {
        state.ResonanceIndex = Math.Clamp(state.ResonanceIndex + delta, 0, state.ResonanceCap);
    }

    /// <summary>透支（`01 §4.1`）：点数为 0 时强行释放元素共鸣，扣除释放者 25% 生命上限，**可致死**。</summary>
    public static void Overdraw(BattleState state, BattleUnit actor, int depth)
    {
        state.OverdrawCount++;
        double cost = actor.MaxHp * BattleConstants.ResonanceOverdrawHpPct;
        state.Say($"· 【透支】{actor.Name} 以 {cost:0.#} 生命（25% 生命上限）强行释放元素共鸣", 3);
        Combat.ApplyPrecomputed(state, actor, actor, Element.Physical, cost, false, DamageCategory.Direct, "共鸣指数透支", depth);
    }

    /// <summary>资源名 → 权阶/装备用来抬上限的旋钮名。</summary>
    private static string StackCapKey(string stack) => stack switch
    {
        "镜印" => "mirrorMarkCap",
        "弹药" => "ammoCap",
        "无人机" => "droneCap",
        "星忆" => "starMemCap",
        "归溟" => "guimingCap",
        "誓言" => "oathCap",
        "期望" => "expectationCap",
        "转速" => "spinCap",
        _ => stack + "Cap",
    };

    private static void DealDamageAtom(BattleState state, EffectDef e, EffectContext ctx, int depth)
    {
        Element element = ParseElement(e.Element, ctx.Actor);
        int hits = Math.Max(1, e.Hits);
        if (e.HitsPerStack > 0 && !string.IsNullOrEmpty(e.PerStack))
        {
            int stacks = Math.Max(1, ctx.Actor.StackOf(e.PerStack));
            hits = Math.Max(1, hits * e.HitsPerStack * stacks);
        }

        // 逐次解析目标：`random` 目标的每一段各自独立随机（01 §1.1 的弹射）。
        for (int i = 0; i < hits; i++)
        {
            List<BattleUnit> targets = Targets(state, e.Target, ctx, beneficial: false);
            if (targets.Count == 0) break;
            foreach (BattleUnit t in targets)
            {
                if (!t.IsAlive) continue;
                double multiplier = e.Multiplier;
                if (!string.IsNullOrEmpty(e.Knob))
                    multiplier *= 1.0 + ctx.Actor.KnobValue(e.Knob);
                if (e.LostHpMultiplier > 0.0)
                {
                    double lost = Math.Clamp(1.0 - t.HpRatio, 0.0, 1.0);
                    multiplier += Math.Min(e.LostHpCap, lost * e.LostHpMultiplier);
                }
                Combat.DealDamage(state, new DamageRequest
                {
                    Source = ctx.Actor,
                    Target = t,
                    Element = element,
                    Multiplier = multiplier,
                    ScalingBase = e.ScalingBase ?? "atk",
                    CanCrit = e.CanCrit,
                    IgnoreDefense = e.IgnoreDefense,
                    Category = DamageCategory.Direct,
                    AccumulateAnomaly = e.AccumulateAnomaly,
                    Skill = ctx.Skill,
                    Label = ctx.Skill?.Name ?? "技能附带伤害",
                }, depth);
            }
        }
    }

    private static void DealTrueDamage(BattleState state, EffectDef e, EffectContext ctx, int depth)
    {
        foreach (BattleUnit t in Targets(state, e.Target, ctx, beneficial: false))
        {
            double amount = ctx.Actor.Stats.AtkValue * e.Multiplier;
            Combat.ApplyPrecomputed(state, ctx.Actor, t, ParseElement(e.Element, ctx.Actor), amount, false,
                DamageCategory.Disorder, "真实伤害", depth);
            if (!t.IsAlive) break;
        }
    }

    /// <summary>
    /// 追加攻击（`01 §9`）：**不消耗该角色的正常行动**。
    /// 它**会**触发 `onHit` / `onCrit` / `onFollowUp`，但**不会**触发 `onAttack` / `onUltimateCast` ——
    /// 否则"终结技触发追击、追击又算一次攻击"会互相递归。
    /// </summary>
    private static void FollowUp(BattleState state, EffectDef e, EffectContext ctx, int depth)
    {
        Element element = ParseElement(e.Element, ctx.Actor);
        foreach (BattleUnit t in Targets(state, e.Target, ctx, beneficial: false))
        {
            state.Say($"· {ctx.Actor.Name} 发动追加攻击", 3);
            Combat.DealDamage(state, new DamageRequest
            {
                Source = ctx.Actor,
                Target = t,
                Element = element,
                Multiplier = e.Multiplier,
                ScalingBase = e.ScalingBase ?? "atk",
                CanCrit = e.CanCrit,
                IgnoreDefense = e.IgnoreDefense,
                Category = DamageCategory.Direct,
                AccumulateAnomaly = e.AccumulateAnomaly,
                Label = "追加攻击",
            }, depth);
            HookBus.Fire(state, "onFollowUp", new HookContext { Actor = ctx.Actor, Target = t }, depth);
        }
    }

    /// <summary>
    /// 附加伤害（`01 §2.4`）：挂在本动作的伤害上追加结算。
    /// <c>perStack</c> 指向某个资源名 → 倍率 × 该资源层数。
    /// </summary>
    private static void AdditionalDamage(BattleState state, EffectDef e, EffectContext ctx, int depth)
    {
        Element element = ParseElement(e.Element, ctx.Actor);
        foreach (BattleUnit t in Targets(state, e.Target, ctx, beneficial: false))
        {
            double mult = e.Multiplier;
            if (!string.IsNullOrEmpty(e.PerStack)) mult *= Math.Max(1, ctx.Actor.StackOf(e.PerStack));
            if (!string.IsNullOrEmpty(e.PerTargetStatus)) mult *= Math.Max(1, t.StatusStacks(e.PerTargetStatus));
            if (!string.IsNullOrEmpty(e.Knob)) mult *= 1.0 + ctx.Actor.KnobValue(e.Knob);

            Combat.DealDamage(state, new DamageRequest
            {
                Source = ctx.Actor,
                Target = t,
                Element = element,
                Multiplier = mult,
                ScalingBase = e.ScalingBase ?? "atk",
                CanCrit = e.CanCrit,
                IgnoreDefense = e.IgnoreDefense,
                Category = DamageCategory.Additional,
                AccumulateAnomaly = e.AccumulateAnomaly,
                Label = "附加伤害",
            }, depth);
            if (!t.IsAlive) break;
        }
    }

    private static void Heal(BattleState state, EffectDef e, EffectContext ctx)
    {
        foreach (BattleUnit t in Targets(state, e.Target, ctx, beneficial: true))
        {
            double amount = e.Flat;
            if (e.PctMaxHp > 0) amount += ctx.Actor.MaxHp * e.PctMaxHp;
            if (e.PctCurrentHp > 0) amount += t.Hp * e.PctCurrentHp;
            if (e.Multiplier > 0) amount += ctx.Actor.Stats.AtkValue * e.Multiplier;
            Combat.Heal(state, ctx.Actor, t, amount, "治疗");
        }
    }

    private static void Shield(BattleState state, EffectDef e, EffectContext ctx)
    {
        foreach (BattleUnit t in Targets(state, e.Target, ctx, beneficial: true))
        {
            double amount = e.Flat;
            if (e.PctMaxHp > 0) amount += ctx.Actor.MaxHp * e.PctMaxHp;
            if (e.Multiplier > 0) amount += ctx.Actor.Stats.AtkValue * e.Multiplier;
            Combat.AddShield(state, ctx.Actor, t, amount, "护盾");
        }
    }

    /// <summary>
    /// `copyBuff`：从目标身上复制 N 个随机增益，施加给施法者（薇拉「这个我想学！」）。
    /// </summary>
    /// <remarks>
    /// 复制的是**状态实例**（保留层数），不是"按名字重新施加"；时长由 `duration` 覆盖，
    /// 留空则沿用原状态的剩余时长。只复制 `kind == buff` 且 Owner == 目标的状态，
    /// 不复制光环的扩散副本（那些挂在施法者身上，不在目标的状态列表里）。
    /// </remarks>
    private static void CopyBuff(BattleState state, EffectDef e, EffectContext ctx, int depth)
    {
        bool doubleNext = ctx.Actor.Statuses.Any(static s => s.Def.DoubleNextBloom);
        bool copiedAny = false;
        int want = e.Count > 0 ? e.Count : 1;
        foreach (BattleUnit t in Targets(state, e.Target, ctx, beneficial: true))
        {
            List<StatusInstance> buffs = [.. t.Statuses.Where(s => s.Def.Kind == "buff" && ReferenceEquals(s.Owner, t))];
            if (buffs.Count == 0)
            {
                state.Say($"· 复制增益：{t.Name} 身上没有可复制的增益", 3);
                continue;
            }

            state.Rng.Shuffle(buffs);
            int copied = 0;
            foreach (StatusInstance src in buffs)
            {
                if (copied >= want) break;
                double duration = e.Duration > 0 ? e.Duration : src.Remaining;
                int stacks = Math.Max(1, src.Stacks);
                if (doubleNext)
                {
                    duration *= 2.0;
                    stacks *= 2;
                }
                ctx.Actor.Statuses.Add(new StatusInstance
                {
                    Def = src.Def,
                    Owner = ctx.Actor,
                    Source = ctx.Actor,
                    TickSource = ctx.Actor,
                    Stacks = stacks,
                    Remaining = duration,
                    SourceLevel = ctx.Actor.Level,
                });
                state.Say($"· 复制增益：{ctx.Actor.Name} 获得【{src.Def.Name}】" +
                          $"（{duration:0.#} 回合{(doubleNext ? "，借你羽梢一瞬：时长 / 层数翻倍" : "")}）", 3);
                copied++;
                copiedAny = true;
            }
            _ = depth;
        }

        if (doubleNext && copiedAny)
        {
            StatusInstance? mark = ctx.Actor.Statuses.FirstOrDefault(static s => s.Def.DoubleNextBloom);
            if (mark is not null)
            {
                ctx.Actor.Statuses.Remove(mark);
                state.Say("· 「借你羽梢一瞬」已消耗", 3);
            }
        }
    }

    private static void ApplyStatus(BattleState state, EffectDef e, EffectContext ctx, string? id, bool beneficial, int depth)
    {
        if (string.IsNullOrEmpty(id)) return;
        StatusDef? def = state.Data.Status(id);
        if (def is null)
        {
            state.Note($"引用了未定义的状态 id：'{id}'（05 §7 校验 10：applyBuff/applyDebuff 引用的 id 必须已定义）。");
            return;
        }

        // 光环（`01 §1.3`）：挂在**施加者**身上、按施加者的行动计时、效果覆盖全队。
        // ⚠️ 只挂**一份**：`Targets(partyAll)` 返回全队，若逐个挂到施法者身上，
        //    同一个光环会变成"队伍人数"份、在施法者身上叠乘（这是一个真实存在过的 bug）。
        //    扩散由 `BattleState.StatusesAffecting` 负责，`BattleUnit.Stats` 会读它。
        bool aura = def.AppliesTo == TargetSpec.PartyAll;

        foreach (BattleUnit t in Targets(state, e.Target, ctx, beneficial))
        {
            if (aura && !ReferenceEquals(t, ctx.Actor)) continue;

            // 专拐标注（02 §0 宪法 5）：只对列出的角色 id 生效。
            if (def.ExclusiveTo is { Count: > 0 } && !def.ExclusiveTo.Contains(t.DefId, StringComparer.Ordinal))
            {
                state.Say($"· {t.Name} 不在「{def.Name}」的适用名单内，跳过", 3);
                continue;
            }

            // **全场唯一**（`EffectDef.Unique`）：
            //   ① 目标已经持有 ⇒ **整个效果跳过**（不叠层、不刷新、也不重复触发它带来的附带效果）；
            //   ② 别人身上有 ⇒ 先摘掉，保证"场上同时只有一个"。
            if (e.Unique)
            {
                if (t.StatusStacks(def.Name) > 0)
                {
                    state.Say($"· {t.Name} 已经持有【{def.Name}】，本次不重复施加", 3);
                    continue;
                }
                foreach (BattleUnit other in state.Units)
                {
                    if (ReferenceEquals(other, t) || !other.IsAlive) continue;
                    for (int i = other.Statuses.Count - 1; i >= 0; i--)
                    {
                        StatusInstance old = other.Statuses[i];
                        if (!ReferenceEquals(old.Def, def)) continue;
                        state.Say($"· 【{def.Name}】从 {other.Name} 转移到 {t.Name}", 3);
                        AnomalySystem.ExpireStatus(state, other, old, expired: false);
                    }
                }
            }

            double duration = e.Duration > 0 ? e.Duration : def.Duration;
            // 层数：默认取 `e.Stacks`；`perStack` 指向某个资源时，**层数 = 该资源的当前层数**
            // （爱丽丝的「誓言 → 二段终结技增伤」要把"攒了多少层"整堆变成一个增益的层数）。
            int stacks = Math.Max(1, e.Stacks);
            if (!string.IsNullOrEmpty(e.PerStack)) stacks = Math.Max(1, ctx.Actor.StackOf(e.PerStack));

            StatusInstance? existing = t.Statuses.Find(s => ReferenceEquals(s.Def, def) && ReferenceEquals(s.Owner, t));
            if (existing is not null)
            {
                int before = existing.Stacks;
                if (def.Stackable) existing.Stacks = Math.Min(def.StackCap, existing.Stacks + stacks);
                existing.Remaining = Math.Max(existing.Remaining, duration); // 叠加时刷新持续时间（01 §6.2 通则 3）。
                state.Say($"· {t.Name} 的【{def.Name}】→ {existing.Stacks} 层（刷新 {duration:0.#}）", 3);
                // 层数**变多**才算「达到」——`onStackReach` 的语义是"某状态层数发生了变化"，
                // 而不是"某状态被施加"。阈值判定交给触发条件的 `statusAtLeast:`（见 Conditions）。
                if (existing.Stacks > before)
                    HookBus.Fire(state, "onStackReach", new HookContext { Actor = t, Target = t }, depth + 1);
                // ⚠️ 这里**必须是 `continue` 而不是 `return`**（本轮修的一个真 bug）：
                // 原来写的是 `return`，于是"给全体刷新同一个状态"只作用于**列表里的第一个**
                // —— 第一个目标身上已经有该状态时就直接退出整个方法，后面三个队友一行日志都没有。
                // 症状极其隐蔽：日志看起来完全正常，只有"为什么只有 1 号位在掉层"这一个疑点。
                continue;
            }

            // 光环（01 §1.3）：挂在**施加者**身上、按施加者的行动次数计时、效果覆盖 AppliesTo 指定的范围。
            BattleUnit holder = aura ? ctx.Actor : t;
            StatusInstance st = new()
            {
                Def = def,
                Owner = holder,
                Source = ctx.Actor,
                TickSource = holder,
                Stacks = Math.Min(def.StackCap, stacks),
                Remaining = duration,
                SourceLevel = ctx.Actor.Level,
            };
            holder.Statuses.Add(st);
            t.NormalizeHp();
            state.Say($"· {t.Name} 获得【{def.Name}】" +
                      (st.Stacks > 1 ? $"×{st.Stacks}" : "") + $"（{duration:0.#} {def.DurationUnit}）", 3);
            // 新挂上的第一层也算"层数达到"——否则 `statusAtLeast:X:1` 的触发永远不会响。
            HookBus.Fire(state, "onStackReach", new HookContext { Actor = t, Target = t }, depth + 1);
        }
    }

    /// <summary>
    /// `decayStatus`：把目标身上某个**状态**的层数减 `amount`（默认 1）；减到 0 就摘掉。
    /// </summary>
    /// <remarks>
    /// <b>为什么不能复用 `spendStack`</b>：那个原子读写的 `BattleUnit.Stacks` 是**资源**计数
    /// （风涌 / 誓言 / 余火），而 `applyBuff` / `applyDebuff` 落的层数在 `Statuses` 里。
    /// 用 `spendStack` 去减「注目」只会静默地什么都没发生 —— 这正是本项目最容易复发的一类 bug
    /// （"写了一个看起来对的原子，运行时无声无息"）。所以这里宁可多一个原子，也不要含糊。
    /// <para>
    /// 摘除走 <see cref="AnomalySystem.ExpireStatus"/>（`expired: false`）—— 与 `cleanse` 同一条路径，
    /// 这样"层数归零时该不该派发到期钩子"的口径只有一处。
    /// </para>
    /// </remarks>
    private static void DecayStatus(BattleState state, EffectDef e, EffectContext ctx)
    {
        string? id = e.StatusId ?? e.DebuffId ?? e.BuffId;
        if (string.IsNullOrEmpty(id)) return;
        StatusDef? def = state.Data.Status(id);
        if (def is null)
        {
            state.Note($"引用了未定义的状态 id：'{id}'（decayStatus）。");
            return;
        }

        int amount = (int)Math.Max(1.0, e.Amount > 0 ? e.Amount : e.Stacks);

        foreach (BattleUnit t in Targets(state, e.Target, ctx, beneficial: false))
        {
            for (int i = t.Statuses.Count - 1; i >= 0; i--)
            {
                StatusInstance st = t.Statuses[i];
                if (!ReferenceEquals(st.Def, def)) continue;

                st.Stacks -= amount;
                if (st.Stacks <= 0)
                {
                    state.Say($"· {t.Name} 的【{def.Name}】层数归零 → 解除", 3);
                    AnomalySystem.ExpireStatus(state, t, st, expired: false);
                }
                else
                {
                    state.Say($"· {t.Name} 的【{def.Name}】−{amount} → {st.Stacks}/{def.StackCap}", 3);
                }
            }
        }
    }

    private static void DeployField(BattleState state, EffectDef e, EffectContext ctx)
    {
        if (string.IsNullOrEmpty(e.FieldId)) return;
        StatusDef? def = state.Data.Status(e.FieldId);
        if (def is null)
        {
            state.Note($"引用了未定义的领域 id：'{e.FieldId}'。");
            return;
        }

        // 领域全场唯一，默认不可并存（01 §1.3）。
        FieldInstance? old = state.Fields.Find(f => ReferenceEquals(f.Def, def));
        if (old is not null) state.Fields.Remove(old);

        double duration = e.Duration > 0 ? e.Duration : def.Duration;
        state.Fields.Add(new FieldInstance
        {
            Def = def,
            Caster = ctx.Actor,
            Remaining = duration,
            DurationUnit = string.Equals(def.DurationUnit, "round", StringComparison.Ordinal) ? 100.0 : duration,
        });
        state.Say($"◆ {ctx.Actor.Name} 展开领域【{def.Name}】（{duration:0.#} {def.DurationUnit}）", 2);
    }

    private static void Summon(BattleState state, EffectDef e, EffectContext ctx, int depth)
    {
        if (string.IsNullOrEmpty(e.SummonId)) return;
        StatusDef? def = state.Data.Status(e.SummonId);
        if (def is null)
        {
            state.Note($"引用了未定义的召唤物 id：'{e.SummonId}'（05 §2.1 的 summon- 命名空间）。");
            return;
        }
        Runner.SpawnSummon(state, def, ctx.Actor, e, depth);
    }

    private static void RunChanceCheck(BattleState state, EffectDef e, EffectContext ctx, int depth)
    {
        // 概率旋钮优先：`chanceKnob` 让"加不加、加多少"完全由权阶/装备决定（首个使用者：瓦列里娅 Ⅵ）。
        double p = e.Chance ?? 0.0;
        if (!string.IsNullOrEmpty(e.ChanceKnob))
        {
            p = ctx.Actor.EidolonKnobs.GetValueOrDefault(e.ChanceKnob) + ctx.Actor.KnobValue(e.ChanceKnob);
        }

        p = Math.Clamp(p, 0.0, 1.0);
        bool ok = state.Rng.Chance(p);
        state.Say($"· 概率判定 {p * 100:0.#}% → {(ok ? "命中" : "未命中")}", 3);
        Run(state, ok ? e.OnSuccess ?? [] : e.OnFail ?? [], ctx, depth + 1);
    }

    private static void RunConditional(BattleState state, EffectDef e, EffectContext ctx, int depth)
    {
        bool ok = Conditions.Evaluate(state, e.Condition, ctx.Actor, ctx.Target);
        Run(state, ok ? e.Then ?? [] : e.OnFail ?? [], ctx, depth + 1);
    }

    /// <summary>
    /// 直接秒杀（`01 §6.2` 通则 6）：**一切直接秒杀 / 处决对精英级及以上无效**，只对杂兵生效。
    /// 守约人天赋的"1% 秒杀分支对精英及以上不生效、直接走 else 分支"就是在这里被短路掉的。
    /// </summary>
    private static void Execute(BattleState state, EffectDef e, EffectContext ctx)
    {
        foreach (BattleUnit t in Targets(state, e.Target, ctx, beneficial: false))
        {
            if (BattleConstants.ExecuteEliteImmune && t.IsEliteOrAbove)
            {
                state.Say($"· {t.Name} 是精英级及以上，免疫秒杀（01 §6.2 通则 6）", 3);
                continue;
            }
            state.Say($"·◆ {ctx.Actor.Name} 直接秒杀 {t.Name}", 3);
            double amount = t.Hp + t.Shield;
            Combat.ApplyPrecomputed(state, ctx.Actor, t, Element.Physical, amount, false, DamageCategory.Direct, "秒杀", 0);
        }
    }

    /// <summary>
    /// **「可燃引爆」**（`01 §3.2.3` 的专属爆发点，使用者：贝尔兰德 `07 §8`）。
    /// </summary>
    /// <remarks>
    /// 为什么单列一个原子而不是拼现有原子：它的三个要素**都随"目标当前可燃层数"变化** ——
    /// <list type="number">
    ///   <item>**触发概率** = `层数 × 每层概率`，且**有上限**（`层数越高越容易炸`）；</item>
    ///   <item>**伤害倍率** = `层数 × 每层系数`（不是固定倍率）；</item>
    ///   <item>**满层资源可以把它从"概率"变成"必定 "+ 增伤**。</item>
    /// </list>
    /// 现有的 `dealDamage` / `additionalDamage` 都只有固定倍率，表达不了"随层数成长"。
    /// <para>
    /// ⚠️ **它是"可燃引爆"这一族的通用原子**，不是贝尔兰德的私有实现 —— 未来的火属性角色
    /// （`102 §3 B2` 的第二名）直接用同一套参数即可，参数写在数据里。
    /// </para>
    /// </remarks>
    private static void DetonateCombust(BattleState state, EffectDef e, EffectContext ctx, int depth)
    {
        double perStack = e.ChancePerStack > 0 ? e.ChancePerStack : 0.015;
        double cap = e.ChanceCap > 0 ? e.ChanceCap : 0.30;
        double perStackMult = e.Multiplier > 0 ? e.Multiplier : 0.25;

        Element element = ParseElement(e.Element, ctx.Actor);
        AnomalyFamily family = ElementInfo.FamilyOf(element);

        foreach (BattleUnit t in Targets(state, e.Target, ctx, beneficial: false))
        {
            if (!t.IsAlive) continue;
            if (!t.Anomalies.TryGetValue(family, out AnomalyState? a) || a.Stacks <= 0)
            {
                state.Say($"· 爆燃判定：{t.Name} 身上没有{Elements2Name(family)}层数，不成立", 3);
                continue;
            }

            int stacks = a.Stacks;

            // ── 机制旋钮：装备特效给的那两条（M7） ──────────────────
            // 「斯瓦迪尔法利之焰剑」的 35~55% 额外施加概率、+8~16% 爆燃伤害都从这里读。
            // 它们**跟着状态的生命周期走**（挂在 `onCombustDetonate` 上、有持续回合），
            // 所以不能写死在单位上。
            double extra = ctx.Actor.KnobValue("extraCombustChance");
            double bonusFromKnobs = ctx.Actor.KnobValue("detonateDamageBonus");
            double chance = Math.Min(cap + extra, stacks * perStack + extra);

            // 「基础概率」（01 §2.3）：受攻击者效果命中与目标效果抵抗修正。
            chance = Math.Clamp(chance * (1.0 + ctx.Actor.Stats.EffectHitValue) * (1.0 - t.Stats.EffectResValue), 0.0, 1.0);

            bool forced = !string.IsNullOrEmpty(e.ForceStack) && ctx.Actor.StackOf(e.ForceStack) >= e.ForceCount;
            bool ok = forced || state.Rng.Chance(chance);

            // ⚠️ 这一行的概率要**把装备旋钮算进去**再报 —— 第一版只印了"层数 × 每层系数"
            //    而实际用的是"再 + 焰剑的 35%"。日志与数值对不上，排查时会先怀疑引擎。
            state.Say($"· 爆燃判定：{stacks} 层 × {perStack:P1} = {stacks * perStack:P0}（上限 {cap:P0}）" +
                      (extra > 0 ? $" + 装备额外 {extra:P0}" : "") +
                      $" ⇒ {chance:P0}" +
                      (forced ? $" → **{e.ForceStack} 满层，必定触发**" : $" → {(ok ? "触发" : "未触发")}"), 3);
            if (!ok) continue;

            double mult = stacks * perStackMult * (1.0 + (forced ? e.ForceBonus : 0.0) + bonusFromKnobs);
            Combat.DealDamage(state, new DamageRequest
            {
                Source = ctx.Actor,
                Target = t,
                Element = element,
                Multiplier = mult,
                Category = DamageCategory.Additional,
                AccumulateAnomaly = false,
                SuppressCombustStackDamage = true,
                Skill = ctx.Skill,
                Label = "爆燃",
            }, depth);

            HookBus.Fire(state, "onCombustDetonate", new HookContext { Actor = ctx.Actor, Target = t }, depth + 1);
        }
    }

    private static string Elements2Name(AnomalyFamily family) => ElementInfo.FamilyToChinese(family);

    /// <summary>
    /// `selfHpCost`（`M7`）：**按自身最大生命的比例扣自己的血，最低留 1 点**。
    /// </summary>
    /// <remarks>
    /// 首个使用者：「璀璨不再的启明星」（`101 §4.3`）—— 装备者释放共鸣解放时消耗自身最大生命值 10%。
    /// <para>
    /// ⚠️ **它不致死**（"最多降至 1 点"是原文口径）。做成致死会在"血量刚好卡在 10% 以下"时
    /// 把一次正常操作变成自杀，而那种死法玩家无从预判。
    /// </para>
    /// <para>
    /// ⚠️ 它**不走伤害管线**：不是"受到伤害"，所以不吃减伤、不触发出血/受击回能、
    /// 也不进 `onDamageTaken`。这是一次**代价**，不是一次攻击。
    /// </para>
    /// </remarks>
    private static void SelfHpCost(BattleState state, EffectDef e, EffectContext ctx)
    {
        double pct = e.Value != 0.0 ? e.Value : e.Delta;
        if (pct <= 0.0) return;

        foreach (BattleUnit u in Targets(state, e.Target ?? "self", ctx, beneficial: false))
        {
            double cost = Math.Min(u.MaxHp * pct, Math.Max(0.0, u.Hp - 1.0));
            if (cost <= 0.0) continue;
            u.Hp -= cost;
            state.Say($"· {u.Name} 支付代价：最大生命 {pct:P0} → 实际扣 {cost:0.#}，剩余 {u.Hp:0}/{u.MaxHp:0}", 3);
        }
    }

    /// <summary>
    /// `modifyStat`（`05 §3.2`）：**给目标挂一个只带一条修正的状态**。
    /// </summary>
    /// <remarks>
    /// <b>为什么不直接改 <see cref="BattleUnit.Base"/></b>：`01 §1.3` 的 buff 三分法要的三件事
    /// ——有时长、可叠层、按谁的行动计时——全都由 <see cref="StatusInstance"/> 承载。
    /// 直接改面板既不会到期、也无法刷新，而且会把"这一刻的面板"与"初始面板"混在一起，
    /// 让 `PanelCalculator`（养成面板）与战斗内面板再也对不上。
    /// <para>
    /// 取值用 `value`；若只写了 `delta` 就用 `delta`（两者都是**带符号**的，见校验 15）。
    /// </para>
    /// </remarks>
    private static void ModifyStat(BattleState state, EffectDef e, EffectContext ctx)
    {
        if (string.IsNullOrEmpty(e.Stat))
        {
            state.Note("原子 modifyStat 没有写 stat，已忽略。");
            return;
        }

        double value = e.Value != 0.0 ? e.Value : e.Delta;
        string target = e.Target ?? "self";
        double duration = e.Duration;
        string? appliesTo = string.Equals(target, "partyAll", StringComparison.Ordinal) ? "partyAll" : null;

        // 合成 id 必须**只由内容决定**，否则同一个效果每次触发都会造出一个新状态定义。
        string id = $"{StatusFactory.Prefix}modifyStat:{e.Stat}:{value:0.####}:{target}";
        StatusDef def = StatusFactory.Build(
            state.Data, id, $"{e.Stat} {value:+0.####;-0.####}", [(e.Stat, value)], duration, appliesTo,
            out List<string> unmapped);

        if (unmapped.Count > 0)
        {
            // **不静默丢弃**：这是"写得进数据、引擎不认"的那一类，必须留下痕迹。
            state.Note($"原子 modifyStat 的属性名 '{e.Stat}' 引擎不认识（既不是面板词条、也不是按属性记的增伤）—— 本次未生效。");
            return;
        }

        foreach (BattleUnit t in Targets(state, target, ctx, beneficial: value >= 0.0))
        {
            StatusFactory.Attach(t, def, ctx.Actor);
            state.Say($"▸ {t.Name} 获得【{def.Name}】" +
                      (duration > 0 ? $"（{duration:0.#} 回合）" : "（本场持续）"), 3);
        }
    }

    private static void Cleanse(BattleState state, EffectDef e, EffectContext ctx)
    {
        foreach (BattleUnit t in Targets(state, e.Target, ctx, beneficial: true))
        {
            for (int i = t.Statuses.Count - 1; i >= 0; i--)
            {
                StatusInstance st = t.Statuses[i];
                if (st.Def.Kind != "debuff") continue;
                AnomalySystem.ExpireStatus(state, t, st, expired: false);
            }
            // 属性异常 / 硬控的解除（count / random 语义见 01 §6.5）。
            foreach (AnomalyState a in t.Anomalies.Values)
            {
                a.Stacks = 0;
                a.Frozen = false;
                a.Stunned = false;
                a.Cower = false;
                a.PendingDelivery = false;
                a.PendingReplay = null;
                a.Remaining = 0.0;
            }
            state.Say($"· {t.Name} 被净化", 3);
        }
    }

    // ────────────────────────────────────────────────────────────────────

    private static Element ParseElement(string? name, BattleUnit fallback)
        => string.IsNullOrEmpty(name) ? fallback.Elements[0] : ElementInfo.Parse(name);

    /// <summary>
    /// 目标解析。**这是"我方角色 vs 我方单位"（`01 §6.3`）唯一落地的地方** ——
    /// 任何地方再写一遍"我方 = 4 人"都会让召唤物被漏掉。
    /// </summary>
    public static List<BattleUnit> Targets(BattleState state, string? spec, EffectContext ctx, bool beneficial)
    {
        TargetSpec.TryResolve(spec, beneficial, out string resolved);
        BattleUnit actor = ctx.Actor;

        List<BattleUnit> result = [];
        switch (resolved)
        {
            case TargetSpec.Self:
                result.Add(actor);
                break;

            case TargetSpec.PartyAll:
                // 「我方全体」= **施法者自己那一边**（含召唤物，01 §6.3）。
                // 施法者是玩家侧时它逐元素等于旧的 `Disposition == Friendly` ⇒ 行为不变；
                // 敌方单位施放增益时从此作用于**它自己的阵营**，而不是我们。
                result.AddRange(state.OwnSide(actor));
                break;

            case TargetSpec.PartyCharacters:
                result.AddRange(state.OwnCharacters(actor));
                break;

            case TargetSpec.PartySlot1:
            {
                List<BattleUnit> own = state.OwnSide(actor);
                if (own.Count > 0) result.Add(own[0]);
                break;
            }

            case TargetSpec.PartyLowestHp:
            {
                BattleUnit? low = state.OwnSide(actor)
                    .OrderBy(static u => u.HpRatio)
                    .ThenBy(static u => u.Serial)
                    .FirstOrDefault();
                if (low is not null) result.Add(low);
                break;
            }

            case TargetSpec.MainTarget:
                if (ctx.MainTarget is { IsAlive: true } main) result.Add(main);
                break;

            case TargetSpec.AllEnemies:
                // 「敌方全体」= 施法者的**对面**（见 `ResolveHitTargets` 的同一处说明）。
                result.AddRange(state.OpposingSide(actor));
                break;

            case TargetSpec.RandomEnemy:
            {
                List<BattleUnit> foes = state.OpposingSide(actor);
                if (foes.Count > 0) result.Add(state.Rng.Pick(foes));
                break;
            }

            case TargetSpec.AdjacentEnemies:
            {
                // 相邻 = **主目标自己那一边**的站位 ±1（与 `Runner.ResolveHitTargets` 同一口径）。
                if (ctx.MainTarget is not null)
                {
                    BattleUnit? left = Combat.SelectAdjacent(state, ctx.MainTarget, -1);
                    BattleUnit? right = Combat.SelectAdjacent(state, ctx.MainTarget, +1);
                    if (left is not null) result.Add(left);
                    if (right is not null) result.Add(right);
                }
                break;
            }

            case TargetSpec.MostRecentlyDamagedEnemy:
                if (state.MostRecentlyDamagedEnemy is { IsAlive: true } recent) result.Add(recent);
                break;

            // 本次协同攻击选中的角色（01 §5.5）—— 共生灵天赋「如常的加护」挂给它。
            case TargetSpec.CoopTarget:
                if (state.CoopTarget is { IsAlive: true } coop) result.Add(coop);
                break;

            case TargetSpec.OwnSummons:
                result.AddRange(state.OwnSummonsOf(actor));
                break;

            case TargetSpec.AllSummons:
                result.AddRange(state.Summons.Where(static s => s.IsAlive));
                break;

            default:
                state.Note($"未登记的目标口径 '{resolved}'。已忽略该效果。");
                break;
        }

        return result;
    }
}
