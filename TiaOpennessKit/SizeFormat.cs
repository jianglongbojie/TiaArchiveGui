using System.Globalization;

namespace TiaOpennessKit
{
    /// <summary>
    /// 字节数的可读化，例如 "12.34 MB"。
    ///
    /// 为什么单独放一个文件：这段逻辑原来在三个地方各写了一遍
    /// （<c>BatchScanner.FormatSize</c> / <c>FolderPackager.FormatSize</c> /
    /// <c>TiaSession.FormatBytes</c>），而且**精度还不一致**（KB 一处保留 1 位小数、
    /// 另一处保留 2 位），同一份日志里同一个大小可能显示成两种样子。
    /// 现在只此一份，谁要显示大小都调它。
    ///
    /// ★ 本文件只依赖 BCL、**不引用任何 Siemens 类型** —— 这一点很关键：
    ///   打包模式（FolderPackager）必须能在"程序集解析钩子还没挂上"时运行，
    ///   所以它不能去调 TiaSession 里的东西；放进这个纯 BCL 文件后两边都能用。
    /// </summary>
    public static class SizeFormat
    {
        /// <summary>单位阶梯（1024 进制）。</summary>
        private static readonly string[] Units = new string[] { "B", "KB", "MB", "GB", "TB" };

        /// <summary>
        /// 把字节数格式化成"数值 + 单位"的可读文本。
        /// </summary>
        /// <param name="bytes">字节数。</param>
        /// <returns>例如 "512 B" / "2.31 KB" / "12.34 MB" / "1.05 GB"。</returns>
        public static string Format(long bytes)
        {
            double value = bytes;
            int unitIndex = 0;

            while (value >= 1024.0 && unitIndex < Units.Length - 1)
            {
                value = value / 1024.0;
                unitIndex++;
            }

            return value.ToString("0.##", CultureInfo.InvariantCulture) + " " + Units[unitIndex];
        }
    }
}