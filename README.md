# Undo the Spire 2

**Language:** **中文** | [English](./README_EN.md)

`Undo the Spire 2` 是一个面向《Slay the Spire 2》的战斗内撤销 / 重做 Mod。  
它的目标不是“全局时间倒流”，而是尽可能稳定地回退单人战斗中的误操作。

## 功能

- 战斗内多步 `Undo / Redo`
- HUD 常驻按钮
- `Ctrl+Z / Ctrl+Y` 快捷键
- 普通出牌、用药、弃药、结束回合的即时回退
- `Choose A Card` / 手牌选择界面的回退
- 撤销后在部分选牌场景中改选其他选项

## 当前不支持

- 地图、事件、商店、篝火、战后奖励的回退
- 多人模式
- 跨重启保留撤销历史
- 手柄专用交互

## 下载

- [前往 Releases 页面](../../releases)
- 发布包通常包含 `UndoTheSpire2.dll` 与 `UndoTheSpire2.json`

## 安装

1. 下载发布包，或自行构建得到 `UndoTheSpire2.dll` 与 `UndoTheSpire2.json`
2. 将这两个文件放入游戏目录的 `mods` 文件夹
3. 如果你之前装过旧版并且目录里还有 `UndoTheSpire2.pck`，请先删除它

## 使用方式

- 左键点击 HUD 按钮：`Undo`
- 右键点击 HUD 按钮：`Redo`
- 快捷键：`Ctrl+Z` / `Ctrl+Y`
- 鼠标悬停按钮时会显示最近一步可撤销或可重做的动作

## 已知说明

- 本 Mod 优先服务单人战斗中的常见误操作回退，不承诺覆盖所有未来卡牌、药水或 choice 类型
- 某些复杂选牌、特效或动画场景仍可能出现异常
- 如果撤销后状态不正确，请优先反馈具体牌、药水、遗物和复现步骤

## 反馈

反馈时如果能提供以下信息，会更容易定位问题：

- 触发问题的牌 / 药水 / 遗物名称
- 复现步骤
- 游戏日志

日志通常位于：

`C:\Users\你的用户名\AppData\Roaming\SlayTheSpire2\undo-the-spire2`

## 致谢

- [Undo the Spire (STS1)](https://github.com/filippobaroni/undo-the-spire)
- [ModTemplate-StS2-master](https://github.com/Alchyr/ModTemplate-StS2)
- [sts2-quickRestart-main](https://github.com/freude916/sts2-quickRestart)
