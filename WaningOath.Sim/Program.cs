using WaningOath.Core;
using WaningOath.Core.Battle;
using WaningOath.Core.Data;
using WaningOath.Core.Progression;

namespace WaningOath.Sim;

/// <summary>
/// 模拟器入口。
/// </summary>
/// <remarks>
/// <b>M1 的验收</b>（`06 §8.2`）：
/// <list type="number">
///   <item><c>battle</c> 打出"守约人 vs 犬型异常"的完整日志。</item>
///   <item><c>verify</c> 的 `02 §9` 伤害管线验算数值吻合。</item>
///   <item><c>verify</c> 的"同一种子 + 同一动作序列 → 重放结果一致"通过。</item>
/// </list>
/// </remarks>
internal static class Program
{
    private const string Stage0 = "M1 · 战斗内核 ＋ M3 · 数据校验器";

    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        string command = args.Length > 0 && !args[0].StartsWith('-') ? args[0] : "";
        Dictionary<string, string> opt = ParseOptions(args);

        try
        {
            return command switch
            {
                "" => SelfCheck(),
                "battle" => Battle(opt),
                "manual" => ManualBattle(opt),
                "verify" => Verify(opt),
                "validate" => Validate(opt),
                "batch" => Batch(opt),
                "loadout" => Loadout(opt),
                "gacha" => Gacha(opt),
                "-h" or "--help" or "help" => Usage(),
                _ => Unknown(command),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"**错误**：{ex.GetType().Name}: {ex.Message}");
            if (opt.ContainsKey("--stack")) Console.Error.WriteLine(ex.StackTrace);
            return 2;
        }
    }

    // ── 子命令 ────────────────────────────────────────────────────────

    private static int SelfCheck()
    {
        Banner();
        return DeterminismSelfCheck() ? 0 : 1;
    }

    private static int Usage()
    {
        Console.WriteLine("""
            用法: dotsim <子命令> [选项]      （经 tools\run-sim.cmd 调用）

               (无参数)                 工具链 + 确定性 RNG 自检
               battle                   跑一场战斗并打印完整日志
               manual                   手动战斗路径自检（逐拍提交指令，日志须与自动跑完全一致）
               verify                   02 §9 伤害管线验算 + 重放一致性
               validate                 数据校验器（M3 · 05 §7 的 21 条 + M3 增补）**0 错误才算通过**
               batch [--runs N]          **M2 分档批量模拟**：四档投入 × 四个靶场 × N 个种子
                                        输出 DPR / 通关轮数 / 伤害构成 / 紊乱频率，并判定 02 §8.3 硬约束
               loadout                   **M7 配装折算**：把"60 级裸装 → 大毕业"的**真实配装**折成面板
                                        并与 02 §7 的锚点逐项对照（这是 M2 档位插值的替代品）
               gacha [--sim N]           **抽卡保底校准**：扫 GACHA_SOFT_PITY_STEP 找"综合出货 1/62.5 抽"

            选项:
               --seed N                 战斗种子（默认 20260815）
               --stage <id>             关卡 id（默认 stage-main-s7）
               --party id1,id2,...      覆盖关卡固定队伍（验收新角色用，最多 4 人）
               --eidolon N              给全队套 N 权阶（0~6，验证权阶进战斗）
               --tier <id>              batch 只跑某个投入档位（默认四档全跑）
               --loadout <id>           **给全队套标准配装**（naked / low / mid / graduate）—— M7 的真实面板 + 装备效果
               --tutorial               走**教学引导**（04 §3.4 的五步脚本），默认走自由策略
               --data <dir>             **换一份 data/ 树**（故障注入测试用；默认自动向上找）
               --hp-mult X              敌人血量倍率。**调试用**，把杂兵战拉长到能观察异常 / 紊乱
               --skill-level N          覆盖技能等级（默认取关卡数据）
               --mode <id>               指定升格模态（双模态角色；见 01 §5.4）
               --overdraw               允许共鸣指数透支
               --out <path>             把日志写到文件
               --json <path>            validate 的报告另存一份机器可读 JSON（故障注入测试用）
               --quiet                  抑制提示级输出（validate）
               --stack                  出错时打印调用栈

            环境: 先跑 tools\env.cmd（.NET SDK 在工作目录 .dotnet\ 内，全局没有）。
            """);
        return 0;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"未知子命令：'{command}'。用 --help 看用法。");
        return 2;
    }

    // ── battle ────────────────────────────────────────────────────────

    private static int Battle(Dictionary<string, string> opt)
    {
        Banner();
        GameData data = LoadData(opt);
        StageDef stage = data.Stage(opt.GetValueOrDefault("--stage", "stage-main-s7"));
        ApplyPartyOverride(stage, opt);

        ulong seed = ulong.TryParse(opt.GetValueOrDefault("--seed"), out ulong s) ? s : 20260815UL;
        double hpMult = double.TryParse(opt.GetValueOrDefault("--hp-mult"),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double h) ? h : 1.0;
        int? skillLevel = int.TryParse(opt.GetValueOrDefault("--skill-level"), out int sl) ? sl : null;

        // 升格模态的战前选定（01 §5.4）：`--mode <id>` 只对"拥有该模态"的上场角色生效。
        // 它是**编队决策**，所以写在关卡/队伍上，而不是角色数据里。
        if (opt.TryGetValue("--mode", out string? forcedMode) && !string.IsNullOrEmpty(forcedMode))
        {
            foreach (string pid in stage.Party)
            {
                CharacterDef cd = data.Character(pid);
                if (cd.Modes is not null && cd.Modes.Entries.ContainsKey(forcedMode))
                {
                    stage.PartyModes[pid] = forcedMode;
                    Console.WriteLine($"模态覆盖：{cd.Name} → {forcedMode}（{cd.Modes.Entries[forcedMode].Name}）");
                }
            }
        }
        BattleState state = BattleFactory.Create(new BattleConfig
        {
            Data = data,
            Stage = stage,
            Seed = seed,
            EnemyHpMultiplier = hpMult,
            SkillLevelOverride = skillLevel,
            AllowOverdraw = opt.ContainsKey("--overdraw"),
            Tier = ResolveTier(data, opt),
            Loadouts = ResolveLoadouts(data, stage, opt),
            EidolonRanks = ResolveEidolonRanks(stage, opt),
        });

        PrintSetup(state, stage, seed, hpMult, skillLevel, opt);

        // `--tutorial` 走教学引导（`04 §3.4` 的五步脚本），默认走自由策略。
        // 这一条让"教学关能不能走通"变成一条能跑的验收，而不是只能靠人在界面上点。
        IActionPolicy policy = opt.ContainsKey("--tutorial")
            ? new TutorialPolicy("char-shouyueren", "char-qianyu-xingyao")
            : new GreedyPolicy { AllowOverdraw = opt.ContainsKey("--overdraw") };
        if (policy is TutorialPolicy tp) Console.WriteLine($"教学引导 : 已启用（{tp.Guidance}）");
        BattleOutcome outcome = Runner.Run(state, policy);
        if (policy is TutorialPolicy done) Console.WriteLine($"教学进度 : 走到第 {done.Step} 步");

        Console.WriteLine();
        Console.WriteLine("──────────────── 战斗日志 ────────────────");
        foreach (string line in state.Log) Console.WriteLine("  " + line);
        Console.WriteLine("──────────────── 日志结束 ────────────────");
        Console.WriteLine();

        PrintSummary(state, outcome);

        if (opt.TryGetValue("--out", out string? outPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath)) ?? ".");
            List<string> text = [$"# {stage.Name}  seed={seed}  hpMult={hpMult}  outcome={outcome}"];
            text.AddRange(state.Log);
            File.WriteAllLines(outPath, text);
            Console.WriteLine($"日志已写入：{outPath}");
        }

        return outcome == BattleOutcome.AllyWin ? 0 : 1;
    }

    /// <summary>
    /// 手动战斗路径自检：同一份种子/配装下，逐拍由 <see cref="ManualPolicy"/> 提交指令，
    /// 日志必须与 <see cref="GreedyPolicy"/> 整场自动跑完全一致。
    /// </summary>
    /// <remarks>
    /// 这验证的是"手动操作没有绕过任何规则"：界面只是替玩家把 <see cref="ActionChoice"/> 放进策略，
    /// 真正的时钟、伤害、异常、AI 仍然全在 <see cref="Runner"/> 里。日志逐行相同 ⇒ 两条路同构。
    /// </remarks>
    private static int ManualBattle(Dictionary<string, string> opt)
    {
        Banner();
        GameData data = LoadData(opt);
        StageDef stage = data.Stage(opt.GetValueOrDefault("--stage", "stage-main-s7"));
        ApplyPartyOverride(stage, opt);
        ulong seed = ulong.TryParse(opt.GetValueOrDefault("--seed"), out ulong s) ? s : 20260815UL;
        double hpMult = double.TryParse(opt.GetValueOrDefault("--hp-mult"),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double h) ? h : 1.0;
        int? skillLevel = int.TryParse(opt.GetValueOrDefault("--skill-level"), out int sl) ? sl : null;

        if (opt.TryGetValue("--mode", out string? forcedMode) && !string.IsNullOrEmpty(forcedMode))
        {
            foreach (string pid in stage.Party)
            {
                CharacterDef cd = data.Character(pid);
                if (cd.Modes is not null && cd.Modes.Entries.ContainsKey(forcedMode))
                    stage.PartyModes[pid] = forcedMode;
            }
        }

        BattleConfig Config() => new()
        {
            Data = data,
            Stage = stage,
            Seed = seed,
            EnemyHpMultiplier = hpMult,
            SkillLevelOverride = skillLevel,
            AllowOverdraw = opt.ContainsKey("--overdraw"),
            Tier = ResolveTier(data, opt),
            Loadouts = ResolveLoadouts(data, stage, opt),
            EidolonRanks = ResolveEidolonRanks(stage, opt),
        };

        BattleState manual = BattleFactory.Create(Config());
        BattleState auto = BattleFactory.Create(Config());
        Runner.Begin(manual);

        ManualPolicy policy = new();
        GreedyPolicy chooser = new() { AllowOverdraw = opt.ContainsKey("--overdraw") };
        int beats = 0;
        while (!manual.IsOver && beats < 500)
        {
            // 不再"开场预填一整拍"：召唤物 / 协同 / 光环可能在拍内改变资源与可选技能，
            // 预填会让后面角色的指令落后于状态。这里在**每个角色真正行动时**再取指令，
            // 但走的是同一条 ManualPolicy → Used → 服务端重放路径。
            Runner.Step(manual, new DeferredManualPolicy(chooser, policy));
            beats++;
        }
        Runner.Finish(manual);

        BattleOutcome expected = Runner.Run(auto, new GreedyPolicy { AllowOverdraw = opt.ContainsKey("--overdraw") });

        bool same = manual.Log.SequenceEqual(auto.Log, StringComparer.Ordinal);
        Console.WriteLine($"手动路径 : 拍了 {beats} 次，结果 {manual.Outcome}；自动对照 {expected}");
        Console.WriteLine($"日志一致 : {(same ? "是" : "否")}（手动 {manual.Log.Count} 行 / 自动 {auto.Log.Count} 行）");
        Console.WriteLine($"日志指纹 : {LogHash.Fnv1a64(manual.Log)}");
        Console.WriteLine("重放指令 : " + System.Text.Json.JsonSerializer.Serialize(
            policy.Used.Select(static c => new { skillKey = c.SkillKey, targetSerial = c.TargetSerial })));

        if (!same)
        {
            int n = Math.Min(manual.Log.Count, auto.Log.Count);
            for (int i = 0; i < n; i++)
            {
                if (string.Equals(manual.Log[i], auto.Log[i], StringComparison.Ordinal)) continue;
                Console.Error.WriteLine($"第一处差异 行 {i + 1}:");
                Console.Error.WriteLine($"  手动: {manual.Log[i]}");
                Console.Error.WriteLine($"  自动: {auto.Log[i]}");
                break;
            }
            if (manual.Log.Count != auto.Log.Count)
                Console.Error.WriteLine($"日志长度不同：手动 {manual.Log.Count} / 自动 {auto.Log.Count}");
        }

        return same && manual.Outcome == expected ? 0 : 1;
    }

    /// <summary>
    /// 解析 `--loadout &lt;spec-id&gt;`：给全队套上一套**标准配装**（`M7` 的真实面板与装备效果路径）。
    /// </summary>
    /// <remarks>
    /// 与 `--tier` 的区别：`--tier` 只换**面板**（`M2` 的插值口径），
    /// `--loadout` 走的是**真配装** —— 面板由 `PanelCalculator` 算，武器特效与套装效果也一起进战斗。
    /// 两者可以同时给，但**配装优先**（见 `BattleConfig`）。
    /// </remarks>
    private static IReadOnlyDictionary<string, Loadout>? ResolveLoadouts(GameData data, StageDef stage,
        Dictionary<string, string> opt)
    {
        if (!opt.TryGetValue("--loadout", out string? id) || string.IsNullOrWhiteSpace(id)) return null;

        CanonicalLoadout.Spec? spec = CanonicalLoadout.All.FirstOrDefault(s => s.Id == id)
            ?? id switch
            {
                "naked" => CanonicalLoadout.Tier0Plus0,
                "low" => CanonicalLoadout.Tier0Plus1,
                "mid" => CanonicalLoadout.TierMid,
                "graduate" => CanonicalLoadout.TierGraduate,
                _ => null,
            };

        if (spec is null)
        {
            throw new ArgumentException($"未登记的配装档位：'{id}'。可用：" +
                                        string.Join(" / ", CanonicalLoadout.All.Select(static s => s.Id)) +
                                        "（短名：naked / low / mid / graduate）");
        }

        Dictionary<string, Loadout> result = new(StringComparer.Ordinal);
        foreach (string pid in stage.Party)
        {
            CharacterDef def = data.Character(pid);
            TierRowDef? row = data.Tiers.Row(BattleFactory.AnchorRowOf(def));
            result[pid] = CanonicalLoadout.Build(data, def, spec, row);
        }
        return result;
    }

    /// <summary>调试/验收用：`--party id1,id2,...` 覆盖关卡固定队伍（不超过 4 人）。</summary>
    private static void ApplyPartyOverride(StageDef stage, Dictionary<string, string> opt)
    {
        if (!opt.TryGetValue("--party", out string? raw) || string.IsNullOrWhiteSpace(raw)) return;
        string[] ids = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (ids.Length is 0 or > 4)
            throw new ArgumentException("--party 需要 1~4 个角色 id，用逗号分隔。");
        stage.Party = [.. ids];
    }

    /// <summary>`--eidolon N`：给全队套同一权阶（0~6），用于验证权阶进战斗。</summary>
    private static IReadOnlyDictionary<string, int>? ResolveEidolonRanks(StageDef stage, Dictionary<string, string> opt)
    {
        if (!opt.TryGetValue("--eidolon", out string? raw) || string.IsNullOrWhiteSpace(raw)) return null;
        if (!int.TryParse(raw, out int rank) || rank is < 0 or > 6)
            throw new ArgumentException("--eidolon 需要 0~6 的整数。");
        Dictionary<string, int> result = new(StringComparer.Ordinal);
        foreach (string id in stage.Party) result[id] = rank;
        return result;
    }

    /// <summary>解析 `--tier &lt;id&gt;`。空 / 缺省 → `null`（不套档位，走关卡自己的面板口径）。</summary>
    private static InvestmentTierDef? ResolveTier(GameData data, Dictionary<string, string> opt)
    {
        if (!opt.TryGetValue("--tier", out string? id) || string.IsNullOrWhiteSpace(id)) return null;
        InvestmentTierDef? tier = data.Tiers.Find(id);
        if (tier is null)
        {
            throw new ArgumentException($"未登记的投入档位：'{id}'。可用：" +
                                        string.Join(" / ", data.Tiers.Tiers.Select(static t => t.Id)));
        }
        return tier;
    }

    private static void PrintSetup(BattleState state, StageDef stage, ulong seed, double hpMult, int? skillLevel,
        Dictionary<string, string> opt)
    {
        Console.WriteLine($"关卡 : {stage.Id}（{stage.Name}）");
        Console.WriteLine($"种子 : {seed}    技能等级 : {skillLevel?.ToString() ?? $"关卡默认 {stage.SkillLevel}"}" +
                          (Math.Abs(hpMult - 1.0) > 1e-9 ? $"    敌人血量倍率 : ×{hpMult:0.##}（调试）" : "") +
                          (opt.TryGetValue("--tier", out string? t) && t.Length > 0 ? $"    投入档位 : {t}" : ""));
        Console.WriteLine($"共鸣指数开局 : {state.ResonanceIndex}/{state.ResonanceCap}" +
                          $"    轮次上限 : {state.MaxRound}    自然回合窗口 : {BattleConstants.RoundAv:0} AV");
        Console.WriteLine();
        Console.WriteLine("站位  单位            等级  攻击    生命      防御   速度  暴击率 面板暴伤  能量");
        foreach (BattleUnit u in state.Units)
        {
            ComputedStats st = u.Stats;
            Console.WriteLine($"  {u.SlotIndex + 1}   {u.Name,-14} {u.Level,3}  {st.AtkValue,7:0} {u.MaxHp,9:0} {st.DefValue,7:0} " +
                              $"{st.SpeedValue,6:0} {st.CritRateValue,7:P0} {st.CritDmgValue,8:0.00} " +
                              (st.EnergyMaxValue > 0 ? $"{st.EnergyMaxValue,5:0}" : "    —"));
        }
        Console.WriteLine();
    }

    private static void PrintSummary(BattleState state, BattleOutcome outcome)
    {
        double dealt = state.Enemies.Sum(u => u.MaxHp - u.Hp);
        Console.WriteLine("[M1 验收摘要]");
        Console.WriteLine($"  结果          : {outcome}");
        Console.WriteLine($"  自然回合数    : {state.Round} / {state.MaxRound}");
        Console.WriteLine($"  行动次数      : {state.TurnCounter}");
        Console.WriteLine($"  对敌总伤害    : {dealt:0.#}");
        Console.WriteLine($"  普攻 / 元素共鸣 : {state.BasicAttackCount} / {state.ResonanceCastCount}" +
                          (state.OverdrawCount > 0 ? $"（透支 {state.OverdrawCount} 次）" : ""));
        Console.WriteLine($"  共鸣指数收尾  : {state.ResonanceIndex}/{state.ResonanceCap}");
        Console.WriteLine($"  紊乱触发回合  : {(state.DisorderUsedThisRound ? "本回合已用" : "本回合未用")}");

        foreach (BattleUnit e in state.Enemies)
        {
            Console.WriteLine($"  敌人 {e.Name,-10} {e.Hp:0}/{e.MaxHp:0}");
            foreach (KeyValuePair<AnomalyFamily, AnomalyState> kv in e.Anomalies.OrderBy(static k => k.Key))
            {
                AnomalyState a = kv.Value;
                if (!a.AnyActive) continue;
                Console.WriteLine($"    └ {ElementInfo.FamilyToChinese(kv.Key),-4} 层数 {a.Stacks,3}  剩余 {a.Remaining,5:0.#}" +
                                  (a.Cower ? "  [畏缩]" : "") + (a.Frozen ? "  [冻结]" : "") + (a.Stunned ? "  [晕眩]" : ""));
            }
        }

        if (state.NotImplementedNotes.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("[接口层已就位 · 实现层待后续里程碑]（06 §8.3 第 4 条：不算错误）");
            foreach (string n in state.NotImplementedNotes) Console.WriteLine("  · " + n);
        }
    }

    // ── verify ────────────────────────────────────────────────────────

    private static int Verify(Dictionary<string, string> opt)
    {
        Banner();
        GameData data = LoadData(opt);
        bool allOk = true;

        allOk &= VerifyLevelMultiplier();
        allOk &= VerifyDamagePipeline(data);
        allOk &= VerifyReplay(data, opt);
        allOk &= VerifyTutorial(data);
        allOk &= VerifyEnemyMechanics(data);
        allOk &= VerifyValeria(data);
        allOk &= VerifyValeriaEidolons(data);
        allOk &= VerifyNewCharacters(data);

        Console.WriteLine();
        Console.WriteLine(allOk
            ? "M1 验收：**通过** —— 02 §9 的管线数值吻合，且同一种子重放逐行一致。"
            : "M1 验收：**失败** —— 见上面的失败项。");
        Console.WriteLine("（数据本身是否可发布，由 `validate` 子命令回答 —— 那是 M3 的验收线。）");
        return allOk ? 0 : 1;
    }

    /// <summary>
    /// 技能等级曲线的口径（`01 §5.2` · 裁决 #62 方案 B）。
    /// </summary>
    /// <remarks>
    /// 方案 B 把两处职责切开了：**`07` 写"1 级是多少"，`01 §5.2` 写"从 1 级升到 14 级涨多少倍"**。
    /// 因此每个数组在 1 级处**必须**归一化成 ×1.00 —— 这条一旦不成立，全部角色会整体虚高 3.8~6.6 倍。
    /// </remarks>
    private static bool VerifyLevelMultiplier()
    {
        Console.WriteLine("[验算 0] 技能等级曲线：1 级必须恒等于 ×1.00（裁决 #62 方案 B）");

        (string Name, double L1, double L10, double L14)[] rows =
        [
            ("普攻", BattleConstants.LevelMult("basic", 1), BattleConstants.LevelMult("basic", 10), BattleConstants.LevelMult("basic", 14)),
            ("元素共鸣", BattleConstants.LevelMult("resonance", 1), BattleConstants.LevelMult("resonance", 10), BattleConstants.LevelMult("resonance", 14)),
            ("元素共鸣·扩散", BattleConstants.LevelMult("resonance_spread", 1), BattleConstants.LevelMult("resonance_spread", 10), BattleConstants.LevelMult("resonance_spread", 14)),
            ("终结技", BattleConstants.LevelMult("ultimate", 1), BattleConstants.LevelMult("ultimate", 10), BattleConstants.LevelMult("ultimate", 14)),
            ("不修正（敌人/召唤物）", BattleConstants.LevelMult("none", 1), BattleConstants.LevelMult("none", 10), BattleConstants.LevelMult("none", 14)),
        ];
        bool ok = true;
        Console.WriteLine("  数组                1 级    10 级   14 级");
        foreach ((string name, double l1, double l10, double l14) in rows)
        {
            Console.WriteLine($"  {name,-18} {l1,5:0.000} {l10,7:0.000} {l14,7:0.000}");
            if (Math.Abs(l1 - 1.0) > 1e-9) ok = false;
        }
        Console.WriteLine($"  → {(ok ? "通过（1 级全部 ×1.00）" : "**失败**")}");
        Console.WriteLine();
        return ok;
    }

    /// <summary>
    /// `02 §9` 的伤害验算用例。
    /// </summary>
    /// <remarks>
    /// ⚠️ 裁决 #62 之后，本用例的定位变清楚了：它验证的是**管线实现**，与"技能等级怎么算"无关。
    /// 因此这里**把 4275% 当作字面输入**直接喂进管线 —— `02 §9` 原文里的"1125% × 3.80"
    /// 被判定为纯笔误（真实运行时的 1 级系数是 ×1.00），但**它的每一步算术仍然是对管线的正确断言**，
    /// 保留下来正好当回归测试。
    /// </remarks>
    private static bool VerifyDamagePipeline(GameData data)
    {
        Console.WriteLine("[验算 1] 02 §9 伤害管线（守约人 80 级大毕业 vs Lv90 精英）");
        Console.WriteLine("  输入：攻击 3,000 · 暴击率 65% · 面板暴伤 270% · 目标防御 600 · 等级差 10");
        Console.WriteLine("        技能倍率 = 4275%（字面输入：02 §9 原文的 1125% × 3.80，见上面的口径说明）");

        BattleUnit attacker = new()
        {
            Serial = 1,
            DefId = "char-shouyueren",
            Name = "守约人（大毕业基准）",
            Kind = UnitKind.Character,
            Disposition = Disposition.Friendly,
            Level = 80,
            PanelIsFinal = true,
            Base = new BaseStatsDef
            {
                Atk = 3000, Hp = 8000, Def = 2000, Speed = 100,
                CritRate = 0.65, CritDmg = 2.70, EnergyMax = 120, Threat = 50,
            },
            Elements = [Element.Wind],
        };
        attacker.Hp = attacker.MaxHp;

        BattleUnit target = new()
        {
            Serial = 2,
            DefId = "synthetic-elite",
            Name = "Lv90 精英",
            Kind = UnitKind.Enemy,
            Disposition = Disposition.Hostile,
            IsEliteOrAbove = true,
            Level = 90,
            PanelIsFinal = true,
            Base = new BaseStatsDef { Atk = 3200, Hp = 130000, Def = 600, Speed = 100 },
        };
        target.Hp = target.MaxHp;

        BattleState state = new()
        {
            Data = data,
            Rng = new Rng(20260815UL),
            Stage = new StageDef { Id = "verify", Name = "管线验算" },
        };

        DamageResult crit = DamagePipeline.Compute(state, new DamageRequest
        {
            Source = attacker,
            Target = target,
            Element = Element.Wind,
            Multiplier = 11.25 * 3.80,
            ForceCrit = true,
        });
        DamageResult noCrit = DamagePipeline.Compute(state, new DamageRequest
        {
            Source = attacker,
            Target = target,
            Element = Element.Wind,
            Multiplier = 11.25 * 3.80,
            ForceNoCrit = true,
        });

        Console.WriteLine($"  ③ 有效防御 {crit.EffectiveDefense:0.#}  减伤率 R = {crit.ReductionRate:P1}");
        Console.WriteLine($"  ④ 基础伤害 {crit.AfterDefense:0.#}");
        Console.WriteLine($"  ⑧ 暴击区 {crit.ZoneCrit:0.00}    ⑨ 越级区 {crit.ZoneLevel:0.00}");
        Console.WriteLine($"  ⑪ 最终：暴击 {crit.Amount:0.#} ／ 未暴击 {noCrit.Amount:0.#}");

        double expected = crit.Amount * 0.65 + noCrit.Amount * 0.35;
        Console.WriteLine($"  期望值 = {crit.Amount:0.#} × 65% + {noCrit.Amount:0.#} × 35% = {expected:0.#}");

        bool ok = true;
        ok &= Check("③ 减伤率 20.0%", crit.ReductionRate, 0.20);
        ok &= Check("④ 基础伤害 102,600", crit.AfterDefense, 102600);
        ok &= Check("⑧ 暴击（越级前）277,020", crit.AfterDefense * crit.ZoneCrit, 277020);
        ok &= Check("⑧ 未暴击（越级前）102,600", noCrit.AfterDefense * noCrit.ZoneCrit, 102600);
        ok &= Check("⑨ 越级区 0.90", crit.ZoneLevel, 0.90);
        ok &= Check("⑪ 最终暴击 249,318", crit.Amount, 249318);
        ok &= Check("⑪ 最终未暴击 92,340", noCrit.Amount, 92340);
        ok &= Check("期望 194,376", expected, 194376);

        Console.WriteLine($"  → {(ok ? "通过" : "**失败**")}");
        Console.WriteLine();
        return ok;
    }

    /// <summary>
    /// `04 §3.4` 的教学五步能不能真的走完。
    /// </summary>
    /// <remarks>
    /// 这一条值得单独验，因为它**失败时的样子很隐蔽**：第一版的非教学行动直接交给自由策略，
    /// 而自由策略的第一优先级是终结技 —— 教学关带开场满能量，于是千语星遥第一手就清场，
    /// **四步引导一步都没走到，而战斗照样"赢了"**。只看胜负是发现不了的。
    /// </remarks>
    private static bool VerifyTutorial(GameData data)
    {
        Console.WriteLine("[验算 3] 教学关引导（04 §3.4 的五步脚本）");

        StageDef stage = data.Stage("stage-main-s7");
        BattleState state = BattleFactory.Create(new BattleConfig { Data = data, Stage = stage, Seed = 20260815UL });
        TutorialPolicy policy = new("char-shouyueren", "char-qianyu-xingyao");
        BattleOutcome outcome = Runner.Run(state, policy);

        bool reached = policy.Step >= 5;
        bool won = outcome == BattleOutcome.AllyWin;
        bool usedBasic = state.Log.Any(static l => l.Contains("无名风起", StringComparison.Ordinal));
        bool usedResonance = state.Log.Any(static l => l.Contains("横刀问路", StringComparison.Ordinal));
        bool usedUltimate = state.Log.Any(static l => l.Contains("风，如约而至", StringComparison.Ordinal));

        Console.WriteLine($"    走到第 {policy.Step} 步 : {(reached ? "通过" : "**失败**（引导没走完）")}");
        Console.WriteLine($"    普攻 / 元素共鸣 / 终结技都演示到了 : " +
                          $"{(usedBasic && usedResonance && usedUltimate ? "通过" : "**失败**")}" +
                          $"（{usedBasic} / {usedResonance} / {usedUltimate}）");
        Console.WriteLine($"    教学关结果 : {outcome}（{(won ? "通过" : "**失败**")}）");
        Console.WriteLine();
        return reached && won && usedBasic && usedResonance && usedUltimate;
    }

    /// <summary>同一种子 + 同一策略 → 逐行一致；换种子 → 应当不同（否则说明随机没有被真正使用）。</summary>
    private static bool VerifyReplay(GameData data, Dictionary<string, string> opt)
    {
        Console.WriteLine("[验算 2] 确定性重放（06 §1 · 裁决 #59）");

        string stageId = opt.GetValueOrDefault("--stage", "stage-main-s7");
        StageDef stage = data.Stage(stageId);

        string[] a = RunToLog(data, stage, 20260815UL);
        string[] b = RunToLog(data, stage, 20260815UL);
        string[] c = RunToLog(data, stage, 20260816UL);

        bool sameSeedSame = a.AsSpan().SequenceEqual(b);
        bool diffSeedDiffers = !a.AsSpan().SequenceEqual(c);

        Console.WriteLine($"  同一种子的两次运行逐行一致 : {(sameSeedSame ? "通过" : "**失败**")}（{a.Length} 行）");
        Console.WriteLine($"  换种子后结果不同           : {(diffSeedDiffers ? "通过" : "**失败**")}");
        if (!sameSeedSame)
        {
            for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
            {
                if (a[i] == b[i]) continue;
                Console.WriteLine($"    首个分歧在第 {i + 1} 行：");
                Console.WriteLine($"      A: {a[i]}");
                Console.WriteLine($"      B: {b[i]}");
                break;
            }
        }
        Console.WriteLine();
        return sameSeedSame && diffSeedDiffers;
    }

    private static string[] RunToLog(GameData data, StageDef stage, ulong seed)
    {
        BattleState state = BattleFactory.Create(new BattleConfig { Data = data, Stage = stage, Seed = seed });
        Runner.Run(state, new GreedyPolicy());
        return [.. state.Log];
    }

    private static BattleState RunEnemyStage(GameData data, string stageId, double hpMult, ulong seed)
    {
        BattleState state = BattleFactory.Create(new BattleConfig
        {
            Data = data,
            Stage = data.Stage(stageId),
            Seed = seed,
            EnemyHpMultiplier = hpMult,
        });
        Runner.Run(state, new GreedyPolicy());
        return state;
    }

    // ── 验算 4：04 §7 的敌方机制 ────────────────────────────────────────

    /// <summary>
    /// `04 §7` 里**只有文档、没有实现**的三条敌方机制（本轮补齐）的验收。
    /// </summary>
    /// <remarks>
    /// <b>为什么这三条必须各写一条断言</b>：它们的失败模式完全一样 ——
    /// **数据文件里写了、校验器不报错、战斗照样打赢，而日志里那一行从来没出现过**。
    /// 只断言"没报错 / 打赢了"是发现不了的，所以每一条都断言"日志里出现了那一行"。
    /// <para>
    /// 血量一律放大 30 倍：这三条都是"打久了才会露头"的机制（覆盖两次、攒满三层、注目攒到三层），
    /// 正常血量的杂兵会在机制露头前就死掉。放大血量是 `--hp-mult` 的正当用途，不是游戏内容。
    /// </para>
    /// </remarks>
    /// <summary>
    /// `[验算 5]` 瓦列里娅的机制链（火 · 施术仪 · 可燃 DoT 副C）。
    /// </summary>
    /// <remarks>
    /// <b>为什么不跑一整场来验</b>：她的循环由「普攻攒火种 → 元素共鸣挂斗争标记 → 队友打标记目标触发无人机」
    /// 三拍组成，而 `GreedyPolicy` 的优先级是**终结技 &gt; 元素共鸣 &gt; 普攻**，她的终结技只要 1 发火种就能放
    /// ⇒ 自动策略下她会一直「普攻 → 大招」循环，**元素共鸣一次都不会放**（实测日志就是这么走的）。
    /// 那验的是策略，不是她的机制。
    /// <para>
    /// 所以这里**直接驱动技能**，把三拍按顺序摆出来，逐拍断言状态与资源 —— 与 `[验算 4]` 同一套做法。
    /// </para>
    /// </remarks>
    private static bool VerifyValeria(GameData data)
    {
        Console.WriteLine("[验算 5] 瓦列里娅的机制链（火 / 施术仪 / 可燃 DoT 副C）");

        BattleState state = BattleFactory.Create(new BattleConfig
        {
            Data = data,
            Stage = data.Stage("stage-charsim-valeria"),
            Seed = 20260815UL,
        });

        BattleUnit? valeria = state.Units.FirstOrDefault(static u => u.DefId == "char-valeria");
        BattleUnit? mate = state.Units.FirstOrDefault(static u => u.DefId == "char-belrand");
        BattleUnit? foe = state.Units.FirstOrDefault(static u => u.IsEnemy);

        if (valeria is null || mate is null || foe is null)
        {
            Console.WriteLine("  → **失败**：靶场里缺人（需要 char-valeria / char-belrand / 一个敌人）");
            Console.WriteLine();
            return false;
        }

        // 先给敌人挂一个增益，用来验"施加可爆火种的那一刻驱散一次"。
        EffectResolver.Run(state, [new EffectDef
        {
            Type = "applyBuff", BuffId = "buff-ember", Stacks = 1, Duration = 9, Target = "mainTarget",
        }], new EffectContext { Actor = mate, Target = foe }, 0);

        // ⚠️ **别用"增益总数"去断言驱散**（第一版就是这么写的，结果假失败）：
        // 伤害先结算、`onHit` 后结算，而铅灰傀儡的「代偿」挂在 `onDamageTaken` 上 ——
        // 这一击会**先给它加一个新 buff**，而驱散按"最新挂上的先掉"正好把这个新 buff 扒掉。
        // 于是"驱散成功"与"驱散失败"数出来的总数一模一样（1 → 1）。
        // ⇒ 改成断言**日志里真的出现了那一行**，与 `[验算 4]` 同一条纪律。
        int dispelLines0 = state.Log.Count(static l => l.Contains("驱散：移除了", StringComparison.Ordinal));

        // ── 第 1 拍：普攻 → 火种 +1、无人机 +2、可爆火种 + 驱散 ──
        Runner.ExecuteSkill(state, valeria, "basic", valeria.Skills["basic"], foe, 0);
        int kindling = valeria.StackOf("火种");
        int drones = valeria.StackOf("无人机");
        int mark = foe.StatusStacks("可爆火种");
        int dispelLines1 = state.Log.Count(static l => l.Contains("驱散：移除了", StringComparison.Ordinal));
        bool dispelled = dispelLines1 == dispelLines0 + 1;

        bool step1 = kindling == 1 && drones == 2 && mark == 1 && dispelled;
        Console.WriteLine($"  1) 普攻：火种 {kindling}/3 · 无人机 {drones}/5 · 可爆火种 {mark} 层 · " +
                          $"施标记时驱散 1 个增益 {(dispelled ? "已发生" : "**没发生**")} → {(step1 ? "通过" : "**失败**")}");

        // ── 第 2 拍：元素共鸣 → 斗争标记、火种 −1 ──
        Runner.ExecuteSkill(state, valeria, "resonance", valeria.Skills["resonance"], foe, 0);
        int kindling2 = valeria.StackOf("火种");
        int strife = foe.StatusStacks("斗争标记");
        bool step2 = kindling2 == 0 && strife == 1;
        Console.WriteLine($"  2) 元素共鸣：火种 {kindling2}/3 · 斗争标记 {strife} 层 → {(step2 ? "通过" : "**失败**")}");

        // ── 第 3 拍：**队友**打被标记的敌人 → 无人机 −1、意志 +1、惊慌、可燃 ──
        int dronesBefore = valeria.StackOf("无人机");
        int combustibleBefore = 0;
        if (foe.Anomalies.TryGetValue(AnomalyFamily.Fire, out AnomalyState? a0) && a0 is not null) combustibleBefore = a0.Stacks;
        Runner.ExecuteSkill(state, mate, "basic", mate.Skills["basic"], foe, 0);
        int dronesAfter = valeria.StackOf("无人机");
        int will = valeria.StackOf("意志");
        int panic = foe.StatusStacks("惊慌");
        int combustibleAfter = 0;
        if (foe.Anomalies.TryGetValue(AnomalyFamily.Fire, out AnomalyState? a1) && a1 is not null) combustibleAfter = a1.Stacks;

        bool step3 = dronesAfter == dronesBefore - 1 && will == 1 && panic == 1 && combustibleAfter > combustibleBefore;
        Console.WriteLine($"  3) 队友打标记目标：无人机 {dronesBefore} → {dronesAfter} · 意志 {will}/5 · " +
                          $"惊慌 {panic} 层 · 可燃 {combustibleBefore} → {combustibleAfter} → {(step3 ? "通过" : "**失败**")}");

        // ── 第 4 拍：**她自己**打被标记的敌人不该触发无人机（scope = allyOther）──
        int dronesBeforeSelf = valeria.StackOf("无人机");
        Runner.ExecuteSkill(state, valeria, "basic", valeria.Skills["basic"], foe, 0);
        int dronesAfterSelf = valeria.StackOf("无人机");
        bool step4 = dronesAfterSelf >= dronesBeforeSelf;   // 普攻自己 +2，只多不少
        Console.WriteLine($"  4) 她自己打标记目标不触发协同：无人机 {dronesBeforeSelf} → {dronesAfterSelf}（普攻 +2）" +
                          $" → {(step4 ? "通过" : "**失败**")}");

        // ── 第 5 拍：3 发火种的终结技 ⇒ 燎原三档里最高的那一档 + 全队火伤光环 ──
        valeria.GainStack("火种", 3, 3);
        Runner.ExecuteSkill(state, valeria, "ultimate", valeria.Skills["ultimate"], foe, 0);
        bool prairie3 = valeria.StatusStacks("燎原") > 0 && valeria.KnobValue("droneDamageBonus") >= 0.50 - 1e-9;
        bool banner = valeria.StatusStacks("燎原·军旗") > 0;
        bool spent = valeria.StackOf("火种") == 0;
        bool step5 = prairie3 && banner && spent;
        Console.WriteLine($"  5) 3 发火种终结技：燎原无人机伤害旋钮 {valeria.KnobValue("droneDamageBonus"):P0} · " +
                          $"全队军旗 {(banner ? "已挂" : "**没有**")} · 火种清空 {spent} → {(step5 ? "通过" : "**失败**")}");

        // ── 第 6 拍：燎原期间无人机攻击**不消耗**无人机 ──
        int beforeFree = valeria.StackOf("无人机");
        Runner.ExecuteSkill(state, mate, "basic", mate.Skills["basic"], foe, 0);
        int afterFree = valeria.StackOf("无人机");
        bool step6 = afterFree == beforeFree;
        Console.WriteLine($"  6) 燎原期间协同不消耗无人机：{beforeFree} → {afterFree} → {(step6 ? "通过" : "**失败**")}");

        bool ok = step1 && step2 && step3 && step4 && step5 && step6;
        Console.WriteLine($"  → {(ok ? "通过" : "**失败**")}");
        Console.WriteLine();
        return ok;
    }

    /// <summary>
    /// `[验算 6]` 瓦列里娅的权阶（Ⅰ/Ⅱ/Ⅳ/Ⅵ）—— 权阶不再是"只存不读"的旋钮。
    /// </summary>
    /// <remarks>
    /// 这一条专门防"数据写了、引擎没读"：每一条都断言**行为差异**，而不是"字段存在"。
    /// 覆盖：Ⅰ 开局 3 架 + 无人机上限 6；Ⅱ 才解锁引爆（0/Ⅰ 不引爆）；
    /// Ⅳ 惊慌额外 −5% 与赤潮意志 3 层；Ⅵ 额外可燃 +1 与 50% 返还判定；
    /// 以及 3 发火种的「燎原·军旗」真的扩散给队友（曾经只加在施法者身上）。
    /// </remarks>
    private static bool VerifyValeriaEidolons(GameData data)
    {
        Console.WriteLine("[验算 6] 瓦列里娅的权阶（Ⅰ 开局无人机 / Ⅱ 引爆闸门 / Ⅳ 惊慌与赤潮 / Ⅵ 返还与额外可燃）");
        bool ok = true;

        BattleState NewState(int rank) => BattleFactory.Create(new BattleConfig
        {
            Data = data,
            Stage = data.Stage("stage-charsim-valeria"),
            Seed = 20260815UL,
            EidolonRanks = new Dictionary<string, int>(StringComparer.Ordinal) { ["char-valeria"] = rank },
        });

        static (BattleUnit V, BattleUnit Mate, BattleUnit Foe) Pick(BattleState s) => (
            s.Units.First(static u => u.DefId == "char-valeria"),
            s.Units.First(static u => u.DefId == "char-belrand"),
            s.Units.First(static u => u.IsEnemy));

        // ── Ⅰ：开局 3 架；droneCap +1 让"cap 5"的 gainStack 实际封顶 6 ──
        {
            BattleState s = NewState(1);
            (BattleUnit v, _, _) = Pick(s);
            int start = v.StackOf("无人机");
            EffectResolver.Run(s, [new EffectDef
            {
                Type = "gainStack", Stack = "无人机", Amount = 5, Cap = 5, Target = "self",
            }], new EffectContext { Actor = v, Target = v }, 0);
            int capped = v.StackOf("无人机");
            bool pass = start == 3 && capped == 6;
            ok &= pass;
            Console.WriteLine($"  Ⅰ) 开局无人机 {start}/6 · 带 cap5 的 gain+5 后 {capped}（应为 6） → {(pass ? "通过" : "**失败**")}");
        }

        // ── Ⅱ：0/Ⅰ 权阶只挂火种不引爆；Ⅱ 起才引爆 ──
        {
            bool MarkSurvives(int rank)
            {
                BattleState s = NewState(rank);
                (BattleUnit v, BattleUnit mate, BattleUnit foe) = Pick(s);
                Runner.ExecuteSkill(s, v, "basic", v.Skills["basic"], foe, 0);
                Runner.ExecuteSkill(s, v, "resonance", v.Skills["resonance"], foe, 0);
                Runner.ExecuteSkill(s, mate, "basic", mate.Skills["basic"], foe, 0);
                return foe.StatusStacks("可爆火种") > 0;
            }

            bool at0 = MarkSurvives(0);
            bool at1 = MarkSurvives(1);
            bool at2 = !MarkSurvives(2);
            bool pass = at0 && at1 && at2;
            ok &= pass;
            Console.WriteLine($"  Ⅱ) 引爆闸门：0 权 {(at0 ? "不引爆" : "**引爆了**")} · Ⅰ 权 {(at1 ? "不引爆" : "**引爆了**")} · " +
                              $"Ⅱ 权 {(at2 ? "引爆" : "**没引爆**")} → {(pass ? "通过" : "**失败**")}");
        }

        // ── Ⅱ 后半句：每回合必定 +1 无人机（有标记时由天赋⑥补，未标记时由①给）──
        {
            int GainAtTurnEnd(int rank, bool marked)
            {
                BattleState s = NewState(rank);
                (BattleUnit v, _, BattleUnit foe) = Pick(s);
                if (marked)
                {
                    EffectResolver.Run(s, [new EffectDef
                    {
                        Type = "applyDebuff", DebuffId = "debuff-strife-mark", Stacks = 1, Duration = 2, Target = "mainTarget",
                    }], new EffectContext { Actor = v, Target = foe }, 0);
                }
                int before = v.StackOf("无人机");
                HookBus.Fire(s, "onTurnEnd", new HookContext { Actor = v }, 0);
                return v.StackOf("无人机") - before;
            }

            int no0 = GainAtTurnEnd(0, marked: false);
            int yes0 = GainAtTurnEnd(0, marked: true);
            int no2 = GainAtTurnEnd(2, marked: false);
            int yes2 = GainAtTurnEnd(2, marked: true);
            bool pass = no0 == 1 && yes0 == 0 && no2 == 1 && yes2 == 1;
            ok &= pass;
            Console.WriteLine($"  Ⅱ) 每回合必定 +1：0 权 无标记 +{no0} / 有标记 +{yes0} · Ⅱ 权 无标记 +{no2} / 有标记 +{yes2} → {(pass ? "通过" : "**失败**")}");
        }

        // ── Ⅳ：惊慌额外 −5%；每次消耗无人机给全队挂「赤潮意志」，3 层封顶 ──
        {
            static bool HasExtraDefDown(BattleUnit foe)
                => foe.Statuses.Any(static st => st.Def.Mods.Any(static m =>
                    m.Kind == StatKind.Def && m.Op == ModOp.Pct && Math.Abs(m.Value + 0.05) < 1e-9));

            BattleState s0 = NewState(0);
            (BattleUnit v0, BattleUnit mate0, BattleUnit foe0) = Pick(s0);
            Runner.ExecuteSkill(s0, v0, "basic", v0.Skills["basic"], foe0, 0);
            Runner.ExecuteSkill(s0, v0, "resonance", v0.Skills["resonance"], foe0, 0);
            Runner.ExecuteSkill(s0, mate0, "basic", mate0.Skills["basic"], foe0, 0);
            bool noAt0 = !HasExtraDefDown(foe0);

            BattleState s = NewState(4);
            (BattleUnit v, BattleUnit mate, BattleUnit foe) = Pick(s);
            Runner.ExecuteSkill(s, v, "basic", v.Skills["basic"], foe, 0);
            Runner.ExecuteSkill(s, v, "resonance", v.Skills["resonance"], foe, 0);
            List<BattleUnit> allies =
                [.. s.Units.Where(static u => u.IsCharacter && u.Disposition == Disposition.Friendly && u.DefId != "char-valeria")];
            foreach (BattleUnit a in allies.Take(3))
                Runner.ExecuteSkill(s, a, "basic", a.Skills["basic"], foe, 0);

            bool extraAt4 = HasExtraDefDown(foe);
            int stacksV = v.StatusStacks("赤潮意志");
            int minAlly = allies.Take(3).Min(static a => a.StatusStacks("赤潮意志"));
            bool pass = noAt0 && extraAt4 && stacksV == 3 && minAlly == 3;
            ok &= pass;
            Console.WriteLine($"  Ⅳ) 惊慌额外 −5%：0 权 {(noAt0 ? "没有" : "**出现了**")} · Ⅳ 权 {(extraAt4 ? "有" : "**没有**")}；" +
                              $"赤潮意志 瓦列里娅 {stacksV}/3 · 队友最低 {minAlly}/3 → {(pass ? "通过" : "**失败**")}");
        }

        // ── Ⅵ：无人机额外叠 1 层可燃 + 50% 返还的概率判定 ──
        {
            (int Fire, bool ChanceLine) CoopProbe(int rank)
            {
                BattleState s = NewState(rank);
                (BattleUnit v, BattleUnit mate, BattleUnit foe) = Pick(s);
                Runner.ExecuteSkill(s, v, "basic", v.Skills["basic"], foe, 0);
                Runner.ExecuteSkill(s, v, "resonance", v.Skills["resonance"], foe, 0);
                if (foe.Anomalies.TryGetValue(AnomalyFamily.Fire, out AnomalyState? fire) && fire is not null)
                {
                    fire.Stacks = 0;
                    fire.Remaining = 0.0;
                }
                Runner.ExecuteSkill(s, mate, "basic", mate.Skills["basic"], foe, 0);
                int stacks = foe.Anomalies.TryGetValue(AnomalyFamily.Fire, out AnomalyState? after) && after is not null
                    ? after.Stacks
                    : 0;
                return (stacks, s.Log.Any(static l => l.Contains("概率判定 50%", StringComparison.Ordinal)));
            }

            (int fire4, bool chance4) = CoopProbe(4);
            (int fire6, bool chance6) = CoopProbe(6);
            bool pass = fire6 == fire4 + 1 && !chance4 && chance6;
            ok &= pass;
            Console.WriteLine($"  Ⅵ) 额外可燃：Ⅳ 权 {fire4} 层 → Ⅵ 权 {fire6} 层（应 +1）· " +
                              $"50% 返还判定 {(chance4 ? "**Ⅳ 权就出现了**" : "Ⅳ 权没有")} / {(chance6 ? "Ⅵ 权有" : "**Ⅵ 权没有**")} → {(pass ? "通过" : "**失败**")}");
        }

        // ── 燎原的「额外叠可燃」与权阶Ⅵ要**相加**（1 档 +1 / 2 档 +1 / 3 档 +2，Ⅵ 再 +1）──
        {
            static int Layers(BattleState s, BattleUnit v, BattleUnit foe)
            {
                if (foe.Anomalies.TryGetValue(AnomalyFamily.Fire, out AnomalyState? fire) && fire is not null)
                {
                    fire.Stacks = 0;
                    fire.Remaining = 0.0;
                }
                EffectResolver.Run(s, [new EffectDef
                {
                    Type = "applyAnomalyStack", Element = "fire", Stacks = 1, Chance = 1.0,
                    ExtraStacksKnob = "droneExtraCombust", Target = "mainTarget", StackCounter = "perHit",
                }], new EffectContext { Actor = v, Target = foe }, 0);
                return foe.Anomalies.TryGetValue(AnomalyFamily.Fire, out AnomalyState? after) && after is not null
                    ? after.Stacks
                    : 0;
            }

            BattleState plain0 = NewState(0);
            (BattleUnit vPlain0, _, BattleUnit foePlain0) = Pick(plain0);
            int base0 = Layers(plain0, vPlain0, foePlain0);

            BattleState prairie0 = NewState(0);
            (BattleUnit vPrairie0, _, BattleUnit foePrairie0) = Pick(prairie0);
            vPrairie0.GainStack("火种", 3, 3);
            Runner.ExecuteSkill(prairie0, vPrairie0, "ultimate", vPrairie0.Skills["ultimate"], foePrairie0, 0);
            int prairie3 = Layers(prairie0, vPrairie0, foePrairie0);

            BattleState eidolon6 = NewState(6);
            (BattleUnit vEidolon6, _, BattleUnit foeEidolon6) = Pick(eidolon6);
            int onlyEidolon = Layers(eidolon6, vEidolon6, foeEidolon6);

            BattleState both6 = NewState(6);
            (BattleUnit vBoth6, _, BattleUnit foeBoth6) = Pick(both6);
            vBoth6.GainStack("火种", 3, 3);
            Runner.ExecuteSkill(both6, vBoth6, "ultimate", vBoth6.Skills["ultimate"], foeBoth6, 0);
            int combined = Layers(both6, vBoth6, foeBoth6);

            bool pass = base0 == 1 && prairie3 == 3 && onlyEidolon == 2 && combined == 4;
            ok &= pass;
            Console.WriteLine($"  额外可燃叠层：裸装 {base0} · 3 档燎原 {prairie3}（应 3）· 仅 Ⅵ 权 {onlyEidolon}（应 2）· " +
                              $"燎原 + Ⅵ {combined}（应 4） → {(pass ? "通过" : "**失败**")}");
        }

        // ── 军旗光环：必须真的加到**队友**身上；曾经只加在施法者身上、且叠了 4 份 ──
        {
            BattleState s = NewState(0);
            (BattleUnit v, BattleUnit mate, BattleUnit foe) = Pick(s);
            double beforeMate = mate.Stats.ElemValue(ElemStat.DmgBonus, Element.Fire);
            double beforeSelf = v.Stats.ElemValue(ElemStat.DmgBonus, Element.Fire);
            v.GainStack("火种", 3, 3);
            Runner.ExecuteSkill(s, v, "ultimate", v.Skills["ultimate"], foe, 0);
            double afterMate = mate.Stats.ElemValue(ElemStat.DmgBonus, Element.Fire);
            double afterSelf = v.Stats.ElemValue(ElemStat.DmgBonus, Element.Fire);
            bool pass = Math.Abs(afterMate - beforeMate - 0.20) < 1e-6
                        && Math.Abs(afterSelf - beforeSelf - 0.20) < 1e-6;
            ok &= pass;
            Console.WriteLine($"  军旗光环：队友火伤 +{afterMate - beforeMate:P0} · 她自己 +{afterSelf - beforeSelf:P0}" +
                              $"（都应为 +20%） → {(pass ? "通过" : "**失败**")}");
        }

        Console.WriteLine($"  → {(ok ? "通过" : "**失败**")}");
        Console.WriteLine();
        return ok;
    }

    /// <summary>
    /// `[验算 7]` 薇拉 / 卡尔的跨角色原语：守护召唤物、全属性转化、动态触发上限、强制行动、额外回合上限。
    /// </summary>
    private static bool VerifyNewCharacters(GameData data)
    {
        Console.WriteLine("[验算 7] 薇拉 / 卡尔的新原语（守护召唤物 / 全属性转化 / 触发上限 / 强制处决 / 额外回合上限）");
        bool ok = true;

        BattleState NewState(string stageId, string characterId, int rank) => BattleFactory.Create(new BattleConfig
        {
            Data = data,
            Stage = data.Stage(stageId),
            Seed = 20260815UL,
            EidolonRanks = new Dictionary<string, int>(StringComparer.Ordinal) { [characterId] = rank },
        });

        // ── ① 薇拉：余烬天龙三倍承伤，薇拉本人不掉血 ──
        {
            BattleState s = NewState("stage-charsim-vera", "char-vera", 0);
            BattleUnit v = s.Units.First(static u => u.DefId == "char-vera");
            BattleUnit foe = s.Units.First(static u => u.IsEnemy);
            Runner.ExecuteSkill(s, v, "resonance", v.Skills["resonance"], foe, 0);
            BattleUnit? dragon = v.GuardianSummon;
            double vHp = v.Hp;
            double dragonHp = dragon?.Hp ?? 0.0;
            if (dragon is not null)
            {
                Combat.DealDamage(s, new DamageRequest
                {
                    Source = foe, Target = v, Element = Element.Physical, Multiplier = 0.10,
                    ScalingBase = "atk", CanCrit = false, Category = DamageCategory.Direct,
                    AccumulateAnomaly = false, Label = "验算·守护",
                }, 0);
            }
            bool pass = dragon is not null && v.Hp >= vHp - 1e-6 && dragon.Hp < dragonHp;
            ok &= pass;
            Console.WriteLine($"  ①) 守护承伤：天龙 {(dragon is null ? "未生成" : $"{dragonHp:0} → {dragon.Hp:0}")} · " +
                              $"薇拉 {vHp:0} → {v.Hp:0}（应不减少；天赋治疗可回血） → {(pass ? "通过" : "**失败**")}");
        }

        // ── ② 薇拉权阶Ⅳ：物理增伤在雷属性上也吃得到 ──
        {
            double LightningBonus(int rank)
            {
                BattleState s = NewState("stage-charsim-vera", "char-vera", rank);
                BattleUnit v = s.Units.First(static u => u.DefId == "char-vera");
                EffectResolver.Run(s, [new EffectDef
                {
                    Type = "modifyStat", Stat = "dmgBonusPhysical", Value = 0.20, Target = "self",
                }], new EffectContext { Actor = v, Target = v }, 0);
                return v.DmgBonusFor(Element.Lightning);
            }

            double at0 = LightningBonus(0);
            double at4 = LightningBonus(4);
            bool pass = at0 < 0.01 && at4 >= 0.19;
            ok &= pass;
            Console.WriteLine($"  ②) 全属性转化：0 权雷伤加成 {at0:P0} · Ⅳ 权 {at4:P0}（应 ≥20%） → {(pass ? "通过" : "**失败**")}");
        }

        // ── ③ 薇拉权阶Ⅰ/Ⅳ：talentCap 动态读取（0 权 1 次 / Ⅰ 权 2 次）──
        {
            int TriggerCount(int rank)
            {
                BattleState s = NewState("stage-charsim-vera", "char-vera", rank);
                BattleUnit v = s.Units.First(static u => u.DefId == "char-vera");
                BattleUnit foe = s.Units.First(static u => u.IsEnemy);
                for (int i = 0; i < 2; i++)
                    HookBus.Fire(s, "onDamageTaken", new HookContext { Actor = v, Target = foe }, 0);
                return v.TriggerCountsTurn
                    .Where(static kv => kv.Key.StartsWith("onDamageTaken#", StringComparison.Ordinal))
                    .Select(static kv => kv.Value)
                    .DefaultIfEmpty(0)
                    .Max();
            }

            int at0 = TriggerCount(0);
            int at1 = TriggerCount(1);
            bool pass = at0 == 1 && at1 == 2;
            ok &= pass;
            Console.WriteLine($"  ③) 天赋触发上限：0 权 {at0} 次 / Ⅰ 权 {at1} 次（应为 1 / 2） → {(pass ? "通过" : "**失败**")}");
        }

        // ── ④ 卡尔：可处决时强制下一次行动为「处决」 ──
        {
            BattleState s = NewState("stage-charsim-karni", "char-karni", 0);
            BattleUnit k = s.Units.First(static u => u.DefId == "char-karni");
            BattleUnit foe = s.Units.First(static u => u.IsEnemy);
            EffectResolver.Run(s, [new EffectDef
            {
                Type = "applyBuff", BuffId = "buff-execution-ready", Stacks = 1, Duration = 0, Target = "self",
            }], new EffectContext { Actor = k, Target = foe }, 0);
            ActionChoice? forced = BattleRules.ForcedChoice(s, k);
            bool pass = forced is not null && forced.SkillKey == "bloom";
            ok &= pass;
            Console.WriteLine($"  ④) 强制处决：ForcedChoice = {forced?.SkillKey ?? "null"}（应为 bloom） → {(pass ? "通过" : "**失败**")}");
        }

        // ── ⑤ 卡尔权阶Ⅵ：额外回合每自然回合最多 2 次 ──
        {
            BattleState s = NewState("stage-charsim-karni", "char-karni", 6);
            BattleUnit k = s.Units.First(static u => u.DefId == "char-karni");
            for (int i = 0; i < 3; i++)
                EffectResolver.Run(s, [new EffectDef { Type = "extraTurn", Target = "self" }],
                    new EffectContext { Actor = k, Target = k }, 0);
            int used = s.ExtraTurnsThisRound.GetValueOrDefault(k.Serial);
            bool pass = used == 2;
            ok &= pass;
            Console.WriteLine($"  ⑤) 额外回合上限：3 次触发后计数 {used}（应为 2） → {(pass ? "通过" : "**失败**")}");
        }

        // ── ⑥ 薇拉「借你羽梢一瞬」：下一次绽放复制增益时长 / 层数翻倍并消耗 ──
        {
            BattleState s = NewState("stage-charsim-vera", "char-vera", 0);
            BattleUnit v = s.Units.First(static u => u.DefId == "char-vera");
            BattleUnit mate = s.Units.First(static u => u.DefId == "char-shouyueren");
            EffectResolver.Run(s, [new EffectDef
            {
                Type = "applyBuff", BuffId = "buff-vera-borrowed-wing", Stacks = 1, Duration = 2, Target = "self",
            }], new EffectContext { Actor = v, Target = v }, 0);
            EffectResolver.Run(s, [new EffectDef
            {
                Type = "applyBuff", BuffId = "buff-starlink", Stacks = 1, Duration = 3, Target = "mainTarget",
            }], new EffectContext { Actor = v, Target = mate }, 0);
            Runner.ExecuteSkill(s, v, "bloom", v.Skills["bloom"], mate, 0);
            bool doubled = s.Log.Any(static l => l.Contains("借你羽梢一瞬：时长 / 层数翻倍", StringComparison.Ordinal));
            bool consumed = !v.Statuses.Any(static st => st.Def.DoubleNextBloom);
            bool pass = doubled && consumed;
            ok &= pass;
            Console.WriteLine($"  ⑥) 绽放翻倍：日志翻倍 {(doubled ? "有" : "**没有**")} · 标记消耗 {(consumed ? "是" : "**否**")} → {(pass ? "通过" : "**失败**")}");
        }

        // ── ⑦ 薇拉：5 层狩猎印记 + 火枪手剩余 <2 回合，强化普攻立即重发一次 ──
        {
            BattleState s = NewState("stage-charsim-vera", "char-vera", 0);
            BattleUnit v = s.Units.First(static u => u.DefId == "char-vera");
            BattleUnit foe = s.Units.First(static u => u.IsEnemy);
            EffectResolver.Run(s, [new EffectDef
            {
                Type = "applyBuff", BuffId = "buff-vera-gunslinger", Stacks = 1, Duration = 3, Target = "self",
            }], new EffectContext { Actor = v, Target = v }, 0);
            StatusInstance? gunslinger = v.Statuses.FirstOrDefault(static st => st.Def.Name == "火枪手");
            if (gunslinger is not null) gunslinger.Remaining = 1.5;
            v.GainStack("狩猎印记", 5, 5);
            Runner.ExecuteSkill(s, v, "enhancedBasic", v.Skills["enhancedBasic"], foe, 0);
            int recasts = s.Log.Count(static l => l.Contains("立即重新发动一次", StringComparison.Ordinal));
            int casts = s.Log.Count(static l => l.Contains("释放【狩猎，从不会止步】", StringComparison.Ordinal));
            bool pass = recasts == 1 && casts == 2;
            ok &= pass;
            Console.WriteLine($"  ⑦) 5 层追发：重发日志 {recasts} 条 / 技能释放 {casts} 次（应为 1 / 2） → {(pass ? "通过" : "**失败**")}");
        }

        // ── ⑧ 薇拉权阶Ⅱ：onBattleStart 直接召唤余烬天龙 ──
        {
            BattleState s = NewState("stage-charsim-vera", "char-vera", 2);
            Runner.Begin(s);
            BattleUnit v = s.Units.First(static u => u.DefId == "char-vera");
            bool pass = v.GuardianSummon is not null;
            ok &= pass;
            Console.WriteLine($"  ⑧) 权阶Ⅱ开局召唤：天龙 {(v.GuardianSummon is null ? "**没有**" : "在场")} → {(pass ? "通过" : "**失败**")}");
        }

        Console.WriteLine($"  → {(ok ? "通过" : "**失败**")}");
        Console.WriteLine();
        return ok;
    }

    private static bool VerifyEnemyMechanics(GameData data)    {
        Console.WriteLine("[验算 4] 04 §7 的敌方机制（509 累积模糊 / 502 完全苏醒 / 411 注目破绽）");
        bool ok = true;

        // ── 509「失焦者」：体液累积 ⇒ 第 2 次覆盖起模糊延长到 4 单位回合，第三次不再加重 ──
        {
            BattleState s = RunEnemyStage(data, "stage-sim-normal", hpMult: 30.0, seed: 20260815UL);
            int first = s.Log.Count(static l => l.Contains("获得【视觉模糊】（2 turn）", StringComparison.Ordinal));
            int deep = s.Log.Count(static l => l.Contains("【视觉模糊】→ 2 层（刷新 4）", StringComparison.Ordinal));
            bool capped = !s.Log.Any(static l => l.Contains("【视觉模糊】→ 3 层", StringComparison.Ordinal));
            StatusDef? blur = data.Status("debuff-blur");
            bool slow = blur is not null &&
                        blur.Mods.Exists(static m => m.Kind == StatKind.Speed && m.Op == ModOp.Pct && m.Value < 0);

            // ⚠️ `deep` 同时是**「全体刷新」那条回归线**：`ApplyStatus` 曾经在"目标身上已有该状态"时
            // 用 `return` 退出整个方法，于是 4 个队友里只有 1 号位被刷新（日志看起来毫无异常）。
            // 覆膜是**对全体**的，所以第 2 次覆盖必须在**4 个人身上各留一行**。
            int partySize = data.Stage("stage-sim-normal").Party.Count;
            bool allRefreshed = deep >= partySize;
            bool pass = first > 0 && allRefreshed && capped && slow;
            ok &= pass;
            Console.WriteLine($"    509 首次覆盖（1 层 · 刷新 2）  : {first} 行");            Console.WriteLine($"    第 2 次覆盖起延长到 4 回合     : {deep}/{partySize} 人 {(allRefreshed ? "通过" : "**失败**（累积没生效，或只有第一个目标被刷新）")}");
            Console.WriteLine($"    第 3 次不再加重（无 3 层）     : {(capped ? "通过" : "**失败**")}");
            Console.WriteLine($"    行动迟滞（每层速度 −10%）      : {(slow ? "通过" : "**失败**")}");
        }

        // ── 502「铅灰傀儡」：3 层「代偿」= 完全苏醒（质变，不只是第 3 个 +10%） ──
        {
            BattleState s = RunEnemyStage(data, "stage-sim-elite", hpMult: 30.0, seed: 20260815UL);
            BattleUnit? puppet = s.Units.FirstOrDefault(static u => u.DefId == "anom-ccrf-502");
            bool awakened = s.Log.Any(static l => l.Contains("完全苏醒", StringComparison.Ordinal));
            double dr = puppet?.Stats.DamageReductionMax ?? 0.0;
            double speed = puppet?.Stats.SpeedValue ?? 0.0;
            bool hardened = Math.Abs(dr - 0.20) < 1e-9;
            bool fast = speed > 85.0;                       // 502 的 base.speed = 85

            bool pass = awakened && hardened && fast;
            ok &= pass;
            Console.WriteLine($"    第 3 层触发「完全苏醒」        : {(awakened ? "通过" : "**失败**（质变没触发）")}");
            Console.WriteLine($"    外壳硬化（减伤 20%）           : {dr:P0} {(hardened ? "通过" : "**失败**")}");
            Console.WriteLine($"    移速 85 → {speed:0.#}            : {(fast ? "通过" : "**失败**")}");
        }

        // ── 411「高塔凝视者」：注目攒到 3 层 ⇒ 贯穿穿刺；非攻击行动使注目 −1（裁决 99 D0） ──
        {
            // 这里**不走一整场混战**，而是把「观测锥面」按同一目标连放三次：
            // 混战里 411 按仇恨加权随机选敌，三次未必落在同一个人身上 ——
            // 那验的是运气，不是机制。直接驱动技能才能把"同一锥面周期内覆盖三次"这个前提摆出来。
            BattleState s = BattleFactory.Create(new BattleConfig
            {
                Data = data, Stage = data.Stage("stage-sim-boss"), Seed = 20260815UL,
            });
            BattleUnit? boss = s.Units.FirstOrDefault(static u => u.DefId == "anom-ccrf-411");
            // 挑**血最厚**的那个当承伤位，并且每次出手前把血补满 ——
            // 411 单次锥面约 3900 伤害，正常配装下同一个人挨不下三次；
            // 这里要验的是"覆盖三次会不会攒到 3 层"，不是"能不能活过三次"（那是 `99 D0` 的另一个话题）。
            BattleUnit? victim = s.Units.Where(static u => u.IsEnemy == false)
                                        .OrderByDescending(static u => u.MaxHp)
                                        .FirstOrDefault();

            int gaze = 0, gazeAfterDecay = 0;
            bool pierceReady = false;
            if (boss is not null && victim is not null)
            {
                SkillDef cone = boss.Skills["skill"];
                for (int i = 0; i < 3; i++)
                {
                    victim.Hp = victim.MaxHp;               // 测试脚手架：不让承伤位中途倒下
                    Runner.ExecuteSkill(s, boss, "skill", cone, victim, 0);
                }
                gaze = victim.StatusStacks("注目");

                // 「满 3 层 ⇒ 基座定向延展并发动贯穿性穿刺」在实现上走的是**能量**：
                // 每次锥面 +30 能量、贯穿穿刺耗 90 ⇒ 覆盖三次正好攒满一次穿刺
                // （敌方 AI 的优先级是 终结技 > 技能 > 普攻，见 Runner.ActEnemy）。
                // 这条断言钉住的正是这个等式 —— 任何一边改了（+30 或 90），这里立刻红。
                int cost = (int)boss.Skills["ultimate"].Cost.Energy;
                pierceReady = boss.Energy >= cost;
                Console.WriteLine($"    3 次锥面后：注目 {gaze}/3，能量 {boss.Energy}/{cost}（穿刺就绪）");

                // 「破绽」：非攻击行动使注目 −1（`99 D0`）。
                // ⚠️ **当前落盘的 4 名角色没有任何"无伤害段"的技能**（全是输出角色），
                // 所以真实的非攻击行动在本轮数据里不存在 —— 这条破绽要等治疗 / 护盾角色落盘
                // 才可能在实战里被触发（已记入 `100`）。但"钩子 → 触发 → 原子"这条链现在就得验。
                HookBus.Fire(s, "onNonAttackAction", new HookContext { Actor = victim, Target = victim }, 0);
                gazeAfterDecay = victim.StatusStacks("注目");
            }

            bool stacked = gaze == 3;
            bool decayed = gaze == 3 && gazeAfterDecay == 2;
            bool pass = stacked && pierceReady && decayed;
            ok &= pass;
            Console.WriteLine($"    同一目标覆盖三次 → 注目满 3  : {(stacked ? "通过" : "**失败**（攒不到 3 层）")}");
            Console.WriteLine($"    注目满 3 ⇒ 贯穿穿刺可出手     : {(pierceReady ? "通过" : "**失败**")}");
            Console.WriteLine($"    非攻击行动使注目 {gaze} → {gazeAfterDecay}        : {(decayed ? "通过" : "**失败**（破绽没接上）")}");
        }

        Console.WriteLine($"  → {(ok ? "通过" : "**失败**")}");
        Console.WriteLine();
        return ok;
    }

    // ── validate（M3 数据校验器） ──────────────────────────────────────

    /// <summary>
    /// `06 §8.2` 的 M3 验收：**0 错误**。
    /// </summary>
    /// <remarks>
    /// 警告**不影响通过** —— 它们标的是"当前有意为之、但需要人看一眼"的事
    /// （例如引用了 M7 才实装的装备套装）。错误必须修。
    /// </remarks>
    private static int Validate(Dictionary<string, string> opt)
    {
        Banner();
        GameData data = LoadData(opt);

        ValidationReport report = DataValidator.Validate(data);

        Console.WriteLine($"共执行 {report.ExecutedChecks.Count} 条校验：" + string.Join(" · ", report.ExecutedChecks));
        Console.WriteLine();

        if (report.Issues.Count == 0)
        {
            Console.WriteLine("没有任何问题。");
        }
        else
        {
            // 错误在前、警告在后，各自按编号排序 —— 顺序固定，便于 diff。
            IEnumerable<ValidationIssue> ordered = report.Issues
                .OrderBy(static i => i.Level)
                .ThenBy(static i => i.Check, StringComparer.Ordinal)
                .ThenBy(static i => i.Where, StringComparer.Ordinal);

            bool quiet = opt.ContainsKey("--quiet");
            foreach (ValidationIssue issue in ordered)
            {
                if (quiet && issue.Level == IssueLevel.Info) continue;
                Console.WriteLine("  " + issue);
            }
            Console.WriteLine();
        }

        Console.WriteLine($"[M3 验收摘要]");
        Console.WriteLine($"  错误 : {report.Errors}");
        Console.WriteLine($"  警告 : {report.Warnings}");
        Console.WriteLine($"  提示 : {report.Infos}");
        Console.WriteLine();
        Console.WriteLine(report.Ok
            ? "M3 验收：**通过** —— 0 错误。"
            : $"M3 验收：**失败** —— {report.Errors} 个错误待修。");

        // `--json <path>`：机器可读的同一份报告。
        // **为什么要它**：控制台输出是 UTF-8，而 PowerShell 管道会按控制台码页解码，
        // 于是任何"拿中文去匹配"的自动化（例如故障注入测试）都会静默地永远匹配不上。
        // 让自动化读 JSON、只按 ASCII 的 check id 判定，就不会有这个问题。
        if (opt.TryGetValue("--json", out string? jsonPath) && jsonPath.Length > 0)
        {
            string full = Path.GetFullPath(jsonPath);
            Directory.CreateDirectory(Path.GetDirectoryName(full) ?? ".");
            var payload = new
            {
                ok = report.Ok,
                errors = report.Errors,
                warnings = report.Warnings,
                infos = report.Infos,
                executedChecks = report.ExecutedChecks,
                issues = report.Issues.Select(static i => new
                {
                    check = i.Check,
                    level = i.Level.ToString(),
                    where = i.Where,
                    message = i.Message,
                    hint = i.Hint,
                }),
            };
            File.WriteAllText(full, System.Text.Json.JsonSerializer.Serialize(payload,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            Console.WriteLine($"报告已写入：{jsonPath}");
        }

        return report.Ok ? 0 : 1;
    }

    // ── batch（M2 平衡实测） ──────────────────────────────────────────

    /// <summary>
    /// `06 §8.2` 的 M2 验收：**四档投入 × 四个靶场 × N 个种子**的批量模拟。
    /// </summary>
    /// <remarks>
    /// <b>为什么要分档</b>：`02 §8.3` 第 8 条 —— "任何平衡结论必须写明投入档位，跨档位比较一律无效"。
    /// 所以 batch 的每一行**必须**带档位，否则那行数字没有意义。
    /// <para>
    /// <b>口径</b>：只换面板，不换技能、不换关卡、不换敌人（见 <c>BattleConfig.Tier</c>）。
    /// 计分用**真正扣掉的血**而不是管线原始伤害 —— 否则最后一击的斩杀溢出会按目标血量上限虚增 DPR。
    /// </para>
    /// </remarks>
    private static int Batch(Dictionary<string, string> opt)
    {
        Banner();
        GameData data = LoadData(opt);

        int runs = int.TryParse(opt.GetValueOrDefault("--runs"), out int n) ? Math.Max(1, n) : 64;
        ulong baseSeed = ulong.TryParse(opt.GetValueOrDefault("--seed"), out ulong s) ? s : 20260815UL;
        string? onlyTier = opt.GetValueOrDefault("--tier");
        string? onlyStage = opt.GetValueOrDefault("--stage");

        List<InvestmentTierDef> tiers = [.. data.Tiers.Tiers.Where(t => onlyTier is null || t.Id == onlyTier)];
        if (tiers.Count == 0)
        {
            Console.Error.WriteLine($"没有匹配的档位：'{onlyTier}'。可用：" +
                                    string.Join(" / ", data.Tiers.Tiers.Select(static t => t.Id)));
            return 2;
        }

        List<StageDef> stages = [.. data.Stages.Values
            .Where(st => st.Kind == "sim")
            .Where(st => onlyStage is null || st.Id == onlyStage)
            .OrderBy(static st => st.Id, StringComparer.Ordinal)];

        if (stages.Count == 0)
        {
            Console.Error.WriteLine("没有可跑的靶场（kind = \"sim\"）。检查 data/stages/stage-sim-*.json。");
            return 2;
        }

        Console.WriteLine($"[M2 分档批量模拟]  档位 {tiers.Count} × 靶场 {stages.Count} × 种子 {runs} 场" +
                          $"（seed {baseSeed} … {baseSeed + (ulong)runs - 1}）");
        Console.WriteLine($"  技能等级 : {data.Tiers.SkillLevel}（0 权阶上限，02 §4）" +
                          "    计分口径 : 真正扣掉的血（不含斩杀溢出）");
        Console.WriteLine("  轮数口径 : 战斗耗时 ÷ 100 AV（**不是** state.Round —— 那会在一回合内打完时显示 0）");
        Console.WriteLine();

        // 跨档位汇总的明细（M2 的"反推异常层倍率"就靠这几张表）。
        Dictionary<Element, double> anomalyByElement = [];
        Dictionary<string, CharRow> byCharacter = new(StringComparer.Ordinal);
        Dictionary<AnomalyFamily, int> anomalyByFamily = [];

        List<TierStageResult> results = [];
        foreach (StageDef stage in stages)
        {
            int enemyHp = (int)stage.Enemies.Sum(id => data.Enemy(id).Base.Hp);
            string enemyName = string.Join(" + ", stage.Enemies.Select(e => data.Enemy(e).Name));
            Console.WriteLine($"━━ {stage.Id}（{stage.Name}）· {enemyName} · 合计 {enemyHp:N0} 血 · 上限 {stage.MaxRound} 轮");
            Console.WriteLine("   档位          通关率   耗时轮     对敌总伤      DPR/轮   直伤%   异常%   紊乱%   异常次  阵亡");

            foreach (InvestmentTierDef tier in tiers)
            {
                TierStageResult r = RunBatch(data, stage, tier, runs, baseSeed, enemyHp,
                    anomalyByElement, byCharacter, anomalyByFamily);
                results.Add(r);
                Console.WriteLine("   " + r.Line());
            }
            Console.WriteLine();
        }

        PrintTierSummary(results, tiers);
        PrintDamageBreakdown(data, results, anomalyByElement, byCharacter, anomalyByFamily);
        int verdict = PrintConstraintChecks(results);
        return verdict;
    }

    /// <summary>一个（档位 × 靶场）的实测结果。</summary>
    private sealed record TierStageResult(
        string TierId, string TierName, string StageId, string StageName,
        int Runs, int Wins, double Rounds, double Damage, int EnemyHp, int MaxRound,
        double Direct, double Anomaly, double Disorder, double Additional, double Replay,
        double Disrupts, double Anomalies, double AllyDeaths, double SkillHits, double EligibleHits,
        double CritHits, double Taken)
    {
        public double WinRate => Runs == 0 ? 0 : (double)Wins / Runs;

        /// <summary>每"自然回合"的伤害。分母用**战斗耗时 ÷ 100 AV** —— 用 <c>state.Round</c> 会在一回合内打完时除零。</summary>
        public double Dpr => Rounds <= 1e-9 ? 0 : Damage / Rounds;

        /// <summary>暴击率实测值（确认面板暴击真的生效，而不是写了没读）。分母只算**可暴击**的实例。</summary>
        public double CritRateActual => EligibleHits <= 0 ? 0 : CritHits / EligibleHits;

        /// <summary>`02 §8.3` 第 4 条要的"大毕业对高难首领 ≤9 轮"用这一列判。</summary>
        public double ClearRounds => WinRate >= 0.5 ? Rounds : double.PositiveInfinity;

        public string Line()
        {
            double total = Math.Max(1e-9, Damage);
            return $"{TierName,-12} {WinRate,6:P0} {Rounds,8:0.00} {Damage,12:N0} {Dpr,11:N0} " +
                   $"{Direct / total,8:P0} {Anomaly / total,8:P0} {Disorder / total,8:P0} " +
                   $"{Anomalies,8:0.0} {AllyDeaths,8:0.0}";
        }
    }

    /// <summary>一个我方角色的累计贡献（跨场次）。</summary>
    private sealed class CharRow
    {
        public double Damage;
        public int Hits;
        public int Eligible;
        public int Crits;
    }

    private static TierStageResult RunBatch(
        GameData data, StageDef stage, InvestmentTierDef tier, int runs, ulong baseSeed, int enemyHp,
        Dictionary<Element, double> anomalyByElement,
        Dictionary<string, CharRow> byCharacter,
        Dictionary<AnomalyFamily, int> anomalyByFamily)
    {
        int wins = 0;
        double rounds = 0, damage = 0, direct = 0, anomaly = 0, disorder = 0, additional = 0, replay = 0;
        double disrupts = 0, anomalies = 0, allyDeaths = 0, hits = 0, eligible = 0, crits = 0, taken = 0;

        for (int i = 0; i < runs; i++)
        {
            BattleState state = BattleFactory.Create(new BattleConfig
            {
                Data = data,
                Stage = stage,
                Seed = baseSeed + (ulong)i,
                Tier = tier,
                SkillLevelOverride = data.Tiers.SkillLevel,
                AllowOverdraw = true,
            });
            BattleOutcome outcome = Runner.Run(state, new GreedyPolicy { AllowOverdraw = true });

            if (outcome == BattleOutcome.AllyWin) wins++;
            rounds += state.ElapsedAv / BattleConstants.RoundAv;
            BattleMetrics m = state.Metrics;
            damage += m.HpRemoved;
            direct += m.CategoryOf(DamageCategory.Direct);
            anomaly += m.CategoryOf(DamageCategory.Anomaly);
            disorder += m.CategoryOf(DamageCategory.Disorder);
            additional += m.CategoryOf(DamageCategory.Additional);
            replay += m.CategoryOf(DamageCategory.Replay);
            disrupts += m.DisorderTriggers;
            anomalies += m.AnomalyTriggers;
            allyDeaths += m.AllyDeaths;
            hits += m.HitInstances;
            eligible += m.CritEligibleInstances;
            crits += m.CritInstances;
            taken += m.HpLost;

            foreach (KeyValuePair<Element, double> kv in m.AnomalyDamageByElement)
                anomalyByElement[kv.Key] = anomalyByElement.GetValueOrDefault(kv.Key) + kv.Value;
            foreach (KeyValuePair<AnomalyFamily, int> kv in m.AnomalyByFamily)
                anomalyByFamily[kv.Key] = anomalyByFamily.GetValueOrDefault(kv.Key) + kv.Value;
            foreach (BattleMetrics.SourceRow row in m.BySource.Values)
            {
                if (!byCharacter.TryGetValue(row.Name, out CharRow? cr))
                {
                    cr = new CharRow();
                    byCharacter[row.Name] = cr;
                }
                cr.Damage += row.HpRemoved;
                cr.Hits += row.Hits;
                cr.Eligible += row.Eligible;
                cr.Crits += row.Crits;
            }
        }

        double k = runs;
        return new TierStageResult(
            tier.Id, tier.Name, stage.Id, stage.Name, runs, wins, rounds / k, damage / k, enemyHp, stage.MaxRound,
            direct / k, anomaly / k, disorder / k, additional / k, replay / k,
            disrupts / k, anomalies / k, allyDeaths / k, hits / k, eligible / k, crits / k, taken / k);
    }

    /// <summary>按档位汇总（跨靶场），用于看"这一档整体是什么水平"。</summary>
    /// <remarks>
    /// ⚠️ DPR 用**加权**（总伤害 ÷ 总耗时轮）而不是"各靶场 DPR 的算术平均"：
    /// 后者会被"一回合内速通杂兵"那一场拉高（分母只有 1 轮），得到的数字比这一档真实水平高一截。
    /// </remarks>
    private static void PrintTierSummary(List<TierStageResult> results, List<InvestmentTierDef> tiers)
    {
        Console.WriteLine("━━ 档位汇总（跨靶场 · 加权）");
        Console.WriteLine("   档位          平均通关率  平均耗时轮     加权 DPR   直伤   异常   紊乱   实测暴击率");
        foreach (InvestmentTierDef tier in tiers)
        {
            List<TierStageResult> rows = [.. results.Where(r => r.TierId == tier.Id)];
            if (rows.Count == 0) continue;
            double total = Math.Max(1e-9, rows.Sum(static r => r.Damage * r.Runs));
            double rounds = rows.Sum(static r => r.Rounds * r.Runs);
            double damage = rows.Sum(static r => r.Damage * r.Runs);
            double hits = rows.Sum(static r => r.EligibleHits);
            Console.WriteLine($"   {tier.Name,-12} {rows.Average(static r => r.WinRate),9:P0} " +
                              $"{rows.Average(static r => r.Rounds),10:0.00} " +
                              $"{(rounds <= 0 ? 0 : damage / rounds),12:N0} " +
                              $"{rows.Sum(static r => r.Direct * r.Runs) / total,6:P0} " +
                              $"{rows.Sum(static r => r.Anomaly * r.Runs) / total,6:P0} " +
                              $"{rows.Sum(static r => r.Disorder * r.Runs) / total,6:P0} " +
                              $"{(hits <= 0 ? 0 : rows.Sum(static r => r.CritHits) / hits),11:P0}");
        }
        Console.WriteLine();
    }

    /// <summary>
    /// 伤害构成明细：**这就是 `100` 甲7 / 丁5 要的"用实测反推异常层倍率"的原料**。
    /// </summary>
    private static void PrintDamageBreakdown(
        GameData data,
        List<TierStageResult> results,
        Dictionary<Element, double> anomalyByElement,
        Dictionary<string, CharRow> byCharacter,
        Dictionary<AnomalyFamily, int> anomalyByFamily)
    {
        double allDamage = results.Sum(static r => r.Damage * r.Runs);
        if (allDamage <= 0) return;

        // ⚠️ 这里必须用**总量**（平均 × 场数）而不是平均值 ——
        //    下面几张累计表记的是"所有场次加总"，拿平均值当分母会得到 293% 这种数字。
        Console.WriteLine("━━ 伤害构成（全部档位 × 全部靶场累计）");
        double direct = results.Sum(static r => r.Direct * r.Runs);
        double anomaly = results.Sum(static r => r.Anomaly * r.Runs);
        double disorder = results.Sum(static r => r.Disorder * r.Runs);
        double additional = results.Sum(static r => r.Additional * r.Runs);
        double replay = results.Sum(static r => r.Replay * r.Runs);
        Console.WriteLine($"   直伤 {direct / allDamage,7:P1}（{direct,14:N0}）");
        Console.WriteLine($"   属性异常 {anomaly / allDamage,3:P1}（{anomaly,14:N0}）" +
                          $"   ← 「异常 vs 直伤」的占比就是甲7 争的那件事");
        Console.WriteLine($"   元素紊乱 {disorder / allDamage,3:P1}（{disorder,14:N0}）");
        if (additional > 0) Console.WriteLine($"   附加伤害 {additional / allDamage,3:P1}（{additional,14:N0}）");
        if (replay > 0) Console.WriteLine($"   复伤回放 {replay / allDamage,3:P1}（{replay,14:N0}）");
        Console.WriteLine();

        if (anomalyByElement.Count > 0)
        {
            Console.WriteLine("   属性异常按属性拆分：");
            foreach (KeyValuePair<Element, double> kv in anomalyByElement.OrderByDescending(static k => k.Value))
            {
                Console.WriteLine($"     {ElementInfo.ToChinese(kv.Key),-4} {kv.Value,14:N0}  占异常伤害 {kv.Value / Math.Max(1e-9, anomaly),6:P0}");
            }
            Console.WriteLine();
        }

        if (anomalyByFamily.Count > 0)
        {
            Console.WriteLine("   异常触发次数按家族拆分（每次进度条满 / 每次直接施加各记 1 次）：");
            foreach (KeyValuePair<AnomalyFamily, int> kv in anomalyByFamily.OrderByDescending(static k => k.Value))
            {
                Console.WriteLine($"     {ElementInfo.FamilyToChinese(kv.Key),-4} {kv.Value,10:N0} 次");
            }
            Console.WriteLine();
        }

        if (byCharacter.Count > 0)
        {
            Console.WriteLine("   我方角色伤害贡献（**跨全部档位合计** —— 所以下面这一列实测暴击率不是单档面板值）：");
            Console.WriteLine("     角色              总伤害     占比    命中  可暴击   实测暴击率");
            foreach (KeyValuePair<string, CharRow> kv in byCharacter.OrderByDescending(static k => k.Value.Damage))
            {
                CharRow c = kv.Value;
                Console.WriteLine($"     {kv.Key,-16} {c.Damage,12:N0} {c.Damage / allDamage,8:P1} {c.Hits,7:N0} {c.Eligible,7:N0} " +
                                  $"{(c.Eligible <= 0 ? 0 : (double)c.Crits / c.Eligible),11:P1}");
            }
            Console.WriteLine();
        }
    }

    /// <summary>`02 §8.3` 的硬约束逐条判定。**返回非 0 表示有一条不成立。**</summary>
    private static int PrintConstraintChecks(List<TierStageResult> results)
    {
        Console.WriteLine("━━ 02 §8.3 硬约束判定");
        int failed = 0;

        // 第 4 条：大毕业 0+1 队伍对高难首领的期望通关时间应 ≤ 9 轮。
        TierStageResult? grad = results.FirstOrDefault(static r => r.TierId == "tier-graduate" && r.StageId.Contains("highboss"));
        if (grad is not null)
        {
            bool ok = grad.ClearRounds <= 9.0;
            if (!ok) failed++;
            Console.WriteLine($"   第 4 条 · 大毕业 vs 高难首领 ≤ 9 轮 : {(ok ? "✅ 成立" : "❌ 不成立")}" +
                              $"（实测 {(double.IsInfinity(grad.ClearRounds) ? "打不过" : $"{grad.ClearRounds:0.0} 轮")}，" +
                              $"通关率 {grad.WinRate:P0}，DPR {grad.Dpr:N0}）");
        }
        else
        {
            Console.WriteLine("   第 4 条 · 大毕业 vs 高难首领 ≤ 9 轮 : ⏭ 跳过（缺少 tier-graduate × highboss 组合）");
        }

        // 第 2 条：血量只允许在档位区间内浮动（这里检查靶场用的怪是否等于 02 §8.2 的表值）。
        Console.WriteLine("   第 2 条 · 敌人血量未越档 : ℹ 由 validate 的 M3-stage 校验负责（本表只读不改）");

        Console.WriteLine();
        Console.WriteLine(failed == 0
            ? "M2 硬约束：**全部成立**。"
            : $"M2 硬约束：**{failed} 条不成立** —— 需要改的是数值或机制，不是这台模拟器。");
        Console.WriteLine("（DPR / 轮数属于实测数据，本身没有「通过 / 失败」；判定只针对 02 §8.3 的硬约束。）");
        return failed == 0 ? 0 : 1;
    }

    // ── loadout（M7 配装折算） ────────────────────────────────────────

    /// <summary>
    /// `M7` 的验收工具：把"真实配装"折成面板，并与 `02 §7` 的锚点逐项对照。
    /// </summary>
    /// <remarks>
    /// <b>为什么这条命令比 `batch` 更重要</b>：`batch` 的档位面板是**插值猜出来的**
    /// （`data/tables/tiers.json`）。真实配装能算之后，"四档投入"就不再是一个人为数字，
    /// 而是一个具体的配装实体 —— 这一条命令就是那个实体的体检报告。
    /// <para>
    /// 它还会顺手回答一个 `M2` 提出的问题（`100` #111）：**按 `02 §6.3` 的装备主属性算出来的
    /// 满配面板，和 `02 §7` 写的锚点到底差多少**。差得多就说明两边不能同时成立，而不是"实现有 bug"。
    /// </para>
    /// </remarks>
    private static int Loadout(Dictionary<string, string> opt)
    {
        Banner();
        GameData data = LoadData(opt);

        Console.WriteLine("[M7 配装折算] 标准配装（主属性按定位取常规解 · 副属性先补双暴到 02 §1.2 的目标值、余下给攻击%）");
        Console.WriteLine("              ⚠️ 这是**标准配装**不是最优配装，读作该档位的下限附近。");
        Console.WriteLine();

        int flagged = 0;
        int nonMonotone = 0;
        foreach (CharacterDef def in data.Characters.Values.OrderBy(static c => c.Id, StringComparer.Ordinal))
        {
            TierRowDef? row = data.Tiers.Row(BattleFactory.AnchorRowOf(def));
            Console.WriteLine($"━━ {def.Name}（{def.Id}）· 锚点行 {BattleFactory.AnchorRowOf(def)}" +
                              $"{(row is null ? "（⚠️ 不在 tiers.json 里）" : $" · {row.Name}")}" +
                              $" · 武器 {def.SignatureWeapon ?? "（无）"}");

            BaseStatsDef? previous = null;
            foreach (CanonicalLoadout.Spec spec in CanonicalLoadout.All)
            {
                Loadout loadout = CanonicalLoadout.Build(data, def, spec, row);
                PanelResult r = PanelCalculator.Compute(data, def, loadout);
                Console.WriteLine($"   {spec.Name,-10} {r}");

                // **档位必须单调**：更高投入不该在任何一条轴上更低。
                // 这条断言抓到过一个真 bug —— 第一版的副属性分配是"队列 + break"，
                // 结果「大毕业的双暴反而低于中配」（档位越高、攻击% 的份额越大，队首被吃掉的顺序不一样）。
                if (previous is not null)
                {
                    List<string> regressions = [];
                    if (r.Panel.Atk < previous.Atk - 0.5) regressions.Add($"攻击 {previous.Atk:0} → {r.Panel.Atk:0}");
                    if (r.Panel.Hp < previous.Hp - 0.5) regressions.Add($"生命 {previous.Hp:0} → {r.Panel.Hp:0}");
                    if (r.Panel.CritRate < previous.CritRate - 1e-6) regressions.Add($"暴击率 {previous.CritRate:P1} → {r.Panel.CritRate:P1}");
                    if (r.Panel.CritDmg < previous.CritDmg - 1e-6) regressions.Add($"面板暴伤 {previous.CritDmg:0.00} → {r.Panel.CritDmg:0.00}");
                    if (regressions.Count > 0)
                    {
                        nonMonotone++;
                        Console.WriteLine($"      ❌ **非单调**：{string.Join(" · ", regressions)}");
                    }
                }
                previous = r.Panel;

                if (r.Unfolded.Count > 0 && spec.Id == "tier-graduate")
                {
                    Console.WriteLine($"      ↳ 未折进面板的特效键（进战斗后才生效，属 M7 的 modifyStat 原子）：" +
                                      string.Join(" · ", r.Unfolded.Distinct(StringComparer.Ordinal)));
                }
            }

            // 与 02 §7 的锚点对照：**这一档就是 M2 用过的「大毕业」**。
            if (row is not null)
            {
                Loadout grad = CanonicalLoadout.Build(data, def, CanonicalLoadout.TierGraduate, row);
                BaseStatsDef p = PanelCalculator.Compute(data, def, grad).Panel;
                double dAtk = p.Atk / Math.Max(1.0, row.AtkFull) - 1.0;
                double dHp = p.Hp / Math.Max(1.0, row.HpFull) - 1.0;
                Console.WriteLine($"   ↳ 对照 02 §7 满配锚点：攻击 {p.Atk:0} vs {row.AtkFull:0}（{dAtk:+0.0%;-0.0%}） · " +
                                  $"生命 {p.Hp:0} vs {row.HpFull:0}（{dHp:+0.0%;-0.0%}）");
                if (Math.Abs(dAtk) > 0.15 || Math.Abs(dHp) > 0.15)
                {
                    flagged++;
                    Console.WriteLine("      ⚠️ 偏差超过 15% —— 这不是实现 bug，是 **§7 的锚点与 §6.3 的装备主属性不能同时成立**（100 #111）");
                }
            }
            Console.WriteLine();
        }

        Console.WriteLine(flagged == 0
            ? "M7 配装折算：**全部在 15% 以内** —— §7 的锚点与 §6.3 的装备规则自洽。"
            : $"M7 配装折算：**{flagged} 个角色的满配面板与 02 §7 偏差超过 15%** —— 见 `100` #111，需要一次裁决。");
        Console.WriteLine(nonMonotone == 0
            ? "档位单调性：**通过**（攻击 / 生命 / 双暴在四个档位之间单调不减）。"
            : $"档位单调性：**失败** —— {nonMonotone} 处非单调，说明副属性分配有问题。");

        // ── 装备效果真的进战斗了吗 ────────────────────────────────────
        //
        // 这是**这条链唯一的端到端断言**：面板算得再对，如果武器特效与套装效果没接进战斗，
        // M7 就只做了一半 —— 而"面板对、效果没生效"这种状态在界面上看起来毫无异常。
        int equipFail = VerifyEquipEffects(data);
        return nonMonotone == 0 && equipFail == 0 ? 0 : 1;
    }

    /// <summary>跑一场足够长的战斗，断言武器 / 套装效果**真的挂上去了**。</summary>
    private static int VerifyEquipEffects(GameData data)
    {
        StageDef stage = data.Stage("stage-sim-elite");
        Dictionary<string, Loadout> loadouts = new(StringComparer.Ordinal);
        foreach (string pid in stage.Party)
        {
            CharacterDef def = data.Character(pid);
            loadouts[pid] = CanonicalLoadout.Build(data, def, CanonicalLoadout.TierGraduate,
                data.Tiers.Row(BattleFactory.AnchorRowOf(def)));
        }

        // 血量倍率拉高是**为了让每个人都放得出终结技** —— 武器特效大多挂在"释放 X 后"上，
        // 一场两回合就结束的战斗根本走不到那些钩子（第一版就是这么把它误判成"没生效"的）。
        BattleState state = BattleFactory.Create(new BattleConfig
        {
            Data = data,
            Stage = stage,
            Seed = 20260815UL,
            EnemyHpMultiplier = 20.0,
            Loadouts = loadouts,
        });
        Runner.Run(state, new GreedyPolicy());

        int attached = state.Units.Sum(u => u.Statuses.Count(s => s.Def.Id.StartsWith(StatusFactory.Prefix, StringComparison.Ordinal)));
        bool weaponFired = state.Log.Any(l => l.Contains("原初@", StringComparison.Ordinal));
        bool setFired = state.Log.Any(static l => l.Contains("本场持续", StringComparison.Ordinal));

        // **机制旋钮被真的读到了吗**：焰剑的 `extraCombustChance` 会出现在爆燃判定的日志里。
        // 这一条比"状态挂上了"更强 —— 状态挂上但没人读，照样是个静默失效。
        bool knobConsumed = state.Log.Any(static l => l.Contains("装备额外", StringComparison.Ordinal));

        Console.WriteLine();
        Console.WriteLine($"装备效果端到端：场上挂了 {attached} 个合成状态 · " +
                          $"武器五段特效 {(weaponFired ? "已触发" : "**未触发**")} · " +
                          $"套装常驻 {(setFired ? "已生效" : "**未生效**")} · " +
                          $"机制旋钮 {(knobConsumed ? "已被消费点读到" : "**没被读到**")}");
        bool ok = attached > 0 && weaponFired && setFired && knobConsumed;
        Console.WriteLine(ok
            ? "⇒ **通过**：配装不只是换了面板，武器特效、套装效果与机制旋钮确实都进了战斗。"
            : "⇒ **失败**：效果没进战斗（面板对而效果不生效，界面上看不出来）。");
        return ok ? 0 : 1;
    }

    // ── gacha（保底校准） ─────────────────────────────────────────────

    /// <summary>
    /// 扫 `GACHA_SOFT_PITY_STEP`，找出"综合出货 ≈ 1/62.5 抽"的那一档（`02 §10` 的待校准项）。
    /// </summary>
    /// <remarks>
    /// 出货率与保底完全独立于战斗，所以这条**不需要引擎**：一个循环就够了。
    /// 它存在的意义是把"待 M2 校准"这个占位符真正填掉 —— 首版 `0.06` 实测 1/50.0 抽，偏慷慨。
    /// </remarks>
    private static int Gacha(Dictionary<string, string> opt)
    {
        Banner();
        GameData data = LoadData(opt);

        int runs = int.TryParse(opt.GetValueOrDefault("--sim"), out int n) ? Math.Max(1000, n) : 200_000;
        double target = 62.5;
        double hard = data.Gacha.Rates.HardPity;
        double baseRate = data.Gacha.Rates.Five;
        double softStart = data.Gacha.Rates.SoftPityStart;

        Console.WriteLine($"[抽卡保底校准] 目标综合出货 1/{target:0.0} 抽 · 蒙特卡洛 {runs:N0} 次 · " +
                          $"基础概率 {baseRate:P1} · 软保底起点 {softStart:0} · 硬保底 {hard:0}");
        Console.WriteLine();
        Console.WriteLine("   每抽增量   平均出货间隔   相对目标");
        List<(double Step, double Expected)> rows = [];
        foreach (double step in new[] { 0.0, 0.002, 0.005, 0.01, 0.015, 0.02, 0.03, 0.04, 0.06 })
        {
            double expected = SimulateInterval(runs, baseRate, softStart, hard, step);
            rows.Add((step, expected));
            Console.WriteLine($"   {step,8:0.000} {expected,12:0.00} {expected / target,10:P0}");
        }
        Console.WriteLine();

        (double Step, double Expected) best = rows.OrderBy(r => Math.Abs(r.Expected - target)).First();
        Console.WriteLine($"最接近 1/{target:0.0} 的是 softPityStep = {best.Step:0.000}（实测 1/{best.Expected:0.0} 抽）。");
        Console.WriteLine();
        Console.WriteLine("⚠️ **这张表说的是反直觉的一件事**：目标 62.5 抽是**靠「几乎不做软保底」达到的**，不是靠加大爬坡。");
        Console.WriteLine("   硬保底 80 + 基础概率 0.6% 本身就给出 E = Σ(0.994^k) ≈ 63.7 抽；");
        Console.WriteLine("   任何软保底都只会把这个期望**往下拉**（0.02 就已经掉到 51.8）。");
        Console.WriteLine($"   ⇒ `02 §10` 的 GACHA_SOFT_PITY_STEP 应当定为 **{best.Step:0.000}**，");
        Console.WriteLine("     并把 03 §4.3 的「首版 0.06 偏慷慨」改成实测口径。");
        return 0;
    }

    /// <summary>从零保底开始连续抽，返回"平均多少抽出一个五星"。</summary>
    private static double SimulateInterval(int runs, double baseRate, double softStart, double hard, double step)
    {
        Rng rng = new(20260815UL);
        long total = 0;
        int pity = 0;
        int fives = 0;
        for (int i = 0; i < runs; i++)
        {
            pity++;
            double chance = pity >= hard
                ? 1.0
                : pity >= softStart ? Math.Min(1.0, baseRate + (pity - softStart + 1) * step) : baseRate;
            total++;
            if (!rng.Chance(chance)) continue;
            fives++;
            pity = 0;
        }
        return fives == 0 ? double.PositiveInfinity : (double)total / fives;
    }


    private static GameData LoadData(Dictionary<string, string>? opt = null)
    {
        // `--data <dir>` 指向另一份 data/ 树。**故障注入测试靠它**：
        // 校验器的"它真的能抓到错"这件事，只能靠在坏数据上跑一遍来证明。
        string? root = opt is not null && opt.TryGetValue("--data", out string? dir) && dir.Length > 0 ? dir : null;
        GameData data = root is null ? GameData.Load() : GameData.Load(root);
        Console.WriteLine($"数据 : {data.Describe()}");
        if (data.Diagnostics.Count > 0)
        {
            Console.WriteLine("数据加载告警：");
            foreach (string d in data.Diagnostics) Console.WriteLine("  ! " + d);
        }
        Console.WriteLine();
        return data;
    }

    private static void Banner()
    {
        Console.WriteLine("未尽之誓 · Waning Oath — 模拟器 (WaningOath.Sim)");
        Console.WriteLine($"  构建阶段 : {Stage0}");
        Console.WriteLine($"  Core     : {typeof(Rng).Assembly.GetName().Name} {typeof(Rng).Assembly.GetName().Version}");
        Console.WriteLine($"  属性枚举 : {string.Join(" / ", Enum.GetNames<Element>())}");
        Console.WriteLine();
    }

    private static bool Check(string label, double actual, double expected, double tolerance = 0.5)
    {
        bool ok = Math.Abs(actual - expected) <= tolerance;
        Console.WriteLine($"    {(ok ? "OK  " : "FAIL")} {label}  → 实际 {actual:0.###}");
        return ok;
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        Dictionary<string, string> result = new(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith('-')) continue;
            if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
            {
                result[args[i]] = args[i + 1];
                i++;
            }
            else
            {
                result[args[i]] = "";
            }
        }
        return result;
    }

    private static bool DeterminismSelfCheck()
    {
        const ulong seed = 20260815UL;
        const int count = 8;

        Rng a = new(seed);
        Rng b = new(seed);
        Rng c = new(seed + 1);

        ulong[] seqA = new ulong[count];
        ulong[] seqB = new ulong[count];
        ulong[] seqC = new ulong[count];
        for (int i = 0; i < count; i++)
        {
            seqA[i] = a.NextU64();
            seqB[i] = b.NextU64();
            seqC[i] = c.NextU64();
        }

        bool sameSeedMatches = seqA.AsSpan().SequenceEqual(seqB);
        bool diffSeedDiffers = !seqA.AsSpan().SequenceEqual(seqC);

        Console.WriteLine("[确定性自检]");
        Console.WriteLine($"  种子 {seed} 的两条流一致      : {(sameSeedMatches ? "通过" : "**失败**")}");
        Console.WriteLine($"  种子 {seed + 1} 的流与之不同 : {(diffSeedDiffers ? "通过" : "**失败**")}");
        Console.WriteLine($"  前 {count} 个值 : {string.Join(", ", seqA)}");

        Rng r = new(1UL);
        bool zeroNever = true, oneAlways = true;
        for (int i = 0; i < 1000; i++)
        {
            if (r.Chance(0.0)) zeroNever = false;
            if (!r.Chance(1.0)) oneAlways = false;
        }
        Console.WriteLine($"  Chance(0) 恒假 / Chance(1) 恒真 : {(zeroNever && oneAlways ? "通过" : "**失败**")}");
        Console.WriteLine();

        bool ok = sameSeedMatches && diffSeedDiffers && zeroNever && oneAlways;
        Console.WriteLine(ok
            ? "自检通过。工具箱就绪（M1 战斗内核 ✅ · M3 数据校验器 ✅）。"
            : "自检**失败** —— 见上面的失败项。");
        return ok;
    }

    /// <summary>
    /// 测试用：在**角色真正行动时**才向 Greedy 取一条指令，再交给 ManualPolicy 记录。
    /// 它与界面"一拍预填"不同，但走的是 ManualPolicy → Used → 服务端重放的同一路径；
    /// 用于避免召唤物 / 协同在拍内改变状态时，预填指令落后于当前状态。
    /// </summary>
    private sealed class DeferredManualPolicy(IActionPolicy chooser, ManualPolicy manual) : IActionPolicy
    {
        public ActionChoice? Choose(BattleState state, BattleUnit actor)
        {
            ActionChoice? choice = chooser.Choose(state, actor);
            if (choice is not null) manual.Choices[actor.Serial] = choice;
            return manual.Choose(state, actor);
        }
    }
}
