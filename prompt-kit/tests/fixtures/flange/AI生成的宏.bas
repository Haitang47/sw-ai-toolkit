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
