using WaningOath.Core.Data;

namespace WaningOath.Core.Battle;

/// <summary>
/// 战斗主循环。严格照 `01 §9` 的回合执行顺序。
/// </summary>
/// <remarks>
/// <code>
/// 战斗开始
///  ├─ 初始化：共鸣指数 = 3，全体 AV = 10000/速度，应用"进入战斗时"类效果
///  └─ 循环：
///      1. 推进 AV（ΔAV = 全场最小 AV），自然回合计数 +（累计 AV / 100）
///      2. 取出 AV ≤ 0 的单位
///      3. 【我方单位】a 结算 dot/延迟 → b 选行动 → c 命中与伤害 → d 异常积蓄 → e 触发类 → f 递减 buff → g AV 重置
///      4. 【敌方单位】同 3，但行动由 AI 决定
///      4b.【召唤物】AV 归零时不产生任何指令
///      5. 检查胜负
///      6. 检查领域 / 召唤物到期、阶段转换
/// </code>
/// </remarks>
public static class Runner
{
    public static BattleOutcome Run(BattleState state, IActionPolicy policy)
    {
        Begin(state);
        while (!state.IsOver) Step(state, policy);
        Finish(state);
        return state.Outcome;
    }

    /// <summary>战斗开场（`onBattleStart` + 共鸣指数那一行）。</summary>
    /// <remarks>
    /// <see cref="Begin"/> / <see cref="Step"/> / <see cref="Finish"/> 是给**界面**用的：
    /// 客户端要"玩家点一下、推进一拍"，而不是"点一下、整场跑完"。
    /// `Run` 就是这三步的循环，**日志与逐行结果完全一致** —— 这一点由 `Sim battle` 的日志 diff 保证。
    /// </remarks>
    public static void Begin(BattleState state)
    {
        HookBus.Fire(state, "onBattleStart", new HookContext { });
        state.Say($"共鸣指数 {state.ResonanceIndex}/{state.ResonanceCap}");
    }

    /// <summary>
    /// 推进**一拍**：结算领域到期 → 推进 AV → 让本拍内 `AV ≤ 0` 的单位各行动一次。
    /// </summary>
    /// <returns>战斗是否仍在继续。</returns>
    public static bool Step(BattleState state, IActionPolicy policy)
    {
        if (state.IsOver) return false;

        // 6. 领域到期。
        TickFields(state);

        if (state.Round >= state.MaxRound)
        {
            state.Outcome = BattleOutcome.AllyLose;
            state.Say($"⚠ 达到轮次上限 {state.MaxRound}，判定失败（02 §10 的 MAX_ROUND_PER_STAGE）。");
            return false;
        }

        List<BattleUnit> alive = [.. state.Units.Where(static u => u.IsAlive)];
        if (alive.Count == 0) return false;

        // 1. 推进 AV。ΔAV = 全场最小 AV。
        double min = alive.Min(static u => u.Av);
        if (min > 0.0)
        {
            bool crossed = state.AdvanceClock(min);
            foreach (BattleUnit u in alive) u.Av -= min;
            if (crossed)
            {
                state.Say($"════ 自然回合 {state.Round} ════");
                HookBus.Fire(state, "onRoundEnd", new HookContext { });
                AnomalySystem.TickRoundEnd(state, 0);
                HookBus.Fire(state, "onRoundStart", new HookContext { });
                BattleFactory.SyncSpeeds(state);
            }
        }

        if (CheckOutcome(state)) return false;

        // 2. 取出 AV ≤ 0 的单位。3. 行动值相同时按站位序号升序（我方优先于敌方）。
        List<BattleUnit> ready =
        [
            .. state.Units
                .Where(static u => u.IsAlive && u.Av <= 0.0)
                .OrderBy(static u => u.Disposition == Disposition.Friendly ? 0 : 1)
                .ThenBy(static u => u.SlotIndex)
                .ThenBy(static u => u.Serial)
        ];
        if (ready.Count == 0) return true;

        foreach (BattleUnit unit in ready)
        {
            if (!unit.IsAlive || state.IsOver) continue;
            ActUnit(state, unit, policy);
            CheckOutcome(state);
        }

        return !state.IsOver;
    }

    /// <summary>收尾：把结果写进日志（M1 验收要看的"完整日志"的最后一段）。</summary>
    public static void Finish(BattleState state)
    {
        state.Say(state.Outcome switch
        {
            BattleOutcome.AllyWin => $"★ 战斗结束：我方胜利（共 {state.Round} 自然回合，{state.TurnCounter} 次行动）",
            BattleOutcome.AllyLose => $"★ 战斗结束：我方失败（共 {state.Round} 自然回合）",
            _ => "★ 战斗结束：结果未定",
        });
    }

    // ── 单位的一次行动 ────────────────────────────────────────────────

    private static void ActUnit(BattleState state, BattleUnit unit, IActionPolicy policy)
    {
        state.TurnCounter++;
        state.Say($"── 回合 {state.Round} · {unit.Name} 行动（{unit.Hp:0}/{unit.MaxHp:0}）──", 1);
        unit.TriggerCountsTurn.Clear();
        unit.RecastsThisTurn.Clear();
        HookBus.Fire(state, "onTurnStart", new HookContext { Actor = unit });

        // a. 结算 dot / 延迟效果，并消耗被硬控的行动。
        bool consumed = AnomalySystem.ResolveTurnStart(state, unit, 0);

        if (unit.IsAlive)
        {
            if (unit.IsSummon)
            {
                if (unit.IsSymbiont && unit.Skills.Count > 0)
                {
                    // 共生灵：**有共生灵技**，靠"关联角色行动后插入额外回合"来行动（01 §1.5 的子类）。
                    string key = unit.Skills.ContainsKey("symbiontSkill")
                        ? "symbiontSkill"
                        : unit.Skills.Keys.OrderBy(static k => k, StringComparer.Ordinal).First();
                    SkillDef symbiontSkill = unit.Skills[key];
                    BattleUnit? symTarget = state.AliveEnemies.Count > 0 ? state.AliveEnemies[0] : null;
                    ExecuteSkill(state, unit, key, symbiontSkill, symTarget, 0);
                }
                else
                {
                    // 4b. 普通召唤物不产生任何指令（01 §1.5）。
                    state.Say("（召唤物只结算回合钩子，不行动）", 2);
                }
            }
            else if (consumed)
            {
                state.Say("（本次行动被控制效果消耗）", 2);
            }
            else if (unit.IsEnemy)
            {
                ActEnemy(state, unit);
            }
            else
            {
                ActAlly(state, unit, policy);
            }
        }

        // f. 递减 buff（单位回合）与异常时长。g. AV 重置。
        if (unit.IsAlive)
        {
            // 共生灵：**关联角色行动后，插入额外回合立即行动**（01 §1.5 的子类 · 07 §9.3）。
            // 它的速度是 0、不进行动轴，所以"AV 置 0"是它唯一的行动方式。
            foreach (BattleUnit s in state.Units)
            {
                if (s.IsAlive && s.IsSymbiont && ReferenceEquals(s.Summoner, unit)) s.Av = 0.0;
            }

            AnomalySystem.TickTurnEnd(state, unit);
            HookBus.Fire(state, "onTurnEnd", new HookContext { Actor = unit });
            BattleFactory.SyncSpeeds(state);
            unit.Av = BattleConstants.AvBase / Math.Max(1.0, unit.Stats.SpeedValue);
            unit.LastSpeed = Math.Max(1.0, unit.Stats.SpeedValue);
            HookBus.Fire(state, "onAllyAction", new HookContext { Actor = unit });
        }
        else
        {
            unit.Av = double.PositiveInfinity;
        }
    }

    private static void ActAlly(BattleState state, BattleUnit unit, IActionPolicy policy)
    {
        ActionChoice? choice = policy.Choose(state, unit);
        if (choice is null)
        {
            state.Say("（无可执行的行动）", 2);
            return;
        }
        if (!unit.Skills.TryGetValue(choice.SkillKey, out SkillDef? skill))
        {
            state.Note($"策略选择了不存在的技能位 '{choice.SkillKey}'（{unit.Name}）。");
            return;
        }

        BattleUnit? target = choice.TargetSerial is int serial
            ? state.Units.Find(u => u.Serial == serial && u.IsAlive)
            : null;
        target ??= state.AliveEnemies.Count > 0 ? state.AliveEnemies[0] : null;

        ExecuteSkill(state, unit, choice.SkillKey, skill, target, 0);
    }

    /// <summary>
    /// 敌方 AI（`01 §7`）：行动优先级 = 可用终结技 &gt; 可用技能 &gt; 普攻；
    /// **按仇恨加权随机**选敌；群攻不按仇恨。
    /// </summary>
    private static void ActEnemy(BattleState state, BattleUnit unit)
    {
        List<BattleUnit> targets = state.LegalTargetsForEnemy;
        if (targets.Count == 0) return;

        string? key = null;
        foreach (string candidate in new[] { "ultimate", "resonance", "skill", "basic" })
        {
            if (!unit.Skills.TryGetValue(candidate, out SkillDef? s)) continue;
            if (s.Cost.Energy > 0 && unit.Energy < s.Cost.Energy) continue;
            key = candidate;
            break;
        }
        key ??= unit.Skills.Keys.OrderBy(static k => k, StringComparer.Ordinal).FirstOrDefault();
        if (key is null) return;

        SkillDef skill = unit.Skills[key];

        // 群攻不按仇恨，命中全部合法目标。
        BattleUnit target = IsAoe(skill) ? targets[0] : SelectByThreat(state, targets);
        ExecuteSkill(state, unit, key, skill, target, 0);
    }

    private static bool IsAoe(SkillDef skill)
        => string.Equals(skill.TargetType, "aoe", StringComparison.Ordinal)
           || skill.Hits.Exists(static h => string.Equals(h.Target, "all", StringComparison.Ordinal));

    /// <summary>按仇恨加权随机选敌：`选中概率 = 该目标仇恨值 ÷ 全体合法目标仇恨值之和`。**权重下限 1。**</summary>
    private static BattleUnit SelectByThreat(BattleState state, List<BattleUnit> candidates)
    {
        // 「嘲讽」不是加权重，而是**改写可选目标集合**：只要场上存在持嘲讽的合法目标，
        // 单体攻击就只能从这些目标里选（01 §7 的强制仇恨）。没有嘲讽时退回原加权随机。
        List<BattleUnit> taunting = [.. candidates.Where(static u =>
            u.Statuses.Exists(static s => s.Def.Taunt && (s.Remaining > 0.0 || s.Def.Duration <= 0.0)))];
        if (taunting.Count > 0) candidates = taunting;

        double total = candidates.Sum(static u => u.ThreatWeight);
        double roll = state.Rng.NextDouble() * total;
        double acc = 0.0;
        foreach (BattleUnit u in candidates)
        {
            acc += u.ThreatWeight;
            if (roll < acc) return u;
        }
        return candidates[^1];
    }

    // ── 技能执行 ──────────────────────────────────────────────────────

    /// <summary>
    /// 释放一个技能。
    /// </summary>
    /// <remarks>
    /// <b>结算顺序（M1 的实现决策，需在文档里补一句）</b>：
    /// <list type="number">
    ///   <item>扣资源（能量 / 共鸣指数；点数为 0 时走<b>透支</b>）。</item>
    ///   <item>`onCast` 效果 —— 先上 buff 再打伤害，这样"本技能自己给的增伤"能吃到。</item>
    ///   <item>`onAttack` 钩子 —— **在伤害之前**：守约人天赋的"1% 直接秒杀当前敌人"若放在伤害之后，
    ///         被秒杀的目标还会先吃到一轮伤害，与"直接秒杀"的语义不符（`07 §1.2`）。</item>
    ///   <item>逐段结算伤害（每段一次 `onHit`）。</item>
    ///   <item>`onUltimateCast` / `onResonanceCast` / `onSkillCast` —— **在伤害之后**：
    ///         星遥的"任意我方释放终结技攻击敌人后，自身立即对该敌人发动追加攻击"（`07 §2.2`）要求终结技先落地。</item>
    /// </list>
    /// </remarks>
    public static void ExecuteSkill(
        BattleState state,
        BattleUnit actor,
        string skillKey,
        SkillDef skill,
        BattleUnit? mainTarget,
        int depth,
        bool ignoreCost = false)
    {
        state.Say($"◆ {actor.Name} 释放【{skill.Name}】" + (mainTarget is not null ? $" → {mainTarget.Name}" : ""), 2);
        Combat.BeginWindow();
        AnomalySystem.OpenWindow(state);

        // **发动前提**（`SkillCostDef.Requires`）。策略层已经拦过一次，这里再拦一次 ——
        // 因为"立即行动""追加攻击"这类非策略路径也可能把技能送进来。
        // ⚠️ `ignoreCost: true`（协同攻击，`01 §5.5`）会连这一条一起跳过 —— "无视条件"是字面意义。
        if (!ignoreCost && !Conditions.Evaluate(state, skill.Cost.Requires, actor, mainTarget))
        {
            state.Say($"· 不满足发动前提（{skill.Cost.Requires}），本次技能取消", 3);
            return;
        }

        // **每自然回合一次**（`SkillCostDef.OncePerRound`）。它约束的是**频率**，
        // 与"能不能放"（Requires）、"付不付得起"（资源）都不是一回事：
        // 没有这一条，行动提前 / 额外回合 / 协同攻击里任何一条路径都能把同一个终结技在一回合里再放一次。
        if (!ignoreCost && skill.Cost.OncePerRound && actor.OncePerRoundUsed(skillKey) == state.Round)
        {
            state.Say($"· 【{skill.Name}】本自然回合已经发动过，本次取消", 3);
            return;
        }

        // **条件免消耗**（`SkillCostDef.FreeWhen`）：成立时不扣 `Stack`，但技能照放。
        bool costFree = !ignoreCost && !string.IsNullOrEmpty(skill.Cost.FreeWhen)
                        && Conditions.Evaluate(state, skill.Cost.FreeWhen, actor, mainTarget);

        // 本技能已经命中过谁。供 `target: "randomUnhit"` 用（风蚀模态的强化共鸣要求"优先弹射未被本次技能攻击过的目标"）。
        HashSet<BattleUnit> hitThisSkill = [];

        // ── 1. 资源 ──
        if (!ignoreCost && skill.Cost.Energy > 0) actor.Energy = Math.Max(0.0, actor.Energy - skill.Cost.Energy);

        if (!ignoreCost && skill.Cost.ResonanceIndex > 0)
        {
            if (state.ResonanceIndex >= skill.Cost.ResonanceIndex)
            {
                state.ResonanceIndex -= skill.Cost.ResonanceIndex;
            }
            else
            {
                // 透支（01 §4.1）：点数为 0 时强行释放，扣释放者 25% 生命上限，**可致死**。
                EffectResolver.Overdraw(state, actor, depth);
                if (!actor.IsAlive) return;
            }
        }

        if (skill.Kind == "basic") state.BasicAttackCount++;
        if (skill.Kind is "resonance" or "enhanced_resonance") state.ResonanceCastCount++;

        // 台账（M2）：统计每个技能位的释放次数 —— 用于确认"自动策略跑出来的循环"到底是什么。
        if (actor.Disposition == Disposition.Friendly)
        {
            string useKey = actor.DefId + ":" + skillKey;
            state.Metrics.SkillUses[useKey] = state.Metrics.SkillUses.GetValueOrDefault(useKey) + 1;
        }

        // 技能自带的资源产出（普攻 +1 共鸣指数 / +10 能量等）。
        if (skill.Gain.ResonanceIndex != 0) EffectResolver.ChangeResonance(state, skill.Gain.ResonanceIndex, actor);
        if (skill.Gain.Energy != 0) Combat.GainEnergy(actor, skill.Gain.Energy);

        // 资源型堆叠的消耗（例：强化元素共鸣消耗 1 层「风涌」）。
        if (!ignoreCost && !string.IsNullOrEmpty(skill.Cost.Stack) && !costFree)
        {
            actor.GainStack(skill.Cost.Stack, -skill.Cost.StackAmount, 0);
            state.Say($"· 消耗「{skill.Cost.Stack}」×{skill.Cost.StackAmount} → 剩余 {actor.StackOf(skill.Cost.Stack)}", 3);
        }
        else if (costFree)
        {
            state.Say($"· 【{skill.Cost.FreeWhen}】成立 ⇒ 本次不消耗「{skill.Cost.Stack}」", 3);
        }

        // 频率台账要记在**真正放出去之后**，否则"被前提拦下"的那一次也会占掉本回合的名额。
        if (!ignoreCost && skill.Cost.OncePerRound) actor.MarkOncePerRound(skillKey, state.Round);

        // ── 2. onCast 效果 ──
        EffectContext ctx = new() { Actor = actor, Target = mainTarget, Skill = skill };
        EffectResolver.Run(state, skill.OnCast, ctx, depth);
        if (skill.Gain.ResonanceIndex != 0 || skill.Cost.ResonanceIndex != 0)
        {
            state.Say($"· 共鸣指数 {state.ResonanceIndex}/{state.ResonanceCap}", 3);
        }

        // ── 3. onAttack（每次攻击 = 一次技能，与命中段数无关 —— 01 §2.5） ──
        HookBus.Fire(state, "onAttack", new HookContext { Actor = actor, Target = mainTarget, Skill = skill }, depth);

        // ── 4. 逐段伤害 ──
        int level = actor.SkillLevel(skillKey);
        foreach (HitDef hit in skill.Hits)
        {
            Element element = string.IsNullOrEmpty(hit.Element)
                ? actor.Elements[0]
                : ElementInfo.Parse(hit.Element);
            double mult = hit.Multiplier * BattleConstants.LevelMult(skill.ScalingArray, level);
            int times = Math.Max(1, hit.Times);

            for (int i = 0; i < times; i++)
            {
                List<BattleUnit> targets = ResolveHitTargets(state, hit, mainTarget, hitThisSkill, actor);
                if (targets.Count == 0) break;

                foreach (BattleUnit t in targets)
                {
                    if (!t.IsAlive) continue;
                    hitThisSkill.Add(t);
                    double targetMult = mult;
                    if (hit.LostHpMultiplier > 0.0)
                    {
                        double lost = Math.Clamp(1.0 - t.HpRatio, 0.0, 1.0);
                        targetMult += Math.Min(hit.LostHpCap, lost * hit.LostHpMultiplier);
                    }
                    Combat.DealDamage(state, new DamageRequest
                    {
                        Source = actor,
                        Target = t,
                        Element = element,
                        Multiplier = targetMult,
                        ScalingBase = skill.ScalingBase,
                        CanCrit = hit.CanCrit,
                        IgnoreDefense = hit.IgnoreDefense,
                        Category = DamageCategory.Direct,
                        AccumulateAnomaly = hit.AccumulateAnomaly,
                        SuppressCombustStackDamage = hit.SuppressCombustStackDamage,
                        Skill = skill,
                        Label = skill.Name,
                    }, depth);

                    // 段级 `onHit`：**对每个被打到的目标各执行一次**（例：为命中者各施加 1 层可燃）。
                    // 技能级的效果列表只会给主目标上，多段技能的对群施加就丢了。
                    if (hit.OnHit.Count > 0 && t.IsAlive)
                    {
                        EffectResolver.Run(state, hit.OnHit,
                            new EffectContext { Actor = actor, Target = t, Skill = skill }, depth + 1);
                    }
                }
                if (string.Equals(hit.Target, "main", StringComparison.Ordinal) && !(mainTarget?.IsAlive ?? false)) break;
            }
        }

        // ── 5. 释放类钩子（在伤害之后） ──
        HookBus.Fire(state, "onSkillCast", new HookContext { Actor = actor, Target = mainTarget, Skill = skill }, depth);
        if (skill.Kind == "ultimate")
        {
            actor.HasCastUltimate = true;
            HookBus.Fire(state, "onUltimateCast", new HookContext { Actor = actor, Target = mainTarget, Skill = skill }, depth);
            HookBus.Fire(state, "onAllyUltimateCast", new HookContext { Actor = actor, Target = mainTarget, Skill = skill }, depth);
        }
        if (skill.Kind is "resonance" or "enhanced_resonance")
        {
            HookBus.Fire(state, "onResonanceCast", new HookContext { Actor = actor, Target = mainTarget, Skill = skill }, depth);
            HookBus.Fire(state, "onAllyResonanceCast", new HookContext { Actor = actor, Target = mainTarget, Skill = skill }, depth);
        }

        // ── 6. 「非攻击行动」 ──
        // 裁决 `99 D0` 给了「注目」的破绽：「**任何非攻击行动（换人 / 防御 / 治疗）使「注目」衰减 1 层**」。
        // 本作目前没有独立的"换人/防御"指令，**能落在这一档的就是"这一手没有伤害段"**（治疗/护盾/纯增益）。
        // 用 `Hits` 的静态形态判定而不是"这一次实际打出了多少伤害"——后者会被闪避 / 免疫 /
        // 目标已死污染（"治疗手打了一下空"不该变成攻击行动）。
        if (!DealsDamage(skill))
            HookBus.Fire(state, "onNonAttackAction", new HookContext { Actor = actor, Target = actor, Skill = skill }, depth);

        AnomalySystem.OpenWindow(state);
    }

    /// <summary>该技能是否**含有伤害段**（`multiplier &gt; 0` 的 hit）。判定一次攻击 / 非攻击行动用。</summary>
    private static bool DealsDamage(SkillDef skill)
        => skill.Hits.Exists(static h => h.Multiplier > 0.0);

    /// <summary>一段命中的目标集合（`01 §1.1` 的四种形态 + 本作新增的"优先未命中"）。</summary>
    private static List<BattleUnit> ResolveHitTargets(
        BattleState state, HitDef hit, BattleUnit? mainTarget, HashSet<BattleUnit> hitThisSkill, BattleUnit actor)
    {
        List<BattleUnit> result = [];

        // ⚠️ **"敌方" = 施法者的对面**（不是 `state.AliveEnemies`）。
        //    旧实现写死了玩家视角 ⇒ **敌方单位放群攻会打自己的队友**。
        //    施法者是玩家侧时，下面的 `OpposingSide` 逐元素等于旧的 `AliveEnemies`，
        //    所以玩家侧数据的行为一字未变（`Sim battle` 的日志 diff 可证）。
        List<BattleUnit> foes = state.OpposingSide(actor);
        List<BattleUnit> foePool = foes;

        switch (hit.Target)
        {
            case "main":
                if (mainTarget is { IsAlive: true }) result.Add(mainTarget);
                break;

            case "adjacent":
                if (mainTarget is { IsAlive: true })
                {
                    result.Add(mainTarget);
                    BattleUnit? left = Combat.SelectAdjacent(state, mainTarget, -1);
                    BattleUnit? right = Combat.SelectAdjacent(state, mainTarget, +1);
                    if (left is not null) result.Add(left);
                    if (right is not null) result.Add(right);
                }
                break;

            case "all":
                result.AddRange(foePool);
                break;

            case "random":
            {
                if (foePool.Count > 0) result.Add(state.Rng.Pick(foePool));
                break;
            }

            /// 守约人**风蚀模态**的强化元素共鸣（`07 §1.3`）：**优先弹射未被本次技能攻击过的敌方目标**。
            /// 它的前置是"在场敌人 ≥ 3 人"（数据里用 `condition` 表达）；这里实现的是**优先规则本身** ——
            /// 还有没打过的就从没打过的里随机抽，全都打过了才退回全体随机（否则第 5 段会抽不出目标）。
            case "randomUnhit":
            {
                List<BattleUnit> all = foePool;
                if (all.Count == 0) break;

                // 段级条件不满足时退回朴素随机（例：风蚀强化共鸣的"优先"只在敌人 ≥ 3 人时启用）。
                if (!Conditions.Evaluate(state, hit.Condition, mainTarget ?? all[0], mainTarget))
                {
                    result.Add(state.Rng.Pick(all));
                    break;
                }

                List<BattleUnit> fresh = [.. all.Where(u => !hitThisSkill.Contains(u))];
                List<BattleUnit> pool = fresh.Count > 0 ? fresh : all;
                result.Add(state.Rng.Pick(pool));
                break;
            }

            default:
                state.Note($"未登记的命中目标形态 '{hit.Target}'（01 §1.1 只定义 single/blast/aoe/bounce）。");
                break;
        }
        return result;
    }

    // ── 协同攻击（01 §5.5） ───────────────────────────────────────────

    /// <summary>
    /// **协同攻击的执行端**：让被选中的我方角色**无视一切条件**发动一次元素共鸣。
    /// </summary>
    /// <remarks>
    /// 规则（`01 §5.5` · `100` #97）：
    /// <list type="bullet">
    ///   <item>**不消耗任何资源** —— 包括共鸣指数、能量、资源型堆叠；</item>
    ///   <item>**无视发动前提** —— 守约人风蚀模态强化共鸣的"场上风蚀 ≥ 9 层"这类门槛也被跳过；</item>
    ///   <item>**若该角色的元素共鸣处于强化状态，就放强化版**。</item>
    /// </list>
    /// ⚠️ 它**算被选中者的一次行动结果**（会计入 `onAttack` / `onResonanceCast` 钩子），
    /// **但不占用被选中者的行动** —— 消耗的是**共生灵的额外回合**。
    /// </remarks>
    public static void CoopResonance(BattleState state, BattleUnit unit, int depth)
    {
        // 强化形态优先：能不能放强化版，取决于它此刻是否"处在强化状态"（对守约人风蚀模态而言
        // 就是 `cost.requires` 那一串条件；因为这里要"无视条件"，所以我们直接看技能表里有没有强化位，
        // 但用 `Evaluate` 判断它是否真的处于强化态 —— 这是"强化状态"的定义，不是"发动前提"）。
        string key = "resonance";
        if (unit.Skills.ContainsKey("enhancedResonance"))
        {
            SkillDef enhanced = unit.Skills["enhancedResonance"];
            bool enhancedActive = string.IsNullOrEmpty(enhanced.Cost.Requires)
                                  || Conditions.Evaluate(state, enhanced.Cost.Requires, unit, null);
            if (enhancedActive) key = "enhancedResonance";
        }

        if (!unit.Skills.TryGetValue(key, out SkillDef? skill))
        {
            state.Say($"· {unit.Name} 没有可用的元素共鸣，协同攻击跳过", 3);
            return;
        }

        BattleUnit? target = state.AliveEnemies.Count > 0 ? state.AliveEnemies[0] : null;
        state.Say($"· {unit.Name} 因协同攻击发动【{skill.Name}】（**不消耗资源、无视条件**）", 3);
        ExecuteSkill(state, unit, key, skill, target, depth, ignoreCost: true);
    }

    // ── 召唤物（01 §1.5） ─────────────────────────────────────────────

    /// <summary>
    /// 生成一个召唤物。
    /// </summary>
    /// <remarks>
    /// 不占 4 人上场位、有独立 AV 但**从不产生指令**、**没有防御力**（故 `R = 0`）、
    /// 免疫一切直接秒杀、**不计入"我方 4 人全部无法行动"**。
    /// </remarks>
    public static void SpawnSummon(BattleState state, StatusDef def, BattleUnit summoner, EffectDef e, int depth)
    {
        // 全场上限 3 个；同一个技能同时只保留 1 个（再次生成顶替旧的）。
        List<BattleUnit> existing = [.. state.Summons.Where(s => s.IsAlive && s.DefId == def.Id)];
        foreach (BattleUnit old in existing) DestroySummon(state, old, expired: false);

        while (state.Summons.Count(static s => s.IsAlive) >= BattleConstants.SummonFieldCap)
        {
            BattleUnit? oldest = state.Summons.Where(static s => s.IsAlive).OrderBy(static s => s.Serial).FirstOrDefault();
            if (oldest is null) break;
            DestroySummon(state, oldest, expired: false);
        }

        double hpPct = e.HpPctOfCaster > 0 ? e.HpPctOfCaster : 0.5;
        double hp = string.Equals(e.ScalingBase, "max", StringComparison.Ordinal)
            ? summoner.MaxHp * hpPct
            : summoner.Hp * hpPct;
        double atk = e.SummonAtkPctOfCaster > 0
            ? summoner.Stats.AtkValue * e.SummonAtkPctOfCaster
            : 0.0;

        BattleUnit summon = new()
        {
            Serial = state.NextSerial++,
            DefId = def.Id,
            Name = def.Name,
            Kind = UnitKind.Summon,
            Battle = state,
            Disposition = Disposition.Friendly,
            Level = summoner.Level,
            PanelIsFinal = true,
            Base = new BaseStatsDef
            {
                Hp = hp,
                Atk = atk,
                Def = 0,
                Speed = summoner.Base.Speed,
                Threat = summoner.Base.Threat,
                CritRate = e.SummonInheritCrit ? summoner.Stats.CritRateValue : BattleConstants.CritRateBase,
                CritDmg = e.SummonInheritCrit ? summoner.Stats.CritDmgValue : BattleConstants.CritDmgBase,
            },
            Elements = summoner.Elements,
            SlotIndex = summoner.SlotIndex,
            Summoner = summoner,
            SummonHpMax = hp,
            SummonKind = def.SummonKind,
            Skills = new Dictionary<string, SkillDef>(def.Skills, StringComparer.Ordinal),
        };
        summon.ThreatBonus = e.ThreatBonus;
        summon.Hp = hp;
        summon.Av = BattleConstants.AvBase / Math.Max(1.0, summon.Stats.SpeedValue);
        summon.LastSpeed = summon.Stats.SpeedValue;
        state.Summons.Add(summon);
        state.Units.Add(summon);

        if (e.SummonGuardian)
        {
            summoner.GuardianSummon = summon;
            summoner.GuardianMultiplier = 3.0;
            state.Say($"· {summoner.Name} 进入【守护】状态：受到的伤害将由【{summon.Name}】承担", 3);
        }

        // 持续时间按生成者的行动次数计时（01 §1.5）。
        StatusInstance timer = new()
        {
            Def = def,
            Owner = summoner,
            Source = summoner,
            TickSource = summoner,
            Remaining = e.Duration > 0 ? e.Duration : def.Duration,
            SourceLevel = summoner.Level,
        };
        summoner.Statuses.Add(timer);

        state.Say($"◆ {summoner.Name} 生成召唤物【{def.Name}】（生命 {hp:0.#}，{timer.Remaining:0.#} 回合）", 2);
    }

    public static void DestroySummon(BattleState state, BattleUnit summon, bool expired)
    {
        if (!state.Summons.Remove(summon)) return;
        state.Units.Remove(summon);
        summon.Hp = 0.0;
        BattleUnit? owner = summon.Summoner;
        if (owner is not null && ReferenceEquals(owner.GuardianSummon, summon))
        {
            owner.GuardianSummon = null;
        }
        state.Say($"▸ 召唤物【{summon.Name}】{(expired ? "持续时间结束" : "被摧毁")}", 3);
        HookBus.Fire(state, expired ? "onSummonExpired" : "onSummonDestroyed",
            new HookContext { Actor = summon.Summoner, Target = summon });
        HookBus.Fire(state, "onDeath", new HookContext { Actor = summon });
    }

    // ── 领域 / 胜负 ───────────────────────────────────────────────────

    private static void TickFields(BattleState state)
    {
        for (int i = state.Fields.Count - 1; i >= 0; i--)
        {
            FieldInstance f = state.Fields[i];
            f.Remaining -= 1.0;
            if (f.Remaining <= 0.0)
            {
                state.Say($"▸ 领域【{f.Def.Name}】结束", 2);
                state.Fields.RemoveAt(i);
            }
        }
    }

    private static bool CheckOutcome(BattleState state)
    {
        if (state.IsOver) return true;
        if (state.AliveEnemies.Count == 0)
        {
            state.Outcome = BattleOutcome.AllyWin;
            return true;
        }
        // 我方 4 人全部无法行动 → 失败。**召唤物不计入**（01 §1.5）。
        if (state.PartyWiped)
        {
            state.Outcome = BattleOutcome.AllyLose;
            return true;
        }
        return false;
    }
}
