using System;
using System.Collections.Generic;
using System.Linq;
using SwKit.Core.Urdf;

namespace SwKit.Core.Cfd
{
    // =====================================================================================
    //  CFD（外流场 + 转子 / 涵道作动盘）的输入和输出。求解器（CfdSolver）和界面（CfdPage）共用。
    //  坐标：模型坐标系，单位米（SolidWorks 默认 Y 向上）。
    // =====================================================================================

    /// <summary>
    /// 作动盘：转子或涵道风扇，用盘面上的体积力代替真实桨叶（OpenFOAM actuationDiskSource / rotorDisk 同类方法）。
    /// Axis 是推力作用在飞行器上的方向（悬停的四旋翼一般是 +Y）；空气被推向相反方向。
    /// </summary>
    public sealed class CfdDisk
    {
        public string Name = "桨";
        public Vec3 Center;              // 盘心（m）
        public Vec3 Axis = new Vec3(0, 1, 0);   // 推力方向（单位向量）
        public double Radius = 0.0635;   // 桨尖半径（m），5 寸桨约 0.0635
        public double HubRadius = 0.01;  // 桨毂半径（m），这以内不加力
        public double Thrust = 5;        // 推力（N），> 0
        public double Torque;            // 反扭矩（N·m）：> 0 表示桨绕 Axis 按右手方向转，带动空气同向旋转；0 = 不加旋流
        public bool Ducted;              // 涵道风扇（只影响显示和报告）
        public CfdLoading Loading = CfdLoading.Uniform;
        public int SourcePart = -1;      // 由模型里的哪个桨叶实体生成（这个实体计算时不当固体，用桨盘代替）；−1 = 手动放的
        public int[] MoreParts;          // 桨叶被拆成几个实体时（例如桨毂和两片叶片），其余的实体
        public IEnumerable<int> Parts { get { if (SourcePart >= 0) yield return SourcePart; if (MoreParts != null) foreach (var p in MoreParts) yield return p; } }
        public CfdDisk Clone() { var c = (CfdDisk)MemberwiseClone(); c.MoreParts = MoreParts?.ToArray(); return c; }
        public override string ToString() => $"{Name}：推力 {Thrust:0.##} N，Φ{Radius * 2000:0} mm";
    }

    /// <summary>推力沿半径的分布：均匀（经典作动盘）或接近真实桨（中间大、根部和桨尖小，∝ r*·√(1−r*)）。</summary>
    public enum CfdLoading { Uniform, Prop }

    /// <summary>计算域边界：远场（固定为来流速度和大气压）、出口（零梯度，尾流可以自由流出）、地面（无滑移墙）。</summary>
    public enum CfdFace { Far, Outflow, Wall }

    public sealed class CfdSetup
    {
        // ---- 几何 ----
        public float[] Triangles = new float[0];   // 9 个数一个三角形（m）
        public int[] TriPart;                      // 每个三角形属于哪个零件（受力分解用），可以为 null
        public string[] PartNames;                 // 零件名（按编号）
        public List<CfdDisk> Disks = new List<CfdDisk>();

        // ---- 工况 ----
        public Vec3 Wind;                          // 来流（m/s），0 = 悬停
        public Vec3 Up = new Vec3(0, 1, 0);        // 模型的「上」方向（重力反方向），决定地面在哪一侧、域怎么延伸
        public bool Ground;                        // 有地面（地面效应）
        public double GroundDistance = 0.3;        // 地面到模型最低点的距离（m）
        public double Density = 1.204;             // 空气密度（kg/m³，20 °C）
        public double Viscosity = 1.5e-5;          // 运动黏度（m²/s）

        // ---- 网格和时间 ----
        public double CellSize;                    // 网格尺寸（m）；0 = 按 TargetCells 自动
        public long TargetCells = 4_000_000;       // 自动网格时的目标格子数
        public double PadSide, PadUp, PadDown;     // 域在几何（含桨盘）包围盒外的延伸（m）；0 = 自动（侧面、上方各约 1 倍跨度，尾流方向约 2.5 倍）
        public double SimTime;                     // 物理时间（s）；0 = 自动（尾流穿过计算域若干次）
        public double AverageFrom = 0.5;           // 从总步数的这个比例开始做时间平均（LES 流场本身是脉动的）
        public double Smagorinsky = 0.12;          // LES 亚格子模型常数
        public string Device;                      // null = 自动（最快的显卡）；"cpu" = CPU；或者 Devices() 返回的名字
    }

    /// <summary>计算过程中的进度（每隔一段时间回调一次）。</summary>
    public sealed class CfdProgress
    {
        public string Phase;
        public int Step, Steps;
        public double Time;                        // 已经算到的物理时间（s）
        public double MaxSpeed;                    // 当前最大速度（m/s）
        public Vec3 BodyForce;                     // 当前作用在几何上的合力（N）
        public double MLups;                       // 每秒更新的百万格子数
        public float[] Slice; public int SliceW, SliceH;   // 过盘心的竖直切面上的速度大小（m/s），预览用；可以为 null
        public double Seconds;                     // 已用时间
    }

    public sealed class CfdPartForce
    {
        public string Part;
        public Vec3 Force;                         // 时间平均的气动力（作用在零件上，N）
    }

    public sealed class CfdDiskStat
    {
        public CfdDisk Disk;
        public double InflowSpeed;                 // 穿过盘面的平均轴向速度（m/s，空气流动方向为正）
        public double IdealInflow;                 // 动量理论 v_i = √(T / (2ρA))，对比用
    }

    public sealed class CfdResult
    {
        // 规则网格：格子 (i, j, k) 的中心在 Origin + (i + 0.5, j + 0.5, k + 0.5)·Dx
        public int Nx, Ny, Nz;
        public Vec3 Origin;
        public double Dx;
        public float[] U;                          // 时间平均速度，每格 3 个数（m/s），下标 3·(i + Nx·(j + Ny·k))
        public byte[] Solid;                       // 1 = 固体（零件内部）；2 = 地面那一层（有地面时）
        public float[] P;                          // 时间平均表压 p − p0（Pa），每格 1 个数，下标 i + Nx·(j + Ny·k)；可以为 null
        public double MLups;                       // 计算速度：每秒更新的百万格子数
        public List<CfdPartForce> PartForces = new List<CfdPartForce>();
        public Vec3 BodyForce;                     // 作用在全部几何上的时间平均合力（N）
        public List<CfdDiskStat> DiskStats = new List<CfdDiskStat>();
        public CfdSetup Setup;
        public string Device;
        public int Steps;
        public double SimTime, Dt, Seconds;
        public bool Oscillating;                   // 合力在平均段里波动较大（尾流不稳定，结果看平均值）
        public List<double> HistoryTime = new List<double>();
        public List<Vec3> HistoryForce = new List<Vec3>();
        public List<string> Notes = new List<string>();

        public long Cells => (long)Nx * Ny * Nz;

        /// <summary>任意一点的时间平均速度（三线性插值；域外或固体里返回零）。</summary>
        public Vec3 Sample(Vec3 p)
        {
            double fx = (p.X - Origin.X) / Dx - 0.5, fy = (p.Y - Origin.Y) / Dx - 0.5, fz = (p.Z - Origin.Z) / Dx - 0.5;
            int i = (int)Math.Floor(fx), j = (int)Math.Floor(fy), k = (int)Math.Floor(fz);
            if (i < 0 || j < 0 || k < 0 || i >= Nx - 1 || j >= Ny - 1 || k >= Nz - 1) return Vec3.Zero;
            double tx = fx - i, ty = fy - j, tz = fz - k;
            double ux = 0, uy = 0, uz = 0;
            for (int c = 0; c < 8; c++)
            {
                int di = c & 1, dj = (c >> 1) & 1, dk = (c >> 2) & 1;
                double w = (di == 1 ? tx : 1 - tx) * (dj == 1 ? ty : 1 - ty) * (dk == 1 ? tz : 1 - tz);
                long n = 3L * ((i + di) + (long)Nx * ((j + dj) + (long)Ny * (k + dk)));
                ux += w * U[n]; uy += w * U[n + 1]; uz += w * U[n + 2];
            }
            return new Vec3(ux, uy, uz);
        }

        public bool IsSolid(Vec3 p)
        {
            int i = (int)Math.Floor((p.X - Origin.X) / Dx), j = (int)Math.Floor((p.Y - Origin.Y) / Dx), k = (int)Math.Floor((p.Z - Origin.Z) / Dx);
            if (i < 0 || j < 0 || k < 0 || i >= Nx || j >= Ny || k >= Nz) return false;
            return Solid != null && Solid[i + (long)Nx * (j + (long)Ny * k)] != 0;
        }

        public bool Inside(Vec3 p) =>
            p.X > Origin.X && p.Y > Origin.Y && p.Z > Origin.Z && p.X < Origin.X + Nx * Dx && p.Y < Origin.Y + Ny * Dx && p.Z < Origin.Z + Nz * Dx;
    }
}
