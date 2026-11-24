using WaningOath.Core.Data;

namespace WaningOath.Core.Battle;

/// <summary>
/// 把"一堆属性键"合成一个 <see cref="StatusDef"/>，并注册进
/// <see cref="GameData.Generated"/>。
/// </summary>
/// <remarks>
/// <b>为什么走"合成一个状态"而不是直接改面板</b>：`01 §1.3` 的三分法里，
/// "有时长 / 可叠层 / 按谁的行动计时"这三件事全部由 <see cref="StatusInstance"/> 承载。
/// 武器特效（"释放终结技后 2 回合"）与套装效果恰好都需要这三件事 ——
/// 直接改 <see cref="BattleUnit.Base"/> 既没有时长、也无法叠加、更不会到期消失。
/// <para>
/// ⚠️ 合成是**幂等**的：同一个 id 第二次合成直接返回缓存，所以一场战斗里反复触发不会
/// 制造出成百上千个等价的状态定义（那会让日志与内存都不可读）。
/// </para>
/// </remarks>
public static class StatusFactory
{
    /// <summary>合成 id 的统一前缀 —— 让"这是运行时造的"在日志里一眼可辨。</summary>
    public const string Prefix = "gen:";

    /// <summary>
    /// 由一组 `(键, 值)` 合成一个状态。
    /// </summary>
    /// <param name="data">静态数据（合成结果会注册进 <see cref="GameData.Generated"/>）。</param>
    /// <param name="id">稳定 id（例：`gen:weap-hengdao-yuanchu@5`）。</param>
    /// <param name="name">显示名（进日志）。</param>
    /// <param name="pairs">属性键与数值。</param>
    /// <param name="duration">持续；**`0` = 本场永久**。</param>
    /// <param name="appliesTo">`null` = 只对挂载对象生效；`partyAll` = 光环（`01 §1.3`）。</param>
    /// <param name="unmapped">**没被引擎认出来的键** —— 由调用方决定怎么报出来，绝不静默丢弃。</param>
    public static StatusDef Build(
        GameData data,
        string id,
        string name,
        IEnumerable<(string Key, double Value)> pairs,
        double duration,
        string? appliesTo,
        out List<string> unmapped)
    {
        if (data.Generated.TryGetValue(id, out StatusDef? cached))
        {
            unmapped = [];
            return cached;
        }

        StatusDef def = new()
        {
            Id = id,
            Name = name,
            Kind = "buff",
            Duration = duration,
            // ⚠️ 时长单位必须是**自然回合**：武器特效的"持续 2 回合"在 `101` 里是按战斗节奏说的，
            //    而 `01 §1.3` 的三分法里 `turn` 会随"被挂的人行动了几次"消耗 —— 一个高速辅助
            //    挂上去的 2 回合增益，在两回合内可能已经被消耗掉四五次。
            DurationUnit = "round",
            // **不可叠层**：重复触发只刷新时间（`01 §6.2` 通则 1 的"同名刷新"）。
            Stackable = false,
            StackCap = 1,
            AppliesTo = appliesTo,
        };

        List<string> missed = [];
        foreach ((string key, double value) in pairs)
        {
            if (!StatKeyBinder.TryApply(def, key, value, name)) missed.Add(key);
        }
        unmapped = missed;

        data.Generated[id] = def;
        return def;
    }

    /// <summary>把合成出来的状态挂到一个单位身上（**不消耗层数、不触发钩子**，专给"装备提供的常驻"用）。</summary>
    public static StatusInstance Attach(BattleUnit owner, StatusDef def, BattleUnit? source = null)
    {
        StatusInstance st = new()
        {
            Def = def,
            Owner = owner,
            Source = source ?? owner,
            TickSource = owner,
            Remaining = def.Duration,
        };
        owner.Statuses.Add(st);
        return st;
    }
}
