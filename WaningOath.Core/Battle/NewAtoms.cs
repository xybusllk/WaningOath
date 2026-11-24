using WaningOath.Core.Data;

namespace WaningOath.Core.Battle;

/// <summary>
/// 新角色（薇拉 / 卡尔）带进来的通用原子。
/// </summary>
/// <remarks>
/// 这两个原子都**不写死角色 id**：参数全部来自数据，任何"资源满层结算"或"逐个处决"
/// 的角色都可以复用。首个使用者分别是薇拉的「狩猎印记」与卡尔的「绝罚」。
/// </remarks>
public static class NewAtoms
{
    /// <summary>
    /// `stackDetonate`：消耗一个资源的所有层数，每层打出固定段数的随机目标伤害；
    /// 达到上限层数时追加一段全体伤害。
    /// </summary>
    /// <remarks>
    /// 数据字段：`stack`（资源名）/ `multiplier`（每段倍率）/ `hits`（每层段数）/
    /// `bonusAtStacks`（达到几层触发追加）/ `bonusMultiplier`（追加倍率）/
    /// `bonusTarget`（追加目标，默认 allEnemies）/ `spendAll`（是否清空）。
    /// </remarks>
    public static void StackDetonate(BattleState state, EffectDef e, EffectContext ctx, int depth)
    {
        if (string.IsNullOrEmpty(e.Stack)) return;
        int stacks = ctx.Actor.StackOf(e.Stack);
        if (stacks <= 0)
        {
            state.Say($"· {ctx.Actor.Name} 没有「{e.Stack}」，本次结算无效果", 3);
            return;
        }

        int cap = e.BonusAtStacks > 0 ? e.BonusAtStacks : stacks;
        int used = Math.Min(stacks, cap);
        int hits = Math.Max(1, e.Hits);
        state.Say($"· {ctx.Actor.Name} 结算「{e.Stack}」{used} 层 × {hits} 段", 3);

        for (int i = 0; i < used; i++)
        {
            for (int h = 0; h < hits; h++)
            {
                List<BattleUnit> enemies = state.AliveEnemies;
                if (enemies.Count == 0) return;
                BattleUnit target = state.Rng.Pick(enemies);
                Combat.DealDamage(state, new DamageRequest
                {
                    Source = ctx.Actor,
                    Target = target,
                    Element = Element.Physical,
                    Multiplier = e.Multiplier,
                    ScalingBase = e.ScalingBase ?? "atk",
                    CanCrit = true,
                    Category = DamageCategory.Direct,
                    AccumulateAnomaly = false,
                    Label = $"{e.Stack}·{i + 1}",
                }, depth + 1);
            }
        }

        if (e.BonusAtStacks > 0 && stacks >= e.BonusAtStacks && e.BonusMultiplier > 0)
        {
            string targetSpec = string.IsNullOrEmpty(e.BonusTarget) ? "allEnemies" : e.BonusTarget;
            foreach (BattleUnit target in EffectResolver.Targets(state, targetSpec, ctx, beneficial: false))
            {
                Combat.DealDamage(state, new DamageRequest
                {
                    Source = ctx.Actor,
                    Target = target,
                    Element = Element.Physical,
                    Multiplier = e.BonusMultiplier,
                    ScalingBase = e.ScalingBase ?? "atk",
                    CanCrit = true,
                    Category = DamageCategory.Direct,
                    AccumulateAnomaly = false,
                    Label = $"{e.Stack}·满层追加",
                }, depth + 1);
            }
        }

        if (e.SpendAll)
        {
            ctx.Actor.Stacks[e.Stack] = 0;
            state.Say($"· {ctx.Actor.Name} 的「{e.Stack}」清空", 3);
        }
    }

    /// <summary>
    /// `recastSelf`：满足条件时**立即重新发动一次当前技能**（薇拉强化普攻的 5 层追发）。
    /// </summary>
    /// <remarks>
    /// 每个单位回合、每个技能最多重发一次；重发走 `ignoreCost:true`，不再扣资源 / 不再检查前置，
    /// 但会正常再触发 `onHit` / `onSkillCast` —— 由 `RecastsThisTurn` 防住第二次递归。
    /// </remarks>
    public static void RecastSelf(BattleState state, EffectContext ctx, int depth)
    {
        if (ctx.Skill is null) return;
        BattleUnit actor = ctx.Actor;
        if (!actor.RecastsThisTurn.Add(ctx.Skill.Id))
        {
            state.Say($"· 【{ctx.Skill.Name}】本回合已经重发过，不再重复", 3);
            return;
        }

        string? key = actor.Skills.FirstOrDefault(kv => ReferenceEquals(kv.Value, ctx.Skill)).Key
                      ?? ctx.Skill.Id;
        BattleUnit? target = ctx.Target;
        if (target is null || !target.IsAlive)
            target = state.AliveEnemies.FirstOrDefault();

        state.Say($"· {actor.Name} 满足【{ctx.Skill.Name}】的追加条件，立即重新发动一次", 2);
        Runner.ExecuteSkill(state, actor, key, ctx.Skill, target, depth + 1, ignoreCost: true);
    }

    /// <summary>
    /// `executionSweep`：对每个满足「印记层数」或「生命比例」的敌人执行一次指定技能；
    /// 终结技内使用时屏蔽「击杀额外回合」，避免连续处决无限连动。
    /// </summary>
    /// <remarks>
    /// 数据字段：`skillKey`（要执行的技能键，例：`bloom`）/ `sweepStatus` + `sweepStatusAtLeast` /
    /// `sweepHpBelow` / 两个对应的权阶旋钮 / `sweepAllTargetsKnob` /
    /// `suppressExtraTurns` / `onEach` / `onAny`。
    /// </remarks>
    public static void ExecutionSweep(BattleState state, EffectDef e, EffectContext ctx, int depth)
    {
        if (string.IsNullOrEmpty(e.SkillKey)) return;
        if (!ctx.Actor.Skills.TryGetValue(e.SkillKey, out SkillDef? skill))
        {
            state.Note($"executionSweep 引用了不存在的技能键 '{e.SkillKey}'");
            return;
        }

        int need = e.SweepStatusAtLeast;
        if (!string.IsNullOrEmpty(e.SweepStatusAtLeastKnob))
            need = (int)ctx.Actor.EidolonKnobs.GetValueOrDefault(e.SweepStatusAtLeastKnob);

        double hpBelow = e.SweepHpBelow;
        if (!string.IsNullOrEmpty(e.SweepHpBelowKnob))
            hpBelow = ctx.Actor.EidolonKnobs.GetValueOrDefault(e.SweepHpBelowKnob);

        bool all = !string.IsNullOrEmpty(e.SweepAllTargetsKnob)
                   && ctx.Actor.EidolonKnobs.GetValueOrDefault(e.SweepAllTargetsKnob) > 0.0;

        List<BattleUnit> targets = [.. state.AliveEnemies];
        int count = 0;
        bool oldSuppress = state.SuppressExtraTurns;
        if (e.SuppressExtraTurns) state.SuppressExtraTurns = true;

        try
        {
            foreach (BattleUnit target in targets)
            {
                if (!target.IsAlive) continue;
                bool qualifies = all
                    || (!string.IsNullOrEmpty(e.SweepStatus) && need > 0 && target.StatusStacks(e.SweepStatus) >= need)
                    || (hpBelow > 0.0 && target.HpRatio <= hpBelow);
                if (!qualifies) continue;

                state.Say($"◆ 【处决判定】{target.Name}（裁决 {target.StatusStacks(e.SweepStatus ?? "")} · " +
                          $"生命 {target.HpRatio:P0}）", 3);
                Runner.ExecuteSkill(state, ctx.Actor, e.SkillKey, skill, target, depth + 1, ignoreCost: true);
                count++;

                if (e.OnEach is { Count: > 0 })
                {
                    EffectResolver.Run(state, e.OnEach,
                        new EffectContext { Actor = ctx.Actor, Target = target }, depth + 1);
                }
            }
        }
        finally
        {
            state.SuppressExtraTurns = oldSuppress;
        }

        state.Say($"· 【绝罚】本次共处决判定 {count} 次", 2);
        if (count > 0 && e.OnAny is { Count: > 0 })
        {
            EffectResolver.Run(state, e.OnAny,
                new EffectContext { Actor = ctx.Actor, Target = ctx.Target }, depth + 1);
        }
    }
}
