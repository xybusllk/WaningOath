# 未尽之誓 / WaningOath

> **延绵若梦，终至如约。**

一个自研世界观（CCRF / UNEG / 暴风眼 / 掠夺者 × 弥尘异常 × 三重访问协议）的
**现代二次元回合制养成游戏**。给家人和朋友玩，没有付费、没有内购。

- **战斗**：崩铁式行动条 + 绝区零式属性异常积蓄条 + 全队共享的「共鸣指数」+ 比率减伤
- **养成**：等级 80 / 突破 / 技能 14 级 / **权阶协议**（同卡突破 6 阶）/ 武器覆写 5 阶 / 6 部位套装
- **抽卡**：叫「权限申请」，160 协议原码一抽，0.6% 五星、55 抽软保底、80 抽小保底
- **当前内容**：主线**序章**（S0~S9 全流程可玩）+ 13 名可玩角色 + 完整的养成 / 抽卡 / 图鉴闭环

![大厅](docs/screenshots/home.png)
![剧情](docs/screenshots/story-s5.png)

> 截图里的人物与场景是 AI 生成的美术资源，**不随本仓库分发**，见 [ASSETS.md](ASSETS.md)。

---

## 技术栈

| 层 | 技术 | 说明 |
|-|-|-|
| 客户端 | **Godot 4.7.1（.NET 版）+ C#** | **全部界面由代码搭建**（只有一个 `Main.tscn`），无绝对像素定位 |
| 战斗内核 | .NET 9 / C# | `WaningOath.Core`：行动条、伤害管线、属性异常、权阶、装备、**确定性 RNG** |
| 服务端 | .NET 9 / C# | `WaningOath.Server`：`TcpListener` + 手写最小 HTTP/1.1，零依赖 |
| 离线模拟 | .NET 9 / C# | `WaningOath.Sim`：`battle` / `verify` / `batch` / `loadout` / `validate` |
| 数据 | JSON | `data/`：13 角色 / 5 敌人 / 29 关卡 / 64 状态 / 17 武器 / 13 套装 |
| 图标 | 已随包 | 技能 / 权阶 / 固有 / 叙事图标为项目自有资源，直接放在 `Assets/Icons/` |

**服务端是唯一权威**：进度、结算、掉落全部在服务器侧算，客户端不留进度副本。
战斗重放由 `battle/verify` 复核——客户端提交 `{choices, claimedOutcome, claimedLogHash}`，
服务端自己算一遍再比对，对不上就 409。

---

## 目录结构

```
WaningOath.sln              四个工程的解决方案
Directory.Build.props       共用编译设置（TreatWarningsAsErrors = true）
nuget.config                离线包源（指向 Godot 自带的 nupkgs）

data/                       游戏静态数据（角色 / 状态 / 武器 / 套装 / 关卡 / 表）
WaningOath.Core/            战斗内核 + 数据模型 + 校验器
WaningOath.Sim/             命令行模拟器（验收都在这里跑）
WaningOath.Server/          游戏服务端
WaningOath/                 Godot 客户端（Scripts/ + Assets/）
tools/                      构建 / 运行 / 冒烟 / 诊断脚本
docs/screenshots/           界面预览
```

> 设计文档（战斗 / 数值 / 经济 / 角色表）、剧情正文与美术需求**不在本仓库**：
> 它们是制作组的内部资料，公开仓库只放"编译与运行所需的东西"。

---

## 快速开始

### 依赖

1. **.NET SDK 9**（本机开发时放在 `.dotnet/`，见 `tools\env.cmd`；也可用全局安装的 SDK）
2. **Godot 4.7.1 — .NET 版**（不是标准版，标准版不支持 C#）
   解压到 `Godot4/Godot_v4.7.1-stable_mono_win64/`
   —— `nuget.config` 把包源指向它的 `GodotSharp\Tools\nupkgs`，路径必须一致
3. 中文字体（可选）：`Noto Sans SC` → `WaningOath/Assets/Fonts/NotoSansSC-VF.ttf`
4. 美术（可选）：见 [ASSETS.md](ASSETS.md)，不放也能跑完整流程

### 构建与验收

```bat
tools\build.cmd                                    :: 四个工程，0 警告 0 错误是硬门禁
tools\run-sim.cmd validate                         :: 数据校验（31 条，0 错误）
tools\run-sim.cmd verify                           :: 验算用例
tools\run-sim.cmd batch                            :: 四档投入 × 四个靶场 × 64 种子
tools\godot-smoke.ps1                              :: 客户端 22 屏 × 两种分辨率（无头）
```

### 跑起来

```bat
tools\run-server.cmd        :: 起 /api/game/*（TcpListener）
tools\run-client.cmd        :: 起 Godot 客户端（先建 Debug 程序集）
```

---

## 已知限制

- **美术缺位**：立绘与场景图不进库，克隆下来能玩但"没画"（见 [ASSETS.md](ASSETS.md)）。
- **没有音频**：BGM / 音效 / 配音全部为 0，客户端也没有播放通路。
- **数据校验有 60 条警告**（0 错误）：主要是历史遗留的未登记技能位与一处面板偏差，改数据前先看 `WaningOath.Core/Data/Validator.cs` 里对应的校验项。
- **只支持 Windows 脚本**：`tools/*.cmd` 是 cmd 批处理；Linux/macOS 需要自己改路径。
- **本机工具链是"工作区内自带"式**：`.dotnet/` + `Godot4/` 都不进库。

---

## 许可

| 范围 | 许可证 |
|-|-|
| 源代码 | **PolyForm Noncommercial License 1.0.0** — [LICENSE](LICENSE) |
| 创作内容（数据 / 剧情 / 文档） | **CC BY-NC 4.0** — [LICENSE-CONTENT](LICENSE-CONTENT) |
| 美术资源 | **不包含、不授权** — [ASSETS.md](ASSETS.md) |
| 第三方 | 各自的许可证 — [NOTICE](NOTICE) |

简言之：**可以读、可以改、可以自己玩，不可以商用**；商业授权需单独联系。

## 致谢

见 [CREDITS.md](CREDITS.md)。
