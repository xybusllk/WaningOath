using System.Net;
using System.Net.Sockets;
using System.Globalization;
using WaningOath.Core;
using System.Text;
using System.Text.Json;
using WaningOath.Core.Battle;
using WaningOath.Core.Data;
using WaningOath.Core.Game;
using WaningOath.Server.Game;

namespace WaningOath.Server;

/// <summary>
/// 游戏服务进程（`WaningOath.Server`）。
/// </summary>
/// <remarks>
/// <b>为什么用 <see cref="TcpListener"/> 而不是 ASP.NET Core / <c>HttpListener</c></b>：
/// `06 §8.4` 记录本机到 `nuget.org` 不通、`.nuget/packages` 为空、只能吃 Godot 自带的包目录。
/// ASP.NET Core 需要额外包；`HttpListener` 虽然也在 BCL 里，但它在 Windows 上依赖 HTTP.sys 的 URL 保留，
/// 非管理员会话连回环地址都会在 `Start()` 抛“句柄无效” —— 那会让“服务端起不来”变成玩家的启动门槛。
/// 因此改为 `TcpListener` + 手写最小 HTTP/1.1 解析（零外部依赖，非管理员可绑回环）。
/// <para>
/// <b>身份来自网关</b>（`06 §2.0`）：客户端只认网站地址，网站把 `/api/game/*` 原样转发过来，
/// 并在转发时注入 <c>X-WO-User</c> / <c>X-WO-Role</c>。账号密码与会话是 Node 的职责，
/// .NET 侧**不重复实现一套登录**。
/// </para>
/// <para>
/// ⚠️ 因此 <c>X-WO-User</c> 是一个**必须被保护的信任边界**：
/// 直接暴露 .NET 端口 = 任何人伪造一个头就能读到别人的存档。
/// 所以默认只绑 `127.0.0.1`，并且可用 `--gateway-secret` 要求网关带一个共享密钥。
/// </para>
/// </remarks>
internal static class Program
{
    private const string Build = "M4 · 存档闭环（/api/game/* · 服务端权威结算）";

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        string host = Arg(args, "--host", "127.0.0.1") ?? "127.0.0.1";
        int port = int.TryParse(Arg(args, "--port", "8720"), out int p) ? p : 8720;
        string? dataRoot = Arg(args, "--data", null);
        string? gatewaySecret = Arg(args, "--gateway-secret", null);
        bool selftest = args.Contains("--selftest", StringComparer.Ordinal);
        bool once = args.Contains("--once", StringComparer.Ordinal);

        Console.WriteLine("未尽之誓 · Waning Oath — 游戏服务 (WaningOath.Server)");
        Console.WriteLine($"  构建阶段 : {Build}");

        GameData data;
        try
        {
            data = dataRoot is null ? GameData.Load() : GameData.Load(dataRoot);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"**数据加载失败**：{ex.GetType().Name}: {ex.Message}");
            return 2;
        }

        Console.WriteLine($"  Core     : {typeof(Rng).Assembly.GetName().Name} {typeof(Rng).Assembly.GetName().Version}");
        Console.WriteLine($"  data/    : {data.Describe()}");
        if (data.Diagnostics.Count > 0)
        {
            Console.WriteLine($"  ⚠ 数据告警 {data.Diagnostics.Count} 条：");
            foreach (string d in data.Diagnostics) Console.WriteLine("      ! " + d);
        }

        ValidationReport report = DataValidator.Validate(data);
        Console.WriteLine($"  数据校验 : {report.ExecutedChecks.Count} 条 → 错误 {report.Errors} · 警告 {report.Warnings} · 提示 {report.Infos}");
        if (report.Errors > 0)
        {
            Console.WriteLine("  ⛔ **校验失败** —— 服务仍会启动，但下列数据不可用于结算：");
            foreach (ValidationIssue issue in report.Issues.Where(static i => i.Level == IssueLevel.Error))
                Console.WriteLine("      " + issue);
        }
        else
        {
            Console.WriteLine("  ✅ 校验通过（0 错误）。");
        }

        // `--selftest` 走**独立的临时存档/审计目录**（除非显式给了 `--store` / `--audit`）。
        // 理由：自检会创建玩家、写审计、穿上装备、拒绝若干次请求 —— 全都会落进真实数据区。
        // 本轮开工时 `data/game/audit.jsonl` 里 **72 行全是 `selftest-*`** 的残留，
        // 而审计日志的价值恰恰是"里面每一行都真的发生过"。
        string defaultStore = selftest
            ? Path.Combine(data.Root, "..", "sim-out", "_selftest", "players")
            : Path.Combine(data.Root, "game", "players");
        string defaultAudit = selftest
            ? Path.Combine(data.Root, "..", "sim-out", "_selftest", "audit.jsonl")
            : Path.Combine(data.Root, "game", "audit.jsonl");

        string storeRoot = Arg(args, "--store", defaultStore) ?? defaultStore;
        string auditPath = Arg(args, "--audit", defaultAudit) ?? defaultAudit;

        // ⚠️ **只在"没显式给 `--store`"时才清空临时区**。否则
        // `--selftest --store data/game/players` 会顺着父目录把**真实存档与真实审计一起删掉** ——
        // "清理测试数据"和"清理用户数据"之间只差一个入参，这种地方不该依赖调用者的自觉。
        bool scratchStore = selftest && Arg(args, "--store", null) is null;
        if (scratchStore)
        {
            // 每次自检从干净目录开始，否则上一轮的存档会让"初始等级 1"这类断言偶然失败。
            string scratch = Path.GetDirectoryName(Path.GetFullPath(storeRoot))!;
            if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
        }

        AuditLog audit = new(auditPath);
        PlayerStore store = new(storeRoot, audit);
        GameService game = new(data, store, audit, () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        // **卡池覆盖配置**（`POST /api/game/admin/config` 的落点）：住在运行时数据区，
        // 不改写 `data/tables/gacha.json`（那张表里有作者手写的 `_comment`，回写会把它吃掉）。
        // 与存档同级，所以它跨重启生效；坏文件只警告、不阻断启动。
        string gachaOverride = Path.Combine(Path.GetDirectoryName(store.Root) ?? store.Root, "gacha-override.json");
        if (File.Exists(gachaOverride))
        {
            try
            {
                GachaTable? over = JsonSerializer.Deserialize<GachaTable>(
                    File.ReadAllText(gachaOverride), PlayerSave.Json);
                if (over is not null)
                {
                    GameApi.ApplyGacha(data, over);
                    Console.WriteLine($"  卡池覆盖 : {gachaOverride}（{data.Gacha.Pools.Count} 个池）");
                }
            }
            catch (JsonException ex)
            {
                Console.WriteLine($"  ⚠️ 卡池覆盖文件无法解析，已忽略：{ex.Message}");
            }
        }

        Console.WriteLine($"  存档目录 : {store.Root}（{store.ListPlayers().Count} 名玩家）");
        Console.WriteLine($"  审计日志 : {Path.GetFullPath(auditPath)}");
        Console.WriteLine();

        if (selftest)
        {
            int code = SelfTest.Run(data, game, store, audit);
            return code;
        }

        GameApi api = new(data, game, store, audit, gatewaySecret);

        try
        {
            RunHttpServer(api, host, port, once);
        }
        catch (Exception ex) when (ex is SocketException or FormatException or InvalidOperationException)
        {
            Console.Error.WriteLine($"**无法监听 http://{host}:{port}/**：{ex.Message}");
            Console.Error.WriteLine("提示：换端口用 --port；本机默认只绑 127.0.0.1。");
            return 3;
        }

        return 0;
    }

    private const int MaxHeaderBytes = 64 * 1024;
    private const int MaxBodyBytes = 4 * 1024 * 1024;

    /// <summary>
    /// 最小 HTTP/1.1 服务端：`TcpListener` + 手写请求解析。
    /// </summary>
    /// <remarks>
    /// <b>为什么不用 `HttpListener`</b>：它在 Windows 上依赖 HTTP.sys 的 URL 保留。
    /// 非管理员会话里连 `http://127.0.0.1:8720/` 都会抛“句柄无效”，而玩家不会为了启动游戏
    /// 去执行 `netsh http add urlacl`。`TcpListener` 是纯用户态 socket，非管理员可直接绑回环。
    /// <para>
    /// 只实现本作需要的子集：请求行 + 头 + `Content-Length` 体，**并支持 `Transfer-Encoding: chunked`**
    /// （网站转发用 `req.pipe(upstream)`，不写 Content-Length，Node 会按 chunked 发）。
    /// 响应一律 `Connection: close`；**不实现** keep-alive / 100-continue。
    /// </para>
    /// </remarks>
    private static void RunHttpServer(GameApi api, string host, int port, bool once)
    {
        IPAddress bind = string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            ? IPAddress.Loopback
            : IPAddress.Parse(host);

        TcpListener listener = new(bind, port);
        listener.Start();

        Console.WriteLine($"✅ 监听中：http://{host}:{port}/（TcpListener · 不依赖 HTTP.sys）");
        Console.WriteLine("   GET  /api/game/health · /config · /stages · /state");
        Console.WriteLine("   POST /api/game/growth · /growth/breakthrough · /growth/skill · /growth/weapon");
        Console.WriteLine("   POST /api/game/save · /gacha · /stage/clear · /battle/verify");
        Console.WriteLine("   GET  /api/game/admin/players · /admin/audit");
        Console.WriteLine("   身份来自网关头 X-WO-User / X-WO-Role（见 Program.cs 的信任边界说明）。");
        Console.WriteLine();

        if (once)
        {
            listener.Stop();
            return;
        }

        CancellationTokenSource cts = new();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
            listener.Stop();
        };

        while (!cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = listener.AcceptTcpClient();
            }
            catch (SocketException) when (cts.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (InvalidOperationException)
            {
                break;
            }

            // 单线程处理：本作是家庭局域网、单机自用，并发不是瓶颈，
            // 而"每个存档只被一个线程碰"消掉了一整类竞态。
            try
            {
                HandleClient(api, client);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[http] {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                client.Dispose();
            }
        }

        try { listener.Stop(); } catch (ObjectDisposedException) { /* Ctrl+C 已经停过 */ }
        Console.WriteLine("已停止。");
    }

    private sealed record RawRequest(
        string Method, string Path, Dictionary<string, string> Headers, byte[] Body);

    private static void HandleClient(GameApi api, TcpClient client)
    {
        client.ReceiveTimeout = 15000;
        client.SendTimeout = 15000;
        using NetworkStream stream = client.GetStream();

        RawRequest? req = ReadRequest(stream);
        if (req is null)
        {
            byte[] bad = JsonSerializer.SerializeToUtf8Bytes(new { ok = false, error = "无法解析 HTTP 请求。" });
            WriteResponse(stream, 400, bad);
            return;
        }

        bool isLocal = client.Client.RemoteEndPoint is IPEndPoint ep && IPAddress.IsLoopback(ep.Address);
        HttpExchange ex = new()
        {
            Method = req.Method,
            Path = req.Path,
            Headers = req.Headers,
            Body = req.Body,
            IsLocal = isLocal,
        };

        try
        {
            api.Handle(ex);
        }
        catch (Exception e)
        {
            ex.Respond(500, JsonSerializer.SerializeToUtf8Bytes(new
            {
                ok = false,
                error = $"{e.GetType().Name}: {e.Message}",
            }));
        }

        WriteResponse(stream, ex);
    }

    private static RawRequest? ReadRequest(NetworkStream stream)
    {
        MemoryStream header = new();
        byte[] buffer = new byte[4096];
        int headerEnd = -1;
        while (headerEnd < 0)
        {
            int n = stream.Read(buffer, 0, buffer.Length);
            if (n <= 0) return null;
            header.Write(buffer, 0, n);
            if (header.Length > MaxHeaderBytes) return null;
            headerEnd = FindHeaderEnd(header.GetBuffer(), (int)header.Length);
        }

        byte[] raw = header.ToArray();
        string head = Encoding.ASCII.GetString(raw, 0, headerEnd);
        string[] lines = head.Split("\r\n", StringSplitOptions.None);
        if (lines.Length == 0) return null;

        string[] first = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (first.Length < 3) return null;

        string method = first[0].ToUpperInvariant();
        string target = first[1];
        int query = target.IndexOf('?');
        string path = query >= 0 ? target[..query] : target;

        Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            int colon = lines[i].IndexOf(':');
            if (colon <= 0) continue;
            string key = lines[i][..colon].Trim();
            string value = lines[i][(colon + 1)..].Trim();
            if (key.Length > 0) headers[key] = value;
        }

        int length = 0;
        if (headers.TryGetValue("Content-Length", out string? declared) && !int.TryParse(declared, out length))
            return null;
        if (length < 0 || length > MaxBodyBytes) return null;

        BodyReader reader = new(stream, raw, headerEnd + 4);
        byte[] body;
        if (headers.TryGetValue("Transfer-Encoding", out string? encoding)
            && encoding.Contains("chunked", StringComparison.OrdinalIgnoreCase))
        {
            byte[]? chunked = ReadChunked(reader);
            if (chunked is null) return null;
            body = chunked;
        }
        else
        {
            body = new byte[length];
            if (!reader.ReadExactly(body, 0, length)) return null;
        }

        return new RawRequest(method, path, headers, body);
    }

    /// <summary>先消费请求头缓冲里剩下的字节，再继续从 socket 读 —— 供 chunked 解码使用。</summary>
    private sealed class BodyReader
    {
        private readonly NetworkStream _stream;
        private readonly byte[] _buffer;
        private int _position;
        private int _length;

        public BodyReader(NetworkStream stream, byte[] initial, int offset)
        {
            _stream = stream;
            _buffer = initial;
            _position = offset;
            _length = initial.Length;
        }

        private int ReadByte()
        {
            if (_position >= _length)
            {
                int n = _stream.Read(_buffer, 0, _buffer.Length);
                if (n <= 0) return -1;
                _position = 0;
                _length = n;
            }
            return _buffer[_position++];
        }

        public bool ReadExactly(byte[] destination, int offset, int count)
        {
            int read = 0;
            while (read < count)
            {
                if (_position >= _length)
                {
                    int n = _stream.Read(_buffer, 0, _buffer.Length);
                    if (n <= 0) return false;
                    _position = 0;
                    _length = n;
                }

                int take = Math.Min(count - read, _length - _position);
                Array.Copy(_buffer, _position, destination, offset + read, take);
                _position += take;
                read += take;
            }
            return true;
        }

        public string? ReadLine()
        {
            List<byte> bytes = [];
            while (true)
            {
                int b = ReadByte();
                if (b < 0) return null;
                if (b == '\n')
                {
                    if (bytes.Count > 0 && bytes[^1] == '\r') bytes.RemoveAt(bytes.Count - 1);
                    return Encoding.ASCII.GetString([.. bytes]);
                }
                bytes.Add((byte)b);
                if (bytes.Count > 8192) return null;
            }
        }
    }

    /// <summary>
    /// `Transfer-Encoding: chunked` 解码（RFC 9112 §7.1）。
    /// </summary>
    /// <remarks>
    /// 网站转发是 `req.pipe(upstream)`，Node 不写 Content-Length ⇒ 默认就是 chunked。
    /// 第一版最小 HTTP 只认 Content-Length，于是**所有经网站转发的 POST 体都是空的**（400「请求体为空」）。
    /// 支持 chunked 之后，直连（Content-Length）与网站（chunked）两条路都通。
    /// </remarks>
    private static byte[]? ReadChunked(BodyReader reader)
    {
        using MemoryStream body = new();
        while (true)
        {
            string? line = reader.ReadLine();
            if (line is null) return null;

            int semicolon = line.IndexOf(';');
            string sizeText = (semicolon >= 0 ? line[..semicolon] : line).Trim();
            if (!int.TryParse(sizeText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int size) || size < 0)
                return null;

            if (size == 0)
            {
                // 0-chunk 之后是可选的 trailer，以空行结束。
                while (true)
                {
                    string? trailer = reader.ReadLine();
                    if (trailer is null) return null;
                    if (trailer.Length == 0) break;
                }
                break;
            }

            if (body.Length + size > MaxBodyBytes) return null;
            byte[] chunk = new byte[size];
            if (!reader.ReadExactly(chunk, 0, size)) return null;
            body.Write(chunk, 0, size);

            string? terminator = reader.ReadLine();
            if (terminator is null || terminator.Length != 0) return null;
        }
        return body.ToArray();
    }

    private static int FindHeaderEnd(byte[] buffer, int length)
    {
        for (int i = 3; i < length; i++)
        {
            if (buffer[i - 3] == '\r' && buffer[i - 2] == '\n'
                && buffer[i - 1] == '\r' && buffer[i] == '\n')
            {
                return i - 3;
            }
        }
        return -1;
    }

    private static void WriteResponse(NetworkStream stream, HttpExchange ex)
        => WriteResponse(stream, ex.StatusCode, ex.ResponseBody, ex.ContentType);

    private static void WriteResponse(NetworkStream stream, int status, byte[] body,
        string contentType = "application/json; charset=utf-8")
    {
        string reason = status switch
        {
            200 => "OK",
            400 => "Bad Request",
            401 => "Unauthorized",
            403 => "Forbidden",
            404 => "Not Found",
            409 => "Conflict",
            413 => "Payload Too Large",
            500 => "Internal Server Error",
            _ => "OK",
        };

        string head = $"HTTP/1.1 {status} {reason}\r\n"
                    + $"Content-Type: {contentType}\r\n"
                    + $"Content-Length: {body.Length}\r\n"
                    + "Connection: close\r\n\r\n";
        byte[] headBytes = Encoding.ASCII.GetBytes(head);
        stream.Write(headBytes, 0, headBytes.Length);
        if (body.Length > 0) stream.Write(body, 0, body.Length);
        stream.Flush();
    }

    private static string? Arg(string[] args, string name, string? fallback)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (!string.Equals(args[i], name, StringComparison.Ordinal)) continue;
            if (i + 1 < args.Length && !args[i + 1].StartsWith('-')) return args[i + 1];
            return fallback;
        }
        return fallback;
    }
}
