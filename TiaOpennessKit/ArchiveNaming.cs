using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace TiaOpennessKit
{
    /// <summary>
    /// 归档产物的文件名规则：自定义后缀、日期时间戳、同名避让。
    ///
    /// 为什么需要这个类：Openness 的 Project.Archive 只有
    /// <c>Archive(DirectoryInfo targetDirectory, string targetName, ProjectArchivationMode mode)</c>
    /// 三个参数，**没有**任何"加时间戳/加后缀"的选项 —— TIA GUI 对话框里的
    /// "将日期和时间添加到目标名称中" 也是界面层自己把名字拼好之后再调底层的。
    /// 所以这件事完全由调用方负责，纯字符串处理，碰不到任何 Siemens 类型。
    ///
    /// 命名规则（后缀与时间戳都加在主名之后、扩展名之前）：
    ///   Demo_V21.zap21
    ///   Demo_V21_backup.zap21                       ← 加后缀 "_backup"
    ///   Demo_V21_backup_20260919_111915.zap21       ← 再加时间戳
    ///
    /// ★ 注意：扩展名部分绝不能改动 —— 归档产物的扩展名由 TIA 按当前主版本决定
    ///   （不压缩模式下你传 .zip，落地的可能是 .zap21）。
    /// </summary>
    public static class ArchiveNaming
    {
        /// <summary>默认时间戳格式。</summary>
        public const string DefaultTimestampFormat = "yyyyMMdd_HHmmss";

        /// <summary>
        /// 界面上可选的常用时间戳格式预设（格式字符串、中文说明、示例由调用方生成）。
        /// 想用别的格式，直接改这里即可 —— 格式字符串就是 .NET 的标准日期格式。
        /// </summary>
        public static readonly string[] TimestampPresets = new string[]
        {
            "yyyyMMdd_HHmmss",      // 20260919_111915
            "yyyyMMdd_HHmm",        // 20260919_1119
            "yyyyMMdd",             // 20260919
            "yyyy-MM-dd_HHmmss",    // 2026-09-19_111915
            "yyyy-MM-dd_HH-mm-ss",  // 2026-09-19_11-19-15
            "yyyyMMddHHmmss"        // 20260919111915
        };

        /// <summary>
        /// 把带时间的格式转成一句"长这样"的示例文本，供界面下拉框显示。
        /// </summary>
        /// <param name="format">格式字符串。</param>
        /// <param name="now">用于生成示例的时间。</param>
        /// <returns>形如 "20260919_111915（yyyyMMdd_HHmmss）"。</returns>
        public static string DescribeFormat(string format, DateTime now)
        {
            return FormatTimestamp(format, now) + "    " + format;
        }

        /// <summary>
        /// 按格式格式化时间戳。格式非法时退回默认格式（不抛异常）。
        /// </summary>
        /// <param name="format">格式字符串。</param>
        /// <param name="now">时间。</param>
        /// <returns>时间戳文本。</returns>
        public static string FormatTimestamp(string format, DateTime now)
        {
            if (string.IsNullOrWhiteSpace(format))
            {
                return string.Empty;
            }

            try
            {
                return now.ToString(format, CultureInfo.InvariantCulture);
            }
            catch (FormatException)
            {
                return now.ToString(DefaultTimestampFormat, CultureInfo.InvariantCulture);
            }
        }

        /// <summary>
        /// 拼出归档文件的新主名（不含目录）。
        /// </summary>
        /// <param name="fileName">原文件名，如 Demo_V21.zap21。</param>
        /// <param name="suffix">用户自定义后缀，可为空（如 backup 或 _backup）。</param>
        /// <param name="timestampFormat">时间戳格式；传空表示不加时间戳。</param>
        /// <param name="now">时间。</param>
        /// <returns>加工后的文件名，如 Demo_V21_backup_20260919_111915.zap21。</returns>
        public static string BuildFileName(string fileName, string suffix, string timestampFormat, DateTime now)
        {
            if (string.IsNullOrEmpty(fileName))
            {
                return fileName;
            }

            string name = Path.GetFileNameWithoutExtension(fileName);
            string extension = Path.GetExtension(fileName);

            if (name == null)
            {
                name = string.Empty;
            }

            name += NormalizeSuffix(suffix);

            if (!string.IsNullOrWhiteSpace(timestampFormat))
            {
                name += "_" + FormatTimestamp(timestampFormat, now);
            }

            return name + (extension == null ? string.Empty : extension);
        }

        /// <summary>
        /// 加工完整路径（目录不变，只改文件名）。
        /// </summary>
        /// <param name="outputPath">用户给的输出路径。</param>
        /// <param name="suffix">后缀。</param>
        /// <param name="timestampFormat">时间戳格式，空表示不加。</param>
        /// <param name="now">时间。</param>
        /// <returns>加工后的完整路径。</returns>
        public static string ApplyRule(string outputPath, string suffix, string timestampFormat, DateTime now)
        {
            if (string.IsNullOrEmpty(outputPath))
            {
                return outputPath;
            }

            string directory = Path.GetDirectoryName(outputPath);
            string fileName = BuildFileName(Path.GetFileName(outputPath), suffix, timestampFormat, now);

            return string.IsNullOrEmpty(directory) ? fileName : Path.Combine(directory, fileName);
        }

        /// <summary>
        /// 规范化后缀：去掉 Windows 文件名非法字符；若用户没写分隔符就补一个下划线。
        /// </summary>
        /// <param name="suffix">用户输入。</param>
        /// <returns>可直接拼进文件名的后缀（可能为空串）。</returns>
        public static string NormalizeSuffix(string suffix)
        {
            if (string.IsNullOrWhiteSpace(suffix))
            {
                return string.Empty;
            }

            string cleaned = suffix.Trim();

            char[] invalid = Path.GetInvalidFileNameChars();
            foreach (char c in invalid)
            {
                cleaned = cleaned.Replace(c.ToString(), string.Empty);
            }

            // 顺手去掉目录分隔符（以防上面的字符集在某些环境里不含它们）
            cleaned = cleaned.Replace("/", string.Empty).Replace("\\", string.Empty);

            if (cleaned.Length == 0)
            {
                return string.Empty;
            }

            char first = cleaned[0];
            if (first != '_' && first != '-' && first != '.')
            {
                cleaned = "_" + cleaned;
            }

            return cleaned;
        }

        /// <summary>
        /// 推导归档产物的扩展名：<c>.zap</c> + **归档时实际运行的那个 TIA 内核的主版本号**。
        ///
        /// 为什么必须显式给内核版本：**归档格式 = 归档时运行的内核版本**，与项目本身是什么版本无关
        /// （用 V21 打开 .ap18 并归档 → 产物就是 .zap21 格式）。
        /// 早期版本这里把 ".zap21" 写死了，于是在装了 V19 的机器上归档 .ap19 项目时，
        /// 文件名却叫 <c>.zap21</c>（内容其实是 V19 格式）—— 实测被用户抓到。
        /// </summary>
        /// <param name="kernelMajorVersion">归档所用内核的主版本号（如 19）；未知时传 0。</param>
        /// <param name="projectFilePath">项目文件路径，用于拿不到内核版本时的兜底。</param>
        /// <returns>形如 ".zap19"；两者都推不出来时返回空串（由调用方决定怎么办）。</returns>
        public static string BuildArchiveExtension(int kernelMajorVersion, string projectFilePath)
        {
            if (kernelMajorVersion > 0)
            {
                return ".zap" + kernelMajorVersion.ToString(CultureInfo.InvariantCulture);
            }

            // 还没探测到环境（例如界面上的"实际输出"预览是在启动 TIA 之前算的）时，
            // 退而用项目自身的版本 —— 至少不会张冠李戴地写成别的版本号。
            int fromProject = GetProjectMajorVersion(projectFilePath);
            return fromProject > 0 ? ".zap" + fromProject.ToString(CultureInfo.InvariantCulture) : string.Empty;
        }

        /// <summary>
        /// 从项目文件名里取主版本号：Demo.ap19 → 19；认不出来返回 0。
        /// </summary>
        /// <param name="projectFilePath">项目文件路径。</param>
        /// <returns>主版本号；认不出返回 0。</returns>
        public static int GetProjectMajorVersion(string projectFilePath)
        {
            if (string.IsNullOrWhiteSpace(projectFilePath))
            {
                return 0;
            }

            string extension = Path.GetExtension(projectFilePath);
            if (string.IsNullOrEmpty(extension) || extension.Length <= 3)
            {
                return 0;
            }

            // 只认 .apXX 这种（.zapXX / .txt 等一律不算）
            if (!extension.StartsWith(".ap", StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            int value;
            return int.TryParse(extension.Substring(3), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out value) ? value : 0;
        }

        /// <summary>
        /// 目标文件已存在时，找一个不冲突的名字：Demo(2).zap21、Demo(3).zap21 ……
        /// </summary>
        /// <param name="fullPath">原始路径。</param>
        /// <returns>不与现有文件冲突的路径（原路径不存在时原样返回）。</returns>
        public static string EnsureUniquePath(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath))
            {
                return fullPath;
            }

            string directory = Path.GetDirectoryName(fullPath);
            string name = Path.GetFileNameWithoutExtension(fullPath);
            string extension = Path.GetExtension(fullPath);

            for (int index = 2; index < 10000; index++)
            {
                string candidate = Path.Combine(
                    directory == null ? string.Empty : directory,
                    name + "(" + index.ToString(CultureInfo.InvariantCulture) + ")"
                        + (extension == null ? string.Empty : extension));

                if (!File.Exists(candidate))
                {
                    return candidate;
                }
            }

            // 极端情况（同名文件超过 1 万个）：退回原路径，交给上层提示
            return fullPath;
        }

        /// <summary>
        /// 批量归档场景：算出与现有文件不冲突的输出路径列表。
        /// </summary>
        /// <param name="paths">候选路径列表。</param>
        /// <returns>避让后的路径列表。</returns>
        public static List<string> EnsureUniquePaths(IEnumerable<string> paths)
        {
            List<string> result = new List<string>();

            if (paths == null)
            {
                return result;
            }

            foreach (string path in paths)
            {
                result.Add(EnsureUniquePath(path));
            }

            return result;
        }
    }
}
