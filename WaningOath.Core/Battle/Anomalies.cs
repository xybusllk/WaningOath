namespace WaningOath.Core.Battle;

/// <summary>紊乱窗口中的一条记录。</summary>
public readonly record struct AnomalyWindowEntry(AnomalyFamily Family, double Damage, BattleUnit Source);

/// <summary>
/// 属性异常积蓄与结算。依据 `01 §3`。
/// </summary>
/// <remarks>
/// <b>三种计时单位在这里同时存在</b>，这是本系统最容易写错的地方：
/// <list type="bullet">
///   <item>脆弱 / 冻结：**被施加者的单位回合**。</item>
///   <item>可燃 / 风蚀 / 复伤：**自然回合**（100 AV 窗口）。</item>
///   <item>引雷：在**目标的下个回合开始**时结算（不是回合结束）。</item>
/// </list>
/// </remarks>
public static class AnomalySystem
{
    public static void OpenWindow(BattleState state) => state.AnomalyWindow.Clear();

    // ── 积蓄（01 §3.1） ────────────────────────────────────────────────

    /// <summary>每次**命中**的积蓄。调用点必须是"每个伤害实例"，不是"每个技能"（`01 §2.5`）。</summary>
    public static void Accumulate(BattleState state, BattleUnit src, BattleUnit tgt, Element element, int depth,
        bool suppressCombustStackDamage = false)
    {
        if (!tgt.IsAlive) return;

        AnomalyFamily family = ElementInfo.FamilyOf(element);
        double resist = ResistOf(tgt, element, family);
        double buildUp = src.Stats.BuildUpFor(element);

        // 抗性 > 10% 且攻击者无任何积蓄效率加成 → 完全无法积蓄（01 §3.1）。
        if (resist > BattleConstants.AnomalyResistBlock && buildUp <= 0.0)
        {
            state.Say($"（{tgt.Name} 的{ElementInfo.ToChinese(element)}抗性 {resist:P0} 过高，无法积蓄）", 3);
            return;
        }

        double gain = BattleConstants.AnomalyGainPerHit * (1.0 - resist) * (1.0 + buildUp);
        if (gain <= 0.0) return;

        double bar = tgt.AnomalyBars.GetValueOrDefault(element) + gain;
        state.Say($"{tgt.Name} 的{ElementInfo.ToChinese(element)}异常积蓄 +{gain:0.#} → {Math.Min(bar, BattleConstants.AnomalyBarMax):0.#}/{BattleConstants.AnomalyBarMax:0}", 3);

        // 进度条满 → 触发。触发后**该属性进度条清零**重新累积（01 §3.1）。
        while (bar >= BattleConstants.AnomalyBarMax)
        {
            bar = 0.0;
            Trigger(state, src, tgt, family, depth, suppressCombustStackDamage);
        }
        tgt.AnomalyBars[element] = bar;
    }

    /// <summary>技能**直接施加**异常层数，不经过进度条（`05 §3.2` 的 `applyAnomalyStack`）。</summary>
    public static void ApplyDirectStacks(BattleState state, BattleUnit src, BattleUnit tgt, Element element, int stacks, int depth,
        bool suppressCombustStackDamage = false)
    {
        AnomalyFamily family = ElementInfo.FamilyOf(element);
        for (int i = 0; i < stacks; i++) Trigger(state, src, tgt, family, depth, suppressCombustStackDamage);
    }

    private static double ResistOf(BattleUnit tgt, Element element, AnomalyFamily family)
    {
        double r = tgt.ResistFor(element);
        if (tgt.AnomalyResist.TryGetValue(family, out double extra)) r += extra;
        return Math.Clamp(r, 0.0, 1.0);
    }

    public static AnomalyState Get(BattleUnit unit, AnomalyFamily family)
    {
        if (!unit.Anomalies.TryGetValue(family, out AnomalyState? a))
        {
            a = new AnomalyState { Family = family };
            unit.Anomalies[family] = a;
        }
        return a;
    }

    // ── 触发（01 §3.2） ────────────────────────────────────────────────

    /// <summary>进度条满（或技能直接施加）时施加 1 层，并按其家族规则立即结算。</summary>
    public static void Trigger(BattleState state, BattleUnit src, BattleUnit tgt, AnomalyFamily family, int depth,
        bool suppressCombustStackDamage = false)
    {
        AnomalyState a = Get(tgt, family);
        a.Source = src;
        // 01 §3.2.5 · 100 #92：记下"谁施加过这条异常"，供"基数取队内生命上限最高者"使用。
        if (!a.Appliers.Contains(src)) a.Appliers.Add(src);
        state.Metrics.AnomalyTriggers++;
        state.Metrics.AnomalyByFamily[family] = state.Metrics.AnomalyByFamily.GetValueOrDefault(family) + 1;

        double applierAtk = src.Stats.AtkValue;

        // 触发时"这次异常自己造成的伤害"。无伤害的异常按 施加者攻击力 × 100% 计入紊乱基数（01 §3.3）。
        double ownDamage = applierAtk * BattleConstants.DisorderNoDamageBase;

        switch (family)
        {
            case AnomalyFamily.Physical:
            {
                // 脆弱：每层 10% 全属性易伤，上限 10 层，持续 3 回合，叠加时重置。
                if (a.Cower)
                {
                    state.Say($"{tgt.Name} 已处于畏缩，脆弱不再累积。", 3);
                    break;
                }
                a.Stacks = Math.Min(BattleConstants.FragileStackCap, a.Stacks + 1);
                a.Remaining = BattleConstants.FragileDurationTurns;
                state.Say($"▸ {tgt.Name} 获得【脆弱】{a.Stacks}/{BattleConstants.FragileStackCap} 层", 3);

                if (a.Stacks >= BattleConstants.FragileStackCap)
                {
                    // 畏缩：打断当前动作 + 攻击力 −20%，并把脆弱延长至 8 回合。
                    // ⚠️ **畏缩触发时脆弱层数清零**（01 §3.2.1），需重新累积。
                    a.Cower = true;
                    a.Stacks = 0;
                    a.Remaining = BattleConstants.CowerDurationTurns;
                    state.Say($"▸▸ {tgt.Name} 进入【畏缩】：打断当前动作、攻击力 −20%，脆弱延长至 8 回合，层数清零", 3);
                    HookBus.Fire(state, "onCower", new HookContext { Actor = src, Target = tgt }, depth + 1);
                }
                break;
            }

            case AnomalyFamily.Ice:
            {
                // 冻结：限制 1 回合行动，本身无伤害；解除时结算破冰。
                a.Frozen = true;
                a.Remaining = BattleConstants.FreezeDurationTurns;
                a.AccumulatedDamage = 0.0;
                state.Say($"▸ {tgt.Name} 被【冻结】1 回合（解除时结算破冰）", 3);
                break;
            }

            case AnomalyFamily.Fire:
            {
                // 可燃：每层 5% 火属性易伤，上限 20 层；**叠加时立即引爆现有层数**。
                int pre = a.Stacks;
                a.Stacks = Math.Min(BattleConstants.BurnStackCap, pre + 1);
                a.Remaining = BattleConstants.BurnDurationRounds;
                state.Say($"▸ {tgt.Name} 获得【可燃】{a.Stacks}/{BattleConstants.BurnStackCap} 层", 3);
                if (pre > 0 && !suppressCombustStackDamage)
                {
                    // 现有层数 × 最新施加者攻击力，按最新施加者的暴击率/暴击伤害判定暴击。
                    DamageResult burst = Combat.DealDamage(state, new DamageRequest
                    {
                        Source = src,
                        Target = tgt,
                        Element = Element.Fire,
                        Multiplier = pre,
                        Category = DamageCategory.Anomaly,
                        Label = $"可燃引爆（{pre} 层）",
                        AccumulateAnomaly = false,
                    }, depth + 1);
                    ownDamage = burst.Amount;
                }
                break;
            }

            case AnomalyFamily.Lightning:
            {
                // 引雷：标记，在目标**下个回合开始**时结算伤害 + 晕眩。
                a.PendingDelivery = true;
                a.Remaining = 1.0;
                state.Say($"▸ {tgt.Name} 被标记【引雷】（下个回合开始时结算）", 3);
                break;
            }

            case AnomalyFamily.Wind:
            {
                // 风蚀：每层每自然回合造成施加者面板生命 10% 的风属性持续伤害，上限 20 层。
                int windCap = AliceAtoms.CapFor(a, BattleConstants.WindErosionStackCap);
                a.Stacks = Math.Min(windCap, a.Stacks + 1);
                a.Remaining = BattleConstants.WindErosionDurationRounds;
                string windName = a.CapOverride > 0 ? "风化" : "风蚀";
                state.Say($"▸ {tgt.Name} 获得【{windName}】{a.Stacks}/{windCap} 层", 3);
                break;
            }

            case AnomalyFamily.Resonance:
            {
                // 复伤：2 回合后回放"最近一次可复制的伤害"。
                a.PendingReplay = tgt.LastCopyableDamage;
                a.Remaining = BattleConstants.ReplayDelayRounds;
                state.Say($"▸ {tgt.Name} 获得【复伤】（2 回合后回放最近一次伤害）", 3);
                break;
            }
        }

        RecordInWindow(state, tgt, family, ownDamage, src);
        TryDisorder(state, tgt, depth);
        HookBus.Fire(state, "onAnomalyTrigger", new HookContext { Actor = src, Target = tgt }, depth + 1);
    }

    private static void RecordInWindow(BattleState state, BattleUnit tgt, AnomalyFamily family, double damage, BattleUnit src)
    {
        if (!state.AnomalyWindow.TryGetValue(tgt, out List<AnomalyWindowEntry>? list))
        {
            list = [];
            state.AnomalyWindow[tgt] = list;
        }
        list.Add(new AnomalyWindowEntry(family, damage, src));
    }

    /// <summary>
    /// 元素紊乱（`01 §3.3`）：**多种异常效果同时触发**时，额外造成
    /// `这些异常效果本身造成的伤害之和 × 250%` 的**真实伤害**。**每自然回合仅 1 次。**
    /// </summary>
    private static void TryDisorder(BattleState state, BattleUnit tgt, int depth)
    {
        if (state.DisorderUsedThisRound) return;
        if (!state.AnomalyWindow.TryGetValue(tgt, out List<AnomalyWindowEntry>? list)) return;

        // 物理不参与紊乱；由物理派生的畏缩同样不参与。
        List<AnomalyWindowEntry> eligible = [.. list.Where(static e => e.Family != AnomalyFamily.Physical)];
        int distinct = eligible.Select(static e => e.Family).Distinct().Count();
        if (distinct < 2) return;

        double sum = eligible.Sum(static e => e.Damage);
        if (sum <= 0.0) return;

        BattleUnit source = eligible[0].Source;
        double amount = sum * BattleConstants.DisorderMult * state.EnvironmentFactor(Element.Physical, DamageCategory.Disorder);

        state.DisorderUsedThisRound = true;
        state.Metrics.DisorderTriggers++;
        state.Say($"◆◆ 元素紊乱！{tgt.Name} 受到真实伤害 {amount:0.#}（基数 {sum:0.#} × {BattleConstants.DisorderMult:0.00}）", 3);
        Combat.ApplyPrecomputed(state, source, tgt, Element.Physical, amount, false, DamageCategory.Disorder, "元素紊乱", depth + 1);
    }

    // ── 计时 ──────────────────────────────────────────────────────────

    /// <summary>
    /// **单位回合开始**：结算引雷、消耗冻结 / 晕眩。返回 `true` 表示本次行动被控制消耗掉。
    /// </summary>
    public static bool ResolveTurnStart(BattleState state, BattleUnit unit, int depth)
    {
        bool consumed = false;

        // 引雷：目标的下个回合开始时结算（01 §3.2.4）。
        AnomalyState lightning = Get(unit, AnomalyFamily.Lightning);
        if (lightning.PendingDelivery && lightning.Source is BattleUnit src && src.IsAlive)
        {
            lightning.PendingDelivery = false;
            OpenWindow(state);
            /* 引雷是延迟结算的 dot，**自成独立窗口**（01 §3.3 实现备忘）。 */
            Combat.DealDamage(state, new DamageRequest
            {
                Source = src,
                Target = unit,
                Element = Element.Lightning,
                Multiplier = BattleConstants.LightningDelayMult,
                Category = DamageCategory.Anomaly,
                Label = "引雷延迟结算",
                AccumulateAnomaly = false,
            }, depth + 1);

            if (unit.IsAlive)
            {
                // 「100% 基础概率」使目标晕眩。M1 无效果抵抗来源，故必中；
                // 高难本若要表达"控制免疫"，走 EnemyDef.AnomalyResist（01 §7）。
                lightning.Stunned = true;
                state.Say($"▸ {unit.Name} 陷入【晕眩】1 回合", 3);
            }
            OpenWindow(state);
        }

        // 冻结：AV 归零时不行动，改由冻结解除流程消耗该次行动（01 §1.2），并立即结算破冰。
        AnomalyState ice = Get(unit, AnomalyFamily.Ice);
        if (ice.Frozen)
        {
            consumed = true;
            ice.Frozen = false;
            ice.Remaining = 0.0;
            double baseDamage = ice.AccumulatedDamage;
            ice.AccumulatedDamage = 0.0;
            if (baseDamage > 0.0)
            {
                // 破冰：冻结期间承受的任何伤害之和 × 30%，**不可暴击**，**不视为任何单位造成的伤害**。
                double burst = baseDamage * BattleConstants.BurstIceRatio
                               * state.EnvironmentFactor(Element.Ice, DamageCategory.Anomaly);
                state.Say($"▸ {unit.Name} 冻结解除 → 破冰结算（基数 {baseDamage:0.#} × {BattleConstants.BurstIceRatio:0.00}）", 3);
                Combat.ApplyPrecomputed(state, ice.Source ?? unit, unit, Element.Ice, burst, false,
                    DamageCategory.Anomaly, "破冰", depth + 1);
            }
            else
            {
                state.Say($"▸ {unit.Name} 冻结解除（期间未承受伤害，破冰为 0）", 3);
            }
        }

        if (lightning.Stunned)
        {
            consumed = true;
            lightning.Stunned = false;
            lightning.Remaining = 0.0;
            state.Say($"▸ {unit.Name} 处于晕眩，本次行动被消耗", 3);
        }

        return consumed;
    }

    /// <summary>**单位回合结束**：递减所有"以该单位为计时源、按单位回合计时"的 buff，以及脆弱 / 畏缩的持续时间。</summary>
    public static void TickTurnEnd(BattleState state, BattleUnit unit)
    {
        // 自身增益：计时源 == 该单位。光环也在这里递减（它的计时源是施加者，见 01 §1.3）。
        for (int i = unit.Statuses.Count - 1; i >= 0; i--)
        {
            StatusInstance st = unit.Statuses[i];
            if (!string.Equals(st.Def.DurationUnit, "turn", StringComparison.Ordinal)) continue;
            if (st.Def.Duration <= 0.0) continue; // 永久状态（如光环）不递减。
            if (!ReferenceEquals(st.EffectiveTickSource, unit)) continue;
            st.Remaining -= 1.0;
            if (st.Remaining <= 0.0) ExpireStatus(state, unit, st, expired: true);
        }

        if (unit.Anomalies.TryGetValue(AnomalyFamily.Physical, out AnomalyState? frag))
        {
            if (frag.Cower)
            {
                frag.Remaining -= 1.0;
                if (frag.Remaining <= 0.0)
                {
                    frag.Cower = false;
                    state.Say($"▸ {unit.Name} 的【畏缩】结束", 3);
                }
            }
            else if (frag.Stacks > 0)
            {
                frag.Remaining -= 1.0;
                if (frag.Remaining <= 0.0)
                {
                    state.Say($"▸ {unit.Name} 的【脆弱】层数消失（{frag.Stacks} 层）", 3);
                    frag.Stacks = 0;
                }
            }
        }
    }

    /// <summary>**自然回合结束**：风蚀 dot 结算，可燃 / 风蚀 / 复伤按自然回合计时递减。</summary>
    public static void TickRoundEnd(BattleState state, int depth)
    {
        foreach (BattleUnit unit in state.Units)
        {
            if (!unit.IsAlive) continue;

            // 风蚀：每层造成施加者面板生命 10% 的风属性持续伤害。每自然回合结算。**一般不可暴击**。
            if (unit.Anomalies.TryGetValue(AnomalyFamily.Wind, out AnomalyState? wind)
                && wind.Stacks > 0)
            {
                // 01 §3.2.5 · 100 #92：基数取"队内施加过风蚀且生命上限最高"的那一位，**不是**最新施加者。
                BattleUnit wsrc = AliceAtoms.BaseSourceFor(unit, AnomalyFamily.Wind) ?? unit;
                OpenWindow(state); // 延迟结算的 dot 自成独立窗口。
                Combat.DealDamage(state, new DamageRequest
                {
                    Source = wsrc,
                    Target = unit,
                    Element = Element.Wind,
                    ScalingBase = "hp",
                    // 每层 = 施加者**面板生命值**的 10%（01 §3.2.5）。倍率是"每层 0.10"，不是"每层 0.001"。
                    Multiplier = wind.Stacks * BattleConstants.WindErosionHpPctPerStack,
                    CanCrit = false,
                    ForceNoCrit = true,
                    Category = DamageCategory.Anomaly,
                    Label = $"风蚀持续伤害（{wind.Stacks} 层）",
                    AccumulateAnomaly = false,
                }, depth + 1);
                OpenWindow(state);
            }

            if (unit.Anomalies.TryGetValue(AnomalyFamily.Fire, out AnomalyState? fire) && fire.Stacks > 0)
            {
                fire.Remaining -= 1.0;
                if (fire.Remaining <= 0.0)
                {
                    state.Say($"▸ {unit.Name} 的【可燃】层数消失（{fire.Stacks} 层）", 3);
                    fire.Stacks = 0;
                }
            }

            if (unit.Anomalies.TryGetValue(AnomalyFamily.Wind, out AnomalyState? wind2) && wind2.Stacks > 0)
            {
                wind2.Remaining -= 1.0;
                if (wind2.Remaining <= 0.0)
                {
                    state.Say($"▸ {unit.Name} 的【风蚀】层数消失（{wind2.Stacks} 层）", 3);
                    wind2.Stacks = 0;
                }
            }

            if (unit.Anomalies.TryGetValue(AnomalyFamily.Resonance, out AnomalyState? replay)
                && replay.PendingReplay is not null)
            {
                replay.Remaining -= 1.0;
                if (replay.Remaining <= 0.0) SettleReplay(state, unit, replay, depth);
            }
        }
    }

    /// <summary>
    /// 复伤回放（`01 §3.2.6`）。**会复制减伤后的伤害**；暴击规则按原文的二分：
    /// 原伤害未暴击 → 二次必定不暴击；原伤害暴击 → 二次有 50% 基础概率以 **100% 原伤害的基础暴击伤害**暴击。
    /// </summary>
    private static void SettleReplay(BattleState state, BattleUnit unit, AnomalyState replay, int depth)
    {
        DamageRecord? rec = replay.PendingReplay;
        replay.PendingReplay = null;
        replay.Remaining = 0.0;
        if (rec is null) return;

        double amount = rec.Amount;
        bool crit = false;
        if (rec.WasCrit && state.Rng.Chance(BattleConstants.ReplayCritChance))
        {
            crit = true;
            amount *= 1.0 + BattleConstants.ReplayCritBonus;
        }

        amount *= state.EnvironmentFactor(Element.Resonance, DamageCategory.Replay);

        state.Say($"▸ {unit.Name} 的【复伤】回放最近一次伤害 {rec.Amount:0.#}" +
                  (crit ? $"（暴击 → {amount:0.#}）" : ""), 3);

        Combat.ApplyPrecomputed(state, rec.Source ?? unit, unit, Element.Resonance, amount, crit,
            DamageCategory.Replay, "复伤", depth + 1);
    }

    /// <summary>状态到期。`01 §1.5` 区分「被摧毁」与「持续时间结束」，两者都触发 `onDeath`。</summary>
    public static void ExpireStatus(BattleState state, BattleUnit owner, StatusInstance st, bool expired)
    {
        owner.Statuses.Remove(st);
        state.Say($"▸ {owner.Name} 的【{st.Def.Name}】{(expired ? "持续时间结束" : "被移除")}", 3);

        // 状态自然到期时的附加结算（首个使用者：薇拉「火枪手」3 回合后结算狩猎印记）。
        // 被驱散 / 净化走 expired:false，不触发。
        if (expired && st.Def.OnExpire.Count > 0)
        {
            EffectContext ctx = new()
            {
                Actor = st.Source ?? owner,
                Target = owner,
            };
            EffectResolver.Run(state, st.Def.OnExpire, ctx, 0);
        }
    }

    public static void RemoveAllStatuses(BattleState state, BattleUnit unit)
    {
        foreach (StatusInstance st in unit.Statuses.ToList()) ExpireStatus(state, unit, st, expired: true);
    }
}
