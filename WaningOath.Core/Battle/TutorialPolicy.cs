using WaningOath.Core.Data;

namespace WaningOath.Core.Battle;

/// <summary>
/// 教学战的**强制引导策略**（`04 §3.4` 的五步脚本）。
/// </summary>
/// <remarks>
/// 前四步是**强制**的：每一步都替玩家把该按的按了，并给出讲解文案；
/// 第五步交给 <see cref="GreedyPolicy"/> 自由收尾。
/// <para>
/// <b>为什么把它放在 Core 而不是客户端</b>：教学关是"会写进存档的首通"，
/// 而服务器要能重放它（`06 §2.3`）。放在 Core 里，`Sim`、客户端、服务端三方
/// 用的是**同一份**引导序列；放在客户端里，服务端的重放就会从第一步开始分叉。
/// </para>
/// <para>
/// ⚠️ 第 4 步要"自动强制充满守约人的能量"（`04 §3.4` 原文）。这不是作弊而是**教学设计**：
/// 按 `07 §1.2` 的能量获取速度，8,000 血的犬型异常活不到攒满 120 能量的那一刻 ——
/// 而本关的教学目标里明确包含"演示终结技"。
/// </para>
/// </remarks>
public sealed class TutorialPolicy(string teacherId, string partnerId) : IActionPolicy
{
    /// <summary>教学步（1~5）。第 5 步之后走自由策略。</summary>
    public int Step { get; private set; } = 1;

    /// <summary>当前这一步的讲解文案（界面直接显示）。</summary>
    public string Guidance => Step switch
    {
        1 => "第 1 步 · 用「守约人」的普攻：普攻 +1 共鸣指数，并回 10 点能量。",
        2 => "第 2 步 · 用「守约人」的元素共鸣「横刀问路」：它消耗 1 点共鸣指数。共鸣指数是全队共享的。",
        3 => "第 3 步 · 换「千语星遥」普攻：同一队里每个人各有一套技能。",
        4 => "第 4 步 · 能量满了就能放终结技 —— 这一次直接把守约人的能量充满。",
        _ => "收尾：接下来你自己决定。",
    };

    public ActionChoice? Choose(BattleState state, BattleUnit actor)
    {
        GreedyPolicy free = new();

        // 第 4 步的"充满能量"要在**轮到守约人之前**就把能量补上，
        // 否则策略会先给他挑一个普攻。
        if (Step == 4)
        {
            BattleUnit? teacher = state.Units.FirstOrDefault(u => u.DefId == teacherId && u.IsAlive);
            if (teacher is not null)
            {
                teacher.Energy = teacher.Stats.EnergyMaxValue + teacher.EnergyOverflowCap;
            }
        }

        switch (Step)
        {
            case 1:
                if (actor.DefId != teacherId) return Safe(state, actor);
                if (Skill(actor, "basic") is { } b1)
                {
                    Step = 2;
                    return new ActionChoice("basic", Target(state, actor)?.Serial);
                }
                Step = 2;
                return Safe(state, actor);

            case 2:
                if (actor.DefId != teacherId) return Safe(state, actor);
                if (Skill(actor, "resonance") is not null && actor.Energy >= 0)
                {
                    Step = 3;
                    return new ActionChoice("resonance", Target(state, actor)?.Serial);
                }
                Step = 3;
                return Safe(state, actor);

            case 3:
                if (actor.DefId != partnerId) return Safe(state, actor);
                if (Skill(actor, "basic") is not null)
                {
                    Step = 4;
                    return new ActionChoice("basic", Target(state, actor)?.Serial);
                }
                Step = 4;
                return Safe(state, actor);

            case 4:
                if (actor.DefId != teacherId) return Safe(state, actor);
                if (Skill(actor, "ultimate") is not null)
                {
                    Step = 5;
                    return new ActionChoice("ultimate", Target(state, actor)?.Serial);
                }
                Step = 5;
                return Safe(state, actor);

            default:
                return free.Choose(state, actor);
        }
    }

    private static SkillDef? Skill(BattleUnit actor, string key)
        => actor.Skills.TryGetValue(key, out SkillDef? s) ? s : null;

    /// <summary>
    /// 轮不到教学指定的那位时，**只让他普攻**。
    /// </summary>
    /// <remarks>
    /// ⚠️ 这一条不是洁癖，是教学关能不能走通的前提。第一版这里直接交给
    /// <see cref="GreedyPolicy"/>，而它的优先级是"终结技 &gt; 元素共鸣 &gt; 普攻"——
    /// 教学关带着 `initialEnergyFull`（`100` #63），于是**速度更快的千语星遥第一手就放终结技，
    /// 8,000 血的犬型异常当场没了**，四步引导一步都没走到。
    /// <para>
    /// 教学关的目标是"演示普攻、元素共鸣、共鸣指数、终结技"（`04 §2`）——顺序本身就是内容。
    /// </para>
    /// </remarks>
    private static ActionChoice? Safe(BattleState state, BattleUnit actor)
    {
        if (actor.Skills.ContainsKey("basic"))
            return new ActionChoice("basic", Target(state, actor)?.Serial);
        return new GreedyPolicy().Choose(state, actor);
    }

    /// <summary>教学关的选敌：永远打**当前生命最低**的敌人（与 <see cref="GreedyPolicy"/> 同款，保证日志稳定）。</summary>
    private static BattleUnit? Target(BattleState state, BattleUnit actor)
        => state.OpposingSide(actor)
            .OrderBy(static u => u.Hp)
            .ThenBy(static u => u.Serial)
            .FirstOrDefault();
}
