using WaningOath.Core.Data;

namespace WaningOath.Core.Progression;

/// <summary>
/// 按**投入档位**生成一套"标准配装"。
/// </summary>
/// <remarks>
/// <b>它是干什么的</b>：`M2` 的 <see cref="TierPanel"/> 是"按锚点插值猜一个面板"，
/// 用处只是"在装备还没进战斗时也能分档跑平衡"。真实配装能算了之后就该用它重跑 ——
/// 而"四档投入"这句话需要一个**具体的配装实体**，否则每一档都只是一个人为数字。
/// <see cref="Build"/> 就是那个实体。
/// <para>
/// ⚠️ <b>它是"标准配装"，不是"最优配装"</b>：主属性按定位取常规解，
/// 副属性**先把双暴补到该定位的目标值、剩下的全给攻击%**。
/// 真玩家会在速度 / 异常积蓄效率 / 生命之间做取舍，所以它跑出来的 DPR 应当读作
/// **该档位的下限附近**，而不是天花板。
/// </para>
/// </remarks>
public static class CanonicalLoadout
{
    /// <summary>一档投入的数量特征。</summary>
    public sealed record Spec(
        string Id,
        string Name,
        int GearLevel,
        bool HasWeapon,
        bool SupportBuild);

    public static readonly Spec Tier0Plus0 = new("tier-0plus0", "0+0 裸装", -1, false, false);
    public static readonly Spec Tier0Plus1 = new("tier-0plus1-low", "0+1 低配", 0, true, false);
    public static readonly Spec TierMid = new("tier-mid", "中配", 8, true, false);
    public static readonly Spec TierGraduate = new("tier-graduate", "大毕业", 15, true, false);

    public static IEnumerable<Spec> All => [Tier0Plus0, Tier0Plus1, TierMid, TierGraduate];

    /// <summary>一件装备在某个强化等级下**一共 roll 出多少条**副属性（初始 3 条 + +3 补的第 4 条 + 5 次强化命中）。</summary>
    public static int RollCount(GearTable gear, int level)
    {
        if (level < 0) return 0;
        int n = gear.Rules.SubStatInitCount;
        if (level >= gear.Rules.SubStatFullAtLevel) n = gear.Rules.SubStatFullCount;
        foreach (int hit in gear.Rules.EnhanceHitLevels)
        {
            if (level >= hit) n++;
        }
        return n;
    }

    /// <summary>一条副属性在一件装备上最多能被强化几次（1 次初始 + 5 次命中）。</summary>
    private const int MaxRollsPerStat = 6;

    /// <summary>一个角色在一档投入下的标准配装。</summary>
    public static Loadout Build(GameData data, CharacterDef def, Spec spec, TierRowDef? row)
    {
        GearTable gear = data.Gear;
        string? weaponId = spec.HasWeapon ? def.SignatureWeapon : null;
        int overwrite = spec.HasWeapon && weaponId is not null && data.Weapon(weaponId)?.Effect is { } eff
            ? Math.Max(1, eff.InitialOverrideRank)
            : 1;

        if (spec.GearLevel < 0)
        {
            return new Loadout(Level: 80, Breakthrough: 0, Eidolon: 0, WeaponId: weaponId,
                WeaponLevel: 80, WeaponOverwrite: overwrite, Gears: []);
        }

        List<GearSlotDef> slots = [.. gear.Slots.OrderBy(static s => s.Order)];
        Dictionary<string, string> mains = new(StringComparer.Ordinal);
        foreach (GearSlotDef slot in slots) mains[slot.Key] = PickMainStat(slot, spec);

        int rollsPerPiece = RollCount(gear, spec.GearLevel);
        int totalRolls = rollsPerPiece * slots.Count;

        // ── 第一遍：只用主属性算一次面板，量出双暴缺口 ──
        BaseStatsDef bare = ComputePanel(data, def, weaponId, spec, overwrite, slots, mains, []);

        double targetRate = row?.CritRateFull ?? bare.CritRate;
        double targetDmg = row?.CritDmgFull ?? bare.CritDmg;
        double perRate = PerRoll(gear, "critRate", 0.036);
        double perDmg = PerRoll(gear, "critDmg", 0.072);
        double perAtk = PerRoll(gear, "atkPct", 0.072);

        int wantRate = Math.Clamp((int)Math.Ceiling(Math.Max(0.0, targetRate - bare.CritRate) / perRate - 1e-9), 0, totalRolls);
        int wantDmg = Math.Clamp((int)Math.Ceiling(Math.Max(0.0, targetDmg - bare.CritDmg) / perDmg - 1e-9), 0, totalRolls);
        if (wantRate + wantDmg > totalRolls)
        {
            // 预算不够就按缺口比例让位（先保住暴击伤害那一半）。
            int over = wantRate + wantDmg - totalRolls;
            int cutDmg = Math.Min(over, wantDmg);
            wantDmg -= cutDmg;
            over -= cutDmg;
            wantRate = Math.Max(0, wantRate - over);
        }
        int wantAtk = Math.Max(0, totalRolls - wantRate - wantDmg);

        // ── 第二遍：把条数摊到 6 件上 ──
        //
        // ⚠️ 第一版这里用的是一个"队列 + break"的写法：每件从队首连续取，
        //    取到某条副属性撞上「单件最多 6 次」的上限就 `break` 出整件 —— 结果是
        //    **每件只填了几条就走人**，而且出现过「大毕业的双暴反而低于中配」这种非单调结果。
        //    现在改成**逐件贪心**：每件内部按"剩余需求最大的那条"依次填满，
        //    于是面板在档位之间**单调不减**（`Sim loadout` 会断言这一点）。
        int[] remaining = [wantRate, wantDmg, wantAtk];
        string[] kinds = ["critRate", "critDmg", "atkPct"];
        List<GearPiece> pieces = [];

        foreach (GearSlotDef slot in slots)
        {
            int[] onPiece = new int[kinds.Length];
            int filled = 0;
            while (filled < rollsPerPiece)
            {
                int pick = -1;
                for (int k = 0; k < kinds.Length; k++)
                {
                    if (remaining[k] <= 0 || onPiece[k] >= MaxRollsPerStat) continue;
                    if (pick < 0 || remaining[k] > remaining[pick]) pick = k;
                }
                if (pick < 0) break;   // 三个方向都填满了，这一件到此为止
                onPiece[pick]++;
                remaining[pick]--;
                filled++;
            }

            List<(string Stat, double Value)> subs = [];
            for (int k = 0; k < kinds.Length; k++)
            {
                if (onPiece[k] <= 0) continue;
                double per = kinds[k] switch
                {
                    "critRate" => perRate,
                    "critDmg" => perDmg,
                    _ => perAtk,
                };
                subs.Add((kinds[k], per * onPiece[k]));
            }

            pieces.Add(new GearPiece($"{def.Id}-{slot.Key}", def.GearSet ?? "gear-none", slot.Key,
                spec.GearLevel, mains[slot.Key], subs));
        }

        return new Loadout(Level: 80, Breakthrough: 4, Eidolon: 0, WeaponId: weaponId,
            WeaponLevel: 80, WeaponOverwrite: overwrite, Gears: pieces);
    }

    private static double PerRoll(GearTable gear, string stat, double fallback)
        => gear.SubStats.TryGetValue(stat, out GearSubStatDef? def) ? def.PerRoll : fallback;

    /// <summary>用给定的主属性与副属性算一次面板（副属性为空时就是"只算主属性"）。</summary>
    private static BaseStatsDef ComputePanel(
        GameData data, CharacterDef def, string? weaponId, Spec spec, int overwrite,
        List<GearSlotDef> slots, Dictionary<string, string> mains, List<(string, double)> _)
    {
        List<GearPiece> pieces = [];
        foreach (GearSlotDef slot in slots)
        {
            pieces.Add(new GearPiece("probe-" + slot.Key, def.GearSet ?? "gear-none", slot.Key,
                spec.GearLevel, mains[slot.Key], []));
        }

        Loadout probe = new(Level: 80, Breakthrough: 4, Eidolon: 0, WeaponId: weaponId,
            WeaponLevel: 80, WeaponOverwrite: overwrite, Gears: pieces);
        return PanelCalculator.Compute(data, def, probe).Panel;
    }

    private static string PickMainStat(GearSlotDef slot, Spec spec)
    {
        if (slot.MainStats.Count == 0) return "";
        if (slot.MainStats.Count == 1) return slot.MainStats[0];

        // 辅助向：生命与充能优先；输出向：攻击 / 双暴 / 属性增伤。
        string[] supportPreference =
            ["hpPct", "energyRegen", "defPct", "atkPct", "healBonus", "shieldBonus", "elemBonus", "effectHit", "effectRes"];
        string[] dpsPreference =
            ["atkPct", "critDmg", "critRate", "elemBonus", "hpPct", "defPct", "effectHit", "effectRes", "energyRegen", "healBonus", "shieldBonus"];
        foreach (string want in spec.SupportBuild ? supportPreference : dpsPreference)
        {
            if (slot.MainStats.Contains(want, StringComparer.Ordinal)) return want;
        }
        return slot.MainStats[0];
    }
}
