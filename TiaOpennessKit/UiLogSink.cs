using System;
using System.Diagnostics;
using System.Windows.Forms;
using TiaOpennessKit;

namespace TiaOpennessKit
{
    /// <summary>
    /// 把核心逻辑产生的日志转发到窗口的日志框。
    ///
    /// 关键点：核心逻辑跑在后台线程上，而 WinForms 控件只能由创建它的 UI 线程访问。
    /// 所以这里用 BeginInvoke 把"往日志框追加一行"这件事丢回 UI 线程去做
    /// （BeginInvoke 是异步投递，不阻塞后台线程，也不会和 UI 线程死锁）。
    ///
    /// 类比 SCL：相当于后台通信任务把数据写进一个缓冲区，再由画面刷新 OB 去读取，
    /// 而不是让通信任务直接去操作画面对象。
    /// </summary>
    internal sealed class UiLogSink : ILogSink
    {
        private readonly Control _owner;
        private readonly Action<LogLevel, string, string> _handler;
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

        /// <summary>
        /// 构造转发器。
        /// </summary>
        /// <param name="owner">用于投递到 UI 线程的控件（通常是主窗体）。</param>
        /// <param name="handler">实际追加日志的委托，运行在 UI 线程上。</param>
        public UiLogSink(Control owner, Action<LogLevel, string, string> handler)
        {
            _owner = owner;
            _handler = handler;
        }

        /// <summary>写一条日志到界面。</summary>
        /// <param name="level">级别。</param>
        /// <param name="prefix">前缀。</param>
        /// <param name="message">消息。</param>
        public void Write(LogLevel level, string prefix, string message)
        {
            string text = prefix + " " + message;

            // 窗体已释放或句柄还没创建时直接丢弃，避免抛 ObjectDisposedException。
            if (_owner == null || _owner.IsDisposed || !_owner.IsHandleCreated)
            {
                Debug.WriteLine(text);
                return;
            }

            try
            {
                _owner.BeginInvoke(_handler, level, prefix, message);
            }
            catch (Exception)
            {
                // 窗体正在关闭时 BeginInvoke 可能失败，此时丢弃日志即可。
                Debug.WriteLine(text);
            }
        }
    }
}
