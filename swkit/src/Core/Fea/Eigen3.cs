using System;
using SwKit.Core.Urdf;

namespace SwKit.Core.Fea
{
    /// <summary>3×3 对称矩阵的特征值和特征向量（Jacobi 旋转），按特征值从小到大排列。</summary>
    public static class Eigen3
    {
        public static void Symmetric(double[,] a, out double[] values, out Vec3[] vectors)
        {
            var A = (double[,])a.Clone();
            var V = new double[3, 3] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
            for (int sweep = 0; sweep < 50; sweep++)
            {
                double off = Math.Abs(A[0, 1]) + Math.Abs(A[0, 2]) + Math.Abs(A[1, 2]);
                if (off < 1e-20 * (Math.Abs(A[0, 0]) + Math.Abs(A[1, 1]) + Math.Abs(A[2, 2]) + 1e-300)) break;
                for (int p = 0; p < 2; p++)
                    for (int q = p + 1; q < 3; q++)
                    {
                        if (Math.Abs(A[p, q]) < 1e-300) continue;
                        double theta = (A[q, q] - A[p, p]) / (2 * A[p, q]);
                        double t = theta == 0 ? 1 : Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                        double c = 1 / Math.Sqrt(t * t + 1), s = t * c;
                        for (int k = 0; k < 3; k++) { double akp = A[k, p], akq = A[k, q]; A[k, p] = c * akp - s * akq; A[k, q] = s * akp + c * akq; }
                        for (int k = 0; k < 3; k++) { double apk = A[p, k], aqk = A[q, k]; A[p, k] = c * apk - s * aqk; A[q, k] = s * apk + c * aqk; }
                        for (int k = 0; k < 3; k++) { double vkp = V[k, p], vkq = V[k, q]; V[k, p] = c * vkp - s * vkq; V[k, q] = s * vkp + c * vkq; }
                    }
            }
            var idx = new[] { 0, 1, 2 };
            Array.Sort(idx, (i, j) => A[i, i].CompareTo(A[j, j]));
            values = new double[3]; vectors = new Vec3[3];
            for (int k = 0; k < 3; k++)
            {
                int i = idx[k];
                values[k] = A[i, i];
                vectors[k] = new Vec3(V[0, i], V[1, i], V[2, i]).Normalized();
            }
        }

        /// <summary>n×n 对称矩阵（n 几百以内）的全部特征值和特征向量（循环 Jacobi），特征值从小到大；vectors[k] 是第 k 个特征向量。</summary>
        public static void SymmetricN(double[,] a, out double[] values, out double[][] vectors)
        {
            int n = a.GetLength(0);
            var A = (double[,])a.Clone();
            var V = new double[n, n];
            for (int i = 0; i < n; i++) V[i, i] = 1;
            double scale = 0; for (int i = 0; i < n; i++) scale += Math.Abs(A[i, i]);
            for (int sweep = 0; sweep < 60; sweep++)
            {
                double off = 0;
                for (int p = 0; p < n; p++) for (int q = p + 1; q < n; q++) off += A[p, q] * A[p, q];
                if (off <= 1e-28 * (scale * scale + 1e-300)) break;
                for (int p = 0; p < n - 1; p++)
                    for (int q = p + 1; q < n; q++)
                    {
                        double apq = A[p, q];
                        if (Math.Abs(apq) < 1e-300) continue;
                        double theta = (A[q, q] - A[p, p]) / (2 * apq);
                        double t = Math.Sign(theta == 0 ? 1 : theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                        double c = 1 / Math.Sqrt(t * t + 1), s = t * c;
                        for (int k = 0; k < n; k++) { double akp = A[k, p], akq = A[k, q]; A[k, p] = c * akp - s * akq; A[k, q] = s * akp + c * akq; }
                        for (int k = 0; k < n; k++) { double apk = A[p, k], aqk = A[q, k]; A[p, k] = c * apk - s * aqk; A[q, k] = s * apk + c * aqk; }
                        for (int k = 0; k < n; k++) { double vkp = V[k, p], vkq = V[k, q]; V[k, p] = c * vkp - s * vkq; V[k, q] = s * vkp + c * vkq; }
                    }
            }
            var idx = new int[n]; for (int i = 0; i < n; i++) idx[i] = i;
            Array.Sort(idx, (i, j) => A[i, i].CompareTo(A[j, j]));
            values = new double[n]; vectors = new double[n][];
            for (int k = 0; k < n; k++)
            {
                int i = idx[k];
                values[k] = A[i, i];
                vectors[k] = new double[n];
                for (int r = 0; r < n; r++) vectors[k][r] = V[r, i];
            }
        }
    }
}
