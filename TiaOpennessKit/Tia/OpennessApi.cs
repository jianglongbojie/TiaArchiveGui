using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;

namespace TiaOpennessKit.Tia
{
    /// <summary>
    /// 归档模式。**本工具自己的枚举**，与 Openness 版本无关。
    ///
    /// 为什么要自己定义一份：以前直接使用 <c>Siemens.Engineering.ProjectArchivationMode</c>，
    /// 于是"用哪个 Openness"这件事在**编译时**就被定死了 —— 一份 exe 只认一个主版本，
    /// 换台机器（装的是别的 TIA 版本）就必然加载失败。
    /// 现在改成按**名字**在运行时翻译成目标版本的真实枚举值（四个成员名各版本完全一致）。
    /// </summary>
    public enum ArchiveMode
    {
        /// <summary>不压缩，类似"另存为"，扩展名由 TIA 决定。</summary>
        None,

        /// <summary>压缩（推荐）。</summary>
        Compressed,

        /// <summary>丢弃可恢复数据（不可逆）。</summary>
        DiscardRestorableData,

        /// <summary>丢弃可恢复数据并压缩（不可逆）。</summary>
        DiscardRestorableDataAndCompressed
    }

    /// <summary>
    /// 运行时绑定的 Openness API：**编译期不引用任何 Siemens 程序集**，
    /// 全靠"从本机 API 目录加载程序集 + 按类型名反射调用"。
    ///
    /// 为什么非这样不可（实测踩过）：
    ///   Openness 的程序集在版本之间**连文件名都不一样** ——
    ///     V15~V19：整个 API 在 `Siemens.Engineering.dll` 里；
    ///     V20/V21：拆成了 `Siemens.Engineering.Base.dll`（+ Step7 / WinCC 模块）。
    ///   命名空间和类型名倒是完全一致（都是 Siemens.Engineering.Project / TiaPortal）。
    ///   只要代码里写了 `Project`、`TiaPortal` 这些类型，编译出来的 exe 就把"程序集名字 + 版本号"
    ///   登记进了引用表，运行时 CLR 只认那一个，换台机器必挂；而且连"绑定重定向"都救不了
    ///   （重定向只能改版本，不能改程序集名）。
    ///   所以唯一的活路是：**编译期一个 Siemens 类型都不提**，运行时按名字去问。
    ///
    /// 类比 SCL：以前相当于在编译时就把某个库的绝对符号地址写死；现在改成运行时按符号名查表。
    ///
    /// 一个进程只能绑定一个版本（同名程序集无法并存），所以这里是单例：
    /// 用 <see cref="Bind"/> 选定本机要用的那个 API 目录即可。
    /// </summary>
    public sealed class OpennessApi
    {
        private static OpennessApi _current;

        /// <summary>类型所在的命名空间前缀（各版本一致）。</summary>
        private const string NamespacePrefix = "Siemens.Engineering.";

        private readonly Logger _logger;
        private readonly TiaEnvironmentInfo _environment;
        private readonly Assembly _apiAssembly;

        private readonly Type _tiaPortalType;
        private readonly Type _tiaPortalModeType;
        private readonly Type _tiaPortalProcessType;
        private readonly Type _projectType;
        private readonly Type _archivationModeType;

        // ── 硬件 / 网络对象（读网络设备与 IP 用）─────────────────────────────
        // 这些类型**不参与归档**，所以一律"取得到就用、取不到就留 null"，
        // 不在构造函数里抛异常 —— 否则一个缺少硬件 API 的版本连归档都用不了。
        // 缺失情况由 SelfCheckDeviceRead() 明确报出来。
        // 实测：V16~V19 与 V20/V21 这些类型都在**主程序集**里
        //（V16~V19 是 Siemens.Engineering.dll，V21 是 Siemens.Engineering.Base.dll），
        // 所以没必要再去扫别的模块 DLL。
        private readonly Type _deviceType;
        private readonly Type _deviceItemType;
        private readonly Type _networkInterfaceType;
        private readonly Type _nodeType;
        private readonly Type _subnetType;
        private readonly Type _attributeInfoType;

        private OpennessApi(Logger logger, TiaEnvironmentInfo environment, Assembly apiAssembly)
        {
            _logger = logger;
            _environment = environment;
            _apiAssembly = apiAssembly;

            _tiaPortalType = RequireType("TiaPortal");
            _tiaPortalModeType = RequireType("TiaPortalMode");
            _tiaPortalProcessType = RequireType("TiaPortalProcess");
            _projectType = RequireType("Project");
            _archivationModeType = RequireType("ProjectArchivationMode");

            _deviceType = FindType("HW.Device");
            _deviceItemType = FindType("HW.DeviceItem");
            _networkInterfaceType = FindType("HW.Features.NetworkInterface");
            _nodeType = FindType("HW.Node");
            _subnetType = FindType("HW.Subnet");
            _attributeInfoType = FindType("EngineeringAttributeInfo");
        }

        /// <summary>当前已绑定的 API；未绑定时为 null。</summary>
        public static OpennessApi Current
        {
            get { return _current; }
        }

        /// <summary>已绑定的版本（如 V19）；未绑定时为空串。</summary>
        public string VersionName
        {
            get { return _environment == null ? string.Empty : _environment.VersionDirectoryName; }
        }

        /// <summary>绑定的 Openness 主版本号（如 19）；没绑定时为 0。归档产物扩展名靠它。</summary>
        public int MajorVersion
        {
            get { return _environment == null ? 0 : _environment.MajorVersion; }
        }

        /// <summary>API 程序集的文件路径（排错时最有用的一条信息）。</summary>
        public string AssemblyPath
        {
            get { return _apiAssembly == null ? string.Empty : _apiAssembly.Location; }
        }

        /// <summary>API 程序集的显示名（含版本号）。</summary>
        public string AssemblyNameText
        {
            get
            {
                if (_apiAssembly == null)
                {
                    return string.Empty;
                }

                AssemblyName name = _apiAssembly.GetName();
                return name.Name + " " + name.Version;
            }
        }

        /// <summary>
        /// 绑定到指定环境里的 Openness API。可重复调用；已绑定同一目录时直接返回。
        /// </summary>
        /// <param name="environment">探测到的环境。</param>
        /// <param name="logger">日志器。</param>
        /// <returns>绑定好的 API。</returns>
        /// <exception cref="ToolException">找不到含 TiaPortal 类型的程序集时抛出。</exception>
        public static OpennessApi Bind(TiaEnvironmentInfo environment, Logger logger)
        {
            if (environment == null)
            {
                throw new ToolException(ExitCodes.Environment, "没有可用的 Openness 环境，无法绑定 API。");
            }

            if (_current != null
                && _current._environment != null
                && string.Equals(_current._environment.ApiDirectory, environment.ApiDirectory,
                    StringComparison.OrdinalIgnoreCase))
            {
                return _current;
            }

            _current = new OpennessApi(logger, environment, LoadApiAssembly(environment, logger));
            logger.Info("已绑定 Openness API：" + _current.AssemblyNameText
                + "（" + _current.AssemblyPath + "）");
            return _current;
        }

        /// <summary>
        /// 在一个目录里找出真正承载 <c>Siemens.Engineering.TiaPortal</c> 的那个程序集。
        /// V19 及更早是 Siemens.Engineering.dll，V20/V21 是 Siemens.Engineering.Base.dll。
        /// </summary>
        /// <param name="environment">环境。</param>
        /// <param name="logger">日志器。</param>
        /// <returns>已加载的程序集。</returns>
        private static Assembly LoadApiAssembly(TiaEnvironmentInfo environment, Logger logger)
        {
            List<string> candidates = new List<string>();

            if (environment.Assemblies != null)
            {
                candidates.AddRange(environment.Assemblies);
            }

            // 兜底：直接扫目录（Assemblies 为空或过期时也能工作）
            try
            {
                candidates.AddRange(Directory.GetFiles(environment.ApiDirectory, "Siemens.Engineering*.dll"));
            }
            catch (Exception)
            {
                // 目录不可读就算了，下面会报"找不到"
            }

            List<string> tried = new List<string>();
            foreach (string path in candidates)
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    continue;
                }

                // Hmi / AddIn 之类的模块不含 TiaPortal，跳过能少加载一堆东西
                string fileName = Path.GetFileNameWithoutExtension(path);
                if (fileName.IndexOf(".Hmi", StringComparison.OrdinalIgnoreCase) >= 0
                    || fileName.IndexOf(".AddIn", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    continue;
                }

                try
                {
                    Assembly assembly = Assembly.LoadFrom(path);
                    if (assembly.GetType(NamespacePrefix + "TiaPortal", false) != null)
                    {
                        return assembly;
                    }

                    tried.Add(Path.GetFileName(path) + "（不含 TiaPortal 类型）");
                }
                catch (Exception ex)
                {
                    tried.Add(Path.GetFileName(path) + "（加载失败：" + ex.GetType().Name + "）");
                }
            }

            throw new ToolException(
                ExitCodes.Api,
                "在 API 目录里找不到承载 Siemens.Engineering.TiaPortal 的程序集："
                + environment.ApiDirectory
                + (tried.Count == 0 ? string.Empty : "\r\n已尝试：" + string.Join("；", tried.ToArray()))
                + "\r\n请确认该目录下是完整的 Openness API（V15~V19 是 Siemens.Engineering.dll，"
                + "V20/V21 是 Siemens.Engineering.Base.dll），或改用 --envdump 查看诊断。");
        }

        /// <summary>
        /// 按短名取类型；取不到就报一条能直接指导排错的错。
        /// </summary>
        /// <param name="shortName">不含命名空间的类型名。</param>
        /// <returns>类型。</returns>
        private Type RequireType(string shortName)
        {
            Type type = _apiAssembly.GetType(NamespacePrefix + shortName, false);
            if (type == null)
            {
                throw new ToolException(
                    ExitCodes.Api,
                    "本机 Openness（" + AssemblyNameText + "）里没有类型 Siemens.Engineering."
                    + shortName + "，无法完成该操作。\r\n"
                    + "请用 probe 命令确认该版本的 API 面是否包含归档/检索功能。");
            }

            return type;
        }

        // ══════════════════════════════════════════════════════════════════
        //  下列方法替代原先的强类型调用；每个都只做"反射 + 明确报错"
        // ══════════════════════════════════════════════════════════════════

        /// <summary>创建一个 TIA Portal 实例。</summary>
        /// <param name="withUserInterface">true 带界面。</param>
        /// <returns>TiaPortal 实例（object，由调用方负责释放）。</returns>
        public object CreateTiaPortal(bool withUserInterface)
        {
            object mode = Enum.Parse(_tiaPortalModeType,
                withUserInterface ? "WithUserInterface" : "WithoutUserInterface", true);
            return Activator.CreateInstance(_tiaPortalType, new object[] { mode });
        }

        /// <summary>枚举本机正在运行的 TIA Portal 进程。</summary>
        /// <returns>进程对象列表（object）。</returns>
        public IEnumerable<object> GetProcesses()
        {
            MethodInfo method = _tiaPortalType.GetMethod("GetProcesses", BindingFlags.Public | BindingFlags.Static,
                null, Type.EmptyTypes, null);
            if (method == null)
            {
                throw new ToolException(ExitCodes.Api,
                    "本机 Openness 的 TiaPortal 类型没有 GetProcesses() 方法，无法附加到已运行的实例。");
            }

            object result = method.Invoke(null, null);
            IEnumerable enumerable = result as IEnumerable;
            if (enumerable == null)
            {
                return new List<object>();
            }

            List<object> processes = new List<object>();
            foreach (object item in enumerable)
            {
                processes.Add(item);
            }
            return processes;
        }

        /// <summary>把进程对象附加成 TiaPortal 实例。</summary>
        /// <param name="process">TiaPortalProcess 对象。</param>
        /// <returns>TiaPortal 实例。</returns>
        public object Attach(object process)
        {
            if (process == null)
            {
                throw new ToolException(ExitCodes.Environment, "附加失败：进程对象为空。");
            }

            MethodInfo method = process.GetType().GetMethod("Attach", BindingFlags.Public | BindingFlags.Instance,
                null, Type.EmptyTypes, null);
            if (method == null)
            {
                throw new ToolException(ExitCodes.Api,
                    "本机 Openness 的 TiaPortalProcess 类型没有 Attach() 方法。");
            }

            return method.Invoke(process, null);
        }

        /// <summary>把 TiaPortalProcess 的关键属性拼成一行可读文本（属性名各版本略有差异，取不到就写 ?）。</summary>
        /// <param name="process">进程对象。</param>
        /// <returns>形如 "Id=1234，Mode=WithUserInterface，AttachedSessions=0，ProjectPath=C:\..."。</returns>
        public string DescribeProcess(object process)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "Id={0}，Mode={1}，AttachedSessions={2}，ProjectPath={3}",
                GetPropertyText(process, "Id"),
                GetPropertyText(process, "Mode"),
                GetPropertyText(process, "AttachedSessions"),
                GetPropertyText(process, "ProjectPath"));
        }

        /// <summary>读取对象的某个属性并转成可读文本；属性不存在或读取失败返回 "?"。</summary>
        /// <param name="instance">对象。</param>
        /// <param name="propertyName">属性名。</param>
        /// <returns>文本。</returns>
        public static string GetPropertyText(object instance, string propertyName)
        {
            if (instance == null || string.IsNullOrEmpty(propertyName))
            {
                return "?";
            }

            try
            {
                PropertyInfo property = instance.GetType().GetProperty(propertyName,
                    BindingFlags.Public | BindingFlags.Instance);
                if (property == null)
                {
                    return "?";
                }

                object value = property.GetValue(instance, null);
                return value == null ? string.Empty : value.ToString();
            }
            catch (Exception)
            {
                return "?";
            }
        }

        /// <summary>取 portal.Projects（项目集合对象）。</summary>
        /// <param name="portal">TiaPortal 实例。</param>
        /// <returns>ProjectComposition 实例。</returns>
        public object GetProjects(object portal)
        {
            PropertyInfo property = portal.GetType().GetProperty("Projects", BindingFlags.Public | BindingFlags.Instance);
            if (property == null)
            {
                throw new ToolException(ExitCodes.Api, "本机 Openness 的 TiaPortal 类型没有 Projects 属性。");
            }

            object projects = property.GetValue(portal, null);
            if (projects == null)
            {
                throw new ToolException(ExitCodes.Api, "TiaPortal.Projects 返回了 null。");
            }

            return projects;
        }

        /// <summary>打开项目文件（可选升级）。返回 Project 对象。</summary>
        /// <param name="projects">ProjectComposition 实例。</param>
        /// <param name="file">项目文件。</param>
        /// <param name="upgrade">true 使用 OpenWithUpgrade。</param>
        /// <returns>Project 对象。</returns>
        public object OpenProject(object projects, FileInfo file, bool upgrade)
        {
            string methodName = upgrade ? "OpenWithUpgrade" : "Open";
            return InvokeByName(projects, methodName, new object[] { file }, "打开项目");
        }

        /// <summary>保存项目（Project.Save）。</summary>
        /// <param name="project">Project 对象。</param>
        public void SaveProject(object project)
        {
            InvokeByName(project, "Save", new object[0], "保存项目");
        }

        /// <summary>关闭项目（Project.Close）。</summary>
        /// <param name="project">Project 对象。</param>
        public void CloseProject(object project)
        {
            InvokeByName(project, "Close", new object[0], "关闭项目");
        }

        /// <summary>释放 TiaPortal 实例。</summary>
        /// <param name="portal">TiaPortal 实例。</param>
        public void DisposePortal(object portal)
        {
            IDisposable disposable = portal as IDisposable;
            if (disposable != null)
            {
                disposable.Dispose();
                return;
            }

            InvokeByName(portal, "Dispose", new object[0], "释放 TIA Portal");
        }

        /// <summary>把本工具的模式枚举翻译成目标版本的真实枚举值（按成员名匹配）。</summary>
        /// <param name="mode">本工具的模式。</param>
        /// <returns>目标版本的枚举值。</returns>
        public object ToArchivationMode(ArchiveMode mode)
        {
            string name = mode.ToString();
            try
            {
                return Enum.Parse(_archivationModeType, name, false);
            }
            catch (Exception)
            {
                throw new ToolException(ExitCodes.Api,
                    "本机 Openness（" + AssemblyNameText + "）的 ProjectArchivationMode 里没有成员 "
                    + name + "。请用 probe 命令查看该版本的枚举取值。");
            }
        }

        /// <summary>归档项目：project.Archive(targetDirectory, targetName, mode)。</summary>
        /// <param name="project">Project 对象。</param>
        /// <param name="targetDirectory">目标目录。</param>
        /// <param name="targetName">目标文件名。</param>
        /// <param name="mode">归档模式。</param>
        public void ArchiveProject(object project, DirectoryInfo targetDirectory, string targetName, ArchiveMode mode)
        {
            object enumValue = ToArchivationMode(mode);
            MethodInfo method = FindArchiveMethod(project.GetType());
            if (method == null)
            {
                throw new ToolException(ExitCodes.Api,
                    "在本机 Openness 的 " + project.GetType().FullName
                    + " 上找不到 Archive(DirectoryInfo, string, ProjectArchivationMode) 方法。"
                    + "请用 probe 命令确认该版本的归档 API。");
            }

            _logger.Debug("反射调用：" + DescribeMethod(method));
            Invoke(method, project, BuildArguments(method, targetDirectory, targetName, enumValue), "归档");
        }

        /// <summary>检索（解包）归档文件：Projects.Retrieve / RetrieveWithUpgrade。</summary>
        /// <param name="projects">ProjectComposition 实例。</param>
        /// <param name="sourceFile">归档文件。</param>
        /// <param name="targetDirectory">解包目录。</param>
        /// <param name="upgrade">true 使用 RetrieveWithUpgrade。</param>
        /// <returns>Project 对象。</returns>
        public object RetrieveProject(object projects, FileInfo sourceFile, DirectoryInfo targetDirectory, bool upgrade)
        {
            string methodName = upgrade ? "RetrieveWithUpgrade" : "Retrieve";
            return InvokeByName(projects, methodName, new object[] { sourceFile, targetDirectory }, "检索/解包");
        }

        /// <summary>
        /// 自检：本机这个版本的 Openness 是否具备归档/检索所需的**全部类型与成员**。
        ///
        /// 这是"这份 exe 能不能驱动这个版本"的最直接验证 —— 全部只读元数据、不连 TIA，
        /// 所以可以对着任意版本的 API 目录跑（probe 命令里就调了它）。
        /// </summary>
        /// <returns>问题清单；空表示全部具备。</returns>
        public IList<string> SelfCheck()
        {
            List<string> problems = new List<string>();

            // ── TiaPortal：构造函数 / GetProcesses / Projects
            if (_tiaPortalType.GetConstructor(new Type[] { _tiaPortalModeType }) == null)
            {
                problems.Add("TiaPortal 缺少 TiaPortal(TiaPortalMode) 构造函数");
            }

            if (_tiaPortalType.GetMethod("GetProcesses", BindingFlags.Public | BindingFlags.Static,
                    null, Type.EmptyTypes, null) == null)
            {
                problems.Add("TiaPortal 缺少静态 GetProcesses()（附加到已运行实例时要用）");
            }

            PropertyInfo projectsProperty = _tiaPortalType.GetProperty("Projects", BindingFlags.Public | BindingFlags.Instance);
            if (projectsProperty == null)
            {
                problems.Add("TiaPortal 缺少 Projects 属性");
                return problems;
            }

            // ── ProjectComposition：Open / OpenWithUpgrade / Retrieve / RetrieveWithUpgrade
            Type composition = projectsProperty.PropertyType;
            foreach (string methodName in new string[] { "Open", "OpenWithUpgrade", "Retrieve", "RetrieveWithUpgrade" })
            {
                if (!HasMethodNamed(composition, methodName))
                {
                    problems.Add(composition.Name + " 缺少方法 " + methodName);
                }
            }

            // ── Project：Archive / Save / Close
            if (FindArchiveMethod(_projectType) == null)
            {
                problems.Add(_projectType.Name + " 缺少 Archive(DirectoryInfo/string, string, 枚举) 重载");
            }

            foreach (string methodName in new string[] { "Save", "Close" })
            {
                if (!HasMethodNamed(_projectType, methodName))
                {
                    problems.Add(_projectType.Name + " 缺少方法 " + methodName);
                }
            }

            // ── ProjectArchivationMode 的四个成员（名字必须能对上，调用时是按名字翻译的）
            foreach (ArchiveMode mode in new ArchiveMode[]
            {
                ArchiveMode.None,
                ArchiveMode.Compressed,
                ArchiveMode.DiscardRestorableData,
                ArchiveMode.DiscardRestorableDataAndCompressed
            })
            {
                if (!Enum.IsDefined(_archivationModeType, mode.ToString()))
                {
                    problems.Add(_archivationModeType.Name + " 缺少成员 " + mode);
                }
            }

            return problems;
        }

        /// <summary>类型上是否有某个名字的公开实例方法（不校验签名）。</summary>
        private static bool HasMethodNamed(Type type, string methodName)
        {
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (string.Equals(method.Name, methodName, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        // ══════════════════════════════════════════════════════════════════
        //  硬件 / 网络：读设备树、网络接口、节点、子网（读网络设备与 IP 用）
        //
        //  调用链（V16~V21 完全一致，已用元数据 dump 逐版本核对）：
        //
        //  ★ 设备清单有**三个来源**，缺一个就会"少读设备"（踩过：只读 Project.Devices，
        //    结果一个含 20 多台分布式 IO 的项目只读到 1 台主 CPU）：
        //    ① Project.Devices              —— 只给**根级设备**（主控制 PLC、HMI、PC 站…）
        //    ② Project.UngroupedDevicesGroup—— "设备系统组"，就是 TIA 项目树里的「未分组的设备」，
        //                                      **全部分布式 IO 设备都在这里**；
        //                                      V16 声明在 Project、V17+ 声明在基类 ProjectBase（都有）
        //    ③ Project.DeviceGroups[*].Devices —— 用户自建的设备组（**V17 及以上才有**）
        //    三个来源可能互相重叠（系统组里也可能含根级设备）→ 调用方必须按设备名去重。
        //
        //    Device 以下（三个来源取到的设备对象完全同构）：
        //      Device → DeviceItem 树
        //      → DeviceItem.GetService<NetworkInterface>()   （泛型，反射用 MakeGenericMethod）
        //        → NetworkInterface.Nodes → Node
        //          → Node.Name / NodeId / NodeType / ConnectedSubnet
        //          → Node.GetAttribute("Address") / ("SubnetMask")   ★ IP 与掩码在这里
        //    Project.Subnets → Subnet（Name / NetType / Nodes / TypeIdentifier）
        //
        //  ★ 关键事实：**任何版本都没有 IPAddress 这个类**。
        //    IP 与掩码是 Node 上的**动态属性**，只能按名字取（官方仓库
        //    tia-portal-applications/discussions/2 给出的写法就是 SetAttribute("Address", ...)）。
        //    动态属性名对个别设备族可能不同，所以查找一律走
        //    TryGetAttribute + GetAttributeNames 兜底，并把真实属性名记进诊断。
        // ══════════════════════════════════════════════════════════════════

        /// <summary>本机 Openness 里 HW.Features.NetworkInterface 的类型；缺失时为 null。</summary>
        public Type NetworkInterfaceType
        {
            get { return _networkInterfaceType; }
        }

        /// <summary>HW.Node 的类型；缺失时为 null。</summary>
        public Type NodeType
        {
            get { return _nodeType; }
        }

        /// <summary>按短名取类型；取不到返回 null（用于"可选类型"，不抛异常）。</summary>
        /// <param name="shortName">不含命名空间的类型名，如 HW.Features.NetworkInterface。</param>
        /// <returns>类型或 null。</returns>
        public Type FindType(string shortName)
        {
            return _apiAssembly.GetType(NamespacePrefix + shortName, false);
        }

        /// <summary>
        /// 读 Project.Devices（DeviceComposition）。
        ///
        /// ★ 注意：它**只返回根级设备**（主控制 PLC、HMI、PC 站这类直接挂在项目下的设备），
        ///   分布式 IO 设备在「未分组的设备」里，见 <see cref="GetProjectUngroupedDevicesGroup"/>；
        ///   调用方**必须**把三个来源合起来看，否则会"只读到主 PLC"。
        /// </summary>
        /// <param name="project">Project 对象。</param>
        /// <returns>设备集合对象。</returns>
        public object GetProjectDevices(object project)
        {
            object devices = GetPropertyValue(project, "Devices");
            if (devices == null)
            {
                throw new ToolException(ExitCodes.Api,
                    "本机 Openness 的 Project 上取不到 Devices 属性，无法读取设备清单。");
            }
            return devices;
        }

        /// <summary>读 Project.Subnets（SubnetComposition）。取不到返回 null（子网信息是可选的）。</summary>
        /// <param name="project">Project 对象。</param>
        /// <returns>子网集合对象或 null。</returns>
        public object GetProjectSubnets(object project)
        {
            return GetPropertyValue(project, "Subnets");
        }

        /// <summary>
        /// 读 Project.UngroupedDevicesGroup —— "设备系统组"，即 TIA 项目树里的**「未分组的设备」**。
        ///
        /// 为什么要专门读它：西门子官方答复明确 ——「项目中的所有分布式 I/O 设备都位于
        /// 『未分组设备』文件夹中」，而这些设备**不会**出现在 <c>Project.Devices</c> 里。
        /// 实测（V21 + 一个含 20 多台分布式 IO 的项目）：只读 Devices 会只拿到 1 台主 CPU。
        ///
        /// 版本差异：V16 声明在 <c>Project</c> 上，V17~V21 在基类 <c>ProjectBase</c> 上（都有）。
        /// 取不到时返回 null（不抛异常），由调用方记诊断并继续。
        /// </summary>
        /// <param name="project">Project 对象。</param>
        /// <returns>设备组对象（DeviceGroup）或 null。</returns>
        public object GetProjectUngroupedDevicesGroup(object project)
        {
            return GetPropertyValue(project, "UngroupedDevicesGroup");
        }

        /// <summary>
        /// 读 Project.DeviceGroups（用户自建的设备组集合）。**V17 及以上才有**，取不到返回 null。
        /// </summary>
        /// <param name="project">Project 对象。</param>
        /// <returns>设备组集合对象或 null。</returns>
        public object GetProjectDeviceGroups(object project)
        {
            return GetPropertyValue(project, "DeviceGroups");
        }

        /// <summary>
        /// 把设备组里的设备拍平（<c>deviceGroup.Devices</c>）。
        /// 组为空、属性缺失或对象为 null 时一律返回空列表，绝不抛异常。
        /// </summary>
        /// <param name="deviceGroup">设备组对象（系统组或用户组）。</param>
        /// <returns>该组直属的设备列表。</returns>
        public IList<object> GetDeviceGroupDevices(object deviceGroup)
        {
            return Flatten(GetPropertyValue(deviceGroup, "Devices"));
        }

        /// <summary>
        /// 设备组下的子组 —— **防御式**实现：V16~V21 的官方元数据里
        /// <c>HW.DeviceGroup</c> 只有 Name / Devices / Parent 三个公开属性（没有子组属性），
        /// 所以这里按"有则用、没有就当空"处理（将来版本若加了 <c>Groups</c> / <c>DeviceGroups</c>
        /// 也能自动用上），绝不因为版本差异抛异常。
        /// </summary>
        /// <param name="deviceGroup">设备组对象。</param>
        /// <returns>子组列表（没有则空列表）。</returns>
        public IList<object> GetDeviceGroupSubGroups(object deviceGroup)
        {
            object groups = GetPropertyValue(deviceGroup, "Groups");
            if (groups == null)
            {
                groups = GetPropertyValue(deviceGroup, "DeviceGroups");
            }

            return Flatten(groups);
        }

        /// <summary>
        /// 把 Openness 的集合/联合（都实现 IEnumerable）拍平成 object 列表。
        /// 索引器式访问（Item）在各版本签名不一，统一用枚举最稳。
        /// </summary>
        /// <param name="enumerable">集合对象。</param>
        /// <returns>元素列表；入参为 null 时返回空列表。</returns>
        public IList<object> Flatten(object enumerable)
        {
            List<object> items = new List<object>();
            IEnumerable sequence = enumerable as IEnumerable;
            if (sequence == null)
            {
                return items;
            }

            foreach (object item in sequence)
            {
                if (item != null)
                {
                    items.Add(item);
                }
            }

            return items;
        }

        /// <summary>取某个公开实例属性；属性不存在或读取失败返回 null。</summary>
        /// <param name="instance">对象。</param>
        /// <param name="propertyName">属性名。</param>
        /// <returns>值或 null。</returns>
        public static object GetPropertyValue(object instance, string propertyName)
        {
            if (instance == null || string.IsNullOrEmpty(propertyName))
            {
                return null;
            }

            try
            {
                PropertyInfo property = instance.GetType().GetProperty(propertyName,
                    BindingFlags.Public | BindingFlags.Instance);
                return property == null ? null : property.GetValue(instance, null);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>取属性并转成文本；取不到返回空串（导出 CSV 时空串比 "?" 干净）。</summary>
        /// <param name="instance">对象。</param>
        /// <param name="propertyName">属性名。</param>
        /// <returns>文本。</returns>
        public static string GetPropertyTextOrEmpty(object instance, string propertyName)
        {
            object value = GetPropertyValue(instance, propertyName);
            return value == null ? string.Empty : value.ToString();
        }

        /// <summary>
        /// 反射调用泛型服务获取：obj.GetService&lt;T&gt;()。
        ///
        /// 为什么必须自己啃泛型：共享内核的设计原则是**编译期不引用任何 Siemens 类型**，
        /// 而 GetService 只有泛型版本（没有 GetService(Type) 重载），
        /// 所以只能 找到泛型方法定义 → MakeGenericMethod(目标服务类型) → Invoke。
        /// 类比 SCL：相当于运行时按名字查到一个"参数化 FB"，再把类型参数填进去实例化。
        /// </summary>
        /// <param name="target">对象（DeviceItem / Node / Subnet 等）。</param>
        /// <param name="serviceType">服务类型（如 HW.Features.NetworkInterface）。</param>
        /// <param name="error">失败原因（成功时为 null）。</param>
        /// <returns>服务对象；该对象不提供此服务时返回 null。</returns>
        public object TryGetService(object target, Type serviceType, out string error)
        {
            error = null;
            if (target == null)
            {
                error = "对象为空";
                return null;
            }

            if (serviceType == null)
            {
                error = "服务类型为空（本机 Openness 里没有这个类型）";
                return null;
            }

            MethodInfo generic = null;
            foreach (MethodInfo method in target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!string.Equals(method.Name, "GetService", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!method.IsGenericMethodDefinition || method.GetGenericArguments().Length != 1)
                {
                    continue;
                }

                if (method.GetParameters().Length != 0)
                {
                    continue;
                }

                generic = method;
                break;
            }

            if (generic == null)
            {
                error = target.GetType().Name + " 上没有 GetService<T>()";
                return null;
            }

            try
            {
                MethodInfo closed = generic.MakeGenericMethod(serviceType);
                object result = closed.Invoke(target, null);
                if (result != null && !serviceType.IsInstanceOfType(result))
                {
                    error = "GetService 返回了 " + result.GetType().FullName + "，与期望的 "
                        + serviceType.FullName + " 不符";
                    return null;
                }

                return result;
            }
            catch (TargetInvocationException ex)
            {
                Exception inner = ex.InnerException ?? ex;
                // 该对象不提供这个服务时 Openness 会抛异常 —— 对遍历来说"没有"是正常情况，
                // 所以这里不抛，只把原因记下来给诊断用。
                error = inner.GetType().Name + "：" + inner.Message;
                return null;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + "：" + ex.Message;
                return null;
            }
        }

        /// <summary>读动态工程属性：obj.GetAttribute(name)。</summary>
        /// <param name="target">对象。</param>
        /// <param name="attributeName">属性名（如 Address / SubnetMask）。</param>
        /// <param name="value">属性值。</param>
        /// <param name="error">失败原因（成功时为 null）。</param>
        /// <returns>成功返回 true。</returns>
        public bool TryGetAttribute(object target, string attributeName, out object value, out string error)
        {
            value = null;
            error = null;
            if (target == null)
            {
                error = "对象为空";
                return false;
            }

            try
            {
                MethodInfo method = FindMethod(target.GetType(), "GetAttribute", typeof(string));
                if (method == null)
                {
                    error = target.GetType().Name + " 上没有 GetAttribute(string)";
                    return false;
                }

                value = method.Invoke(target, new object[] { attributeName });
                return true;
            }
            catch (TargetInvocationException ex)
            {
                Exception inner = ex.InnerException ?? ex;
                error = inner.GetType().Name + "：" + inner.Message;
                return false;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + "：" + ex.Message;
                return false;
            }
        }

        /// <summary>读动态工程属性并转文本；不存在或读取失败返回空串。</summary>
        /// <param name="target">对象。</param>
        /// <param name="attributeName">属性名。</param>
        /// <returns>文本。</returns>
        public string GetAttributeText(object target, string attributeName)
        {
            object value;
            string error;
            if (!TryGetAttribute(target, attributeName, out value, out error))
            {
                return string.Empty;
            }

            return value == null ? string.Empty : value.ToString();
        }

        /// <summary>
        /// 枚举对象上**真实存在**的动态属性名（GetAttributeInfos()）。
        /// 这是对付"属性名各设备族不一致"的兜底手段：查不到候选名时，把真实名单打出来。
        /// </summary>
        /// <param name="target">对象。</param>
        /// <returns>属性名列表（失败时为空列表）。</returns>
        public IList<string> GetAttributeNames(object target)
        {
            List<string> names = new List<string>();
            if (target == null)
            {
                return names;
            }

            MethodInfo method = FindMethod(target.GetType(), "GetAttributeInfos");
            if (method == null)
            {
                return names;
            }

            object result;
            try
            {
                result = method.Invoke(target, null);
            }
            catch (Exception)
            {
                return names;
            }

            foreach (object info in Flatten(result))
            {
                string name = GetPropertyTextOrEmpty(info, "Name");
                if (!string.IsNullOrEmpty(name))
                {
                    names.Add(name);
                }
            }

            return names;
        }

        // ══════════════════════════════════════════════════════════════════
        //  CAx 导出（AutomationML / .aml）
        //
        //  这就是 TIA 菜单里「项目 → 导出 CAx 数据…」背后的**同一个 API** ——
        //  有了它，"先在 TIA 里手点导出、再用离线工具读 .aml" 就能合成一步：
        //      Openness 打开/附加项目 → 导出 .aml → 离线解析器接着读。
        //
        //  ★ 关键事实：**Project 上并没有 ExportAsDocuments 之类的方法**
        //    （对着 V16~V21 的官方 API 文档逐版本核对过）。CAx 导出是"服务式"入口：
        //
        //        CaxProvider provider = project.GetService<CaxProvider>();
        //        provider.Export(project, 导出文件FileInfo[, 日志文件FileInfo]);
        //
        //    各版本真实签名（V16~V21，均为 CaxProvider 上的实例方法）：
        //        Export(Device,      FileInfo, FileInfo)   → bool            （V16 起）
        //        Export(Project,     FileInfo, FileInfo)   → bool            （V16 起）
        //        Export(ProjectBase, FileInfo, FileInfo)   → bool            （V18 起）
        //        Export(Device,      FileInfo)             → TransferResult  （V19 起）
        //        Export(ProjectBase, FileInfo)             → TransferResult  （V19 起）
        //
        //    所以调用策略是"先挑信息量大的、再退回兼容面最广的"：
        //      ① 两参数（对象, 导出文件）→ TransferResult，能读到 State 与逐条消息；
        //      ② 三参数（对象, 导出文件, 日志文件）→ bool，失败原因写在日志文件里。
        //
        //  ★ 另一处版本差异：**V21 把 Cax 拆进了模块程序集**
        //        <Portal>\PublicAPI\V21\net48\Siemens.Engineering.Step7.dll
        //    主程序集（Siemens.Engineering.Base.dll）里没有 CaxProvider，
        //    所以按类型名查找必须**跨模块**（见 ResolveTypeAcrossModules）。
        // ══════════════════════════════════════════════════════════════════

        /// <summary>CaxProvider 的类型名（不含命名空间前缀）。</summary>
        private const string CaxProviderShortName = "Cax.CaxProvider";

        private Type _caxProviderType;
        private bool _caxProviderTypeSearched;

        /// <summary>
        /// CaxProvider 类型（延迟解析、跨模块查找）；本机版本没有则返回 null。
        ///
        /// 为什么延迟到用时才找：V16~V20 它在主程序集里，V21 才跑到 Step7 模块里，
        /// 而"加载所有模块程序集"只该在真要导出 CAx 时才付代价（启动时不白扫一遍）。
        /// </summary>
        public Type CaxProviderType
        {
            get
            {
                if (!_caxProviderTypeSearched)
                {
                    _caxProviderTypeSearched = true;
                    _caxProviderType = ResolveTypeAcrossModules(CaxProviderShortName);
                }

                return _caxProviderType;
            }
        }

        /// <summary>
        /// 把工程对象导出成 CAx 数据（.aml）。
        ///
        /// 调用链：target.GetService&lt;CaxProvider&gt;() → provider.Export(target, 导出文件[, 日志文件])。
        /// </summary>
        /// <param name="target">Project（整个项目）或 Device（单个设备）。</param>
        /// <param name="exportFile">导出文件（绝对路径；扩展名用 .aml）。父目录必须已存在。</param>
        /// <param name="logFile">日志文件（三参数重载用，可为 null）。</param>
        /// <param name="reportedError">
        /// TIA 是否报告了 Error 级问题。
        ///
        /// ★ 它**不等于"导出失败"**：实测（V21 + 一个含 HMI 的 Demo 项目）TIA 会给出
        ///   "device HMI_1 的执行将跳过。所需属性 TypeIdentifier 无效或缺失" 这类
        ///   **单台设备**的问题，并把整体 State 标成 Error，但 .aml 文件其实照常生成、
        ///   其余设备数据完整。所以调用方应当以"文件是否真的生成"为准，
        ///   把这个标志当作**告警**转达给用户，而不是一票否决。
        /// </param>
        /// <returns>结果说明（TransferResult 的 State + 消息，或 bool 的成败），可直接记进日志。</returns>
        /// <exception cref="ToolException">本机版本不支持、服务取不到时抛出。</exception>
        public string ExportCax(object target, FileInfo exportFile, FileInfo logFile, out bool reportedError)
        {
            reportedError = false;
            if (target == null)
            {
                throw new ToolException(ExitCodes.Usage, "导出 CAx 数据失败：没有可导出的工程对象。");
            }

            if (exportFile == null)
            {
                throw new ToolException(ExitCodes.Usage, "导出 CAx 数据失败：没有指定导出文件路径。");
            }

            Type providerType = CaxProviderType;
            if (providerType == null)
            {
                throw new ToolException(ExitCodes.Api,
                    "本机 Openness（" + AssemblyNameText + "）里找不到 Siemens.Engineering.Cax.CaxProvider，"
                    + "无法直接导出 CAx 数据。\r\n"
                    + "请确认安装 TIA 时勾选了 \"TIA Portal Openness\" 组件（V16~V21 都随组件提供 CAx 导出）；"
                    + "实在没有也可以退回离线模式：先在 TIA 里手动导出 .aml，再用本工具读。");
            }

            string serviceError;
            object provider = TryGetService(target, providerType, out serviceError);
            if (provider == null)
            {
                throw new ToolException(ExitCodes.Api,
                    "取不到 CAx 导出服务（" + target.GetType().Name + ".GetService<CaxProvider>()）："
                    + (string.IsNullOrEmpty(serviceError) ? "未知原因" : serviceError));
            }

            MethodInfo method = FindCaxExportMethod(providerType, target.GetType());
            if (method == null)
            {
                throw new ToolException(ExitCodes.Api,
                    "本机 Openness 的 " + providerType.Name
                    + " 上没有可用的 Export(对象, 文件[, 日志文件]) 重载，无法导出 CAx 数据。");
            }

            _logger.Debug("反射调用：" + DescribeMethod(method));

            ParameterInfo[] parameters = method.GetParameters();
            object[] arguments = new object[parameters.Length];
            arguments[0] = target;
            for (int i = 1; i < arguments.Length; i++)
            {
                arguments[i] = (i == 1) ? (object)exportFile : (logFile ?? exportFile);
            }

            object result = Invoke(method, provider, arguments, "导出 CAx 数据");

            // ── 三参数重载（V16 起都有）：返回 bool，失败原因看日志文件
            if (result is bool)
            {
                if (!(bool)result)
                {
                    reportedError = true;
                    return "Export 返回 false（TIA 认为导出失败）" + ReadTextTail(logFile);
                }

                return "Export 返回 true（导出成功）";
            }

            // ── 两参数重载（V19 起）：返回 TransferResult，能读到状态与逐条消息
            if (result == null)
            {
                return "导出完成（该版本没有返回结果对象）。";
            }

            string state = GetPropertyTextOrEmpty(result, "State");
            List<string> messages = new List<string>();
            CollectTransferMessages(result, messages, 0);

            string detail = "状态 " + (string.IsNullOrEmpty(state) ? "?" : state);
            if (messages.Count > 0)
            {
                detail = detail + "：" + string.Join("；", messages.ToArray());
            }

            // ★ 这里刻意**不抛异常**：State=Error 常常只是"个别设备被跳过"，
            //   而 .aml 往往照常生成（实测见 reportedError 的说明）。
            //   由调用方按"文件是否真的生成"下结论。
            if (string.Equals(state, "Error", StringComparison.OrdinalIgnoreCase))
            {
                reportedError = true;
            }

            return detail;
        }

        /// <summary>
        /// 从 CaxProvider 上挑 Export 重载：第一个形参能接受 <paramref name="targetType"/>，其余形参都是 FileInfo。
        /// 优先两参数（返回 TransferResult，信息最全），没有才退回三参数（返回 bool）。
        /// </summary>
        /// <param name="providerType">CaxProvider 类型。</param>
        /// <param name="targetType">要导出的对象类型（Project / Device …）。</param>
        /// <returns>方法；没有返回 null。</returns>
        private static MethodInfo FindCaxExportMethod(Type providerType, Type targetType)
        {
            MethodInfo fallback = null;
            foreach (MethodInfo method in providerType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!string.Equals(method.Name, "Export", StringComparison.Ordinal))
                {
                    continue;
                }

                if (method.IsGenericMethodDefinition)
                {
                    continue;
                }

                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length < 2 || parameters.Length > 3)
                {
                    continue;
                }

                if (!parameters[0].ParameterType.IsAssignableFrom(targetType))
                {
                    continue;
                }

                bool restAreFiles = true;
                for (int i = 1; i < parameters.Length; i++)
                {
                    if (parameters[i].ParameterType != typeof(FileInfo))
                    {
                        restAreFiles = false;
                        break;
                    }
                }

                if (!restAreFiles)
                {
                    continue;
                }

                if (parameters.Length == 2)
                {
                    return method;
                }

                if (fallback == null)
                {
                    fallback = method;
                }
            }

            return fallback;
        }

        /// <summary>
        /// 递归收集 TransferResult / TransferResultMessage 上的消息（形如 "[Error] 描述"）。
        /// 只递归三层、最多 20 条：消息可能嵌套且很长，日志不能被它刷屏。
        /// </summary>
        /// <param name="result">TransferResult（或某条 TransferResultMessage）。</param>
        /// <param name="messages">收集器。</param>
        /// <param name="depth">当前深度。</param>
        private void CollectTransferMessages(object result, List<string> messages, int depth)
        {
            if (result == null || messages.Count >= 20 || depth > 3)
            {
                return;
            }

            foreach (object item in Flatten(GetPropertyValue(result, "Messages")))
            {
                string text = GetPropertyTextOrEmpty(item, "Message");
                if (!string.IsNullOrEmpty(text))
                {
                    string state = GetPropertyTextOrEmpty(item, "State");
                    messages.Add((string.IsNullOrEmpty(state) ? string.Empty : "[" + state + "] ") + text);
                    if (messages.Count >= 20)
                    {
                        return;
                    }
                }

                CollectTransferMessages(item, messages, depth + 1);
            }
        }

        /// <summary>读文本文件尾部（日志文件可能较大，只取最后 2000 字符）；读不到返回空串。</summary>
        /// <param name="file">日志文件（可为 null）。</param>
        /// <returns>可直接拼进错误信息的一小段文本。</returns>
        private static string ReadTextTail(FileInfo file)
        {
            if (file == null || !file.Exists)
            {
                return string.Empty;
            }

            try
            {
                string text = File.ReadAllText(file.FullName);
                if (string.IsNullOrEmpty(text))
                {
                    return string.Empty;
                }

                const int keep = 2000;
                string tail = text.Length <= keep ? text : text.Substring(text.Length - keep);
                return "\r\n导出日志尾部（" + file.FullName + "）：\r\n" + tail.Trim();
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// 按短名找类型：先看主程序集，再扫 API 目录里的**模块程序集**。
        ///
        /// 为什么必须扫模块：V21 把 API 拆成了多个 DLL
        /// （Siemens.Engineering.Base.dll + Step7 / WinCC 等模块），
        /// CaxProvider 恰好在 Step7 模块里 —— 只查主程序集会出现
        /// "本机明明支持 CAx 导出，却报找不到类型"的假故障。
        /// </summary>
        /// <param name="shortName">不含命名空间的类型名，如 Cax.CaxProvider。</param>
        /// <returns>类型；找不到返回 null。</returns>
        private Type ResolveTypeAcrossModules(string shortName)
        {
            string fullName = NamespacePrefix + shortName;
            Type type = _apiAssembly.GetType(fullName, false);
            if (type != null)
            {
                return type;
            }

            if (_environment == null)
            {
                return null;
            }

            foreach (string path in EnumerateApiAssemblyFiles(_environment))
            {
                string fileName = Path.GetFileNameWithoutExtension(path);
                if (fileName.IndexOf(".Hmi", StringComparison.OrdinalIgnoreCase) >= 0
                    || fileName.IndexOf(".AddIn", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    continue;
                }

                try
                {
                    Assembly module = Assembly.LoadFrom(path);
                    type = module.GetType(fullName, false);
                    if (type != null)
                    {
                        _logger.Debug("类型 " + fullName + " 来自模块程序集：" + Path.GetFileName(path));
                        return type;
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug("加载模块程序集失败：" + Path.GetFileName(path)
                        + "：" + ex.GetType().Name + "：" + ex.Message);
                }
            }

            return null;
        }

        /// <summary>API 目录里所有 Siemens.Engineering*.dll（环境探测给的清单 + 现场兜底扫描，已去重）。</summary>
        /// <param name="environment">环境信息。</param>
        /// <returns>程序集文件路径列表。</returns>
        private static List<string> EnumerateApiAssemblyFiles(TiaEnvironmentInfo environment)
        {
            List<string> files = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (environment.Assemblies != null)
            {
                foreach (string path in environment.Assemblies)
                {
                    if (!string.IsNullOrEmpty(path) && seen.Add(path))
                    {
                        files.Add(path);
                    }
                }
            }

            try
            {
                foreach (string path in Directory.GetFiles(environment.ApiDirectory, "Siemens.Engineering*.dll"))
                {
                    if (seen.Add(path))
                    {
                        files.Add(path);
                    }
                }
            }
            catch (Exception)
            {
                // 目录不可读就用清单里的那些
            }

            return files;
        }

        /// <summary>
        /// 自检：本机这个版本的 Openness 是否具备"直接导出 CAx 数据（.aml）"的能力。
        /// 纯元数据检查：不连 TIA、不打开项目，只确认 CaxProvider 类型、Export 重载，
        /// 以及 Project 上的 GetService&lt;T&gt;() 都在（界面的"环境体检"用它）。
        /// </summary>
        /// <returns>问题清单；空表示具备。</returns>
        public IList<string> SelfCheckCaxExport()
        {
            List<string> problems = new List<string>();

            Type providerType = CaxProviderType;
            if (providerType == null)
            {
                problems.Add("找不到类型 Siemens.Engineering.Cax.CaxProvider"
                    + "（V16~V20 在主程序集里，V21 在 Siemens.Engineering.Step7.dll 模块里）");
                return problems;
            }

            if (FindCaxExportMethod(providerType, _projectType) == null)
            {
                problems.Add(providerType.Name
                    + " 上没有可用的 Export(Project/ProjectBase, FileInfo[, FileInfo]) 重载");
            }

            if (!HasGenericMethodNamed(_projectType, "GetService"))
            {
                problems.Add(_projectType.Name + " 缺少 GetService<T>()（取 CaxProvider 服务的入口）");
            }

            return problems;
        }

        /// <summary>
        /// 自检：本机这个版本的 Openness 是否具备"读网络设备与 IP"所需的类型与成员。
        /// 纯元数据检查，不连 TIA、不打开项目，所以可以对着任意版本跑（probe 用它）。
        ///
        /// 检查项含**设备清单三个来源**中的两个必需项：<c>Devices</c> 与
        /// <c>UngroupedDevicesGroup</c>（缺后者会"只读到主 PLC"）。
        /// <c>DeviceGroups</c> **不作为问题报出** —— V16 官方就没有这个属性，
        /// 按"有则读、无则跳过"处理（见 TiaDeviceInventory 的来源合并）。
        /// </summary>
        /// <returns>问题清单；空表示具备。</returns>
        public IList<string> SelfCheckDeviceRead()
        {
            List<string> problems = new List<string>();

            string[] requiredTypes = new string[]
            {
                "HW.Device", "HW.DeviceItem", "HW.Features.NetworkInterface", "HW.Node", "HW.Subnet"
            };
            foreach (string shortName in requiredTypes)
            {
                if (FindType(shortName) == null)
                {
                    problems.Add("缺少类型 Siemens.Engineering." + shortName);
                }
            }

            if (!HasPropertyNamed(_projectType, "Devices"))
            {
                problems.Add(_projectType.Name + " 缺少 Devices 属性（取设备清单的入口）");
            }

            // 「未分组的设备」＝全部分布式 IO 设备（西门子官方：所有分布式 I/O 设备都位于该文件夹中）。
            // 缺了它就会"只读到主控制 PLC"。V16 声明在 Project 上、V17+ 在基类 ProjectBase 上，
            // 这里用的 GetProperty 对继承的公开属性同样有效，两种版本都能命中。
            if (!HasPropertyNamed(_projectType, "UngroupedDevicesGroup"))
            {
                problems.Add(_projectType.Name + " 缺少 UngroupedDevicesGroup 属性"
                    + "（项目树里「未分组的设备」＝分布式 IO 设备会读不到，只会读到根级设备）");
            }

            if (!HasPropertyNamed(_projectType, "Subnets"))
            {
                problems.Add(_projectType.Name + " 缺少 Subnets 属性（子网信息会缺失）");
            }

            if (_deviceItemType != null)
            {
                if (!HasGenericMethodNamed(_deviceItemType, "GetService"))
                {
                    problems.Add(_deviceItemType.Name + " 缺少 GetService<T>()（取网络接口服务的入口）");
                }

                if (!HasPropertyNamed(_deviceItemType, "DeviceItems") && !HasPropertyNamed(_deviceItemType, "Items"))
                {
                    problems.Add(_deviceItemType.Name + " 缺少 DeviceItems / Items 子项集合（无法递归设备树）");
                }

                if (!HasPropertyNamed(_deviceItemType, "Name"))
                {
                    problems.Add(_deviceItemType.Name + " 缺少 Name 属性");
                }
            }

            if (_networkInterfaceType != null && !HasPropertyNamed(_networkInterfaceType, "Nodes"))
            {
                problems.Add(_networkInterfaceType.Name + " 缺少 Nodes 属性（拿不到节点就没有 IP）");
            }

            if (_nodeType != null)
            {
                if (FindMethod(_nodeType, "GetAttribute", typeof(string)) == null)
                {
                    problems.Add(_nodeType.Name + " 缺少 GetAttribute(string)（IP 属性就靠它读）");
                }

                if (FindMethod(_nodeType, "GetAttributeInfos") == null)
                {
                    problems.Add(_nodeType.Name + " 缺少 GetAttributeInfos()（属性名兜底会失效）");
                }
            }

            if (_subnetType != null && !HasPropertyNamed(_subnetType, "Name"))
            {
                problems.Add(_subnetType.Name + " 缺少 Name 属性");
            }

            return problems;
        }

        /// <summary>类型上是否有该名字的公开实例属性。</summary>
        private static bool HasPropertyNamed(Type type, string propertyName)
        {
            if (type == null)
            {
                return false;
            }

            return type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance) != null;
        }

        /// <summary>类型上是否有该名字的公开泛型方法（只看名字与泛型性）。</summary>
        private static bool HasGenericMethodNamed(Type type, string methodName)
        {
            if (type == null)
            {
                return false;
            }

            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (string.Equals(method.Name, methodName, StringComparison.Ordinal) && method.IsGenericMethodDefinition)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>按"名字 + 精确形参类型"找方法（找不到返回 null）。</summary>
        private static MethodInfo FindMethod(Type type, string methodName, params Type[] parameterTypes)
        {
            if (type == null)
            {
                return null;
            }

            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!string.Equals(method.Name, methodName, StringComparison.Ordinal))
                {
                    continue;
                }

                if (method.IsGenericMethodDefinition)
                {
                    continue;
                }

                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length != parameterTypes.Length)
                {
                    continue;
                }

                bool matched = true;
                for (int i = 0; i < parameters.Length; i++)
                {
                    if (parameters[i].ParameterType != parameterTypes[i])
                    {
                        matched = false;
                        break;
                    }
                }

                if (matched)
                {
                    return method;
                }
            }

            return null;
        }

        // ══════════════════════════════════════════════════════════════════
        //  反射基础设施
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// 按名字找方法并调用，自动在候选重载里挑"参数能匹配上"的那个。
        /// </summary>
        private object InvokeByName(object instance, string methodName, object[] desired, string context)
        {
            MethodInfo best = null;
            foreach (MethodInfo method in instance.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!string.Equals(method.Name, methodName, StringComparison.Ordinal))
                {
                    continue;
                }

                if (TryBuildArguments(method, desired) == null)
                {
                    continue;
                }

                best = method;
                break;
            }

            if (best == null)
            {
                throw new ToolException(ExitCodes.Api,
                    "在本机 Openness 的 " + instance.GetType().FullName + " 上找不到可用的 " + methodName
                    + " 重载（" + context + "）。请用 probe 命令查看该版本的签名。");
            }

            _logger.Debug("反射调用：" + DescribeMethod(best));
            return Invoke(best, instance, TryBuildArguments(best, desired), context);
        }

        /// <summary>
        /// 找 3 参数的 Archive 方法（DirectoryInfo / string / 枚举），兼容老版本用 string 传目录的情形。
        /// </summary>
        private MethodInfo FindArchiveMethod(Type projectType)
        {
            foreach (MethodInfo method in projectType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!string.Equals(method.Name, "Archive", StringComparison.Ordinal))
                {
                    continue;
                }

                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length != 3)
                {
                    continue;
                }

                bool firstIsPath = parameters[0].ParameterType == typeof(DirectoryInfo)
                    || parameters[0].ParameterType == typeof(string);
                bool secondIsName = parameters[1].ParameterType == typeof(string);
                bool thirdIsEnum = parameters[2].ParameterType.IsEnum;

                if (firstIsPath && secondIsName && thirdIsEnum)
                {
                    return method;
                }
            }

            return null;
        }

        /// <summary>按目标方法的形参构造实参；无法匹配返回 null。</summary>
        private static object[] BuildArguments(MethodInfo method, DirectoryInfo targetDirectory, string targetName, object enumValue)
        {
            ParameterInfo[] parameters = method.GetParameters();
            object[] arguments = new object[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                Type parameterType = parameters[i].ParameterType;
                if (parameterType == typeof(DirectoryInfo))
                {
                    arguments[i] = targetDirectory;
                }
                else if (parameterType == typeof(string))
                {
                    arguments[i] = (i == 0) ? targetDirectory.FullName : targetName;
                }
                else
                {
                    arguments[i] = enumValue;
                }
            }
            return arguments;
        }

        /// <summary>尝试把"期望实参"适配到某方法的形参；不兼容返回 null。</summary>
        private static object[] TryBuildArguments(MethodInfo method, object[] desired)
        {
            ParameterInfo[] parameters = method.GetParameters();
            if (parameters.Length != desired.Length)
            {
                return null;
            }

            object[] arguments = new object[desired.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                Type parameterType = parameters[i].ParameterType;
                object value = desired[i];

                if (value == null)
                {
                    if (parameterType.IsValueType && Nullable.GetUnderlyingType(parameterType) == null)
                    {
                        return null;
                    }
                    arguments[i] = null;
                    continue;
                }

                if (parameterType.IsInstanceOfType(value))
                {
                    arguments[i] = value;
                    continue;
                }

                // FileInfo / DirectoryInfo / string 之间的温和适配（个别版本形参类型不同）
                if (parameterType == typeof(string) && value is FileSystemInfo)
                {
                    arguments[i] = ((FileSystemInfo)value).FullName;
                    continue;
                }

                if (parameterType == typeof(FileInfo) && value is string)
                {
                    arguments[i] = new FileInfo((string)value);
                    continue;
                }

                if (parameterType == typeof(DirectoryInfo) && value is string)
                {
                    arguments[i] = new DirectoryInfo((string)value);
                    continue;
                }

                return null;
            }

            return arguments;
        }

        /// <summary>调用方法并把 TargetInvocationException 的内层异常翻出来交给统一的排错翻译。</summary>
        private static object Invoke(MethodInfo method, object instance, object[] arguments, string context)
        {
            try
            {
                return method.Invoke(instance, arguments);
            }
            catch (TargetInvocationException ex)
            {
                throw TiaSession.WrapSiemensFailure(
                    ex.InnerException ?? ex, "调用 " + method.DeclaringType.Name + "." + method.Name + " 失败（" + context + "）。");
            }
        }

        /// <summary>形如 "void Archive(DirectoryInfo targetDirectory, string targetName, ProjectArchivationMode archivationMode)"。</summary>
        private static string DescribeMethod(MethodInfo method)
        {
            ParameterInfo[] parameters = method.GetParameters();
            string[] texts = new string[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                texts[i] = parameters[i].ParameterType.Name + " " + parameters[i].Name;
            }

            return method.ReturnType.Name + " " + method.Name + "(" + string.Join(", ", texts) + ")";
        }
    }
}
