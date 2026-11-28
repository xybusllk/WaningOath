using Godot;

namespace WaningOath;

/// <summary>
/// 异常体（敌人）立绘：战斗卡牌右侧那张竖图，以及图鉴里的大图。
/// </summary>
/// <remarks>
/// <b>命名规则是约定死的</b>：数据里的 <c>data/enemies/&lt;id&gt;.json</c> 叫什么，
/// 图就叫 <c>Assets/Enemies/&lt;id&gt;-base.png</c>（`美术策划案/15` 的 ASCII 名一栏）。
/// 例：<c>anom-ccrf-017</c> → <c>anom-ccrf-017-base.png</c>；
/// 第六十一具的 id 是 <c>anom-ccrf-502-61</c>（**不是** <c>-061</c>）→ <c>anom-ccrf-502-61-base.png</c>。
/// 图鉴图标另走 <c>-icon</c> 后缀，尺寸 1:1。
/// <para>
/// 与 <see cref="Portraits"/> 同一套做法：<see cref="Image.LoadFromFile"/> 直读字节
/// （绕开 `res://` 导入），**取不到就返回 null**，卡牌少一张图而不是崩掉。
/// </para>
/// <para>
/// 素材由美术侧交付到 <c>异常体立绘/</c>，再由开发侧按 ASCII 名放进来 ——
/// 缺图时战斗卡牌只有文字，流程完全不受影响。
/// </para>
/// </remarks>
public static class EnemyArt
{
    private const string Dir = "res://Assets/Enemies/";

    private static readonly System.Collections.Generic.Dictionary<string, Texture2D?> Cache = new();

    /// <summary>成功加载过的张数（启动自报用）。</summary>
    public static int LoadedCount { get; private set; }

    /// <summary>读不到的张数（启动自报用）。</summary>
    public static int MissingCount { get; private set; }

    /// <summary>由数据 id 推出卡牌图文件名。</summary>
    public static string FileFor(string defId) => defId + "-base.png";

    /// <summary>
    /// 候选文件名：高难档（`anom-ccrf-411-highboss`）画的还是同一只异常，
    /// 所以没有专属图时**退回基础档**，不必为同一只怪再出一张。
    /// </summary>
    private static System.Collections.Generic.IEnumerable<string> Candidates(string defId)
    {
        yield return defId + "-base.png";

        // 关卡变体画的还是同一只怪：找不到自己的图就退回基础档，
        // 这样「库存个体」「高难档」不会各占一张美术资源。
        string[] variantSuffixes = ["-highboss", "-c1", "-c2", "-c3"];
        foreach (string suffix in variantSuffixes)
        {
            if (!defId.EndsWith(suffix, System.StringComparison.Ordinal)) continue;
            yield return defId[..^suffix.Length] + "-base.png";
        }
    }

    /// <summary>取一张异常体立绘；取不到返回 <c>null</c>。</summary>
    public static Texture2D? TryLoad(string defId)
    {
        if (string.IsNullOrEmpty(defId)) return null;
        if (Cache.TryGetValue(defId, out Texture2D? cached)) return cached;

        Texture2D? tex = null;
        foreach (string fileName in Candidates(defId))
        {
            tex = TextureLoader.FromRes(Dir + fileName);
            if (tex is not null) break;
        }

        if (tex is not null) LoadedCount++;
        else MissingCount++;      // 缺图是常态（多数异常还没出图），不刷屏

        Cache[defId] = tex;
        return tex;
    }


    /// <summary>定好尺寸的立绘控件；没有图就返回 <c>null</c>（调用方不加这一列）。</summary>
    public static TextureRect? Tile(string defId, float width, float height)
    {
        Texture2D? tex = TryLoad(defId);
        if (tex is null) return null;

        return new TextureRect
        {
            Texture = tex,
            CustomMinimumSize = new Vector2(width, height),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            TooltipText = FileFor(defId),
        };
    }
}
