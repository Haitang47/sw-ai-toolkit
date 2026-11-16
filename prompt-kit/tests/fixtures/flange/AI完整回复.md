### 1. 识别结论

- **物体**：带轮毂的轴连接法兰（法兰轴套，也就是刚性法兰联轴器的一半）。Ø8 的轴插进中心孔，用轮毂侧面的 M3 顶丝径向锁紧；法兰上的 4 个 M3 过孔用来把板件、轮子或另一半联轴器固定上来。常用在小车轮毂、光轴/丝杆与平台的连接处。
- **制造工艺推测**：车削 + 钻孔/攻丝。依据：法兰、轮毂、中心孔都是同轴回转体，轮毂顶部的 45° 倒角是车床上顺手倒出来的；4 个过孔和径向顶丝孔要在钻床或 CNC 上二次加工。材料常见 6061 铝合金或 45 钢。
- **复杂度等级**：L2。理由：单个零件，几何规则，但有 Ø8 轴孔配合（要标 H7）、4 孔圆周孔系和径向螺纹孔，属于"联轴法兰"类机械件。

### 2. 尺度推算

- **参考**：你给的法兰外径 60 mm。三张图都是 SolidWorks 截图：正视图、俯视图是正交投影，没有透视，可以直接按像素换算；斜视图是标准等轴测，只用来核对方位和交叉验证。
- **视图方向判断**：截图里的蓝色原点标记在法兰底面圆心（正视图里在底边中点，俯视图里在圆心）。正视图 = SW 前视（看向 −Z，X 向右、Y 向上）；俯视图 = SW 上视（X 向右，屏幕向上是 −Z）；斜视图 = SW 等轴测（+X 指向右下，+Z 指向左下）。截图里原点旁边另一个小蓝箭头标记，在三张图里离原点的像素偏移都差不多，是屏幕显示用的标记，不属于零件，忽略。
- **比例**：
  - 俯视图：法兰外圆直径 967 px = 60 mm → **16.12 px/mm**
  - 正视图：法兰宽 1253 px = 60 mm → **20.88 px/mm**
  - 斜视图：法兰椭圆长轴 885 px = 60 mm → 14.75 px/mm（等轴测里水平圆的长轴等于真实直径）
- **推算过程**：

  | 量的是什么 | 视图 | 像素 | 换算 (mm) | 取值 |
  |---|---|---|---|---|
  | 安装孔分布圆（左右孔心 743 px，上下孔心 742 px） | 俯视 | 742.5 | 46.07 | 46 |
  | 安装孔直径 | 俯视 | 55 | 3.41 | 3.4（M3 过孔-中） |
  | 中心孔直径 | 俯视 | 128 | 7.94 | 8 |
  | 轮毂外径 | 俯视 / 正视 | 402 / 520 | 24.94 / 24.90 | 25 |
  | 轮毂顶面倒角上沿直径 | 俯视 / 正视 | 370 / 480 | 22.96 / 22.98 | 23 → 倒角 (25 − 23) / 2 = 1 |
  | 法兰厚度 | 正视 | 126 | 6.03 | 6 |
  | 轮毂伸出高度（法兰上表面到顶面） | 正视 | 292 | 13.98 | 14 |
  | 其中倒角竖直高度 | 正视 | 21 | 1.0 | 1 |
  | 总高 | 正视 | 418 | 20.02 | 20 |
  | 顶丝孔直径 | 正视 | 52 | 2.49 | 2.5（M3 攻丝底孔） |
  | 顶丝孔心到法兰底面 | 正视 | 272 | 13.02 | 13 |
  | 顶丝孔心到法兰上表面 | 正视 | 146 | 6.99 | 7（正好是轮毂高度的一半） |

  斜视图交叉验证：轮毂 369 px → 25.0，中心孔 118 px → 8.0，对角两个安装孔的水平距离 480 px → 分布圆半径 23.0，都对得上。
- **方位判断**：
  - 4 个安装孔在俯视图里位于上下左右，即在 ±X、±Z 轴上（0°/90°/180°/270°）。
  - 顶丝孔在正视图里位于轮毂正中，在斜视图里出现在轮毂左下侧。按 X=0、Y=13、Z=+12.5 投影到等轴测图，算出来的位置是 (948, 620) px，图上实际位置是 (947, 620) px，所以顶丝孔朝 **+Z**（正视方向）。斜视图里能看到的 +X 侧没有孔。
- **精度评估**：
  - 正视图、俯视图读数误差约 ±2 px，相当于 ±0.1 mm，所有取整值和原始估算的差都小于 0.1 mm，**可信度高**。
  - 看不到的部位：法兰底面、轮毂背面（−Z 侧和 −X 侧）、顶丝孔里有没有螺纹。这些按常规设计假设，见第 7 节。

### 3. 尺寸表

| 编号 | 名称 | 数值 (mm) | 来源 | 置信度 |
|---|---|---|---|---|
| D1 | 法兰外径 | 60 | 【测】（你给定的参考尺寸） | 高 |
| D2 | 法兰厚度 | 6 | 【测】 | 高 |
| D3 | 轮毂外径 | 25 | 【测】 | 高 |
| D4 | 轮毂伸出高度（法兰上表面以上） | 14 | 【测】 | 高 |
| D5 | 总高（= D2 + D4） | 20 | 【测】 | 高 |
| D6 | 中心孔直径 | 8 H7（估 7.94） | 【标】 | 高 |
| D7 | 安装孔分布圆直径 PCD | 46 | 【测】 | 高 |
| D8 | 安装孔直径 | 3.4，M3 过孔-中（估 3.41） | 【标】 | 高 |
| D9 | 顶丝孔底孔直径 | 2.5，M3 攻丝底孔（估 2.49） | 【标】 | 中（看不到螺纹） |
| D10 | 顶丝孔中心高（距法兰底面） | 13 | 【测】 | 高 |
| D11 | 轮毂顶面外缘倒角 | C1（1 × 45°） | 【测】 | 高 |
| D12 | 顶丝孔深度 | 从轮毂外圆穿到中心孔，只穿 +Z 一侧壁 | 【猜】 | 低 |

### 3b. 孔系表

| 编号 | 类型 | 规格 | 数量 | 位置（相对原点） | 来源 |
|---|---|---|---|---|---|
| H1 | 通孔（轴孔） | Ø8 H7 | 1 | 原点，沿 Y 轴贯穿法兰和轮毂 | 【标】 |
| H2 | 过孔 | M3（Ø3.4，正常） | 4 | PCD 46 圆周均布，在 +X、+Z、−X、−Z 轴上 | 【标】 |
| H3 | 螺纹孔（紧定螺钉） | M3（底孔 Ø2.5） | 1 | 轮毂 +Z 侧径向孔，X = 0、Y = 13，从外圆钻到中心孔 | 【标】（数量和深度【猜】） |

### 4. 建模规划

- **原点位置**：法兰底面圆心，与截图里原设计的原点一致。底面是安装面，作为第一基准，与上视基准面 (Top Plane) 重合；回转轴是第二基准，与 Y 轴重合。所有孔位都从原点和轴线标注，不从倒角边标注。
- **主视方向**：回转轴竖直向上（+Y）。顶丝孔所在一侧朝前视方向（+Z），和你给的正视图一致。
- **对称关系**：零件是回转体，不需要"两侧对称"拉伸。草图里的圆全部和原点"重合"（同心）。4 个安装孔落在前视基准面 (Front Plane) 和右视基准面 (Right Plane) 上；顶丝孔轴线在右视基准面内，整个零件关于右视基准面对称。
- **特征树概览**：
  - 拉伸-法兰 Ø60 × 6
    - 拉伸-轮毂 Ø25 × 14
    - 切除-中心孔 Ø8 H7
    - 异型孔-M3 安装孔
    - 圆周阵列-M3 安装孔 ×4
    - 基准面1-顶丝孔
    - 异型孔-M3 顶丝孔
  - 倒角-轮毂顶 C1

### 5. 建模步骤

**步骤 1：法兰（拉伸凸台/基体 Extruded Boss/Base）**
- 草图平面：上视基准面 (Top Plane)
- 草图内容：画一个圆，圆心与原点"重合"；标直径 60（D1）。草图完全定义（黑色）。
- 特征参数：终止条件 给定深度 (Blind)，深度 6（D2），方向向上（+Y）。如果预览朝下，点"反向"。
- 设计意图：法兰底面就是安装面，让它和上视基准面重合，后面所有高度都从这里量。

**步骤 2：轮毂（拉伸凸台/基体 Extruded Boss/Base）**
- 草图平面：法兰上表面（Y = 6）
- 草图内容：圆，圆心与原点"重合"；直径 25（D3）。
- 特征参数：给定深度 (Blind) 14（D4），向上；勾选"合并结果 (Merge result)"。
- 设计意图：轮毂高度从法兰上表面量，以后改法兰厚度，轮毂伸出高度不变。

**步骤 3：中心孔 Ø8 H7（拉伸切除 Extruded Cut）**
- 草图平面：轮毂顶面（Y = 20）
- 草图内容：圆，圆心与原点"重合"；直径 8（D6）。双击直径尺寸 → 公差/精度 (Tolerance/Precision) → 公差类型选"配合 (Fit)"，孔配合选 H7。
- 特征参数：终止条件 完全贯穿 (Through All)，方向向下。
- 设计意图：轴孔和回转轴同轴，是零件的第二基准；H7 写在尺寸上，出工程图时会自动带出来。

**步骤 4：M3 安装孔（异型孔向导 Hole Wizard）**
- 草图平面：法兰上表面（在"位置"选项卡里点法兰上表面轮毂以外的区域）
- 草图内容：
  - "类型"选项卡：孔类型选 孔 (Hole)；标准 GB；类型 螺钉间隙 (Screw Clearances)；大小 M3；套合 (Fit) 选 正常 (Normal)，孔径 3.4（D8）。
  - "位置"选项卡：只放 1 个点。先画一个构造圆：圆心与原点"重合"，直径 46（D7）；再让点"重合"在构造圆上，并和原点"水平"，点放在原点右侧（+X）。
- 特征参数：终止条件 完全贯穿 (Through All)。
- 设计意图：异型孔向导自带 M3 标准孔径，以后用 Toolbox 自动配螺钉时能识别；只做 1 个孔再阵列，改孔数只需要改一个地方。

**步骤 5：安装孔 ×4（圆周阵列 Circular Pattern）**
- 草图平面：不需要草图
- 草图内容：无
- 特征参数：阵列轴选中心孔内壁圆柱面（SolidWorks 会自动用它的轴线；也可以先打开 视图 → 隐藏/显示 → 临时轴 (Temporary Axes)，再选那条临时轴）；勾选"等间距 (Equal spacing)"，角度 360°，实例数 4；要阵列的特征选步骤 4 的异型孔。
- 设计意图：4 个孔绕回转轴均布；阵列轴取中心孔，保证孔系和轴孔同心。

**步骤 6：顶丝孔基准面（基准面 Plane）**
- 草图平面：不需要草图
- 草图内容：无
- 特征参数：第一参考在轮毂前侧（+Z 侧）点选轮毂外圆柱面，关系选"相切 (Tangent)"；第二参考选前视基准面 (Front Plane)，关系选"平行 (Parallel)"。确认新基准面在 +Z 一侧，也就是正视时朝向你的那一侧；如果跑到了背面，在圆柱面前侧换个位置重新点选。更省事的做法：第一参考选前视基准面，等距 12.5（= D3 / 2），朝 +Z。缺点是以后改 D3，要手动同步这个等距值。
- 设计意图：径向孔需要一个平面来放；基准面和轮毂外圆相切，以后改 D3 它会自动跟着走。

**步骤 7：M3 顶丝孔（异型孔向导 Hole Wizard）**
- 草图平面：基准面1（步骤 6 建的面）
- 草图内容：
  - "类型"选项卡：孔类型选 直螺纹孔 (Straight Tap)；标准 GB；类型 螺纹孔 (Tapped hole)；大小 M3，底孔 2.5（D9）；勾选"装饰螺纹线 (Cosmetic thread)"。
  - "位置"选项卡：放 1 个点，点与原点"竖直"（使 X = 0），点到原点的竖直距离标 13（D10）。
- 特征参数：终止条件 成形到一面 (Up To Surface)，面选中心孔内壁圆柱面。预览应该是从基准面往轴心方向（−Z）钻；方向反了就点终止条件旁边的"反向"。
- 设计意图：只钻穿前侧一层壁、通到中心孔，不打穿对面；孔高度从安装面（原点）标注，不依赖轮毂顶面。

**步骤 8：轮毂顶部倒角 C1（倒角 Chamfer）**
- 草图平面：不需要草图
- 草图内容：无
- 特征参数：倒角类型 角度距离 (Angle Distance)；边线选轮毂顶面外缘圆；距离 1（D11），角度 45°。
- 设计意图：倒角放在最后，前面的孔和阵列都不引用这条边，以后改倒角或删掉都不会导致重建失败。

### 6. 建完后检查

1. **总体尺寸**：用 测量 (Measure) 工具核对：法兰 Ø60 × 6，轮毂 Ø25，底面到顶面总高 20。
2. **安装孔**：切到上视（Ctrl+5），4 个 Ø3.4 孔在上下左右，和你的俯视图一致；相对两孔中心距 46，相邻两孔中心距约 32.53。
3. **顶丝孔**：切到前视（Ctrl+1），Ø2.5 孔在轮毂正中，孔心距底面 13、距法兰上表面 7。再用 剖面视图 (Section View) 沿前视基准面剖开，确认孔只穿前侧壁，背面没有孔。
4. **中心孔**：从上视方向能看穿 Ø8 孔，贯通法兰和轮毂。
5. **质量属性 (Mass Properties)**：体积应约为 **22534 mm³**。按 6061 铝合金（2.70 g/cm³）约 60.8 g，按 45 钢（7.85 g/cm³）约 177 g。

### 7. 待确认问题

1. **顶丝孔数量和深度**：图里只能看到 +Z 侧 1 个孔，背面（−Z）和左后方（−X）看不到。**当前默认**：只有 1 个 M3 螺纹孔，只穿 +Z 侧壁到中心孔。这类零件也常见"两个顶丝互成 90°"或"一个孔对穿两侧壁"，如果是这样，告诉我位置。
2. **法兰底面和安装孔形式**：底面看不到。**当前默认**：底面是平面，没有止口、沉槽；4 个安装孔是普通 M3 过孔，没有沉头。如果螺钉头要埋进法兰，告诉我螺钉从哪一面装，我改成柱形沉头孔 (Counterbore)。
3. **中心孔配合**：**当前默认** Ø8 H7，配 Ø8 光轴或电机轴，用顶丝固定。如果轴带 D 形切平、键槽，或者要压配，告诉我轴的规格。
4. **材料**：**当前默认** 6061 铝合金，质量约 61 g。如果是钢件，约 177 g。
5. **其他边**：图上只有轮毂顶部外缘有 C1，其余是锐边。**当前默认**：保持锐边，加工时去毛刺。如果需要统一倒 C0.5（比如法兰外缘、中心孔口），告诉我。

### 8. 下一步

宏在下面。

### 9. SolidWorks 宏

```vba
' ==========================================================
'  SolidWorks 自动建模宏（由 AI 根据建模步骤生成）
'  适用版本：SolidWorks 2023 及以上
'  运行方法：工具 → 宏 → 新建 → 保存为 .swp → 删除编辑器里原有代码，
'            粘贴本代码 → 按 F5 运行
'  零件：带轮毂法兰（法兰外径 60，轴孔直径 8）    对应建模步骤：1-8
'  原点：法兰底面圆心；回转轴 = 模型 Y 轴；顶丝孔朝 +Z（正视方向）
'  运行后弹窗显示的体积应约为 22534 立方毫米
' ==========================================================
Option Explicit

Const MM As Double = 0.001                    ' API 长度单位是米：50 * MM 表示 50 mm
Const DEG As Double = 1.74532925199433E-02    ' API 角度单位是弧度：45 * DEG 表示 45 度

Dim swApp As Object
Dim swModel As Object
Dim swSketchMgr As Object
Dim swFeatMgr As Object

Sub main()
    Set swApp = Application.SldWorks
    Set swModel = NewPart()
    If swModel Is Nothing Then MsgBox "新建零件失败，请检查 工具 → 选项 → 默认模板": Exit Sub
    Set swSketchMgr = swModel.SketchManager
    Set swFeatMgr = swModel.FeatureManager

    Dim f As Object, i As Integer
    Dim ang As Double, px As Double, pz As Double

    ' ---- 宏步骤 1（对应建模步骤 1）：法兰 直径 60（D1）x 厚 6（D2），画在上视基准面，向上 (+Y) 拉伸 ----
    StartSketch GetPlane(2)
    swSketchMgr.CreateCircleByRadius 0, 0, 0, 30 * MM
    EndSketch
    Set f = ExtrudeBoss(6 * MM)
    If Not Check(f, 1, "法兰") Then Exit Sub

    ' ---- 宏步骤 2（对应建模步骤 2）：轮毂 直径 25（D3），从法兰上表面 (Y=6) 向上拉伸 14（D4） ----
    StartSketch OffsetPlane(GetPlane(2), 6 * MM)
    swSketchMgr.CreateCircleByRadius 0, 0, 0, 12.5 * MM
    EndSketch
    Set f = ExtrudeBoss(14 * MM)
    If Not Check(f, 2, "轮毂") Then Exit Sub

    ' ---- 宏步骤 3（对应建模步骤 3）：中心孔 直径 8（D6），两个方向完全贯穿法兰和轮毂 ----
    StartSketch GetPlane(2)
    swSketchMgr.CreateCircleByRadius 0, 0, 0, 4 * MM
    EndSketch
    Set f = CutThroughAll()
    If Not Check(f, 3, "中心孔D8H7") Then Exit Sub

    ' ---- 宏步骤 4（对应建模步骤 4、5）：4 个 M3 过孔 直径 3.4（D8），分布圆直径 46（D7）----
    '      孔在 +X、+Z、-X、-Z 轴上（0/90/180/270 度）。用循环在同一个草图里画 4 个圆，
    '      代替"异型孔向导 + 圆周阵列"
    StartSketch GetPlane(2)
    For i = 0 To 3
        ang = i * 90 * DEG
        px = 23 * MM * Cos(ang)                   ' 模型 X（分布圆半径 23）
        pz = 23 * MM * Sin(ang)                   ' 模型 Z
        ' 上视面：草图 x = 模型 X，草图 y = 模型 -Z
        swSketchMgr.CreateCircleByRadius px, -pz, 0, 1.7 * MM
    Next i
    EndSketch
    Set f = CutThroughAll()
    If Not Check(f, 4, "M3安装孔x4") Then Exit Sub

    ' ---- 宏步骤 5（对应建模步骤 6、7）：M3 顶丝底孔 直径 2.5（D9），孔心 X=0、Y=13（D10），朝 +Z ----
    '      在前视基准面前方 15 mm（Z=15，零件外面）建等距基准面画圆，向 -Z 切 15 mm 切到 Z=0。
    '      Z=0 处在中心孔内部（空的），所以只穿透 +Z 一侧的壁，不碰背面
    StartSketch OffsetPlane(GetPlane(1), 15 * MM)
    ' 前视面（及其等距面）：草图 x = 模型 X，草图 y = 模型 Y
    swSketchMgr.CreateCircleByRadius 0, 13 * MM, 0, 1.25 * MM
    EndSketch
    Set f = CutBlind(15 * MM)
    If Not Check(f, 5, "M3顶丝底孔") Then Exit Sub
    ' TODO 手动：这里只做了直径 2.5 的底孔，没有螺纹。请删除特征「M3顶丝底孔」，
    '            按建模步骤 6、7 用异型孔向导做 M3 直螺纹孔（会自动带装饰螺纹线）

    ' ---- 宏步骤 6（对应建模步骤 8）：轮毂顶面外缘倒角 C1（D11），取外缘圆上一点 X=12.5, Y=20, Z=0 ----
    SelectNearest "EDGE", 12.5 * MM, 20 * MM, 0
    Set f = Chamfer(1 * MM)
    If Not Check(f, 6, "轮毂倒角C1") Then Exit Sub

    ' TODO 手动：如果要让 Toolbox 识别安装孔，删除特征「M3安装孔x4」，按建模步骤 4、5 改成异型孔向导 + 圆周阵列
    ' TODO 手动：中心孔直径 8 加 H7 公差（编辑草图 → 标注直径 → 公差/精度 → 配合 → H7）
    ' TODO 手动：设置材料（默认 6061 铝合金）并保存文件

    Finish
End Sub

'================ 以下是辅助函数（已实测，照抄即可，不要修改） ================

' 新建零件。默认模板可能是占位名，此时到模板文件夹里找第一个 .prtdot
Function NewPart() As Object
    Dim tmpl As String, locs As String, folder As String, f As String
    tmpl = swApp.GetUserPreferenceStringValue(8)            ' 8 = 默认零件模板
    If Len(tmpl) > 0 Then
        If Left(tmpl, 1) <> "~" Then
            Set NewPart = swApp.NewDocument(tmpl, 0, 0, 0)
            If Not NewPart Is Nothing Then Exit Function
        End If
    End If
    locs = swApp.GetUserPreferenceStringValue(6)            ' 6 = 模板文件夹（可能有多个，用分号分隔）
    If Len(locs) = 0 Then Exit Function
    folder = Split(locs, ";")(0)
    If Right(folder, 1) <> "\" Then folder = folder & "\"
    f = Dir(folder & "*.prtdot")
    If f <> "" Then Set NewPart = swApp.NewDocument(folder & f, 0, 0, 0)
End Function

' 按特征树顺序取默认基准面：1 = 前视，2 = 上视，3 = 右视（与界面语言无关）
Function GetPlane(idx As Integer) As Object
    Dim f As Object, n As Integer
    Set f = swModel.FirstFeature
    Do While Not f Is Nothing
        If f.GetTypeName2 = "RefPlane" Then
            n = n + 1
            If n = idx Then
                Set GetPlane = f
                Exit Function
            End If
        End If
        Set f = f.GetNextFeature
    Loop
End Function

' 等距基准面：从 basePlane 沿法向偏移 dist；flip = True 时向反方向偏移
' 新基准面上的草图坐标和拉伸方向都与 basePlane 相同
Function OffsetPlane(basePlane As Object, dist As Double, Optional flip As Boolean = False) As Object
    Dim opt As Long, rp As Object
    opt = 8                                       ' 8 = 按距离偏移
    If flip Then opt = 8 + 256                    ' 256 = 反向
    swModel.ClearSelection2 True
    basePlane.Select2 False, 0
    Set rp = swFeatMgr.InsertRefPlane(opt, dist, 0, 0, 0, 0)
    If rp Is Nothing Then Exit Function
    Set OffsetPlane = swModel.FeatureByPositionReverse(0)
End Function

' 在基准面上开始画草图。AddToDB = True 关闭自动捕捉，坐标写多少就是多少
Sub StartSketch(plane As Object)
    swModel.ClearSelection2 True
    plane.Select2 False, 0
    swSketchMgr.AddToDB = True
    swSketchMgr.InsertSketch True
End Sub

' 退出草图，并选中刚画好的草图，供下一个特征使用
Sub EndSketch()
    swSketchMgr.AddToDB = False
    swSketchMgr.InsertSketch True
    swModel.ClearSelection2 True
    swModel.FeatureByPositionReverse(0).Select2 False, 0
End Sub

' 拉伸凸台：默认沿草图平面法向正方向；reverse = True 反向；midPlane = True 两侧对称（总厚度 = depth）
Function ExtrudeBoss(depth As Double, Optional reverse As Boolean = False, Optional midPlane As Boolean = False) As Object
    Dim endCond As Long
    endCond = 0                                   ' 0 = 给定深度
    If midPlane Then endCond = 6                  ' 6 = 两侧对称
    Set ExtrudeBoss = swFeatMgr.FeatureExtrusion3(True, False, reverse, endCond, 0, depth, depth, _
        False, False, False, False, 0, 0, False, False, False, False, True, True, True, 0, 0, False)
End Function

' 给定深度切除：默认沿草图平面法向的反方向；reverse = True 反向
Function CutBlind(depth As Double, Optional reverse As Boolean = False) As Object
    Set CutBlind = swFeatMgr.FeatureCut4(True, False, reverse, 0, 0, depth, depth, _
        False, False, False, False, 0, 0, False, False, False, False, False, True, True, True, True, False, 0, 0, False, False)
End Function

' 完全贯穿切除（两个方向都贯穿），不用关心草图在实体的哪一侧
Function CutThroughAll() As Object
    Set CutThroughAll = swFeatMgr.FeatureCut4(False, False, False, 1, 1, 0.01, 0.01, _
        False, False, False, False, 0, 0, False, False, False, False, False, True, True, True, True, False, 0, 0, False, False)
End Function

' 旋转凸台：草图里只能有一条中心线（旋转轴）加封闭轮廓
Function Revolve(Optional angleDeg As Double = 360) As Object
    Set Revolve = swFeatMgr.FeatureRevolve2(True, True, False, False, False, False, 0, 0, angleDeg * DEG, 0, _
        False, False, 0, 0, 0, 0, 0, True, True, True)
End Function

' 按几何距离选边或面：找离点 (x, y, z) 最近的边或面并选中。与视角无关
' kind = "EDGE" 或 "FACE"；坐标是模型坐标（米）；边取边上的点，面取面内部的点
Function SelectNearest(kind As String, x As Double, y As Double, z As Double, Optional append As Boolean = False) As Boolean
    Dim bodies As Variant, b As Variant, ents As Variant, e As Variant
    Dim best As Object, p As Variant, d As Double, bestD As Double
    bestD = 1E+30
    If Not append Then swModel.ClearSelection2 True
    bodies = swModel.GetBodies2(0, False)         ' 0 = 实体
    If IsEmpty(bodies) Then Exit Function
    For Each b In bodies
        If kind = "EDGE" Then ents = b.GetEdges Else ents = b.GetFaces
        If Not IsEmpty(ents) Then
            For Each e In ents
                p = e.GetClosestPointOn(x, y, z)
                d = (p(0) - x) ^ 2 + (p(1) - y) ^ 2 + (p(2) - z) ^ 2
                If d < bestD Then
                    bestD = d
                    Set best = e
                End If
            Next e
        End If
    Next b
    If Not best Is Nothing Then SelectNearest = best.Select4(append, Nothing)
End Function

' 圆角：先用 SelectNearest 选好一条或多条边（第二条起 append = True）
Function Fillet(radius As Double) As Object
    Set Fillet = swFeatMgr.FeatureFillet3(195, radius, 0, 0, 0, 0, 0, Empty, Empty, Empty, Empty, Empty, Empty, Empty)
End Function

' 倒角（角度-距离，默认 45 度）：先用 SelectNearest 选好边
Function Chamfer(dist As Double, Optional angleDeg As Double = 45) As Object
    Set Chamfer = swFeatMgr.InsertFeatureChamfer(4, 1, dist, angleDeg * DEG, 0, 0, 0, 0)
End Function

' 抽壳（向内）：先用 SelectNearest "FACE" 选好要去掉的面（开口面）
' 注意：VBA 自带 Shell 函数，所以这里叫 ShellIn
Function ShellIn(thickness As Double) As Object
    Dim f As Object
    swModel.InsertFeatureShell thickness, False
    Set f = swModel.FeatureByPositionReverse(0)
    If f.GetTypeName2 = "Shell" Then Set ShellIn = f
End Function

' 可选：给草图线段加尺寸并设为 value（米）。必须在草图编辑状态下调用
' seg 是 CreateLine 等函数的返回值；(x, y) 是尺寸文字的位置（草图坐标）
Sub AddDim(seg As Object, x As Double, y As Double, value As Double)
    Dim old As Boolean, dd As Object
    old = swApp.GetUserPreferenceToggle(10)       ' 10 = 创建尺寸时弹出输入框
    swApp.SetUserPreferenceToggle 10, False
    swModel.ClearSelection2 True
    seg.Select4 False, Nothing
    Set dd = swModel.AddDimension2(x, y, 0)
    If Not dd Is Nothing Then dd.GetDimension2(0).SystemValue = value
    swApp.SetUserPreferenceToggle 10, old
End Sub

' 检查特征是否创建成功：失败时提示第几步并返回 False；成功时给特征改名
Function Check(feat As Object, stepNo As Integer, featName As String) As Boolean
    If feat Is Nothing Then
        MsgBox "第 " & stepNo & " 步「" & featName & "」创建失败。" & vbCrLf & _
               "前面的步骤已经完成。请从这一步开始手动建模，或者把这条提示发给 AI 修正宏。", vbExclamation
        Check = False
    Else
        On Error Resume Next
        feat.Name = featName
        On Error GoTo 0
        Check = True
    End If
End Function

' 收尾：清除选择、重建、缩放到全屏，并显示体积供核对
Sub Finish()
    Dim v As Double
    swModel.ClearSelection2 True
    swModel.ForceRebuild3 False
    swModel.ViewZoomtofit2
    v = swModel.Extension.CreateMassProperty.Volume * 1000000000#
    MsgBox "建模完成。体积约 " & Format(v, "0") & " 立方毫米，请与预期值对比。", vbInformation
End Sub
```

**使用说明**

1. **运行**：SolidWorks 里点 工具 → 宏 → 新建，保存为 `.swp`，删掉编辑器里原有的代码，把上面的代码完整粘贴进去，按 F5 运行。宏会新建一个零件，依次做出法兰、轮毂、中心孔、4 个安装孔、顶丝底孔和轮毂倒角，最后弹窗显示体积。**体积应约为 22534 立方毫米**，差得多就说明哪一步不对。
2. **需要手动完成的 TODO**：
   - 顶丝孔目前只是 Ø2.5 光孔。删掉特征「M3顶丝底孔」，按建模步骤 6、7 用异型孔向导做 M3 直螺纹孔。
   - 想让 Toolbox 识别安装孔的话，把「M3安装孔x4」换成步骤 4、5 的异型孔向导 + 圆周阵列。
   - 中心孔直径尺寸加 H7 公差；设置材料；保存文件。
3. **草图是欠定义的**：宏只保证几何位置正确，画的圆没有和原点的"重合"关系，也没有尺寸，草图线是蓝色的。需要参数化修改时，按建模步骤 1–8 给每个草图补上"圆心与原点重合"和对应的 D 编号尺寸。
4. **多出来的基准面**：宏建了两个等距基准面，一个在 Y = 6，用来画轮毂；一个在 Z = 15，用来画顶丝孔。不影响模型，可以右键隐藏。
5. **出错时**：如果某一步弹出"创建失败"，或者顶丝孔出现在背面（−Z 侧），把提示内容或截图发给我，我来修正宏。
