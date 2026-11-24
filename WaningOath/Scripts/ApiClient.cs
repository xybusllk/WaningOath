using Godot;
using System.Text.Json;
using WaningOath.Core.Battle;
using WaningOath.Core.Game;

namespace WaningOath;

/// <summary>
/// 与网站 / 游戏服务之间的那一层 HTTP。
/// </summary>
/// <remarks>
/// <b>两条拓扑都支持，因为"要不要同时开两个进程"不该由玩家去猜</b>：
/// <list type="number">
///   <item><b>网站（推荐）</b>：客户端连 <c>http://127.0.0.1:8080</c>。账号、会话、<c>/api/game/auth</c>
///         都在网站上，网站再把 <c>/api/game/*</c> 转发给 .NET 游戏服务并注入身份头。
///         ⇒ **只开网站是不够的**，游戏逻辑在 .NET 那一边（这正是"连不上服务器"的根因）。</item>
///   <item><b>直连</b>：客户端连 <c>http://127.0.0.1:8720</c>（.NET 游戏服务本身）。
///         没有账号体系，靠 <c>X-WO-User</c> 头认人 —— 开发 / 单人自用够用。</item>
/// </list>
/// <see cref="ProbeAsync"/> 会**按顺序试**这两个地址，用哪个由"谁答得上 <c>/api/game/health</c>"决定，
/// 玩家不需要知道这件事。
/// <para>
/// 所有调用都是 best-effort：连不上只把状态显示出来，**不阻断任何流程**（`06 §2.3`）。
/// </para>
/// </remarks>
public sealed class ApiClient
{
    private const string WebsiteUrl = "http://127.0.0.1:8080";
    private const string ServiceUrl = "http://127.0.0.1:8720";

    private readonly System.Net.Http.HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(6) };
    private readonly List<string> _candidates = [];

    public ApiClient()
    {
        string? env = System.Environment.GetEnvironmentVariable("WO_GAME_API");
        // 顺序：显式指定的 → 网站 → 直连服务。前两个都答不上才算"未连接"。
        if (!string.IsNullOrWhiteSpace(env)) _candidates.Add(env.TrimEnd('/'));
        _candidates.Add(WebsiteUrl);
        _candidates.Add(ServiceUrl);
    }

    /// <summary>当前生效的地址（探测之后才有意义）。</summary>
    public string BaseUrl { get; private set; } = "";

    /// <summary>`website`（有账号体系）还是 `service`（直连，无账号）。</summary>
    public string Mode { get; private set; } = "none";

    /// <summary>Bearer token（只有网站那条路有）。</summary>
    public string? Token { get; private set; }

    /// <summary>当前账号 id。</summary>
    public string UserId { get; private set; } = "";

    public string Nickname { get; private set; } = "";

    public bool IsAdmin { get; private set; }

    /// <summary>最近一次调用的结果描述，直接显示在界面上。</summary>
    public string Status { get; private set; } = "未连接";

    public bool Reachable { get; private set; }

    /// <summary>连线是否经过账号体系（决定"要不要登录"）。</summary>
    public bool NeedsLogin => Mode == "website";

    // ── 探测 ──────────────────────────────────────────────────────────

    /// <summary>按顺序试候选地址，谁答得上 <c>/api/game/health</c> 就用谁。</summary>
    public async Task<bool> ProbeAsync()
    {
        foreach (string baseUrl in _candidates)
        {
            try
            {
                using HttpRequestMessage req = new(HttpMethod.Get, baseUrl + "/api/game/health");
                using HttpResponseMessage res = await _http.SendAsync(req);
                if (!res.IsSuccessStatusCode) continue;
                string body = await res.Content.ReadAsStringAsync();
                if (!body.Contains("\"ok\":true", StringComparison.OrdinalIgnoreCase)) continue;

                BaseUrl = baseUrl;
                Mode = baseUrl.Contains(":8720", StringComparison.Ordinal) ? "service" : "website";
                Reachable = true;
                Status = Mode == "website" ? "已连接（网站）" : "已连接（直连服务）";
                return true;
            }
            catch (Exception ex)
            {
                // 把"为什么连不上"留在日志里：空 catch 会把代理 / 证书 / 端口问题
                // 全部伪装成"服务器没起"，上一轮就是这么误判的。
                GD.Print($"[连接] 探测 {baseUrl} 失败：{ex.GetType().Name}: {ex.Message}");
                // 试下一个候选。
            }
        }

        Reachable = false;
        BaseUrl = _candidates.Count > 0 ? _candidates[0] : ServiceUrl;
        Mode = "none";
        Status = "未连接（服务器没起？见 tools\\run-all.cmd）";
        return false;
    }

    // ── 账号 ──────────────────────────────────────────────────────────

    /// <summary>登录。网站那条路拿 token；直连那条路只记名字（没有账号体系）。</summary>
    public async Task<(bool Ok, string Message)> LoginAsync(string username, string password)
    {
        if (!Reachable) await ProbeAsync();
        if (!Reachable) return (false, Status);

        if (Mode == "service")
        {
            // 直连：没有账号体系，用户名就是身份（`X-WO-User`）。
            if (string.IsNullOrWhiteSpace(username)) return (false, "请输入名字。");
            UserId = username.Trim();
            Nickname = UserId;
            Status = "直连模式（无账号）";
            return (true, "已进入直连模式");
        }

        try
        {
            using HttpRequestMessage req = new(HttpMethod.Post, BaseUrl + "/api/game/auth");
            req.Content = Json($"{{\"username\":{Str(username)},\"password\":{Str(password)}}}");
            using HttpResponseMessage res = await _http.SendAsync(req);
            string body = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode) return (false, ExtractError(body, (int)res.StatusCode));

            Token = Field(body, "token");
            UserId = Field(body, "id");
            Nickname = Field(body, "nickname");
            IsAdmin = body.Contains("\"role\":\"admin\"", StringComparison.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(Token) || string.IsNullOrEmpty(UserId))
                return (false, "登录响应里没有 token / user.id。");

            Status = "已登录：" + (string.IsNullOrEmpty(Nickname) ? UserId : Nickname);
            return (true, Status);
        }
        catch (Exception ex)
        {
            return (false, "登录失败：" + ex.GetType().Name);
        }
    }

    /// <summary>注册一个账号并立刻登录。**第一个注册的账号是管理员**（网站侧的规则）。</summary>
    public async Task<(bool Ok, string Message)> RegisterAsync(string username, string password, string nickname)
    {
        if (!Reachable) await ProbeAsync();
        if (!Reachable) return (false, Status);
        if (Mode == "service") return (false, "直连模式下没有账号体系，直接进游戏就行。");

        try
        {
            using HttpRequestMessage req = new(HttpMethod.Post, BaseUrl + "/api/auth/register");
            string nick = string.IsNullOrWhiteSpace(nickname) ? username : nickname;
            req.Content = Json($"{{\"username\":{Str(username)},\"password\":{Str(password)},\"nickname\":{Str(nick)}}}");
            using HttpResponseMessage res = await _http.SendAsync(req);
            string body = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode) return (false, ExtractError(body, (int)res.StatusCode));
        }
        catch (Exception ex)
        {
            return (false, "注册失败：" + ex.GetType().Name);
        }

        return await LoginAsync(username, password);
    }

    // ── 存档 ──────────────────────────────────────────────────────────

    /// <summary>一次服务端调用的结果。</summary>
    public readonly record struct Reply(bool Ok, int Status, string Body, string Error)
    {
        /// <summary>服务端原话（成功时是 `result`，失败时是 `error`）—— 直接显示给玩家。</summary>
        public string Message => Ok ? Body : (Error.Length > 0 ? Error : $"服务返回 {Status}");
    }

    /// <summary>
    /// 通用调用：**所有会改存档的动作都走这里**。
    /// </summary>
    /// <remarks>
    /// 本作没有离线模式：进度全部在服务器上，客户端只是把操作送过去、把结果读回来。
    /// 所以每一个动作（养成 / 通关 / 剧情 / 档案）都是"一次服务端往返"，
    /// 没有"先改本地、回头再同步"这条路 —— 那条路会让"客户端与服务端不一致"变成一个必然事件。
    /// </remarks>
    public async Task<Reply> CallAsync(string path, string json)
    {
        if (!Reachable) return new Reply(false, 0, "", "没有连上服务器。");
        try
        {
            using HttpRequestMessage req = new(HttpMethod.Post, BaseUrl + path);
            Authorize(req);
            req.Content = Json(json);
            using HttpResponseMessage res = await _http.SendAsync(req);
            string body = await res.Content.ReadAsStringAsync();
            int code = (int)res.StatusCode;
            if (res.IsSuccessStatusCode)
            {
                Status = "已同步";
                return new Reply(true, code, ResultOf(body), "");
            }

            Status = $"被拒（{code}）";
            return new Reply(false, code, "", ExtractError(body, code));
        }
        catch (Exception ex)
        {
            Status = "调用失败：" + ex.GetType().Name;
            return new Reply(false, 0, "", "调用失败：" + ex.Message);
        }
    }

    /// <summary>养成结算（服务端权威）。</summary>
    public Task<Reply> GrowthAsync(string body) => CallAsync("/api/game/growth", body);

    /// <summary>写编队（1~4 人，顺序即站位）；modes 为双模态角色的模态选择。</summary>
    public Task<Reply> SetTeamAsync(IReadOnlyList<string> team, IReadOnlyDictionary<string, string>? modes = null)
    {
        List<string> ids = [];
        foreach (string id in team) ids.Add(Str(id));
        List<string> modeParts = [];
        if (modes is not null)
        {
            foreach ((string id, string mode) in modes) modeParts.Add($"{Str(id)}:{Str(mode)}");
        }
        return CallAsync("/api/game/team",
            $"{{\"team\":[{string.Join(",", ids)}],\"modes\":{{{string.Join(",", modeParts)}}}}}");
    }

    /// <summary>开发/验收：解锁全部角色（仅回环地址可用）。</summary>
    public Task<Reply> UnlockAllAsync() => CallAsync("/api/game/dev/unlock-all", "{}");

    /// <summary>装备/卸下武器（weaponId 为空 = 卸下）。</summary>
    public Task<Reply> EquipWeaponAsync(string characterId, string? weaponId)
        => CallAsync("/api/game/weapon/equip",
            $"{{\"characterId\":{Str(characterId)},\"weaponId\":{(weaponId is null ? "null" : Str(weaponId))}}}");

    /// <summary>回执商店兑换。targetId 用于权阶 / 武器覆写许可。</summary>
    public Task<Reply> ShopExchangeAsync(string itemId, int count, string? targetId = null)
        => CallAsync("/api/game/shop/exchange",
            $"{{\"itemId\":{Str(itemId)},\"count\":{count},\"targetId\":{(targetId is null ? "null" : Str(targetId))}}}");

    /// <summary>领取每日委托奖励。</summary>
    public Task<Reply> ClaimDailyAsync() => CallAsync("/api/game/daily/claim", "{}");

    /// <summary>领取每日档案演习（免费经验）。</summary>
    public Task<Reply> ClaimFreeExpAsync() => CallAsync("/api/game/daily/free-exp", "{}");

    /// <summary>领取周常任务奖励。</summary>
    public Task<Reply> ClaimWeeklyAsync() => CallAsync("/api/game/weekly/claim", "{}");

    /// <summary>抽卡。返回格式化后的出货列表。</summary>
    public async Task<(bool Ok, string Message, List<string> Pulls)> GachaAsync(string pool, int count)
    {
        if (!Reachable) return (false, "没有连上服务器。", []);
        try
        {
            using HttpRequestMessage req = new(HttpMethod.Post, BaseUrl + "/api/game/gacha");
            Authorize(req);
            req.Content = Json($"{{\"pool\":{Str(pool)},\"count\":{count}}}");
            using HttpResponseMessage res = await _http.SendAsync(req);
            string body = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode)
                return (false, ExtractError(body, (int)res.StatusCode), []);

            List<string> pulls = [];
            int receiptApp = 0;
            int receiptPass = 0;
            using JsonDocument doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("result", out JsonElement result))
            {
                if (result.TryGetProperty("receiptGained", out JsonElement gained))
                {
                    if (gained.TryGetProperty("application", out JsonElement ga) && ga.TryGetInt32(out int av)) receiptApp = av;
                    if (gained.TryGetProperty("pass", out JsonElement gp) && gp.TryGetInt32(out int pv)) receiptPass = pv;
                }
                if (result.TryGetProperty("pulls", out JsonElement arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement p in arr.EnumerateArray())
                    {
                        int rarity = p.TryGetProperty("rarity", out JsonElement r) ? r.GetInt32() : 0;
                        string name = p.TryGetProperty("name", out JsonElement n) ? (n.GetString() ?? "") : "";
                        bool isNew = p.TryGetProperty("isNew", out JsonElement nn) && nn.GetBoolean();
                        bool guaranteed = p.TryGetProperty("guaranteed", out JsonElement gg) && gg.GetBoolean();
                        pulls.Add($"{new string('★', Math.Clamp(rarity, 1, 5))} {name}" +
                                  (isNew ? "　NEW" : "") + (guaranteed ? "　保底" : ""));
                    }
                }
            }
            Status = "抽卡完成";
            return (true, $"抽到 {pulls.Count} 个结果。申请回执 +{receiptApp} · 通过回执 +{receiptPass}", pulls);
        }
        catch (Exception ex)
        {
            Status = "抽卡失败：" + ex.GetType().Name;
            return (false, "抽卡失败：" + ex.Message, []);
        }
    }

    /// <summary>写主角档案（性别 / 主角名 / 衣装）。</summary>
    public Task<Reply> SetProfileAsync(string? gender, string? playerName, string? skinCharacter = null, string? skinId = null)
    {
        List<string> parts = [];
        if (!string.IsNullOrEmpty(gender)) parts.Add($"\"protagonistGender\":{Str(gender)}");
        if (!string.IsNullOrEmpty(playerName)) parts.Add($"\"playerName\":{Str(playerName)}");
        if (!string.IsNullOrEmpty(skinCharacter) && !string.IsNullOrEmpty(skinId))
            parts.Add($"\"skins\":{{{Str(skinCharacter)}:{Str(skinId)}}}");
        return CallAsync("/api/game/profile", "{" + string.Join(",", parts) + "}");
    }

    /// <summary>拉服务端存档（`GET /api/game/state`）。**这是"当前进度"的唯一来源。**</summary>
    public async Task<PlayerSave?> FetchSaveAsync()
    {
        if (!Reachable || string.IsNullOrEmpty(UserId)) return null;
        try
        {
            using HttpRequestMessage req = new(HttpMethod.Get, BaseUrl + "/api/game/state");
            Authorize(req);
            using HttpResponseMessage res = await _http.SendAsync(req);
            string body = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode)
            {
                Status = $"读档被拒（{(int)res.StatusCode}）";
                return null;
            }

            // 响应是 `{ ok, save: {...} }` —— 把 `save` 那一段交给 PlayerSave 解析。
            int at = body.IndexOf("\"save\":", StringComparison.Ordinal);
            if (at < 0) return null;
            string json = body[(at + "\"save\":".Length)..].TrimEnd();
            if (json.EndsWith('}')) json = json[..^1].TrimEnd();
            return PlayerSave.FromJson(json);
        }
        catch (Exception ex)
        {
            Status = "拉存档失败：" + ex.GetType().Name;
            return null;
        }
    }

    /// <summary>上报剧情（章）通关。**奖励由服务端发**（它带白名单与锚点校验）。</summary>
    public Task<Reply> ReportStoryClearAsync(string storyId)
        => CallAsync("/api/game/story/clear", $"{{\"storyId\":{Str(storyId)}}}");

    /// <summary>上报一次通关；掉落与首通奖励以服务端返回为准。</summary>
    public Task<Reply> ReportStageClearAsync(string stageId, string outcome)
        => CallAsync("/api/game/stage/clear", $"{{\"stageId\":{Str(stageId)},\"outcome\":{Str(outcome)}}}");

    /// <summary>上报通关并解析奖励摘要 / 装备掉落数量（结算页给玩家看）。</summary>
    public async Task<(bool Ok, int GearDrops, string Summary, string Error)> ReportStageClearDetailedAsync(
        string stageId, string outcome)
    {
        if (!Reachable) return (false, 0, "", "没有连上服务器。");
        try
        {
            using HttpRequestMessage req = new(HttpMethod.Post, BaseUrl + "/api/game/stage/clear");
            Authorize(req);
            req.Content = Json($"{{\"stageId\":{Str(stageId)},\"outcome\":{Str(outcome)}}}");
            using HttpResponseMessage res = await _http.SendAsync(req);
            string body = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode)
                return (false, 0, "", ExtractError(body, (int)res.StatusCode));

            int drops = 0;
            int code = 0;
            int credit = 0;
            using JsonDocument doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("result", out JsonElement result))
            {
                if (result.TryGetProperty("gearDrops", out JsonElement arr) && arr.ValueKind == JsonValueKind.Array)
                    drops = arr.GetArrayLength();
                if (result.TryGetProperty("protocolCode", out JsonElement pc) && pc.TryGetInt32(out int pcv)) code = pcv;
                if (result.TryGetProperty("credit", out JsonElement cr) && cr.TryGetInt32(out int crv)) credit = crv;
            }
            List<string> parts = [];
            if (code > 0) parts.Add($"协议原码 +{code}");
            if (credit > 0) parts.Add($"授信 +{credit}");
            Status = "已同步";
            return (true, drops, parts.Count > 0 ? string.Join(" · ", parts) : "无额外货币奖励", "");
        }
        catch (Exception ex)
        {
            Status = "结算失败：" + ex.GetType().Name;
            return (false, 0, "", "结算失败：" + ex.Message);
        }
    }

    /// <summary>上报战斗重放（服务端会自己算一遍，比对结果与日志指纹）。</summary>
    public Task<Reply> ReportBattleAsync(string stageId, ulong seed, string outcome, string logHash,
        IReadOnlyList<ActionChoice> choices)
    {
        List<string> items = [];
        foreach (ActionChoice choice in choices)
        {
            string target = choice.TargetSerial is int serial ? serial.ToString() : "null";
            items.Add($"{{\"skillKey\":{Str(choice.SkillKey)},\"targetSerial\":{target}}}");
        }
        return CallAsync("/api/game/battle/verify",
            $"{{\"stageId\":{Str(stageId)},\"seed\":{seed},\"claimedOutcome\":{Str(outcome)}," +
            $"\"claimedLogHash\":{Str(logHash)},\"choices\":[{string.Join(",", items)}]}}");
    }

    /// <summary>把 `{ ok, result: <...> }` 里的 `result` 段摘出来显示；没有就返回原文。</summary>
    private static string ResultOf(string body)
    {
        int at = body.IndexOf("\"result\":", StringComparison.Ordinal);
        if (at < 0) return Truncate(body, 240);
        string json = body[(at + "\"result\":".Length)..].TrimEnd();
        if (json.EndsWith('}')) json = json[..^1].TrimEnd();
        return Truncate(json, 240);
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";

    /// <summary>
    /// 带身份。**网站那条路用 Bearer token，直连那条路用 `X-WO-User`**。
    /// </summary>
    /// <remarks>
    /// 不能用"两个都带"来图省事：网站会**忽略**客户端自带的 <c>X-WO-User</c>（防伪造，
    /// 由服务端会话推导身份），而直连服务没有 Bearer 的解析。两条路各带各的才都成立。
    /// </remarks>
    private void Authorize(HttpRequestMessage req)
    {
        if (Mode == "website" && !string.IsNullOrEmpty(Token))
        {
            req.Headers.Add("Authorization", "Bearer " + Token);
            return;
        }
        req.Headers.Add("X-WO-User", UserId);
        req.Headers.Add("X-WO-Role", IsAdmin ? "admin" : "player");
    }

    // ── 小工具 ────────────────────────────────────────────────────────

    private static System.Net.Http.StringContent Json(string json)
        => new(json, System.Text.Encoding.UTF8, "application/json");

    /// <summary>JSON 字符串字面量转义（拼请求体用）。</summary>
    public static string Str(string s)
        => System.Text.Json.JsonSerializer.Serialize(s ?? "");

    private static string Field(string json, string name)
    {
        int at = json.IndexOf('"' + name + '"', StringComparison.Ordinal);
        if (at < 0) return "";
        int colon = json.IndexOf(':', at);
        if (colon < 0) return "";
        int start = json.IndexOf('"', colon + 1);
        if (start < 0) return "";
        int end = start + 1;
        while (end < json.Length && (json[end] != '"' || json[end - 1] == '\\')) end++;
        return end < json.Length ? json[(start + 1)..end] : "";
    }

    private static string ExtractError(string body, int code)
    {
        string msg = Field(body, "error");
        return string.IsNullOrEmpty(msg) ? $"服务返回 {code}" : msg;
    }
}
