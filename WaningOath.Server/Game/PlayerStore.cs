using System.Text;
using WaningOath.Core.Game;
using System.Text.Json;

namespace WaningOath.Server.Game;

/// <summary>
/// 玩家存档的落盘层：`&lt;store&gt;/&lt;userId&gt;.json`，**原子写**（先写 `.tmp` 再改名）。
/// </summary>
/// <remarks>
/// <b>为什么必须原子写</b>：服务进程与玩家同时在跑，一次断电 / 一次 kill 落在
/// "文件已截断、内容还没写完"的窗口里，玩家的整个养成进度就没了。
/// `File.Replace` 在同盘上是原子的；跨盘或不支持时退化为"先删后移"，
/// 但那一步只在异常路径上，且已先写好 `.tmp`。
/// </remarks>
public sealed class PlayerStore(string root, AuditLog audit)
{
    private readonly string _root = Path.GetFullPath(root);
    private readonly Lock _gate = new();

    public string Root => _root;

    private string PathOf(string userId) => Path.Combine(_root, Sanitize(userId) + ".json");

    /// <summary>
    /// userId 必须落在一个安全文件名里。
    /// </summary>
    /// <remarks>
    /// ⚠️ 这条不是洁癖：`userId` 来自网关转发头，而它会被直接拼进路径。
    /// `../` 或 `..\` 会让一次 `/api/game/state` 覆盖仓库里任意一个文件。
    /// </remarks>
    public static string Sanitize(string userId)
    {
        StringBuilder sb = new();
        foreach (char c in userId)
        {
            if (char.IsLetterOrDigit(c) || c is '_' or '-') sb.Append(c);
        }
        string s = sb.ToString();
        if (s.Length == 0) throw new ArgumentException("userId 为空或只含非法字符。", nameof(userId));
        if (s.Length > 64) s = s[..64];
        return s;
    }

    public bool Exists(string userId) => File.Exists(PathOf(userId));

    public PlayerSave? TryLoad(string userId)
    {
        string path = PathOf(userId);
        if (!File.Exists(path)) return null;
        try
        {
            return PlayerSave.FromJson(File.ReadAllText(path, Encoding.UTF8));
        }
        catch (JsonException ex)
        {
            audit.Write("player.load.failed", userId, new { error = ex.Message });
            throw new InvalidOperationException($"存档 '{userId}' 不是合法 JSON：{ex.Message}", ex);
        }
    }

    /// <summary>读存档；不存在则按序章初始状态创建一份并落盘。</summary>
    public PlayerSave LoadOrCreate(string userId, string username, string protagonist, long nowMs)
    {
        lock (_gate)
        {
            PlayerSave? save = TryLoad(userId);
            if (save is not null) return save;
            PlayerSave created = PlayerSave.Create(userId, username, protagonist, nowMs);
            Save(created);
            audit.Write("player.created", userId, new { protagonist });
            return created;
        }
    }

    public void Save(PlayerSave save)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(_root);
            string path = PathOf(save.UserId);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, save.ToJson(), new UTF8Encoding(false));

            // ⚠️ **换名这一步必须带兜底**。
            //
            // `File.Replace` 走的是 Win32 `ReplaceFile`，它比 `File.Move` 多做了两件"附加"的事：
            // 继承目标文件的 ACL / 属性、以及可选的备份文件。因此它**会在一批正常场景下失败**：
            // 目标被别的进程打开着（杀毒、备份、编辑器）、文件在一个受限的目录里、
            // 或者宿主给进程套了一层文件访问策略（本项目的自动化环境就是这样）。
            // 原来的写法没有兜底 ⇒ 一次失败 = **整个服务进程带着异常退出**，
            // 而这个方法的语义只是"把存档写下去"。
            //
            // `File.Move(overwrite: true)` 在同盘上同样是一次原子换名（`MoveFileEx` + `REPLACE_EXISTING`），
            // 只是不做上面那两件附加的事 —— 对一个"我们自己写出来的临时文件"来说，那两件事本来也不需要。
            if (File.Exists(path))
            {
                try
                {
                    File.Replace(tmp, path, destinationBackupFileName: null);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
                {
                    File.Move(tmp, path, overwrite: true);
                }
            }
            else
            {
                File.Move(tmp, path);
            }
        }
    }

    /// <summary>列出全部已落盘的玩家 id。</summary>
    public List<string> ListPlayers()
    {
        if (!Directory.Exists(_root)) return [];
        return [.. Directory.EnumerateFiles(_root, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(static n => !string.IsNullOrEmpty(n))
            .Select(static n => n!)
            .Order(StringComparer.Ordinal)];
    }
}

/// <summary>审计日志：**追加写**，不重写整文件（`06 §3.3`）。</summary>
public sealed class AuditLog(string path, int cap = 4000)
{
    private readonly string _path = Path.GetFullPath(path);
    private readonly Lock _gate = new();

    public void Write(string action, string userId, object? detail = null)
    {
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? ".");
                string line = JsonSerializer.Serialize(new
                {
                    at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    action,
                    userId,
                    detail,
                });
                File.AppendAllText(_path, line + Environment.NewLine, new UTF8Encoding(false));
                TrimIfHuge();
            }
        }
        catch (IOException)
        {
            // 审计写失败**不能让业务失败** —— 它只是事后追溯用的。
        }
    }

    /// <summary>行数超过 <paramref name="cap"/> 时只保留最后一半（避免无限增长）。</summary>
    private void TrimIfHuge()
    {
        string[] lines = File.ReadAllLines(_path);
        if (lines.Length <= cap) return;
        File.WriteAllLines(_path, lines[(lines.Length / 2)..], new UTF8Encoding(false));
    }

    public List<string> Tail(int count)
    {
        if (!File.Exists(_path)) return [];
        string[] lines = File.ReadAllLines(_path);
        return [.. lines[Math.Max(0, lines.Length - count)..]];
    }

    /// <summary>
    /// 给**后台页面**用的审计尾巴：把 JSONL 行解析成对象。
    /// </summary>
    /// <remarks>
    /// <b>为什么不能直接把原始行发出去</b>：`admin-game.html` 的审计表读的是 `e.ts` / `e.action` /
    /// `e.actor` / `e.detail` 四个字段，而落盘的行用的是 `at` / `action` / `userId` / `detail`
    /// —— 字段名对不上，表格会整片空白（既不报错、也没有"暂无记录"的兜底，因为数组是非空的）。
    /// 这里做一次**映射**，把落盘口径与界面口径隔开：**磁盘上怎么写是工程决定，界面上怎么读是契约**。
    /// <para>
    /// 坏行（进程被杀时写了一半）直接跳过 —— 审计是事后追溯用的，一个半行不该让整页打不开。
    /// </para>
    /// </remarks>
    public List<object> TailForUi(int count)
    {
        List<object> rows = [];
        foreach (string line in Tail(count))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(line);
                JsonElement root = doc.RootElement;
                long ts = root.TryGetProperty("at", out JsonElement at) && at.TryGetInt64(out long v) ? v : 0L;
                string action = root.TryGetProperty("action", out JsonElement a) ? a.GetString() ?? "" : "";
                string actor = root.TryGetProperty("userId", out JsonElement u) ? u.GetString() ?? "" : "";
                object? detail = root.TryGetProperty("detail", out JsonElement d) && d.ValueKind != JsonValueKind.Null
                    ? JsonSerializer.Deserialize<object>(d.GetRawText())
                    : null;
                rows.Add(new { ts, action, actor, detail });
            }
            catch (JsonException)
            {
                // 半行 / 坏行：跳过。见上面的说明。
            }
        }
        return rows;
    }
}
