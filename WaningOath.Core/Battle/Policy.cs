using WaningOath.Core.Data;
using WaningOath.Core.Progression;

namespace WaningOath.Core.Battle;

/// <summary>一次行动的指令。**这就是"动作序列"的元素** —— 服务端的确定性重放校验提交的就是它（裁决 #59）。</summary>
public sealed record ActionChoice(string SkillKey, int? TargetSerial);

/// <summary>
/// 我方行动的策略。
/// </summary>
/// <remarks>
/// `M1` 的 `Sim` 没有界面，所以需要一个**确定性**的自动策略把战斗跑完。
/// 它**不是游戏内容**（真实客户端由玩家选择），但必须是确定性的，否则"同一种子重放一致"无法验收。
/// </remarks>
public interface IActionPolicy
{
    ActionChoice? Choose(BattleState state, BattleUnit actor);
}

/// <summary>
/// 默认策略：**终结技 &gt; 元素共鸣 &gt; 普攻**（与 `01 §7` 给敌方 AI 的默认优先级同款）。
/// </summary>
/// <remarks>
/// 选敌规则：优先打**当前生命最低**的敌人（同值比 <see cref="BattleUnit.Serial"/>），
/// 这样日志里的目标选择稳定且可解释。策略本身**不消耗 RNG**（确定性）。
/// </remarks>
public sealed class GreedyPolicy : IActionPolicy
{
    /// <summary>是否允许共鸣指数透支（`01 §4.1`：点数为 0 时强行释放，扣 25% 生命上限，**可致死**）。</summary>
    public bool AllowOverdraw { get; init; }

    public ActionChoice? Choose(BattleState state, BattleUnit actor)
    {
        // 强制行动（例：卡尔「可处决」）优先于任何策略；它仍然是一条普通 ActionChoice，
        // 因此客户端提交、服务端重放都走同一条路径，不会出现"策略分支导致日志分叉"。
        if (BattleRules.ForcedChoice(state, actor) is { } forced) return forced;

        List<BattleUnit> enemies = state.AliveEnemies;
        if (enemies.Count == 0) return null;

        BattleUnit target = enemies
            .OrderBy(static u => u.Hp)
            .ThenBy(static u => u.Serial)
            .First();

        string? key = PickSkillKey(state, actor);
        return key is null ? null : new ActionChoice(key, target.Serial);
    }

    private string? PickSkillKey(BattleState state, BattleUnit actor)
    {
        if (actor.Skills.Count == 0) return null;

        // 强化形态优先（例：持有「风涌」时元素共鸣被强化为「风，过亦留痕」）。
        if (actor.Skills.TryGetValue("enhancedResonance", out SkillDef? enhanced) && CanPay(state, actor, enhanced))
            return "enhancedResonance";

        if (actor.Skills.TryGetValue("enhancedUltimate", out SkillDef? enhancedUlt) && CanPay(state, actor, enhancedUlt))
            return "enhancedUltimate";

        if (actor.Skills.TryGetValue("ultimate", out SkillDef? ult) && CanPay(state, actor, ult))
            return "ultimate";

        if (actor.Skills.TryGetValue("enhancedBasic", out SkillDef? enhancedBasic) && CanPay(state, actor, enhancedBasic))
            return "enhancedBasic";

        if (actor.Skills.TryGetValue("resonance", out SkillDef? res) && CanPay(state, actor, res))
            return "resonance";

        if (actor.Skills.ContainsKey("basic")) return "basic";
        return actor.Skills.Keys.OrderBy(static k => k, StringComparer.Ordinal).First();
    }

    private bool CanPay(BattleState state, BattleUnit actor, SkillDef skill)
        => BattleRules.CanUse(state, actor, skill, AllowOverdraw, out _);
}

/// <summary>
/// 手动战斗：由界面按 <see cref="BattleUnit.Serial"/> 预先放入本拍每个待行动我方单位的指令。
/// </summary>
/// <remarks>
/// 返回 <c>null</c> 表示"这个单位本拍没有指令"，<see cref="Runner"/> 会记一行"无可执行的行动"并跳过它。
/// 因此界面必须在 <see cref="Runner.Step"/> 之前，为 <see cref="BattleRules.PredictReadyAllies"/>
/// 返回的**每一个**单位放入指令；漏一个就会白丢一回合。
/// </remarks>
public sealed class ManualPolicy : IActionPolicy
{
    public Dictionary<int, ActionChoice> Choices { get; } = [];

    /// <summary>本场实际提交过的指令，按顺序；服务端重放要的就是它。</summary>
    public List<ActionChoice> Used { get; } = [];

    public ActionChoice? Choose(BattleState state, BattleUnit actor)
    {
        // 强制行动：丢弃玩家为这一拍预填的指令，改由规则生成一条（并记入 Used，
        // 服务端重放拿到的就是这条，不会与客户端分叉）。
        if (BattleRules.ForcedChoice(state, actor) is { } forced)
        {
            Choices.Remove(actor.Serial);
            Used.Add(forced);
            return forced;
        }

        if (!Choices.TryGetValue(actor.Serial, out ActionChoice? choice)) return null;
        Choices.Remove(actor.Serial);
        Used.Add(choice);
        return choice;
    }
}

/// <summary>
/// 手动战斗与界面共用的**合法行动判定**。
/// </summary>
/// <remarks>
/// <b>唯一真源</b>：这里的判定必须与 <see cref="Runner.ExecuteSkill"/> 里的实际扣费一致，
/// 否则界面会显示"可点"、点下去却被 Core 按另一套规则拒绝（或反过来）。
/// <see cref="GreedyPolicy"/> 也复用 <see cref="CanUse"/>，保证自动托管与手操不会分叉。
/// </remarks>
public static class BattleRules
{
    /// <summary>这个技能现在能不能放；<paramref name="reason"/> 给界面直接显示。</summary>
    public static bool CanUse(BattleState state, BattleUnit actor, SkillDef skill, bool allowOverdraw, out string reason)
    {
        reason = "";
        if (skill.Cost.Energy > 0)
        {
            double max = actor.Stats.EnergyMaxValue + actor.EnergyOverflowCap;
            if (max <= 0 || actor.Energy < skill.Cost.Energy)
            {
                reason = $"能量不足（{actor.Energy:0}/{skill.Cost.Energy:0}）";
                return false;
            }
        }

        if (skill.Cost.ResonanceIndex > 0
            && state.ResonanceIndex < skill.Cost.ResonanceIndex
            && !allowOverdraw)
        {
            reason = $"共鸣指数不足（{state.ResonanceIndex}/{skill.Cost.ResonanceIndex}）";
            return false;
        }

        if (!string.IsNullOrEmpty(skill.Cost.Stack)
            && actor.StackOf(skill.Cost.Stack) < skill.Cost.StackAmount)
        {
            reason = $"「{skill.Cost.Stack}」不足（{actor.StackOf(skill.Cost.Stack)}/{skill.Cost.StackAmount}）";
            return false;
        }

        if (!Conditions.Evaluate(state, skill.Cost.Requires, actor, null))
        {
            reason = "发动条件未满足";
            return false;
        }

        if (skill.Cost.OncePerRound)
        {
            string? key = actor.Skills.FirstOrDefault(kv => ReferenceEquals(kv.Value, skill)).Key;
            if (key is not null && actor.OncePerRoundUsed(key) == state.Round)
            {
                reason = "本自然回合已经用过了";
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 当前是否有「强制行动」状态；有则返回这一拍要执行的动作。
    /// </summary>
    /// <remarks>
    /// 首个使用者：卡尔尼菲琪亚「打开保险」——敌人进入可处决状态后，下一次行动被替换为「处决」。
    /// 它返回的是一条普通 <see cref="ActionChoice"/>，因此手动策略、自动策略与服务端重放
    /// 走的是同一条路径；手动策略会丢弃玩家预填的指令并记录这一条。
    /// </remarks>
    public static ActionChoice? ForcedChoice(BattleState state, BattleUnit actor)
    {
        foreach (StatusInstance st in actor.Statuses)
        {
            string? key = st.Def.ForcedSkill;
            if (string.IsNullOrEmpty(key)) continue;
            if (!actor.Skills.TryGetValue(key, out SkillDef? skill)) continue;
            if (!CanUse(state, actor, skill, allowOverdraw: true, out _)) continue;

            BattleUnit? target;
            if (string.Equals(st.Def.ForcedTarget, "self", StringComparison.Ordinal)
                || skill.Hits.Count == 0
                || string.Equals(skill.TargetType, "self", StringComparison.Ordinal))
            {
                target = actor;
            }
            else
            {
                target = state.AliveEnemies
                    .OrderBy(static u => u.Hp)
                    .ThenBy(static u => u.Serial)
                    .FirstOrDefault();
            }
            return new ActionChoice(key, target?.Serial);
        }
        return null;
    }

    /// <summary>
    /// 预测下一拍会行动的我方单位（不修改状态）。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="Runner.Step"/> 的"ΔAV = 全场最小 AV，减完后 AV ≤ 0 者行动"完全同口径；
    /// 排序也照抄（我方优先 → 站位 → Serial）。界面拿它决定"要收集几条指令"。
    /// </remarks>
    public static List<BattleUnit> PredictReadyAllies(BattleState state)
    {
        List<BattleUnit> alive = [.. state.Units.Where(static u => u.IsAlive)];
        if (alive.Count == 0) return [];
        double delta = alive.Min(static u => u.Av);
        return
        [
            .. alive
                .Where(u => u.Av - delta <= 1e-9)
                .Where(static u => !u.IsEnemy)
                .OrderBy(static u => u.Disposition == Disposition.Friendly ? 0 : 1)
                .ThenBy(static u => u.SlotIndex)
                .ThenBy(static u => u.Serial)
        ];
    }

    /// <summary>这个技能需不需要玩家点敌方目标；返回合法目标列表（空 = 不需要选）。</summary>
    public static List<BattleUnit> LegalTargets(BattleState state, BattleUnit actor, SkillDef skill)
    {
        bool support = skill.Hits.Count == 0
                       || string.Equals(skill.TargetType, "self", StringComparison.Ordinal);
        if (support)
            return [.. state.Allies.Where(static u => u.IsAlive)];

        bool aoe = string.Equals(skill.TargetType, "aoe", StringComparison.Ordinal)
                   || skill.Hits.Exists(static h => string.Equals(h.Target, "all", StringComparison.Ordinal));
        if (aoe)
            return state.AliveEnemies.Count > 0 ? [state.AliveEnemies[0]] : [];

        return state.AliveEnemies;
    }

    /// <summary>给界面用：这个技能是不是治疗/增益（决定目标选我方还是敌方）。</summary>
    public static bool IsSupport(SkillDef skill)
        => skill.Hits.Count == 0 || string.Equals(skill.TargetType, "self", StringComparison.Ordinal);
}

/// <summary>
/// 把另一个策略包起来、**顺便记下它每一步选了什么**。
/// </summary>
/// <remarks>
/// 这是"服务器重放校验"（`06 §2.2` 的 `POST /api/game/battle/verify`）的客户端那一半：
/// 客户端跑一场、把 <see cref="Choices"/> 一起提交；服务器用 <see cref="ScriptedPolicy"/> 重跑一遍，
/// 逐行比对日志。**只要日志一致，就说明客户端的技能结算与伤害管线与服务端完全同构** ——
/// 这是 `06 §8.1` 的"客户端跑、服务器重放"边界的可执行形式。
/// </remarks>
public sealed class RecordingPolicy(IActionPolicy inner) : IActionPolicy
{
    public List<ActionChoice> Choices { get; } = [];

    public ActionChoice? Choose(BattleState state, BattleUnit actor)
    {
        ActionChoice? choice = inner.Choose(state, actor);
        if (choice is not null) Choices.Add(choice);
        return choice;
    }
}

/// <summary>按给定的动作序列重放。序列用完后返回 `null`（战斗就此结束）。</summary>
public sealed class ScriptedPolicy(IEnumerable<ActionChoice> choices) : IActionPolicy
{
    private readonly List<ActionChoice> _choices = [.. choices];
    private int _index;

    public int Consumed => _index;
    public int Remaining => _choices.Count - _index;

    public ActionChoice? Choose(BattleState state, BattleUnit actor)
        => _index < _choices.Count ? _choices[_index++] : null;
}

/// <summary>建一场战斗所需的输入。</summary>
public sealed class BattleConfig
{
    public required GameData Data { get; init; }
    public required StageDef Stage { get; init; }
    public ulong Seed { get; init; } = 20260815UL;

    /// <summary>敌人血量的**调试**倍率。用于把一场杂兵战拉长到能观察异常 / 紊乱（不是游戏内容）。</summary>
    public double EnemyHpMultiplier { get; init; } = 1.0;

    /// <summary>固定技能等级；`null` → 用关卡里的 <see cref="StageDef.SkillLevel"/>。</summary>
    public int? SkillLevelOverride { get; init; }

    /// <summary>是否允许我方策略透支共鸣指数。</summary>
    public bool AllowOverdraw { get; init; }

    /// <summary>
    /// **投入档位**（`02 §8.1.1` · `data/tables/tiers.json`）。
    /// </summary>
    /// <remarks>
    /// `null` = 不套档位，走关卡自己的面板口径（教学关的固定预设面板 / 角色数据里的基础面板）。
    /// 一旦填上，我方全员的面板会被 <see cref="Progression.TierResolver"/> **整体替换**为
    /// 该档位的目标面板 —— 这正是 `M2` 分档实测的手段：**同一个关卡、同一份技能数据，只换面板**。
    /// </remarks>
    public InvestmentTierDef? Tier { get; init; }

    /// <summary>
    /// **每个上场角色的实际配装**（键 = 角色 id）。
    /// </summary>
    /// <remarks>
    /// 这是 `M7` 之后唯一正确的面板来源：存档里只有"几级 / 哪把武器 / 哪几件装备"，
    /// 面板由 <see cref="Progression.PanelCalculator"/> 现算。
    /// <para>
    /// 优先级：<see cref="Loadouts"/> &gt; <see cref="Tier"/> &gt; 关卡自己的面板口径。
    /// 三者都不填 = 走 <c>def.Base</c> × 等级成长（`M1` 的老路径）。
    /// </para>
    /// </remarks>
    public IReadOnlyDictionary<string, Progression.Loadout>? Loadouts { get; init; }

    /// <summary>每个上场角色的权阶（键 = 角色 id，值 0~6）。缺省 = 0 权。</summary>
    public IReadOnlyDictionary<string, int>? EidolonRanks { get; init; }
}

public static class BattleFactory
{
    public static BattleState Create(BattleConfig cfg)
    {
        GameData data = cfg.Data;
        StageDef stage = cfg.Stage;
        int skillLevel = cfg.SkillLevelOverride ?? stage.SkillLevel;

        // 运行时合成的状态（武器/套装）缓存挂在 GameData 上；**每场战斗开头清空**，
        // 否则同一进程里第二场会复用上一场的 StatusDef，造成“同一场手动/自动日志不同”的隐蔽分叉。
        data.Generated.Clear();

        BattleState state = new()
        {
            Data = data,
            Rng = new Rng(cfg.Seed),
            Stage = stage,
            MaxRound = stage.MaxRound,
        };

        // 我方：按关卡写明的站位顺序（一号位在最前，01 §1.1）。
        for (int i = 0; i < stage.Party.Count; i++)
        {
            CharacterDef def = data.Character(stage.Party[i]);
            stage.PartyModes.TryGetValue(def.Id, out string? modeId);
            int eidolon = 0;
            if (!stage.FixedPanel) cfg.EidolonRanks?.TryGetValue(def.Id, out eidolon);
            BattleUnit unit = BuildCharacter(state, def, modeId, slot: i, level: stage.PresetLevel,
                skillLevel: skillLevel, eidolonRank: Math.Clamp(eidolon, 0, 6));

            // 面板来源的优先级（见 BattleConfig 的说明）：真配装 > 投入档位 > 关卡口径。
            if (cfg.Loadouts is not null && cfg.Loadouts.TryGetValue(def.Id, out Progression.Loadout? lo))
            {
                unit.OverridePanel(Progression.PanelCalculator.Compute(data, def, lo).Panel);

                // 面板之外的**装备与武器效果**（`M7`）：套装词条 + 武器五段特效 + 武器常驻。
                // 它们不走面板，而是走 `01 §1.3` 的状态机制 —— 因为"持续 2 回合 / 释放终结技后"
                // 这类时机与时长，只有状态承载得了。
                EquipEffects.Apply(state, unit, def, lo);
            }
            else if (cfg.Tier is not null)
            {
                TierRowDef? row = data.Tiers.Row(AnchorRowOf(def));
                TierPanel? panel = TierResolver.Resolve(cfg.Tier, row, def);
                if (panel is null)
                {
                    throw new InvalidOperationException(
                        $"角色 '{def.Id}' 的锚点行 '{AnchorRowOf(def)}' 不在 data/tables/tiers.json 的 rows 里；" +
                        "无法按投入档位折算面板。");
                }
                unit.OverridePanel(TierResolver.ToBaseStats(panel, def));
            }

            if (stage.InitialEnergyFull)
            {
                unit.Energy = unit.Stats.EnergyMaxValue + unit.EnergyOverflowCap;
            }
            state.Allies.Add(unit);
            state.Units.Add(unit);
        }

        // 敌方。
        for (int i = 0; i < stage.Enemies.Count; i++)
        {
            EnemyDef def = data.Enemy(stage.Enemies[i]);
            BattleUnit unit = BuildEnemy(state, def, slot: i, hpMultiplier: cfg.EnemyHpMultiplier);
            state.Enemies.Add(unit);
            state.Units.Add(unit);
        }

        SyncSpeeds(state, force: true);
        return state;
    }

    /// <summary>
    /// 速度变化时的行动值重算：`AV = 原AV × 原速度 / 新速度`（`01 §1.2`）。
    /// </summary>
    /// <remarks>
    /// 这条规则**不能**只在"buff 施加时"做一次。倒计时归零、状态到期、权阶生效都会改速度，
    /// 而这在任何地方都可能发生 —— 所以做成一个每次循环都跑的幂等同步，而不是散落各处的回调。
    /// </remarks>
    public static void SyncSpeeds(BattleState state, bool force = false)
    {
        foreach (BattleUnit u in state.Units)
        {
            double speed = Math.Max(1.0, u.Stats.SpeedValue);
            if (force)
            {
                u.LastSpeed = speed;
                if (u.Av <= 0.0) u.Av = BattleConstants.AvBase / speed;
                continue;
            }
            if (Math.Abs(speed - u.LastSpeed) < 1e-9) continue;
            if (u.Av > 0.0) u.Av = u.Av * u.LastSpeed / speed;
            u.LastSpeed = speed;
        }
    }

    private static BattleUnit BuildCharacter(BattleState state, CharacterDef def, string? modeId, int slot, int level,
        int skillLevel, int eidolonRank)
    {
        // 升格模态（01 §5.4）：在这里**展开成实际技能表**。单模态角色走 def.Skills，等同于"没有模态"。
        Dictionary<string, SkillDef> skills = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, SkillDef> kv in def.ResolveSkills(modeId))
            skills[kv.Key] = kv.Value.CloneForBattle();

        BattleUnit unit = new()
        {
            Serial = state.NextSerial++,
            DefId = def.Id,
            Name = def.Name,
            Kind = UnitKind.Character,
            Battle = state,
            Disposition = Disposition.Friendly,
            Level = level,
            Rarity = def.RarityEnum,
            PanelIsFinal = false,
            Base = def.Base,
            Elements = def.ResolveElements(),
            SlotIndex = slot,
            Skills = skills,
            ModeId = modeId,
            EnergyOverflowCap = def.Base.EnergyOverflowCap,
            DefaultSkillLevel = skillLevel,
            FinalDamageVsCategory = new Dictionary<string, double>(def.FinalDamageVsCategory, StringComparer.Ordinal),
            FinalDamageVsShielded = def.FinalDamageVsShielded,
        };

        // 天赋与权阶的触发钩子摊平成一张表。**遍历顺序固定**（按技能位名排序）。
        // ⚠️ 取的是**展开后**的技能表 —— 所以两个模态各自的 `talent` 天然只会挂上当前那一个。
        foreach (KeyValuePair<string, SkillDef> kv in skills.OrderBy(static k => k.Key, StringComparer.Ordinal))
        {
            foreach (TriggerDef t in kv.Value.Triggers) unit.Triggers.Add(t);
        }

        if (eidolonRank > 0) ApplyEidolons(state, unit, def, eidolonRank);

        unit.Hp = unit.MaxHp;
        return unit;
    }

    /// <summary>
    /// 把权阶 Ⅰ~Ⅵ 的声明转成战斗内的初始状态。
    /// </summary>
    /// <remarks>
    /// 目前只覆盖**静态、无需时机**的部分：技能等级、基础面板、初始层数、权阶触发器，
    /// 以及把暂时没有落点的旋钮收进 <see cref="BattleUnit.EidolonKnobs"/>。
    /// 依赖“释放终结技时 / 受治疗时 / 领域展开时”的权阶效果仍需要后续钩子，不能假装已经生效。
    /// </remarks>
    private static void ApplyEidolons(BattleState state, BattleUnit unit, CharacterDef def, int rank)
    {
        foreach (EidolonDef eidolon in def.Eidolons.Where(e => e.Rank <= rank).OrderBy(static e => e.Rank))
        {
            foreach (TriggerDef trigger in eidolon.Triggers) unit.Triggers.Add(trigger);
            foreach (EffectDef effect in eidolon.Effects) ApplyEidolonEffect(state, unit, effect);
        }
    }

    private static void ApplyEidolonEffect(BattleState state, BattleUnit unit, EffectDef effect)
    {
        switch (effect.Type)
        {
            case "modifyStat":
            {
                string stat = effect.Stat ?? "";
                double amount = Math.Abs(effect.Delta) > 1e-9 ? effect.Delta : effect.Value;
                bool mul = string.Equals(effect.Mode, "mul", StringComparison.OrdinalIgnoreCase);
                switch (stat)
                {
                    case "skillLevel":
                    {
                        int add = (int)Math.Round(amount);
                        int cap = effect.Cap > 0 ? effect.Cap : int.MaxValue;
                        unit.DefaultSkillLevel = Math.Min(cap, unit.DefaultSkillLevel + add);
                        return;
                    }
                    case "atk":
                    case "hp":
                    case "def":
                    case "speed":
                    case "critRate":
                    case "critDmg":
                    case "energyMax":
                    case "energyOverflowCap":
                    case "threat":
                    {
                        BaseStatsDef b = new()
                        {
                            Atk = unit.Base.Atk, Hp = unit.Base.Hp, Def = unit.Base.Def,
                            Speed = unit.Base.Speed, CritRate = unit.Base.CritRate,
                            CritDmg = unit.Base.CritDmg, EnergyMax = unit.Base.EnergyMax,
                            EnergyOverflowCap = unit.Base.EnergyOverflowCap, Threat = unit.Base.Threat,
                        };
                        double Apply(double cur) => mul ? cur * amount : cur + amount;
                        switch (stat)
                        {
                            case "atk": b.Atk = Apply(b.Atk); break;
                            case "hp": b.Hp = Apply(b.Hp); break;
                            case "def": b.Def = Apply(b.Def); break;
                            case "speed": b.Speed = Apply(b.Speed); break;
                            case "critRate": b.CritRate = Apply(b.CritRate); break;
                            case "critDmg": b.CritDmg = Apply(b.CritDmg); break;
                            case "energyMax": b.EnergyMax = Apply(b.EnergyMax); break;
                            case "energyOverflowCap": b.EnergyOverflowCap = Apply(b.EnergyOverflowCap); break;
                            case "threat": b.Threat = Apply(b.Threat); break;
                        }
                        unit.Base = b;
                        return;
                    }
                    default:
                        // 未登记为面板键的旋钮：先存起来，让后续机制能读；不宣称已生效。
                        unit.EidolonKnobs[stat] = amount;
                        return;
                }
            }
            case "gainStack":
            {
                if (string.IsNullOrEmpty(effect.Stack)) return;
                int current = unit.StackOf(effect.Stack);
                int cap = effect.Cap > 0 ? effect.Cap : int.MaxValue;
                unit.Stacks[effect.Stack] = Math.Min(cap, current + Math.Max(0, effect.Amount is 0 ? 1 : (int)effect.Amount));
                return;
            }
            case "gainResonance":
                state.ResonanceIndex += (int)effect.Amount;
                return;
            case "changeEnergyCost":
                if (unit.Skills.TryGetValue("ultimate", out SkillDef? ultimate))
                    ultimate.Cost.Energy = effect.SetTo > 0 ? effect.SetTo : Math.Max(0, ultimate.Cost.Energy + effect.Delta);
                return;
            case "changeResonanceCost":
                if (unit.Skills.TryGetValue("resonance", out SkillDef? resonance))
                    resonance.Cost.ResonanceIndex = Math.Max(0, resonance.Cost.ResonanceIndex + (int)effect.Delta);
                return;
            case "changeResonanceCap":
                state.ResonanceCapBonus += (int)effect.Delta;
                return;
            default:
                // applyBuff / extraTurn / changeEnergyCost 等需要战斗时机；先登记为旋钮。
                if (!string.IsNullOrEmpty(effect.Type))
                {
                    unit.EidolonKnobs[effect.Type] =
                        unit.EidolonKnobs.GetValueOrDefault(effect.Type) + effect.Value;
                }
                return;
        }
    }

    private static BattleUnit BuildEnemy(BattleState state, EnemyDef def, int slot, double hpMultiplier)
    {
        Dictionary<string, SkillDef> skills = new(StringComparer.Ordinal);
        foreach (SkillDef s in def.Skills)
        {
            string key = s.Kind.Length > 0 ? s.Kind : s.Id;
            skills[key] = s;
        }

        BaseStatsDef stats = new()
        {
            Atk = def.Base.Atk,
            Hp = def.Base.Hp * hpMultiplier,
            Def = def.Base.Def,
            Speed = def.Base.Speed,
            CritRate = def.Base.CritRate,
            CritDmg = def.Base.CritDmg,
            EnergyMax = def.Base.EnergyMax,
            Threat = def.Base.Threat,
        };

        BattleUnit unit = new()
        {
            Serial = state.NextSerial++,
            DefId = def.Id,
            Name = def.Name,
            Kind = UnitKind.Enemy,
            Battle = state,
            Disposition = Disposition.Hostile,
            IsEliteOrAbove = def.IsEliteOrAbove,
            Category = def.Category,
            Level = def.Level,
            PanelIsFinal = true,
            Base = stats,
            Elements = [Element.Physical],
            SlotIndex = slot,
            Skills = skills,
        };

        if (def.ResistAll != 0)
        {
            for (int i = 0; i < ElementInfo.Count; i++) unit.BaseResist[i] = def.ResistAll;
        }
        foreach (KeyValuePair<string, double> kv in def.Resist)
        {
            unit.BaseResist[(int)ElementInfo.Parse(kv.Key)] = kv.Value;
        }
        foreach (KeyValuePair<string, double> kv in def.AnomalyResist)
        {
            unit.AnomalyResist[ParseFamily(kv.Key)] = kv.Value;
        }

        unit.Hp = unit.MaxHp;

        // 天赋 / 被动钩子摊平成一张表（与 `BuildCharacter` 同一处理）。
        // ⚠️ 这一段是**本轮补的**：旧实现只给角色收集 triggers ⇒ **敌人的技能上写了 triggers 也不会生效**，
        //    而「铅灰傀儡」的「代偿」（受到打击后原样回敬）正挂在 `onDamageTaken` 上。
        //    遍历顺序固定（按技能位名排序），保证确定性。
        foreach (KeyValuePair<string, SkillDef> kv in skills.OrderBy(static k => k.Key, StringComparer.Ordinal))
        {
            foreach (TriggerDef t in kv.Value.Triggers) unit.Triggers.Add(t);
        }

        return unit;
    }

    /// <summary>
    /// 取一个角色的**面板锚点行**键（`02 §1.2` 的一行）。
    /// </summary>
    /// <remarks>
    /// 优先用角色数据里显式写明的 `anchorRow`；没写才按 `path` 猜一个默认。
    /// **猜只是兜底** —— 定位（path）与面板锚点是两条轴（爱丽丝 `path=dps` 但锚点是「生命主C」），
    /// 所以校验器会把"没写 `anchorRow`"报成警告，逼数据把它写清楚。
    /// </remarks>
    public static string AnchorRowOf(CharacterDef def)
    {
        if (!string.IsNullOrWhiteSpace(def.AnchorRow)) return def.AnchorRow.Trim();
        return def.Path.Trim().ToLowerInvariant() switch
        {
            "dps" => "dps",
            "sub_dps" or "subdps" => "subDps",
            "support" => "support",
            "survival" or "healer" => "survival",
            _ => "dps",
        };
    }

    private static AnomalyFamily ParseFamily(string name) => name.Trim().ToLowerInvariant() switch    {
        "physical" => AnomalyFamily.Physical,
        "ice" => AnomalyFamily.Ice,
        "fire" => AnomalyFamily.Fire,
        "lightning" => AnomalyFamily.Lightning,
        "wind" => AnomalyFamily.Wind,
        "resonance" => AnomalyFamily.Resonance,
        _ => throw new ArgumentException($"未登记的异常家族名：'{name}'。", nameof(name)),
    };
}
