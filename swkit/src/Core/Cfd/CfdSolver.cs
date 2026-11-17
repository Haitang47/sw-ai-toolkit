using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SwKit.Core.Urdf;

namespace SwKit.Core.Cfd
{
    /// <summary>
    /// 外流场 CFD：格子玻尔兹曼 D3Q19 + Smagorinsky 大涡模拟，转子 / 涵道风扇用作动盘（体积力）代替。
    /// 优先用显卡（OpenCL），没有或出错时用 CPU。
    ///
    /// 单位换算（物理量 → 格子单位，ρ_格 = 1 对应空气密度 ρ）：
    ///   速度  u_格 = u·dt/dx            时间步 dt 让最大预计速度（最强桨盘的远尾流 2·v_i + 风速）对应 u_格 ≈ 0.09
    ///   黏度  ν_格 = ν·dt/dx²，τ0 = 3ν_格 + 0.5
    ///   力密度 F_格 = (N/m³)·dt²/(ρ·dx)，即每个格子里的力 F(N)·dt²/(ρ·dx⁴)
    ///   受力  F(N) = F_格·ρ·dx⁴/dt²        压力 p − p0 = δρ_格·(1/3)·ρ·(dx/dt)²
    /// </summary>
    public static class CfdSolver
    {
        /// <summary>CPU 自动网格的格子数上限（内存：每格约 180 字节）。</summary>
        public const long CpuMaxCells = 3_000_000;
        const double ULat = 0.09;          // 最大预计速度对应的格子速度
        const int GpuBytesPerCell = 173;   // 两份分布函数 152 + 类型 1 + 体积力索引 4 + 时间平均 16
        const int SampleEvery = 3;         // 流场时间平均的取样间隔（步）：奇数，单双步交替取样

        // ================= 设备 =================

        /// <summary>可用的计算设备，最快的在前面。没有 OpenCL 时只有 CPU，不抛异常。</summary>
        public static IList<string> Devices()
        {
            var list = new List<string>();
            try { foreach (var d in Cl.Devices()) list.Add(d.Display); } catch { }
            list.Add(CpuName);
            return list;
        }

        static string CpuName => $"CPU（{Environment.ProcessorCount} 线程）";

        static bool WantsCpu(string want) => want != null && (want.Trim().Equals("cpu", StringComparison.OrdinalIgnoreCase) || want.Trim().StartsWith("CPU", StringComparison.OrdinalIgnoreCase));

        /// <summary>按名字挑设备：null / "gpu" = 最好的显卡；"cpu" = CPU；或者 Devices() 里的名字（也可以是其中一段）。返回 null = CPU。</summary>
        internal static ClDevice PickDevice(string want)
        {
            if (WantsCpu(want)) return null;
            List<ClDevice> devs;
            try { devs = Cl.Devices(); } catch { return null; }
            if (devs.Count == 0) return null;
            if (string.IsNullOrWhiteSpace(want) || want.Trim().Equals("gpu", StringComparison.OrdinalIgnoreCase) || want.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase)) return devs[0];
            return devs.FirstOrDefault(d => d.Display == want)
                ?? devs.FirstOrDefault(d => d.Display.IndexOf(want.Trim(), StringComparison.OrdinalIgnoreCase) >= 0 || want.IndexOf(d.Name, StringComparison.OrdinalIgnoreCase) >= 0)
                ?? devs[0];
        }

        static long MaxCells(ClDevice dev) => dev == null ? CpuMaxCells : Math.Min((long)(dev.GlobalMem * 0.85 / GpuBytesPerCell), (long)(dev.MaxAlloc / 76));

        /// <summary>算之前估计网格：格子数、格子尺寸、原点、各方向格子数（按 s.Device 套用显卡显存 / CPU 的上限）。</summary>
        public static long EstimateCells(CfdSetup s, out double dx, out Vec3 origin, out int nx, out int ny, out int nz)
        {
            var d = CfdDomain.Build(s, MaxCells(PickDevice(s.Device)));
            dx = d.Dx; origin = d.Origin; nx = d.Nx; ny = d.Ny; nz = d.Nz;
            return d.N;
        }

        // ================= 计算 =================

        /// <summary>
        /// 计算到 s.SimTime（0 = 自动），返回时间平均流场和受力。progress 大约每 0.7 秒回调一次（在计算线程上）。
        /// 取消时抛 OperationCanceledException（显存已释放）。显卡出错时自动改用 CPU 重算，原因写在 Notes 里。
        /// </summary>
        public static CfdResult Run(CfdSetup s, Action<string> log, Action<CfdProgress> progress, CancellationToken ct) => Run(s, log, progress, ct, 0);

        /// <summary>fixedSteps &gt; 0：只算这么多步（测速用）。</summary>
        internal static CfdResult Run(CfdSetup s, Action<string> log, Action<CfdProgress> progress, CancellationToken ct, int fixedSteps)
        {
            if (s == null) throw new ArgumentNullException(nameof(s));
            log = log ?? (_ => { });
            if (s.Disks == null) s.Disks = new List<CfdDisk>();
            if (!s.Disks.Any(d => d.Thrust > 0 && d.Radius > 0) && s.Wind.Length <= 0)
                throw new InvalidOperationException("没有桨盘也没有来流，空气不会动：先放桨盘或者设来流风速");
            string note = null;
            ClDevice dev = PickDevice(s.Device);
            if (dev == null && !WantsCpu(s.Device))
            {
                note = "没有找到可用的 OpenCL 显卡，用 CPU 计算" + (Cl.ProbeError != null ? "（" + Cl.ProbeError + "）" : "");
                log(note);
            }
            try
            {
                if (dev != null)
                {
                    try { return RunOn(s, dev, log, progress, ct, null, fixedSteps); }
                    catch (ClException ex) when (ex.Message.Contains("ALLOCATION") || ex.Message.Contains("OUT_OF_RESOURCES"))
                    {
                        // 显存暂时不够（例如上一个计算刚结束、驱动还没回收，或者别的程序占着显存）：等一下再试一次显卡，比改用 CPU 快几十倍
                        log("显存暂时不够，3 秒后用显卡再试一次……");
                        GC.Collect(); GC.WaitForPendingFinalizers();
                        if (ct.WaitHandle.WaitOne(3000)) ct.ThrowIfCancellationRequested();
                        try { return RunOn(s, dev, log, progress, ct, null, fixedSteps); }
                        catch (ClException ex2) { log("显卡计算出错：" + ex2.Message); note = "显存不够，已改用 CPU（可以关掉占显存的程序、或者选「粗」网格后重算）：" + Short(ex2.Message); }
                    }
                    catch (ClException ex) { log("显卡计算出错：" + ex.Message); note = "显卡计算出错，已改用 CPU：" + Short(ex.Message); }
                    catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException || ex is SEHException || ex is AccessViolationException)
                    { log("显卡驱动调用失败：" + ex.Message); note = "显卡驱动调用失败，已改用 CPU：" + Short(ex.Message); }
                    log("改用 CPU 重新计算");
                    GC.Collect();
                }
                return RunOn(s, null, log, progress, ct, note, fixedSteps);
            }
            catch (OutOfMemoryException)
            {
                GC.Collect();
                throw new InvalidOperationException("内存不够：请用粗一点的网格（少一些格子），或者关掉别的程序再算");
            }
        }

        static string Short(string m)
        {
            m = (m ?? "").Trim();
            int nl = m.IndexOf('\n');
            if (nl > 0 && nl < 200) m = m.Substring(0, nl).TrimEnd('：', ':') + "……";
            return m.Length > 300 ? m.Substring(0, 300) + "……" : m;
        }

        sealed class Prep
        {
            public CfdDomain Dom;
            public LbmData Data;
            public byte[] Solid;                 // 结果用：1 = 零件，2 = 地面
            public List<DiskCells> Disks = new List<DiskCells>();
            public double Dt, Vel;               // Vel = dx/dt：格子速度 → m/s
            public int Steps, AvgStart;
            public string[] PartNames;
        }

        static CfdResult RunOn(CfdSetup s, ClDevice dev, Action<string> log, Action<CfdProgress> progress, CancellationToken ct, string note, int fixedSteps)
        {
            var sw = Stopwatch.StartNew();
            var r = new CfdResult { Setup = s, Device = dev?.Display ?? CpuName };
            if (note != null) r.Notes.Add(note);
            progress?.Invoke(new CfdProgress { Phase = "生成网格", Seconds = sw.Elapsed.TotalSeconds });
            var p = Prepare(s, dev, log, r.Notes, ct, fixedSteps);
            var dom = p.Dom; var data = p.Data;
            r.Nx = dom.Nx; r.Ny = dom.Ny; r.Nz = dom.Nz; r.Origin = dom.Origin; r.Dx = dom.Dx;
            r.Dt = p.Dt; r.Steps = p.Steps; r.SimTime = p.Steps * p.Dt;
            long N = dom.N;
            double rho = s.Density, dx = dom.Dx, dt = p.Dt;
            double forceN = rho * Math.Pow(dx, 4) / (dt * dt);     // 格子力 → N
            int parts = data.Parts;

            ILbm lbm;
            try { lbm = dev != null ? (ILbm)new LbmGpu(dev, data) : new LbmCpu(data); }
            catch (OutOfMemoryException) { throw new InvalidOperationException($"内存不够：{N / 1e6:0.#} M 格的网格放不下，请用粗一点的网格或者关掉别的程序"); }
            using (lbm)
            {
                r.Device = lbm.Name;
                log($"开始计算：{lbm.Name}，{p.Steps:N0} 步（物理时间 {p.Steps * dt * 1000:0.#} ms，时间步 {dt * 1e6:0.###} µs），从第 {p.AvgStart:N0} 步开始做时间平均");
                lbm.Init();
                var ivl = new float[3 * Math.Max(1, parts)];
                var acc = new float[3 * Math.Max(1, parts)];
                int histEvery = Math.Max(2, (p.Steps / 400) & ~1), ivlSteps = 0;   // 偶数步一段：单双步振荡正好抵消
                var lastF = Vec3.Zero;
                int ramp = Math.Max(1, Math.Min(300, p.Steps / 20));     // 体积力在开头几百步里慢慢加上去，减小启动冲击
                var lastT = sw.Elapsed.TotalSeconds; int lastStep = 0;
                var t0 = lastT;
                double maxLat = 0;
                int nSamples = 0;
                for (int step = 0; step < p.Steps; step++)
                {
                    ct.ThrowIfCancellationRequested();
                    bool avg = step >= p.AvgStart;
                    // 流场平均每 SampleEvery 步取一次样（相邻几步几乎一样，省掉大部分累加的显存读写）；受力每步都累加
                    bool sample = avg && (step - p.AvgStart) % SampleEvery == 0;
                    if (sample) nSamples++;
                    bool hist = (step + 1) % histEvery == 0 || (step == p.Steps - 1 && ivlSteps + 1 >= histEvery / 2 && ((ivlSteps + 1) & 1) == 0);
                    float fscale = step < ramp ? (float)(step + 1) / ramp : 1f;
                    lbm.Step(sample, avg, fscale);
                    ivlSteps++;
                    if (hist)
                    {
                        // 记录的是这一段（histEvery 步）里的平均受力，时间取段中点
                        lbm.TakeInterval(ivl);
                        lastF = Sum(ivl, parts) * (forceN / ivlSteps);
                        r.HistoryTime.Add((step + 1 - 0.5 * ivlSteps) * dt);
                        r.HistoryForce.Add(lastF);
                        ivlSteps = 0;
                    }
                    double now = sw.Elapsed.TotalSeconds;
                    if (now - lastT >= 0.7 || step == p.Steps - 1 || step == 0)
                    {
                        lbm.Finish();
                        now = sw.Elapsed.TotalSeconds;
                        float m = lbm.MaxSpeed();
                        if (float.IsNaN(m) || float.IsInfinity(m) || m > 0.5f)
                            throw new InvalidOperationException($"计算发散（第 {step + 1} 步，最大格子速度 {m:0.###}）：网格太粗或者推力太大。可以加密网格、减小推力或把 Smagorinsky 常数调大");
                        if (avg) maxLat = Math.Max(maxLat, m);
                        if (progress != null)
                        {
                            var sl = lbm.Slice();
                            if (sl != null) for (int i = 0; i < sl.Length; i++) if (sl[i] >= 0) sl[i] = (float)(sl[i] * p.Vel);
                            progress(new CfdProgress
                            {
                                Phase = avg ? "时间平均" : "计算",
                                Step = step + 1, Steps = p.Steps, Time = (step + 1) * dt, MaxSpeed = m * p.Vel,
                                BodyForce = lastF,
                                MLups = now > lastT ? N * (double)(step + 1 - lastStep) / (now - lastT) / 1e6 : 0,
                                Slice = sl, SliceW = data.SliceW, SliceH = data.SliceH, Seconds = now,
                            });
                        }
                        lastT = now; lastStep = step + 1;
                    }
                }
                lbm.Finish();
                double loopSec = sw.Elapsed.TotalSeconds - t0;
                double mlups = N * (double)p.Steps / Math.Max(1e-9, loopSec) / 1e6;
                r.MLups = mlups;
                log($"计算完成：{loopSec:0.#} 秒，{mlups:0} 百万格/秒（MLUPS）");
                if (maxLat > 0.25) r.Notes.Add($"局部瞬时速度达到 {maxLat * p.Vel:0} m/s，比预计的大很多（格子速度 {maxLat:0.##}），可压缩误差偏大，结果仅供参考");

                // ---- 时间平均受力 ----
                int cnt = Math.Max(1, p.Steps - p.AvgStart);
                lbm.ReadForces(acc);
                var total = Vec3.Zero;
                var pf = new List<CfdPartForce>();
                for (int q = 0; q < parts; q++)
                {
                    var f = new Vec3(acc[3 * q], acc[3 * q + 1], acc[3 * q + 2]) * (forceN / cnt);
                    total += f;
                    pf.Add(new CfdPartForce { Part = q < (p.PartNames?.Length ?? 0) && !string.IsNullOrEmpty(p.PartNames[q]) ? p.PartNames[q] : $"零件 {q + 1}", Force = f });
                }
                r.BodyForce = total;
                double fmax = pf.Count > 0 ? pf.Max(x => x.Force.Length) : 0;
                r.PartForces = pf.Where(x => x.Force.Length > Math.Max(1e-12, 0.005 * fmax)).OrderByDescending(x => x.Force.Length).ToList();

                // ---- 时间平均流场（分块读回，省内存）----
                progress?.Invoke(new CfdProgress { Phase = "读取结果", Step = p.Steps, Steps = p.Steps, Time = p.Steps * dt, BodyForce = total, Seconds = sw.Elapsed.TotalSeconds });
                r.U = new float[3 * N];
                r.P = new float[N];
                int chunk = (int)Math.Min(N, 1 << 22);
                var buf = new float[chunk];
                int ns = Math.Max(1, nSamples);
                float su = (float)(p.Vel / ns), sp = (float)(rho * p.Vel * p.Vel / 3.0 / ns);
                for (int c = 0; c < 4; c++)
                    for (long o = 0; o < N; o += chunk)
                    {
                        ct.ThrowIfCancellationRequested();
                        int len = (int)Math.Min(chunk, N - o);
                        lbm.ReadAverage(c, o, buf, len);
                        if (c < 3) { var U = r.U; for (int i = 0; i < len; i++) U[3 * (o + i) + c] = buf[i] * su; }
                        else { var P = r.P; for (int i = 0; i < len; i++) P[o + i] = buf[i] * sp; }
                    }
            }
            r.Solid = p.Solid;

            // ---- 桨盘入流 ----
            foreach (var dc in p.Disks)
            {
                var a = dc.Disk.Axis.Normalized();
                double sw_ = 0, su_ = 0;
                for (int i = 0; i < dc.Cells.Count; i++)
                {
                    long n = dc.Cells[i];
                    var u = new Vec3(r.U[3 * n], r.U[3 * n + 1], r.U[3 * n + 2]);
                    su_ += dc.Geo[i] * -u.Dot(a); sw_ += dc.Geo[i];
                }
                r.DiskStats.Add(new CfdDiskStat { Disk = dc.Disk, InflowSpeed = sw_ > 0 ? su_ / sw_ : 0, IdealInflow = CfdDomain.IdealInflow(dc.Disk, rho) });
            }

            // ---- 受力是否脉动 ----
            double tAvg = p.AvgStart * dt;
            var win = r.HistoryForce.Where((f, i) => r.HistoryTime[i] > tAvg).ToList();
            if (win.Count >= 5)
            {
                var mean = win.Aggregate(Vec3.Zero, (x, y) => x + y) / win.Count;
                double rms = Math.Sqrt(win.Sum(f => (f - mean).Dot(f - mean)) / win.Count);
                double thrust = s.Disks.Sum(d => Math.Max(0, d.Thrust));
                r.Oscillating = rms > 0.1 * mean.Length && rms > 1e-4 * Math.Max(1, thrust);
            }
            r.Seconds = sw.Elapsed.TotalSeconds;
            return r;
        }

        static Vec3 Sum(float[] f, int parts)
        {
            double x = 0, y = 0, z = 0;
            for (int q = 0; q < parts; q++) { x += f[3 * q]; y += f[3 * q + 1]; z += f[3 * q + 2]; }
            return new Vec3(x, y, z);
        }

        // ================= 准备：网格、体素化、作动盘、单位 =================

        static Prep Prepare(CfdSetup s, ClDevice dev, Action<string> log, List<string> notes, CancellationToken ct, int fixedSteps)
        {
            var p = new Prep();
            var dom = CfdDomain.Build(s, MaxCells(dev));
            p.Dom = dom;
            long N = dom.N;
            if (dom.CappedFrom > 0)
                notes.Add($"目标格子数 {dom.CappedFrom / 1e6:0.#} M 超过{(dev == null ? " CPU 计算的上限" : "显存能放下的")} {N / 1e6:0.#} M，已自动放粗");
            log($"计算域 {dom.Nx}×{dom.Ny}×{dom.Nz} = {N / 1e6:0.##} M 格，格子 {dom.Dx * 1000:0.##} mm；" +
                string.Join("，", new[] { "X", "Y", "Z" }.SelectMany((ax, a) => new[] { $"−{ax} {CfdDomain.FaceName(dom.FaceType[2 * a])}", $"+{ax} {CfdDomain.FaceName(dom.FaceType[2 * a + 1])}" })));
            CheckHostMemory(N, dev == null ? 185.0 : 26.0);   // 显卡计算时主机上只有体素化、格子类型和结果（每格约 26 字节）
            if (N > int.MaxValue) throw new InvalidOperationException("网格太大，请加大格子尺寸");

            // ---- 体素化 ----
            log("体素化几何……");
            var solid = new byte[N];
            var part = new ushort[N];
            CfdVoxelizer.Run(s, dom, solid, part, out int thin, ct);
            long nSolid = 0;
            for (long n = 0; n < N; n++) if (solid[n] != 0) nSolid++;
            log($"固体格子 {nSolid:N0} 个" + (thin > 0 ? $"（其中 {thin:N0} 个是为了挡住薄壁 / 薄板补上的）" : ""));

            // ---- 格子类型 ----
            var flags = new byte[N];
            int nx = dom.Nx, ny = dom.Ny, nz = dom.Nz;
            Parallel.For(0, nz, k =>
            {
                for (int j = 0; j < ny; j++)
                    for (int i = 0; i < nx; i++)
                    {
                        long n = i + (long)nx * (j + (long)ny * k);
                        if (solid[n] != 0) { flags[n] = CellType.Solid; continue; }
                        if (i == 0 || j == 0 || k == 0 || i == nx - 1 || j == ny - 1 || k == nz - 1)
                        {
                            var bt = dom.BoundaryType(i, j, k);
                            flags[n] = bt;
                            if (bt == CellType.Wall) solid[n] = 2;
                        }
                        else flags[n] = CellType.Fluid;
                    }
            });
            var off = new int[19];
            for (int i = 0; i < 19; i++) off[i] = D3Q19.Cx[i] + D3Q19.Cy[i] * nx + D3Q19.Cz[i] * nx * ny;
            // 旁边有固体 / 地面的流体格子：只有它们需要逐个方向检查反弹（其余格子直接拉取，省掉 18 次邻居类型读取）
            Parallel.For(1, nz - 1, k =>
            {
                for (int j = 1; j < ny - 1; j++)
                    for (int i = 1; i < nx - 1; i++)
                    {
                        int n = i + nx * (j + ny * k);
                        if (flags[n] != CellType.Fluid) continue;
                        for (int q = 1; q < 19; q++)
                            if (CellType.IsWall(flags[n + off[q]])) { flags[n] |= CellType.NearWall; break; }
                    }
            });

            // ---- 单位 ----
            double dx = dom.Dx, wind = s.Wind.Length;
            double uref = dom.VFarMax + 1.3 * wind;
            if (uref <= 0) uref = 1;
            double dt = ULat * dx / uref;
            p.Dt = dt; p.Vel = dx / dt;
            double nuLat = s.Viscosity * dt / (dx * dx);
            double tau0 = 3 * nuLat + 0.5;
            double cs = Math.Max(0, s.Smagorinsky);

            // ---- 作动盘 ----
            var force = new Dictionary<long, Vec3>();
            foreach (var k in s.Disks)
            {
                if (k.Thrust <= 0 && Math.Abs(k.Torque) <= 0 || k.Radius <= 0) continue;
                if (k.Axis.Length < 1e-9) { notes.Add($"{k.Name}：没有设推力方向，已跳过"); continue; }
                var dc = DiskCells.Rasterize(k, dom, solid);
                p.Disks.Add(dc);
                if (dc.Empty) { notes.Add($"{k.Name}：桨盘整个在固体里或者比格子还小，没有加力"); continue; }
                if (dc.LostFraction > 0.2) notes.Add($"{k.Name}：{dc.LostFraction:P0} 的桨盘面积落在固体（电机、桨毂）里，推力已经集中到其余部分");
                if (2 * k.Radius / dx < 8) notes.Add($"{k.Name}：桨盘直径只有 {2 * k.Radius / dx:0.#} 个格子，桨附近的流动比较粗糙");
                for (int i = 0; i < dc.Cells.Count; i++)
                    force[dc.Cells[i]] = (force.TryGetValue(dc.Cells[i], out var f0) ? f0 : Vec3.Zero) + dc.Force[i];
            }
            double fLat = dt * dt / (s.Density * Math.Pow(dx, 4));    // 每格的力 N → 格子单位
            var fc = force.Keys.OrderBy(x => x).ToArray();
            var fv = new float[3 * fc.Length];
            for (int i = 0; i < fc.Length; i++)
            {
                var f = force[fc[i]] * fLat;
                fv[3 * i] = (float)f.X; fv[3 * i + 1] = (float)f.Y; fv[3 * i + 2] = (float)f.Z;
                flags[fc[i]] |= CellType.Forced;
            }
            if (fc.Length > 0)
            {
                double fm = 0; for (int i = 0; i < fc.Length; i++) fm = Math.Max(fm, Math.Sqrt(fv[3 * i] * fv[3 * i] + fv[3 * i + 1] * fv[3 * i + 1] + fv[3 * i + 2] * fv[3 * i + 2]));
                log($"作动盘：{p.Disks.Count} 个，{fc.Length:N0} 个格子，最大体积力（格子单位）{fm:0.####}");
            }

            // ---- 贴着零件的流体格子（算受力用），按零件分组 ----
            int nParts = 1;
            if (s.TriPart != null && s.TriPart.Length > 0) nParts = Math.Min(65535, s.TriPart.Max() + 1);
            p.PartNames = s.PartNames ?? (s.TriPart == null ? new[] { "模型" } : null);
            var bl = new List<(int n, int part)>();
            var lk = new object();
            Parallel.For(1, nz - 1, k =>
            {
                var loc = new List<(int, int)>();
                for (int j = 1; j < ny - 1; j++)
                    for (int i = 1; i < nx - 1; i++)
                    {
                        int n = i + nx * (j + ny * k);
                        if ((flags[n] & 7) != CellType.Fluid) continue;
                        for (int q = 1; q < 19; q++)
                        {
                            int m = n + off[q];
                            if ((flags[m] & 7) == CellType.Solid) { loc.Add((n, Math.Max(0, Math.Min(nParts - 1, part[m] - 1)))); break; }
                        }
                    }
                lock (lk) bl.AddRange(loc);
            });
            part = null;
            var boff = new int[nParts + 1];
            foreach (var e in bl) boff[e.part + 1]++;
            for (int q = 0; q < nParts; q++) boff[q + 1] += boff[q];
            var bcell = new int[bl.Count];
            var cur = (int[])boff.Clone();
            foreach (var e in bl.OrderBy(x => x.n)) bcell[cur[e.part]++] = e.n;
            bl = null;

            // ---- 时间 ----
            double vTime = (dom.VFarMean > 0 ? dom.VFarMean : 0) + wind;
            if (vTime <= 0) vTime = uref;
            double lWake = Math.Abs(dom.WakeDir.X) * dom.Extent(0) + Math.Abs(dom.WakeDir.Y) * dom.Extent(1) + Math.Abs(dom.WakeDir.Z) * dom.Extent(2);
            // 有地面：尾流先到地面再沿地面向四周流出去，路程再加上水平方向的一半
            if (s.Ground) lWake += 0.5 * Math.Max(dom.Extent(dom.H1), dom.Extent(dom.H2));
            double simTime = s.SimTime > 0 ? s.SimTime : 4 * lWake / vTime;
            long steps = (long)Math.Ceiling(simTime / dt);
            if (fixedSteps > 0) steps = fixedSteps;
            else if (s.SimTime <= 0)
            {
                if (steps < 2000) steps = 2000;
                if (steps > 100_000) { steps = 100_000; notes.Add($"自动计算时间被限制在 {steps * dt * 1000:0} ms（10 万步），尾流可能还没完全发展"); }
            }
            else if (steps < 50) steps = 50;
            else if (steps > 2_000_000) throw new InvalidOperationException("计算时间太长（超过 200 万步），请减小物理时间或加大格子");
            p.Steps = (int)steps;
            p.AvgStart = Math.Max(0, Math.Min(p.Steps - 1, (int)(Math.Max(0, Math.Min(0.95, s.AverageFrom)) * p.Steps)));
            log($"单位：格子 {dx * 1000:0.###} mm，时间步 {dt * 1e6:0.###} µs，格子速度 1 = {p.Vel:0.#} m/s，τ0 = {tau0:0.######}（分子黏度），Smagorinsky Cs = {cs:0.###}");

            // ---- 预览切面：竖直平面，沿跨度较大的水平轴，过桨盘平均中心 ----
            int axW = dom.Extent(dom.H1) >= dom.Extent(dom.H2) ? dom.H1 : dom.H2;
            int axN = axW == dom.H1 ? dom.H2 : dom.H1;
            Vec3 cen = s.Disks.Count > 0 ? s.Disks.Aggregate(Vec3.Zero, (a, d) => a + d.Center) / s.Disks.Count : (dom.GeoMin + dom.GeoMax) / 2;
            double cn = axN == 0 ? cen.X - dom.Origin.X : axN == 1 ? cen.Y - dom.Origin.Y : cen.Z - dom.Origin.Z;
            int stride = Math.Max(1, (int)Math.Ceiling(Math.Max(dom.Dim(axW), dom.Dim(dom.UpAxis) / 1.5) / 300.0));

            p.Data = new LbmData
            {
                Nx = nx, Ny = ny, Nz = nz, Flags = flags,
                ForceCells = fc.Select(x => (int)x).ToArray(), ForceVec = fv,
                BCells = bcell, BOffsets = boff,
                Tau0 = (float)tau0, Smag = (float)(18 * Math.Sqrt(2) * cs * cs),
                Ux = (float)(s.Wind.X / p.Vel), Uy = (float)(s.Wind.Y / p.Vel), Uz = (float)(s.Wind.Z / p.Vel),
                SliceAxW = axW, SliceAxH = dom.UpAxis, SliceAxN = axN,
                SliceN = Math.Max(1, Math.Min(dom.Dim(axN) - 2, (int)Math.Floor(cn / dx))),
                SliceStride = stride,
                SliceW = (dom.Dim(axW) + stride - 1) / stride, SliceH = (dom.Dim(dom.UpAxis) + stride - 1) / stride,
                SliceFlipH = dom.UpSign < 0,
            };
            p.Solid = solid;
            return p;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MemoryStatusEx
        {
            public uint Length, MemoryLoad;
            public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
        }
        [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx m);

        /// <summary>计算前检查内存（CPU 每格约 185 字节，显卡时主机上每格约 26 字节）：提交量不够就直接报错，别让系统卡死。</summary>
        static void CheckHostMemory(long cells, double bytesPerCell)
        {
            var m = new MemoryStatusEx { Length = (uint)Marshal.SizeOf(typeof(MemoryStatusEx)) };
            if (!GlobalMemoryStatusEx(ref m)) return;
            double need = cells * bytesPerCell;
            if (need > m.AvailPageFile * 0.9)
                throw new InvalidOperationException($"内存不够：{cells / 1e6:0.#} M 格要约 {need / 1e9:0.##} GB，系统只剩 {m.AvailPageFile / 1e9:0.##} GB 可用。请用粗一点的网格或者关掉别的程序");
        }
    }
}
