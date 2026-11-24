namespace WaningOath.Server;

/// <summary>
/// 一次 HTTP 交换的最小载体：**把路由层从 `HttpListener` 解耦**。
/// </summary>
/// <remarks>
/// <b>为什么不再用 `HttpListener`</b>：它在 Windows 上依赖 HTTP.sys 的 URL 保留。
/// 非管理员会话里连 `http://127.0.0.1:8720/` 都会在 `Start()` 抛“句柄无效”，
/// 而这不该成为“游戏能不能启动”的单点 —— 玩家不会为了打游戏去跑 `netsh http add urlacl`。
/// <para>
/// 现在由 <c>Program.cs</c> 用 <see cref="System.Net.Sockets.TcpListener"/> 解析 HTTP/1.1
/// 的最小集（请求行 + 头 + Content-Length 体），把结果装进这个对象交给 <c>GameApi</c>。
/// `GameApi` 只读 <see cref="Method"/> / <see cref="Path"/> / <see cref="Header"/> /
/// <see cref="Body"/> / <see cref="IsLocal"/>，再用 <see cref="Respond"/> 写回。
/// </para>
/// <para>
/// 不追求完整 HTTP：没有 keep-alive、没有 chunked、没有 100-continue。
/// 服务端只服务本机的网站转发与验收脚本，这三样都不需要。
/// </para>
/// </remarks>
public sealed class HttpExchange
{
    public required string Method { get; init; }
    public required string Path { get; init; }
    public required IReadOnlyDictionary<string, string> Headers { get; init; }
    public required byte[] Body { get; init; }

    /// <summary>请求是否来自本机回环 —— `dev/unlock-all` 的唯一门槛。</summary>
    public required bool IsLocal { get; init; }

    public int StatusCode { get; private set; } = 200;
    public byte[] ResponseBody { get; private set; } = [];
    public string ContentType { get; private set; } = "application/json; charset=utf-8";

    /// <summary>取请求头（大小写不敏感）；没有返回 <c>null</c>。</summary>
    public string? Header(string name)
        => Headers.TryGetValue(name, out string? value) ? value : null;

    /// <summary>由路由层写入响应；之后由传输层统一发出去。</summary>
    public void Respond(int status, byte[] body, string contentType = "application/json; charset=utf-8")
    {
        StatusCode = status;
        ResponseBody = body;
        ContentType = contentType;
    }
}
