using System.Text.Json;
using System.Text.Json.Serialization;

namespace WaningOath.Core.Game;

/// <summary>
/// 一个玩家的存档（`05 §4`，键名按 `103` 把 `relics` 改成 `gears`）。
/// </summary>
/// <remarks>
/// <b>只存养成状态，不存静态数据</b>：角色 / 武器 / 关卡的静态定义在 `data/`，
/// 客户端与服务端各持一份（`06 §2.0`）。存档里出现的 id 一律是 `data/` 里的**完整 id**
/// （`char-shouyueren` 而不是 `shouyueren`）—— 短名会让"存档里的 id 能不能在 data 里找到"
/// 这件事多一次转换，而转换点就是错的地方。
/// </remarks>
public sealed class PlayerSave
{
    public string UserId { get; set; } = "";
    public string Username { get; set; } = "";
    public long CreatedAt { get; set; }
    public ProfileData Profile { get; set; } = new();
    public StaminaState Stamina { get; set; } = new();
    public CurrencyState Currency { get; set; } = new();
    public Dictionary<string, CharacterState> Characters { get; set; } = new(StringComparer.Ordinal);

    /// <summary>当前编队（按站位，1~4 人；一号位有阵营判定意义）。</summary>
    public List<string> Team { get; set; } = [];

    /// <summary>编队里双模态角色选定的模态（键 = 角色 id，值 = 模态 id）。</summary>
    public Dictionary<string, string> TeamModes { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, WeaponState> Weapons { get; set; } = new(StringComparer.Ordinal);
    public List<GearState> Gears { get; set; } = [];
    public Dictionary<string, int> Inventory { get; set; } = new(StringComparer.Ordinal);
    public GachaState Gacha { get; set; } = new();
    public ProgressState Progress { get; set; } = new();
    public List<string> TutorialDone { get; set; } = [];
    public long UpdatedAt { get; set; }

    public sealed class ProfileData
    {
        public string PlayerName { get; set; } = "";
        public string Protagonist { get; set; } = "char-shouyueren";
        public string ProtagonistGender { get; set; } = "female";
        public long LastGenderSwitchAt { get; set; }

        /// <summary>
        /// **手动选定的衣装**（键 = 角色 id，值 = 衣装 id；缺省 = 原皮）。
        /// </summary>
        /// <remarks>
        /// ⚠️ 它只记"玩家选了什么"，**不决定正式流程里显示哪一张**：
        /// `04 §3.2` 的口径是主线剧情不生效、部分活动剧情会指定特定衣装（并免费试用），
        /// 所以主线里永远显示原皮，活动剧情里显示剧情指定的那一套。
        /// 之所以现在就落盘，是因为"选了要留住"这件事与"什么时候生效"是两件事。
        /// </remarks>
        public Dictionary<string, string> Skins { get; set; } = new(StringComparer.Ordinal);

        /// <summary>协议等级。**写入时按角色等级重算**，不信任客户端上报（`06 §3.1`）。</summary>
        public int ProtocolLevel { get; set; }
    }

    public sealed class StaminaState
    {
        public int Current { get; set; } = 240;
        public long LastRegenAt { get; set; }
    }

    public sealed class CurrencyState
    {
        public int ProtocolCode { get; set; }
        public int Credit { get; set; }
        public int Receipt { get; set; }
        public int PassReceipt { get; set; }
        public int NormalTicket { get; set; }
        public int SpecialTicket { get; set; }
    }

    public sealed class CharacterState
    {
        public int Level { get; set; } = 1;
        public double Exp { get; set; }
        public int Breakthrough { get; set; }

        /// <summary>权阶 0~6。**只能靠抽卡重复获得**，不走养成结算。</summary>
        public int Eidolon { get; set; }

        /// <summary>技能等级（键 = 技能位，如 `basic` / `resonance` / `talent` / `ultimate`）。</summary>
        public Dictionary<string, int> SkillLevels { get; set; } = new(StringComparer.Ordinal);

        /// <summary>当前装备的武器 id。</summary>
        public string? Weapon { get; set; }

        /// <summary>当前装备的装备 uid 列表（最多 6 件）。</summary>
        public List<string> Gears { get; set; } = [];
    }

    public sealed class WeaponState
    {
        public int Level { get; set; } = 1;

        /// <summary>权限覆写阶数 1~5（**只强化特效**，`02 §5.3`）。</summary>
        public int Overwrite { get; set; } = 1;

        public string? EquippedBy { get; set; }
        public int Count { get; set; } = 1;
    }

    public sealed class GearState
    {
        public string Uid { get; set; } = "";
        public string Set { get; set; } = "";

        /// <summary>`identity` / `chip` / `hourglass` / `container` / `core` / `peripheral`。</summary>
        public string Part { get; set; } = "";

        public int Level { get; set; }
        public string MainStat { get; set; } = "";
        public List<GearSubStat> SubStats { get; set; } = [];
        public string? EquippedBy { get; set; }
    }

    public sealed class GearSubStat
    {
        public string Stat { get; set; } = "";
        public double Value { get; set; }
    }

    public sealed class GachaState
    {
        public int StandardPity { get; set; }
        public int LimitedPity { get; set; }
        public int WeaponPity { get; set; }
        public int FourPity { get; set; }
        public bool GuaranteedLimited { get; set; }
        public bool GuaranteedWeapon { get; set; }
        public int TotalPulls { get; set; }
    }

    public sealed class ProgressState
    {
        public Dictionary<string, StageRecord> Stages { get; set; } = new(StringComparer.Ordinal);
        /// <summary>
        /// 剧情（章）的通关记录。键 = `data/tables/stories.json` 的 id。
        /// </summary>
        /// <remarks>
        /// 结构对齐 `05 §4` 的 `mainStory: { prologue: { cleared, firstClearRewarded } }` ——
        /// **"通了没"和"奖发了没"必须分开记**：合成一个布尔值的话，一次写入失败就会
        /// 让玩家永远拿不到首通奖励（或者永远能重复拿）。
        /// </remarks>
        public Dictionary<string, StoryRecord> Stories { get; set; } = new(StringComparer.Ordinal);
        public long LastDailyResetAt { get; set; }
        public int DailyDelegateDone { get; set; }
        public bool DailyDelegateClaimed { get; set; }
        public bool FreeExpRunDone { get; set; }
        public string WeekKey { get; set; } = "";
        public int BossFreeUsed { get; set; }
        public int WeeklyTaskDone { get; set; }
        public bool WeeklyTaskClaimed { get; set; }
        public string MonthKey { get; set; } = "";
        public Dictionary<string, int> MonthlyPurchases { get; set; } = new(StringComparer.Ordinal);
    }

    public sealed class StoryRecord
    {
        public bool Cleared { get; set; }
        public bool FirstClearRewarded { get; set; }
        public long LastClearedAt { get; set; }
    }

    public sealed class StageRecord
    {
        public bool Cleared { get; set; }
        public bool FirstClearRewarded { get; set; }
        public int ClearCount { get; set; }
        public long LastClearedAt { get; set; }
    }

    // ── 序列化 ────────────────────────────────────────────────────────

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static PlayerSave? FromJson(string json) => JsonSerializer.Deserialize<PlayerSave>(json, Json);

    /// <summary>新建一份最小存档。**只给序章主角一人**（`04 §1` 的协议等级 0 状态）。</summary>
    public static PlayerSave Create(string userId, string username, string protagonist, long nowMs)
    {
        PlayerSave s = new()
        {
            UserId = userId,
            Username = username,
            CreatedAt = nowMs,
            UpdatedAt = nowMs,
        };
        s.Profile.PlayerName = username;
        s.Profile.Protagonist = protagonist;
        s.Stamina.Current = 240;
        s.Stamina.LastRegenAt = nowMs;
        s.Progress.LastDailyResetAt = nowMs;
        s.Progress.WeekKey = WeekKeyOf(nowMs);
        s.Characters[protagonist] = new CharacterState
        {
            Level = 1,
            SkillLevels = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["basic"] = 1, ["resonance"] = 1, ["talent"] = 1, ["ultimate"] = 1,
            },
        };
        s.Team.Add(protagonist);
        return s;
    }

    /// <summary>周键：**周一 04:00** 翻周（`02 §10` 的 `RESET_HOUR = 4`）。</summary>
    public static string WeekKeyOf(long nowMs) => WeekKeyOf(DateTimeOffset.FromUnixTimeMilliseconds(nowMs));

    public static string WeekKeyOf(DateTimeOffset t)
    {
        DateTimeOffset reset = t.Hour < 4 ? t.AddHours(-4) : t;
        int diff = ((int)reset.DayOfWeek + 6) % 7;   // 周一 = 0
        DateTimeOffset monday = reset.AddDays(-diff);
        return $"{monday.Year:D4}-W{System.Globalization.ISOWeek.GetWeekOfYear(monday.DateTime):D2}";
    }
}
