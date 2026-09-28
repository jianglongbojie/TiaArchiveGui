using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using TiaOpennessKit.Tia;

namespace TiaOpennessKit.Services
{
    /// <summary>
    /// API 探针：不依赖任何"我以为的签名"，直接用反射把本机 Siemens.Engineering 程序集里
    /// 与归档/检索有关的真实方法签名、枚举值打印出来。
    ///
    /// 为什么必须有这条命令？
    ///   Openness 的归档接口在不同版本之间发生过变化——早期版本用 string 传路径，
    ///   V16+ 常见 FileInfo / DirectoryInfo 重载，V21 又把 DLL 拆成模块化程序集。
    ///   与其在文档里猜，不如让工具自己把本机的事实读出来。
    ///
    /// 类比 SCL/ST：这相当于在线读取 PLC 里某个 FB 的接口表（Interface / Declaration），
    /// 而不是照着印刷手册抄引脚。
    /// </summary>
    public static class ApiProbe
    {
        /// <summary>曾被若干资料提到的归档服务接口全名（在 V21 上实测不存在，这里只做存在性检查）。</summary>
        private const string LegacyArchiveServiceName = "Siemens.Engineering.Archive.IArchiveService";

        /// <summary>
        /// 与归档相关的关键类型全名清单。
        /// 注意：这里只列**本机真实存在**的全名——probe 的价值就在于照它打的输出写代码不会错，
        /// 混入历史拼错的类型名会让使用者无法判断谁真谁假。
        /// （V21 实测：UserGlobalLibrary 在 Siemens.Engineering.Library 命名空间下，
        ///   不存在 Siemens.Engineering.UserGlobalLibrary 这种写法。）
        /// </summary>
        private static readonly string[] InterestingTypeNames = new string[]
        {
            "Siemens.Engineering.Project",
            "Siemens.Engineering.ProjectComposition",
            "Siemens.Engineering.TiaPortal",
            "Siemens.Engineering.Library.UserGlobalLibrary"
        };

        /// <summary>用于匹配"归档/检索相关"成员名字的关键字。</summary>
        private static readonly string[] InterestingMemberNames = new string[]
        {
            "Archive",
            "Retrieve"
        };

        /// <summary>
        /// 执行探测：打印环境、程序集清单、签名、枚举值。
        /// </summary>
        /// <param name="environment">探测到的 TIA 环境。</param>
        /// <param name="logger">日志器。</param>
        /// <param name="assemblyNameFilter">程序集文件名过滤关键字；可为 null 或空。</param>
        public static void Run(TiaEnvironmentInfo environment, Logger logger, string assemblyNameFilter)
        {
            logger.Section("Openness API 探测开始");
            logger.Info(environment.ToString());

            foreach (string assemblyPath in environment.Assemblies)
            {
                logger.Debug("可用程序集：" + assemblyPath);
            }

            List<Assembly> loaded = LoadAssemblies(environment, logger, assemblyNameFilter);
            logger.Section("第一步：检查历史资料里提到的 IArchiveService 是否存在");
            ReportLegacyArchiveService(loaded, logger);

            logger.Section("第二步：打印与归档/检索相关的方法签名");
            ReportArchiveMethods(loaded, logger);

            logger.Section("第三步：打印与归档相关的枚举及其取值");
            ReportArchiveEnums(loaded, logger);

            logger.Section("第四步：关键类型是否存在");
            ReportInterestingTypes(loaded, logger);

            // 第五步：用运行时绑定层（OpennessApi）做一次"能不能真的驱动这个版本"的自检。
            // 这一节才是重点：前四步是"这个版本有什么"，这一步是"本程序要用的东西它有没有"。
            // 只读元数据、不连 TIA，所以可以对着任意版本的 API 目录跑。
            logger.Section("第五步：本程序所需 API 的可用性自检");
            ReportBindingSelfCheck(environment, logger);

            logger.Section("探测结束");
            logger.Ok("以上均读取自本机实际安装的 DLL，可直接据此编写调用代码。");
        }

        /// <summary>
        /// 绑定该版本并逐项检查归档/检索所需的类型与成员是否齐全。
        /// </summary>
        /// <param name="environment">环境。</param>
        /// <param name="logger">日志器。</param>
        private static void ReportBindingSelfCheck(TiaEnvironmentInfo environment, Logger logger)
        {
            try
            {
                OpennessApi api = OpennessApi.Bind(environment, logger);
                IList<string> problems = api.SelfCheck();

                if (problems.Count == 0)
                {
                    logger.Ok("可以驱动该版本：" + api.AssemblyNameText
                        + " 里归档/检索所需的类型与成员齐全。");
                    return;
                }

                logger.Error("该版本缺少本程序需要的 API，共 " + problems.Count + " 项：");
                foreach (string problem in problems)
                {
                    logger.Error("  - " + problem);
                }
            }
            catch (Exception ex)
            {
                logger.Error("绑定该版本失败：" + ex.GetType().Name + "：" + ex.Message);
            }
        }

        /// <summary>
        /// 加载 API 目录下的 Siemens 程序集（过滤后），加载失败的会打印原因但不中断。
        /// </summary>
        /// <param name="environment">TIA 环境。</param>
        /// <param name="logger">日志器。</param>
        /// <param name="assemblyNameFilter">文件名过滤关键字。</param>
        /// <returns>加载成功的程序集列表。</returns>
        private static List<Assembly> LoadAssemblies(TiaEnvironmentInfo environment, Logger logger, string assemblyNameFilter)
        {
            List<Assembly> loaded = new List<Assembly>();

            foreach (string assemblyPath in environment.Assemblies)
            {
                string fileName = Path.GetFileName(assemblyPath);
                if (!string.IsNullOrWhiteSpace(assemblyNameFilter) &&
                    fileName.IndexOf(assemblyNameFilter, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                try
                {
                    Assembly assembly = Assembly.LoadFrom(assemblyPath);
                    loaded.Add(assembly);
                    logger.Debug("已加载：" + fileName);
                }
                catch (Exception ex)
                {
                    logger.Warning("加载 " + fileName + " 失败：" + ex.Message);
                    logger.Info("  这通常表示该程序集缺少宿主依赖（例如没有其他 Siemens 模块），不影响其余程序集的探测。");
                }
            }

            if (loaded.Count == 0)
            {
                throw new ToolException(
                    ExitCodes.Environment,
                    "没有任何 Siemens 程序集可以加载。用 -v 查看具体原因，或用 --assembly <关键字> 缩小范围。");
            }

            return loaded;
        }

        /// <summary>
        /// 报告历史资料中提到的 IArchiveService 是否存在于本机。
        /// </summary>
        /// <param name="assemblies">已加载的程序集。</param>
        /// <param name="logger">日志器。</param>
        private static void ReportLegacyArchiveService(List<Assembly> assemblies, Logger logger)
        {
            Type found = FindType(assemblies, LegacyArchiveServiceName);
            if (found == null)
            {
                logger.Warning("未找到 " + LegacyArchiveServiceName + "。");
                logger.Info("  结论：本机安装的 Openness 版本不使用这套接口。");
                logger.Info("  请以下面第二步打印的真实签名为准来调用归档功能。");
                return;
            }

            logger.Ok("找到 " + LegacyArchiveServiceName + "。");
            PrintTypeMembers(found, logger, "  ");
        }

        /// <summary>
        /// 打印所有与 Archive/Retrieve 相关的方法签名（含所在类型、参数类型、返回值）。
        /// </summary>
        /// <param name="assemblies">已加载的程序集。</param>
        /// <param name="logger">日志器。</param>
        private static void ReportArchiveMethods(List<Assembly> assemblies, Logger logger)
        {
            int printed = 0;

            foreach (Assembly assembly in assemblies)
            {
                foreach (Type type in SafeGetTypes(assembly, logger))
                {
                    if (!TypeLooksInteresting(type))
                    {
                        continue;
                    }

                    List<MethodInfo> methods = CollectInterestingMethods(type);
                    if (methods.Count == 0)
                    {
                        continue;
                    }

                    logger.Info("● " + type.FullName + "   [" + assembly.GetName().Name + "]");
                    foreach (MethodInfo method in methods)
                    {
                        logger.Info("    " + DescribeSignature(method));
                        printed++;
                    }
                }
            }

            if (printed == 0)
            {
                logger.Warning("没有找到任何名为 Archive / Retrieve 的公开方法。请确认加载的程序集是否正确。");
                return;
            }

            logger.Info("共打印 " + printed.ToString(CultureInfo.InvariantCulture) + " 个方法签名。");
        }

        /// <summary>
        /// 打印名字里带 Archive/Retrieve 的枚举类型及其全部取值。
        /// </summary>
        /// <param name="assemblies">已加载的程序集。</param>
        /// <param name="logger">日志器。</param>
        private static void ReportArchiveEnums(List<Assembly> assemblies, Logger logger)
        {
            int printed = 0;

            foreach (Assembly assembly in assemblies)
            {
                foreach (Type type in SafeGetTypes(assembly, logger))
                {
                    if (!type.IsEnum)
                    {
                        continue;
                    }
                    if (type.Name.IndexOf("Archive", StringComparison.OrdinalIgnoreCase) < 0 &&
                        type.Name.IndexOf("Retrieve", StringComparison.OrdinalIgnoreCase) < 0 &&
                        type.Name.IndexOf("Archivation", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    logger.Info("● 枚举 " + type.FullName + "   [" + assembly.GetName().Name + "]");
                    foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Static))
                    {
                        object raw = field.GetValue(null);
                        string numeric;
                        try
                        {
                            numeric = Convert.ToInt64(raw, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
                        }
                        catch (Exception)
                        {
                            numeric = raw != null ? raw.ToString() : "(null)";
                        }
                        logger.Info("    " + field.Name + " = " + numeric);
                        printed++;
                    }
                }
            }

            if (printed == 0)
            {
                logger.Warning("没有找到与归档相关的枚举类型。");
            }
        }

        /// <summary>
        /// 逐个检查关键类型的存在性与成员概览。
        /// </summary>
        /// <param name="assemblies">已加载的程序集。</param>
        /// <param name="logger">日志器。</param>
        private static void ReportInterestingTypes(List<Assembly> assemblies, Logger logger)
        {
            foreach (string typeName in InterestingTypeNames)
            {
                Type type = FindType(assemblies, typeName);
                if (type == null)
                {
                    logger.Warning("缺失类型：" + typeName);
                    continue;
                }

                logger.Ok("存在类型：" + type.FullName + "   [" + (type.Assembly != null ? type.Assembly.GetName().Name : "?") + "]");
                foreach (MethodInfo method in CollectInterestingMethods(type))
                {
                    logger.Info("    " + DescribeSignature(method));
                }
            }
        }

        /// <summary>
        /// 判断某个类型是否值得列入出具。
        /// </summary>
        /// <param name="type">类型。</param>
        /// <returns>值得则返回 true。</returns>
        private static bool TypeLooksInteresting(Type type)
        {
            if (type == null || !type.IsPublic)
            {
                return false;
            }

            if (CollectInterestingMethods(type).Count > 0)
            {
                return true;
            }

            return type.FullName.IndexOf("Archive", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// 收集类型里名字命中 Archive/Retrieve 的公开实例方法。
        /// </summary>
        /// <param name="type">类型。</param>
        /// <returns>方法列表。</returns>
        private static List<MethodInfo> CollectInterestingMethods(Type type)
        {
            List<MethodInfo> result = new List<MethodInfo>();

            MethodInfo[] methods;
            try
            {
                methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);
            }
            catch (Exception)
            {
                return result;
            }

            foreach (MethodInfo method in methods)
            {
                foreach (string keyword in InterestingMemberNames)
                {
                    if (string.Equals(method.Name, keyword, StringComparison.Ordinal) ||
                        method.Name.StartsWith(keyword, StringComparison.Ordinal))
                    {
                        if (!result.Contains(method))
                        {
                            result.Add(method);
                        }
                        break;
                    }
                }
            }

            result.Sort(delegate (MethodInfo left, MethodInfo right)
            {
                int byName = string.CompareOrdinal(left.Name, right.Name);
                if (byName != 0)
                {
                    return byName;
                }
                return left.GetParameters().Length.CompareTo(right.GetParameters().Length);
            });

            return result;
        }

        /// <summary>
        /// 安全读取程序集里的所有类型；部分类型因缺依赖无法加载时返回能加载的部分。
        /// </summary>
        /// <param name="assembly">程序集。</param>
        /// <param name="logger">日志器。</param>
        /// <returns>可加载的类型数组。</returns>
        private static Type[] SafeGetTypes(Assembly assembly, Logger logger)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                List<Type> usable = new List<Type>();
                if (ex.Types != null)
                {
                    foreach (Type type in ex.Types)
                    {
                        if (type != null)
                        {
                            usable.Add(type);
                        }
                    }
                }
                logger.Debug("部分类型无法加载（" + assembly.GetName().Name + "）：" + usable.Count + " 个可用，" + ex.Message);
                return usable.ToArray();
            }
            catch (Exception ex)
            {
                logger.Warning("读取类型失败（" + assembly.GetName().Name + "）：" + ex.Message);
                return new Type[0];
            }
        }

        /// <summary>
        /// 在多程序集中按全名查找类型。
        /// </summary>
        /// <param name="assemblies">程序集列表。</param>
        /// <param name="fullName">类型全名。</param>
        /// <returns>找到返回类型，否则 null。</returns>
        private static Type FindType(List<Assembly> assemblies, string fullName)
        {
            foreach (Assembly assembly in assemblies)
            {
                Type type = assembly.GetType(fullName, false, false);
                if (type != null)
                {
                    return type;
                }
            }
            return null;
        }

        /// <summary>
        /// 把方法签名格式化为一行可读文本。
        /// </summary>
        /// <param name="method">方法。</param>
        /// <returns>签名文本。</returns>
        private static string DescribeSignature(MethodInfo method)
        {
            ParameterInfo[] parameters = method.GetParameters();
            string[] texts = new string[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                texts[i] = PrettyTypeName(parameters[i].ParameterType) + " " + parameters[i].Name;
            }
            return PrettyTypeName(method.ReturnType) + " " + method.Name + "(" + string.Join(", ", texts) + ")";
        }

        /// <summary>
        /// 把类型转成可读名字（处理泛型）。
        /// </summary>
        /// <param name="type">类型。</param>
        /// <returns>可读名字。</returns>
        private static string PrettyTypeName(Type type)
        {
            if (type == null)
            {
                return "void";
            }

            if (!type.IsGenericType)
            {
                return type.Name;
            }

            string name = type.Name;
            int tick = name.IndexOf('`');
            if (tick > 0)
            {
                name = name.Substring(0, tick);
            }

            Type[] arguments = type.GetGenericArguments();
            string[] pieces = new string[arguments.Length];
            for (int i = 0; i < arguments.Length; i++)
            {
                pieces[i] = PrettyTypeName(arguments[i]);
            }
            return name + "<" + string.Join(", ", pieces) + ">";
        }

        /// <summary>
        /// 打印某个类型的公开成员（用于 IArchiveService 命中时的详细展开）。
        /// </summary>
        /// <param name="type">类型。</param>
        /// <param name="logger">日志器。</param>
        /// <param name="indent">缩进。</param>
        private static void PrintTypeMembers(Type type, Logger logger, string indent)
        {
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            {
                logger.Info(indent + DescribeSignature(method));
            }
        }
    }
}
