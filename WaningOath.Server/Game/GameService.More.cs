using WaningOath.Core;
using WaningOath.Core.Game;
using WaningOath.Core.Battle;
using WaningOath.Core.Data;
using WaningOath.Core.Progression;

namespace WaningOath.Server.Game;

/// <summary>
/// 服务端业务（续）：抽卡 · 关卡白名单与产出 · 战斗重放校验 · 存档写入校验。
/// </summary>
public sealed partial class GameService
{
    // ── 剧情白名单与章节奖励（06 §2.2 的 story/clear · 100 戊10） ─────

    /// <summary>
    /// 上报剧情通关并由服务端发奖。
    /// </summary>
    /// <remarks>
    /// <b>原实现的洞</b>：它接受**任意 `storyId` 字符串**，每个新字符串都算一次首通
    /// ⇒ 客户端循环 POST 不同的字符串就能无限白拿原码（`06 §3.2` 的缺陷 2 · `100` 戊10）。
    /// <para>
    /// 三条拒绝路径：id 不在 `data/tables/stories.json` 里 · 该章不可重复且已领过 ·
    /// **锚点关卡还没通**（"这一章结束了吗"必须有可判定的依据，不能只听客户端说）。
    /// </para>
    /// </remarks>
    public ApiResult StoryClear(PlayerSave save, string storyId)
    {
        if (!data.Stories.TryGetValue(storyId, out StoryDef? story))
            return ApiResult.Bad(400, $"剧情 '{storyId}' 不在服务端剧情白名单里。");

        if (!save.Progress.Stories.TryGetValue(storyId, out PlayerSave.StoryRecord? rec))
        {
            rec = new PlayerSave.StoryRecord();
            save.Progress.Stories[storyId] = rec;
        }

        // 锚点关卡必须先通 —— 否则"我通关了序章"就只是一句客户端声明。
        bool anchorCleared = !string.IsNullOrEmpty(story.AnchorStage)
                             && save.Progress.Stages.TryGetValue(story.AnchorStage, out PlayerSave.StageRecord? st)
                             && st.Cleared;
        if (!anchorCleared)
        {
            audit.Write("story.clear.rejected", save.UserId, new { storyId, reason = "anchor", story.AnchorStage });
            return ApiResult.Bad(409, $"'{storyId}' 的锚点关卡 '{story.AnchorStage}' 还没通关。");
        }

        if (rec.FirstClearRewarded && !story.Repeatable)
        {
            audit.Write("story.clear.repeatRejected", save.UserId, new { storyId });
            return ApiResult.Bad(409, $"'{story.Name}' 的奖励已经领过了（03 §4.1：主线章节不可重复领取）。");
        }

        rec.Cleared = true;
        rec.FirstClearRewarded = true;
        rec.LastClearedAt = Now;

        save.Currency.ProtocolCode += story.Reward.ProtocolCode;
        save.Currency.Credit += story.Reward.Credit;
        foreach (KeyValuePair<string, int> kv in story.Reward.Items)
            save.Inventory[kv.Key] = save.Inventory.GetValueOrDefault(kv.Key) + kv.Value;

        save.UpdatedAt = Now;
        audit.Write("story.clear", save.UserId, new { storyId, story.Reward.ProtocolCode });

        return ApiResult.Good(new
        {
            storyId,
            name = story.Name,
            protocolCode = story.Reward.ProtocolCode,
            credit = story.Reward.Credit,
            items = story.Reward.Items,
            currency = save.Currency,
        });
    }

    /// <summary>剧情白名单（客户端用它知道"哪几章存在"，服务端用它拒绝未知 id）。</summary>
    public object StoryList()
        => new
        {
            count = data.Stories.Count,
            stories = data.Stories.Values
                .OrderBy(static s => s.Id, StringComparer.Ordinal)
                .Select(static s => new
                {
                    s.Id,
                    s.Name,
                    s.Kind,
                    s.AnchorStage,
                    s.Repeatable,
                    reward = new { s.Reward.ProtocolCode, s.Reward.Credit, s.Reward.Items },
                }),
        };

    // ── M7：存档 → 配装 → 面板 ────────────────────────────────────────

    /// <summary>
    /// 把一个角色的存档状态折成 <see cref="Loadout"/>。
    /// </summary>
    /// <remarks>
    /// <b>这是"客户端跑、服务器重放"能成立的最后一环</b>：两边都必须从**同一份存档**
    /// 算出**同一个面板**，否则重放会分叉，而 409 会被误读成"玩家作弊"。
    /// 所以这段换算只有一份实现，且它只读存档、不读任何客户端提交的数字。
    /// </remarks>
    public static Loadout LoadoutOf(GameData data, PlayerSave save, string characterId)
    {
        if (!save.Characters.TryGetValue(characterId, out PlayerSave.CharacterState? st))
            return new Loadout();

        string? weaponId = st.Weapon;
        int weaponLevel = 80;
        int overwrite = 1;
        if (weaponId is not null && save.Weapons.TryGetValue(weaponId, out PlayerSave.WeaponState? w))
        {
            weaponLevel = w.Level;
            overwrite = w.Overwrite;
        }

        List<GearPiece> pieces = [];
        foreach (string uid in st.Gears)
        {
            PlayerSave.GearState? g = save.Gears.FirstOrDefault(x => string.Equals(x.Uid, uid, StringComparison.Ordinal));
            if (g is null) continue;
            pieces.Add(new GearPiece(g.Uid, g.Set, g.Part, g.Level, g.MainStat,
                [.. g.SubStats.Select(static s => (s.Stat, s.Value))]));
        }

        return new Loadout(st.Level, st.Breakthrough, st.Eidolon, weaponId, weaponLevel, overwrite, pieces);
    }

    /// <summary>一关的我方全员配装（给 <see cref="BattleVerify"/> 用）。</summary>
    public IReadOnlyDictionary<string, Loadout> LoadoutsFor(PlayerSave save, StageDef stage)
    {
        Dictionary<string, Loadout> result = new(StringComparer.Ordinal);
        foreach (string id in stage.Party)
        {
            if (save.Characters.ContainsKey(id)) result[id] = LoadoutOf(data, save, id);
        }
        return result;
    }

    /// <summary>穿戴 / 卸下 / 强化装备（`02 §6.2`：强化上限 +15，只强化**已有**副属性、不新增第 5 条）。</summary>
    public ApiResult GearAction(PlayerSave save, string action, string characterId, string uid, int? targetLevel)
    {
        switch (action)
        {
            case "equip":
            {
                if (!save.Characters.ContainsKey(characterId))
                    return ApiResult.Bad(404, $"存档里没有角色 '{characterId}'。");
                PlayerSave.GearState? g = save.Gears.FirstOrDefault(x => string.Equals(x.Uid, uid, StringComparison.Ordinal));
                if (g is null) return ApiResult.Bad(404, $"存档里没有装备 '{uid}'。");
                GearSlotDef? slot = data.Gear.Slot(g.Part);
                if (slot is null) return ApiResult.Bad(400, $"部位 '{g.Part}' 不在 data/tables/gear.json 的 slots 里。");

                // 同一个部位只能穿一件：先把该角色身上占用这个部位的那件脱下来。
                foreach (PlayerSave.GearState other in save.Gears)
                {
                    if (!string.Equals(other.EquippedBy, characterId, StringComparison.Ordinal)) continue;
                    if (string.Equals(other.Part, g.Part, StringComparison.Ordinal)) other.EquippedBy = null;
                }
                g.EquippedBy = characterId;
                PlayerSave.CharacterState st = save.Characters[characterId];
                st.Gears.RemoveAll(x => x == uid);
                st.Gears.Add(uid);
                save.UpdatedAt = Now;
                audit.Write("gear.equip", save.UserId, new { characterId, uid, part = g.Part });
                return ApiResult.Good(new { characterId, uid, part = g.Part, geared = st.Gears.Count });
            }

            case "unequip":
            {
                PlayerSave.GearState? g = save.Gears.FirstOrDefault(x => string.Equals(x.Uid, uid, StringComparison.Ordinal));
                if (g is null) return ApiResult.Bad(404, $"存档里没有装备 '{uid}'。");
                g.EquippedBy = null;
                if (save.Characters.TryGetValue(characterId, out PlayerSave.CharacterState? st)) st.Gears.RemoveAll(x => x == uid);
                save.UpdatedAt = Now;
                audit.Write("gear.unequip", save.UserId, new { characterId, uid });
                return ApiResult.Good(new { characterId, uid, geared = st?.Gears.Count ?? 0 });
            }

            case "enhance":
            {
                PlayerSave.GearState? g = save.Gears.FirstOrDefault(x => string.Equals(x.Uid, uid, StringComparison.Ordinal));
                if (g is null) return ApiResult.Bad(404, $"存档里没有装备 '{uid}'。");
                int max = data.Gear.Rules.MaxLevel;
                int want = Math.Clamp(targetLevel ?? g.Level + 1, g.Level, max);
                if (want == g.Level) return ApiResult.Bad(409, $"'{uid}' 已经是 +{g.Level}。");

                // ⚠️ **强化只提升主属性，不新增副属性**（02 §6.2：5 次命中只强化已有副属性）。
                //    这里直接改等级，面板由 PanelCalculator 按等级现算 —— 所以**不需要**在这里
                //    重写副属性数值，"等级"与"roll 结果"因此不会各说各话。
                int before = g.Level;
                g.Level = want;
                save.UpdatedAt = Now;
                audit.Write("gear.enhance", save.UserId, new { uid, from = before, to = want });
                return ApiResult.Good(new { uid, levelBefore = before, levelAfter = g.Level, max });
            }

            default:
                return ApiResult.Bad(400, $"未知的装备动作 '{action}'。可用：equip / unequip / enhance。");
        }
    }

    // ── 关卡白名单与产出（06 §2.2 的 stage/clear · 100 戊10） ────────

    /// <summary>关卡白名单：**只从 `data/stages/` 来**。表外的 id 一律不接受。</summary>
    public object StageList()
        => new
        {
            count = data.Stages.Count,
            stages = data.Stages.Values
                .OrderBy(static s => s.Id, StringComparer.Ordinal)
                .Select(static s => new
                {
                    s.Id,
                    s.Name,
                    s.Kind,
                    s.MaxRound,
                    s.Party,
                    s.Enemies,
                    repeatable = s.Reward.Repeatable,
                    rewardable = s.Kind != "sim",
                    firstClear = new { s.Reward.FirstClear.ProtocolCode, s.Reward.FirstClear.Credit, s.Reward.FirstClear.Items },
                    repeat = new { s.Reward.Repeat.ProtocolCode, s.Reward.Repeat.Credit, s.Reward.Repeat.Items },
                }),
        };

    /// <summary>
    /// 上报通关并**由服务端发奖**（`100` 戊10：奖励表驱动，不再硬编码）。
    /// </summary>
    /// <remarks>
    /// 三条拒绝路径都是"以前能无限刷"的洞：
    /// <list type="number">
    ///   <item>id 不在表里 → 400（原实现接受任意字符串，每个新 id 都算首通）。</item>
    ///   <item>`kind == "sim"` → 409（靶场是 `M2` 的实测工具，不是游戏内容）。</item>
    ///   <item>不可重复且已首通 → 409（主线关卡不可刷取，`04 §5.1`）。</item>
    /// </list>
    /// </remarks>
    public ApiResult StageClear(PlayerSave save, string stageId, string outcome)
    {
        if (!data.Stages.TryGetValue(stageId, out StageDef? stage))
            return ApiResult.Bad(400, $"关卡 '{stageId}' 不在服务端关卡白名单里。");

        if (stage.Kind == "sim")
            return ApiResult.Bad(409, "'{0}' 是平衡实测靶场，不发奖励。".Replace("{0}", stageId));

        if (!string.Equals(outcome, "AllyWin", StringComparison.OrdinalIgnoreCase))
        {
            // 打输了也记一次挑战，但不发奖。
            save.UpdatedAt = Now;
            audit.Write("stage.clear.rejected", save.UserId, new { stageId, outcome });
            return ApiResult.Good(new { stageId, rewarded = false, reason = "未通关" });
        }

        if (string.Equals(stage.Kind, "boss", StringComparison.Ordinal) && save.Progress.BossFreeUsed >= 1)
            return ApiResult.Bad(409, "本周的每周首领奖励已经领取过了，周一 04:00 重置。");

        // 先判“还能不能再打”，再扣配额：否则已通关且不可重复的关卡会白扣一次。
        if (save.Progress.Stages.TryGetValue(stageId, out PlayerSave.StageRecord? existing)
            && existing.FirstClearRewarded
            && !stage.Reward.Repeatable)
        {
            save.UpdatedAt = Now;
            audit.Write("stage.clear.repeatRejected", save.UserId, new { stageId });
            return ApiResult.Bad(409, $"'{stageId}' 不可重复刷取（04 §5.1）。");
        }

        // 配额在**服务端结算时扣**，客户端的余额检查只负责提前提示，不能当扣费依据。
        int staminaCost = stage.Reward.StaminaCost;
        if (save.Stamina.Current < staminaCost)
            return ApiResult.Bad(400, $"运维配额不足：需要 {staminaCost}，持有 {save.Stamina.Current}。");
        if (staminaCost > 0)
        {
            save.Stamina.Current -= staminaCost;
            audit.Write("stamina.spend", save.UserId, new { stageId, cost = staminaCost, left = save.Stamina.Current });
        }

        if (!save.Progress.Stages.TryGetValue(stageId, out PlayerSave.StageRecord? rec))
        {
            rec = new PlayerSave.StageRecord();
            save.Progress.Stages[stageId] = rec;
        }

        rec.ClearCount++;
        rec.Cleared = true;
        rec.LastClearedAt = Now;

        List<string> warnings = [];
        RewardBundle? bundle;
        // 每周首领的 60 原码是**每周一次**，不能只在人生第一次通关发。
        bool weeklyBossFirst = string.Equals(stage.Kind, "boss", StringComparison.Ordinal)
                               && save.Progress.BossFreeUsed == 0;
        bool first = !rec.FirstClearRewarded || weeklyBossFirst;

        if (first)
        {
            rec.FirstClearRewarded = true;
            bundle = stage.Reward.FirstClear;
        }
        else if (stage.Reward.Repeatable)
        {
            bundle = stage.Reward.Repeat;
        }
        else
        {
            save.UpdatedAt = Now;
            audit.Write("stage.clear.repeatRejected", save.UserId, new { stageId });
            return ApiResult.Bad(409, $"'{stageId}' 不可重复刷取（04 §5.1）。");
        }

        if (bundle is null)
        {
            bundle = new RewardBundle();
            warnings.Add("该关卡没有配置奖励（data/stages 的 reward 段为空）。");
        }

        save.Currency.ProtocolCode += bundle.ProtocolCode;
        save.Currency.Credit += bundle.Credit;
        foreach (KeyValuePair<string, int> kv in bundle.Items)
            save.Inventory[kv.Key] = save.Inventory.GetValueOrDefault(kv.Key) + kv.Value;

        // ── 装备掉落（M7） ────────────────────────────────────────────
        // 种子 = (玩家, 关卡, 第几次通关) ⇒ **同一条件下永远掉同一件**。
        // 这不是为了公平，是为了可查：作者本人就是客服，"这件装备哪来的"必须能重放出来。
        List<object> drops = [];
        IReadOnlyList<string> sets = GearRoller.DropSetsFor(data, stage);
        int dropCount = GearRoller.DropCountFor(data, stage, first);
        for (int i = 0; i < dropCount && sets.Count > 0; i++)
        {
            int pick = (int)(HashCode($"{save.UserId}:{stageId}:{rec.ClearCount}:{i}", i + 1) % (ulong)sets.Count);
            string setId = sets[pick];
            string uid = $"g_{save.UserId}_{stageId}_{rec.ClearCount}_{i}";
            if (save.Gears.Any(g => string.Equals(g.Uid, uid, StringComparison.Ordinal))) continue;

            GearPiece? rolled = GearRoller.Roll(data, HashCode(uid, rec.ClearCount), setId, 0, uid);
            if (rolled is null) continue;

            save.Gears.Add(new PlayerSave.GearState
            {
                Uid = rolled.Uid,
                Set = rolled.SetId,
                Part = rolled.Part,
                Level = rolled.Level,
                MainStat = rolled.MainStat,
                SubStats = [.. rolled.SubStats.Select(static s => new PlayerSave.GearSubStat { Stat = s.Stat, Value = s.Value })],
            });
            drops.Add(new { uid, set = setId, part = rolled.Part, mainStat = rolled.MainStat, subCount = rolled.SubStats.Count });
        }

        if (string.Equals(stage.Kind, "boss", StringComparison.Ordinal))
            save.Progress.BossFreeUsed = 1;
        if (save.Progress.DailyDelegateDone < 3)
            save.Progress.DailyDelegateDone++;
        if (save.Progress.WeeklyTaskDone < 10)
            save.Progress.WeeklyTaskDone++;

        save.UpdatedAt = Now;
        audit.Write("stage.clear", save.UserId, new { stageId, first, bundle.ProtocolCode, bundle.Credit, drops = drops.Count });

        return ApiResult.Good(new
        {
            stageId,
            firstClear = first,
            rewarded = true,
            protocolCode = bundle.ProtocolCode,
            credit = bundle.Credit,
            items = bundle.Items,
            gearDrops = drops,
            warnings,
            currency = save.Currency,
        });
    }

    // ── 战斗重放校验（06 §2.2 的 battle/verify） ─────────────────────

    /// <summary>
    /// 客户端提交 `{stageId, seed, choices[], claimedOutcome, claimedLogHash}`，服务端**用同一份 Core 重跑**并比对。
    /// </summary>
    /// <remarks>
    /// <b>为什么不是"信任客户端上报的结果"</b>：`06 §2.3` 把战斗划给客户端跑，
    /// 那"这一局到底赢没赢"就成了客户端说了算 —— 上报 `AllyWin` 就能白拿首通。
    /// 重放校验把这件事收回服务端：**客户端只能提交它实际做出的选择**，
    /// 而"赢了"必须由服务端自己算出来。
    /// <para>
    /// ⚠️ <b>只重放两次是没用的</b>：同一个 `ScriptedPolicy` 跑两遍当然逐行一致 ——
    /// 那只证明了确定性，没有证明"客户端说的结果属实"。所以服务端算完之后，
    /// 必须拿结果去比对**客户端声称的结果与日志指纹**；对不上就是 409。
    /// </para>
    /// </remarks>
    public ApiResult BattleVerify(
        PlayerSave save, string stageId, ulong seed, List<ActionChoice> choices,
        string? claimedOutcome, string? claimedLogHash)
    {
        if (!data.Stages.TryGetValue(stageId, out StageDef? stage))
            return ApiResult.Bad(400, $"关卡 '{stageId}' 不在服务端关卡白名单里。");

        // 教学/剧情关固定面板，不套玩家编队；普通关卡按存档编队重建。
        StageDef runStage = stage;
        if (!stage.FixedPanel && save.Team.Count > 0)
        {
            runStage = stage.Clone();
            runStage.Party = [.. save.Team];
            runStage.PartyModes = new Dictionary<string, string>(save.TeamModes, StringComparer.Ordinal);
        }

        // 客户端这一场用的是**关卡自己的面板口径**（`BattleFactory` 未传 Loadouts）；
        // 服务端重放必须与客户端同参，否则日志必然分叉。养成面板进战斗是 M7 的后续工作，
        // 到那时要**同时**改客户端与服务端的构造参数，不能只改一边。
        Dictionary<string, int> eidolons = new(StringComparer.Ordinal);
        foreach ((string id, PlayerSave.CharacterState st) in save.Characters)
            eidolons[id] = st.Eidolon;

        BattleState run = BattleFactory.Create(new BattleConfig
        {
            Data = data,
            Stage = runStage,
            Seed = seed,
            EidolonRanks = eidolons,
            Loadouts = stage.FixedPanel ? null : LoadoutsFor(save, runStage),
        });
        BattleOutcome outcome = Runner.Run(run, new ScriptedPolicy(choices));
        string hash = HashLog(run.Log);

        string outcomeText = outcome.ToString();
        bool outcomeOk = claimedOutcome is null
                         || string.Equals(claimedOutcome, outcomeText, StringComparison.OrdinalIgnoreCase);
        bool hashOk = claimedLogHash is null
                      || string.Equals(claimedLogHash, hash, StringComparison.Ordinal);

        audit.Write("battle.verify", save.UserId, new { stageId, seed, outcome = outcomeText, outcomeOk, hashOk });

        if (!outcomeOk || !hashOk)
        {
            return ApiResult.Bad(409, "服务端重放的结果与客户端声称的不一致。", new
            {
                stageId,
                seed,
                choices = choices.Count,
                serverOutcome = outcomeText,
                claimedOutcome,
                serverLogHash = hash,
                claimedLogHash,
            });
        }

        return ApiResult.Good(new
        {
            stageId,
            seed,
            choices = choices.Count,
            outcome = outcomeText,
            rounds = run.Round,
            elapsedRounds = Math.Round(run.ElapsedAv / BattleConstants.RoundAv, 3),
            damageToEnemies = Math.Round(run.Metrics.HpRemoved, 1),
            logLines = run.Log.Count,
            logHash = hash,
            verified = true,
        });
    }

    /// <summary>
    /// 日志指纹。**实现只在 `Core` 的 <see cref="LogHash"/> 里有一份** ——
    /// 客户端与服务端各写一份的话，只要差一个分隔符，重放校验就会永远失败，
    /// 而"永远失败"和"永远成功"一样没用。
    /// </summary>
    public static string HashLog(IEnumerable<string> lines) => LogHash.Fnv1a64(lines);

    // ── 抽卡（06 §2.4：必须放服务器） ────────────────────────────────

    /// <summary>
    /// 服务端抽卡。概率 / 保底 / 池子名单全部来自 `data/tables/gacha.json`。
    /// </summary>
    /// <remarks>
    /// 随机源用 <see cref="Rng"/> 并以 `(userId, 抽数)` 播种 —— **可复现**。
    /// 这不是为了好玩：出问题时能拿"这个玩家第 N 抽"重放一遍，
    /// 而"不可复现的随机 + 不可信的客户端"会让任何一次客服申诉都无从查起。
    /// </remarks>
    public ApiResult Gacha(PlayerSave save, string poolId, int count, string currency)
    {
        if (!data.Gacha.Pools.TryGetValue(poolId, out GachaPoolDef? pool))
            return ApiResult.Bad(404, $"卡池 '{poolId}' 不存在。已登记：{string.Join(" / ", data.Gacha.Pools.Keys)}。");

        count = count switch { <= 0 => 1, 1 => 1, 10 => 10, _ => 10 };

        // **先查池子、后扣钱**：池子没配好就把原码扣掉，等于玩家花了钱被告知"出不了货"。
        // 100 戊4 的口径是「不静默降级」，这里连"先扣后退"都不做。
        List<string> missing = [];
        if (pool.Five.Count == 0) missing.Add("5★");
        if (pool.Four.Count == 0) missing.Add("4★");
        if (missing.Count > 0)
        {
            audit.Write("gacha.poolIncomplete", save.UserId, new { poolId, missing });
            return ApiResult.Bad(409, $"卡池 '{poolId}' 的 {string.Join(" / ", missing)} 名单为空 —— 拒绝出货（100 戊4：不静默降级）。",
                new { poolId, missing, hint = "名单是管理员可改项（100 丁4），写在 data/tables/gacha.json 的 pools 段。" });
        }

        int perPull = data.Gacha.Cost.ProtocolCodePerPull;

        // 申请书优先（03 §4.2）：有券用券，没券才扣原码。
        string? ticket = poolId switch
        {
            "limited" => "specialTicket",
            "standard" => "normalTicket",
            _ => null,
        };
        int tickets = ticket switch
        {
            "specialTicket" => save.Currency.SpecialTicket,
            "normalTicket" => save.Currency.NormalTicket,
            _ => 0,
        };

        int useTickets = ticket is null ? 0 : Math.Min(count, tickets);
        int useCode = count - useTickets;
        int needCode = useCode * perPull;
        if (save.Currency.ProtocolCode < needCode)
            return ApiResult.Bad(400, $"协议原码不足：需要 {needCode}，持有 {save.Currency.ProtocolCode}。");

        save.Currency.ProtocolCode -= needCode;
        if (useTickets > 0 && ticket is not null)
        {
            if (ticket == "specialTicket") save.Currency.SpecialTicket -= useTickets;
            else save.Currency.NormalTicket -= useTickets;
        }

        List<object> pulls = [];
        List<string> warnings = [];
        int applicationReceipt = 0;
        int passReceipt = 0;
        for (int i = 0; i < count; i++)
        {
            save.Gacha.TotalPulls++;
            GachaResult r = RollOne(save, poolId, pool, warnings);
            if (r.Error is not null) return ApiResult.Bad(409, r.Error);
            applicationReceipt += r.ApplicationReceipt;
            passReceipt += r.PassReceipt;
            pulls.Add(new
            {
                r.Rarity, r.Id, r.Name, r.IsNew, r.Guaranteed,
                r.ApplicationReceipt, r.PassReceipt,
                pity = PityOf(save, pool),
            });
        }
        save.Currency.Receipt += applicationReceipt;
        save.Currency.PassReceipt += passReceipt;

        save.UpdatedAt = Now;
        audit.Write("gacha", save.UserId, new { poolId, count, source = currency, applicationReceipt, passReceipt });
        return ApiResult.Good(new
        {
            poolId,
            count,
            usedTickets = useTickets,
            usedProtocolCode = needCode,
            receiptGained = new { application = applicationReceipt, pass = passReceipt },
            currency = save.Currency,
            gacha = save.Gacha,
            warnings,
            pulls,
        });
    }

    /// <summary>回执商店：授信 / 申请书 / 材料包 / 权阶与武器覆写许可（03 §3.4）。</summary>
    public ApiResult ShopExchange(PlayerSave save, string itemId, int count, string? targetId = null)
    {
        count = Math.Clamp(count, 1, 99);
        switch (itemId)
        {
            case "eidolonLicense":
            {
                if (string.IsNullOrWhiteSpace(targetId) || !save.Characters.TryGetValue(targetId, out PlayerSave.CharacterState? st))
                    return ApiResult.Bad(404, "权阶升级许可需要指定已拥有的角色。");
                CharacterDef? cd = data.Characters.GetValueOrDefault(targetId);
                if (cd is null || cd.Rarity < 5)
                    return ApiResult.Bad(400, "权阶升级许可只适用于五星角色。");
                if (st.Eidolon >= 6)
                    return ApiResult.Bad(409, $"'{cd.Name}' 已满权阶。");
                int cost = 360 * count;
                if (save.Currency.PassReceipt < cost)
                    return ApiResult.Bad(400, $"通过回执不足：需要 {cost}，持有 {save.Currency.PassReceipt}。");
                save.Currency.PassReceipt -= cost;
                st.Eidolon = Math.Min(6, st.Eidolon + count);
                save.UpdatedAt = Now;
                audit.Write("shop.exchange", save.UserId, new { itemId, targetId, count, cost, eidolon = st.Eidolon });
                return ApiResult.Good(new { itemId, targetId, count, cost, eidolon = st.Eidolon, passReceipt = save.Currency.PassReceipt });
            }
            case "weaponOverwriteLicense":
            {
                if (string.IsNullOrWhiteSpace(targetId) || !save.Weapons.TryGetValue(targetId, out PlayerSave.WeaponState? w))
                    return ApiResult.Bad(404, "武器覆写许可需要指定已拥有的武器。");
                WeaponDef? wd = data.Weapons.GetValueOrDefault(targetId);
                if (wd is null || wd.Rarity < 5)
                    return ApiResult.Bad(400, "武器覆写许可只适用于五星武器。");
                if (w.Overwrite >= 5)
                    return ApiResult.Bad(409, $"'{wd.Name}' 已满覆写。");
                int cost = 280 * count;
                if (save.Currency.PassReceipt < cost)
                    return ApiResult.Bad(400, $"通过回执不足：需要 {cost}，持有 {save.Currency.PassReceipt}。");
                save.Currency.PassReceipt -= cost;
                w.Overwrite = Math.Min(5, w.Overwrite + count);
                save.UpdatedAt = Now;
                audit.Write("shop.exchange", save.UserId, new { itemId, targetId, count, cost, overwrite = w.Overwrite });
                return ApiResult.Good(new { itemId, targetId, count, cost, overwrite = w.Overwrite, passReceipt = save.Currency.PassReceipt });
            }
            case "credit":
            {
                int cost = 60 * count;
                if (save.Currency.Receipt < cost)
                    return ApiResult.Bad(400, $"申请回执不足：需要 {cost}，持有 {save.Currency.Receipt}。");
                save.Currency.Receipt -= cost;
                save.Currency.Credit += 20_000 * count;
                save.UpdatedAt = Now;
                audit.Write("shop.exchange", save.UserId, new { itemId, count, cost });
                return ApiResult.Good(new { itemId, count, cost, credit = save.Currency.Credit, receipt = save.Currency.Receipt });
            }
            case "normalTicket":
            {
                int cost = 60 * count;
                if (save.Currency.Receipt < cost)
                    return ApiResult.Bad(400, $"申请回执不足：需要 {cost}，持有 {save.Currency.Receipt}。");
                save.Currency.Receipt -= cost;
                save.Currency.NormalTicket += count;
                save.UpdatedAt = Now;
                audit.Write("shop.exchange", save.UserId, new { itemId, count, cost });
                return ApiResult.Good(new { itemId, count, cost, normalTicket = save.Currency.NormalTicket, receipt = save.Currency.Receipt });
            }
            case "specialTicket":
            {
                int cost = 150 * count;
                if (save.Currency.PassReceipt < cost)
                    return ApiResult.Bad(400, $"通过回执不足：需要 {cost}，持有 {save.Currency.PassReceipt}。");
                save.Currency.PassReceipt -= cost;
                save.Currency.SpecialTicket += count;
                save.UpdatedAt = Now;
                audit.Write("shop.exchange", save.UserId, new { itemId, count, cost });
                return ApiResult.Good(new { itemId, count, cost, specialTicket = save.Currency.SpecialTicket, passReceipt = save.Currency.PassReceipt });
            }
            case "jumpCore":
            case "hazardSample":
            case "chiefDossier":
            {
                (int costEach, int amountEach, int monthlyLimit) = itemId switch
                {
                    "jumpCore" => (200, 5, 48),
                    "hazardSample" => (300, 4, 16),
                    _ => (100, 20, 10),
                };
                int used = save.Progress.MonthlyPurchases.GetValueOrDefault(itemId);
                if (used + count > monthlyLimit)
                    return ApiResult.Bad(400, $"本月限购：{itemId} 最多 {monthlyLimit} 次，已买 {used} 次。");
                int cost = costEach * count;
                if (save.Currency.Receipt < cost)
                    return ApiResult.Bad(400, $"申请回执不足：需要 {cost}，持有 {save.Currency.Receipt}。");
                save.Currency.Receipt -= cost;
                save.Progress.MonthlyPurchases[itemId] = used + count;
                save.Inventory[itemId] = save.Inventory.GetValueOrDefault(itemId) + amountEach * count;
                save.UpdatedAt = Now;
                audit.Write("shop.exchange", save.UserId, new { itemId, count, cost });
                return ApiResult.Good(new
                {
                    itemId,
                    count,
                    cost,
                    itemCount = save.Inventory[itemId],
                    receipt = save.Currency.Receipt,
                    monthlyUsed = save.Progress.MonthlyPurchases[itemId],
                    monthlyLimit,
                });
            }
            default:
                return ApiResult.Bad(400, $"未知的兑换商品 '{itemId}'。");
        }
    }

    private sealed record GachaResult(int Rarity, string Id, string Name, bool IsNew, bool Guaranteed,
        string? Error = null, int ApplicationReceipt = 0, int PassReceipt = 0);

    private GachaResult RollOne(PlayerSave save, string poolId, GachaPoolDef pool, List<string> warnings)
    {
        GachaRateDef rate = data.Gacha.Rates;
        int pity = PityOf(save, pool);
        int fourPity = save.Gacha.FourPity;

        Rng rng = new(HashCode(save.UserId, save.Gacha.TotalPulls));

        // 五星：硬保底 > 软保底爬坡 > 基础概率。
        double five = rate.Five;
        if (pity + 1 >= rate.HardPity) five = 1.0;
        else if (pity + 1 >= rate.SoftPityStart) five = Math.Min(1.0, rate.Five + (pity + 1 - rate.SoftPityStart + 1) * rate.SoftPityStep);

        int rarity;
        if (rng.Chance(five)) rarity = 5;
        else if (fourPity + 1 >= rate.FourPityHits || rng.Chance(rate.Four)) rarity = 4;
        else rarity = 3;

        List<string> list = rarity switch
        {
            5 => pool.Five,
            4 => pool.Four,
            _ => pool.ThreeWeapons,
        };

        string? fallback = null;
        if (list.Count == 0 && rarity == 3 && pool.Four.Count > 0)
        {
            // 「3★ 武器池为空 → 用 4★ 顶替」是**配置项**，不是静默降级：
            // 100 戊4 禁的是"把 4★ 标成 5★ 还不说"，这里是"稀有度只升不降、且写进响应与审计"。
            list = pool.Four;
            fallback = "3★ 武器池为空，本次以 4★ 顶替（data/tables/gacha.json 的池子配置项）";
            if (!warnings.Contains(fallback, StringComparer.Ordinal)) warnings.Add(fallback);
        }

        if (list.Count == 0)
            return new GachaResult(0, "", "", false, false, $"卡池 '{poolId}' 的 {rarity}★ 名单为空 —— 拒绝出货（100 戊4：不静默降级）。");

        string id = rng.Pick(list);
        string name = LookupName(id);
        bool character = id.StartsWith("char-", StringComparison.Ordinal);
        bool wasMax = character
                      && save.Characters.TryGetValue(id, out PlayerSave.CharacterState? before)
                      && before.Eidolon >= 6;
        bool isNew = Grant(save, rarity, id);

        // 03 §3.3 抽卡副产物：每次固定 6 申请回执；五星/四星角色与武器再按档加。
        int applicationReceipt = 6;
        int passReceipt = 0;
        if (rarity == 5)
        {
            if (character) { applicationReceipt += wasMax ? 120 : 60; passReceipt += wasMax ? 60 : 30; }
            else { applicationReceipt += 36; passReceipt += 12; }
        }
        else if (rarity == 4)
        {
            if (character) { applicationReceipt += wasMax ? 12 : 6; passReceipt += wasMax ? 6 : 3; }
            else { applicationReceipt += 6; passReceipt += 2; }
        }

        // 计数推进。
        if (rarity == 5) SetPity(save, pool, 0);
        else SetPity(save, pool, pity + 1);
        save.Gacha.FourPity = rarity >= 4 ? 0 : fourPity + 1;

        return new GachaResult(rarity, id, name, isNew, pity + 1 >= rate.HardPity,
            null, applicationReceipt, passReceipt);
    }

    private string LookupName(string id)
        => data.Characters.TryGetValue(id, out CharacterDef? c) ? c.Name
         : data.Weapons.TryGetValue(id, out WeaponDef? w) ? w.Name
         : id;

    /// <summary>把出货写进存档。返回"是不是第一次拿到"。</summary>
    private static bool Grant(PlayerSave save, int rarity, string id)
    {
        if (id.StartsWith("char-", StringComparison.Ordinal))
        {
            if (save.Characters.TryGetValue(id, out PlayerSave.CharacterState? st))
            {
                // 重复角色 → 权阶 +1（上限 6）。
                if (st.Eidolon < 6) st.Eidolon++;
                return false;
            }
            save.Characters[id] = new PlayerSave.CharacterState
            {
                Level = 1,
                SkillLevels = new Dictionary<string, int>(StringComparer.Ordinal)
                {
                    ["basic"] = 1, ["resonance"] = 1, ["talent"] = 1, ["ultimate"] = 1,
                },
            };
            return true;
        }

        if (save.Weapons.TryGetValue(id, out PlayerSave.WeaponState? w))
        {
            w.Count++;
            return false;
        }
        save.Weapons[id] = new PlayerSave.WeaponState { Level = 1, Overwrite = 1, Count = 1 };
        _ = rarity;
        return true;
    }

    private static int PityOf(PlayerSave save, GachaPoolDef pool) => pool.PityKey switch
    {
        "standardPity" => save.Gacha.StandardPity,
        "limitedPity" => save.Gacha.LimitedPity,
        "weaponPity" => save.Gacha.WeaponPity,
        _ => save.Gacha.StandardPity,
    };

    private static void SetPity(PlayerSave save, GachaPoolDef pool, int value)
    {
        switch (pool.PityKey)
        {
            case "standardPity": save.Gacha.StandardPity = value; break;
            case "limitedPity": save.Gacha.LimitedPity = value; break;
            case "weaponPity": save.Gacha.WeaponPity = value; break;
        }
    }

    /// <summary>把 `(userId, 抽数)` 混成一个 64 位种子 —— 同一玩家同一抽数永远得到同一次出货。</summary>
    public static ulong HashCode(string userId, int totalPulls)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong h = offset;
        foreach (char c in userId) { h ^= c; h *= prime; }
        h ^= (ulong)totalPulls;
        h *= prime;
        return h == 0 ? 1UL : h;
    }

    // ── 存档写入的范围校验（06 §3.1） ────────────────────────────────

    /// <summary>
    /// 把客户端提交的存档与服务端旧存档逐项比对，**任一不通过即整单拒绝**。
    /// </summary>
    /// <remarks>
    /// ⚠️ 养成（升级 / 突破 / 技能）现在走 `growth` 端点由服务端结算，所以这里的
    /// "只增不减"退化为**兜底**：它挡不住"客户端自己算好再提交"，
    /// 但能挡住"把 1 级角色写成 80 级"这种最粗暴的改档（等级一次不能涨超过 1 级）。
    /// </remarks>
    public ApiResult ValidateSaveWrite(PlayerSave old, PlayerSave incoming)
    {
        List<string> problems = [];

        if (incoming.Currency.ProtocolCode > old.Currency.ProtocolCode) problems.Add("protocolCode 只能减不能增");
        if (incoming.Currency.Credit > old.Currency.Credit) problems.Add("credit 只能减不能增");
        if (incoming.Currency.Receipt > old.Currency.Receipt) problems.Add("receipt 只能减不能增");
        if (incoming.Currency.PassReceipt > old.Currency.PassReceipt) problems.Add("passReceipt 只能减不能增");
        if (incoming.Currency.NormalTicket > old.Currency.NormalTicket) problems.Add("normalTicket 只能减不能增");
        if (incoming.Currency.SpecialTicket > old.Currency.SpecialTicket) problems.Add("specialTicket 只能减不能增");

        if (incoming.Stamina.Current > Math.Min(BattleConstants.StaminaMax, old.Stamina.Current + BattleConstants.StaminaMax))
            problems.Add("stamina.current 暴涨");
        if (incoming.Stamina.LastRegenAt < old.Stamina.LastRegenAt - 60_000) problems.Add("lastRegenAt 回退");

        foreach (KeyValuePair<string, PlayerSave.CharacterState> kv in incoming.Characters)
        {
            if (!old.Characters.TryGetValue(kv.Key, out PlayerSave.CharacterState? before))
            {
                // 新角色只能来自抽卡 / 发放；客户端直接塞一个不认识的 id 一律拒绝。
                if (!data.Characters.ContainsKey(kv.Key)) problems.Add($"角色 '{kv.Key}' 不在 data/characters/ 里");
                continue;
            }
            if (kv.Value.Level < before.Level) problems.Add($"{kv.Key} 等级回退");
            if (kv.Value.Level > before.Level + 1) problems.Add($"{kv.Key} 等级一次涨了 {kv.Value.Level - before.Level} 级");
            if (kv.Value.Level > 80) problems.Add($"{kv.Key} 等级超过 80");
            if (kv.Value.Eidolon < before.Eidolon) problems.Add($"{kv.Key} 权阶回退");
            if (kv.Value.Eidolon > 6) problems.Add($"{kv.Key} 权阶超过 6");
            foreach (KeyValuePair<string, int> s in kv.Value.SkillLevels)
            {
                int was = before.SkillLevels.GetValueOrDefault(s.Key, 1);
                if (s.Value < was) problems.Add($"{kv.Key}.{s.Key} 技能等级回退");
                if (s.Value > 14) problems.Add($"{kv.Key}.{s.Key} 技能等级超过 14");
            }
        }

        foreach (string id in old.Characters.Keys)
        {
            if (!incoming.Characters.ContainsKey(id)) problems.Add($"已有角色 '{id}' 不能被删除");
        }

        if (incoming.Team.Count is 0 or > 4)
            problems.Add("team 必须有 1~4 名角色");
        HashSet<string> teamSeen = new(StringComparer.Ordinal);
        foreach (string id in incoming.Team)
        {
            if (!teamSeen.Add(id)) problems.Add($"team 里有重复角色 '{id}'");
            if (!data.Characters.ContainsKey(id)) problems.Add($"team 角色 '{id}' 不在 data/characters/ 里");
            if (!incoming.Characters.ContainsKey(id)) problems.Add($"team 角色 '{id}' 不在存档角色里");
        }
        int teamLimit = 4;
        string? teamLimiter = null;
        foreach (string id in incoming.Team)
        {
            if (!data.Characters.TryGetValue(id, out CharacterDef? def)) continue;
            if (def.TeamLimit is int lim && lim > 0 && lim < teamLimit)
            {
                teamLimit = lim;
                teamLimiter = def.Name;
            }
        }
        if (incoming.Team.Count > teamLimit)
            problems.Add($"{teamLimiter} 在场时 team 最多 {teamLimit} 人");
        foreach ((string id, string mode) in incoming.TeamModes)
        {
            if (string.IsNullOrWhiteSpace(mode)) { problems.Add($"teamModes[{id}] 为空"); continue; }
            if (!teamSeen.Contains(id)) continue;
            CharacterDef def = data.Character(id);
            if (def.Modes is null || !def.Modes.Entries.ContainsKey(mode))
                problems.Add($"teamModes[{id}]='{mode}' 不是该角色的模态");
        }

        if (!IsSafePlayerName(incoming.Profile.PlayerName)) problems.Add("playerName 含白名单外的字符");

        if (problems.Count > 0)
        {
            audit.Write("game.save.rejected", incoming.UserId, new { problems });
            return ApiResult.Bad(400, "存档校验未通过：" + string.Join("；", problems), new { problems });
        }
        return ApiResult.Good(null!);
    }

    /// <summary>装备/卸下武器。武器类型必须与角色适用类型一致；一把武器只能被一名角色装备。</summary>
    public ApiResult EquipWeapon(PlayerSave save, string characterId, string? weaponId)
    {
        if (!save.Characters.TryGetValue(characterId, out PlayerSave.CharacterState? st))
            return ApiResult.Bad(404, $"存档里没有角色 '{characterId}'。");
        CharacterDef def = data.Character(characterId);

        if (string.IsNullOrWhiteSpace(weaponId))
        {
            if (st.Weapon is not null && save.Weapons.TryGetValue(st.Weapon, out PlayerSave.WeaponState? old))
                old.EquippedBy = null;
            st.Weapon = null;
            save.UpdatedAt = Now;
            audit.Write("weapon.unequip", save.UserId, new { characterId });
            return ApiResult.Good(new { characterId, weaponId = (string?)null });
        }

        if (!save.Weapons.TryGetValue(weaponId, out PlayerSave.WeaponState? owned))
            return ApiResult.Bad(404, $"存档里没有武器 '{weaponId}'。");
        if (!data.Weapons.TryGetValue(weaponId, out WeaponDef? wdef))
            return ApiResult.Bad(404, $"data/weapons/ 里没有 '{weaponId}'。");
        if (!string.Equals(wdef.WeaponType, def.WeaponType, StringComparison.OrdinalIgnoreCase))
            return ApiResult.Bad(400, $"武器类型 '{wdef.WeaponType}' 与角色适用类型 '{def.WeaponType}' 不一致。");

        // 一把武器只能跟着一个人：先从其他人身上卸下。
        foreach ((string id, PlayerSave.CharacterState other) in save.Characters)
        {
            if (id != characterId && string.Equals(other.Weapon, weaponId, StringComparison.Ordinal))
                other.Weapon = null;
        }
        if (st.Weapon is not null && st.Weapon != weaponId
            && save.Weapons.TryGetValue(st.Weapon, out PlayerSave.WeaponState? previous))
        {
            previous.EquippedBy = null;
        }

        st.Weapon = weaponId;
        owned.EquippedBy = characterId;
        save.UpdatedAt = Now;
        audit.Write("weapon.equip", save.UserId, new { characterId, weaponId });
        return ApiResult.Good(new { characterId, weaponId, level = owned.Level, overwrite = owned.Overwrite });
    }

    /// <summary>设置编队：1~4 名、不可重复、必须已拥有。</summary>
    public ApiResult SetTeam(PlayerSave save, List<string>? team, Dictionary<string, string>? modes = null)
    {
        if (team is null || team.Count == 0)
            return ApiResult.Bad(400, "编队至少要有 1 名角色。");
        if (team.Count > 4)
            return ApiResult.Bad(400, "编队最多 4 名角色。");

        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string id in team)
        {
            if (string.IsNullOrWhiteSpace(id))
                return ApiResult.Bad(400, "编队里有空角色 id。");
            if (!seen.Add(id))
                return ApiResult.Bad(400, $"编队里有重复角色 '{id}'。");
            if (!data.Characters.ContainsKey(id))
                return ApiResult.Bad(404, $"角色 '{id}' 不在 data/characters 里。");
            if (!save.Characters.ContainsKey(id))
                return ApiResult.Bad(403, $"还没有角色 '{id}'。");
        }

        // 角色自带的编队上限（薇拉「孤胆英雄」：有她在场时最多 2 人）。
        int allowed = 4;
        string? limiter = null;
        foreach (string id in team)
        {
            CharacterDef def = data.Character(id);
            if (def.TeamLimit is int lim && lim > 0 && lim < allowed)
            {
                allowed = lim;
                limiter = def.Name;
            }
        }
        if (team.Count > allowed)
            return ApiResult.Bad(400, $"{limiter} 在场时队伍最多 {allowed} 人。");

        Dictionary<string, string> nextModes = new(save.TeamModes, StringComparer.Ordinal);
        foreach ((string id, string mode) in modes ?? [])        {
            if (string.IsNullOrWhiteSpace(mode))
                return ApiResult.Bad(400, $"角色 '{id}' 的模态不能为空。");
            if (!seen.Contains(id)) continue;
            CharacterDef def = data.Character(id);
            if (def.Modes is null || !def.Modes.Entries.ContainsKey(mode))
                return ApiResult.Bad(400, $"角色 '{id}' 没有模态 '{mode}'。");
            nextModes[id] = mode;
        }

        save.Team = [.. team];
        save.TeamModes = nextModes;
        save.UpdatedAt = Now;
        audit.Write("team.set", save.UserId, new { team = save.Team, modes = save.TeamModes });
        return ApiResult.Good(new { team = save.Team, modes = save.TeamModes });
    }

    /// <summary>
    /// 写主角档案（性别 / 主角名）。
    /// </summary>
    /// <remarks>
    /// 规则放在 <see cref="GameService"/> 而不是路由里，是为了让自检能直接调它 ——
    /// "路由里内联一段校验"是这类项目里最常见的漏测点。
    /// </remarks>
    public ApiResult SetProfile(PlayerSave save, string? gender, string? playerName, long nowMs,
                               Dictionary<string, string>? skins = null)
    {
        if (gender is null && playerName is null && (skins is null || skins.Count == 0))
            return ApiResult.Bad(400, "没有要改的字段。");

        if (gender is not null)
        {
            if (gender is not ("male" or "female")) return ApiResult.Bad(400, "protagonistGender 只能是 male / female。");
            save.Profile.ProtagonistGender = gender;
            save.Profile.LastGenderSwitchAt = nowMs;
            audit.Write("profile.gender", save.UserId, new { gender });
        }

        if (playerName is not null)
        {
            string clean = playerName.Trim();
            if (!IsSafePlayerName(clean))
                return ApiResult.Bad(400, "主角名不符合白名单（06 §3.1；该字段会渲染进后台 HTML）。");
            save.Profile.PlayerName = clean;
            audit.Write("profile.name", save.UserId, new { name = clean });
        }

        // 衣装选择：**只记"选了哪一套"**，不决定正式流程里显示哪一张（`04 §3.2`）。
        if (skins is not null)
        {
            foreach (KeyValuePair<string, string> kv in skins)
            {
                if (!data.Characters.ContainsKey(kv.Key))
                    return ApiResult.Bad(400, $"没有角色 '{kv.Key}'。");
                save.Profile.Skins[kv.Key] = kv.Value;
            }
            audit.Write("profile.skin", save.UserId, new { count = skins.Count });
        }

        save.UpdatedAt = nowMs;
        return ApiResult.Good(new { save.Profile.ProtagonistGender, save.Profile.PlayerName, skins = save.Profile.Skins });
    }

    /// <summary>主角名白名单（`06 §3.1`）。**这条是 XSS 防线**：该字段会渲染进管理员后台 HTML。</summary>
    public static bool IsSafePlayerName(string name)
    {
        if (name.Length is 0 or > 24) return false;
        foreach (char c in name)
        {
            if (char.IsLetterOrDigit(c)) continue;
            if (c is '_' or '-' or ' ') continue;
            return false;
        }
        return true;
    }
}
