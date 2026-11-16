using System;
using System.Diagnostics;
using System.Runtime;

namespace SwKit.App
{
    /// <summary>
    /// 重计算（应力分析、拓扑优化、自适应加密）期间把本进程降到「低于正常」优先级：用户同时在用 SolidWorks 或别的软件时不卡；
    /// 没有别的程序抢 CPU 时速度不变。结束后恢复，并把大数组占的内存还给系统。
    /// </summary>
    static class HeavyWork
    {
        static int _depth;

        public static IDisposable Begin()
        {
            if (_depth++ == 0) { try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal; } catch { } }
            return new End();
        }

        sealed class End : IDisposable
        {
            bool _done;
            public void Dispose()
            {
                if (_done) return;
                _done = true;
                if (--_depth == 0)
                {
                    try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.Normal; } catch { }
                    ReleaseMemory();
                }
            }
        }

        /// <summary>整理大对象堆并回收：几百 MB 的矩阵用完后，任务管理器里的内存占用也会降下来。</summary>
        public static void ReleaseMemory()
        {
            try
            {
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
                GC.WaitForPendingFinalizers();
                SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, (IntPtr)(-1), (IntPtr)(-1));
            }
            catch { }
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);
    }
}