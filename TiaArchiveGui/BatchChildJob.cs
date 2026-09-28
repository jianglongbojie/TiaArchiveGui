using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace TiaArchiveGui
{
    /// <summary>
    /// "按项目版本自动选内核"用的**子进程任务文件**。
    ///
    /// 为什么需要子进程：一个进程同一时刻只能绑定**一个**版本的 Siemens.Engineering 程序集
    /// （Openness 的硬约束），所以一批里混着 .ap16 和 .ap19 时，没法在同一进程里换内核。
    /// 父进程（界面）按项目版本分组，每组拉起一个本程序的子进程、各自绑定匹配的 API 目录，
    /// 这样才能**原生打开**旧版本项目（不升级），产物也保持原版本（.ap16 → .zap16）。
    ///
    /// 文件格式刻意做成"人能看懂、能手改"的纯文本：
    /// <code>
    /// # TiaArchiveGui 子进程任务（不要手改分隔符）
    /// apdir=C:\Program Files\Siemens\Automation\Portal V19\PublicAPI\V16
    /// mode=compressed
    /// savefirst=0
    /// upgrade=0
    /// overwrite=0
    /// verbose=0
    /// item=D:\Proj\A\A.ap16|D:\Bak\A_20260919.zap16
    /// </code>
    /// 结果由子进程写到 <see cref="ResultPath"/>（每行：项目路径 \t 是否成功 \t 字节数 \t 说明）。
    /// </summary>
    internal sealed class BatchChildJob
    {
        /// <summary>内核的 API 目录（子进程用它来探测 + 绑定）。</summary>
        public string ApiDirectory = string.Empty;

        /// <summary>归档模式关键字（compressed / none / …）。</summary>
        public string ModeKeyword = "compressed";

        /// <summary>每个项目归档前先保存。</summary>
        public bool SaveFirst;

        /// <summary>旧版本项目升级打开（原生匹配内核时通常为 false）。</summary>
        public bool Upgrade;

        /// <summary>
        /// 目标已存在时是否覆盖（V21 实测 TIA 的归档**不会**覆盖已存在的目标，
        /// 覆盖由工具先改名备份再归档实现，见 ArchiveService.MoveExistingTargetAside）。
        /// </summary>
        public bool OverwriteExisting;

        /// <summary>详细日志。</summary>
        public bool Verbose;

        /// <summary>结果文件路径。</summary>
        public string ResultPath = string.Empty;

        /// <summary>要处理的项目（项目路径 + 归档输出路径）。</summary>
        public readonly List<BatchChildItem> Items = new List<BatchChildItem>();

        /// <summary>把任务写进文件（UTF-8）。</summary>
        /// <param name="path">目标文件。</param>
        public void Save(string path)
        {
            StringBuilder builder = new StringBuilder();
            builder.AppendLine("# TiaArchiveGui 子进程任务文件（由界面自动生成）");
            builder.AppendLine("apdir=" + ApiDirectory);
            builder.AppendLine("mode=" + ModeKeyword);
            builder.AppendLine("savefirst=" + (SaveFirst ? "1" : "0"));
            builder.AppendLine("upgrade=" + (Upgrade ? "1" : "0"));
            builder.AppendLine("overwrite=" + (OverwriteExisting ? "1" : "0"));
            builder.AppendLine("verbose=" + (Verbose ? "1" : "0"));
            builder.AppendLine("result=" + ResultPath);
            foreach (BatchChildItem item in Items)
            {
                builder.AppendLine("item=" + item.ProjectPath + "|" + item.OutputPath);
            }

            File.WriteAllText(path, builder.ToString(), new UTF8Encoding(true));
        }

        /// <summary>读取任务文件。</summary>
        /// <param name="path">任务文件路径。</param>
        /// <returns>任务对象。</returns>
        /// <exception cref="ToolException">文件格式不对时抛出。</exception>
        public static BatchChildJob Load(string path)
        {
            if (!File.Exists(path))
            {
                throw new TiaOpennessKit.ToolException(
                    TiaOpennessKit.ExitCodes.Usage, "任务文件不存在：" + path);
            }

            BatchChildJob job = new BatchChildJob();

            foreach (string rawLine in File.ReadAllLines(path, Encoding.UTF8))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                int split = line.IndexOf('=');
                if (split <= 0)
                {
                    continue;
                }

                string key = line.Substring(0, split).Trim().ToLowerInvariant();
                string value = line.Substring(split + 1);

                switch (key)
                {
                    case "apdir":
                        job.ApiDirectory = value.Trim();
                        break;
                    case "mode":
                        job.ModeKeyword = value.Trim();
                        break;
                    case "savefirst":
                        job.SaveFirst = value.Trim() == "1";
                        break;
                    case "upgrade":
                        job.Upgrade = value.Trim() == "1";
                        break;
                    case "overwrite":
                        job.OverwriteExisting = value.Trim() == "1";
                        break;
                    case "verbose":
                        job.Verbose = value.Trim() == "1";
                        break;
                    case "result":
                        job.ResultPath = value.Trim();
                        break;
                    case "item":
                        int bar = value.IndexOf('|');
                        if (bar > 0)
                        {
                            BatchChildItem item = new BatchChildItem();
                            item.ProjectPath = value.Substring(0, bar).Trim();
                            item.OutputPath = value.Substring(bar + 1).Trim();
                            job.Items.Add(item);
                        }
                        break;
                }
            }

            if (job.Items.Count == 0)
            {
                throw new TiaOpennessKit.ToolException(
                    TiaOpennessKit.ExitCodes.Usage, "任务文件里没有任何 item= 行：" + path);
            }

            return job;
        }

        /// <summary>把子进程的结果写成一行（供 <see cref="AppendResult"/> 使用）。</summary>
        /// <param name="resultPath">结果文件。</param>
        /// <param name="projectPath">项目路径。</param>
        /// <param name="success">是否成功。</param>
        /// <param name="bytes">产物字节数。</param>
        /// <param name="message">失败说明（成功时为空）。</param>
        public static void AppendResult(
            string resultPath, string projectPath, bool success, long bytes, string message)
        {
            if (string.IsNullOrEmpty(resultPath))
            {
                return;
            }

            string safeMessage = (message ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
            string line = projectPath + "\t" + (success ? "1" : "0") + "\t"
                + bytes.ToString(CultureInfo.InvariantCulture) + "\t" + safeMessage;

            try
            {
                File.AppendAllText(resultPath, line + Environment.NewLine, new UTF8Encoding(false));
            }
            catch (Exception)
            {
                // 结果写不出去不影响归档本身：父进程会因为缺行判为失败并给出提示
            }
        }
    }

    /// <summary>子进程任务里的一个项目。</summary>
    internal sealed class BatchChildItem
    {
        /// <summary>项目文件路径。</summary>
        public string ProjectPath = string.Empty;

        /// <summary>归档输出路径。</summary>
        public string OutputPath = string.Empty;
    }
}
