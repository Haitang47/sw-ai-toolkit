using System;
using System.Collections.Generic;
using System.Linq;

namespace SwKit.Core.Parts
{
    /// <summary>电机和电子件外形：舵机、直流 / 减速电机、TT 马达、无刷电机、开发板、锂电池。都是用于装配和留空间的外形，不是精确模型。</summary>
    public static partial class Catalog
    {
        sealed class Servo
        {
            public double L, W, H, TabL, TabT, TabY, HoleD, HoleSpan, HoleDz, CapD, CapH, ShaftD, ShaftH, ShaftOff;
        }
        static readonly Dictionary<string, Servo> Servos = new Dictionary<string, Servo>
        {
            ["微型 9g（SG90 / MG90S）"] = new Servo { L = 22.8, W = 12.2, H = 22.5, TabL = 32.3, TabT = 2.5, TabY = 15.9, HoleD = 2.0, HoleSpan = 27.8, HoleDz = 0, CapD = 11.8, CapH = 4.0, ShaftD = 4.8, ShaftH = 3.2, ShaftOff = 6.0 },
            ["标准 40×20（MG996R / MG995）"] = new Servo { L = 40.7, W = 19.7, H = 37.0, TabL = 54.0, TabT = 2.5, TabY = 26.5, HoleD = 4.2, HoleSpan = 49.5, HoleDz = 10, CapD = 0, CapH = 0, ShaftD = 5.8, ShaftH = 4.0, ShaftOff = 10.0 },
            ["标准 40×20（DS3218 / DS3225）"] = new Servo { L = 40.0, W = 20.0, H = 37.5, TabL = 54.5, TabT = 2.5, TabY = 27.0, HoleD = 4.2, HoleSpan = 49.5, HoleDz = 10, CapD = 0, CapH = 0, ShaftD = 5.9, ShaftH = 4.5, ShaftOff = 10.0 },
        };
        // 外转子无刷电机：外径、机身高、底座直径、底座高、安装孔距 a × b、安装螺纹、轴径、轴伸出长
        static readonly Dictionary<string, double[]> Brushless = new Dictionary<string, double[]>
        {
            ["2204"] = new double[] { 27.7, 17.0, 22, 3.5, 16, 19, 3, 5, 9 }, ["2207"] = new double[] { 27.9, 18.8, 22, 3.5, 16, 16, 3, 5, 12 },
            ["2306"] = new double[] { 27.9, 17.5, 22, 3.5, 16, 16, 3, 5, 12 }, ["2212"] = new double[] { 27.9, 26.0, 25, 4.0, 16, 19, 3, 3.17, 14 },
            ["2814"] = new double[] { 35.2, 26.5, 30, 4.0, 16, 19, 3, 4, 15 }, ["3508"] = new double[] { 41.6, 25.0, 35, 4.0, 25, 25, 3, 4, 15 },
        };
        // 开发板：板长 × 板宽 × 板厚、圆角、安装孔（x, y, 直径）、上面的大件（x0, x1, y0, y1, 高）
        sealed class Board { public double L, W, T, R; public double[][] Holes; public double[][] Parts; }
        static readonly Dictionary<string, Board> Boards = new Dictionary<string, Board>
        {
            ["树莓派 4B"] = new Board
            {
                L = 85, W = 56, T = 1.4, R = 3,
                Holes = new[] { new double[] { 3.5, 3.5, 2.7 }, new double[] { 61.5, 3.5, 2.7 }, new double[] { 3.5, 52.5, 2.7 }, new double[] { 61.5, 52.5, 2.7 } },
                Parts = new[] { new double[] { 70, 87, 2.5, 15.6, 16 }, new double[] { 70, 87, 20.5, 33.6, 16 }, new double[] { 66, 87.5, 38, 54, 13.5 }, new double[] { 7.1, 57.9, 50, 55, 8.5 } },
            },
            ["树莓派 5"] = new Board
            {
                L = 85, W = 56, T = 1.4, R = 3,
                Holes = new[] { new double[] { 3.5, 3.5, 2.7 }, new double[] { 61.5, 3.5, 2.7 }, new double[] { 3.5, 52.5, 2.7 }, new double[] { 61.5, 52.5, 2.7 } },
                Parts = new[] { new double[] { 66, 87.5, 2, 18.5, 13.5 }, new double[] { 70, 87, 22.5, 35.6, 16 }, new double[] { 70, 87, 40.5, 53.6, 16 }, new double[] { 7.1, 57.9, 50, 55, 8.5 } },
            },
            ["树莓派 Zero 2 W"] = new Board
            {
                L = 65, W = 30, T = 1.4, R = 3,
                Holes = new[] { new double[] { 3.5, 3.5, 2.75 }, new double[] { 61.5, 3.5, 2.75 }, new double[] { 3.5, 26.5, 2.75 }, new double[] { 61.5, 26.5, 2.75 } },
                Parts = new double[0][],
            },
            ["Arduino Uno R3"] = new Board
            {
                L = 68.6, W = 53.3, T = 1.6, R = 0,
                Holes = new[] { new double[] { 14.0, 2.5, 3.2 }, new double[] { 15.3, 50.7, 3.2 }, new double[] { 66.1, 7.6, 3.2 }, new double[] { 66.1, 35.5, 3.2 } },
                Parts = new[] { new double[] { -6.2, 10.3, 31.7, 43.7, 10.9 }, new double[] { -1.8, 11.6, 3.3, 12.3, 11 } },
            },
        };
        // 圆柱锂电池：直径、长度
        static readonly Dictionary<string, double[]> Cells = new Dictionary<string, double[]>
        {
            ["14500"] = new double[] { 14.2, 50.5 }, ["18650"] = new double[] { 18.4, 65.2 }, ["21700"] = new double[] { 21.2, 70.2 },
            ["26650"] = new double[] { 26.2, 65.4 }, ["32700"] = new double[] { 32.2, 70.5 },
        };

        static IEnumerable<PartType> Devices() => new List<PartType>
        {
            new PartType
            {
                Id = "servo", Category = "电机", Name = "舵机", Standard = null, Keywords = "舵机 伺服 servo SG90 MG996R DS3218",
                Note = "外形按常见厂家图纸（误差约 ±0.5 mm），用于装配和留安装空间。底面在 Y = 0，输出轴朝 +Y，安装耳沿 X。安装前请用实物核对孔距。",
                Params = { Choice("model", "型号", _ => Servos.Keys.ToArray(), "微型 9g（SG90 / MG90S）") },
                Size = p => p["model"].Split('（')[1].TrimEnd('）').Split(' ')[0],
                Build = p =>
                {
                    var s = Servos[p["model"]];
                    double L = s.L * MM, W = s.W * MM, H = s.H * MM, tl = s.TabL * MM, tt = s.TabT * MM, ty = s.TabY * MM;
                    var shaft = new V2(L / 2 - s.ShaftOff * MM, 0);
                    var tab = new List<Loop> { Loop.Rect(tl, W) };
                    foreach (var sx in new[] { -1, 1 })
                        foreach (var dz in s.HoleDz > 0 ? new[] { -s.HoleDz / 2, s.HoleDz / 2 } : new[] { 0.0 })
                            tab.Add(Loop.Circle(new V2(sx * s.HoleSpan / 2 * MM, dz * MM), s.HoleD / 2 * MM));
                    var g = new PartGeom()
                        .Add(Op.Extrude("机身下部", 0, ty, Loop.Rect(L, W)))
                        .Add(Op.Extrude("安装耳", ty, tt, tab))
                        .Add(Op.Extrude("机身上部", ty + tt, H - ty - tt, Loop.Rect(L, W)));
                    var top = H;
                    if (s.CapD > 0) { g.Add(Op.Extrude("齿轮盖", top, s.CapH * MM, Loop.Circle(shaft, s.CapD / 2 * MM))); top += s.CapH * MM; }
                    return g.Add(Op.Extrude("输出轴", top, s.ShaftH * MM, Loop.Circle(shaft, s.ShaftD / 2 * MM)));
                },
            },
            new PartType
            {
                Id = "dc_motor", Category = "电机", Name = "直流 / 减速电机", Standard = null, Keywords = "N20 130 直流电机 减速电机 dc motor gear motor",
                Note = "N20 微型减速电机（12 × 10 机壳、D 形输出轴）和 130 直流电机外形。底面（电机尾部）在 Y = 0，输出轴朝 +Y。减速箱长度随减速比略有不同。",
                Params =
                {
                    Choice("model", "型号", _ => new[] { "N20 减速电机", "130 直流电机" }, "N20 减速电机"),
                    Choice("shaft", "输出轴长", _ => new[] { "6", "10", "15" }, "10", "mm"),
                },
                Size = p => (p["model"].StartsWith("N20") ? "N20" : "130") + $" 轴{p["shaft"]}",
                Build = p =>
                {
                    double sl = D(p, "shaft") * MM;
                    if (p["model"].StartsWith("N20"))
                        return new PartGeom()
                            .Add(Op.Extrude("电机", 0, 15 * MM, Loop.DoubleD(6 * MM, 5 * MM)))
                            .Add(Op.Extrude("减速箱", 15 * MM, 9 * MM, Loop.Rect(10 * MM, 12 * MM)))
                            .Add(Op.Extrude("轴套", 24 * MM, 1 * MM, Loop.Circle(2 * MM)))
                            .Add(Op.Extrude("输出轴", 25 * MM, sl, Loop.DShape(1.5 * MM, 1.0 * MM)));
                    return new PartGeom()
                        .Add(Op.Extrude("电机", 0, 25 * MM, Loop.DoubleD(10 * MM, 7.5 * MM)))
                        .Add(Op.Extrude("端盖凸台", 25 * MM, 1.8 * MM, Loop.Circle(3 * MM)))
                        .Add(Op.Extrude("输出轴", 26.8 * MM, sl, Loop.Circle(1 * MM)));
                },
            },
            new PartType
            {
                Id = "tt_motor", Category = "电机", Name = "TT 减速电机", Standard = null, Keywords = "TT 马达 黄色减速电机 智能小车 tt motor",
                Note = "常见 1:48 黄色 TT 减速电机的外形（误差约 ±1 mm）：减速箱、电机、两侧伸出的双 D 输出轴。减速箱底面在 Y = 0，电机沿 +X，输出轴沿 Z。",
                Params = { Choice("shaft", "输出轴", _ => new[] { "双轴", "单轴" }, "双轴") },
                Size = p => p["shaft"],
                Build = p =>
                {
                    double bl = 37.5 * MM, bw = 22.5 * MM, bh = 18.8 * MM, cy = bh / 2, sx = 11.3 * MM, sl = 9 * MM;
                    var shaft = Loop.DoubleD(2.7 * MM, 1.85 * MM, new V2(sx, cy));
                    var g = new PartGeom()
                        .Add(Op.Extrude("减速箱", -bw / 2, bw, Loop.Rect(bl, bh, new V2(bl / 2, bh / 2))).Along(Axis.Z))
                        .Add(Op.Extrude("电机", bl, 27 * MM, Loop.DoubleD(9.4 * MM, 7.5 * MM, new V2(0, cy))).Along(Axis.X))
                        .Add(Op.Extrude("输出轴", bw / 2, sl, shaft).Along(Axis.Z));
                    if (p["shaft"] == "双轴") g.Add(Op.Extrude("输出轴 ", -bw / 2 - sl, sl, shaft).Along(Axis.Z));
                    return g;
                },
            },
            new PartType
            {
                Id = "brushless", Category = "电机", Name = "无刷电机", Standard = null, Keywords = "无刷电机 外转子 穿越机 航模 brushless outrunner 2204 2212",
                Note = "外转子无刷电机外形，底座 4 个安装螺纹孔。尺寸和孔距按常见型号，不同厂家略有差异，请以实物为准。底面在 Y = 0，轴朝 +Y。",
                Params = { Choice("model", "型号", _ => Brushless.Keys.ToArray(), "2212") },
                Size = p => p["model"],
                Build = p =>
                {
                    var b = Brushless[p["model"]].Select(v => v * MM).ToArray();
                    double od = b[0], H = b[1], a = b[4], c = b[5], m = b[6], sd = b[7], sl = b[8], bh = b[3];
                    double bd = Math.Min(od, Math.Max(b[2], Math.Max(a, c) + m + 2 * MM));   // 底座要把安装孔包住
                    var holes = new[] { new V2(a / 2, 0), new V2(-a / 2, 0), new V2(0, c / 2), new V2(0, -c / 2) }.Select(v => Loop.Circle(v, m / 2)).ToArray();
                    return new PartGeom()
                        .Add(Op.Extrude("底座", 0, bh, Loop.Circle(bd / 2)))
                        .Add(Op.Cut("安装孔", 0.8 * bh, 0.8 * bh, holes))
                        .Add(Op.Extrude("转子", bh, H - bh, Loop.Circle(od / 2)))
                        .Add(Op.Extrude("轴", H, sl, Loop.Circle(sd / 2)));
                },
            },
            new PartType
            {
                Id = "dev_board", Category = "电子", Name = "开发板外形", Standard = null, Keywords = "树莓派 raspberry pi arduino 开发板 PCB 主板",
                Note = "电路板外形、安装孔和最高的几个接口（USB、网口、排针）的包络块，用于设计外壳和留空间。板的左下角在原点，元件面朝 +Y。",
                Params = { Choice("model", "型号", _ => Boards.Keys.ToArray(), "树莓派 4B") },
                Size = p => p["model"],
                Build = p =>
                {
                    var b = Boards[p["model"]];
                    var pcb = new List<Loop> { Loop.RoundedRect(b.L * MM, b.W * MM, b.R * MM, new V2(b.L / 2 * MM, b.W / 2 * MM)) };
                    pcb.AddRange(b.Holes.Select(h => Loop.Circle(new V2(h[0] * MM, h[1] * MM), h[2] / 2 * MM)));
                    var g = new PartGeom().Add(Op.Extrude("电路板", 0, b.T * MM, pcb));
                    foreach (var c in b.Parts)
                        g.Add(Op.Extrude("接口", b.T * MM, c[4] * MM, Loop.Rect((c[1] - c[0]) * MM, (c[3] - c[2]) * MM, new V2((c[0] + c[1]) / 2 * MM, (c[2] + c[3]) / 2 * MM))));
                    return g;
                },
            },
            new PartType
            {
                Id = "li_cell", Category = "电子", Name = "圆柱锂电池", Standard = null, Keywords = "18650 21700 锂电池 电芯 battery cell",
                Note = "圆柱锂电芯外形（含正极凸点）。负极在 Y = 0。",
                Params = { Choice("model", "型号", _ => Cells.Keys.ToArray(), "18650") },
                Size = p => p["model"],
                Build = p =>
                {
                    var c = Cells[p["model"]];
                    double r = c[0] / 2 * MM, L = c[1] * MM, rb = 0.3 * r, e = 0.5 * MM;
                    return new PartGeom().Add(Op.Revolve("电池", Loop.Polygon(new V2(0, 0), new V2(r - e, 0), new V2(r, e), new V2(r, L - 1.5 * MM), new V2(r - e, L - 1 * MM),
                        new V2(rb, L - 1 * MM), new V2(rb, L), new V2(0, L))));
                },
            },
        };
    }
}
