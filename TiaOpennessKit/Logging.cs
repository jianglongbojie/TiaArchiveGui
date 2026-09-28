using System;
using System.Diagnostics;
using System.Globalization;

namespace TiaOpennessKit
{
    /// <summary>
    /// 日志级别。数值越大越严重；Debug 仅在 --verbose 时输出。
    /// </summary>
    public enum LogLevel
    {
        /// <summary>调试细节（仅 --verbose 打开）。</summary>
        Debug = 0,

        /// <summary>常规过程信息。</summary>
        Info = 1,

        /// <summary>警告：可以继续，但结果可能不符合预期。</summary>
        Warning = 2,

        /// <summary>错误：操作失败。</summary>
        Error = 3,

        /// <summary>成功提示（绿色高亮）。</summary>
        Success = 4
    }

    /// <summary>
    /// 日志的输出目的地。
    ///
    /// 为什么要抽出这个接口：命令行版把日志打到控制台，图形界面版需要把同一批日志
    /// 显示到窗口里的日志框。核心逻辑（归档/检索/探测）不应该关心"日志最后去了哪里"，
    /// 所以让它只依赖这个接口 —— 命令版传控制台实现，界面版传窗口实现。
    ///
    /// 类比 SCL：这就是一个接口类型的形参。归档那个 FB 只管往接口里写，
    /// 具体接的是"控制台"还是"HMI 报警视图"，由上层组态决定。
    /// </summary>
    public interface ILogSink
    {
        /// <summary>
        /// 写一条日志。
        /// </summary>
        /// <param name="level">级别。</param>
        /// <param name="prefix">已经算好的前缀，如 "[INFO]"、"[WARN]"。</param>
        /// <param name="message">消息正文（不含前缀）。</param>
        void Write(LogLevel level, string prefix, string message);
    }

    /// <summary>
    /// 控制台日志输出：带颜色，兼容 stdout 重定向（重定向时自动降级为纯文本）。
    /// 这是命令行版的默认实现，行为与抽出接口之前完全一致。
    /// </summary>
    public sealed class ConsoleLogSink : ILogSink
    {
        private static readonly ConsoleColor[] LevelColors = new ConsoleColor[]
        {
            ConsoleColor.DarkGray,  // Debug
            ConsoleColor.Gray,      // Info
            ConsoleColor.Yellow,    // Warning
            ConsoleColor.Red,       // Error
            ConsoleColor.Green      // Success
        };

        private bool _colorEnabled;

        /// <summary>
        /// 构造控制台日志输出。颜色是否可用取决于 stdout 有没有被重定向。
        /// </summary>
        public ConsoleLogSink()
        {
            _colorEnabled = !Console.IsOutputRedirected;
        }

        /// <summary>写一条到控制台。</summary>
        /// <param name="level">级别。</param>
        /// <param name="prefix">前缀。</param>
        /// <param name="message">消息。</param>
        public void Write(LogLevel level, string prefix, string message)
        {
            if (!_colorEnabled)
            {
                Console.WriteLine(prefix + " " + message);
                return;
            }

            try
            {
                ConsoleColor previous = Console.ForegroundColor;
                Console.ForegroundColor = LevelColors[(int)level];
                Console.Write(prefix);
                Console.ForegroundColor = previous;
                Console.WriteLine(" " + message);
            }
            catch (Exception)
            {
                // 颜色不可用时永久降级，避免每次都抛异常。
                _colorEnabled = false;
                Console.WriteLine(prefix + " " + message);
            }
        }
    }

    /// <summary>
    /// 极简分级日志：支持颜色、中文编码、耗时统计。
    /// 不引入第三方 NuGet，保持依赖最小（只依赖 BCL）。
    /// 默认输出到控制台；也可以注入别的输出目的地（图形界面）。
    /// </summary>
    public sealed class Logger
    {
        private static readonly string[] LevelPrefixes = new string[]
        {
            "[DBG]", "[INFO]", "[WARN]", "[ERR ]", "[ OK ]"
        };

        private readonly bool _verbose;
        private readonly ILogSink _sink;

        /// <summary>是否输出 Debug 级别日志。</summary>
        public bool Verbose
        {
            get { return _verbose; }
        }

        /// <summary>
        /// 构造 Logger，日志输出到控制台。
        /// </summary>
        /// <param name="verbose">true 表示同时输出 Debug 级别日志。</param>
        public Logger(bool verbose)
            : this(verbose, new ConsoleLogSink())
        {
            // ★ 这里刻意**不**设置 Console.OutputEncoding = UTF8。
            // QA 实测取证：一旦强制 UTF-8，把输出重定向到文件或管道时（> log.txt、| more，
            // 这正是批处理/CI 的典型用法），宿主会按系统默认 ANSI（中文 Windows 是 GBK/936）
            // 去解码 UTF-8 字节 -> 中文乱码。
            // 沿用控制台默认编码即可：中文 Windows 的 cmd 默认代码页就是 936，
            // 我们用到的中文字符都能正确输出，重定向到文件也不会坏。
        }

        /// <summary>
        /// 构造 Logger，日志输出到指定的目的地（图形界面版走这条）。
        /// </summary>
        /// <param name="verbose">true 表示同时输出 Debug 级别日志。</param>
        /// <param name="sink">输出目的地；传 null 则退化为控制台。</param>
        public Logger(bool verbose, ILogSink sink)
        {
            _verbose = verbose;
            _sink = sink ?? new ConsoleLogSink();
        }

        /// <summary>Debug 级别日志（仅 --verbose 时输出）。</summary>
        /// <param name="message">消息。</param>
        public void Debug(string message)
        {
            if (_verbose)
            {
                Write(LogLevel.Debug, message);
            }
        }

        /// <summary>常规信息。</summary>
        /// <param name="message">消息。</param>
        public void Info(string message)
        {
            Write(LogLevel.Info, message);
        }

        /// <summary>警告。</summary>
        /// <param name="message">消息。</param>
        public void Warning(string message)
        {
            Write(LogLevel.Warning, message);
        }

        /// <summary>错误。</summary>
        /// <param name="message">消息。</param>
        public void Error(string message)
        {
            Write(LogLevel.Error, message);
        }

        /// <summary>成功标记。</summary>
        /// <param name="message">消息。</param>
        public void Ok(string message)
        {
            Write(LogLevel.Success, message);
        }

        /// <summary>
        /// 打印一个分节标题，便于把长日志切成可读段落。
        /// </summary>
        /// <param name="title">分节标题。</param>
        public void Section(string title)
        {
            Info(string.Empty);
            Info("──── " + title + " ────");
        }

        /// <summary>
        /// 开始计时，并立即打印一行"开始"。
        /// 返回值配合 <c>using</c> 使用：离开作用域自动打印耗时。
        /// 类比 SCL：就像在一对 S_ODT / 定时器前后打时间戳。
        /// </summary>
        /// <param name="label">阶段名称。</param>
        /// <returns>实现了 IDisposable 的计时块。</returns>
        public IDisposable Measure(string label)
        {
            return new TimingScope(this, label);
        }

        private void Write(LogLevel level, string message)
        {
            _sink.Write(level, LevelPrefixes[(int)level], message);
        }

        /// <summary>
        /// 耗时统计块。Dispose 时打印毫秒与秒。
        /// </summary>
        private sealed class TimingScope : IDisposable
        {
            private readonly Logger _owner;
            private readonly string _label;
            private readonly Stopwatch _stopwatch;
            private bool _disposed;

            /// <summary>构造并开始计时。</summary>
            /// <param name="owner">日志器。</param>
            /// <param name="label">阶段名称。</param>
            public TimingScope(Logger owner, string label)
            {
                _owner = owner;
                _label = label;
                _stopwatch = Stopwatch.StartNew();
                _disposed = false;
                _owner.Info("→ " + _label + " ...");
            }

            /// <summary>停止计时并打印耗时。</summary>
            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }
                _disposed = true;
                _stopwatch.Stop();
                double seconds = _stopwatch.Elapsed.TotalSeconds;
                string elapsed = seconds.ToString("0.###", CultureInfo.InvariantCulture) + " 秒";
                _owner.Info("← " + _label + " 完成，耗时 " + elapsed);
            }
        }
    }
}
