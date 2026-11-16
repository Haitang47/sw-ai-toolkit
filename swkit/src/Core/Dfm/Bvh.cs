using System;
using System.Collections.Generic;
using System.Linq;
using SwKit.Core.Urdf;

namespace SwKit.Core.Dfm
{
    /// <summary>
    /// 三角形包围体层次（BVH）：按最长轴的形心中位数二分，叶子最多 4 个三角形；
    /// 射线求交用 Möller–Trumbore。线程安全（只读），可以在多个线程里同时投射。
    /// </summary>
    public sealed class Bvh
    {
        readonly DfmMesh _m;
        readonly double[] _v;          // 三角形顶点：每个三角形 9 个数（按 BVH 顺序重排）
        readonly int[] _tri;           // 重排后第 i 个三角形对应的原编号
        float[] _bmin, _bmax;          // 每个节点 3 个数
        int[] _left, _count;           // 内部节点：_left = 左子节点（右 = 左 + 1）、_count = 0；叶子：_left = 第一个三角形、_count = 个数
        int _nodes;

        public Bvh(DfmMesh m)
        {
            _m = m;
            int n = m.Tris;
            var order = Enumerable.Range(0, n).ToArray();
            var cx = new double[n]; var cy = new double[n]; var cz = new double[n];
            for (int t = 0; t < n; t++) { cx[t] = m.C[t].X; cy[t] = m.C[t].Y; cz[t] = m.C[t].Z; }
            int cap = Math.Max(1, 2 * n);
            _bmin = new float[3 * cap]; _bmax = new float[3 * cap]; _left = new int[cap]; _count = new int[cap];
            _nodes = 1;
            var stack = new Stack<(int node, int start, int end)>();
            stack.Push((0, 0, n));
            while (stack.Count > 0)
            {
                var (node, s, e) = stack.Pop();
                // 包围盒
                double x0 = double.MaxValue, y0 = x0, z0 = x0, x1 = double.MinValue, y1 = x1, z1 = x1;
                double c0x = x0, c0y = x0, c0z = x0, c1x = x1, c1y = x1, c1z = x1;
                for (int i = s; i < e; i++)
                {
                    int t = order[i];
                    for (int k = 0; k < 3; k++)
                    {
                        int vi = m.T[3 * t + k];
                        double px = m.V[3 * vi], py = m.V[3 * vi + 1], pz = m.V[3 * vi + 2];
                        if (px < x0) x0 = px; if (py < y0) y0 = py; if (pz < z0) z0 = pz;
                        if (px > x1) x1 = px; if (py > y1) y1 = py; if (pz > z1) z1 = pz;
                    }
                    if (cx[t] < c0x) c0x = cx[t]; if (cy[t] < c0y) c0y = cy[t]; if (cz[t] < c0z) c0z = cz[t];
                    if (cx[t] > c1x) c1x = cx[t]; if (cy[t] > c1y) c1y = cy[t]; if (cz[t] > c1z) c1z = cz[t];
                }
                _bmin[3 * node] = (float)x0 - 1e-4f; _bmin[3 * node + 1] = (float)y0 - 1e-4f; _bmin[3 * node + 2] = (float)z0 - 1e-4f;
                _bmax[3 * node] = (float)x1 + 1e-4f; _bmax[3 * node + 1] = (float)y1 + 1e-4f; _bmax[3 * node + 2] = (float)z1 + 1e-4f;
                int cnt = e - s;
                double ex = c1x - c0x, ey = c1y - c0y, ez = c1z - c0z;
                if (cnt <= 4 || Math.Max(ex, Math.Max(ey, ez)) < 1e-9)
                {
                    _left[node] = s; _count[node] = cnt;
                    continue;
                }
                var key = ex >= ey && ex >= ez ? cx : ey >= ez ? cy : cz;
                int mid = (s + e) / 2;
                NthElement(order, s, e, mid, key);
                int l = _nodes; _nodes += 2;
                _left[node] = l; _count[node] = 0;
                stack.Push((l, s, mid));
                stack.Push((l + 1, mid, e));
            }
            _tri = order;
            _v = new double[9 * n];
            for (int i = 0; i < n; i++)
            {
                int t = order[i];
                for (int k = 0; k < 3; k++)
                {
                    int vi = m.T[3 * t + k];
                    _v[9 * i + 3 * k] = m.V[3 * vi]; _v[9 * i + 3 * k + 1] = m.V[3 * vi + 1]; _v[9 * i + 3 * k + 2] = m.V[3 * vi + 2];
                }
            }
        }

        static void NthElement(int[] a, int s, int e, int nth, double[] key)
        {
            var rnd = new Random(nth);
            while (e - s > 1)
            {
                double pivot = key[a[s + rnd.Next(e - s)]];
                int i = s, j = e - 1;
                while (i <= j)
                {
                    while (key[a[i]] < pivot) i++;
                    while (key[a[j]] > pivot) j--;
                    if (i <= j) { var t = a[i]; a[i] = a[j]; a[j] = t; i++; j--; }
                }
                if (nth <= j) e = j + 1; else if (nth >= i) s = i; else return;
            }
        }

        /// <summary>
        /// 从 o 沿单位方向 d 投射射线，返回最近交点的距离（没有交点返回 +∞），hitTri = 原三角形编号。
        /// skip：忽略的三角形（射线起点所在的三角形）；tMax：最远距离。
        /// </summary>
        public double Cast(Vec3 o, Vec3 d, int skip, out int hitTri, double tMax = double.PositiveInfinity)
        {
            hitTri = -1;
            double best = tMax;
            double ox = o.X, oy = o.Y, oz = o.Z, dx = d.X, dy = d.Y, dz = d.Z;
            double ix = 1 / (Math.Abs(dx) < 1e-300 ? 1e-300 : dx), iy = 1 / (Math.Abs(dy) < 1e-300 ? 1e-300 : dy), iz = 1 / (Math.Abs(dz) < 1e-300 ? 1e-300 : dz);
            var stack = new int[64]; int sp = 0;
            stack[sp++] = 0;
            while (sp > 0)
            {
                int n = stack[--sp];
                // 射线与包围盒（slab）
                double t0 = (_bmin[3 * n] - ox) * ix, t1 = (_bmax[3 * n] - ox) * ix;
                if (t0 > t1) { var x = t0; t0 = t1; t1 = x; }
                double u0 = (_bmin[3 * n + 1] - oy) * iy, u1 = (_bmax[3 * n + 1] - oy) * iy;
                if (u0 > u1) { var x = u0; u0 = u1; u1 = x; }
                double w0 = (_bmin[3 * n + 2] - oz) * iz, w1 = (_bmax[3 * n + 2] - oz) * iz;
                if (w0 > w1) { var x = w0; w0 = w1; w1 = x; }
                double tn = Math.Max(t0, Math.Max(u0, w0)), tf = Math.Min(t1, Math.Min(u1, w1));
                if (tf < Math.Max(tn, 0) || tn > best) continue;
                if (_count[n] == 0)
                {
                    if (sp + 2 > stack.Length) Array.Resize(ref stack, stack.Length * 2);
                    stack[sp++] = _left[n]; stack[sp++] = _left[n] + 1;
                    continue;
                }
                int end = _left[n] + _count[n];
                for (int i = _left[n]; i < end; i++)
                {
                    if (_tri[i] == skip) continue;
                    int b = 9 * i;
                    double e1x = _v[b + 3] - _v[b], e1y = _v[b + 4] - _v[b + 1], e1z = _v[b + 5] - _v[b + 2];
                    double e2x = _v[b + 6] - _v[b], e2y = _v[b + 7] - _v[b + 1], e2z = _v[b + 8] - _v[b + 2];
                    double px = dy * e2z - dz * e2y, py = dz * e2x - dx * e2z, pz = dx * e2y - dy * e2x;
                    double det = e1x * px + e1y * py + e1z * pz;
                    if (Math.Abs(det) < 1e-14) continue;
                    double inv = 1 / det;
                    double sx = ox - _v[b], sy = oy - _v[b + 1], sz = oz - _v[b + 2];
                    double u = (sx * px + sy * py + sz * pz) * inv;
                    if (u < -1e-9 || u > 1 + 1e-9) continue;
                    double qx = sy * e1z - sz * e1y, qy = sz * e1x - sx * e1z, qz = sx * e1y - sy * e1x;
                    double v = (dx * qx + dy * qy + dz * qz) * inv;
                    if (v < -1e-9 || u + v > 1 + 1e-9) continue;
                    double t = (e2x * qx + e2y * qy + e2z * qz) * inv;
                    if (t > 1e-7 && t < best) { best = t; hitTri = _tri[i]; }
                }
            }
            return hitTri >= 0 ? best : double.PositiveInfinity;
        }
    }
}
