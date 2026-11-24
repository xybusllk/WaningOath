using Godot;

namespace WaningOath;

/// <summary>
/// 立绘（`04 §3.2`）：**开局选性别，只替换人称代词与立绘**。
/// </summary>
/// <remarks>
/// <b>为什么走 <see cref="Image.LoadFromFile"/> 而不是 <c>GD.Load&lt;Texture2D&gt;</c></b>：
/// <c>res://</c> 里的图片要先被**编辑器导入**（生成 <c>.import</c> 旁挂文件）才能当资源加载，
/// 而本机跑 Godot 编辑器/`--headless` 都会挂住（见 `06 §8.2` 的 M5 行）⇒ 导入这一步**没法在流水线里做**。
/// <see cref="Image.LoadFromFile"/> 直接读字节，绕开导入系统 —— 代价是导出发布时要显式把
/// <c>Assets/Portraits/*.jpg</c> 加进导出过滤器（记在 `06 §8.2`）。
/// <para>
/// <b>失败必须软着陆</b>：读不到就返回 <c>null</c>，界面少一张立绘而不是崩掉。
/// 立绘是**表现层**，不能成为"客户端能不能打开"的单点。
/// </para>
/// </remarks>
public static class Portraits
{
    /// <summary>资源目录（`WaningOath/Assets/Portraits/` 下的 ASCII 文件名）。</summary>
    private const string Dir = "res://Assets/Portraits/";

    private static readonly System.Collections.Generic.Dictionary<string, Texture2D?> Cache = new();

    /// <summary>成功加载过的张数（启动自报用）。</summary>
    public static int LoadedCount { get; private set; }

    /// <summary>读不到的张数（启动自报用）。</summary>
    public static int MissingCount { get; private set; }

    /// <summary>主角「守约人」按性别取原皮立绘（`04 §3.2`）。</summary>
    public static string ProtagonistFile(string? gender)
        => string.Equals(gender, "female", System.StringComparison.OrdinalIgnoreCase)
            ? "shouyueren-f-base.png"
            : "shouyueren-m-base.png";

    /// <summary>一套衣装（皮肤）。</summary>
    /// <remarks>
    /// <b>数据来源</b>：`角色立绘/` 里已有的图。作者给的 8 张图正好分成"原皮 + 若干衣装"，
    /// 所以这里不发明任何名字 —— 文件名就是衣装名。
    /// <para>
    /// ⚠️ **衣装的生效规则不是这里决定的**：`04 §3.2` 的口径是"主线剧情不生效，
    /// 部分活动剧情会指定特定衣装（且免费试用），因此手动设置的也不生效"。
    /// 也就是说本表只负责"有哪些可选"，**"当前该显示哪一张"由剧情决定**；
    /// 现在没有任何活动剧情 ⇒ 手选的那一张在正式流程里不会被采用。
    /// </para>
    /// </remarks>
    public sealed record SkinDef(string Id, string Name, string File);

    /// <summary>某角色可选的衣装（含原皮）。没落盘立绘的角色返回空表。</summary>
    public static System.Collections.Generic.IReadOnlyList<SkinDef> SkinsFor(string characterId, string? gender)
    {
        bool female = string.Equals(gender, "female", System.StringComparison.OrdinalIgnoreCase);

        if (string.Equals(characterId, "char-shouyueren", System.StringComparison.Ordinal))
        {
            System.Collections.Generic.List<SkinDef> list =
            [
                new("base", "原皮", female ? "shouyueren-f-base.png" : "shouyueren-m-base.png"),
                new("persia", "波斯神域", female ? "shouyueren-f-persia.png" : "shouyueren-m-persia.png"),
            ];
            if (female)
            {
                list.Add(new("breeze", "晴日微风", "shouyueren-f-breeze.png"));
                list.Add(new("eyecalm", "风眼半日闲", "shouyueren-f-eyecalm.png"));
            }
            return list;
        }

        if (string.Equals(characterId, "char-qianyu-xingyao", System.StringComparison.Ordinal))
        {
            return
            [
                new SkinDef("base", "原皮", "xingyao-base.png"),
                new SkinDef("summer", "冰镇的夏日梦", "xingyao-summer.png"),
            ];
        }

        // 以下角色的立绘已接入；没有正式图的先用 Assets/Portraits 里的占位图。
        return characterId switch
        {
            "char-alice" => [new SkinDef("base", "原皮", "alice-base.png")],
            "char-xingyao-darknight" => [new SkinDef("base", "原皮", "darknight-base.png")],
            "char-belrand" => [new SkinDef("base", "原皮（占位）", "belrand-base.png")],
            "char-valeria" => [new SkinDef("base", "原皮（占位）", "valeria-base.png")],
            "char-vera" => [new SkinDef("base", "原皮（占位）", "vera-base.png")],
            "char-karni" => [new SkinDef("base", "原皮（占位）", "karni-base.png")],
            "char-rolanda" => [new SkinDef("base", "原皮（占位）", "rolanda-base.png")],
            "char-xingyao-star-oath" => [new SkinDef("base", "原皮", "star-oath-base.png")],
            "char-qianyu-jingxing" => [new SkinDef("base", "原皮", "jingxing-base.png")],
            "char-hoshimi-aino" => [new SkinDef("base", "原皮", "aino-base.png")],
            "char-lin-jingyuan" => [new SkinDef("base", "原皮（占位）", "lin-base.png")],
            _ => [],
        };
    }

    /// <summary>
    /// 序章专用：至暗长夜的**剪影**（`美术策划案/05 §6` / `100` 丁8）。
    /// </summary>
    /// <remarks>
    /// 她序章显示为 `？？？`，所以**不能挂正式立绘**（会剧透血瞳与配色）。
    /// 这张是从正式立绘后处理出来的纯黑剪影，与正式图同尺寸、透明底。
    /// </remarks>
    public const string DarknightSilhouette = "darknight-silhouette.png";

    /// <summary>按衣装 id 取立绘文件；找不到就退回原皮，再找不到返回 <c>null</c>。</summary>
    public static string? FileForSkin(string characterId, string? gender, string? skinId)
    {
        System.Collections.Generic.IReadOnlyList<SkinDef> skins = SkinsFor(characterId, gender);
        if (skins.Count == 0) return null;

        foreach (SkinDef s in skins)
        {
            if (string.Equals(s.Id, skinId, System.StringComparison.Ordinal)) return s.File;
        }
        return skins[0].File;
    }

    /// <summary>取一张立绘；取不到返回 <c>null</c>（调用方负责不加节点）。</summary>
    public static Texture2D? TryLoad(string fileName)
    {
        if (Cache.TryGetValue(fileName, out Texture2D? cached)) return cached;

        Texture2D? tex = null;
        try
        {
            string path = Dir + fileName;
            if (!Godot.FileAccess.FileExists(path))
            {
                GD.Print($"[立绘] 缺少 {path} —— 跳过（不影响其他界面）");
                MissingCount++;
            }
            else
            {
                Image? img = Image.LoadFromFile(path);
                if (img is not null)
                {
                    tex = ImageTexture.CreateFromImage(img);
                    LoadedCount++;
                }
                else
                {
                    MissingCount++;
                }
            }
        }
        catch (System.Exception e)
        {
            // 注意：`GD.PushWarning` 在部分宿主里会把消息升级成错误；立绘缺失不算错误。
            GD.Print($"[立绘] 读取 {fileName} 失败：{e.Message} —— 跳过");
            MissingCount++;
        }

        Cache[fileName] = tex;
        return tex;
    }

    /// <summary>一张定好尺寸的立绘控件；取不到图时返回 <c>null</c>。</summary>
    public static TextureRect? Tile(string fileName, float width, float height)
    {
        Texture2D? tex = TryLoad(fileName);
        if (tex is null) return null;

        return new TextureRect
        {
            Texture = tex,
            CustomMinimumSize = new Vector2(width, height),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
            TooltipText = fileName,
        };
    }
}
