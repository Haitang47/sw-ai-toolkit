---
name: sw-image-to-model
description: 根据实物照片在 SolidWorks 中逆向建模。用户发来零件、产品或机构的照片或截图，想在 SW 里画出来、估算尺寸、要建模步骤、特征树、装配与配合方案，或要自动建模的 SW 宏时使用。
---

# 图片 → SolidWorks 建模

{{include:common/rules.md}}

## 工作流程

**第 1 步：看图分级。** 按下表判断等级。用户已经指定等级时，以用户为准。

{{include:common/level-criteria.md}}

**第 2 步：检查信息够不够。** 没有任何参考尺寸，或者关键方向完全没拍到时，先读 `references/L0-triage.md`，按里面的格式告诉用户缺什么、怎么补拍，然后停下来等用户补充。用户明确说"先按估计的来"时就继续，把相关尺寸标成【比】或【猜】。

**第 3 步：读对应等级的流程文件**，按里面的要点分析：
- L1 → `references/L1-simple.md`
- L2 → `references/L2-mechanical.md`
- L3 → `references/L3-complex-shape.md`
- L4 → `references/L4-assembly.md`

**第 4 步：估尺寸时查** `references/standard-values.md`，能套标准值的就套。

**第 5 步：按** `references/output-format.md` **输出。** L4 使用 L4 文件里的专用结构。

**第 6 步：用户要宏时**（"生成宏"、"自动建模"、"写个 VBA"），先读 `references/macro-rules.md` 和 `references/sw-api-cheatsheet.md`，只用速查表里实测过的函数生成宏。

**第 7 步：L4 之后**，用户说"开始建 P3"这类话时，按该零件的等级重新走第 3–5 步，并沿用 L4 里定好的接口尺寸。
