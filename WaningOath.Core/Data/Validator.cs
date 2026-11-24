using System.Globalization;
using System.Reflection;
using WaningOath.Core.Battle;

namespace WaningOath.Core.Data;

/// <summary>问题的严重度。**验收线是 `0 错误`**（`06 §8.2` 的 M3 行）—— 警告不影响通过。</summary>
public enum IssueLevel
{
    /// <summary>会导致数据不可用 / 语义错误。必须修。</summary>
    Error,

    /// <summary>需要人看一眼，但当前是有意的（例：引用了一个 M7 才实装的 id）。</summary>
    Warning,

    /// <summary>纯提示（例：某条校验本次没有可检查的对象）。</summary>
    Info,
}

/// <summary>一条校验结论。</summary>
/// <param name="Check">校验编号（`05 §7` 的表号，或 M3 增补的 `M3-x`）。</param>
/// <param name="Level">严重度。</param>
/// <param name="Where">出错位置（`id.字段`）。</param>
/// <param name="Message">结论。</param>
/// <param name="Hint">怎么修。</param>
public sealed record ValidationIssue(string Check, IssueLevel Level, string Where, string Message, string? Hint = null)
{
    private string LevelText => Level switch
    {
        IssueLevel.Error => "错误",
        IssueLevel.Warning => "警告",
        _ => "提示",
    };

    public override string ToString()
        => $"[{LevelText}] 校验 {Check} · {Where}：{Message}" + (Hint is null ? "" : $"　⇒ {Hint}");
}

/// <summary>一次校验的完整报告。</summary>
public sealed class ValidationReport
{
    public List<ValidationIssue> Issues { get; } = [];

    /// <summary>实际跑过的校验编号（用来证明"清单全跑了"，而不是"没报错所以大概是好的"）。</summary>
    public List<string> ExecutedChecks { get; } = [];

    public int Errors => Issues.Count(static i => i.Level == IssueLevel.Error);
    public int Warnings => Issues.Count(static i => i.Level == IssueLevel.Warning);
    public int Infos => Issues.Count(static i => i.Level == IssueLevel.Info);

    /// <summary>**M3 的验收判据**：0 错误。</summary>
    public bool Ok => Errors == 0;

    public void Error(string check, string where, string message, string? hint = null)
        => Issues.Add(new ValidationIssue(check, IssueLevel.Error, where, message, hint));

    public void Warn(string check, string where, string message, string? hint = null)
        => Issues.Add(new ValidationIssue(check, IssueLevel.Warning, where, message, hint));

    public void Info(string check, string where, string message, string? hint = null)
        => Issues.Add(new ValidationIssue(check, IssueLevel.Info, where, message, hint));

    /// <summary>登记一条"跑过了"的校验（不管有没有问题）。</summary>
    public void Ran(string check)
    {
        if (!ExecutedChecks.Contains(check)) ExecutedChecks.Add(check);
    }
}

/// <summary>
/// `data/**/*.json` 的 Schema 校验器（**M3**）。
/// </summary>
/// <remarks>
/// <b>覆盖范围</b>（`06 §8.2` 的 M3 行 + `00 §6` 的实现路线表 + `05 §7`）：
/// <list type="bullet">
///   <item>`05 §7` 的校验 1~21（14 条已找回 + 7 条待核对，本轮全部落地）。</item>
///   <item>M3 增补：**武器部件数与星级匹配** · **技能等级上限与权阶 / 星级一致** ·
///         **权阶Ⅲ/Ⅴ 按星级分档** · **`stackCounter`** · **`fieldSkills` 登记** ·
///         **敌方单位类别** · **跨表引用存在性**。</item>
/// </list>
/// <para>
/// ⚠️ <b>为什么校验器是必需的而不是可选的</b>：<see cref="EffectDef"/> 是一个"宽类型"
/// （`05 §3.2` 的三十来个原子共用一个 C# 类型），**编译期查不出任何字段错误** ——
/// 写错一个 `buffId` 不会编译失败，只会在战斗里安静地什么都不发生。
/// 校验器就是那层缺失的类型检查。
/// </para>
/// <para>
/// <b>纪律</b>：校验器**不修改数据、不生成数据、不发明数值**。它只回答"这份数据能不能发布"。
/// </para>
/// </remarks>
public static class DataValidator
{
    // ── 登记表（文档口径的机器可读形态） ────────────────────────────────

    /// <summary>`103 §2.1` 的 ID 命名空间表 + `104` 补的 `summon-` + `01 §8.1` 补的 `fs-`。</summary>
    private static readonly string[] IdPrefixes =
        ["char-", "skill-", "weap-", "gear-", "anom-", "stage-", "mod-", "buff-", "debuff-", "field-", "fs-",
         "summon-", "map-"];

    /// <summary>登记过的技能位名。`05 §7` 校验 5：技能位**按角色动态**，但这几个名字是登记过的。</summary>
    private static readonly string[] SkillSlots =
        ["basic", "enhancedBasic", "resonance", "enhancedResonance", "talent", "ultimate", "bloom", "enhanced",
         "enhancedUltimate", "symbiontSkill", "symbiontTalent"];

    /// <summary>
    /// `HitDef.Target` 的登记取值（段级形态，只有这五个）。**这是 `Runner.ResolveHitTargets` 的 switch 分支**。
    /// </summary>
    private static readonly string[] HitTargets = ["main", "adjacent", "all", "random", "randomUnhit"];

    /// <summary>
    /// `EffectDef.Target` 的登记取值 —— **从 <see cref="TargetSpec"/> 反射出来，不手抄**。
    /// </summary>
    /// <remarks>
    /// 手抄一份就会漂移：`coopTarget`（爱丽丝）与 `adjacentEnemies` 都出现过
    /// "代码里有、校验器表里没有"的情形。反射拿的是唯一真相。
    /// </remarks>
    private static readonly string[] EffectTargets =
    [
        .. typeof(TargetSpec)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(static f => f is { IsLiteral: true } && f.FieldType == typeof(string))
            .Select(static f => (string?)f.GetRawConstantValue() ?? "")
            .Where(static v => v.Length > 0)
            .Order(StringComparer.Ordinal),
    ];

    /// <summary>`SkillDef.Kind` 的登记取值。`skill` 是**敌方 AI 的技能位名**（`Runner.ActEnemy` 会找它）。</summary>
    private static readonly string[] SkillKinds =
        ["basic", "resonance", "talent", "ultimate", "enhanced_basic", "enhanced_resonance", "bloom",
         "enhanced_ultimate", "skill", "symbiont_skill", "symbiont_talent"];

    /// <summary>`05 §3.3` 的触发钩子表。</summary>
    private static readonly string[] HookNames =
        ["onBattleStart", "onTurnStart", "onTurnEnd", "onRoundStart", "onRoundEnd", "onAttack", "onHit", "onCrit",
         "onSkillCast", "onUltimateCast", "onResonanceCast", "onFollowUp", "onAllyAttacked", "onDamageTaken",
         "onKill", "onDeath", "onAnomalyTrigger", "onDisorder", "onStackReach", "onHpThreshold",
         "onSummonDestroyed", "onSummonExpired", "onAllyResonanceCast", "onAllyUltimateCast", "onAllyAction",
         "onEnemyFieldEnter", "onCombustDetonate", "onNonAttackAction"];

    /// <summary>`05 §3.3` 的触发对象 `scope` 四档。</summary>
    private static readonly string[] Scopes = ["self", "ally", "allyOther", "enemy", "any"];

    /// <summary>`05 §3.2` 的效果原子清单。</summary>
    private static readonly string[] EffectAtoms =
        ["gainStack", "spendStack", "gainResonance", "gainEnergy", "changeEnergyCost", "changeResonanceCost",
         "changeResonanceCap", "extraTurn", "advanceAction", "delayAction", "dealDamage", "followUpAttack",
         "additionalDamage", "detonateCombust", "tickAnomalyNow", "trueDamage", "heal", "shield", "applyBuff",
         "applyDebuff", "applyAnomaly", "applyAnomalyStack", "deployField", "summon", "coopAttack", "chanceCheck",
         "conditional", "execute", "revive", "cleanse", "dispel", "copyBuff", "threatChange", "dodge",
         "modifyStat", "anomalyCapOverride", "transferStack", "selfHpCost", "decayStatus",
         "stackDetonate", "executionSweep", "recastSelf"];

    /// <summary>`05 §3` 的敌方单位类别（`100` #87）。</summary>
    private static readonly string[] Categories = ["human", "anomaly", "machine"];

    /// <summary>敌人档位（`01 §6.2` 通则 6 决定秒杀是否生效）。</summary>
    private static readonly string[] Tiers = ["minion", "normal", "elite", "boss", "highboss"];

    /// <summary>`StatusDef.Kind` 四类。</summary>
    private static readonly string[] StatusKinds = ["buff", "debuff", "field", "summon"];

    /// <summary>`01 §1.5` 的召唤物子类。</summary>
    private static readonly string[] SummonKinds = ["common", "symbiont"];

    /// <summary>`01 §2.5` 的两种计数口径。</summary>
    private static readonly string[] StackCounters = ["perAttack", "perHit"];

    private static readonly string[] Paths = ["dps", "sub_dps", "support", "survival", "healer"];
    private static readonly string[] WeaponTypes = ["dao", "sword", "focus", "gun"];
    private static readonly string[] ElementNames =
        ["physical", "ice", "fire", "lightning", "wind", "resonance", "liye", "aria"];

    /// <summary>`02 §10` 的等级曲线数组名（M3 直接从常量读，不复制进 JSON —— `06` 的"一处真相"）。</summary>
    private static readonly string[] LevelArrays = ["basic", "resonance", "resonance_spread", "ultimate", "none"];

    /// <summary>需要"引用的 id 必须已定义"的效果原子 → 字段名。</summary>
    private static readonly (string Atom, string Field)[] IdReferences =
        [("applyBuff", "buffId"), ("applyDebuff", "debuffId"), ("deployField", "fieldId"), ("summon", "summonId"),
         ("decayStatus", "statusId")];

    /// <summary>
    /// `02 §1.2` 的**定位锚点表**（1 级攻击 / 1 级生命 + 宪法天花板的满配攻击 / 满配生命）。
    /// 校验 6 / 7 靠它。
    /// </summary>
    /// <remarks>
    /// 后两列是**宪法天花板**（`02 §0 宪法 1` / `§1.3`）：满配攻击 / 满配生命，**不得突破**。
    /// 「生存 / 治疗位」的满配生命在文档里是一个区间（`18,000 ~ 20,000`），本表取**上界 20,000**
    /// 当硬线 —— "区间"是配装差异，`20,000` 才是那个不得突破的数（`02 §1.2`）。
    /// </remarks>
    private static readonly (string Row, double Atk1, double Hp1, double AtkFull, double HpFull)[] Anchors =
    [
        ("脆皮辅助（4星）", 240, 2600, 2000, 5800),
        ("常规辅助", 260, 2900, 2200, 6400),
        ("副C", 290, 3200, 2600, 7100),
        ("常规主C", 300, 3600, 3000, 8000),
        ("生命主C", 260, 5200, 2000, 15000),
        ("生存 / 治疗位（5★）", 260, 4000, 2200, 20000),
    ];

    /// <summary>校验 6 的硬上限（`05 §7` 校验 6）。</summary>
    private const double BaseAtkCeiling = 400;
    private const double BaseHpCeiling = 6000;

    /// <summary>`02 §1.2` 的定位行名 ↔ `data/tables/tiers.json` 的 `rows` 键。</summary>
    private static readonly Dictionary<string, string> TierRowNames = new(StringComparer.Ordinal)
    {
        ["frailSupport"] = "脆皮辅助（4星）",
        ["support"] = "常规辅助",
        ["subDps"] = "副C",
        ["dps"] = "常规主C",
        ["hpDps"] = "生命主C",
        ["survival"] = "生存 / 治疗位（5★）",
    };

    /// <summary>`02 §1.2`：每级 +1% 复利 ⇒ 80 级 = `1.01^79 ≈ 2.195`。</summary>
    private static readonly double PanelGrowth80 = Math.Pow(1.01, 79);

    // ── 入口 ────────────────────────────────────────────────────────────

    /// <summary>跑完整套校验。</summary>
    public static ValidationReport Validate(GameData data)
    {
        ValidationReport report = new();

        CheckIdsAndFilenames(data, report);      // 1 · 11
        CheckEnumerations(data, report);         // 2 · 3 · 4
        CheckSkillSlots(data, report);           // 5
        CheckPanelAnchors(data, report);         // 6 · 7
        CheckWeaponParts(data, report);          // 8（M3 增补）
        CheckEidolonSkillLevels(data, report);   // 9（M3 增补）
        CheckEffectReferences(data, report);     // 10
        CheckHitFields(data, report);            // 12 · 13
        CheckConditionSyntax(data, report);      // 14
        CheckNumericSanity(data, report);        // 15
        CheckModes(data, report);                // 16 · 20
        CheckStackCounter(data, report);         // 17
        CheckRoleHeal(data, report);             // 18
        CheckFieldSkills(data, report);          // 19
        CheckExclusiveTo(data, report);          // 21
        CheckEnemyCategory(data, report);        // M3 增补（100 #87）
        CheckHooksAndStatuses(data, report);     // M3 增补（05 §3.3 · §3.2）
        CheckReferences(data, report);           // M3 增补：跨表引用 · 原子名兜底
        CheckStages(data, report);               // M3 增补：关卡能开起来
        CheckTiers(data, report);                // M3 增补：投入档位表与 02 §1.2 锚点表一致
        CheckGrowth(data, report);               // M3 增补：养成规则与 02 §2/§3/§4 一致
        CheckGear(data, report);                 // M3 增补：装备规则与套装效果里的属性名可识别
        CheckStories(data, report);              // M3 增补：剧情白名单（story/clear 的输入）
        CheckUnknownKeys(data, report);          // M3 增补：JSON 键名拼错会被静默忽略
        CheckLoadDiagnostics(data, report);      // M3 增补：**加载期的告警一律升级为错误**

        return report;
    }

    /// <summary>
    /// 加载期的任何告警 = **发布阻断**。
    /// </summary>
    /// <remarks>
    /// <b>为什么这条必须有</b>：`DataLoader` 遇到 JSON 语法错误时只是往 <see cref="GameData.Diagnostics"/>
    /// 里记一行，然后**跳过整个文件** —— 于是一个写坏了的 `anom-ccrf-509.json` 的后果是
    /// "敌人少了一个"。如果那个敌人恰好没有被任何关卡引用，校验器就会报 **0 错误**、
    /// M3 宣布通过，而数据里静静地少了一个单位。
    /// <para>
    /// 本轮真的踩到了：`_note` 里混进一个 ASCII 双引号 ⇒ `509` 整个文件没被加载 ⇒
    /// 唯一的症状是 `stage-sim-normal` 报"敌人不是已登记的敌人"。所以这条从"告警"改成"错误" ——
    /// **告警是给人看的，错误是给流水线看的**。
    /// </para>
    /// </remarks>
    private static void CheckLoadDiagnostics(GameData data, ValidationReport r)
    {
        r.Ran("M3-load");
        foreach (string d in data.Diagnostics)
            r.Error("M3-load", "data/", d, "修掉它 —— 加载期的告警意味着有文件被整个跳过");
    }

    // ── 1 · 11 · id 与文件名、前缀、唯一性 ──────────────────────────────

    private static void CheckIdsAndFilenames(GameData data, ValidationReport r)
    {
        r.Ran("1");
        r.Ran("11");

        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach ((string file, string id, _) in data.Sources)
        {
            // 校验 1：id 与文件名一致。
            if (!string.Equals(file, id, StringComparison.Ordinal))
                r.Error("1", file + ".json", $"id = '{id}'，与文件名 '{file}' 不一致",
                    $"把 \"id\" 改成 \"{file}\"，或把文件改名成 {id}.json");

            // 校验 11：模块前缀。
            if (!IdPrefixes.Any(p => id.StartsWith(p, StringComparison.Ordinal)))
                r.Error("11", file + ".json", $"id = '{id}' 不带任何已登记的模块前缀",
                    "已登记前缀：" + string.Join(" / ", IdPrefixes) + "（103 §2.1）");

            // 校验 11：全局唯一（跨目录 —— char-x 与 weap-x 撞名也是错的）。
            if (!seen.Add(id))
                r.Error("11", file + ".json", $"id = '{id}' 在别处已出现过，全局不唯一",
                    "id 是全局命名空间：跨目录也不能撞（103 §2.1）");
        }

        // 技能 id 不独立成文件，单独扫一遍。
        foreach (CharacterDef c in data.Characters.Values)
        {
            foreach ((string slot, SkillDef skill) in EnumerateSkills(c))
            {
                string where = c.Id + "." + slot;
                if (skill.Id.Length == 0)
                {
                    r.Error("1", where, "技能没有 id", "每个技能都要有 id（05 §3.1），且带 skill- 前缀");
                }
                else if (!skill.Id.StartsWith("skill-", StringComparison.Ordinal))
                {
                    r.Error("11", where, $"技能 id '{skill.Id}' 不带 skill- 前缀", "103 §2.1");
                }
                else if (!seen.Add(skill.Id))
                {
                    r.Error("11", where, $"技能 id '{skill.Id}' 与别处重复", "技能 id 也要全局唯一");
                }

                if (skill.Kind.Length > 0 && !SkillKinds.Contains(skill.Kind, StringComparer.Ordinal))
                    r.Warn("5", where, $"技能 kind = '{skill.Kind}' 不在登记表内",
                        "已登记：" + string.Join(" / ", SkillKinds));
            }
        }

        // 召唤物 / 共生灵自带的技能表也要查 id（爱丽丝的九尾狐两个技能就挂在这里）。
        foreach (StatusDef st in data.Statuses.Values)
        {
            foreach ((string slot, SkillDef skill) in st.Skills.OrderBy(static kv => kv.Key, StringComparer.Ordinal))
            {
                string where = st.Id + "." + slot;
                if (skill.Id.Length == 0)
                    r.Error("1", where, "技能没有 id", "每个技能都要有 id（05 §3.1）");
                else if (!skill.Id.StartsWith("skill-", StringComparison.Ordinal))
                    r.Error("11", where, $"技能 id '{skill.Id}' 不带 skill- 前缀", "103 §2.1");
                else if (!seen.Add(skill.Id))
                    r.Error("11", where, $"技能 id '{skill.Id}' 与别处重复", "技能 id 也要全局唯一");

                if (skill.Kind.Length > 0 && !SkillKinds.Contains(skill.Kind, StringComparer.Ordinal))
                    r.Warn("5", where, $"技能 kind = '{skill.Kind}' 不在登记表内",
                        "已登记：" + string.Join(" / ", SkillKinds));
            }
        }

        // 敌人的技能表：id 与 kind 都要查。**kind 尤其重要** —— `Runner.ActEnemy` 是按 kind
        // 找技能位的（`ultimate` / `resonance` / `skill` / `basic`），写错 kind 的技能**永远不会被放出来**。
        foreach (EnemyDef e in data.Enemies.Values)
        {
            HashSet<string> kinds = new(StringComparer.Ordinal);
            foreach (SkillDef skill in e.Skills)
            {
                string where = e.Id + "." + (skill.Id.Length > 0 ? skill.Id : "?");
                if (skill.Id.Length == 0)
                    r.Error("1", where, "技能没有 id", "每个技能都要有 id（05 §3.1）");
                else if (!skill.Id.StartsWith("skill-", StringComparison.Ordinal))
                    r.Error("11", where, $"技能 id '{skill.Id}' 不带 skill- 前缀", "103 §2.1");
                else if (!seen.Add(skill.Id))
                    r.Error("11", where, $"技能 id '{skill.Id}' 与别处重复", "技能 id 也要全局唯一");

                if (skill.Kind.Length == 0)
                    r.Error("5", where, "技能没有 kind", "Runner.ActEnemy 靠 kind 找技能位");
                else if (!SkillKinds.Contains(skill.Kind, StringComparer.Ordinal))
                    r.Error("5", where, $"技能 kind = '{skill.Kind}' 不是登记过的技能位",
                        "已登记：" + string.Join(" / ", SkillKinds));
                else if (!kinds.Add(skill.Kind))
                    r.Error("5", where, $"两个技能用了同一个 kind '{skill.Kind}'",
                        "敌人技能表以 kind 为键（BattleFactory.BuildEnemy）⇒ 同 kind 会互相覆盖，后者吃掉前者");

                if (!LevelArrays.Contains(skill.ScalingArray, StringComparer.Ordinal))
                    r.Error("5", where, $"scalingArray = '{skill.ScalingArray}' 不是已知的等级曲线",
                        "敌人技能惯例写 none（不随技能等级成长）：" + string.Join(" / ", LevelArrays));
            }

            // 敌人至少要有一个能用的技能，否则它的回合会空转。
            if (e.Skills.Count == 0)
                r.Error("5", e.Id, "敌人一个技能都没有", "至少要有一个 basic，否则它的行动永远无事发生");
        }
    }

    // ── 2 · 3 · 4 枚举值 ────────────────────────────────────────────────

    private static void CheckEnumerations(GameData data, ValidationReport r)
    {
        r.Ran("2");
        r.Ran("3");
        r.Ran("4");

        foreach (CharacterDef c in data.Characters.Values)
        {
            if (c.Rarity is not (3 or 4 or 5))
                r.Error("2", c.Id, $"rarity = {c.Rarity}，不在 {{3,4,5}} 内");

            // 单属性用 element，双属性用 elements —— 两者至少有一个。
            if (c.Element is null && (c.Elements is null || c.Elements.Count == 0))
                r.Error("2", c.Id, "既没有 element 也没有 elements", "单属性写 element，双属性写 elements（05 §3）");

            foreach (string e in AllElementNames(c))
            {
                if (!ElementNames.Contains(e, StringComparer.Ordinal))
                    r.Error("2", c.Id, $"属性 '{e}' 不是已登记属性名", "已登记：" + string.Join(" / ", ElementNames));
            }

            if (!WeaponTypes.Contains(c.WeaponType, StringComparer.Ordinal))
                r.Error("3", c.Id, $"weaponType = '{c.WeaponType}' 不在 {{dao, sword, focus, gun}} 内");

            if (!Paths.Contains(c.Path, StringComparer.Ordinal))
                r.Error("4", c.Id, $"path = '{c.Path}' 不在 {{dps, sub_dps, support, survival, healer}} 内");
        }

        foreach (WeaponDef w in data.Weapons.Values)
        {
            if (!WeaponTypes.Contains(w.WeaponType, StringComparer.Ordinal))
                r.Error("3", w.Id, $"weaponType = '{w.WeaponType}' 不在 {{dao, sword, focus, gun}} 内");
            if (w.Rarity is not (3 or 4 or 5))
                r.Error("2", w.Id, $"rarity = {w.Rarity}，不在 {{3,4,5}} 内");
        }

        foreach (EnemyDef e in data.Enemies.Values)
        {
            if (!Tiers.Contains(e.Tier, StringComparer.Ordinal))
                r.Error("2", e.Id, $"tier = '{e.Tier}' 不在 {{{string.Join(", ", Tiers)}}} 内");
        }

        foreach (StatusDef s in data.Statuses.Values)
        {
            if (!StatusKinds.Contains(s.Kind, StringComparer.Ordinal))
                r.Error("2", s.Id, $"kind = '{s.Kind}' 不在 {{{string.Join(", ", StatusKinds)}}} 内", "05 §3");
            if (!SummonKinds.Contains(s.SummonKind, StringComparer.Ordinal))
                r.Error("2", s.Id, $"summonKind = '{s.SummonKind}' 不在 {{common, symbiont}} 内", "01 §1.5 · 100 #90");

            // 共生灵必须有共生灵技；普通召唤物不该有技能表（104 §3.2）。
            if (s.Kind == "summon" && s.SummonKind == "symbiont" && s.Skills.Count == 0)
                r.Error("2", s.Id, "共生灵没有共生灵技", "01 §1.5：共生灵有共生灵技与共生灵天赋");
            if (s.Kind == "summon" && s.SummonKind == "common" && s.Skills.Count > 0)
                r.Warn("2", s.Id, "普通召唤物带了技能表",
                    "01 §1.5：普通召唤物默认不行动（要行动就登记成 symbiont）");

            // 前缀：buff- / debuff- / field- / summon- 与 kind 必须对应。
            string want = s.Kind switch
            {
                "buff" => "buff-",
                "debuff" => "debuff-",
                "field" => "field-",
                _ => "summon-",
            };
            if (!s.Id.StartsWith(want, StringComparison.Ordinal))
                r.Error("11", s.Id, $"{s.Kind} 的 id 应以 {want} 开头", "103 §2.1 的 ID 命名空间表");
        }
    }

    // ── 5 · 技能位与等级曲线数组 ───────────────────────────────────────

    private static void CheckSkillSlots(GameData data, ValidationReport r)
    {
        r.Ran("5");
        foreach (CharacterDef c in data.Characters.Values)
        {
            // 校验 5 的实质：技能位**由角色自己的 skills 段决定**，不做全局固定 5 项。
            // 因此不检查"缺哪个位"，只检查"写了没登记过的位名"。
            foreach (string slot in c.Skills.Keys)
            {
                if (!SkillSlots.Contains(slot, StringComparer.Ordinal))
                    r.Warn("5", c.Id, $"技能位 '{slot}' 不在登记表内",
                        "已登记：" + string.Join(" / ", SkillSlots) + "（技能位按角色动态，新增位名要登记）");
            }

            // scalingArray 必须指向一条已知的等级曲线（裁决 #62 方案 B）。
            foreach ((string slot, SkillDef s) in EnumerateSkills(c))
            {
                if (!LevelArrays.Contains(s.ScalingArray, StringComparer.Ordinal))
                    r.Error("5", c.Id + "." + slot, $"scalingArray = '{s.ScalingArray}' 不是已知的等级曲线",
                        "已登记：" + string.Join(" / ", LevelArrays) + "（01 §5.2 · 02 §10）");

                if (s.ScalingBase is not ("atk" or "hp"))
                    r.Error("5", c.Id + "." + slot, $"scalingBase = '{s.ScalingBase}' 应为 atk / hp",
                        "05 §3：生命倍率靠 scalingBase = hp 表达，不新增字段");
            }
        }
    }

    // ── 6 · 7 面板锚点与宪法天花板 ──────────────────────────────────────

    private static void CheckPanelAnchors(GameData data, ValidationReport r)
    {
        r.Ran("6");
        r.Ran("7");

        foreach (CharacterDef c in data.Characters.Values)
        {
            // 校验 6：1 级裸装硬上限。
            if (c.Base.Atk > BaseAtkCeiling)
                r.Error("6", c.Id, $"1 级裸装 atk = {c.Base.Atk}，超过上限 {BaseAtkCeiling}");
            if (c.Base.Hp > BaseHpCeiling)
                r.Error("6", c.Id, $"1 级裸装 hp = {c.Base.Hp}，超过上限 {BaseHpCeiling}");

            (string row, double atk1, double hp1, double _, double hpFull) = AnchorRow(c);

            // ① 1 级锚点是 02 §1.2 表里的**精确值**。偏离只警告 —— 可能是有意的差异化，
            //    但"表和数据各说各话"必须被看见。
            if (Math.Abs(c.Base.Atk - atk1) > 1)
                r.Warn("7", c.Id, $"1 级 atk = {c.Base.Atk}，锚点行「{row}」写的是 {atk1}",
                    "02 §1.2 的定位锚点表；确实要差异化就改表");
            if (Math.Abs(c.Base.Hp - hp1) > 30)
                r.Warn("7", c.Id, $"1 级 hp = {c.Base.Hp}，锚点行「{row}」写的是 {hp1}", "02 §1.2");

            // ② **逻辑上不可能的事**：80 级裸装（= 1 级 × 2.195）居然已经超过"满配"天花板。
            //    「满配」按定义含 80 级裸装，所以这一条一旦成立，该定位行的两个锚点就是互相矛盾的。
            //    依据 02 §1.2 的成长规则：每级 +1% 复利 ⇒ 1.01^79 ≈ 2.195。
            double bareHp80 = c.Base.Hp * PanelGrowth80;
            if (bareHp80 > hpFull + 1)
                r.Error("7", c.Id,
                    $"80 级裸装生命 ≈ {bareHp80:0}，超过「{row}」的满配天花板 {hpFull}（还没算装备）",
                    "02 §1.2：满配含裸装 ⇒ 该行的 1 级生命 与 满配生命 自相矛盾，需要裁决哪一列是准的");

            // ③ 速度不随等级成长（02 §1.2），由装备 / 技能提供 ⇒ 裸面板不该超过排轴上限。
            if (c.Base.Speed > 180)
                r.Warn("7", c.Id, $"1 级裸装速度 = {c.Base.Speed}，超过 01 §1.2 的排轴辅助上限 180",
                    "速度只由装备 / 技能提供（02 §1.2）");
        }
    }

    /// <summary>定位 → `02 §1.2` 锚点表的行。**生命倍率的主C 走「生命主C」行**。</summary>
    private static (string Row, double Atk1, double Hp1, double AtkFull, double HpFull) AnchorRow(CharacterDef c)
    {
        // `anchorRow`（显式写明的面板锚点行）优先 —— 它是权威，因为「定位」与「面板锚点」是两条轴
        // （爱丽丝 path=dps 但锚点是「生命主C」）。没写才回退到按 path / scalingBase 推断。
        if (!string.IsNullOrWhiteSpace(c.AnchorRow) && TierRowNames.TryGetValue(c.AnchorRow.Trim(), out string? named))
            return LookupAnchor(named);

        bool hpScaling = EnumerateSkills(c)
            .Any(static kv => string.Equals(kv.Skill.ScalingBase, "hp", StringComparison.Ordinal));

        string row = c.Path switch
        {
            "healer" or "survival" => "生存 / 治疗位（5★）",
            "support" => c.Rarity <= 4 ? "脆皮辅助（4星）" : "常规辅助",
            "sub_dps" => "副C",
            _ => hpScaling ? "生命主C" : "常规主C",
        };

        return LookupAnchor(row);
    }

    private static (string Row, double Atk1, double Hp1, double AtkFull, double HpFull) LookupAnchor(string row)
    {
        foreach ((string r0, double a, double h, double af, double hf) in Anchors)
        {
            if (r0 == row) return (r0, a, h, af, hf);
        }
        return ("常规主C", 300, 3600, 3000, 8000);
    }

    // ── M3 增补：投入档位表（data/tables/tiers.json）与 02 §1.2 锚点表一致 ──

    /// <summary>
    /// 校验 M3-tiers：`data/tables/tiers.json` 是本轮为 `M2` 新建的**第二张锚点表**。
    /// </summary>
    /// <remarks>
    /// 两张表（`02 §1.2` 的中文定位行、`tiers.json` 的英文行键）说的是同一件事，
    /// 一旦它们各说各话，`M2` 会在一个谁也不认识的面板上跑完整个平衡实测 —— 而结论看起来毫无异常。
    /// 所以这里逐行比对：裸装列必须等于 `1 级值 × 1.01^79`，满配列必须等于 `02 §1.2` 的锚点。
    /// </remarks>
    private static void CheckTiers(GameData data, ValidationReport r)
    {
        r.Ran("M3-tiers");

        if (data.Tiers.Rows.Count == 0 || data.Tiers.Tiers.Count == 0)
        {
            r.Warn("M3-tiers", "data/tables/tiers.json", "投入档位表为空 —— Sim batch 无法分档",
                "补 rows 与 tiers 两段（格式见文件自身的 _comment）");
            return;
        }

        // ① 每个档位行必须对得上 02 §1.2 的某一行。
        foreach (KeyValuePair<string, TierRowDef> kv in data.Tiers.Rows.OrderBy(static k => k.Key, StringComparer.Ordinal))
        {
            if (!TierRowNames.TryGetValue(kv.Key, out string? cn))
            {
                r.Error("M3-tiers", $"rows.{kv.Key}", "这个行键没有对应的 02 §1.2 定位行",
                    "已登记：" + string.Join(" / ", TierRowNames.Keys));
                continue;
            }

            (string _, double atk1, double hp1, double atkFull, double hpFull) = LookupAnchor(cn);

            if (Math.Abs(kv.Value.AtkNaked - atk1 * PanelGrowth80) > 2)
                r.Error("M3-tiers", $"rows.{kv.Key}.atkNaked",
                    $"裸装攻击 {kv.Value.AtkNaked:0} ≠ 1 级攻击 {atk1} × 2.195 = {atk1 * PanelGrowth80:0}",
                    "02 §1.2 / §10 的 PANEL_GROWTH");

            if (Math.Abs(kv.Value.HpNaked - hp1 * PanelGrowth80) > 5)
                r.Error("M3-tiers", $"rows.{kv.Key}.hpNaked",
                    $"裸装生命 {kv.Value.HpNaked:0} ≠ 1 级生命 {hp1} × 2.195 = {hp1 * PanelGrowth80:0}",
                    "02 §1.2 / §10 的 PANEL_GROWTH");

            if (Math.Abs(kv.Value.AtkFull - atkFull) > 1)
                r.Error("M3-tiers", $"rows.{kv.Key}.atkFull",
                    $"满配攻击 {kv.Value.AtkFull:0} 与 02 §1.2 的 {atkFull:0} 不一致");

            // 「生存 / 治疗位」的满配生命在 02 §1.2 里是一个区间（18,000~20,000），只查落在区间内。
            bool range = cn == "生存 / 治疗位（5★）";
            bool hpOk = range
                ? kv.Value.HpFull is >= 18000 and <= 20000
                : Math.Abs(kv.Value.HpFull - hpFull) < 1;
            if (!hpOk)
                r.Error("M3-tiers", $"rows.{kv.Key}.hpFull",
                    $"满配生命 {kv.Value.HpFull:0} 与 02 §1.2 的 {(range ? "区间 18,000~20,000" : $"{hpFull:0}")} 不一致");

            // 裸装 > 满配 = 逻辑上不可能（与校验 7 同一条推理）。
            if (kv.Value.HpNaked > kv.Value.HpFull + 1)
                r.Error("M3-tiers", $"rows.{kv.Key}",
                    $"裸装生命 {kv.Value.HpNaked:0} 已经超过满配生命 {kv.Value.HpFull:0}",
                    "02 §1.2：满配按定义包含裸装");
        }

        // ② 档位系数必须落在 [0,1]，且从低到高单调不减、末档必须到 1.0。
        InvestmentTierDef? prev = null;
        foreach (InvestmentTierDef t in data.Tiers.Tiers)
        {
            foreach ((string name, double v) in new[]
                     {
                         ("panelK", t.PanelK), ("critRateK", t.CritRateK), ("critDmgK", t.CritDmgK),
                     })
            {
                if (v is < 0.0 or > 1.0)
                    r.Error("M3-tiers", $"{t.Id}.{name}", $"{name} = {v}，必须落在 [0, 1]");
            }

            if (prev is not null)
            {
                if (t.PanelK < prev.PanelK - 1e-9)
                    r.Error("M3-tiers", $"{t.Id}.panelK", $"完成度比上一档（{prev.Id}）还低 —— 档位必须单调",
                        "02 §8.1.1 的四档是 低配 → 标准 → 大毕业 → 卷王 的递进");
            }
            prev = t;
        }

        if (data.Tiers.Tiers.Count > 0)
        {
            InvestmentTierDef last = data.Tiers.Tiers[^1];
            if (Math.Abs(last.PanelK - 1.0) > 1e-9 || Math.Abs(last.CritRateK - 1.0) > 1e-9)
            {
                r.Warn("M3-tiers", last.Id, "最后一档的完成度不是 1.0 —— 「大毕业」必须正好等于 02 §7 的平衡基准",
                    "02 §8.3 第 4 条的「大毕业 vs 高难首领 ≤9 轮」判的就是这一档");
            }
        }

        // ③ 角色的 anchorRow 必须真的存在。
        foreach (CharacterDef c in data.Characters.Values)
        {
            if (string.IsNullOrWhiteSpace(c.AnchorRow))
            {
                r.Warn("M3-tiers", c.Id, "没有写明 anchorRow —— 只能按 path 推断锚点行",
                    "定位与面板锚点是两条轴（爱丽丝 path=dps 但锚点是生命主C），建议显式写上");
                continue;
            }
            if (!data.Tiers.Rows.ContainsKey(c.AnchorRow.Trim()))
                r.Error("M3-tiers", $"{c.Id}.anchorRow", $"'{c.AnchorRow}' 不在 tiers.json 的 rows 里",
                    "已登记：" + string.Join(" / ", data.Tiers.Rows.Keys.OrderBy(static k => k, StringComparer.Ordinal)));
        }
    }

    // ── 8 武器部件数与星级匹配（M3 增补） ───────────────────────────────

    private static void CheckWeaponParts(GameData data, ValidationReport r)
    {
        r.Ran("8");

        // 101 §0.1：5★ 三件（主 + 副 + 特效）/ 4★ 两件（主 + 特效，无副）/ 3★ 一件（只有主）。
        foreach (WeaponDef w in data.Weapons.Values)
        {
            int expected = w.Rarity switch { 5 => 3, 4 => 2, _ => 1 };
            if (w.PartCount != expected)
                r.Error("8", w.Id,
                    $"{w.Rarity}★ 武器应有 {expected} 个部件，实际 {w.PartCount}" +
                    $"（主 {(w.MainStat is null ? "无" : "有")} / 副 {(w.SubStat is null ? "无" : "有")} / 特效 {(w.Effect is null ? "无" : "有")}）",
                    "101 §0.1：5★ 三件 / 4★ 两件（无副属性）/ 3★ 一件（仅主属性）");

            if (w.MainStat is null)
                r.Error("8", w.Id, "没有主属性", "101 §0.1：三 / 两 / 一件里都含主属性");

            // 4★ / 3★ 不得有副属性位（101 §0.1 · 104 的"副属性池收敛为只有 5★"）。
            if (w.Rarity < 5 && w.SubStat is not null)
                r.Error("8", w.Id, $"{w.Rarity}★ 武器写了副属性，但副属性位只有 5★ 有", "101 §0.1");

            // 特效必须是五段、段号 1~5 连续（101 §0.1：特效只随权限覆写成长）。
            if (w.Effect is { Tiers.Count: > 0 } eff)
            {
                int[] ranks = [.. eff.Tiers.Select(static t => t.Rank).Order()];
                if (!ranks.SequenceEqual(Enumerable.Range(1, ranks.Length)))
                    r.Error("8", w.Id, $"特效段号不是从 1 开始的连续整数：{string.Join(", ", ranks)}", "101 §0.1");
                if (ranks.Length != 5)
                    r.Warn("8", w.Id, $"特效有 {ranks.Length} 段，惯例是 5 段（权限覆写 1~5 阶）", "101 §0.1");
                if (eff.InitialOverrideRank is < 1 or > 5)
                    r.Error("8", w.Id, $"initialOverrideRank = {eff.InitialOverrideRank}，应在 1~5", "101 §0.3");
            }

            if (w.Owner is { Length: > 0 } owner && !data.Characters.ContainsKey(owner))
                r.Warn("8", w.Id, $"owner = '{owner}' 目前不是已登记的角色",
                    "该角色在 07 里有设计、但 data/characters/ 里还没有文件（尚未落盘）—— 落盘后本警告会变成一致性检查");

            // 类型契合：appliesWhen 里的武器类别与武器自身要能对上。
            if (w.Effect is { AppliesWhen.Count: > 0 } e2 &&
                e2.AppliesWhen.TryGetValue("weaponType", out string? wt) && wt != w.WeaponType)
                r.Error("8", w.Id, $"特效的 appliesWhen.weaponType = '{wt}'，与武器自身类别 '{w.WeaponType}' 不一致",
                    "101 §0.3 的类型契合");
        }
    }

    // ── 9 技能等级上限与权阶 / 星级一致（M3 增补） ──────────────────────

    private static void CheckEidolonSkillLevels(GameData data, ValidationReport r)
    {
        r.Ran("9");

        foreach (CharacterDef c in data.Characters.Values)
        {
            // 01 §5.2 的星级分档：5★ 权阶Ⅲ +2（上限 12）· 权阶Ⅴ 再 +2（上限 14）；
            //                    4★ 权阶Ⅲ +1（上限 11）· 权阶Ⅴ 再 +1（上限 12）。
            int gain = c.Rarity <= 4 ? 1 : 2;
            int capAt3 = c.Rarity <= 4 ? 11 : 12;
            int capAt5 = c.Rarity <= 4 ? 12 : 14;

            foreach (int rank in (int[])[3, 5])
            {
                EidolonDef? e = c.Eidolons.FirstOrDefault(x => x.Rank == rank);
                if (e is null)
                {
                    r.Warn("9", c.Id, $"缺少权阶 {rank}", "01 §5.3：权阶协议是 Ⅰ~Ⅵ 六档");
                    continue;
                }

                if (!e.Shared)
                    r.Error("9", c.Id, $"权阶 {rank}『{e.Name}』没有 shared: true",
                        "01 §5.2 的等级解锁是通用档（4★ 也照此走），必须标 shared");

                List<EffectDef> ups =
                [
                    .. e.Effects.Where(static x =>
                        (x.Type == "modifyStat" && string.Equals(x.Stat, "skillLevel", StringComparison.Ordinal)) ||
                        x.Type == "skillLevelUp"),
                ];

                if (ups.Count == 0)
                {
                    r.Error("9", c.Id, $"权阶 {rank}『{e.Name}』没有提高技能等级的效果",
                        $"01 §5.2：权阶Ⅲ 应 +{gain}，权阶Ⅴ 应再 +{gain}");
                    continue;
                }

                foreach (EffectDef up in ups)
                {
                    double declared = up.Type == "skillLevelUp" ? up.Delta : up.Value;
                    if (Math.Abs(declared - gain) > 1e-9)
                        r.Error("9", c.Id, $"权阶 {rank} 的技能等级增量 = {declared}，但 {c.Rarity}★ 应为 +{gain}",
                            "01 §5.2 的星级分档：5★ +2/+2 · 4★ +1/+1");
                }

                int declaredCap = 0;
                foreach (EffectDef up in ups) declaredCap = Math.Max(declaredCap, up.Cap);
                foreach (EffectDef x in e.Effects) declaredCap = Math.Max(declaredCap, x.Cap);
                if (declaredCap > 0)
                {
                    int want = rank == 3 ? capAt3 : capAt5;
                    if (declaredCap != want)
                        r.Error("9", c.Id, $"权阶 {rank} 写的 cap = {declaredCap}，{c.Rarity}★ 应为 {want}", "01 §5.2");
                }
            }
        }
    }

    // ── 10 效果引用的 id 必须已定义 ────────────────────────────────────

    private static void CheckEffectReferences(GameData data, ValidationReport r)
    {
        r.Ran("10");

        foreach ((string where, EffectDef e) in EnumerateAllEffects(data))
        {
            foreach ((string atom, string field) in IdReferences)
            {
                if (e.Type != atom) continue;
                string? id = field switch
                {
                    "buffId" => e.BuffId,
                    "debuffId" => e.DebuffId,
                    "fieldId" => e.FieldId,
                    "statusId" => e.StatusId,
                    _ => e.SummonId,
                };
                if (id is null) continue;
                if (data.Status(id) is null)
                    r.Error("10", where, $"{atom} 引用的 '{id}' 在 data/effects/ 里没有定义", "05 §7 校验 10");
            }

            // 名字对但类型错，同样是错的。
            if (e.Type == "deployField" && data.Status(e.FieldId) is { } f && f.Kind != "field")
                r.Error("10", where, $"deployField 引用的 '{e.FieldId}' 的 kind = '{f.Kind}'，应为 field");
            if (e.Type == "summon" && data.Status(e.SummonId) is { } s && s.Kind != "summon")
                r.Error("10", where, $"summon 引用的 '{e.SummonId}' 的 kind = '{s.Kind}'，应为 summon");

            // 引用了状态的效果还要检查前缀与 kind 对上。
            CheckStatusPrefix(r, where, "applyBuff", e.BuffId, "buff-");
            CheckStatusPrefix(r, where, "applyDebuff", e.DebuffId, "debuff-");
        }
    }

    private static void CheckStatusPrefix(ValidationReport r, string where, string atom, string? id, string prefix)
    {
        if (id is null || id.StartsWith(prefix, StringComparison.Ordinal)) return;
        r.Error("11", where, $"{atom} 引用的 '{id}' 不以 {prefix} 开头", "103 §2.1 的 ID 命名空间表");
    }

    // ── 12 · 13 命中段的属性与目标 ──────────────────────────────────────

    private static void CheckHitFields(GameData data, ValidationReport r)
    {
        r.Ran("12");
        r.Ran("13");

        foreach ((string where, SkillDef skill, CharacterDef? owner, string? modeElement) in EnumerateSkillsWithOwner(data))
        {
            foreach (HitDef h in skill.Hits)
            {
                // 校验 12：hits[].element 必须属于该角色拥有的属性（双属性两个都算）。
                // 模态技能额外允许该模态自己的 element。
                if (h.Element is { Length: > 0 } he && owner is not null)
                {
                    HashSet<string> allowed = new(AllElementNames(owner), StringComparer.Ordinal);
                    if (modeElement is { Length: > 0 }) allowed.Add(modeElement);
                    if (!allowed.Contains(he))
                        r.Error("12", where + ".hits",
                            $"段属性 '{he}' 不属于该角色拥有的属性（{string.Join(" / ", allowed)}）",
                            "双属性角色两个都算；模态技能可用模态自己的属性（05 §7 校验 12）");
                }

                // 校验 13：hits[].target 枚举（**段级词汇表**，只有五个）。
                if (!HitTargets.Contains(h.Target, StringComparer.Ordinal))
                    r.Error("13", where + ".hits", $"target = '{h.Target}' 不在段级登记表内",
                        "段级已登记：" + string.Join(" / ", HitTargets));

                foreach (EffectDef e in h.OnHit)
                {
                    foreach (EffectDef x in Flatten(e))
                    {
                        if (x.Target is { Length: > 0 } t && !EffectTargets.Contains(t, StringComparer.Ordinal))
                            r.Error("13", where + ".hits.onHit", $"target = '{t}' 不在登记表内",
                                "已登记：" + string.Join(" / ", EffectTargets));
                    }
                }
            }
        }

        // 效果里的 target 用 TargetSpec 那张表（权阶 / 状态里的效果也要查）。
        foreach ((string where, EffectDef e) in EnumerateAllEffects(data))
        {
            if (e.Target is { Length: > 0 } t && !EffectTargets.Contains(t, StringComparer.Ordinal))
                r.Error("13", where, $"target = '{t}' 不在登记表内", "已登记：" + string.Join(" / ", EffectTargets));
        }
    }

    // ── 14 条件表达式语法 ──────────────────────────────────────────────

    private static void CheckConditionSyntax(GameData data, ValidationReport r)
    {
        r.Ran("14");
        foreach ((string where, string condition) in EnumerateConditions(data))
            ValidateCondition(condition, where, r);
    }

    /// <summary>条件表达式：`A AND B`（**没有"或"与括号**），登记的谓词见 `05 §3.2`。</summary>
    private static void ValidateCondition(string condition, string where, ValidationReport r)
    {
        string c = condition.Trim();
        if (c.Length == 0) return;
        if (string.Equals(c, "always", StringComparison.Ordinal)) return;

        if (c.Contains('(') || c.Contains(')'))
        {
            r.Error("14", where, $"条件表达式含括号：'{c}'", "05 §3.2：没有括号，需要时再升级");
            return;
        }
        if (c.Contains('|') || c.Contains(" OR ", StringComparison.OrdinalIgnoreCase))
        {
            r.Error("14", where, $"条件表达式含「或」：'{c}'", "05 §3.2：只支持 AND");
            return;
        }

        foreach (string part in c.Split(" AND ", StringSplitOptions.RemoveEmptyEntries))
        {
            string p = part.Trim();
            if (p.Length == 0)
            {
                r.Error("14", where, $"条件表达式里有空的 AND 分支：'{c}'");
                continue;
            }

            string[] bits = p.Split(':');
            string head = bits[0].Trim();
            string[] args = [.. bits.Skip(1).Select(static b => b.Trim())];

            bool ok = head switch
            {
                "always" or "usedUltimate" or "targetIsElitePlus" or "targetIsMinion" or "targetAlive" => args.Length == 0,
                "hasStack" or "hasSummon" or "notStatus" or "targetLacksStatus" or "noEnemyHasStatus" or "enemyHasStatus" => args.Length == 1 && args[0].Length > 0,
                "stackAtLeast" or "anomalyStacksAtLeast" or "statusAtLeast" or "actorStatusAtLeast" or "stackExactly" or "targetStatusAtLeast" =>
                    args.Length == 2 && args[0].Length > 0 && int.TryParse(args[1], out _),
                "knobAtLeast" =>
                    args.Length == 2 && args[0].Length > 0 &&
                    double.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out _),
                "knobBelow" =>
                    args.Length == 2 && args[0].Length > 0 &&
                    double.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out _),
                "targetHpBelow" =>
                    args.Length == 1 &&
                    double.TryParse(args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double thp) && thp is > 0 and <= 1,
                "targetHpBelowKnob" => args.Length == 1 && args[0].Length > 0,
                "statusRemainingBelow" =>
                    args.Length == 2 && args[0].Length > 0 &&
                    double.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out _),
                "hpBelow" => args.Length == 1 &&
                    double.TryParse(args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double hp) && hp is >= 0 and <= 1,
                "enemyCountLE" or "enemyCountGE" => args.Length == 1 && int.TryParse(args[0], out _),
                "targetCategoryIs" => args.Length == 1 && Categories.Contains(args[0], StringComparer.Ordinal),
                _ => false,
            };

            if (!ok)
                r.Error("14", where, $"条件表达式里的 '{p}' 不是已登记语法",
                    "已登记：always · hasStack:<名> · stackAtLeast:<名>:<n> · stackExactly:<名>:<n> · " +
                    "statusAtLeast:<状态名>:<n> · actorStatusAtLeast:<状态名>:<n> · targetStatusAtLeast:<状态名>:<n> · notStatus:<状态名> · targetLacksStatus:<状态名> · noEnemyHasStatus:<状态名> · hpBelow:<0~1> · " +
                    "knobAtLeast:<权阶旋钮>:<n> · knobBelow:<权阶旋钮>:<n> · targetHpBelow:<0~1> · targetHpBelowKnob:<旋钮> · statusRemainingBelow:<状态名>:<n> · " +
                    "enemyCountLE/GE:<n> · anomalyStacksAtLeast:<element>:<n> · usedUltimate · hasSummon:<id> · " +
                    "targetCategoryIs:<cat> · targetIsElitePlus · targetIsMinion · targetAlive（05 §3.2）");
        }
    }

    // ── 15 数值合理性 ──────────────────────────────────────────────────

    private static void CheckNumericSanity(GameData data, ValidationReport r)
    {
        r.Ran("15");

        foreach (CharacterDef c in data.Characters.Values)
        {
            if (c.Base.Atk < 0 || c.Base.Hp < 0 || c.Base.Def < 0 || c.Base.Speed < 0 || c.Base.EnergyMax < 0)
                r.Error("15", c.Id, "base 里有负数");

            foreach ((string slot, SkillDef s) in EnumerateSkills(c))
            {
                if (s.Cost.Energy < 0 || s.Cost.ResonanceIndex < 0)
                    r.Error("15", c.Id + "." + slot, "cost 里有负数");
                if (s.Gain.Energy < 0 || s.Gain.ResonanceIndex < 0)
                    r.Error("15", c.Id + "." + slot, "gain 里有负数");

                foreach (HitDef h in s.Hits)
                {
                    if (h.Multiplier < 0)
                        r.Error("15", c.Id + "." + slot + ".hits", $"倍率为负：{h.Multiplier}");
                    // 倍率必须写成小数（1125% → 11.25）。全游最高的终结技是 11.25，
                    // 所以 ≥ 100 几乎一定是写成了百分数。
                    if (h.Multiplier >= 100)
                        r.Error("15", c.Id + "." + slot + ".hits", $"倍率 {h.Multiplier} 看着像百分数",
                            "05 §7 校验 15：1125% 要写成 11.25");
                    if (h.Times < 1)
                        r.Error("15", c.Id + "." + slot + ".hits", $"times = {h.Times}，必须 ≥ 1");
                }
            }
        }

        foreach ((string where, EffectDef e) in EnumerateAllEffects(data))
        {
            // ⚠️ `delta` / `value` / `intensity` / `threatBonus` **本来就是带符号的**
            //    （`changeEnergyCost.delta = -10` 是降价，`modifyStat.value = -2` 是减需求），
            //    所以只有物理上不可能为负的字段才查。
            if (e.Multiplier < 0 || e.Amount < 0 || e.PctMaxHp < 0 || e.PctCurrentHp < 0 ||
                e.Flat < 0 || e.HpPctOfCaster < 0)
                r.Error("15", where, "效果里有负数");

            if (e.Multiplier >= 100)
                r.Error("15", where, $"倍率 {e.Multiplier} 看着像百分数", "05 §7 校验 15");

            // 概率类字段必须落在 0~1（写成 35 而不是 0.35 是这里最常见的错）。
            CheckProbability(r, where, "chance", e.Chance);
            if (e.BaseChance != 0) CheckProbability(r, where, "baseChance", e.BaseChance);
            if (e.ChancePerStack != 0) CheckProbability(r, where, "chancePerStack", e.ChancePerStack);
            if (e.ChanceCap != 0) CheckProbability(r, where, "chanceCap", e.ChanceCap);
        }
    }

    private static void CheckProbability(ValidationReport r, string where, string name, double? v)
    {
        if (v is null) return;
        if (v < 0) r.Error("15", where, $"{name} = {v} 为负");
        else if (v > 1.0) r.Error("15", where, $"{name} = {v} > 1（概率要写成 0~1 的小数）", "35% → 0.35");
    }

    // ── 16 · 20 升格模态 ───────────────────────────────────────────────

    private static void CheckModes(GameData data, ValidationReport r)
    {
        r.Ran("16");
        r.Ran("20");

        foreach (CharacterDef c in data.Characters.Values)
        {
            if (c.Modes is null)
            {
                if (c.Skills.Count == 0)
                    r.Error("5", c.Id, "既没有 modes，skills 也是空的", "单模态角色直接写 skills（05 §3）");
                continue;
            }

            if (c.Modes.Entries.Count == 0)
                r.Error("16", c.Id, "写了 modes 但一个模态都没解析出来",
                    "模态 id 必须与 default 平级，不能包在 entries 里（05 §7 校验 16）");

            if (c.Modes.Default.Length == 0)
                r.Error("20", c.Id, "modes 里没有 default", "01 §5.4：必须有默认模态");
            else if (!c.Modes.Entries.ContainsKey(c.Modes.Default))
                r.Error("20", c.Id, $"modes.default = '{c.Modes.Default}' 不存在",
                    "已定义的模态：" + string.Join(" / ", c.Modes.Entries.Keys));

            foreach ((string modeId, ModeDef mode) in c.Modes.Entries)
            {
                // 校验 16：每个模态必须自带完整的一套技能位。
                if (mode.Skills.Count == 0)
                    r.Error("16", c.Id + ".modes." + modeId, "这个模态没有任何技能位",
                        "每个模态都要自带完整的一套技能，不能只写差异（05 §7 校验 16）");
                if (mode.Element is { Length: > 0 } me && !ElementNames.Contains(me, StringComparer.Ordinal))
                    r.Error("2", c.Id + ".modes." + modeId, $"模态属性 '{me}' 不是已登记属性名");
            }

            // 校验 20 的后半句：模态内技能位命名一致。
            if (!c.Modes.Entries.TryGetValue(c.Modes.Default, out ModeDef? def)) continue;
            foreach ((string modeId, ModeDef mode) in c.Modes.Entries)
            {
                if (modeId == c.Modes.Default) continue;
                string[] missing = [.. def.Skills.Keys
                    .Where(k => !mode.Skills.ContainsKey(k))
                    .Where(k => !mode.SkillDifferences.Contains(k, StringComparer.Ordinal))];
                if (missing.Length > 0)
                    r.Warn("20", c.Id + ".modes." + modeId,
                        $"相对默认模态 '{c.Modes.Default}' 少了技能位：{string.Join(" / ", missing)}",
                        "05 §7 校验 20：模态内技能位命名要一致（确实该少就登记为差异）");
            }
        }
    }

    // ── 17 「每次攻击」类效果的 stackCounter ────────────────────────────

    /// <summary>
    /// `01 §2.5` 的两种计数口径。**只在两者真有区别的地方才要求显式声明**。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>为什么不是"凡 applyBuff 都要写"</b>：技能级 `onCast`、权阶效果、状态自带效果
    /// 都**只执行一次**，`perAttack` 与 `perHit` 在那里结果完全相同 —— 对它们报 26 条警告只是噪音，
    /// 会把真正的歧义淹掉。歧义只出现在两类位置：
    /// <list type="number">
    ///   <item><c>hits[].onHit</c> —— 每个被打到的目标各执行一次，**天生是 per-hit**；</item>
    ///   <item>挂在 <c>onAttack</c> / <c>onHit</c> 钩子上的效果 —— 一次技能 vs 一段伤害，口径不同结果不同。</item>
    /// </list>
    /// 这正是 `05 §7` 校验 17 那句「"每次攻击"类效果必须声明 `stackCounter`」的适用范围。
    /// </remarks>
    private static void CheckStackCounter(GameData data, ValidationReport r)
    {
        r.Ran("17");

        // ① hits[].onHit：段级效果。
        foreach ((string where, SkillDef skill, CharacterDef? _, string? _) in EnumerateSkillsWithOwner(data))
        {
            foreach (HitDef h in skill.Hits)
            {
                foreach (EffectDef e in h.OnHit)
                    CheckCounter(e, where + ".hits.onHit", r);
            }
        }

        // ② 挂在 onAttack / onHit 上的效果。
        foreach ((string where, TriggerDef t) in EnumerateTriggers(data))
        {
            if (t.Hook is not ("onAttack" or "onHit")) continue;
            foreach (EffectDef e in t.Effects)
                foreach (EffectDef x in Flatten(e)) CheckCounter(x, where, r);
        }
    }

    private static void CheckCounter(EffectDef e, string where, ValidationReport r)
    {
        if (e.Type is not ("applyBuff" or "applyDebuff" or "applyAnomalyStack" or "applyAnomaly")) return;

        if (e.StackCounter is null)
        {
            // 缺失按 perAttack 处理是**正确行为**，所以只警告（05 §7 校验 17）。
            // 但 perHit 必须显式写 —— 否则"多段弹射会不会叠 N 层"每次都要重新推（105 的「爱1」）。
            r.Warn("17", where, $"{e.Type} 没有声明 stackCounter，按 perAttack 处理",
                "01 §2.5：要按命中段数叠就必须显式写 \"stackCounter\": \"perHit\"");
            return;
        }

        if (!StackCounters.Contains(e.StackCounter, StringComparer.Ordinal))
            r.Error("17", where, $"stackCounter = '{e.StackCounter}' 不是 perAttack / perHit",
                "01 §2.5 · 05 §7 校验 17");
    }

    // ── 18 定位与治疗原子 ──────────────────────────────────────────────

    private static void CheckRoleHeal(GameData data, ValidationReport r)
    {
        r.Ran("18");

        foreach (CharacterDef c in data.Characters.Values)
        {
            bool hasHeal = EnumerateSkills(c).Any(static kv => AllEffectsOf(kv.Skill).Any(static e => e.Type is "heal" or "shield"));
            bool hasRealHeal = EnumerateSkills(c).Any(static kv => AllEffectsOf(kv.Skill).Any(static e => e.Type == "heal"));

            if (c.Path == "healer" && !hasRealHeal)
                r.Error("18", c.Id, "path = healer，但技能里没有 heal 原子（护盾不算治疗）",
                    "05 §7 校验 18：healer 必须有 heal");

            if (c.Path == "support" && hasRealHeal)
                r.Error("18", c.Id, "path = support，但技能里有 heal 原子 —— 治疗位要写 healer",
                    "05 §7 校验 18：support 不得冒充治疗位（生存向辅助写 support，非 healer；见 07 §5.7）");

            if (c.Path is "dps" or "sub_dps" && hasHeal && !hasRealHeal)
                r.Info("18", c.Id, "攻击位带了护盾原子，但不含治疗", "护盾不算治疗位，无需改 path");
        }
    }

    // ── 19 fieldSkills 必须登记在 03 ───────────────────────────────────

    private static void CheckFieldSkills(GameData data, ValidationReport r)
    {
        r.Ran("19");

        foreach (CharacterDef c in data.Characters.Values)
        {
            foreach (string id in c.Lore.FieldSkills)
            {
                FieldSkillDef? reg = data.FieldSkill(id);
                if (reg is null)
                {
                    r.Error("19", c.Id + ".lore.fieldSkills", $"'{id}' 没有登记在 data/tables/field-skills.json",
                        "05 §7 校验 19：未登记的场外技能不得发布。先在 03 §2 的运维配额表里登记，再镜像进 JSON");
                    continue;
                }

                if (reg.Owner.Length > 0 && !string.Equals(reg.Owner, c.Id, StringComparison.Ordinal))
                    r.Error("19", c.Id + ".lore.fieldSkills", $"'{id}' 登记归属是 '{reg.Owner}'，不是 {c.Id}",
                        "场外技能归属要与角色一致（01 §8.1）");

                // ⚠️ 命名空间纪律（01 §8.1 · 103 §2.1）：`field-` 归属「领域」，`fs-` 归属「场外技能」。
                // 两者一旦混用，"这个 id 到底是领域还是场外技能"就只能靠猜。
                if (!id.StartsWith("fs-", StringComparison.Ordinal))
                {
                    r.Error("19", c.Id + ".lore.fieldSkills", $"'{id}' 不以 fs- 开头",
                        "01 §8.1：场外技能的前缀是 fs-（领域才是 field-）");
                }
                else if (data.Status(id) is { } clash)
                {
                    r.Error("19", c.Id + ".lore.fieldSkills",
                        $"'{id}' 同时也是 data/effects/ 里的一条 {clash.Kind} —— 场外技能与领域撞名了",
                        "两套前缀必须分开：field- = 领域 · fs- = 场外技能");
                }
            }
        }

        // 反向：登记了但没有任何角色拥有它。
        foreach (FieldSkillDef fs in data.FieldSkills.Values)
        {
            if (fs.Owner.Length == 0) continue;
            CharacterDef? owner = data.Characters.GetValueOrDefault(fs.Owner);
            if (owner is null)
                r.Warn("19", fs.Id, $"登记归属 '{fs.Owner}' 目前不是已登记的角色",
                    "该角色在 07 里有设计、但 data/characters/ 里还没有文件（尚未落盘）—— 落盘后本警告会变成一致性检查");
            else if (!owner.Lore.FieldSkills.Contains(fs.Id, StringComparer.Ordinal))
                r.Warn("19", fs.Id, $"登记归属是 {fs.Owner}，但该角色的 lore.fieldSkills 里没有它",
                    "两处要对上，否则这张表就是死的");
        }
    }

    // ── 21 专拐 exclusiveTo ────────────────────────────────────────────

    private static void CheckExclusiveTo(GameData data, ValidationReport r)
    {
        r.Ran("21");

        foreach (StatusDef s in data.Statuses.Values)
        {
            foreach (string id in s.ExclusiveTo ?? [])
            {
                if (!data.Characters.ContainsKey(id))
                    r.Error("21", s.Id + ".exclusiveTo", $"'{id}' 不是已登记的角色", "02 §0 宪法 5");
            }

            // 空数组与"没写"是两件事：空数组表示"我审过了，它是通用拐"。
            if (s.ExclusiveTo is { Count: 0 })
                r.Info("21", s.Id, "exclusiveTo = []（显式声明为通用拐）", "02 §0 宪法 5：缺失即按通用拐审");
        }

        foreach ((string where, EffectDef e) in EnumerateAllEffects(data))
        {
            foreach (string id in e.ExclusiveTo ?? [])
            {
                if (!data.Characters.ContainsKey(id))
                    r.Error("21", where, $"exclusiveTo 里的 '{id}' 不是已登记的角色", "02 §0 宪法 5");
            }
        }
    }

    // ── M3 增补：敌方单位类别 ──────────────────────────────────────────

    private static void CheckEnemyCategory(GameData data, ValidationReport r)
    {
        r.Ran("M3-category");

        foreach (EnemyDef e in data.Enemies.Values)
        {
            if (!Categories.Contains(e.Category, StringComparer.Ordinal))
                r.Error("M3-category", e.Id, $"category = '{e.Category}' 不在 {{human, anomaly, machine}} 内",
                    "100 #87：没有这个字段，「对异常敌人额外生效」会变成无条件生效");

            foreach ((string el, double v) in e.Resist)
            {
                if (!ElementNames.Contains(el, StringComparer.Ordinal))
                    r.Error("2", e.Id, $"resist 里的属性名 '{el}' 未登记");
                if (v < -1.0)
                    r.Error("15", e.Id, $"resist['{el}'] = {v}，低于 −1（大于 100% 的增伤）");
            }
            if (e.ResistAll is < -1.0 or > 1.0)
                r.Error("15", e.Id, $"resistAll = {e.ResistAll}，应在 −1 ~ 1");
        }

        foreach (CharacterDef c in data.Characters.Values)
        {
            foreach (string cat in c.FinalDamageVsCategory.Keys)
            {
                if (!Categories.Contains(cat, StringComparer.Ordinal))
                    r.Error("M3-category", c.Id + ".finalDamageVsCategory", $"键 '{cat}' 不是合法类别",
                        "100 #87：" + string.Join(" / ", Categories));
            }
        }
    }

    // ── M3 增补：钩子 / scope / 状态定义 ───────────────────────────────

    private static void CheckHooksAndStatuses(GameData data, ValidationReport r)
    {
        r.Ran("M3-hooks");

        foreach ((string where, TriggerDef t) in EnumerateTriggers(data))
        {
            if (!HookNames.Contains(t.Hook, StringComparer.Ordinal))
                r.Error("M3-hooks", where, $"hook = '{t.Hook}' 不是已登记的钩子",
                    "已登记：" + string.Join(" / ", HookNames) + "（05 §3.3）");
            if (!Scopes.Contains(t.Scope, StringComparer.Ordinal))
                r.Error("M3-hooks", where, $"scope = '{t.Scope}' 不是 self / ally / enemy / any", "05 §3.3");
            if (t.Effects.Count == 0 && (t.OnSuccess is null || t.OnSuccess.Count == 0))
                r.Warn("M3-hooks", where, "钩子上没有任何效果", "空钩子不会报错，但通常是漏写");
        }

        foreach (StatusDef s in data.Statuses.Values)
        {
            if (s.Kind == "field" && s.Duration <= 0)
                r.Warn("M3-hooks", s.Id + ".duration", "领域没有持续时间", "01 §8.2 的领域按自然回合计时");

            if (s.Stackable && s.StackCap < 1)
                r.Error("M3-hooks", s.Id + ".stackCap", $"可叠层状态的上限是 {s.StackCap}", "上限至少为 1");

            if (!s.Stackable && s.StackCap > 1)
                r.Warn("M3-hooks", s.Id + ".stackCap", $"stackable = false 但上限写了 {s.StackCap}", "不可叠层时上限无意义");

            foreach (StatMod m in s.Mods)
            {
                if (m.Value < 0 && m.Op == ModOp.Mul)
                    r.Error("15", s.Id + ".mods", $"乘算修正为负（{m.Value}）", "乘算修正应当 > 0");
            }
        }
    }

    // ── M3 增补：跨表引用存在性 + 原子名兜底 ───────────────────────────

    private static void CheckReferences(GameData data, ValidationReport r)
    {
        r.Ran("M3-refs");

        foreach (CharacterDef c in data.Characters.Values)
        {
            if (c.SignatureWeapon is { Length: > 0 } sw)
            {
                WeaponDef? w = data.Weapon(sw);
                // 武器系统整体属 M7（101 的说明）。文件还没落盘 ⇒ 警告；
                // 落盘了但归属写错 ⇒ 错误。两者的区别是"没做"与"做错"。
                if (w is null)
                    r.Warn("M3-refs", c.Id + ".signatureWeapon", $"'{sw}' 在 data/weapons/ 里还没有文件",
                        "武器系统属 M7；101 §4 已给出它的数值，落盘时按那份写");
                else if (w.Owner is { Length: > 0 } o && !string.Equals(o, c.Id, StringComparison.Ordinal))
                    r.Error("M3-refs", c.Id + ".signatureWeapon", $"'{sw}' 的 owner 是 '{o}'，不是 {c.Id}");
            }

            // 装备套装：M7 才实装 ⇒ 缺了只警告（报错会让 M3 永远过不去）。
            if (c.GearSet is { Length: > 0 } gs && data.GearSet(gs) is null)
                r.Warn("M3-refs", c.Id + ".gearSet", $"'{gs}' 尚未定义（装备系统属 M7）",
                    "M7 落地时补 data/gears/sets/；本警告在 M7 结束后应变成错误");
        }

        // 效果原子名必须是登记过的 —— 万能兜底。
        foreach ((string where, EffectDef e) in EnumerateAllEffects(data))
        {
            if (e.Type.Length == 0)
                r.Error("M3-atom", where, "效果没有 type");
            else if (!EffectAtoms.Contains(e.Type, StringComparer.Ordinal))
                r.Error("M3-atom", where, $"'{e.Type}' 不是已登记的效果原子",
                    "已在 05 §3.2 登记的原子：" + string.Join(" / ", EffectAtoms));
        }
    }

    // ── M3 增补：关卡能开起来 ──────────────────────────────────────────

    private static void CheckStages(GameData data, ValidationReport r)
    {
        r.Ran("M3-stage");

        foreach (StageDef s in data.Stages.Values)
        {
            if (s.Party.Count == 0) r.Error("M3-stage", s.Id, "关卡没有我方上场角色");
            if (s.Enemies.Count == 0) r.Error("M3-stage", s.Id, "关卡没有敌人");

            foreach (string id in s.Party)
            {
                if (!data.Characters.ContainsKey(id))
                {
                    r.Error("M3-stage", s.Id, $"我方角色 '{id}' 不是已登记的角色");
                    continue;
                }

                if (s.PartyModes.TryGetValue(id, out string? modeId))
                {
                    CharacterDef c = data.Character(id);
                    if (c.Modes is null || !c.Modes.Entries.ContainsKey(modeId))
                        r.Error("M3-stage", s.Id, $"为 {id} 指定了模态 '{modeId}'，但该角色没有这个模态",
                            "已定义：" + (c.Modes is null ? "（无 modes 段）" : string.Join(" / ", c.Modes.Entries.Keys)));
                }
            }

            foreach (string key in s.PartyModes.Keys)
            {
                if (!s.Party.Contains(key, StringComparer.Ordinal))
                    r.Error("M3-stage", s.Id, $"PartyModes 里的 '{key}' 不在队伍里");
            }

            foreach (string id in s.Enemies)
            {
                if (!data.Enemies.ContainsKey(id))
                    r.Error("M3-stage", s.Id, $"敌人 '{id}' 不是已登记的敌人");
            }

            if (s.SkillLevel is < 1 or > 14) r.Error("M3-stage", s.Id, $"skillLevel = {s.SkillLevel}，应在 1~14", "01 §5.2");
            if (s.PresetLevel is < 1 or > 90) r.Error("M3-stage", s.Id, $"presetLevel = {s.PresetLevel}，应在 1~90");
            if (s.MaxRound <= 0) r.Error("M3-stage", s.Id, $"maxRound = {s.MaxRound} 必须为正");
        }
    }

    // ── M3 增补：养成规则（data/tables/growth.json）与 02 §2/§3/§4 一致 ──

    /// <summary>
    /// 校验 M3-growth：经验曲线、突破节点、技能等级上限与消耗。
    /// </summary>
    /// <remarks>
    /// 这张表是 `M4` 的服务端结算与 `M7` 的养成界面的**唯一**输入。
    /// 它一旦与 `02` 的已定数字不符，玩家看到的"还差多少经验"与服务端扣的东西就会分叉 ——
    /// 而两边都不会报错。
    /// </remarks>
    private static void CheckGrowth(GameData data, ValidationReport r)
    {
        r.Ran("M3-growth");

        GrowthTable g = data.Growth;
        if (g.Level.Cap == 0 || g.Skill.Costs.Count == 0)
        {
            r.Warn("M3-growth", "data/tables/growth.json", "养成规则表为空 —— 服务端成长结算不可用",
                "补 level / breakthrough / skill / items 四段");
            return;
        }

        // ① 等级曲线：总经验必须等于 02 §2.1 的已定值。
        double total = Progression.GrowthRules.TotalExpTo(g, g.Level.Cap);
        if (Math.Abs(total - g.Level.TotalExp) > 1.0)
        {
            r.Error("M3-growth", "level.totalExp",
                $"按 rateTiers 逐级累计到 {g.Level.Cap} 级得 {total:0}，与表里写的 {g.Level.TotalExp:0} 不符",
                "02 §2.1【已定】总经验 2,928,936");
        }

        // ② 突破节点 = 02 §1.1 / §3 的 20 / 40 / 60 / 80。
        int[] expectedNodes = [20, 40, 60, 80];
        if (!g.Level.BreakthroughNodes.OrderBy(static n => n).SequenceEqual(expectedNodes))
        {
            r.Error("M3-growth", "level.breakthroughNodes",
                $"突破节点是 [{string.Join(", ", g.Level.BreakthroughNodes)}]，应为 [{string.Join(", ", expectedNodes)}]",
                "02 §1.1 / §3");
        }

        // ③ 技能等级上限必须与 01 §5.2 的星级分档一致。
        Dictionary<string, int[]> expectedCaps = new(StringComparer.Ordinal)
        {
            ["5"] = [10, 10, 10, 12, 12, 14, 14],
            ["4"] = [10, 10, 10, 11, 11, 12, 12],
        };
        foreach (KeyValuePair<string, int[]> kv in expectedCaps)
        {
            if (!g.Skill.CapsByEidolon.TryGetValue(kv.Key, out List<int>? caps))
            {
                r.Error("M3-growth", $"skill.capsByEidolon.{kv.Key}", $"{kv.Key}★ 的权阶分档缺失", "01 §5.2");
                continue;
            }
            if (!caps.SequenceEqual(kv.Value))
            {
                r.Error("M3-growth", $"skill.capsByEidolon.{kv.Key}",
                    $"[{string.Join(", ", caps)}] 与 01 §5.2 的 [{string.Join(", ", kv.Value)}] 不一致");
            }
        }

        // ④ 2~14 每一级都要有登记消耗，否则服务端会在某一级突然说"消耗尚未登记"。
        for (int lv = 2; lv <= 14; lv++)
        {
            SkillUpgradeCostDef? cost = Progression.GrowthRules.SkillCost(g, lv);
            if (cost is null)
            {
                r.Error("M3-growth", $"skill.costs[{lv}]", $"技能升到 {lv} 级没有登记消耗");
                continue;
            }
            if (!g.Items.ContainsKey(cost.Material))
                r.Error("M3-growth", $"skill.costs[{lv}].material", $"材料 '{cost.Material}' 不在 items 里");
        }

        // ⑤ 突破材料与经验道具同样要登记在 items 里。
        if (!g.Items.ContainsKey(g.Breakthrough.Material))
            r.Error("M3-growth", "breakthrough.material", $"'{g.Breakthrough.Material}' 不在 items 里");
        foreach (string id in g.ExpItems.Keys)
        {
            if (!g.Items.ContainsKey(id)) r.Error("M3-growth", "expItems." + id, $"'{id}' 不在 items 里");
        }
    }

    // ── M3 增补：装备规则（data/tables/gear.json）与套装效果 ──────────

    /// <summary>
    /// 校验 M3-gear：装备规则表内部一致，且**套装效果里写的属性名有人认**。
    /// </summary>
    /// <remarks>
    /// 后半条才是重点：`modifyStat` 原子当前是"接口层已就位、实现层待 M7"，
    /// 所以一个**拼错的 `stat` 名不会报错、不会崩溃，只会永远不生效**。
    /// 装备套装的数值全都是手写的，正是最容易拼错的地方。
    /// </remarks>
    private static void CheckGear(GameData data, ValidationReport r)
    {
        r.Ran("M3-gear");

        GearTable gear = data.Gear;
        if (gear.Slots.Count == 0)
        {
            r.Warn("M3-gear", "data/tables/gear.json", "装备规则表为空 —— 面板折算与装备强化都不可用",
                "补 slots / mainStats / subStats / rules 四段");
            return;
        }

        // ① 部位引用的主属性必须都在 mainStats 里。
        foreach (GearSlotDef slot in gear.Slots)
        {
            if (slot.MainStats.Count == 0)
                r.Error("M3-gear", $"slots.{slot.Key}", "这个部位没有任何可出的主属性");
            foreach (string m in slot.MainStats)
            {
                if (!gear.MainStats.ContainsKey(m))
                    r.Error("M3-gear", $"slots.{slot.Key}.mainStats", $"'{m}' 不在 mainStats 里");
            }
        }

        // ② 四件套 / 两件套的部位必须真的存在，且合起来正好覆盖 6 个部位。
        foreach (string k in gear.Rules.FourPieceSlots.Concat(gear.Rules.TwoPieceSlots))
        {
            if (gear.Slot(k) is null) r.Error("M3-gear", "rules", $"'{k}' 不是一个已登记的部位");
        }
        int covered = gear.Rules.FourPieceSlots.Count + gear.Rules.TwoPieceSlots.Count;
        if (covered != gear.Slots.Count)
            r.Error("M3-gear", "rules", $"四件套 + 两件套一共覆盖 {covered} 个部位，但登记了 {gear.Slots.Count} 个",
                "02 §6.1：6 个部位，前 4 件四件套 + 后 2 件两件套");

        // ③ **装备套装效果里的属性名必须有人认**（面板键，或明确登记为战斗内键）。
        foreach (GearSetDef set in data.GearSets.Values)
        {
            foreach (GearSetTierDef tier in set.Tiers)
            {
                foreach (EffectDef e in tier.Effects)
                {
                    if (!string.Equals(e.Type, "modifyStat", StringComparison.Ordinal)) continue;
                    if (string.IsNullOrEmpty(e.Stat))
                    {
                        r.Error("M3-gear", $"{set.Id}.{tier.Pieces}", "modifyStat 没有写 stat");
                        continue;
                    }
                    if (Progression.PanelCalculator.IsPanelKey(e.Stat)) continue;
                    if (BattleTimeStatKeys.Contains(e.Stat, StringComparer.Ordinal)) continue;
                    r.Warn("M3-gear", $"{set.Id}.{tier.Pieces}",
                        $"属性名 '{e.Stat}' 既不是面板键、也不在战斗内键清单里 —— 实现出来之前它**不会生效**",
                        "面板键见 PanelCalculator.PanelKeys；战斗内键见 Validator.BattleTimeStatKeys");
                }
            }
        }

        // ④ **武器五段特效与常驻特效里的键**同样要有人认。
        //
        //    这一条原本只能在**战斗日志**里发现（`EquipEffects` 会把它 `state.Note` 出来），
        //    但"打一场才看得到"意味着它只会在真的有人去跑那一场时才被发现。
        //    挪到校验期，写数据的人当场就知道。
        foreach (WeaponDef w in data.Weapons.Values)
        {
            if (w.Effect is not { } eff) continue;

            // **每把武器只报一次、按"不认识的键"去重。**
            // ⚠️ 第一版是逐段逐键地报，结果 `明镜鉴心` 的同一个键在 5 段里各报一次、
            //    `焰剑` 的两个键报 10 次 —— 一共 16 条警告说 3 件事。
            //    那正是 `05 §7.3`（校验 17 为什么收窄）记下的教训：**吵的校验等于没有校验**。
            SortedSet<string> unknown = new(StringComparer.Ordinal);
            foreach (KeyValuePair<string, double> kv in eff.Constant)
            {
                if (!IsKnownEffectKey(kv.Key)) unknown.Add(kv.Key);
            }
            foreach (WeaponTierDef tier in eff.Tiers)
            {
                foreach (KeyValuePair<string, System.Text.Json.JsonElement> kv in tier.Values)
                {
                    if (kv.Value.ValueKind != System.Text.Json.JsonValueKind.Number) continue;
                    if (!IsKnownEffectKey(kv.Key)) unknown.Add(kv.Key);
                }
            }

            if (unknown.Count > 0)
            {
                r.Warn("M3-gear", $"{w.Id}.effect",
                    $"这些特效键是**机制旋钮**，引擎还没有实现点 —— 它们不会生效：{string.Join(" / ", unknown)}",
                    "若其中某条本该是面板词条或属性增伤，先检查拼写；否则登记到 Validator.BattleTimeStatKeys 并补实现点");
            }

            // ⚠️ 两种写法都要认：单条 `trigger` 与多条 `triggers`（后者用于"同一段里的键分属不同时机"）。
            //    第一版只查了前者，于是「斯瓦迪尔法利之焰剑」改成 `triggers` 之后立刻被误报了一次。
            bool hasTrigger = eff.Trigger is { Hook.Length: > 0 }
                              || eff.Triggers.Any(static t => t.Hook.Length > 0);
            if (eff.Tiers.Count > 0 && !hasTrigger)
            {
                r.Warn("M3-gear", $"{w.Id}.effect",
                    "有五段特效但没写 `trigger.hook` / `triggers[].hook` —— 不知道什么时候生效，整套特效都不会进战斗",
                    "例：\"trigger\": { \"hook\": \"onUltimateCast\", \"duration\": 2, \"target\": \"self\" }");
            }
        }
    }

    /// <summary>引擎认不认这个装备 / 武器效果键（面板词条 ∪ 按属性记的增伤 ∪ 机制旋钮清单）。</summary>
    private static bool IsKnownEffectKey(string key)
        => Battle.StatKeyBinder.IsKnown(key) || BattleTimeStatKeys.Contains(key, StringComparer.Ordinal);

    /// <summary>
    /// **进战斗才生效**的属性键清单（`01 §2.3` 的词条 + `07` 里各角色的机制旋钮）。
    /// </summary>
    /// <remarks>
    /// 它是一份"人工维护的白名单"，作用只有一个：让"装备写了一个谁也不认识的属性名"**被看见**。
    /// 名单外的键会报警告 —— 那不代表它错，只代表实现出来之前它不会生效。
    /// </remarks>
    private static readonly string[] BattleTimeStatKeys =
    [
        // 01 §2.3 的属性词条
        "dmgBonus", "dmgAmp", "vuln", "pen", "resist", "anomalyBuildUp", "damageReduction",
        "effectHit", "effectRes", "healBonus", "shieldBonus", "energyRegen", "threat",
        // 07 里各角色的机制旋钮（按角色归类，写在这里是为了让拼错能被抓到）
        "burnDetonateChancePerStack", "burnDurationRounds", "burnStackCap", "lavaFieldExtraRoll",
        // M7 已实现的四个机制旋钮（Consumed by DetonateCombust / SelfHpCost / advanceAction）
        "extraCombustChance", "detonateDamageBonus", "selfHpCostPct", "partyAdvanceActionPct", "energyOnHit",
        "resonanceFree", "basicExtraHit", "ultimateExtraHits", "followUpMultiplier",
        "skillLevel", "critRate", "executeChance", "dodge", "combustDetonateDamage",
        "dmgBonusFire", "dmgBonusIce", "dmgBonusWind", "dmgBonusLightning", "dmgBonusPhysical", "dmgBonusResonance",
        "damageVsAnomalyCarrier", "oathCap", "mirrorMarkCap", "enhancedResonanceBounces",
        "followUpDamage", "summonAlive", "fieldActive",
        // 瓦列里娅（火 · 施术仪）：无人机伤害的旋钮，由 `EffectDef.Knob` 在伤害原子里读。
        "droneDamageBonus", "droneExtraCombust", "droneDamageBonusPerWill", "willDeployDrones",
        "counterDamageBonus",
    ];

    // ── M3 增补：剧情白名单（data/tables/stories.json） ────────────────

    /// <summary>
    /// 校验 M3-story：剧情表能被服务端当成 `story/clear` 的白名单用。
    /// </summary>
    /// <remarks>
    /// 这张表**唯一的作用就是防刷**（`100` 戊10：原实现接受任意 `storyId`）。
    /// 所以它一旦有洞，防刷就是假的：`anchorStage` 指向一个不存在的关卡，
    /// "这一章通了吗"就永远判不出来；奖励里写一个没登记的 item id，玩家就白领一个空气。
    /// </remarks>
    private static void CheckStories(GameData data, ValidationReport r)
    {
        r.Ran("M3-story");

        if (data.Stories.Count == 0)
        {
            r.Warn("M3-story", "data/tables/stories.json", "剧情表为空 —— 服务端会拒绝全部 story/clear",
                "填一条最小记录（id / name / anchorStage / reward）");
            return;
        }

        foreach (StoryDef s in data.Stories.Values)
        {
            if (string.IsNullOrWhiteSpace(s.Id))
                r.Error("M3-story", "stories", "有一条记录没有 id");

            if (s.AnchorStage.Length == 0)
            {
                r.Error("M3-story", s.Id, "没有写 anchorStage —— 服务端将无法判断'这一章通关了吗'",
                    "指向一个已登记的 stage- id");
            }
            else if (!data.Stages.ContainsKey(s.AnchorStage))
            {
                r.Error("M3-story", s.Id, $"anchorStage '{s.AnchorStage}' 不是已登记的关卡",
                    "已登记：" + string.Join(" / ", data.Stages.Keys.OrderBy(static k => k, StringComparer.Ordinal)));
            }

            foreach (string item in s.Reward.Items.Keys)
            {
                if (!data.Growth.Items.ContainsKey(item))
                    r.Warn("M3-story", s.Id, $"奖励里的道具 '{item}' 不在 data/tables/growth.json 的 items 里");
            }

            if (s.Reward.ProtocolCode == 0 && s.Reward.Credit == 0 && s.Reward.Items.Count == 0)
                r.Warn("M3-story", s.Id, "奖励是空的 —— 这条记录不会给玩家任何东西");
        }
    }

    // ── M3 增补：JSON 键名必须落在 DTO 上 ──────────────────────────────

    /// <summary>文件类型名 → DTO 类型（与 <see cref="GameData.RawFiles"/> 的 `Kind` 对应）。</summary>
    private static readonly Dictionary<string, Type> DefTypes = new(StringComparer.Ordinal)
    {
        ["CharacterDef"] = typeof(CharacterDef),
        ["EnemyDef"] = typeof(EnemyDef),
        ["StageDef"] = typeof(StageDef),
        ["StatusDef"] = typeof(StatusDef),
        ["WeaponDef"] = typeof(WeaponDef),
        ["GearSetDef"] = typeof(GearSetDef),
        ["GearTable"] = typeof(GearTable),
        // `data/tables/*.json` 也走同一条检查。
        // ⚠️ 这四行是**补上的**：最初只登记了上面六个实体，于是 `growth.json` 里的
        //    `breakthrough` / `skill` / `eidolon` 三段**没有被检查过**，而 DTO 当时是按扁平结构写的
        //    ⇒ 三段全被静默忽略、`SkillCosts` 为空，症状是"服务端说技能 2 级的消耗尚未登记"。
        //    **这正是 M3-keys 存在的原因，而它当时没有覆盖到这几张表。**
        ["TierTable"] = typeof(TierTable),
        ["GrowthTable"] = typeof(GrowthTable),
        ["GachaTable"] = typeof(GachaTable),
        ["FieldSkillRegistry"] = typeof(FieldSkillRegistry),
        ["StoryTable"] = typeof(StoryTable),
    };

    /// <summary>
    /// 找出**写错名字的键**。
    /// </summary>
    /// <remarks>
    /// <b>为什么这条必须有</b>：`System.Text.Json` 默认**静默忽略**反序列化时对不上的键。
    /// 这个特性已经害过一次 —— `modes` 早期写成 `{"default": ..., "entries": {...}}`，
    /// `entries` 被安静丢掉，症状是"默认模态是对的、但技能表是空的"。
    /// 一条键名拼写错误不会报错、不会崩溃，只会让一个机制永远不触发。
    /// </remarks>
    private static void CheckUnknownKeys(GameData data, ValidationReport r)
    {
        r.Ran("M3-keys");

        foreach ((string kind, string file, string json) in data.RawFiles)
        {
            if (!DefTypes.TryGetValue(kind, out Type? type)) continue;

            try
            {
                using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(json);
                WalkKeys(doc.RootElement, type, "", file, r, 0);
            }
            catch (System.Text.Json.JsonException)
            {
                // 语法错误已由 DataLoader 报过，这里不重复。
            }
        }
    }

    private static void WalkKeys(System.Text.Json.JsonElement el, Type type, string path, string file, ValidationReport r, int depth)
    {
        if (depth > 8) return;

        if (el.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            Type? elem = ElementType(type);
            if (elem is null) return;
            foreach (System.Text.Json.JsonElement item in el.EnumerateArray()) WalkKeys(item, elem, path, file, r, depth + 1);
            return;
        }
        if (el.ValueKind != System.Text.Json.JsonValueKind.Object) return;

        // 字典：键由数据自己定 ⇒ 不查键名，但要继续往下走值的类型。
        if (DictionaryValueType(type) is { } valType)
        {
            foreach (System.Text.Json.JsonProperty p in el.EnumerateObject())
                WalkKeys(p.Value, valType, path + "." + p.Name, file, r, depth + 1);
            return;
        }

        // 带 [JsonExtensionData] 的类型故意接受任意键（例：ModesDef 的模态 id）。
        if (HasExtensionData(type)) return;

        Dictionary<string, PropertyInfo> props = PropertiesOf(type);

        foreach (System.Text.Json.JsonProperty p in el.EnumerateObject())
        {
            if (p.Name.StartsWith('_')) continue;   // 数据文件里的注释键，约定用下划线开头

            if (!props.TryGetValue(p.Name, out PropertyInfo? pi))
            {
                r.Warn("M3-keys", file,
                    $"'{path}.{p.Name}' 不在 {type.Name} 的 Schema 里 —— 会被**静默忽略**",
                    "要么补进 DTO（05 §3 的声明层），要么改掉错拼的键名");
                continue;
            }

            WalkKeys(p.Value, pi.PropertyType, path + "." + p.Name, file, r, depth + 1);
        }
    }

    private static Dictionary<string, PropertyInfo> PropertiesOf(Type type)
    {
        Dictionary<string, PropertyInfo> props = new(StringComparer.OrdinalIgnoreCase);
        foreach (PropertyInfo p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!p.CanWrite) continue;
            // [JsonIgnore] 的属性**反序列化不认** —— 写进 JSON 就是被忽略，应该报警告。
            if (p.GetCustomAttribute<System.Text.Json.Serialization.JsonIgnoreAttribute>() is not null) continue;
            string name = p.GetCustomAttribute<System.Text.Json.Serialization.JsonPropertyNameAttribute>()?.Name ?? p.Name;
            props[name] = p;
        }
        return props;
    }

    private static bool HasExtensionData(Type type)
        => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
               .Any(static p => p.GetCustomAttribute<System.Text.Json.Serialization.JsonExtensionDataAttribute>() is not null);

    private static Type? ElementType(Type type)
    {
        if (type.IsArray) return type.GetElementType();
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>)) return type.GetGenericArguments()[0];
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>)) return type.GetGenericArguments()[1];
        return null;
    }

    private static Type? DictionaryValueType(Type type)
        => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>)
            ? type.GetGenericArguments()[1]
            : null;

    // ── 遍历工具 ───────────────────────────────────────────────────────
    /// <summary>角色展开后的**全部**技能（单模态 = `skills`；双模态 = 每个模态各一套）。</summary>
    private static IEnumerable<(string Slot, SkillDef Skill)> EnumerateSkills(CharacterDef c)
    {
        if (c.Modes is { Entries.Count: > 0 })
        {
            foreach ((string modeId, ModeDef mode) in c.Modes.Entries.OrderBy(static kv => kv.Key, StringComparer.Ordinal))
            {
                foreach ((string slot, SkillDef s) in mode.Skills.OrderBy(static kv => kv.Key, StringComparer.Ordinal))
                    yield return ("modes." + modeId + "." + slot, s);
            }
            yield break;
        }

        foreach ((string slot, SkillDef s) in c.Skills.OrderBy(static kv => kv.Key, StringComparer.Ordinal))
            yield return (slot, s);
    }

    private static IEnumerable<(string Where, SkillDef Skill, CharacterDef? Owner, string? ModeElement)> EnumerateSkillsWithOwner(GameData data)
    {
        foreach (CharacterDef c in data.Characters.Values)
        {
            if (c.Modes is { Entries.Count: > 0 })
            {
                foreach ((string modeId, ModeDef mode) in c.Modes.Entries.OrderBy(static kv => kv.Key, StringComparer.Ordinal))
                {
                    foreach ((string slot, SkillDef s) in mode.Skills.OrderBy(static kv => kv.Key, StringComparer.Ordinal))
                        yield return (c.Id + ".modes." + modeId + "." + slot, s, c, mode.Element);
                }
                continue;
            }

            foreach ((string slot, SkillDef s) in c.Skills.OrderBy(static kv => kv.Key, StringComparer.Ordinal))
                yield return (c.Id + "." + slot, s, c, null);
        }

        foreach (EnemyDef e in data.Enemies.Values)
        {
            foreach (SkillDef s in e.Skills) yield return (e.Id + "." + s.Id, s, null, null);
        }

        foreach (StatusDef st in data.Statuses.Values)
        {
            foreach ((string slot, SkillDef s) in st.Skills.OrderBy(static kv => kv.Key, StringComparer.Ordinal))
                yield return (st.Id + "." + slot, s, null, null);
        }
    }

    /// <summary>一个技能里的全部效果（含段级 `onHit` 与各分支）。</summary>
    private static IEnumerable<EffectDef> AllEffectsOf(SkillDef skill)
    {
        foreach (EffectDef e in skill.OnCast)
            foreach (EffectDef x in Flatten(e)) yield return x;

        foreach (HitDef h in skill.Hits)
            foreach (EffectDef e in h.OnHit)
                foreach (EffectDef x in Flatten(e)) yield return x;

        foreach (TriggerDef t in skill.Triggers)
        {
            foreach (EffectDef e in t.Effects) foreach (EffectDef x in Flatten(e)) yield return x;
            foreach (EffectDef e in t.OnSuccess ?? []) foreach (EffectDef x in Flatten(e)) yield return x;
            foreach (EffectDef e in t.OnFail ?? []) foreach (EffectDef x in Flatten(e)) yield return x;
        }
    }

    /// <summary>递归展开一个效果的全部分支。**必须递归** —— 爱丽丝的二段终结技就藏在 `conditional.then` 里。</summary>
    private static IEnumerable<EffectDef> Flatten(EffectDef e)
    {
        yield return e;
        foreach (EffectDef x in e.Then ?? []) foreach (EffectDef y in Flatten(x)) yield return y;
        foreach (EffectDef x in e.OnSuccess ?? []) foreach (EffectDef y in Flatten(x)) yield return y;
        foreach (EffectDef x in e.OnFail ?? []) foreach (EffectDef y in Flatten(x)) yield return y;
        foreach (EffectDef x in e.OnDestroy ?? []) foreach (EffectDef y in Flatten(x)) yield return y;
        foreach (EffectDef x in e.OnExpire ?? []) foreach (EffectDef y in Flatten(x)) yield return y;
    }

    /// <summary>全库的每一个效果（含钩子、权阶、状态自身的效果）。</summary>
    private static IEnumerable<(string Where, EffectDef Effect)> EnumerateAllEffects(GameData data)
    {
        foreach (CharacterDef c in data.Characters.Values)
        {
            foreach ((string slot, SkillDef s) in EnumerateSkills(c))
            {
                foreach (EffectDef e in AllEffectsOf(s)) yield return (c.Id + "." + slot + "." + e.Type, e);
            }

            foreach (EidolonDef eid in c.Eidolons)
            {
                foreach (EffectDef e in eid.Effects)
                    foreach (EffectDef x in Flatten(e)) yield return ($"{c.Id}.eidolon{eid.Rank}.{x.Type}", x);

                foreach (TriggerDef t in eid.Triggers)
                    foreach (EffectDef e in t.Effects)
                        foreach (EffectDef x in Flatten(e)) yield return ($"{c.Id}.eidolon{eid.Rank}.{t.Hook}.{x.Type}", x);
            }
        }

        foreach (EnemyDef e in data.Enemies.Values)
        {
            foreach (EffectDef x in e.OnBattleStart)
                foreach (EffectDef y in Flatten(x)) yield return ($"{e.Id}.onBattleStart.{x.Type}", y);

            foreach (SkillDef s in e.Skills)
                foreach (EffectDef x in AllEffectsOf(s)) yield return ($"{e.Id}.{s.Id}.{x.Type}", x);
        }

        foreach (StatusDef s in data.Statuses.Values)
        {
            foreach (SkillDef sk in s.Skills.Values)
                foreach (EffectDef x in AllEffectsOf(sk)) yield return ($"{s.Id}.{sk.Id}.{x.Type}", x);

            foreach (EffectDef x in s.OnEnemyEnter)
                foreach (EffectDef y in Flatten(x)) yield return ($"{s.Id}.onEnemyEnter.{x.Type}", y);
        }
    }

    /// <summary>全库的每一条触发钩子。</summary>
    private static IEnumerable<(string Where, TriggerDef Trigger)> EnumerateTriggers(GameData data)
    {
        static IEnumerable<(string, TriggerDef)> OfSkill(string where, SkillDef s)
        {
            foreach (TriggerDef t in s.Triggers) yield return ($"{where}.triggers[{t.Hook}]", t);
        }

        foreach (CharacterDef c in data.Characters.Values)
        {
            foreach ((string slot, SkillDef s) in EnumerateSkills(c))
                foreach ((string w, TriggerDef t) in OfSkill(c.Id + "." + slot, s)) yield return (w, t);

            foreach (EidolonDef eid in c.Eidolons)
                foreach (TriggerDef t in eid.Triggers) yield return ($"{c.Id}.eidolon{eid.Rank}.triggers[{t.Hook}]", t);
        }

        foreach (EnemyDef e in data.Enemies.Values)
            foreach (SkillDef s in e.Skills)
                foreach ((string w, TriggerDef t) in OfSkill(e.Id + "." + s.Id, s)) yield return (w, t);

        foreach (StatusDef s in data.Statuses.Values)
            foreach (SkillDef sk in s.Skills.Values)
                foreach ((string w, TriggerDef t) in OfSkill(s.Id + "." + sk.Id, sk)) yield return (w, t);
    }

    /// <summary>全库的每一条条件表达式（含 `cost.requires`、段级 `condition`、钩子 `condition`、`conditional` 原子）。</summary>
    private static IEnumerable<(string Where, string Condition)> EnumerateConditions(GameData data)
    {
        static IEnumerable<(string, string)> OfSkill(string where, SkillDef s)
        {
            if (s.Cost.Requires is { Length: > 0 } req) yield return ($"{where}.cost.requires", req);

            foreach (HitDef h in s.Hits)
                if (h.Condition is { Length: > 0 } hc) yield return ($"{where}.hits.condition", hc);

            foreach (TriggerDef t in s.Triggers)
                if (t.Condition is { Length: > 0 } tc) yield return ($"{where}.triggers[{t.Hook}].condition", tc);

            foreach (EffectDef e in AllEffectsOf(s))
                if (e.Condition is { Length: > 0 } ec) yield return ($"{where}.{e.Type}.condition", ec);
        }

        foreach (CharacterDef c in data.Characters.Values)
        {
            foreach ((string slot, SkillDef s) in EnumerateSkills(c))
                foreach ((string w, string cond) in OfSkill(c.Id + "." + slot, s)) yield return (w, cond);

            foreach (EidolonDef eid in c.Eidolons)
                foreach (TriggerDef t in eid.Triggers)
                    if (t.Condition is { Length: > 0 } tc) yield return ($"{c.Id}.eidolon{eid.Rank}.condition", tc);
        }

        foreach (EnemyDef e in data.Enemies.Values)
            foreach (SkillDef s in e.Skills)
                foreach ((string w, string cond) in OfSkill(e.Id + "." + s.Id, s)) yield return (w, cond);

        foreach (StatusDef s in data.Statuses.Values)
            foreach (SkillDef sk in s.Skills.Values)
                foreach ((string w, string cond) in OfSkill(s.Id + "." + sk.Id, sk)) yield return (w, cond);
    }

    /// <summary>一个角色声明拥有的全部属性名（用于校验 2 与 12）。</summary>
    private static IEnumerable<string> AllElementNames(CharacterDef c)
    {
        if (c.Element is { Length: > 0 } e) yield return e;
        foreach (string x in c.Elements ?? []) yield return x;
    }
}
