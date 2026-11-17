# swkit 维护说明

SW 工具箱的桌面程序（界面版 `SwKit.exe`）和命令行版（`swkit-cli.exe`）。用户看的说明在 `使用说明.md`。

## 技术选型

- C#，目标 .NET Framework 4.8：Windows 10/11 自带，同事拿到 exe 直接能用。
- 界面用 WPF。布局和样式写在 `.xaml` 里，编译时作为资源嵌入 exe，运行时用 `XamlReader` 加载；XAML 里不写事件和 `x:Class`，交互逻辑都在 `.cs` 里通过 `FindName` 绑定。
- SolidWorks 接口用 `/link` 把接口定义嵌入 exe，运行时不需要附带 SolidWorks 的 dll。编译用的是本机 SolidWorks 2025 的接口定义，只调用 2023 已有的方法（SolidWorks 的 COM 接口只在末尾追加新方法，旧方法位置不变）。
- 不依赖 NuGet，也不需要 .NET SDK。用 Visual Studio 自带的新版 C# 编译器直接编译。

## 目录

```
swkit/
├── src/
│   ├── Core/      导出引擎（界面版和命令行版共用）
│   │   ├── SwSession.cs        启动隐藏的独立 SolidWorks，并按进程号连接；绑定作业对象，程序崩溃时一起结束
│   │   ├── ExportRunner.cs     打开 → 激活 → 导出 → 校验输出 → 关闭；导出设置的修改结束后全部恢复
│   │   ├── ExportFormats.cs / ExportOptions.cs / ExportItem.cs / PrefSnapshot.cs / ComMessageFilter.cs
│   │   ├── Urdf/
│   │   │   ├── AssemblyReader.cs   读装配体：零部件位姿、质量属性（零件文档的质量属性 × 位姿）、配合几何；展开子装配体（柔性总是展开，刚性默认展开）
│   │   │   ├── JointSolver.cs      配合 → 约束行 → 零空间求剩余自由度；并查集合并刚体；闭链整体求运动空间；建树；限位配合；可空转零件 / 螺旋桨
│   │   │   ├── RobotModel.cs / Geo.cs   连杆、关节、坐标系计算；向量、矩阵、Jacobi 特征分解
│   │   │   ├── MeshBuilder.cs      SolidWorks 按零部件拆分导出 STL，按连杆合并，算体积质心，简化过大的网格并生成碰撞网格
│   │   │   ├── UrdfWriter.cs       写同时支持 ROS 1 和 ROS 2 的功能包（URDF、launch、rviz、package.xml、CMakeLists）
│   │   │   ├── UrdfValidator.cs    重新读入 URDF 做结构、惯量、网格、零位、关节轴检查，写校验报告
│   │   │   └── UrdfExporter.cs     串起上面几步；导出时把固定连接并入上级连杆
│   │   ├── Parts/
│   │   │   ├── PartGeom.cs     零件的几何描述：草图轮廓（直线、圆弧、样条）+ 拉伸 / 旋转 / 切除，约定轴线沿 +Y
│   │   │   ├── Catalog.cs      标准件目录：ISO / GB 尺寸表和每种零件的建模步骤
│   │   │   ├── Gear.cs         渐开线直齿轮齿廓
│   │   │   ├── PartMesher.cs   不经过 SolidWorks 直接生成预览网格（带孔多边形耳切三角剖分）
│   │   │   ├── SwPartBuilder.cs 在后台 SolidWorks 里按几何描述建模、设材料和属性、保存，并核对体积
│   │   │   └── PartsLibrary.cs 零件库默认位置
│   │   ├── Fea/
│   │   │   ├── FeMesh.cs       调用 Gmsh 划二次四面体网格（STEP 或 .geo；可带背景网格 -bgm 做自适应），读 .msh 2.2，整理出每个面（编号 = STEP 的面）
│   │   │   ├── FeSolver.cs     材料表（33 种，塑性 / 脆性）、载荷、FeSystem（稀疏结构 + 单元位置表，按块并行组装）、IC(0)-PCG、节点应力、主应力、ZZ 误差估计、反力平衡
│   │   │   ├── FeAdaptive.cs   h 自适应：按单元误差算新尺寸，写 Gmsh 背景网格，重划重算直到收敛
│   │   │   ├── TopoOpt.cs      拓扑优化：SIMP + 体积加权密度过滤 + Heaviside 投影 + OC，线性四面体；薄板按列共用设计变量（只开穿透孔）；等值面提取（marching tetrahedra + 表面裁剪 + Taubin 平滑）、STL
│   │   │   ├── FeHoles.cs      「选同组孔」：同样大小、挨在一起的孔面（单链接聚类）
│   │   │   └── FeSamples.cs    自带示例（tools\samples）和按几何规则设约束载荷
│   │   ├── Dfm/                可制造性：DfmMesh（STL / STEP→Gmsh 表面网格、法向修复、面分类）、Bvh（射线加速）、DfmAnalyzer（悬垂、支撑、薄壁、摆放方向、可达性、内角、深腔、深孔、估算）、DfmSheet（钣金、板材切割）、DfmPowder（尼龙烧结、金属打印）、DfmMold（注塑）、DfmMaterials（各工艺的材料和价格参数）、DfmReport
│   │   ├── Report/             报告：ReportDoc（内容模型）、DocxWriter（手写 OpenXML，三线表、自动编号、GB/T 7714）、MarkdownWriter、CsvWriter
│   │   └── Workspace/          工作区：快速遍历、同名文件分组、引用检查（GetDocumentDependencies2）、版本历史库、SW 自动恢复文件
│   ├── Cli/       命令行版（McpServer.cs：swkit-cli mcp，MCP stdio 服务器，转发到界面版的本地接口）
│   └── App/       界面版
│       ├── Theme.xaml          设计系统：颜色、字体、按钮、开关、格式卡片、分段选择器、细滚动条
│       ├── MainWindow.xaml/.cs 窗口、侧栏导航、页面切换动画、SolidWorks 状态
│       ├── ExportPage.xaml/.cs 一键导出页面
│       ├── UrdfPage.xaml/.cs   URDF 导出页面（关节卡片、范围、预览滑条）
│       ├── RobotView.cs        Viewport3D 预览：旋转 / 平移 / 缩放，按关节值做正运动学
│       ├── PartsPage.xaml/.cs  标准件库页面（类型、参数、实时预览、生成、拖进 SolidWorks）
│       ├── FeaPage.xaml/.cs    应力分析页面（分步引导、示例、选面加约束和载荷、材料分类、四种结果显示、自适应加密、拓扑优化、使用指南）
│       ├── FeaView.cs          应力分析的 3D 视图：所有面一个网格 + 面颜色贴图（拾取按三角形查面）、云图（绝对纹理坐标）、拓扑结果；相机每帧更新一次
│       ├── DfmPage.xaml/.cs / DfmView.cs 可制造性页面（工艺切换、问题列表定位、摆放方向、报告）
│       ├── FeaPage.Report.cs   应力分析 / 拓扑优化的实验报告（300 dpi 截图、节点 CSV、方法段落）
│       ├── WorkspacePage.xaml/.cs 工作区页面（概览、重名检查、版本历史、崩溃找回）
│       ├── PromptKitPage.xaml/.cs AI 建模提示词：软件内阅读（Markdown.cs 渲染）、复制、安装包复制 / 另存 / 装到 Claude Code
│       ├── ApiServer.cs / ApiArgs.cs / ApiPage.cs 本地接口（127.0.0.1:47821，令牌在 %APPDATA%\SwKit\api.json）、参数工具、「AI 控制接口」页
│       ├── app.manifest / SwKit.exe.config PerMonitorV2 按屏幕缩放（还需要 AppMain.cs 里的 TargetFramework 声明）
│       ├── SwWorker.cs         专门调用 SolidWorks 的后台线程；后台实例空闲 10 分钟后自动关闭
│       └── Pages.cs / PromptKitPage.xaml / ComingSoonPage.xaml / FolderPicker.cs / Ui.cs / AppMain.cs
├── tools/gmsh/                 Gmsh 4.15.2（官网 gmsh.info 的 Windows 版，GPL，附许可证），发布时复制到程序旁边
├── tools/dfm-tests/             可制造性测试件（.geo 和生成的 STEP，特征尺寸已知，用来核对检测结果）
├── tools/samples/              应力分析示例零件（.geo → gmsh -0 -o 生成 STEP），发布时复制成「示例」文件夹
├── tests/
│   ├── _isolated-sw.ps1        测试共用：另外启动隐藏的 SolidWorks
│   ├── make-test-data.ps1      生成测试用的零件、法兰、钣金件、装配体、工程图
│   └── verify-outputs.ps1      检查 STL 包围盒 / 单位和 STEP 协议版本
├── build.ps1                   编译
└── release.ps1                 编译并打包发布 zip（包含提示词包）
```

## 编译和发布

```
powershell -ExecutionPolicy Bypass -File .\build.ps1
powershell -ExecutionPolicy Bypass -File .\release.ps1
```

需要 Visual Studio 2019 及以上（或 Build Tools）和本机安装的 SolidWorks。

## 测试

```
# 生成测试文件（会另外启动隐藏的 SolidWorks）
powershell -ExecutionPolicy Bypass -File .\tests\make-test-data.ps1 -OutDir D:\swkit-test\数据
# 命令行导出全部格式
.\bin\swkit-cli.exe export --to step,stl,3mf,iges,x_t,dxf_flat,pdf,dwg,dxf --out D:\swkit-test\输出 --subfolders D:\swkit-test\数据
# 检查结果
powershell -ExecutionPolicy Bypass -File .\tests\verify-outputs.ps1 -Path D:\swkit-test\输出
# 界面版自测：在屏幕外通过界面流程导出两轮，第二轮应复用后台 SolidWorks（约 10 秒）
.\bin\SwKit.exe --selftest D:\swkit-test\数据 D:\swkit-test\输出GUI D:\swkit-test\gui-selftest.txt
# 界面截图：把各页面渲染成 PNG，用来检查视觉效果
.\bin\SwKit.exe --snapshot D:\swkit-test\截图
# URDF：生成测试机械臂（底座固定；转台转动；大臂转动；滑块移动，带距离限位）
.\bin\swkit-cli.exe dev-make-robot D:\swkit-test\测试机械臂
.\bin\swkit-cli.exe urdf D:\swkit-test\测试机械臂\测试机械臂.SLDASM --name test_arm --out D:\swkit-test\urdf
# URDF 界面自测：分析 → 预览 → 导出，结果文本和截图写在结果文件旁边
.\bin\SwKit.exe --selftest-urdf D:\swkit-test\测试机械臂\测试机械臂.SLDASM D:\swkit-test\urdf-gui D:\swkit-test\urdf-gui.txt
# 标准件：先不用 SolidWorks 检查所有零件的几何和预览网格，再在后台 SolidWorks 里生成一组代表性零件并核对体积
.\bin\swkit-cli.exe parts check
.\bin\swkit-cli.exe parts selftest D:\swkit-test\标准件
# 标准件界面自测：通过界面生成 3 个零件，再导出 STEP / STL
.\bin\SwKit.exe --selftest-parts D:\swkit-test\标准件GUI D:\swkit-test\parts-gui.txt
# 应力分析：用理论解核对求解器（不需要 SolidWorks，约 20 秒）
.\bin\swkit-cli.exe fea bench
# 应力分析界面自测：读入零件（.sldprt 会启动后台 SolidWorks），固定最大的平面、在最远的平面上压 1000 N，计算并加密复核
.\bin\SwKit.exe --selftest-fea D:\swkit-test\数据\法兰.SLDPRT D:\swkit-test\fea-gui.txt
# 示例零件：自适应加密（L 形支架）、拓扑优化（悬臂块，约 1 分钟）
.\bin\swkit-cli.exe fea sample bracket --adaptive 0.03
.\bin\swkit-cli.exe fea sample cantilever --topo 0.3 --stl D:\swkit-test\topo.stl
.\bin\SwKit.exe --selftest-fea sample:cantilever D:\swkit-test\topo-gui.txt
# 任意零件的拓扑优化：力、扭矩可以重复（例如每个电机座一组），--png 输出俯视图，--mode auto|plate|3d
.\bin\swkit-cli.exe fea run 板.step --material "碳纤维板（3K 斜纹）" --fix 5,6,7 --force 18,19:0,13,0 --torque 18,19:0,1,0:0.25 --topo 0.4 --png D:\swkit-test\topo.png
# 界面自测「拓扑结果显示时换材料」和「选同组孔」：SWKIT_MATPICK_BC 指向 JSON（center、m1–m4 为逗号分隔的面编号）
.\bin\SwKit.exe --selftest-fea matpick:板.step D:\swkit-test\matpick.txt
# 并行开发时编译到另一个文件夹，互不锁文件
powershell -ExecutionPolicy Bypass -File .\build.ps1 -BinDir bin-dev
# AI 接口：屏幕外启动并开接口（接口信息写到指定文件，不碰用户设置），再用 MCP 调用
.\bin\SwKit.exe --api-test D:\swkit-test\api\api.json
$env:SWKIT_API_INFO = "D:\swkit-test\api\api.json"; '{"jsonrpc":"2.0","id":1,"method":"tools/list"}' | .\bin\swkit-cli.exe mcp
# 不同窗口大小下截图
$env:SWKIT_SNAP_SIZE = "980x640"; .\bin\SwKit.exe --snapshot D:\swkit-test\shots-small
```

## 已知结论（来自实测，修改前请先看）

- **不要在用户正在用的 SolidWorks 里做批量操作。** 2026-11-16 曾这样测试，导致用户的 SolidWorks 崩溃、未保存的修改丢失。现在导出和所有测试都在另外启动的隐藏 SolidWorks 里进行（每个进程在 ROT 里登记为 `SolidWorks_PID_进程号`）。
- **STL、IGES、工程图和钣金展开导出的是"活动文档"，不是调用 SaveAs 的那个文档**，所以导出前必须先 `ActivateDoc3`，并核对活动文档的路径。
- 窗口隐藏启动时，`StartupProcessCompleted` 会一直是 false，不能用它判断启动完成。
- 工程图导出 PDF 时会返回错误码 1，但文件其实正常生成。所以成功与否以"是否生成了新文件"为准，导出前会先删除旧文件。
- 首选项编号 75（STEP 协议）设为 242 时，实际输出的还是 AP203。
- SolidWorks 会把扩展名写成大写（`.STL`），导出后统一改成小写。
- 启动 SLDWORKS.exe 时要重定向并丢弃它的标准输出，否则 DWG/DXF 转换器会刷屏。
- 微软雅黑没有半粗体：`SemiBold` 的中文会显示成粗体，英文却是半粗，两者不一致。标题统一用 `Bold`，正文用 `Normal`。
- 用 `Ui.Fade` 做过淡入淡出的元素，不能再直接改 `Visibility`，要用 `Ui.SetShown`，否则记录的目标状态会错乱。

### URDF 相关

- **配合的几何参数（`MateEntity2.EntityParams`）是装配体坐标**，不用再乘零部件变换。
- **零部件的 `GetBox` 是宽松包围盒**，零件斜放时会偏大很多，不能拿来做精确比对。零位校验改用"网格体积质心 vs SolidWorks 质心"。
- **装配体的 `MassProperty.AddBodies` 返回整个装配体的质量**，`MassProperty2.SelectedItems` 会触发 `RPC_E_SERVERFAULT`。每个零件的质量属性改为：打开零件文档读质量属性，再用零部件变换（`MathUtility`）换到装配体坐标。
- **限位配合的 `MinimumVariation` / `MaximumVariation` 是相对当前值的**，换算成关节上下限时要乘上由几何算出的"关节变量对配合值的变化率"符号。
- 装配体按零部件拆分导出 STL 时，文件名是 `asm - <Name2，其中 / 换成空格>.stl`；只有一个零部件时不拆分，直接写成 `asm.stl`。
- 柔性子装配体（`Solving == 1`）里的配合要从子装配体文档读，坐标换算：`顶层零部件位姿 × 子文档里该零部件位姿的逆`。
- 只看两两之间的配合会误判闭链：两组零部件之间还能动，但整个机构一起看被锁死了（例如板子和基座、电机输出轴各同心一次，两根轴不重合）。`MergeRigidInLoops` 先剪掉树枝，只对闭链部分一起求运动空间。一阶分析只会多算自由度、不会少算，所以据此合并是安全的。
- 拆分导出 STL 时，个别嵌套零件的文件名对不上 `Name2`；`MeshBuilder.Match` 先精确匹配，再在没被占用的文件里做唯一的后缀匹配，对不上的列进校验报告。

### 在 ROS 里实测得到的结论（Ubuntu 20.04 + Noetic）

- 用户机器上常见的是 ROS 1 Noetic，只生成 ROS 2 包（ament_cmake + launch.py）根本编译不了。现在用 package.xml 第 3 版的 `condition="$ROS_VERSION == 1/2"` 同时声明 catkin 和 ament_cmake，CMakeLists 按 `$ENV{ROS_VERSION}` 分支；`catkin_make` 实测通过，`roslaunch --nodes/--dump-params` 能解析 launch 文件。ROS 2 侧还没有在真机上编译过。
- 细节多的零件（舵机、电机）会让单个连杆网格到几十万个三角形（30 MB 以上）。外观网格超过 20 万个三角形、碰撞网格超过 2 万个三角形时用顶点聚类简化（`MeshBuilder.Decimate`），零位校验仍用完整网格。
- RViz（Noetic）打开时会弹出 ROS 1 停止维护的提示框，自动化测试时设 `DISABLE_ROS1_EOL_WARNINGS=1`。

### 标准件相关

- 中文版 SolidWorks 的材料库名称是中文：`普通碳钢`、`6061 合金`；英文名设置不上。`SwPartBuilder` 按候选名称逐个尝试，以读回的名称为准。自带材料库里没有 PLA。
- 等距基准面（包括向下等距的）上的草图坐标和拉伸方向都和上视基准面一样，所以"在高度 y 处画草图向 +Y 拉伸"统一用上视基准面的等距面实现。
- 渐开线用 `CreateSpline2` 画，体积和理论值差 0.01%–0.14%（样条拟合误差），其余零件完全一致。

### 应力分析相关

- 网格：`gmsh 零件.step -3 -order 2 -format msh2 -save_all -setstring Geometry.OCCTargetUnit M`，按曲率加密（`Mesh.MeshSizeFromCurvature 16`）。`-save_all` 保留每个面的编号，界面按面拾取、施加载荷都靠它。
- 求解器：TET10，4 点积分；组装成按节点块排列的 CSR；不完全 Cholesky(0) 预条件共轭梯度（主元非正时加对角偏移重试，再不行退回 Jacobi）。约 2 万单元几秒算完。
- 节点应力：在每个单元的 10 个节点处求应力，再按节点平均。固定约束会在约束面边缘产生不真实的应力集中，所以另外给出"离开约束面一层单元以外"的最大值。
- 验证：`swkit-cli fea bench` 用拉杆、悬臂梁（含剪切变形）、带孔板（Heywood Kt）、扭转轴、自重悬臂梁对照理论解，误差 0%–2.1%，外载与反力平衡误差 0。
- 面力合力按曲面实际积分的面积来分配（不能用平面三角形的面积，否则合力会差千分之几）。
- 误差估计（ZZ）：恢复应力 = 节点平均应力，误差 = ∫(σ*−σh)ᵀD⁻¹(σ*−σh)，相对误差 η = √(E²/(U²+E²))。自适应：h_new = h·(e_允许/e_单元)^(1/2)，比例限制在 0.3–1.6，预计单元数超过上限时整体放大；节点尺寸取相邻单元最小值写成 SS 视图给 `-bgm`，同时 `Mesh.MeshSizeExtendFromBoundary 0`。L 形支架 2 轮从 4.8% 降到 2.2%。
- 拓扑优化先用二次单元试过：网格只能很粗（5 千单元），30% 体积时刚度只有实心的 10%、灰度高、不收敛。改成线性四面体（只用角点，单元刚度只算一次，每轮按 ρ^p 缩放组装）+ 6 万单元 + Heaviside 投影（β 1→16）后，刚度 37%、灰度 6%、约 1 分钟。载荷在二次网格上积分后把边中点的力平分给两端角点。
- 拓扑优化的「必须保留」区域曾经是「碰到任何约束 / 受载面的单元」：3 mm 碳板只有 1–2 层单元，用户把力加在整块上表面时所有单元都变成必须保留，结果保留 98%、刚度 99%，看起来像算完了其实什么都没做。现在按每个面带来的保留体积从小到大加入，单个面超过总体积 25% 或累计超过目标体积一半就不算；同时在开始前和结束后明确提示（大面载荷 / 大面固定 / 没减到目标）。
- 薄板（最大一组平行平面的间距 / 面内尺寸 < 0.15）默认按板件优化：形心投影到板平面的网格（边长 = 平均单元边长）上，同一列单元共用一个设计变量，过滤按面内距离；结果只有穿透孔，能直接切板。网格限制在板厚的 2/3 以下。
- 拓扑网格的曲率加密改成每圈 6 个单元、最小尺寸为一半（应力分析是 16 个、1/8）：有几十个 M3 孔的机架板按应力分析的加密会多出 4 倍单元（23 万 vs 8 万），每轮 11 秒。
- β ≥ 8 时移动限减到 0.1，β = 16 时 0.05；最近 3 轮柔度起伏 < 0.5% 也算稳定。之前高 β 时少量单元来回翻转，每轮变化都卡在 0.2，一直跑到最大轮数。
- 机架板实测（3 mm 碳板，242 mm，中心孔固定 + 4 个电机座各 13 N 升力和 0.25 N·m 反扭矩，保留 40%）：37 轮、约 2 分钟。只固定连接柱孔、电池 / 飞控孔加重量时得到对称的 X 形：机臂根部最宽，靠近电机的一段开大孔。
- 3D 云图的颜色纹理必须用 `ViewportUnits = Absolute`：默认相对包围盒映射会把颜色条拉伸到实际出现的数值范围，应力较均匀时颜色会错。
- 脆性材料（铸铁、碳纤维、玻纤、亚克力、树脂）的安全系数 = min(抗拉/最大主应力, 抗压/|最小主应力|)。
- 求解器性能（v0.5）：不完全 Cholesky 的迭代次数随网格加密明显增加（6 mm 网格 980 次 → 2 mm 网格 2123 次），占总时间 90% 以上。改成光滑聚集 AMG（Amg.cs）后基本不变，一直在 45–72 次：
  - 节点块聚集，强连接阈值 0.04；6 个刚体模态做 QR；一步 Jacobi 光滑延拓；Galerkin 粗网格；Chebyshev 3 次光滑；最粗层不超过 1200 自由度，用稠密 Cholesky。
  - 不收敛或出现 NaN 时自动退回 IC(0)。
  - 2.7 万单元从 9.0 秒降到 3.1 秒。
  - 拓扑优化每轮用 Rebuild 复用聚集和 T，只重算 P 和粗网格。
  - 刚度矩阵乘向量按节点 3×3 块做（Csr.Block3），列号只读一次。
  - 内存比 IC(0) 多，6 万单元峰值约 1 GB。
- Gmsh 多线程（-nt > 1）每次划出的网格略有不同，最大应力能差 1–2%，结果不可复现。现在固定 `-nt 1` 并用 HXT（Algorithm3D 10）：比 Delaunay 单线程还快约 40%，且确定性；失败时退回 Delaunay。
- 缓存都在 %LOCALAPPDATA%\SwKit\cache，按「路径 + 大小 + 修改时间」做键，有大小上限：
  - step\：sldprt 转出的 STEP 和材料名
  - geom\：URDF 的零件 B-rep 特征，BinaryFormatter
- HeavyWork.Begin()：重计算期间进程优先级降为 BelowNormal，结束后 LOH 压缩 + SetProcessWorkingSetSize 归还内存。
- SwWorker.Prewarm()：导出页加入文件时提前启动后台 SolidWorks；开发模式（截图、自测）不预热。
