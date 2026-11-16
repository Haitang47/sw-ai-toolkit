using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SwKit.Core.Urdf;

namespace SwKit.Core.Fea
{
    /// <summary>
    /// 自带的示例零件（发布版的「示例」文件夹，开发时在 tools\samples）以及它们的约束和载荷（按几何规则选面，不依赖面编号）。
    /// </summary>
    public static class FeSamples
    {
        public sealed class Sample
        {
            public string Id, Name, File, Description, Material;
            public bool ForTopology;
        }

        public static readonly List<Sample> All = new List<Sample>
        {
            new Sample { Id = "bracket", Name = "L 形支架", File = "L形支架.step", Material = "6061-T6",
                Description = "底面固定（用螺栓压紧在台面上），立板顶面受 300 N 水平推力。看内角圆角处的应力集中，再试试自适应加密。" },
            new Sample { Id = "cantilever", Name = "悬臂块", File = "悬臂块.step", Material = "6061-T6", ForTopology = true,
                Description = "左端面固定，右端面中间受 2000 N 向下的力。拓扑优化只保留 30% 材料，看材料该放在哪里。" },
        };

        public static string Folder()
        {
            var dir = AppDomain.CurrentDomain.BaseDirectory;
            foreach (var c in new[] { Path.Combine(dir, "示例"), Path.Combine(dir, "..", "tools", "samples") })
                if (Directory.Exists(c)) return Path.GetFullPath(c);
            return null;
        }

        public static string PathOf(Sample s) { var f = Folder(); return f == null ? null : Path.Combine(f, s.File); }

        /// <summary>按几何规则给示例零件设置约束和载荷。</summary>
        public static void Setup(Sample s, FeMesh m, List<int> fixedFaces, List<FeLoad> loads)
        {
            fixedFaces.Clear(); loads.Clear();
            m.Bounds(out var mn, out var mx);
            switch (s.Id)
            {
                case "bracket":
                {
                    // 底面（法向 −Y、最低的平面）
                    fixedFaces.Add(m.Faces.Where(f => f.IsPlane && f.Normal.Y < -0.99).OrderBy(f => f.Centroid.Y).First().Id);
                    // 立板顶面：法向 +Y、最高的平面
                    var top = m.Faces.Where(f => f.IsPlane && f.Normal.Y > 0.99).OrderByDescending(f => f.Centroid.Y).First();
                    loads.Add(new FeLoad { Kind = LoadKind.Force, Faces = { top.Id }, Value = 300, Dir = new Vec3(1, 0, 0) });
                    break;
                }
                case "cantilever":
                {
                    fixedFaces.Add(m.Faces.Where(f => f.IsPlane && f.Normal.X < -0.99).OrderBy(f => f.Centroid.X).First().Id);
                    // 右端面上压印出来的窄条（右端面里面积最小的那块）
                    var strip = m.Faces.Where(f => f.IsPlane && f.Normal.X > 0.99 && f.Centroid.X > mx.X - 1e-4).OrderBy(f => f.Area).First();
                    loads.Add(new FeLoad { Kind = LoadKind.Force, Faces = { strip.Id }, Value = 2000, Dir = new Vec3(0, -1, 0) });
                    break;
                }
            }
        }
    }
}
