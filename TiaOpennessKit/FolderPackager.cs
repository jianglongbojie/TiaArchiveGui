using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace TiaOpennessKit
{
    /// <summary>
    /// 打包结果统计。
    /// </summary>
    public sealed class FolderPackResult
    {
        /// <summary>打进包里的文件数。</summary>
        public long FileCount;

        /// <summary>源文件总字节数。</summary>
        public long TotalSourceBytes;

        /// <summary>压缩包字节数。</summary>
        public long ZipBytes;

        /// <summary>读不了、被跳过的文件数。</summary>
        public int SkippedCount;

        /// <summary>被跳过的文件（最多记录若干个）。</summary>
        public List<string> SkippedFiles = new List<string>();

        /// <summary>是否发现疑似"项目正被 TIA 打开"的迹象（文件被独占）。</summary>
        public bool SourceLooksLocked;

        /// <summary>压缩率文本，如 "38%"。源为 0 时返回 "—"。</summary>
        public string CompressionRatioText
        {
            get
            {
                if (TotalSourceBytes <= 0)
                {
                    return "—";
                }

                double ratio = ZipBytes * 100.0 / TotalSourceBytes;
                return ratio.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "%";
            }
        }
    }

    /// <summary>
    /// 把 TIA 项目**文件夹**打包成 .zip。
    ///
    /// 为什么要这个东西：TIA 的归档（.zapXX）必须由匹配版本的 Openness 执行，
    /// 而"把整个项目目录打个包"是版本无关的 —— V18 的项目打包之后还是 V18，
    /// 拿 V18 的 TIA 解压出来直接就能打开，不会触发升级。
    /// 而且它**完全不需要 Openness、也不需要启动 TIA**。
    ///
    /// 实现只用 .NET Framework 自带的 System.IO.Compression，不引第三方库。
    ///
    /// ★ 关键点：必须打包**整个项目文件夹**（含 System / IM / XRef / AdditionalFiles 等
    ///   全部子目录）。TIA 项目文件 .apXX 本身只有一百多 KB，光打包它解压出来是打不开的。
    ///   包内保留一层项目文件夹名，解压后得到 "项目名\项目名.apXX"，符合 TIA 的目录习惯。
    /// </summary>
    public static class FolderPackager
    {
        /// <summary>
        /// 每处理多少个文件回调一次进度（1 = 每个文件都回调一次）。
        /// 真正的节流在调用方（界面每 15 个才刷一次状态栏），这里保持"有进展就报"。
        ///
        /// （原先这里的注释写的是"文件被独占时的最大读取尝试"，与本常量的名字和取值都不符，
        ///   照它去理解会以为打包在重试读文件 —— 实际上被占用的文件是直接跳过并记进
        ///   SkippedFiles 的，并不重试。）
        /// </summary>
        private const int ProgressReportEveryFiles = 1;

        /// <summary>
        /// 打包项目文件夹。
        /// </summary>
        /// <param name="projectFilePath">项目文件路径（如 D:\Proj\Demo\Demo.ap20），用于定位它所在的文件夹。</param>
        /// <param name="zipOutputPath">输出 zip 路径。</param>
        /// <param name="logger">日志器。</param>
        /// <param name="onProgress">进度回调（已完成文件数, 总文件数, 当前文件相对路径），可为 null。</param>
        /// <returns>打包统计。</returns>
        /// <exception cref="ToolException">项目文件/文件夹不存在，或无法创建压缩包时抛出。</exception>
        public static FolderPackResult PackProject(
            string projectFilePath,
            string zipOutputPath,
            Logger logger,
            Action<long, long, string> onProgress)
        {
            if (string.IsNullOrWhiteSpace(projectFilePath) || !File.Exists(projectFilePath))
            {
                throw new ToolException(ExitCodes.InputOutput, "项目文件不存在：" + projectFilePath);
            }

            FileInfo projectFile = new FileInfo(projectFilePath);
            string sourceDirectory = projectFile.DirectoryName;

            if (string.IsNullOrEmpty(sourceDirectory) || !Directory.Exists(sourceDirectory))
            {
                throw new ToolException(ExitCodes.InputOutput,
                    "找不到项目所在的文件夹：" + (sourceDirectory ?? "(空)"));
            }

            string outputFullPath = Path.GetFullPath(zipOutputPath);
            string projectDirectoryFullPath = Path.GetFullPath(sourceDirectory);

            logger.Info("项目文件夹：" + projectDirectoryFullPath);
            logger.Info("输出压缩包：" + outputFullPath);

            // 输出到项目文件夹内部时，必须把压缩包自己排除掉，否则会边写边读自己
            bool outputInsideSource = IsInsideDirectory(projectDirectoryFullPath, outputFullPath);
            if (outputInsideSource)
            {
                logger.Warning("压缩包输出到了项目文件夹内部，已自动把它自己排除在打包范围外。"
                    + "（建议改到项目文件夹外面，备份更干净。）");
            }

            // ── 第一步：摸清要打包的文件与空目录
            List<string> fileList = new List<string>();
            List<string> emptyDirectories = new List<string>();
            List<string> unreadableDirectories = new List<string>();

            CollectFiles(projectDirectoryFullPath, outputFullPath, outputInsideSource,
                fileList, emptyDirectories, unreadableDirectories, logger);

            if (fileList.Count == 0)
            {
                throw new ToolException(ExitCodes.InputOutput,
                    "项目文件夹里没有找到任何文件，无法打包：" + projectDirectoryFullPath);
            }

            long totalBytes = 0;
            foreach (string file in fileList)
            {
                try
                {
                    totalBytes += new FileInfo(file).Length;
                }
                catch (Exception)
                {
                    // 统计不到就不统计，不影响打包
                }
            }

            logger.Info("待打包：" + fileList.Count + " 个文件，共 " + SizeFormat.Format(totalBytes));
            if (emptyDirectories.Count > 0)
            {
                logger.Info("其中空目录 " + emptyDirectories.Count + " 个（也会写进包里，保持目录结构完整）");
            }

            // ── 第二步：写压缩包
            FolderPackResult result = new FolderPackResult();
            string baseDirectoryName = new DirectoryInfo(projectDirectoryFullPath).Name;
            long processed = 0;

            try
            {
                string outputDirectory = Path.GetDirectoryName(outputFullPath);
                if (!string.IsNullOrEmpty(outputDirectory) && !Directory.Exists(outputDirectory))
                {
                    Directory.CreateDirectory(outputDirectory);
                }

                using (FileStream fileStream = new FileStream(outputFullPath, FileMode.Create, FileAccess.Write))
                using (ZipArchive archive = new ZipArchive(fileStream, ZipArchiveMode.Create))
                {
                    // 空目录也要留个条目，否则解压后目录结构会缺
                    foreach (string directory in emptyDirectories)
                    {
                        string entryName = BuildEntryName(baseDirectoryName, projectDirectoryFullPath, directory);
                        archive.CreateEntry(entryName + "/");
                    }

                    foreach (string file in fileList)
                    {
                        string entryName = BuildEntryName(baseDirectoryName, projectDirectoryFullPath, file);
                        string relativeForDisplay = entryName.Substring(baseDirectoryName.Length + 1);

                        try
                        {
                            ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
                            entry.LastWriteTime = File.GetLastWriteTime(file);

                            using (Stream entryStream = entry.Open())
                            using (FileStream sourceStream = new FileStream(
                                file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                            {
                                sourceStream.CopyTo(entryStream);
                            }

                            result.FileCount++;
                            result.TotalSourceBytes += new FileInfo(file).Length;
                        }
                        catch (IOException ex)
                        {
                            // 文件被别的进程占着（典型就是 TIA 正开着这个项目）
                            result.SkippedCount++;
                            result.SourceLooksLocked = true;

                            if (result.SkippedFiles.Count < 20)
                            {
                                result.SkippedFiles.Add(relativeForDisplay + "（" + ex.GetType().Name + "）");
                            }

                            logger.Warning("跳过被占用的文件：" + relativeForDisplay);
                        }
                        catch (UnauthorizedAccessException ex)
                        {
                            result.SkippedCount++;
                            if (result.SkippedFiles.Count < 20)
                            {
                                result.SkippedFiles.Add(relativeForDisplay + "（无权限）");
                            }
                            logger.Warning("跳过无权限的文件：" + relativeForDisplay + "（" + ex.GetType().Name + "）");
                        }

                        processed++;

                        if (onProgress != null && (processed % ProgressReportEveryFiles) == 0)
                        {
                            onProgress(processed, fileList.Count, relativeForDisplay);
                        }
                    }
                }
            }
            catch (ToolException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new ToolException(ExitCodes.InputOutput,
                    "打包失败：" + ex.Message + "（输出：" + outputFullPath + "）", ex);
            }

            try
            {
                result.ZipBytes = new FileInfo(outputFullPath).Length;
            }
            catch (Exception)
            {
                // 取不到大小不影响结果
            }

            return result;
        }

        /// <summary>
        /// 递归收集文件、空目录、读不了的目录。
        ///
        /// 用手写队列而不是 Directory.GetFiles(AllDirectories)：后者遇到任何一个
        /// 无权限的子目录就会整体抛异常，把已经找到的结果全丢掉。
        /// </summary>
        private static void CollectFiles(
            string rootDirectory,
            string outputFullPath,
            bool outputInsideSource,
            List<string> files,
            List<string> emptyDirectories,
            List<string> unreadableDirectories,
            Logger logger)
        {
            Queue<string> pending = new Queue<string>();
            pending.Enqueue(rootDirectory);

            while (pending.Count > 0)
            {
                string current = pending.Dequeue();

                string[] subDirectories;
                try
                {
                    subDirectories = Directory.GetDirectories(current);
                }
                catch (Exception ex)
                {
                    unreadableDirectories.Add(current);
                    logger.Warning("跳过读不了的文件夹：" + current + "（" + ex.GetType().Name + "）");
                    subDirectories = new string[0];
                }

                foreach (string sub in subDirectories)
                {
                    pending.Enqueue(sub);
                }

                string[] currentFiles;
                try
                {
                    currentFiles = Directory.GetFiles(current);
                }
                catch (Exception ex)
                {
                    logger.Warning("跳过读不了的文件夹：" + current + "（" + ex.GetType().Name + "）");
                    continue;
                }

                int added = 0;
                foreach (string file in currentFiles)
                {
                    // 排除输出文件自己
                    if (outputInsideSource && string.Equals(
                            Path.GetFullPath(file), outputFullPath, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    files.Add(file);
                    added++;
                }

                if (added == 0 && subDirectories.Length == 0)
                {
                    emptyDirectories.Add(current);
                }
            }
        }

        /// <summary>
        /// 拼出压缩包内的条目名：顶层是项目文件夹名，后面接相对路径，分隔符统一用 /。
        /// </summary>
        private static string BuildEntryName(string baseDirectoryName, string rootDirectory, string fullPath)
        {
            string relative = GetRelativePath(rootDirectory, fullPath);
            return baseDirectoryName + "/" + relative.Replace('\\', '/');
        }

        /// <summary>
        /// 取相对路径。.NET Framework 没有 Path.GetRelativePath，自己实现一个够用的版本。
        /// </summary>
        private static string GetRelativePath(string baseDirectory, string fullPath)
        {
            string prefix = baseDirectory.TrimEnd('\\', '/') + "\\";

            if (fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return fullPath.Substring(prefix.Length);
            }

            return Path.GetFileName(fullPath);
        }

        /// <summary>
        /// 判断某个路径是否位于给定目录内部。
        /// </summary>
        private static bool IsInsideDirectory(string directory, string path)
        {
            string prefix = directory.TrimEnd('\\', '/') + "\\";
            return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 判断文件夹是否可以打包：项目文件能独占打开，通常意味着项目没被 TIA 占用。
        /// </summary>
        /// <param name="projectFilePath">项目文件路径。</param>
        /// <param name="message">结果说明。</param>
        /// <returns>可以打包返回 true。</returns>
        public static bool TryProbeProjectNotLocked(string projectFilePath, out string message)
        {
            message = string.Empty;

            try
            {
                using (FileStream stream = new FileStream(
                    projectFilePath, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    stream.ReadByte();
                }

                message = "项目文件当前没有被其它程序占用，看起来没有在 TIA 里打开。";
                return true;
            }
            catch (IOException)
            {
                message = "检测到项目文件正被其它程序占用（多半是这个项目已经在 TIA Portal 里打开了）。"
                    + "建议先在 TIA 里关闭项目再打包，否则可能打到不一致的内容。";
                return false;
            }
            catch (Exception ex)
            {
                message = "无法判断项目是否被占用（" + ex.GetType().Name + "）。";
                return false;
            }
        }
    }
}
