using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace SwKit.Core.Cfd
{
    /// <summary>OpenCL 调用出错（显卡驱动、显存不够、内核编译失败……）。求解器捕获它以后改用 CPU。</summary>
    public sealed class ClException : Exception
    {
        public ClException(string msg) : base(msg) { }
    }

    /// <summary>一块 OpenCL 计算设备。</summary>
    internal sealed class ClDevice
    {
        public IntPtr Platform, Id;
        public string Name, Vendor, PlatformName, Version;
        public ulong Type;
        public uint Units, ClockMHz;
        public ulong GlobalMem, MaxAlloc;
        public bool Unified;          // 和 CPU 共用内存（核显）
        public double Score;
        public string Display;        // 给用户看的名字：「NVIDIA GeForce RTX 5070 Ti Laptop GPU（OpenCL）」
    }

    /// <summary>
    /// OpenCL 的最小封装：直接 P/Invoke 系统里的 OpenCL.dll（显卡驱动自带，不需要另装运行库）。
    /// 只用 OpenCL 1.2 的接口；所有读写都是阻塞的（托管数组只在调用期间固定）。
    /// </summary>
    internal static class Cl
    {
        const string Dll = "OpenCL.dll";
        public const ulong DEVICE_TYPE_GPU = 1 << 2, DEVICE_TYPE_ALL = 0xFFFFFFFF;
        public const ulong MEM_READ_WRITE = 1 << 0, MEM_READ_ONLY = 1 << 2;
        const uint PLATFORM_NAME = 0x0902, PLATFORM_VERSION = 0x0901;
        const uint DEVICE_TYPE = 0x1000, DEVICE_MAX_COMPUTE_UNITS = 0x1002, DEVICE_MAX_CLOCK_FREQUENCY = 0x100C,
                   DEVICE_MAX_MEM_ALLOC_SIZE = 0x1010, DEVICE_GLOBAL_MEM_SIZE = 0x101F, DEVICE_HOST_UNIFIED_MEMORY = 0x1035,
                   DEVICE_NAME = 0x102B, DEVICE_VENDOR = 0x102C;
        const uint PROGRAM_BUILD_LOG = 0x1183;

        [DllImport(Dll)] static extern int clGetPlatformIDs(uint num, [Out] IntPtr[] platforms, out uint numPlatforms);
        [DllImport(Dll)] static extern int clGetPlatformInfo(IntPtr platform, uint name, UIntPtr size, byte[] value, out UIntPtr sizeRet);
        [DllImport(Dll)] static extern int clGetDeviceIDs(IntPtr platform, ulong type, uint num, [Out] IntPtr[] devices, out uint numDevices);
        [DllImport(Dll)] static extern int clGetDeviceInfo(IntPtr device, uint name, UIntPtr size, byte[] value, out UIntPtr sizeRet);
        [DllImport(Dll)] static extern IntPtr clCreateContext(IntPtr[] props, uint num, IntPtr[] devices, IntPtr notify, IntPtr user, out int err);
        [DllImport(Dll)] static extern IntPtr clCreateCommandQueue(IntPtr ctx, IntPtr device, ulong props, out int err);
        [DllImport(Dll)] static extern IntPtr clCreateProgramWithSource(IntPtr ctx, uint count, string[] strings, IntPtr lengths, out int err);
        [DllImport(Dll)] static extern int clBuildProgram(IntPtr program, uint num, IntPtr[] devices, string options, IntPtr notify, IntPtr user);
        [DllImport(Dll)] static extern int clGetProgramBuildInfo(IntPtr program, IntPtr device, uint name, UIntPtr size, byte[] value, out UIntPtr sizeRet);
        [DllImport(Dll)] static extern IntPtr clCreateKernel(IntPtr program, string name, out int err);
        [DllImport(Dll)] static extern IntPtr clCreateBuffer(IntPtr ctx, ulong flags, UIntPtr size, IntPtr host, out int err);
        [DllImport(Dll)] static extern int clSetKernelArg(IntPtr kernel, uint index, UIntPtr size, ref IntPtr value);
        [DllImport(Dll)] static extern int clSetKernelArg(IntPtr kernel, uint index, UIntPtr size, ref int value);
        [DllImport(Dll)] static extern int clSetKernelArg(IntPtr kernel, uint index, UIntPtr size, ref float value);
        [DllImport(Dll)] static extern int clEnqueueNDRangeKernel(IntPtr q, IntPtr kernel, uint dim, IntPtr offset, UIntPtr[] global, UIntPtr[] local, uint nev, IntPtr evs, IntPtr ev);
        [DllImport(Dll)] static extern int clEnqueueReadBuffer(IntPtr q, IntPtr buf, uint blocking, UIntPtr offset, UIntPtr size, IntPtr ptr, uint nev, IntPtr evs, IntPtr ev);
        [DllImport(Dll)] static extern int clEnqueueWriteBuffer(IntPtr q, IntPtr buf, uint blocking, UIntPtr offset, UIntPtr size, IntPtr ptr, uint nev, IntPtr evs, IntPtr ev);
        [DllImport(Dll)] static extern int clFinish(IntPtr q);
        [DllImport(Dll)] static extern int clFlush(IntPtr q);
        [DllImport(Dll)] static extern int clReleaseMemObject(IntPtr m);
        [DllImport(Dll)] static extern int clReleaseKernel(IntPtr k);
        [DllImport(Dll)] static extern int clReleaseProgram(IntPtr p);
        [DllImport(Dll)] static extern int clReleaseCommandQueue(IntPtr q);
        [DllImport(Dll)] static extern int clReleaseContext(IntPtr c);

        // ================= 设备 =================

        static List<ClDevice> _devices;
        static string _probeError;
        static readonly object Lock = new object();

        /// <summary>本机的 OpenCL 显卡，按算力从高到低排（独显优先）。没有 OpenCL 时返回空表，不抛异常。</summary>
        public static List<ClDevice> Devices()
        {
            lock (Lock)
            {
                if (_devices != null) return _devices;
                var list = new List<ClDevice>();
                try
                {
                    if (clGetPlatformIDs(0, null, out uint np) == 0 && np > 0)
                    {
                        var plats = new IntPtr[np];
                        clGetPlatformIDs(np, plats, out np);
                        foreach (var p in plats)
                        {
                            if (clGetDeviceIDs(p, DEVICE_TYPE_ALL, 0, null, out uint nd) != 0 || nd == 0) continue;
                            var devs = new IntPtr[nd];
                            if (clGetDeviceIDs(p, DEVICE_TYPE_ALL, nd, devs, out nd) != 0) continue;
                            foreach (var d in devs)
                            {
                                var dev = new ClDevice
                                {
                                    Platform = p, Id = d, PlatformName = PlatformStr(p, PLATFORM_NAME), Version = PlatformStr(p, PLATFORM_VERSION),
                                    Name = DevStr(d, DEVICE_NAME), Vendor = DevStr(d, DEVICE_VENDOR), Type = DevU64(d, DEVICE_TYPE),
                                    Units = (uint)DevU64(d, DEVICE_MAX_COMPUTE_UNITS, 4), ClockMHz = (uint)DevU64(d, DEVICE_MAX_CLOCK_FREQUENCY, 4),
                                    GlobalMem = DevU64(d, DEVICE_GLOBAL_MEM_SIZE), MaxAlloc = DevU64(d, DEVICE_MAX_MEM_ALLOC_SIZE),
                                    Unified = DevU64(d, DEVICE_HOST_UNIFIED_MEMORY, 4) != 0,
                                };
                                if ((dev.Type & DEVICE_TYPE_GPU) == 0) continue;   // 只用显卡；CPU 走托管代码
                                var v = (dev.Vendor + " " + dev.Name).ToUpperInvariant();
                                bool discrete = !dev.Unified || v.Contains("NVIDIA") || v.Contains("GEFORCE") || v.Contains("RADEON PRO") || v.Contains("RTX");
                                dev.Score = (discrete ? 1e6 : 0) + dev.Units * (double)Math.Max(1u, dev.ClockMHz) + dev.GlobalMem / 1e9;
                                list.Add(dev);
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException || ex is BadImageFormatException || ex is SEHException || ex is AccessViolationException)
                {
                    _probeError = ex.Message;
                }
                list = list.OrderByDescending(d => d.Score).ToList();
                var seen = new Dictionary<string, int>();
                foreach (var d in list)
                {
                    var name = d.Name.Trim();
                    if (name.Length == 0) name = d.Vendor.Trim();
                    seen[name] = seen.TryGetValue(name, out int c) ? c + 1 : 1;
                    d.Display = seen[name] == 1 ? name + "（OpenCL）" : $"{name} #{seen[name]}（OpenCL）";
                }
                _devices = list;
                return list;
            }
        }

        public static string ProbeError => _probeError;

        static string PlatformStr(IntPtr p, uint what)
        {
            if (clGetPlatformInfo(p, what, UIntPtr.Zero, null, out var sz) != 0) return "";
            var b = new byte[(int)sz.ToUInt64()];
            if (clGetPlatformInfo(p, what, (UIntPtr)b.Length, b, out sz) != 0) return "";
            return Encoding.UTF8.GetString(b).TrimEnd('\0', ' ');
        }

        static string DevStr(IntPtr d, uint what)
        {
            if (clGetDeviceInfo(d, what, UIntPtr.Zero, null, out var sz) != 0) return "";
            var b = new byte[(int)sz.ToUInt64()];
            if (clGetDeviceInfo(d, what, (UIntPtr)b.Length, b, out sz) != 0) return "";
            return Encoding.UTF8.GetString(b).TrimEnd('\0', ' ');
        }

        static ulong DevU64(IntPtr d, uint what, int size = 8)
        {
            var b = new byte[8];
            if (clGetDeviceInfo(d, what, (UIntPtr)size, b, out _) != 0) return 0;
            return BitConverter.ToUInt64(b, 0);
        }

        // ================= 上下文、程序、缓冲区 =================

        public static void Check(int err, string what)
        {
            if (err != 0) throw new ClException($"OpenCL {what} 出错：{ErrName(err)}");
        }

        public static string ErrName(int e)
        {
            switch (e)
            {
                case -1: return "找不到设备 (CL_DEVICE_NOT_FOUND)";
                case -2: return "设备不可用 (CL_DEVICE_NOT_AVAILABLE)";
                case -4: return "显存分配失败 (CL_MEM_OBJECT_ALLOCATION_FAILURE)";
                case -5: return "资源不足 (CL_OUT_OF_RESOURCES)";
                case -6: return "主机内存不足 (CL_OUT_OF_HOST_MEMORY)";
                case -11: return "内核编译失败 (CL_BUILD_PROGRAM_FAILURE)";
                case -30: return "参数无效 (CL_INVALID_VALUE)";
                case -36: return "命令队列无效 (CL_INVALID_COMMAND_QUEUE)";
                case -38: return "缓冲区无效 (CL_INVALID_MEM_OBJECT)";
                case -48: return "内核无效 (CL_INVALID_KERNEL)";
                case -51: return "内核参数大小不对 (CL_INVALID_ARG_SIZE)";
                case -52: return "内核参数缺失 (CL_INVALID_KERNEL_ARGS)";
                case -54: return "工作组大小不对 (CL_INVALID_WORK_GROUP_SIZE)";
                case -61: return "缓冲区太大 (CL_INVALID_BUFFER_SIZE)";
                default: return "错误码 " + e;
            }
        }

        public static IntPtr CreateContext(ClDevice dev)
        {
            var props = new[] { (IntPtr)0x1084 /* CL_CONTEXT_PLATFORM */, dev.Platform, IntPtr.Zero };
            var ctx = clCreateContext(props, 1, new[] { dev.Id }, IntPtr.Zero, IntPtr.Zero, out int err);
            Check(err, "创建上下文");
            return ctx;
        }

        public static IntPtr CreateQueue(IntPtr ctx, ClDevice dev)
        {
            var q = clCreateCommandQueue(ctx, dev.Id, 0, out int err);
            Check(err, "创建命令队列");
            return q;
        }

        /// <summary>编译内核源码；失败时把编译日志放进异常信息。</summary>
        public static IntPtr Build(IntPtr ctx, ClDevice dev, string source, string options)
        {
            var prog = clCreateProgramWithSource(ctx, 1, new[] { source }, IntPtr.Zero, out int err);
            Check(err, "创建程序");
            int b = clBuildProgram(prog, 1, new[] { dev.Id }, options, IntPtr.Zero, IntPtr.Zero);
            if (b != 0)
            {
                string log = "";
                if (clGetProgramBuildInfo(prog, dev.Id, PROGRAM_BUILD_LOG, UIntPtr.Zero, null, out var sz) == 0 && sz.ToUInt64() > 0)
                {
                    var buf = new byte[(int)sz.ToUInt64()];
                    if (clGetProgramBuildInfo(prog, dev.Id, PROGRAM_BUILD_LOG, (UIntPtr)buf.Length, buf, out sz) == 0) log = Encoding.UTF8.GetString(buf).TrimEnd('\0');
                }
                clReleaseProgram(prog);
                if (log.Length > 3000) log = log.Substring(0, 3000) + "……";
                throw new ClException($"OpenCL 内核编译失败（{ErrName(b)}）：\n{log}");
            }
            return prog;
        }

        public static IntPtr Kernel(IntPtr prog, string name)
        {
            var k = clCreateKernel(prog, name, out int err);
            Check(err, "创建内核 " + name);
            return k;
        }

        public static IntPtr Buffer(IntPtr ctx, long bytes, string what)
        {
            var b = clCreateBuffer(ctx, MEM_READ_WRITE, (UIntPtr)(ulong)Math.Max(4, bytes), IntPtr.Zero, out int err);
            Check(err, $"分配显存（{what}，{bytes / 1048576.0:0} MB）");
            return b;
        }

        public static void Arg(IntPtr k, uint i, IntPtr mem) { Check(clSetKernelArg(k, i, (UIntPtr)IntPtr.Size, ref mem), "设置内核参数"); }
        public static void Arg(IntPtr k, uint i, int v) { Check(clSetKernelArg(k, i, (UIntPtr)4, ref v), "设置内核参数"); }
        public static void Arg(IntPtr k, uint i, float v) { Check(clSetKernelArg(k, i, (UIntPtr)4, ref v), "设置内核参数"); }

        public static void Run(IntPtr q, IntPtr k, long global, int local)
        {
            long g = (global + local - 1) / local * local;
            Check(clEnqueueNDRangeKernel(q, k, 1, IntPtr.Zero, new[] { (UIntPtr)(ulong)g }, new[] { (UIntPtr)(uint)local }, 0, IntPtr.Zero, IntPtr.Zero), "启动内核");
        }

        public static void Finish(IntPtr q) => Check(clFinish(q), "等待显卡");
        public static void Flush(IntPtr q) => clFlush(q);

        /// <summary>阻塞写：host[hostOffset ..] 的 count 个元素写到显存 buf 的 devOffset（元素）处。</summary>
        public static void Write<T>(IntPtr q, IntPtr buf, T[] host, long devOffset, int hostOffset, int count) where T : struct
        {
            if (count <= 0) return;
            int es = Marshal.SizeOf(typeof(T));
            var h = GCHandle.Alloc(host, GCHandleType.Pinned);
            try
            {
                var ptr = h.AddrOfPinnedObject() + hostOffset * es;
                Check(clEnqueueWriteBuffer(q, buf, 1, (UIntPtr)(ulong)(devOffset * es), (UIntPtr)(ulong)((long)count * es), ptr, 0, IntPtr.Zero, IntPtr.Zero), "写显存");
            }
            finally { h.Free(); }
        }

        /// <summary>阻塞读：显存 buf 的 devOffset（元素）处 count 个元素读到 host[hostOffset ..]。</summary>
        public static void Read<T>(IntPtr q, IntPtr buf, T[] host, long devOffset, int hostOffset, int count) where T : struct
        {
            if (count <= 0) return;
            int es = Marshal.SizeOf(typeof(T));
            var h = GCHandle.Alloc(host, GCHandleType.Pinned);
            try
            {
                var ptr = h.AddrOfPinnedObject() + hostOffset * es;
                Check(clEnqueueReadBuffer(q, buf, 1, (UIntPtr)(ulong)(devOffset * es), (UIntPtr)(ulong)((long)count * es), ptr, 0, IntPtr.Zero, IntPtr.Zero), "读显存");
            }
            finally { h.Free(); }
        }

        public static void Release(ref IntPtr h, Func<IntPtr, int> rel)
        {
            if (h != IntPtr.Zero) { try { rel(h); } catch { } h = IntPtr.Zero; }
        }
        public static void ReleaseMem(ref IntPtr h) => Release(ref h, clReleaseMemObject);
        public static void ReleaseKernel(ref IntPtr h) => Release(ref h, clReleaseKernel);
        public static void ReleaseProgram(ref IntPtr h) => Release(ref h, clReleaseProgram);
        public static void ReleaseQueue(ref IntPtr h) => Release(ref h, clReleaseCommandQueue);
        public static void ReleaseContext(ref IntPtr h) => Release(ref h, clReleaseContext);
        public static void FinishQuiet(IntPtr q) { if (q != IntPtr.Zero) try { clFinish(q); } catch { } }
    }
}
