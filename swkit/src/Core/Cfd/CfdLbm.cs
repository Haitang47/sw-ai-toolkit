using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace SwKit.Core.Cfd
{
    // =====================================================================================
    //  格子玻尔兹曼（D3Q19）求解核心。两套实现算法完全相同：OpenCL（显卡）和托管代码（CPU，Parallel.For）。
    //
    //  · 分布函数存「偏差」g_i = f_i − w_i（参考密度 1），float32 下小扰动的精度高很多；
    //  · 拉格式流动（pull）+ A/B 两份数组交替；固体格子用半格反弹（halfway bounce-back）；
    //  · 碰撞：正则化 BGK（非平衡部分投影到二阶 Hermite 空间，压掉高阶「鬼模态」，τ 接近 0.5 时也稳定）
    //    + Smagorinsky 亚格子模型（由非平衡应力直接算总松弛时间 τ_eff）；
    //  · 体积力：Guo 格式（速度加半个力的修正、碰撞里加 (1 − ω/2)·S_i 源项）。正则化时非平衡部分要保留一阶项
    //    3w_i c_i·(−F/2)（Guo 格式里非平衡分布的一阶矩正好是 −F/2），否则碰撞后动量只增加 F/2，推力只剩一半；
    //  · 边界：远场 = 平衡态。法向速度取里面一层（零梯度），空气可以自由流进流出；切向速度每步往来流拉 10 %；
    //    流进来时总压等于来流总压（静压 p0 + ½ρ(V∞² − |u|²)，OpenFOAM 的 totalPressure），流出去时静压 = p0。
    //    这样从远场吸进来的空气带着来流的总压和方向，不会在域边上积成一团「死水」。
    //    （直接把切向速度设成来流会在边上形成分辨不了的剪切层而发散；法向也拉会阻碍悬停时的卷吸、整个域压力偏低。）
    //    入口 = 平衡态（密度取里面一层，速度 = 来流）；
    //    出口 = 里面一层的分布函数外推（零梯度，回流的法向分量截掉）；地面 = 反弹墙。
    // =====================================================================================

    /// <summary>传给计算核心的网格数据（格子单位）。</summary>
    internal sealed class LbmData
    {
        public int Nx, Ny, Nz;
        public long N => (long)Nx * Ny * Nz;
        public byte[] Flags;                 // CellType + Forced 位
        public int[] ForceCells;             // 有体积力的格子
        public float[] ForceVec;             // 每个 3 个数（格子单位的力密度）
        public int[] BCells, BOffsets;       // 贴着零件的流体格子，按零件排好；BOffsets 长度 = 零件数 + 1
        public int Parts => BOffsets.Length - 1;
        public float Tau0, Smag;             // Smag = 18·√2·Cs²
        public float Ux, Uy, Uz;             // 来流（格子单位）
        // 预览切面
        public int SliceAxW, SliceAxH, SliceAxN, SliceN, SliceStride, SliceW, SliceH;
        public bool SliceFlipH;
        /// <summary>远场边界每步把速度往来流拉的比例（一层「海绵」：防止从远场吸进来的空气慢慢失去来流速度）。</summary>
        public const float FarRelax = 0.1f;
    }

    internal interface ILbm : IDisposable
    {
        string Name { get; }
        void Init();
        /// <summary>
        /// 走一步。accumU：累加流场时间平均；零件受力每步都算，累加到「区间和」里，accumF 时同时累加到「平均段总和」。
        /// （动量交换力在 τ → 0.5 时有明显的单双步交替振荡，只能按步累加后平均，不能隔几步取一个瞬时值。）
        /// </summary>
        void Step(bool accumU, bool accumF, float fscale);
        void Finish();
        /// <summary>读零件受力的区间和（格子单位，3·零件数）并清零。</summary>
        void TakeInterval(float[] sum);
        /// <summary>读平均段的受力总和（格子单位，3·零件数）。</summary>
        void ReadForces(float[] acc);
        float MaxSpeed();
        float[] Slice();
        /// <summary>读时间平均累加量：comp 0–2 = 速度分量，3 = 密度偏差。</summary>
        void ReadAverage(int comp, long offset, float[] dst, int count);
    }

    internal static class D3Q19
    {
        public static readonly int[] Cx = { 0, 1, -1, 0, 0, 0, 0, 1, -1, 1, -1, 0, 0, 1, -1, 1, -1, 0, 0 };
        public static readonly int[] Cy = { 0, 0, 0, 1, -1, 0, 0, 1, -1, 0, 0, 1, -1, -1, 1, 0, 0, 1, -1 };
        public static readonly int[] Cz = { 0, 0, 0, 0, 0, 1, -1, 0, 0, 1, -1, 1, -1, 0, 0, -1, 1, -1, 1 };
        public static readonly float[] W = { 1f / 3, 1f / 18, 1f / 18, 1f / 18, 1f / 18, 1f / 18, 1f / 18,
            1f / 36, 1f / 36, 1f / 36, 1f / 36, 1f / 36, 1f / 36, 1f / 36, 1f / 36, 1f / 36, 1f / 36, 1f / 36, 1f / 36 };
        public static int Opp(int i) => i == 0 ? 0 : (i & 1) == 1 ? i + 1 : i - 1;
    }

    // =====================================================================================
    //  OpenCL 实现
    // =====================================================================================
    internal sealed class LbmGpu : ILbm
    {
        // 内核源码（只能用 ASCII）。网格尺寸、松弛时间、来流在编译时用 -D 传进来。
        const string Source = @"
#define NXY (NX*NY)
__constant float W[19] = {1.0f/3.0f, 1.0f/18.0f,1.0f/18.0f,1.0f/18.0f,1.0f/18.0f,1.0f/18.0f,1.0f/18.0f,
  1.0f/36.0f,1.0f/36.0f,1.0f/36.0f,1.0f/36.0f,1.0f/36.0f,1.0f/36.0f,1.0f/36.0f,1.0f/36.0f,1.0f/36.0f,1.0f/36.0f,1.0f/36.0f,1.0f/36.0f};
__constant int CX[19] = {0, 1,-1, 0, 0, 0, 0, 1,-1, 1,-1, 0, 0, 1,-1, 1,-1, 0, 0};
__constant int CY[19] = {0, 0, 0, 1,-1, 0, 0, 1,-1, 0, 0, 1,-1,-1, 1, 0, 0, 1,-1};
__constant int CZ[19] = {0, 0, 0, 0, 0, 1,-1, 0, 0, 1,-1, 1,-1, 0, 0,-1, 1,-1, 1};
#define OPP(i) ((i)==0 ? 0 : (((i)&1) ? (i)+1 : (i)-1))
#define T_FLUID 0
#define T_SOLID 1
#define T_WALL 2
#define T_FAR 3
#define T_INLET 4
#define T_OUT 5
#define F_FORCE 8
#define F_NEAR 16
inline int is_wall(uchar f) { const uchar t = f & 7; return t == T_SOLID || t == T_WALL; }

inline float geq(int i, float drho, float rho, float ux, float uy, float uz, float u2) {
  const float cu = 3.0f*(CX[i]*ux + CY[i]*uy + CZ[i]*uz);
  return W[i]*(drho + rho*(cu + 0.5f*cu*cu - u2));
}

__kernel void init_f(__global float* a, __global float* b, __global const uchar* flags) {
  const int n = get_global_id(0); if (n >= NN) return;
  const int wall = is_wall(flags[n]);
  const float ux = wall ? 0.0f : UX0, uy = wall ? 0.0f : UY0, uz = wall ? 0.0f : UZ0;
  const float u2 = 1.5f*(ux*ux + uy*uy + uz*uz);
  for (int i = 0; i < 19; i++) { const float g = geq(i, 0.0f, 1.0f, ux, uy, uz, u2); a[i*NN + n] = g; b[i*NN + n] = g; }
}

__kernel void zero_f(__global float* a, int count) { const int n = get_global_id(0); if (n < count) a[n] = 0.0f; }

__kernel void set_fidx(__global int* fidx, __global const int* cells, int count) {
  const int k = get_global_id(0); if (k < count) fidx[cells[k]] = k;
}

__kernel void stream_collide(__global const float* src, __global float* dst, __global const uchar* flags,
                             __global const int* fidx, __global const float* ftab, __global float* avg, int accum, float fscale) {
  const int n = get_global_id(0); if (n >= NN) return;
  const uchar fl = flags[n];
  const uchar t = fl & 7;
  if (t == T_SOLID || t == T_WALL) return;
  float drho, ux, uy, uz;
  if (t == T_FLUID) {
    float g[19];
    if (fl & F_NEAR) {
      #pragma unroll
      for (int i = 0; i < 19; i++) {
        const int m = n - (CX[i] + CY[i]*NX + CZ[i]*NXY);
        g[i] = is_wall(flags[m]) ? src[OPP(i)*NN + n] : src[i*NN + m];
      }
    } else {
      #pragma unroll
      for (int i = 0; i < 19; i++) g[i] = src[i*NN + n - (CX[i] + CY[i]*NX + CZ[i]*NXY)];
    }
    float jx = 0.0f, jy = 0.0f, jz = 0.0f, pxx = 0.0f, pyy = 0.0f, pzz = 0.0f, pxy = 0.0f, pxz = 0.0f, pyz = 0.0f;
    drho = 0.0f;
    #pragma unroll
    for (int i = 0; i < 19; i++) {
      const float v = g[i];
      drho += v; jx += CX[i]*v; jy += CY[i]*v; jz += CZ[i]*v;
      pxx += CX[i]*CX[i]*v; pyy += CY[i]*CY[i]*v; pzz += CZ[i]*CZ[i]*v;
      pxy += CX[i]*CY[i]*v; pxz += CX[i]*CZ[i]*v; pyz += CY[i]*CZ[i]*v;
    }
    float fx = 0.0f, fy = 0.0f, fz = 0.0f;
    if (fl & F_FORCE) { const int k = fidx[n]; fx = fscale*ftab[3*k]; fy = fscale*ftab[3*k+1]; fz = fscale*ftab[3*k+2]; }
    const float rho = 1.0f + drho, ir = 1.0f/rho;
    ux = (jx + 0.5f*fx)*ir; uy = (jy + 0.5f*fy)*ir; uz = (jz + 0.5f*fz)*ir;
    const float Pxx = pxx - drho*(1.0f/3.0f) - rho*ux*ux, Pyy = pyy - drho*(1.0f/3.0f) - rho*uy*uy, Pzz = pzz - drho*(1.0f/3.0f) - rho*uz*uz;
    const float Pxy = pxy - rho*ux*uy, Pxz = pxz - rho*ux*uz, Pyz = pyz - rho*uy*uz;
    const float Qxx = Pxx + fx*ux, Qyy = Pyy + fy*uy, Qzz = Pzz + fz*uz;
    const float Qxy = Pxy + 0.5f*(fx*uy + fy*ux), Qxz = Pxz + 0.5f*(fx*uz + fz*ux), Qyz = Pyz + 0.5f*(fy*uz + fz*uy);
    const float Q = sqrt(Qxx*Qxx + Qyy*Qyy + Qzz*Qzz + 2.0f*(Qxy*Qxy + Qxz*Qxz + Qyz*Qyz));
    const float tau = 0.5f*(TAU0 + sqrt(TAU0*TAU0 + SMAG*Q*ir));
    const float om = 1.0f/tau, c1 = 1.0f - om, c2 = 1.0f - 0.5f*om;
    const float u2 = 1.5f*(ux*ux + uy*uy + uz*uz), uF = ux*fx + uy*fy + uz*fz;
    #pragma unroll
    for (int i = 0; i < 19; i++) {
      const float cx = CX[i], cy = CY[i], cz = CZ[i];
      const float cu = 3.0f*(cx*ux + cy*uy + cz*uz);
      const float e = W[i]*(drho + rho*(cu + 0.5f*cu*cu - u2));
      const float f1 = 4.5f*W[i]*((cx*cx - (1.0f/3.0f))*Pxx + (cy*cy - (1.0f/3.0f))*Pyy + (cz*cz - (1.0f/3.0f))*Pzz
                                  + 2.0f*(cx*cy*Pxy + cx*cz*Pxz + cy*cz*Pyz));
      const float cF = cx*fx + cy*fy + cz*fz;
      const float S = W[i]*(3.0f*(cF - uF) + 3.0f*cu*cF);
      dst[i*NN + n] = e + c1*(f1 - 1.5f*W[i]*cF) + c2*S;
    }
  } else {
    const int x = n % NX, y = (n / NX) % NY, z = n / NXY;
    const int m = clamp(x, 1, NX-2) + NX*(clamp(y, 1, NY-2) + NY*clamp(z, 1, NZ-2));
    float h[19];
    float dri = 0.0f, jx = 0.0f, jy = 0.0f, jz = 0.0f;
    #pragma unroll
    for (int i = 0; i < 19; i++) { const float v = src[i*NN + m]; h[i] = v; dri += v; jx += CX[i]*v; jy += CY[i]*v; jz += CZ[i]*v; }
    float uix = jx/(1.0f + dri), uiy = jy/(1.0f + dri), uiz = jz/(1.0f + dri);
    if ((flags[m] & 7) != T_FLUID) { dri = 0.0f; uix = UX0; uiy = UY0; uiz = UZ0; }
    if (t == T_FAR) {
      ux = uix; uy = uiy; uz = uiz;
      if (x > 0 && x < NX-1) ux += FARK*(UX0 - uix);
      if (y > 0 && y < NY-1) uy += FARK*(UY0 - uiy);
      if (z > 0 && z < NZ-1) uz += FARK*(UZ0 - uiz);
      const float s = sqrt(ux*ux + uy*uy + uz*uz);
      if (s > 0.2f) { ux *= 0.2f/s; uy *= 0.2f/s; uz *= 0.2f/s; }
      const float un = (x == 0 ? ux : 0.0f) - (x == NX-1 ? ux : 0.0f) + (y == 0 ? uy : 0.0f) - (y == NY-1 ? uy : 0.0f) + (z == 0 ? uz : 0.0f) - (z == NZ-1 ? uz : 0.0f);
      const float w = clamp(0.5f + 50.0f*un, 0.0f, 1.0f);
      drho = w*1.5f*(UX0*UX0 + UY0*UY0 + UZ0*UZ0 - ux*ux - uy*uy - uz*uz);
    } else if (t == T_INLET) {
      drho = clamp(dri, -0.05f, 0.05f); ux = UX0; uy = UY0; uz = UZ0;
    } else {
      drho = dri; ux = uix; uy = uiy; uz = uiz;
      if (x == 0) ux = fmin(ux, 0.0f); if (x == NX-1) ux = fmax(ux, 0.0f);
      if (y == 0) uy = fmin(uy, 0.0f); if (y == NY-1) uy = fmax(uy, 0.0f);
      if (z == 0) uz = fmin(uz, 0.0f); if (z == NZ-1) uz = fmax(uz, 0.0f);
    }
    const float rho = 1.0f + drho, u2 = 1.5f*(ux*ux + uy*uy + uz*uz);
    const float rhoi = 1.0f + dri, u2i = 1.5f*(uix*uix + uiy*uiy + uiz*uiz);
    #pragma unroll
    for (int i = 0; i < 19; i++) {
      float v = geq(i, drho, rho, ux, uy, uz, u2);
      if (t == T_OUT) v += h[i] - geq(i, dri, rhoi, uix, uiy, uiz, u2i);
      dst[i*NN + n] = v;
    }
  }
  if (accum) { avg[n] += ux; avg[NN + n] += uy; avg[2*NN + n] += uz; avg[3*NN + n] += drho; }
}

#define LS 128
__kernel void part_force(__global const float* f, __global const uchar* flags, __global const int* bcell, __global const int* boff,
                         __global float* ivl, __global float* acc, int accum) {
  const int p = get_group_id(0), lid = get_local_id(0);
  __local float sx[LS], sy[LS], sz[LS];
  float ax = 0.0f, ay = 0.0f, az = 0.0f;
  const int b1 = boff[p + 1];
  for (int b = boff[p] + lid; b < b1; b += LS) {
    const int n = bcell[b];
    #pragma unroll
    for (int i = 1; i < 19; i++) {
      const int m = n + (CX[i] + CY[i]*NX + CZ[i]*NXY);
      if ((flags[m] & 7) == T_SOLID) { const float v = 2.0f*f[i*NN + n]; ax += CX[i]*v; ay += CY[i]*v; az += CZ[i]*v; }
    }
  }
  sx[lid] = ax; sy[lid] = ay; sz[lid] = az;
  barrier(CLK_LOCAL_MEM_FENCE);
  for (int s = LS/2; s > 0; s >>= 1) {
    if (lid < s) { sx[lid] += sx[lid + s]; sy[lid] += sy[lid + s]; sz[lid] += sz[lid + s]; }
    barrier(CLK_LOCAL_MEM_FENCE);
  }
  if (lid == 0) {
    ivl[3*p] += sx[0]; ivl[3*p+1] += sy[0]; ivl[3*p+2] += sz[0];
    if (accum) { acc[3*p] += sx[0]; acc[3*p+1] += sy[0]; acc[3*p+2] += sz[0]; }
  }
}

__kernel void speed_max(__global const float* f, __global const uchar* flags, __global float* out) {
  const int lid = get_local_id(0), gs = get_global_size(0);
  __local float sm[LS];
  float mx = 0.0f;
  for (int n = get_global_id(0); n < NN; n += gs) {
    if ((flags[n] & 7) != T_FLUID) continue;
    float d = 0.0f, jx = 0.0f, jy = 0.0f, jz = 0.0f;
    for (int i = 0; i < 19; i++) { const float v = f[i*NN + n]; d += v; jx += CX[i]*v; jy += CY[i]*v; jz += CZ[i]*v; }
    mx = fmax(mx, (jx*jx + jy*jy + jz*jz)/((1.0f + d)*(1.0f + d)));
  }
  sm[lid] = mx;
  barrier(CLK_LOCAL_MEM_FENCE);
  for (int s = LS/2; s > 0; s >>= 1) { if (lid < s) sm[lid] = fmax(sm[lid], sm[lid + s]); barrier(CLK_LOCAL_MEM_FENCE); }
  if (lid == 0) out[get_group_id(0)] = sqrt(sm[0]);
}

__kernel void slice(__global const float* f, __global const uchar* flags, __global float* out,
                    int W_, int H_, int axW, int axH, int axN, int cN, int stride, int flipH) {
  const int p = get_global_id(0); if (p >= W_*H_) return;
  const int i = p % W_, j = p / W_;
  int dims[3] = {NX, NY, NZ};
  int c[3];
  c[axN] = cN;
  c[axW] = min(i*stride + stride/2, dims[axW] - 1);
  const int jh = min(j*stride + stride/2, dims[axH] - 1);
  c[axH] = flipH ? dims[axH] - 1 - jh : jh;
  const int n = c[0] + NX*(c[1] + NY*c[2]);
  if (is_wall(flags[n])) { out[p] = -1.0f; return; }
  float d = 0.0f, jx = 0.0f, jy = 0.0f, jz = 0.0f;
  for (int q = 0; q < 19; q++) { const float v = f[q*NN + n]; d += v; jx += CX[q]*v; jy += CY[q]*v; jz += CZ[q]*v; }
  out[p] = sqrt(jx*jx + jy*jy + jz*jz)/(1.0f + d);
}
";

        const int LS = 128, MaxGroups = 1024;
        readonly ClDevice _dev;
        readonly LbmData _d;
        IntPtr _ctx, _q, _prog;
        IntPtr _kInit, _kZero, _kFidx, _kStepAB, _kStepBA, _kForceA, _kForceB, _kMax, _kSlice;
        IntPtr _fA, _fB, _flags, _fidx, _ftab, _avg, _bcell, _boff, _inst, _acc, _mx, _slice, _tmp;
        bool _curIsB;    // 最新的分布函数在 B 里
        int _sinceFinish;

        public string Name => _dev.Display;

        public LbmGpu(ClDevice dev, LbmData d)
        {
            _dev = dev; _d = d;
            long N = d.N;
            long need = N * (19L * 4 * 2 + 1 + 4 + 16);
            if (19L * 4 * N > (long)dev.MaxAlloc) throw new ClException($"网格太大：一份分布函数要 {19L * 4 * N / 1e9:0.##} GB，超过显卡单块显存上限 {dev.MaxAlloc / 1e9:0.##} GB");
            if (need > (long)(dev.GlobalMem * 0.92)) throw new ClException($"显存不够：需要约 {need / 1e9:0.##} GB，显卡有 {dev.GlobalMem / 1e9:0.##} GB");
            try
            {
                _ctx = Cl.CreateContext(dev);
                _q = Cl.CreateQueue(_ctx, dev);
                var inv = CultureInfo.InvariantCulture;
                string F(float v) => ((double)v).ToString("0.0##########E+0", inv) + "f";   // 必须带小数点（"0f" 不是合法的 C 常量）
                var opt = $"-cl-fast-relaxed-math -D NX={d.Nx} -D NY={d.Ny} -D NZ={d.Nz} -D NN={N} -D TAU0={F(d.Tau0)} -D SMAG={F(d.Smag)} -D UX0={F(d.Ux)} -D UY0={F(d.Uy)} -D UZ0={F(d.Uz)} -D FARK={F(LbmData.FarRelax)}";
                _prog = Cl.Build(_ctx, dev, Source, opt);
                _kInit = Cl.Kernel(_prog, "init_f"); _kZero = Cl.Kernel(_prog, "zero_f"); _kFidx = Cl.Kernel(_prog, "set_fidx");
                _kStepAB = Cl.Kernel(_prog, "stream_collide"); _kStepBA = Cl.Kernel(_prog, "stream_collide");
                _kForceA = Cl.Kernel(_prog, "part_force"); _kForceB = Cl.Kernel(_prog, "part_force");
                _kMax = Cl.Kernel(_prog, "speed_max"); _kSlice = Cl.Kernel(_prog, "slice");

                _fA = Cl.Buffer(_ctx, 19L * 4 * N, "分布函数 A");
                _fB = Cl.Buffer(_ctx, 19L * 4 * N, "分布函数 B");
                _flags = Cl.Buffer(_ctx, N, "格子类型");
                _fidx = Cl.Buffer(_ctx, 4 * N, "体积力索引");
                _avg = Cl.Buffer(_ctx, 16 * N, "时间平均");
                int nf = d.ForceCells.Length;
                _ftab = Cl.Buffer(_ctx, 12L * Math.Max(1, nf), "体积力");
                _tmp = Cl.Buffer(_ctx, 4L * Math.Max(1, nf), "临时");
                _bcell = Cl.Buffer(_ctx, 4L * Math.Max(1, d.BCells.Length), "边界格子");
                _boff = Cl.Buffer(_ctx, 4L * d.BOffsets.Length, "边界格子分段");
                _inst = Cl.Buffer(_ctx, 12L * Math.Max(1, d.Parts), "零件受力");
                _acc = Cl.Buffer(_ctx, 12L * Math.Max(1, d.Parts), "零件受力累加");
                _mx = Cl.Buffer(_ctx, 4L * MaxGroups, "最大速度");
                _slice = Cl.Buffer(_ctx, 4L * Math.Max(1, d.SliceW * d.SliceH), "切面");

                // 上传
                for (long o = 0; o < N; o += 1 << 26) Cl.Write(_q, _flags, d.Flags, o, (int)o, (int)Math.Min(1 << 26, N - o));
                if (nf > 0)
                {
                    Cl.Write(_q, _ftab, d.ForceVec, 0, 0, 3 * nf);
                    Cl.Write(_q, _tmp, d.ForceCells, 0, 0, nf);
                    Cl.Arg(_kFidx, 0, _fidx); Cl.Arg(_kFidx, 1, _tmp); Cl.Arg(_kFidx, 2, nf);
                    Cl.Run(_q, _kFidx, nf, LS);
                }
                if (d.BCells.Length > 0) Cl.Write(_q, _bcell, d.BCells, 0, 0, d.BCells.Length);
                Cl.Write(_q, _boff, d.BOffsets, 0, 0, d.BOffsets.Length);

                // 内核参数（A→B、B→A 各一套，省得每步重设）
                SetStep(_kStepAB, _fA, _fB); SetStep(_kStepBA, _fB, _fA);
                SetForce(_kForceA, _fA); SetForce(_kForceB, _fB);
                Cl.Arg(_kMax, 1, _flags); Cl.Arg(_kMax, 2, _mx);
                Cl.Arg(_kSlice, 1, _flags); Cl.Arg(_kSlice, 2, _slice);
                Cl.Arg(_kSlice, 3, d.SliceW); Cl.Arg(_kSlice, 4, d.SliceH); Cl.Arg(_kSlice, 5, d.SliceAxW); Cl.Arg(_kSlice, 6, d.SliceAxH);
                Cl.Arg(_kSlice, 7, d.SliceAxN); Cl.Arg(_kSlice, 8, d.SliceN); Cl.Arg(_kSlice, 9, d.SliceStride); Cl.Arg(_kSlice, 10, d.SliceFlipH ? 1 : 0);
                Cl.Finish(_q);
            }
            catch { Dispose(); throw; }
        }

        void SetStep(IntPtr k, IntPtr src, IntPtr dst)
        {
            Cl.Arg(k, 0, src); Cl.Arg(k, 1, dst); Cl.Arg(k, 2, _flags); Cl.Arg(k, 3, _fidx); Cl.Arg(k, 4, _ftab); Cl.Arg(k, 5, _avg);
            Cl.Arg(k, 6, 0); Cl.Arg(k, 7, 1f);
        }

        void SetForce(IntPtr k, IntPtr f)
        {
            Cl.Arg(k, 0, f); Cl.Arg(k, 1, _flags); Cl.Arg(k, 2, _bcell); Cl.Arg(k, 3, _boff); Cl.Arg(k, 4, _inst); Cl.Arg(k, 5, _acc); Cl.Arg(k, 6, 0);
        }

        /// <summary>清零（缓冲区都小于 2^31 个元素：分布函数单块已经受显卡单块上限约束）。</summary>
        void Zero(IntPtr buf, long count)
        {
            Cl.Arg(_kZero, 0, buf); Cl.Arg(_kZero, 1, (int)count);
            Cl.Run(_q, _kZero, count, LS);
        }

        public void Init()
        {
            Cl.Arg(_kInit, 0, _fA); Cl.Arg(_kInit, 1, _fB); Cl.Arg(_kInit, 2, _flags);
            Cl.Run(_q, _kInit, _d.N, LS);
            Zero(_avg, 4 * _d.N);
            Zero(_inst, 3L * Math.Max(1, _d.Parts));
            Zero(_acc, 3L * Math.Max(1, _d.Parts));
            _curIsB = false;
            Cl.Finish(_q);
        }

        public void Step(bool accumU, bool accumF, float fscale)
        {
            var k = _curIsB ? _kStepBA : _kStepAB;
            Cl.Arg(k, 6, accumU ? 1 : 0); Cl.Arg(k, 7, fscale);
            Cl.Run(_q, k, _d.N, LS);
            _curIsB = !_curIsB;
            if (_d.Parts > 0)
            {
                var kf = _curIsB ? _kForceB : _kForceA;
                Cl.Arg(kf, 6, accumF ? 1 : 0);
                Cl.Run(_q, kf, (long)_d.Parts * LS, LS);
            }
            if (++_sinceFinish >= 16) { _sinceFinish = 0; Cl.Finish(_q); }   // 不让命令队列积压，取消、进度也及时
            else Cl.Flush(_q);
        }

        public void Finish() { Cl.Finish(_q); _sinceFinish = 0; }

        public void TakeInterval(float[] sum)
        {
            int p = _d.Parts;
            if (p == 0) return;
            Cl.Read(_q, _inst, sum, 0, 0, 3 * p);
            Zero(_inst, 3L * p);
        }

        public void ReadForces(float[] acc)
        {
            int p = _d.Parts;
            if (p == 0) return;
            Cl.Read(_q, _acc, acc, 0, 0, 3 * p);
        }

        public float MaxSpeed()
        {
            Cl.Arg(_kMax, 0, _curIsB ? _fB : _fA);
            int groups = (int)Math.Min(MaxGroups, Math.Max(1, _d.N / LS / 8));
            Cl.Run(_q, _kMax, (long)groups * LS, LS);
            var r = new float[groups];
            Cl.Read(_q, _mx, r, 0, 0, groups);
            float m = 0; foreach (var v in r) if (v > m) m = v;
            return m;
        }

        public float[] Slice()
        {
            int cnt = _d.SliceW * _d.SliceH;
            if (cnt <= 0) return null;
            Cl.Arg(_kSlice, 0, _curIsB ? _fB : _fA);
            Cl.Run(_q, _kSlice, cnt, LS);
            var r = new float[cnt];
            Cl.Read(_q, _slice, r, 0, 0, cnt);
            return r;
        }

        public void ReadAverage(int comp, long offset, float[] dst, int count) => Cl.Read(_q, _avg, dst, comp * _d.N + offset, 0, count);

        public void Dispose()
        {
            Cl.FinishQuiet(_q);
            Cl.ReleaseMem(ref _fA); Cl.ReleaseMem(ref _fB); Cl.ReleaseMem(ref _flags); Cl.ReleaseMem(ref _fidx); Cl.ReleaseMem(ref _ftab);
            Cl.ReleaseMem(ref _avg); Cl.ReleaseMem(ref _bcell); Cl.ReleaseMem(ref _boff); Cl.ReleaseMem(ref _inst); Cl.ReleaseMem(ref _acc);
            Cl.ReleaseMem(ref _mx); Cl.ReleaseMem(ref _slice); Cl.ReleaseMem(ref _tmp);
            Cl.ReleaseKernel(ref _kInit); Cl.ReleaseKernel(ref _kZero); Cl.ReleaseKernel(ref _kFidx); Cl.ReleaseKernel(ref _kStepAB); Cl.ReleaseKernel(ref _kStepBA);
            Cl.ReleaseKernel(ref _kForceA); Cl.ReleaseKernel(ref _kForceB); Cl.ReleaseKernel(ref _kMax); Cl.ReleaseKernel(ref _kSlice);
            Cl.ReleaseProgram(ref _prog);
            Cl.ReleaseQueue(ref _q);
            Cl.ReleaseContext(ref _ctx);
        }
    }

    // =====================================================================================
    //  CPU 实现（和显卡内核逐行对应）
    // =====================================================================================
    internal sealed class LbmCpu : ILbm
    {
        readonly LbmData _d;
        readonly int _n, _nx, _ny, _nz, _nxy;
        float[][] _a, _b, _avg;
        readonly int[] _fidx;
        readonly float[] _inst, _acc;
        readonly int[] _off = new int[19];
        readonly int _threads;
        bool _curIsB;

        public string Name => $"CPU（{_threads} 线程）";

        public LbmCpu(LbmData d)
        {
            _d = d;
            if (d.N > int.MaxValue) throw new InvalidOperationException("网格太大，CPU 算不了");
            _n = (int)d.N; _nx = d.Nx; _ny = d.Ny; _nz = d.Nz; _nxy = _nx * _ny;
            _threads = Environment.ProcessorCount;
            for (int i = 0; i < 19; i++) _off[i] = D3Q19.Cx[i] + D3Q19.Cy[i] * _nx + D3Q19.Cz[i] * _nxy;
            _a = new float[19][]; _b = new float[19][]; _avg = new float[4][];
            for (int i = 0; i < 19; i++) { _a[i] = new float[_n]; _b[i] = new float[_n]; }
            for (int i = 0; i < 4; i++) _avg[i] = new float[_n];
            _fidx = new int[_n];
            for (int k = 0; k < d.ForceCells.Length; k++) _fidx[d.ForceCells[k]] = k;
            _inst = new float[3 * Math.Max(1, d.Parts)];
            _acc = new float[3 * Math.Max(1, d.Parts)];
        }

        static float Geq(int i, float drho, float rho, float ux, float uy, float uz, float u2)
        {
            float cu = 3.0f * (D3Q19.Cx[i] * ux + D3Q19.Cy[i] * uy + D3Q19.Cz[i] * uz);
            return D3Q19.W[i] * (drho + rho * (cu + 0.5f * cu * cu - u2));
        }

        public void Init()
        {
            var fl = _d.Flags;
            Parallel.For(0, _nz, z =>
            {
                for (int n = z * _nxy, e = n + _nxy; n < e; n++)
                {
                    bool wall = CellType.IsWall(fl[n]);
                    float ux = wall ? 0 : _d.Ux, uy = wall ? 0 : _d.Uy, uz = wall ? 0 : _d.Uz;
                    float u2 = 1.5f * (ux * ux + uy * uy + uz * uz);
                    for (int i = 0; i < 19; i++) { float g = Geq(i, 0, 1, ux, uy, uz, u2); _a[i][n] = g; _b[i][n] = g; }
                }
            });
            _curIsB = false;
        }

        public void Step(bool accumU, bool accumF, float fscale)
        {
            var src = _curIsB ? _b : _a;
            var dst = _curIsB ? _a : _b;
            Parallel.For(0, _nz, new ParallelOptions { MaxDegreeOfParallelism = _threads }, () => new float[19], (z, st, g) =>
            {
                for (int y = 0; y < _ny; y++)
                    for (int x = 0; x < _nx; x++)
                        Cell(x + _nx * (y + _ny * z), x, y, z, src, dst, g, accumU, fscale);
                return g;
            }, g => { });
            _curIsB = !_curIsB;
            if (_d.Parts > 0) PartForces(_curIsB ? _b : _a, accumF);
        }

        void Cell(int n, int x, int y, int z, float[][] src, float[][] dst, float[] g, bool accum, float fscale)
        {
            var flags = _d.Flags;
            byte fl = flags[n];
            int t = fl & 7;
            if (t == CellType.Solid || t == CellType.Wall) return;
            var CX = D3Q19.Cx; var CY = D3Q19.Cy; var CZ = D3Q19.Cz; var W = D3Q19.W;
            float drho, ux, uy, uz;
            if (t == CellType.Fluid)
            {
                if ((fl & CellType.NearWall) != 0)
                    for (int i = 0; i < 19; i++)
                    {
                        int m = n - _off[i];
                        g[i] = CellType.IsWall(flags[m]) ? src[D3Q19.Opp(i)][n] : src[i][m];
                    }
                else
                    for (int i = 0; i < 19; i++) g[i] = src[i][n - _off[i]];
                // 矩（按 D3Q19 方向表展开）
                float g1 = g[1], g2 = g[2], g3 = g[3], g4 = g[4], g5 = g[5], g6 = g[6], g7 = g[7], g8 = g[8], g9 = g[9], g10 = g[10],
                      g11 = g[11], g12 = g[12], g13 = g[13], g14 = g[14], g15 = g[15], g16 = g[16], g17 = g[17], g18 = g[18];
                drho = g[0] + g1 + g2 + g3 + g4 + g5 + g6 + g7 + g8 + g9 + g10 + g11 + g12 + g13 + g14 + g15 + g16 + g17 + g18;
                float jx = g1 - g2 + g7 - g8 + g9 - g10 + g13 - g14 + g15 - g16;
                float jy = g3 - g4 + g7 - g8 + g11 - g12 - g13 + g14 + g17 - g18;
                float jz = g5 - g6 + g9 - g10 + g11 - g12 - g15 + g16 - g17 + g18;
                float pxx = g1 + g2 + g7 + g8 + g9 + g10 + g13 + g14 + g15 + g16;
                float pyy = g3 + g4 + g7 + g8 + g11 + g12 + g13 + g14 + g17 + g18;
                float pzz = g5 + g6 + g9 + g10 + g11 + g12 + g15 + g16 + g17 + g18;
                float pxy = g7 + g8 - g13 - g14, pxz = g9 + g10 - g15 - g16, pyz = g11 + g12 - g17 - g18;
                float fx = 0, fy = 0, fz = 0;
                if ((fl & CellType.Forced) != 0) { int k = _fidx[n]; fx = fscale * _d.ForceVec[3 * k]; fy = fscale * _d.ForceVec[3 * k + 1]; fz = fscale * _d.ForceVec[3 * k + 2]; }
                float rho = 1.0f + drho, ir = 1.0f / rho;
                ux = (jx + 0.5f * fx) * ir; uy = (jy + 0.5f * fy) * ir; uz = (jz + 0.5f * fz) * ir;
                const float third = 1.0f / 3.0f;
                float Pxx = pxx - drho * third - rho * ux * ux, Pyy = pyy - drho * third - rho * uy * uy, Pzz = pzz - drho * third - rho * uz * uz;
                float Pxy = pxy - rho * ux * uy, Pxz = pxz - rho * ux * uz, Pyz = pyz - rho * uy * uz;
                float Qxx = Pxx + fx * ux, Qyy = Pyy + fy * uy, Qzz = Pzz + fz * uz;
                float Qxy = Pxy + 0.5f * (fx * uy + fy * ux), Qxz = Pxz + 0.5f * (fx * uz + fz * ux), Qyz = Pyz + 0.5f * (fy * uz + fz * uy);
                float Q = (float)Math.Sqrt(Qxx * Qxx + Qyy * Qyy + Qzz * Qzz + 2.0f * (Qxy * Qxy + Qxz * Qxz + Qyz * Qyz));
                float tau0 = _d.Tau0;
                float tau = 0.5f * (tau0 + (float)Math.Sqrt(tau0 * tau0 + _d.Smag * Q * ir));
                float om = 1.0f / tau, c1 = 1.0f - om, c2 = 1.0f - 0.5f * om;
                float u2 = 1.5f * (ux * ux + uy * uy + uz * uz), uF = ux * fx + uy * fy + uz * fz;
                for (int i = 0; i < 19; i++)
                {
                    float cx = CX[i], cy = CY[i], cz = CZ[i];
                    float cu = 3.0f * (cx * ux + cy * uy + cz * uz);
                    float e = W[i] * (drho + rho * (cu + 0.5f * cu * cu - u2));
                    float f1 = 4.5f * W[i] * ((cx * cx - third) * Pxx + (cy * cy - third) * Pyy + (cz * cz - third) * Pzz
                                              + 2.0f * (cx * cy * Pxy + cx * cz * Pxz + cy * cz * Pyz));
                    float cF = cx * fx + cy * fy + cz * fz;
                    float S = W[i] * (3.0f * (cF - uF) + 3.0f * cu * cF);
                    dst[i][n] = e + c1 * (f1 - 1.5f * W[i] * cF) + c2 * S;
                }
            }
            else
            {
                int m = Clamp(x, 1, _nx - 2) + _nx * (Clamp(y, 1, _ny - 2) + _ny * Clamp(z, 1, _nz - 2));
                float dri = 0, jx = 0, jy = 0, jz = 0;
                for (int i = 0; i < 19; i++) { float v = src[i][m]; g[i] = v; dri += v; jx += CX[i] * v; jy += CY[i] * v; jz += CZ[i] * v; }
                float uix = jx / (1.0f + dri), uiy = jy / (1.0f + dri), uiz = jz / (1.0f + dri);
                if ((flags[m] & 7) != CellType.Fluid) { dri = 0; uix = _d.Ux; uiy = _d.Uy; uiz = _d.Uz; }
                if (t == CellType.Far)
                {
                    ux = uix; uy = uiy; uz = uiz;
                    if (x > 0 && x < _nx - 1) ux += FarK * (_d.Ux - uix);
                    if (y > 0 && y < _ny - 1) uy += FarK * (_d.Uy - uiy);
                    if (z > 0 && z < _nz - 1) uz += FarK * (_d.Uz - uiz);
                    float s = (float)Math.Sqrt(ux * ux + uy * uy + uz * uz);
                    if (s > 0.2f) { ux *= 0.2f / s; uy *= 0.2f / s; uz *= 0.2f / s; }
                    // 空气从这个面流进来时：总压 = 来流总压（静压 = p0 + ½ρ(V∞² − |u|²)）；流出去时静压 = p0。中间平滑过渡
                    float un = (x == 0 ? ux : 0f) - (x == _nx - 1 ? ux : 0f) + (y == 0 ? uy : 0f) - (y == _ny - 1 ? uy : 0f) + (z == 0 ? uz : 0f) - (z == _nz - 1 ? uz : 0f);
                    float w = Math.Max(0f, Math.Min(1f, 0.5f + 50.0f * un));
                    drho = w * 1.5f * (_d.Ux * _d.Ux + _d.Uy * _d.Uy + _d.Uz * _d.Uz - ux * ux - uy * uy - uz * uz);
                }
                else if (t == CellType.Inlet)
                {
                    drho = Math.Max(-0.05f, Math.Min(0.05f, dri)); ux = _d.Ux; uy = _d.Uy; uz = _d.Uz;
                }
                else
                {
                    drho = dri; ux = uix; uy = uiy; uz = uiz;
                    if (x == 0) ux = Math.Min(ux, 0); if (x == _nx - 1) ux = Math.Max(ux, 0);
                    if (y == 0) uy = Math.Min(uy, 0); if (y == _ny - 1) uy = Math.Max(uy, 0);
                    if (z == 0) uz = Math.Min(uz, 0); if (z == _nz - 1) uz = Math.Max(uz, 0);
                }
                float rho = 1.0f + drho, u2 = 1.5f * (ux * ux + uy * uy + uz * uz);
                float rhoi = 1.0f + dri, u2i = 1.5f * (uix * uix + uiy * uiy + uiz * uiz);
                for (int i = 0; i < 19; i++)
                {
                    float v = Geq(i, drho, rho, ux, uy, uz, u2);
                    if (t == CellType.Outflow) v += g[i] - Geq(i, dri, rhoi, uix, uiy, uiz, u2i);
                    dst[i][n] = v;
                }
            }
            if (accum) { _avg[0][n] += ux; _avg[1][n] += uy; _avg[2][n] += uz; _avg[3][n] += drho; }
        }

        static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;
        const float FarK = LbmData.FarRelax;

        void PartForces(float[][] f, bool accum)
        {
            int parts = _d.Parts;
            var flags = _d.Flags;
            Parallel.For(0, parts, p =>
            {
                double ax = 0, ay = 0, az = 0;
                for (int b = _d.BOffsets[p]; b < _d.BOffsets[p + 1]; b++)
                {
                    int n = _d.BCells[b];
                    for (int i = 1; i < 19; i++)
                        if ((flags[n + _off[i]] & 7) == CellType.Solid)
                        {
                            float v = 2.0f * f[i][n];
                            ax += D3Q19.Cx[i] * v; ay += D3Q19.Cy[i] * v; az += D3Q19.Cz[i] * v;
                        }
                }
                _inst[3 * p] += (float)ax; _inst[3 * p + 1] += (float)ay; _inst[3 * p + 2] += (float)az;
                if (accum) { _acc[3 * p] += (float)ax; _acc[3 * p + 1] += (float)ay; _acc[3 * p + 2] += (float)az; }
            });
        }

        public void Finish() { }

        public void TakeInterval(float[] sum)
        {
            int p = _d.Parts;
            Array.Copy(_inst, sum, 3 * p);
            Array.Clear(_inst, 0, 3 * p);
        }

        public void ReadForces(float[] acc) => Array.Copy(_acc, acc, 3 * _d.Parts);

        public float MaxSpeed()
        {
            var f = _curIsB ? _b : _a;
            var flags = _d.Flags;
            float best = 0;
            var lk = new object();
            Parallel.For(0, _nz, () => 0f, (z, st, mx) =>
            {
                for (int n = z * _nxy, e = n + _nxy; n < e; n++)
                {
                    if ((flags[n] & 7) != CellType.Fluid) continue;
                    float d = 0, jx = 0, jy = 0, jz = 0;
                    for (int i = 0; i < 19; i++) { float v = f[i][n]; d += v; jx += D3Q19.Cx[i] * v; jy += D3Q19.Cy[i] * v; jz += D3Q19.Cz[i] * v; }
                    float s = (jx * jx + jy * jy + jz * jz) / ((1 + d) * (1 + d));
                    if (s > mx) mx = s;
                }
                return mx;
            }, mx => { lock (lk) if (mx > best) best = mx; });
            return (float)Math.Sqrt(best);
        }

        public float[] Slice()
        {
            var d = _d;
            int cnt = d.SliceW * d.SliceH;
            if (cnt <= 0) return null;
            var f = _curIsB ? _b : _a;
            var r = new float[cnt];
            int[] dims = { _nx, _ny, _nz };
            Parallel.For(0, d.SliceH, j =>
            {
                var c = new int[3];
                for (int i = 0; i < d.SliceW; i++)
                {
                    c[d.SliceAxN] = d.SliceN;
                    c[d.SliceAxW] = Math.Min(i * d.SliceStride + d.SliceStride / 2, dims[d.SliceAxW] - 1);
                    int jh = Math.Min(j * d.SliceStride + d.SliceStride / 2, dims[d.SliceAxH] - 1);
                    c[d.SliceAxH] = d.SliceFlipH ? dims[d.SliceAxH] - 1 - jh : jh;
                    int n = c[0] + _nx * (c[1] + _ny * c[2]);
                    if (CellType.IsWall(d.Flags[n])) { r[i + d.SliceW * j] = -1; continue; }
                    float dd = 0, jx = 0, jy = 0, jz = 0;
                    for (int q = 0; q < 19; q++) { float v = f[q][n]; dd += v; jx += D3Q19.Cx[q] * v; jy += D3Q19.Cy[q] * v; jz += D3Q19.Cz[q] * v; }
                    r[i + d.SliceW * j] = (float)Math.Sqrt(jx * jx + jy * jy + jz * jz) / (1 + dd);
                }
            });
            return r;
        }

        public void ReadAverage(int comp, long offset, float[] dst, int count) => Array.Copy(_avg[comp], offset, dst, 0, count);

        public void Dispose() { _a = _b = _avg = null; }
    }
}
