using System;

namespace TiaOpennessKit
{
    /// <summary>
    /// 进程退出码常量。
    /// 类比 SCL：这就像 FB 的 ENO/返回值——上层脚本（批处理、CI）靠它判断成功或失败类别。
    /// </summary>
    public static class ExitCodes
    {
        /// <summary>成功：命令完整执行并通过。</summary>
        public const int Success = 0;

        /// <summary>用法/参数错误：命令行解析失败、缺少必填参数、取值非法。</summary>
        public const int Usage = 1;

        /// <summary>环境问题：找不到 TIA Portal PublicAPI 目录、程序集无法加载、用户不在 Openness 组等。</summary>
        public const int Environment = 2;

        /// <summary>Openness API 报错：EngineeringException / EngineeringSecurityException 等。</summary>
        public const int Api = 3;

        /// <summary>文件系统错误：文件不存在、目录不可写、路径非法等。</summary>
        public const int InputOutput = 4;

        /// <summary>
        /// 前置条件体检未通过：环境缺项（Openness 组件没装、用户不在 Openness 组等）。
        ///
        /// 与 <see cref="Usage"/>（1，命令行参数写错了）和 <see cref="Environment"/>（2，
        /// 定位不到可用的 PublicAPI 目录）区分开：脚本/CI 拿到 5 就能判断"是环境没准备好，
        /// 而不是我参数写错了"，不必再去猜。
        /// </summary>
        public const int EnvironmentCheckFailed = 5;

        /// <summary>未预期的内部错误。</summary>
        public const int Unhandled = 9;

        /// <summary>
        /// 把退出码翻译成一句中文说明，便于日志尾部统一打印。
        /// </summary>
        /// <param name="code">退出码。</param>
        /// <returns>中文说明；未知码返回 Unknown。</returns>
        public static string Describe(int code)
        {
            switch (code)
            {
                case Success: return "成功";
                case Usage: return "命令行参数错误";
                case Environment: return "TIA Portal 环境异常";
                case Api: return "Openness API 调用失败";
                case InputOutput: return "文件系统错误";
                case EnvironmentCheckFailed: return "前置条件体检未通过";
                case Unhandled: return "未预期错误";
                default: return "未知退出码 " + code;
            }
        }
    }

    /// <summary>
    /// 携带退出码的受控异常。程序中所有"我知道该怎么解释"的错误都包装成它，
    /// 由 Program.Main 统一翻译成中文提示 + 对应退出码。
    /// 禁止吞异常：捕获后要么重抛 ToolException（含 inner），要么打印并返回码。
    /// </summary>
    public sealed class ToolException : Exception
    {
        /// <summary>应返回的进程退出码（取值来自 <see cref="ExitCodes"/>）。</summary>
        public int ExitCode { get; private set; }

        /// <summary>创建带退出码的异常。</summary>
        /// <param name="exitCode">退出码。</param>
        /// <param name="message">面向用户的中文错误信息。</param>
        public ToolException(int exitCode, string message)
            : base(message)
        {
            ExitCode = exitCode;
        }

        /// <summary>创建带退出码与内部异常的异常。</summary>
        /// <param name="exitCode">退出码。</param>
        /// <param name="message">面向用户的中文错误信息。</param>
        /// <param name="innerException">原始异常，不允许丢弃。</param>
        public ToolException(int exitCode, string message, Exception innerException)
            : base(message, innerException)
        {
            ExitCode = exitCode;
        }
    }
}
