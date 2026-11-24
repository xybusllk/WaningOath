using Godot;

namespace WaningOath;

/// <summary>
/// 场景图（剧情 / 战斗的背景美术）。
/// </summary>
/// <remarks>
/// <b>这是一条新管线</b>：在此之前客户端**只有立绘一套加载路径**，任何背景都进不来
/// （`美术策划案/02 §7` 的现状审计把它列为"纯绿地"）。素材目前是 0 张 ——
/// 所以这里先建**通路**，作者一出图就能直接进游戏，不必再等一轮程序改动。
/// <para>
/// <b>六条要求（`美术策划案/02 §7` 的 R1~R6）逐条对应</b>：
/// <list type="number">
///   <item><b>R1</b> 沿用 <see cref="Image.LoadFromFile"/> 直读字节 —— 与 <see cref="Portraits"/> 同一模式。
///         理由：`res://` 图片要先被编辑器导入，而本机跑编辑器/无头都会挂住 ⇒ 导入这一步在流水线里做不了。</item>
///   <item><b>R2</b> 导出发布时把 <c>Assets/Backgrounds/*.png</c> 加进导出过滤器（`06 §8.2` 已为立绘记过同一条）。</item>
///   <item><b>R3</b> 读不到就**软着陆**：整层不加背景，其余界面照常（不抛异常、不占位）。</item>
///   <item><b>R4</b> 启动自报里给出**加载张数 / 缺失张数**（照抄立绘那一行的形状）。</item>
///   <item><b>R5</b> `tools/assets-check.ps1` 覆盖本目录（文件名打错会**静默丢图**，必须机器验）。</item>
///   <item><b>R6</b> 目录 = <c>WaningOath/Assets/Backgrounds/</c>，与 <c>Assets/Portraits/</c> 并列。</item>
/// </list>
/// </para>
/// <para>
/// ⚠️ **命名一旦被代码引用就冻结**：改名 = 静默丢图。所以 <see cref="Requested"/> 是
/// "客户端会去找哪几张"的唯一清单，与 `美术策划案/02 §2.3` 的 slug 表成对维护。
/// </para>
/// </remarks>
public static class Backgrounds
{
    private const string Dir = "res://Assets/Backgrounds/";

    /// <summary>
    /// 客户端会去取的场景图（**这一列就是给美术的需求清单**）。
    /// </summary>
    /// <remarks>
    /// slug 取自 `美术策划案/00 §6.2` 的命名规范 `<场景族>-<场景slug>[-变体]`。
    /// **现在一张都还没有** —— 缺一张只是少一层背景，`missing` 会照实报出来。
    /// </remarks>
    public static readonly string[] Requested =
    [
        "site14-hill-dawn.png",     // 序章 S1：后山 · 晨雾（正文第 9 行）
        "site14-hill-ridge.png",    // 序章 S1 后段：远眺 Site 14 设施群（正文第 61 行）
        "battle-site14-hill.png",   // 序章 S7：路边树丛的战斗背景（正文第 81 行）
        "site14-canteen.png",       // 序章 S8：食堂（正文第 102 行）
    ];

    private static readonly System.Collections.Generic.Dictionary<string, Texture2D?> Cache = new();

    /// <summary>成功加载过的张数（启动自报用）。</summary>
    public static int LoadedCount { get; private set; }

    /// <summary>读不到的张数（启动自报用）。</summary>
    public static int MissingCount { get; private set; }

    /// <summary>取一张场景图；取不到返回 <c>null</c>（调用方负责不加这一层）。</summary>
    public static Texture2D? TryLoad(string fileName)
    {
        if (Cache.TryGetValue(fileName, out Texture2D? cached)) return cached;

        Texture2D? tex = null;
        try
        {
            string path = Dir + fileName;
            if (!Godot.FileAccess.FileExists(path))
            {
                GD.Print($"[场景图] 缺少 {path} —— 这一屏不放背景（不影响其他界面）");
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
            GD.Print($"[场景图] 读取 {fileName} 失败：{e.Message} —— 跳过");
            MissingCount++;
        }

        Cache[fileName] = tex;
        return tex;
    }

    /// <summary>这张场景图在不在包里（大厅那种"有专用图就用、没有就退回"的地方用）。</summary>
    public static bool Exists(string fileName)
        => !string.IsNullOrEmpty(fileName) && Godot.FileAccess.FileExists(Dir + fileName);

    /// <summary>
    /// 按优先顺序取第一张存在的场景图，挂成整屏背景层（大厅这种"正式图还没出"的地方用）。
    /// </summary>
    /// <remarks>
    /// 例：大厅先找 <c>hall-main.png</c>（美术侧以后的专用主视觉），没有就退回
    /// <c>site14-hill-ridge.png</c>（远眺设施群，语义上就是"基地全景"）。
    /// **取不到就什么都不挂**，绝不抛异常（R3）。
    /// </remarks>
    public static void AttachLayerAny(Control host, params string[] fileNames)
    {
        foreach (string name in fileNames)
        {
            if (string.IsNullOrEmpty(name)) continue;
            if (!Godot.FileAccess.FileExists(Dir + name)) continue;
            AttachLayer(host, name);
            return;
        }
    }

    /// <summary>
    /// 一整屏的背景层：**铺满、等比裁切、不参与容器布局**（所以用锚点而不是容器）。
    /// </summary>
    /// <remarks>
    /// 挂在 <paramref name="host"/> 上作为**第一个子节点** ⇒ 永远在内容底下。
    /// 取不到图就什么都不加，宿主照常工作（R3）。
    /// </remarks>
    public static void AttachLayer(Control host, string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return;   // S0 黑屏：明确不配背景，不算"缺图"
        Texture2D? tex = TryLoad(fileName);
        if (tex is null) return;

        // **不要用 `TopLevel`**：它会让这张图逃开父容器的排版，但同时也会被画到
        // **所有兄弟节点之上** —— 症状是"整个界面只剩一张风景"（已实测踩过一次）。
        // 这里只靠锚点铺满，调用方保证 host 不是容器（容器会重新排版它的子节点）。
        TextureRect layer = new()
        {
            Texture = tex,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        layer.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        host.AddChild(layer);
        host.MoveChild(layer, 0);
    }
}
