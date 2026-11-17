using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using SwKit.Core;
using SwKit.Core.Mass;

namespace SwKit.Cli
{
    /// <summary>
    /// swkit-cli mass &lt;装配体.sldasm | 零件.sldprt | .step | .stl&gt; [--material 名称] [--infill 0.15] [--shell 0.8] [--csv 输出.csv] [--no-memory]
    /// swkit-cli mass materials        列出材料和密度
    /// .sldasm / .sldprt 在独立的后台 SolidWorks 里只读打开；STEP / STL 不需要 SolidWorks。
    /// --material：没有材料的零件都按它算（例如「6061-T6 铝」）。
    /// </summary>
    static class MassCli
    {
        public static int Run(string[] args)
        {
            if (args.Length == 0) { Console.WriteLine("用法：swkit-cli mass <装配体.sldasm | 零件.sldprt | .step | .stl> [--material 名称] [--infill 0.15] [--shell 0.8] [--csv 输出.csv] [--no-memory]  |  swkit-cli mass materials"); return 2; }
            if (args[0] == "materials")
            {
                foreach (var g in MassMaterials.All.GroupBy(m => m.Category))
                {
                    Console.WriteLine(g.Key);
                    foreach (var m in g) Console.WriteLine($"  {m.Name,-28} {m.Density,6:0.###} g/cm³{(m.Fdm ? "  （FDM：按外壳 + 填充率算）" : "")}{(m.Note != null ? "  " + m.Note : "")}");
                }
                return 0;
            }
            string file = Path.GetFullPath(args[0]), material = null, csv = null; bool memory = true;
            var s = new MassSettings();
            for (int i = 1; i < args.Length; i++)
                switch (args[i])
                {
                    case "--material": material = args[++i]; break;
                    case "--infill": { var v = double.Parse(args[++i], CultureInfo.InvariantCulture); s.Infill = v > 1 ? v / 100 : v; break; }
                    case "--shell": s.ShellMm = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--csv": csv = Path.GetFullPath(args[++i]); break;
                    case "--no-memory": memory = false; break;
                }
            var ext = Path.GetExtension(file).ToLowerInvariant();
            MassAssembly asm;
            if (ext == ".sldasm" || ext == ".sldprt")
            {
                using (var ss = SwSession.Connect(SwMode.Isolated, false, Console.WriteLine, CancellationToken.None))
                {
                    int e = 0, w = 0;
                    var doc = ss.App.OpenDoc6(file, ext == ".sldasm" ? 2 : 1, 1 | 2, "", ref e, ref w);   // 静默、只读
                    if (doc == null) { Console.Error.WriteLine("打开失败：" + ExportRunner.LoadErrorText(e)); return 3; }
                    asm = MassReader.ReadSw(ss.App, doc, Console.WriteLine);
                    ss.App.CloseAllDocuments(true);
                }
            }
            else asm = MassReader.ReadMesh(file, Console.WriteLine);

            if (memory) MassStore.Apply(asm.Parts);
            MassStore.AutoAssign(asm.Parts);
            if (material != null)
            {
                var m = MassMaterials.Find(material) ?? throw new ArgumentException("没有这个材料：" + material + "（swkit-cli mass materials 查看）");
                foreach (var p in asm.Parts.Where(p => p.Material == null)) { p.Material = m.Name; p.MaterialWhy = "命令行 --material"; }
            }
            Console.WriteLine();
            Console.WriteLine($"{asm.Name}：{asm.Parts.Count} 种零件、{asm.Instances} 个实例");
            foreach (var p in asm.Parts.OrderByDescending(p => double.IsNaN(MassCalc.UnitGrams(p, s)) ? -1 : MassCalc.SubtotalGrams(p, s)))
            {
                var unit = MassCalc.UnitGrams(p, s);
                Console.WriteLine($"  {p.Name,-30} ×{p.Quantity,-3} {p.Volume * 1e6,9:0.###} cm³  {MassCalc.MaterialLabel(p),-26} {(double.IsNaN(unit) ? "      —" : $"{unit,9:0.##} g")}  小计 {(double.IsNaN(unit) ? "—" : (unit * p.Quantity).ToString("0.##"))} g" +
                                  (p.MaterialWhy != null ? $"  （{p.MaterialWhy}）" : "") + (p.Note != null ? "  ⚠ " + p.Note : ""));
            }
            var sum = MassCalc.Summarize(asm.Parts, s);
            Console.WriteLine();
            Console.WriteLine($"总质量：{sum.TotalGrams:0.##} g（{sum.TotalGrams / 1000:0.###} kg）" + (sum.Unassigned > 0 ? $"；{sum.Unassigned} 种零件没选材料，没有计入" : ""));
            if (sum.Com.HasValue) Console.WriteLine($"重心：({sum.Com.Value.X * 1000:0.##}, {sum.Com.Value.Y * 1000:0.##}, {sum.Com.Value.Z * 1000:0.##}) mm（装配体坐标）");
            foreach (var (mat, g, n) in sum.ByMaterial) Console.WriteLine($"  {mat,-30} {g,10:0.##} g  {g / Math.Max(sum.TotalGrams, 1e-9),6:P1}  {n} 种");
            foreach (var note in asm.Notes) Console.WriteLine("注：" + note);
            if (csv != null) { File.WriteAllText(csv, MassCalc.ToCsv(asm, s), new UTF8Encoding(true)); Console.WriteLine("已写出 " + csv); }
            return sum.Unassigned > 0 ? 1 : 0;
        }
    }
}
