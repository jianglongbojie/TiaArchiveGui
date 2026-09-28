using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using TiaOpennessKit;
using TiaOpennessKit.Cli;
using TiaOpennessKit.Tia;

namespace TiaArchiveGui
{
    /// <summary>
    /// 主窗口的行为部分：收集输入 → 校验 → 丢到后台线程 → 显示结果。
    ///
    /// ★ 本文件刻意不引用任何 Siemens 类型。所有触碰 Openness 的代码都在 CoreRunner 里，
    ///   由 Execute* 方法在挂好程序集解析钩子之后再去调用（详见 CoreRunner 的类注释）。
    /// </summary>
    internal partial class MainForm
    {
        // ═══════════════════════════════════════════════════════════════════
        //  归档
        // ═══════════════════════════════════════════════════════════════════

        private void OnStartArchive(object sender, EventArgs e)
        {
            if (_busy)
            {
                return;
            }

            string projectPath = _txtProject.Text.Trim();
            string outputPath = _txtArchiveOut.Text.Trim();

            if (projectPath.Length == 0)
            {
                Warn("请先选择要归档的项目文件（.ap21）。");
                return;
            }

            if (!File.Exists(projectPath))
            {
                Warn("项目文件不存在：\r\n" + projectPath);
                return;
            }

            if (outputPath.Length == 0)
            {
                outputPath = SuggestArchivePath(projectPath);
                _txtArchiveOut.Text = outputPath;
            }

            ModeItem mode = _cmbMode.SelectedItem as ModeItem;
            if (mode == null)
            {
                Warn("请选择归档模式。");
                return;
            }

            bool packMode = mode.IsFolderPack;

            // ── 应用文件名规则（自定义后缀 + 日期时间戳）
            string ruleSuffix;
            string ruleTimestampFormat;
            ReadNamingRule(out ruleSuffix, out ruleTimestampFormat);

            DateTime stampTime = DateTime.Now;
            string finalOutputPath = ArchiveNaming.ApplyRule(
                outputPath, ruleSuffix, ruleTimestampFormat, stampTime);

            // 打包模式产出的是 .zip，不是 TIA 的 .zapXX
            if (packMode)
            {
                finalOutputPath = Path.ChangeExtension(finalOutputPath, ".zip");
            }

            // ── 同名处理：交给用户决定，不擅自覆盖历史备份
            //    ★ "覆盖"的意图要传给内核：TIA 自己不会覆盖已存在的目标（V21 实测报 is already exist），
            //      由 ArchiveService 先把旧文件改名备份、归档成功后删除备份、失败则恢复。
            bool overwriteExisting = false;
            if (File.Exists(finalOutputPath))
            {
                string alternative = ArchiveNaming.EnsureUniquePath(finalOutputPath);

                // 额外提醒：目标名与当前项目名对不上 —— 多半是上一次归档留下的产物，
                // 直接点"覆盖"就会把上一个项目的归档结果冲掉（实测踩过这个坑）。
                string mismatchHint = string.Empty;
                string projectStem = Path.GetFileNameWithoutExtension(_txtProject.Text);
                if (!string.IsNullOrEmpty(projectStem)
                    && !Path.GetFileName(finalOutputPath).StartsWith(projectStem, StringComparison.OrdinalIgnoreCase))
                {
                    mismatchHint = "\r\n\r\n⚠ 注意：这个名字与当前项目（" + projectStem
                        + "）不一致，很可能是**上一次归档**留下的产物。\r\n"
                        + "   建议选【否】或【取消】，别把它覆盖掉。";
                }

                DialogResult sameName = MessageBox.Show(
                    "目标文件已存在：\r\n\r\n    " + Path.GetFileName(finalOutputPath)
                    + mismatchHint
                    + "\r\n\r\n【是】覆盖它\r\n"
                    + "【否】自动加序号，改为保存为 " + Path.GetFileName(alternative) + "\r\n"
                    + "【取消】返回，我自己改路径或加时间戳",
                    ToolTitle,
                    MessageBoxButtons.YesNoCancel,
                    MessageBoxIcon.Question);

                if (sameName == DialogResult.Cancel)
                {
                    SetStatus("已取消（目标文件已存在）。");
                    return;
                }

                if (sameName == DialogResult.No)
                {
                    finalOutputPath = alternative;
                }
            }

            // ── 归档目标体检：不能把产物写进项目自身目录（TIA 会拒绝，V19 实测报“项目目录已存在”）。
            //    拦在启动 TIA 之前，省掉几十秒冷启动；提示里直接给出替代路径。
            //    打包（.zip）模式不经过 Openness，不受这条限制。
            if (!packMode)
            {
                try
                {
                    CoreRunner.EnsureArchiveTargetUsable(null, projectPath, finalOutputPath);
                }
                catch (ToolException ex)
                {
                    MessageBox.Show(ex.Message, ToolTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    SetStatus("已取消：归档输出不能放在项目自身目录里。");
                    return;
                }
            }

            string outputError;
            if (!TryPrepareOutputDirectory(finalOutputPath, out outputError))
            {
                Warn(outputError);
                return;
            }

            if (mode.IsDangerous && !packMode)
            {
                DialogResult confirm = MessageBox.Show(
                    "⚠ 你选择的归档模式是：\r\n\r\n    " + mode.Text + "\r\n\r\n" +
                    "该模式会**不可逆**地丢弃项目的可恢复数据（下载 / 在线比较所需的源码与符号信息）。\r\n" +
                    "归档文件一旦生成，这些数据就找不回来了。\r\n\r\n" +
                    "确定要继续吗？",
                    ToolTitle,
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);

                if (confirm != DialogResult.Yes)
                {
                    SetStatus("已取消（危险模式未确认）。");
                    return;
                }
            }

            if (!packMode && _chkUpgradeArchive.Checked)
            {
                // ★ 这里有个会破坏原项目的组合必须拦住：
                // "升级打开" + "归档前先保存" = TIA 升级后立即写盘，原项目被升级版本覆盖。
                // 而归档本身**并不需要**保存项目（归档只是读取内存中的项目并打包），
                // 所以推荐做法是取消保存，这样原项目仍保持旧版本。
                if (_chkSaveFirst.Checked)
                {
                    DialogResult conflict = MessageBox.Show(
                        "检测到一个会破坏原项目的组合：\r\n\r\n"
                        + "  · 旧版本项目升级打开（OpenWithUpgrade"
                        + (CurrentKernelMajorVersion() > 0 ? " → V" + CurrentKernelMajorVersion() : string.Empty)
                        + "）\r\n"
                        + "  · 归档前先保存项目\r\n\r\n"
                        + "两者一起用 = TIA 把项目升级后**立即保存**，原项目文件会被升级版本覆盖，"
                        + "此后再也无法用旧版本 TIA 打开它。\r\n\r\n"
                        + "小知识：归档并不需要保存项目 —— 归档只是把内存里的项目打包，"
                        + "不保存就不会改动磁盘上的原项目。\r\n\r\n"
                        + "【是】 两项都保留，接受原项目被升级覆盖\r\n"
                        + "【否】 自动取消“归档前先保存项目”，原项目保持旧版本（推荐）\r\n"
                        + "【取消】 返回，我自己调整",
                        ToolTitle,
                        MessageBoxButtons.YesNoCancel,
                        MessageBoxIcon.Warning,
                        MessageBoxDefaultButton.Button2);

                    if (conflict == DialogResult.Cancel)
                    {
                        SetStatus("已取消。");
                        return;
                    }

                    if (conflict == DialogResult.No)
                    {
                        _chkSaveFirst.Checked = false;
                        SetStatus("已自动取消“归档前先保存项目”：原项目保持旧版本不变。");
                    }
                }
                else
                {
                    DialogResult upgradeConfirm = MessageBox.Show(
                        "已勾选“旧版本项目升级打开”（OpenWithUpgrade）。\r\n\r\n"
                        + "TIA 会把该项目升级到本机安装的 TIA 主版本。\r\n"
                        + "· 归档产物是**当前内核版本**的 .zapXX（用 V19 内核就是 .zap19），不是原项目那个版本\r\n"
                        + "· 因为你没有勾“归档前先保存项目”，磁盘上的原项目不会被改动\r\n\r\n"
                        + "确定继续吗？",
                        ToolTitle,
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning,
                        MessageBoxDefaultButton.Button2);

                    if (upgradeConfirm != DialogResult.Yes)
                    {
                        SetStatus("已取消（未确认升级旧版本项目）。");
                        return;
                    }
                }
            }

            AppendLog(LogLevel.Info, "[INFO]",
                (packMode ? "最终压缩包：" : "最终归档文件：") + Path.GetFileName(finalOutputPath));

            ArchiveRequest request = new ArchiveRequest();
            request.ProjectPath = projectPath;
            request.OutputPath = finalOutputPath;
            request.ModeKeyword = mode.Keyword;
            request.IsFolderPack = packMode;
            request.SaveFirst = _chkSaveFirst.Checked;
            request.Upgrade = _chkUpgradeArchive.Checked;
            request.KeepProjectOpen = _chkKeepOpen.Checked;
            request.KeepOpenSpecified = true;
            request.OverwriteExisting = overwriteExisting;
            request.StartMode = GetStartMode(_rbArchiveNoUi, _rbArchiveUi, _rbArchiveAttach);
            request.ApiDirectory = _txtApiDir.Text.Trim();
            request.Verbose = _chkVerbose.Checked;

            SaveSettingsFromUi(true, false);

            if (packMode)
            {
                SetBusy(true, "正在打包项目文件夹…（不需要启动 TIA，通常比归档快得多）");

                Thread packWorker = new Thread(ExecutePackProject);
                packWorker.IsBackground = true;
                packWorker.Name = "PackWorker";
                packWorker.Start(request);
            }
            else
            {
                SetBusy(true, "正在归档…（TIA Portal 启动可能耗时数十秒，请耐心等待）");

                Thread worker = new Thread(ExecuteArchive);
                worker.IsBackground = true;
                worker.Name = "ArchiveWorker";
                worker.Start(request);
            }
        }

        /// <summary>
        /// 后台线程：执行一次归档。整个方法体不引用 Siemens 类型。
        /// </summary>
        /// <param name="state">ArchiveRequest。</param>
        private void ExecuteArchive(object state)
        {
            ArchiveRequest request = (ArchiveRequest)state;
            Logger logger = new Logger(request.Verbose, new UiLogSink(this, AppendLog));
            int exitCode = ExitCodes.Success;
            string productPath = null;

            try
            {
                logger.Section("归档任务开始");
                logger.Info("项目文件：" + request.ProjectPath);
                logger.Info("归档输出：" + request.OutputPath);
                logger.Info("实例方式：" + CoreRunner.DescribeStartMode(request.StartMode));

                ReportGroupMembership(logger);

                // ★ 第一步：探测环境并挂载程序集解析钩子（必须在触碰 Siemens 类型之前）
                TiaEnvironmentInfo environment = CoreRunner.DetectEnvironment(logger, request.ApiDirectory);
                logger.Ok(environment.ToString());
                ReportEnvironmentSummary(environment);
                CoreRunner.InstallResolver(environment, logger);

                // ★ 第二步：到这里才允许进入会引用 Siemens 类型的代码
                productPath = CoreRunner.Archive(logger, request);

                logger.Section("归档任务结束");
                logger.Ok("归档成功完成。");
            }
            catch (ToolException ex)
            {
                exitCode = ex.ExitCode;
                LogFailure(logger, ex);
            }
            catch (Exception ex)
            {
                exitCode = ExitCodes.Api;
                logger.Error("执行失败：" + CoreRunner.ExplainFailure(ex));
                logger.Error("异常详情：" + ex.GetType().FullName + "：" + ex.Message);
                logger.Error("调用栈：\r\n" + ex.StackTrace);
            }
            finally
            {
                FinishTask(exitCode, "归档", productPath);
            }
        }

        /// <summary>
        /// 后台线程：打包项目文件夹（"压缩包"模式）。
        ///
        /// 与 ExecuteArchive 最大的不同：**完全不碰 Openness、也不启动 TIA**。
        /// 所以它不需要探测环境、不需要挂程序集解析钩子，也没有版本匹配问题。
        /// 也正因如此，这里刻意不调用 TiaSession 之类的类型（那会把 Siemens 程序集拖进来）。
        /// </summary>
        /// <param name="state">ArchiveRequest。</param>
        private void ExecutePackProject(object state)
        {
            ArchiveRequest request = (ArchiveRequest)state;
            Logger logger = new Logger(request.Verbose, new UiLogSink(this, AppendLog));
            int exitCode = ExitCodes.Success;

            try
            {
                logger.Section("打包项目文件夹（不需要 Openness）");
                logger.Info("项目文件：" + request.ProjectPath);
                logger.Info("输出压缩包：" + request.OutputPath);
                logger.Info("压缩级别：快速");
                logger.Info("这个模式不启动 TIA Portal，也不受 Openness 版本限制 —— "
                    + "V21 的环境照样能打包 V18 项目，解压后仍是 V18，不会触发升级。");

                // 打包前探一下项目是否被别的进程占着（典型就是它正开在 TIA 里）
                string lockMessage;
                if (FolderPackager.TryProbeProjectNotLocked(request.ProjectPath, out lockMessage))
                {
                    logger.Ok(lockMessage);
                }
                else
                {
                    logger.Warning(lockMessage);
                    logger.Warning("仍会继续打包，但请自行确认打出来的内容是一致的。");
                }

                FolderPackResult result = CoreRunner.PackProjectFolder(
                    logger, request.ProjectPath, request.OutputPath, ReportPackProgress);

                logger.Section("打包结束");
                logger.Ok("打包完成：" + result.FileCount + " 个文件，"
                    + SizeFormat.Format(result.TotalSourceBytes) + " → "
                    + SizeFormat.Format(result.ZipBytes)
                    + "（压缩后为原大小的 " + result.CompressionRatioText + "）");

                if (result.SkippedCount > 0)
                {
                    logger.Warning("有 " + result.SkippedCount + " 个文件被跳过，包内可能不完整（详见上面警告）。");

                    foreach (string skipped in result.SkippedFiles)
                    {
                        logger.Warning("    · " + skipped);
                    }

                    if (result.SourceLooksLocked)
                    {
                        logger.Warning("被跳过的多半是正被占用的文件 —— 请先在 TIA 里关闭该项目再重新打包。");
                    }

                    exitCode = ExitCodes.InputOutput;
                }
                else
                {
                    logger.Ok("包内含完整项目目录，解压后可直接用 TIA 打开。");
                }
            }
            catch (ToolException ex)
            {
                exitCode = ex.ExitCode;
                LogFailure(logger, ex);
            }
            catch (Exception ex)
            {
                exitCode = ExitCodes.InputOutput;
                logger.Error("打包失败：" + ex.GetType().Name + "：" + ex.Message);
                logger.Error("调用栈：\r\n" + ex.StackTrace);
            }
            finally
            {
                FinishTask(exitCode, "打包", request.OutputPath);
            }
        }

        /// <summary>
        /// 打包进度回调（从后台线程调用）。文件多时没必要每个都刷界面。
        /// </summary>
        /// <param name="completed">已完成文件数。</param>
        /// <param name="total">总文件数。</param>
        /// <param name="currentFile">当前文件。</param>
        private void ReportPackProgress(long completed, long total, string currentFile)
        {
            if (total > 0 && completed % 15 != 0 && completed != total)
            {
                return;
            }

            string name = currentFile ?? string.Empty;
            if (name.Length > 60)
            {
                name = "…" + name.Substring(name.Length - 58);
            }

            UiInvoke(delegate
            {
                SetStatus("打包中：" + completed + " / " + total + " 个文件 —— " + name);
            });
        }

        // ═══════════════════════════════════════════════════════════════════
        //  恢复
        // ═══════════════════════════════════════════════════════════════════

        private void OnStartRetrieve(object sender, EventArgs e)
        {
            if (_busy)
            {
                return;
            }

            string archiveFile = _txtArchiveFile.Text.Trim();
            string targetDirectory = _txtRetrieveTarget.Text.Trim();

            if (archiveFile.Length == 0)
            {
                Warn("请先选择要恢复的归档文件（.zapXX）。");
                return;
            }

            if (!File.Exists(archiveFile))
            {
                Warn("归档文件不存在：\r\n" + archiveFile);
                return;
            }

            if (targetDirectory.Length == 0)
            {
                Warn("请选择解包目标目录。");
                return;
            }

            try
            {
                if (!Directory.Exists(targetDirectory))
                {
                    Directory.CreateDirectory(targetDirectory);
                }

                // 目标目录非空时提醒一下：里面有同名项目会导致解包失败。
                string[] existing = Directory.GetFileSystemEntries(targetDirectory);
                if (existing.Length > 0)
                {
                    DialogResult confirm = MessageBox.Show(
                        "解包目标目录不是空的：\r\n\r\n    " + targetDirectory + "\r\n\r\n" +
                        "目录中已有 " + existing.Length + " 个文件/文件夹。若其中存在与归档同名的项目，解包会失败。\r\n\r\n" +
                        "仍然继续吗？",
                        ToolTitle,
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning,
                        MessageBoxDefaultButton.Button2);

                    if (confirm != DialogResult.Yes)
                    {
                        SetStatus("已取消。");
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                Warn("无法使用该目标目录：" + ex.Message);
                return;
            }

            RetrieveRequest request = new RetrieveRequest();
            request.ArchiveFilePath = archiveFile;
            request.TargetDirectory = targetDirectory;
            request.Upgrade = _chkUpgrade.Checked;
            request.SaveAfterRetrieve = _chkSaveAfterRetrieve.Checked;
            request.KeepProjectOpen = _chkKeepOpenRetrieve.Checked;
            request.KeepOpenSpecified = true;
            request.StartMode = GetStartMode(_rbRetrieveNoUi, _rbRetrieveUi, _rbRetrieveAttach);
            request.ApiDirectory = _txtApiDir.Text.Trim();
            request.Verbose = _chkVerbose.Checked;

            SaveSettingsFromUi(false, true);
            SetBusy(true, "正在恢复…（TIA Portal 启动可能耗时数十秒，请耐心等待）");

            Thread worker = new Thread(ExecuteRetrieve);
            worker.IsBackground = true;
            worker.Name = "RetrieveWorker";
            worker.Start(request);
        }

        /// <summary>
        /// 后台线程：执行一次恢复。
        /// </summary>
        /// <param name="state">RetrieveRequest。</param>
        private void ExecuteRetrieve(object state)
        {
            RetrieveRequest request = (RetrieveRequest)state;
            Logger logger = new Logger(request.Verbose, new UiLogSink(this, AppendLog));
            int exitCode = ExitCodes.Success;
            string projectPath = null;

            try
            {
                logger.Section("恢复任务开始");
                logger.Info("归档文件：" + request.ArchiveFilePath);
                logger.Info("解包目录：" + request.TargetDirectory);
                logger.Info("实例方式：" + CoreRunner.DescribeStartMode(request.StartMode));

                ReportGroupMembership(logger);

                TiaEnvironmentInfo environment = CoreRunner.DetectEnvironment(logger, request.ApiDirectory);
                logger.Ok(environment.ToString());
                ReportEnvironmentSummary(environment);
                CoreRunner.InstallResolver(environment, logger);

                projectPath = CoreRunner.Retrieve(logger, request);

                logger.Section("恢复任务结束");
                logger.Ok("恢复成功完成。");
            }
            catch (ToolException ex)
            {
                exitCode = ex.ExitCode;
                LogFailure(logger, ex);
            }
            catch (Exception ex)
            {
                exitCode = ExitCodes.Api;
                logger.Error("执行失败：" + CoreRunner.ExplainFailure(ex));
                logger.Error("异常详情：" + ex.GetType().FullName + "：" + ex.Message);
                logger.Error("调用栈：\r\n" + ex.StackTrace);
            }
            finally
            {
                FinishTask(exitCode, "恢复", projectPath);
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        //  环境探测
        // ═══════════════════════════════════════════════════════════════════

        private void OnStartProbe(object sender, EventArgs e)
        {
            if (_busy)
            {
                return;
            }

            ProbeRequest request = new ProbeRequest();
            request.ApiDirectory = _txtApiDir.Text.Trim();
            request.AssemblyFilter = _txtAssemblyFilter.Text.Trim();
            request.Verbose = _chkVerbose.Checked;

            SetBusy(true, "正在探测 Openness 环境…");

            Thread worker = new Thread(ExecuteProbe);
            worker.IsBackground = true;
            worker.Name = "ProbeWorker";
            worker.Start(request);
        }

        /// <summary>
        /// 后台线程：执行环境探测。不启动 TIA Portal，也不触碰 Siemens 类型（ApiProbe 走反射）。
        /// </summary>
        /// <param name="state">ProbeRequest。</param>
        private void ExecuteProbe(object state)
        {
            ProbeRequest request = (ProbeRequest)state;
            Logger logger = new Logger(request.Verbose, new UiLogSink(this, AppendLog));
            int exitCode = ExitCodes.Success;

            try
            {
                logger.Section("环境探测开始");
                ReportGroupMembership(logger);

                TiaEnvironmentInfo environment = CoreRunner.DetectEnvironment(logger, request.ApiDirectory);
                logger.Ok(environment.ToString());
                ReportEnvironmentSummary(environment);
                CoreRunner.InstallResolver(environment, logger);

                logger.Section("反射打印本机真实 API 签名");
                CoreRunner.Probe(environment, logger, request.AssemblyFilter);

                logger.Section("环境探测结束");
                logger.Ok("探测完成。上面打印的就是本机 Openness 的真实签名，照它写代码不会错。");
            }
            catch (ToolException ex)
            {
                exitCode = ex.ExitCode;
                LogFailure(logger, ex);
            }
            catch (Exception ex)
            {
                exitCode = ExitCodes.Environment;
                logger.Error("探测失败：" + CoreRunner.ExplainFailure(ex));
                logger.Error("异常详情：" + ex.GetType().FullName + "：" + ex.Message);
            }
            finally
            {
                FinishTask(exitCode, "环境探测", null);
            }
        }

        /// <summary>
        /// 探测任务的参数载体。
        /// </summary>
        private sealed class ProbeRequest
        {
            /// <summary>手工指定的 API 目录，可为空。</summary>
            public string ApiDirectory;

            /// <summary>程序集名过滤关键字，可为空。</summary>
            public string AssemblyFilter;

            /// <summary>是否输出调试日志。</summary>
            public bool Verbose;
        }

        // ═══════════════════════════════════════════════════════════════════
        //  公共辅助
        // ═══════════════════════════════════════════════════════════════════

        // ═══════════════════════════════════════════════════════════════════
        //  前置条件体检
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// 逐项检查跑 Openness 需要的前置条件，并在界面上列出结果与修复办法。
        /// 不启动 TIA、不需要管理员权限，可以随时点。
        /// </summary>
        private void OnCheckEnvironment(object sender, EventArgs e)
        {
            if (_busy)
            {
                return;
            }

            bool verbose = _chkVerbose.Checked;

            // 在 UI 线程先把"当前选中的 API 目录"取出来（体检跑在后台线程，不要跨线程读控件）。
            // 传进去的原因：体检不能比程序的实际能力更严格 —— 用户已经指定了目录并且能用，
            // 就不该再报"Openness 组件尚未安装"（实测踩过这个自相矛盾的结论）。
            string inspectedApiDirectory = _txtApiDir == null ? null : _txtApiDir.Text.Trim();

            SetBusy(true, "正在检查前置条件…");

            int failedCount = 0;

            Thread worker = new Thread(delegate ()
            {
                Logger logger = new Logger(verbose, new UiLogSink(this, AppendLog));
                try
                {
                    logger.Section("前置条件体检");
                    List<EnvironmentCheckResult> results = EnvironmentChecks.Run(logger, inspectedApiDirectory);

                    bool needGroupFix = false;
                    foreach (EnvironmentCheckResult result in results)
                    {
                        if (result.IsInfoOnly)
                        {
                            logger.Info("[须知] " + result.Name + "：" + result.Detail);
                            if (!string.IsNullOrEmpty(result.FixHint))
                            {
                                logger.Info("        " + result.FixHint);
                            }
                            continue;
                        }

                        if (result.Passed)
                        {
                            logger.Ok("[通过] " + result.Name + "：" + result.Detail);
                            continue;
                        }

                        failedCount++;
                        logger.Warning("[未通过] " + result.Name + "：" + result.Detail);
                        if (!string.IsNullOrEmpty(result.FixHint))
                        {
                            logger.Warning("        → " + result.FixHint);
                        }

                        if (result.Name.IndexOf("用户", StringComparison.Ordinal) >= 0
                            || result.Name.IndexOf("权限", StringComparison.Ordinal) >= 0)
                        {
                            needGroupFix = true;
                        }
                    }

                    // 用户组相关的问题：先尝试直接帮你加（当前是管理员就能成），
                    // 不行再生成一键修复脚本，让用户右键"以管理员身份运行"完成
                    if (needGroupFix)
                    {
                        string addMessage;
                        bool added = EnvironmentChecks.TryAddCurrentUserToGroup(out addMessage);

                        if (added)
                        {
                            logger.Ok(addMessage);
                            logger.Warning("注意：在重新登录之前，本次会话仍然过不了 Openness 的权限校验。");
                        }
                        else
                        {
                            logger.Warning(addMessage);

                            string scriptPath;
                            if (EnvironmentChecks.TryGenerateFixScript(
                                    AppDomain.CurrentDomain.BaseDirectory, out scriptPath))
                            {
                                logger.Info("已生成一键修复脚本：" + scriptPath);
                                logger.Info("用法：关闭本程序 → 右键该文件 → “以管理员身份运行” → "
                                    + "完成后**注销并重新登录**（这一步不能省）。");
                            }
                        }
                    }

                    logger.Section("体检结论");
                    if (failedCount == 0)
                    {
                        logger.Ok("前置条件全部满足，可以开始归档。");
                    }
                    else
                    {
                        logger.Warning("有 " + failedCount + " 项未通过，按上面的修复步骤处理后请重新体检。");
                    }

                    UiInvoke(delegate { ShowCheckResults(results); });
                }
                catch (Exception ex)
                {
                    logger.Error("体检过程出错：" + ex.GetType().Name + "：" + ex.Message);
                }
                finally
                {
                    SetBusy(false, failedCount == 0
                        ? "前置条件体检通过。"
                        : "前置条件有 " + failedCount + " 项未通过，详见体检结果。");
                }
            });

            worker.IsBackground = true;
            worker.Name = "EnvCheckWorker";
            worker.Start();
        }

        /// <summary>
        /// 把体检结果画到"环境探测"页的结果框里（按状态着色）。
        /// </summary>
        /// <param name="results">检查结果。</param>
        private void ShowCheckResults(List<EnvironmentCheckResult> results)
        {
            if (_rtbCheckResult == null || _rtbCheckResult.IsDisposed)
            {
                return;
            }

            _rtbCheckResult.Clear();

            int passed = 0;
            int failed = 0;
            int infoOnly = 0;

            foreach (EnvironmentCheckResult result in results)
            {
                if (result.IsInfoOnly)
                {
                    infoOnly++;
                    AppendCheckLine("[须知] " + result.Name + "：" + result.Detail, UiTheme.TextSecondary);
                    if (!string.IsNullOrEmpty(result.FixHint))
                    {
                        AppendCheckLine("        " + result.FixHint, UiTheme.TextSecondary);
                    }
                }
                else if (result.Passed)
                {
                    passed++;
                    AppendCheckLine("[通过] " + result.Name + "：" + result.Detail, UiTheme.LogSuccess);
                }
                else
                {
                    failed++;
                    AppendCheckLine("[未通过] " + result.Name + "：" + result.Detail, UiTheme.LogError);
                    if (!string.IsNullOrEmpty(result.FixHint))
                    {
                        AppendCheckLine("        → " + result.FixHint, UiTheme.LogWarning);
                    }
                }
            }

            AppendCheckLine(string.Empty, UiTheme.TextPrimary);
            AppendCheckLine(
                "通过 " + passed + " 项，未通过 " + failed + " 项，须知 " + infoOnly + " 项",
                failed == 0 ? UiTheme.LogSuccess : UiTheme.LogError);
        }

        private void AppendCheckLine(string text, Color color)
        {
            if (_rtbCheckResult == null || _rtbCheckResult.IsDisposed)
            {
                return;
            }

            _rtbCheckResult.SelectionStart = _rtbCheckResult.TextLength;
            _rtbCheckResult.SelectionLength = 0;
            _rtbCheckResult.SelectionColor = color;
            _rtbCheckResult.AppendText(text + Environment.NewLine);
            _rtbCheckResult.SelectionColor = _rtbCheckResult.ForeColor;
        }

        /// <summary>
        /// 往日志里写一条 Openness 用户组检查结果（新手最常卡在这一步）。
        /// </summary>
        private void ReportGroupMembership(Logger logger)
        {
            string message;
            bool inGroup = IsCurrentUserInOpennessGroup(out message);
            if (inGroup)
            {
                logger.Info(message);
            }
            else
            {
                logger.Warning(message);
            }
        }

        private void LogFailure(Logger logger, ToolException exception)
        {
            logger.Error(exception.Message);
            if (exception.InnerException != null)
            {
                logger.Error("内部异常：" + exception.InnerException.GetType().Name
                    + "：" + exception.InnerException.Message);
            }
            logger.Error("退出码：" + exception.ExitCode + "（" + ExitCodes.Describe(exception.ExitCode) + "）");
        }

        /// <summary>
        /// 检查输出目录是否可以写，必要时创建它。
        /// </summary>
        /// <param name="outputPath">归档输出文件路径。</param>
        /// <param name="error">失败原因。</param>
        /// <returns>true 表示可用。</returns>
        private static bool TryPrepareOutputDirectory(string outputPath, out string error)
        {
            error = null;

            try
            {
                string directory = Path.GetDirectoryName(outputPath);
                if (string.IsNullOrEmpty(directory))
                {
                    error = "归档输出路径缺少目录部分：\r\n" + outputPath;
                    return false;
                }

                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                if (File.Exists(outputPath))
                {
                    // 已存在时不阻止：Openness 会按模式覆盖。这里只在最后确认一次。
                    return true;
                }

                return true;
            }
            catch (Exception ex)
            {
                error = "归档输出目录不可用：" + ex.Message + "\r\n" + outputPath;
                return false;
            }
        }

        private void Warn(string message)
        {
            MessageBox.Show(message, ToolTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        /// <summary>
        /// 切换"忙碌"状态：禁用三个启动按钮并显示进度条。
        /// 可从任意线程调用。
        /// </summary>
        private void SetBusy(bool busy, string status)
        {
            if (IsDisposed || !IsHandleCreated)
            {
                return;
            }

            if (InvokeRequired)
            {
                try
                {
                    BeginInvoke(new Action<bool, string>(SetBusy), busy, status);
                }
                catch (Exception)
                {
                    // 窗体会话已结束，忽略。
                }
                return;
            }

            _busy = busy;
            _btnStartArchive.Enabled = !busy;
            _btnStartRetrieve.Enabled = !busy;
            _btnStartProbe.Enabled = !busy;
            if (_btnCheckEnvironment != null)
            {
                _btnCheckEnvironment.Enabled = !busy;
            }
            _cmbMode.Enabled = !busy;
            _progress.Visible = busy;
            UpdateBatchBusyState(busy);
            if (!string.IsNullOrEmpty(status))
            {
                SetStatus(status);
            }
        }

        /// <summary>
        /// 把一段代码投递到 UI 线程执行（可从任意线程调用）。
        /// 窗体已关闭或句柄尚未创建时安全忽略。
        /// </summary>
        /// <param name="action">要执行的动作。</param>
        private void UiInvoke(Action action)
        {
            if (action == null || IsDisposed || !IsHandleCreated)
            {
                return;
            }

            try
            {
                BeginInvoke(action);
            }
            catch (Exception)
            {
                // 窗体会话已结束，忽略。
            }
        }

        /// <summary>
        /// 任务结束：恢复按钮、更新状态、成功且有产物时询问是否打开所在文件夹。
        /// </summary>
        /// <param name="exitCode">退出码。</param>
        /// <param name="taskName">任务名（归档/恢复/环境探测）。</param>
        /// <param name="productPath">产物路径，可为 null。</param>
        private void FinishTask(int exitCode, string taskName, string productPath)
        {
            bool success = exitCode == ExitCodes.Success;

            SetBusy(false, success
                ? taskName + "完成。"
                : taskName + "失败：" + ExitCodes.Describe(exitCode) + "（退出码 " + exitCode + "）");

            if (!success || string.IsNullOrEmpty(productPath))
            {
                return;
            }

            if (IsDisposed || !IsHandleCreated)
            {
                return;
            }

            try
            {
                BeginInvoke(new Action<string, string>(OfferOpenFolder), taskName, productPath);
            }
            catch (Exception)
            {
                // 忽略
            }
        }

        private void OfferOpenFolder(string taskName, string productPath)
        {
            DialogResult result = MessageBox.Show(
                taskName + "成功。\r\n\r\n产物：" + productPath + "\r\n\r\n是否打开所在文件夹？",
                ToolTitle,
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Information);

            if (result != DialogResult.Yes)
            {
                return;
            }

            try
            {
                Process.Start("explorer.exe", "/select,\"" + productPath + "\"");
            }
            catch (Exception ex)
            {
                Warn("无法打开文件夹：" + ex.Message);
            }
        }

        /// <summary>
        /// 把探测到的环境信息显示到"环境探测"页（可从任意线程调用）。
        /// </summary>
        private void ReportEnvironmentSummary(TiaEnvironmentInfo environment)
        {
            if (environment == null || IsDisposed || !IsHandleCreated)
            {
                return;
            }

            string summary =
                "TIA Portal 主版本：V" + environment.MajorVersion + "\r\n" +
                "安装目录：" + environment.PortalDirectory + "\r\n" +
                "PublicAPI 目录：" + environment.ApiDirectory + "\r\n" +
                "程序集数量：" + (environment.Assemblies == null ? 0 : environment.Assemblies.Count)
                + "    探测来源：" + environment.DetectionSource;

            try
            {
                BeginInvoke(new Action<string, string>(UpdateEnvironmentUi), summary, environment.ApiDirectory);
            }
            catch (Exception)
            {
                // 忽略
            }
        }

        private void UpdateEnvironmentUi(string summary, string apiDirectory)
        {
            if (_rtbCheckResult != null && !_rtbCheckResult.IsDisposed)
            {
                AppendCheckLine(string.Empty, UiTheme.TextPrimary);
                AppendCheckLine("── 本次探测到的环境 ──", UiTheme.TextSecondary);
                AppendCheckLine(summary, UiTheme.TextPrimary);
            }

            _lblEnvironment.Text = "Openness：V" + ExtractVersionFromPath(apiDirectory);

            if (string.IsNullOrWhiteSpace(_txtApiDir.Text))
            {
                _txtApiDir.Text = apiDirectory;
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        //  文件名规则：自定义后缀 + 日期时间戳
        //
        //  为什么要在调用方做：Openness 的 Project.Archive 只有
        //  (DirectoryInfo, string targetName, ProjectArchivationMode) 三个参数，
        //  没有任何加时间戳/后缀的选项 —— TIA GUI 那个"添加日期和时间"也是界面自己拼的。
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// 读出界面上当前生效的文件名规则。
        /// </summary>
        /// <param name="suffix">自定义后缀（未启用时为空串）。</param>
        /// <param name="timestampFormat">时间戳格式（未启用时为空串）。</param>
        private void ReadNamingRule(out string suffix, out string timestampFormat)
        {
            suffix = (_chkCustomSuffix != null && _chkCustomSuffix.Checked && _txtSuffix != null)
                ? _txtSuffix.Text
                : string.Empty;

            TimestampItem item = _cmbTimestampFormat == null
                ? null
                : _cmbTimestampFormat.SelectedItem as TimestampItem;

            timestampFormat = (_chkAppendTimestamp != null && _chkAppendTimestamp.Checked && item != null)
                ? item.Format
                : string.Empty;
        }

        /// <summary>
        /// 规则控件状态变化：同步启用状态、刷新预览、并把当前规则同步给批量页提示。
        /// </summary>
        private void OnNamingRuleChanged(object sender, EventArgs e)
        {
            if (_txtSuffix != null && _chkCustomSuffix != null)
            {
                _txtSuffix.Enabled = _chkCustomSuffix.Checked;
            }

            if (_cmbTimestampFormat != null && _chkAppendTimestamp != null)
            {
                _cmbTimestampFormat.Enabled = _chkAppendTimestamp.Checked;
            }

            UpdateOutputPreview();
            UpdateBatchNamingHint();
            UpdateVersionVerdict();
        }

        /// <summary>
        /// 让"旧版本项目升级打开"这个勾选框**自己说清升到哪个版本**。
        ///
        /// 起因（用户反馈）：勾了升级打开，却不知道会被升到哪个版本、产物是什么格式。
        /// 现在文字直接带目标版本（"…（→ V21）"），悬停提示里写清三件事：
        /// 只升级内存、产物是内核格式、想保留原版本就切内核。
        /// </summary>
        private void UpdateUpgradeOptionText()
        {
            if (_chkUpgradeArchive == null || _chkUpgradeArchive.IsDisposed)
            {
                return;
            }

            int kernel = CurrentKernelMajorVersion();
            string text = kernel > 0
                ? "旧版本项目升级打开（→ V" + kernel + "）"
                : "旧版本项目升级打开";

            if (!string.Equals(_chkUpgradeArchive.Text, text, StringComparison.Ordinal))
            {
                _chkUpgradeArchive.Text = text;
            }

            if (_toolTip != null)
            {
                _toolTip.SetToolTip(_chkUpgradeArchive,
                    "旧版本项目（.ap18 / .ap19 等）必须勾这个才能被打开。\r\n"
                    + (kernel > 0
                        ? "· 会把它升级到本机内核 V" + kernel + " 打开，**归档产物也是 V" + kernel + " 格式（.zap" + kernel + "）**\r\n"
                        : "· 会把它升级到本机 TIA 内核版本打开，产物也是内核版本格式\r\n")
                    + "· 只升级**内存**：只要不勾“归档前先保存项目”，磁盘上的原项目不会被改动\r\n"
                    + "· 想保留项目原版本又要把归档留档：把“可用版本”切到项目自己的版本"
                    + "（本机装了该版本 Openness 才行），那样是原生打开、不升级、产物也是那个版本");
            }
        }

        /// <summary>
        /// 归档页的**版本提示**：项目版本与当前内核不一致时，把"会发生什么"直接写在状态栏上。
        ///
        /// 起因：用户选了 .ap20 项目，看到"归档到 …zap21"会以为是 bug —— 其实那是**对的**
        /// （归档格式由内核决定：V21 内核归档出来就是 V21 格式），但工具必须把这层因果说清楚，
        /// 否则用户既不知道为什么，也不知道怎么才能拿到 .zap20。
        /// </summary>
        private void UpdateVersionVerdict()
        {
            if (_txtProject == null || _lblOutputPreview == null)
            {
                return;
            }

            int projectVersion = ArchiveNaming.GetProjectMajorVersion(_txtProject.Text);
            int kernel = CurrentKernelMajorVersion();
            if (projectVersion <= 0)
            {
                return;
            }

            if (kernel <= 0)
            {
                SetStatus("项目是 V" + projectVersion + "；还没探测到本机 Openness 内核"
                    + "（看“环境探测”页，或在“可用版本”里选一个）。");
                return;
            }

            if (projectVersion == kernel)
            {
                SetStatus("项目 V" + projectVersion + " 与当前内核一致，可直接归档；产物为 .zap" + kernel + "。");
                return;
            }

            if (projectVersion < kernel)
            {
                bool nativeAvailable = false;
                foreach (TiaEnvironmentInfo info in _availableVersions)
                {
                    if (info != null && info.MajorVersion == projectVersion)
                    {
                        nativeAvailable = true;
                        break;
                    }
                }

                SetStatus("项目是 V" + projectVersion + "，当前内核是 V" + kernel
                    + "：归档必须勾“旧版本项目升级打开（→ V" + kernel + "）”，产物是 .zap" + kernel
                    + (nativeAvailable
                        ? "；本机装了 V" + projectVersion + " 的 Openness → 把“可用版本”切过去就能不升级地归档（产物 .zap" + projectVersion + "）。"
                        : "；想保留 V" + projectVersion + " 格式，需先给该版本补装 Openness 组件。"));
                return;
            }

            SetStatus("⚠ 项目是 V" + projectVersion + "，比当前内核（V" + kernel + "）新，这个内核打不开它："
                + "请把“可用版本”切到 V" + projectVersion + " 再归档。");
        }

        /// <summary>
        /// 刷新"实际输出"预览，让用户直接看到最终文件名。
        /// 预览用当前时间，所以秒会一直跳 —— 实际归档时以那一刻的时间为准。
        /// </summary>
        private void UpdateOutputPreview()
        {
            if (_lblOutputPreview == null || _lblOutputPreview.IsDisposed)
            {
                return;
            }

            string outputPath = _txtArchiveOut == null ? string.Empty : _txtArchiveOut.Text.Trim();
            if (outputPath.Length == 0)
            {
                _lblOutputPreview.Text = "（选择项目文件和输出路径后显示）";
                _lblOutputPreview.ForeColor = UiTheme.TextSecondary;
                return;
            }

            string suffix;
            string timestampFormat;
            ReadNamingRule(out suffix, out timestampFormat);

            try
            {
                _lblOutputPreview.Text = ArchiveNaming.ApplyRule(
                    outputPath, suffix, timestampFormat, DateTime.Now);
                _lblOutputPreview.ForeColor = UiTheme.Accent;

                // 版本不一致时在预览前面加个醒目标记并换色：
                // 名字里的 .zapXX 由**内核版本**决定，不是项目版本 ——
                // 不提示的话用户会以为是 bug（实测被问到过：.ap20 项目为什么建议 .zap21）。
                int projectVersion = ArchiveNaming.GetProjectMajorVersion(_txtProject == null
                    ? string.Empty : _txtProject.Text);
                int kernel = CurrentKernelMajorVersion();
                if (projectVersion > 0 && kernel > 0 && projectVersion != kernel)
                {
                    _lblOutputPreview.Text = "⚠ " + _lblOutputPreview.Text;
                    _lblOutputPreview.ForeColor = projectVersion > kernel
                        ? UiTheme.Danger
                        : UiTheme.Warning;
                }
            }
            catch (Exception ex)
            {
                _lblOutputPreview.Text = "路径无效：" + ex.Message;
                _lblOutputPreview.ForeColor = UiTheme.Danger;
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        //  可用 Openness 版本：枚举与选择（多版本支持）
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// 在后台枚举本机可用的 Openness 版本（装了 Openness 组件的版本才会出现）。
        /// 放后台是因为要扫描所有固定盘，不该拖慢窗口显示。
        /// </summary>
        private void StartVersionEnumeration()
        {
            Thread worker = new Thread(delegate ()
            {
                Logger logger = new Logger(false, new UiLogSink(this, AppendLog));
                try
                {
                    IList<TiaEnvironmentInfo> versions = TiaEnvironment.EnumerateInstalled(logger);
                    UiInvoke(delegate { PopulateVersionList(versions); });
                }
                catch (Exception ex)
                {
                    logger.Debug("枚举可用版本失败：" + ex.Message);
                }
            });

            worker.IsBackground = true;
            worker.Name = "VersionEnumWorker";
            worker.Start();
        }

        private void OnEnumerateVersions(object sender, EventArgs e)
        {
            if (_busy)
            {
                return;
            }

            // 这是"重新检测"按钮：用户多半是刚补装 / 卸载了 Openness 才来点的，
            // 必须先把进程内缓存清掉，否则拿到的还是第一次探测的结果（点了等于没点）。
            TiaEnvironment.InvalidateInstalledCache();

            SetStatus("正在检测本机可用的 Openness 版本…");
            StartVersionEnumeration();
        }

        /// <summary>
        /// 把枚举结果填进"可用版本"下拉。
        /// 如果用户之前手工填过 API 目录，就选中与之匹配的那一项，不覆盖用户的选择。
        /// </summary>
        /// <param name="versions">枚举到的版本列表。</param>
        private void PopulateVersionList(IList<TiaEnvironmentInfo> versions)
        {
            if (_cmbApiVersion == null || _cmbApiVersion.IsDisposed)
            {
                return;
            }

            _availableVersions = versions == null
                ? new List<TiaEnvironmentInfo>()
                : new List<TiaEnvironmentInfo>(versions);

            _suppressVersionEvents = true;
            try
            {
                _cmbApiVersion.Items.Clear();
                foreach (TiaEnvironmentInfo info in _availableVersions)
                {
                    _cmbApiVersion.Items.Add(new VersionItem(info));
                }

                if (_availableVersions.Count == 0)
                {
                    _lblEnvironment.Text = "Openness：未检测到";
                    SetStatus("没有检测到可用的 Openness。请先给对应版本的 TIA Portal 补装 Openness 组件。");
                    return;
                }

                string current = _txtApiDir.Text.Trim();
                int matchIndex = 0;
                if (current.Length > 0)
                {
                    for (int i = 0; i < _availableVersions.Count; i++)
                    {
                        if (string.Equals(_availableVersions[i].ApiDirectory, current,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            matchIndex = i;
                            break;
                        }
                    }
                }

                _cmbApiVersion.SelectedIndex = matchIndex;

                if (current.Length == 0)
                {
                    // 用户没手工指定过：自动采用版本最高的那个（与命令行版默认行为一致）
                    _txtApiDir.Text = _availableVersions[matchIndex].ApiDirectory;
                }

                _lblEnvironment.Text = "Openness：" + TiaEnvironment.DescribeVersions(_availableVersions);
                SetStatus("检测到 " + _availableVersions.Count + " 个可用的 Openness 版本："
                    + TiaEnvironment.DescribeVersions(_availableVersions));

                // 内核确定了：把"升级打开"的文字/提示刷成带目标版本的形式（"…（→ V21）"）。
                // 注意这里 _suppressVersionEvents=true，OnApiVersionSelected 不会被触发，必须显式刷。
                UpdateUpgradeOptionText();
                UpdateVersionVerdict();
            }
            finally
            {
                _suppressVersionEvents = false;
            }
        }

        /// <summary>
        /// 用户切换了版本：把该版本的 API 目录写进输入框。
        /// 之后所有归档 / 恢复 / 探测都会用这个版本。
        /// </summary>
        private void OnApiVersionSelected(object sender, EventArgs e)
        {
            if (_suppressVersionEvents)
            {
                return;
            }

            VersionItem item = _cmbApiVersion.SelectedItem as VersionItem;
            if (item == null)
            {
                return;
            }

            _txtApiDir.Text = item.Info.ApiDirectory;
            _settings.SetString("LastApiDir", item.Info.ApiDirectory);
            _settings.Save();

            AppendLog(LogLevel.Info, "[INFO]",
                "已选定 Openness 内核：V" + item.Info.MajorVersion + " → " + item.Info.ApiDirectory);
            AppendLog(LogLevel.Info, "[INFO]",
                "后续归档 / 恢复 / 探测都用这个版本（一个进程同一时刻只能用一个版本）。");

            SetStatus("已选定 V" + item.Info.MajorVersion + " 内核，归档产物会是该版本对应的格式。");

            // 批量页的"版本"列是按当前内核着色的，换了内核要重刷一遍颜色与提示
            RefreshVersionHints();

            // 归档页的"升级打开"文字里写着目标版本，也要跟着刷
            UpdateUpgradeOptionText();
            UpdateVersionVerdict();
        }

        /// <summary>
        /// "可用版本"下拉项。
        /// </summary>
        private sealed class VersionItem
        {
            /// <summary>对应的环境信息。</summary>
            public TiaEnvironmentInfo Info { get; private set; }

            /// <summary>构造。</summary>
            /// <param name="info">环境信息。</param>
            public VersionItem(TiaEnvironmentInfo info)
            {
                Info = info;
            }

            /// <summary>下拉框显示文本。</summary>
            /// <returns>形如 "V21  —  E:\...\PublicAPI\V21\net48"。</returns>
            public override string ToString()
            {
                return "V" + Info.MajorVersion + "  —  " + Info.ApiDirectory;
            }
        }

        private static string ExtractVersionFromPath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return "未知";
            }

            try
            {
                System.Text.RegularExpressions.Match match =
                    System.Text.RegularExpressions.Regex.Match(path, @"V(\d+)");
                return match.Success ? match.Groups[1].Value : "未知";
            }
            catch (Exception)
            {
                return "未知";
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        //  界面截图（供 --uicheck 生成说明文档配图）
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// 把三个页签各截一张图存到指定目录，返回生成的文件路径列表。
        ///
        /// 优先用屏幕抓取（CopyFromScreen）：窗口此时是真的显示着的，抓到的是用户
        /// 实际看到的画面。早先只用 DrawToBitmap 离屏渲染，按钮文字会被裁掉一截 ——
        /// 那是离屏渲染对按钮/文本框的已知不完整，不是界面的真实问题，容易误导排查。
        /// 屏幕抓取失败时（例如没有桌面会话）再退回 DrawToBitmap。
        /// </summary>
        /// <param name="outputDirectory">输出目录。</param>
        /// <returns>生成的 PNG 文件路径。</returns>
        internal List<string> CaptureTabScreenshots(string outputDirectory)
        {
            List<string> files = new List<string>();

            if (_tabs == null)
            {
                return files;
            }

            if (!Directory.Exists(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

            Activate();
            BringToFront();

            for (int i = 0; i < _tabs.TabPages.Count; i++)
            {
                _tabs.SelectedIndex = i;

                // 让布局、重绘、滚动都先稳定下来，再抓图。
                Application.DoEvents();
                Update();
                Application.DoEvents();

                files.Add(CaptureWindow(outputDirectory, "gui-tab" + (i + 1) + ".png"));
            }

            _tabs.SelectedIndex = 0;
            Application.DoEvents();

            // 归档页在"打包项目文件夹"模式下的样子单独来一张：
            // 这一模式下会多出一条黄色提示条，是布局最容易出问题的地方（实测被压成一条看不清），
            // 留一张图才能对比确认它真的显示完整了。
            try
            {
                string packShot = CapturePackModeScreenshot(outputDirectory);
                if (packShot != null)
                {
                    files.Add(packShot);
                }
            }
            catch (Exception)
            {
                // 截图失败不影响自检结论
            }

            // 批量页"收起设置"的样子也留一张：这是项目多时推荐的使用姿势
            try
            {
                string collapsedShot = CaptureBatchCollapsedScreenshot(outputDirectory);
                if (collapsedShot != null)
                {
                    files.Add(collapsedShot);
                }
            }
            catch (Exception)
            {
                // 同上
            }

            return files;
        }

        /// <summary>
        /// 按当前界面状态截一张整窗图。
        ///
        /// 优先屏幕抓取（CopyFromScreen）：窗口此时真的显示着，抓到的是用户实际看到的画面。
        /// 早先只用 DrawToBitmap 离屏渲染，按钮文字会被裁掉一截 —— 那是离屏渲染的已知不完整，
        /// 不是界面的真实问题，容易误导排查。屏幕抓取失败再退回 DrawToBitmap。
        /// </summary>
        private string CaptureWindow(string outputDirectory, string fileName)
        {
            string path = Path.Combine(outputDirectory, fileName);

            using (Bitmap bitmap = new Bitmap(Width, Height))
            {
                bool captured = false;

                try
                {
                    using (Graphics graphics = Graphics.FromImage(bitmap))
                    {
                        graphics.CopyFromScreen(Left, Top, 0, 0, new Size(Width, Height));
                    }
                    captured = true;
                }
                catch (Exception)
                {
                    captured = false;
                }

                if (!captured)
                {
                    DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height));
                }

                bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }

            return path;
        }

        /// <summary>
        /// 截一张"批量页 + 设置区已收起"的图（项目多时推荐这么用），然后恢复原状。
        /// </summary>
        private string CaptureBatchCollapsedScreenshot(string outputDirectory)
        {
            if (_tabs == null || _tabs.TabPages.Count < 4 || _gridBatch == null)
            {
                return null;
            }

            _tabs.SelectedIndex = 3;
            Application.DoEvents();

            SetBatchSettingsCollapsed(true);
            Application.DoEvents();
            Update();

            string path = CaptureWindow(outputDirectory, "gui-tab4-collapsed.png");

            SetBatchSettingsCollapsed(false);

            // ★ 必须把页签切回归档页再走人：自检里有一批判断依赖
            //   `_lblModeWarning.Visible`，而控件在**未被选中的页签**上时 Visible 读出来是 false
            //   （父级 TabPage 不可见），会误报"提示条没显示"。这个坑最开始就是这么踩出来的。
            _tabs.SelectedIndex = 0;
            Application.DoEvents();

            return path;
        }

        /// <summary>
        /// 把归档页切到"打包项目文件夹"模式截一张图，然后再切回原来的模式。
        /// 这样既能看到提示条，也不会把用户原本的模式设置改掉。
        /// </summary>
        private string CapturePackModeScreenshot(string outputDirectory)
        {
            if (_cmbMode == null || _tabs == null)
            {
                return null;
            }

            int originalIndex = _cmbMode.SelectedIndex;
            int packIndex = -1;

            for (int i = 0; i < _cmbMode.Items.Count; i++)
            {
                ModeItem candidate = _cmbMode.Items[i] as ModeItem;
                if (candidate != null && candidate.IsFolderPack)
                {
                    packIndex = i;
                    break;
                }
            }

            if (packIndex < 0)
            {
                return null;
            }

            _tabs.SelectedIndex = 0;
            _cmbMode.SelectedIndex = packIndex;
            Application.DoEvents();
            Update();

            // 提示条刚显示出来时，容器可能还没把行高算好，这里明确催一次布局，
            // 否则截到的是"还没长开"的中间态，看起来跟真问题一样。
            PerformLayout();
            Application.DoEvents();

            string path = CaptureWindow(outputDirectory, "gui-tab1-packmode.png");

            _cmbMode.SelectedIndex = originalIndex;
            Application.DoEvents();

            return path;
        }

        // ═══════════════════════════════════════════════════════════════════
        //  界面自检（供 --uicheck 使用）
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// 递归导出控件树与实测尺寸，用于定位布局问题（文字被裁、控件被压扁等）。
        /// </summary>
        private static void DumpControls(Control parent, int depth, StringBuilder report)
        {
            foreach (Control child in parent.Controls)
            {
                string text = child.Text;
                if (text == null)
                {
                    text = string.Empty;
                }
                if (text.Length > 26)
                {
                    text = text.Substring(0, 26) + "…";
                }
                text = text.Replace("\\r", " ").Replace("\\n", " ");

                report.AppendLine(new string(' ', depth * 2)
                    + child.GetType().Name + " [" + text + "] "
                    + child.Width + "x" + child.Height
                    + " Dock=" + child.Dock
                    + " 实际可见=" + child.Visible);

                if (child.Controls.Count > 0)
                {
                    DumpControls(child, depth + 1, report);
                }
            }
        }

        /// <summary>
        /// 量一个固定宽度的标签放不放得下它的文字。AutoSize=true 的标签不参与判定
        /// （它自己会撑开，不存在被裁的可能）。
        /// </summary>
        private static void CheckLabelFits(
            Control label, string text, List<string> problems, StringBuilder report)
        {
            if (label == null || text == null)
            {
                return;
            }

            Size size = TextRenderer.MeasureText(text, label.Font);
            int needed = size.Width + label.Padding.Horizontal + 6;
            bool ok = label.AutoSize || needed <= label.Width;

            report.AppendLine("  标签“" + Shorten(text) + "”：宽 " + label.Width
                + "px" + (label.AutoSize ? "（自动）" : string.Empty)
                + "，文字需 " + needed + "px" + (ok ? "  → 放得下" : "  ← 会被裁"));

            if (!ok)
            {
                problems.Add("标签“" + Shorten(text) + "”会被裁：宽 " + label.Width
                    + "px，需要约 " + needed + "px");
            }
        }

        /// <summary>
        /// 量一个按钮放不放得下它的文字。
        /// </summary>
        private static void CheckButtonFits(Button button, List<string> problems, StringBuilder report)
        {
            if (button == null)
            {
                return;
            }

            Size size = TextRenderer.MeasureText(button.Text, button.Font);
            int needed = size.Width + 12;   // 按钮左右内边距（Flat 样式下最小留白）
            bool ok = needed <= button.Width;

            report.AppendLine("  按钮“" + button.Text + "”：宽 " + button.Width
                + "px，文字需 " + needed + "px" + (ok ? "  → 放得下" : "  ← 会被裁"));

            if (!ok)
            {
                problems.Add("按钮“" + button.Text + "”会被裁：宽 " + button.Width
                    + "px，需要约 " + needed + "px");
            }
        }

        private static string Shorten(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            return text.Length > 16 ? text.Substring(0, 16) + "…" : text;
        }

        /// <summary>
        /// 检查窗口是否真的建起来了：控件是否齐全、交互联动是否正确。
        /// 这不是给用户看的功能，而是给"界面能不能打开"这件事一个可执行的证据 ——
        /// 光看代码是看不出某个控件忘了 new 的。
        /// </summary>
        /// <returns>中文检查报告。</returns>
        internal string UiSelfCheck()
        {
            List<string> problems = new List<string>();
            StringBuilder report = new StringBuilder();

            // ── 一、控件是否齐全
            if (_tabs == null) { problems.Add("页签控件未创建"); }
            else if (_tabs.TabPages.Count != 4) { problems.Add("页签数量不是 4，实际 " + _tabs.TabPages.Count); }

            if (_rtbLog == null) { problems.Add("运行日志框未创建"); }
            if (_cmbMode == null) { problems.Add("归档模式下拉框未创建"); }
            else if (_cmbMode.Items.Count != 5) { problems.Add("归档模式选项数量不是 5，实际 " + _cmbMode.Items.Count); }
            if (_txtProject == null) { problems.Add("项目文件输入框未创建"); }
            if (_txtArchiveOut == null) { problems.Add("归档输出输入框未创建"); }
            if (_txtArchiveFile == null) { problems.Add("归档文件输入框未创建"); }
            if (_txtRetrieveTarget == null) { problems.Add("解包目录输入框未创建"); }
            if (_txtApiDir == null) { problems.Add("API 目录输入框未创建"); }
            if (_cmbApiVersion == null) { problems.Add("“可用版本”下拉框未创建"); }
            if (_rtbCheckResult == null) { problems.Add("前置条件体检结果框未创建"); }
            if (_btnCheckEnvironment == null) { problems.Add("“检查前置条件”按钮未创建"); }
            if (_txtAssemblyFilter == null) { problems.Add("程序集过滤输入框未创建"); }
            if (_btnStartArchive == null) { problems.Add("归档按钮未创建"); }
            if (_btnStartRetrieve == null) { problems.Add("恢复按钮未创建"); }
            if (_btnStartProbe == null) { problems.Add("探测按钮未创建"); }
            if (_rbArchiveNoUi == null || _rbArchiveUi == null || _rbArchiveAttach == null)
            {
                problems.Add("归档页的 TIA 实例方式单选按钮未创建");
            }
            if (_rbRetrieveNoUi == null || _rbRetrieveUi == null || _rbRetrieveAttach == null)
            {
                problems.Add("恢复页的 TIA 实例方式单选按钮未创建");
            }
            if (_lblModeWarning == null) { problems.Add("危险模式提示条未创建"); }
            if (_lblStatus == null) { problems.Add("状态栏文字未创建"); }
            if (_progress == null) { problems.Add("进度条未创建"); }
            if (_chkAutoScroll == null) { problems.Add("自动滚动勾选框未创建"); }
            if (_chkUpgradeArchive == null) { problems.Add("归档页的“旧版本项目升级打开”勾选框未创建"); }

            // ── 文件名规则控件
            if (_chkAppendTimestamp == null) { problems.Add("“日期时间”勾选框未创建"); }
            if (_cmbTimestampFormat == null) { problems.Add("时间戳格式下拉框未创建"); }
            else if (_cmbTimestampFormat.Items.Count != ArchiveNaming.TimestampPresets.Length)
            {
                problems.Add("时间戳格式预设数量不符，实际 " + _cmbTimestampFormat.Items.Count);
            }
            if (_chkCustomSuffix == null) { problems.Add("“自定义后缀”勾选框未创建"); }
            if (_txtSuffix == null) { problems.Add("后缀输入框未创建"); }
            if (_lblOutputPreview == null) { problems.Add("“实际输出”预览标签未创建"); }

            // ── 批量归档页控件
            if (_gridBatch == null) { problems.Add("批量页的项目列表未创建"); }
            if (_btnBatchScan == null) { problems.Add("批量页的“扫描项目”按钮未创建"); }
            if (_btnBatchStart == null) { problems.Add("批量页的“开始批量归档”按钮未创建"); }
            if (_cmbBatchMode == null) { problems.Add("批量页的归档模式下拉框未创建"); }
            if (_lblBatchSummary == null) { problems.Add("批量页的统计文字未创建"); }
            if (_txtBatchFolder == null) { problems.Add("批量页的文件夹输入框未创建"); }
            if (_txtBatchOutputDir == null) { problems.Add("批量页的输出目录输入框未创建"); }
            if (_chkBatchIgnoreBackup == null) { problems.Add("批量页的“忽略 .backup 备份目录”勾选框未创建"); }

            // ── 一之二、核心规则的纯逻辑自检（项目识别 / 输出路径 / 文件名规则 / 提示文本）
            //     这些断言与界面无关，实现已搬到 CoreSelfChecks：无头模式（--selftest）
            //     跑的是同一份，不必先把窗体建起来（环境探测失败时也不会把它们一起掩掉）。
            CoreSelfCheckResult core = CoreSelfChecks.Run();
            problems.AddRange(core.Problems);

            // 用当前字体实测最宽的那条提示需要多少像素，确认 Label 放得下
            int hintNeededWidth = 0;
            if (_lblBatchHint != null)
            {
                try
                {
                    using (Graphics measureGraphics = CreateGraphics())
                    {
                        hintNeededWidth = (int)Math.Ceiling(
                            measureGraphics.MeasureString(core.HintBoth, UiTheme.Body).Width);

                        if (hintNeededWidth > _lblBatchHint.Width)
                        {
                            problems.Add("批量页规则提示文字过宽（需要 " + hintNeededWidth
                                + "px，Label 只有 " + _lblBatchHint.Width + "px），会被截断");
                        }
                    }
                }
                catch (Exception)
                {
                    // 量不出来就不判这一项
                }
            }

            // ── 二、危险模式提示条是否按选择联动
            bool warningWorks = false;
            if (_cmbMode != null && _lblModeWarning != null)
            {
                _cmbMode.SelectedIndex = 0;                       // 压缩（安全）
                bool hiddenOnSafe = !_lblModeWarning.Visible;
                _cmbMode.SelectedIndex = 2;                       // 丢弃可恢复数据（危险）
                bool shownOnDanger = _lblModeWarning.Visible;
                _cmbMode.SelectedIndex = 0;
                warningWorks = hiddenOnSafe && shownOnDanger;
                if (!warningWorks)
                {
                    problems.Add("危险模式提示条联动异常：安全模式应隐藏、危险模式应显示"
                        + "（实测 安全=" + hiddenOnSafe + "，危险=" + shownOnDanger + "）");
                }
            }

            // ── 二之补充、打包模式的提示条
            bool packModeHintWorks = false;
            if (_cmbMode != null && _lblModeWarning != null)
            {
                for (int i = 0; i < _cmbMode.Items.Count; i++)
                {
                    ModeItem candidate = _cmbMode.Items[i] as ModeItem;
                    if (candidate != null && candidate.IsFolderPack)
                    {
                        _cmbMode.SelectedIndex = i;
                        packModeHintWorks = _lblModeWarning.Visible
                            && _lblModeWarning.Text.IndexOf("打包", StringComparison.Ordinal) >= 0;
                        break;
                    }
                }

                _cmbMode.SelectedIndex = 0;   // 还原，免得影响后面的截图

                if (!packModeHintWorks)
                {
                    problems.Add("“打包项目文件夹”模式的提示条没有正确显示");
                }
            }

            // ── 三、归档输出路径自动建议
            // 建议路径 = **项目目录的上一级** + 与项目同名的 .zapXX：不能落在项目目录里 ——
            // TIA 拒绝归档到项目自身目录（V19 实测报“项目目录已存在，无法保存”）。
            // 扩展名同样随内核版本走：期望值按"当前选中的内核版本"算出来，
            // 不写死 .zap21（在 V19 机器上跑自检时才不会误报）。
            int kernelForSuggestion = CurrentKernelMajorVersion();
            string expectedSuggested = @"D:\Demo"
                + ArchiveNaming.BuildArchiveExtension(kernelForSuggestion, @"D:\Proj\Demo.ap21");
            string suggested = SuggestArchivePath(@"D:\Proj\Demo.ap21");
            bool suggestWorks = string.Equals(suggested, expectedSuggested, StringComparison.OrdinalIgnoreCase);
            if (!suggestWorks)
            {
                problems.Add("归档路径自动建议异常：" + suggested + "（期望 " + expectedSuggested + "）");
            }

            // ── 三之补充、“归档到”里是非法路径时不能崩（回归断言）
            //   真实崩溃：用户在“归档到”里输入含引号/竖线的非法路径，再点“浏览”换项目，
            //   旧代码在这一步对文本直接调 Path.GetDirectoryName 且无保护，抛
            //   ArgumentException（“路径的形式不合法”）→ 弹“程序遇到未处理的错误”。
            //   超长路径则是另一类（PathTooLongException），两者互不继承，都要接住。
            //   期望行为：不抛异常，并且退回“建议路径”（放弃保留那个非法目录）。
            bool illegalPathSafe = true;
            if (_txtArchiveOut != null && _txtProject != null)
            {
                string savedOut = _txtArchiveOut.Text;
                bool savedUserChosen = _archiveOutUserChosen;

                string[] badPaths = new string[]
                {
                    @"D:\Bak\a""b.zap21",                          // 非法字符：引号
                    @"D:\Bak\a|b.zap21",                           // 非法字符：竖线
                    @"D:\" + new string('x', 300) + @"\a.zap21"     // 超长路径（>260）
                };

                foreach (string badPath in badPaths)
                {
                    try
                    {
                        _suppressNamingEvents = true;
                        _txtProject.Text = @"D:\Proj\Demo.ap21";
                        _txtArchiveOut.Text = badPath;
                        _suppressNamingEvents = false;
                        _archiveOutUserChosen = true;   // 触发“保留用户选过的目录”那条分支

                        ApplySuggestedArchiveOut();

                        // 非法路径被放弃 → 结果应当正好是建议路径
                        string wanted = SuggestArchivePath(@"D:\Proj\Demo.ap21");
                        if (!string.Equals(_txtArchiveOut.Text, wanted, StringComparison.OrdinalIgnoreCase))
                        {
                            illegalPathSafe = false;
                            problems.Add("“归档到”为非法路径时没有退回建议路径：输入 " + badPath
                                + "，得到 " + _txtArchiveOut.Text + "（期望 " + wanted + "）");
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        illegalPathSafe = false;
                        problems.Add("“归档到”为非法路径时抛异常：" + badPath + " → "
                            + ex.GetType().Name + "：" + ex.Message);
                        break;
                    }
                }

                // 还原（自检不能把用户/后续断言的状态改坏）
                _suppressNamingEvents = true;
                _txtArchiveOut.Text = savedOut;
                _suppressNamingEvents = false;
                _archiveOutUserChosen = savedUserChosen;
            }
            else
            {
                illegalPathSafe = false;
                problems.Add("“归档到”输入框未创建，无法验证非法路径保护");
            }

            // ── 四、TIA 实例方式映射
            bool startModeWorks = false;
            if (_rbArchiveNoUi != null && _rbArchiveUi != null && _rbArchiveAttach != null)
            {
                _rbArchiveNoUi.Checked = true;
                bool a = GetStartMode(_rbArchiveNoUi, _rbArchiveUi, _rbArchiveAttach) == TiaStartMode.WithoutUserInterface;
                _rbArchiveUi.Checked = true;
                bool b = GetStartMode(_rbArchiveNoUi, _rbArchiveUi, _rbArchiveAttach) == TiaStartMode.WithUserInterface;
                _rbArchiveAttach.Checked = true;
                bool c = GetStartMode(_rbArchiveNoUi, _rbArchiveUi, _rbArchiveAttach) == TiaStartMode.AttachExisting;
                _rbArchiveNoUi.Checked = true;

                startModeWorks = a && b && c;
                if (!startModeWorks)
                {
                    problems.Add("TIA 实例方式映射异常（无界面=" + a + "，显示界面=" + b + "，附加=" + c + "）");
                }
            }

            // ── 四之补充、批量页两个内核选项必须互斥（回归断言）
            //   曾经的 bug：两个分支的条件写成了同一句（都是"两个都勾着"），于是用户去勾
            //   "升级打开"时反被程序取消 —— 想勾的那个永远勾不上，第二个分支成了死代码。
            bool kernelExclusiveWorks = false;
            if (_chkBatchAutoKernel != null && _chkBatchUpgrade != null)
            {
                bool autoBefore = _chkBatchAutoKernel.Checked;
                bool upgradeBefore = _chkBatchUpgrade.Checked;

                // 起点：自动选内核 = 勾上，升级打开 = 未勾
                _chkBatchAutoKernel.Checked = true;
                _chkBatchUpgrade.Checked = false;

                // ① 去勾"升级打开" → 它应当保持勾上，且"自动选内核"被自动取消
                _chkBatchUpgrade.Checked = true;
                bool upgradeStayed = _chkBatchUpgrade.Checked;
                bool autoWasUnchecked = !_chkBatchAutoKernel.Checked;

                // ② 再回来勾"自动选内核" → 它应当保持勾上，且"升级打开"被自动取消
                _chkBatchAutoKernel.Checked = true;
                bool autoStayed = _chkBatchAutoKernel.Checked;
                bool upgradeWasUnchecked = !_chkBatchUpgrade.Checked;

                kernelExclusiveWorks = upgradeStayed && autoWasUnchecked
                    && autoStayed && upgradeWasUnchecked;
                if (!kernelExclusiveWorks)
                {
                    problems.Add("批量页内核选项互斥异常：勾“升级打开”后 该项=" + upgradeStayed
                        + "、自动选内核被取消=" + autoWasUnchecked
                        + "；再勾“自动选内核”后 该项=" + autoStayed
                        + "、升级打开被取消=" + upgradeWasUnchecked);
                }

                // 还原成进入自检前的状态（不能把用户的选择留在自检过程中改过的样子）
                _chkBatchAutoKernel.Checked = autoBefore;
                _chkBatchUpgrade.Checked = upgradeBefore;
            }

            // ── 五、日志分级着色表是否有遗漏
            bool colorWorks = true;
            LogLevel[] levels = new LogLevel[]
            {
                LogLevel.Debug, LogLevel.Info, LogLevel.Warning, LogLevel.Error, LogLevel.Success
            };
            foreach (LogLevel level in levels)
            {
                if (ColorForLevel(level) == Color.Empty)
                {
                    colorWorks = false;
                    problems.Add("日志级别 " + level + " 没有配色");
                }
            }

            // ── 文字宽度实测（用于判断标签是否真的会被裁）
            try
            {
                using (Graphics graphics = CreateGraphics())
                {
                    SizeF width1 = graphics.MeasureString("API 目录（可留空）", UiTheme.Body);
                    SizeF width2 = graphics.MeasureString("程序集过滤（可留空）", UiTheme.Body);
                    report.AppendLine("【文字宽度实测】");
                    report.AppendLine("  \"API 目录（可留空）\" 需要 " + width1.Width.ToString("0.0")
                        + "px，\"程序集过滤（可留空）\" 需要 " + width2.Width.ToString("0.0")
                        + "px，可用列宽 190px");
                    report.AppendLine();
                }
            }
            catch (Exception ex)
            {
                report.AppendLine("文字宽度实测失败：" + ex.Message);
            }

            // ── 控件树实测尺寸（排查布局问题最有用的一手数据）
            if (_tabs != null && _tabs.TabPages.Count > 3)
            {
                report.AppendLine("【批量归档页控件树实测尺寸】");
                DumpControls(_tabs.TabPages[3], 0, report);
                report.AppendLine();

                report.AppendLine("【归档页控件树实测尺寸】");
                DumpControls(_tabs.TabPages[0], 0, report);
                report.AppendLine();
            }

            // ── 一之五、会不会"裁字"的实测（提示条高度 / 表格列宽 / 下拉框宽度）
            // 用户反馈："归档模式选打包后下面那条黄色的是什么"（提示条被压成一条看不清）、
            // "批量归档页有些字显示不全"。这一类问题光看代码看不出来，只能量。
            report.AppendLine("【裁字风险实测】");
            try
            {
                // (1) 归档页模式提示条：切到打包模式，强制重排，量实际高度够不够放文字
                if (_cmbMode != null && _lblModeWarning != null)
                {
                    for (int i = 0; i < _cmbMode.Items.Count; i++)
                    {
                        ModeItem candidate = _cmbMode.Items[i] as ModeItem;
                        if (candidate != null && candidate.IsFolderPack)
                        {
                            _cmbMode.SelectedIndex = i;
                            break;
                        }
                    }

                    PerformLayout();
                    Application.DoEvents();

                    int innerWidth = _lblModeWarning.Width - _lblModeWarning.Padding.Horizontal;
                    if (innerWidth < 60)
                    {
                        innerWidth = 60;
                    }

                    Size needed = TextRenderer.MeasureText(
                        _lblModeWarning.Text,
                        _lblModeWarning.Font,
                        new Size(innerWidth, int.MaxValue),
                        TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);

                    int neededTotal = needed.Height + _lblModeWarning.Padding.Vertical;

                    report.AppendLine("  模式提示条（打包模式）：标签 " + _lblModeWarning.Width + "×"
                        + _lblModeWarning.Height + "px，可见 " + _lblModeWarning.Visible
                        + "；文字按 " + innerWidth + "px 宽换行需 " + needed.Height + "px，"
                        + "加内边距共需 " + neededTotal + "px"
                        + (neededTotal > _lblModeWarning.Height ? "  ← 高度不足，会被裁" : "  → 放得下"));

                    bool warningTallEnough = _lblModeWarning.Visible && neededTotal <= _lblModeWarning.Height;
                    if (!warningTallEnough)
                    {
                        problems.Add("归档页模式提示条高度不足：需要 " + neededTotal
                            + "px，实际只有 " + _lblModeWarning.Height + "px，文字会被裁");
                    }

                    // (1b) 顺带确认归档页在"打包模式"（提示条最长的时候）放不放得下 ——
                    // 提示条是从页签区的高度里扣的，写太长会把整页顶出滚动条。
                    if (_tabs != null && _tabs.TabPages.Count > 0)
                    {
                        _tabs.SelectedIndex = 0;
                        Application.DoEvents();
                        PerformLayout();
                        Application.DoEvents();

                        TableLayoutPanel archiveRoot = _tabs.TabPages[0].Controls.Count > 0
                            ? _tabs.TabPages[0].Controls[0] as TableLayoutPanel
                            : null;

                        if (archiveRoot != null)
                        {
                            bool scrolling = archiveRoot.VerticalScroll.Visible;
                            report.AppendLine("  归档页在打包模式下："
                                + (scrolling ? "内容放不下，出现纵向滚动条" : "正好放得下，无需滚动")
                                + "（页芯 " + archiveRoot.ClientSize.Width + "×"
                                + archiveRoot.ClientSize.Height + "px）");

                            if (scrolling)
                            {
                                problems.Add("归档页在默认窗口尺寸下放不下（会出滚动条），"
                                    + "提示条文字该精简或页芯该加高");
                            }
                        }
                    }

                    _cmbMode.SelectedIndex = 0;   // 还原
                    PerformLayout();
                }

                // (2) 批量表格各列：列头文字放不放得下
                if (_gridBatch != null)
                {
                    // 列宽不归 WinForms 自动缩放管，是 ApplyGridDpiScale() 自己按 DPI 换算的 ——
                    // 把"控件认为的 DPI"和"记录的设计宽度"一并打出来，出问题时一眼能定位。
                    report.AppendLine("  批量表格：控件 DPI=" + _gridBatch.DeviceDpi
                        + "，所在窗体 DPI=" + (_gridBatch.FindForm() == null
                            ? -1 : _gridBatch.FindForm().DeviceDpi)
                        + "，UiTheme.Scale 用的基准=" + UiTheme.Scale(_gridBatch, 96));

                    foreach (DataGridViewColumn column in _gridBatch.Columns)
                    {
                        Size headerSize = TextRenderer.MeasureText(column.HeaderText, UiTheme.BodyBold);
                        int neededColumnWidth = headerSize.Width + 18;   // 表头左右内边距
                        report.AppendLine("  批量表格列[" + column.HeaderText + "]：列宽 " + column.Width
                            + "px（记录的设计宽 "
                            + (column.Tag is int ? ((int)column.Tag).ToString() : "无") + "），列头文字需 "
                            + headerSize.Width + "px（含内边距约 " + neededColumnWidth + "px）"
                            + (neededColumnWidth > column.Width ? "  ← 会被裁" : "  → 放得下"));

                        if (neededColumnWidth > column.Width)
                        {
                            problems.Add("批量表格列头“" + column.HeaderText + "”会被裁：列宽 "
                                + column.Width + "px，需要约 " + neededColumnWidth + "px");
                        }
                    }
                }

                // (3) 归档页"时间戳格式"下拉框：最长选项文字放不放得下
                if (_cmbTimestampFormat != null)
                {
                    int longest = 0;
                    string longestText = string.Empty;
                    foreach (object item in _cmbTimestampFormat.Items)
                    {
                        string itemText = item == null ? string.Empty : item.ToString();
                        Size size = TextRenderer.MeasureText(itemText, _cmbTimestampFormat.Font);
                        if (size.Width > longest)
                        {
                            longest = size.Width;
                            longestText = itemText;
                        }
                    }

                    int neededComboWidth = longest + 42;   // 左右内边距 + 下拉箭头
                    report.AppendLine("  时间戳格式下拉框：宽 " + _cmbTimestampFormat.Width
                        + "px，最长选项“" + longestText + "”需 " + longest + "px（含箭头约 "
                        + neededComboWidth + "px）"
                        + (neededComboWidth > _cmbTimestampFormat.Width ? "  ← 会被裁" : "  → 放得下"));

                    if (neededComboWidth > _cmbTimestampFormat.Width)
                    {
                        problems.Add("时间戳格式下拉框会被裁：宽 " + _cmbTimestampFormat.Width
                            + "px，需要约 " + neededComboWidth + "px");
                    }
                }

                // (4) 批量页的两个复选框与"开始批量归档"按钮：文字放不放得下
                CheckLabelFits(_chkBatchSaveFirst, "每个项目归档前先保存", problems, report);
                CheckLabelFits(_chkBatchUpgrade, "旧版本项目升级打开", problems, report);
                CheckLabelFits(_lblBatchSummary, "尚未扫描。点击“扫描项目”列出文件夹里的 TIA 项目。", problems, report);
                CheckButtonFits(_btnBatchScan, problems, report);
                CheckButtonFits(_btnBatchStart, problems, report);
                CheckButtonFits(_btnBatchCancel, problems, report);
                CheckButtonFits(_btnBatchToggleSettings, problems, report);

                // (6) 现代文件夹对话框（IFileOpenDialog）能不能用。
                // 弹窗是模态的、没法在自动自检里真的弹出来，所以测"建得出来 + 选项设得上 + 回读正确"，
                // 这已经能覆盖绝大多数失败情形（老系统没这个接口、COM 不可用等）。
                string folderDialogDetail;
                bool folderDialogOk = ModernFolderDialog.SmokeTest(out folderDialogDetail);
                report.AppendLine("  现代文件夹对话框（IFileOpenDialog）："
                    + (folderDialogOk ? "可用" : "不可用（会回退到老式对话框）")
                    + " —— " + folderDialogDetail);
                if (!folderDialogOk)
                {
                    problems.Add("现代文件夹对话框不可用：" + folderDialogDetail);
                }

                // (5) 批量页"收起设置"能给列表让出多少高度
                // （用户反馈：项目多的时候列表只能看到 3 行）
                if (_gridBatch != null && _batchRoot != null && _tabs != null && _tabs.TabPages.Count > 3)
                {
                    _tabs.SelectedIndex = 3;
                    Application.DoEvents();
                    PerformLayout();
                    Application.DoEvents();

                    int expandedHeight = _gridBatch.Height;
                    float sourceRowBefore = _batchRoot.RowStyles[0].Height;
                    float settingsRowBefore = _batchRoot.RowStyles[1].Height;

                    SetBatchSettingsCollapsed(true);
                    Application.DoEvents();
                    PerformLayout();
                    Application.DoEvents();

                    int collapsedHeight = _gridBatch.Height;
                    SetBatchSettingsCollapsed(false);
                    Application.DoEvents();
                    PerformLayout();
                    Application.DoEvents();

                    float sourceRowAfter = _batchRoot.RowStyles[0].Height;
                    float settingsRowAfter = _batchRoot.RowStyles[1].Height;

                    int rowHeight = _gridBatch.RowTemplate.Height;
                    int extraRows = rowHeight > 0 ? (collapsedHeight - expandedHeight) / rowHeight : 0;

                    report.AppendLine("  批量页列表高度：设置展开 " + expandedHeight + "px → 收起 "
                        + collapsedHeight + "px（多出 " + (collapsedHeight - expandedHeight)
                        + "px ≈ " + extraRows + " 行；单行高 " + rowHeight + "px）");
                    report.AppendLine("  批量页设置区行高：收起前 " + sourceRowBefore.ToString("0") + " / "
                        + settingsRowBefore.ToString("0") + "px → 展开后 "
                        + sourceRowAfter.ToString("0") + " / " + settingsRowAfter.ToString("0") + "px"
                        + (Math.Abs(sourceRowAfter - sourceRowBefore) > 0.5f
                            || Math.Abs(settingsRowAfter - settingsRowBefore) > 0.5f
                            ? "  ← 没还原，卡片会被压扁" : "  → 已还原"));

                    if (collapsedHeight <= expandedHeight)
                    {
                        problems.Add("批量页“收起设置”没能让列表变高（" + expandedHeight
                            + "px → " + collapsedHeight + "px）");
                    }

                    // 还原必须回到"收起前的真实行高"。曾经这里写的是设计值 CardTwoRows/CardThreeRows，
                    // 而实际行高已按 DPI 放大过 → 展开后卡片被压扁、下半截按钮看不见（用户实测踩到）
                    if (Math.Abs(sourceRowAfter - sourceRowBefore) > 0.5f
                        || Math.Abs(settingsRowAfter - settingsRowBefore) > 0.5f)
                    {
                        problems.Add("批量页设置区展开后行高没还原："
                            + sourceRowBefore.ToString("0") + "/" + settingsRowBefore.ToString("0")
                            + " → " + sourceRowAfter.ToString("0") + "/" + settingsRowAfter.ToString("0"));
                    }

                    _tabs.SelectedIndex = 0;
                    Application.DoEvents();
                }
            }
            catch (Exception ex)
            {
                report.AppendLine("  实测失败：" + ex.Message);
            }

            report.AppendLine();

            // ── 报告
            report.AppendLine("窗口标题：" + Text);
            report.AppendLine("窗口尺寸：" + Width + " × " + Height
                + "（客户区 " + ClientSize.Width + " × " + ClientSize.Height + "）");
            report.AppendLine("最小尺寸：" + MinimumSize.Width + " × " + MinimumSize.Height);

            // DPI 是"字被裁"的总开关：界面按 96 DPI 的像素尺寸设计，
            // 屏幕缩放 200% 时文字会按 2 倍像素绘制，布局若不跟着放大就会到处裁字。
            try
            {
                int dpiX = 96;
                using (Graphics graphics = CreateGraphics())
                {
                    dpiX = (int)Math.Round(graphics.DpiX);
                }

                report.AppendLine("缩放：屏幕 " + dpiX + " DPI（" + (dpiX * 100 / 96) + "%），"
                    + "AutoScaleMode=" + AutoScaleMode + "，AutoScaleDimensions="
                    + AutoScaleDimensions.Width.ToString("0") + "×"
                    + AutoScaleDimensions.Height.ToString("0")
                    + "，界面基准字体高 " + Font.Height + "px");
            }
            catch (Exception ex)
            {
                report.AppendLine("缩放信息读取失败：" + ex.Message);
            }

            // Ctrl+A 开关验证：这个开关以前在 App.config 里，现在由 Program.Main 的
            // AppContext.SetSwitch 设置（为了单文件发布）。这里优先反射读 WinForms 自己
            // 缓存的那个属性 —— 那是最贴近真实行为的判据；读不到就退回 AppContext.TryGetSwitch
            // （弱一点但一定可用）。两者都拿不到时只提示、不判失败，免得误报。
            try
            {
                bool valueRead = false;
                bool doNotSupportSelectAll = false;

                // AppContextSwitches 是 internal 类型，编译期引用不到 → 按名字从
                // System.Windows.Forms 程序集里取（反射可以，直接写 typeof 不行）。
                Type switchType = typeof(Form).Assembly.GetType(
                    "System.Windows.Forms.AppContextSwitches");

                System.Reflection.PropertyInfo switchProperty = switchType == null
                    ? null
                    : switchType.GetProperty(
                        "DoNotSupportSelectAllShortcutInMultilineTextBox",
                        System.Reflection.BindingFlags.NonPublic
                            | System.Reflection.BindingFlags.Static);

                if (switchProperty != null)
                {
                    doNotSupportSelectAll = (bool)switchProperty.GetValue(null, null);
                    valueRead = true;
                    report.AppendLine("多行文本框 Ctrl+A 全选："
                        + (doNotSupportSelectAll
                            ? "已禁用（异常，需检查 Program.Main 里的 SetSwitch）"
                            : "已启用（符合预期）"));
                }
                else if (AppContext.TryGetSwitch(
                    "Switch.System.Windows.Forms.DoNotSupportSelectAllShortcutInMultilineTextBox",
                    out doNotSupportSelectAll))
                {
                    valueRead = true;
                    report.AppendLine("多行文本框 Ctrl+A 全选：AppContext 开关="
                        + (doNotSupportSelectAll ? "已禁用（异常）" : "未禁用（符合预期）"));
                }
                else
                {
                    report.AppendLine("多行文本框 Ctrl+A 全选：开关未设置，读不到（跳过判定）");
                }

                if (valueRead && doNotSupportSelectAll)
                {
                    problems.Add("多行文本框 Ctrl+A 全选开关没生效（应设为 false 以恢复全选）");
                }
            }
            catch (Exception ex)
            {
                report.AppendLine("Ctrl+A 开关检查跳过：" + ex.Message);
            }

            if (_tabs != null)
            {
                List<string> pageNames = new List<string>();
                foreach (TabPage page in _tabs.TabPages)
                {
                    pageNames.Add(page.Text.Trim());
                }
                report.AppendLine("页签：" + string.Join(" / ", pageNames.ToArray()));
            }

            if (_cmbMode != null)
            {
                List<string> modeNames = new List<string>();
                foreach (object item in _cmbMode.Items)
                {
                    modeNames.Add(item.ToString());
                }
                report.AppendLine("归档模式选项：" + string.Join(" | ", modeNames.ToArray()));
            }

            if (_rtbLog != null)
            {
                report.AppendLine("日志框：字体 " + _rtbLog.Font.Name + " " + _rtbLog.Font.Size + "pt"
                    + "，只读 " + _rtbLog.ReadOnly + "，换行 " + _rtbLog.WordWrap);
            }

            report.AppendLine("危险模式提示联动：" + (warningWorks ? "正常" : "异常"));
            report.AppendLine("打包模式提示条：" + (packModeHintWorks ? "正常" : "异常"));
            report.AppendLine("归档路径自动建议：" + suggested + "（" + (suggestWorks ? "正确" : "错误") + "）");
            report.AppendLine("“归档到”非法路径保护：" + (illegalPathSafe ? "正常（退回建议路径）" : "异常"));
            report.AppendLine("TIA 实例方式映射：" + (startModeWorks ? "正常" : "异常"));
            report.AppendLine("批量页内核选项互斥：" + (kernelExclusiveWorks ? "正常" : "异常"));
            report.AppendLine("日志级别配色：" + (colorWorks ? "完整" : "有遗漏"));
            report.AppendLine("核心规则自检（项目识别 / 输出路径 / 文件名规则 / 规则提示）："
                + (core.AllPassed ? "全部通过" : "有 " + core.Problems.Count + " 项异常")
                + "，共 " + core.CheckCount + " 项");
            report.AppendLine("批量页规则提示：" + core.HintBoth
                + "　（实测需要 " + hintNeededWidth + "px"
                + (_lblBatchHint == null ? string.Empty : "，Label 宽 " + _lblBatchHint.Width + "px")
                + "）");
            report.AppendLine();

            if (problems.Count == 0)
            {
                report.AppendLine("[ OK ] 界面自检通过：控件齐全，危险模式提示、路径建议、非法路径保护、实例方式映射、内核选项互斥、日志配色均正常。");
            }
            else
            {
                report.AppendLine("[错误] 界面自检发现问题 " + problems.Count + " 项：");
                foreach (string problem in problems)
                {
                    report.AppendLine("  - " + problem);
                }
            }

            return report.ToString();
        }
    }
}
