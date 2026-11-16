using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SwKit.Core.Parts
{
    /// <summary>各分部目录共用的小工具。</summary>
    public static partial class Catalog
    {
        static V2 P(double xMm, double yMm) => new V2(xMm * MM, yMm * MM);

        /// <summary>开口环（C 形）：内半径 ri、外半径 ro、开口宽 g（开口在 +x 方向，开口两边平行）。</summary>
        internal static Loop CRing(double ri, double ro, double g)
        {
            double go = Math.Asin(g / 2 / ro), gi = Math.Asin(g / 2 / ri);
            return new PathBuilder(V2.Polar(ro, go))
                .Arc(new V2(0, 0), V2.Polar(ro, 2 * Math.PI - go))
                .Line(V2.Polar(ri, 2 * Math.PI - gi))
                .Arc(new V2(0, 0), V2.Polar(ri, gi), cw: true)
                .Close();
        }

        /// <summary>节圆上均布的 n 个孔（第一个在角度 start）。</summary>
        internal static IEnumerable<Loop> HoleCircle(double pcd, int n, double d, double start = 0, V2 c = default(V2)) =>
            Enumerable.Range(0, n).Select(i => Loop.Circle(c + V2.Polar(pcd / 2, start + 2 * Math.PI * i / n), d / 2));

        /// <summary>矩形阵列孔：x 方向间距 sx、y 方向间距 sy（只有一排时传 0），中心在 c。</summary>
        internal static IEnumerable<Loop> HoleGrid(double sx, double sy, double d, int nx = 2, int ny = 2, V2 c = default(V2))
        {
            for (int i = 0; i < nx; i++)
                for (int j = 0; j < ny; j++)
                    yield return Loop.Circle(c + new V2((i - (nx - 1) / 2.0) * sx, (j - (ny - 1) / 2.0) * sy), d / 2);
        }

        /// <summary>两端倒角的圆柱（旋转轮廓），从 y0 开始、长 L、两端倒角 c1（底）/ c2（顶）。</summary>
        internal static Loop ChamferedRod(double r, double L, double c1, double c2, double y0 = 0) =>
            new PathBuilder(new V2(0, y0)).Line(r - c1, y0).Line(r, y0 + c1).Line(r, y0 + L - c2).Line(r - c2, y0 + L).Line(0, y0 + L).Close();

        /// <summary>两端倒角的圆管（旋转轮廓）：内半径 ri、外半径 ro。</summary>
        internal static Loop ChamferedTube(double ri, double ro, double L, double c, double y0 = 0) =>
            new PathBuilder(new V2(ri, y0)).Line(ro - c, y0).Line(ro, y0 + c).Line(ro, y0 + L - c).Line(ro - c, y0 + L).Line(ri, y0 + L).Close();

        /// <summary>材料选择参数（零件可以选材料时用）。</summary>
        static ParamDef MaterialParam(string def, params string[] keys) =>
            Choice("mat", "材料", _ => keys.Select(SwPartBuilder.MaterialText).ToArray(), SwPartBuilder.MaterialText(def));
        static Func<Dictionary<string, string>, string> MaterialFromParam(params string[] keys) =>
            p => keys.FirstOrDefault(k => SwPartBuilder.MaterialText(k) == p["mat"]);

        static string[] Range(params double[] v) => v.Select(F).ToArray();
        static int I(Dictionary<string, string> p, string k) => (int)Math.Round(D(p, k));
    }
}
