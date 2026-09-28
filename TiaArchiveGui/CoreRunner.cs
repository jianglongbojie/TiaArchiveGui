using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using TiaOpennessKit;
using TiaOpennessKit.Cli;
using TiaOpennessKit.Services;
using TiaOpennessKit.Tia;

namespace TiaArchiveGui
{
    /// <summary>
    /// 归档任务的输入参数（只装数据，不含任何 Siemens 类型）。
    /// </summary>
    internal sealed class ArchiveRequest
    {
        /// <summary>待归档的项目文件。</summary>
        public string ProjectPath;

        /// <summary>归档输出文件路径。</summary>
        public string OutputPath;

        /// <summary>归档模式关键字（compressed / none / discard-restorable …）。</summary>
        public string ModeKeyword;

        /// <summary>
        /// 是否为"打包项目文件夹"模式：不经过 Openness，直接把整个项目目录压成 .zip。
        /// 置为 true 时 ModeKeyword 不再代表 ProjectArchivationMode，也不会启动 TIA Portal。
        /// </summary>
        public bool IsFolderPack;

        /// <summary>归档前是否先保存项目。</summary>
        public bool SaveFirst;

        /// <summary>
        /// 项目为旧版本时是否用 OpenWithUpgrade 升级后打开。
        ///
        /// 适用场景：拿新版本 TIA（例如 V21）去归档一个旧版本项目（例如 .ap18）。
        /// 此时普通的 Projects.Open 会因为版本不匹配而失败，必须走 OpenWithUpgrade。
        /// 注意升级是不可逆的：项目一旦保存就变成新版本，旧版 TIA 将无法再打开它。
        /// </summary>
        public bool Upgrade;

        /// <summary>结束后是否保持项目打开。</summary>
        public bool KeepProjectOpen;

        /// <summary>
        /// 目标已存在时是否覆盖（来自界面上"目标文件已存在"确认框的【是】）。
        /// TIA 的归档**不会**覆盖已存在的目标（V21 实测报 is already exist），
        /// 覆盖由 ArchiveService 先改名备份、归档成功后删除备份、失败则恢复。
        /// </summary>
        public bool OverwriteExisting;

        /// <summary>用户是否显式指定过"保持打开"（附加模式下用它决定默认值）。</summary>
        public bool KeepOpenSpecified;

        /// <summary>TIA 实例的获取方式。</summary>
        public TiaStartMode StartMode;

        /// <summary>手工指定的 PublicAPI 目录，可为空。</summary>
        public string ApiDirectory;

        /// <summary>是否输出调试日志。</summary>
        public bool Verbose;
    }

    /// <summary>
    /// 恢复任务的输入参数。
    /// </summary>
    internal sealed class RetrieveRequest
    {
        /// <summary>归档文件路径。</summary>
        public string ArchiveFilePath;

        /// <summary>解包目标目录。</summary>
        public string TargetDirectory;

        /// <summary>归档来自更早版本时是否升级打开。</summary>
        public bool Upgrade;

        /// <summary>解包后是否保存项目。</summary>
        public bool SaveAfterRetrieve;

        /// <summary>结束后是否保持项目打开。</summary>
        public bool KeepProjectOpen;

        /// <summary>用户是否显式指定过"保持打开"。</summary>
        public bool KeepOpenSpecified;

        /// <summary>TIA 实例的获取方式。</summary>
        public TiaStartMode StartMode;

        /// <summary>手工指定的 PublicAPI 目录，可为空。</summary>
        public string ApiDirectory;

        /// <summary>是否输出调试日志。</summary>
        public bool Verbose;
    }

    /// <summary>
    /// 批量归档里的一项。
    /// </summary>
    internal sealed class BatchArchiveItem
    {
        /// <summary>项目文件路径。</summary>
        public string ProjectPath;

        /// <summary>归档输出文件路径（由界面按"输出目录 + 项目名"预先算好）。</summary>
        public string OutputPath;
    }

    /// <summary>
    /// 批量归档里一项的执行结果。
    /// </summary>
    internal sealed class BatchArchiveResult
    {
        /// <summary>项目文件路径。</summary>
        public string ProjectPath;

        /// <summary>归档输出路径。</summary>
        public string OutputPath;

        /// <summary>产生的归档文件大小（字节）。</summary>
        public long ProductBytes;

        /// <summary>是否成功。</summary>
        public bool Success;

        /// <summary>失败原因（成功时为空）。</summary>
        public string Message;
    }

    /// <summary>
    /// 批量归档的请求参数。
    /// </summary>
    internal sealed class BatchArchiveRequest
    {
        /// <summary>待处理的项目列表。</summary>
        public List<BatchArchiveItem> Items;

        /// <summary>归档模式关键字。</summary>
        public string ModeKeyword;

        /// <summary>是否为"打包项目文件夹"模式（不经过 Openness，直接压 .zip）。</summary>
        public bool IsFolderPack;

        /// <summary>每个项目归档前是否先保存。</summary>
        public bool SaveFirst;

        /// <summary>旧版本项目是否升级打开。</summary>
        public bool Upgrade;

        /// <summary>TIA 实例获取方式。</summary>
        public TiaStartMode StartMode;

        /// <summary>是否在每项完成后保持项目打开（批量场景一般关掉，否则下一个项目打不开）。</summary>
        public bool KeepProjectOpen;

        /// <summary>
        /// 目标已存在时是否覆盖（批量页"全部覆盖"的选择）。
        /// TIA 的归档**不会**覆盖已存在的目标（V21 实测报 is already exist），
        /// 覆盖由 ArchiveService 先改名备份、归档成功后删除备份、失败则恢复。
        /// </summary>
        public bool OverwriteExisting;

        /// <summary>手工指定的 API 目录，可为空。</summary>
        public string ApiDirectory;

        /// <summary>是否输出调试日志。</summary>
        public bool Verbose;

        /// <summary>
        /// 是否**按项目版本自动选内核**：把"版本与当前内核不同、且本机装了对应 Openness"的项目
        /// 交给子进程处理（子进程绑定匹配的 API 目录 → 原生打开、不升级，产物保持原版本）。
        /// </summary>
        public bool AutoSelectKernel;
    }

    /// <summary>
    /// 核心执行层：真正去调用 Openness 的地方。
    ///
    /// ★★★ 这里有一处必须遵守的铁律（踩过坑，命令行版 README 第 2 条也记着）★★★
    ///
    /// CLR 在 JIT 编译一个方法时，会先解析该方法体内用到的所有类型。而 Openness 的
    /// Siemens.Engineering.* 程序集不在本程序目录里（Copy Local = False），
    /// 必须靠 TiaEnvironment.InstallAssemblyResolver 挂上的 AssemblyResolve 钩子
    /// 从 TIA 安装目录去加载。
    ///
    /// 结论：**凡是方法体里出现 Siemens 类型的方法，都不能在钩子挂上之前被调用。**
    /// 所以本类被刻意切成两半：
    ///   · DetectEnvironment / InstallResolver / ExplainFailure → 不引用任何 Siemens 类型，随时可调；
    ///   · Archive / Retrieve / Probe                          → 引用 Siemens 类型，只能在 InstallResolver 之后调。
    /// 界面层也必须按这个顺序调用，否则一启动就 FileNotFoundException。
    ///
    /// 类比 SCL：就像必须先分配好背景 DB 的实例，才能去调用那个 FB；
    /// 顺序反了，调用点拿到的就是一个无效引用。
    /// </summary>
    internal static class CoreRunner
    {
        // ───────────────────────────────────────────────────────────────
        //  第一半：不引用 Siemens 类型，可在任何时候调用
        // ───────────────────────────────────────────────────────────────

        /// <summary>
        /// 探测本机的 TIA Portal / Openness 环境（纯文件系统与注册表操作，不引用 Siemens 类型）。
        /// </summary>
        /// <param name="logger">日志器。</param>
        /// <param name="apiDirectory">手工指定的 API 目录，可为空。</param>
        /// <returns>环境信息。</returns>
        public static TiaEnvironmentInfo DetectEnvironment(Logger logger, string apiDirectory)
        {
            logger.Section("定位 TIA Portal Openness 环境");
            return TiaEnvironment.Detect(logger, apiDirectory);
        }

        /// <summary>
        /// 挂载程序集解析钩子。必须在任何触碰 Siemens 类型的代码之前执行。
        /// </summary>
        /// <param name="environment">环境信息。</param>
        /// <param name="logger">日志器。</param>
        public static void InstallResolver(TiaEnvironmentInfo environment, Logger logger)
        {
            TiaEnvironment.InstallAssemblyResolver(environment, logger);

            // ★ 钩子只登记一次：如果它先前已登记成**另一个**版本，那么这次请求的版本不会生效。
            //   此时**不能**再把 OpennessApi 绑到 environment 上 —— 那会造成
            //   "OpennessApi.Current 说 V18、真实解析目录却是 V20"的不一致；
            //   一旦在进程内驱动 TIA，就会加载出“V18 主程序集 + V20 依赖”的混合体，
            //   报 MissingMethodException（找不到 IAppSupport.Initialize(Process)，实测过）。
            //   做法：保持原有绑定不动（它与解析目录一致），需要别的版本时由调用方改走子进程。
            string boundDirectory = TiaEnvironment.BoundApiDirectory;
            if (environment != null
                && !string.IsNullOrEmpty(boundDirectory)
                && !string.Equals(boundDirectory, environment.ApiDirectory, StringComparison.OrdinalIgnoreCase))
            {
                logger.Warning("本进程已绑定 V" + TiaEnvironment.BoundMajorVersion
                    + "（" + boundDirectory + "），保持不动；"
                    + "V" + environment.MajorVersion + " 不会在进程内绑定。");
                logger.Warning("  需要 V" + environment.MajorVersion
                    + " 的操作请走子进程（批量归档会自动这样做），或关掉本程序重开后直接选该版本。");
                return;
            }

            // 顺手绑定本机版本的 Openness API（运行期反射调用，编译期不引用 Siemens 程序集），
            // 这是"一份 exe 通吃 V15~V21"的关键：各版本程序集文件名都不同，只能运行时按类型名找。
            OpennessApi.Bind(environment, logger);
        }

        /// <summary>
        /// 创建会话，并按"是否附加"决定退出时要不要关项目。
        ///
        /// ★ 附加模式必须**不关项目**：我们是附加到用户已经打开的 TIA 实例上干活，
        ///   如果结束时调 `Project.Close()`，会把**用户正在编辑的那个项目关掉** ——
        ///   他只是让我们借用一下实例，结果项目被关，这是不能接受的副作用。
        ///   （只放弃引用即可；我们本来也不做任何 Save，所以没有任何数据风险。）
        /// </summary>
        /// <param name="startMode">实例获取方式。</param>
        /// <param name="logger">日志器。</param>
        /// <returns>会话（调用方用 using 包裹）。</returns>
        private static TiaSession CreateSession(TiaStartMode startMode, Logger logger)
        {
            TiaSession session = TiaSession.Create(startMode, logger);
            if (startMode == TiaStartMode.AttachExisting)
            {
                session.CloseProjectOnDispose = false;
                logger.Info("附加模式：结束后不会关闭你的项目（只释放附加会话，TIA 本体也不关）。");
            }

            return session;
        }

        /// <summary>
        /// 把底层异常翻译成一句人话。
        ///
        /// 刻意**不**用 catch (EngineeringSecurityException) 这种强类型捕获 —— 那会在本方法
        /// 被 JIT 时就要求加载 Siemens 程序集。这里改用类型名字符串判断，于是本方法在任何
        /// 阶段都能安全调用（包括连 Siemens 程序集都还没解析成功的时候）。
        /// </summary>
        /// <param name="exception">原始异常。</param>
        /// <returns>面向用户的中文说明。</returns>
        public static string ExplainFailure(Exception exception)
        {
            if (exception == null)
            {
                return "未知错误。";
            }

            Exception current = exception;
            while (current != null)
            {
                string typeName = current.GetType().Name;
                string messageText = current.Message ?? string.Empty;

                // ★ 先按“消息特征”给精准解释：归档到项目自身目录时 TIA 返回的这组错误，
                //   下面按类型名匹配的通用建议（版本 / 权限 / 先保存）一条都不沾边，
                //   用户真正要做的只有一件事 —— 把归档输出移出项目自身目录。
                if (messageText.IndexOf("项目目录已存在", StringComparison.Ordinal) >= 0
                    || messageText.IndexOf("project directory already exists", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return "TIA 拒绝把这个归档写到目标位置：那里已经存在一个“项目目录”。\r\n"
                        + "最常见的原因：**归档输出选在了项目自身所在的目录**（把 .zapXX 和 .apXX 放在同一个文件夹里）。\r\n"
                        + "实测（V19 Openness）：这种组合必然失败；换到项目目录之外的任何目录都能成功"
                        + "（批量归档正是因为输出目录另选才没踩到这个坑）。\r\n"
                        + "办法：把“归档到”改到项目目录之外再试（归档页默认建议值已经是项目目录的上一级）。\r\n"
                        + "（原始信息：" + messageText + "）";
                }

                if (messageText.IndexOf("is already exist", StringComparison.OrdinalIgnoreCase) >= 0
                    || messageText.IndexOf("already exists", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return "归档目标已经存在，而 TIA 的归档**不会覆盖**已存在的文件 / 文件夹"
                        + "（原文：Archive Operation is not possible as the target file/folder … is already exist.）。\r\n"
                        + "办法：换一个文件名（加时间戳 / 后缀 / 序号），或选择“覆盖”"
                        + "（工具会先把旧文件改名为 .old 备份，归档成功后才删除它；失败会把旧文件恢复回来）。\r\n"
                        + "（原始信息：" + messageText + "）";
                }

                if (string.Equals(typeName, "EngineeringSecurityException", StringComparison.Ordinal))
                {
                    return "Openness 授权被拒绝：TIA Portal 弹出的 Openness 连接授权窗口被点了“拒绝”，"
                        + "或连续拒绝达到 3 次。解决办法：重新运行并在弹窗时选择“允许”；"
                        + "同时确认当前 Windows 用户已加入本地组 “" + TiaEnvironment.OpennessUserGroup + "”。"
                        + "\r\n（原始信息：" + current.Message + "）";
                }

                if (string.Equals(typeName, "EngineeringException", StringComparison.Ordinal))
                {
                    return "Openness API 返回错误：" + current.Message
                        + "\r\n建议：先用“环境探测”页确认 API 版本；确认项目文件未被其他 TIA 实例占用；"
                        + "归档前勾选“先保存项目”。";
                }

                if (string.Equals(typeName, "FileNotFoundException", StringComparison.Ordinal)
                    || string.Equals(typeName, "FileLoadException", StringComparison.Ordinal)
                    || string.Equals(typeName, "BadImageFormatException", StringComparison.Ordinal))
                {
                    string message = "程序集加载失败：" + current.Message;

                    // 最常见也最容易被误判的一种：程序集名字里带着**编译时**的版本号
                    // （例如 "Siemens.Engineering.Base, Version=21.0.0.0"）。
                    // 这说明本 exe 是拿某个 TIA 版本的 Openness DLL 编出来的，
                    // 运行时就只认那个主版本；本机装的是别的版本时，API 目录选得再对也加载不了。
                    // 原来的提示让人去"检查 TIA 安装是否完整"，方向完全错了 —— 实测踩过。
                    if (current.Message != null
                        && current.Message.IndexOf("Version=", StringComparison.OrdinalIgnoreCase) >= 0
                        && current.Message.IndexOf("Siemens.", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        message += "\r\n这通常是**跨版本**导致的：本程序在编译时绑定了某个 TIA 版本的 Openness 程序集"
                            + "（就是错误信息里 Version= 后面那个版本号），而本机装的是另一个主版本，"
                            + "两者无法互换。请用 --envdump 生成诊断报告，它会打印"
                            + "“本程序需要哪个版本 / 本机有哪些版本 / 各自目录里是什么版本”。"
                            + "\r\n两条出路：① 用本机版本的 Openness DLL 重新编译一份 exe；"
                            + "② 改用“打包项目文件夹（.zip）”模式 —— 它完全不加载 Openness，任何版本都能用。";
                    }
                    else
                    {
                        message += "\r\n建议：确认 TIA Portal 安装完整、Openness 组件已安装，"
                            + "且使用“环境探测”页查看探测到的 API 目录是否正确（也可用 --envdump 生成诊断报告）。";
                    }

                    return message;
                }

                if (string.Equals(typeName, "UnauthorizedAccessException", StringComparison.Ordinal))
                {
                    return "没有权限访问：" + current.Message
                        + "\r\n建议：换一个可写目录作为输出位置，或右键以管理员身份运行（TIA 安装目录需要读权限）。";
                }

                if (string.Equals(typeName, "IOException", StringComparison.Ordinal))
                {
                    return "文件系统错误：" + current.Message
                        + "\r\n建议：确认目标文件没有被其他程序（尤其是另一个 TIA 实例）占用。";
                }

                current = current.InnerException;
            }

            return exception.Message;
        }

        /// <summary>
        /// 打包项目文件夹（.zip）。
        ///
        /// 这个方法属于"第一半" —— 它完全不引用 Siemens 类型，所以既不需要挂程序集解析钩子、
        /// 也不需要启动 TIA Portal 就能调用。这正是"压缩包"模式的价值：
        /// 没装 Openness、或者版本不匹配时，照样能做出可用的备份。
        /// </summary>
        /// <param name="logger">日志器。</param>
        /// <param name="projectFilePath">项目文件路径（用来定位它所在的文件夹）。</param>
        /// <param name="zipOutputPath">输出 zip 路径。</param>
        /// <param name="onProgress">进度回调（已完成文件数, 总文件数, 当前文件）。</param>
        /// <returns>打包统计。</returns>
        public static FolderPackResult PackProjectFolder(
            Logger logger,
            string projectFilePath,
            string zipOutputPath,
            Action<long, long, string> onProgress)
        {
            return FolderPackager.PackProject(projectFilePath, zipOutputPath, logger, onProgress);
        }

        // ───────────────────────────────────────────────────────────────
        //  第二半：引用 Siemens 类型，只能在 InstallResolver 成功之后调用
        // ───────────────────────────────────────────────────────────────
        /// <summary>
        /// 执行归档：打开项目 →（可选）保存 → 归档 → （可选）关闭项目。
        /// </summary>
        /// <param name="logger">日志器。</param>
        /// <param name="request">参数。</param>
        /// <returns>归档产物路径；无法定位产物时返回 null。</returns>
        public static string Archive(Logger logger, ArchiveRequest request)
        {
            // 归档模式提前解析：非法值在这里就报错，不必等 TIA 起来几十秒。
            ArchiveMode mode = ArchiveService.ParseArchivationMode(request.ModeKeyword);
            logger.Info("归档模式：" + request.ModeKeyword + " → " + mode);

            if (ArchiveService.IsIrreversibleMode(mode))
            {
                logger.Warning("当前归档模式会丢弃可恢复数据，操作不可逆。");
                logger.Warning("归档后的项目将无法再用于下载 / 在线比较。");
            }

            bool keepOpen = ResolveKeepOpen(request.StartMode, request.KeepProjectOpen, request.KeepOpenSpecified, logger);

            if (request.Upgrade)
            {
                logger.Warning("已启用“旧版本项目升级打开”（OpenWithUpgrade）。");
                logger.Warning("TIA 会把该项目升级到本机安装的主版本；若同时勾了“归档前先保存项目”，"
                    + "原项目文件会被升级后的版本覆盖，且无法回退。");
                logger.Warning("如果不是在项目副本上操作，请先取消。");
            }

            // 版本体检：注定失败的组合当场拦下，不去启动 TIA（冷启动几十秒）
            EnsureProjectVersionUsable(logger, request.ProjectPath, request.Upgrade);

            // 目标体检：归档产物不能落在项目自身目录（TIA 会拒绝，V19/V21 实测），同样启动前拦下
            EnsureArchiveTargetUsable(logger, request.ProjectPath, request.OutputPath);

            // 目标已存在且未授权覆盖：也提前拦下（TIA 不会覆盖，让它跑到归档步只会白等）
            EnsureArchiveOutputAvailable(logger, request.OutputPath, request.OverwriteExisting);

            string productPath = null;
            ArchiveService service = new ArchiveService(logger);

            using (TiaSession session = CreateSession(request.StartMode, logger))
            {
                object project = session.OpenProject(request.ProjectPath, request.Upgrade);

                if (request.SaveFirst)
                {
                    SaveProject(project, logger);
                }

                FileInfo product = service.ArchiveProject(
                    project, request.OutputPath, request.ModeKeyword, request.OverwriteExisting);
                if (product != null)
                {
                    productPath = product.FullName;
                    logger.Ok("归档产物：" + product.FullName
                        + "（" + SizeFormat.Format(product.Length) + "）");
                }

                if (!keepOpen)
                {
                    session.CloseProject();
                }
            }

            return productPath;
        }

        /// <summary>
        /// 执行恢复（解包）：把归档文件还原成项目并在 TIA 里打开。
        /// </summary>
        /// <param name="logger">日志器。</param>
        /// <param name="request">参数。</param>
        /// <returns>还原出来的项目路径；拿不到路径时返回空串。</returns>
        public static string Retrieve(Logger logger, RetrieveRequest request)
        {
            logger.Info("归档文件：" + request.ArchiveFilePath);
            logger.Info("解包目录：" + request.TargetDirectory);
            logger.Info("实例方式：" + DescribeStartMode(request.StartMode));

            if (request.Upgrade)
            {
                logger.Warning("已启用“升级后打开”（RetrieveWithUpgrade）：归档来自更早版本时，"
                    + "项目会被升级到本机 TIA 主版本。");
                logger.Warning("若同时勾了“解包后保存项目”，磁盘上的项目就会变成新版本，且无法回退。");
            }

            bool keepOpen = ResolveKeepOpen(
                request.StartMode, request.KeepProjectOpen, request.KeepOpenSpecified, logger);

            string projectPath = string.Empty;
            ArchiveService service = new ArchiveService(logger);

            using (TiaSession session = CreateSession(request.StartMode, logger))
            {
                object project = service.RetrieveProject(
                    session.Portal,
                    request.ArchiveFilePath,
                    request.TargetDirectory,
                    request.Upgrade);

                projectPath = TryGetProjectPath(project);
                if (!string.IsNullOrEmpty(projectPath))
                {
                    logger.Info("项目路径：" + projectPath);
                }

                if (request.SaveAfterRetrieve)
                {
                    SaveProject(project, logger);
                }

                if (!keepOpen)
                {
                    session.CloseProject();
                }
            }

            return projectPath;
        }

        /// <summary>
        /// 环境探测（反射打印本机 Openness 的真实签名），不启动 TIA。
        /// </summary>
        /// <param name="environment">环境信息。</param>
        /// <param name="logger">日志器。</param>
        /// <param name="assemblyNameFilter">程序集名过滤关键字，可为空。</param>
        public static void Probe(TiaEnvironmentInfo environment, Logger logger, string assemblyNameFilter)
        {
            ApiProbe.Run(environment, logger, assemblyNameFilter);
        }

        /// <summary>
        /// 批量归档：**只启动一次 TIA Portal**，然后循环处理所有项目。
        ///
        /// 为什么强调"只启动一次"：TIA Portal 冷启动动辄几十秒，如果每个项目都新建一次实例，
        /// 10 个项目光启动就要等十几分钟。共用同一个实例可以把这个开销压成一份。
        /// 代价是每处理完一个项目都必须把项目关掉（CloseProject），否则下一个打不开 ——
        /// 这一步放在 finally 里，保证失败的项目也不会把整批卡死。
        ///
        /// 单个项目失败不会中断整批：记录下来继续跑，最后统一汇总。
        ///
        /// ★ 若 <see cref="BatchArchiveRequest.AutoSelectKernel"/> 为真：
        ///   版本与"本进程已绑定的内核"不同的项目，会按版本分组交给**子进程**原生归档
        ///   （一个进程只能绑定一个版本的 Siemens.Engineering 程序集）。
        /// </summary>
        /// <param name="logger">日志器。</param>
        /// <param name="request">批量参数。</param>
        /// <param name="isCancelled">取消判定委托，可为 null。每处理一个项目前询问一次。</param>
        /// <param name="onProgress">进度回调（已完成数, 总数, 当前项目），可为 null。会从后台线程调用。</param>
        /// <returns>每个项目的执行结果。</returns>
        public static List<BatchArchiveResult> ArchiveMany(
            Logger logger,
            BatchArchiveRequest request,
            Func<bool> isCancelled,
            Action<int, int, string> onProgress)
        {
            List<BatchArchiveResult> results = new List<BatchArchiveResult>();

            if (request == null || request.Items == null || request.Items.Count == 0)
            {
                logger.Warning("没有需要归档的项目。");
                return results;
            }

            int total = request.Items.Count;

            // 归档模式先解析：非法值在启动 TIA 之前就报错
            ArchiveMode mode = ArchiveService.ParseArchivationMode(request.ModeKeyword);
            logger.Section("批量归档开始");
            logger.Info("待归档项目：" + total + " 个");
            logger.Info("归档模式：" + request.ModeKeyword + " → " + mode);
            logger.Info("每个项目归档前保存：" + (request.SaveFirst ? "是" : "否"));
            logger.Info("旧版本项目升级打开：" + (request.Upgrade ? "是" : "否"));

            if (ArchiveService.IsIrreversibleMode(mode))
            {
                logger.Warning("当前模式会丢弃可恢复数据，且对每一个项目都生效。");
            }

            if (request.Upgrade)
            {
                logger.Warning("已启用升级打开：每个旧版本项目都会被升级到本机 TIA 主版本，不可回退。");
            }

            // ── 按项目版本分组：版本与"本进程**实际绑定**的内核"不同、且本机装了对应 Openness 的，
            //    交给子进程处理（子进程绑定匹配的 API 目录 → 原生打开、不升级）。
            //    原因：一个进程同一时刻只能绑定一个版本的 Siemens.Engineering 程序集。
            //
            // ★ "本进程内核"必须取**解析钩子实际登记的那个版本**（BoundMajorVersion），
            //   **不能**取 OpennessApi.Current —— 实测踩到过这个坑（V18 归档必崩）：
            //     · 界面是长驻进程：先跑过 V20（或点过“环境探测”），解析钩子登记的就是 V20；
            //     · 之后把“可用版本”切到 V18 再批量归档 → MainForm 会先 InstallResolver(V18)，
            //       于是 OpennessApi.Current 变成 18，但解析目录**仍是 V20**（钩子只登记一次）；
            //     · 旧代码按 Current 判断 → V18 项目被判成"与内核一致 → 进程内处理"，
            //       结果在父进程里加载出“V18 主程序集 + V20 依赖”的混合体 →
            //       MissingMethodException：找不到 IAppSupport.Initialize(Process)。
            //   改用 BoundMajorVersion 后，这种"想换但换不了"的项目会被判为与内核不同，
            //   自动改走子进程（干净进程重新绑定 V18），从根上避开混合加载。
            int boundKernel = TiaEnvironment.BoundMajorVersion;
            if (boundKernel <= 0 && OpennessApi.Current != null)
            {
                // 解析钩子尚未登记（正常流程不会走到）：退回最后一次 Bind 的版本，聊胜于无
                boundKernel = OpennessApi.Current.MajorVersion;
            }

            List<BatchArchiveItem> localItems = new List<BatchArchiveItem>();
            List<KernelGroup> remoteGroups = new List<KernelGroup>();

            if (request.AutoSelectKernel)
            {
                Dictionary<int, string> availableKernels = CollectAvailableKernels(logger);
                foreach (BatchArchiveItem item in request.Items)
                {
                    int projectVersion = ArchiveNaming.GetProjectMajorVersion(item.ProjectPath);
                    string apiDirectory;
                    if (projectVersion > 0 && projectVersion != boundKernel
                        && availableKernels.TryGetValue(projectVersion, out apiDirectory))
                    {
                        KernelGroup group = null;
                        foreach (KernelGroup candidate in remoteGroups)
                        {
                            if (candidate.Version == projectVersion)
                            {
                                group = candidate;
                                break;
                            }
                        }

                        if (group == null)
                        {
                            group = new KernelGroup();
                            group.Version = projectVersion;
                            group.ApiDirectory = apiDirectory;
                            remoteGroups.Add(group);
                        }

                        group.Items.Add(item);
                    }
                    else
                    {
                        localItems.Add(item);
                    }
                }
            }
            else
            {
                localItems.AddRange(request.Items);
            }

            ArchiveService service = new ArchiveService(logger);
            int completed = 0;
            int succeeded = 0;
            int failed = 0;

            if (request.AutoSelectKernel)
            {
                logger.Section("内核策略：按项目版本自动选内核");
                logger.Info("本进程**已绑定**的内核：V" + boundKernel
                    + (string.IsNullOrEmpty(TiaEnvironment.BoundApiDirectory)
                        ? string.Empty
                        : "（" + TiaEnvironment.BoundApiDirectory + "）"));
                logger.Info("本批**选定**的内核："
                    + (string.IsNullOrEmpty(request.ApiDirectory)
                        ? "自动探测"
                        : request.ApiDirectory));

                if (remoteGroups.Count == 0)
                {
                    logger.Info("本批项目的版本都与本进程已绑定的内核一致，不需要子进程。");
                }
                else
                {
                    // 选定的内核与已绑定的不一致时，说清为什么必须换进程 ——
                    // 否则用户会疑惑"我明明选了 V18，怎么日志说内核是 V20"。
                    bool forcedByBinding = boundKernel > 0
                        && !string.IsNullOrEmpty(request.ApiDirectory)
                        && !string.Equals(request.ApiDirectory, TiaEnvironment.BoundApiDirectory,
                            StringComparison.OrdinalIgnoreCase);

                    if (forcedByBinding)
                    {
                        logger.Warning("本进程已绑定 V" + boundKernel + " 的 Openness 程序集（"
                            + TiaEnvironment.BoundApiDirectory + "），**无法在进程内改绑**到 "
                            + request.ApiDirectory + "。");
                        logger.Warning("  程序集解析钩子只登记一次；在同一个进程里换版本会加载出"
                            + "“新版本主程序集 + 旧版本依赖”的混合体，报 MissingMethodException 之类的怪错。");
                        logger.Warning("  因此本批改走**子进程**：每个版本一个干净进程重新绑定，互不干扰。");
                    }

                    foreach (KernelGroup group in remoteGroups)
                    {
                        logger.Info("  V" + group.Version + "：" + group.Items.Count + " 个项目 → 子进程用 "
                            + group.ApiDirectory);
                    }
                    logger.Info("子进程会用与项目同版本的内核**原生打开**（不升级），产物扩展名也是该版本。");
                }
            }

            bool aborted = false;

            // 先处理需要换内核的那些：每个版本一个子进程（组内共用一个 TIA 实例）
            foreach (KernelGroup group in remoteGroups)
            {
                if (isCancelled != null && isCancelled())
                {
                    logger.Warning("收到中止请求：V" + group.Version + " 的 "
                        + group.Items.Count + " 个项目未处理。");
                    aborted = true;
                    break;
                }

                RunKernelGroupInChild(logger, request, group, results, ref completed, ref succeeded, ref failed,
                    total, onProgress);
            }

            // 逐项版本体检：把注定失败的挑出来单独记账，**不进 TIA 循环**（省得白等冷启动）。
            List<BatchArchiveItem> usableItems = new List<BatchArchiveItem>();
            foreach (BatchArchiveItem item in localItems)
            {
                try
                {
                    EnsureProjectVersionUsable(logger, item.ProjectPath, request.Upgrade);
                    EnsureArchiveTargetUsable(logger, item.ProjectPath, item.OutputPath);
                    EnsureArchiveOutputAvailable(logger, item.OutputPath, request.OverwriteExisting);
                    usableItems.Add(item);
                }
                catch (ToolException ex)
                {
                    BatchArchiveResult skipped = new BatchArchiveResult();
                    skipped.ProjectPath = item.ProjectPath;
                    skipped.OutputPath = item.OutputPath;
                    skipped.Success = false;
                    skipped.Message = ex.Message;

                    results.Add(skipped);
                    failed++;
                    completed++;
                    logger.Error("跳过（前置体检未通过，未尝试归档）：" + Path.GetFileName(item.ProjectPath));
                    logger.Warning(ex.Message);

                    if (onProgress != null)
                    {
                        onProgress(completed, total, item.ProjectPath);
                    }
                }
            }

            localItems = usableItems;

            // 本进程能处理的那些：共用一次 TIA 实例
            if (localItems.Count > 0 && !aborted)
            {
                using (TiaSession session = CreateSession(request.StartMode, logger))
                {
                    foreach (BatchArchiveItem item in localItems)
                    {
                        if (isCancelled != null && isCancelled())
                        {
                            logger.Warning("收到中止请求：还有 " + (total - completed) + " 个项目未处理。");
                            break;
                        }

                        completed++;
                        logger.Section("[" + completed + "/" + total + "] " + Path.GetFileName(item.ProjectPath));

                        BatchArchiveResult result = new BatchArchiveResult();
                        result.ProjectPath = item.ProjectPath;
                        result.OutputPath = item.OutputPath;

                        try
                        {
                            object project = session.OpenProject(item.ProjectPath, request.Upgrade);

                            if (request.SaveFirst)
                            {
                                SaveProject(project, logger);
                            }

                            FileInfo product = service.ArchiveProject(
                                project, item.OutputPath, request.ModeKeyword, request.OverwriteExisting);

                            if (product != null)
                            {
                                result.ProductBytes = product.Length;
                                logger.Ok("归档产物：" + product.FullName
                                    + "（" + SizeFormat.Format(product.Length) + "）");
                            }

                            result.Success = true;
                            succeeded++;
                        }
                        catch (Exception ex)
                        {
                            result.Success = false;
                            result.Message = ExplainFailure(ex);
                            failed++;
                            logger.Error("本项归档失败：" + result.Message);
                            logger.Warning("记录后继续处理下一个项目。");
                        }
                        finally
                        {
                            // ★ 关键：无论成功失败都要关闭项目。批量场景下不关就会导致下一个项目打不开。
                            try
                            {
                                session.CloseProject();
                            }
                            catch (Exception closeError)
                            {
                                logger.Debug("关闭项目时出现异常（已忽略）：" + closeError.Message);
                            }
                        }

                        results.Add(result);

                        if (onProgress != null)
                        {
                            onProgress(completed, total, item.ProjectPath);
                        }
                    }
                }
            }

            logger.Section("批量归档结束");
            logger.Info("共 " + completed + " / " + total + " 项已处理：成功 " + succeeded + " 个，失败 " + failed + " 个");

            if (failed > 0)
            {
                logger.Warning("以下项目未成功归档：");
                foreach (BatchArchiveResult failure in results)
                {
                    if (!failure.Success)
                    {
                        logger.Warning("  · " + failure.ProjectPath + " —— " + failure.Message);
                    }
                }
            }

            return results;
        }

        /// <summary>
        /// **归档目标体检**：拦下"归档产物落在项目目录里"的组合（同目录与子目录都不行）。
        ///
        /// 为什么要有它（实测，V19 与 V21 + 无界面实例）：
        ///   TIA 的 Project.Archive **拒绝把归档产物写进项目所在的目录树**，报
        ///   "Unable to archive the project. / Archiving failed. / 项目目录已存在，无法保存。请选择一个不同的路径。"
        ///   实测矩阵（同一项目、同一无界面实例）：
        ///     · 输出到项目自身目录     → 失败（V19 用户机与 V21 本机都复现）；
        ///     · 输出到项目目录的子目录 → 失败（V21 实测，同样报“项目目录已存在”）；
        ///     · 输出到项目目录的上一级 → **成功**（V21 实测，产物正常生成）；
        ///     · 批量归档另选输出目录   → 成功（用户实测）。
        ///   结论：只要目标目录位于项目目录之内（含等于）就必然失败，与版本 / 权限 /
        ///   项目本身都无关；所以默认建议路径改成"项目目录的上一级"。
        ///
        /// 放在"启动 TIA 之前"的原因与版本体检相同：否则用户白等几十秒冷启动，
        /// 最后拿到的还是一句没头没脑的英文异常。
        ///
        /// 注意：打包项目文件夹（.zip）不经过 Openness，不在拦截范围（调用方自行区分）。
        /// </summary>
        /// <param name="logger">日志器，可为 null。</param>
        /// <param name="projectPath">项目文件路径。</param>
        /// <param name="outputPath">归档输出文件路径。</param>
        /// <exception cref="ToolException">目标落在项目自身目录时抛出（消息里含替代路径建议）。</exception>
        public static void EnsureArchiveTargetUsable(Logger logger, string projectPath, string outputPath)
        {
            if (string.IsNullOrWhiteSpace(projectPath) || string.IsNullOrWhiteSpace(outputPath))
            {
                return;
            }

            string projectDirectory;
            string targetDirectory;
            try
            {
                projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectPath));
                targetDirectory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            }
            catch (Exception ex)
            {
                // 路径本身解析不了不在这里拦：后面的 Open / 归档调用会给出更准确的报错。
                if (logger != null)
                {
                    logger.Debug("归档目标体检跳过（路径无法解析）：" + ex.Message);
                }
                return;
            }

            if (string.IsNullOrEmpty(projectDirectory) || string.IsNullOrEmpty(targetDirectory))
            {
                return;
            }

            string normalizedProject = projectDirectory.TrimEnd('\\', '/');
            string normalizedTarget = targetDirectory.TrimEnd('\\', '/');

            bool sameAsProject = string.Equals(normalizedTarget, normalizedProject, StringComparison.OrdinalIgnoreCase);
            bool insideProject = IsUnder(normalizedTarget, normalizedProject);
            if (!sameAsProject && !insideProject)
            {
                return;
            }

            if (logger != null)
            {
                logger.Warning("归档目标不安全：" + targetDirectory
                    + (sameAsProject ? "（就是项目自身目录）" : "（在项目目录内部）"));
            }

            string suggested = SuggestOutsidePath(projectDirectory, Path.GetFileName(outputPath));
            throw new ToolException(
                ExitCodes.Usage,
                "归档输出不能放在项目目录里 —— 目标位置"
                + (sameAsProject ? "就是项目自身所在的目录" : "在项目目录内部（子目录同样不行）") + "。\r\n"
                + "  项目目录：" + projectDirectory + "\r\n"
                + "  现在的归档到：" + outputPath + "\r\n"
                + "实测（V21 与 V19）：TIA 的归档只要目标落在项目目录之内就必然失败，"
                + "报“项目目录已存在，无法保存。请选择一个不同的路径。”；"
                + "换到项目目录之外（例如上一级）就正常 —— 与版本 / 权限 / 项目本身都无关。\r\n"
                + "建议改成：" + suggested);
        }

        /// <summary>
        /// childDirectory 是否位于 ancestorDirectory 内部（不相等；按完整路径比较，忽略大小写）。
        /// </summary>
        private static bool IsUnder(string childDirectory, string ancestorDirectory)
        {
            return childDirectory.StartsWith(
                ancestorDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 给一个"项目目录之外"的替代输出路径：优先用上一级目录；项目就在盘根时返回提示文本。
        /// </summary>
        private static string SuggestOutsidePath(string projectDirectory, string outputFileName)
        {
            string parent = Path.GetDirectoryName(projectDirectory.TrimEnd('\\', '/'));
            if (string.IsNullOrEmpty(parent))
            {
                return "（项目就在盘根，没有上一级目录可用，请改到别的盘或别的目录）"
                    + Path.Combine("其它目录", outputFileName);
            }

            return Path.Combine(parent, outputFileName);
        }

        /// <summary>
        /// **归档输出可用性体检**：目标已存在且没有"覆盖"授权时，当场拦下。
        ///
        /// 为什么：TIA 的 Project.Archive **不会覆盖**已存在的目标文件 / 文件夹（V21 实测）：
        ///   "Archive Operation is not possible as the target file/folder '…' is already exist."
        /// 与其让 TIA 在归档那一刻抛英文异常（还要先白等启动 + 打开项目），
        /// 不如和版本 / 目标体检一样，在启动 TIA 之前给一句中文 + 办法。
        ///
        /// 注意：这里只做"提前拦截"；真正的覆盖动作（改名备份 → 归档 → 删备份 / 回滚）
        /// 在 <see cref="ArchiveService.ArchiveProject"/> 里，两边判据一致（文件是否存在 + 是否授权覆盖）。
        /// </summary>
        /// <param name="logger">日志器，可为 null。</param>
        /// <param name="outputPath">归档输出文件路径。</param>
        /// <param name="overwriteExisting">调用方是否已获用户"覆盖"授权。</param>
        /// <exception cref="ToolException">目标已存在且未授权覆盖时抛出。</exception>
        public static void EnsureArchiveOutputAvailable(Logger logger, string outputPath, bool overwriteExisting)
        {
            if (overwriteExisting || string.IsNullOrWhiteSpace(outputPath))
            {
                return;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(outputPath);
            }
            catch (Exception)
            {
                // 路径解析不了不在这里拦：后面会给出更准确的报错。
                return;
            }

            if (!File.Exists(fullPath))
            {
                return;
            }

            if (logger != null)
            {
                logger.Warning("归档目标已存在且未选择覆盖：" + fullPath);
            }

            throw new ToolException(
                ExitCodes.InputOutput,
                "归档目标已存在，且没有选择“覆盖”：\r\n  " + fullPath + "\r\n"
                + "TIA 的归档不会覆盖已存在的文件，直接调用必然失败"
                + "（实测报 “Archive Operation is not possible as the target file/folder … is already exist.”）。\r\n"
                + "办法：换个文件名（加时间戳 / 后缀 / 序号），或在界面上选“覆盖”"
                + "（覆盖会先把旧文件改名为 .old 备份，归档成功后才删除它）。");
        }

        // ───────────────────────────────────────────────────────────────
        //  按项目版本换内核（子进程）
        // ───────────────────────────────────────────────────────────────

        /// <summary>
        /// **版本体检**：在启动 TIA 之前判断"这个项目用当前内核能不能归档"，不能就直接抛出带办法的异常。
        ///
        /// 为什么要有它：TIA 冷启动几十秒，注定失败的事不该让它白等。实测两个注定失败的组合：
        ///   · 项目比内核**新**（拿 V19 内核去开 V20 项目）→ TIA 直接不接受；
        ///   · 项目比内核**旧**、本机也没有那个版本的 Openness、又没勾"升级打开" → TIA 不接受低版本项目。
        /// 这两种情况现在当场拦下，并把可选办法写在异常消息里。
        /// </summary>
        /// <param name="logger">日志器。</param>
        /// <param name="projectPath">项目文件路径。</param>
        /// <param name="upgrade">是否已勾选"旧版本项目升级打开"。</param>
        /// <exception cref="ToolException">该组合注定失败时抛出（消息里含解决办法）。</exception>
        public static void EnsureProjectVersionUsable(Logger logger, string projectPath, bool upgrade)
        {
            int projectVersion = ArchiveNaming.GetProjectMajorVersion(projectPath);
            if (projectVersion <= 0)
            {
                // 认不出项目版本（文件名不是 .apXX）就不拦，交给 TIA 自己判断
                logger.Debug("认不出项目版本（" + Path.GetFileName(projectPath) + "），跳过版本体检。");
                return;
            }

            int kernel = OpennessApi.Current == null ? 0 : OpennessApi.Current.MajorVersion;
            if (kernel <= 0)
            {
                return;
            }

            if (projectVersion == kernel)
            {
                return;
            }

            if (projectVersion > kernel)
            {
                throw new ToolException(
                    TiaOpennessKit.ExitCodes.Api,
                    "项目是 V" + projectVersion + "，比当前内核（V" + kernel + "）新 —— 这个内核打不开它，"
                    + "不必再试了。\r\n"
                    + "办法：把“可用版本”切到 V" + projectVersion + "（前面要有 V" + projectVersion
                    + " 的 Openness），或者换一台装了 V" + projectVersion + " 及以上版本的机器。");
            }

            // projectVersion < kernel
            bool matchingKernelAvailable = false;
            try
            {
                matchingKernelAvailable = CollectAvailableKernels(logger).ContainsKey(projectVersion);
            }
            catch (Exception)
            {
                // 枚举失败就当没有，下面按"没有"处理
            }

            if (matchingKernelAvailable)
            {
                logger.Warning("提示：项目是 V" + projectVersion + "，本机也装了 V" + projectVersion
                    + " 的 Openness。更推荐把“可用版本”切到 V" + projectVersion
                    + "：原生打开、不升级、产物是 .zap" + projectVersion + "。");
                if (upgrade)
                {
                    logger.Warning("当前勾选了“升级打开”，将用 V" + kernel
                        + " 打开它，产物为 .zap" + kernel + "。");
                }
                return;
            }

            if (!upgrade)
            {
                throw new ToolException(
                    TiaOpennessKit.ExitCodes.Api,
                    "项目是 V" + projectVersion + "，当前内核（V" + kernel + "）不接受更低版本的项目，"
                    + "而本机没有 V" + projectVersion + " 的 Openness，也没勾“旧版本项目升级打开” —— "
                    + "这样一定失败，不必再试了。\r\n"
                    + "两条路：① 勾选“旧版本项目升级打开”（会把项目升级到 V" + kernel
                    + " 打开，产物 .zap" + kernel + "）；"
                    + "② 给 V" + projectVersion + " 补装 Openness 组件，再把“可用版本”切过去（不升级）。");
            }
        }

        /// <summary>
        /// 枚举本机装了 Openness 的版本 → API 目录。
        /// 同一版本在多个目录下都有程序集时（例如 V19 与 V19\net48），取程序集最多的那个。
        /// </summary>
        private static Dictionary<int, string> CollectAvailableKernels(Logger logger)
        {
            Dictionary<int, string> map = new Dictionary<int, string>();
            Dictionary<int, int> counts = new Dictionary<int, int>();

            try
            {
                foreach (TiaEnvironmentInfo info in TiaEnvironment.EnumerateInstalled(logger))
                {
                    if (info == null || info.MajorVersion <= 0)
                    {
                        continue;
                    }

                    int count = info.Assemblies == null ? 0 : info.Assemblies.Count;
                    int existing;
                    if (!map.ContainsKey(info.MajorVersion)
                        || (counts.TryGetValue(info.MajorVersion, out existing) && count > existing))
                    {
                        map[info.MajorVersion] = info.ApiDirectory;
                        counts[info.MajorVersion] = count;
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Debug("枚举本机 Openness 版本失败（按版本换内核将不可用）：" + ex.Message);
            }

            return map;
        }

        /// <summary>
        /// 把一组"同一版本"的项目交给子进程**原生归档**（不升级）。
        ///
        /// 为什么要子进程：一个进程只能绑定一个版本的 Siemens.Engineering 程序集，
        /// 想用 V16 的内核归档 .ap16 项目，就只能另起一个进程去绑定 V16。
        /// </summary>
        /// <param name="logger">父进程日志器。</param>
        /// <param name="request">批量参数。</param>
        /// <param name="group">同一版本的一组项目。</param>
        /// <param name="results">结果列表（会被追加）。</param>
        /// <param name="completed">已完成计数（会被累加）。</param>
        /// <param name="succeeded">成功计数（会被累加）。</param>
        /// <param name="failed">失败计数（会被累加）。</param>
        /// <param name="total">总数。</param>
        /// <param name="onProgress">进度回调。</param>
        private static void RunKernelGroupInChild(
            Logger logger,
            BatchArchiveRequest request,
            KernelGroup group,
            List<BatchArchiveResult> results,
            ref int completed,
            ref int succeeded,
            ref int failed,
            int total,
            Action<int, int, string> onProgress)
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture)
                + "-V" + group.Version;
            string jobPath = Path.Combine(Path.GetTempPath(), "TiaArchiveGui-child-" + stamp + ".job");
            string resultPath = jobPath + ".result.txt";

            if (File.Exists(resultPath))
            {
                try
                {
                    File.Delete(resultPath);
                }
                catch (Exception)
                {
                    // 删不掉就算了，下面读的时候按行覆盖
                }
            }

            BatchChildJob job = new BatchChildJob();
            job.ApiDirectory = group.ApiDirectory;
            job.ModeKeyword = request.ModeKeyword;
            job.SaveFirst = request.SaveFirst;
            job.OverwriteExisting = request.OverwriteExisting;
            // 内核与项目同版本，不需要升级打开
            job.Upgrade = false;
            job.Verbose = request.Verbose;
            job.ResultPath = resultPath;
            foreach (BatchArchiveItem item in group.Items)
            {
                BatchChildItem childItem = new BatchChildItem();
                childItem.ProjectPath = item.ProjectPath;
                childItem.OutputPath = item.OutputPath;
                job.Items.Add(childItem);
            }

            logger.Section("子进程原生归档：V" + group.Version + "（" + group.Items.Count + " 个项目，不升级）");
            logger.Info("子进程内核目录：" + group.ApiDirectory);

            int exitCode;
            try
            {
                job.Save(jobPath);
                logger.Debug("任务文件：" + jobPath);
                exitCode = RunChildProcess(logger, jobPath);
                logger.Info("子进程已退出，退出码 " + exitCode + "。");
            }
            catch (Exception ex)
            {
                exitCode = TiaOpennessKit.ExitCodes.Api;
                logger.Error("拉起子进程失败：" + ex.GetType().Name + "：" + ex.Message);
            }

            Dictionary<string, string[]> parsed = ParseChildResults(resultPath);

            foreach (BatchArchiveItem item in group.Items)
            {
                BatchArchiveResult result = new BatchArchiveResult();
                result.ProjectPath = item.ProjectPath;
                result.OutputPath = item.OutputPath;

                string[] parsedItem;
                if (parsed.TryGetValue(item.ProjectPath, out parsedItem))
                {
                    result.Success = parsedItem[0] == "1";

                    long bytes;
                    if (long.TryParse(parsedItem[1], System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture, out bytes))
                    {
                        result.ProductBytes = bytes;
                    }

                    result.Message = parsedItem[2];
                }
                else
                {
                    result.Success = false;
                    result.Message = "子进程没有返回该项的结果（可能启动失败或被提前结束，退出码 "
                        + exitCode + "）。详见上面的子进程日志。";
                }

                if (result.Success)
                {
                    succeeded++;
                }
                else
                {
                    failed++;
                    logger.Error("本项归档失败（子进程）：" + Path.GetFileName(item.ProjectPath)
                        + " —— " + result.Message);
                }

                results.Add(result);
                completed++;

                if (onProgress != null)
                {
                    onProgress(completed, total, item.ProjectPath);
                }
            }
        }

        /// <summary>
        /// 启动子进程（就是本程序自己的隐藏模式 --archive-child），并把它的标准输出实时转发到界面日志。
        /// </summary>
        /// <param name="logger">日志器。</param>
        /// <param name="jobPath">任务文件路径。</param>
        /// <returns>子进程退出码。</returns>
        private static int RunChildProcess(Logger logger, string jobPath)
        {
            string exePath = Process.GetCurrentProcess().MainModule.FileName;

            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = exePath;
            startInfo.Arguments = "--archive-child \"" + jobPath + "\"";
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            // 子进程按 UTF-8 输出（见 Program.ArchiveChild），这里必须对齐，否则中文日志乱码
            startInfo.StandardOutputEncoding = Encoding.UTF8;
            startInfo.StandardErrorEncoding = Encoding.UTF8;

            using (Process process = Process.Start(startInfo))
            {
                if (process == null)
                {
                    throw new ToolException(TiaOpennessKit.ExitCodes.Api, "无法启动子进程：" + exePath);
                }

                process.OutputDataReceived += delegate (object sender, DataReceivedEventArgs e)
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        logger.Info("  │ " + e.Data);
                    }
                };
                process.ErrorDataReceived += delegate (object sender, DataReceivedEventArgs e)
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        logger.Warning("  │ " + e.Data);
                    }
                };

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.WaitForExit();

                return process.ExitCode;
            }
        }

        /// <summary>
        /// 读子进程写回的结果文件：项目路径 → [是否成功, 字节数, 说明]。
        /// </summary>
        /// <param name="resultPath">结果文件。</param>
        /// <returns>字典；文件不存在时返回空字典。</returns>
        private static Dictionary<string, string[]> ParseChildResults(string resultPath)
        {
            Dictionary<string, string[]> parsed =
                new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrEmpty(resultPath) || !File.Exists(resultPath))
            {
                return parsed;
            }

            string[] lines;
            try
            {
                lines = File.ReadAllLines(resultPath, Encoding.UTF8);
            }
            catch (Exception)
            {
                return parsed;
            }

            foreach (string line in lines)
            {
                if (string.IsNullOrEmpty(line))
                {
                    continue;
                }

                string[] parts = line.Split('\t');
                if (parts.Length < 4)
                {
                    continue;
                }

                parsed[parts[0]] = new string[] { parts[1], parts[2], parts[3] };
            }

            return parsed;
        }

        /// <summary>把 TIA 实例方式翻译成一句人话（日志用）。界面层也调这个方法，别再各写一份。</summary>
        /// <param name="startMode">实例方式。</param>
        /// <returns>中文描述。</returns>
        internal static string DescribeStartMode(TiaOpennessKit.Cli.TiaStartMode startMode)
        {
            switch (startMode)
            {
                case TiaOpennessKit.Cli.TiaStartMode.WithUserInterface:
                    return "启动带界面 TIA 实例";
                case TiaOpennessKit.Cli.TiaStartMode.AttachExisting:
                    return "附加到已运行的 TIA 实例";
                default:
                    return "启动无界面 TIA 实例";
            }
        }

        /// <summary>同一内核版本的一组项目。</summary>
        private sealed class KernelGroup
        {
            /// <summary>内核主版本号。</summary>
            public int Version;

            /// <summary>该版本的 API 目录。</summary>
            public string ApiDirectory;

            /// <summary>属于这一组的项目。</summary>
            public readonly List<BatchArchiveItem> Items = new List<BatchArchiveItem>();
        }

        // ───────────────────────────────────────────────────────────────
        //  内部辅助
        // ───────────────────────────────────────────────────────────────

        /// <summary>
        /// 决定结束后是否保持项目打开。附加到用户自己正在使用的 TIA 实例时，
        /// 未经明确指定一律保持打开 —— 否则用户会觉得"我只是归档，项目怎么被关了"。
        /// </summary>
        private static bool ResolveKeepOpen(
            TiaStartMode startMode,
            bool keepOpen,
            bool specified,
            Logger logger)
        {
            if (startMode == TiaStartMode.AttachExisting && !specified)
            {
                logger.Warning("当前是“附加到已运行的 TIA”模式，默认保持项目打开 —— "
                    + "这个实例是你自己在用的，工具不会替你把它正在编辑的项目关掉。");
                return true;
            }

            return keepOpen;
        }

        private static void SaveProject(object project, Logger logger)
        {
            if (project == null)
            {
                return;
            }

            logger.Section("保存项目");
            try
            {
                using (logger.Measure("Project.Save"))
                {
                    OpennessApi.Current.SaveProject(project);
                }
                logger.Ok("项目已保存。");
            }
            catch (Exception ex)
            {
                throw TiaSession.WrapSiemensFailure(ex, "调用 Project.Save 失败。");
            }
        }

        /// <summary>
        /// 尽力读取项目文件路径（不同版本属性的可用性与类型都不一致，读不到就返回 null）。
        ///
        /// 这里刻意用反射而不是直接写 project.Path：一是不必在编译期对某个具体属性名产生
        /// 硬依赖（老版本没有这个属性时照样能编译），二是属性类型在各版本间存在
        /// FileInfo / string 两种可能，用 as 逐个尝试比强转安全。
        /// </summary>
        private static string TryGetProjectPath(object project)
        {
            if (project == null)
            {
                return null;
            }

            try
            {
                System.Reflection.PropertyInfo property = project.GetType().GetProperty(
                    "Path",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

                if (property == null)
                {
                    return null;
                }

                object value = property.GetValue(project, null);
                FileInfo fileInfo = value as FileInfo;
                if (fileInfo != null)
                {
                    return fileInfo.FullName;
                }

                return value as string;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
