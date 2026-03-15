# 项目结构说明

本项目当前按“捕获 -> 快照 -> 恢复 -> UI/补丁 -> 场景验证”的方向组织。

## 目录职责

- `undo the spire2/Capture`
  - 负责把官方运行时状态提炼成可保存的 undo 数据。
- `undo the spire2/Core`
  - 放撤销/重做主流程、choice 分流、核心枚举、快照入口和内部工具。
- `undo the spire2/Infrastructure`
  - 放日志、反射、序列化等横切工具。
- `undo the spire2/Patches`
  - 放 Harmony patch，原则上只做“拦截与转发”，不承载复杂业务。
- `undo the spire2/Restore`
  - 放具体恢复 codec，把快照状态重新接回官方对象。
- `undo the spire2/Snapshot`
  - 放可序列化的状态模型，不掺杂控制流程。
- `undo the spire2/Scenarios`
  - 放回归场景、覆盖率审计和调试执行器。
- `undo the spire2/UI`
  - 放 HUD 和视觉归一化逻辑。

## 根目录约定

项目根目录只保留下面几类文件：

- Mod/Godot 入口与配置
- 构建配置
- 清晰指向模块职责的文档

不再把新的核心业务源码直接放在项目根目录。

## 后续维护约束

- 新的战斗快照捕获逻辑优先放进 `Capture/`。
- 新的可序列化状态模型优先放进 `Snapshot/`。
- 新的恢复路径优先放进 `Restore/`，不要继续堆进 `UndoController`。
- `Patches/` 中只保留 patch 入口；复杂逻辑应委托给 `Core/` 或服务类。
- 文件级注释只说明“这个文件负责什么”，复杂算法只在必要代码段附近写短注释。
