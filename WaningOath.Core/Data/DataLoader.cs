using System.Text.Json;
using System.Text.Json.Serialization;

namespace WaningOath.Core.Data;

/// <summary>
/// 全部静态数据的运行时视图。**服务端与客户端读的是同一份 `data/`**（`06 §2.3`）。
/// </summary>
/// <remarks>
/// 目录约定见 `05 §2`：<c>data/characters</c> · <c>data/enemies</c> · <c>data/stages</c> · <c>data/effects</c> · <c>data/tables</c>。
/// </remarks>
public sealed class GameData
{
    public string Root { get; private init; } = "";

    public Dictionary<string, CharacterDef> Characters { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, EnemyDef> Enemies { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, StageDef> Stages { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, StatusDef> Statuses { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, WeaponDef> Weapons { get; } = new(StringComparer.Ordinal);

    /// <summary>装备套装（`data/gears/sets/`）。**M3 起只声明与校验，行为属 M7**。</summary>
    public Dictionary<string, GearSetDef> GearSets { get; } = new(StringComparer.Ordinal);

    /// <summary>场外技能登记表（`data/tables/field-skills.json`）。`03 §2` 的机器可读镜像。</summary>
    public Dictionary<string, FieldSkillDef> FieldSkills { get; } = new(StringComparer.Ordinal);

    /// <summary>投入档位表（`data/tables/tiers.json`）。`M2` 分档用，`M7` 的养成折算复用同一份。</summary>
    public TierTable Tiers { get; private set; } = new();

    /// <summary>养成规则与消耗（`data/tables/growth.json`）。**服务端权威**（`06 §2.3`）。</summary>
    public GrowthTable Growth { get; private set; } = new();

    /// <summary>卡池配置（`data/tables/gacha.json`）。**只在服务端持有权威副本**（`06 §2.1`）。</summary>
    public GachaTable Gacha { get; private set; } = new();

    /// <summary>装备规则（`data/tables/gear.json`）。`M7` 的生成 / 强化 / 面板折算都读它。</summary>
    public GearTable Gear { get; private set; } = new();

    /// <summary>
    /// **运行时合成的状态**（`M7`）：武器五段特效、套装效果、`modifyStat` 原子都往这里注册。
    /// </summary>
    /// <remarks>
    /// ⚠️ 它们**不是 `data/effects/` 里的文件**，所以：
    /// <list type="bullet">
    ///   <item>不进 <see cref="RawFiles"/> —— 键名来自各武器的扩展数据，`M3-keys` 无从比对；</item>
    ///   <item>不进 <see cref="Sources"/> —— 校验 1 / 11 的"id 与文件名一致"对它们不适用；</item>
    ///   <item>id 由**稳定规则**生成（`gen:` 前缀 + 来源 id + 档位），因此同一次结算里重复合成会命中缓存。</item>
    /// </list>
    /// </remarks>
    public Dictionary<string, StatusDef> Generated { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// 剧情白名单（`data/tables/stories.json`）。**这张表就是服务端 `story/clear` 的白名单**
    /// （`06 §2.2` · `100` 戊10）。
    /// </summary>
    public Dictionary<string, StoryDef> Stories { get; } = new(StringComparer.Ordinal);

    /// <summary>加载过程中收集的问题。**不抛异常** —— 加载失败要能一次看全，而不是改一个报一个。</summary>
    public List<string> Diagnostics { get; } = [];

    /// <summary>各类数据的来源文件主名 → id。校验 1 / 11 靠它查"id 与文件名一致"。</summary>
    public List<(string File, string Id, string Kind)> Sources { get; } = [];

    /// <summary>每个数据文件的原始文本。**只给"有没有写错的键名"这条校验用**。</summary>
    public List<(string Kind, string File, string Json)> RawFiles { get; } = [];

    public string Describe()
        => $"root={Root} 角色={Characters.Count} 敌人={Enemies.Count} 关卡={Stages.Count} " +
           $"状态={Statuses.Count} 武器={Weapons.Count} 装备={GearSets.Count} 剧情={Stories.Count} 场外技能={FieldSkills.Count}";

    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 全库共用的反序列化设置。**公开**是因为 <see cref="ModesDef.Entries"/> 要惰性解析嵌套的模态对象
    /// —— 模态 id 是任意字符串，只能用 `JsonExtensionData` 接，接到的还是 <see cref="JsonElement"/>，
    /// 二次解析必须用**同一套**设置（否则枚举会退化成数字、注释会报错）。
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// 从工作目录向上寻找 <c>data/</c>。
    /// </summary>
    /// <remarks>
    /// <b>为什么不写死路径</b>：`Sim`（控制台）、`Server`（服务）、Godot 客户端三者的
    /// 当前目录各不相同，而 `06 §8.4` 已记录"文档里的路径一律不可信"这条教训。
    /// 用"从可执行文件目录逐级向上找 `data/characters`"是唯一在三处都成立的定位方式。
    /// </remarks>
    public static string LocateDataRoot(string? startDirectory = null)
    {
        string dir = Path.GetFullPath(startDirectory ?? AppContext.BaseDirectory);
        for (int i = 0; i < 12; i++)
        {
            string candidate = Path.Combine(dir, "data", "characters");
            if (Directory.Exists(candidate)) return Path.Combine(dir, "data");
            DirectoryInfo? parent = Directory.GetParent(dir);
            if (parent is null) break;
            dir = parent.FullName;
        }
        throw new DirectoryNotFoundException(
            "找不到 data/characters。从 '" + (startDirectory ?? AppContext.BaseDirectory) + "' 起向上找了 12 层都没有。" +
            "请确认工作目录在仓库根下（05 §2 的目录结构）。");
    }

    /// <summary>加载整棵 <c>data/</c> 树。</summary>
    public static GameData Load(string? dataRoot = null)
    {
        string root = dataRoot ?? LocateDataRoot();
        GameData data = new() { Root = root };

        data.LoadDirectory(Path.Combine(root, "characters"), "char-*", (g, f) =>
        {
            CharacterDef def = Read<CharacterDef>(f, g);
            if (def.Id.Length > 0) g.Characters[def.Id] = def;
        });
        data.LoadDirectory(Path.Combine(root, "enemies"), "anom-*", (g, f) =>
        {
            EnemyDef def = Read<EnemyDef>(f, g);
            if (def.Id.Length > 0) g.Enemies[def.Id] = def;
        });
        data.LoadDirectory(Path.Combine(root, "stages"), "stage-*", (g, f) =>
        {
            StageDef def = Read<StageDef>(f, g);
            if (def.Id.Length > 0) g.Stages[def.Id] = def;
        });
        data.LoadDirectory(Path.Combine(root, "effects"), "*", (g, f) =>
        {
            StatusDef def = Read<StatusDef>(f, g);
            if (def.Id.Length > 0) g.Statuses[def.Id] = def;
        });
        data.LoadDirectory(Path.Combine(root, "weapons"), "weap-*", (g, f) =>
        {
            WeaponDef def = Read<WeaponDef>(f, g);
            if (def.Id.Length > 0) g.Weapons[def.Id] = def;
        });
        data.LoadDirectory(Path.Combine(root, "gears", "sets"), "gear-*", (g, f) =>
        {
            GearSetDef def = Read<GearSetDef>(f, g);
            if (def.Id.Length > 0) g.GearSets[def.Id] = def;
        });

        // 场外技能是**一张表**，不是一堆文件（05 §7 校验 19）。
        string fieldSkillTable = Path.Combine(root, "tables", "field-skills.json");
        if (File.Exists(fieldSkillTable))
        {
            FieldSkillRegistry? registry = Read<FieldSkillRegistry>(fieldSkillTable, data);
            if (registry is not null)
            {
                foreach (FieldSkillDef skill in registry.Skills)
                {
                    if (skill.Id.Length > 0) data.FieldSkills[skill.Id] = skill;
                }
            }
        }
        else
        {
            data.Diagnostics.Add("缺少 data/tables/field-skills.json —— 05 §7 校验 19 将无法执行。");
        }

        // 投入档位表（M2 分档 / M7 折算）。缺了只影响 batch，不影响单场战斗。
        string tierTable = Path.Combine(root, "tables", "tiers.json");
        if (File.Exists(tierTable))
        {
            TierTable? tiers = Read<TierTable>(tierTable, data);
            if (tiers is not null) data.Tiers = tiers;
        }
        else
        {
            data.Diagnostics.Add("缺少 data/tables/tiers.json —— Sim batch 的四档分档将退化为单档。");
        }

        // 养成规则与卡池配置（M4 的服务端结算用）。
        data.Growth = ReadTable<GrowthTable>(root, "growth.json", data, "养成结算与抽卡端点将不可用");
        data.Gacha = ReadTable<GachaTable>(root, "gacha.json", data, "抽卡端点将不可用");
        data.Gear = ReadTable<GearTable>(root, "gear.json", data, "装备的面板折算与强化将不可用");

        // 剧情白名单：缺了它 `story/clear` 只能拒绝一切（那比"接受一切"好，但不能长期那样）。
        string storyTable = Path.Combine(root, "tables", "stories.json");
        if (File.Exists(storyTable))
        {
            StoryTable? stories = Read<StoryTable>(storyTable, data);
            if (stories is not null)
            {
                foreach (StoryDef s in stories.Stories)
                {
                    if (s.Id.Length > 0) data.Stories[s.Id] = s;
                }
            }
        }
        else
        {
            data.Diagnostics.Add("缺少 data/tables/stories.json —— 故事/clear 将拒绝全部剧情 id。");
        }

        return data;
    }

    /// <summary>读一张 `data/tables/*.json`；缺文件时记一条加载告警并返回默认值（**不抛异常**）。</summary>
    private static T ReadTable<T>(string root, string fileName, GameData data, string consequence) where T : new()
    {
        string path = Path.Combine(root, "tables", fileName);
        if (!File.Exists(path))
        {
            data.Diagnostics.Add($"缺少 data/tables/{fileName} —— {consequence}。");
            return new T();
        }
        return Read<T>(path, data);
    }

    private void LoadDirectory(string directory, string pattern, Action<GameData, string> load)    {
        if (!Directory.Exists(directory))
        {
            // characters/ 是必需的（LocateDataRoot 已经检查过）；其余目录允许为空，但要留下痕迹。
            Diagnostics.Add($"目录不存在，已跳过：{Path.GetRelativePath(Root, directory)}");
            return;
        }

        // 排序是为了**确定性**：字典的插入顺序会影响任何"按插入顺序遍历"的代码路径，
        // 而文件系统的枚举顺序在不同机器上不一致（06 §1 的确定性纪律）。
        List<string> files = [.. Directory.EnumerateFiles(directory, "*.json").OrderBy(static p => p, StringComparer.Ordinal)];
        foreach (string file in files)
        {
            string name = Path.GetFileName(file);
            if (pattern != "*" && !name.StartsWith(pattern[..^1], StringComparison.Ordinal)) continue;
            load(this, file);
        }
    }

    private static T Read<T>(string file, GameData data) where T : new()
    {
        try
        {
            string json = File.ReadAllText(file);
            data.RawFiles.Add((typeof(T).Name, Path.GetFileName(file), json));
            T? value = JsonSerializer.Deserialize<T>(json, JsonOptions);
            if (value is null)
            {
                data.Diagnostics.Add($"解析结果为空：{file}");
                return new T();
            }
            // 记下"这个 id 来自哪个文件"，供校验 1 / 11 使用。
            if (value is ISourcedDef sourced)
            {
                string stem = Path.GetFileNameWithoutExtension(file);
                sourced.SourceFile = stem;
                string id = value switch
                {
                    CharacterDef c => c.Id,
                    EnemyDef e => e.Id,
                    StageDef s => s.Id,
                    StatusDef st => st.Id,
                    WeaponDef w => w.Id,
                    GearSetDef g => g.Id,
                    _ => "",
                };
                if (id.Length > 0) data.Sources.Add((stem, id, typeof(T).Name));
            }
            return value;
        }
        catch (JsonException ex)
        {
            data.Diagnostics.Add($"JSON 语法错误：{Path.GetFileName(file)} → {ex.Message}");
            return new T();
        }
    }

    /// <summary>按 id 取角色；不存在时抛错（**不要静默返回 null** —— 那会变成一个更难查的空引用）。</summary>
    public CharacterDef Character(string id)
        => Characters.TryGetValue(id, out CharacterDef? def)
            ? def
            : throw new KeyNotFoundException($"未登记的角色 id：'{id}'。检查 data/characters/ 与文件名一致性（05 §7 校验 1）。");

    public EnemyDef Enemy(string id)
        => Enemies.TryGetValue(id, out EnemyDef? def)
            ? def
            : throw new KeyNotFoundException($"未登记的敌人 id：'{id}'。检查 data/enemies/。");

    public StageDef Stage(string id)
        => Stages.TryGetValue(id, out StageDef? def)
            ? def
            : throw new KeyNotFoundException($"未登记的关卡 id：'{id}'。检查 data/stages/（这张表同时是服务端白名单，100 戊10）。");

    public StatusDef? Status(string? id)
        => id is not null && Statuses.TryGetValue(id, out StatusDef? def) ? def
         : id is not null && Generated.TryGetValue(id, out StatusDef? gen) ? gen
         : null;

    public WeaponDef? Weapon(string? id)
        => id is not null && Weapons.TryGetValue(id, out WeaponDef? def) ? def : null;

    /// <summary>装备套装。**允许不存在**（M7 才实装）—— 返回 `null`，由校验器决定报警告还是报错。</summary>
    public GearSetDef? GearSet(string? id)
        => id is not null && GearSets.TryGetValue(id, out GearSetDef? def) ? def : null;

    /// <summary>按 id 取剧情；不在白名单里返回 `null`（**调用方负责拒绝，不要静默放行**）。</summary>
    public StoryDef? Story(string? id)
        => id is not null && Stories.TryGetValue(id, out StoryDef? def) ? def : null;

    public FieldSkillDef? FieldSkill(string? id)
        => id is not null && FieldSkills.TryGetValue(id, out FieldSkillDef? def) ? def : null;
}
