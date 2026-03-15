# UndoTheSpire2

`UndoTheSpire2` 是一个面向《Slay the Spire 2》的战斗内撤销/重做 Mod。它的目标不是做“全局时间倒流”，而是尽可能稳定地支持单人战斗中的误操作回退。

当前版本已经实现：
- 战斗内多步 `Undo / Redo`
- HUD 常驻按钮
- `Ctrl+Z / Ctrl+Y` 热键
- 普通出牌、用药、弃药、结束回合的瞬时回退
- `Choose A Card` / 手牌选择界面的回退
- `Choose A Card` 在撤销后改选其他选项时的瞬时分支切换

当前版本明确不做：
- 地图、事件、商店、篝火、战后奖励的回退
- 多人模式兼容
- 跨重启保留撤销历史
- 手柄专用交互

## 为什么这个 Mod 的实现方式和一代不同

一代 `Undo the Spire` 的思路更接近“细粒度 patch + 游戏状态重建”。

二代的代码结构、Godot UI、网络同步层和战斗状态对象都和一代差异很大，因此本项目没有直接照搬一代实现，而是采用了下面这套二代专用方案：
- 普通战斗操作优先使用 `NetFullCombatState` 做原地瞬时恢复。
- 战斗 replay 只作为历史记录和分支辅助，不作为普通路径的主要恢复方式。
- 对“选择一张牌 / 选择手牌”这种暂停中的 choice，额外构建 synthetic choice 层，以避免整场黑屏重进战斗。

## 实现概览

### 1. 快照系统
每次关键玩家动作发生前，Mod 会捕获当前战斗快照，主要包括：
- `NetFullCombatState`
- 当前回合侧、回合同步状态
- `nextActionId / nextHookId / nextChecksumId`
- replay 事件计数
- choice 元数据（如当前是否停在选牌界面、选项卡内容等）

核心代码：
- `UndoController.cs`
- `UndoSnapshot.cs`
- `UndoCombatFullState.cs`
- `UndoCombatReplayState.cs`

### 2. 普通动作的瞬时恢复
对普通动作（如出牌、用药、结束回合），撤销/重做时优先：
1. 直接把保存下来的 `NetFullCombatState` 应用回当前战斗。
2. 强制同步 `CombatManager`、`ActionQueueSet`、`NPlayerHand`、`NEndTurnButton` 等运行时/UI状态。
3. 重建手牌、出牌区、抽牌堆/弃牌堆计数等展示层。

这条路径的目标是：
- 不黑屏
- 不 replay
- 不看到动作重新播放
- 恢复后立即可继续出牌和结束回合

### 3. Choice 的瞬时恢复
choice 是二代里最麻烦的一部分，因为引擎没有公开、通用、可序列化的“暂停中 action continuation”。

当前实现分两层：
- `HandSelection` / `Choose A Card` 会先回到锚点快照（也就是 choice 打开前的稳定状态）。
- 再由 Mod 自己重新打开 synthetic choice UI。

对于 `Choose A Card`，如果用户撤销后不再选原来的 `A`，而是改选 `B/C`：
- 旧方案只能命中缓存过的原始分支，因此会卡在确认按钮。
- 当前方案会把“原分支快照”当作模板，识别出该 choice 在战斗状态里实际新增的那张牌，再把它替换为新选中的目标卡，生成一个新的瞬时分支快照并立即应用。

因此现在 `A -> undo -> B/C` 已经可以瞬时生效，不再依赖 replay。

### 4. HUD 与输入
UI 没有直接改原版场景，而是通过 patch 在战斗 UI 激活时动态挂载 HUD：
- 左键：Undo
- 右键：Redo
- tooltip 会根据最近快照显示动作名

同时支持：
- `Ctrl+Z` -> Undo
- `Ctrl+Y` -> Redo

## 项目结构

目录边界的详细说明见 ARCHITECTURE.md

核心文件：
- `MainFile.cs`：Mod 入口，Harmony patch 注册
- `UndoController.cs`：撤销/重做核心状态机
- `UndoSnapshot.cs`：快照对象
- `UndoCombatFullState.cs`：战斗完整状态封装
- `UndoCombatReplayState.cs`：replay 历史与分支记录
- `UndoChoiceCapturePatch.cs`：捕获 choice 元数据
- `UndoCombatReplayPatch.cs`：捕获 replay 事件和 player choice 结果
- `UndoHud.cs` / `UndoHudButton.cs`：HUD、按钮、tooltip
- `UndoRunLifecyclePatch.cs`：战斗开始/结束时清理历史
- `UndoDebugLog.cs`：额外调试日志
- `ModLocalization.cs`：本地化加载

资源与配置：
- `mod_manifest.json`
- `project.godot`
- `export_presets.cfg`
- `UndoTheSpire2/localization/*.json`

当前目录约定：
- 项目根目录只保留入口、构建配置和文档
- 核心业务源码统一放在 `Capture/`、`Core/`、`Restore/`、`Snapshot/`、`Patches/`、`UI/`、`Scenarios/`

## 构建与发布

当前工程使用：
- .NET 9
- `Godot.NET.Sdk/4.5.1`
- 游戏自带 `0Harmony.dll`
- 反编译后的 `sts2.dll` 作为 API 参考和编译引用

当前项目的构建/发布行为：
- `dotnet build` 会输出 DLL，并自动复制到游戏 `mods` 目录。
- `dotnet publish` 后会调用 Godot headless 导出 `.pck`。

当前项目里默认使用的关键路径：
- 游戏目录：`\SteamLibrary\steamapps\common\Slay the Spire 2`
- Godot：`\Godot_v4.5.1-stable_mono_win64\Godot_v4.5.1-stable_mono_win64_console.exe`
- 构建缓存：\undo-the-spire2-cache`

## 参考资料 / 致谢

这个项目主要参考了以下副本与资料：

### 直接参考项目
- [`ModTemplate-StS2-master`](https://github.com/Alchyr/ModTemplate-StS2)
  - 作用：二代 Mod 模板，提供 Godot/.NET 项目骨架、manifest、导出方式
- [`sts2-quickRestart-main`](https://github.com/freude916/sts2-quickRestart)
  - 作用：验证二代中 run/combat restore、重新载入路径的可行性
- [`undo-the-spire-main`](https://github.com/filippobaroni/undo-the-spire)
  - 作用：一代 Undo 的产品目标、交互设计、功能边界参考
- `slay the spire2`
  - 作用：二代 `sts2.dll` 反编译目录，用于确认引擎结构、战斗状态、choice 流程、UI 节点和 replay 机制

## 已知限制

- `Choose A Card` 的“改选分支瞬时恢复”目前是基于战斗状态差量合成实现的，覆盖了当前已验证的卡牌/药水选牌场景，但还不是一个对所有未来 choice 类型都自动成立的通用框架。
- 当前日志里的 checksum divergence 仍未完全解决。
- 当前工程仍然是“强功能优先”，不是“日志零噪声优先”。

## 反馈建议

如果你在使用过程中遇到以下情况，欢迎反馈：

- 某张牌 / 某个药水撤销后不正确
- 某类选牌场景无法正常回退
- 某些特效或动画表现异常
- 某些回合切换后状态不一致

反馈时如果能提供：

- 触发该问题的牌 / 药水 / 遗物名称
- 复现步骤
- 游戏日志，它通常位于C:\Users\你的用户名\AppData\Roaming\SlayTheSpire2\undo-the-spire2

会非常有助于定位问题。
