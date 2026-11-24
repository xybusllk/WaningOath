# data/gears/ —— 装备套装（M7 才实装）

这个目录**当前是空的，这是有意的**。

## 现状

`data/characters/*.json` 里的 `gearSet` 字段已经指向了规划中的套装 id：

|角色|`gearSet`|
|-|-|
|守约人|`gear-ruyue-zhizheng`|
|千语星遥|`gear-yiku-suocang`|
|贝尔兰德|`gear-slag-and-ember`|
|爱丽丝|`gear-dash-to-the-finish`|

但这四个 `.json` **都还没写** —— 因为装备系统整体属 **M7 养成闭环**，
而 `07` 的数值纪律是"精确值待 `M2` 实测"。

## 校验器的口径（`05 §7` 校验 19 的邻居）

M3 数据校验器对"套装未定义"报**警告**、不报错：

```
[警告] 校验 M3-refs · char-shouyueren.gearSet：'gear-ruyue-zhizheng' 尚未定义（装备系统属 M7）
```

**报错会让 M3 永远过不去**，而"这两件事哪个先做"是排期问题、不是数据错误。
⇒ M7 落地时把套装文件补齐，然后**把这条警告升级成错误**（改 `Validator.CheckReferences`）。

## 目录结构（`103 §2.2`）

```
data/gears/
├── sets/     # 套装（2 件 / 4 件效果）—— 校验器读这里
└── parts/    # 部位（暂无 schema）
```

⚠️ `parts/` 目前连 schema 都没有（`101` 只定义了**武器**的三件结构，
装备的部位划分还没设计）。**不要**在没有 schema 的情况下先建文件。
