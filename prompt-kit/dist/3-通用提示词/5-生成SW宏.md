> **使用方法**：在 AI 已经给出建模步骤和尺寸表之后，把整篇内容复制，发送到**同一个对话**里。

请根据上面已经确定的建模步骤和尺寸表，生成 SolidWorks 宏，严格遵守下面的规则和速查表。

# 生成 SolidWorks 宏的规则

用户要"生成宏"时，根据已经确定的建模步骤和尺寸表，生成一个能在 SolidWorks 2023 及以上版本直接运行的 VBA 宏，自动建出初版模型。宏的目标是把重复劳动自动化，让用户在几秒内得到一个尺寸正确的初版模型，然后在 SolidWorks 里继续细化。宏不追求覆盖所有特征。

## 规则

1. **只用速查表里的辅助函数和 API。** 速查表里的每个调用都在 SolidWorks 上实测过。AI 生成宏最常见的失败原因，是编造了不存在的 API 或者把参数个数写错。速查表没覆盖的特征（异型孔向导、扫描、放样、筋、拔模、钣金、阵列特征等），在宏里写一行注释 `' TODO 手动：……`，说明用户需要手动完成什么，然后接着做后面能做的步骤。

2. **单位换算**：API 内部长度一律用米，角度一律用弧度。宏里统一写成 `50 * MM`、`45 * DEG`，不要自己换算成小数，这样用户对照尺寸表一眼就能看懂。

3. **不按名字找基准面。** 中文版叫"前视基准面"，英文版叫"Front Plane"，按名字找会在另一种语言的 SolidWorks 上失败。统一用 `GetPlane(1/2/3)` 按特征树顺序取。

4. **只在基准面上画草图，不在模型表面上画。** 模型表面上的草图坐标方向不固定，坐标很容易算错。需要在某个面上画时，用 `OffsetPlane` 建一个和默认基准面平行、位于该面高度的等距基准面，再在它上面画。等距面的草图坐标和拉伸方向都与原基准面相同。

   **圆柱面上的径向孔**（例如轮毂上的顶丝孔）：在零件外侧建一个与孔轴线垂直的等距基准面，画圆后用 `CutBlind` 切到孔该停的位置，例如切到中心孔内部，这样只穿一侧的壁。不要把基准面建在和圆柱面相切的位置，相切处容易出错。这个做法已在 SolidWorks 上实测通过。

5. **草图坐标映射**（实测结果）。在下列基准面上画草图时，草图坐标 (x, y) 对应的模型坐标如下：

   | 草图平面 | 草图 x | 草图 y | 拉伸凸台默认方向 | 拉伸切除默认方向 |
   |---|---|---|---|---|
   | 前视 (1) | +X | +Y | +Z | −Z |
   | 上视 (2) | +X | −Z | +Y | −Y |
   | 右视 (3) | −Z | +Y | +X | −X |

   在上视面画草图时，模型里 Z = +20 的点要写成草图 y = −20。写宏前先把每个草图里的关键点换算一遍。

6. **选边和选面用 `SelectNearest`，不要用 `SelectByID2` 加坐标。** 实测 `SelectByID2` 按坐标选边时，会沿当前视角的视线方向选到别的边，结果随视角变化。`SelectNearest` 按几何距离选，和视角无关。给的点要满足：
   - 选边：点在边上，取边的中点最稳
   - 选面：点在面的内部，不要取在边线上
   - 坐标是模型坐标（不是草图坐标），单位是米
   - 圆角、倒角、抽壳会改变附近的边和面，所以在它们之后再选边时，要按新的几何重新算点

7. **每个特征都要检查**：用 `Check(特征, 步骤号, "特征名")` 检查。失败时提示是第几步，并停止运行，让用户知道从哪一步开始手动处理；成功时把特征改成有意义的名字，和建模步骤对应。

8. **孔和阵列用最简单的方式做**：
   - 孔：草图画圆 + `CutThroughAll`（通孔）或 `CutBlind`（盲孔）
   - 阵列和镜向：用 VBA 循环在同一个草图里画多个圆或多个轮廓，算好每个实例的坐标，不调用阵列特征的 API
   - 螺纹：只做底孔，在 TODO 里提醒用户手动加装饰螺纹线或改成异型孔向导

9. **回转体**：草图里只画一条中心线（作为旋转轴）和一个封闭的半剖面轮廓，然后调用 `Revolve`。草图里有多条中心线时，SolidWorks 不知道该绕哪条转。

10. **代码结构**：输出一个完整模块，包括文件头注释、常量、全部辅助函数和 `main`。`main` 里每一段前加注释 `' ---- 步骤 N：名称 ----`。宏的步骤可以合并或拆分建模步骤（例如把异型孔向导的孔改成画圆加切除、把几组通孔放进同一个草图），但注释里要写明对应的建模步骤编号，例如 `' ---- 宏步骤 4（对应建模步骤 4、5）----`。结束时清除选择、重建、缩放到全屏，并弹窗显示体积，方便用户和预期值对比。

11. **草图定义状态**：宏只保证几何位置正确，不会自动给草图加尺寸和几何关系，所以宏建出的草图是欠定义的（草图线显示为蓝色）。需要参数化修改的关键尺寸（最多 1–3 个），可以用 `AddDim` 标上，但 `AddDim` 只对直线实测过，圆和圆弧不要用；其余的在使用说明里提醒用户按建模步骤手动补全。

12. **字符集**：中文 Windows 上的 VBA 编辑器按 GBK 编码显示，Ø、³、–（短破折号）这类字符会变成问号，所以代码里（包括注释和字符串）不要用。直径写成"直径"或"D"，范围写成"1-6"。

13. **代码之后**附一段简短的使用说明：怎么运行、哪些 TODO 需要手动完成、草图是欠定义的、出错时把错误提示发回来就可以修正。

# SolidWorks 宏 API 速查表（已实测）

下面的辅助函数和 API 调用都在 SolidWorks 2025（33.5）上通过自检脚本逐项实测，17/17 通过；用到的 API 在 2023 版本中都已存在。生成宏时照抄辅助函数，只改 `main` 里的内容。

## 实测得到的关键事实

| 事项 | 结论 |
|---|---|
| 默认基准面名称 | 中文版是"前视基准面/上视基准面/右视基准面"，所以要用 `GetPlane(1/2/3)` 按顺序取 |
| 默认零件模板 | 可能返回 `~BLANK_PART_TEMPLATE.prtdot` 这种占位名，用它新建会时好时坏，所以 `NewPart` 会退回到模板文件夹里找 .prtdot |
| 按坐标选边 | `SelectByID2("", "EDGE", x, y, z, ...)` 会沿视线方向选到别的边，必须用 `SelectNearest` |
| 拉伸方向 | 凸台默认沿草图平面法向正方向，切除默认沿法向反方向 |
| 等距基准面 | 包括反向等距面在内，草图坐标和拉伸方向都与原基准面相同 |
| 通孔 | `FeatureCut4` 用"两个方向都完全贯穿"最稳；单向贯穿的方向一旦朝向空的一侧就会失败 |
| `CreateArc` 方向 | Direction = 1：从起点绕圆心逆时针画到终点；-1 为顺时针 |
| `CreatePolygon` | 给定点 (Xp, Yp) 始终是多边形的一个顶点，与 Inscribed 参数无关。画对边宽为 s 的六角形时，顶点到中心距离 = s ÷ √3 |
| 旋转 | 草图里只有一条中心线时，`FeatureRevolve2` 会自动把它当作旋转轴 |
| 标注尺寸 | 要先关闭"输入尺寸值"选项，否则每次都会弹出修改框 |

## 草图坐标映射

| 草图平面 | 草图 x | 草图 y | 拉伸凸台默认方向 | 拉伸切除默认方向 |
|---|---|---|---|---|
| GetPlane(1) 前视 | +X | +Y | +Z | −Z |
| GetPlane(2) 上视 | +X | −Z | +Y | −Y |
| GetPlane(3) 右视 | −Z | +Y | +X | −X |

## 宏模板（辅助函数 + 示例）

示例零件：80×50×8 底板，中间是装 608 轴承的 Ø30 凸台，4 个 M4 过孔，底板四角 R5 圆角，凸台顶部外缘倒角 C1。运行后弹窗显示的体积应约为 36074 立方毫米。

```vba
' ==========================================================
'  SolidWorks 自动建模宏（由 AI 根据建模步骤生成）
'  适用版本：SolidWorks 2023 及以上
'  运行方法：工具 → 宏 → 新建 → 保存为 .swp → 删除编辑器里原有代码，
'            粘贴本代码 → 按 F5 运行
'  零件：示例-轴承座底板      对应建模步骤：1-6
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

    ' ---- 步骤 1：底板 80×50×8，画在上视基准面上，向上 (+Y) 拉伸 ----
    StartSketch GetPlane(2)
    swSketchMgr.CreateCenterRectangle 0, 0, 0, 40 * MM, 25 * MM, 0
    EndSketch
    Set f = ExtrudeBoss(8 * MM)
    If Not Check(f, 1, "底板") Then Exit Sub

    ' ---- 步骤 2：轴承凸台 直径 30，从底板上表面 (Y=8) 向上拉伸 12 ----
    StartSketch OffsetPlane(GetPlane(2), 8 * MM)
    swSketchMgr.CreateCircleByRadius 0, 0, 0, 15 * MM
    EndSketch
    Set f = ExtrudeBoss(12 * MM)
    If Not Check(f, 2, "轴承凸台") Then Exit Sub

    ' ---- 步骤 3：608 轴承孔 直径 22 深 7，从凸台顶面 (Y=20) 向下切（切除默认方向 -Y） ----
    StartSketch OffsetPlane(GetPlane(2), 20 * MM)
    swSketchMgr.CreateCircleByRadius 0, 0, 0, 11 * MM
    EndSketch
    Set f = CutBlind(7 * MM)
    If Not Check(f, 3, "608轴承孔") Then Exit Sub

    ' ---- 步骤 4：中心通孔 直径 10 + 4 个 M4 过孔 直径 4.5（孔距 60×35），画在同一个草图里一起贯穿 ----
    StartSketch GetPlane(2)
    swSketchMgr.CreateCircleByRadius 0, 0, 0, 5 * MM
    For i = 0 To 3
        ' 上视面：草图 x = 模型 X，草图 y = 模型 -Z。孔位关于原点对称，正负号不影响结果
        swSketchMgr.CreateCircleByRadius IIf(i Mod 2 = 0, 30, -30) * MM, IIf(i < 2, 17.5, -17.5) * MM, 0, 2.25 * MM
    Next i
    EndSketch
    Set f = CutThroughAll()
    If Not Check(f, 4, "通孔") Then Exit Sub

    ' ---- 步骤 5：底板四条竖直棱边圆角 R5（取每条棱边的中点，Y=4） ----
    SelectNearest "EDGE", 40 * MM, 4 * MM, 25 * MM
    SelectNearest "EDGE", -40 * MM, 4 * MM, 25 * MM, True
    SelectNearest "EDGE", 40 * MM, 4 * MM, -25 * MM, True
    SelectNearest "EDGE", -40 * MM, 4 * MM, -25 * MM, True
    Set f = Fillet(5 * MM)
    If Not Check(f, 5, "底板圆角R5") Then Exit Sub

    ' ---- 步骤 6：凸台顶面外缘倒角 C1（外缘圆上的一点：X=15, Y=20, Z=0） ----
    SelectNearest "EDGE", 15 * MM, 20 * MM, 0
    Set f = Chamfer(1 * MM)
    If Not Check(f, 6, "凸台倒角C1") Then Exit Sub

    ' TODO 手动：M4 过孔如果需要沉孔，用异型孔向导替换步骤 4 里的 4 个小圆

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

' 可选：给草图直线加长度尺寸并设为 value（米）。必须在草图编辑状态下调用
' seg 是 CreateLine 的返回值（只对直线实测过）；(x, y) 是尺寸文字的位置（草图坐标）
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
        MsgBox "宏步骤 " & stepNo & "「" & featName & "」创建失败（对应的建模步骤见宏里这一步的注释）。" & vbCrLf & _
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

## 草图绘制 API（在 StartSketch 和 EndSketch 之间调用，坐标为草图坐标，单位米）

| 用途 | 写法 | 说明 |
|---|---|---|
| 中心矩形 | `swSketchMgr.CreateCenterRectangle cx, cy, 0, cx + w / 2, cy + h / 2, 0` | 第二个点是一个角点 |
| 角点矩形 | `swSketchMgr.CreateCornerRectangle x1, y1, 0, x2, y2, 0` | 两个对角点 |
| 圆 | `swSketchMgr.CreateCircleByRadius cx, cy, 0, r` | |
| 直线 | `swSketchMgr.CreateLine x1, y1, 0, x2, y2, 0` | 首尾坐标完全相同的线段会自动连成封闭轮廓 |
| 中心线 | `swSketchMgr.CreateCenterLine x1, y1, 0, x2, y2, 0` | 旋转轴、对称线 |
| 圆弧 | `swSketchMgr.CreateArc cx, cy, 0, x1, y1, 0, x2, y2, 0, 1` | 圆心、起点、终点；1 = 逆时针，-1 = 顺时针 |
| 正多边形 | `swSketchMgr.CreatePolygon cx, cy, 0, vx, vy, 0, n, True` | (vx, vy) 是一个顶点 |

需要拿返回值给尺寸标注用时，写成 `Set seg = swSketchMgr.CreateLine(...)`（要加括号）。

## 速查表没有覆盖的功能

在宏里写 `' TODO 手动：……`，并在使用说明里列出来：异型孔向导（沉孔、螺纹孔）、装饰螺纹线、扫描、放样、边界、筋、拔模、钣金、阵列特征和镜向特征（能用循环画草图代替就代替）、变半径圆角、材料设置、文件保存。

## 宏运行出错时怎么改

| 现象 | 常见原因 | 修改方法 |
|---|---|---|
| 某一步提示"创建失败" | 草图轮廓不封闭，或者线段首尾坐标有微小差异 | 检查每段线的端点坐标，保证完全一致 |
| 切除失败 | 切除方向朝着没有实体的一侧 | 加 `reverse = True`，或者改用 `CutThroughAll` |
| 圆角或倒角选错了边 | 给的点不在目标边上，或者前面的特征已经改变了这条边 | 重新计算边中点的模型坐标；注意上视/右视面的坐标映射 |
| 旋转失败 | 草图里有多条中心线，或者轮廓跨过了中心线 | 只保留一条中心线，轮廓整体放在中心线的一侧 |
| 运行到 NewPart 就报错 | 默认模板设置异常 | 工具 → 选项 → 系统选项 → 默认模板，选一个有效的零件模板 |
| VBA 提示"编译错误" | 代码复制不完整，或者中文标点混进了代码 | 重新完整复制；代码里的逗号、括号、引号必须是英文半角 |
