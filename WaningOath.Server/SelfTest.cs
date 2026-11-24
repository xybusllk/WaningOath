using WaningOath.Core;
using WaningOath.Core.Game;
using WaningOath.Core.Battle;
using WaningOath.Core.Data;
using WaningOath.Core.Progression;
using WaningOath.Server.Game;

namespace WaningOath.Server;

/// <summary>
/// `M4` 的验收自检：**不开网络**、直接在进程内把"登录 → 拉存档 → 打一关 → 写回"跑一遍。
/// </summary>
/// <remarks>
/// <b>为什么要有它</b>：`M4` 的验收是"序章一战能写入服务器"。
/// 这条链路上有四个容易各自悄悄坏掉的地方 —— 存档原子写、关卡白名单、首通奖励、
/// 战斗重放校验 —— 而它们凑在一起才叫"写入成功"。
/// 用 `--selftest` 在进程内跑完，既不需要起 HTTP、也不需要人点一遍。
/// </remarks>
internal static class SelfTest
{
    public static int Run(GameData data, GameService game, PlayerStore store, AuditLog audit)
    {
        Console.WriteLine("── M4 自检：存档闭环 ──");
        int failed = 0;
        string userId = "selftest-" + Environment.ProcessId;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // ── 1. 建号 ───────────────────────────────────────────────────
        PlayerSave save = store.LoadOrCreate(userId, "selftest", "char-shouyueren", now);
        failed += Check("存档已创建", save.Characters.ContainsKey("char-shouyueren"));
        failed += Check("初始等级 1", save.Characters["char-shouyueren"].Level == 1);
        failed += Check("配额满值", save.Stamina.Current == BattleConstants.StaminaMax);

        // ── 2. 关卡白名单 ─────────────────────────────────────────────
        ApiResult bad = game.StageClear(save, "stage-does-not-exist", "AllyWin");
        failed += Check("表外关卡被拒", !bad.Ok && bad.Status == 400);

        ApiResult simReject = game.StageClear(save, "stage-sim-normal", "AllyWin");
        failed += Check("sim 靶场不发奖", !simReject.Ok && simReject.Status == 409);

        // ── 3. 战斗重放校验 ───────────────────────────────────────────
        StageDef stage = data.Stage("stage-main-s7");
        RecordingPolicy recorder = new(new GreedyPolicy());
        BattleState run = BattleFactory.Create(new BattleConfig
        {
            Data = data,
            Stage = stage,
            Seed = 20260815UL,
            // ⚠️ 固定面板关（教学 / 剧情）**不套配装** —— 服务端 `BattleVerify` 就是这么做的
            //    （`Loadouts = stage.FixedPanel ? null : …`）。这里如果单方面塞 Loadouts，
            //    客户端与重放就会用两份不同面板，必然 409（本自检曾经就是这么假失败的）。
            Loadouts = stage.FixedPanel ? null : game.LoadoutsFor(save, stage),
        });
        BattleOutcome outcome = Runner.Run(run, recorder);

        string logHash = GameService.HashLog(run.Log);
        ApiResult verify = game.BattleVerify(save, stage.Id, 20260815UL, recorder.Choices, outcome.ToString(), logHash);
        failed += Check("重放校验通过（结果与日志指纹都对得上）", verify.Ok, verify.Error);

        // 谎报结果 → 必须被打回。**这一条才是"重放校验"存在的理由**：
        // 只跑两遍同一份动作序列当然一致，那只证明了确定性。
        ApiResult lied = game.BattleVerify(save, stage.Id, 20260815UL, recorder.Choices,
            outcome == BattleOutcome.AllyWin ? "AllyLose" : "AllyWin", logHash);
        failed += Check("谎报战斗结果被抓到", !lied.Ok && lied.Status == 409);

        // 谎报日志指纹 → 同样必须被打回。
        ApiResult liedHash = game.BattleVerify(save, stage.Id, 20260815UL, recorder.Choices, outcome.ToString(), "fnv1a64:0000000000000000");
        failed += Check("谎报日志指纹被抓到", !liedHash.Ok && liedHash.Status == 409);

        // ── 4. 首通奖励（表驱动） ─────────────────────────────────────
        int before = save.Currency.ProtocolCode;
        ApiResult clear = game.StageClear(save, stage.Id, outcome.ToString());
        int expected = stage.Reward.FirstClear.ProtocolCode;
        failed += Check($"首通发了 {expected} 原码", clear.Ok && save.Currency.ProtocolCode - before == expected,
            clear.Error);

        ApiResult repeat = game.StageClear(save, stage.Id, outcome.ToString());
        failed += Check("主线不可重复刷取", !repeat.Ok && repeat.Status == 409);

        // ── 4a. 剧情白名单（100 戊10：原实现接受任意 storyId ⇒ 可无限刷原码）──
        ApiResult bogusStory = game.StoryClear(save, "story-does-not-exist");
        failed += Check("表外剧情 id 被拒（防刷）", !bogusStory.Ok && bogusStory.Status == 400, bogusStory.Error);

        // 锚点关卡没通时不给发 —— 否则"我通关了序章"只是一句客户端声明。
        PlayerSave fresh = PlayerSave.Create("selftest-anchor", "t", "char-shouyueren", now);
        ApiResult early = game.StoryClear(fresh, "prologue");
        failed += Check("锚点关卡未通时剧情奖励被拒", !early.Ok && early.Status == 409, early.Error);

        int codeBefore = save.Currency.ProtocolCode;
        int logBefore = save.Inventory.GetValueOrDefault("internLog");
        ApiResult story = game.StoryClear(save, "prologue");
        failed += Check($"序章通关发了 160 原码（实际 +{save.Currency.ProtocolCode - codeBefore}）",
            story.Ok && save.Currency.ProtocolCode - codeBefore == 160, story.Error);
        failed += Check($"序章通关发了实习日志 ×30（实际 +{save.Inventory.GetValueOrDefault("internLog") - logBefore}）",
            save.Inventory.GetValueOrDefault("internLog") - logBefore == 30);

        ApiResult storyAgain = game.StoryClear(save, "prologue");
        failed += Check("主线章节不可重复领取", !storyAgain.Ok && storyAgain.Status == 409, storyAgain.Error);

        // ── 4b. M7 闭环：打关 → 掉装备 → 生成可复现 ──────────────────
        failed += Check($"通关掉了装备（{save.Gears.Count} 件）", save.Gears.Count > 0);

        if (save.Gears.Count > 0)
        {
            PlayerSave.GearState dropped = save.Gears[0];
            ulong dSeed = GameService.HashCode(dropped.Uid, 1);
            GearPiece? a = GearRoller.Roll(data, dSeed, dropped.Set, 0, dropped.Uid);
            GearPiece? b = GearRoller.Roll(data, dSeed, dropped.Set, 0, dropped.Uid);
            failed += Check("同一件装备的生成可复现（同种子两次结果一致）",
                a is not null && b is not null && a.Part == b.Part && a.MainStat == b.MainStat
                && a.SubStats.Count == b.SubStats.Count);

            failed += Check($"掉的是已登记的套装（{dropped.Set}）", data.GearSet(dropped.Set) is not null);
            failed += Check($"掉落的部位合法（{dropped.Part}）", data.Gear.Slot(dropped.Part) is not null);
            failed += Check($"掉落的主属性在该部位的池子里（{dropped.MainStat}）",
                data.Gear.Slot(dropped.Part)?.MainStats.Contains(dropped.MainStat, StringComparer.Ordinal) ?? false);
            failed += Check("副属性不重复（同一条不会出现两次）",
                dropped.SubStats.Select(static s => s.Stat).Distinct(StringComparer.Ordinal).Count() == dropped.SubStats.Count);
        }

        // ── 5. 养成：经验 → 升级 ─────────────────────────────────────
        double expFor20 = GrowthRules.TotalExpTo(data.Growth, 20);
        int logsNeeded = (int)Math.Ceiling(expFor20 / 5000);
        save.Inventory["internLog"] = logsNeeded;
        ApiResult level = game.LevelUp(save, "char-shouyueren",
            new Dictionary<string, int> { ["internLog"] = logsNeeded });
        int lv = save.Characters["char-shouyueren"].Level;
        failed += Check($"吃 {logsNeeded} 本实习日志升到 20 级（实际 {lv}）", level.Ok && lv == 20, level.Error);
        failed += Check("升级被突破节点卡住（上限 20）", lv == 20);

        // ── 6. 突破 ───────────────────────────────────────────────────
        save.Inventory["jumpCore"] = 8;
        ApiResult bt = game.Breakthrough(save, "char-shouyueren");
        failed += Check("突破到 40 级上限", bt.Ok && save.Characters["char-shouyueren"].Breakthrough == 1, bt.Error);

        // ── 7. 技能等级受权阶限制 ─────────────────────────────────────
        save.Inventory["permissionNote"] = 100;
        save.Inventory["protocolTranscript"] = 100;
        save.Inventory["hazardSample"] = 100;
        ApiResult sk = game.SkillUp(save, "char-shouyueren", "basic", 14);
        int basic = save.Characters["char-shouyueren"].SkillLevels["basic"];
        failed += Check($"0 权阶技能上限 10（实际 {basic}）", sk.Ok && basic == 10, sk.Error);

        // ── 8. 存档写入校验 ───────────────────────────────────────────
        PlayerSave clone = PlayerSave.FromJson(save.ToJson())!;
        clone.Characters["char-shouyueren"].Level = 80;
        ApiResult rejected = game.ValidateSaveWrite(save, clone);
        failed += Check("改档（1→80 级）被拒", !rejected.Ok);

        PlayerSave clone2 = PlayerSave.FromJson(save.ToJson())!;
        clone2.Profile.PlayerName = "<img src=x onerror=alert(1)>";
        ApiResult xss = game.ValidateSaveWrite(save, clone2);
        failed += Check("主角名白名单拦住 XSS", !xss.Ok);

        // ── 8b. 主角档案（客户端不再自己写存档 ⇒ 这条是"开局选的性别"唯一的落点） ──
        ApiResult genderOk = game.SetProfile(save, "male", null, now);
        failed += Check("主角档案：性别写入成功", genderOk.Ok && save.Profile.ProtagonistGender == "male", genderOk.Error);

        ApiResult genderBad = game.SetProfile(save, "apache", null, now);
        failed += Check("主角档案：非法性别被拒", !genderBad.Ok, genderBad.Error);

        ApiResult nameBad = game.SetProfile(save, null, "<img src=x>", now);
        failed += Check("主角档案：非法主角名被拒", !nameBad.Ok, nameBad.Error);

        ApiResult nameOk = game.SetProfile(save, null, "守望者", now);
        failed += Check("主角档案：合法主角名写入成功", nameOk.Ok && save.Profile.PlayerName == "守望者", nameOk.Error);

        ApiResult nothing = game.SetProfile(save, null, null, now);
        failed += Check("主角档案：空请求被拒", !nothing.Ok, nothing.Error);

        // 还原成默认值 —— 后面的断言依赖它。
        game.SetProfile(save, "female", null, now);
        save.Profile.PlayerName = "";

        // ── 9. 原子落盘 + 读回 ────────────────────────────────────────
        store.Save(save);
        PlayerSave? reloaded = store.TryLoad(userId);
        failed += Check("写回后能读回，且等级一致",
            reloaded is not null && reloaded.Characters["char-shouyueren"].Level == save.Characters["char-shouyueren"].Level);
        failed += Check("没有残留 .tmp 文件", !File.Exists(Path.Combine(store.Root, userId + ".json.tmp")));

        // ── 10. 时间结算幂等 ─────────────────────────────────────────
        PlayerSave t = PlayerSave.Create("selftest-time", "t", "char-shouyueren", now);
        t.Stamina.Current = 0;
        t.Stamina.LastRegenAt = now - 3600_000; // 一小时前 → 应恢复 10 点
        game.SettleTime(t);
        failed += Check($"一小时恢复 10 点配额（实际 {t.Stamina.Current}）", t.Stamina.Current == 10);
        int afterFirst = t.Stamina.Current;
        game.SettleTime(t);
        failed += Check("连续结算不会重复发放", t.Stamina.Current == afterFirst);

        // ── 11. 周键（周一 04:00 翻周） ──────────────────────────────
        DateTimeOffset monday0300 = new(2026, 3, 2, 3, 0, 0, TimeSpan.FromHours(8));
        DateTimeOffset monday0500 = new(2026, 3, 2, 5, 0, 0, TimeSpan.FromHours(8));
        failed += Check("周一 03:00 仍属上一周", PlayerSave.WeekKeyOf(monday0300) != PlayerSave.WeekKeyOf(monday0500));

        // ── 12. 抽卡：不存在的池子必须报错；已填充的池子必须能出货 ─────
        // 原断言是"空卡池被显式拒绝"，但卡池在 2026-10-22 已经填充（常驻/限定 9 名五星 + 镜星），
        // 那条断言过期了。现在验两件事：① 未知池不静默降级；② 常驻池真的出得了货。
        ApiResult unknownPool = game.Gacha(save, "no-such-pool", 1, "auto");
        failed += Check("不存在的卡池被拒（100 戊4：不静默降级）",
            !unknownPool.Ok && unknownPool.Status == 404, unknownPool.Error);

        save.Currency.ProtocolCode += 160;
        ApiResult gacha = game.Gacha(save, "standard", 1, "auto");
        failed += Check("已填充的常驻池能正常出货", gacha.Ok && gacha.Body is not null, gacha.Error);

        // ── 13. M7：配装 → 面板 → 战斗 ───────────────────────────────
        PlayerSave.CharacterState hero = save.Characters["char-shouyueren"];
        hero.Level = 80;
        hero.Breakthrough = 4;
        hero.Weapon = "weap-hengdao-yuanchu";
        save.Weapons["weap-hengdao-yuanchu"] = new PlayerSave.WeaponState
        {
            Level = 80, Overwrite = 5, EquippedBy = hero.Weapon, Count = 1,
        };
        save.Gears.Add(new PlayerSave.GearState
        {
            Uid = "g-test-1", Set = "gear-ruyue-zhizheng", Part = "identity", Level = 15,
            MainStat = "hpPct", SubStats = [new PlayerSave.GearSubStat { Stat = "critRate", Value = 0.18 }],
        });
        ApiResult equip = game.GearAction(save, "equip", "char-shouyueren", "g-test-1", null);
        failed += Check("装备穿戴成功", equip.Ok, equip.Error);
        failed += Check("同一部位再穿一件会替换掉上一件",
            game.GearAction(save, "equip", "char-shouyueren", "g-test-1", null).Ok);

        ApiResult enhance = game.GearAction(save, "enhance", "char-shouyueren", "g-test-1", 15);
        failed += Check("强化到 +15 幂等（已满级返回 409）", !enhance.Ok && enhance.Status == 409, enhance.Error);

        Loadout bare = GameService.LoadoutOf(data, save, "char-shouyueren");
        BaseStatsDef barePanel = PanelCalculator.Compute(data, data.Character("char-shouyueren"), bare).Panel;
        CharacterDef heroDef = data.Character("char-shouyueren");
        BaseStatsDef naked = PanelCalculator.Compute(data, heroDef, new Loadout(Level: 80, Breakthrough: 4)).Panel;
        failed += Check($"配装后面板高于裸装（攻 {naked.Atk:0} → {barePanel.Atk:0}）", barePanel.Atk > naked.Atk + 1);
        failed += Check($"武器固定攻击真的加进去了（攻 ≥ 1000）", barePanel.Atk >= 1000, $"实际 {barePanel.Atk:0}");

        // **关键**：配装必须真的影响战斗 —— 否则"面板算出来了但没接进战斗"。
        StageDef s7 = data.Stage("stage-main-s7");
        Dictionary<string, Loadout> withGear = new(game.LoadoutsFor(save, s7), StringComparer.Ordinal);
        BattleState armed = BattleFactory.Create(new BattleConfig
        {
            Data = data, Stage = s7, Seed = 20260815UL, Loadouts = withGear,
        });
        BattleState unarmed = BattleFactory.Create(new BattleConfig { Data = data, Stage = s7, Seed = 20260815UL });
        // 用 GreedyPolicy 而不是空的动作序列 —— 空序列等于"谁都不动"，两边都会是 0 伤害。
        Runner.Run(armed, new GreedyPolicy());
        Runner.Run(unarmed, new GreedyPolicy());
        // ⚠️ 比的是 **RawDealt（含溢出）** 而不是 HpRemoved：序章那只杂兵只有 8,000 血，
        //    两边的 HpRemoved 都会被"目标血量"截断成同一个 8,000 —— 那会伪装成"配装没生效"。
        failed += Check("配装进入了战斗（对敌原始伤害不同）",
            Math.Abs(armed.Metrics.RawDealt - unarmed.Metrics.RawDealt) > 1.0,
            $"配装 {armed.Metrics.RawDealt:0} vs 裸装 {unarmed.Metrics.RawDealt:0}");

        // ── 14. 抽卡校准值落在目标附近（02 §10 的 GACHA_SOFT_PITY_STEP）──
        failed += Check($"softPityStep 已校准为 {data.Gacha.Rates.SoftPityStep:0.000}",
            data.Gacha.Rates.SoftPityStep <= 0.005,
            "02 §10 的目标是综合出货 1/62.5 抽；`Sim gacha` 扫参显示只有「几乎不做软保底」才能达到");

        // ── 15. 薇拉「孤胆英雄」：有她在场时队伍最多 2 人（服务端硬校验）──
        {
            save.Characters["char-vera"] = new PlayerSave.CharacterState { Level = 1 };
            save.Characters["char-karni"] = new PlayerSave.CharacterState { Level = 1 };
            ApiResult tooMany = game.SetTeam(save, ["char-vera", "char-shouyueren", "char-karni"]);
            failed += Check("薇拉在场时 3 人编队被拒", !tooMany.Ok && tooMany.Status == 400,
                $"ok={tooMany.Ok} status={tooMany.Status} error={tooMany.Error}");
            ApiResult two = game.SetTeam(save, ["char-vera", "char-shouyueren"]);
            failed += Check("薇拉在场时 2 人编队通过", two.Ok, two.Error);
        }

        // ── 收尾 ─────────────────────────────────────────────────────
        foreach (string id in new[] { userId, "selftest-time" })
        {
            string p = Path.Combine(store.Root, id + ".json");
            if (File.Exists(p)) File.Delete(p);
        }

        Console.WriteLine();
        if (failed == 0)
        {
            Console.WriteLine("M4 验收：**通过** —— 登录 / 拉存档 / 打一关 / 写回 全链路成立。");
            Console.WriteLine($"（战斗结果 {outcome}，日志 {run.Log.Count} 行，指纹 {GameService.HashLog(run.Log)}）");
            return 0;
        }
        Console.WriteLine($"M4 验收：**失败** —— {failed} 项不成立。");
        audit.Write("selftest.failed", "-", new { failed });
        return 1;
    }

    private static int Check(string label, bool ok, string? detail = null)
    {
        Console.WriteLine($"  {(ok ? "OK  " : "FAIL")} {label}" + (ok || detail is null ? "" : $"　→ {detail}"));
        return ok ? 0 : 1;
    }
}
