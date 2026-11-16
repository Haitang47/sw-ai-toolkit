using System;
using System.Runtime.InteropServices;

namespace SwKit.Core
{
    [ComImport, Guid("00000016-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IOleMessageFilter
    {
        [PreserveSig] int HandleInComingCall(int dwCallType, IntPtr hTaskCaller, int dwTickCount, IntPtr lpInterfaceInfo);
        [PreserveSig] int RetryRejectedCall(IntPtr hTaskCallee, int dwTickCount, int dwRejectType);
        [PreserveSig] int MessagePending(IntPtr hTaskCallee, int dwTickCount, int dwPendingType);
    }

    /// <summary>
    /// SolidWorks 忙（启动中、重建中、弹着对话框）时会拒绝外部调用。
    /// 注册这个消息过滤器后，被拒绝的调用会自动重试，最长等待 2 分钟，而不是直接抛异常。
    /// 必须在 STA 线程上注册。
    /// </summary>
    public sealed class ComMessageFilter : IOleMessageFilter
    {
        const int SERVERCALL_RETRYLATER = 2;
        const int PENDINGMSG_WAITDEFPROCESS = 2;

        [DllImport("Ole32.dll")]
        static extern int CoRegisterMessageFilter(IOleMessageFilter newFilter, out IOleMessageFilter oldFilter);

        public static void Register() => CoRegisterMessageFilter(new ComMessageFilter(), out _);
        public static void Revoke() => CoRegisterMessageFilter(null, out _);

        int IOleMessageFilter.HandleInComingCall(int dwCallType, IntPtr hTaskCaller, int dwTickCount, IntPtr lpInterfaceInfo) => 0;

        int IOleMessageFilter.RetryRejectedCall(IntPtr hTaskCallee, int dwTickCount, int dwRejectType)
            => dwRejectType == SERVERCALL_RETRYLATER && dwTickCount < 120000 ? 200 : -1;

        int IOleMessageFilter.MessagePending(IntPtr hTaskCallee, int dwTickCount, int dwPendingType) => PENDINGMSG_WAITDEFPROCESS;
    }
}
