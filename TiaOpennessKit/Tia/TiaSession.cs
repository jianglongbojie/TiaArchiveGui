using System;
using System.Collections.Generic;
using System.IO;

namespace TiaOpennessKit.Tia
{
    /// <summary>
    /// TIA Portal 会话的生命周期管理：启动/附加实例、打开项目、成对释放资源。
    ///
    /// ★ 这个类里**没有任何 Siemens 类型**（portal / project 都是 <c>object</c>），
    ///   所有 Openness 调用都走 <see cref="OpennessApi"/> 的运行时反射。
    ///   原因见 OpennessApi 的类注释：Openness 的程序集**名字**在版本之间都不一样
    ///   （V15~V19 是 Siemens.Engineering.dll，V20/V21 是 Siemens.Engineering.Base.dll），
    ///   一旦编译期引用了某个类型，exe 就被钉死在一个版本上。
    ///
    /// 类比 SCL/ST：
    ///   - TiaPortal 就像 FB 的"资源宿主"，new 出来必须 Dispose，否则后台会残留 TIA 进程
    ///     （PLC 侧类比：FB 实例对应的背景 DB / 句柄必须在退出前释放）。
    ///   - C# 的 using 语句 ≈ SCL 里保证成对调用的功能块；只要变量离开作用域就一定 Dispose，
    ///     哪怕中途抛异常也不会漏。这是本项目唯一推荐的用法，不允许手动 Close 了事。
    /// </summary>
    public sealed class TiaSession : IDisposable
    {
        private readonly Logger _logger;
        private object _portal;
        private object _project;
        private bool _disposed;

        /// <summary>
        /// 释放会话时是否要关闭项目（默认 true）。
        ///
        /// ★ **附加模式必须置为 false**：附加到用户已经打开的 TIA 实例时，
        ///   `Project.Close()` 会把**用户正在编辑的那个项目关掉** ——
        ///   用户只是想借他的实例读点信息，结果项目被关掉，这是不能接受的副作用。
        ///   只读遍历（我们不做任何修改、也不 Save）的情况下，直接放弃引用即可。
        /// </summary>
        public bool CloseProjectOnDispose { get; set; }

        /// <summary>
        /// 释放会话时是否要释放（Dispose）TIA Portal 实例（默认 true）。
        ///
        /// ★ **附加模式会被自动置为 false**（见 <see cref="Create"/>）：附加到用户已经打开的
        ///   TIA 实例时，我们只是"借他的实例读点东西"，不应该反过来去释放他的 TIA。
        ///   此前这里没有这个开关，Dispose 一律调用 TiaPortal.Dispose()，与三处注释/日志
        ///   写的"只释放附加会话，TIA 本体也不关"自相矛盾 —— 也就是**代码实际做的事比承诺的多**。
        ///   宁可多留一个附加连接（进程退出即回收），也不冒误关用户 TIA 的风险。
        /// </summary>
        public bool DisposePortalOnDispose { get; set; }

        /// <summary>当前的 TIA Portal 实例（Openness 的 TiaPortal 对象）。</summary>
        public object Portal
        {
            get { return _portal; }
        }

        /// <summary>当前打开的项目（Openness 的 Project 对象）；未打开时为 null。</summary>
        public object Project
        {
            get { return _project; }
        }

        private TiaSession(Logger logger)
        {
            _logger = logger;
            _portal = null;
            _project = null;
            _disposed = false;
            CloseProjectOnDispose = true;
            DisposePortalOnDispose = true;
        }

        /// <summary>
        /// 按指定方式获得 TIA Portal 实例。
        /// </summary>
        /// <param name="startMode">获取的来源。</param>
        /// <param name="logger">日志器。</param>
        /// <returns>已就绪的会话对象（调用方用 using 包裹）。</returns>
        /// <exception cref="ToolException">附加失败或 API 报错时抛出。</exception>
        public static TiaSession Create(Cli.TiaStartMode startMode, Logger logger)
        {
            TiaSession session = new TiaSession(logger);
            OpennessApi api = RequireApi();

            switch (startMode)
            {
                case Cli.TiaStartMode.WithUserInterface:
                    logger.Section("启动 TIA Portal（带界面）");
                    try
                    {
                        session._portal = api.CreateTiaPortal(true);
                    }
                    catch (Exception ex)
                    {
                        throw WrapSiemensFailure(ex, "创建带界面 TIA Portal 实例失败");
                    }
                    logger.Ok("TIA Portal 已启动（WithUserInterface）。");
                    break;

                case Cli.TiaStartMode.AttachExisting:
                    logger.Section("附加到已运行的 TIA Portal 实例");
                    session._portal = AttachToRunningInstance(logger, api);
                    // ★ 附加模式的"安全默认值"就在这里定死，不依赖调用方记得去设：
                    //   既不去关用户的项目，也不去释放用户的 TIA 实例。
                    //   （调用方仍可显式覆盖，但两个工具设的都是同样的值。）
                    session.CloseProjectOnDispose = false;
                    session.DisposePortalOnDispose = false;
                    logger.Ok("已附加到现有 TIA Portal 实例（结束时不会关闭你的项目，也不释放你的 TIA）。");
                    break;

                default:
                    logger.Section("启动 TIA Portal（无界面）");
                    try
                    {
                        session._portal = api.CreateTiaPortal(false);
                    }
                    catch (Exception ex)
                    {
                        throw WrapSiemensFailure(ex, "创建无界面 TIA Portal 实例失败");
                    }
                    logger.Ok("TIA Portal 已启动（WithoutUserInterface，后台模式）。");
                    break;
            }

            return session;
        }

        /// <summary>
        /// 打开已有项目文件。
        /// </summary>
        /// <param name="projectFilePath">项目文件（.ap18/.ap19/.ap20/.ap21）。</param>
        /// <param name="upgrade">true 时使用 OpenWithUpgrade，把低版本项目升级到当前 TIA 版本。</param>
        /// <returns>打开后的 Project 对象（会话持有引用，勿自行释放）。</returns>
        public object OpenProject(string projectFilePath, bool upgrade)
        {
            FileInfo fileInfo = new FileInfo(Path.GetFullPath(projectFilePath));
            if (!fileInfo.Exists)
            {
                throw new ToolException(ExitCodes.InputOutput, "项目文件不存在：" + fileInfo.FullName);
            }

            _logger.Section("打开项目");
            _logger.Info("项目文件：" + fileInfo.FullName);
            _logger.Info("文件大小：" + SizeFormat.Format(fileInfo.Length));

            OpennessApi api = RequireApi();
            try
            {
                using (_logger.Measure(upgrade ? "OpenWithUpgrade" : "Projects.Open"))
                {
                    _project = api.OpenProject(api.GetProjects(_portal), fileInfo, upgrade);
                }
            }
            catch (Exception ex)
            {
                throw WrapSiemensFailure(ex, "打开项目失败：" + fileInfo.FullName);
            }

            if (_project == null)
            {
                throw new ToolException(ExitCodes.Api, "Projects.Open 返回了 null，未能打开项目：" + fileInfo.FullName);
            }

            _logger.Ok("项目已打开。");
            return _project;
        }

        /// <summary>
        /// 列出当前 TIA 实例里**已经打开**的项目。
        ///
        /// 为什么需要：`Projects.Open` 对"已经打开的项目"会直接报错，而附加模式最常见的场景
        /// 恰恰是"用户已经把项目开在 TIA 里了"—— 这时正确做法是复用那个 Project 对象，
        /// 而不是再去 Open 一次（那会把用户的现场搅乱）。
        /// </summary>
        /// <returns>已打开的项目列表；实例不可用或读不到时返回空列表。</returns>
        public IList<object> GetOpenProjects()
        {
            if (_portal == null)
            {
                return new List<object>();
            }

            OpennessApi api = RequireApi();
            return api.Flatten(api.GetProjects(_portal));
        }

        /// <summary>
        /// 按项目文件路径，在已打开的项目里找同一个（忽略大小写）。
        /// </summary>
        /// <param name="projectFilePath">项目文件路径（.ap1x）。</param>
        /// <returns>匹配的项目对象；没找到返回 null。</returns>
        public object FindOpenProject(string projectFilePath)
        {
            if (_portal == null || string.IsNullOrEmpty(projectFilePath))
            {
                return null;
            }

            string wanted;
            try
            {
                wanted = Path.GetFullPath(projectFilePath);
            }
            catch (Exception)
            {
                return null;
            }

            foreach (object project in GetOpenProjects())
            {
                string path = OpennessApi.GetPropertyTextOrEmpty(project, "Path");
                if (string.IsNullOrEmpty(path))
                {
                    continue;
                }

                try
                {
                    if (string.Equals(Path.GetFullPath(path), wanted, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.Info("这个项目已经在该 TIA 实例里打开，直接复用（不会重新打开）：" + wanted);
                        return project;
                    }
                }
                catch (Exception)
                {
                    // 单个项目的路径读不出来就跳过，继续看下一个
                }
            }

            return null;
        }

        /// <summary>
        /// 把一个**已打开**的项目纳入会话（附加模式复用用户的项目）。
        /// 只登记引用，不做任何修改；附加模式下 Dispose 也不会去关它。
        /// </summary>
        /// <param name="project">已打开的项目对象。</param>
        public void UseProject(object project)
        {
            if (project == null)
            {
                throw new ToolException(ExitCodes.Usage, "复用项目失败：项目对象为空。");
            }

            _project = project;
        }

        /// <summary>
        /// 把当前项目导出成 CAx 数据（.aml）—— 等价于 TIA 的「项目 → 导出 CAx 数据」。
        ///
        /// 内部走 project.GetService&lt;CaxProvider&gt;().Export(project, 文件[, 日志])；
        /// 产物是 <paramref name="directory"/> 下的 <paramref name="fileName"/>.aml，
        /// 同目录还会留一份 <paramref name="fileName"/>_导出日志.log（三参数重载失败时的原因在里面）。
        /// 目录必须已存在（TIA 不会自动建目录），由调用方先建好。
        /// </summary>
        /// <param name="directory">目标目录（必须已存在）。</param>
        /// <param name="fileName">不带扩展名的文件名。</param>
        /// <param name="resultText">TIA 返回的导出结果原文（State + 逐条消息），供调用方转达给用户。</param>
        /// <returns>产物路径（已确认文件真实存在；不存在时抛异常）。</returns>
        /// <exception cref="ToolException">项目未打开，或 TIA 最终没有生成 .aml 文件时抛出。</exception>
        public string ExportCaxData(string directory, string fileName, out string resultText)
        {
            if (_project == null)
            {
                throw new ToolException(ExitCodes.Usage, "还没有打开项目，无法导出 CAx 数据。");
            }

            string exported = Path.Combine(directory, fileName + ".aml");
            FileInfo exportFile = new FileInfo(exported);
            FileInfo logFile = new FileInfo(Path.Combine(directory, fileName + "_导出日志.log"));

            _logger.Section("导出 CAx 数据（.aml）");
            _logger.Info("目标文件：" + exported);

            bool reportedError;
            using (_logger.Measure("CaxProvider.Export"))
            {
                resultText = RequireApi().ExportCax(_project, exportFile, logFile, out reportedError);
            }

            _logger.Info("导出结果：" + resultText);

            exportFile.Refresh();
            if (exportFile.Exists)
            {
                if (reportedError)
                {
                    // 典型情形：个别设备缺 TypeIdentifier 被跳过（实测见过），数据本身仍然可用。
                    // 所以这里是**告警**而不是失败：文件已经生成，由调用方把原因转达给用户。
                    _logger.Warning("TIA 报告导出过程中有问题，但 CAx 文件已生成（"
                        + SizeFormat.Format(exportFile.Length) + "）—— 具体见上面的导出结果。");
                }
                else
                {
                    _logger.Ok("CAx 数据已导出：" + exported
                        + "（" + SizeFormat.Format(exportFile.Length) + "）");
                }

                return exported;
            }

            // 文件确实没有生成：这次是真失败，把 TIA 的原话一起抛出去
            throw new ToolException(ExitCodes.Api,
                "TIA 没有生成 CAx 文件：" + exported + "\r\n导出结果：" + resultText
                + (logFile.Exists ? "\r\n导出日志：" + logFile.FullName : string.Empty));
        }

        /// <summary>
        /// 关闭当前项目（会先释放 Project 对象）。重复调用安全。
        ///
        /// 关于异常处理：**这里刻意吞掉异常，不再往外抛**。
        /// 本方法是释放路径的一环，会被会话 Dispose() 以及 finally 调用；
        /// 如果在这里把异常抛出去，很可能把真正导致失败的原始异常盖掉，
        /// 让排错信息从"归档失败原因"变成"关项目失败"这种无关内容。
        /// 所以正确做法是：把异常记录成告警，让主流程的退出码如实反映真实原因。
        /// 类比 SCL：相当于在 OB100/停机清理逻辑里做 guard，不让清理步骤的故障篡改诊断结果。
        /// </summary>
        public void CloseProject()
        {
            if (_project == null)
            {
                return;
            }

            try
            {
                _logger.Debug("关闭项目中...");
                RequireApi().CloseProject(_project);
                _logger.Ok("项目已关闭。");
            }
            catch (Exception ex)
            {
                // 这里是释放路径，与上面的 XML 注释一致：刻意只记录告警、不往外抛。
                // 抛出去会盖掉主流程真正的失败原因，让退出码指向无关的"关项目失败"。
                _logger.Warning("关闭项目时出现异常：" + ex.Message);
            }
            finally
            {
                _project = null;
            }
        }

        /// <summary>
        /// 释放 TIA Portal 实例。必须调用（推荐通过 using）。
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                if (CloseProjectOnDispose)
                {
                    CloseProject();
                }
                else
                {
                    // 附加模式：只放弃引用，绝不去关用户正在编辑的项目
                    _logger.Debug("附加模式：不关闭项目（避免影响用户已打开的 TIA 会话）。");
                    _project = null;
                }

                if (_portal != null)
                {
                    if (DisposePortalOnDispose)
                    {
                        _logger.Debug("释放 TIA Portal 实例...");
                        RequireApi().DisposePortal(_portal);
                        _logger.Ok("TIA Portal 实例已释放。");
                    }
                    else
                    {
                        // 附加模式：绝不释放用户的 TIA。只放弃引用，进程退出时自然会回收。
                        _logger.Debug("附加模式：不释放 TIA Portal 实例（避免影响用户正在使用的 TIA）。");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warning("释放 TIA Portal 实例时出现异常：" + ex.Message);
            }
            finally
            {
                _portal = null;
                _disposed = true;
            }
        }

        /// <summary>
        /// 取当前绑定的 Openness API；没绑定就先按探测结果绑定。
        /// （探测在 Program 里已经做过一次，这里只是兜底，保证单独调用也安全。）
        /// </summary>
        private static OpennessApi RequireApi()
        {
            if (OpennessApi.Current == null)
            {
                throw new ToolException(
                    ExitCodes.Environment,
                    "还没有绑定 Openness API。请先完成环境探测（正常情况下程序启动时就会做）。");
            }

            return OpennessApi.Current;
        }

        /// <summary>
        /// 附加到本机已运行的 TIA Portal 实例。
        /// 用 TiaPortal.GetProcesses() 枚举，取第一个可附加的进程。
        /// </summary>
        /// <param name="logger">日志器。</param>
        /// <param name="api">已绑定的 API。</param>
        /// <returns>附加得到的 TiaPortal 对象。</returns>
        private static object AttachToRunningInstance(Logger logger, OpennessApi api)
        {
            IEnumerable<object> processes;
            try
            {
                processes = api.GetProcesses();
            }
            catch (Exception ex)
            {
                throw WrapSiemensFailure(ex, "枚举 TIA Portal 进程失败");
            }

            int index = 0;
            List<object> found = new List<object>();
            foreach (object process in processes)
            {
                index++;
                found.Add(process);
                // 各版本 TiaPortalProcess 的属性名可能略有差异，统一用反射取、取不到写 "?"，
                // 不硬编码类型，避免又把某个版本的属性钉死在编译期。
                logger.Info("发现实例 #" + index + "：" + api.DescribeProcess(process));
            }

            if (index == 0)
            {
                throw new ToolException(
                    ExitCodes.Environment,
                    "没有检测到正在运行的 TIA Portal 实例。请先手动打开 TIA Portal，或去掉 --attach 让工具自己启动。");
            }

            List<string> failures = new List<string>();
            foreach (object process in found)
            {
                string id = OpennessApi.GetPropertyText(process, "Id");
                try
                {
                    object portal = api.Attach(process);
                    logger.Info("已附加到进程 Id=" + id);
                    return portal;
                }
                catch (Exception ex)
                {
                    // ★ 一个进程附加失败不代表别的也不行（它可能正在关闭、被别的会话占着等），
                    //   所以这里是"记录后继续试下一个"，而不是直接抛出 ——
                    //   以前这里写的是 throw，循环形同虚设，明明有第二个可用实例也会整体失败。
                    //   详细的排错建议（授权/用户组/版本不匹配）仍然打进日志。
                    logger.Warning(WrapSiemensFailure(ex, "附加到 TIA Portal 进程 Id=" + id + " 失败").Message);
                    failures.Add("Id=" + id + "：" + ex.GetType().Name + "：" + ex.Message);
                }
            }

            throw new ToolException(
                ExitCodes.Environment,
                "枚举到 " + found.Count + " 个 TIA Portal 进程，但一个都无法附加："
                + Environment.NewLine + string.Join(Environment.NewLine, failures.ToArray())
                + Environment.NewLine + "可以先手动关掉多余实例，或在 TIA 里确认 Openness 授权弹窗已被允许。");
        }

        /// <summary>
        /// 把 Siemens Openness 抛出的异常翻译成带中文排错建议的 ToolException。
        /// 重点：EngineeringSecurityException 表示 Openness 防火墙授权被拒绝。
        ///
        /// ★ 这里按**类型名**判断，而不是 <c>catch (EngineeringSecurityException)</c>：
        ///   写类型名就又变成编译期依赖了（那正是"一份 exe 只认一个版本"的根源）。
        /// </summary>
        /// <param name="exception">原始异常。</param>
        /// <param name="context">中文上下文描述。</param>
        /// <returns>带退出码和建议的 ToolException。</returns>
        public static ToolException WrapSiemensFailure(Exception exception, string context)
        {
            System.Text.StringBuilder builder = new System.Text.StringBuilder();
            builder.AppendLine(context);
            builder.AppendLine("  异常类型：" + exception.GetType().FullName);
            builder.AppendLine("  异常消息：" + exception.Message);

            Exception current = exception.InnerException;
            while (current != null)
            {
                builder.AppendLine("  └ 内部异常：" + current.GetType().Name + "：" + current.Message);
                current = current.InnerException;
            }

            if (HasExceptionNamed(exception, "EngineeringSecurityException"))
            {
                builder.AppendLine();
                builder.AppendLine("排错建议（Openness 授权被拒绝，EngineeringSecurityException）：");
                builder.AppendLine("  1) 首次调用时 TIA Portal 会弹出 \"Openness 防火墙\" 授权窗口，必须选择允许；");
                builder.AppendLine("     连续拒绝 3 次后，TIA 会把该应用加入黑名单并抛出本异常。");
                builder.AppendLine("     解决：重新运行并选择允许；若已在黑名单，需清理 TIA 侧的授权记录后重试。");
                builder.AppendLine("  2) 当前 Windows 用户必须加入本地用户组 \"" + TiaEnvironment.OpennessUserGroup + "\"：");
                builder.AppendLine("     Win + R -> lusrmgr.msc -> 组 -> Siemens TIA Openness -> 添加当前用户 -> 注销后重新登录。");
                builder.AppendLine("  3) 确认 Visual Studio / 本工具没有以不同凭据（管理员 vs 普通）混用打开 TIA。");
                return new ToolException(ExitCodes.Api, builder.ToString(), exception);
            }

            if (HasExceptionNamed(exception, "EngineeringException"))
            {
                builder.AppendLine();

                if (ContainsMessage(exception, "项目目录已存在")
                    || ContainsMessage(exception, "project directory already exists"))
                {
                    // 归档输出落在项目自身目录：TIA 必拒（V19 实测）。
                    // 这种情况下面那套通用建议（版本 / 权限 / 先保存）一条都用不上，只会把人带偏 ——
                    // 真正的办法只有一个：把归档输出移出项目自身目录。
                    builder.AppendLine("排错建议（TIA 拒绝把归档写进目标位置）：");
                    builder.AppendLine("  1) 目标位置已经有一个“项目目录” —— 最常见的原因是**归档输出选在了项目自身所在目录**"
                        + "（.zapXX 和 .apXX 放在同一个文件夹里）；");
                    builder.AppendLine("  2) 实测（V19 Openness）：换到项目目录之外的任何目录都能成功"
                        + "（批量归档正是因为输出目录另选才没踩到这个坑）；");
                    builder.AppendLine("  3) 把“归档到”改到项目目录之外再试（归档页默认建议值已经是项目目录的上一级）。");
                }
                else if (ContainsMessage(exception, "is already exist")
                    || ContainsMessage(exception, "already exists"))
                {
                    // 目标文件 / 文件夹已存在：TIA 的归档不覆盖（V21 实测）。
                    // 工具侧的"覆盖"是这么做的：先把旧文件改名成 .old 备份 → 归档成功后才删备份，失败则恢复。
                    builder.AppendLine("排错建议（归档目标已存在）：");
                    builder.AppendLine("  1) TIA 的归档不会覆盖已存在的目标文件 / 文件夹"
                        + "（原文：Archive Operation is not possible as the target file/folder … is already exist.）；");
                    builder.AppendLine("  2) 在界面上选“覆盖”——工具会先把旧文件改名成 .old 备份，归档成功后才删除它；失败会把旧文件恢复回来；");
                    builder.AppendLine("  3) 或者换一个文件名（加时间戳 / 后缀 / 序号）再来。");
                }
                else
                {
                    builder.AppendLine("排错建议（Openness API 返回 EngineeringException）：");
                    builder.AppendLine("  1) 确认所用 Openness 主版本与本机 TIA Portal 主版本一致"
                        + "（本工具会自动挑与本机一致的版本，也可在界面上手动选）；");
                    builder.AppendLine("     用 probe 命令确认本机 API 的实际版本和真实签名。");
                    builder.AppendLine("  2) 确认目标项目没有被独占锁定、没有在其他 TIA 实例里打开；");
                    builder.AppendLine("  3) 归档前请先保存项目（先保存 / --save-first），未保存修改会让归档失败；");
                    builder.AppendLine("  4) 确认当前 Windows 用户属于本地组 \"" + TiaEnvironment.OpennessUserGroup + "\"。");
                }

                return new ToolException(ExitCodes.Api, builder.ToString(), exception);
            }

            if (HasExceptionNamed(exception, "IOException")
                || exception is IOException
                || exception is UnauthorizedAccessException)
            {
                builder.AppendLine();
                builder.AppendLine("排错建议（文件系统层失败）：");
                builder.AppendLine("  1) 检查目标路径是否存在、是否有写权限、磁盘是否写满；");
                builder.AppendLine("  2) 归档/解包时 TIA 需要写临时目录，确保 %TEMP% 可用；");
                builder.AppendLine("  3) 路径不要放在需要提权的系统保护目录（如 C:\\Windows）。");
                return new ToolException(ExitCodes.InputOutput, builder.ToString(), exception);
            }

            builder.AppendLine();
            builder.AppendLine("排错建议（未分类错误）：先用 -v 打开详细日志，再跑 probe 确认本机 API 签名。");
            return new ToolException(ExitCodes.Api, builder.ToString(), exception);
        }

        /// <summary>
        /// 异常链里的消息是否包含某段文本（不区分大小写）。
        /// 用于区分"业务语义明确的错误"和"没头没脑的错误"，好给不同的排错建议。
        /// </summary>
        /// <param name="exception">起始异常。</param>
        /// <param name="text">要查找的片段。</param>
        /// <returns>命中返回 true。</returns>
        private static bool ContainsMessage(Exception exception, string text)
        {
            Exception current = exception;
            while (current != null)
            {
                if (current.Message != null
                    && current.Message.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }

                current = current.InnerException;
            }

            return false;
        }

        /// <summary>
        /// 异常链里是否有某个名字的异常类型（含自身）。按名字判断 = 不产生编译期依赖。
        /// </summary>
        /// <param name="exception">起始异常。</param>
        /// <param name="typeName">类型短名，例如 EngineeringSecurityException。</param>
        /// <returns>命中返回 true。</returns>
        public static bool HasExceptionNamed(Exception exception, string typeName)
        {
            Exception current = exception;
            while (current != null)
            {
                Type type = current.GetType();
                while (type != null)
                {
                    if (string.Equals(type.Name, typeName, StringComparison.Ordinal))
                    {
                        return true;
                    }
                    type = type.BaseType;
                }

                current = current.InnerException;
            }

            return false;
        }
    }
}
