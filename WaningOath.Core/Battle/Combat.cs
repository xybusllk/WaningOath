using WaningOath.Core.Data;

namespace WaningOath.Core.Battle;

/// <summary>
/// 伤害 / 治疗 / 护盾的**落地**（管线只管算，本类负责改状态）。
/// </summary>
/// <remarks>
/// <b>顺序纪律</b>（`01 §9` 的回合执行顺序 3c→3e）：
/// <list type="number">
///   <item>算管线 → 扣盾 → 扣血 → 记录 → 结算"受击回能"。</item>
///   <item>**异常积蓄**（`§3.1`，按"每次命中"）。</item>
///   <item>最后才是**触发类效果**（`§9 3e`：天赋、追加攻击、乘胜追击、复伤回放）。</item>
/// </list>
/// 把 2 与 3 调换会让"每次命中追加 X"类天赋在异常触发之前结算，从而与文档的时序表对不上。
/// </remarks>
public static class Combat
{
    /// <summary>递归护栏。附加伤害会触发 `onHit`，而 `onHit` 又可能产生附加伤害（`01 §2.4` 明确警告过这个风险）。</summary>
    private const int MaxHookDepth = 8;

    /// <summary>本动作内已经产生过多少伤害实例（防止"附加伤害无限递归"这类数据错误把进程挂死）。</summary>
    private const int MaxInstancesPerWindow = 512;

    public static int CurrentWindowDepth { get; set; }
    public static int InstancesThisWindow { get; set; }

    public static void BeginWindow()
    {
        CurrentWindowDepth = 0;
        InstancesThisWindow = 0;
    }

    /// <summary>走完整管线并落地一次伤害。</summary>
    public static DamageResult DealDamage(BattleState state, DamageRequest req, int depth = 0)
    {
        DamageResult result = DamagePipeline.Compute(state, req);
        ApplyResolved(state, req, result, depth);
        return result;
    }

    /// <summary>落地一次**已经算好**的伤害（复伤回放走这条路：基数本身已是减伤后的数字）。</summary>
    public static void ApplyPrecomputed(
        BattleState state,
        BattleUnit source,
        BattleUnit target,
        Element element,
        double amount,
        bool crit,
        DamageCategory category,
        string label,
        int depth = 0)
    {
        DamageRequest req = new()
        {
            Source = source,
            Target = target,
            Element = element,
            Multiplier = 0,
            Category = category,
            Label = label,
            AccumulateAnomaly = false,
            ApplyLevelPenalty = false,
        };
        DamageResult result = new()
        {
            Amount = amount,
            Crit = crit,
            PanelBase = amount,
            AfterDefense = amount,
        };
        ApplyResolved(state, req, result, depth);
    }

    private static void ApplyResolved(BattleState state, DamageRequest req, DamageResult result, int depth)
    {
        BattleUnit src = req.Source;
        BattleUnit tgt = req.Target;

        if (!tgt.IsAlive) return;
        if (result.Amount <= 0.0 && result.Amount > -0.001) return;

        if (++InstancesThisWindow > MaxInstancesPerWindow)
        {
            state.Note($"[护栏] 单个窗口内的伤害实例超过 {MaxInstancesPerWindow} 次，已中止后续结算。" +
                       "最可能的原因是一条 'onHit → 附加伤害 → onHit' 的递归链没有声明 `ignoreAdditionalDamage`（01 §2.4）。");
            return;
        }

        double amount = Math.Max(0.0, result.Amount);
        double absorbed = 0.0;

        if (tgt.Shield > 0.0)
        {
            absorbed = Math.Min(tgt.Shield, amount);
            tgt.Shield -= absorbed;
            amount -= absorbed;
            // 「只吸收一次伤害实例、剩余不结转」的护盾（05 §3.2 的 shieldOneHit）。
            bool oneHit = tgt.Statuses.Exists(static s => s.Def.ShieldOneHit);
            if (oneHit)
            {
                tgt.Shield = 0.0;
                tgt.ShieldConsumed = true;
            }
        }

        // 守护召唤物（薇拉「余烬天龙」）：召唤者受到的伤害改由它承担，且召唤者不会因此倒下。
        // 它排在护盾之后：先吃召唤者自己的护盾，再把剩余伤害按倍率转给守护者。
        if (amount > 0.0 && tgt.Kind == UnitKind.Character
            && tgt.GuardianSummon is { IsAlive: true } guardian
            && !ReferenceEquals(guardian, tgt)
            && !ReferenceEquals(src, guardian))
        {
            double redirected = amount * Math.Max(1.0, tgt.GuardianMultiplier);
            double gBefore = guardian.Hp;
            guardian.Hp = Math.Max(0.0, guardian.Hp - redirected);
            state.Say($"· 【守护】{guardian.Name} 代替 {tgt.Name} 承受 {redirected:0.#} 伤害" +
                      $"（{gBefore:0.#} → {guardian.Hp:0.#}）", 3);
            tgt.Hp = Math.Max(1.0, tgt.Hp);
            HookBus.Fire(state, "onDamageTaken", new HookContext { Actor = tgt, Target = src, Damage = result }, depth + 1);
            if (guardian.Hp <= 0.0) Runner.DestroySummon(state, guardian, expired: false);
            return;
        }

        TryLethalGuard(state, tgt, ref amount);

        double hpBefore = tgt.Hp;
        tgt.Hp -= amount;
        if (tgt.Hp <= 0.0) tgt.Hp = 0.0;
        double hpRemoved = hpBefore - tgt.Hp;

        // 台账（M2）。**记的是"真正扣掉的血"** —— 溢出与护盾吸收各自单列，
        // 否则最后一击的斩杀溢出会按目标血量上限虚增 DPR。
        state.Metrics.RecordDamage(src, tgt, req.Element, req.Category, result.Amount, absorbed, hpRemoved,
            critEligible: req.CanCrit && !req.ForceNoCrit);
        if (result.Crit) state.Metrics.RecordCrit(src, tgt);

        DamageRecord rec = new()
        {
            Target = tgt,
            Source = src,
            Element = req.Element,
            Amount = result.Amount,
            WasCrit = result.Crit,
            SourceLevel = src.Level,
            Category = req.Category,
        };
        tgt.LastDamageTaken = rec;

        // 复伤**无法复制**持续伤害 / 附加伤害 / 属性异常伤害 / 另一份复伤（01 §3.2.6）。
        if (req.Category == DamageCategory.Direct) tgt.LastCopyableDamage = rec;

        // 冻结期间承受的伤害之和 —— 破冰的基数（01 §3.2.2）。**包括属性异常伤害**。
        if (tgt.Anomalies.TryGetValue(AnomalyFamily.Ice, out AnomalyState? ice) && ice.Frozen)
        {
            ice.AccumulatedDamage += amount;
        }

        // 记录"最近被任意我方目标造成伤害的敌方单位"（01 §6.4）。
        if (tgt.IsEnemy && src.Disposition == Disposition.Friendly) state.MostRecentlyDamagedEnemy = tgt;

        // 受击统一回复 5 点能量（01 §4.2）。**敌方一般无能量条，加了也无副作用。**
        GainEnergy(tgt, BattleConstants.EnergyOnHit);

        string shieldNote = absorbed > 0 ? $"（护盾吸收 {absorbed:0.#}）" : "";
        string critNote = result.Crit ? " 暴击" : "";
        state.Say($"{src.Name} → {tgt.Name}  {amount:0.#} {ElementInfo.ToChinese(req.Element)}伤害{critNote}{shieldNote}" +
                  $"  [{tgt.Hp:0}/{tgt.MaxHp:0}]" + (req.Label.Length > 0 ? $"  ← {req.Label}" : ""), 2);

        if (tgt.IsAlive)
        {
            // ② 异常积蓄（按"每次命中"，01 §2.5）。
            if (req.AccumulateAnomaly && !tgt.IsSummon)
            {
                AnomalySystem.Accumulate(state, src, tgt, req.Element, depth, req.SuppressCombustStackDamage);
            }

            // ③ 触发类效果。
            HookBus.Fire(state, "onHit", new HookContext { Actor = src, Target = tgt, Damage = result, Skill = req.Skill }, depth + 1);
            if (result.Crit)
            {
                HookBus.Fire(state, "onCrit", new HookContext { Actor = src, Target = tgt, Damage = result, Skill = req.Skill }, depth + 1);
            }
            HookBus.Fire(state, "onDamageTaken", new HookContext { Actor = tgt, Target = src, Damage = result }, depth + 1);
        }
        else
        {
            state.Say($"{tgt.Name} 已被击倒。", 3);
            if (tgt.IsEnemy) state.Metrics.EnemyDeaths++;
            else if (tgt.Kind == UnitKind.Character) state.Metrics.AllyDeaths++;
            HookBus.Fire(state, "onKill", new HookContext { Actor = src, Target = tgt, Skill = req.Skill }, depth + 1);
            HookBus.Fire(state, "onDeath", new HookContext { Actor = tgt, Target = src }, depth + 1);
        }
    }

    public static void Heal(BattleState state, BattleUnit source, BattleUnit target, double amount, string label)
    {
        if (!target.IsAlive || amount <= 0.0) return;
        double final = amount * (1.0 + source.Stats.HealBonusValue);
        double before = target.Hp;
        // 治疗不可超过生命上限（01 §2.2 关键裁定）。
        target.Hp = Math.Min(target.MaxHp, target.Hp + final);
        state.Metrics.RecordHeal(final, target.Hp - before);
        state.Say($"{source.Name} → {target.Name}  回复 {target.Hp - before:0.#} 生命  [{target.Hp:0}/{target.MaxHp:0}]" +
                  (label.Length > 0 ? $"  ← {label}" : ""), 2);
    }

    public static void AddShield(BattleState state, BattleUnit source, BattleUnit target, double amount, string label)
    {
        if (!target.IsAlive) return;
        double final = amount * (1.0 + source.Stats.ShieldBonusValue);
        // 护盾不可超过无拐面板生命上限（01 §2.2 关键裁定）。
        double cap = target.Base.Hp;
        target.Shield = Math.Min(cap, target.Shield + final);
        state.Say($"{source.Name} → {target.Name}  获得护盾 {final:0.#}（当前 {target.Shield:0.#}）" +
                  (label.Length > 0 ? $"  ← {label}" : ""), 2);
    }

    /// <summary>
    /// 致命伤害保护（罗兰达「黑林特制急救注射剂」）：本次伤害会使目标倒下时，
    /// 取消这次伤害并按守护状态恢复生命；每个单位每场只触发一次。
    /// </summary>
    private static void TryLethalGuard(BattleState state, BattleUnit target, ref double amount)
    {
        if (amount <= 0.0 || target.LethalGuardUsed) return;
        if (target.Hp - amount > 0.0) return;

        StatusDef? guard = target.Statuses
            .Where(static s => s.Def.LethalGuardHealPct > 0.0
                               && (s.Remaining > 0.0 || s.Def.Duration <= 0.0))
            .OrderByDescending(static s => s.Def.LethalGuardHealPct)
            .Select(static s => s.Def)
            .FirstOrDefault();
        if (guard is null) return;

        target.LethalGuardUsed = true;
        target.Hp = Math.Max(1.0, target.MaxHp * guard.LethalGuardHealPct);
        amount = 0.0;
        state.Say($"◆ 【{guard.Name}】{target.Name} 免疫本次致命伤害，恢复至 {target.Hp:0}/{target.MaxHp:0}", 2);

        if (!string.IsNullOrEmpty(guard.LethalGuardBuffId))
        {
            EffectResolver.Run(state,
            [
                new EffectDef
                {
                    Type = "applyBuff",
                    BuffId = guard.LethalGuardBuffId,
                    Stacks = 1,
                    Duration = 0,
                    Target = "partyAll",
                },
            ], new EffectContext { Actor = target, Target = target }, 0);
        }
    }

    /// <summary>回能。有效上限 = 能量上限 + 溢出储存；超出部分**直接丢弃**（`01 §4.2`）。</summary>
    public static void GainEnergy(BattleUnit unit, double amount)
    {
        if (amount <= 0.0) return;
        double max = unit.Stats.EnergyMaxValue + unit.EnergyOverflowCap;
        if (max <= 0.0) return; // 无能量系统的角色（如群星誓约）直接忽略。
        unit.Energy = Math.Min(max, unit.Energy + amount);
    }

    /// <summary>
    /// 「相邻」= **主目标自己那一边**的位置序号 ±1（`01 §1.1`）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 旧实现写死 `state.Enemies` ⇒ **敌方单位用「相邻」会静默失效**
    /// （`IndexOf` 在敌方列表里找不到我方单位，返回 −1 ⇒ `null`）。
    /// 现在按**主目标所在阵营**取列表，玩家侧行为不变（主目标是敌方时取的就是 `Enemies`）。
    /// </remarks>
    public static BattleUnit? SelectAdjacent(BattleState state, BattleUnit main, int offsetIndex)
    {
        // ⚠️ **用未过滤存活的那份列表**：`01 §1.1` 的"相邻 = 位置序号 ±1"指的是**站位**，
        //    所以中间那个位置死了就是"没人"，而不是"顺延到下一个活着的"。
        //    （旧实现也是这个语义 —— 它在 `state.Enemies` 上取 ±1 再判存活。）
        List<BattleUnit> list = state.AllOnSide(main.Disposition);
        int idx = list.IndexOf(main);
        if (idx < 0) return null;
        int i = idx + offsetIndex;
        if (i < 0 || i >= list.Count) return null;
        BattleUnit u = list[i];
        return u.IsAlive ? u : null;
    }
}
