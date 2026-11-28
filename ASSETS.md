# 美术资源说明（ASSETS）

> 一句话：**本仓库不含角色立绘与场景图。** 它们是 AI 生成资源，著作权归属不明晰，
> 所以既不随仓库分发，也不被 [LICENSE-CONTENT](LICENSE-CONTENT) 授权。
> 代码与数据是完整的，克隆下来**能编译、能跑、能过验收**，只是没有画。

---

## 1. 为什么不放进仓库

| 资源 | 来源 | 著作权状态 | 处理 |
|-|-|-|-|
| 角色立绘（9 名） | AI 图像生成（豆包 / Seedream 系，成品带「豆包 AI 生成」水印） | **不明**：AI 生成物的可版权性在现行法下没有定论，生成工具的条款也各有保留 | **不提交、不授权** |
| 场景图（4 张） | 同上 | 同上 | **不提交、不授权** |
| 技能 / 权阶 / 固有 / 叙事图标，132 张 | 项目自有的几何定义（由制作组的图标工具链生成） | 项目自有 | **提交** |
| `Noto Sans SC` 字体 | 第三方（SIL OFL 1.1） | OFL，但**不随仓库分发**以避免体积与再分发清单 | 自行下载，见 §4 |

> 仓库里保留的 `README.txt` 只说明每个目录该放什么，不含任何图片。

---

## 2. 游戏在缺图时的行为（软着陆）

客户端**不会因为缺图而崩溃或黑屏**：

- 立绘取不到 → 那一格不画，其余界面照常（`Portraits.TryLoad` 返回 `null`）。
- 场景图取不到 → 不放背景层，只剩主题底色（`Backgrounds.TryLoad` 同样返回 `null`）。
- 启动自报会把"加载了几张 / 缺几张"打出来，`tools\godot-smoke.ps1` 会读这一行。

所以**没有美术也能跑完整流程**：登录 → 序章 → 战斗 → 结算 → 养成 → 抽卡。

---

## 3. 在本地补齐美术资源

把文件**按下列文件名**放进去即可，代码里的名字是**冻结**的（改名 = 静默丢图）：

```
WaningOath/Assets/Portraits/          立绘，透明底 PNG，2:3，宽边 ≥1200
  aino-base.png            星见爱乃
  star-oath-base.png       千语星遥·群星誓约
  jingxing-base.png        千语镜星
  lin-base.png             林靖远
  belrand-base.png         贝尔兰德
  valeria-base.png         瓦列里娅
  vera-base.png            薇拉·约克
  karni-base.png           卡尔尼菲琪亚
  rolanda-base.png         罗兰达
  darknight-base.png       千语星遥·至暗长夜
  darknight-silhouette.png 至暗长夜·序章剪影
  shouyueren-f-base.png / shouyueren-m-base.png   守约人（女/男）
  xingyao-base.png         千语星遥

WaningOath/Assets/Enemies/            异常体立绘，透明底 PNG，2:3
  anom-ccrf-017-base.png  候门（CCRF-017）

WaningOath/Assets/Backgrounds/        场景图，16:9 PNG（或 JPEG），≤1920×1080 起
  site14-hill-dawn.png     后山·晨雾
  site14-hill-ridge.png    远眺设施群（也当大厅背景的退路）
  battle-site14-hill.png   战斗·路边树丛
  site14-canteen.png       食堂
  hall-main.png            （可选）大厅专用主视觉，代码优先取它
```

技能 / 权阶 / 固有 / 叙事图标（`WaningOath/Assets/Icons/`）是**项目自有资源**，
已经随仓库分发，不需要你自己补。

属性图标（`element-*.png`，共 8 个）暂缺；它是界面色标用的，缺了不影响任何流程。

---

## 4. 字体

界面用的是 **Noto Sans SC**（SIL Open Font License 1.1），仓库不含该字体文件。

1. 下载：https://fonts.google.com/noto/specimen/Noto+Sans+SC
2. 放到：`WaningOath/Assets/Fonts/NotoSansSC-VF.ttf`
3. 不放也能跑：客户端会**退回系统字体**（Windows 上是微软雅黑），
   只是换一台没有中文字体的机器会显示成方块。

---

## 5. 界面预览图的说明

`docs/screenshots/` 里的两张图**是本项目的界面截图**，其中包含上面第 1 节所述的
AI 生成美术。它们**仅用于文档说明**，不代表对这些美术的任何授权或再许可。

---

## 6. 打包产物不含在本仓库

本仓库**只有源码、数据、文档与工具**，不放任何可执行发布包（`dist/` 下的 exe / pck / zip 都在 `.gitignore` 里）。
原因同上：发布包里含 AI 生成的立绘与场景图，公开分发会有版权风险。
需要能直接跑的版本，请在本地用 `tools\build-release.ps1` 自己出包，或向制作组索取。

## 7. 权利主张

本仓库不含上述美术文件。若你认为仓库中仍有内容侵犯了你的权利，
请在本仓库开 issue 说明具体文件，我们会移除相关内容。
