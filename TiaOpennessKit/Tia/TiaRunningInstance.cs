using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace TiaOpennessKit.Tia
{
    /// <summary>本机正在运行的一个 TIA Portal 进程。</summary>
    public sealed class RunningTiaInstance
    {
        /// <summary>进程 Id。</summary>
        public int ProcessId { get; set; }

        /// <summary>进程名（如 Siemens.Automation.Portal）。</summary>
        public string ProcessName { get; set; }

        /// <summary>可执行文件完整路径（读不到时为空串）。</summary>
        public string ExecutablePath { get; set; }

        /// <summary>从路径/文件版本解析出的 TIA 主版本号；无法确定时为 0。</summary>
        public int MajorVersion { get; set; }

        /// <summary>版本来源说明（排错用）。</summary>
        public string VersionSource { get; set; }

        /// <summary>可读描述。</summary>
        public override string ToString()
        {
            return "PID " + ProcessId.ToString(CultureInfo.InvariantCulture)
                + " · " + (MajorVersion > 0 ? "V" + MajorVersion.ToString(CultureInfo.InvariantCulture) : "版本未知")
                + " · " + (string.IsNullOrEmpty(ExecutablePath) ? ProcessName : ExecutablePath);
        }
    }

    /// <summary>
    /// 探测"本机正在运行的 TIA Portal"，并解析它是哪个主版本。
    ///
    /// 为什么需要它（用户需求 7②：附加到已打开的实例时，要自动用对应版本的 Openness，且不升级项目）：
    ///   一个进程只能加载一个版本的 Siemens.Engineering，**必须先知道目标 TIA 的版本、再绑定 API**，
    ///   顺序反了就会加载错版本然后必然失败（这是之前踩过的坑）。
    ///
    /// 判定依据按可靠性排序：
    ///   ① 进程可执行文件路径里的 `Portal V<数字>`（如 `...\Portal V19\bin\Siemens.Automation.Portal.exe`）；
    ///   ② 取不到路径时，退而读 exe 的文件版本号（ProductMajorPart）；
    ///   ③ 都拿不到 → 主版本 0，由调用方让用户手动指定（界面版有"可用版本"下拉）。
    /// </summary>
    public static class TiaRunningInstance
    {
        private static readonly Regex PortalVersionPattern =
            new Regex(@"Portal[\s_\-]*V(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>进程名里出现这些片段就认为是 TIA Portal 主进程。</summary>
        private static readonly string[] ProcessNameHints = new string[]
        {
            "Siemens.Automation.Portal", "TIA Portal", "TiaPortal"
        };

        /// <summary>枚举本机正在运行的 TIA Portal 进程。</summary>
        /// <param name="logger">日志器。</param>
        /// <returns>进程列表（可能为空）。</returns>
        public static IList<RunningTiaInstance> Enumerate(Logger logger)
        {
            List<RunningTiaInstance> found = new List<RunningTiaInstance>();
            Process[] processes;
            try
            {
                processes = Process.GetProcesses();
            }
            catch (Exception ex)
            {
                if (logger != null)
                {
                    logger.Warning("枚举进程失败：" + ex.Message);
                }

                return found;
            }

            foreach (Process process in processes)
            {
                try
                {
                    string name = process.ProcessName;
                    if (!LooksLikePortal(name))
                    {
                        continue;
                    }

                    RunningTiaInstance instance = new RunningTiaInstance();
                    instance.ProcessId = process.Id;
                    instance.ProcessName = name;

                    string path = TryGetExecutablePath(process);
                    instance.ExecutablePath = path ?? string.Empty;

                    int major;
                    string source;
                    ResolveMajorVersion(path, out major, out source);
                    instance.MajorVersion = major;
                    instance.VersionSource = source;

                    found.Add(instance);
                }
                catch (Exception)
                {
                    // 单个进程读不到就跳过（权限、已退出等），不影响整体
                }
                finally
                {
                    process.Dispose();
                }
            }

            if (logger != null)
            {
                foreach (RunningTiaInstance instance in found)
                {
                    logger.Info("发现运行中的 TIA：PID " + instance.ProcessId.ToString(CultureInfo.InvariantCulture)
                        + "，版本 V" + instance.MajorVersion.ToString(CultureInfo.InvariantCulture)
                        + "（" + instance.VersionSource + "）");
                }
            }

            return found;
        }

        /// <summary>
        /// 探测"该用哪个主版本"，用于附加已打开的实例。
        /// 只有**所有**运行中的 TIA 都是同一个主版本时才返回该版本；否则返回 0（需用户指定）。
        /// </summary>
        /// <param name="logger">日志器。</param>
        /// <param name="detail">说明文本（用于界面提示）。</param>
        /// <returns>主版本号；0 表示无法唯一确定。</returns>
        public static int DetectSingleMajorVersion(Logger logger, out string detail)
        {
            IList<RunningTiaInstance> instances = Enumerate(logger);
            if (instances.Count == 0)
            {
                detail = "本机没有检测到正在运行的 TIA Portal。";
                return 0;
            }

            int major = 0;
            bool conflict = false;
            List<string> unknown = new List<string>();
            foreach (RunningTiaInstance instance in instances)
            {
                if (instance.MajorVersion <= 0)
                {
                    unknown.Add("PID " + instance.ProcessId.ToString(CultureInfo.InvariantCulture));
                    continue;
                }

                if (major == 0)
                {
                    major = instance.MajorVersion;
                }
                else if (major != instance.MajorVersion)
                {
                    conflict = true;
                }
            }

            if (conflict)
            {
                detail = "同时有多个不同主版本的 TIA Portal 在运行，无法自动判定，请手动选择版本。";
                return 0;
            }

            if (major == 0)
            {
                detail = "检测到 " + instances.Count.ToString(CultureInfo.InvariantCulture)
                    + " 个 TIA Portal 进程（" + string.Join("、", unknown.ToArray()) + "），但读不出版本，请手动选择。";
                return 0;
            }

            detail = "运行中的 TIA Portal 主版本：V" + major.ToString(CultureInfo.InvariantCulture)
                + "（" + instances.Count.ToString(CultureInfo.InvariantCulture) + " 个进程）";
            return major;
        }

        /// <summary>
        /// 在已枚举的安装版本里，找出与指定主版本匹配的那个（用于"自动调用对应版本的 Openness"）。
        /// 匹配时**优先取本机已安装且版本号相同**的那个；找不到返回 null。
        /// </summary>
        /// <param name="installed">已安装版本列表（TiaEnvironment.EnumerateInstalled 的结果）。</param>
        /// <param name="majorVersion">目标主版本号。</param>
        /// <returns>匹配的环境或 null。</returns>
        public static TiaEnvironmentInfo FindMatchingEnvironment(IList<TiaEnvironmentInfo> installed, int majorVersion)
        {
            if (installed == null || majorVersion <= 0)
            {
                return null;
            }

            foreach (TiaEnvironmentInfo info in installed)
            {
                if (info != null && info.MajorVersion == majorVersion)
                {
                    return info;
                }
            }

            return null;
        }

        /// <summary>进程名是否像 TIA Portal。</summary>
        private static bool LooksLikePortal(string processName)
        {
            if (string.IsNullOrEmpty(processName))
            {
                return false;
            }

            foreach (string hint in ProcessNameHints)
            {
                if (processName.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>读进程的可执行文件路径；读不到返回 null（跨位数/权限受限时会发生）。</summary>
        private static string TryGetExecutablePath(Process process)
        {
            try
            {
                ProcessModule module = process.MainModule;
                return module == null ? null : module.FileName;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 从一段路径里解析 TIA 主版本号（形如 `...\Portal V19\bin\...` → 19）。
        /// 公开出来是为了让自检能直接断言解析规则，而不是各自再写一份正则。
        /// 解析不到返回 0。
        /// </summary>
        /// <param name="path">可执行文件路径。</param>
        /// <returns>主版本号；解析不到为 0。</returns>
        public static int ParseMajorVersionFromPath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return 0;
            }

            Match match = PortalVersionPattern.Match(path);
            if (!match.Success)
            {
                return 0;
            }

            int parsed;
            if (int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)
                && parsed > 0)
            {
                return parsed;
            }

            return 0;
        }

        /// <summary>从路径或文件版本解析主版本号。</summary>
        private static void ResolveMajorVersion(string path, out int major, out string source)
        {
            major = 0;
            source = "无法判定";

            int fromPath = ParseMajorVersionFromPath(path);
            if (fromPath > 0)
            {
                major = fromPath;
                source = "安装路径";
                return;
            }

            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                try
                {
                    FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
                    if (info.ProductMajorPart > 0)
                    {
                        major = info.ProductMajorPart;
                        source = "文件版本号";
                        return;
                    }
                }
                catch (Exception)
                {
                    // 落到下面返回无法判定
                }
            }

            if (string.IsNullOrEmpty(path))
            {
                source = "读不到进程路径（可能需要管理员权限或位数不同）";
            }
        }
    }
}
