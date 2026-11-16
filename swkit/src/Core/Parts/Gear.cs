using System;
using System.Collections.Generic;
using System.Linq;

namespace SwKit.Core.Parts
{
    /// <summary>
    /// 渐开线直齿轮的齿廓（可以变位）。
    /// 分度圆 r = m·z/2，基圆 rb = r·cos α，齿顶圆 ra = r + m(1 + x)，齿根圆 rf = r − m(1.25 − x)，
    /// 分度圆齿厚 s = m(π/2 + 2x·tan α)。
    /// 渐开线上半径 ρ 处，齿厚对应的半角 β(ρ) = s/(2r) + inv α − inv φρ，其中 φρ = acos(rb/ρ)，inv φ = tan φ − φ。
    /// 齿根圆小于基圆时，基圆以下用径向直线连到齿根圆（不做根切和齿根圆角，3D 打印和一般设计足够）。
    /// 第 0 个齿的中心线在草图 +x 方向。
    /// </summary>
    public static class Gear
    {
        public static double Inv(double phi) => Math.Tan(phi) - phi;

        /// <summary>反渐开线函数：已知 inv φ 求 φ（牛顿迭代）。</summary>
        public static double InvInverse(double inv)
        {
            double phi = Math.Pow(3 * inv, 1.0 / 3);   // 小角度时 inv φ ≈ φ³/3
            for (int i = 0; i < 30; i++)
            {
                var f = Inv(phi) - inv;
                var df = Math.Tan(phi) * Math.Tan(phi);
                phi -= f / df;
            }
            return phi;
        }

        /// <param name="m">模数（米）</param>
        /// <param name="z">齿数</param>
        /// <param name="alpha">压力角（弧度）</param>
        /// <param name="backlash">齿侧间隙（米，在分度圆上量的总量，本齿轮减薄一半）</param>
        /// <param name="x">变位系数（正变位齿更厚、齿根更粗）</param>
        public static Loop Outline(double m, int z, double alpha, double backlash, int flankPoints = 12, double x = 0)
        {
            double r = m * z / 2, rb = r * Math.Cos(alpha), ra = r + m * (1 + x), rf = r - m * (1.25 - x);
            double s = m * (Math.PI / 2 + 2 * x * Math.Tan(alpha)) - backlash / 2;   // 分度圆齿厚
            double psi = s / (2 * r);                                                  // 分度圆上的半齿厚角
            double Beta(double rho) => psi + Inv(alpha) - Inv(Math.Acos(Math.Min(1, rb / rho)));

            // 齿数少或正变位大时齿顶会变尖：把齿顶圆限制在齿厚降到 0.25m 的位置以内
            while (Beta(ra) * ra < 0.125 * m && ra > Math.Max(r, rb) + 0.05 * m) ra -= 0.01 * m;
            double rStart = Math.Max(rb, rf);
            double betaStart = Beta(rStart);
            double pitch = 2 * Math.PI / z;
            // 齿根两侧的直线不能和相邻齿相交
            if (betaStart * 2 >= pitch) throw new ArgumentException("齿数太少、负变位太大或间隙太大，齿根重叠");
            if (Beta(ra) <= 0) throw new ArgumentException("齿顶变尖：正变位太大");

            var loop = new Loop();
            for (int k = 0; k < z; k++)
            {
                double c = k * pitch;
                // 齿的右侧（角度较小一侧）：齿根 → 基圆 → 渐开线 → 齿顶
                if (rf < rb) loop.Segs.Add(new LineSeg(V2.Polar(rf, c - betaStart), V2.Polar(rb, c - betaStart)));
                var right = Enumerable.Range(0, flankPoints).Select(i => rStart + (ra - rStart) * i / (flankPoints - 1)).Select(rho => V2.Polar(rho, c - Beta(rho))).ToList();
                loop.Segs.Add(new SplineSeg(right));
                loop.Segs.Add(new ArcSeg(new V2(0, 0), ra, c - Beta(ra), c + Beta(ra)));
                var left = Enumerable.Range(0, flankPoints).Select(i => ra - (ra - rStart) * i / (flankPoints - 1)).Select(rho => V2.Polar(rho, c + Beta(rho))).ToList();
                loop.Segs.Add(new SplineSeg(left));
                if (rf < rb) loop.Segs.Add(new LineSeg(V2.Polar(rb, c + betaStart), V2.Polar(rf, c + betaStart)));
                // 齿根圆弧到下一个齿
                loop.Segs.Add(new ArcSeg(new V2(0, 0), rf < rb ? rf : rStart, c + betaStart, c + pitch - betaStart));
            }
            return loop;
        }

        /// <summary>两个外啮合齿轮的中心距（标准安装）。</summary>
        public static double CenterDistance(double m, int z1, int z2) => m * (z1 + z2) / 2;

        /// <summary>不根切的最小变位系数（压力角 20°、齿顶高系数 1：x ≥ (17 − z)/17）。</summary>
        public static double MinShift(int z) => (17.0 - z) / 17;
    }
}
