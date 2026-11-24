using System.Net;
using WaningOath.Core.Game;
using System.Text;
using System.Text.Json;
using WaningOath.Core;
using WaningOath.Core.Battle;
using WaningOath.Core.Data;
using WaningOath.Server.Game;

namespace WaningOath.Server;

/// <summary>
/// `/api/game/*` 的路由与鉴权。
/// </summary>
/// <remarks>
/// <b>鉴权模型</b>（`06 §2.0`）：账号密码与会话在 Node 网站侧，网站把 `/api/game/*` 原样转发过来，
/// 并在转发时注入 <c>X-WO-User</c> / <c>X-WO-Role</c>。因此 .NET 侧**不实现登录**。
/// <para>
/// ⚠️ 这条设计的代价是：<c>X-WO-User</c> 变成了一个**必须被保护的信任边界**。
/// 直接访问 .NET 端口 + 伪造一个头 = 读到别人的存档。所以：
/// <list type="number">
///   <item>默认只绑 `127.0.0.1`（不是 `0.0.0.0`）。</item>
///   <item>网关密钥 <c>X-WO-Gateway</c> 一旦用 `--gateway-secret` 配上就**强制校验**。</item>
///   <item>请求体大小、`userId` 字符集、主角名字符集都有硬上限。</item>
/// </list>
/// </para>
/// </remarks>
public sealed class GameApi(
    GameData data,
    GameService game,
    PlayerStore store,
    AuditLog audit,
    string? gatewaySecret)
{
    private const int MaxBodyBytes = 4 * 1024 * 1024;

    public void Handle(HttpExchange ctx)
    {
        string path = ctx.Path;
        string method = ctx.Method.ToUpperInvariant();

        try
        {
            Route(ctx, method, path);
        }
        catch (ArgumentException ex)
        {
            Fail(ctx, 400, ex.Message);
        }
        catch (KeyNotFoundException ex)
        {
            Fail(ctx, 404, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            Fail(ctx, 409, ex.Message);
        }
    }

    private void Route(HttpExchange ctx, string method, string path)
    {
        // 健康检查**不需要身份**：它是给运维和账号体系探活用的。
        if (method == "GET" && path == "/api/game/health")
        {
            Ok(ctx, new
            {
                ok = true,
                service = "WaningOath.Server",
                build = "M4",
                dataRoot = data.Root,
                stages = data.Stages.Count,
                characters = data.Characters.Count,
                players = store.ListPlayers().Count,
                gatewaySecretRequired = gatewaySecret is not null,
            });
            return;
        }

        // ── 身份（网关注入） ─────────────────────────────────────────
        if (gatewaySecret is not null)
        {
            string? got = ctx.Header("X-WO-Gateway");
            if (!string.Equals(got, gatewaySecret, StringComparison.Ordinal))
            {
                audit.Write("auth.gatewayRejected", "-", new { path });
                Fail(ctx, 401, "网关密钥无效。请求必须经网站转发（06 §2.0）。");
                return;
            }
        }

        string? userId = ctx.Header("X-WO-User");
        string role = ctx.Header("X-WO-Role") ?? "player";
        if (string.IsNullOrWhiteSpace(userId))
        {
            Fail(ctx, 401, "缺少 X-WO-User —— 请求必须由网站转发并带上登录身份。");
            return;
        }
        userId = PlayerStore.Sanitize(userId);

        // 管理员路由单独判：它们在存档之外，且必须留审计。
        if (path.StartsWith("/api/game/admin/", StringComparison.Ordinal))
        {
            if (!string.Equals(role, "admin", StringComparison.Ordinal))
            {
                audit.Write("auth.adminRejected", userId, new { path });
                Fail(ctx, 403, "需要管理员权限。");
                return;
            }
            HandleAdmin(ctx, method, path, userId);
            return;
        }

        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        switch (method, path)
        {
            case ("GET", "/api/game/config"):
                Ok(ctx, Config());
                return;

            case ("GET", "/api/game/stages"):
                Ok(ctx, game.StageList());
                return;

            case ("GET", "/api/game/stories"):
                Ok(ctx, game.StoryList());
                return;

            case ("GET", "/api/game/state"):
            {
                PlayerSave save = store.LoadOrCreate(userId, OptionalNameHeader(ctx, userId),
                    "char-shouyueren", now);
                game.SettleTime(save);
                store.Save(save);
                Ok(ctx, new { ok = true, save });
                return;
            }

            case ("POST", "/api/game/save"):
            {
                PlayerSave? old = store.TryLoad(userId);
                if (old is null) { Fail(ctx, 404, "存档不存在；先 GET /api/game/state。"); return; }

                // 时间先结算，否则"客户端提交的配额"必然被这条校验误杀。
                game.SettleTime(old);

                PlayerSave incoming = ReadJson<PlayerSave>(ctx) ?? throw new ArgumentException("请求体不是合法存档。");
                // 归属强制覆写（06 §3.1）：防把别账号内容写进来。
                incoming.UserId = old.UserId;
                incoming.Username = old.Username;
                incoming.CreatedAt = old.CreatedAt;
                incoming.Profile.ProtocolLevel = old.Profile.ProtocolLevel;

                ApiResult check = game.ValidateSaveWrite(old, incoming);
                if (!check.Ok) { Fail(ctx, check.Status, check.Error!); return; }

                incoming.UpdatedAt = now;
                store.Save(incoming);
                audit.Write("game.save", userId, new { characters = incoming.Characters.Count });
                Ok(ctx, new { ok = true, updatedAt = incoming.UpdatedAt });
                return;
            }

            case ("POST", "/api/game/team"):
            {
                PlayerSave save = Require(userId, now);
                TeamRequest req = ReadJson<TeamRequest>(ctx) ?? throw new ArgumentException("请求体为空。");
                ApiResult r = game.SetTeam(save, req.Team, req.Modes);
                if (r.Ok) store.Save(save);
                Respond(ctx, r);
                return;
            }

            case ("POST", "/api/game/weapon/equip"):
            {
                PlayerSave save = Require(userId, now);
                WeaponEquipRequest req = ReadJson<WeaponEquipRequest>(ctx) ?? throw new ArgumentException("请求体为空。");
                ApiResult r = game.EquipWeapon(save, req.CharacterId ?? "", req.WeaponId);
                if (r.Ok) store.Save(save);
                Respond(ctx, r);
                return;
            }

            case ("POST", "/api/game/shop/exchange"):
            {
                PlayerSave save = Require(userId, now);
                ShopExchangeRequest req = ReadJson<ShopExchangeRequest>(ctx) ?? throw new ArgumentException("请求体为空。");
                ApiResult r = game.ShopExchange(save, req.ItemId ?? "", req.Count, req.TargetId);
                if (r.Ok) store.Save(save);
                Respond(ctx, r);
                return;
            }

            case ("POST", "/api/game/daily/claim"):
            {
                PlayerSave save = Require(userId, now);
                ApiResult r = game.ClaimDailyDelegate(save);
                if (r.Ok) store.Save(save);
                Respond(ctx, r);
                return;
            }

            case ("POST", "/api/game/daily/free-exp"):
            {
                PlayerSave save = Require(userId, now);
                ApiResult r = game.ClaimFreeExp(save);
                if (r.Ok) store.Save(save);
                Respond(ctx, r);
                return;
            }

            case ("POST", "/api/game/weekly/claim"):
            {
                PlayerSave save = Require(userId, now);
                ApiResult r = game.ClaimWeeklyTask(save);
                if (r.Ok) store.Save(save);
                Respond(ctx, r);
                return;
            }

            case ("POST", "/api/game/profile"):
            {
                PlayerSave save = Require(userId, now);
                ProfileRequest req = ReadJson<ProfileRequest>(ctx) ?? throw new ArgumentException("请求体为空。");
                ApiResult r = game.SetProfile(save, req.ProtagonistGender, req.PlayerName, now, req.Skins);
                if (r.Ok) store.Save(save);
                Respond(ctx, r);
                return;
            }

            case ("POST", "/api/game/growth"):
            {
                PlayerSave save = Require(userId, now);
                GrowthRequest req = ReadJson<GrowthRequest>(ctx) ?? throw new ArgumentException("请求体为空。");
                ApiResult r = req.Action switch
                {
                    "levelUp" => game.LevelUp(save, req.CharacterId ?? "", req.Items ?? []),
                    "breakthrough" => game.Breakthrough(save, req.CharacterId ?? ""),
                    "skill" => game.SkillUp(save, req.CharacterId ?? "", req.Slot ?? "basic", req.Times),
                    "weapon" => game.WeaponUp(save, req.WeaponId ?? "", req.Level, req.Overwrite),
                    "gear" => game.GearAction(save, req.GearAction ?? "equip", req.CharacterId ?? "", req.Uid ?? "", req.Level),
                    _ => ApiResult.Bad(400, $"未知的养成动作 '{req.Action}'。可用：levelUp / breakthrough / skill / weapon / gear。"),
                };
                if (r.Ok) store.Save(save);
                Respond(ctx, r);
                return;
            }

            case ("POST", "/api/game/stage/clear"):
            {
                PlayerSave save = Require(userId, now);
                StageClearRequest req = ReadJson<StageClearRequest>(ctx) ?? throw new ArgumentException("请求体为空。");
                ApiResult r = game.StageClear(save, req.StageId ?? "", req.Outcome ?? "AllyWin");
                if (r.Ok) store.Save(save);
                Respond(ctx, r);
                return;
            }

            case ("POST", "/api/game/story/clear"):
            {
                PlayerSave save = Require(userId, now);
                StoryClearRequest req = ReadJson<StoryClearRequest>(ctx) ?? throw new ArgumentException("请求体为空。");
                ApiResult r = game.StoryClear(save, req.StoryId ?? "");
                if (r.Ok) store.Save(save);
                Respond(ctx, r);
                return;
            }

            case ("POST", "/api/game/battle/verify"):
            {
                PlayerSave save = Require(userId, now);
                BattleRequest req = ReadJson<BattleRequest>(ctx) ?? throw new ArgumentException("请求体为空。");
                List<ActionChoice> choices = [.. (req.Choices ?? []).Select(static c => new ActionChoice(c.SkillKey, c.TargetSerial))];
                ApiResult r = game.BattleVerify(save, req.StageId ?? "", req.Seed, choices, req.ClaimedOutcome, req.ClaimedLogHash);
                Respond(ctx, r);
                return;
            }

            case ("POST", "/api/game/gacha"):
            {
                PlayerSave save = Require(userId, now);
                GachaRequest req = ReadJson<GachaRequest>(ctx) ?? throw new ArgumentException("请求体为空。");
                ApiResult r = game.Gacha(save, req.Pool ?? "standard", req.Count, req.Currency ?? "auto");
                if (r.Ok) store.Save(save);
                Respond(ctx, r);
                return;
            }

            // ── 开发/验收：一键解锁全部角色 ──────────────────────────────
            // 只允许本机回环请求；用途是"全角色实装"验收，不改变正常抽卡/养成路径。
            case ("POST", "/api/game/dev/unlock-all"):
            {
                if (!ctx.IsLocal)
                {
                    Fail(ctx, 403, "只允许本机回环调用。");
                    return;
                }
                PlayerSave save = Require(userId, now);
                int added = 0;
                foreach (string id in data.Characters.Keys.OrderBy(static x => x, StringComparer.Ordinal))
                {
                    if (save.Characters.ContainsKey(id)) continue;
                    save.Characters[id] = new PlayerSave.CharacterState
                    {
                        Level = 1,
                        SkillLevels = new Dictionary<string, int>(StringComparer.Ordinal)
                        {
                            ["basic"] = 1, ["resonance"] = 1, ["talent"] = 1, ["ultimate"] = 1,
                        },
                    };
                    added++;
                }
                save.UpdatedAt = now;
                store.Save(save);
                audit.Write("dev.unlockAll", userId, new { added, total = save.Characters.Count });
                Ok(ctx, new { ok = true, added, total = save.Characters.Count });
                return;
            }

            // ── 全角色名册（图鉴/编队用；只读） ──────────────────────────
            case ("GET", "/api/game/roster"):
            {
                PlayerSave save = Require(userId, now);
                Ok(ctx, new
                {
                    ok = true,
                    characters = data.Characters.Values
                        .OrderBy(static c => c.Id, StringComparer.Ordinal)
                        .Select(c => new
                        {
                            id = c.Id,
                            name = c.Name,
                            title = c.Title,
                            rarity = c.Rarity,
                            elements = c.ResolveElements().Select(static e => e.ToString().ToLowerInvariant()),
                            path = c.Path,
                            weaponType = c.WeaponType,
                            owned = save.Characters.ContainsKey(c.Id),
                        }),
                });
                return;
            }

            default:
                Fail(ctx, 404, $"未知路由：{method} {path}");
                return;
        }
    }

    private PlayerSave Require(string userId, long now)
    {
        PlayerSave? save = store.TryLoad(userId);
        if (save is null) throw new InvalidOperationException("存档不存在；先 GET /api/game/state。");
        game.SettleTime(save);
        return save;
    }

    private object Config() => new
    {
        ok = true,
        gachaCost = data.Gacha.Cost,
        gachaRates = data.Gacha.Rates,
        pools = data.Gacha.Pools,
        skillCapsByEidolon = data.Growth.SkillCapsByEidolon,
        levelCap = data.Growth.Level.Cap,
        breakthroughNodes = data.Growth.Level.BreakthroughNodes,
        expItems = data.Growth.ExpItems,
        items = data.Growth.Items,
        constants = new
        {
            staminaMax = BattleConstants.StaminaMax,
            staminaRegenSec = BattleConstants.StaminaRegenSec,
            resetHour = BattleConstants.ResetHour,
            protocolLevelRequirement = new[] { 20, 40, 60, 80 },
            protocolLevelRequiredCharacters = 4,
            maxRoundPerStage = BattleConstants.MaxRoundPerStage,
        },
    };

    /// <summary>
    /// 可选显示名头 <c>X-WO-Name</c>。
    /// </summary>
    /// <remarks>
    /// <b>为什么是"可选"而且必须包在 try 里</b>：HTTP 头按规范是 **latin-1**，
    /// 中文昵称**根本走不了这条路** —— 读一个含非 ASCII 的头，旧 `HttpListener` 会抛
    /// <c>ArgumentException: Specified value has invalid Control characters</c>，
    /// 而那个异常会把**整个请求**打成 400（连 `X-WO-User` 都不再被处理）。
    /// <para>
    /// 正确做法是**不再用头传名字**：网站侧已经删掉这个头，客户端登录后通过
    /// `POST /api/game/profile`（JSON body，UTF-8 安全）写名字。这里保留读它的能力只为兼容
    /// 别的调用方，并且**只接受纯 ASCII**，读失败或含非 ASCII 就退回 <paramref name="fallback"/>。
    /// </para>
    /// </remarks>
    private static string OptionalNameHeader(HttpExchange ctx, string fallback)
    {
        try
        {
            string? name = ctx.Header("X-WO-Name");
            if (string.IsNullOrWhiteSpace(name)) return fallback;
            foreach (char c in name)
            {
                if (c is < ' ' or > '~') return fallback;
            }
            return name.Length > 24 ? fallback : name;
        }
        catch (ArgumentException)
        {
            return fallback;
        }
    }

    private void HandleAdmin(HttpExchange ctx, string method, string path, string userId)    {
        // ⚠️ **这一节的口径以 `本地API调用网站/public/admin-game.html` 为准，不是以"服务端觉得好看"为准。**
        // 那张页面是先照着 Node 实现写的，.NET 迁移时字段名/响应形状各写各的，结果是**后台 6 个页签里 4 个是坏的**：
        // 玩家列表的「配额」「角色数」「总权阶」三列空白（服务端发的是 `current` / `characters`，页面读 `stamina` / `characterCount` / `totalEidolon`）、
        // 玩家详情整块打不开（页面读 `r.player`，服务端发 `r.save`）、审计表整片空白（页面读 `r.entries`，服务端发 `r.lines`）、
        // 发放 / 改档 / 卡池配置三个动作直接 404（服务端没实现）。
        // ⇒ 修法统一为**把服务端改成页面已经在用的那份契约**：这样 Node 与 .NET 两条后端可以互换，页面一个字不用动。
        if (method == "GET" && path == "/api/game/admin/players")
        {
            List<object> rows = [];
            foreach (string id in store.ListPlayers())
            {
                PlayerSave? s = store.TryLoad(id);
                if (s is null) continue;
                rows.Add(new
                {
                    s.UserId,
                    s.Username,
                    playerName = s.Profile.PlayerName,
                    protocolLevel = s.Profile.ProtocolLevel,
                    characterCount = s.Characters.Count,
                    maxLevel = s.Characters.Count == 0 ? 0 : s.Characters.Values.Max(static c => c.Level),
                    totalEidolon = s.Characters.Values.Sum(static c => c.Eidolon),
                    protocolCode = s.Currency.ProtocolCode,
                    stamina = s.Stamina.Current,
                    totalPulls = s.Gacha.TotalPulls,
                    updatedAt = s.UpdatedAt,
                    suspicious = Suspicious(s),
                });
            }
            Ok(ctx, new { ok = true, count = rows.Count, players = rows });
            return;
        }

        if (method == "GET" && path == "/api/game/admin/audit")
        {
            // 同时发 `entries`（页面读这个）与 `lines`（`Sim` / 其他脚本读这个）—— 见本节开头的说明。
            List<object> entries = audit.TailForUi(300);
            Ok(ctx, new { ok = true, count = entries.Count, entries, lines = audit.Tail(300) });
            return;
        }

        if (method == "GET" && path.StartsWith("/api/game/admin/player/", StringComparison.Ordinal))
        {
            string target = PlayerStore.Sanitize(path["/api/game/admin/player/".Length..]);
            PlayerSave? s = store.TryLoad(target);
            if (s is null) { Fail(ctx, 404, $"没有玩家 '{target}'。"); return; }
            Ok(ctx, new { ok = true, player = s, save = s, suspicious = Suspicious(s) });
            return;
        }

        if (method == "POST" && path == "/api/game/admin/patch")
        {
            PatchRequest req = ReadJson<PatchRequest>(ctx) ?? throw new ArgumentException("请求体为空。");
            if (string.IsNullOrWhiteSpace(req.UserId)) throw new ArgumentException("缺少 userId。");
            string target = PlayerStore.Sanitize(req.UserId);
            PlayerSave? s = store.TryLoad(target);
            if (s is null) { Fail(ctx, 404, $"没有玩家 '{target}'。"); return; }

            // **先按存档真实情况重算协议等级，再允许管理员显式覆盖**（顺序不能反）：
            // 反过来的话，刚设的值会立刻被重算覆盖掉，后台输入框"永远无效却提示已应用"。
            s.Profile.ProtocolLevel = GameService.RecomputeProtocolLevel(s);
            List<string> applied = ["protocolLevel(recomputed)"];

            if (req.Set is { } set)
            {
                if (set.ProtocolLevel is int pl)
                {
                    s.Profile.ProtocolLevel = Math.Clamp(pl, 0, 4);
                    applied.Add($"protocolLevel={s.Profile.ProtocolLevel}");
                }
                if (set.PlayerName is string name)
                {
                    string clean = name.Trim();
                    if (!GameService.IsSafePlayerName(clean))
                        throw new ArgumentException("主角名不符合白名单（06 §3.1；该字段会渲染进后台 HTML）。");
                    s.Profile.PlayerName = clean;
                    applied.Add("playerName");
                }
                if (set.Stamina is int sta)
                {
                    s.Stamina.Current = Math.Clamp(sta, 0, BattleConstants.StaminaMax);
                    applied.Add($"stamina={s.Stamina.Current}");
                }
                if (set.Character is { } ch && !string.IsNullOrWhiteSpace(ch.Id))
                {
                    if (!s.Characters.TryGetValue(ch.Id, out PlayerSave.CharacterState? cs))
                        throw new ArgumentException($"该玩家没有角色 '{ch.Id}'。");
                    if (ch.Level is int lv) cs.Level = Math.Clamp(lv, 1, data.Growth.Level.Cap);
                    if (ch.Eidolon is int ei) cs.Eidolon = Math.Clamp(ei, 0, data.Growth.Eidolon.Cap);
                    if (ch.Breakthrough is int bt) cs.Breakthrough = Math.Clamp(bt, 0, data.Growth.Level.BreakthroughNodes.Count);
                    applied.Add($"{ch.Id}(lv{cs.Level}/e{cs.Eidolon}/bt{cs.Breakthrough})");
                }
            }

            if (req.ResetGacha)
            {
                int pulls = s.Gacha.TotalPulls;
                s.Gacha = new PlayerSave.GachaState { TotalPulls = pulls };
                applied.Add("gacha(reset)");
            }

            s.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            store.Save(s);
            audit.Write("admin.patch", userId, new { target, applied });
            Ok(ctx, new { ok = true, applied, player = s });
            return;
        }

        if (method == "POST" && path == "/api/game/admin/grant")
        {
            GrantRequest req = ReadJson<GrantRequest>(ctx) ?? throw new ArgumentException("请求体为空。");

            // 两种口径合流：页面发的是 `{grant:{...}, or:"all"|targetUserIds:[...]}`，
            // 早期脚本发的是扁平的 `{to:"all"|userIds:[...], protocolCode:...}`。**都认。**
            GrantPayload g = req.Grant ?? new GrantPayload
            {
                ProtocolCode = req.ProtocolCode,
                Credit = req.Credit,
                NormalTicket = req.NormalTicket,
                SpecialTicket = req.SpecialTicket,
                Items = req.Items,
                UnlockCharacters = req.UnlockCharacters,
            };

            bool all = string.Equals(req.Or, "all", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(req.To, "all", StringComparison.OrdinalIgnoreCase);
            List<string> wanted = req.TargetUserIds ?? req.UserIds ?? [];
            List<string> targets = all ? store.ListPlayers() : [.. wanted.Select(PlayerStore.Sanitize)];
            if (targets.Count == 0) { Fail(ctx, 400, "没有指定发放对象。"); return; }

            List<object> results = [];
            int done = 0;
            foreach (string id in targets)
            {
                PlayerSave? s = store.TryLoad(id);
                if (s is null) { results.Add(new { userId = id, ok = false, reason = "没有存档" }); continue; }

                if (g.ProtocolCode is int pc) s.Currency.ProtocolCode += pc;
                if (g.Credit is int cr) s.Currency.Credit += cr;
                if (g.Receipt is int rc) s.Currency.Receipt += rc;
                if (g.PassReceipt is int pr) s.Currency.PassReceipt += pr;
                if (g.SpecialTicket is int st) s.Currency.SpecialTicket += st;
                if (g.NormalTicket is int nt) s.Currency.NormalTicket += nt;
                // 「配额道具」：`03 §2` 只把它写成"命名提案：紧急调拨令（+60 配额）"，**道具本身没有落盘**，
                // 所以这里按"一个道具 = 直接补 60 配额"兑现，而不是伪造一个不存在的道具 id。
                if (g.StaminaItem is int si && si != 0)
                    s.Stamina.Current = Math.Clamp(s.Stamina.Current + 60 * si, 0, BattleConstants.StaminaMax);
                if (req.Stamina is int sta)
                    s.Stamina.Current = Math.Clamp(s.Stamina.Current + sta, 0, BattleConstants.StaminaMax);
                foreach (KeyValuePair<string, int> kv in g.Items ?? [])
                    s.Inventory[kv.Key] = s.Inventory.GetValueOrDefault(kv.Key) + kv.Value;
                foreach (string cid in g.UnlockCharacters ?? [])
                {
                    if (!data.Characters.ContainsKey(cid)) continue;
                    if (!s.Characters.ContainsKey(cid))
                        s.Characters[cid] = new PlayerSave.CharacterState
                        {
                            Level = 1,
                            SkillLevels = new Dictionary<string, int>(StringComparer.Ordinal)
                            {
                                ["basic"] = 1, ["resonance"] = 1, ["talent"] = 1, ["ultimate"] = 1,
                            },
                        };
                }
                s.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                store.Save(s);
                done++;
                results.Add(new { userId = id, ok = true, protocolCode = s.Currency.ProtocolCode });
            }

            audit.Write("admin.grant", userId, new { count = done, g.ProtocolCode, g.Credit });
            Ok(ctx, new { ok = true, count = done, granted = results, results });
            return;
        }

        if (method == "GET" && path == "/api/game/admin/config")
        {
            Ok(ctx, new { ok = true, gacha = data.Gacha, source = GachaOverridePath() });
            return;
        }

        if (method == "POST" && path == "/api/game/admin/config")
        {
            AdminConfigRequest req = ReadJson<AdminConfigRequest>(ctx) ?? throw new ArgumentException("请求体为空。");
            if (req.Gacha is null) { Fail(ctx, 400, "缺少 gacha 段。"); return; }

            List<string> bad = ValidateGachaPools(req.Gacha);
            if (bad.Count > 0)
            {
                Fail(ctx, 400, "卡池名单里有未登记的 id（05 §7 校验 10 的同类口径）：" + string.Join(" · ", bad));
                return;
            }

            // 写**覆盖文件**而不是改写 `data/tables/gacha.json`：那张表里带作者手写的 `_comment`，
            // 而 DTO 里没有这些键 ⇒ 直接回写会把注释整片吃掉。覆盖文件与 Node 时代的
            // `db.gameConfig.gacha` 是同一个思路（配置住在运行时数据区，不污染数据源）。
            string target = GachaOverridePath();
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            string json = JsonSerializer.Serialize(req.Gacha, PlayerSave.Json);
            string tmp = target + ".tmp";
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            File.Move(tmp, target, overwrite: true);

            ApplyGacha(data, req.Gacha);
            audit.Write("admin.config", userId, new { pools = req.Gacha.Pools.Keys.ToArray() });
            Ok(ctx, new { ok = true, gacha = data.Gacha, source = target });
            return;
        }

        if (method == "POST" && path == "/api/game/admin/reset-daily")
        {
            List<object> done = [];
            foreach (string id in store.ListPlayers())
            {
                PlayerSave? s = store.TryLoad(id);
                if (s is null) continue;
                s.Progress.LastDailyResetAt = 0;
                s.Progress.DailyDelegateDone = 0;
                s.Progress.DailyDelegateClaimed = false;
                s.Progress.FreeExpRunDone = false;
                s.Progress.WeekKey = "";
                s.Progress.BossFreeUsed = 0;
                s.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                store.Save(s);
                done.Add(id);
            }
            audit.Write("admin.resetDaily", userId, new { count = done.Count });
            Ok(ctx, new { ok = true, count = done.Count, players = done });
            return;
        }

        Fail(ctx, 404, $"未知管理路由：{method} {path}");
    }

    /// <summary>卡池配置的覆盖文件（与存档同级的运行时数据区）。</summary>
    private string GachaOverridePath()
        => Path.Combine(Path.GetDirectoryName(store.Root) ?? store.Root, "gacha-override.json");

    /// <summary>卡池名单里的 id 必须都在 `data/` 里登记过。</summary>
    /// <remarks>
    /// 这条检查是必要的而不是洁癖：一个手滑写错的角色 id 会让卡池**少一个五星**，
    /// 表现为"抽不到某人"，从日志里完全看不出来（`GetGachaPool` 只按名单过滤）。
    /// </remarks>
    private List<string> ValidateGachaPools(GachaTable t)
    {
        List<string> bad = [];
        foreach ((string poolId, GachaPoolDef pool) in t.Pools)
        {
            // ⚠️ 五星/四星名单里**角色与武器都可能出现**（`03 §3.2`：常驻池 = 常驻五星角色 + 常驻五星武器），
            // 所以只判"这个名字在 data/ 里存在吗"，**不能**判"它是不是角色" —— 第一版就是那样写的，
            // 于是把一把合法武器报成了错误（后台保存直接 400）。
            foreach (string id in pool.Five.Concat(pool.Four).Concat(pool.ThreeWeapons))
            {
                if (!data.Characters.ContainsKey(id) && !data.Weapons.ContainsKey(id)) bad.Add($"{poolId}.{id}");
            }
        }
        return [.. bad.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// 把管理员提交的卡池配置**就地**换进 <see cref="GameData.Gacha"/>。
    /// </summary>
    /// <remarks>
    /// `GameData.Gacha` 的 setter 是 `private set`（数据只在加载期赋值），所以这里逐字段覆盖。
    /// 不改成公开 setter 是因为"加载完之后还有人能整体替换数据表"本身就是个坑。
    /// </remarks>
    public static void ApplyGacha(GameData data, GachaTable incoming)
    {
        data.Gacha.Version = incoming.Version;
        data.Gacha.Cost = incoming.Cost;
        data.Gacha.Rates = incoming.Rates;
        data.Gacha.Pools = incoming.Pools;
    }

    /// <summary>异常数据高亮：**只提示，不自动处置**（反作弊上限见 `06 §0`）。</summary>
    private static List<string> Suspicious(PlayerSave s)
    {
        List<string> flags = [];
        int expected = GameService.RecomputeProtocolLevel(s);
        if (s.Profile.ProtocolLevel != expected) flags.Add($"协议等级 {s.Profile.ProtocolLevel} ≠ 重算值 {expected}");
        foreach (KeyValuePair<string, PlayerSave.CharacterState> kv in s.Characters)
        {
            if (kv.Value.Level > 80) flags.Add($"{kv.Key} 等级 {kv.Value.Level} > 80");
            if (kv.Value.Eidolon > 6) flags.Add($"{kv.Key} 权阶 {kv.Value.Eidolon} > 6");
        }
        if (s.Currency.ProtocolCode < 0) flags.Add("协议原码为负");
        return flags;
    }

    // ── 请求体模型 ───────────────────────────────────────────────────

    private sealed class GrowthRequest
    {
        public string? Action { get; set; }
        public string? CharacterId { get; set; }
        public Dictionary<string, int>? Items { get; set; }
        public string? Slot { get; set; }
        public int? Times { get; set; }
        public string? WeaponId { get; set; }
        public int? Level { get; set; }
        public int? Overwrite { get; set; }

        /// <summary>`gear` 动作的子动作：`equip` / `unequip` / `enhance`。</summary>
        public string? GearAction { get; set; }

        /// <summary>装备实例 id（`gear` 动作用）。</summary>
        public string? Uid { get; set; }
    }

    private sealed class StageClearRequest
    {
        public string? StageId { get; set; }
        public string? Outcome { get; set; }
    }

    /// <summary>
    /// 主角档案的写入体（`POST /api/game/profile`）。
    /// </summary>
    /// <remarks>
    /// **为什么需要这条**：客户端不再自己写存档（进度全部走服务端），所以"开局选的性别"
    /// 必须有一条自己的写入路径 —— 没有它，那个选择只能活在这一局的界面里。
    /// </remarks>
    private sealed class ProfileRequest
    {
        public string? ProtagonistGender { get; set; }
        public string? PlayerName { get; set; }

        /// <summary>衣装选择（键 = 角色 id，值 = 衣装 id）。</summary>
        public Dictionary<string, string>? Skins { get; set; }
    }

    private sealed class StoryClearRequest
    {
        public string? StoryId { get; set; }
    }

    private sealed class BattleRequest
    {
        public string? StageId { get; set; }
        public ulong Seed { get; set; }
        public List<ChoiceDto>? Choices { get; set; }

        /// <summary>客户端声称的结果。**服务端算完之后会拿它对比** —— 谎报会被 409 打回。</summary>
        public string? ClaimedOutcome { get; set; }

        /// <summary>客户端声称的日志指纹（`fnv1a64:...`）。</summary>
        public string? ClaimedLogHash { get; set; }
    }

    private sealed class ChoiceDto
    {
        public string SkillKey { get; set; } = "basic";
        public int? TargetSerial { get; set; }
    }

    private sealed class TeamRequest
    {
        public List<string>? Team { get; set; }
        public Dictionary<string, string>? Modes { get; set; }
    }

    private sealed class WeaponEquipRequest
    {
        public string? CharacterId { get; set; }
        public string? WeaponId { get; set; }
    }

    private sealed class ShopExchangeRequest
    {
        public string? ItemId { get; set; }
        public int Count { get; set; } = 1;
        public string? TargetId { get; set; }
    }

    private sealed class GachaRequest
    {
        public string? Pool { get; set; }
        public int Count { get; set; } = 1;
        public string? Currency { get; set; }
    }

    private sealed class GrantRequest
    {
        // ── 扁平口径（早期脚本 / `Sim` 用） ──
        public string? To { get; set; }
        public List<string>? UserIds { get; set; }
        public int? ProtocolCode { get; set; }
        public int? Credit { get; set; }
        public int? SpecialTicket { get; set; }
        public int? NormalTicket { get; set; }
        public int? Stamina { get; set; }
        public Dictionary<string, int>? Items { get; set; }
        public List<string>? UnlockCharacters { get; set; }

        // ── 嵌套口径（`admin-game.html` 实际发的） ──
        public string? Or { get; set; }
        public List<string>? TargetUserIds { get; set; }
        public GrantPayload? Grant { get; set; }
    }

    /// <summary>后台页面的发放表单体。字段名逐个对着 `admin-game.html` 的 `doGrant()`。</summary>
    private sealed class GrantPayload
    {
        public int? ProtocolCode { get; set; }
        public int? Credit { get; set; }
        public int? Receipt { get; set; }
        public int? PassReceipt { get; set; }
        public int? NormalTicket { get; set; }
        public int? SpecialTicket { get; set; }

        /// <summary>「配额道具」个数。见 <c>HandleAdmin</c> 里的说明（`03 §2` 只给了命名提案）。</summary>
        public int? StaminaItem { get; set; }

        public Dictionary<string, int>? Items { get; set; }
        public List<string>? UnlockCharacters { get; set; }
    }

    /// <summary>改档请求（字段名对着 `admin-game.html` 的 `applyPatch()` / `resetGacha()`）。</summary>
    private sealed class PatchRequest
    {
        public string? UserId { get; set; }
        public PatchSet? Set { get; set; }
        public bool ResetGacha { get; set; }
    }

    private sealed class PatchSet
    {
        public int? ProtocolLevel { get; set; }
        public string? PlayerName { get; set; }
        public int? Stamina { get; set; }
        public PatchCharacter? Character { get; set; }
    }

    private sealed class PatchCharacter
    {
        public string? Id { get; set; }
        public int? Level { get; set; }
        public int? Eidolon { get; set; }
        public int? Breakthrough { get; set; }
    }

    /// <summary>卡池配置写入体（字段名对着 `admin-game.html` 的 `saveGacha()`）。</summary>
    private sealed class AdminConfigRequest
    {
        public GachaTable? Gacha { get; set; }
    }

    // ── JSON 收发 ────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions Out = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static T? ReadJson<T>(HttpExchange ctx) where T : class
    {
        if (ctx.Body.Length > MaxBodyBytes)
            throw new ArgumentException($"请求体超过 {MaxBodyBytes} 字节上限。");
        if (ctx.Body.Length == 0) return null;
        string body = Encoding.UTF8.GetString(ctx.Body);
        return JsonSerializer.Deserialize<T>(body, PlayerSave.Json);
    }

    private static void Respond(HttpExchange ctx, ApiResult r)
    {
        if (!r.Ok)
        {
            object payload = r.Body is null
                ? new { ok = false, error = r.Error }
                : new { ok = false, error = r.Error, detail = r.Body };
            Write(ctx, r.Status, payload);
            return;
        }
        Write(ctx, 200, r.Body is null ? new { ok = true } : new { ok = true, result = r.Body });
    }

    private static void Ok(HttpExchange ctx, object payload) => Write(ctx, 200, payload);

    private static void Fail(HttpExchange ctx, int status, string error)
        => Write(ctx, status, new { ok = false, error });

    private static void Write(HttpExchange ctx, int status, object payload)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload, Out);
        ctx.Respond(status, bytes);
    }
}
