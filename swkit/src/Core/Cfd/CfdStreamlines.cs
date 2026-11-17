using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SwKit.Core.Urdf;

namespace SwKit.Core.Cfd
{
    /// <summary>流线种子的类别（和常见的旋翼流场图一致）。</summary>
    public enum CfdSeedSet { Inlet, OuterTip, Above, Custom }

    public sealed class CfdStreamline
    {
        public CfdSeedSet Set;
        public int Disk = -1;                      // 从哪个桨盘出发（-1 = 不属于某个盘）
        public List<Vec3> Points = new List<Vec3>();
        public List<float> Speeds = new List<float>();
        public double Length;
    }

    /// <summary>
    /// 在时间平均速度场里画流线：四阶龙格–库塔，步长约半个格子，速度按三线性插值。
    /// 碰到固体、出了计算域、速度降到很小或者转圈停在回流区时停止。
    /// </summary>
    public static class CfdStreamlines
    {
        public static string SetName(CfdSeedSet s) => s == CfdSeedSet.Inlet ? "盘面进口" : s == CfdSeedSet.OuterTip ? "桨尖外圈" : s == CfdSeedSet.Above ? "盘面上方" : "自定义";

        /// <summary>
        /// 每个桨盘的种子：
        ///  · 盘面进口：盘面上游一点（0.15R），半径 0.35 / 0.6 / 0.85 R 的三圈；
        ///  · 桨尖外圈：1.08R 的一圈，看桨尖涡和外侧的卷吸；
        ///  · 盘面上方：上游 0.8R 处、盘面范围内的网格，看来流从哪里被吸进来。
        /// density：1 = 标准，2 = 加倍。
        /// </summary>
        public static List<(Vec3 p, CfdSeedSet set, int disk)> Seeds(CfdResult r, bool inlet, bool tip, bool above, double density = 1)
        {
            var list = new List<(Vec3, CfdSeedSet, int)>();
            var disks = r.Setup?.Disks ?? new List<CfdDisk>();
            for (int d = 0; d < disks.Count; d++)
            {
                var k = disks[d];
                var a = k.Axis.Normalized();
                a.Perpendiculars(out var e1, out var e2);
                double R = k.Radius;
                if (inlet)
                {
                    var c = k.Center + a * (0.15 * R);
                    foreach (var (rr, n) in new[] { (0.35, 6), (0.6, 10), (0.85, 14) })
                    {
                        int m = Math.Max(3, (int)Math.Round(n * density));
                        for (int i = 0; i < m; i++)
                        {
                            double t = 2 * Math.PI * (i + 0.5 * (n % 2)) / m;
                            list.Add((c + (e1 * Math.Cos(t) + e2 * Math.Sin(t)) * (rr * R), CfdSeedSet.Inlet, d));
                        }
                    }
                }
                if (tip)
                {
                    var c = k.Center + a * (0.05 * R);
                    int m = Math.Max(6, (int)Math.Round(16 * density));
                    for (int i = 0; i < m; i++)
                    {
                        double t = 2 * Math.PI * i / m;
                        list.Add((c + (e1 * Math.Cos(t) + e2 * Math.Sin(t)) * (1.08 * R), CfdSeedSet.OuterTip, d));
                    }
                }
                if (above)
                {
                    var c = k.Center + a * (0.8 * R);
                    int m = Math.Max(3, (int)Math.Round(5 * density));
                    for (int i = 0; i < m; i++)
                        for (int j = 0; j < m; j++)
                        {
                            double u = (i + 0.5) / m * 2 - 1, v = (j + 0.5) / m * 2 - 1;
                            if (u * u + v * v > 1) continue;
                            list.Add((c + e1 * (u * R) + e2 * (v * R), CfdSeedSet.Above, d));
                        }
                }
            }
            // 种子不能在固体里
            return list.Where(s => r.Inside(s.Item1) && !r.IsSolid(s.Item1)).ToList();
        }

        public static List<CfdStreamline> Trace(CfdResult r, IList<(Vec3 p, CfdSeedSet set, int disk)> seeds, bool backward = false, int maxPoints = 3000)
        {
            // 最大速度（停止判据用）
            double umax = 0;
            for (long i = 0; i < r.U.Length; i += 3)
            {
                double s = r.U[i] * r.U[i] + r.U[i + 1] * r.U[i + 1] + r.U[i + 2] * r.U[i + 2];
                if (s > umax) umax = s;
            }
            umax = Math.Sqrt(umax);
            double h = 0.5 * r.Dx;                        // 每步走半个格子
            double stopSpeed = Math.Max(0.02 * umax, 0.05);
            var lines = new CfdStreamline[seeds.Count];
            Parallel.For(0, seeds.Count, n =>
            {
                var (p0, set, disk) = seeds[n];
                var line = new CfdStreamline { Set = set, Disk = disk };
                double sign = backward ? -1 : 1;
                Vec3 Dir(Vec3 p) { var u = r.Sample(p); var s = u.Length; return s < 1e-12 ? Vec3.Zero : u * (sign / s); }
                var p = p0;
                var u0 = r.Sample(p);
                line.Points.Add(p); line.Speeds.Add((float)u0.Length);
                int slow = 0;
                for (int it = 0; it < maxPoints; it++)
                {
                    // RK4（按弧长积分：方向场归一化，步长固定，速度慢的地方不会卡住）
                    var k1 = Dir(p);
                    if (k1.Length < 0.5) break;
                    var k2 = Dir(p + k1 * (h / 2));
                    var k3 = Dir(p + k2 * (h / 2));
                    var k4 = Dir(p + k3 * h);
                    var step = (k1 + k2 * 2 + k3 * 2 + k4) * (h / 6);
                    var q = p + step;
                    if (!r.Inside(q) || r.IsSolid(q)) break;
                    var uq = r.Sample(q);
                    double sp = uq.Length;
                    if (sp < stopSpeed) { if (++slow > 20) break; } else slow = 0;
                    line.Length += step.Length;
                    p = q;
                    // 每两步记一个点（够平滑，点数少一半）
                    if ((it & 1) == 1) { line.Points.Add(p); line.Speeds.Add((float)sp); }
                    // 回流区里转圈：走了很长但离起点不远
                    if (it > 400 && it % 200 == 0 && (p - p0).Length < 0.05 * line.Length) break;
                }
                if (backward) { line.Points.Reverse(); line.Speeds.Reverse(); }
                lines[n] = line;
            });
            return lines.Where(l => l.Points.Count >= 3).ToList();
        }
    }
}
