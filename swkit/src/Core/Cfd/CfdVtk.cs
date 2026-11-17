using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace SwKit.Core.Cfd
{
    /// <summary>
    /// 把结果写成 VTK 旧格式（二进制 STRUCTURED_POINTS），ParaView 直接打开：
    /// U（时间平均速度，m/s，向量）、p（表压，Pa）、solid（1 = 零件，2 = 地面）。点在格子中心，坐标单位米（模型坐标）。
    /// </summary>
    public static class CfdVtk
    {
        public static void Write(CfdResult r, string path)
        {
            if (r?.U == null) throw new InvalidOperationException("没有计算结果");
            var inv = CultureInfo.InvariantCulture;
            long n = r.Cells;
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                void Text(string s) { var b = Encoding.ASCII.GetBytes(s); fs.Write(b, 0, b.Length); }
                Text("# vtk DataFile Version 3.0\nSwKit CFD time-averaged flow (SI units)\nBINARY\nDATASET STRUCTURED_POINTS\n");
                Text($"DIMENSIONS {r.Nx} {r.Ny} {r.Nz}\n");
                Text(string.Format(inv, "ORIGIN {0:R} {1:R} {2:R}\n", r.Origin.X + 0.5 * r.Dx, r.Origin.Y + 0.5 * r.Dx, r.Origin.Z + 0.5 * r.Dx));
                Text(string.Format(inv, "SPACING {0:R} {0:R} {0:R}\n", r.Dx));
                Text($"POINT_DATA {n}\n");
                Text("VECTORS U float\n");
                Floats(fs, r.U, 3 * n);
                Text("\n");
                if (r.P != null)
                {
                    Text("SCALARS p float 1\nLOOKUP_TABLE default\n");
                    Floats(fs, r.P, n);
                    Text("\n");
                }
                Text("SCALARS solid unsigned_char 1\nLOOKUP_TABLE default\n");
                if (r.Solid != null) fs.Write(r.Solid, 0, (int)n);
                else { var z = new byte[1 << 20]; for (long o = 0; o < n; o += z.Length) fs.Write(z, 0, (int)Math.Min(z.Length, n - o)); }
                Text("\n");
            }
        }

        /// <summary>VTK 二进制要求大端序：分块拷贝后逐个翻转字节。</summary>
        static void Floats(Stream s, float[] a, long count)
        {
            const int chunk = 1 << 18;
            var b = new byte[4 * chunk];
            for (long o = 0; o < count; o += chunk)
            {
                int len = (int)Math.Min(chunk, count - o);
                Buffer.BlockCopy(a, (int)(4 * o), b, 0, 4 * len);
                for (int i = 0; i < 4 * len; i += 4)
                {
                    byte t0 = b[i], t1 = b[i + 1];
                    b[i] = b[i + 3]; b[i + 1] = b[i + 2]; b[i + 2] = t1; b[i + 3] = t0;
                }
                s.Write(b, 0, 4 * len);
            }
        }
    }
}
