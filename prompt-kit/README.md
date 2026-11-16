# prompt-kit 维护说明

功能 1（图片 → 建模提示词）的源文件和构建工具。分发给用户的是 `dist/` 文件夹，用户看的说明在 `dist/README.md`。

## 目录

```
prompt-kit/
├── src/                      唯一的源文件，只改这里
│   ├── common/               通用规则、分级标准、输出格式、标准值、填写模板
│   ├── levels/               L0–L4 各等级流程
│   ├── macro/                宏生成规则 + 实测过的 API 速查表
│   ├── claude/               SKILL.md 外壳 + 安装说明
│   ├── chatgpt/              GPT 指令外壳 + 安装说明
│   ├── prompts/              通用提示词外壳 + 使用说明
│   └── README.md             会复制成 dist/README.md
├── tests/
│   ├── sw-api-selftest.ps1   在本机 SW 上逐项验证速查表里的 API
│   └── example-macro-check.ps1  按示例宏的调用顺序建零件并核对体积
├── build.ps1                 从 src 生成 dist
└── dist/                     生成结果（不要手改）
```

外壳文件里用 `{{include:相对 src 的路径}}` 引用其他文件，构建时展开，支持嵌套。这样同一份内容只维护一处，三种交付物始终一致。

## 修改流程

1. 改 `src/` 里的文件。
2. 运行构建：

   ```
   powershell -ExecutionPolicy Bypass -File .\build.ps1
   ```

   构建会检查：引用是否全部展开、GPT 指令是否超过 8000 字符、SKILL.md 的 name 和 description 是否符合要求，并打印 zip 内的文件列表。
3. 改了 `src/macro/sw-api-cheatsheet.md` 里的 API 用法时，先在 `tests/` 里补对应的测试，再在装有 SolidWorks 的电脑上运行：

   ```
   powershell -ExecutionPolicy Bypass -File .\tests\sw-api-selftest.ps1
   powershell -ExecutionPolicy Bypass -File .\tests\example-macro-check.ps1
   ```

   测试只会新建临时零件，测完不保存直接关闭；运行前请先保存手头的工作。

## 已知结论（来自实测，修改速查表前请先看）

- 中文版的基准面名称是中文，宏里不能按名字找基准面。
- 默认零件模板可能是 `~BLANK_PART_TEMPLATE.prtdot` 占位名，用它新建零件时好时坏。
- `SelectByID2` 按坐标选边时，会沿当前视线方向选到别的边，因此改为按几何距离选。
- 单向"完全贯穿"切除的方向朝向空的一侧时会失败，通孔统一用两个方向都贯穿。
- PowerShell 5.1 必须用带 BOM 的 UTF-8 保存含中文的 .ps1 文件，否则会解析出错。
- 中文 Windows 的 VBA 编辑器按 GBK 显示，宏代码里不能出现 Ø、³、– 这类字符。

## 测试 skill 效果时注意

速查表里的示例宏建的是"80×50×8 底板 + 608 轴承凸台"。用这个零件当测试图，AI 可能直接照抄示例，测试就失去意义了。测试请换成别的零件，最好用真实照片。

现成的回归测试在 `tests/fixtures/flange/`：三张图、真值表，以及 AI 上次生成的宏。改完 skill 后用同样的消息重测，对比真值。
