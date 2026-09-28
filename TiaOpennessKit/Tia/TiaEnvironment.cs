using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using Microsoft.Win32;

namespace TiaOpennessKit.Tia
{
    /// <summary>
    /// 探测到的 TIA Portal Openness API 环境信息。
    /// </summary>
    public sealed class TiaEnvironmentInfo
    {
        /// <summary>Portal 安装根目录，例如 D:\Program Files\Siemens\Automation\Portal V21。</summary>
        public string PortalDirectory { get; private set; }

        /// <summary>API 版本目录名，例如 V21。</summary>
        public string VersionDirectoryName { get; private set; }

        /// <summary>主版本号数字，例如 21。无法确定时为 0。</summary>
        public int MajorVersion { get; private set; }

        /// <summary>真正存放 Siemens.Engineering*.dll 的目录（V21 是 ...\PublicAPI\V21\net48）。</summary>
        public string ApiDirectory { get; private set; }

        /// <summary>ApiDirectory 下所有 Siemens.Engineering*.dll 的绝对路径（供 probe 枚举与解析使用）。</summary>
        public IReadOnlyList<string> Assemblies { get; private set; }

        /// <summary>
        /// 仅用于运行期程序集解析的**补充**目录。
        /// V21 实测：Openness 运行时还需要 <Portal>\Bin\PublicAPI\ 下的程序集
        /// （Siemens.Engineering.Contract.dll、Siemens.Engineering.ClientAdapter.Interfaces.dll、
        /// 以及 Siemens.Automation.Opns.ObjectModel.*.Impl.dll 等实现程序集），
        /// 它们**不在** PublicAPI\V21\net48\ 里。只扫 API 目录会在 TIA 启动阶段解析失败。
        /// </summary>
        public IReadOnlyList<string> AdditionalAssemblyDirectories { get; private set; }

        /// <summary>该环境的来源说明（环境变量/注册表/安装根扫描），便于排错。</summary>
        public string DetectionSource { get; private set; }

        /// <summary>构造环境信息。</summary>
        /// <param name="portalDirectory">Portal 安装根目录。</param>
        /// <param name="versionDirectoryName">版本目录名。</param>
        /// <param name="majorVersion">主版本号。</param>
        /// <param name="apiDirectory">实际 API 目录。</param>
        /// <param name="assemblies">程序集清单。</param>
        /// <param name="additionalAssemblyDirectories">运行期解析的补充目录。</param>
        /// <param name="detectionSource">来源说明。</param>
        public TiaEnvironmentInfo(
            string portalDirectory,
            string versionDirectoryName,
            int majorVersion,
            string apiDirectory,
            IReadOnlyList<string> assemblies,
            IReadOnlyList<string> additionalAssemblyDirectories,
            string detectionSource)
        {
            PortalDirectory = portalDirectory;
            VersionDirectoryName = versionDirectoryName;
            MajorVersion = majorVersion;
            ApiDirectory = apiDirectory;
            Assemblies = assemblies;
            AdditionalAssemblyDirectories = additionalAssemblyDirectories;
            DetectionSource = detectionSource;
        }

        /// <summary>返回人类可读的一行摘要。</summary>
        /// <returns>中文摘要。</returns>
        public override string ToString()
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "TIA Portal {0}（{1}） API 目录：{2}（来源：{3}，程序集 {4} 个）",
                VersionDirectoryName,
                PortalDirectory,
                ApiDirectory,
                DetectionSource,
                Assemblies.Count);
        }
    }

    /// <summary>
    /// TIA Portal Openness API 环境探测 + 运行期程序集解析。
    ///
    /// 关键点（类比 SCL/ST 更好理解）：
    ///   - Openness 的 DLL 必须在 TIA 安装目录里加载，不能复制到自己的输出目录
    ///     （官方明确 Copy Local = False）。这就像 FB 的背景 DB：实例必须挂在正确的宿主上，
    ///     私下拷一份出来用是不支持的。
    ///   - AssemblyResolve 相当于给 CLR 挂了一个"当找不到某个 FB/FC 时，去这里找"的后备钩子。
    ///     必须在代码里**第一次触碰任何 Siemens 类型之前**注册，晚了就来不及。
    ///   - 不同版本布局不同：
    ///       V15 ~ V20：<安装根>\Portal V20\PublicAPI\V20\Siemens.Engineering.dll
    ///       V21       ：<安装根>\Portal V21\PublicAPI\V21\net48\Siemens.Engineering.Base.dll 等模块化程序集
    ///     所以不能硬编码 Portal V19/V20，而是按版本号扫描并取最高版本。
    /// </summary>
    public static class TiaEnvironment
    {
        /// <summary>允许用户用环境变量覆盖 API 目录。</summary>
        public const string ApiDirectoryEnvironmentVariable = "TIA_PORTAL_PUBLIC_API_DIR";

        /// <summary>Openness 要求的本地用户组名。</summary>
        public const string OpennessUserGroup = "Siemens TIA Openness";

        /// <summary>Portal 安装目录的匹配模式。</summary>
        private const string PortalDirectoryPattern = "Portal V*";

        /// <summary>PublicAPI 子目录名。</summary>
        private const string PublicApiDirectoryName = "PublicAPI";

        /// <summary>API 程序集通配名字前缀。</summary>
        private const string AssemblyFilePattern = "Siemens.Engineering*.dll";

        /// <summary>
        /// Openness 组件登记自己"把 API 装在哪了"的注册表位置（相对 HKLM\SOFTWARE）。
        /// 实测这里才是真正有内容的地方：顶层键只有一个空的 (默认) 值，
        /// 实际路径在 PublicAPI\&lt;程序集版本&gt;\&lt;目标框架&gt; 子键的值里。
        /// </summary>
        private const string OpennessRegistryPath = @"SOFTWARE\Siemens\Automation\Openness";

        /// <summary>
        /// Siemens 安装器登记"每个产品装在哪"的注册表位置（相对 HKLM\SOFTWARE）。
        /// 这里是唯一能找到**自定义安装目录**的地方（TIA 允许装在任意路径）。
        /// </summary>
        private const string InstalledSoftwareRegistryPath = @"SOFTWARE\Siemens\Automation\_InstalledSW";

        private static bool _resolverInstalled;

        /// <summary>
        /// 解析钩子是按**哪个 API 目录**建立的（用于把"同一进程里换了版本"这件事显式报出来）。
        /// </summary>
        private static string _resolverApiDirectory;

        /// <summary>
        /// 解析钩子实际登记的那个 Openness 主版本（0 表示还没登记）。
        ///
        /// ★ 它与 <c>OpennessApi.Current.MajorVersion</c> **不是一回事**，必须区分开：
        ///   Current 反映的是"最后一次 Bind 用哪个版本反射"，而本字段反映的是
        ///   "运行期到底从哪个目录去解析 DLL 字节"。决定真实的跨版本后果的是后者。
        ///   实测踩过：同进程先跑过 V20，再选 V18 批量归档 → Current=18 但解析目录仍是 V20 →
        ///   归档被误判为"与内核一致、进程内处理" → 加载出 V18 主程序集 + V20 依赖的混合体 →
        ///   MissingMethodException（找不到 IAppSupport.Initialize(Process)）。
        /// </summary>
        private static int _resolverMajorVersion;

        /// <summary>
        /// 本进程解析钩子实际登记的 API 目录（还没登记时为空串）。
        /// 调用方可用它判断"现在真正生效的是哪个版本"。
        /// </summary>
        public static string BoundApiDirectory
        {
            get { return _resolverApiDirectory; }
        }

        /// <summary>
        /// 本进程解析钩子实际登记的 Openness 主版本；还没登记返回 0。
        ///
        /// 用途：归档/批量这类"要真正驱动 TIA"的调用方必须按它（而不是 <c>OpennessApi.Current</c>）
        /// 判断本进程能不能直接干活 —— 版本不一致时应改走子进程，而不是在进程内硬干。
        /// </summary>
        public static int BoundMajorVersion
        {
            get { return _resolverMajorVersion; }
        }

        /// <summary>
        /// 已安装版本列表的进程内缓存。
        ///
        /// 界面里下拉框、版本提示、体检等好几处都会调用 <see cref="EnumerateInstalled"/>，
        /// 而"本机装了哪些 Openness"在程序运行期间通常不会变。没有缓存时，一份日志里能连刷
        /// 7 次“没有枚举到任何可用的 Openness 版本”，既吵又白跑注册表与磁盘扫描。
        ///
        /// ★ 但"通常不变"不等于"保证不变"：用户可能在本程序开着的时候补装 / 卸载 Openness。
        ///   所以界面上那个"重新检测"按钮必须先调 <see cref="InvalidateInstalledCache"/>，
        ///   否则点了等于没点（拿到的还是第一次探测的结果）。
        /// </summary>
        private static List<TiaEnvironmentInfo> _installedCache;

        /// <summary>
        /// 要查的注册表视图。**两个都要查**：
        ///
        /// 本程序是 AnyCPU 的 WinExe，默认以 32 位运行，而 <c>Registry.LocalMachine</c>
        /// 在 32 位进程里会被 Windows 自动重定向到 WOW6432Node。
        /// 实测 Openness 键**只写在 64 位视图**里（WOW6432Node 下压根没有），
        /// 于是"读注册表找 Openness"这一步在 32 位进程里永远读不到东西 ——
        /// 这正是它以前形同虚设的第二个原因（第一个是只读顶层值）。
        /// </summary>
        private static readonly RegistryView[] RegistryViews = new RegistryView[]
        {
            RegistryView.Registry64,
            RegistryView.Registry32
        };

        /// <summary>
        /// 探测本机 TIA Portal Openness API 环境。优先级：
        ///   1) 命令行 --api-dir 指定的目录
        ///   2) 环境变量 TIA_PORTAL_PUBLIC_API_DIR
        ///   3) 多来源自动定位（见 <see cref="CollectCandidateApiDirectories"/>）
        /// </summary>
        /// <param name="logger">日志器。</param>
        /// <param name="explicitApiDirectory">命令行 --api-dir 指定的目录；可为 null 或空。</param>
        /// <returns>探测到的环境信息。</returns>
        /// <exception cref="ToolException">找不到 API 目录时抛出（退出码=环境错误）。</exception>
        public static TiaEnvironmentInfo Detect(Logger logger, string explicitApiDirectory)
        {
            List<string> notes = new List<string>();

            if (!string.IsNullOrWhiteSpace(explicitApiDirectory))
            {
                string normalized = Environment.ExpandEnvironmentVariables(explicitApiDirectory.Trim());
                logger.Debug("尝试命令行指定的 API 目录：" + normalized);
                TiaEnvironmentInfo fromArgument = TryBuildFromDirectory(normalized, "--api-dir 参数");
                if (fromArgument != null)
                {
                    return fromArgument;
                }
                throw CreateDetectionFailure(normalized + "（由 --api-dir 指定）", notes);
            }

            TiaEnvironmentInfo fromEnvironment = TryCreateFromEnvironmentVariable(logger, notes);
            if (fromEnvironment != null)
            {
                return fromEnvironment;
            }

            // ★ 自动定位统一走"多来源候选"这条路。
            //   以前这里是"注册表只读顶层值 → 再扫标准安装根"，而 TIA 允许装到自定义目录：
            //   实测有一台机器把 V18 装在 D:\V19\Portal V18，两条路都落空，
            //   界面报"Openness 组件尚未安装"，可手动把同一个目录填进 --api-dir 却一切正常。
            List<ApiDirectoryCandidate> candidates = CollectCandidateApiDirectories(logger, notes);
            if (candidates.Count > 0)
            {
                // 列表已按"本程序编译版本优先、其余版本降序"排好，取第一个即可。
                ApiDirectoryCandidate best = candidates[0];
                if (candidates.Count > 1)
                {
                    notes.Add("共找到 " + candidates.Count + " 个候选 API 目录，自动选用：" + best.Directory);
                }

                TiaEnvironmentInfo info = TryBuildFromDirectory(best.Directory, best.Source);
                if (info != null)
                {
                    return info;
                }
            }

            throw CreateDetectionFailure(null, notes);
        }

        /// <summary>
        /// 枚举本机所有**可用**的 TIA Portal Openness 版本。
        ///
        /// 与 <see cref="Detect"/> 的区别：Detect 只挑出版本号最高的那一个（够用就行），
        /// 本方法把所有能用的版本都列出来，供界面提供"用哪个版本的 API 来归档"这种选择。
        ///
        /// 前提：那些版本都**装了 Openness 组件**。只装了 TIA Portal 本体、安装时没勾
        /// Openness 的话，它的 Portal 目录下不会有 PublicAPI，自然也就列不出来。
        ///
        /// 注意：这里枚举的是"可用的 API 程序集版本"，不是"本机有哪些 TIA 内核"。
        /// 一个进程同一时刻只能用其中一个版本（程序集同名不同版本无法共存）。
        /// </summary>
        /// <param name="logger">日志器。</param>
        /// <returns>可用版本列表，按主版本号从高到低排序；一个都没有时返回空列表。</returns>
        public static IList<TiaEnvironmentInfo> EnumerateInstalled(Logger logger)
        {
            // 只算一次（注册表 + 磁盘扫描不便宜，而且结果在进程生命周期内不会变）。
            // 返回副本，避免调用方排序/改动污染缓存。
            if (_installedCache == null)
            {
                _installedCache = BuildInstalledList(logger);
            }

            return new List<TiaEnvironmentInfo>(_installedCache);
        }

        /// <summary>
        /// 清空"已安装版本"的进程内缓存，让下一次 <see cref="EnumerateInstalled"/> 重新探测。
        ///
        /// 为什么需要它：缓存的前提是"本机装了哪些 Openness 在运行期间不会变"，
        /// 但界面上有"重新检测"按钮，而用户点它的时机往往就是**刚补装完 Openness 组件**。
        /// 不清缓存的话这个按钮等于没按，用户会误以为补装没生效（从而去折腾别的地方）。
        ///
        /// 线程安全：这里只做一次引用赋值。极端情况下另一线程正读到旧列表，
        /// 那也只是这一次拿到旧结果，不会出错 —— 与缓存本身的做法一致（不加锁）。
        /// </summary>
        public static void InvalidateInstalledCache()
        {
            _installedCache = null;
        }

        /// <summary>
        /// 真正的枚举实现（由 <see cref="EnumerateInstalled"/> 缓存调用）。
        ///
        /// 走的是与 <see cref="Detect"/> 完全相同的多来源定位 —— 这一点很重要：
        /// 以前体检单独调本方法，用的是"只扫标准安装根"的老逻辑，于是出现
        /// “同一份日志里，上面说没装 Openness、下面用 --api-dir 绑定得好好的”这种自相矛盾。
        /// </summary>
        /// <param name="logger">日志器。</param>
        /// <returns>可用版本列表。</returns>
        private static List<TiaEnvironmentInfo> BuildInstalledList(Logger logger)
        {
            List<TiaEnvironmentInfo> result = new List<TiaEnvironmentInfo>();
            List<string> notes = new List<string>();
            HashSet<string> seenApiDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (ApiDirectoryCandidate candidate in CollectCandidateApiDirectories(logger, notes))
            {
                if (!seenApiDirectories.Add(candidate.Directory))
                {
                    continue;
                }

                TiaEnvironmentInfo info = TryBuildFromDirectory(candidate.Directory, candidate.Source);
                if (info != null)
                {
                    result.Add(info);
                }
            }

            // 排序规则：与本程序**编译时**所用版本一致的那个排最前（那个才跑得起来），
            // 其余按版本号从高到低。界面的"可用版本"下拉取第一项当默认值，所以这个顺序有意义。
            int requiredMajor = GetReferencedOpennessMajorVersion();
            result.Sort(delegate (TiaEnvironmentInfo a, TiaEnvironmentInfo b)
            {
                bool aPreferred = requiredMajor > 0 && a.MajorVersion == requiredMajor;
                bool bPreferred = requiredMajor > 0 && b.MajorVersion == requiredMajor;
                if (aPreferred != bPreferred)
                {
                    return aPreferred ? -1 : 1;
                }

                return b.MajorVersion.CompareTo(a.MajorVersion);
            });

            if (result.Count == 0)
            {
                // 这句以前只写“没有枚举到”，容易被读成“本机没装 Openness”。
                // TIA 允许自定义安装目录，所以要把“可能只是没找到”说清楚，并给出可操作的办法。
                logger.Warning("没有枚举到任何可用的 Openness 版本。"
                    + "已尝试注册表登记路径、常见安装位置与浅层全盘扫描；"
                    + "若 TIA 装在非常规目录，请在“API 目录”里手动指定该版本的 PublicAPI 目录。");
            }
            else
            {
                logger.Info("本机可用的 Openness 版本：" + DescribeVersions(result));
            }

            return result;
        }

        /// <summary>
        /// 列出"自动定位"从各个来源分别找到了什么。
        ///
        /// 只读注册表与目录名，**不加载任何程序集**，可以随时调用。
        /// 排查"明明装了 Openness 却报没装"时这一段是决定性的：它能区分
        /// "注册表没登记"、"登记了但目录已不存在（卸载残留）"、
        /// "只装了本体没勾 Openness 组件"、以及"装在非常规目录、只有兜底扫描才找得到"。
        /// </summary>
        /// <param name="logger">日志器。</param>
        /// <returns>可读的多行文本。</returns>
        public static string DescribeDetectionSources(Logger logger)
        {
            StringBuilder report = new StringBuilder();
            List<string> notes = new List<string>();

            List<ApiDirectoryCandidate> candidates = CollectCandidateApiDirectories(logger, notes);

            if (candidates.Count == 0)
            {
                report.AppendLine("  没有任何来源找到可用的 API 目录。");
            }
            else
            {
                foreach (ApiDirectoryCandidate candidate in candidates)
                {
                    report.AppendLine("  [命中] " + candidate.Directory);
                    report.AppendLine("         来源：" + candidate.Source);
                }
            }

            if (notes.Count > 0)
            {
                report.AppendLine();
                report.AppendLine("  过程明细（含被否决的候选）：");
                foreach (string note in notes)
                {
                    report.AppendLine("    - " + note);
                }
            }

            return report.ToString();
        }

        /// <summary>
        /// 排错用：列出"浅层扫描"（各固定盘的根目录及其一级子目录）找到的所有 Portal V* 目录，
        /// 以及每个目录里到底有没有装 Openness 组件。
        ///
        /// 这是"TIA 装在非常规目录"时的兜底发现路径。单独暴露出来有两个用处：
        /// 一是可以在不改动任何注册表的情况下验证这套逻辑真的有效；
        /// 二是新机器上排错时，一眼能看出"到底扫到了哪些 Portal 目录、卡在哪一步"。
        /// </summary>
        /// <param name="logger">日志器。</param>
        /// <returns>可读的多行文本。</returns>
        public static string DescribeLoosePortalScan(Logger logger)
        {
            StringBuilder report = new StringBuilder();
            List<string> notes = new List<string>();
            List<string> portals = CollectLoosePortalDirectories(logger, notes);

            report.AppendLine("扫描范围：各固定盘的根目录，以及根目录下的一级子目录（不递归整盘）。");
            report.AppendLine("覆盖的非常规布局：X:\\Portal V18 与 X:\\任意子目录\\Portal V18。");
            report.AppendLine();

            if (portals.Count == 0)
            {
                report.AppendLine("  没有找到名为 Portal V* 的目录。");
            }
            else
            {
                foreach (string portal in portals)
                {
                    string publicApiRoot = Path.Combine(portal, PublicApiDirectoryName);
                    if (!Directory.Exists(publicApiRoot))
                    {
                        // 与自动定位同一套判据：区分"装了本体但没勾组件"和"只是残留目录"
                        bool hasPortalExecutable = File.Exists(
                            Path.Combine(portal, "bin", "Siemens.Automation.Portal.exe"));

                        report.AppendLine(hasPortalExecutable
                            ? "  [只有本体] " + portal
                            : "  [残留目录] " + portal);
                        report.AppendLine(hasPortalExecutable
                            ? "             没有 PublicAPI —— 装本体时没勾 Openness 组件"
                            : "             既没有主程序也没有 PublicAPI —— 多半是卸载残留");
                        continue;
                    }

                    List<string> apiDirectories = new List<string>(CollectVersionDirectories(publicApiRoot, notes));
                    if (apiDirectories.Count == 0)
                    {
                        report.AppendLine("  [PublicAPI 为空] " + portal);
                        continue;
                    }

                    report.AppendLine("  [Openness 可用] " + portal);
                    foreach (string apiDirectory in apiDirectories)
                    {
                        report.AppendLine("                 " + apiDirectory);
                    }
                }
            }

            if (notes.Count > 0)
            {
                report.AppendLine();
                report.AppendLine("  过程明细：");
                foreach (string note in notes)
                {
                    report.AppendLine("    - " + note);
                }
            }

            return report.ToString();
        }

        /// <summary>
        /// 把版本列表拼成一句可读文本，如 "V21 / V19 / V16"。
        /// </summary>
        /// <param name="environments">版本列表。</param>
        /// <returns>可读文本。</returns>
        public static string DescribeVersions(IEnumerable<TiaEnvironmentInfo> environments)
        {
            if (environments == null)
            {
                return "无";
            }

            List<string> names = new List<string>();
            foreach (TiaEnvironmentInfo environment in environments)
            {
                // 版本号为 0 的条目不可用（目录名里没有版本号，说明不是真正的版本目录）；
                // 同一个版本在多个目录下都有程序集时（例如 V19 与 V19\net48），只报一次，
                // 否则会输出成 "V19 / V19 / V19 / V16 / V16" 这种让人以为装了好多版本的样子。
                if (environment == null || environment.MajorVersion <= 0)
                {
                    continue;
                }

                string name = "V" + environment.MajorVersion;
                if (!names.Contains(name))
                {
                    names.Add(name);
                }
            }

            return names.Count == 0 ? "无" : string.Join(" / ", names.ToArray());
        }

        /// <summary>
        /// 读本程序**自己**编译时引用的 Openness 主版本号（没有引用则返回 0）。
        ///
        /// 这是"运行时到底要什么版本"的唯一权威来源：CLR 只按编译时记下的程序集标识去找，
        /// 与界面上选了哪个 API 目录无关。跨版本排查时第一个要看的就是它。
        /// </summary>
        internal static int GetReferencedOpennessMajorVersion()
        {
            int major = 0;
            try
            {
                Assembly self = typeof(TiaEnvironment).Assembly;
                foreach (AssemblyName reference in self.GetReferencedAssemblies())
                {
                    if (reference.Name == null
                        || !reference.Name.StartsWith("Siemens.", StringComparison.OrdinalIgnoreCase)
                        || reference.Version == null)
                    {
                        continue;
                    }

                    if (reference.Version.Major > major)
                    {
                        major = reference.Version.Major;
                    }
                }
            }
            catch (Exception)
            {
                // 读不到就当没有引用（打包模式就是这种）
            }

            return major;
        }

        /// <summary>
        /// 生成"为什么这台机器上归档会失败"的诊断报告（界面版/命令行版的 --envdump）。
        ///
        /// 起因是一次真实故障：在只有 V19 的机器上运行**按 V21 编译**的 exe，
        /// 环境探测全部通过（它只做文件与反射层面的检查），可一开始归档就报
        /// "未能加载 Siemens.Engineering.Base, Version=21.0.0.0"。
        ///
        /// 根因是**编译时引用的程序集版本**和**本机可用版本**不是一回事 ——
        /// 这件事光看日志看不出来，所以这份报告把三样东西摆在一起：
        ///   ① 本程序编译时要什么版本（读自身程序集的引用表，最权威）
        ///   ② 本机枚举到哪些版本、各自目录里有哪些 DLL、分别是什么版本
        ///   ③ 运行期程序集解析会搜哪些目录、每个目录里能找到什么
        /// 最后给出"匹配/不匹配"的结论与两条出路。
        /// </summary>
        /// <param name="logger">日志器。</param>
        /// <param name="explicitApiDirectory">界面上/命令行指定的 API 目录；可为 null。</param>
        /// <returns>中文报告全文。</returns>
        public static string BuildDiagnosticReport(Logger logger, string explicitApiDirectory)
        {
            StringBuilder report = new StringBuilder();

            report.AppendLine("──── Openness 环境诊断（envdump）────");
            report.AppendLine();

            // ── ① 本程序编译时引用的 Siemens 程序集版本
            report.AppendLine("【1】本程序编译时引用的 Openness 程序集（决定运行时“要什么版本”）");
            Assembly self = typeof(TiaEnvironment).Assembly;
            List<AssemblyName> references = new List<AssemblyName>();
            foreach (AssemblyName reference in self.GetReferencedAssemblies())
            {
                if (reference.Name != null
                    && reference.Name.StartsWith("Siemens.", StringComparison.OrdinalIgnoreCase))
                {
                    references.Add(reference);
                }
            }

            if (references.Count == 0)
            {
                report.AppendLine("  （没有引用任何 Siemens 程序集 —— 这个构建不依赖 Openness）");
            }
            else
            {
                foreach (AssemblyName reference in references)
                {
                    report.AppendLine("  " + reference.Name.PadRight(42)
                        + " 需要 " + (reference.Version == null ? "?" : reference.Version.ToString()));
                }
            }

            int requiredMajor = 0;
            foreach (AssemblyName reference in references)
            {
                if (reference.Version != null && reference.Version.Major > requiredMajor)
                {
                    requiredMajor = reference.Version.Major;
                }
            }

            report.AppendLine();
            if (references.Count > 0)
            {
                report.AppendLine("  ★ 这就是关键：程序在运行时只会去找上面这些版本。"
                    + "本机没有这个版本时，无论 API 目录选得多对都加载不了。");
            }
            else
            {
                report.AppendLine("  ★ 好现象：本构建在**编译期一个 Siemens 类型都没引用**，"
                    + "运行期才按类型名从本机 API 目录加载，");
                report.AppendLine("    所以它能驱动本机任意一个版本（V15~V21 都行），"
                    + "也不会出现“程序集名字 / 版本对不上”这种问题。");
            }

            report.AppendLine();

            // ── ② 自动定位过程明细
            //     TIA 允许自定义安装目录，所以"为什么没找到"必须能看到每一步。
            report.AppendLine("【2】自动定位过程明细（注册表登记路径 / 常见安装位置 / 浅层兜底扫描）");
            report.Append(DescribeDetectionSources(logger));
            report.AppendLine();

            // ── ③ 本机枚举到的版本 + 每个目录里的程序集
            report.AppendLine("【3】本机枚举到的可用 Openness 版本");
            IList<TiaEnvironmentInfo> versions = EnumerateInstalled(logger);
            if (versions.Count == 0)
            {
                report.AppendLine("  （一个都没有：这些版本没装 Openness 组件，或 PublicAPI 目录不在预期位置）");
            }
            else
            {
                foreach (TiaEnvironmentInfo version in versions)
                {
                    report.AppendLine("  " + version);
                    AppendAssemblyInventory(report, version.ApiDirectory, "      ");
                }
            }

            report.AppendLine();

            // ── ④ 被选中的环境 + 运行期解析会搜的目录
            report.AppendLine("【4】实际会用的 API 目录与运行期程序集解析路径");
            TiaEnvironmentInfo selected = null;
            try
            {
                selected = Detect(logger, explicitApiDirectory);
            }
            catch (ToolException ex)
            {
                report.AppendLine("  探测失败：" + ex.Message);
            }

            if (selected != null)
            {
                report.AppendLine("  选中：" + selected);
                report.AppendLine("  解析优先级依次为：");
                foreach (string directory in BuildSearchDirectories(selected))
                {
                    AppendAssemblyInventory(report, directory, "      ");
                }
            }

            report.AppendLine();

            // ── ④ 结论
            report.AppendLine("【5】结论");
            if (selected == null)
            {
                report.AppendLine("  没能定位到可用的 API 目录，先解决【3】里的探测失败原因。");
                report.AppendLine("  提示：只要本机装了任一版本的 Openness 组件就能用；"
                    + "完全没有 Openness 时，仍可用“打包项目文件夹（.zip）”模式（不经过 Openness）。");
            }
            else
            {
                string binding;
                try
                {
                    AssemblyName apiName = AssemblyName.GetAssemblyName(
                        FirstAssemblyThatDeclaresTiaPortal(selected));
                    binding = apiName.Name + " " + apiName.Version;
                }
                catch (Exception ex)
                {
                    binding = "（读不出来：" + ex.GetType().Name + "）";
                }

                report.AppendLine("  ✔ 本机可用版本：V" + selected.MajorVersion
                    + "，运行期将加载 " + binding + "。");

                if (requiredMajor > 0 && selected.MajorVersion != requiredMajor)
                {
                    report.AppendLine("  ⚠ 注意：本构建编译时引用过 V" + requiredMajor
                        + "（老版本的行为），与本机 V" + selected.MajorVersion
                        + " 对不上就会加载失败 —— 请改用不引用 Siemens 程序集的新构建。");
                }
                else if (requiredMajor <= 0)
                {
                    report.AppendLine("      本构建不引用任何 Siemens 程序集，"
                        + "因此换成别的版本（或换台机器）也不用重新编译。");
                }

                report.AppendLine();
                report.AppendLine("  归档/恢复功能要求本机装有与项目匹配的 Openness 组件；"
                    + "“打包项目文件夹（.zip）”模式完全不需要 Openness。");
            }

            return report.ToString();
        }

        /// <summary>
        /// 运行期程序集解析会搜索的目录（与 <see cref="InstallAssemblyResolver"/> 用的规则一致）。
        /// </summary>
        /// <param name="info">环境信息。</param>
        /// <returns>按优先级排列的目录列表。</returns>
        private static List<string> BuildSearchDirectories(TiaEnvironmentInfo info)
        {
            List<string> directories = new List<string>();
            if (info == null)
            {
                return directories;
            }

            directories.Add(info.ApiDirectory);
            foreach (string extra in info.AdditionalAssemblyDirectories)
            {
                if (!directories.Contains(extra))
                {
                    directories.Add(extra);
                }
            }

            return directories;
        }

        /// <summary>
        /// 把一个目录里的 Siemens 程序集连同各自的真实版本号列出来。
        /// </summary>
        private static void AppendAssemblyInventory(StringBuilder report, string directory, string indent)
        {
            report.AppendLine(indent + "目录：" + directory);

            string[] files;
            try
            {
                files = Directory.GetFiles(directory, AssemblyFilePattern);
            }
            catch (Exception ex)
            {
                report.AppendLine(indent + "  （读不到：" + ex.GetType().Name + "）");
                return;
            }

            if (files.Length == 0)
            {
                report.AppendLine(indent + "  （没有 Siemens.Engineering*.dll）");
                return;
            }

            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            foreach (string file in files)
            {
                string versionText;
                try
                {
                    AssemblyName name = AssemblyName.GetAssemblyName(file);
                    versionText = name.Name + "  " + name.Version
                        + (string.Equals(name.Name, Path.GetFileNameWithoutExtension(file),
                            StringComparison.OrdinalIgnoreCase) ? string.Empty : "  ← 文件名与内部名称不一致！");
                }
                catch (Exception ex)
                {
                    versionText = "（读不出程序集信息：" + ex.GetType().Name + "）";
                }

                report.AppendLine(indent + "  " + Path.GetFileName(file) + "  →  " + versionText);
            }
        }


        /// <summary>
        /// 找出该环境里承载 <c>Siemens.Engineering.TiaPortal</c> 的程序集路径（V19 是
        /// Siemens.Engineering.dll，V20/V21 是 Siemens.Engineering.Base.dll）。
        /// </summary>
        private static string FirstAssemblyThatDeclaresTiaPortal(TiaEnvironmentInfo info)
        {
            foreach (string path in BuildSearchDirectories(info))
            {
                string[] files;
                try
                {
                    files = Directory.GetFiles(path, AssemblyFilePattern);
                }
                catch (Exception)
                {
                    continue;
                }

                foreach (string file in files)
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    if (name.IndexOf(".Hmi", StringComparison.OrdinalIgnoreCase) >= 0
                        || name.IndexOf(".AddIn", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        continue;
                    }

                    try
                    {
                        if (AssemblyName.GetAssemblyName(file) != null)
                        {
                            return file;
                        }
                    }
                    catch (Exception)
                    {
                        // 读不出程序集信息的跳过
                    }
                }
            }

            throw new FileNotFoundException("在 API 目录里找不到可用的 Siemens 程序集。");
        }

        /// <summary>
        /// 注册 AppDomain 级 AssemblyResolve 处理器，使 Siemens.Engineering* 程序集
        /// 从 <see cref="TiaEnvironmentInfo.ApiDirectory"/> 加载。
        /// 必须在任何 Siemens 类型被 JIT 触碰之前调用一次。
        /// </summary>
        /// <param name="info">探测到的环境。</param>
        /// <param name="logger">日志器。</param>
        public static void InstallAssemblyResolver(TiaEnvironmentInfo info, Logger logger)
        {
            if (_resolverInstalled)
            {
                // ★ 显式化"一个进程只能用一个版本"这条隐含约束。
                //
                //   以前这里直接静默 return：同一个进程里想换第二个 Openness 版本时，
                //   解析钩子的搜索目录**仍是按第一次登记的那个环境**建的（不会重建），
                //   于是换过去之后一旦需要解析该版本自己的依赖（Contract / ClientAdapter /
                //   实现程序集），就会以"某个 Siemens 程序集加载失败"的形式炸出来 ——
                //   报错现象离真正的原因（在这里换了版本）很远，很难定位。
                //   现在在这里就把这件事写进日志，说明"发生了什么、为什么危险"。
                //
                //   这里刻意**只告警、不抛异常**：跨版本自检（--apimatrix / --apiprobe）
                //   本来就是"在同一进程里逐个版本只读元数据"，那是合法且有价值的用法，
                //   硬抛会把那条路堵死。真正会踩坑的是"换版本之后还要驱动 TIA"的场景，
                //   那种情况必须另起进程（归档工具就是这么做的，见 BatchChildJob）——
                //   调用方应当用 BoundMajorVersion 判断，不一致就改走子进程。
                if (info != null
                    && !string.IsNullOrEmpty(_resolverApiDirectory)
                    && !string.Equals(_resolverApiDirectory, info.ApiDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    logger.Warning("本进程的 Openness 程序集解析目录**已经登记为另一个版本**，不再变更：");
                    logger.Warning("  已登记：" + _resolverApiDirectory + "（V" + _resolverMajorVersion + "）");
                    logger.Warning("  本次请求：" + info.ApiDirectory + "（V" + info.MajorVersion + "）");
                    logger.Warning("  后果：若在进程内驱动 TIA，会加载出“V" + info.MajorVersion
                        + " 主程序集 + V" + _resolverMajorVersion + " 依赖”的混合体，"
                        + "报 MissingMethodException 之类的怪错。");
                    logger.Warning("  出路：真正要驱动 TIA 的操作改用**子进程**（干净进程重新绑定该版本）；"
                        + "只读元数据自检（--apiprobe 等）不受影响。");
                }

                return;
            }

            // 解析时按此顺序查找：主 API 目录优先，其次是 Bin\PublicAPI 下的补充目录。
            List<string> searchDirectories = BuildSearchDirectories(info);

            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string directoryPath in searchDirectories)
            {
                string[] files;
                try
                {
                    files = Directory.GetFiles(directoryPath, "Siemens.*.dll");
                }
                catch (Exception)
                {
                    continue;
                }

                foreach (string path in files)
                {
                    string simpleName = Path.GetFileNameWithoutExtension(path);
                    if (!map.ContainsKey(simpleName))
                    {
                        map.Add(simpleName, path);
                    }
                }
            }

            logger.Debug("AssemblyResolve 搜索路径共 " + searchDirectories.Count
                + " 个目录，索引到 " + map.Count + " 个 Siemens 程序集。");

            AppDomain.CurrentDomain.AssemblyResolve += delegate (object sender, ResolveEventArgs args)
            {
                string simpleName = GetSimpleName(args.Name);
                logger.Debug("AssemblyResolve 请求：" + simpleName + "（来源：" + args.RequestingAssembly + "）");

                if (!simpleName.StartsWith("Siemens.", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                Version requested = ParseRequestedVersion(args.Name);

                string candidatePath;
                if (map.TryGetValue(simpleName, out candidatePath) && File.Exists(candidatePath))
                {
                    // ★ 版本对不上要明确说出来。
                    //   这是"环境探测全过、一归档就报程序集加载失败"的真正原因：
                    //   exe 是按某个 TIA 版本的 Openness DLL 编译的，运行时就只认那个版本号；
                    //   本机装的是另一个主版本时，同名文件也救不了（CLR 拒绝加载）。
                    Version available = TryReadAssemblyVersion(candidatePath);
                    if (requested != null && available != null && requested.Major != available.Major)
                    {
                        logger.Warning("程序集版本不匹配：" + simpleName
                            + " 需要 " + requested + "（本 exe 编译时用的 Openness 版本），"
                            + "而 " + candidatePath + " 是 " + available + "。跨主版本无法加载。");
                        logger.Warning("  处理办法：① 用本机版本的 Openness DLL 重新编译一份 exe；"
                            + "② 改用“打包项目文件夹（.zip）”模式（不经过 Openness）；"
                            + "③ 跑一次 --envdump 看完整诊断（会打印需要什么版本、本机有什么版本）。");
                    }
                    else
                    {
                        logger.Debug("  -> 命中：" + candidatePath);
                    }

                    return Assembly.LoadFrom(candidatePath);
                }

                // 兜底：按目录顺序直接拼路径（覆盖 map 建立时目录不可读等情形）。
                foreach (string directoryPath in searchDirectories)
                {
                    string fallback = Path.Combine(directoryPath, simpleName + ".dll");
                    if (File.Exists(fallback))
                    {
                        logger.Debug("  -> 回退命中：" + fallback);
                        return Assembly.LoadFrom(fallback);
                    }
                }

                logger.Warning("无法解析 Siemens 程序集：" + simpleName
                    + (requested == null ? string.Empty : "（需要版本 " + requested + "）")
                    + "。已搜索 " + searchDirectories.Count + " 个目录。");
                logger.Warning("  排错提示：Siemens 程序集不在这些目录里（老版本 TIA 的 Openness DLL 可能"
                    + "在 PublicAPI\\Vxx 下的 net48 子目录，或 Bin\\PublicAPI）。"
                    + "跑一次 --envdump 会把每个目录里实际有哪些 Siemens DLL、分别是什么版本列出来。");
                return null;
            };

            _resolverInstalled = true;
            _resolverApiDirectory = info == null ? string.Empty : info.ApiDirectory;
            _resolverMajorVersion = info == null ? 0 : info.MajorVersion;
            logger.Debug("AssemblyResolve 处理器已注册（按 " + _resolverApiDirectory + " 建立搜索目录）。");
        }

        /// <summary>
        /// 从 Assembly 全名里解析出请求的版本号；解析不了返回 null。
        /// </summary>
        private static Version ParseRequestedVersion(string fullName)
        {
            try
            {
                AssemblyName name = new AssemblyName(fullName);
                return name.Version;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 读一个 DLL 文件里的程序集版本（只读元数据，不加载它）。
        /// </summary>
        private static Version TryReadAssemblyVersion(string path)
        {
            try
            {
                return AssemblyName.GetAssemblyName(path).Version;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 从 Assembly 全名里取出简单名（去掉 Version/Culture/PublicKeyToken 尾部）。
        /// </summary>
        /// <param name="fullName">程序集全名。</param>
        /// <returns>简单名。</returns>
        private static string GetSimpleName(string fullName)
        {
            int commaIndex = fullName.IndexOf(',');
            return commaIndex > 0 ? fullName.Substring(0, commaIndex).Trim() : fullName.Trim();
        }

        private static TiaEnvironmentInfo TryCreateFromEnvironmentVariable(Logger logger, List<string> notes)
        {
            string value = Environment.GetEnvironmentVariable(ApiDirectoryEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(value))
            {
                notes.Add("环境变量 " + ApiDirectoryEnvironmentVariable + " 未设置。");
                return null;
            }

            string trimmed = value.Trim();
            logger.Debug("尝试环境变量指定的 API 目录：" + trimmed);
            TiaEnvironmentInfo info = TryBuildFromDirectory(trimmed, "环境变量 " + ApiDirectoryEnvironmentVariable);
            if (info == null)
            {
                notes.Add("环境变量 " + ApiDirectoryEnvironmentVariable + " 指向的目录里没有 Siemens.Engineering*.dll：" + trimmed);
            }
            return info;
        }

        /// <summary>
        /// 来源①：注册表 Openness 键里直接写着 Openness 程序集的**绝对路径**。
        ///
        /// 实测结构（V21）：
        ///   HKLM\SOFTWARE\Siemens\Automation\Openness\21.0\PublicAPI\21.0.0.0\net48
        ///     Siemens.Engineering.Base = E:\...\Portal V21\PublicAPI\V21\net48\Siemens.Engineering.Base.dll
        ///
        /// 注意顶层 Openness 键只有一个**空的** (默认) 值 —— 只读顶层等于没读，
        /// 这正是以前"注册表来源"形同虚设的原因。真正的路径在子键里，必须递归。
        /// 好处是它与安装位置完全无关，TIA 装在哪都能找到。
        /// </summary>
        /// <param name="logger">日志器。</param>
        /// <param name="notes">排错说明收集器。</param>
        /// <returns>候选 API 目录列表。</returns>
        private static List<ApiDirectoryCandidate> CollectRegistryOpennessApiDirectories(Logger logger, List<string> notes)
        {
            List<ApiDirectoryCandidate> result = new List<ApiDirectoryCandidate>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool anyKeyFound = false;

            foreach (RegistryView view in RegistryViews)
            {
                RegistryKey key = OpenRegistryKey(view, OpennessRegistryPath, notes);
                if (key == null)
                {
                    continue;
                }

                anyKeyFound = true;
                using (key)
                {
                    // 来源说明里带上视图，排错时能一眼看出是从哪一份注册表读到的
                    CollectRegistryAssemblyPaths(
                        key,
                        DescribeRegistryView(view) + @" HKLM\SOFTWARE\Siemens\Automation\Openness",
                        result, seen, logger);
                }
            }

            if (!anyKeyFound)
            {
                notes.Add(@"注册表里没有 Openness 键（已查 64 位与 32 位两个视图）："
                    + @"HKLM\SOFTWARE\Siemens\Automation\Openness");
            }
            else if (result.Count == 0)
            {
                notes.Add("Openness 键存在，但里面没有指向 PublicAPI 的程序集路径。");
            }

            return result;
        }

        /// <summary>
        /// 按指定视图打开一个 HKLM 下的注册表子键；打不开或不存在时返回 null（并把原因记进 notes）。
        /// </summary>
        /// <param name="view">注册表视图。</param>
        /// <param name="path">相对 HKLM\SOFTWARE 的路径。</param>
        /// <param name="notes">排错说明收集器。</param>
        /// <returns>打开成功返回键，否则 null。</returns>
        private static RegistryKey OpenRegistryKey(RegistryView view, string path, List<string> notes)
        {
            RegistryKey baseKey = null;
            try
            {
                baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                return baseKey.OpenSubKey(path);
            }
            catch (Exception ex)
            {
                notes.Add("读取注册表失败（" + DescribeRegistryView(view) + "）：" + path + " -> " + ex.Message);
                return null;
            }
            finally
            {
                if (baseKey != null)
                {
                    baseKey.Close();
                }
            }
        }

        /// <summary>
        /// 注册表视图的可读名称（写进日志与来源说明，便于排错）。
        /// </summary>
        /// <param name="view">注册表视图。</param>
        /// <returns>可读名称。</returns>
        private static string DescribeRegistryView(RegistryView view)
        {
            return view == RegistryView.Registry64 ? "64 位视图" : "32 位视图";
        }

        /// <summary>
        /// 递归一个注册表子树，收集所有"值是 ...\PublicAPI\...\Siemens.Engineering*.dll 绝对路径"的键值。
        /// </summary>
        /// <param name="key">当前键。</param>
        /// <param name="keyPath">当前键的可读路径（用于日志与来源标注）。</param>
        /// <param name="result">结果收集器。</param>
        /// <param name="seen">去重集合。</param>
        /// <param name="logger">日志器。</param>
        private static void CollectRegistryAssemblyPaths(
            RegistryKey key, string keyPath, List<ApiDirectoryCandidate> result, HashSet<string> seen, Logger logger)
        {
            string[] valueNames;
            try
            {
                valueNames = key.GetValueNames();
            }
            catch (Exception)
            {
                valueNames = new string[0];
            }

            foreach (string valueName in valueNames)
            {
                string value = null;
                try
                {
                    value = key.GetValue(valueName) as string;
                }
                catch (Exception)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                // 只认"指向 Openness 程序集文件"的值；同一个键里的版本号、公钥标记等一律跳过。
                if (!value.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                    || value.IndexOf(PublicApiDirectoryName, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                string directory;
                try
                {
                    directory = Path.GetDirectoryName(value);
                }
                catch (Exception)
                {
                    continue;
                }

                if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                {
                    continue;
                }

                // 目录里必须真的有程序集：注册表可能留着卸载残留。
                if (Directory.GetFiles(directory, AssemblyFilePattern).Length == 0)
                {
                    continue;
                }

                if (seen.Add(directory))
                {
                    ApiDirectoryCandidate candidate = new ApiDirectoryCandidate();
                    candidate.Directory = directory;
                    candidate.Source = "注册表 " + keyPath + "\\" + valueName;
                    result.Add(candidate);
                    logger.Debug("注册表命中 " + keyPath + "\\" + valueName + " → " + directory);
                }
            }

            string[] subKeyNames;
            try
            {
                subKeyNames = key.GetSubKeyNames();
            }
            catch (Exception)
            {
                subKeyNames = new string[0];
            }

            foreach (string subKeyName in subKeyNames)
            {
                // ★ 跳过 AllowList：那是"允许调用 Openness 的程序清单"，
                //   每授权一次就多一个 Entry 子键，会一直长；递归它纯属浪费。
                if (string.Equals(subKeyName, "AllowList", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                RegistryKey child = null;
                try
                {
                    child = key.OpenSubKey(subKeyName);
                }
                catch (Exception)
                {
                    continue;
                }

                if (child == null)
                {
                    continue;
                }

                using (child)
                {
                    CollectRegistryAssemblyPaths(child, keyPath + "\\" + subKeyName, result, seen, logger);
                }
            }
        }

        /// <summary>
        /// 来源②：注册表 _InstalledSW 里 Siemens 安装器登记的 Portal 安装路径。
        ///
        /// 这是唯一能拿到**自定义安装目录**的地方 —— TIA 允许装在任意路径
        /// （实测见过 D:\V19\Portal V18 这种），那种机器扫 Program Files 一个都找不到。
        /// </summary>
        /// <param name="logger">日志器。</param>
        /// <param name="notes">排错说明收集器。</param>
        /// <returns>Portal 安装目录列表（已校验目录与 PublicAPI 真实存在）。</returns>
        private static List<string> CollectRegistryPortalDirectories(Logger logger, List<string> notes)
        {
            List<string> result = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (RegistryView view in RegistryViews)
            {
                RegistryKey root = OpenRegistryKey(view, InstalledSoftwareRegistryPath, notes);
                if (root == null)
                {
                    continue;
                }

                using (root)
                {
                    string rootPath = DescribeRegistryView(view)
                        + @" HKLM\SOFTWARE\Siemens\Automation\_InstalledSW";

                    string[] productNames;
                    try
                    {
                        productNames = root.GetSubKeyNames();
                    }
                    catch (Exception)
                    {
                        productNames = new string[0];
                    }

                    foreach (string productName in productNames)
                    {
                        // 只认 TIA Portal 自己的产品键（TIAP21 是新版命名，TIAP12 是老版命名）。
                        if (!productName.StartsWith("TIAP", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        string installPath = ReadPortalInstallPath(root, productName);
                        if (string.IsNullOrWhiteSpace(installPath))
                        {
                            notes.Add(rootPath + "\\" + productName
                                + " 没有记录安装路径（多半是卸载残留的空壳子键）。");
                            continue;
                        }

                        string portalDirectory = installPath.TrimEnd('\\', '/');

                        // ★ 拿到路径后必须校验：_InstalledSW 会留下已卸载版本的记录
                        //   （实测 TIAP12 只剩空壳），光看注册表会误以为"本机装了 V12"。
                        if (!Directory.Exists(portalDirectory))
                        {
                            notes.Add("注册表记录的 TIA 安装目录已不存在（卸载残留）：" + portalDirectory);
                            continue;
                        }

                        if (!Directory.Exists(Path.Combine(portalDirectory, PublicApiDirectoryName)))
                        {
                            notes.Add("注册表记录的 TIA 安装目录里没有 PublicAPI（装本体时没勾 Openness 组件）："
                                + portalDirectory);
                            continue;
                        }

                        if (seen.Add(portalDirectory))
                        {
                            result.Add(portalDirectory);
                            logger.Debug(rootPath + "\\" + productName + " → " + portalDirectory);
                        }
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// 读某个产品键登记的 Portal 安装路径。
        ///
        /// 实测（V21）：路径写在子键 EditionMain 的 Path 值里，
        ///   _InstalledSW\TIAP21\EditionMain → E:\Program Files\Siemens\Automation\Portal V21
        /// 少数版本写在 Global 里，所以两个位置都试。
        /// 刻意**不递归**：同级的 Products\SEBU_* 里也有 Path，但那是 "Siemens\Automation" 根目录，
        /// 不是 Portal 安装目录，递归会拿到错的值。
        /// </summary>
        /// <param name="installedSoftwareRoot">_InstalledSW 根键。</param>
        /// <param name="productName">产品键名，如 TIAP21。</param>
        /// <returns>安装路径；读不到返回 null。</returns>
        private static string ReadPortalInstallPath(RegistryKey installedSoftwareRoot, string productName)
        {
            RegistryKey product = null;
            try
            {
                product = installedSoftwareRoot.OpenSubKey(productName);
            }
            catch (Exception)
            {
                return null;
            }

            if (product == null)
            {
                return null;
            }

            using (product)
            {
                // 依次尝试：产品键自己（少数版本）、EditionMain（实测位置）、Global（备用）。
                foreach (string keyName in new string[] { null, "EditionMain", "Global" })
                {
                    RegistryKey source = product;
                    RegistryKey opened = null;

                    if (keyName != null)
                    {
                        try
                        {
                            opened = product.OpenSubKey(keyName);
                        }
                        catch (Exception)
                        {
                            opened = null;
                        }

                        if (opened == null)
                        {
                            continue;
                        }

                        source = opened;
                    }

                    try
                    {
                        string text = source.GetValue("Path") as string;
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            return text.Trim();
                        }
                    }
                    catch (Exception)
                    {
                        // 读不到就试下一个位置
                    }
                    finally
                    {
                        if (opened != null)
                        {
                            opened.Close();
                        }
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// ★ 汇总全部"能找到 Openness 的途径"，返回去重并排好序的候选 API 目录。
        ///
        /// 为什么要四个来源：**TIA Portal 允许装在任意目录**（不限于 Program Files）。
        /// 只扫 “各盘 Program Files\Siemens\Automation\Portal V*” 的话，
        /// 装在 D:\V19\Portal V18 这类自定义路径的机器会一个都找不到 ——
        /// 实测就有这样一份日志：体检报"Openness 组件尚未安装"，
        /// 但同一份日志里用手动指定的同一个目录却能正常绑定，结论自相矛盾、把人带偏。
        ///
        /// 四个来源按可靠性排序：
        ///   ① 注册表 Openness 键里的程序集绝对路径（与安装位置无关，最可靠）；
        ///   ② 注册表 _InstalledSW 登记的 Portal 安装路径（支持自定义目录）；
        ///   ③ 常见安装位置扫描（Program Files\Siemens\Automation\Portal V*）；
        ///   ④ 浅层兜底扫描（各固定盘根目录及其一级子目录下的 Portal V*），
        ///      只在 ①②③ 全部落空时才执行，避免平时白付 IO 开销。
        /// </summary>
        /// <param name="logger">日志器。</param>
        /// <param name="notes">排错说明收集器。</param>
        /// <returns>候选 API 目录，已按"本程序编译版本优先、其余版本降序"排序。</returns>
        private static List<ApiDirectoryCandidate> CollectCandidateApiDirectories(Logger logger, List<string> notes)
        {
            List<ApiDirectoryCandidate> candidates = new List<ApiDirectoryCandidate>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 来源①：注册表 Openness 键
            foreach (ApiDirectoryCandidate candidate in CollectRegistryOpennessApiDirectories(logger, notes))
            {
                if (seen.Add(candidate.Directory))
                {
                    candidates.Add(candidate);
                }
            }

            // 来源②：注册表登记的自定义安装路径
            foreach (ApiDirectoryCandidate candidate in CollectPortalApiDirectories(
                CollectRegistryPortalDirectories(logger, notes),
                "注册表登记的安装路径（支持自定义安装目录）", logger, notes))
            {
                if (seen.Add(candidate.Directory))
                {
                    candidates.Add(candidate);
                }
            }

            // 来源③：常见安装位置
            foreach (ApiDirectoryCandidate candidate in CollectPortalApiDirectories(
                CollectStandardPortalDirectories(logger, notes), "常见安装位置扫描", logger, notes))
            {
                if (seen.Add(candidate.Directory))
                {
                    candidates.Add(candidate);
                }
            }

            // 来源④：兜底浅层扫描（有 IO 开销，只在前面全空时跑）
            if (candidates.Count == 0)
            {
                notes.Add("注册表与常见安装位置都没有找到 Openness，改为浅层扫描各固定盘根目录及一级子目录。");
                foreach (ApiDirectoryCandidate candidate in CollectPortalApiDirectories(
                    CollectLoosePortalDirectories(logger, notes), "浅层全盘扫描（非常规安装目录）", logger, notes))
                {
                    if (seen.Add(candidate.Directory))
                    {
                        candidates.Add(candidate);
                    }
                }
            }

            // 排序：与本程序编译版本一致的最优先，其余按版本号从高到低。
            int requiredMajor = GetReferencedOpennessMajorVersion();
            candidates.Sort(delegate (ApiDirectoryCandidate a, ApiDirectoryCandidate b)
            {
                int aVersion = ExtractVersionNumber(a.Directory);
                int bVersion = ExtractVersionNumber(b.Directory);
                bool aPreferred = requiredMajor > 0 && aVersion == requiredMajor;
                bool bPreferred = requiredMajor > 0 && bVersion == requiredMajor;

                if (aPreferred != bPreferred)
                {
                    return aPreferred ? -1 : 1;
                }

                if (aVersion != bVersion)
                {
                    return bVersion.CompareTo(aVersion);
                }

                return string.Compare(a.Directory, b.Directory, StringComparison.OrdinalIgnoreCase);
            });

            foreach (ApiDirectoryCandidate candidate in candidates)
            {
                logger.Debug("候选 API 目录：" + candidate.Directory + "（来源：" + candidate.Source + "）");
            }

            return candidates;
        }

        /// <summary>
        /// 把一批 Portal 安装目录展开成其中的 API 目录（拼 PublicAPI 后逐版本收集）。
        /// </summary>
        /// <param name="portalDirectories">Portal 安装目录。</param>
        /// <param name="source">来源说明（写进环境的 DetectionSource）。</param>
        /// <param name="logger">日志器。</param>
        /// <param name="notes">排错说明收集器。</param>
        /// <returns>候选 API 目录。</returns>
        private static List<ApiDirectoryCandidate> CollectPortalApiDirectories(
            IEnumerable<string> portalDirectories, string source, Logger logger, List<string> notes)
        {
            List<ApiDirectoryCandidate> result = new List<ApiDirectoryCandidate>();

            foreach (string portalDirectory in portalDirectories)
            {
                string publicApiRoot = Path.Combine(portalDirectory, PublicApiDirectoryName);
                if (!Directory.Exists(publicApiRoot))
                {
                    // 区分两种情况 —— 对排错的意义完全不同，日志不能含糊：
                    //   · 有 TIA 主程序、只是没 PublicAPI → 装本体时没勾 Openness 组件（补装即可）
                    //   · 连主程序都没有                 → 多半只是卸载残留的空目录（补装也解决不了）
                    bool hasPortalExecutable = File.Exists(
                        Path.Combine(portalDirectory, "bin", "Siemens.Automation.Portal.exe"));

                    notes.Add(hasPortalExecutable
                        ? "发现 TIA 安装但没有 PublicAPI 目录（装本体时没勾 Openness 组件）：" + portalDirectory
                        : "发现 TIA 残留目录（既没有主程序也没有 PublicAPI，多半是卸载残留）：" + portalDirectory);
                    continue;
                }

                foreach (string apiDirectory in CollectVersionDirectories(publicApiRoot, notes))
                {
                    ApiDirectoryCandidate candidate = new ApiDirectoryCandidate();
                    candidate.Directory = apiDirectory;
                    candidate.Source = source;
                    result.Add(candidate);
                }
            }

            return result;
        }

        /// <summary>
        /// 来源③：扫常见安装位置 —— 各固定盘的
        /// Program Files[ (x86)]\Siemens\Automation\Portal V*。
        /// </summary>
        /// <param name="logger">日志器。</param>
        /// <param name="notes">排错说明收集器。</param>
        /// <returns>Portal 安装目录列表。</returns>
        private static List<string> CollectStandardPortalDirectories(Logger logger, List<string> notes)
        {
            List<string> result = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string automationRoot in EnumerateAutomationRoots(logger, notes))
            {
                string[] portalDirectories;
                try
                {
                    portalDirectories = Directory.GetDirectories(automationRoot, PortalDirectoryPattern);
                }
                catch (Exception ex)
                {
                    notes.Add("枚举安装根失败：" + automationRoot + " -> " + ex.Message);
                    continue;
                }

                foreach (string portalDirectory in portalDirectories)
                {
                    if (seen.Add(portalDirectory))
                    {
                        result.Add(portalDirectory);
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// 来源④：兜底 —— 在各固定盘的**浅层**找名为 "Portal V*" 的目录。
        ///
        /// 为什么需要：TIA 允许装到任意目录，极端情况下注册表里也没有记录
        /// （手工拷贝的目录树、注册表被清理等）。实测见过的非常规布局：
        ///   D:\Portal V18        直接放在盘根
        ///   D:\V19\Portal V18    放在盘根下的一级子目录里
        /// 所以只扫"盘根 + 盘根下的一级子目录"两层，不递归整个磁盘。
        /// 代价可控，而且只在前面几个来源全都落空时才会执行。
        /// </summary>
        /// <param name="logger">日志器。</param>
        /// <param name="notes">排错说明收集器。</param>
        /// <returns>Portal 安装目录列表。</returns>
        private static List<string> CollectLoosePortalDirectories(Logger logger, List<string> notes)
        {
            List<string> result = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
                {
                    continue;
                }

                string root = drive.RootDirectory.FullName;

                // 盘根，以及盘根下的一级子目录
                AddPortalDirectories(root, result, seen);
                foreach (string firstLevel in SafeGetDirectories(root))
                {
                    AddPortalDirectories(firstLevel, result, seen);
                }
            }

            foreach (string directory in result)
            {
                logger.Debug("浅层扫描命中：" + directory);
            }

            if (result.Count == 0)
            {
                notes.Add("浅层扫描（盘根及一级子目录）也没有找到 Portal V* 目录。");
            }

            return result;
        }

        /// <summary>
        /// 把一个目录下匹配 "Portal V*" 的子目录加进结果集（无权限目录静默跳过）。
        /// </summary>
        /// <param name="parent">父目录。</param>
        /// <param name="result">结果收集器。</param>
        /// <param name="seen">去重集合。</param>
        private static void AddPortalDirectories(string parent, List<string> result, HashSet<string> seen)
        {
            string[] matches;
            try
            {
                matches = Directory.GetDirectories(parent, PortalDirectoryPattern);
            }
            catch (Exception)
            {
                // System Volume Information 之类的无权限目录
                return;
            }

            foreach (string match in matches)
            {
                if (seen.Add(match))
                {
                    result.Add(match);
                }
            }
        }

        private static IEnumerable<string> EnumerateAutomationRoots(Logger logger, List<string> notes)
        {
            HashSet<string> roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // ★ 位数陷阱：本程序是 AnyCPU 的 WinExe，默认以 32 位运行，
            //   此时环境变量 "ProgramFiles" 给出的是 "C:\Program Files (x86)"，
            //   真正的 64 位 Program Files 要用 "ProgramW6432" 才拿得到。
            //   TIA Portal 是 64 位程序，装在真正的 Program Files 下，
            //   所以三个变量都要看，并且**先确认目录存在**再加（否则后面枚举会抛异常、白记一条失败）。
            foreach (string variable in new string[] { "ProgramW6432", "ProgramFiles", "ProgramFiles(x86)" })
            {
                string programFiles = Environment.GetEnvironmentVariable(variable);
                if (string.IsNullOrWhiteSpace(programFiles))
                {
                    continue;
                }

                string candidate = Path.Combine(programFiles, "Siemens", "Automation");
                if (Directory.Exists(candidate))
                {
                    roots.Add(candidate);
                }
            }

            // 很多人（包括本项目开发机）把 TIA 装到非系统盘，例如 D:\、E:\，
            // 因此必须扫描所有固定磁盘，而不是只认 C 盘。
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
                {
                    continue;
                }

                foreach (string folderName in new string[] { "Program Files", "Program Files (x86)" })
                {
                    string candidate = Path.Combine(drive.RootDirectory.FullName, folderName, "Siemens", "Automation");
                    if (Directory.Exists(candidate))
                    {
                        roots.Add(candidate);
                    }
                }
            }

            foreach (string root in roots)
            {
                logger.Debug("候选安装根：" + root);
            }

            if (roots.Count == 0)
            {
                notes.Add("没有找到任何 Siemens\\Automation 安装根（磁盘扫描后仍为空）。");
            }

            return roots;
        }

        private static IEnumerable<string> CollectVersionDirectories(string publicApiRoot, List<string> notes)
        {
            List<string> result = new List<string>();

            // 老布局：PublicAPI 下直接就是 DLL。
            if (Directory.GetFiles(publicApiRoot, AssemblyFilePattern).Length > 0)
            {
                result.Add(publicApiRoot);
            }

            // 新布局：PublicAPI\Vxx\ 或 PublicAPI\Vxx\netYY\
            string[] versionDirectories;
            try
            {
                versionDirectories = Directory.GetDirectories(publicApiRoot);
            }
            catch (Exception ex)
            {
                notes.Add("枚举 PublicAPI 子目录失败：" + publicApiRoot + " -> " + ex.Message);
                return result;
            }

            foreach (string versionDirectory in versionDirectories)
            {
                // ★ 跳过 *.AddIn 目录。
                //   它们里面装的是 AddIn（TIA 插件）宿主程序集，不是给外部程序调用的归档 API：
                //   实测某台机器上 PublicAPI 下有 V16.AddIn / V17.AddIn / V18.AddIn / V19.AddIn，
                //   目录名里的版本号也解析不出来，结果在"可用版本"列表里显示成一堆 V0，
                //   还会干扰"自动挑最新版本"的判断。
                if (Path.GetFileName(versionDirectory).IndexOf(".AddIn", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    continue;
                }

                if (Directory.GetFiles(versionDirectory, AssemblyFilePattern).Length > 0)
                {
                    result.Add(versionDirectory);
                }

                // V21 这一类会把 net48 之类的目标框架目录再嵌一层。
                foreach (string frameworkDirectory in SafeGetDirectories(versionDirectory))
                {
                    if (Directory.GetFiles(frameworkDirectory, AssemblyFilePattern).Length > 0)
                    {
                        result.Add(frameworkDirectory);
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// 从完整路径里取出版本号，用于"哪个更新"的排序。
        ///
        /// 规则与 <see cref="ParseMajorVersion"/> 保持一致（只看"V"开头的段、只取开头连续数字），
        /// 否则会出现 V15.1 被算成 151、排到 V19 前面这类错误排序。
        /// </summary>
        internal static int ExtractVersionNumber(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return 0;
            }

            string[] segments = path.Split(Path.DirectorySeparatorChar);
            for (int i = segments.Length - 1; i >= 0; i--)
            {
                if (segments[i].StartsWith("V", StringComparison.OrdinalIgnoreCase))
                {
                    int value = ParseMajorVersion(segments[i]);
                    if (value > 0)
                    {
                        return value;
                    }
                }
            }

            return 0;
        }

        private static string StripNonDigits(string input)
        {
            char[] buffer = new char[input.Length];
            int count = 0;
            for (int i = 0; i < input.Length; i++)
            {
                if (char.IsDigit(input[i]))
                {
                    buffer[count++] = input[i];
                }
            }
            return new string(buffer, 0, count);
        }

        private static TiaEnvironmentInfo TryBuildFromDirectory(string directory, string source)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return null;
            }

            string[] assemblies;
            try
            {
                assemblies = Directory.GetFiles(directory, AssemblyFilePattern);
            }
            catch (Exception)
            {
                return null;
            }

            if (assemblies.Length == 0)
            {
                return null;
            }

            Array.Sort(assemblies, StringComparer.OrdinalIgnoreCase);

            string versionDirectoryName = ExtractVersionDirectoryName(directory);
            int majorVersion = ParseMajorVersion(versionDirectoryName);
            string portalDirectory = ExtractPortalDirectory(directory);

            return new TiaEnvironmentInfo(
                portalDirectory,
                versionDirectoryName,
                majorVersion,
                directory,
                assemblies,
                CollectRuntimeResolutionDirectories(portalDirectory),
                source);
        }

        /// <summary>
        /// 收集"运行期解析"需要的补充目录（主 API 目录之外的部分）。
        ///
        /// V21 实测布局：<Portal>\Bin\PublicAPI\ 下还散落着 Openness 运行时必需的
        /// 契约/适配器/实现程序集，其中一些在根目录，另一些在 V21\net48 子目录里。
        /// 这里采取"整棵子树都收进来"的策略，比硬编码某几个子目录更能抗版本差异。
        /// </summary>
        /// <param name="portalDirectory">Portal 安装根目录；为空或目录不存在时返回空清单。</param>
        /// <returns>补充目录清单（已去重、已排序）。</returns>
        private static IReadOnlyList<string> CollectRuntimeResolutionDirectories(string portalDirectory)
        {
            List<string> result = new List<string>();

            if (string.IsNullOrEmpty(portalDirectory) || !Directory.Exists(portalDirectory))
            {
                return result;
            }

            string binaryApiRoot = Path.Combine(portalDirectory, "Bin");
            binaryApiRoot = Path.Combine(binaryApiRoot, PublicApiDirectoryName);

            if (!Directory.Exists(binaryApiRoot))
            {
                return result;
            }

            result.Add(binaryApiRoot);
            result.AddRange(CollectSubDirectoriesContainingAssemblies(binaryApiRoot));
            return result;
        }

        /// <summary>
        /// 递归收集某个根目录下所有"含 .dll 文件"的子目录。
        /// 只收目录本身，不枚举文件，避免在解析钩子里做大量 IO。
        /// </summary>
        /// <param name="root">根目录。</param>
        /// <returns>子目录清单。</returns>
        private static List<string> CollectSubDirectoriesContainingAssemblies(string root)
        {
            List<string> found = new List<string>();
            try
            {
                foreach (string sub in Directory.GetDirectories(root, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        if (Directory.GetFiles(sub, "*.dll").Length > 0)
                        {
                            found.Add(sub);
                        }
                    }
                    catch (Exception)
                    {
                        // 单个目录不可读就跳过，不影响其余目录。
                    }
                }
            }
            catch (Exception)
            {
                // 根目录不可读时退化成"只认根目录"。
            }
            return found;
        }

        private static string ExtractVersionDirectoryName(string directory)
        {
            DirectoryInfo info = new DirectoryInfo(directory);
            if (info.Name.StartsWith("net", StringComparison.OrdinalIgnoreCase) && info.Parent != null)
            {
                return info.Parent.Name;
            }
            return info.Name;
        }

        /// <summary>
        /// 从版本目录名里解析主版本号：V19 → 19，V15.1 → 15，net48 / V16.AddIn → 0。
        ///
        /// 注意"取开头的连续数字"这个规则：TIA 的小版本目录叫 V15.1，直接 int.TryParse
        /// 会失败（返回 0），而 StripNonDigits 之后会变成 151 —— 后者更坑：
        /// 实测某台机器上因此把"最新版本"选成了 V15.1 而不是 V19（151 > 19）。
        /// </summary>
        internal static int ParseMajorVersion(string versionDirectoryName)
        {
            if (string.IsNullOrEmpty(versionDirectoryName))
            {
                return 0;
            }

            string text = versionDirectoryName.Trim();
            if (text.StartsWith("V", StringComparison.OrdinalIgnoreCase))
            {
                text = text.Substring(1);
            }

            int length = 0;
            while (length < text.Length && char.IsDigit(text[length]))
            {
                length++;
            }

            if (length == 0)
            {
                return 0;
            }

            int value;
            return int.TryParse(text.Substring(0, length), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
                ? value
                : 0;
        }

        private static string ExtractPortalDirectory(string directory)
        {
            DirectoryInfo info = new DirectoryInfo(directory);
            while (info != null)
            {
                if (info.Name.StartsWith("Portal", StringComparison.OrdinalIgnoreCase))
                {
                    return info.FullName;
                }
                info = info.Parent;
            }
            return string.Empty;
        }

        private static string[] SafeGetDirectories(string path)
        {
            try
            {
                return Directory.GetDirectories(path);
            }
            catch (Exception)
            {
                return new string[0];
            }
        }

        private static ToolException CreateDetectionFailure(string explicitDirectory, List<string> notes)
        {
            System.Text.StringBuilder builder = new System.Text.StringBuilder();
            builder.AppendLine("未能定位 TIA Portal Openness 的 PublicAPI 目录。");

            if (!string.IsNullOrEmpty(explicitDirectory))
            {
                builder.AppendLine("已尝试：" + explicitDirectory);
            }

            builder.AppendLine();
            builder.AppendLine("请按以下顺序排查：");
            builder.AppendLine("  1) 确认已安装 TIA Portal，并在安装选项中勾选了 \"TIA Portal Openness\" 组件；");
            builder.AppendLine("     安装后应存在类似目录（盘符、版本与安装位置都可能不同）：");
            builder.AppendLine("       <盘符>:\\Program Files\\Siemens\\Automation\\Portal V21\\PublicAPI\\V21\\net48\\");
            builder.AppendLine("       D:\\V19\\Portal V18\\PublicAPI\\V18\\        ← 装在自定义目录也是合法的");
            builder.AppendLine("  2) 本程序会自动读取注册表登记的安装路径并做浅层扫描，装在非默认目录也能找到；");
            builder.AppendLine("     万一仍没找到，手工指定即可（两种方式等效）：");
            builder.AppendLine("       set TIA_PORTAL_PUBLIC_API_DIR=<你的 PublicAPI 目录>");
            builder.AppendLine("       或在界面的“API 目录”输入框里填写该目录");
            builder.AppendLine("  3) 注意 Openness 的主版本必须与 TIA Portal 主版本一致（V21 API 不能连 V20 Portal）。");
            builder.AppendLine("  4) 若 Portal 目录存在但没有 PublicAPI 子目录，说明 Openness 组件没装上，需要修改安装。");

            if (notes.Count > 0)
            {
                builder.AppendLine();
                builder.AppendLine("探测过程明细：");
                foreach (string note in notes)
                {
                    builder.AppendLine("  - " + note);
                }
            }

            return new ToolException(ExitCodes.Environment, builder.ToString());
        }

        /// <summary>
        /// 一个候选的 API 目录，附带"它是怎么被找到的"。
        ///
        /// 带上来源有两个用处：一是写进 <c>TiaEnvironmentInfo.DetectionSource</c>，
        /// 出问题时一眼能看出是注册表、常见位置还是兜底扫描找到的；
        /// 二是四个来源可能指向同一个目录，靠它保留最先（最可靠）的那条说明。
        /// </summary>
        private sealed class ApiDirectoryCandidate
        {
            /// <summary>真正存放 Siemens.Engineering*.dll 的目录。</summary>
            public string Directory;

            /// <summary>来源说明。</summary>
            public string Source;
        }
    }
}
