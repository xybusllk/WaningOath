# Credits

## 项目

- **未尽之誓 / WaningOath**
- 维护：[xybusllk](https://github.com/xybusllk)
- 仓库：https://github.com/xybusllk/WaningOath

## 设计与内容

- [宪法号五等风帆护卫舰](https://github.com/xybusllk) — 世界观、角色、数值与剧情
- 制作组 — 剧情正文与美术协作

## 代码与工具链

- 战斗内核（`WaningOath.Core`）：行动条、伤害管线、属性异常、权阶、装备、确定性 RNG
- 数据校验器（`WaningOath.Core/Data/Validator.cs`）：31 条校验，含故障注入测试
- 服务端（`WaningOath.Server`）：`TcpListener` + 手写最小 HTTP/1.1、服务端权威存档
- 客户端（`WaningOath/`）：Godot 4 + C#，**全部界面由代码搭建**，无 `.tscn` 场景文件
- 图标工具链（`iconforge/`）：spec JSON → 校验 → 渲染

## 第三方

- Godot Engine — MIT（不随仓库分发）
- Noto Sans SC — SIL OFL 1.1（不随仓库分发）
- .NET SDK 9 — MIT（不随仓库分发）

许可证细节见 [NOTICE](NOTICE)。

## 未包含的资源

角色立绘与场景图为 AI 生成资源，著作权不明晰，**不在本仓库内**，见 [ASSETS.md](ASSETS.md)。

## 许可证

- 源代码：PolyForm Noncommercial License 1.0.0
- 创作内容：CC BY-NC 4.0
- 第三方组件：各自的许可证
