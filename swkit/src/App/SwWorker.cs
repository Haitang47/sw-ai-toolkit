using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using SwKit.Core;

namespace SwKit.App
{
    /// <summary>
    /// 专门和 SolidWorks 打交道的后台线程（STA）。界面线程从不直接调用 SolidWorks，所以导出时界面始终流畅。
    /// 后台 SolidWorks 在两次导出之间保持运行，空闲 10 分钟后自动关闭；程序退出时一定关闭。
    /// </summary>
    sealed class SwWorker : IDisposable
    {
        public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(10);

        readonly BlockingCollection<Action> _queue = new BlockingCollection<Action>();
        readonly Thread _thread;
        SwSession _session;
        DateTime _idleSince = DateTime.MaxValue;

        /// <summary>后台 SolidWorks 状态变化。参数为 null 表示没有后台 SolidWorks 在运行。在后台线程上触发。</summary>
        public event Action<string> StatusChanged;

        public SwWorker()
        {
            _thread = new Thread(Loop) { IsBackground = true, Name = "SolidWorks" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        void Loop()
        {
            ComMessageFilter.Register();
            try
            {
                while (!_queue.IsCompleted)
                {
                    Action action;
                    try
                    {
                        if (!_queue.TryTake(out action, TimeSpan.FromSeconds(10)))
                        {
                            if (_session != null && DateTime.Now - _idleSince > IdleTimeout) CloseCore();
                            continue;
                        }
                    }
                    catch (InvalidOperationException) { break; }
                    action();
                }
            }
            finally
            {
                CloseCore();
                ComMessageFilter.Revoke();
            }
        }

        /// <summary>在后台线程上执行一段工作。</summary>
        public Task<T> Run<T>(Func<T> work)
        {
            var tcs = new TaskCompletionSource<T>();
            _queue.Add(() =>
            {
                try { tcs.SetResult(work()); }
                catch (Exception ex) { tcs.SetException(ex); }
            });
            return tcs.Task;
        }

        /// <summary>（后台线程上调用）取得可用的后台 SolidWorks：已有且还活着就复用，否则新启动一个。</summary>
        public SwSession Acquire(Action<string> log, CancellationToken ct)
        {
            if (_session != null)
            {
                bool alive;
                try { _session.App.RevisionNumber(); alive = true; }
                catch { alive = false; }
                if (alive)
                {
                    _idleSince = DateTime.MaxValue;
                    log?.Invoke($"使用已在后台运行的 {_session.DisplayName}");
                    Notify();
                    return _session;
                }
                CloseCore();
            }
            _session = SwSession.Connect(SwMode.Isolated, false, log, ct);
            _session.ExitOnDispose = true;
            _idleSince = DateTime.MaxValue;
            Notify();
            return _session;
        }

        /// <summary>（后台线程上调用）一次导出结束，开始计算空闲时间。</summary>
        public void Release()
        {
            _idleSince = DateTime.Now;
            Notify();
        }

        public void CloseSession() => _queue.Add(CloseCore);

        bool _prewarming;
        /// <summary>
        /// 提前在后台启动 SolidWorks（用户刚把文件拖进来、还在选格式的时候），真正开始导出时就不用再等 20–60 秒。
        /// 已经在运行或正在启动时什么也不做；启动完进入待命，按正常的空闲时间自动关闭。
        /// </summary>
        public void Prewarm(Action<string> log = null)
        {
            if (_session != null || _prewarming || _queue.Count > 0) return;
            _prewarming = true;
            _queue.Add(() =>
            {
                try { if (_session == null) { Acquire(log, CancellationToken.None); Release(); } }
                catch { }
                finally { _prewarming = false; }
            });
        }

        void CloseCore()
        {
            if (_session == null) return;
            try { _session.Dispose(); } catch { }
            _session = null;
            Notify();
        }

        void Notify()
        {
            var s = _session;
            string text = null;
            if (s != null)
                text = _idleSince == DateTime.MaxValue
                    ? $"后台 {s.DisplayName} 正在工作"
                    : $"后台 {s.DisplayName} 待命中，空闲 {IdleTimeout.TotalMinutes:0} 分钟后自动关闭";
            StatusChanged?.Invoke(text);
        }

        public void Dispose()
        {
            try
            {
                _queue.Add(CloseCore);
                _queue.CompleteAdding();
            }
            catch (InvalidOperationException) { }
            _thread.Join(TimeSpan.FromSeconds(45));
        }
    }
}
