using System;
using System.Collections.Generic;
using System.Globalization;

namespace SwKit.Core.Urdf
{
    /// <summary>三维向量（单位：米）。</summary>
    [Serializable]
    public struct Vec3
    {
        public double X, Y, Z;
        public Vec3(double x, double y, double z) { X = x; Y = y; Z = z; }
        public static readonly Vec3 Zero = new Vec3(0, 0, 0);
        public static Vec3 From(double[] a, int offset = 0) => new Vec3(a[offset], a[offset + 1], a[offset + 2]);

        public static Vec3 operator +(Vec3 a, Vec3 b) => new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec3 operator -(Vec3 a, Vec3 b) => new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vec3 operator -(Vec3 a) => new Vec3(-a.X, -a.Y, -a.Z);
        public static Vec3 operator *(Vec3 a, double s) => new Vec3(a.X * s, a.Y * s, a.Z * s);
        public static Vec3 operator *(double s, Vec3 a) => a * s;
        public static Vec3 operator /(Vec3 a, double s) => new Vec3(a.X / s, a.Y / s, a.Z / s);

        public double Dot(Vec3 b) => X * b.X + Y * b.Y + Z * b.Z;
        public Vec3 Cross(Vec3 b) => new Vec3(Y * b.Z - Z * b.Y, Z * b.X - X * b.Z, X * b.Y - Y * b.X);
        public double Length => Math.Sqrt(Dot(this));
        public Vec3 Normalized() { var l = Length; return l > 1e-15 ? this / l : this; }
        public double this[int i] => i == 0 ? X : i == 1 ? Y : Z;

        /// <summary>任取两个与 n 垂直、彼此垂直的单位向量。</summary>
        public void Perpendiculars(out Vec3 t1, out Vec3 t2)
        {
            var n = Normalized();
            var helper = Math.Abs(n.X) < 0.9 ? new Vec3(1, 0, 0) : new Vec3(0, 1, 0);
            t1 = n.Cross(helper).Normalized();
            t2 = n.Cross(t1).Normalized();
        }

        /// <summary>让绝对值最大的分量为正，便于输出统一的轴方向。</summary>
        public Vec3 Canonical()
        {
            var m = Math.Abs(X) >= Math.Abs(Y) && Math.Abs(X) >= Math.Abs(Z) ? X : Math.Abs(Y) >= Math.Abs(Z) ? Y : Z;
            return m < 0 ? -this : this;
        }

        /// <summary>接近 0 的分量清零（去掉 1e-17 这类数值噪声）。</summary>
        public Vec3 Clean(double eps = 1e-12) => new Vec3(Math.Abs(X) < eps ? 0 : X, Math.Abs(Y) < eps ? 0 : Y, Math.Abs(Z) < eps ? 0 : Z);

        public string Fmt(string f = "0.######") => $"{Num(X, f)} {Num(Y, f)} {Num(Z, f)}";
        public static string Num(double v, string f = "0.######")
        {
            var s = v.ToString(f, CultureInfo.InvariantCulture);
            return s == "-0" ? "0" : s;
        }
        public override string ToString() => $"({Num(X, "0.####")}, {Num(Y, "0.####")}, {Num(Z, "0.####")})";
    }

    /// <summary>3×3 矩阵（行优先）。</summary>
    [Serializable]
    public struct Mat3
    {
        public double[] M;   // 长度 9
        public Mat3(double[] m) { M = m; }
        public static Mat3 Zero => new Mat3(new double[9]);
        public static Mat3 Identity => new Mat3(new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 });
        public double this[int r, int c] => M[r * 3 + c];

        public static Mat3 operator +(Mat3 a, Mat3 b) { var r = new double[9]; for (int i = 0; i < 9; i++) r[i] = a.M[i] + b.M[i]; return new Mat3(r); }
        public static Mat3 operator *(Mat3 a, double s) { var r = new double[9]; for (int i = 0; i < 9; i++) r[i] = a.M[i] * s; return new Mat3(r); }
        public static Mat3 operator *(Mat3 a, Mat3 b)
        {
            var r = new double[9];
            for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) { double s = 0; for (int k = 0; k < 3; k++) s += a[i, k] * b[k, j]; r[i * 3 + j] = s; }
            return new Mat3(r);
        }
        public static Vec3 operator *(Mat3 a, Vec3 v) => new Vec3(a[0, 0] * v.X + a[0, 1] * v.Y + a[0, 2] * v.Z, a[1, 0] * v.X + a[1, 1] * v.Y + a[1, 2] * v.Z, a[2, 0] * v.X + a[2, 1] * v.Y + a[2, 2] * v.Z);
        public Mat3 T() => new Mat3(new[] { M[0], M[3], M[6], M[1], M[4], M[7], M[2], M[5], M[8] });

        /// <summary>绕单位轴 axis 旋转 angle 弧度（罗德里格斯公式）。</summary>
        public static Mat3 Rotation(Vec3 axis, double angle)
        {
            var k = axis.Normalized();
            double c = Math.Cos(angle), s = Math.Sin(angle), t = 1 - c;
            return new Mat3(new[]
            {
                t * k.X * k.X + c,       t * k.X * k.Y - s * k.Z, t * k.X * k.Z + s * k.Y,
                t * k.X * k.Y + s * k.Z, t * k.Y * k.Y + c,       t * k.Y * k.Z - s * k.X,
                t * k.X * k.Z - s * k.Y, t * k.Y * k.Z + s * k.X, t * k.Z * k.Z + c,
            });
        }

        /// <summary>平行轴定理：质心处惯量 → 相对点 d（质心到参考点的向量为 d）的惯量。</summary>
        public static Mat3 ParallelAxis(double mass, Vec3 d)
        {
            var dd = d.Dot(d);
            return new Mat3(new[]
            {
                mass * (dd - d.X * d.X), -mass * d.X * d.Y,       -mass * d.X * d.Z,
                -mass * d.Y * d.X,       mass * (dd - d.Y * d.Y), -mass * d.Y * d.Z,
                -mass * d.Z * d.X,       -mass * d.Z * d.Y,       mass * (dd - d.Z * d.Z),
            });
        }
    }

    /// <summary>刚体位姿：p_world = R·p + T。</summary>
    [Serializable]
    public struct Pose
    {
        public Mat3 R; public Vec3 T;
        public Pose(Mat3 r, Vec3 t) { R = r; T = t; }
        public static Pose Identity => new Pose(Mat3.Identity, Vec3.Zero);
        public Vec3 Apply(Vec3 p) => R * p + T;
        public Vec3 ApplyDir(Vec3 d) => R * d;
        public Pose Inverse() { var rt = R.T(); return new Pose(rt, -(rt * T)); }
        public static Pose operator *(Pose a, Pose b) => new Pose(a.R * b.R, a.R * b.T + a.T);
    }

    public static class LinAlg
    {
        /// <summary>对称矩阵的雅可比特征分解。返回特征值，vectors 的第 j 列是对应的特征向量。</summary>
        public static double[] SymEigen(double[,] a, out double[,] vectors)
        {
            int n = a.GetLength(0);
            var A = (double[,])a.Clone();
            var V = new double[n, n];
            for (int i = 0; i < n; i++) V[i, i] = 1;
            for (int sweep = 0; sweep < 100; sweep++)
            {
                double off = 0;
                for (int i = 0; i < n; i++) for (int j = i + 1; j < n; j++) off += A[i, j] * A[i, j];
                if (off < 1e-30) break;
                for (int p = 0; p < n; p++)
                    for (int q = p + 1; q < n; q++)
                    {
                        if (Math.Abs(A[p, q]) < 1e-300) continue;
                        double theta = (A[q, q] - A[p, p]) / (2 * A[p, q]);
                        double t = Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                        if (theta == 0) t = 1;
                        double c = 1 / Math.Sqrt(t * t + 1), s = t * c;
                        for (int k = 0; k < n; k++)
                        {
                            double akp = A[k, p], akq = A[k, q];
                            A[k, p] = c * akp - s * akq; A[k, q] = s * akp + c * akq;
                        }
                        for (int k = 0; k < n; k++)
                        {
                            double apk = A[p, k], aqk = A[q, k];
                            A[p, k] = c * apk - s * aqk; A[q, k] = s * apk + c * aqk;
                        }
                        for (int k = 0; k < n; k++)
                        {
                            double vkp = V[k, p], vkq = V[k, q];
                            V[k, p] = c * vkp - s * vkq; V[k, q] = s * vkp + c * vkq;
                        }
                    }
            }
            vectors = V;
            var vals = new double[n];
            for (int i = 0; i < n; i++) vals[i] = A[i, i];
            return vals;
        }

        /// <summary>
        /// 约束矩阵（每行 6 个数，作用在相对运动旋量 [ω; v] 上）的零空间，即剩下的自由度。
        /// 行会先归一化；特征值小于 tol 视为零。
        /// </summary>
        public static List<double[]> NullSpace(IList<double[]> rows, double tol = 1e-8)
        {
            var ctc = new double[6, 6];
            foreach (var r0 in rows)
            {
                double norm = 0; foreach (var x in r0) norm += x * x;
                norm = Math.Sqrt(norm);
                if (norm < 1e-12) continue;
                for (int i = 0; i < 6; i++) for (int j = 0; j < 6; j++) ctc[i, j] += r0[i] * r0[j] / (norm * norm);
            }
            var vals = SymEigen(ctc, out var vecs);
            var result = new List<double[]>();
            for (int k = 0; k < 6; k++)
            {
                if (vals[k] > tol) continue;
                var v = new double[6];
                for (int i = 0; i < 6; i++) v[i] = vecs[i, k];
                result.Add(v);
            }
            return result;
        }
    }
}
