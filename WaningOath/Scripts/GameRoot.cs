using Godot;
using WaningOath.Core;
using WaningOath.Core.Battle;
using WaningOath.Core.Data;
using WaningOath.Core.Game;
using WaningOath.Core.Progression;

namespace WaningOath;

/// <summary>
/// M5 客户端根节点：**整套界面在代码里搭出来**。
/// </summary>
/// <remarks>
/// <b>为什么不用 `.tscn` 拼界面</b>：本作的布局纪律（`06 §5.3`）是"**禁止绝对像素定位** ·
/// 卡牌宽度自适应 · 文本框 `autowrap`"，而这些恰好是容器树天然保证、手摆坐标天然违反的东西。
/// 把布局写在代码里，`1440×1080`（4:3）与 `1920×1080`（16:9）就是同一棵容器树在两个宽度下的两次求解，
/// 而不是两份需要同步维护的场景文件。
/// <para>
/// <b>分辨率</b>（`06 §5`）：`project.godot` 里定的是基准 `1920×1080` + `stretch/mode = canvas_items`
/// + `stretch/aspect = expand`（**不是 `keep`** —— 那会加黑边，而作者的习惯是 4:3 拉伸）。
/// `expand` 会按窗口比例放宽画布：16:9 时横向多出的空间被容器的 `ExpandFill` 吃掉，
/// 4:3 时纵向空间更富余、横向更紧 —— 所以**横向最紧的那一档才是约束**：
/// 4 张角色卡在 1440 宽下必须放得下（每张最小 220 + 间距 ≈ 960 < 1440）✓。
/// </para>
/// </remarks>
public partial class GameRoot : Control
{
    /// <summary>`06 §5.1` 要求下拉里至少能选到这些分辨率（两个一等公民 + 常见档位）。</summary>
    private static readonly (string Label, int W, int H)[] Resolutions =
    [
        ("1920×1080（16:9 · 默认）", 1920, 1080),
        ("1440×1080（4:3 拉伸）", 1440, 1080),
        ("1600×900", 1600, 900),
        ("1280×720", 1280, 720),
        ("2560×1440", 2560, 1440),
    ];

    private GameData _data = null!;
    private ApiClient _api = null!;
    private Control _screenHost = null!;
    private Label _statusBar = null!;

    /// <summary>全屏背景层：在渐变之上、内容之下。切屏时由 <see cref="SetNextBackground"/> 换图。</summary>
    private Control _bgLayer = null!;

    /// <summary>下一次 <see cref="Swap"/> 要铺的背景（空 = 背景层清空，只剩全局底噪）。</summary>
    private string _pendingBackground = "";
    private Font _font = null!;
    private PlayerSave _save = null!;
    private string _toast = "";
    private string _gachaMessage = "";
    private string _characterFilter = "";
    private string _inventoryFilter = "";
    private string _codexTier = "";
    private readonly List<string> _gachaPulls = [];

    public override void _Ready()
    {
        // **验收用**：`WO_UI_SIZE=1440x1080` 把拉伸基准改成给定尺寸再建界面。
        // 这一条是 `--headless` 下唯一能拿到**真实布局数字**的手段（见 `SelfReport` 的说明）。
        ApplyRequestedUiSize();

        _font = LoadCjkFont();
        Theme = BuildTheme(_font);

        // 静态数据：客户端与服务端读同一份 data/（06 §2.0）。找不到就明确报错，不要静默降级。
        try
        {
            _data = GameData.Load();
        }
        catch (Exception ex)
        {
            GD.PushError($"数据加载失败：{ex.Message}");
            BuildFatal(ex.Message);
            return;
        }

        _api = new ApiClient();
        CheckStoryAsset();

        // 登录之前界面上得有个东西可显示，所以先摆一份**空白占位存档**。
        // ⚠️ 它**不是**"离线进度"：没有任何一处会去改它、也没有任何一处会把它存下来。
        // 真正的进度在"开始游戏 → 登录"之后从服务端取回（见 `FinishLoginAsync`）。
        _save = PlayerSave.Create("u_placeholder", "未登录", "char-shouyueren",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        BuildShell();
        ShowTitle();
        SelfReport();
        RunUiSmoke();
    }

    // ── 布局巡检（`WO_UI_SMOKE=1`）──────────────────────────────────────

    /// <summary>
    /// **验收用**：把六个屏幕逐个搭出来，量一遍"有没有横向溢出"。
    /// </summary>
    /// <remarks>
    /// <b>为什么需要它</b>：`06 §5.3` 的布局纪律（无绝对像素定位 · 卡牌自适应 · 文本 autowrap）
    /// 是为了"1920×1080 与 1440×1080 两个比例都铺得满"这条要求服务的，而这条要求
    /// **此前从没被任何东西检查过** —— 只有一句"视觉验收未做"。
    /// <para>
    /// 能自动化的那一半：**控件树的右边界有没有超出视口宽度**。
    /// 不能自动化的是"长得对不对"（间距、对齐、观感）—— 那还得人看。
    /// </para>
    /// <para>
    /// 只在 <c>WO_UI_SMOKE=1</c> 时跑，正常启动完全不走这条路（不污染首屏、不产生额外网络请求）。
    /// </para>
    /// </remarks>
    private void RunUiSmoke()
    {
        if (System.Environment.GetEnvironmentVariable("WO_UI_SMOKE") is null) return;
        _ = UiSmokeAsync();
    }

    /// <summary>
    /// 竖向预算：1080 减去页眉（标题 + 状态栏）与上下边距之后，一屏内容还剩多少可用高度。
    /// </summary>
    /// <remarks>
    /// 1920×1080 与 1440×1080 **高度相同**，所以这一条对两种分辨率是同一个数 ——
    /// 但它是最容易被忽略的那一半（"4:3 拉伸"改的是宽度）。
    /// </remarks>
    private const float VerticalBudget = 940f;

    private async System.Threading.Tasks.Task UiSmokeAsync()
    {
        // 容器是**下一帧**才排完版的：`_Ready` 里量到的尺寸全是 0。等两帧再量。
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        // 巡检用的占位存档只给了主角；这里临时补齐 10 名角色，
        // 才能量到“角色 / 编队”两屏在满编情况下是否溢出。**不落盘、不走服务端。**
        foreach (CharacterDef def in _data.Characters.Values)
        {
            if (_save.Characters.ContainsKey(def.Id)) continue;
            _save.Characters[def.Id] = new PlayerSave.CharacterState
            {
                Level = 1,
                SkillLevels = new Dictionary<string, int>(StringComparer.Ordinal)
                {
                    ["basic"] = 1, ["resonance"] = 1, ["talent"] = 1, ["ultimate"] = 1,
                },
            };
        }
        _save.Team = [.. _data.Characters.Keys.OrderBy(static x => x, StringComparer.Ordinal).Take(4)];

        float vpWidth = GetViewport().GetVisibleRect().Size.X;
        (string Key, string Name, Action Build)[] screens =
        [
            ("title", "标题", ShowTitle),
            ("login", "登录", ShowLogin),
            ("connect-failed", "连不上", ShowConnectFailed),
            ("home", "主界面", ShowHome),
            ("stages", "定期清剿", ShowStages),
            ("sim-challenge", "黑檀演习", ShowSimChallenge),
            ("settings", "设置", ShowSettings),
            ("inventory", "仓库", ShowInventory),
            ("stage-detail", "关卡详情", () => ShowStageDetail("stage-exp-1")),
            ("story-list", "故事", ShowStoryList),
            ("story-s0", "剧情 S0", () => ShowStory(0)),
            ("story-s5", "剧情 S5", () => ShowStory(20)),
            ("story-s7", "剧情 S7", () => ShowStory(IndexOfScene(BattleScene) + 1)),
            ("story-c1", "第一章", () =>
            {
                StoryScript? s1 = StoryScript.Load("chapter-1");
                if (s1 is not null) StartScript(s1);
            }),
            ("story-int", "幕间", () =>
            {
                StoryScript? s2 = StoryScript.Load("interlude-1");
                if (s2 is not null)
                {
                    StartScript(s2);
                    _scriptSection = s2.Sections.Count - 1;   // 直接看最后一幕
                    _scriptBeat = 0;
                    _scriptScene = s2.Sections[_scriptSection].Scene ?? "";
                    ShowScriptBeat();
                }
            }),
            ("characters", "角色", ShowCharacters),
            ("team", "编队", ShowTeam),
            ("character-detail", "角色详情", () => ShowCharacterDetail(FirstOwnedCharacter())),
            ("shop", "商城", ShowShop),
            ("gacha", "权限许可", ShowGacha),
            ("codex", "图鉴", ShowCodex),
            ("battle", "战斗", () =>
            {
                ShowBattle("stage-main-s7", tutorial: true, fromStory: true);
                _battle?.RunToEnd();
                // **序章这一场必须真的打得出结果** —— "序章战斗没做"这条就是靠这一行钉住的：
                // 之前按钮写着"进入战斗"，按下去却只是翻页，`ShowBattle()` 从来没被调用过。
                if (_battle is not null)
                {
                    GD.Print($"[SMOKE] story-battle outcome={_battle.State.Outcome} " +
                             $"rounds={_battle.State.Round} loglines={_battle.State.Log.Count}");
                }
            }),
            ("result", "结算", ShowResult),
            ("final", "序章完成", () => ShowStory(Prologue.Length - 1)),
        ];

        int failed = 0;
        foreach ((string key, string name, Action build) in screens)
        {
            try
            {
                build();
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }
            catch (Exception ex)
            {
                failed++;
                GD.Print($"[自报] 屏幕 {name} 搭建抛异常：{ex.GetType().Name}: {ex.Message}");
                GD.Print($"[SMOKE] screen={key} ok=0 threw=1");
                continue;
            }

            float maxRight = 0f, maxBottom = 0f;
            int count = 0;
            Measure(_screenHost, ref maxRight, ref maxBottom, ref count);

            // 截图（只在 WO_UI_SHOT 指向一个目录、且**不是** --headless 时有效）：
            // 无头驱动根本不渲染，存出来是黑的。这一条是给"改完 UI 自己先看一眼"用的。
            TrySaveShot(key);

            // **竖向是另一回事**：无头下视口会退化成 1920x1920，量出来的 `bottom` 说明不了
            // "在 1080 高的窗口里放不放得下"。所以竖向改成问**内容自己的最小高度**
            // （`GetCombinedMinimumSize`）—— 那个数与视口无关，1080 减掉页眉与边距就是预算。
            float minHeight = _screenHost.GetChildCount() > 0 && _screenHost.GetChild(0) is Control root
                ? root.GetCombinedMinimumSize().Y
                : 0f;

            bool okWidth = maxRight <= vpWidth + 1f;
            bool okHeight = minHeight <= VerticalBudget;
            if (!okWidth || !okHeight) failed++;
            GD.Print($"[自报] 屏幕 {name} 控件 {count} 个 · 最右 {maxRight:0} / 视口宽 {vpWidth:0} · " +
                     $"内容最小高 {minHeight:0} / 预算 {VerticalBudget:0} → " +
                     $"{(okWidth && okHeight ? "OK" : $"**{(okWidth ? "" : "横向溢出")}{(okHeight ? "" : " 竖向超预算")}**")}");
            // **ASCII 行是给 `tools\godot-smoke.ps1` 读的**：PowerShell 5.1 把无 BOM 的 .ps1
            // 按 ANSI 读，脚本里一旦出现中文就会变成乱码（这一坑在本项目里已经踩过三次）。
            // 所以"给人看的中文"和"给机器读的 ASCII"分成两行，谁也别将就谁。
            GD.Print($"[SMOKE] screen={key} count={count} right={maxRight:0} minh={minHeight:0} " +
                     $"width={vpWidth:0} budget={VerticalBudget:0} ok={(okWidth && okHeight ? 1 : 0)}");
        }

        GD.Print($"[自报] 布局巡检 {(failed == 0 ? "通过" : $"**失败 {failed} 项**")}（视口宽 {vpWidth:0}）");
        // 立绘是**按需加载**的（标题页已经不显示立绘了），所以要等所有屏幕都建过一遍再报数，
        // 开机时那一行 `[SMOKE] boot=...` 里的 portraits 必然是 0。
        // 场景图同理：S8 食堂在巡检里没被单独打开，这里主动把 Requested 清单过一遍，
        // 让自报的"4 张都试过"成立（而不是把没访问到的那张算成 missing）。
        foreach (string bg in Backgrounds.Requested) Backgrounds.TryLoad(bg);
        // 异常体立绘同理：只有候门有图，巡检里没打开到那只的话就主动加载一遍。
        foreach (EnemyDef enemyDef in _data.Enemies.Values) EnemyArt.TryLoad(enemyDef.Id);
        GD.Print($"[SMOKE] assets portraits={Portraits.LoadedCount} missing={Portraits.MissingCount} " +
                 $"backgrounds={Backgrounds.LoadedCount}/{Backgrounds.MissingCount} " +
                 $"font={(ReferenceEquals(_font.GetType(), typeof(SystemFont)) ? "system" : "bundled")} " +
                 $"enemies={EnemyArt.LoadedCount}/{EnemyArt.MissingCount} " +
                 $"bgreq={Backgrounds.Requested.Length}");
        GD.Print($"[SMOKE] layout={(failed == 0 ? "ok" : "fail")} failed={failed} screens={screens.Length} width={vpWidth:0}");
        GD.Print("[自报] 巡检结束");
        GetTree().Quit(failed == 0 ? 0 : 1);
    }

    /// <summary>递归量控件树的包围盒。**横向看真实右边界**（视口宽是真的），竖向另算。</summary>
    private static void Measure(Control node, ref float maxRight, ref float maxBottom, ref int count)    {
        count++;
        Rect2 r = node.GetGlobalRect();
        if (r.End.X > maxRight) maxRight = r.End.X;
        if (r.End.Y > maxBottom) maxBottom = r.End.Y;
        foreach (Node child in node.GetChildren())
        {
            if (child is Control c) Measure(c, ref maxRight, ref maxBottom, ref count);
        }
    }

    /// <summary>
    /// 把当前视口存成 PNG（巡检专用，`WO_UI_SHOT=<目录>` 时启用）。
    /// </summary>
    /// <remarks>
    /// **只在带窗口跑的时候有意义**：`--headless` 用 dummy 驱动，根本不渲染，存出来是全黑的。
    /// 目的很窄 —— 改完界面先自己看一眼有没有错位，再决定要不要交给人眼验收。
    /// </remarks>
    private void TrySaveShot(string key)
    {
        string? dir = System.Environment.GetEnvironmentVariable("WO_UI_SHOT");
        if (string.IsNullOrEmpty(dir)) return;

        try
        {
            System.IO.Directory.CreateDirectory(dir);
            Image img = GetViewport().GetTexture().GetImage();
            if (img is null) return;
            img.SavePng(System.IO.Path.Combine(dir, key + ".png"));
        }
        catch (Exception ex)
        {
            GD.Print($"[截图] {key} 失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 启动自报（`06 §8.2` 的 M5/M6/M7 验收用）。
    /// </summary>
    /// <remarks>
    /// <b>为什么客户端要打这几行</b>：本机没有能看画面的手段，而"代码完成、视觉验收未做"
    /// 这句话已经挂了三个里程碑。`godot --headless --quit-after N` 是**能跑通**的
    /// （第 7 轮试出来的），于是"客户端能不能起来 / 资源有没有加载到 / UI 树建没建起来"
    /// 这些**可以自动断言**。剩下真正需要人眼的只有"长得对不对"。
    /// <para>
    /// ⚠️ **headless 下 `--resolution` 是无效的**（dummy display driver：`窗口 0x0`，
    /// 而视口会退化成一个方形的 <c>1920x1920</c>）⇒ 想量两种分辨率下的布局，必须走
    /// <c>WO_UI_SIZE</c> 改 **content scale 基准**（<see cref="ApplyRequestedUiSize"/>），
    /// 那才是 `canvas_items` 拉伸真正读的那个数。
    /// </para>
    /// <para>前缀统一用 <c>[自报]</c>，`tools\godot-smoke.ps1` 按行匹配。</para>
    /// </remarks>
    private void SelfReport()
    {
        Vector2 visible = GetViewport().GetVisibleRect().Size;
        Vector2I window = DisplayServer.WindowGetSize();
        GD.Print($"[自报] 视口 {visible.X:0}x{visible.Y:0} · 窗口 {window.X}x{window.Y} · " +
                 $"拉伸基准 {GetWindow().ContentScaleSize.X}x{GetWindow().ContentScaleSize.Y}" +
                 "（canvas_items · expand）");
        GD.Print($"[自报] 字体 {(ReferenceEquals(_font.GetType(), typeof(SystemFont)) ? "系统字体（回退）" : "随包 NotoSansSC")}");
        GD.Print($"[自报] 立绘已加载 {Portraits.LoadedCount} 张 / 缺 {Portraits.MissingCount} 张");
        GD.Print($"[自报] 静态数据 {_data.Describe()}");
        GD.Print($"[自报] 当前屏幕子节点数 {_screenHost.GetChildCount()}");
        GD.Print($"[自报] 外壳尺寸 {Size.X:0}x{Size.Y:0}");
        GD.Print("[自报] 启动完成");
        // ASCII 摘要行：给 `tools\godot-smoke.ps1` 读（见 `UiSmokeAsync` 里的说明）。
        GD.Print($"[SMOKE] boot=ok font={(ReferenceEquals(_font.GetType(), typeof(SystemFont)) ? "system" : "bundled")} " +
                 $"portraits={Portraits.LoadedCount} missing={Portraits.MissingCount} " +
                 $"screens={_screenHost.GetChildCount()}");    }

    /// <summary>
    /// 把 `WO_UI_SIZE`（形如 `1440x1080`）设成**拉伸基准**。
    /// </summary>
    /// <remarks>
    /// 改的是 <see cref="Window.ContentScaleSize"/>，不是窗口大小 —— 在 headless 里窗口大小是 0，
    /// 而 `canvas_items` 拉伸布局读的正是 content scale 基准。这样得到的 <c>Size</c> 与
    /// 容器分配出来的子控件尺寸，与真机同分辨率下**走的是同一条代码路径**。
    /// 只认环境变量、不认命令行开关，是为了不污染正常启动路径。
    /// </remarks>
    private void ApplyRequestedUiSize()
    {
        string? raw = System.Environment.GetEnvironmentVariable("WO_UI_SIZE");
        if (string.IsNullOrWhiteSpace(raw)) return;

        string[] parts = raw.Split('x', 'X');
        if (parts.Length != 2 || !int.TryParse(parts[0], out int w) || !int.TryParse(parts[1], out int h))
        {
            GD.PushWarning($"[自报] WO_UI_SIZE='{raw}' 不是 WxH，已忽略。");
            return;
        }

        Window win = GetWindow();
        win.ContentScaleSize = new Vector2I(w, h);
        win.ContentScaleMode = Window.ContentScaleModeEnum.CanvasItems;
        win.ContentScaleAspect = Window.ContentScaleAspectEnum.Expand;
        GD.Print($"[自报] 拉伸基准被 WO_UI_SIZE 改为 {w}x{h}");
    }

    // ── 外壳 ─────────────────────────────────────────────────────────

    private void BuildShell()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        // 底噪：先铺一张压暗的实景图，再盖一层渐变。
        // ⚠️ 这里**手搓**而不是调 `Backgrounds.AttachLayer`：那个函数用 `TopLevel` 逃开容器排版，
        // 而 `TopLevel` 会把图画到**所有内容之上**（实测每屏都成了双重曝光）。
        Texture2D? ambient = Backgrounds.TryLoad("site14-hill-ridge.png");
        if (ambient is not null)
        {
            TextureRect photo = new()
            {
                Texture = ambient,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
                MouseFilter = MouseFilterEnum.Ignore,
                Modulate = new Color(1, 1, 1, 0.32f),
            };
            photo.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            AddChild(photo);
        }

        // 渐变：中段略透明，让底图透出来；上下两端压深，保证顶栏与小字看得清。
        Gradient gradient = new()
        {
            Colors =
            [
                new Color(0.043f, 0.063f, 0.110f, 0.92f),
                new Color(0.075f, 0.110f, 0.180f, 0.62f),
                new Color(0.031f, 0.047f, 0.082f, 0.94f),
            ],
            Offsets = [0f, 0.52f, 1f],
        };
        GradientTexture2D background = new()
        {
            Gradient = gradient,
            FillFrom = new Vector2(0, 0),
            FillTo = new Vector2(1, 1),
        };
        TextureRect backdrop = new()
        {
            Texture = background,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        backdrop.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(backdrop);

        // 每个屏幕自己的背景图都挂在这一层：**在渐变之上、内容之下**。
        // ⚠️ 之前是 `Backgrounds.AttachLayer(root, …)` 挂在屏幕里当第一个子节点，
        // 而那个函数为了逃开容器排版用了 `TopLevel` —— 实测 `TopLevel` 会画到兄弟节点**之上**，
        // 于是背景把整个界面盖住了（截图里只剩一张风景，没有任何 UI）。
        _bgLayer = new Control { MouseFilter = MouseFilterEnum.Ignore };
        _bgLayer.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(_bgLayer);

        MarginContainer margin = new();
        margin.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        margin.AddThemeConstantOverride("margin_left", 24);
        margin.AddThemeConstantOverride("margin_right", 24);
        margin.AddThemeConstantOverride("margin_top", 18);
        margin.AddThemeConstantOverride("margin_bottom", 18);
        AddChild(margin);

        VBoxContainer column = new() { SizeFlagsVertical = SizeFlags.ExpandFill };
        column.AddThemeConstantOverride("separation", 12);
        margin.AddChild(column);

        // 顶部：游戏名 + 分辨率提示。**分辨率提示是给"两种分辨率各走一遍"这条验收用的**：
        // 走查时一眼就能看出当前画布到底是多少。
        HBoxContainer header = new();
        column.AddChild(header);

        Label title = new()
        {
            Text = "未尽之誓",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        title.AddThemeFontSizeOverride("font_size", 34);
        header.AddChild(title);

        _statusBar = new Label
        {
            Text = "分辨率 —",
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        header.AddChild(_statusBar);

        // 中部：屏幕栈。**只有一个位置**，四个屏幕轮流占它 —— 避免"四份界面同时在树里"。
        _screenHost = new Control { SizeFlagsVertical = SizeFlags.ExpandFill };
        column.AddChild(_screenHost);

        GetViewport().SizeChanged += UpdateResolutionReadout;
        UpdateResolutionReadout();
    }

    private void UpdateResolutionReadout()
    {
        Vector2 size = GetViewportRect().Size;
        int w = Mathf.RoundToInt(size.X);
        int h = Mathf.RoundToInt(size.Y);
        string ratio = h == 0 ? "—" : ((double)w / h).ToString("0.00");
        string ratioName = Mathf.Abs((float)w / h - 16f / 9f) < 0.01f ? "16:9"
                         : Mathf.Abs((float)w / h - 4f / 3f) < 0.01f ? "4:3" : "其他";
        _statusBar.Text = $"画布 {w}×{h} · 比例 {ratio}（{ratioName}） · 服务器 {_api?.Status ?? "未连接"}";
    }

    private void BuildFatal(string message)
    {
        Label label = new()
        {
            Text = "数据加载失败：\n" + message + "\n\n请确认客户端工程位于仓库内（data/ 与 WaningOath/ 同级）。",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        label.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(label);
    }

    // ── 屏幕栈 ───────────────────────────────────────────────────────

    private void Swap(Control screen)
    {
        ApplyBackground();
        // **背景是"这一屏"的，不是"这一局"的**：用完就清，
        // 否则没申请背景的界面（角色 / 商店 / 设置…）会一直挂着上一屏的图。
        _pendingBackground = "";
        foreach (Node child in _screenHost.GetChildren()) child.QueueFree();
        screen.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _screenHost.AddChild(screen);
    }

    /// <summary>请求"下一个屏幕要用的背景图"（多个候选按顺序取第一张存在的）。</summary>
    private void SetNextBackground(params string[] candidates)
    {
        _pendingBackground = "";
        foreach (string name in candidates)
        {
            if (string.IsNullOrEmpty(name)) continue;
            if (!Backgrounds.Exists(name)) continue;
            _pendingBackground = name;
            return;
        }
    }

    /// <summary>把挂起的背景铺到背景层上；空串就清空。</summary>
    private void ApplyBackground()
    {
        foreach (Node child in _bgLayer.GetChildren())
        {
            _bgLayer.RemoveChild(child);
            child.QueueFree();
        }

        if (string.IsNullOrEmpty(_pendingBackground)) return;

        Texture2D? tex = Backgrounds.TryLoad(_pendingBackground);
        if (tex is null) return;

        TextureRect layer = new()
        {
            Texture = tex,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        layer.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _bgLayer.AddChild(layer);
    }

    /// <summary>编辑器导出的 JSON 与客户端内置正文做一次轻量一致性检查；不一致只告警，不阻断启动。</summary>
    private static void CheckStoryAsset()
    {
        const string path = "res://Assets/Story/prologue.story.json";
        try
        {
            if (!Godot.FileAccess.FileExists(path)) return;
            using System.Text.Json.JsonDocument doc =
                System.Text.Json.JsonDocument.Parse(Godot.FileAccess.GetFileAsString(path));
            if (!doc.RootElement.TryGetProperty("beats", out System.Text.Json.JsonElement beats)
                || beats.ValueKind != System.Text.Json.JsonValueKind.Array) return;
            int count = beats.GetArrayLength();
            GD.Print($"[故事] 编辑器导出 {count} 拍 / 客户端内置 {Prologue.Length} 拍");
            if (count != Prologue.Length)
                GD.PushWarning($"[故事] prologue.story.json 与客户端 Prologue 拍数不一致（{count} vs {Prologue.Length}）；请重新导出或更新客户端。");
        }
        catch (Exception ex)
        {
            GD.PushWarning($"[故事] 读取 prologue.story.json 失败：{ex.Message}");
        }
    }

    // ── 标题页 ───────────────────────────────────────────────────────

    /// <summary>
    /// 标题页。**只有游戏名、标语、一个「开始游戏」**（外加一个不抢戏的分辨率选择）。
    /// </summary>
    /// <remarks>
    /// 之前这一屏是反的：游戏名在左上角、标语占正中，还塞了两张立绘和三个入口按钮。
    /// 现在：**名字最大最正**、标语其次、入口只有一个 —— 登录、主界面、图鉴、养成全在它后面。
    /// </remarks>
    private void ShowTitle()
    {
        VBoxContainer box = new() { Alignment = BoxContainer.AlignmentMode.Center };
        box.AddThemeConstantOverride("separation", 10);

        Label name = new()
        {
            Text = "未尽之誓",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        name.AddThemeFontSizeOverride("font_size", 66);
        name.AddThemeColorOverride("font_color", Color.FromHtml("#f0f5ff"));
        box.AddChild(name);

        ColorRect divider = new()
        {
            Color = Color.FromHtml("#7fb0ff"),
            CustomMinimumSize = new Vector2(240, 2),
            SizeFlagsHorizontal = SizeFlags.ShrinkCenter,
        };
        box.AddChild(divider);

        Label latin = new()
        {
            Text = "W A N I N G   O A T H",
            HorizontalAlignment = HorizontalAlignment.Center,
            Modulate = new Color(1, 1, 1, 0.45f),
        };
        latin.AddThemeFontSizeOverride("font_size", 14);
        box.AddChild(latin);

        box.AddChild(new Control { CustomMinimumSize = new Vector2(0, 18) });

        Label slogan = new()
        {
            Text = "延绵若梦，终至如约",
            HorizontalAlignment = HorizontalAlignment.Center,
            Modulate = new Color(1, 1, 1, 0.82f),
        };
        slogan.AddThemeFontSizeOverride("font_size", 22);
        box.AddChild(slogan);

        box.AddChild(new Control { CustomMinimumSize = new Vector2(0, 34) });

        Button start = new() { Text = "开始游戏", CustomMinimumSize = new Vector2(300, 56) };
        start.AddThemeFontSizeOverride("font_size", 22);
        start.Pressed += () => _ = EnterGameAsync();
        box.AddChild(Wrap(start));

        // 分辨率：两种是一等公民（1920×1080 / 1440×1080），做成一个不起眼的设置行。
        HBoxContainer res = new()
        {
            Alignment = BoxContainer.AlignmentMode.Center,
            Modulate = new Color(1, 1, 1, 0.5f),
        };
        OptionButton picker = new() { CustomMinimumSize = new Vector2(220, 34) };
        foreach ((string label, _, _) in Resolutions) picker.AddItem(label);
        picker.Selected = 0;
        picker.ItemSelected += index =>
        {
            (string label, int w, int h) = Resolutions[(int)index];
            DisplayServer.WindowSetSize(new Vector2I(w, h));
            GD.Print($"分辨率切换 → {label}");
        };
        res.AddChild(picker);
        box.AddChild(res);

        Swap(box);
        StatusLine();
    }

    // ── 开始游戏 → 检查登录 ───────────────────────────────────────────

    /// <summary>
    /// 「开始游戏」之后的全部判定：**连得上吗 → 要登录吗 → 这个号通到哪一步了**。
    /// </summary>
    /// <remarks>
    /// 这一段存在的意义就是把"要开哪个进程、要不要注册、新号该去哪"从玩家脑子里挪走。
    /// 三条出口：连不上（给重试）、要登录（给登录页）、有身份（读存档并按进度分派）。
    /// </remarks>
    /// <remarks>
    /// 这一段存在的意义就是把"要开哪个进程、要不要注册、新号该去哪"从玩家脑子里挪走。
    /// **没有第四条出口** —— 不存在"绕过登录直接开打"这条路（本作没有离线模式）。
    /// </remarks>
    private async Task EnterGameAsync()
    {
        ShowBusy("正在连接服务器…");
        bool reachable = await _api.ProbeAsync();

        if (!reachable)
        {
            ShowConnectFailed();
            return;
        }

        if (_api.NeedsLogin)
        {
            ShowLogin();
            return;
        }

        await FinishLoginAsync();
    }

    /// <summary>
    /// 连不上时的一屏。**没有离线模式** —— 进度全部在服务器上，连不上就没法开始。
    /// </summary>
    /// <remarks>
    /// ⚠️ 这里原来有一个「离线游玩」按钮，已按作者要求删除。理由不是"技术上做不到"，
    /// 而是**"本地也能改进度"与"服务端是唯一权威"互相矛盾**：
    /// 只要允许本地先改、回头再同步，客户端与服务端不一致就从"不该发生"变成"迟早发生"。
    /// 所以现在**每一个动作都是一次服务端往返**，连不上就什么也做不了 —— 这是设计，不是缺陷。
    /// </remarks>
    private void ShowConnectFailed()
    {
        VBoxContainer box = new() { Alignment = BoxContainer.AlignmentMode.Center };
        box.AddThemeConstantOverride("separation", 12);

        Label title = new() { Text = "没有连上服务器", HorizontalAlignment = HorizontalAlignment.Center };
        title.AddThemeFontSizeOverride("font_size", 28);
        box.AddChild(title);

        Label detail = new()
        {
            Text = "进度与结算都保存在服务器上，连不上就无法开始。\n\n" +
                   "游戏服务与网站是两个进程，都要开着：\n" +
                   "    tools\\run-all.cmd     一键开两个\n" +
                   "    tools\\run-client.cmd  再开客户端",
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        box.AddChild(detail);

        Button retry = new() { Text = "重试", CustomMinimumSize = new Vector2(240, 46) };
        retry.Pressed += () => _ = EnterGameAsync();
        box.AddChild(Wrap(retry));

        Button back = new() { Text = "返回标题", CustomMinimumSize = new Vector2(240, 46) };
        back.Pressed += ShowTitle;
        box.AddChild(Wrap(back));

        Swap(box);
        StatusLine();
    }

    /// <summary>登录 / 注册。**新号在这一页顺手把主角性别定了**（它是账号属性，不是标题页属性）。</summary>
    private void ShowLogin()
    {
        VBoxContainer box = new() { Alignment = BoxContainer.AlignmentMode.Center };
        box.AddThemeConstantOverride("separation", 12);

        Label title = new() { Text = "登录", HorizontalAlignment = HorizontalAlignment.Center };
        title.AddThemeFontSizeOverride("font_size", 34);
        box.AddChild(title);

        Label sub = new()
        {
            Text = $"服务器：{_api.BaseUrl}",
            HorizontalAlignment = HorizontalAlignment.Center,
            Modulate = new Color(1, 1, 1, 0.5f),
        };
        box.AddChild(sub);

        LineEdit user = new() { PlaceholderText = "账号", CustomMinimumSize = new Vector2(320, 42) };
        LineEdit pass = new() { PlaceholderText = "密码", Secret = true, CustomMinimumSize = new Vector2(320, 42) };
        LineEdit nick = new() { PlaceholderText = "昵称（注册时用）", CustomMinimumSize = new Vector2(320, 42) };
        box.AddChild(Wrap(user));
        box.AddChild(Wrap(pass));
        box.AddChild(Wrap(nick));

        HBoxContainer gender = new() { Alignment = BoxContainer.AlignmentMode.Center };
        OptionButton genderPicker = new() { CustomMinimumSize = new Vector2(320, 40) };
        genderPicker.AddItem("主角形象：女");
        genderPicker.AddItem("主角形象：男");
        genderPicker.Selected = string.Equals(_save.Profile.ProtagonistGender, "female", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
        genderPicker.ItemSelected += index => _pendingGender = index == 0 ? "female" : "male";
        gender.AddChild(genderPicker);
        box.AddChild(gender);

        Label message = new() { Text = "", HorizontalAlignment = HorizontalAlignment.Center };
        box.AddChild(message);

        Button login = new() { Text = "登录", CustomMinimumSize = new Vector2(240, 46) };
        login.Pressed += () => _ = DoLoginAsync(user.Text, pass.Text, message);
        box.AddChild(Wrap(login));

        Button register = new() { Text = "注册并开始", CustomMinimumSize = new Vector2(240, 46) };
        register.Pressed += () => _ = DoRegisterAsync(user.Text, pass.Text, nick.Text, message);
        box.AddChild(Wrap(register));

        Button back = new() { Text = "返回标题", CustomMinimumSize = new Vector2(240, 40) };
        back.Pressed += ShowTitle;
        box.AddChild(Wrap(back));

        Swap(box);
        StatusLine();
    }

    private async Task DoLoginAsync(string username, string password, Label message)
    {
        message.Text = "登录中…";
        (bool ok, string msg) = await _api.LoginAsync(username, password);
        message.Text = msg;
        if (ok) await FinishLoginAsync();
    }

    private async Task DoRegisterAsync(string username, string password, string nickname, Label message)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            message.Text = "账号和密码都要填。";
            return;
        }
        message.Text = "注册中…";
        (bool ok, string msg) = await _api.RegisterAsync(username, password, nickname);
        message.Text = msg;
        if (ok) await FinishLoginAsync();
    }

    /// <summary>
    /// 有身份之后：**拉服务端存档 → 落定主角档案 → 决定进序章还是进主界面**。
    /// </summary>
    /// <remarks>
    /// "新号自动进序章"这条不是靠"有没有存档"判断的 —— 服务端会给新号建一份默认存档，
    /// 所以真正的判据是**序章这一章的 <c>cleared</c> 标记**。
    /// <para>
    /// ⚠️ **拉不到存档就到此为止**，不做"本地先起一份"的兜底：本作没有离线模式，
    /// 本地起一份等于玩家在一个**不会保存**的进度里玩，比直接说"读不到"糟糕得多。
    /// </para>
    /// </remarks>
    private async Task FinishLoginAsync()
    {
        ShowBusy("正在读取存档…");

        PlayerSave? server = await _api.FetchSaveAsync();
        if (server is null)
        {
            _toast = "读不到存档：" + _api.Status;
            ShowConnectFailed();
            return;
        }
        _save = server;

        // 登录页上选的性别 / 账号昵称要落到服务端 —— 客户端不再自己写存档。
        string wanted = _save.Profile.ProtagonistGender;
        if (!string.IsNullOrEmpty(_pendingGender)) wanted = _pendingGender;
        // ⚠️ 昵称**必须走这条 JSON 路径**，不能靠请求头带：HTTP 头按规范是 latin-1，
        // 中文昵称在转发那一步就会被 Node 直接拒绝（ERR_INVALID_CHAR），
        // 症状是"只有昵称含中文的账号登不进"。
        string? accountName = string.IsNullOrWhiteSpace(_api.Nickname) ? null : _api.Nickname;
        ApiClient.Reply profile = await _api.SetProfileAsync(wanted, accountName);
        if (profile.Ok)
        {
            PlayerSave? again = await _api.FetchSaveAsync();
            if (again is not null) _save = again;
        }

        bool prologueDone = _save.Progress.Stories.TryGetValue("prologue", out PlayerSave.StoryRecord? rec)
                            && rec.Cleared;

        if (!prologueDone) ShowStory(0);
        else ShowHome();
    }

    /// <summary>登录页上选的性别（登录成功后一次性写进服务端）。</summary>
    private string? _pendingGender;

    /// <summary>重新拉一次存档。**每个改存档的动作之后都要调它** —— 客户端不留自己的副本。</summary>
    private async Task ReloadSaveAsync()
    {
        PlayerSave? fresh = await _api.FetchSaveAsync();
        if (fresh is not null) _save = fresh;
    }

    private void ShowBusy(string message)
    {
        VBoxContainer box = new() { Alignment = BoxContainer.AlignmentMode.Center };
        Label label = new() { Text = message, HorizontalAlignment = HorizontalAlignment.Center };
        label.AddThemeFontSizeOverride("font_size", 22);
        box.AddChild(label);
        Swap(box);
    }

    // ── 主界面 ───────────────────────────────────────────────────────

    /// <summary>
    /// <summary>
    /// 游戏主界面（大厅）：**背景图 + 任务条 + 入口卡片 + 右侧主角立绘**。
    /// </summary>
    /// <remarks>
    /// 这是序章结束之后的落点（不是回标题页）。重做时只改了三件与"观感"有关的事：
    /// <list type="number">
    ///   <item><b>给它一张背景图</b> —— 原来是纯渐变，整屏一个色，所以"灰暗单调"。
    ///         现在优先取 `hall-main.png`（美术侧以后的主视觉），没有就退回
    ///         `site14-hill-ridge.png`（远眺设施群，语义上就是"基地全景"）。</item>
    ///   <item><b>底部一条渐变 + 页脚面板**把场景图右下角的水印压掉** —— 货源那张图带水印，
    ///         而大厅不能像剧情屏那样用对话框遮，所以页脚面板横跨整屏、正好盖住那一角。</item>
    ///   <item><b>入口从 3×4 的裸按钮变成卡片</b>，卡片半透明、能透出背景；右侧留一块
    ///         主角立绘的位置，让首页第一眼有人物、不是一屏按钮。</item>
    /// </list>
    /// </remarks>
    private void ShowHome()
    {
        _storyAuto = false;
        _storyAutoVersion++;

        Control root = new();

        // 背景：优先大厅主视觉，没有就退回设施群远景（取不到图就只剩底下的渐变，软着陆）。
        SetNextBackground("hall-main.png", "site14-hill-ridge.png");
        root.AddChild(SideScrim());
        root.AddChild(BottomShade(520f));

        MarginContainer frame = new();
        frame.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        frame.AddThemeConstantOverride("margin_left", 26);
        frame.AddThemeConstantOverride("margin_right", 26);
        frame.AddThemeConstantOverride("margin_top", 22);
        frame.AddThemeConstantOverride("margin_bottom", 22);
        root.AddChild(frame);

        VBoxContainer column = new() { SizeFlagsVertical = SizeFlags.ExpandFill };
        column.AddThemeConstantOverride("separation", 14);
        frame.AddChild(column);

        // ── 顶部：账户 + 资源（一条，不再占两屏） ──
        PanelContainer header = Card();
        HBoxContainer row = new();
        row.AddThemeConstantOverride("separation", 22);

        VBoxContainer who = new() { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddChild(who);

        Label account = new() { Text = DisplayName() };
        account.AddThemeFontSizeOverride("font_size", 26);
        who.AddChild(account);

        Label level = new()
        {
            Text = $"协议等级 {_save.Profile.ProtocolLevel} · 已收录角色 {_save.Characters.Count}",
            Modulate = new Color(1, 1, 1, 0.72f),
        };
        level.AddThemeFontSizeOverride("font_size", 14);
        who.AddChild(level);

        bool prologueDone = _save.Progress.Stories.TryGetValue("prologue", out PlayerSave.StoryRecord? prologue)
                            && prologue.Cleared;
        Label storyProgress = new()
        {
            Text = prologueDone ? "序章 · 已通关（可重看）" : "序章 · 未通关",
            Modulate = new Color(0.72f, 0.85f, 1f, 0.85f),
        };
        storyProgress.AddThemeFontSizeOverride("font_size", 13);
        who.AddChild(storyProgress);

        HBoxContainer wallet = new();
        wallet.AddThemeConstantOverride("separation", 6);
        wallet.AddChild(ResourceCell("运维配额", $"{_save.Stamina.Current}/{BattleConstants.StaminaMax}"));
        wallet.AddChild(ResourceCell("协议原码", _save.Currency.ProtocolCode.ToString("N0")));
        wallet.AddChild(ResourceCell("授信额度", _save.Currency.Credit.ToString("N0")));
        row.AddChild(wallet);

        Pad(header, row);
        column.AddChild(header);

        // ── 中部：左（任务 + 入口）/ 右（主角立绘） ──
        HBoxContainer middle = new() { SizeFlagsVertical = SizeFlags.ExpandFill };
        middle.AddThemeConstantOverride("separation", 18);
        column.AddChild(middle);

        VBoxContainer left = new() { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        left.AddThemeConstantOverride("separation", 12);
        middle.AddChild(left);

        left.AddChild(HomeTasks());

        GridContainer grid = new() { Columns = 3, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        grid.AddThemeConstantOverride("h_separation", 10);
        grid.AddThemeConstantOverride("v_separation", 8);
        grid.AddChild(Entry("定期清剿", "阶跃核心 · 高危样本 · 武器素材 · 经验", ShowStages, true));
        grid.AddChild(Entry("故事", "主线剧情", ShowStoryList, true));
        grid.AddChild(Entry("角色", "查看与养成", ShowCharacters, true));
        grid.AddChild(Entry("编队", "选择上场的 1~4 名角色", ShowTeam, true));
        grid.AddChild(Entry("仓库", "材料 · 道具 · 掉落", ShowInventory, true));
        grid.AddChild(Entry("异常图鉴", "已收录的异常体档案", ShowCodex, true));
        grid.AddChild(Entry("权限许可", "常驻 / 限定 抽卡", ShowGacha, true));
        grid.AddChild(Entry("商城", "回执兑换 · 衣装 · 充值", ShowShop, true));
        grid.AddChild(Entry("黑檀演习", "高难模拟 · 风险与回报", ShowSimChallenge, true));
        grid.AddChild(Entry("每周首领", _save.Progress.BossFreeUsed >= 1 ? "本周已领取 · 周一重置" : "每周一次 · 60 原码",
            () => ShowStageDetail("stage-weekly-boss"), true));
        grid.AddChild(Entry("设置", "分辨率 · 全屏", ShowSettings, true));
        left.AddChild(grid);
        left.AddChild(new Control { SizeFlagsVertical = SizeFlags.ExpandFill });

        // 右侧：主角立绘（顺带把场景图右下角那块压住）
        VBoxContainer heroCol = new()
        {
            CustomMinimumSize = new Vector2(310, 0),
            Alignment = BoxContainer.AlignmentMode.End,
        };
        heroCol.AddThemeConstantOverride("separation", 6);

        TextureRect? hero = Portraits.Tile(
            Portraits.ProtagonistFile(_save.Profile.ProtagonistGender), 300f, 460f);
        if (hero is not null)
        {
            hero.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
            heroCol.AddChild(hero);
        }

        Label heroName = new()
        {
            Text = "守约人",
            HorizontalAlignment = HorizontalAlignment.Center,
            Modulate = new Color(0.78f, 0.88f, 1f, 0.9f),
        };
        heroName.AddThemeFontSizeOverride("font_size", 14);
        heroCol.AddChild(heroName);
        middle.AddChild(heroCol);

        // ── 底部：页脚面板（横跨整屏，盖住水印） ──
        PanelContainer footer = Card();
        HBoxContainer foot = new() { Alignment = BoxContainer.AlignmentMode.Center };
        foot.AddThemeConstantOverride("separation", 14);

        Button reload = new() { Text = "刷新", CustomMinimumSize = new Vector2(140, 42) };
        reload.Pressed += () => _ = ReloadAndShowHomeAsync();
        foot.AddChild(reload);

        Button toTitle = new() { Text = "返回标题", CustomMinimumSize = new Vector2(140, 42) };
        toTitle.Pressed += ShowTitle;
        foot.AddChild(toTitle);

        // Debug 构建才显示：一键解锁全部角色（服务端只接受本机回环请求）。
        if (OS.IsDebugBuild())
        {
            Button unlock = new() { Text = "开发：解锁全角色", CustomMinimumSize = new Vector2(180, 42) };
            unlock.Pressed += () => _ = UnlockAllAsync();
            foot.AddChild(unlock);
        }

        Pad(footer, foot);
        column.AddChild(footer);

        Swap(root);
        StatusLine();
    }

    /// <summary>大厅的任务条：每日委托 / 档案演习 / 周常，三件事挤进一张卡。</summary>
    private Control HomeTasks()
    {
        PanelContainer card = Card();
        VBoxContainer rows = new();
        rows.AddThemeConstantOverride("separation", 8);

        // 每日委托
        HBoxContainer dailyRow = new();
        dailyRow.AddThemeConstantOverride("separation", 12);
        int dailyDone = _save.Progress.DailyDelegateDone;
        bool dailyClaimed = _save.Progress.DailyDelegateClaimed;
        Label dailyLabel = new()
        {
            Text = dailyClaimed
                ? "每日委托 · 今日已领取（04:00 重置）"
                : $"每日委托 · 完成普通关卡 {Math.Min(dailyDone, 3)}/3 → 80 协议原码（04:00 重置）",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        dailyRow.AddChild(dailyLabel);
        Button dailyClaim = new() { Text = dailyClaimed ? "已领取" : "领取", CustomMinimumSize = new Vector2(110, 36) };
        dailyClaim.Disabled = dailyClaimed || dailyDone < 3;
        dailyClaim.Pressed += () => _ = ClaimDailyAsync();
        dailyRow.AddChild(dailyClaim);

        bool freeExpDone = _save.Progress.FreeExpRunDone;
        Button freeExp = new()
        {
            Text = freeExpDone ? "演习已领" : "档案演习",
            CustomMinimumSize = new Vector2(110, 36),
            Disabled = freeExpDone,
            TooltipText = "每日免费经验：" + DescribeReward(_data.Stage("stage-exp-1").Reward.Repeat) + "（不消耗运维配额）",
        };
        freeExp.Pressed += () => _ = ClaimFreeExpAsync();
        dailyRow.AddChild(freeExp);
        rows.AddChild(dailyRow);

        // 周常
        HBoxContainer weeklyRow = new();
        weeklyRow.AddThemeConstantOverride("separation", 12);
        int weeklyDone = _save.Progress.WeeklyTaskDone;
        bool weeklyClaimed = _save.Progress.WeeklyTaskClaimed;
        Label weeklyLabel = new()
        {
            Text = weeklyClaimed
                ? "周常任务 · 本周已领取（周一 04:00 重置）"
                : $"周常任务 · 完成普通关卡 {Math.Min(weeklyDone, 10)}/10 → 160 协议原码（周一 04:00 重置）",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        weeklyRow.AddChild(weeklyLabel);
        Button weeklyClaim = new() { Text = weeklyClaimed ? "已领取" : "领取", CustomMinimumSize = new Vector2(110, 36) };
        weeklyClaim.Disabled = weeklyClaimed || weeklyDone < 10;
        weeklyClaim.Pressed += () => _ = ClaimWeeklyAsync();
        weeklyRow.AddChild(weeklyClaim);
        rows.AddChild(weeklyRow);

        Pad(card, rows);
        return card;
    }

    /// <summary>手动重拉一次存档再回主界面（换设备 / 管理员改过档之后用）。</summary>
    private async Task ReloadAndShowHomeAsync()
    {
        ShowBusy("正在读取存档…");
        await ReloadSaveAsync();
        _toast = "已从服务器重新读取。";
        ShowHome();
    }

    private async Task ClaimDailyAsync()
    {
        ShowBusy("正在领取每日委托…");
        ApiClient.Reply reply = await _api.ClaimDailyAsync();
        await ReloadSaveAsync();
        _toast = reply.Ok ? "每日委托奖励已领取（协议原码 +80）。" : "领取失败：" + reply.Message;
        ShowHome();
    }

    private async Task ClaimFreeExpAsync()
    {
        ShowBusy("正在领取每日档案演习…");
        ApiClient.Reply reply = await _api.ClaimFreeExpAsync();
        await ReloadSaveAsync();
        _toast = reply.Ok ? "每日档案演习已领取。" : "领取失败：" + reply.Message;
        ShowHome();
    }

    private async Task ClaimWeeklyAsync()
    {
        ShowBusy("正在领取周常任务…");
        ApiClient.Reply reply = await _api.ClaimWeeklyAsync();
        await ReloadSaveAsync();
        _toast = reply.Ok ? "周常任务奖励已领取（协议原码 +160）。" : "领取失败：" + reply.Message;
        ShowHome();
    }

    /// <summary>开发/验收：让服务端把 data/characters 里的全部角色写进存档，再刷新主界面。</summary>
    private async Task UnlockAllAsync()
    {
        ShowBusy("正在解锁全部角色…");
        ApiClient.Reply reply = await _api.UnlockAllAsync();
        await ReloadSaveAsync();
        _toast = reply.Ok ? "已解锁全部角色。" : $"解锁失败：{reply.Message}";
        ShowHome();
    }

    private string DisplayName()
    {
        string nick = _save.Profile.PlayerName;
        if (!string.IsNullOrWhiteSpace(nick)) return nick;
        if (!string.IsNullOrWhiteSpace(_save.Username)) return _save.Username;
        return _save.UserId;
    }

    private static Control ResourceCell(string label, string value)
    {
        VBoxContainer cell = new() { CustomMinimumSize = new Vector2(140, 0) };
        Label v = new() { Text = value, HorizontalAlignment = HorizontalAlignment.Center };
        v.AddThemeFontSizeOverride("font_size", 20);
        cell.AddChild(v);
        Label l = new()
        {
            Text = label,
            HorizontalAlignment = HorizontalAlignment.Center,
            // 0.5 的白字压在亮背景上几乎看不见；给偏冷的浅色 + 0.82 不透明度。
            Modulate = new Color(0.86f, 0.92f, 1f, 0.82f),
        };
        l.AddThemeFontSizeOverride("font_size", 13);
        cell.AddChild(l);
        return cell;
    }

    /// <summary>一个入口：标题 + 一行小字说明。锁着的时候按钮不可按、说明改成锁定原因。</summary>
    private static Control Entry(string title, string subtitle, Action? action, bool unlocked)
    {
        VBoxContainer box = new() { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        box.AddThemeConstantOverride("separation", 2);

        Button button = new()
        {
            Text = unlocked ? title : title + "（未开放）",
            CustomMinimumSize = new Vector2(0, 62),
            Disabled = !unlocked || action is null,
        };
        button.AddThemeFontSizeOverride("font_size", 20);
        if (action is not null && unlocked) button.Pressed += action;
        box.AddChild(button);

        Label note = new()
        {
            Text = subtitle,
            HorizontalAlignment = HorizontalAlignment.Center,
            Modulate = new Color(0.86f, 0.92f, 1f, 0.82f),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        note.AddThemeFontSizeOverride("font_size", 12);
        box.AddChild(note);
        return box;
    }

    // ── 定期清剿 ─────────────────────────────────────────────────────

    /// <summary>
    /// 定期清剿：**按"打什么材料"分组，而不是按关卡 id 堆一长条**。
    /// </summary>
    /// <remarks>
    /// 阶跃核心与高危样本都是**每个属性一关**（离谒与冰共用、咏叹与共振共用 ⇒ 六关）；
    /// 武器素材按四类武器分（单刀 / 长剑 / 施术仪 / 配枪）。分组的口径来自 `03 §2` 的材料表。
    /// </remarks>
    private void ShowStages()
    {
        VBoxContainer root = new();
        root.AddThemeConstantOverride("separation", 10);

        Label title = new() { Text = "定期清剿" };
        title.AddThemeFontSizeOverride("font_size", 28);
        root.AddChild(title);

        Label hint = new()
        {
            Text = $"进本消耗运维配额（当前 {_save.Stamina.Current}/{BattleConstants.StaminaMax}）。" +
                   "结算由服务端发放，掉落与首通奖励以服务端返回为准。\n" +
                   $"已首通 {_save.Progress.Stages.Values.Count(static r => r.FirstClearRewarded)} 关。",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            Modulate = new Color(1, 1, 1, 0.6f),
        };
        root.AddChild(hint);
        root.AddChild(new Label
        {
            Text = "配额恢复：" + StaminaRegenText(),
            Modulate = new Color(1, 1, 1, 0.45f),
        });

        ScrollContainer scroll = new() { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        VBoxContainer list = new() { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        list.AddThemeConstantOverride("separation", 10);
        scroll.AddChild(list);
        root.AddChild(scroll);

        AddFarmGroup(list, "阶跃核心", "角色突破材料", "core");
        AddFarmGroup(list, "高危样本", "技能 8~14 级材料", "hazard");
        AddFarmGroup(list, "武器素材", "武器升级材料", "weapon");
        AddFarmGroup(list, "经验", "角色经验", "exp");

        root.AddChild(Wrap(BackButton(ShowHome)));
        Swap(root);
        StatusLine();
    }

    private string StaminaRegenText()
    {
        if (_save.Stamina.Current >= BattleConstants.StaminaMax) return "配额已满";
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        double elapsed = Math.Max(0, now - _save.Stamina.LastRegenAt) / 1000.0;
        int sec = (int)Math.Ceiling(BattleConstants.StaminaRegenSec - elapsed % BattleConstants.StaminaRegenSec);
        if (sec <= 0) sec = (int)BattleConstants.StaminaRegenSec;
        return $"下次 +1 {sec / 60:00}:{sec % 60:00}";
    }

    private void AddFarmGroup(VBoxContainer list, string title, string note, string kind)
    {
        List<StageDef> stages = [.. _data.Stages.Values
            .Where(s => string.Equals(s.Kind, kind, StringComparison.Ordinal))
            .OrderBy(s => s.Id, StringComparer.Ordinal)];

        int cleared = stages.Count(s =>
            _save.Progress.Stages.TryGetValue(s.Id, out PlayerSave.StageRecord? rec) && rec.FirstClearRewarded);
        Label head = new() { Text = $"{title}　{note} · 首通 {cleared}/{stages.Count}" };
        head.AddThemeFontSizeOverride("font_size", 18);
        list.AddChild(head);

        if (stages.Count == 0)
        {
            Label none = new()
            {
                Text = "　（还没有落盘的关卡）",
                Modulate = new Color(1, 1, 1, 0.45f),
            };
            list.AddChild(none);
            return;
        }

        GridContainer grid = new() { Columns = 3, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        grid.AddThemeConstantOverride("h_separation", 10);
        grid.AddThemeConstantOverride("v_separation", 8);
        foreach (StageDef stage in stages)
            grid.AddChild(StageButton(stage));
        list.AddChild(grid);
    }

    private Control StageButton(StageDef stage)
    {
        bool locked = _save.Profile.ProtocolLevel < stage.Reward.UnlockProtocolLevel;
        bool tooPoor = _save.Stamina.Current < stage.Reward.StaminaCost;
        bool cleared = _save.Progress.Stages.TryGetValue(stage.Id, out PlayerSave.StageRecord? rec)
                       && rec.FirstClearRewarded;
        Button b = new()
        {
            Text = stage.Name + (cleared ? "\uFF08\u5DF2\u9996\u901A\uFF09" : "") + "\n" +
                   $"{stage.Reward.StaminaCost} 配额" + (locked ? "　（协议等级不足）" : tooPoor ? "　（配额不足）" : ""),
            CustomMinimumSize = new Vector2(0, 64),
            Disabled = locked,
        };
        b.Pressed += () => ShowStageDetail(stage.Id);
        return b;
    }

    /// <summary>一关的详情：敌人、消耗、掉落、以及"开打"。</summary>
    /// <summary>设置：分辨率与全屏。**只改显示层，不改存档/战斗**。</summary>
    private void ShowSettings()
    {
        VBoxContainer root = new();
        root.AddThemeConstantOverride("separation", 12);

        Label title = new() { Text = "设置" };
        title.AddThemeFontSizeOverride("font_size", 28);
        root.AddChild(title);

        PanelContainer card = Card();
        VBoxContainer body = new();
        body.AddThemeConstantOverride("separation", 10);

        body.AddChild(new Label { Text = "分辨率（16:9 与 4:3 是一等公民）" });
        OptionButton picker = new() { CustomMinimumSize = new Vector2(360, 40) };
        foreach ((string label, _, _) in Resolutions) picker.AddItem(label);
        Vector2 viewport = GetViewport().GetVisibleRect().Size;
        int selected = 0;
        for (int i = 0; i < Resolutions.Length; i++)
        {
            if (Math.Abs(Resolutions[i].W - viewport.X) < 2 && Math.Abs(Resolutions[i].H - viewport.Y) < 2)
            {
                selected = i;
                break;
            }
        }
        picker.Selected = selected;
        picker.ItemSelected += index =>
        {
            (string label, int w, int h) = Resolutions[(int)index];
            DisplayServer.WindowSetSize(new Vector2I(w, h));
            GD.Print($"分辨率切换 → {label}");
        };
        body.AddChild(picker);

        CheckBox fullscreen = new()
        {
            Text = "全屏",
            ButtonPressed = DisplayServer.WindowGetMode() == DisplayServer.WindowMode.Fullscreen,
        };
        fullscreen.Toggled += on =>
        {
            if (on) DisplayServer.WindowSetMode(DisplayServer.WindowMode.Fullscreen);
            else
            {
                DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
                DisplayServer.WindowSetSize(new Vector2I(1920, 1080));
            }
        };
        body.AddChild(fullscreen);

        Label dataInfo = new()
        {
            Text = $"数据：{_data.Characters.Count} 角色 · {_data.Stages.Count} 关卡 · {_data.Weapons.Count} 武器 · {_data.GearSets.Count} 套装",
            Modulate = new Color(1, 1, 1, 0.6f),
        };
        dataInfo.AddThemeFontSizeOverride("font_size", 13);
        body.AddChild(dataInfo);

        body.AddChild(new Label { Text = "剧情自动播放间隔" });
        (string Label, double Seconds)[] speeds = [("快（1.2 秒）", 1.2), ("标准（1.8 秒）", 1.8), ("慢（2.5 秒）", 2.5)];
        OptionButton autoSpeed = new() { CustomMinimumSize = new Vector2(240, 36) };
        foreach ((string speedLabel, _) in speeds) autoSpeed.AddItem(speedLabel);
        autoSpeed.Selected = Math.Max(0, Array.FindIndex(speeds, s => Math.Abs(s.Seconds - _storyAutoDelay) < 0.01));
        autoSpeed.ItemSelected += index => _storyAutoDelay = speeds[(int)index].Seconds;
        body.AddChild(autoSpeed);

        Label note = new()
        {
            Text = "设置只影响当前运行窗口，不写入存档；下次启动仍按 project.godot 的基准分辨率。",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            Modulate = new Color(1, 1, 1, 0.5f),
        };
        note.AddThemeFontSizeOverride("font_size", 12);
        body.AddChild(note);
        Pad(card, body);
        root.AddChild(card);

        root.AddChild(new Control { SizeFlagsVertical = SizeFlags.ExpandFill });
        root.AddChild(Wrap(BackButton(ShowHome)));
        Swap(root);
        StatusLine();
    }

    /// <summary>黑檀演习：`kind = sim` 的高难模拟关卡入口。</summary>
    private void ShowSimChallenge()
    {
        VBoxContainer root = new();
        root.AddThemeConstantOverride("separation", 12);

        Label title = new() { Text = "黑檀演习" };
        title.AddThemeFontSizeOverride("font_size", 28);
        root.AddChild(title);

        Label desc = new()
        {
            Text = "高风险模拟战。敌人强度高于定期清剿，首通奖励也更高；队伍与普通关卡共用编队。",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            Modulate = new Color(1, 1, 1, 0.68f),
        };
        root.AddChild(desc);

        ScrollContainer scroll = new() { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        VBoxContainer list = new() { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        list.AddThemeConstantOverride("separation", 8);
        scroll.AddChild(list);
        root.AddChild(scroll);

        foreach (StageDef stage in _data.Stages.Values
                     .Where(static s => string.Equals(s.Kind, "sim", StringComparison.Ordinal))
                     .OrderBy(static s => s.Id, StringComparer.Ordinal))
        {
            string enemies = string.Join(" · ", stage.Enemies.Select(id => _data.Enemy(id).Name));
            Button b = new()
            {
                Text = $"{stage.Name}　[{enemies}]\n平衡实测靶场 · 不发奖励　配额 {stage.Reward.StaminaCost}",
                CustomMinimumSize = new Vector2(0, 76),
                Alignment = HorizontalAlignment.Left,
            };
            string stageId = stage.Id;
            b.Pressed += () => ShowStageDetail(stageId);
            list.AddChild(b);
        }

        root.AddChild(new Control { SizeFlagsVertical = SizeFlags.ExpandFill });
        root.AddChild(Wrap(BackButton(ShowHome)));
        Swap(root);
        StatusLine();
    }

    private void ShowStageDetail(string stageId)
    {
        StageDef stage = _data.Stage(stageId);

        VBoxContainer root = new();
        root.AddThemeConstantOverride("separation", 10);

        Label title = new() { Text = stage.Name };
        title.AddThemeFontSizeOverride("font_size", 28);
        root.AddChild(title);

        List<string> enemies = [.. stage.Enemies.Select(e => _data.Enemy(e).Name)];
        int hp = (int)stage.Enemies.Sum(e => _data.Enemy(e).Base.Hp);

        VBoxContainer info = new();
        info.AddChild(Line($"敌人：{string.Join(" · ", enemies)}"));
        info.AddChild(Line($"合计生命：{hp:N0}　　轮次上限：{stage.MaxRound}"));
        List<string> resistLines = [];
        foreach (string enemyId in stage.Enemies)
        {
            EnemyDef enemy = _data.Enemy(enemyId);
            if (enemy.ResistAll == 0 && enemy.Resist.Count == 0) continue;
            List<string> parts = [];
            if (enemy.ResistAll != 0) parts.Add($"全 {enemy.ResistAll:P0}");
            foreach ((string key, double value) in enemy.Resist.OrderBy(static kv => kv.Key, StringComparer.Ordinal))
            {
                if (Enum.TryParse(key, true, out WaningOath.Core.Element element))
                    parts.Add($"{ElementZh(element)} {value:P0}");
            }
            foreach ((string key, double value) in enemy.AnomalyResist.OrderBy(static kv => kv.Key, StringComparer.Ordinal))
                parts.Add($"异常·{key} {value:P0}");
            if (parts.Count > 0) resistLines.Add($"{enemy.Name}：" + string.Join(" / ", parts));
        }
        if (resistLines.Count > 0)
            info.AddChild(Line("敌方抗性：" + string.Join(" · ", resistLines)));
        foreach (string enemyId in stage.Enemies)
        {
            EnemyDef enemy = _data.Enemy(enemyId);
            string skills = string.Join(" · ", enemy.Skills.Select(static s => s.Name));
            if (skills.Length > 0)
            {
                Label skillLine = Line($"敌人技能（{enemy.Name}）：{skills}");
                skillLine.TooltipText = string.Join("\n\n", enemy.Skills
                    .Where(static skill => !string.IsNullOrWhiteSpace(skill.Description))
                    .Select(static skill => $"{skill.Name}：{skill.Description}"));
                info.AddChild(skillLine);
            }
        }
        List<string> party = !stage.FixedPanel && _save.Team.Count > 0 ? _save.Team : stage.Party;
        string partyText = string.Join(" · ", party.Select(id => _data.Character(id).Name));
        info.AddChild(Line($"上场队伍：{partyText}" + (stage.FixedPanel ? "（教学固定）" : "")));
        info.AddChild(Line($"消耗：运维配额 {stage.Reward.StaminaCost}　　解锁协议等级：{stage.Reward.UnlockProtocolLevel}"));
        info.AddChild(Line($"掉落（每次）：{DescribeReward(stage.Reward.Repeat)}"));
        bool firstClaimed = string.Equals(stage.Kind, "boss", StringComparison.Ordinal)
            ? _save.Progress.BossFreeUsed >= 1
            : _save.Progress.Stages.TryGetValue(stageId, out PlayerSave.StageRecord? stageRec)
              && stageRec.FirstClearRewarded;
        if (string.Equals(stage.Kind, "sim", StringComparison.Ordinal))
        {
            info.AddChild(Line("首通奖励：不适用（平衡实测靶场）"));
            info.AddChild(Line("结算：平衡实测靶场，不发放奖励（服务端会拒绝 stage/clear）。"));
        }
        else
        {
            info.AddChild(Line($"首通：{DescribeReward(stage.Reward.FirstClear)}"));
            info.AddChild(Line(firstClaimed ? "首通奖励：已领取" : "首通奖励：未领取"));
            if (string.Equals(stage.Kind, "boss", StringComparison.Ordinal))
            {
                info.AddChild(Line(_save.Progress.BossFreeUsed >= 1
                    ? "本周奖励：已领取（可继续挑战，但不再发奖；周一 04:00 重置）。"
                    : "本周奖励：首通 60 协议原码，尚未领取。"));
            }
        }
        root.AddChild(info);

        root.AddChild(new Control { SizeFlagsVertical = SizeFlags.ExpandFill });

        HBoxContainer bar = new() { Alignment = BoxContainer.AlignmentMode.Center };
        Button fight = new() { Text = "开打", CustomMinimumSize = new Vector2(200, 48) };
        fight.Pressed += () => StartStageBattle(stageId);
        bar.AddChild(fight);

        Button back = new() { Text = "返回", CustomMinimumSize = new Vector2(160, 44) };
        back.Pressed += ShowStages;
        bar.AddChild(back);
        root.AddChild(bar);

        Swap(root);
        StatusLine();
    }

    private string DescribeReward(RewardBundle bundle)
    {
        List<string> parts = [];
        if (bundle.ProtocolCode != 0) parts.Add($"协议原码 ×{bundle.ProtocolCode}");
        if (bundle.Credit != 0) parts.Add($"授信额度 ×{bundle.Credit}");
        foreach ((string id, int count) in bundle.Items)
            parts.Add($"{_data.Growth.ItemName(id)} ×{count}");
        return parts.Count == 0 ? "无" : string.Join(" · ", parts);
    }

    private Control OwnerRow(string text, string? ownerId)
    {
        HBoxContainer row = new();
        row.AddThemeConstantOverride("separation", 8);
        Label label = Line(text);
        label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        row.AddChild(label);
        if (ownerId is not null)
        {
            Button view = new() { Text = "查看", CustomMinimumSize = new Vector2(64, 30) };
            view.Pressed += () => ShowCharacterDetail(ownerId);
            row.AddChild(view);
        }
        return row;
    }

    private static Label Line(string text)
    {
        Label label = new() { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        return label;
    }

    // ── 商城 ─────────────────────────────────────────────────────────

    /// <summary>
    /// 商城：**回执兑换 / 衣装 / 充值**。
    /// </summary>
    /// <remarks>
    /// 充值只做空壳 —— 点击任意项都提示"请联系管理员处理"（本作不接任何支付渠道）。
    /// 衣装页列的是 `角色立绘/` 里已经有的那几套；**能不能生效由剧情决定**，
    /// 所以这一页只负责"看"和"选"，不承诺"下一场就穿上了"。
    /// </remarks>
    /// <summary>权限许可（抽卡）：服务端决定出货，客户端只展示结果。</summary>
    private void ShowGacha()
    {
        VBoxContainer root = new();
        root.AddThemeConstantOverride("separation", 12);

        HBoxContainer head = new();
        Label title = new() { Text = "权限许可", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        title.AddThemeFontSizeOverride("font_size", 28);
        head.AddChild(title);
        head.AddChild(BackButton(ShowHome));
        root.AddChild(head);

        Label wallet = new()
        {
            Text = $"协议原码 {_save.Currency.ProtocolCode:N0}　·　常驻许可 {_save.Currency.NormalTicket}　·　限定许可 {_save.Currency.SpecialTicket}",
            Modulate = new Color(1, 1, 1, 0.72f),
        };
        root.AddChild(wallet);
        Label pity = new()
        {
            Text = $"常驻保底 {_save.Gacha.StandardPity}/80　·　限定保底 {_save.Gacha.LimitedPity}/80　·　四星保底 {_save.Gacha.FourPity}/10",
            Modulate = new Color(1, 1, 1, 0.52f),
        };
        pity.AddThemeFontSizeOverride("font_size", 13);
        root.AddChild(pity);
        Label weaponPity = new()
        {
            Text = $"武器保底 {_save.Gacha.WeaponPity}/80",
            Modulate = new Color(1, 1, 1, 0.45f),
        };
        weaponPity.AddThemeFontSizeOverride("font_size", 12);
        root.AddChild(weaponPity);
        Label rates = new()
        {
            Text = $"基础概率：五星 {_data.Gacha.Rates.Five:P1} · 四星 {_data.Gacha.Rates.Four:P1} · 软保底 {_data.Gacha.Rates.SoftPityStart} 抽起逐步提升 · 累计 {_save.Gacha.TotalPulls} 抽",
            Modulate = new Color(1, 1, 1, 0.45f),
        };
        rates.AddThemeFontSizeOverride("font_size", 12);
        root.AddChild(rates);

        HBoxContainer cards = new() { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        cards.AddThemeConstantOverride("separation", 12);
        (string Id, string Name, string Desc)[] pools =
        [
            ("standard", "常驻权限许可", "全部常驻角色；四星保底 10 抽，五星硬保底 80 抽。"),
            ("limited", "限定权限许可", "与常驻共享五星保底规则；限定池之间共享保底。"),
            ("weapon", "权限覆写许可", "五星武器 / 四星武器 / 3★ 制式武器；用于武器与覆写养成。"),
        ];
        foreach ((string id, string name, string desc) in pools)
        {
            PanelContainer card = new() { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            VBoxContainer body = new();
            body.AddThemeConstantOverride("separation", 8);
            Label n = new() { Text = name };
            n.AddThemeFontSizeOverride("font_size", 20);
            body.AddChild(n);
            body.AddChild(new Label { Text = desc, AutowrapMode = TextServer.AutowrapMode.WordSmart });
            HBoxContainer buttons = new();
            Button one = new() { Text = "单抽 · 160", CustomMinimumSize = new Vector2(130, 42) };
            one.Pressed += () => _ = PullAsync(id, 1);
            buttons.AddChild(one);
            Button ten = new() { Text = "十连 · 1,600", CustomMinimumSize = new Vector2(150, 42) };
            ten.Pressed += () => _ = PullAsync(id, 10);
            buttons.AddChild(ten);
            body.AddChild(buttons);
            Pad(card, body);
            cards.AddChild(card);
        }
        root.AddChild(cards);

        if (_gachaMessage.Length > 0)
        {
            Label msg = new() { Text = _gachaMessage, AutowrapMode = TextServer.AutowrapMode.WordSmart };
            root.AddChild(msg);
        }
        if (_gachaPulls.Count > 0)
        {
            PanelContainer result = new();
            VBoxContainer lines = new();
            foreach (string pull in _gachaPulls)
                lines.AddChild(new Label { Text = pull });
            Pad(result, lines);
            root.AddChild(result);
        }

        root.AddChild(new Control { SizeFlagsVertical = SizeFlags.ExpandFill });
        root.AddChild(Wrap(BackButton(ShowHome)));
        Swap(root);
        StatusLine();
    }

    private async Task PullAsync(string pool, int count)
    {
        ShowBusy("正在请求权限许可…");
        (bool Ok, string Message, List<string> Pulls) result = await _api.GachaAsync(pool, count);
        _gachaMessage = result.Ok ? result.Message : "抽卡失败：" + result.Message;
        _gachaPulls.Clear();
        _gachaPulls.AddRange(result.Pulls);
        await ReloadSaveAsync();
        ShowGacha();
    }

    private void ShowShop()
    {
        VBoxContainer root = new();
        root.AddThemeConstantOverride("separation", 10);

        Label title = new() { Text = "商城" };
        title.AddThemeFontSizeOverride("font_size", 28);
        root.AddChild(title);

        PanelContainer wallet = Card();
        HBoxContainer row = new();
        row.AddThemeConstantOverride("separation", 24);
        row.AddChild(ResourceCell("申请回执", _save.Currency.Receipt.ToString("N0")));
        row.AddChild(ResourceCell("通过回执", _save.Currency.PassReceipt.ToString("N0")));
        row.AddChild(ResourceCell("常规申请书", _save.Currency.NormalTicket.ToString("N0")));
        row.AddChild(ResourceCell("特别申请书", _save.Currency.SpecialTicket.ToString("N0")));
        row.AddChild(ResourceCell("协议原码", _save.Currency.ProtocolCode.ToString("N0")));
        Pad(wallet, row);
        root.AddChild(wallet);

        HBoxContainer tabs = new() { Alignment = BoxContainer.AlignmentMode.Center };
        tabs.AddChild(TabButton("回执兑换", () => ShowShopTab("exchange")));
        tabs.AddChild(TabButton("衣装", () => ShowShopTab("skin")));
        tabs.AddChild(TabButton("充值", () => ShowShopTab("recharge")));
        root.AddChild(tabs);

        ScrollContainer scroll = new() { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        VBoxContainer body = new() { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        body.Name = "shopBody";
        body.AddThemeConstantOverride("separation", 8);
        scroll.AddChild(body);
        root.AddChild(scroll);

        FillShopBody(body, "exchange");

        root.AddChild(Wrap(BackButton(ShowHome)));
        Swap(root);
        StatusLine();
    }

    private void ShowShopTab(string tab)
    {
        // 重画整页最省事，也让"当前在哪一页"永远与内容一致。
        ShowShop();
        if (_screenHost.GetChild(0) is VBoxContainer root)
        {
            foreach (Node child in root.GetChildren())
            {
                if (child is ScrollContainer sc && sc.GetChild(0) is VBoxContainer body)
                {
                    FillShopBody(body, tab);
                    return;
                }
            }
        }
    }

    private void ShopExchangeRow(VBoxContainer body, string name, string cost, string itemId, int monthlyLimit = 0)
    {
        HBoxContainer row = new();
        row.AddThemeConstantOverride("separation", 10);
        int used = _save.Progress.MonthlyPurchases.GetValueOrDefault(itemId);
        string limitText = monthlyLimit > 0 ? $"　本月 {used}/{monthlyLimit}" : "";
        Label label = new() { Text = $"{name}　·　{cost}{limitText}", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddChild(label);
        Button buy = new() { Text = "兑换", CustomMinimumSize = new Vector2(90, 38), Disabled = monthlyLimit > 0 && used >= monthlyLimit };
        buy.Pressed += () => _ = ExchangeAsync(itemId);
        row.AddChild(buy);
        body.AddChild(row);
    }

    private async Task ExchangeAsync(string itemId)
    {
        ShowBusy("正在兑换…");
        ApiClient.Reply reply = await _api.ShopExchangeAsync(itemId, 1);
        await ReloadSaveAsync();
        _toast = reply.Ok ? "兑换完成。" : "兑换失败：" + reply.Message;
        ShowShop();
    }

    private void FillShopBody(VBoxContainer body, string tab)
    {
        foreach (Node child in body.GetChildren()) child.QueueFree();

        if (tab == "exchange")
        {
            body.AddChild(Line($"回执兑换　持有：申请 {_save.Currency.Receipt:N0} · 通过 {_save.Currency.PassReceipt:N0}"));
            ShopExchangeRow(body, "授信额度 ×20,000", "60 申请回执", "credit");
            ShopExchangeRow(body, "常规权限申请书 ×1", "60 申请回执", "normalTicket");
            ShopExchangeRow(body, "特别权限申请书 ×1", "150 通过回执", "specialTicket");
            ShopExchangeRow(body, "阶跃核心 ×5", "200 申请回执", "jumpCore", 48);
            ShopExchangeRow(body, "高危样本 ×4", "300 申请回执", "hazardSample", 16);
            ShopExchangeRow(body, "首席卷宗 ×20", "100 申请回执", "chiefDossier", 10);
            body.AddChild(Line("权阶升级许可 / 武器覆写许可不在此列表：去「角色」→ 角色详情 / 五星武器旁边兑换。"));
            Label note = new()
            {
                Text = "以上六项直接购买；权阶与覆写许可按角色/武器逐个购买。",
                Modulate = new Color(1, 1, 1, 0.5f),
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
            };
            body.AddChild(note);
            return;
        }

        if (tab == "skin")
        {
            body.AddChild(Line("衣装：已落盘立绘的衣装如下。"));
            foreach ((string id, string name) in OwnedCharacters())
            {
                System.Collections.Generic.IReadOnlyList<Portraits.SkinDef> skins =
                    Portraits.SkinsFor(id, _save.Profile.ProtagonistGender);
                if (skins.Count == 0) continue;
                body.AddChild(Line($"　{name}：{string.Join(" · ", skins.Select(s => s.Name))}"));
            }
            Label rule = new()
            {
                Text = "衣着规则：主线剧情里固定显示原皮；部分活动剧情会指定特定衣装（那段剧情里免费试用）。" +
                       "因此在角色页手动选的衣装，不会改变正式流程里的立绘。",
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                Modulate = new Color(1, 1, 1, 0.6f),
            };
            body.AddChild(rule);
            return;
        }

        body.AddChild(Line("充值：本作不接任何支付渠道。"));
        foreach (string item in new[]
                 {
                     "协议原码 ×160（约 1 抽）", "协议原码 ×1600（约 10 抽）",
                     "常规权限申请书 ×10", "特别权限申请书 ×10", "运维配额 补充包",
                 })
        {
            Button b = new() { Text = item + "　→　请联系管理员处理", CustomMinimumSize = new Vector2(0, 46) };
            b.Pressed += () =>
            {
                _toast = "请联系管理员处理。";
                StatusLine();
            };
            body.AddChild(b);
        }
    }

    private static Button TabButton(string text, Action action)
    {
        Button b = new() { Text = text, CustomMinimumSize = new Vector2(160, 40) };
        b.Pressed += action;
        return b;
    }

    // ── 故事 ─────────────────────────────────────────────────────────

    /// <summary>故事入口：目前只有序章；打完的会显示已通关。</summary>
    private void ShowStoryList()
    {
        VBoxContainer root = new();
        root.AddThemeConstantOverride("separation", 12);

        Label title = new() { Text = "故事" };
        title.AddThemeFontSizeOverride("font_size", 28);
        root.AddChild(title);

        bool done = _save.Progress.Stories.TryGetValue("prologue", out PlayerSave.StoryRecord? rec) && rec.Cleared;

        Button prologue = new()
        {
            Text = "序章 · 如约而至" + (done ? "　（已通关 · 可重看）" : ""),
            CustomMinimumSize = new Vector2(0, 64),
        };
        prologue.Pressed += () => ShowStory(0);
        root.AddChild(prologue);

        // 第一章与幕间走数据通路：Assets/Story/*.story.json（由 tools/story-convert.mjs 生成）。
        AddScriptEntry(root, "chapter-1", "第一章 · 旧账未清");
        AddScriptEntry(root, "interlude-1", "第一章幕间 · 请低头");

        root.AddChild(new Control { SizeFlagsVertical = SizeFlags.ExpandFill });
        root.AddChild(Wrap(BackButton(ShowHome)));
        Swap(root);
        StatusLine();
    }

    // ── 角色 ─────────────────────────────────────────────────────────

    /// <summary>角色入口：**先列表，再进详情**（立绘、衣装、养成都在详情里）。</summary>
    private void ShowCharacters()
    {
        VBoxContainer root = new();
        root.AddThemeConstantOverride("separation", 12);

        Label title = new() { Text = "角色" };
        title.AddThemeFontSizeOverride("font_size", 28);
        root.AddChild(title);

        HBoxContainer filters = new();
        filters.AddThemeConstantOverride("separation", 6);
        filters.AddChild(new Label { Text = "筛选" });
        foreach (string element in new[] { "", "物理", "冰", "火", "雷", "风", "共振", "离谒", "咏叹" })
        {
            string captured = element;
            Button filter = new()
            {
                Text = captured.Length == 0 ? "全部" : captured,
                ToggleMode = true,
                ButtonPressed = _characterFilter == captured,
                CustomMinimumSize = new Vector2(72, 34),
            };
            filter.Pressed += () =>
            {
                _characterFilter = captured;
                ShowCharacters();
            };
            filters.AddChild(filter);
        }
        root.AddChild(filters);

        GridContainer grid = new() { Columns = 4, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        grid.AddThemeConstantOverride("h_separation", 12);
        grid.AddThemeConstantOverride("v_separation", 12);

        bool wroteCharacter = false;
        foreach ((string id, string name) in OwnedCharacters())
        {
            _save.Characters.TryGetValue(id, out PlayerSave.CharacterState? st);
            CharacterDef def = _data.Character(id);
            if (_characterFilter.Length > 0
                && !def.ResolveElements().Any(e => string.Equals(ElementZh(e), _characterFilter, StringComparison.Ordinal)))
                continue;
            VBoxContainer cell = new() { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            cell.AddThemeConstantOverride("separation", 6);

            // 四列卡位：立绘缩到 120×180，让 4 行在 1080 高里排得下（13 名角色 = 4 行），
            // 之前 150×240 时每格过高，滚动区里两行会挤在一起。
            TextureRect? tex = Portraits.Tile(
                Portraits.FileForSkin(id, _save.Profile.ProtagonistGender,
                    _save.Profile.Skins.GetValueOrDefault(id)) ?? "", 120f, 180f);
            if (tex is not null) cell.AddChild(tex);

            string elements = string.Join(" / ", def.ResolveElements().Select(ElementZh));
            Label meta = new()
            {
                Text = $"{def.Rarity}★ · {elements}",
                HorizontalAlignment = HorizontalAlignment.Center,
                Modulate = new Color(1, 1, 1, 0.65f),
            };
            meta.AddThemeFontSizeOverride("font_size", 12);
            meta.AddThemeColorOverride("font_color", ElementColor(def.ResolveElements().FirstOrDefault()));
            cell.AddChild(meta);

            Button b = new()
            {
                Text = $"{name}\nLv{st?.Level ?? 1} · 权阶 {st?.Eidolon ?? 0}",
                CustomMinimumSize = new Vector2(0, 56),
            };
            string captured = id;
            b.Pressed += () => ShowCharacterDetail(captured);
            cell.AddChild(b);
            grid.AddChild(cell);
            wroteCharacter = true;
        }
        if (!wroteCharacter)
            root.AddChild(new Label { Text = "该属性暂无可筛选角色。", Modulate = new Color(1, 1, 1, 0.5f) });

        ScrollContainer scroll = new()
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        scroll.AddChild(grid);
        root.AddChild(scroll);
        // 不要再补一个 ExpandFill 的占位控件：它会和滚动区平分高度，
        // 结果滚动区只剩一半、下面一大片空白（13 名角色只看得到两行）。
        root.AddChild(Wrap(BackButton(ShowHome)));
        Swap(root);
        StatusLine();
    }

    /// <summary>编队：从已拥有角色里选 1~4 人，顺序即站位；保存走服务端校验。</summary>
    private void ShowTeam()
    {
        VBoxContainer root = new();
        root.AddThemeConstantOverride("separation", 12);

        Label hint = new()
        {
            Text = "选择 1~4 名角色。点选顺序就是站位；一号位会影响部分阵营判定。薇拉·约克在场时，队伍最多 2 人。",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            Modulate = new Color(1, 1, 1, 0.72f),
        };
        root.AddChild(hint);

        List<(string Id, string Name)> owned = OwnedCharacters();
        List<string> selected = [.. _save.Team.Where(id => owned.Exists(x => x.Id == id))];
        if (selected.Count == 0 && owned.Count > 0) selected.Add(owned[0].Id);

        // 角色自带的编队上限（薇拉「孤胆英雄」：有她在场时最多 2 人）。
        (int Limit, string? Limiter) TeamLimitOf(IEnumerable<string> ids)
        {
            int limit = 4;
            string? limiter = null;
            foreach (string id in ids)
            {
                CharacterDef d = _data.Character(id);
                if (d.TeamLimit is int lim && lim > 0 && lim < limit)
                {
                    limit = lim;
                    limiter = d.Name;
                }
            }
            return (limit, limiter);
        }

        Button save = new() { Text = "保存编队", CustomMinimumSize = new Vector2(180, 44) };
        Label picked = new() { Text = "" };
        void RefreshPicked()
        {
            (int limit, string? limiter) = TeamLimitOf(selected);
            picked.Text = selected.Count == 0
                ? "尚未选择"
                : $"已选（{selected.Count}/{limit}）：" + string.Join(" → ", selected.Select(id => _data.Character(id).Name))
                  + (limiter is not null ? $"　⚠️ {limiter} 在场，队伍最多 {limit} 人" : "");
            save.Disabled = selected.Count == 0 || selected.Count > limit;
        }

        GridContainer grid = new() { Columns = 4, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        grid.AddThemeConstantOverride("h_separation", 10);
        grid.AddThemeConstantOverride("v_separation", 10);

        foreach ((string id, string name) in owned)
        {
            VBoxContainer cell = new() { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            TextureRect? tex = Portraits.Tile(
                Portraits.FileForSkin(id, _save.Profile.ProtagonistGender,
                    _save.Profile.Skins.GetValueOrDefault(id)) ?? "", 120f, 180f);
            if (tex is not null) cell.AddChild(tex);

            Button toggle = new()
            {
                Text = name,
                ToggleMode = true,
                ButtonPressed = selected.Contains(id),
                CustomMinimumSize = new Vector2(0, 40),
            };
            string captured = id;
            toggle.Toggled += on =>
            {
                if (on)
                {
                    List<string> next = [.. selected, captured];
                    (int limit, string? limiter) = TeamLimitOf(next);
                    if (next.Count > limit)
                    {
                        toggle.SetPressedNoSignal(false);
                        _toast = limiter is null
                            ? $"编队最多 {limit} 人。"
                            : $"{limiter} 在场时队伍最多 {limit} 人。";
                        StatusLine();
                        return;
                    }
                    if (!selected.Contains(captured)) selected.Add(captured);
                }
                else
                {
                    selected.Remove(captured);
                }
                RefreshPicked();
            };
            cell.AddChild(toggle);
            grid.AddChild(cell);
        }

        ScrollContainer teamScroll = new()
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        teamScroll.AddChild(grid);
        root.AddChild(teamScroll);
        root.AddChild(picked);
        RefreshPicked();

        Dictionary<string, string> chosenModes = new(_save.TeamModes, StringComparer.Ordinal);
        VBoxContainer modeBox = new();
        modeBox.AddThemeConstantOverride("separation", 4);
        foreach ((string id, string name) in owned)
        {
            CharacterDef def = _data.Character(id);
            if (def.Modes is null || def.Modes.Entries.Count <= 1) continue;

            HBoxContainer modeRow = new();
            modeRow.AddThemeConstantOverride("separation", 8);
            modeRow.AddChild(new Label { Text = $"{name} 模态" });
            OptionButton picker = new() { CustomMinimumSize = new Vector2(260, 34) };
            List<string> keys = [.. def.Modes.Entries.Keys.OrderBy(static k => k, StringComparer.Ordinal)];
            foreach (string key in keys)
            {
                ModeDef mode = def.Modes.Entries[key];
                picker.AddItem(string.IsNullOrWhiteSpace(mode.Name) ? key : mode.Name);
            }
            string current = chosenModes.GetValueOrDefault(id) ?? def.Modes.Default;
            picker.Selected = Math.Max(0, keys.IndexOf(current));
            picker.ItemSelected += index => chosenModes[id] = keys[(int)index];
            modeRow.AddChild(picker);
            modeBox.AddChild(modeRow);
        }
        if (modeBox.GetChildCount() > 0)
        {
            root.AddChild(new Label
            {
                Text = "双模态角色：进战斗前选定，战斗内不可切换。",
                Modulate = new Color(1, 1, 1, 0.5f),
            });
            root.AddChild(modeBox);
        }

        root.AddChild(new Control { SizeFlagsVertical = SizeFlags.ExpandFill });
        HBoxContainer bottom = new() { Alignment = BoxContainer.AlignmentMode.Center };
        save.Pressed += () => _ = SaveTeamAsync(selected, chosenModes);
        bottom.AddChild(save);
        bottom.AddChild(BackButton(ShowHome));
        root.AddChild(bottom);

        Swap(root);
        StatusLine();
    }

    private async Task SaveTeamAsync(List<string> team, IReadOnlyDictionary<string, string>? modes = null)
    {
        ShowBusy("正在保存编队…");
        ApiClient.Reply reply = await _api.SetTeamAsync(team, modes);
        await ReloadSaveAsync();
        _toast = reply.Ok ? "编队已保存。" : $"编队保存失败：{reply.Message}";
        ShowHome();
    }

    /// <summary>玩家已拥有的角色（按 `data/characters` 的顺序稳定排序）。</summary>
    private List<(string Id, string Name)> OwnedCharacters()
    {
        List<(string, string)> list = [];
        foreach (CharacterDef def in _data.Characters.Values.OrderBy(c => c.Id, StringComparer.Ordinal))
        {
            if (_save.Characters.ContainsKey(def.Id)) list.Add((def.Id, def.Name));
        }
        return list;
    }

    /// <summary>第一个已拥有角色的 id（自动巡检与默认进入用）。都没有就退回主角。</summary>
    private string FirstOwnedCharacter()
        => OwnedCharacters() is { Count: > 0 } list ? list[0].Id : _save.Profile.Protagonist;

    /// <summary>
    /// 从关卡详情开打。**配额不够就不放行** —— 扣配额是服务端的事，客户端只保证"别让玩家白跑一趟"。
    /// </summary>
    private void StartStageBattle(string stageId)
    {
        StageDef stage = _data.Stage(stageId);
        if (_save.Stamina.Current < stage.Reward.StaminaCost)
        {
            _toast = $"运维配额不足（需要 {stage.Reward.StaminaCost}，当前 {_save.Stamina.Current}）";
            ShowStageDetail(stageId);
            return;
        }
        ShowBattle(stageId, tutorial: false, fromStory: false);
    }

    private Button BackButton(Action action)
    {
        Button b = new() { Text = "返回", CustomMinimumSize = new Vector2(180, 42) };
        b.Pressed += action;
        return b;
    }

    // ── 小工具 ───────────────────────────────────────────────────────

    private static PanelContainer Card()
    {
        PanelContainer panel = new() { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        return panel;
    }

    private static void Pad(PanelContainer panel, Control child)
    {
        MarginContainer pad = new();
        pad.AddThemeConstantOverride("margin_left", 16);
        pad.AddThemeConstantOverride("margin_right", 16);
        pad.AddThemeConstantOverride("margin_top", 12);
        pad.AddThemeConstantOverride("margin_bottom", 12);
        pad.AddChild(child);
        panel.AddChild(pad);
    }

    /// <summary>底部状态行：连接状态 + 一次性提示（toast）。**按钮文案里不放策划案出处。**</summary>
    private void StatusLine()
    {
        _statusBar.Text = _toast.Length > 0
            ? _toast
            : $"分辨率 {GetViewport().GetVisibleRect().Size.X:0}×{GetViewport().GetVisibleRect().Size.Y:0}　·　服务器：{_api.Status}";
    }


    // ── 序章剧本 ─────────────────────────────────────────────────────

    /// <summary>
    /// 序章剧本：**逐句照抄 `剧情文本\序章 如约而至.txt` 的正文**，一句都不省。
    /// </summary>
    /// <remarks>
    /// 每个元素是 <c>(场景, 说话人, 台词, 立绘)</c>：
    /// <list type="bullet">
    ///   <item><b>场景</b>只用来标记"这一段在哪儿"，**不显示给玩家** —— S0/S7 这种编号是内部术语，
    ///         摆在屏幕上等于让玩家读策划案。</item>
    ///   <item>说话人为 <c>——</c> 表示**旁白**：不显示说话人、台词用暗一档的排版。</item>
    ///   <item><b>立绘</b>只有 <c>protagonist</c> 与 <c>xingyao</c> 两种。剧本里其余角色没有落盘立绘；
    ///         而那位「？？？」**故意不给立绘** —— 她的身份是剧情悬念，给张图就是剧透。</item>
    /// </list>
    /// <para>
    /// ⚠️ 这里**不允许出现概括**。之前的版本把台词砍掉了近一半（"我认识的不是你这个人，而是你的名字"
    /// 那一整段、警卫的"是！总监！"、守约人的"我好像天生就会用这把刀"等等），
    /// 玩家看到的就是一个残缺的序章。补的时候以 `剧情文本/` 为准，逐行核对。
    /// </para>
    /// </remarks>
    private static readonly (string Scene, string Speaker, string Line, string Portrait)[] Prologue =
    [
        // ── S0 ──
        ("S0", "——", "（黑屏。只有一道少女的声音。似从很远的地方传来，又近得像贴着耳畔。）", ""),
        ("S0", "？？？", "欢迎，无名的守望者。", "silhouette"),
        ("S0", "？？？", "这里，亦为终焉，也为起始。", "silhouette"),
        // ── S1 ──
        ("S1", "——", "（画面渐亮。Site 14 后山，晨雾弥漫。一个银发身影仰躺在倾斜的草地上，湖蓝色的眼睛缓缓睁开。）", ""),
        ("S1", "守约人", "……唔。", "protagonist"),
        ("S1", "——", "（他撑着地面坐起来，脑袋像被塞进了一团暴风。视线晃了晃，缓过来时，最先落进眼里的是一双血红色的眼睛。）", ""),
        ("S1", "——", "（面前站着一个黑红色哥特裙装的少女，银白长发垂至腰际，领口的红色蝴蝶结像一滴未干的血。她没在看他——视线越过他，落在更远的地方，像在核对一个早已约定的时刻。晨雾在她身边分开，像绕开了一件并不存在的东西。）", ""),
        // ── S2 ──
        ("S2", "守约人", "你……是谁？我为什么在这里？", "protagonist"),
        ("S2", "？？？", "这个问题，我想先反过来问。你记得自己是谁吗？", "silhouette"),
        ("S2", "守约人", "……不记得。什么都……不记得了。", "protagonist"),
        ("S2", "？？？", "果然如此。那么——欢迎来到 Site 14，无名的守望者。", "silhouette"),
        ("S2", "守约人", "守望者……？", "protagonist"),
        ("S2", "？？？", "你是「守约人」。一个被某种约定绑到这个世界来的人。虽然你忘了那约定，但约定还认得你。", "silhouette"),
        // ── S3 ──
        ("S3", "——", "（少女的身形慢慢消散在了空气中。她仿佛还想说什么，但没说出口，只是一个微笑，和一个“相信你”的嘴型。）", ""),
        ("S3", "守约人", "……那是……？", "protagonist"),
        ("S3", "——", "（风穿过草地。刚才站着人的地方，只剩下一小片被压平的草。）", ""),
        // ── S4 ──
        ("S4", "——", "（远处传来跑动的脚步声，和金属相互碰撞的声音。）", ""),
        ("S4", "警惕的设施警卫队长", "看那边！那边有个人！快通知总监！", ""),
        ("S4", "冷静的设施警卫", "好，队长，我马上通知！（接起内部通讯）总监！这里是 C-1 警卫队！我们在后山发现不明人员，麻烦您过来一趟！", ""),
        ("S4", "守约人", "（疑惑地问）你们是……？", "protagonist"),
        ("S4", "警卫队长", "我们是 CCRF·Site 14 设施警卫队。你是什么人？！", ""),
        ("S4", "守约人", "（迷茫地）我……？我也不记得了。", "protagonist"),
        // ── S5 ──
        ("S5", "？？？", "把枪放下。", "xingyao"),
        ("S5", "警卫队长、警卫队员", "是！总监！", ""),
        ("S5", "千语星遥", "我是 CCRF·Site 14 的设施总监，千语星遥。敢问阁下尊姓大名？", "xingyao"),
        ("S5", "守约人", "名字……？我不太记得了。我只记得我是「守约人」。", "protagonist"),
        ("S5", "千语星遥", "「守约人」……？（示意警卫退下）你们继续巡逻，这里交给我。", "xingyao"),
        ("S5", "守约人", "你认识我？说起来，刚才有个穿红衣服的人跟我说了几句话……嗯，现在想来也想不太起来她说过什么了。不过她长得好像跟你挺像的。", "protagonist"),
        ("S5", "千语星遥", "唔，怎么说呢——我认识的不是你这个人，而是你的名字。有人很早以前跟我说过：‘如果有一天，一个无名无姓、没有记忆的守约人在你附近出现，记得请好好待他。’", "xingyao"),
        ("S5", "千语星遥", "至于你说的那个跟我长得像的人……可能是你的幻觉吧？监控里并没有其他人来过这里。", "xingyao"),
        ("S5", "——", "（她顿了一下。目光像是落在什么他看不见的东西上。）", ""),
        ("S5", "千语星遥", "……算了。这片草地我也读过。什么也没有。", "xingyao"),
        ("S5", "守约人", "读过？", "protagonist"),
        ("S5", "千语星遥", "哦，别在意。走吧，边走边说——你脚下这个世界，现在乱得很。", "xingyao"),
        // ── S6 ──
        ("S6", "——", "（她转身朝山坡下走去，示意守约人跟上。两人穿过渐疏的树丛，远处露出了灰色的水泥建筑群——那是 Site 14 的设施群。）", ""),
        ("S6", "千语星遥", "先说大背景吧。大概三年前，一种被当时叫做‘无名元素’的物质在全球泄露——我们后来把它定名为「弥尘」。它把人类文明折腾得不成样子。动物会变异，物品会有各种异常效应，到处乱成一锅粥。", "xingyao"),
        ("S6", "守约人", "……异常？", "protagonist"),
        ("S6", "千语星遥", "对，异常。现在世界上剩下来的几个势力：残存政府搞了个 UNEG，整天喊着要把异常统统消灭；我们 CCRF 觉得，与其消灭，不如研究、控制，甚至利用。理念不同嘛，一来二去就成了对家。", "xingyao"),
        ("S6", "守约人", "CCRF……我好像听过。", "protagonist"),
        ("S6", "千语星遥", "哦？看来记忆不是完全清零。", "xingyao"),
        // ── S6B ──
        ("S6B", "——", "（她忽然停下步子，抬手示意他别动。）", ""),
        ("S6B", "——", "（前方的路边，一扇锈死的铁栅门半开着。门边站着一只四足的东西——体表覆着一层黑色、半透明的结晶，结晶顺着骨骼生长，像一具从里面长出来的铠甲。它没有动。它甚至没有看他们。）", ""),
        ("S6B", "千语星遥", "……唔。这就有点奇怪了。", "xingyao"),
        ("S6B", "守约人", "那是……什么？", "protagonist"),
        ("S6B", "千语星遥", "「候门」。野外都叫它犬型异常，编号 CCRF-017。", "xingyao"),
        ("S6B", "千语星遥", "这东西的规矩很简单：你不碰它，它就不理你。它会一直站在那个门口，不吃、不动、不回应——一直站到那扇门后面的人回来为止。或者，永远。", "xingyao"),
        ("S6B", "千语星遥", "记住一件事：这一路上，你可以看它，可以数它身上的结晶，唯独别碰它。", "xingyao"),
        ("S6B", "守约人", "如果碰了呢？", "protagonist"),
        ("S6B", "千语星遥", "那它这辈子，就只认你一个了。", "xingyao"),
        ("S6B", "——", "（星遥先走。她目不斜视，步伐不快不慢，像走过一段再平常不过的路。）", ""),
        ("S6B", "——", "（守约人跟在后面。经过那扇铁栅门时，他和那东西之间只剩下不到一步。）", ""),
        ("S6B", "——", "（结晶层下面有纹路。细密的、连续的纹路，沿着骨骼走向生长，像一条走熟了的路。）", ""),
        ("S6B", "——", "（他不知道自己为什么会去看那些纹路。）", ""),
        ("S6B", "——", "（他只知道，下一瞬间，刀已经出鞘了。）", ""),
        ("S6B", "——", "（刀刃磕在结晶上，发出敲击石头的声音。结晶表面连一道痕都没有留下。）", ""),
        ("S6B", "——", "（它没有反击。它只是，抬起了头。）", ""),
        ("S6B", "——", "（那双赤红色的眼睛，第一次真正对上了他。）", ""),
        ("S6B", "千语星遥", "……你碰它了。", "xingyao"),
        ("S6B", "守约人", "我……", "protagonist"),
        ("S6B", "千语星遥", "（叹了口气）行吧。这下它认识你了。", "xingyao"),
        ("S6B", "——", "（那东西缓缓地、平稳地转过身来，面朝守约人。它不再守着那扇门了。）", ""),
        ("S6B", "千语星遥", "它现在不会走了。除非我们把它结束掉——一次结束掉。", "xingyao"),
        ("S6B", "千语星遥", "这种事，绕开它就行了。你已经把最简单的那条路走掉了。", "xingyao"),
        ("S6B", "千语星遥", "……算了。试试手吧，新人。", "xingyao"),
        ("S6B", "千语星遥", "听着——你打不死它。它的结晶不吃刀。", "xingyao"),
        ("S6B", "千语星遥", "但你要是能攒够力气，一次把它整个掀翻，那它就只是个死物了。", "xingyao"),
        // ── S7 ──
        ("S7", "千语星遥", "别磨。你只有一次机会。", "xingyao"),
        // ── S8 ──
        ("S8", "——", "（结晶碎裂，散在地上，像一地没化的黑冰。里面那些纹路，断了。）", ""),
        ("S8", "千语星遥", "（啪啪鼓掌）哇哦。失忆了还这么厉害，我都有点嫉妒了。", "xingyao"),
        ("S8", "守约人", "……它本来不会动的。", "protagonist"),
        ("S8", "千语星遥", "对。它本来不会动的。", "xingyao"),
        ("S8", "守约人", "我……不知道为什么。好像天生就会用这把刀。", "protagonist"),
        ("S8", "千语星遥", "那就先别纠结了。身体还记得的东西，往往就是最真实的答案。", "xingyao"),
        ("S8", "——", "（她蹲下来，看了看那扇铁栅门。）", ""),
        ("S8", "千语星遥", "这是 C 区七号竖井的备勤通道。平时锁着，没人走。", "xingyao"),
        ("S8", "守约人", "那它为什么站在这儿？", "protagonist"),
        ("S8", "千语星遥", "候门只守两样东西：走过那扇门的人，或者那扇门本身。", "xingyao"),
        ("S8", "千语星遥", "门平时锁着——所以它不是在守门。它是在等人。", "xingyao"),
        ("S8", "——", "（她伸手把那扇半开的铁栅门推了推，又停住。）", ""),
        ("S8", "千语星遥", "……而它等的那扇门，不该是开着的。", "xingyao"),
        ("S8", "千语星遥", "（站起来，拍了拍手上的灰）我先喊人去检查一下 C 区收容单元。", "xingyao"),
        ("S8", "——", "（她朝坡下招了招手，示意跟上。走出一段路后，她像忽然想起什么。）", ""),
        ("S8", "千语星遥", "对了，刚才没说完——Site 14 是我管的站点。往南走半天，有个中立交易区叫「暴风眼」。末日里少有的一片净土，什么人都能去，什么都能买。", "xingyao"),
        ("S8", "千语星遥", "但野外还是得小心掠夺者。那群人可不像我这样讲道理。", "xingyao"),
        ("S8", "守约人", "你一下子跟我说这么多，不怕我其实是敌人？", "protagonist"),
        ("S8", "千语星遥", "（停下步子，回头看了他一眼）怕什么。你要是敌人，刚才就不会躺在我后山了。", "xingyao"),
        ("S8", "千语星遥", "再说了——", "xingyao"),
        ("S8", "——", "（琥珀色的眼睛里带着一点认真。）", ""),
        ("S8", "千语星遥", "你是「守约人」。会如约而至的人，不会那么坏。", "xingyao"),
        ("S8", "——", "（她从口袋里掏出一张蓝白配色的 ID 卡，递到守约人面前。卡片上没有姓名，只有一行空格，空格前印着「欢迎」二字。）", ""),
        ("S8", "千语星遥", "你的临时通行证。等你想起真正的名字，或者，决定一个属于自己的名字之前，就先用「守约人」这个代号吧。", "xingyao"),
        ("S8", "守约人", "……我记住了。", "protagonist"),
        ("S8", "——", "（他低头看了看手中的 ID 卡，又看向少女轻快的背影。晨光落在她银白的头发上，像为这个混沌的世界镀上一层短暂的温柔。）", ""),
        ("S8", "千语星遥", "走吧，食堂在这边。今天刚好有烤肉，我请客——就当是替那份约定预付的。", "xingyao"),
        ("S8", "千语星遥", "（回头）对了，从今天起你归我管。别乱跑，也别再碰不该碰的东西。", "xingyao"),
        ("S8", "——", "（他迈开步子，跟了上去。）", ""),
    ];

    /// <summary>哪一幕打完进战斗。</summary>
    private const string BattleScene = "S7";

    private int _storyIndex;
    private bool _storyAuto;
    private int _storyAutoVersion;
    private double _storyAutoDelay = 1.8;

    /// <summary>自动播放：每 1.8 秒推一拍；遇战斗/结尾/离开剧情自动停。</summary>
    private async Task AutoStoryAsync(int version)
    {
        while (_storyAuto && version == _storyAutoVersion && IsInsideTree())
        {
            await ToSignal(GetTree().CreateTimer(_storyAutoDelay), SceneTreeTimer.SignalName.Timeout);
            if (!_storyAuto || version != _storyAutoVersion || !IsInsideTree()) return;
            if (_storyIndex >= Prologue.Length - 1 || IsLastBattleBeat(_storyIndex))
            {
                _storyAuto = false;
                _storyAutoVersion++;
                return;
            }
            ShowStory(_storyIndex + 1);
        }
    }

    /// <summary>当前这一拍该配哪张场景图（`美术策划案/00 §5.2` 的场景表）。</summary>
    /// <remarks>
    /// 只有序章那三个节点有明确依据（正文第 9 / 61 / 81 行），其余拍复用最近的一张：
    /// 剧本是连续的一段路，走两步换一次背景反而跳。S0 是黑屏独白，**故意不配图**。
    /// </remarks>
    private static string BackgroundFor(int index)
    {
        string scene = index < Prologue.Length ? Prologue[index].Scene : "S9";
        return scene switch
        {
            "S0" => "",                              // 黑屏独白：不给背景才对
            "S1" => "site14-hill-dawn.png",
            "S2" or "S3" or "S4" or "S5" => "site14-hill-dawn.png",
            "S6" => "site14-hill-ridge.png",         // 两人朝山坡下走、远处露出设施群
            "S6B" => "site14-hill-ridge.png",        // 铁栅门段：场景图未交付，复用远眺
            "S7" => "battle-site14-hill.png",
            "S8" => "site14-canteen.png",
            _ => "site14-canteen.png",
        };
    }

    /// <summary>
    /// 剧情屏：**视觉小说式对话** —— 两侧立绘 + 压底的对话框。
    /// </summary>
    /// <remarks>
    /// 灵感取自蔚蓝档案那种"人物站两侧、对话框压底、名牌浮在框上沿"的排法，
    /// 但**布局与配色是自研的**，没有照搬它的色系与版式。三条硬约束决定了它的形状：
    /// <list type="number">
    ///   <item><b>底部要盖住场景图的水印。</b>场景图货源右下角带「豆包 AI 生成」的水印，
    ///         所以从屏幕底往上 560px 有一层逐渐加深的渐变，对话框再压在它上面 ——
    ///         水印正好落在渐变最深的那一段里，看不见。</item>
    ///   <item><b>人名与阵营分层</b>：名牌浮在对话框上沿，上写人名、下写阵营。
    ///         阵营不明 / 需要保密 → `？？？`；**明确暂无归属 → 整行不写**
    ///         （`00 §4`：阵营与"对谁生效"是两条独立的轴，不能拿"无阵营"当"敌对"）。</item>
    ///   <item><b>立绘分左 / 中 / 右三格</b>：守约人固定左、千语星遥固定右、
    ///         至暗长夜的剪影居中 —— 她此刻是 `？？？`，**不能按"同一个人"摆同一侧**
    ///         （`07 §4.7`：同框是事故）。</item>
    /// </list>
    /// </remarks>
    private void ShowStory(int index)
    {
        _storyIndex = Math.Clamp(index, 0, Prologue.Length);

        // 回到开头就清空立绘位，避免"重看序章时还挂着上一次的人"。
        if (_storyIndex == 0)
        {
            _storySlotLeft = "";
            _storySlotCenter = "";
            _storySlotRight = "";
        }

        Control root = new();

        // 背景层：**先申请**，`Swap` 时会铺到渐变之上、内容之下（见 `_bgLayer`）。
        // S0 是黑屏独白：`BackgroundFor` 返回空串 ⇒ 不挂场景图，改用一块近黑的底板压住全局底噪。
        string storyBg = BackgroundFor(_storyIndex);
        SetNextBackground(storyBg);
        if (string.IsNullOrEmpty(storyBg))
        {
            ColorRect black = new() { Color = new Color(0.01f, 0.015f, 0.025f, 0.97f) };
            black.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            black.MouseFilter = MouseFilterEnum.Ignore;
            root.AddChild(black);
        }

        // 立绘层：整屏、不吃鼠标（点击穿透到底下的对话框）。
        Control stage = new() { MouseFilter = MouseFilterEnum.Ignore };
        stage.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        root.AddChild(stage);

        if (_storyIndex < Prologue.Length)
        {
            (string scene, string speaker, string line, string portrait) = Prologue[_storyIndex];
            if (portrait.Length > 0) RememberStoryPortrait(portrait);

            // 「？？？」在 S5 之后归属千语星遥 ⇒ 剪影退场，免得两个人同框。
            if (SceneNumber(scene) >= 5) _storySlotCenter = "";
        }

        AddStoryPortrait(stage, _storySlotLeft, StorySlot.Left);
        AddStoryPortrait(stage, _storySlotCenter, StorySlot.Center);
        AddStoryPortrait(stage, _storySlotRight, StorySlot.Right);

        // 底部渐变：盖水印 + 把对话框从场景里"托"出来。
        root.AddChild(BottomShade(560f));

        if (_storyIndex < Prologue.Length)
        {
            (string scene, string speaker, string line, string portrait) = Prologue[_storyIndex];
            bool narration = speaker == "——";

            MarginContainer frame = new();
            frame.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            frame.AddThemeConstantOverride("margin_left", 88);
            frame.AddThemeConstantOverride("margin_right", 88);
            frame.AddThemeConstantOverride("margin_top", 0);
            frame.AddThemeConstantOverride("margin_bottom", 30);
            root.AddChild(frame);

            VBoxContainer stack = new() { Alignment = BoxContainer.AlignmentMode.End };
            stack.AddThemeConstantOverride("separation", 0);
            frame.AddChild(stack);

            if (!narration) stack.AddChild(Nameplate(speaker, FactionOf(speaker)));

            PanelContainer box = new() { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            box.AddThemeStyleboxOverride("panel", DialogStyle());

            VBoxContainer body = new();
            body.AddThemeConstantOverride("separation", 10);

            Label text = new()
            {
                // 性别只替换人称代词，台词本身一个字不改。
                Text = Pronoun(line),
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                CustomMinimumSize = new Vector2(0, 96),
                Modulate = narration ? new Color(1, 1, 1, 0.82f) : Colors.White,
                VerticalAlignment = VerticalAlignment.Top,
            };
            text.AddThemeFontSizeOverride("font_size", 22);
            body.AddChild(text);

            HBoxContainer bar = new() { Alignment = BoxContainer.AlignmentMode.End };
            bar.AddThemeConstantOverride("separation", 12);

            Button next = new() { Text = NextButtonText(), CustomMinimumSize = new Vector2(240, 48) };
            next.Pressed += OnStoryNext;
            bar.AddChild(next);
            body.AddChild(bar);

            Pad(box, body);
            stack.AddChild(box);

            // 自动播放与返回：放在对话框右上角外侧，不占对话框的正文宽度。
            HBoxContainer tools = new() { Alignment = BoxContainer.AlignmentMode.End };
            tools.AddThemeConstantOverride("separation", 10);

            Button auto = new()
            {
                Text = _storyAuto ? "自动中" : "自动",
                ToggleMode = true,
                ButtonPressed = _storyAuto,
                CustomMinimumSize = new Vector2(110, 40),
            };
            auto.Toggled += on =>
            {
                _storyAuto = on;
                _storyAutoVersion++;
                if (on) _ = AutoStoryAsync(_storyAutoVersion);
            };
            tools.AddChild(auto);

            Button home = new() { Text = "返回主界面", CustomMinimumSize = new Vector2(150, 40) };
            home.Pressed += ShowHome;
            tools.AddChild(home);

            // 场景编号只给开发看：走查时能对上 `04 §3`，玩家界面不放。
            Label sceneTag = new()
            {
                Text = scene,
                Modulate = new Color(1, 1, 1, 0.35f),
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                VerticalAlignment = VerticalAlignment.Center,
            };
            sceneTag.AddThemeFontSizeOverride("font_size", 13);
            tools.AddChild(sceneTag);
            body.AddChild(tools);
        }

        Swap(root);
        StatusLine();
    }

    // ── 数据驱动的剧情（第一章 / 幕间） ─────────────────────────────
    //
    // 序章是内置的 96 拍数组（带 S0~S9 场景号与教学战跳转）；
    // 第一章 / 幕间走 Assets/Story/*.story.json，四类拍：
    //   say 台词（带人名与阵营）· act 演出/旁白 · note 系统提示/档案 · battle 战斗节点。

    private StoryScript? _script;
    private int _scriptSection;
    private int _scriptBeat;
    private string _scriptScene = "";

    /// <summary>故事列表里的一个章节入口（脚本读不到就置灰）。</summary>
    private void AddScriptEntry(VBoxContainer root, string id, string title)
    {
        StoryScript? script = StoryScript.Load(id);
        bool ok = script is not null && script.Sections.Count > 0;
        Button entry = new()
        {
            Text = ok ? $"{title}　（{script!.Sections.Count} 节 · {script.BeatCount} 拍）" : $"{title}　（待落盘）",
            CustomMinimumSize = new Vector2(0, 56),
            Disabled = !ok,
        };
        if (ok) entry.Pressed += () => StartScript(script!);
        root.AddChild(entry);
    }

    private void StartScript(StoryScript script)
    {
        _script = script;
        _scriptSection = 0;
        _scriptBeat = 0;
        _scriptScene = script.Sections[0].Scene ?? "";
        _storySlotLeft = _storySlotCenter = _storySlotRight = "";
        ShowScriptBeat();
    }

    private void ShowScriptBeat()
    {
        if (_script is null) { ShowStoryList(); return; }
        if (_scriptSection >= _script.Sections.Count) { EndScript(); return; }

        StorySection section = _script.Sections[_scriptSection];
        if (_scriptBeat >= section.Beats.Count)
        {
            _scriptSection++;
            _scriptBeat = 0;
            if (_scriptSection >= _script.Sections.Count) { EndScript(); return; }
            section = _script.Sections[_scriptSection];
            _scriptScene = section.Scene ?? _scriptScene;
        }

        StoryBeat beat = section.Beats[_scriptBeat];
        if (!string.IsNullOrEmpty(beat.Scene)) _scriptScene = beat.Scene!;

        Control root = new();
        SetNextBackground(string.IsNullOrEmpty(_scriptScene) ? "" : _scriptScene + ".png");

        Control stage = new() { MouseFilter = MouseFilterEnum.Ignore };
        stage.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        root.AddChild(stage);

        if (beat.Kind == "say" && !string.IsNullOrEmpty(beat.Portrait)) RememberStoryPortrait(beat.Portrait!);
        AddStoryPortrait(stage, _storySlotLeft, StorySlot.Left);
        AddStoryPortrait(stage, _storySlotCenter, StorySlot.Center);
        AddStoryPortrait(stage, _storySlotRight, StorySlot.Right);

        root.AddChild(BottomShade(560f));

        MarginContainer frame = new();
        frame.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        frame.AddThemeConstantOverride("margin_left", 88);
        frame.AddThemeConstantOverride("margin_right", 88);
        frame.AddThemeConstantOverride("margin_top", 0);
        frame.AddThemeConstantOverride("margin_bottom", 30);
        root.AddChild(frame);

        VBoxContainer stack = new() { Alignment = BoxContainer.AlignmentMode.End };
        stack.AddThemeConstantOverride("separation", 0);
        frame.AddChild(stack);

        bool isSay = beat.Kind == "say";
        bool isNote = beat.Kind == "note";
        bool isBattle = beat.Kind == "battle";

        if (isSay)
        {
            string faction = beat.Faction ?? FactionOf(beat.Speaker ?? "");
            stack.AddChild(Nameplate(beat.Speaker ?? "", faction));
        }
        else if (isNote) stack.AddChild(Nameplate("档案 · 系统", ""));
        else if (isBattle) stack.AddChild(Nameplate("战斗节点", ""));

        PanelContainer box = new() { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        box.AddThemeStyleboxOverride("panel", DialogStyle());

        VBoxContainer body = new();
        body.AddThemeConstantOverride("separation", 10);

        string line = beat.Text ?? "";
        if (isNote && beat.Table is { Count: > 0 })
        {
            line = string.Join("\n", beat.Table.Select(static row => string.Join(" ｜ ", row)));
        }
        if (isBattle) line += "\n（战斗关卡数据待建，先按剧情推进）";

        Label text = new()
        {
            // 章节脚本默认不做"他→她"替换（见 StoryScript.Pronoun 的说明）。
            Text = isSay && _script is { Pronoun: true } ? Pronoun(line) : line,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(0, isNote ? 120 : 96),
            Modulate = isSay ? Colors.White : new Color(0.86f, 0.90f, 1f, 0.88f),
            VerticalAlignment = VerticalAlignment.Top,
        };
        text.AddThemeFontSizeOverride("font_size", isNote ? 18 : 22);
        body.AddChild(text);

        HBoxContainer bar = new() { Alignment = BoxContainer.AlignmentMode.End };
        bar.AddThemeConstantOverride("separation", 12);

        bool last = _scriptSection == _script.Sections.Count - 1
                    && _scriptBeat == section.Beats.Count - 1;
        Button next = new()
        {
            Text = last ? "结束本章" : "继续",
            CustomMinimumSize = new Vector2(220, 48),
        };
        next.Pressed += AdvanceScript;
        bar.AddChild(next);
        body.AddChild(bar);

        HBoxContainer tools = new() { Alignment = BoxContainer.AlignmentMode.End };
        tools.AddThemeConstantOverride("separation", 10);

        Label sectionTag = new()
        {
            Text = $"{section.Title}　·　{_scriptBeat + 1}/{section.Beats.Count}",
            Modulate = new Color(1, 1, 1, 0.4f),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            VerticalAlignment = VerticalAlignment.Center,
        };
        sectionTag.AddThemeFontSizeOverride("font_size", 13);
        tools.AddChild(sectionTag);

        Button back = new() { Text = "返回故事列表", CustomMinimumSize = new Vector2(150, 40) };
        back.Pressed += ShowStoryList;
        tools.AddChild(back);
        body.AddChild(tools);

        Pad(box, body);
        stack.AddChild(box);

        Swap(root);
        StatusLine();
    }

    private void AdvanceScript()
    {
        if (_script is null) return;
        _scriptBeat++;
        ShowScriptBeat();
    }

    private void EndScript()
    {
        _toast = $"{_script?.Title} 已读到最后一拍（战斗与章节结算待接）。";
        _script = null;
        ShowStoryList();
    }

    // ── 剧情屏的零件 ─────────────────────────────────────────────────

    private enum StorySlot { Left, Center, Right }

    private string _storySlotLeft = "";
    private string _storySlotCenter = "";
    private string _storySlotRight = "";

    /// <summary>`S6B` → 6。场景编号只用来判断"剪影该不该还在场"。</summary>
    private static int SceneNumber(string scene)
    {
        int n = 0;
        foreach (char c in scene)
        {
            if (char.IsDigit(c)) n = n * 10 + (c - '0');
            else if (n > 0) break;
        }
        return n;
    }

    /// <summary>把这一拍的立绘记进它自己的格子（守约人左 / 剪影中 / 其他人右）。</summary>
    private void RememberStoryPortrait(string portrait)
    {
        string file = ResolveStoryPortrait(portrait);
        switch (portrait)
        {
            case "protagonist": _storySlotLeft = file; break;
            case "silhouette": _storySlotCenter = file; break;
            default: _storySlotRight = file; break;
        }
    }

    private string ResolveStoryPortrait(string key) => key switch
    {
        "protagonist" => Portraits.ProtagonistFile(_save.Profile.ProtagonistGender),
        "xingyao" => "xingyao-base.png",
        "darknight" => "darknight-base.png",
        "silhouette" => Portraits.DarknightSilhouette,
        _ => key,
    };

    private static void AddStoryPortrait(Control stage, string file, StorySlot slot)
    {
        if (string.IsNullOrEmpty(file)) return;

        // 立绘按 2:3 出图；框给足高度，底边压到屏幕外 48px，让人物"站"在对话框后面。
        const float w = 460f, h = 690f, bleed = 48f;
        TextureRect? tex = Portraits.Tile(file, w, h);
        if (tex is null) return;

        tex.MouseFilter = MouseFilterEnum.Ignore;
        switch (slot)
        {
            case StorySlot.Left:
                tex.AnchorLeft = 0; tex.AnchorRight = 0;
                tex.OffsetLeft = 56; tex.OffsetRight = 56 + w;
                break;
            case StorySlot.Center:
                tex.AnchorLeft = 0.5f; tex.AnchorRight = 0.5f;
                tex.OffsetLeft = -w / 2; tex.OffsetRight = w / 2;
                break;
            default:
                tex.AnchorLeft = 1; tex.AnchorRight = 1;
                tex.OffsetLeft = -(56 + w); tex.OffsetRight = -56;
                break;
        }
        tex.AnchorTop = 1; tex.AnchorBottom = 1;
        tex.OffsetTop = bleed - h;
        tex.OffsetBottom = bleed;

        // 未在说话的人压暗一点，一眼能看出是谁在说。
        stage.AddChild(tex);
    }

    /// <summary>名牌：上写人名（大字）、下写阵营（小字，可用 `？？？` 或留空）。</summary>
    private static Control Nameplate(string name, string faction)
    {
        PanelContainer plate = new() { SizeFlagsHorizontal = SizeFlags.ShrinkBegin };
        StyleBoxFlat style = new()
        {
            BgColor = Color.FromHtml("#16233a"),
            BorderColor = Color.FromHtml("#6f9bdd"),
            BorderWidthLeft = 0,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 0,
            CornerRadiusTopLeft = 12,
            CornerRadiusTopRight = 12,
            ContentMarginLeft = 20,
            ContentMarginTop = 8,
            ContentMarginRight = 22,
            ContentMarginBottom = 8,
        };
        plate.AddThemeStyleboxOverride("panel", style);

        VBoxContainer box = new();
        box.AddThemeConstantOverride("separation", 0);

        Label who = new() { Text = name };
        who.AddThemeFontSizeOverride("font_size", 24);
        box.AddChild(who);

        if (!string.IsNullOrEmpty(faction))
        {
            Label camp = new()
            {
                Text = faction,
                Modulate = new Color(0.72f, 0.84f, 1f, 0.9f),
            };
            camp.AddThemeFontSizeOverride("font_size", 13);
            box.AddChild(camp);
        }

        plate.AddChild(box);
        return plate;
    }

    /// <summary>
    /// 派系名（**进对话框的阵营行**）。规则来自 `00 §4` 与作者 2026-11-24 的口径：
    /// 不明 / 保密 → `？？？`；明确暂无归属 → 空字符串（整行不写）。
    /// </summary>
    private static string FactionOf(string speaker) => speaker switch
    {
        "？？？" => "？？？",
        "千语星遥" => "CCRF · SITE 14",
        "警卫队长" or "冷静的设施警卫" or "警惕的设施警卫队长" => "CCRF · SITE 14",
        "警卫队长、警卫队员" => "CCRF · SITE 14",
        _ => "",     // 守约人在序章尚未归属任何势力 ⇒ 不写阵营
    };

    /// <summary>大厅左侧的横向压暗：卡片列压在它上面，右侧的景与人保持亮。</summary>
    private static Control SideScrim()
    {
        Gradient ramp = new()
        {
            Colors =
            [
                new Color(0.020f, 0.032f, 0.056f, 0.86f),
                new Color(0.020f, 0.032f, 0.056f, 0.58f),
                new Color(0.020f, 0.032f, 0.056f, 0f),
            ],
            Offsets = [0f, 0.42f, 0.78f],
        };
        GradientTexture2D tex = new()
        {
            Gradient = ramp,
            Width = 256,
            Height = 8,
            FillFrom = new Vector2(0, 0),
            FillTo = new Vector2(1, 0),
        };
        TextureRect scrim = new()
        {
            Texture = tex,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        scrim.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        return scrim;
    }

    /// <summary>从屏幕底往上逐渐加深的渐变（盖水印 + 托住对话框）。</summary>
    private static Control BottomShade(float height)
    {
        Gradient ramp = new()
        {
            Colors =
            [
                new Color(0.024f, 0.040f, 0.070f, 0f),
                new Color(0.024f, 0.040f, 0.070f, 0.55f),
                new Color(0.024f, 0.040f, 0.070f, 0.80f),
                new Color(0.020f, 0.032f, 0.058f, 0.97f),
            ],
            Offsets = [0f, 0.34f, 0.62f, 1f],
        };
        GradientTexture2D tex = new()
        {
            Gradient = ramp,
            Width = 8,
            Height = 128,
            FillFrom = new Vector2(0, 0),
            FillTo = new Vector2(0, 1),
        };

        TextureRect shade = new()
        {
            Texture = tex,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        shade.AnchorLeft = 0; shade.AnchorRight = 1;
        shade.AnchorTop = 1; shade.AnchorBottom = 1;
        shade.OffsetLeft = 0; shade.OffsetRight = 0;
        shade.OffsetTop = -height; shade.OffsetBottom = 0;
        return shade;
    }

    /// <summary>对话框的底板：半透明深色 + 上缘一道冷光边。</summary>
    private static StyleBoxFlat DialogStyle()
    {
        StyleBoxFlat box = new()
        {
            // 0.97：背景是明亮的晨雾照，0.9 上下会让白字发灰。
            BgColor = new Color(0.043f, 0.070f, 0.118f, 0.97f),
            BorderColor = new Color(0.66f, 0.81f, 1f, 0.55f),
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            CornerRadiusTopLeft = 6,
            CornerRadiusTopRight = 14,
            CornerRadiusBottomLeft = 14,
            CornerRadiusBottomRight = 14,
            ContentMarginLeft = 30,
            ContentMarginTop = 22,
            ContentMarginRight = 30,
            ContentMarginBottom = 20,
        };
        return box;
    }

    /// <summary>当前这一拍按下去会发生什么：**进入战斗 / 完成序章 / 下一句**。</summary>
    private void OnStoryNext()
    {
        _storyAuto = false;
        _storyAutoVersion++;
        if (_storyIndex >= Prologue.Length - 1)
        {
            _ = FinishPrologueAsync();
            return;
        }

        // ⚠️ 之前漏的就是这一跳：走到 S7 的最后一拍之后要**进战斗**，而不是继续翻页。
        if (IsLastBattleBeat(_storyIndex))
        {
            ShowBattle("stage-main-s7", tutorial: true, fromStory: true);
            return;
        }

        ShowStory(_storyIndex + 1);
    }

    private static bool IsLastBattleBeat(int index)
        => string.Equals(Prologue[index].Scene, BattleScene, StringComparison.Ordinal)
           && (index + 1 >= Prologue.Length
               || !string.Equals(Prologue[index + 1].Scene, BattleScene, StringComparison.Ordinal));

    /// <summary>
    /// 下一步按钮的文案。
    /// </summary>
    /// <remarks>
    /// 判据是"**当前这一拍**是不是 S7 的最后一拍" —— 之前的写法看的是"下一拍是不是 S7"，
    /// 于是按钮在**进战斗之前那一拍**就写着"进入战斗"，按下去却还在翻页。
    /// </remarks>
    private string NextButtonText()
    {
        if (_storyIndex >= Prologue.Length - 1) return "完成序章";
        if (IsLastBattleBeat(_storyIndex)) return "进入战斗";
        return "继续";
    }

    /// <summary>序章走完：上报首通（奖励由服务端发），然后**进主界面**。</summary>
    /// <remarks>
    /// 没有"离线也记一笔"的分支了 —— 本作没有离线模式，上报失败就是失败，
    /// 界面会照实说，玩家重进一次序章再走完即可。
    /// </remarks>
    private async Task FinishPrologueAsync()
    {
        ShowBusy("正在结算序章…");
        ApiClient.Reply reply = await _api.ReportStoryClearAsync("prologue");
        await ReloadSaveAsync();

        _toast = reply.Ok ? "序章完成 · " + reply.Message : "序章上报被拒：" + reply.Message;
        ShowHome();
    }

    /// <summary>
    /// 人称代词替换：**只换第三人称的「他」**，台词内容一个字不改。
    /// </summary>
    private string Pronoun(string line)
        => string.Equals(_save.Profile.ProtagonistGender, "female", StringComparison.OrdinalIgnoreCase)
            ? System.Text.RegularExpressions.Regex.Replace(line, "(?<!其)他(?!们)", "她")
            : line;


    // ── 异常收容图鉴（M6） ───────────────────────────────────────────

    /// <summary>
    /// 图鉴：**数据全部来自 `data/enemies/`**，客户端不另存一份副本。
    /// </summary>
    /// <remarks>
    /// `06 §2.0` 的静态数据归属：`data/` 是 `WaningOath.Core` 的共享静态数据目录，
    /// 客户端随游戏一起分发、直接读本地。所以图鉴**不需要任何接口** ——
    /// 原 `GET /api/game/data/characters` 因此作废。
    /// <para>
    /// 权限编码（`ccrfCode`）、档位（`tier`）、类别（`category` · `01 §7` / `100` #87）
    /// 都是**敌人数据里已有的字段**；减伤率走 `01 §2.2` ③ 的同一条公式。
    /// **没有虚构任何档案内容。**
    /// </para>
    /// </remarks>
    /// <summary>仓库：商城材料、关卡掉落、剧情道具统一在这里看。</summary>
    private void ShowInventory()
    {
        VBoxContainer root = new();
        root.AddThemeConstantOverride("separation", 10);

        Label title = new() { Text = "仓库" };
        title.AddThemeFontSizeOverride("font_size", 28);
        root.AddChild(title);
        root.AddChild(new Label
        {
            Text = $"共 {_save.Inventory.Count} 种材料 / 道具",
            Modulate = new Color(1, 1, 1, 0.6f),
        });
        int equippedGears = _save.Gears.Count(static g => g.EquippedBy is not null);
        root.AddChild(new Label
        {
            Text = $"装备：{_save.Gears.Count} 件 · 已装备 {equippedGears} 件",
            Modulate = new Color(1, 1, 1, 0.6f),
        });
        int equippedWeapons = _save.Weapons.Values.Count(static w => w.EquippedBy is not null);
        root.AddChild(new Label
        {
            Text = $"武器：{_save.Weapons.Count} 把 · 已装备 {equippedWeapons} 把",
            Modulate = new Color(1, 1, 1, 0.6f),
        });

        HBoxContainer filters = new();
        filters.AddThemeConstantOverride("separation", 6);
        filters.AddChild(new Label { Text = "筛选" });
        foreach (string kind in new[] { "", "突破", "技能", "经验" })
        {
            string captured = kind;
            Button filter = new()
            {
                Text = captured.Length == 0 ? "全部" : captured,
                ToggleMode = true,
                ButtonPressed = _inventoryFilter == captured,
                CustomMinimumSize = new Vector2(72, 32),
            };
            filter.Pressed += () =>
            {
                _inventoryFilter = captured;
                ShowInventory();
            };
            filters.AddChild(filter);
        }
        root.AddChild(filters);

        ScrollContainer scroll = new()
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        VBoxContainer list = new() { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        list.AddThemeConstantOverride("separation", 4);
        if (_save.Inventory.Count == 0 && _save.Gears.Count == 0 && _save.Weapons.Count == 0)
        {
            list.AddChild(Line("仓库是空的。去清剿、商店或剧情里拿点东西。"));
        }
        else
        {
            bool wroteMaterial = false;
            foreach ((string id, int count) in _save.Inventory.OrderBy(static kv => kv.Key, StringComparer.Ordinal))
            {
                if (_inventoryFilter.Length > 0
                    && (!_data.Growth.Items.TryGetValue(id, out ItemDef? filterItem)
                        || !string.Equals(ItemKindZh(filterItem.Kind), _inventoryFilter, StringComparison.Ordinal)))
                    continue;
                HBoxContainer row = new();
                Label name = new()
                {
                    Text = _data.Growth.ItemName(id),
                    SizeFlagsHorizontal = SizeFlags.ExpandFill,
                };
                row.AddChild(name);
                if (_data.Growth.Items.TryGetValue(id, out ItemDef? item) && item.Kind.Length > 0)
                {
                    Label kindLabel = new()
                    {
                        Text = ItemKindZh(item.Kind),
                        Modulate = new Color(1, 1, 1, 0.45f),
                    };
                    kindLabel.AddThemeFontSizeOverride("font_size", 12);
                    row.AddChild(kindLabel);
                }
                row.AddChild(new Label { Text = $"×{count:N0}", Modulate = new Color(1, 1, 1, 0.75f) });
                list.AddChild(row);
                wroteMaterial = true;
            }
            if (!wroteMaterial && _inventoryFilter.Length > 0)
                list.AddChild(Line("该分类暂无材料。"));
        }
        if (_save.Gears.Count > 0)
        {
            list.AddChild(new Label
            {
                Text = "装备明细",
                Modulate = new Color(1, 1, 1, 0.55f),
            });
            foreach (PlayerSave.GearState g in _save.Gears
                         .OrderByDescending(static g => g.Level)
                         .ThenBy(static g => g.Uid, StringComparer.Ordinal))
            {
                string set = _data.GearSets.TryGetValue(g.Set, out GearSetDef? gs) ? gs.Name : g.Set;
                string part = _data.Gear.Slot(g.Part)?.Name ?? g.Part;
                string ownerName = g.EquippedBy is null
                    ? "未装备"
                    : _data.Characters.TryGetValue(g.EquippedBy, out CharacterDef? ownerDef) ? ownerDef.Name : g.EquippedBy;
                list.AddChild(OwnerRow($"{set} · {part} +{g.Level}　{g.MainStat}　→ {ownerName}", g.EquippedBy));
            }
        }
        if (_save.Weapons.Count > 0)
        {
            list.AddChild(new Label
            {
                Text = "武器明细",
                Modulate = new Color(1, 1, 1, 0.55f),
            });
            foreach ((string weaponId, PlayerSave.WeaponState w) in _save.Weapons
                         .OrderByDescending(kv => _data.Weapons.TryGetValue(kv.Key, out WeaponDef? wd) ? wd.Rarity : 0)
                         .ThenByDescending(static kv => kv.Value.Level)
                         .ThenBy(static kv => kv.Key, StringComparer.Ordinal))
            {
                string name = _data.Weapons.TryGetValue(weaponId, out WeaponDef? def) ? def.Name : weaponId;
                int rarity = def?.Rarity ?? 0;
                string ownerName = w.EquippedBy is null
                    ? "未装备"
                    : _data.Characters.TryGetValue(w.EquippedBy, out CharacterDef? ownerDef) ? ownerDef.Name : w.EquippedBy;
                list.AddChild(OwnerRow($"{name}（{rarity}★） +{w.Level} · 覆写 {w.Overwrite}　→ {ownerName}", w.EquippedBy));
            }
        }
        scroll.AddChild(list);
        root.AddChild(scroll);

        root.AddChild(Wrap(BackButton(ShowHome)));
        Swap(root);
        StatusLine();
    }

    private void ShowCodex()
    {
        VBoxContainer root = new();
        root.AddThemeConstantOverride("separation", 10);

        int codexCount = _codexTier.Length == 0
            ? _data.Enemies.Count
            : _data.Enemies.Values.Count(e => string.Equals(e.Tier, _codexTier, StringComparison.Ordinal));
        Label title = new() { Text = $"异常收容图鉴（{codexCount} 条）" };
        title.AddThemeFontSizeOverride("font_size", 26);
        root.AddChild(title);

        HBoxContainer tierFilters = new();
        tierFilters.AddThemeConstantOverride("separation", 6);
        foreach ((string key, string text) in new[]
                 {
                     ("", "全部"), ("minion", "杂兵"), ("normal", "普通"),
                     ("elite", "精英"), ("boss", "强敌"), ("highboss", "首领"),
                 })
        {
            string captured = key;
            Button filter = new()
            {
                Text = text,
                ToggleMode = true,
                ButtonPressed = _codexTier == captured,
                CustomMinimumSize = new Vector2(72, 32),
            };
            filter.Pressed += () =>
            {
                _codexTier = captured;
                ShowCodex();
            };
            tierFilters.AddChild(filter);
        }
        root.AddChild(tierFilters);

        ScrollContainer scroll = new() { SizeFlagsVertical = SizeFlags.ExpandFill };
        VBoxContainer list = new() { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        scroll.AddChild(list);
        root.AddChild(scroll);

        bool wroteCodex = false;
        foreach (EnemyDef e in _data.Enemies.Values.OrderBy(static x => x.Id, StringComparer.Ordinal))
        {
            if (_codexTier.Length > 0 && !string.Equals(e.Tier, _codexTier, StringComparison.Ordinal)) continue;
            PanelContainer card = new() { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            MarginContainer pad = new();
            pad.AddThemeConstantOverride("margin_left", 14);
            pad.AddThemeConstantOverride("margin_right", 14);
            pad.AddThemeConstantOverride("margin_top", 10);
            pad.AddThemeConstantOverride("margin_bottom", 10);
            card.AddChild(pad);

            VBoxContainer box = new();
            box.AddThemeConstantOverride("separation", 4);
            pad.AddChild(box);

            Label name = new()
            {
                Text = $"{e.Name}　{e.CcrfCode}　[{TierName(e.Tier)} · Lv{e.Level} · {CategoryName(e.Category)}]",
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
            };
            name.AddThemeFontSizeOverride("font_size", 19);
            box.AddChild(name);

            Label stats = new()
            {
                Text = $"生命 {e.Base.Hp:N0}　防御 {e.Base.Def:N0}（减伤 {Reduction(e.Base.Def):P1}）　" +
                       $"攻击 {e.Base.Atk:N0}　速度 {e.Base.Speed:0}",
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
            };
            box.AddChild(stats);
            if (e.ResistAll != 0 || e.Resist.Count > 0)
            {
                List<string> resistParts = [];
                if (e.ResistAll != 0) resistParts.Add($"全 {e.ResistAll:P0}");
                foreach ((string key, double value) in e.Resist.OrderBy(static kv => kv.Key, StringComparer.Ordinal))
                {
                    if (Enum.TryParse(key, true, out WaningOath.Core.Element element))
                        resistParts.Add($"{ElementZh(element)} {value:P0}");
                }
                if (resistParts.Count > 0)
                {
                    box.AddChild(new Label
                    {
                        Text = "抗性：" + string.Join(" · ", resistParts),
                        AutowrapMode = TextServer.AutowrapMode.WordSmart,
                        Modulate = new Color(1, 1, 1, 0.65f),
                    });
                }
            }
            if (e.AnomalyResist.Count > 0)
            {
                box.AddChild(new Label
                {
                    Text = "异常抗性：" + string.Join(" · ", e.AnomalyResist
                        .OrderBy(static kv => kv.Key, StringComparer.Ordinal)
                        .Select(static kv => $"{kv.Key} {kv.Value:P0}")),
                    AutowrapMode = TextServer.AutowrapMode.WordSmart,
                    Modulate = new Color(1, 1, 1, 0.6f),
                });
            }

            string skills = string.Join(" · ", e.Skills.Select(static s => s.Name));
            if (skills.Length > 0)
            {
                Label s = new() { Text = "技能：" + skills, AutowrapMode = TextServer.AutowrapMode.WordSmart };
                s.TooltipText = string.Join("\n\n", e.Skills
                    .Where(static skill => !string.IsNullOrWhiteSpace(skill.Description))
                    .Select(static skill => $"{skill.Name}：{skill.Description}"));
                box.AddChild(s);
            }
            list.AddChild(card);
            wroteCodex = true;
        }
        if (!wroteCodex)
            list.AddChild(new Label { Text = "该等级暂无档案。", Modulate = new Color(1, 1, 1, 0.5f) });

        Button back = new() { Text = "返回主界面", CustomMinimumSize = new Vector2(240, 44) };
        back.Pressed += ShowHome;
        root.AddChild(Wrap(back));

        Swap(root);
        StatusLine();
    }

    private static string TierName(string tier) => tier switch
    {
        "minion" => "杂兵",
        "normal" => "普通",
        "elite" => "精英",
        "boss" => "强敌",
        "highboss" => "首领",
        _ => tier,
    };

    private static string CategoryName(string category) => category switch
    {
        "human" => "人形",
        "machine" => "机械",
        _ => "异常",
    };

    /// <summary>减伤率 = `D / (D + DEF_K)`（`01 §2.2` ③）。**界面也走同一条公式**，不另写一份。</summary>
    private static double Reduction(double def) => def / (def + BattleConstants.DefK);

    // ── 权限协议 · 养成（M7） ────────────────────────────────────────

    /// <summary>
    /// 角色详情：**立绘 + 衣装 + 面板 + 养成**都在这一屏。
    /// </summary>
    /// <remarks>
    /// 这一屏原来叫"权限协议 · 养成"，只能养成主角一个人，而且从标题页直接进 ——
    /// 现在是"角色 → 选人 → 详情"，养成跟着角色走。
    /// <para>
    /// <b>规则全部来自 <see cref="GrowthRules"/> 与 <see cref="PanelCalculator"/></b>，
    /// 与服务端结算用的是同一套，所以"界面上显示的还差多少"与"服务端要扣的东西"不可能对不上。
    /// </para>
    /// <para>
    /// ⚠️ **衣装只决定这一屏显示哪张立绘**。主线剧情里固定显示原皮、活动剧情里显示剧情指定的那一套，
    /// 所以这里选了什么，不会改变正式流程里的立绘（`04 §3.2` 的口径）。
    /// </para>
    /// </remarks>
    private void ShowCharacterDetail(string characterId)
    {
        if (!_save.Characters.TryGetValue(characterId, out PlayerSave.CharacterState? st))
        {
            _toast = "这个角色还没获得。";
            ShowCharacters();
            return;
        }

        CharacterDef def = _data.Character(characterId);

        VBoxContainer root = new();
        root.AddThemeConstantOverride("separation", 10);

        HBoxContainer top = new();
        Label title = new() { Text = def.Name, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        title.AddThemeFontSizeOverride("font_size", 28);
        top.AddChild(title);
        top.AddChild(BackButton(ShowCharacters));
        root.AddChild(top);

        // ── 左：立绘 + 衣装 ──
        HBoxContainer body = new() { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 18);

        VBoxContainer left = new();
        left.AddThemeConstantOverride("separation", 8);
        System.Collections.Generic.IReadOnlyList<Portraits.SkinDef> skins =
            Portraits.SkinsFor(characterId, _save.Profile.ProtagonistGender);

        string? currentSkin = _save.Profile.Skins.GetValueOrDefault(characterId);
        TextureRect? portrait = Portraits.Tile(
            Portraits.FileForSkin(characterId, _save.Profile.ProtagonistGender, currentSkin) ?? "", 260f, 420f);
        if (portrait is not null) left.AddChild(portrait);

        if (skins.Count > 0)
        {
            OptionButton picker = new() { CustomMinimumSize = new Vector2(260, 36) };
            foreach (Portraits.SkinDef skin in skins) picker.AddItem(skin.Name);
            for (int i = 0; i < skins.Count; i++)
            {
                if (string.Equals(skins[i].Id, currentSkin ?? "base", StringComparison.Ordinal)) picker.Selected = i;
            }
            picker.ItemSelected += index =>
            {
                // **衣装选择也要过服务端**：客户端不留自己的进度副本，
                // 所以"选了但没保存"这种事不该存在（哪怕它现在并不影响主线立绘）。
                _ = SetSkinAsync(characterId, skins[(int)index].Id);
            };
            left.AddChild(picker);

            Label rule = new()
            {
                Text = "衣装在主线剧情里不生效（活动剧情会指定并免费试用）",
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                CustomMinimumSize = new Vector2(260, 0),
                Modulate = new Color(1, 1, 1, 0.45f),
            };
            rule.AddThemeFontSizeOverride("font_size", 12);
            left.AddChild(rule);
        }
        body.AddChild(left);

        // ── 右：面板与养成 ──
        VBoxContainer right = new() { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        right.AddThemeConstantOverride("separation", 8);

        Loadout loadout = new(
            Level: st.Level,
            Breakthrough: st.Breakthrough,
            Eidolon: st.Eidolon,
            WeaponId: st.Weapon,
            WeaponLevel: st.Weapon is not null && _save.Weapons.TryGetValue(st.Weapon, out PlayerSave.WeaponState? ws) ? ws.Level : 1,
            WeaponOverwrite: st.Weapon is not null && _save.Weapons.TryGetValue(st.Weapon, out PlayerSave.WeaponState? ws2) ? ws2.Overwrite : 1,
            Gears: [.. st.Gears
                .Select(uid => _save.Gears.FirstOrDefault(g => g.Uid == uid))
                .Where(static g => g is not null)
                .Select(static g => new GearPiece(g!.Uid, g.Set, g.Part, g.Level, g.MainStat,
                    [.. g.SubStats.Select(static s => (s.Stat, s.Value))]))]);
        PanelResult panel = PanelCalculator.Compute(_data, def, loadout);

        Label stats = new()
        {
            Text = $"等级 {st.Level}/{_data.Growth.Level.Cap}　突破 {st.Breakthrough} 次　权阶 {st.Eidolon}\n" +
                   $"{panel}\n" +
                   $"武器：{(_save.Weapons.TryGetValue(st.Weapon ?? "", out PlayerSave.WeaponState? w0) ? $"{st.Weapon} +{w0.Level} · 覆写 {w0.Overwrite}" : "（未装备）")}　" +
                   $"装备：{st.Gears.Count}/6 件",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        right.AddChild(stats);

        // 武器切换：只列“类型契合且已拥有”的武器；gacha 出的武器在这里才真正用得上。
        List<string?> weaponIds = [null];
        List<string> weaponLabels = ["不装备"];
        foreach ((string wid, PlayerSave.WeaponState w) in _save.Weapons.OrderBy(static kv => kv.Key, StringComparer.Ordinal))
        {
            if (!_data.Weapons.TryGetValue(wid, out WeaponDef? wdef)) continue;
            if (!string.Equals(wdef.WeaponType, def.WeaponType, StringComparison.OrdinalIgnoreCase)) continue;
            weaponIds.Add(wid);
            weaponLabels.Add($"{wdef.Name} +{w.Level}");
        }
        HBoxContainer weaponRow = new();
        weaponRow.AddThemeConstantOverride("separation", 8);
        weaponRow.AddChild(new Label { Text = "切换武器" });
        OptionButton weaponPicker = new() { CustomMinimumSize = new Vector2(270, 36), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        foreach (string label in weaponLabels) weaponPicker.AddItem(label);
        int currentWeapon = st.Weapon is not null ? weaponIds.IndexOf(st.Weapon) : 0;
        weaponPicker.Selected = Math.Max(0, currentWeapon);
        weaponPicker.ItemSelected += index =>
        {
            string? chosen = weaponIds[(int)index];
            _ = EquipWeaponAsync(characterId, chosen);
        };
        weaponRow.AddChild(weaponPicker);
        PlayerSave.WeaponState? weaponState = st.Weapon is not null &&
            _save.Weapons.TryGetValue(st.Weapon, out PlayerSave.WeaponState? equipped) ? equipped : null;
        Button weaponUp = new()
        {
            Text = "+1",
            CustomMinimumSize = new Vector2(48, 36),
            Disabled = weaponState is null || weaponState.Level >= 80,
        };
        if (weaponState is not null && weaponState.Level < 80)
        {
            string weaponId = st.Weapon!;
            int nextLevel = weaponState.Level + 1;
            weaponUp.Pressed += () => _ = WeaponUpAsync(characterId, weaponId, nextLevel);
        }
        weaponRow.AddChild(weaponUp);
        if (weaponState is not null && st.Weapon is not null
            && _data.Weapons.TryGetValue(st.Weapon, out WeaponDef? currentWeaponDef)
            && currentWeaponDef.Rarity >= 5 && weaponState.Overwrite < 5)
        {
            string weaponId = st.Weapon;
            Button overwrite = new() { Text = "覆写 +1", CustomMinimumSize = new Vector2(88, 36) };
            overwrite.Pressed += () => _ = ExchangeForWeaponAsync(characterId, weaponId);
            weaponRow.AddChild(overwrite);
        }
        right.AddChild(weaponRow);

        // 装备：按部位列出“未装备 + 本角色可用的散件”；同部位替换由服务端处理。
        Label gearTitle = new() { Text = "装备", Modulate = new Color(1, 1, 1, 0.7f) };
        right.AddChild(gearTitle);
        GridContainer gearGrid = new() { Columns = 2, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        gearGrid.AddThemeConstantOverride("h_separation", 8);
        gearGrid.AddThemeConstantOverride("v_separation", 6);
        foreach (GearSlotDef slot in _data.Gear.Slots.OrderBy(static s => s.Order))
        {
            PlayerSave.GearState? current = _save.Gears.FirstOrDefault(g =>
                string.Equals(g.EquippedBy, characterId, StringComparison.Ordinal) &&
                string.Equals(g.Part, slot.Key, StringComparison.Ordinal));

            List<string?> gearIds = [null];
            List<string> gearLabels = ["不装备"];
            foreach (PlayerSave.GearState g in _save.Gears
                         .Where(g => string.Equals(g.Part, slot.Key, StringComparison.Ordinal))
                         .Where(g => g.EquippedBy is null || string.Equals(g.EquippedBy, characterId, StringComparison.Ordinal))
                         .OrderBy(static g => g.Uid, StringComparer.Ordinal))
            {
                gearIds.Add(g.Uid);
                string set = _data.GearSets.TryGetValue(g.Set, out GearSetDef? gs) ? gs.Name : g.Set;
                gearLabels.Add($"{set} +{g.Level}" + (g.MainStat.Length > 0 ? $" · {g.MainStat}" : ""));
            }

            VBoxContainer cell = new() { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            cell.AddChild(new Label { Text = slot.Name });
            OptionButton picker = new() { CustomMinimumSize = new Vector2(220, 34), SizeFlagsHorizontal = SizeFlags.ExpandFill };
            foreach (string label in gearLabels) picker.AddItem(label);
            picker.Selected = current is null ? 0 : Math.Max(0, gearIds.IndexOf(current.Uid));
            picker.ItemSelected += index =>
            {
                string? uid = gearIds[(int)index];
                _ = GearActionAsync(characterId, uid is null ? "unequip" : "equip", uid);
            };
            HBoxContainer gearRow = new();
            gearRow.AddThemeConstantOverride("separation", 6);
            gearRow.AddChild(picker);
            Button enhance = new() { Text = "+1", CustomMinimumSize = new Vector2(48, 34), Disabled = current is null };
            if (current is not null)
            {
                int nextLevel = current.Level + 1;
                string uid = current.Uid;
                enhance.Pressed += () => _ = GearActionAsync(characterId, "enhance", uid, nextLevel);
            }
            gearRow.AddChild(enhance);
            cell.AddChild(gearRow);
            gearGrid.AddChild(cell);
        }
        right.AddChild(gearGrid);

        if (def.Rarity >= 5 && st.Eidolon < 6)
        {
            Button license = new()
            {
                Text = "权阶 +1（360 通过回执）",
                CustomMinimumSize = new Vector2(240, 36),
            };
            license.Pressed += () => _ = ExchangeForCharacterAsync(characterId);
            right.AddChild(license);
        }

        double need = GrowthRules.ExpToNext(_data.Growth, st.Level);
        ProgressBar expBar = new()
        {
            MaxValue = Math.Max(1.0, need),
            Value = Math.Clamp(st.Exp, 0, Math.Max(1.0, need)),
            ShowPercentage = false,
            CustomMinimumSize = new Vector2(0, 18),
        };
        right.AddChild(expBar);
        right.AddChild(new Label { Text = $"经验 {st.Exp:0} / {need:0}" });

        // ── 技能 ──
        HBoxContainer skills = new();
        skills.AddThemeConstantOverride("separation", 8);
        // 新角色有 bloom / enhanced 技能位，存档里可能只预置了四个基础位；
        // 这里把“角色数据里存在、但存档还没记录”的槽位也显示出来（服务端 SkillUp 默认按 1 级处理）。
        List<string> slots = [.. st.SkillLevels.Keys];
        foreach (string key in def.ResolveSkills(null).Keys)
            if (!slots.Contains(key)) slots.Add(key);
        foreach (string slot in slots.OrderBy(static k => k, StringComparer.Ordinal))
        {
            int lv = st.SkillLevels.GetValueOrDefault(slot, 1);
            Button b = new()
            {
                Text = $"{SkillSlotName(slot)} {lv}→{lv + 1}",
                CustomMinimumSize = new Vector2(150, 40),
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
            };
            b.Pressed += () => _ = SkillUpAsync(characterId, slot);
            if (def.ResolveSkills(null).TryGetValue(slot, out SkillDef? skillDef) && skillDef.Description.Length > 0)
                b.TooltipText = skillDef.Description;
            skills.AddChild(b);
        }
        right.AddChild(skills);

        // 技能说明：新角色的 bloom / enhanced 位在按钮上只有名字，这里给可读描述。
        ScrollContainer skillTextScroll = new()
        {
            CustomMinimumSize = new Vector2(0, 110),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        VBoxContainer skillTexts = new() { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        skillTexts.AddThemeConstantOverride("separation", 3);
        foreach (string slot in slots.OrderBy(static k => k, StringComparer.Ordinal))
        {
            if (!def.ResolveSkills(null).TryGetValue(slot, out SkillDef? skillDef)) continue;
            if (string.IsNullOrWhiteSpace(skillDef.Description)) continue;
            Label line = new()
            {
                Text = $"【{SkillSlotName(slot)}】{skillDef.Description}",
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                Modulate = new Color(1, 1, 1, 0.72f),
            };
            line.AddThemeFontSizeOverride("font_size", 12);
            skillTexts.AddChild(line);
        }
        skillTextScroll.AddChild(skillTexts);
        right.AddChild(skillTextScroll);

        right.AddChild(new Control { SizeFlagsVertical = SizeFlags.ExpandFill });

        HBoxContainer bar = new();
        bar.AddThemeConstantOverride("separation", 10);

        Button levelUp = new() { Text = "使用经验道具", CustomMinimumSize = new Vector2(180, 44) };
        levelUp.Pressed += () => _ = LevelUpAsync(characterId);
        bar.AddChild(levelUp);

        Button breakthrough = new() { Text = "突破", CustomMinimumSize = new Vector2(140, 44) };
        breakthrough.Pressed += () => _ = BreakthroughAsync(characterId);
        bar.AddChild(breakthrough);

        right.AddChild(bar);
        body.AddChild(right);
        root.AddChild(body);

        if (_toast.Length > 0)
        {
            Label t = new() { Text = "· " + _toast, AutowrapMode = TextServer.AutowrapMode.WordSmart };
            root.AddChild(t);
        }

        Swap(root);
        StatusLine();
    }

    // ── 养成：三个动作都走服务端 ──────────────────────────────────────

    /// <summary>
    /// 吃经验道具升级。**消耗与结果都由服务端算**（`POST /api/game/growth`）。
    /// </summary>
    /// <remarks>
    /// 客户端的职责缩到两件：决定"提交哪些道具"，以及把服务端的话原样显示出来。
    /// 经验曲线、突破门槛、材料扣减一律不在本地复算 —— 那是同一套规则的第二份实现，
    /// 两份实现迟早会有一份是错的。
    /// </remarks>
    private async Task LevelUpAsync(string characterId)
    {
        List<string> pairs = [];
        foreach (KeyValuePair<string, int> kv in _save.Inventory.OrderBy(static k => k.Key, StringComparer.Ordinal))
        {
            if (kv.Value <= 0) continue;
            if (GrowthRules.ExpValue(_data.Growth, kv.Key) <= 0) continue;
            pairs.Add($"{ApiClient.Str(kv.Key)}:{kv.Value}");
        }
        if (pairs.Count == 0)
        {
            _toast = "没有可用的经验道具（去「定期清剿 · 经验」刷）。";
            ShowCharacterDetail(characterId);
            return;
        }

        ShowBusy("正在结算…");
        ApiClient.Reply reply = await _api.GrowthAsync(
            $"{{\"action\":\"levelUp\",\"characterId\":{ApiClient.Str(characterId)},\"items\":{{{string.Join(",", pairs)}}}}}");
        await ReloadSaveAsync();
        _toast = reply.Ok ? "升级完成 · " + reply.Message : "升级被拒：" + reply.Message;
        ShowCharacterDetail(characterId);
    }

    private async Task BreakthroughAsync(string characterId)
    {
        ShowBusy("正在结算…");
        ApiClient.Reply reply = await _api.GrowthAsync(
            $"{{\"action\":\"breakthrough\",\"characterId\":{ApiClient.Str(characterId)}}}");
        await ReloadSaveAsync();
        _toast = reply.Ok ? "突破完成 · " + reply.Message : "突破被拒：" + reply.Message;
        ShowCharacterDetail(characterId);
    }

    private async Task GearActionAsync(string characterId, string action, string? uid, int? targetLevel = null)
    {
        if (uid is null) return;
        ShowBusy("正在更新装备…");
        string levelJson = targetLevel is int lv ? $",\"level\":{lv}" : "";
        string body = $"{{\"action\":\"gear\",\"gearAction\":{ApiClient.Str(action)}," +
                      $"\"characterId\":{ApiClient.Str(characterId)},\"uid\":{ApiClient.Str(uid)}{levelJson}}}";
        ApiClient.Reply reply = await _api.GrowthAsync(body);
        await ReloadSaveAsync();
        _toast = reply.Ok ? "装备已更新。" : "装备更新失败：" + reply.Message;
        ShowCharacterDetail(characterId);
    }

    private async Task ExchangeForCharacterAsync(string characterId)
    {
        ShowBusy("正在兑换权阶许可…");
        ApiClient.Reply reply = await _api.ShopExchangeAsync("eidolonLicense", 1, characterId);
        await ReloadSaveAsync();
        _toast = reply.Ok ? "权阶已提升。" : "兑换失败：" + reply.Message;
        ShowCharacterDetail(characterId);
    }

    private async Task ExchangeForWeaponAsync(string characterId, string weaponId)
    {
        ShowBusy("正在兑换武器覆写许可…");
        ApiClient.Reply reply = await _api.ShopExchangeAsync("weaponOverwriteLicense", 1, weaponId);
        await ReloadSaveAsync();
        _toast = reply.Ok ? "武器覆写已提升。" : "兑换失败：" + reply.Message;
        ShowCharacterDetail(characterId);
    }

    private async Task WeaponUpAsync(string characterId, string weaponId, int level)
    {
        ShowBusy("正在强化武器…");
        ApiClient.Reply reply = await _api.GrowthAsync(
            $"{{\"action\":\"weapon\",\"weaponId\":{ApiClient.Str(weaponId)},\"level\":{level}}}");
        await ReloadSaveAsync();
        _toast = reply.Ok ? $"武器强化到 +{level}。" : "武器强化失败：" + reply.Message;
        ShowCharacterDetail(characterId);
    }

    private async Task EquipWeaponAsync(string characterId, string? weaponId)
    {
        ShowBusy("正在切换武器…");
        ApiClient.Reply reply = await _api.EquipWeaponAsync(characterId, weaponId);
        await ReloadSaveAsync();
        _toast = reply.Ok ? "武器已更新。" : "武器切换失败：" + reply.Message;
        ShowCharacterDetail(characterId);
    }

    private async Task SkillUpAsync(string characterId, string slot)
    {
        ShowBusy("正在结算…");
        ApiClient.Reply reply = await _api.GrowthAsync(
            $"{{\"action\":\"skill\",\"characterId\":{ApiClient.Str(characterId)},\"slot\":{ApiClient.Str(slot)}}}");
        await ReloadSaveAsync();
        _toast = reply.Ok ? $"{SkillSlotName(slot)} 升级完成 · " + reply.Message : "升级被拒：" + reply.Message;
        ShowCharacterDetail(characterId);
    }

    /// <summary>换衣装：服务端写档案 → 重读存档 → 重画这一屏。</summary>
    private async Task SetSkinAsync(string characterId, string skinId)
    {
        ApiClient.Reply reply = await _api.SetProfileAsync(null, null, characterId, skinId);
        await ReloadSaveAsync();
        _toast = reply.Ok ? "衣装已记录。" : "衣装没能保存：" + reply.Message;
        ShowCharacterDetail(characterId);
    }

    /// <summary>技能位的中文名。玩家的界面上不该出现 `basic` / `enhancedResonance` 这种内部键。</summary>
    private static string SkillSlotName(string slot) => slot switch
    {
        "basic" => "普攻",
        "enhancedBasic" => "强化普攻",
        "resonance" => "元素共鸣",
        "enhancedResonance" => "强化共鸣",
        "bloom" => "元素绽放",
        "talent" => "天赋",
        "ultimate" => "终结技",
        "enhancedUltimate" => "强化终结技",
        _ => slot,
    };

    private static string ItemKindZh(string kind) => kind switch
    {
        "exp" => "经验",
        "ascension" => "突破",
        "skill" => "技能",
        "gear" => "装备",
        "weapon" => "武器",
        _ => kind,
    };

    private static string ElementZh(WaningOath.Core.Element element) => element switch
    {
        WaningOath.Core.Element.Physical => "物理",
        WaningOath.Core.Element.Ice => "冰",
        WaningOath.Core.Element.Fire => "火",
        WaningOath.Core.Element.Lightning => "雷",
        WaningOath.Core.Element.Wind => "风",
        WaningOath.Core.Element.Resonance => "共振",
        WaningOath.Core.Element.Liye => "离谒",
        WaningOath.Core.Element.Aria => "咏叹",
        _ => element.ToString(),
    };

    private static Color ElementColor(WaningOath.Core.Element element) => element switch
    {
        WaningOath.Core.Element.Physical => Color.FromHtml("#d7dbe4"),
        WaningOath.Core.Element.Ice => Color.FromHtml("#7fd8ff"),
        WaningOath.Core.Element.Fire => Color.FromHtml("#ff8a5c"),
        WaningOath.Core.Element.Lightning => Color.FromHtml("#c6a4ff"),
        WaningOath.Core.Element.Wind => Color.FromHtml("#7ee7b0"),
        WaningOath.Core.Element.Resonance => Color.FromHtml("#ff9ede"),
        WaningOath.Core.Element.Liye => Color.FromHtml("#ff6b8a"),
        WaningOath.Core.Element.Aria => Color.FromHtml("#ffb3d1"),
        _ => Colors.White,
    };

    /// <summary>当前突破次数下允许到达的等级上限（与服务端 `GameService.AllowedLevelCap` 同一个口径）。</summary>
    private static int AllowedLevelCap(GrowthTable g, int breakthrough)
    {
        List<int> nodes = [.. g.Level.BreakthroughNodes.OrderBy(static n => n)];
        return breakthrough >= 0 && breakthrough < nodes.Count ? nodes[breakthrough] : g.Level.Cap;
    }

    // ── 战斗 ─────────────────────────────────────────────────────────

    private BattlePresenter? _battle;
    private VBoxContainer _logBox = null!;
    private ScrollContainer _logScroll = null!;
    private HBoxContainer _partyRow = null!;
    private VBoxContainer _enemyRow = null!;
    private Label _resonanceLabel = null!;
    private Button _advanceButton = null!;
    private Button _autoButton = null!;
    private VBoxContainer _commandPanel = null!;
    private ManualPolicy _manualPolicy = new();
    private readonly List<CommandRow> _commandRows = [];
    private readonly Dictionary<int, string> _pendingSkills = [];
    private readonly Dictionary<int, int?> _pendingTargets = [];

    /// <summary>手动战斗的一行指令：一个待行动的我方单位 + 技能下拉 + 目标下拉。</summary>
    private sealed class CommandRow
    {
        public required BattleUnit Actor;
        public required OptionButton Skill;
        public required OptionButton Target;
        public required Label Hint;
        public List<BattleUnit> TargetUnits = [];
        public List<string> SkillKeys = [];
    }

    /// <summary>当前这一场的来源：序章的战斗打完回剧情，清剿本打完回主界面。</summary>
    private bool _battleFromStory;
    private string _battleStageId = "";

    /// <summary>
    /// 战斗屏。**按关卡开**，序章与定期清剿走的是同一条路。
    /// </summary>
    /// <param name="stageId">关卡 id（`data/stages/` 里的白名单）。</param>
    /// <param name="tutorial">是否用教学引导 —— 序章那一场要教普攻 / 共鸣 / 终结技，清剿本不用。</param>
    /// <param name="fromStory">打完之后回剧情（序章）还是回主界面（清剿）。</param>
    /// <summary>把当前存档的等级/武器/装备折成战斗面板；教学/剧情固定面板返回 null。</summary>
    private IReadOnlyDictionary<string, Loadout>? BuildLoadouts(StageDef stage)
    {
        if (stage.FixedPanel) return null;
        Dictionary<string, Loadout> result = new(StringComparer.Ordinal);
        foreach (string id in stage.Party)
        {
            if (!_save.Characters.TryGetValue(id, out PlayerSave.CharacterState? st)) continue;
            PlayerSave.WeaponState? weapon = st.Weapon is not null &&
                _save.Weapons.TryGetValue(st.Weapon, out PlayerSave.WeaponState? ws) ? ws : null;
            result[id] = new Loadout(
                Level: st.Level,
                Breakthrough: st.Breakthrough,
                Eidolon: st.Eidolon,
                WeaponId: st.Weapon,
                WeaponLevel: weapon?.Level ?? 80,
                WeaponOverwrite: weapon?.Overwrite ?? 1,
                Gears: [.. st.Gears
                    .Select(uid => _save.Gears.FirstOrDefault(g => g.Uid == uid))
                    .Where(static g => g is not null)
                    .Select(static g => new GearPiece(g!.Uid, g.Set, g.Part, g.Level, g.MainStat,
                        [.. g.SubStats.Select(static s => (s.Stat, s.Value))]))]);
        }
        return result.Count > 0 ? result : null;
    }

    private void ShowBattle(string stageId, bool tutorial, bool fromStory)
    {
        _storyAuto = false;
        _storyAutoVersion++;
        StageDef stage = _data.Stage(stageId);
        // 普通关卡按玩家编队重建；教学/剧情关保留固定面板（stage.FixedPanel）。
        if (!fromStory && !stage.FixedPanel && _save.Team.Count > 0)
        {
            stage = stage.Clone();
            stage.Party = [.. _save.Team];
            stage.PartyModes = new Dictionary<string, string>(_save.TeamModes, StringComparer.Ordinal);
        }
        _battleStageId = stageId;
        _battleFromStory = fromStory;

        _manualPolicy = new ManualPolicy();
        Dictionary<string, int> eidolons = new(StringComparer.Ordinal);
        foreach ((string id, PlayerSave.CharacterState st) in _save.Characters)
            eidolons[id] = st.Eidolon;
        _battle = new BattlePresenter(_data, stage, 20260815UL,
            System.Environment.GetEnvironmentVariable("WO_MODE"), eidolons, BuildLoadouts(stage))
        {
            Policy = _manualPolicy,
        };
        _battle.Start();

        VBoxContainer root = new();
        root.AddThemeConstantOverride("separation", 10);

        // 战斗背景：序章那一场用后山路边的树丛；清剿本暂用同一张，
        // 等属性靶场背景出图后再按 StageDef 分派（美术策划案/02 §3.3）。
        SetNextBackground(_battleFromStory ? "battle-site14-hill.png" : "site14-hill-dawn.png");

        // 敌方区（上方）
        _enemyRow = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        root.AddChild(_enemyRow);

        // 日志区（中间，唯一会滚动的部分）
        _logScroll = new ScrollContainer
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 200),
        };
        _logBox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _logScroll.AddChild(_logBox);
        root.AddChild(_logScroll);

        // 我方卡牌行（下方）。**横向最紧的一档是 4:3**：4 张 × 最小 220 + 间距 < 1440。
        _partyRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _partyRow.AddThemeConstantOverride("separation", 10);
        root.AddChild(_partyRow);

        // 手动战斗指令区：只对"下一拍会行动的我方单位"收集指令。
        // 自动托管仍然走 GreedyPolicy —— 两条路共用 BattleRules.CanUse，不会分叉。
        _commandPanel = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _commandPanel.AddThemeConstantOverride("separation", 6);
        root.AddChild(_commandPanel);

        // 操作条
        HBoxContainer bar = new();
        bar.AddThemeConstantOverride("separation", 10);

        _resonanceLabel = new Label { SizeFlagsHorizontal = SizeFlags.ExpandFill, VerticalAlignment = VerticalAlignment.Center };
        bar.AddChild(_resonanceLabel);

        _advanceButton = new Button { Text = "执行本拍", CustomMinimumSize = new Vector2(160, 44) };
        _advanceButton.Pressed += OnAdvance;
        bar.AddChild(_advanceButton);

        _autoButton = new Button { Text = "自动打完", CustomMinimumSize = new Vector2(160, 44) };
        _autoButton.Pressed += OnAuto;
        bar.AddChild(_autoButton);
        root.AddChild(bar);

        Swap(root);
        RefreshBattle();
        AppendLog([.. _battle.State.Log]);
    }

    private void OnAdvance()
    {
        if (_battle is null) return;
        if (_battle.Finished) { ShowResult(); return; }

        // 收集本拍所有待行动我方单位的指令；没选的用 GreedyPolicy 补，避免"漏一个就白丢回合"。
        List<BattleUnit> ready = BattleRules.PredictReadyAllies(_battle.State);
        GreedyPolicy fallback = new();
        _manualPolicy = new ManualPolicy();
        foreach (BattleUnit actor in ready)
        {
            if (_pendingSkills.TryGetValue(actor.Serial, out string? key)
                && actor.Skills.TryGetValue(key, out SkillDef? skill)
                && BattleRules.CanUse(_battle.State, actor, skill, allowOverdraw: false, out _))
            {
                int? target = _pendingTargets.GetValueOrDefault(actor.Serial);
                _manualPolicy.Choices[actor.Serial] = new ActionChoice(key, target);
            }
            else
            {
                ActionChoice? auto = fallback.Choose(_battle.State, actor);
                if (auto is not null) _manualPolicy.Choices[actor.Serial] = auto;
            }
        }

        _battle.Policy = _manualPolicy;
        int from = _battle.State.Log.Count;
        _battle.Advance();
        AppendLog([.. _battle.State.Log.Skip(from)]);
        _pendingSkills.Clear();
        _pendingTargets.Clear();
        RefreshBattle();
        if (_battle.Finished)
        {
            _advanceButton.Text = "查看结算";
            _autoButton.Disabled = true;
            _advanceButton.Disabled = true;
        }
    }

    private void OnAuto()
    {
        if (_battle is null) return;
        _battle.Policy = new GreedyPolicy();
        _battle.RunToEnd();
        foreach (Node child in _logBox.GetChildren()) child.QueueFree();
        AppendLog([.. _battle.State.Log]);
        RefreshBattle();
        ShowResult();
    }

    private void RefreshBattle()
    {
        if (_battle is null) return;
        BattleState s = _battle.State;

        _resonanceLabel.Text = $"自然回合 {s.Round}/{s.MaxRound} · 行动 {s.TurnCounter} · " +
                               $"共鸣指数 {s.ResonanceIndex}/{s.ResonanceCap} · " +
                               $"对敌伤害 {s.Metrics.HpRemoved:N0}";

        foreach (Node child in _enemyRow.GetChildren()) child.QueueFree();
        foreach (Node child in _partyRow.GetChildren()) child.QueueFree();

        foreach (BattleUnit u in s.Enemies) _enemyRow.AddChild(UnitCard(u, false));
        foreach (BattleUnit u in s.Allies) _partyRow.AddChild(UnitCard(u, true));

        BuildCommandPanel();
    }

    /// <summary>
    /// 手动战斗指令面板：只给"下一拍会行动的我方单位"建行。
    /// </summary>
    /// <remarks>
    /// 合法性判定全部走 <see cref="BattleRules.CanUse"/>，不在这里重写一套；
    /// 自动托管用的是同一个 <see cref="GreedyPolicy"/>，所以手操与托管不会算出两种结果。
    /// </remarks>
    private void BuildCommandPanel()
    {
        foreach (Node child in _commandPanel.GetChildren()) child.QueueFree();
        _commandRows.Clear();

        if (_battle is null || _battle.Finished)
        {
            _advanceButton.Disabled = true;
            return;
        }

        _advanceButton.Disabled = false;
        List<BattleUnit> ready = BattleRules.PredictReadyAllies(_battle.State);
        if (ready.Count == 0)
        {
            Label idle = new()
            {
                Text = "本拍我方没有行动；点「执行本拍」让敌方行动。",
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
            };
            _commandPanel.AddChild(idle);
            return;
        }

        Label title = new()
        {
            Text = "本拍指令（未选择则自动托管；点「执行本拍」结算）",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        title.AddThemeFontSizeOverride("font_size", 15);
        _commandPanel.AddChild(title);

        foreach (BattleUnit actor in ready)
        {
            PanelContainer panel = new() { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            HBoxContainer row = new();
            row.AddThemeConstantOverride("separation", 8);
            panel.AddChild(row);
            _commandPanel.AddChild(panel);

            Label who = new()
            {
                Text = $"{actor.Name}  AV {actor.Av:0.0}",
                CustomMinimumSize = new Vector2(170, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ClipText = true,
            };
            row.AddChild(who);

            OptionButton skill = new() { CustomMinimumSize = new Vector2(230, 36) };
            OptionButton target = new() { CustomMinimumSize = new Vector2(190, 36) };
            Label hint = new()
            {
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                VerticalAlignment = VerticalAlignment.Center,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
            };

            CommandRow command = new()
            {
                Actor = actor,
                Skill = skill,
                Target = target,
                Hint = hint,
            };

            // 技能顺序：普攻/共鸣/终结技优先，强化与特殊位排后面。
            foreach (string key in actor.Skills.Keys
                         .Where(k => actor.Skills[k].Kind is "basic" or "resonance" or "ultimate"
                             or "enhanced_basic" or "enhanced_resonance" or "enhanced_ultimate" or "bloom")
                         .OrderBy(static k => k switch
                         {
                             "basic" => 0,
                             "resonance" => 1,
                             "ultimate" => 2,
                             "enhancedUltimate" => 3,
                             "enhancedResonance" => 4,
                             "enhancedBasic" => 5,
                             _ => 6,
                         })
                         .ThenBy(static k => k, StringComparer.Ordinal))
            {
                SkillDef def = actor.Skills[key];
                bool can = BattleRules.CanUse(_battle.State, actor, def, allowOverdraw: false, out string reason);
                int idx = command.SkillKeys.Count;
                command.SkillKeys.Add(key);
                command.Skill.AddItem($"{def.Name}  [{key}]");
                command.Skill.SetItemDisabled(idx, !can);
                if (!can) command.Skill.SetItemTooltip(idx, reason);
            }

            skill.ItemSelected += idx => OnCommandSkillSelected(command, (int)idx);
            target.ItemSelected += idx => OnCommandTargetSelected(command, (int)idx);

            row.AddChild(skill);
            row.AddChild(target);
            row.AddChild(hint);
            _commandRows.Add(command);
        }

        // 默认选中每一行第一个可用技能，减少点击次数。
        foreach (CommandRow command in _commandRows)
        {
            // 「可处决」等强制行动：界面直接把行锁到规则给出的技能与目标，
            // 避免玩家选了别的、最后被 ManualPolicy 静默改掉。
            ActionChoice? forced = _battle is null ? null : BattleRules.ForcedChoice(_battle.State, command.Actor);
            int forcedIdx = forced is null ? -1 : command.SkillKeys.IndexOf(forced.SkillKey);
            if (forcedIdx >= 0)
            {
                for (int i = 0; i < command.Skill.ItemCount; i++)
                {
                    if (i == forcedIdx) continue;
                    command.Skill.SetItemDisabled(i, true);
                    command.Skill.SetItemTooltip(i, "本拍被「可处决」强制为「处决」。");
                }
                command.Skill.Select(forcedIdx);
                OnCommandSkillSelected(command, forcedIdx);
                command.Hint.Text = "本拍被「可处决」强制为「处决」。";
                if (forced?.TargetSerial is int serial)
                {
                    int ti = command.TargetUnits.FindIndex(u => u.Serial == serial);
                    if (ti >= 0)
                    {
                        command.Target.Select(ti);
                        OnCommandTargetSelected(command, ti);
                    }
                }
                continue;
            }

            int first = -1;
            for (int i = 0; i < command.Skill.ItemCount; i++)
            {
                if (!command.Skill.IsItemDisabled(i)) { first = i; break; }
            }
            if (first >= 0)
            {
                command.Skill.Select(first);
                OnCommandSkillSelected(command, first);
            }
            else
            {
                command.Hint.Text = "本拍没有可用技能，执行时会自动托管。";
            }
        }
    }

    private void OnCommandSkillSelected(CommandRow row, int index)
    {
        if (_battle is null || index < 0 || index >= row.SkillKeys.Count) return;
        string key = row.SkillKeys[index];
        if (!row.Actor.Skills.TryGetValue(key, out SkillDef? skill)) return;

        _pendingSkills[row.Actor.Serial] = key;
        row.Target.Clear();
        row.TargetUnits = BattleRules.LegalTargets(_battle.State, row.Actor, skill);
        foreach (BattleUnit t in row.TargetUnits)
            row.Target.AddItem(t.IsAlive ? t.Name : $"{t.Name}（倒下）");

        if (row.TargetUnits.Count > 0)
        {
            row.Target.Disabled = false;
            row.Target.Select(0);
            _pendingTargets[row.Actor.Serial] = row.TargetUnits[0].Serial;
        }
        else
        {
            row.Target.Disabled = true;
            _pendingTargets[row.Actor.Serial] = null;
        }

        bool can = BattleRules.CanUse(_battle.State, row.Actor, skill, allowOverdraw: false, out string reason);
        row.Hint.Text = can
            ? (BattleRules.IsSupport(skill) ? "辅助/治疗技能，目标默认我方。" : "点「执行本拍」结算。")
            : reason;
    }

    private void OnCommandTargetSelected(CommandRow row, int index)
    {
        if (index < 0 || index >= row.TargetUnits.Count) return;
        _pendingTargets[row.Actor.Serial] = row.TargetUnits[index].Serial;
    }

    /// <summary>
    /// 一张单位卡。**宽度靠 `ExpandFill` 自适应、最小宽度写死 220** ——
    /// 这是 4:3（1440 宽）下四张卡仍然放得下的那条约束。
    /// </summary>
    private Control UnitCard(BattleUnit u, bool ally)
    {
        PanelContainer panel = new()
        {
            // 我方是四张并排的窄卡（撑满）；敌方是"一张卡"，收成内容宽度 ——
            // 铺满整行会让右边的立绘变成角落里的一张贴纸。
            SizeFlagsHorizontal = ally ? SizeFlags.ExpandFill : SizeFlags.ShrinkBegin,
            CustomMinimumSize = ally ? new Vector2(220, 0) : new Vector2(430, 250),
        };

        MarginContainer pad = new();
        pad.AddThemeConstantOverride("margin_left", 10);
        pad.AddThemeConstantOverride("margin_right", 10);
        pad.AddThemeConstantOverride("margin_top", 8);
        pad.AddThemeConstantOverride("margin_bottom", 8);
        panel.AddChild(pad);

        // 敌方卡牌：左边文字、右边立绘（没有图就只留文字，见 EnemyArt.Tile）。
        // 我方卡牌保持纯文字 —— 一张屏上放四张立绘会挤，也抢掉战斗日志的位置。
        HBoxContainer row = new();
        row.AddThemeConstantOverride("separation", 12);
        pad.AddChild(row);

        VBoxContainer box = new() { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        box.AddThemeConstantOverride("separation", 4);
        row.AddChild(box);

        Label name = new()
        {
            Text = (u.IsAlive ? "" : "（已倒下）") + u.Name,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        name.AddThemeFontSizeOverride("font_size", ally ? 17 : 20);
        box.AddChild(name);

        ProgressBar hp = new()
        {
            MaxValue = Math.Max(1.0, u.MaxHp),
            Value = Math.Max(0.0, u.Hp),
            ShowPercentage = false,
            CustomMinimumSize = new Vector2(0, 16),
        };
        box.AddChild(hp);

        Label hpText = new() { Text = $"{u.Hp:N0} / {u.MaxHp:N0}" };
        box.AddChild(hpText);

        // 行动条：AV 越小越靠近行动。**竖排靠左 + 数值并排**，不依赖固定像素位置。
        Label av = new() { Text = $"行动值 {u.Av:0.0}" };
        box.AddChild(av);

        double energyMax = u.Stats.EnergyMaxValue + u.EnergyOverflowCap;
        if (energyMax > 0)
        {
            Label energy = new() { Text = $"能量 {u.Energy:0}/{energyMax:0}" };
            box.AddChild(energy);
        }
        if (u.Shield > 0)
            box.AddChild(new Label { Text = $"护盾 {u.Shield:N0}" });

        if (u.Stacks.Count > 0)
        {
            string stackText = string.Join(" · ", u.Stacks
                .Where(static kv => kv.Value > 0)
                .OrderBy(static kv => kv.Key, StringComparer.Ordinal)
                .Select(static kv => $"{kv.Key}{kv.Value}"));
            if (stackText.Length > 0)
                box.AddChild(new Label { Text = stackText, AutowrapMode = TextServer.AutowrapMode.WordSmart });
        }

        string stacks = string.Join(" · ", u.Anomalies
            .Where(static kv => kv.Value.AnyActive)
            .OrderBy(static kv => kv.Key)
            .Select(static kv => $"{ElementInfo.FamilyToChinese(kv.Key)}{kv.Value.Stacks}"));
        if (stacks.Length > 0)
        {
            Label anomalies = new()
            {
                Text = stacks,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
            };
            box.AddChild(anomalies);
        }

        if (!ally)
        {
            TextureRect? art = EnemyArt.Tile(u.DefId, 176f, 264f);
            if (art is not null) row.AddChild(art);
        }

        return panel;
    }

    private void AppendLog(IEnumerable<string> lines)
    {
        foreach (string line in lines)
        {
            Label entry = new()
            {
                Text = line,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
            };
            entry.AddThemeFontSizeOverride("font_size", 15);
            _logBox.AddChild(entry);
        }

        // 滚到底：等一帧，让新加的 Label 参与完布局再设置滚动位置。
        CallDeferred(nameof(ScrollLogToBottom));
    }

    private void ScrollLogToBottom()
    {
        _logScroll.ScrollVertical = (int)_logScroll.GetVScrollBar().MaxValue;
    }

    // ── 结算 ─────────────────────────────────────────────────────────

    private void ShowResult()
    {
        if (_battle is null) { ShowHome(); return; }

        BattleState s = _battle.State;
        bool win = s.Outcome == BattleOutcome.AllyWin;
        StageDef stage = _data.Stage(_battleStageId);

        VBoxContainer box = new() { Alignment = BoxContainer.AlignmentMode.Center };
        box.AddThemeConstantOverride("separation", 12);

        Label big = new()
        {
            Text = win ? "作战完成" : "作战失败",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        big.AddThemeFontSizeOverride("font_size", 36);
        box.AddChild(big);

        Label where = new() { Text = stage.Name, HorizontalAlignment = HorizontalAlignment.Center };
        where.AddThemeFontSizeOverride("font_size", 20);
        box.AddChild(where);

        Label detail = new()
        {
            Text = $"自然回合 {s.Round}/{s.MaxRound} · 行动 {s.TurnCounter}\n" +
                   $"对敌总伤害 {s.Metrics.HpRemoved:N0}（直伤 {s.Metrics.CategoryOf(DamageCategory.Direct):N0} · " +
                   $"属性异常 {s.Metrics.CategoryOf(DamageCategory.Anomaly):N0} · " +
                   $"元素紊乱 {s.Metrics.CategoryOf(DamageCategory.Disorder):N0}）\n" +
                   $"我方承伤 {s.Metrics.HpLost:N0} · 阵亡 {s.Metrics.AllyDeaths}",
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        box.AddChild(detail);

        Label report = new()
        {
            Text = "（正在向服务器上报……）",
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        box.AddChild(report);

        if (win && _battleFromStory)
        {
            Button onward = new() { Text = "继续剧情", CustomMinimumSize = new Vector2(240, 46) };
            onward.Pressed += () => ShowStory(IndexOfScene("S8"));
            box.AddChild(Wrap(onward));
        }

        Button retry = new() { Text = "再打一次", CustomMinimumSize = new Vector2(240, 46) };
        retry.Pressed += () => ShowBattle(_battleStageId, tutorial: _battleFromStory, fromStory: _battleFromStory);
        box.AddChild(Wrap(retry));

        Button home = new() { Text = "返回主界面", CustomMinimumSize = new Vector2(240, 46) };
        home.Pressed += ShowHome;
        box.AddChild(Wrap(home));

        Swap(box);

        if (win) _ = ReportAsync(report);
        else report.Text = "本场未上报（只有通关才结算奖励）。";
    }

    /// <summary>某一幕的第一拍在剧本数组里的下标。</summary>
    private static int IndexOfScene(string scene)
    {
        for (int i = 0; i < Prologue.Length; i++)
        {
            if (string.Equals(Prologue[i].Scene, scene, StringComparison.Ordinal)) return i;
        }
        return Prologue.Length;
    }

    private async Task ReportAsync(Label report)
    {
        if (string.Equals(_data.Stage(_battleStageId).Kind, "sim", StringComparison.Ordinal))
        {
            report.Text = "平衡实测靶场：本场不结算奖励。";
            return;
        }

        // 手动战斗先做重放校验：服务端用同一份 choices 重跑，日志指纹一致才允许结算。
        if (_battle is not null && _battle.Policy is ManualPolicy manual && manual.Used.Count > 0)
        {
            string hash = LogHash.Fnv1a64(_battle.State.Log);
            ApiClient.Reply verify = await _api.ReportBattleAsync(
                _battleStageId, _battle.Seed, "AllyWin", hash, manual.Used);
            if (!verify.Ok)
            {
                report.Text = $"重放校验未通过，未结算：{verify.Message}";
                return;
            }
        }

        (bool Ok, int GearDrops, string Summary, string Error) settled =
            await _api.ReportStageClearDetailedAsync(_battleStageId, "AllyWin");
        await ReloadSaveAsync();
        if (!IsInstanceValid(report)) return;
        report.Text = settled.Ok
            ? $"服务器结算成功。{settled.Summary}" +
              (settled.GearDrops > 0 ? $"　装备掉落 {settled.GearDrops} 件。" : "")
            : $"结算被拒：{settled.Error}";
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    // ── 小工具 ───────────────────────────────────────────────────────

    /// <summary>把一个按钮放进居中容器 —— 直接用 `Alignment` 会在 4:3 下被拉满宽。</summary>
    private static HBoxContainer Wrap(Control child)
    {
        HBoxContainer row = new() { Alignment = BoxContainer.AlignmentMode.Center };
        row.AddChild(child);
        return row;
    }

    /// <summary>
    /// 中文字体。**优先用工程里自带的 `NotoSansSC-VF.ttf`**（SIL OFL，可随游戏分发），
    /// 找不到才退到系统字体 —— Godot 默认字体没有 CJK 字形，整屏会变成方块。
    /// </summary>
    /// <remarks>
    /// ⚠️ **两条路都要走**（第 7 轮修）：<c>ResourceLoader</c> 只认**已导入**的资源
    /// （<c>res://*.ttf</c> 需要编辑器先生成 <c>.ttf.import</c>），而本机跑编辑器/`--headless --editor`
    /// 都会挂住 ⇒ 导入这一步在流水线里做不了。于是"随包字体"实际上**从来没被加载过**，
    /// 一直靠系统字体撑着（`GD.PushWarning` 一直有，只是没人看 headless 的 stderr）。
    /// 现在补一条 <see cref="FontFile.LoadDynamicFont"/> 直读字节的回退路径 —— 与
    /// <see cref="Portraits"/> 对图片用的是同一个思路。
    /// </remarks>
    private static Font LoadCjkFont()
    {
        const string bundled = "res://Assets/Fonts/NotoSansSC-VF.ttf";

        if (ResourceLoader.Exists(bundled))
        {
            FontFile? imported = ResourceLoader.Load<FontFile>(bundled);
            if (imported is not null)
            {
                GD.Print($"[字体] 已导入资源加载成功：{bundled}");
                return imported;
            }
        }

        if (Godot.FileAccess.FileExists(bundled))
        {
            try
            {
                FontFile direct = new();
                Error err = direct.LoadDynamicFont(bundled);
                if (err == Error.Ok)
                {
                    GD.Print($"[字体] 未导入，走 FontFile.LoadDynamicFont 直读：{bundled}");
                    return direct;
                }
                GD.PushWarning($"[字体] LoadDynamicFont({bundled}) 返回 {err}。");
            }
            catch (Exception ex)
            {
                GD.PushWarning($"[字体] 直读 {bundled} 抛异常：{ex.Message}");
            }
        }
        else
        {
            GD.PushWarning($"[字体] 工程里没有 {bundled} —— 分发前必须把字体打进包里。");
        }

        GD.PushWarning($"退到系统字体（Godot 默认字体没有 CJK 字形，缺字体时整屏会是方块）。");
        return new SystemFont { FontNames = ["Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "Noto Sans SC"] };
    }

    private static Theme BuildTheme(Font font)
    {
        Color text = Color.FromHtml("#eef3fb");
        Color muted = Color.FromHtml("#a6b6d0");
        Color accent = Color.FromHtml("#84b4ff");

        // 半透明"玻璃"面板：背景那张实景图能透出来，界面才不会是一块死灰。
        // 注意 alpha 不能太低 —— 文字仍然压在这层上，0.8 左右是"看得见底图又保证对比度"的分界。
        Color panelBg = new(0.055f, 0.086f, 0.145f, 0.82f);
        Color panelBorder = new(0.62f, 0.78f, 1f, 0.22f);
        Color buttonBg = new(0.100f, 0.155f, 0.245f, 0.88f);
        Color buttonHover = new(0.170f, 0.300f, 0.500f, 0.96f);
        Color buttonPressed = new(0.130f, 0.230f, 0.400f, 0.97f);
        Color buttonOff = new(0.085f, 0.105f, 0.150f, 0.55f);

        Theme theme = new()
        {
            DefaultFont = font,
            DefaultFontSize = 17,
        };

        theme.SetColor("font_color", "Label", text);
        theme.SetColor("font_color", "Button", text);
        theme.SetColor("font_color", "OptionButton", text);
        theme.SetColor("font_color", "LineEdit", text);
        theme.SetColor("font_color", "TextEdit", text);
        theme.SetColor("font_color", "RichTextLabel", text);
        theme.SetColor("font_color", "TooltipLabel", text);
        theme.SetColor("font_color", "PopupMenu", text);
        theme.SetColor("font_disabled_color", "Button", muted);
        theme.SetColor("font_hover_color", "Button", Color.FromHtml("#ffffff"));

        theme.SetStylebox("panel", "PanelContainer", PanelStyle(panelBg, panelBorder, 14, 16));
        theme.SetStylebox("panel", "PopupMenu", PanelStyle(new Color(0.06f, 0.09f, 0.15f, 0.96f), panelBorder, 10, 8));
        theme.SetStylebox("normal", "Button", PanelStyle(buttonBg, panelBorder, 10, 12));
        theme.SetStylebox("hover", "Button", PanelStyle(buttonHover, accent, 10, 12));
        theme.SetStylebox("pressed", "Button", PanelStyle(buttonPressed, accent, 10, 12));
        theme.SetStylebox("disabled", "Button", PanelStyle(buttonOff, new Color(0.45f, 0.55f, 0.72f, 0.16f), 10, 12));
        theme.SetStylebox("focus", "Button", PanelStyle(new Color(0, 0, 0, 0), accent, 10, 12));

        theme.SetStylebox("normal", "OptionButton", PanelStyle(buttonBg, panelBorder, 9, 10));
        theme.SetStylebox("hover", "OptionButton", PanelStyle(buttonHover, accent, 9, 10));
        theme.SetStylebox("pressed", "OptionButton", PanelStyle(buttonPressed, accent, 9, 10));
        theme.SetStylebox("normal", "LineEdit", PanelStyle(new Color(0.04f, 0.06f, 0.10f, 0.88f), panelBorder, 9, 10));
        theme.SetStylebox("focus", "LineEdit", PanelStyle(new Color(0.04f, 0.06f, 0.10f, 0.88f), accent, 9, 10));
        theme.SetStylebox("normal", "TextEdit", PanelStyle(new Color(0.04f, 0.06f, 0.10f, 0.88f), panelBorder, 9, 10));
        theme.SetStylebox("focus", "TextEdit", PanelStyle(new Color(0.04f, 0.06f, 0.10f, 0.88f), accent, 9, 10));

        theme.SetConstant("separation", "VBoxContainer", 10);
        theme.SetConstant("separation", "HBoxContainer", 10);
        return theme;
    }

    private static StyleBoxFlat PanelStyle(Color bg, Color border, int radius, int padding)
    {
        StyleBoxFlat box = new()
        {
            BgColor = bg,
            BorderColor = border,
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            CornerRadiusTopLeft = radius,
            CornerRadiusTopRight = radius,
            CornerRadiusBottomLeft = radius,
            CornerRadiusBottomRight = radius,
            ContentMarginLeft = padding,
            ContentMarginTop = padding,
            ContentMarginRight = padding,
            ContentMarginBottom = padding,
        };
        return box;
    }
}
