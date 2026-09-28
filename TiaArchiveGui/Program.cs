using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using TiaOpennessKit;
using TiaOpennessKit.Services;
using TiaOpennessKit.Tia;

namespace TiaArchiveGui
{
    /// <summary>
    /// 图形界面版入口。
    ///
    /// 除了正常开窗，还支持一个隐藏参数 --selftest：
    ///   TiaArchiveGui.exe --selftest [输出文件]
    /// 它不开窗，而是把"环境探测 → 挂载程序集解析 → 反射打印真实 API 签名"跑一遍并把
    /// 结果写进文本文件。用途是在没有桌面（或不想弹窗）的环境里做冒烟验证，
    /// 也是排查"界面打不开/日志为空"这类问题时最快的对照手段。
    /// </summary>
    internal static class Program
    {
        /// <summary>程序入口。</summary>
        /// <param name="args">命令行参数。</param>
        /// <returns>进程退出码。</returns>
        [STAThread]
        private static int Main(string[] args)
        {
            // 多行文本框 Ctrl+A 全选（原来声明在 App.config 的 AppContextSwitchOverrides 里，
            // 为了做到"只拷一个 exe 就能用"，迁到了代码里）。
            // 必须早于任何 TextBox 的创建与交互：WinForms 对这个开关是"首次访问时缓存"，
            // 而首次访问发生在用户按下 Ctrl+A 的时候，远晚于 Main，所以放在这里一定来得及。
            AppContext.SetSwitch(
                "Switch.System.Windows.Forms.DoNotSupportSelectAllShortcutInMultilineTextBox", false);

            // 跨版本兼容性矩阵自检（不需要装对应版本的 TIA：
            // 只做"元数据绑定 + 类型/成员齐全性"检查，用来确认一份 exe 能否驱动该版本）。
            //   TiaArchiveGui.exe --apiprobe [输出文件] [API目录]
            if (args != null && args.Length > 0 &&
                string.Equals(args[0], "--apiprobe", StringComparison.OrdinalIgnoreCase))
            {
                string output = args.Length > 1
                    ? args[1]
                    : Path.Combine(Path.GetTempPath(), "TiaArchiveGui.apiprobe.log");
                return ApiProbeOnly(output, args.Length > 2 ? args[2] : null);
            }

            // 一条命令扫遍 V15~V21：把某个根目录下的 PublicAPI\V* 逐个验证一遍，
            // 输出一张"版本 → 能不能驱动"的矩阵表（只读元数据，不启动 TIA）。
            //   TiaArchiveGui.exe --apimatrix [输出文件] [PublicAPI根目录]
            if (args != null && args.Length > 0 &&
                string.Equals(args[0], "--apimatrix", StringComparison.OrdinalIgnoreCase))
            {
                string output = args.Length > 1
                    ? args[1]
                    : Path.Combine(Path.GetTempPath(), "TiaArchiveGui.apimatrix.log");
                return ApiMatrix(output, args.Length > 2 ? args[2] : null);
            }

            // 浅层安装目录扫描自检（排错用）：列出"各盘根目录 + 一级子目录"里的 Portal V* 目录，
            // 并说明每个目录有没有装 Openness 组件。用于验证"TIA 装在非常规目录"时能否被发现。
            //   TiaArchiveGui.exe --apiscan [输出文件]
            if (args != null && args.Length > 0 &&
                string.Equals(args[0], "--apiscan", StringComparison.OrdinalIgnoreCase))
            {
                string output = args.Length > 1
                    ? args[1]
                    : Path.Combine(Path.GetTempPath(), "TiaArchiveGui.apiscan.log");
                return ApiScanLoose(output);
            }

            if (args != null && args.Length > 0 &&
                string.Equals(args[0], "--selftest", StringComparison.OrdinalIgnoreCase))
            {
                string output = args.Length > 1
                    ? args[1]
                    : Path.Combine(Path.GetTempPath(), "TiaArchiveGui.selftest.log");
                return SelfTest(output);
            }

            if (args != null && args.Length > 0 &&
                string.Equals(args[0], "--uicheck", StringComparison.OrdinalIgnoreCase))
            {
                string output = args.Length > 1
                    ? args[1]
                    : Path.Combine(Path.GetTempPath(), "TiaArchiveGui.uicheck.log");
                return UiCheck(output);
            }

            if (args != null && args.Length > 0 &&
                string.Equals(args[0], "--envcheck", StringComparison.OrdinalIgnoreCase))
            {
                string output = args.Length > 1
                    ? args[1]
                    : Path.Combine(Path.GetTempPath(), "TiaArchiveGui.envcheck.log");
                // 第三个参数可选：指定 API 目录，体检会把它也当作"组件可用"的依据之一
                return EnvCheck(output, args.Length > 2 ? args[2] : null);
            }

            if (args != null && args.Length > 0 &&
                string.Equals(args[0], "--archive-child", StringComparison.OrdinalIgnoreCase))
            {
                return ArchiveChild(args.Length > 1 ? args[1] : null);
            }

            if (args != null && args.Length > 0 &&
                string.Equals(args[0], "--envdump", StringComparison.OrdinalIgnoreCase))
            {
                string output = args.Length > 1
                    ? args[1]
                    : Path.Combine(Path.GetTempPath(), "TiaArchiveGui.envdump.log");
                return EnvDump(output, args.Length > 2 ? args[2] : null);
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // 把"没人接住"的异常也变成看得见的提示，而不是窗口无声消失。
            Application.ThreadException += OnThreadException;
            AppDomain.CurrentDomain.UnhandledException += OnDomainException;

            using (MainForm form = new MainForm())
            {
                Application.Run(form);
            }

            return 0;
        }

        private static void OnThreadException(object sender, ThreadExceptionEventArgs e)
        {
            ShowFatal(e.Exception);
        }

        private static void OnDomainException(object sender, UnhandledExceptionEventArgs e)
        {
            ShowFatal(e.ExceptionObject as Exception);
        }

        private static void ShowFatal(Exception exception)
        {
            string text = exception == null ? "未知错误。" : exception.ToString();
            MessageBox.Show(
                "程序遇到未处理的错误：\r\n\r\n" + text,
                "TIA Portal 归档助手",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }

        /// <summary>
        /// 界面自检：真的把主窗口建起来、跑一遍控件完整性检查，然后把窗体关掉。
        /// 用于在无人值守的环境里验证"窗口能不能正常打开"，检查结果写进文本文件。
        /// </summary>
        /// <param name="outputPath">报告输出文件。</param>
        /// <returns>0 表示界面构建正常，非 0 表示有问题。</returns>
        private static int UiCheck(string outputPath)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            int exitCode = ExitCodes.Success;
            StringBuilder report = new StringBuilder();

            try
            {
                using (MainForm form = new MainForm())
                {
                    // 必须让消息循环真正跑起来，控件句柄才会创建。
                    // 用标准名限定：本文件同时 using 了 System.Threading，直接写 Timer 会有歧义。
                    System.Windows.Forms.Timer autoClose = new System.Windows.Forms.Timer();
                    autoClose.Interval = 1500;
                    autoClose.Tick += delegate
                    {
                        autoClose.Stop();

                        // 先把三个页签各截一张图 —— 既是交付文档的配图，也顺带验证
                        // 每一页都能正常渲染（切页时崩掉的界面，这里就会暴露）。
                        try
                        {
                            string logDirectory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
                            string screenshotDirectory = Path.Combine(logDirectory, "screenshots");
                            var shots = form.CaptureTabScreenshots(screenshotDirectory);

                            report.AppendLine("已生成界面截图 " + shots.Count + " 张：");
                            foreach (string shot in shots)
                            {
                                report.AppendLine("  " + shot);
                            }
                            report.AppendLine();
                        }
                        catch (Exception ex)
                        {
                            report.AppendLine("[警告] 界面截图失败：" + ex.Message);
                            report.AppendLine();
                        }

                        report.AppendLine(form.UiSelfCheck());
                        form.Close();
                    };

                    form.Shown += delegate { autoClose.Start(); };
                    Application.Run(form);
                }

                report.Insert(0, "[ OK ] 主窗口创建并显示成功。\r\n");
            }
            catch (Exception ex)
            {
                exitCode = ExitCodes.Unhandled;
                report.AppendLine("[错误] 界面构建失败。");
                report.AppendLine(ex.ToString());
            }
            finally
            {
                try
                {
                    File.WriteAllText(outputPath, report.ToString(), new UTF8Encoding(true));
                }
                catch (Exception)
                {
                    // 报告写不出去时，退出码仍然有效。
                }
            }

            return exitCode;
        }

        /// <summary>
        /// 浅层安装目录扫描自检：列出各盘根目录与其一级子目录下的 Portal V* 目录，
        /// 以及各自到底有没有装 Openness 组件。
        ///
        /// 用途：TIA 允许装在任意目录，正常情况靠注册表就能找到；万一注册表里也没有记录
        /// （手工拷贝的目录树、注册表被清理），程序会退到这条"浅层扫描"兜底路径。
        /// 这个开关让人能在不改动任何系统设置的前提下，验证它究竟扫到了哪些 Portal 目录。
        /// </summary>
        /// <param name="outputPath">报告输出文件。</param>
        /// <returns>始终返回 0（纯诊断，不表示通过与否）。</returns>
        private static int ApiScanLoose(string outputPath)
        {
            FileLogSink sink = new FileLogSink(outputPath);
            Logger logger = new Logger(true, sink);

            try
            {
                logger.Section("浅层安装目录扫描");
                sink.WriteRaw(TiaEnvironment.DescribeLoosePortalScan(logger));
                logger.Section("扫描结束");
            }
            catch (Exception ex)
            {
                logger.Error("扫描失败：" + ex.GetType().Name + "：" + ex.Message);
                logger.Error(ex.ToString());
            }
            finally
            {
                sink.Flush();
            }

            return ExitCodes.Success;
        }

        /// <summary>
        /// 一条命令扫遍所有版本：把根目录下每个 `V*` 的 PublicAPI 目录逐个绑定并检查，
        /// 最后打印一张"版本 → 能不能驱动"的矩阵表。
        ///
        /// 用法（本机把 V16~V19 的 PublicAPI 收在一个目录里时最方便）：
        ///   TiaArchiveGui.exe --apimatrix &lt;日志&gt; &lt;含 V16..V21 子目录的根目录&gt;
        /// 根目录传空则只验本机自动探测到的那个版本。
        /// 只读元数据、不启动 TIA，所以可以在一台只装了 V21 的机器上验证全部版本。
        /// </summary>
        /// <param name="outputPath">报告输出文件。</param>
        /// <param name="rootDirectory">含各版本 API 目录的根目录；为空表示只验本机。</param>
        /// <returns>0 表示全部通过，非 0 表示有版本不通过。</returns>
        private static int ApiMatrix(string outputPath, string rootDirectory)
        {
            FileLogSink sink = new FileLogSink(outputPath);
            Logger logger = new Logger(true, sink);
            int exitCode = ExitCodes.Success;

            // 结果行：版本目录名 / 绑定到的程序集 / 是否通过 / 前言
            List<string[]> rows = new List<string[]>();
            List<string> directories = new List<string>();

            try
            {
                logger.Section("跨版本兼容性矩阵");

                if (!string.IsNullOrWhiteSpace(rootDirectory) && Directory.Exists(rootDirectory))
                {
                    foreach (string dir in Directory.GetDirectories(rootDirectory))
                    {
                        // 跳过 *.AddIn 之类的非归档 API 目录（它们也含 Siemens DLL，但不是 Openness 本体）。
                        string name = Path.GetFileName(dir);
                        if (name.IndexOf(".AddIn", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            continue;
                        }

                        // 目录里必须真有 Siemens 程序集才算候选。
                        if (Directory.GetFiles(dir, "Siemens.Engineering*.dll").Length == 0)
                        {
                            continue;
                        }

                        directories.Add(dir);
                    }

                    directories.Sort(StringComparer.OrdinalIgnoreCase);
                    logger.Info("扫描根目录：" + rootDirectory + "，找到 " + directories.Count + " 个候选版本目录。");
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(rootDirectory))
                    {
                        logger.Warning("根目录不存在：" + rootDirectory + "，改为只验证本机自动探测到的版本。");
                    }
                    else
                    {
                        logger.Info("未指定根目录，只验证本机自动探测到的版本。");
                    }
                    directories.Add(null);
                }

                // 扫了外部目录时，额外把"本机真正安装的那个版本"也加进来验一遍 ——
                // 这样一张表同时回答两件事："通用性够不够"（外部目录里那些版本）
                // 与"本机这个版本能不能用"。若本来就在验本机，则不重复。
                if (rootDirectory != null && directories.Count > 0 && directories[0] != null)
                {
                    directories.Add(null);
                }

                foreach (string dir in directories)
                {
                    string label = string.IsNullOrEmpty(dir) ? "(本机自动探测)" : Path.GetFileName(dir);
                    logger.Section("验证：" + label);

                    string assemblyName = "-";
                    string detail = string.Empty;
                    bool ok = false;

                    try
                    {
                        // 关键：每个版本单独探测 + 绑定。绑定是纯元数据操作（只加载反射信息、
                        // 不启动 TIA），所以同一个进程里逐个跑不会互相污染。
                        TiaEnvironmentInfo environment = TiaEnvironment.Detect(logger, dir);
                        TiaEnvironment.InstallAssemblyResolver(environment, logger);
                        OpennessApi api = OpennessApi.Bind(environment, logger);

                        assemblyName = api.AssemblyNameText;
                        IList<string> problems = api.SelfCheck();
                        ok = problems.Count == 0;

                        if (ok)
                        {
                            logger.Ok("可以驱动该版本：" + assemblyName + " 里归档/检索所需的类型与成员齐全。");
                        }
                        else
                        {
                            logger.Error("缺少本程序需要的 API，共 " + problems.Count + " 项：");
                            foreach (string problem in problems)
                            {
                                logger.Error("  - " + problem);
                            }
                            detail = problems.Count + " 项缺失";
                        }
                    }
                    catch (Exception ex)
                    {
                        detail = ex.GetType().Name + "：" + ex.Message;
                        logger.Error("验证失败：" + detail);
                    }

                    rows.Add(new string[] { label, assemblyName, ok ? "可以驱动 ✓" : "不通过 ✗", detail });
                    if (!ok)
                    {
                        exitCode = ExitCodes.Api;
                    }
                }

                // 汇总表：让人一眼看完全部版本，不必在日志里翻。
                logger.Section("矩阵汇总");
                logger.Info(Pad("版本目录", 22) + Pad("假定的 Openness 程序集", 38) + "结果");
                logger.Info(new string('-', 78));
                foreach (string[] row in rows)
                {
                    logger.Info(Pad(row[0], 22) + Pad(row[1], 38) + row[2]
                        + (string.IsNullOrEmpty(row[3]) ? string.Empty : "  ← " + row[3]));
                }
                logger.Info(new string('-', 78));
                logger.Info("共 " + rows.Count + " 个版本，通过 "
                    + rows.FindAll(r => r[2].IndexOf("✓", StringComparison.Ordinal) >= 0).Count + " 个。");

                if (exitCode == ExitCodes.Success)
                {
                    logger.Ok("APIMATRIX_OK");
                }
                else
                {
                    logger.Error("APIMATRIX_FAILED");
                }
            }
            catch (Exception ex)
            {
                exitCode = ExitCodes.Unhandled;
                logger.Error("矩阵自检失败：" + ex.GetType().Name + "：" + ex.Message);
                logger.Error(ex.ToString());
                logger.Error("APIMATRIX_FAILED");
            }
            finally
            {
                sink.Flush();
            }

            return exitCode;
        }

        /// <summary>按显示宽度补空格（中文字符按 2 列算，保证表格对齐）。</summary>
        /// <param name="text">原文。</param>
        /// <param name="width">目标列宽。</param>
        /// <returns>补齐后的文本。</returns>
        private static string Pad(string text, int width)
        {
            string value = text ?? string.Empty;
            int displayWidth = 0;
            foreach (char c in value)
            {
                displayWidth += c > 0x7F ? 2 : 1;
            }

            int pad = width - displayWidth;
            return pad <= 0 ? value + " " : value + new string(' ', pad);
        }

        /// <summary>
        /// 跨版本兼容性自检：**只验证"一份 exe 能不能驱动这个版本的 Openness"**，
        /// 不启动 TIA、不需要本机装该版本（只要有它的 PublicAPI 目录即可）。
        ///
        /// 与 <see cref="SelfTest"/> 的区别：这里刻意**不**做与本机安装状态相关的检查
        /// （用户组、TIA 进程、注册表…），所以可以拿一台只装了 V21 的机器去逐个验证
        /// V16/V17/V18/V19/V20 的 API 目录能不能被正确绑定、类型与成员是否齐全。
        /// </summary>
        /// <param name="outputPath">报告输出文件。</param>
        /// <param name="apiDirectory">要验证的 API 目录（PublicAPI\Vxx）；为空表示自动探测本机。</param>
        /// <returns>0 表示通过，非 0 表示不通过。</returns>
        private static int ApiProbeOnly(string outputPath, string apiDirectory)
        {
            FileLogSink sink = new FileLogSink(outputPath);
            Logger logger = new Logger(true, sink);
            int exitCode = ExitCodes.Success;

            try
            {
                logger.Section(string.IsNullOrEmpty(apiDirectory)
                    ? "跨版本兼容性自检（自动探测本机 Openness）"
                    : "跨版本兼容性自检（指定 API 目录）");
                logger.Info("API 目录：" + (string.IsNullOrEmpty(apiDirectory) ? "（自动探测）" : apiDirectory));

                TiaEnvironmentInfo environment = TiaEnvironment.Detect(logger, apiDirectory);
                logger.Ok(environment.ToString());
                TiaEnvironment.InstallAssemblyResolver(environment, logger);

                OpennessApi api = OpennessApi.Bind(environment, logger);
                logger.Ok("API 绑定成功：" + environment.MajorVersion + " / " + api.AssemblyNameText);

                // 判据与设备工具的 --apicap 完全一致：拿本程序真正要用的类型与成员逐项对。
                IList<string> problems = api.SelfCheck();
                bool capable = problems.Count == 0;

                logger.Section("归档/检索所需类型与成员");
                if (capable)
                {
                    logger.Ok("可以驱动该版本：" + api.AssemblyNameText
                        + " 里归档/检索所需的类型与成员齐全。");
                }
                else
                {
                    logger.Error("该版本缺少本程序需要的 API，共 " + problems.Count + " 项：");
                    foreach (string problem in problems)
                    {
                        logger.Error("  - " + problem);
                    }
                }

                logger.Section("结论");
                logger.Info("版本：V" + environment.MajorVersion + "，程序集：" + api.AssemblyNameText);
                logger.Info("引用目录：" + environment.ApiDirectory);
                if (capable)
                {
                    logger.Ok("APIPROBE_OK");
                }
                else
                {
                    exitCode = ExitCodes.Api;
                    logger.Error("APIPROBE_FAILED");
                }
            }
            catch (Exception ex)
            {
                exitCode = ExitCodes.Unhandled;
                logger.Error("兼容性自检失败：" + ex.GetType().Name + "：" + ex.Message);
                logger.Error(ex.ToString());
                logger.Error("APIPROBE_FAILED");
            }
            finally
            {
                sink.Flush();
            }

            return exitCode;
        }

        /// <summary>
        /// 无窗口自检：把核心逻辑跑一遍，日志写进文本文件。
        /// </summary>
        /// <param name="outputPath">输出文件路径。</param>
        /// <returns>0 表示全部通过，非 0 表示失败/异常。</returns>
        private static int SelfTest(string outputPath)
        {
            FileLogSink sink = new FileLogSink(outputPath);
            Logger logger = new Logger(true, sink);
            int exitCode = ExitCodes.Success;

            try
            {
                logger.Section("自检开始");
                logger.Info("程序目录：" + AppDomain.CurrentDomain.BaseDirectory);
                logger.Info("CLR 版本：" + Environment.Version);
                logger.Info("当前用户：" + Environment.UserDomainName + "\\" + Environment.UserName);

                // ★ 核心规则的纯逻辑自检放在最前面：它不需要 Openness、不需要窗口，
                //   所以哪怕这台机器根本没装 TIA（下面 Detect 会失败），这些规则也照样被验证 ——
                //   不会被环境问题一起掩掉。（同一份断言也被界面自检 --uicheck 使用。）
                logger.Section("核心规则自检（不依赖 Openness）");
                CoreSelfCheckResult coreCheck = CoreSelfChecks.Run();
                if (coreCheck.AllPassed)
                {
                    logger.Ok("核心规则全部通过（共 " + coreCheck.CheckCount + " 项）。");
                }
                else
                {
                    exitCode = ExitCodes.Api;
                    logger.Error("核心规则有 " + coreCheck.Problems.Count + " 项异常：");
                    foreach (string problem in coreCheck.Problems)
                    {
                        logger.Error("  - " + problem);
                    }
                }

                // ★ 顺序铁律：先挂程序集解析钩子，再触碰任何 Siemens 类型。
                logger.Section("定位 TIA Portal Openness 环境");
                TiaEnvironmentInfo environment = TiaEnvironment.Detect(logger, null);
                logger.Ok(environment.ToString());
                TiaEnvironment.InstallAssemblyResolver(environment, logger);

                // 绑定本机 Openness API：会真的加载程序集并解析出 TiaPortal / Project /
                // ProjectArchivationMode 等类型，是"这个版本能不能用"的最直接验证。
                OpennessApi api = OpennessApi.Bind(environment, logger);
                logger.Ok("API 绑定成功：" + api.AssemblyNameText);

                // 到这里才可以安全地引用 Siemens 类型（probe 内部是反射，仍然安全）。
                logger.Section("反射打印本机真实 API 签名");
                ApiProbe.Run(environment, logger, null);

                // 版本解析自检：这几个函数决定"自动挑哪个版本的 API 目录"，
                // 一旦算错就会挑到跑不起来的版本上（真实踩过：把 V15.1 算成 151，排在 V19 前面）。
                logger.Section("版本号解析自检");
                bool versionParsingOk = CheckVersionParsing(logger);
                if (!versionParsingOk)
                {
                    exitCode = ExitCodes.Api;
                }

                bool inGroup = MainForm.IsCurrentUserInOpennessGroup(out string groupMessage);
                if (inGroup)
                {
                    logger.Ok(groupMessage);
                }
                else
                {
                    logger.Warning(groupMessage);
                }

                logger.Section("自检结束");
                logger.Ok("SELFTEST_OK");
            }
            catch (Exception ex)
            {
                exitCode = ExitCodes.Unhandled;
                logger.Error("自检失败：" + ex.GetType().Name + "：" + ex.Message);
                logger.Error(ex.ToString());
                logger.Error("SELFTEST_FAILED");
            }
            finally
            {
                sink.Flush();
            }

            return exitCode;
        }

        /// <summary>
        /// 隐藏模式：**"按项目版本换内核"的子进程入口**。
        ///
        /// 背景：一个进程同一时刻只能绑定一个版本的 Siemens.Engineering 程序集，
        /// 所以一批里混着 .ap16 与 .ap19 时没法在同一个进程里换内核。
        /// 界面（父进程）会按项目版本分组，每组拉起一个本程序的子进程、各自绑定匹配的 API 目录，
        /// 这样旧版本项目就能被**原生打开**（不升级），产物也保持原版本（.ap16 → .zap16）。
        ///
        /// 用法（父进程自动拼，人也可以手动跑，便于排错）：
        ///   TiaArchiveGui.exe --archive-child "&lt;任务文件.job&gt;"
        /// 任务文件格式见 <see cref="BatchChildJob"/>。
        /// 日志打到标准输出（UTF-8），父进程会实时转发到界面日志框；结果另写结果文件。
        /// </summary>
        /// <param name="jobFilePath">任务文件路径。</param>
        /// <returns>退出码：0 全部成功，其它表示有失败。</returns>
        private static int ArchiveChild(string jobFilePath)
        {
            // ★ 父进程按 UTF-8 读我们的标准输出，这里必须真的输出 UTF-8。
            //   注意：WinExe（无控制台）上设 Console.OutputEncoding 常常不生效（会静默失败），
            //   实测仍然按系统默认代码页（GBK）输出，父进程按 UTF-8 读就成了乱码。
            //   可靠做法：直接把标准输出换成一个 UTF-8 的 StreamWriter。
            try
            {
                StreamWriter standardOutput = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
                standardOutput.AutoFlush = true;
                Console.SetOut(standardOutput);
            }
            catch (Exception)
            {
                // 实在拿不到标准输出就算了（日志只是给人看的，不影响归档）
            }

            Logger logger = new Logger(false, new ConsoleLogSink());
            int exitCode = ExitCodes.Success;
            // 已经写进结果文件的项目（异常兜底时只补没写过的，免得把成功项覆盖成失败）
            HashSet<string> reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                if (string.IsNullOrWhiteSpace(jobFilePath))
                {
                    logger.Error("缺少任务文件参数。用法：TiaArchiveGui.exe --archive-child <任务文件.job>");
                    return ExitCodes.Usage;
                }

                BatchChildJob job = BatchChildJob.Load(jobFilePath);

                logger.Section("子进程归档开始（内核按项目版本匹配）");
                logger.Info("API 目录：" + job.ApiDirectory);
                logger.Info("项目数量：" + job.Items.Count);
                logger.Info("归档模式：" + job.ModeKeyword
                    + "；先保存：" + (job.SaveFirst ? "是" : "否")
                    + "；升级打开：" + (job.Upgrade ? "是" : "否"));

                // 顺序铁律与主程序一致：先探测 + 挂解析钩子，再绑定 API（绑定会触碰 Siemens 类型）
                TiaEnvironmentInfo environment = TiaEnvironment.Detect(logger, job.ApiDirectory);
                logger.Ok(environment.ToString());
                TiaEnvironment.InstallAssemblyResolver(environment, logger);
                OpennessApi api = OpennessApi.Bind(environment, logger);
                logger.Ok("已绑定：" + api.AssemblyNameText);

                IList<string> problems = api.SelfCheck();
                if (problems.Count > 0)
                {
                    logger.Error("该版本缺少本程序需要的 API，共 " + problems.Count + " 项：");
                    foreach (string problem in problems)
                    {
                        logger.Error("  - " + problem);
                    }
                    return ExitCodes.Api;
                }

                BatchArchiveRequest request = new BatchArchiveRequest();
                request.ApiDirectory = job.ApiDirectory;
                request.ModeKeyword = job.ModeKeyword;
                request.SaveFirst = job.SaveFirst;
                request.Upgrade = job.Upgrade;
                request.OverwriteExisting = job.OverwriteExisting;
                request.Verbose = job.Verbose;
                request.StartMode = TiaOpennessKit.Cli.TiaStartMode.WithoutUserInterface;
                // 子进程不再分组：版本已经由父进程定好了
                request.AutoSelectKernel = false;

                // Items 是普通字段（由调用方负责 new），子进程这里要自己初始化
                request.Items = new List<BatchArchiveItem>();
                foreach (BatchChildItem item in job.Items)
                {
                    BatchArchiveItem archiveItem = new BatchArchiveItem();
                    archiveItem.ProjectPath = item.ProjectPath;
                    archiveItem.OutputPath = item.OutputPath;
                    request.Items.Add(archiveItem);
                }

                List<BatchArchiveResult> results = CoreRunner.ArchiveMany(logger, request, null, null);

                int failed = 0;
                foreach (BatchArchiveResult result in results)
                {
                    BatchChildJob.AppendResult(job.ResultPath, result.ProjectPath, result.Success,
                        result.ProductBytes, result.Message);
                    reported.Add(result.ProjectPath);
                    if (!result.Success)
                    {
                        failed++;
                    }
                }

                // 万一有项目根本没走到（例如中途异常），也要补一行，免得父进程把它当"没结果"
                foreach (BatchChildItem item in job.Items)
                {
                    bool handled = false;
                    foreach (BatchArchiveResult result in results)
                    {
                        if (string.Equals(result.ProjectPath, item.ProjectPath, StringComparison.OrdinalIgnoreCase))
                        {
                            handled = true;
                            break;
                        }
                    }

                    if (!handled)
                    {
                        BatchChildJob.AppendResult(job.ResultPath, item.ProjectPath, false, 0,
                            "子进程未处理到该项目（可能提前结束）");
                        reported.Add(item.ProjectPath);
                        failed++;
                    }
                }

                logger.Section("子进程归档结束");
                if (failed == 0)
                {
                    logger.Ok("子进程全部成功：" + results.Count + " 个项目。");
                }
                else
                {
                    logger.Error("子进程完成：失败 " + failed + " 个。");
                    exitCode = ExitCodes.Api;
                }
            }
            catch (Exception ex)
            {
                exitCode = ExitCodes.Api;
                string reason = "子进程失败：" + ex.GetType().Name + "：" + ex.Message;
                logger.Error(reason);
                // 打出完整堆栈：子进程是独立进程，父进程界面里只看到这一份日志，
                // 没有堆栈就无法定位（实测踩过 NullReferenceException 只有一行消息，没法查）。
                logger.Error(ex.ToString());

                // 整批性故障（例如本机没装这个版本的 TIA Portal，启动实例就失败）也要落进结果文件，
                // 否则父进程只会看到"子进程没有返回结果"，用户拿不到真正原因。
                try
                {
                    if (!string.IsNullOrWhiteSpace(jobFilePath))
                    {
                        BatchChildJob failedJob = BatchChildJob.Load(jobFilePath);
                        foreach (BatchChildItem item in failedJob.Items)
                        {
                            if (!reported.Contains(item.ProjectPath))
                            {
                                BatchChildJob.AppendResult(failedJob.ResultPath, item.ProjectPath,
                                    false, 0, reason);
                                reported.Add(item.ProjectPath);
                            }
                        }
                    }
                }
                catch (Exception)
                {
                    // 连结果都写不出去就只能靠上面的日志了
                }
            }

            return exitCode;
        }

        /// <summary>
        /// 自检：版本目录名与路径的版本号解析是否正确。
        ///
        /// 这两个函数决定"自动挑哪个版本的 API 目录"，算错就会挑到跑不起来的版本上。
        /// 真实故障：某台机器上 PublicAPI 下有 V15.1 与 V19，而被选中却是不该被考虑的 V15.1 ——
        /// 因为旧实现把 "V15.1" 里的非数字全删掉算成了 151，比 V19 的 19 还大。
        /// </summary>
        private static bool CheckVersionParsing(Logger logger)
        {
            bool ok = true;

            // 目录名 → 主版本号
            string[,] nameCases = new string[,]
            {
                { "V19", "19" },
                { "V21", "21" },
                { "V15.1", "15" },   // 小版本目录：只取主版本号
                { "v16", "16" },     // 大小写不敏感
                { "net48", "0" },    // 目标框架目录不是版本目录
                { "V16.AddIn", "16" } // AddIn 目录会被上层过滤掉，这里只保证不解析成垃圾值
            };

            for (int i = 0; i < nameCases.GetLength(0); i++)
            {
                string name = nameCases[i, 0];
                int expected = int.Parse(nameCases[i, 1], CultureInfo.InvariantCulture);
                int actual = TiaEnvironment.ParseMajorVersion(name);
                if (actual != expected)
                {
                    logger.Error("版本目录名解析异常：" + name + " 期望 " + expected + "，实际 " + actual);
                    ok = false;
                }
            }

            // 完整路径 → 版本号（用于"挑最新版本"的排序）
            string[,] pathCases = new string[,]
            {
                { @"C:\Program Files\Siemens\Automation\Portal V19\PublicAPI\V19", "19" },
                { @"C:\Program Files\Siemens\Automation\Portal V17\PublicAPI\V15.1", "15" },
                { @"E:\Program Files\Siemens\Automation\Portal V21\PublicAPI\V21\net48", "21" }
            };

            for (int i = 0; i < pathCases.GetLength(0); i++)
            {
                string path = pathCases[i, 0];
                int expected = int.Parse(pathCases[i, 1], CultureInfo.InvariantCulture);
                int actual = TiaEnvironment.ExtractVersionNumber(path);
                if (actual != expected)
                {
                    logger.Error("路径版本号解析异常：" + path + " 期望 " + expected + "，实际 " + actual);
                    ok = false;
                }
            }

            // 排序：V19 必须排在 V15.1 前面（"挑最新版本"就靠它）
            if (TiaEnvironment.ExtractVersionNumber(@"X\PublicAPI\V19")
                <= TiaEnvironment.ExtractVersionNumber(@"X\PublicAPI\V15.1"))
            {
                logger.Error("版本排序异常：V19 没有排在 V15.1 前面，自动挑选会选错。");
                ok = false;
            }

            int requiredMajor = TiaEnvironment.GetReferencedOpennessMajorVersion();
            logger.Info("本程序编译时引用的 Openness 主版本："
                + (requiredMajor > 0 ? "V" + requiredMajor : "无（不依赖 Openness）"));

            if (ok)
            {
                logger.Ok("版本号解析与排序正常。");
            }

            return ok;
        }

        /// <summary>
        /// Openness 环境诊断：把"本程序编译时要什么版本的 Openness / 本机有什么版本 /
        /// 运行期程序集解析会搜哪些目录、里面各是什么版本"一次写清楚。
        ///
        /// 专治这一类问题：**环境探测全过，一开始归档就报
        /// "未能加载 Siemens.Engineering.Base, Version=xx.0.0.0"**。
        /// 原因是 exe 在编译时就把 Openness 版本号绑定死了，本机装的是别的主版本时
        /// 无论 API 目录选得多对都加载不了。
        /// </summary>
        /// <param name="outputPath">报告输出文件。</param>
        /// <param name="apiDirectory">可选：指定要诊断的 API 目录（对应界面上的"API 目录"输入框）。</param>
        /// <returns>退出码。</returns>
        private static int EnvDump(string outputPath, string apiDirectory)
        {
            FileLogSink sink = new FileLogSink(outputPath);
            // 诊断过程内部会自己记一些日志，那些不需要进报告文件，丢给 NullLogSink。
            Logger internalLogger = new Logger(false, new NullLogSink());
            int exitCode = ExitCodes.Success;

            try
            {
                sink.WriteRaw(TiaEnvironment.BuildDiagnosticReport(internalLogger, apiDirectory));
            }
            catch (Exception ex)
            {
                exitCode = ExitCodes.Unhandled;
                sink.WriteRaw("环境诊断失败：" + ex.GetType().Name + "：" + ex.Message
                    + Environment.NewLine + ex);
            }
            finally
            {
                sink.Flush();
            }

            return exitCode;
        }

        /// <summary>
        /// 无窗口前置条件体检：把检查结果写进文本文件，并按结果返回退出码。
        /// 用途：不开窗就能确认环境是否就绪，也方便把结果贴给别人看。
        /// </summary>
        /// <param name="outputPath">报告输出文件。</param>
        /// <param name="apiDirectory">可选的 API 目录；指定且可用时，体检会把它当作"组件可用"的依据。</param>
        /// <returns>0 表示全部通过；<see cref="ExitCodes.EnvironmentCheckFailed"/> 表示有未通过项；9 表示体检本身出错。</returns>
        private static int EnvCheck(string outputPath, string apiDirectory)
        {
            StringBuilder report = new StringBuilder();
            int failed = 0;

            try
            {
                List<EnvironmentCheckResult> results = EnvironmentChecks.Run(
                    new Logger(false, new NullLogSink()), apiDirectory);

                foreach (EnvironmentCheckResult result in results)
                {
                    string mark = result.IsInfoOnly
                        ? "[须知]"
                        : (result.Passed ? "[通过]" : "[未通过]");

                    if (!result.IsInfoOnly && !result.Passed)
                    {
                        failed++;
                    }

                    report.AppendLine(mark + " " + result.Name + "：" + result.Detail);
                    if (!string.IsNullOrEmpty(result.FixHint))
                    {
                        report.AppendLine("        " + result.FixHint);
                    }
                }

                report.AppendLine();

                // 顺手验证"一键修复脚本"能不能生成（这本身也是功能的一部分）
                string scriptPath;
                bool scriptOk = EnvironmentChecks.TryGenerateFixScript(
                    AppDomain.CurrentDomain.BaseDirectory, out scriptPath);

                report.AppendLine("修复脚本生成：" + (scriptOk ? "成功 → " + scriptPath : "失败"));
                report.AppendLine();
                report.AppendLine(failed == 0
                    ? "ENVCHECK_OK：前置条件全部满足"
                    : "ENVCHECK_FAILED：有 " + failed + " 项未通过");
            }
            catch (Exception ex)
            {
                failed = 9;
                report.AppendLine("ENVCHECK_ERROR：" + ex);
            }

            try
            {
                File.WriteAllText(outputPath, report.ToString(), new UTF8Encoding(true));
            }
            catch (Exception)
            {
                // 报告写不出去时退出码仍然有效
            }

            // 有未通过项时用专用的体检退出码（以前复用的是 Usage=1，会被读成"命令行参数写错了"）；
            // failed=9 是"体检过程本身出错"，按未预期错误返回，别跟"环境缺项"混在一起。
            if (failed == 0)
            {
                return ExitCodes.Success;
            }

            return failed >= 9 ? ExitCodes.Unhandled : ExitCodes.EnvironmentCheckFailed;
        }

        /// <summary>
        /// 什么都不做的日志目的地（体检时不需要往控制台刷日志）。
        /// </summary>
        private sealed class NullLogSink : ILogSink
        {
            /// <summary>丢弃。</summary>
            /// <param name="level">级别。</param>
            /// <param name="prefix">前缀。</param>
            /// <param name="message">消息。</param>
            public void Write(LogLevel level, string prefix, string message)
            {
                // 故意不输出
            }
        }

        /// <summary>
        /// 把日志写进文本文件的输出目的地（仅自检用）。
        /// </summary>
        private sealed class FileLogSink : ILogSink
        {
            private readonly StringBuilder _buffer = new StringBuilder();
            private readonly string _path;

            /// <summary>构造。</summary>
            /// <param name="path">输出文件路径。</param>
            public FileLogSink(string path)
            {
                _path = path;
            }

            /// <summary>追加一行。</summary>
            /// <param name="level">级别。</param>
            /// <param name="prefix">前缀。</param>
            /// <param name="message">消息。</param>
            public void Write(LogLevel level, string prefix, string message)
            {
                _buffer.AppendLine(prefix + " " + message);
            }

            /// <summary>原样追加一段文本（不加上级别前缀），用于写入多行报告。</summary>
            /// <param name="text">文本。</param>
            public void WriteRaw(string text)
            {
                _buffer.Append(text);
                if (!text.EndsWith(Environment.NewLine, StringComparison.Ordinal))
                {
                    _buffer.AppendLine();
                }
            }

            /// <summary>落盘。</summary>
            public void Flush()
            {
                try
                {
                    File.WriteAllText(_path, _buffer.ToString(), new UTF8Encoding(true));
                }
                catch (Exception)
                {
                    // 自检输出写不出去时无能为力，返回码仍能反映结果。
                }
            }
        }
    }
}
