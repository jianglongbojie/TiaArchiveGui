using System;
using System.Collections.Generic;
using System.IO;
using TiaOpennessKit;

namespace TiaOpennessKit
{
    /// <summary>
    /// 扫描到的一个 TIA 项目文件。
    /// </summary>
    internal sealed class TiaProjectEntry
    {
        /// <summary>项目文件的完整路径。</summary>
        public string ProjectPath;

        /// <summary>用户是否勾选了它（由界面维护）。</summary>
        public bool IsSelected;

        /// <summary>文件名，如 Demo_V21.ap21。</summary>
        public string FileName;

        /// <summary>所在文件夹。</summary>
        public string DirectoryName;

        /// <summary>版本后缀，如 ap21 -> V21。</summary>
        public string VersionText;

        /// <summary>主版本号（.ap19 → 19）；认不出为 0。用于和"当前内核版本"比对。</summary>
        public int MajorVersion;

        /// <summary>文件大小（字节）。</summary>
        public long SizeBytes;

        /// <summary>最后修改时间。</summary>
        public DateTime ModifiedTime;

        /// <summary>供列表显示的大小文本。</summary>
        public string SizeText;

        /// <summary>供列表显示的修改时间文本。</summary>
        public string TimeText;

        /// <summary>
        /// 由 FileInfo 构造。
        /// </summary>
        /// <param name="file">项目文件。</param>
        public TiaProjectEntry(FileInfo file)
        {
            ProjectPath = file.FullName;
            FileName = file.Name;
            DirectoryName = file.DirectoryName;
            SizeBytes = file.Length;
            ModifiedTime = file.LastWriteTime;
            SizeText = SizeFormat.Format(file.Length);
            TimeText = file.LastWriteTime.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);

            string extension = file.Extension;          // ".ap21"
            VersionText = extension.Length > 3
                ? "V" + extension.Substring(3)
                : string.Empty;
            MajorVersion = ArchiveNaming.GetProjectMajorVersion(file.Name);
        }
    }

    /// <summary>
    /// 批量归档用的项目文件扫描器。
    ///
    /// 只做文件系统操作，不引用任何 Siemens 类型 —— 所以它可以独立测试，
    /// 也不会踩到"程序集解析钩子还没挂上"的那个坑。
    /// </summary>
    internal static class BatchScanner
    {
        /// <summary>
        /// 判断一个文件是不是 TIA Portal 项目文件。
        ///
        /// 规则：扩展名形如 .ap + 纯数字（.ap13 ~ .ap21 等），且数字在合理区间内。
        /// 这样能自动排除 .zap21（归档产物）、.ap_bak（备份）、.txt 等等。
        /// </summary>
        /// <param name="path">文件路径。</param>
        /// <returns>是项目文件返回 true。</returns>
        public static bool IsTiaProjectFile(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            string extension;
            try
            {
                extension = Path.GetExtension(path);
            }
            catch (Exception)
            {
                return false;
            }

            if (extension == null || extension.Length < 4)
            {
                return false;
            }

            if (!extension.StartsWith(".ap", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string digits = extension.Substring(3);
            foreach (char c in digits)
            {
                if (!char.IsDigit(c))
                {
                    return false;
                }
            }

            int version;
            if (!int.TryParse(digits, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out version))
            {
                return false;
            }

            // V11 是 TIA Portal 的第一个版本，上限放宽到 30 以避免将来版本被判错
            return version >= 11 && version <= 30;
        }

        /// <summary>
        /// 扫描文件夹，找出其中的 TIA 项目文件。
        ///
        /// 用自己维护的队列做广度优先遍历，而不是 Directory.GetFiles(..., AllDirectories)：
        /// 后者遇到**任何一个**无权限的子目录就会整个抛异常，把已经找到的结果全丢掉。
        /// 这里改成逐个目录 try/catch，跳过读不了的目录即可。
        /// </summary>
        /// <param name="folder">要扫描的文件夹。</param>
        /// <param name="recursive">是否包含子文件夹。</param>
        /// <param name="logger">日志器。</param>
        /// <returns>找到的项目文件列表（按路径排序）。</returns>
        public static List<TiaProjectEntry> Scan(
            string folder,
            bool recursive,
            bool ignoreBackupDirectories,
            Logger logger)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                throw new ToolException(ExitCodes.InputOutput, "文件夹不存在或不可访问：" + folder);
            }

            logger.Info("扫描文件夹：" + folder);
            logger.Info("包含子文件夹：" + (recursive ? "是" : "否"));
            logger.Info("忽略 TIA 自动备份目录（*.backup）：" + (ignoreBackupDirectories ? "是" : "否"));

            List<TiaProjectEntry> result = new List<TiaProjectEntry>();
            Queue<string> pending = new Queue<string>();
            pending.Enqueue(folder);

            int scannedDirectories = 0;
            int skippedDirectories = 0;
            int skippedBackupDirectories = 0;

            while (pending.Count > 0)
            {
                string current = pending.Dequeue();
                scannedDirectories++;

                string[] files;
                try
                {
                    files = Directory.GetFiles(current);
                }
                catch (Exception ex)
                {
                    skippedDirectories++;
                    logger.Debug("跳过无法读取的文件夹：" + current + "（" + ex.GetType().Name + "）");
                    files = new string[0];
                }

                foreach (string file in files)
                {
                    if (!IsTiaProjectFile(file))
                    {
                        continue;
                    }

                    try
                    {
                        result.Add(new TiaProjectEntry(new FileInfo(file)));
                    }
                    catch (Exception ex)
                    {
                        logger.Debug("跳过无法读取的项目文件：" + file + "（" + ex.GetType().Name + "）");
                    }
                }

                if (!recursive)
                {
                    continue;
                }

                string[] subDirectories;
                try
                {
                    subDirectories = Directory.GetDirectories(current);
                }
                catch (Exception)
                {
                    subDirectories = new string[0];
                }

                foreach (string sub in subDirectories)
                {
                    // TIA 的自动备份目录（形如 项目名.backup）里是按时间戳分的历史快照，
                    // 不是可直接打开的项目，批量归档时通常不想把它们算进来。
                    if (ignoreBackupDirectories && IsTiaBackupDirectory(sub))
                    {
                        skippedBackupDirectories++;
                        continue;
                    }

                    pending.Enqueue(sub);
                }
            }

            result.Sort(delegate (TiaProjectEntry a, TiaProjectEntry b)
            {
                return string.Compare(a.ProjectPath, b.ProjectPath, StringComparison.OrdinalIgnoreCase);
            });

            logger.Ok("扫描完成：遍历 " + scannedDirectories + " 个文件夹，找到 " + result.Count + " 个 TIA 项目文件"
                + (skippedDirectories > 0 ? "（跳过 " + skippedDirectories + " 个无权限文件夹）" : string.Empty)
                + (skippedBackupDirectories > 0 ? "（跳过 " + skippedBackupDirectories + " 个 *.backup 备份目录）" : string.Empty));

            return result;
        }

        /// <summary>
        /// 判断目录是不是 TIA 自动生成的项目备份目录（形如 "项目名.backup"）。
        ///
        /// TIA 在保存项目时会自动建立这种目录，里面按时间戳分子目录存放历史快照。
        /// 它们不是可以直接打开的项目，批量扫描时一般希望排除掉。
        /// </summary>
        /// <param name="directoryPath">目录路径或目录名。</param>
        /// <returns>是备份目录返回 true。</returns>
        public static bool IsTiaBackupDirectory(string directoryPath)
        {
            if (string.IsNullOrEmpty(directoryPath))
            {
                return false;
            }

            string name = Path.GetFileName(directoryPath.TrimEnd('\\', '/'));
            return name.EndsWith(".backup", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 为一个项目文件推导归档输出路径：输出目录 + 项目名 + <c>.zap&lt;内核版本&gt;</c>。
        ///
        /// ★ 扩展名必须按**实际做归档的那个内核版本**来取（19 内核 → .zap19），
        /// 不能写死 .zap21，也不能想当然地按项目自己的版本 —— 归档格式由内核决定：
        /// 用 V21 内核打开 .ap19 并归档，产物就是 V21 格式的 .zap21。
        /// （写死 .zap21 的旧实现在 V19 机器上会把 V19 格式的归档命名成 .zap21，实测被用户抓到。）
        /// </summary>
        /// <param name="outputDirectory">归档输出目录。</param>
        /// <param name="projectPath">项目文件路径。</param>
        /// <param name="kernelMajorVersion">归档所用内核主版本号（如 19）；未知传 0。</param>
        /// <returns>归档文件完整路径。</returns>
        public static string BuildArchivePath(string outputDirectory, string projectPath, int kernelMajorVersion)
        {
            string name = Path.GetFileNameWithoutExtension(projectPath);
            string extension = ArchiveNaming.BuildArchiveExtension(kernelMajorVersion, projectPath);
            return Path.Combine(outputDirectory, name + extension);
        }
    }
}
