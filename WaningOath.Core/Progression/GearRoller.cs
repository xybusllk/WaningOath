using WaningOath.Core.Data;

namespace WaningOath.Core.Progression;

/// <summary>
/// **装备生成**（掉落 / 兑换 / 管理员发放都走它）。
/// </summary>
/// <remarks>
/// <b>为什么生成要放在 Core 而不是服务端</b>：同一件装备的"主属性是什么、副属性 roll 成什么样"
/// 必须**可复现**。放服务端、用 <c>System.Random</c> 也能跑，但那样一来
/// "玩家说这件装备不对劲"就无从查起 —— 而这里是家庭局域网、作者本人就是客服。
/// <para>
/// 因此：种子由 **(玩家 + 关卡 + 第几次通关)** 决定，同一条件下永远得到同一件。
/// 这跟抽卡用 <see cref="Rng"/> 播种是同一条纪律（`06 §1` 的确定性）。
/// </para>
/// <para>
/// ⚠️ <b>roll 出来的副属性是"结果"</b>：存档存的是已经定下来的数值，面板折算再按它算。
/// 所以"装备的随机性"只在**生成那一刻**存在一次 —— 之后它就是一件确定的装备。
/// </para>
/// </remarks>
public static class GearRoller
{
    /// <summary>生成一件装备。</summary>
    /// <param name="data">静态数据。</param>
    /// <param name="seed">确定性种子。</param>
    /// <param name="setId">套装 id（必须是已登记的）。</param>
    /// <param name="level">初始强化等级（掉落一般是 0）。</param>
    /// <param name="uid">实例 id（由调用方保证唯一）。</param>
    public static GearPiece? Roll(GameData data, ulong seed, string setId, int level, string uid)
    {
        GearTable gear = data.Gear;
        if (gear.Slots.Count == 0 || data.GearSet(setId) is null) return null;

        Rng rng = new(seed);
        GearSlotDef slot = rng.Pick(gear.Slots);
        string main = slot.MainStats.Count > 0 ? rng.Pick(slot.MainStats) : "";

        int rolls = CanonicalLoadout.RollCount(gear, level);
        // 副属性池：**同一件装备上同一条只能出现一次**（这是本作与崩铁一致的常识，也是
        // "4 条副属性"这句话能成立的前提）。所以先洗牌再取前 N 条。
        List<string> pool = [.. gear.SubStats.Keys];
        rng.Shuffle(pool);

        List<(string Stat, double Value)> subs = [];
        foreach (string stat in pool.Take(Math.Min(gear.Rules.SubStatFullCount, pool.Count)))
        {
            // 每条初始 roll 出 1 次强化量；剩下的次数在 +3/+6/+9/+12/+15 里随机命中有副属性的那几条。
            subs.Add((stat, gear.SubStats[stat].PerRoll));
        }

        int extra = rolls - subs.Count;
        for (int i = 0; i < extra && subs.Count > 0; i++)
        {
            int idx = rng.NextInt(subs.Count);
            (string stat, double value) = subs[idx];
            double per = gear.SubStats[stat].PerRoll;
            // 单条最多被强化 5 次（初始 1 次 + 5 次命中 = 6 倍单次量，即满级上限）。
            if (value + per > gear.SubStats[stat].Max + 1e-9) { i--; continue; }
            subs[idx] = (stat, value + per);
        }

        return new GearPiece(uid, setId, slot.Key, level, main, subs);
    }

    /// <summary>这一关该掉哪些套装（`data/tables/gear.json` 的 `drop` 段）。空 = 不掉装备。</summary>
    public static IReadOnlyList<string> DropSetsFor(GameData data, StageDef stage)
        => data.Gear.Drop.TryGetValue(stage.Kind, out List<string>? sets) ? sets : [];

    /// <summary>这一关掉几件。</summary>
    public static int DropCountFor(GameData data, StageDef stage, bool firstClear)
        => data.Gear.DropCount.GetValueOrDefault(firstClear ? "firstClear" : "repeat", 0);
}
