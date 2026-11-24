using WaningOath.Core;
using WaningOath.Core.Game;
using WaningOath.Core.Battle;
using WaningOath.Core.Data;
using WaningOath.Core.Progression;

namespace WaningOath.Server.Game;

/// <summary>一次业务调用的结果。`Ok == false` 时 HTTP 层用 <see cref="Status"/> 回错误。</summary>
public sealed record ApiResult(bool Ok, int Status, object? Body, string? Error = null)
{
    public static ApiResult Good(object? body) => new(true, 200, body);
    public static ApiResult Bad(int status, string error, object? extra = null)
        => new(false, status, extra, error);
}

/// <summary>
/// 服务端业务：养成结算 · 抽卡 · 关卡产出 · 战斗重放校验。
/// </summary>
/// <remarks>
/// <b>职责边界</b>（`06 §2.3`）：
/// <list type="bullet">
///   <item>战斗过程 —— 客户端跑，服务器**用同一份 Core 重放校验**。</item>
///   <item>养成结算 / 掉落 / 抽卡 / 首通奖励 / 配额恢复 / 每日每周重置 —— **服务器权威**。</item>
/// </list>
/// 因此本类里所有"能给玩家加东西"的路径都**只由服务端决定数量**，客户端的输入只用来表达意图。
/// </remarks>
public sealed partial class GameService(GameData data, PlayerStore store, AuditLog audit, Func<long> clock)
{
    public GameData Data => data;
    public PlayerStore Store => store;
    private long Now => clock();

    // ── 时间结算（06 §2.6） ───────────────────────────────────────────

    /// <summary>按时间差恢复配额 + 每日 / 每周重置。**幂等**：同一时刻重复调用不产生额外收益。</summary>
    public void SettleTime(PlayerSave save)
    {
        long now = Now;

        // 配额：每点 360 秒（6 分钟），上限 240（02 §10）。
        if (save.Stamina.Current < BattleConstants.StaminaMax && save.Stamina.LastRegenAt > 0)
        {
            long elapsedSec = Math.Max(0, (now - save.Stamina.LastRegenAt) / 1000);
            int gained = (int)(elapsedSec / (long)BattleConstants.StaminaRegenSec);
            if (gained > 0)
            {
                int before = save.Stamina.Current;
                save.Stamina.Current = Math.Min(BattleConstants.StaminaMax, save.Stamina.Current + gained);
                // 只在真的涨了的时候推进 lastRegenAt，否则会把"没满一分钟的零头"丢掉。
                save.Stamina.LastRegenAt += (long)(gained * BattleConstants.StaminaRegenSec * 1000);
                if (save.Stamina.Current >= BattleConstants.StaminaMax) save.Stamina.LastRegenAt = now;
                if (save.Stamina.Current != before) audit.Write("stamina.regen", save.UserId, new { gained = save.Stamina.Current - before });
            }
        }
        else if (save.Stamina.LastRegenAt == 0)
        {
            save.Stamina.LastRegenAt = now;
        }

        // 每日重置：边界是**本地 04:00**（02 §10 的 RESET_HOUR）。
        DateTimeOffset local = DateTimeOffset.FromUnixTimeMilliseconds(now).ToLocalTime();
        DateTimeOffset todayReset = new(local.Year, local.Month, local.Day, BattleConstants.ResetHour, 0, 0, local.Offset);
        if (local < todayReset) todayReset = todayReset.AddDays(-1);
        long todayResetMs = todayReset.ToUnixTimeMilliseconds();

        if (save.Progress.LastDailyResetAt < todayResetMs)
        {
            save.Progress.LastDailyResetAt = todayResetMs;
            save.Progress.DailyDelegateDone = 0;
            save.Progress.DailyDelegateClaimed = false;
            save.Progress.FreeExpRunDone = false;
            audit.Write("daily.reset", save.UserId, null);
        }

        string weekKey = PlayerSave.WeekKeyOf(now);
        if (!string.Equals(save.Progress.WeekKey, weekKey, StringComparison.Ordinal))
        {
            save.Progress.WeekKey = weekKey;
            save.Progress.BossFreeUsed = 0;
            save.Progress.WeeklyTaskDone = 0;
            save.Progress.WeeklyTaskClaimed = false;
            audit.Write("weekly.reset", save.UserId, new { weekKey });
        }

        string monthKey = local.ToString("yyyy-MM");
        if (!string.Equals(save.Progress.MonthKey, monthKey, StringComparison.Ordinal))
        {
            save.Progress.MonthKey = monthKey;
            save.Progress.MonthlyPurchases.Clear();
            audit.Write("monthly.reset", save.UserId, new { monthKey });
        }

        // 协议等级按角色等级重算（06 §3.1：不信任客户端上报）。
        save.Profile.ProtocolLevel = RecomputeProtocolLevel(save);
    }

    /// <summary>`04 §1`：协议等级 N 需要 **4 名角色**达到 20 / 40 / 60 / 80 级。</summary>
    public static int RecomputeProtocolLevel(PlayerSave save)
    {
        int[] req = [20, 40, 60, 80];
        int level = 0;
        for (int i = 0; i < req.Length; i++)
        {
            int count = save.Characters.Values.Count(c => c.Level >= req[i]);
            if (count >= 4) level = i + 1;
        }
        return level;
    }

    // ── 养成结算（06 §2.2 的 growth） ────────────────────────────────

    /// <summary>
    /// 升级：客户端只说"我要用哪些经验道具升这个角色"，**经验换算与扣材料都在这里**。
    /// </summary>
    public ApiResult LevelUp(PlayerSave save, string characterId, Dictionary<string, int> items)
    {
        if (!save.Characters.TryGetValue(characterId, out PlayerSave.CharacterState? st))
            return ApiResult.Bad(404, $"存档里没有角色 '{characterId}'。");
        if (items.Count == 0) return ApiResult.Bad(400, "没有提交任何经验道具。");

        GrowthTable g = data.Growth;
        double gained = 0;
        Dictionary<string, int> consumed = new(StringComparer.Ordinal);

        foreach (KeyValuePair<string, int> kv in items.OrderBy(static k => k.Key, StringComparer.Ordinal))
        {
            if (kv.Value <= 0) return ApiResult.Bad(400, $"道具 '{kv.Key}' 的数量必须为正。");
            double per = GrowthRules.ExpValue(g, kv.Key);
            if (per <= 0) return ApiResult.Bad(400, $"'{GrowthRules.ItemName(g, kv.Key)}' 不是经验道具。");
            if (!save.Inventory.TryGetValue(kv.Key, out int have) || have < kv.Value)
                return ApiResult.Bad(400, $"'{GrowthRules.ItemName(g, kv.Key)}' 不足：需要 {kv.Value}，持有 {have}。");
            consumed[kv.Key] = kv.Value;
            gained += per * kv.Value;
        }

        if (st.Level >= g.Level.Cap)
            return ApiResult.Bad(409, $"{characterId} 已经是 {g.Level.Cap} 级。");

        (int level, double exp) = GrowthRules.ApplyExp(g, st.Level, st.Exp, gained);

        // 突破门槛：等级不能越过尚未突破的节点（02 §3：20 / 40 / 60 / 80）。
        int allowedMax = AllowedLevelCap(save, characterId, g, st.Breakthrough, st.Level);
        if (level > allowedMax)
        {
            level = allowedMax;
            exp = 0.0;
        }

        foreach (KeyValuePair<string, int> kv in consumed) save.Inventory[kv.Key] -= kv.Value;

        int before = st.Level;
        st.Level = level;
        st.Exp = exp;
        EnsureSkillLevels(save, characterId, st);
        save.Profile.ProtocolLevel = RecomputeProtocolLevel(save);
        save.UpdatedAt = Now;
        audit.Write("growth.levelUp", save.UserId, new { characterId, from = before, to = level, gained });

        return ApiResult.Good(new
        {
            characterId,
            levelBefore = before,
            levelAfter = level,
            exp = st.Exp,
            expGained = gained,
            consumed,
            cappedByBreakthrough = level == allowedMax && before < allowedMax,
            protocolLevel = save.Profile.ProtocolLevel,
        });
    }

    /// <summary>角色在"当前突破次数"下允许到达的等级上限。</summary>
    public static int AllowedLevelCap(PlayerSave save, string characterId, GrowthTable g, int breakthrough, int currentLevel)
    {
        List<int> nodes = [.. g.Level.BreakthroughNodes.OrderBy(static n => n)];
        int idx = Math.Clamp(breakthrough, 0, nodes.Count);
        return idx >= nodes.Count ? g.Level.Cap : nodes[idx];
    }

    /// <summary>突破：等级到节点后花「阶跃核心」把等级上限抬到下一位。</summary>
    public ApiResult Breakthrough(PlayerSave save, string characterId)
    {
        if (!save.Characters.TryGetValue(characterId, out PlayerSave.CharacterState? st))
            return ApiResult.Bad(404, $"存档里没有角色 '{characterId}'。");
        GrowthTable g = data.Growth;

        int? node = GrowthRules.NextBreakthroughRequirement(g, st.Breakthrough);
        if (node is null) return ApiResult.Bad(409, $"{characterId} 已经完成全部 4 次突破。");
        if (st.Level < node.Value) return ApiResult.Bad(409, $"等级 {st.Level} 还没到突破节点 {node.Value}。");

        int? cost = GrowthRules.BreakthroughCost(g, node.Value);
        if (cost is null)
            return ApiResult.Bad(501, $"突破节点 {node.Value} 的「阶跃核心」消耗尚未登记（data/tables/growth.json）。");

        string material = g.BreakthroughMaterial;
        int have = save.Inventory.GetValueOrDefault(material);
        if (have < cost.Value)
            return ApiResult.Bad(400, $"「{GrowthRules.ItemName(g, material)}」不足：需要 {cost.Value}，持有 {have}。");

        save.Inventory[material] = have - cost.Value;
        st.Breakthrough++;
        save.UpdatedAt = Now;
        audit.Write("growth.breakthrough", save.UserId, new { characterId, node = node.Value, cost = cost.Value });

        return ApiResult.Good(new
        {
            characterId,
            breakthrough = st.Breakthrough,
            node = node.Value,
            nextLevelCap = AllowedLevelCap(save, characterId, g, st.Breakthrough, st.Level),
            consumed = new Dictionary<string, int> { [material] = cost.Value },
        });
    }

    /// <summary>每日委托：完成 3 次普通关卡后领取 80 协议原码（03 §4.1）。</summary>
    public ApiResult ClaimDailyDelegate(PlayerSave save)
    {
        if (save.Progress.DailyDelegateClaimed)
            return ApiResult.Bad(409, "今天的每日委托奖励已经领过了。");
        if (save.Progress.DailyDelegateDone < 3)
            return ApiResult.Bad(409, $"每日委托还没完成：{save.Progress.DailyDelegateDone}/3。");
        save.Progress.DailyDelegateClaimed = true;
        save.Currency.ProtocolCode += 80;
        save.UpdatedAt = Now;
        audit.Write("daily.claim", save.UserId, new { protocolCode = 80 });
        return ApiResult.Good(new { protocolCode = 80, currency = save.Currency });
    }

    /// <summary>每日档案演习：免费领取一次经验本重复档奖励，不消耗配额。</summary>
    public ApiResult ClaimFreeExp(PlayerSave save)
    {
        if (save.Progress.FreeExpRunDone)
            return ApiResult.Bad(409, "今天的免费档案演习已经领取过了。");
        if (!data.Stages.TryGetValue("stage-exp-1", out StageDef? stage))
            return ApiResult.Bad(404, "缺少 stage-exp-1，无法发放免费经验。");
        RewardBundle bundle = stage.Reward.Repeat;
        save.Currency.ProtocolCode += bundle.ProtocolCode;
        save.Currency.Credit += bundle.Credit;
        foreach ((string itemId, int count) in bundle.Items)
            save.Inventory[itemId] = save.Inventory.GetValueOrDefault(itemId) + count;
        save.Progress.FreeExpRunDone = true;
        save.UpdatedAt = Now;
        audit.Write("daily.freeExp", save.UserId, new { stageId = stage.Id, bundle.Items });
        return ApiResult.Good(new
        {
            stageId = stage.Id,
            protocolCode = bundle.ProtocolCode,
            credit = bundle.Credit,
            items = bundle.Items,
            currency = save.Currency,
        });
    }

    /// <summary>周常任务：完成 10 次普通关卡后领取 160 协议原码（03 §4.1）。</summary>
    public ApiResult ClaimWeeklyTask(PlayerSave save)
    {
        if (save.Progress.WeeklyTaskClaimed)
            return ApiResult.Bad(409, "本周的周常任务奖励已经领过了。");
        if (save.Progress.WeeklyTaskDone < 10)
            return ApiResult.Bad(409, $"周常任务还没完成：{save.Progress.WeeklyTaskDone}/10。");
        save.Progress.WeeklyTaskClaimed = true;
        save.Currency.ProtocolCode += 160;
        save.UpdatedAt = Now;
        audit.Write("weekly.claim", save.UserId, new { protocolCode = 160 });
        return ApiResult.Good(new { protocolCode = 160, currency = save.Currency });
    }

    /// <summary>技能升级：一次升一级，按 `02 §4` 的材料表扣，按权阶 + 星级卡上限。</summary>
    public ApiResult SkillUp(PlayerSave save, string characterId, string slot, int? times)
    {
        if (!save.Characters.TryGetValue(characterId, out PlayerSave.CharacterState? st))
            return ApiResult.Bad(404, $"存档里没有角色 '{characterId}'。");
        CharacterDef def = data.Character(characterId);
        GrowthTable g = data.Growth;
        int cap = GrowthRules.SkillCap(g, def.RarityEnum, st.Eidolon);
        int steps = Math.Clamp(times ?? 1, 1, 14);

        List<object> applied = [];
        for (int i = 0; i < steps; i++)
        {
            int cur = st.SkillLevels.GetValueOrDefault(slot, 1);
            if (cur >= cap)
            {
                if (i == 0) return ApiResult.Bad(409, $"{slot} 已到上限 {cap}（权阶 {st.Eidolon} · {def.Rarity}★）。");
                break;
            }
            int next = cur + 1;
            SkillUpgradeCostDef? cost = GrowthRules.SkillCost(g, next);
            if (cost is null)
                return ApiResult.Bad(501, $"技能 {next} 级的消耗尚未登记（data/tables/growth.json）。");

            int have = save.Inventory.GetValueOrDefault(cost.Material);
            if (have < cost.Count) break;

            save.Inventory[cost.Material] = have - cost.Count;
            st.SkillLevels[slot] = next;
            applied.Add(new { slot, level = next, material = cost.Material, count = cost.Count });
        }

        if (applied.Count == 0)
            return ApiResult.Bad(400, "材料不足，一级也升不了。");

        save.UpdatedAt = Now;
        audit.Write("growth.skillUp", save.UserId, new { characterId, slot, applied = applied.Count });
        return ApiResult.Good(new { characterId, slot, level = st.SkillLevels.GetValueOrDefault(slot, 1), cap, applied });
    }

    /// <summary>武器升级 / 权限覆写（覆写**只强化特效**，`02 §5.3`）。</summary>
    public ApiResult WeaponUp(PlayerSave save, string weaponId, int? level, int? overwrite)
    {
        if (!save.Weapons.TryGetValue(weaponId, out PlayerSave.WeaponState? w))
            return ApiResult.Bad(404, $"存档里没有武器 '{weaponId}'。");
        WeaponDef? def = data.Weapon(weaponId);
        if (def is null) return ApiResult.Bad(404, $"data/weapons/ 里没有 '{weaponId}'。");

        if (level is int target)
        {
            if (target is < 1 or > 80) return ApiResult.Bad(400, "武器等级应在 1~80。");
            if (target < w.Level) return ApiResult.Bad(409, "武器等级不能回退。");
            w.Level = target;
        }
        if (overwrite is int ow)
        {
            if (ow is < 1 or > 5) return ApiResult.Bad(400, "权限覆写应在 1~5 阶（02 §5.3）。");
            if (ow < w.Overwrite) return ApiResult.Bad(409, "权限覆写不能回退。");
            w.Overwrite = ow;
        }
        save.UpdatedAt = Now;
        audit.Write("growth.weapon", save.UserId, new { weaponId, level = w.Level, overwrite = w.Overwrite });
        return ApiResult.Good(new { weaponId, level = w.Level, overwrite = w.Overwrite, partCount = def.PartCount });
    }

    /// <summary>补齐角色缺失的技能位等级（新建存档 / 老存档升级时用）。</summary>
    public static void EnsureSkillLevels(PlayerSave save, string characterId, PlayerSave.CharacterState st)
    {
        _ = save;
        _ = characterId;
        foreach (string slot in new[] { "basic", "resonance", "talent", "ultimate" })
        {
            if (!st.SkillLevels.ContainsKey(slot)) st.SkillLevels[slot] = 1;
        }
    }
}
