using WaningOath.Core.Data;

namespace WaningOath.Core.Progression;

/// <summary>
/// 养成结算规则（`data/tables/growth.json`）。**纯函数，不碰存档** ——
/// 服务端与客户端读同一份规则，因此"客户端显示的消耗"与"服务端扣的材料"不可能对不上。
/// </summary>
/// <remarks>
/// 依据 `02 §2`（经验曲线）· `02 §3`（突破）· `02 §4`（技能等级与上限）· `01 §5.2`（星级分档）。
/// </remarks>
public static class GrowthRules
{
    /// <summary>从 <paramref name="level"/> 升到下一级需要的经验。</summary>
    public static double ExpToNext(GrowthTable g, int level)
    {
        if (level >= g.Level.Cap) return 0.0;
        double exp = g.Level.ExpBase;
        // 递推口径：`ExpToNext(n) = ExpToNext(n-1) × rate(n)`，`rate(n)` 按**第 n 级**落在哪个段取。
        // 也就是说 `02 §2.1` 那句"每级需求按 ×1.10 增长（1~60 级）"里的"级"是**被乘的那个等级**，
        // 不是"要升到的那一级"。
        //
        // ⚠️ 这条差一位是**用 `02 §2.1` 自己的表校出来的**（`M3-growth` 校验盯着它）：
        //    表里 `需求(61) = 55,360 = 需求(60) × 1.10` ⇒ 第 60 级仍在 1.10 段；
        //    `需求(62) = 59,789 = 需求(61) × 1.08` ⇒ 第 61 级已经进了 1.08 段。
        //    若改成"按要升到的那一级查段"，总经验会算成 2,999,912 而不是表里的 **2,928,936**。
        //    2.4% 的偏差不会让任何一处报错，只会让"满级要多少经验"悄悄对不上。
        for (int n = 2; n <= level; n++) exp *= RateAt(g, n);
        return exp;
    }

    /// <summary>第 <paramref name="level"/> 级升到 <c>level+1</c> 时使用的增长率（按"起始级"查段）。</summary>
    public static double RateAt(GrowthTable g, int level)
    {
        foreach (LevelRateDef t in g.Level.RateTiers)
        {
            if (level >= t.FromLevel && level <= t.ToLevel) return t.Rate;
        }
        return g.Level.RateTiers.Count > 0 ? g.Level.RateTiers[^1].Rate : 1.10;
    }

    /// <summary>从 1 级升到 <paramref name="targetLevel"/> 一共需要多少经验（含小数取整）。</summary>
    public static double TotalExpTo(GrowthTable g, int targetLevel)
    {
        double sum = 0;
        for (int lv = 1; lv < targetLevel; lv++) sum += ExpToNext(g, lv);
        return sum;
    }

    /// <summary>
    /// 技能等级上限（`02 §4`）：**按星级分档**，再按当前权阶取。
    /// 5★ → 0~Ⅱ = 10 / Ⅲ~Ⅳ = 12 / Ⅴ~Ⅵ = 14；4★ → 10 / 11 / 12。
    /// </summary>
    public static int SkillCap(GrowthTable g, Rarity rarity, int eidolon)
    {
        string key = ((int)rarity).ToString();
        if (!g.SkillCapsByEidolon.TryGetValue(key, out List<int>? caps) || caps.Count == 0)
        {
            // 没有该星级的档位就退到 5★ 的档 —— 静默用"没有上限"是更糟的选择。
            caps = g.SkillCapsByEidolon.TryGetValue("5", out List<int>? five) ? five : [10, 10, 10, 12, 12, 14, 14];
        }
        int idx = Math.Clamp(eidolon, 0, caps.Count - 1);
        return caps[idx];
    }

    /// <summary>突破到 <paramref name="node"/> 级需要多少个材料；`null` = 该节点没有登记消耗。</summary>
    public static int? BreakthroughCost(GrowthTable g, int node)
    {
        foreach (BreakthroughCostDef c in g.BreakthroughCosts)
        {
            if (c.Node == node) return c.Count;
        }
        return null;
    }

    /// <summary>
    /// **下一次突破需要先达到的等级**（= 当前被卡住的那个等级上限）；已满级返回 `null`。
    /// </summary>
    /// <remarks>
    /// ⚠️ 参数是**突破次数**不是当前等级：突破节点 `[20, 40, 60, 80]` 与"上限"是同一个数 ——
    /// 0 次突破时上限 20、必须**正好到 20 级**才能突破；突破后上限变 40。
    /// 早先的写法"取第一个大于当前等级的节点"在 20 级时会返回 40，于是玩家**到了 20 级却突破不了**。
    /// </remarks>
    public static int? NextBreakthroughRequirement(GrowthTable g, int breakthrough)
    {
        List<int> nodes = [.. g.Level.BreakthroughNodes.OrderBy(static n => n)];
        return breakthrough >= 0 && breakthrough < nodes.Count ? nodes[breakthrough] : null;
    }

    /// <summary>突破次数 = 已经过了几个节点。</summary>
    public static int BreakthroughCount(GrowthTable g, int level)
        => g.Level.BreakthroughNodes.Count(n => level >= n);

    /// <summary>升到 <paramref name="toLevel"/> 需要什么材料；`null` = 该级没有登记消耗。</summary>
    public static SkillUpgradeCostDef? SkillCost(GrowthTable g, int toLevel)
        => g.SkillCosts.FirstOrDefault(c => c.ToLevel == toLevel);

    /// <summary>一个经验道具值多少经验（不是经验道具则返回 0）。</summary>
    public static double ExpValue(GrowthTable g, string itemId)
    {
        if (g.ExpItems.TryGetValue(itemId, out double v)) return v;
        return g.Items.TryGetValue(itemId, out ItemDef? def) ? def.Exp : 0.0;
    }

    /// <summary>道具的显示名（没登记就回退成 id，便于排错）。</summary>
    public static string ItemName(GrowthTable g, string itemId)
        => g.Items.TryGetValue(itemId, out ItemDef? def) && def.Name.Length > 0 ? def.Name : itemId;

    /// <summary>
    /// 把经验加到某等级上，返回新的等级与剩余经验（**不碰道具库存**）。
    /// </summary>
    /// <remarks>
    /// 服务端结算与客户端的"预览升到几级"共用这一条，因此预览与实际结果不会分叉。
    /// </remarks>
    public static (int Level, double Exp) ApplyExp(GrowthTable g, int level, double exp, double gained)
    {
        double pool = exp + gained;
        int lv = level;
        while (lv < g.Level.Cap)
        {
            double need = ExpToNext(g, lv);
            if (need <= 0 || pool < need) break;
            pool -= need;
            lv++;
        }
        if (lv >= g.Level.Cap) pool = 0.0;
        return (lv, pool);
    }
}
