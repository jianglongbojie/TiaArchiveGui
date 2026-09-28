using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using TiaOpennessKit;
using TiaOpennessKit.Tia;

namespace TiaOpennessKit
{
    /// <summary>
    /// 一条前置条件检查结果。
    /// </summary>
    internal sealed class EnvironmentCheckResult
    {
        /// <summary>检查项名称。</summary>
        public string Name;

        /// <summary>是否通过。</summary>
        public bool Passed;

        /// <summary>true 表示只是"须知"类信息，不参与通过/不通过判定。</summary>
        public bool IsInfoOnly;

        /// <summary>检查到的具体情况。</summary>
        public string Detail;

        /// <summary>不通过时怎么办（可直接照做的话术）。</summary>
        public string FixHint;
    }

    /// <summary>
    /// 前置条件体检。
    ///
    /// 只做 Windows / .NET 层面的检查，**不引用任何 Siemens 类型** ——
    /// 所以哪怕 Openness 程序集还没被解析出来，这个体检也能正常跑。
    ///
    /// 覆盖的是新手最容易卡住的几件事：Openness 组件装没装、用户在不在
    /// Siemens TIA Openness 组里（以及"加了组但没重新登录"这种坑）、
    /// TIA 本体在不在、.NET 版本够不够。
    /// </summary>
    internal static class EnvironmentChecks
    {
        /// <summary>
        /// 执行全部检查（不指定 API 目录）。
        /// </summary>
        /// <param name="logger">日志器。</param>
        /// <returns>检查结果列表。</returns>
        public static List<EnvironmentCheckResult> Run(Logger logger)
        {
            return Run(logger, null);
        }

        /// <summary>
        /// 执行全部检查。
        /// </summary>
        /// <param name="logger">日志器。</param>
        /// <param name="explicitApiDirectory">
        /// 用户手工指定的 API 目录（界面上的“API 目录”输入框，或命令行的 --api-dir）；可为 null。
        /// 指定且可用时，“Openness API 组件”那一项直接判为通过。
        ///
        /// 为什么必须带上它：体检以前只调 EnumerateInstalled（当时只扫常见安装位置），
        /// 于是出现“同一份日志里，体检说 Openness 没装，下面用 --api-dir 却绑定得好好的”
        /// 这种自相矛盾的结论。体检不能比程序的实际能力更严格。
        /// </param>
        /// <returns>检查结果列表。</returns>
        public static List<EnvironmentCheckResult> Run(Logger logger, string explicitApiDirectory)
        {
            List<EnvironmentCheckResult> results = new List<EnvironmentCheckResult>();

            // ── ① Openness API 组件是否安装
            IList<TiaEnvironmentInfo> versions = TiaEnvironment.EnumerateInstalled(logger);

            // 用户手工指定且能绑定成功的目录，同样算“组件可用”。
            TiaEnvironmentInfo explicitEnvironment = null;
            if (!string.IsNullOrWhiteSpace(explicitApiDirectory))
            {
                try
                {
                    explicitEnvironment = TiaEnvironment.Detect(logger, explicitApiDirectory.Trim());
                }
                catch (Exception ex)
                {
                    logger.Debug("手动指定的 API 目录不可用：" + ex.GetType().Name + "：" + ex.Message);
                }
            }

            EnvironmentCheckResult apiCheck = new EnvironmentCheckResult();
            apiCheck.Name = "Openness API 组件";
            if (versions.Count > 0)
            {
                apiCheck.Passed = true;
                apiCheck.Detail = "已安装 " + versions.Count + " 个版本："
                    + TiaEnvironment.DescribeVersions(versions);
            }
            else if (explicitEnvironment != null)
            {
                apiCheck.Passed = true;
                apiCheck.Detail = "自动定位没有找到已安装版本，但手动指定的目录可用："
                    + explicitEnvironment.ApiDirectory;
            }
            else
            {
                apiCheck.Passed = false;
                apiCheck.Detail = "没有找到任何包含 Openness 程序集的 PublicAPI 目录";
                apiCheck.FixHint = "已尝试：注册表登记路径 / 常见安装位置 / 浅层全盘扫描。"
                    + "若 TIA 装在非常规目录，请在“API 目录”里直接指定该版本的 PublicAPI 目录"
                    + "（例如 D:\\V19\\Portal V18\\PublicAPI\\V18）。"
                    + "若确实没装 Openness 组件：运行 TIA 安装介质里的 setup.exe → 选“修改”(Modify) → "
                    + "勾选 “TIA Portal Openness” → 继续安装（免费，需要管理员权限）";
            }
            results.Add(apiCheck);

            // ── ② Openness 本地用户组是否存在
            List<string> members;
            bool groupExists = TryReadGroupMembers(TiaEnvironment.OpennessUserGroup, out members);

            EnvironmentCheckResult groupCheck = new EnvironmentCheckResult();
            groupCheck.Name = "Openness 用户组";
            if (groupExists)
            {
                groupCheck.Passed = true;
                groupCheck.Detail = "本地组 " + TiaEnvironment.OpennessUserGroup + " 存在，成员 "
                    + (members.Count == 0 ? "（空）" : string.Join("、", members.ToArray()));
            }
            else
            {
                groupCheck.Passed = false;
                groupCheck.Detail = "本地组 " + TiaEnvironment.OpennessUserGroup + " 不存在或读取失败";
                groupCheck.FixHint = "正常安装 Openness 组件后该组会自动创建。若确实缺失，"
                    + "可用管理员权限执行：net localgroup \"" + TiaEnvironment.OpennessUserGroup + "\" /add";
            }
            results.Add(groupCheck);

            // ── ③ 当前登录会话是否具备组成员身份（这才是"现在能不能用"的判定）
            bool inGroupNow = false;
            bool isAdministrator = false;
            bool tokenReadable = false;
            try
            {
                WindowsIdentity identity = WindowsIdentity.GetCurrent();
                WindowsPrincipal principal = new WindowsPrincipal(identity);
                inGroupNow = principal.IsInRole(TiaEnvironment.OpennessUserGroup);
                isAdministrator = principal.IsInRole(WindowsBuiltInRole.Administrator);
                tokenReadable = true;
            }
            catch (Exception ex)
            {
                logger.Debug("读取当前用户身份失败：" + ex.Message);
            }

            EnvironmentCheckResult identityCheck = new EnvironmentCheckResult();
            identityCheck.Name = "当前用户权限生效";
            if (!tokenReadable)
            {
                identityCheck.Passed = false;
                identityCheck.Detail = "无法读取当前用户身份";
                identityCheck.FixHint = "以普通用户身份重新运行本程序；若仍失败请检查系统策略。";
            }
            else if (inGroupNow)
            {
                identityCheck.Passed = true;
                identityCheck.Detail = "当前会话已具备 " + TiaEnvironment.OpennessUserGroup + " 权限，Openness 可以通过校验";
            }
            else
            {
                // 关键区分：是真的没加组，还是加了组但当前会话没刷新
                bool listedInGroup = ContainsUser(members, Environment.UserName);
                identityCheck.Passed = false;

                if (listedInGroup)
                {
                    identityCheck.Detail = "你已经在组成员名单里，但**当前登录会话尚未生效** —— "
                        + "Windows 的本地组变更必须重新登录才会作用到当前会话。";
                    identityCheck.FixHint = "注销当前用户后重新登录（或重启电脑），再运行本程序。"
                        + "这一步不做，Openness 一定会报权限错误。";
                }
                else
                {
                    identityCheck.Detail = "当前用户 " + Environment.UserName
                        + " 不在 " + TiaEnvironment.OpennessUserGroup + " 组中";
                    identityCheck.FixHint = "以管理员身份执行：net localgroup \""
                        + TiaEnvironment.OpennessUserGroup + "\" \"" + Environment.UserName + "\" /add"
                        + "  然后注销并重新登录。也可以用本程序生成的“修复前置条件.cmd”一键完成。";
                }
            }
            results.Add(identityCheck);

            // ── ④ TIA Portal 本体是否可用
            EnvironmentCheckResult portalCheck = new EnvironmentCheckResult();
            portalCheck.Name = "TIA Portal 本体";
            if (versions.Count > 0)
            {
                List<string> missing = new List<string>();
                List<string> available = new List<string>();
                foreach (TiaEnvironmentInfo info in versions)
                {
                    string exePath = Path.Combine(info.PortalDirectory, "bin", "Siemens.Automation.Portal.exe");
                    if (File.Exists(exePath))
                    {
                        available.Add("V" + info.MajorVersion);
                    }
                    else
                    {
                        missing.Add("V" + info.MajorVersion);
                    }
                }

                if (missing.Count == 0)
                {
                    portalCheck.Passed = true;
                    portalCheck.Detail = "以下版本的 TIA Portal 主程序存在：" + string.Join("、", available.ToArray());
                }
                else
                {
                    portalCheck.Passed = false;
                    portalCheck.Detail = "以下版本找不到 Siemens.Automation.Portal.exe：" + string.Join("、", missing.ToArray());
                    portalCheck.FixHint = "确认这些版本的 TIA Portal 安装完整；否则归档时 TIA 起不来。";
                }
            }
            else if (explicitEnvironment != null)
            {
                // 手动指定了目录，就用那个版本去核对主程序在不在
                string explicitExe = Path.Combine(
                    explicitEnvironment.PortalDirectory, "bin", "Siemens.Automation.Portal.exe");
                bool explicitExists = File.Exists(explicitExe);
                portalCheck.Passed = explicitExists;
                portalCheck.Detail = explicitExists
                    ? "手动指定的 V" + explicitEnvironment.MajorVersion + " 主程序存在"
                    : "手动指定的 V" + explicitEnvironment.MajorVersion
                        + " 目录下找不到 Siemens.Automation.Portal.exe";
                if (!explicitExists)
                {
                    portalCheck.FixHint = "确认该目录下的 TIA Portal 安装是否完整；否则归档时 TIA 起不来。";
                }
            }
            else
            {
                portalCheck.Passed = false;
                portalCheck.Detail = "未检测到可用的 TIA Portal（依赖上一项：先装 Openness 组件）";
                portalCheck.FixHint = "先完成 Openness API 组件那一项的修复。";
            }
            results.Add(portalCheck);

            // ── ⑤ .NET Framework 版本
            EnvironmentCheckResult dotNetCheck = new EnvironmentCheckResult();
            dotNetCheck.Name = ".NET Framework 版本";
            bool atLeast48;
            dotNetCheck.Detail = DescribeDotNetVersion(out atLeast48);
            dotNetCheck.Passed = atLeast48;
            if (!dotNetCheck.Passed)
            {
                dotNetCheck.FixHint = "从微软官网安装 .NET Framework 4.8 运行时后重试"
                    + "（注意：Openness 不支持 .NET Core / .NET 5+）。";
            }
            results.Add(dotNetCheck);

            // ── ⑥ 管理员权限（只作提示，归档本身不需要）
            EnvironmentCheckResult adminCheck = new EnvironmentCheckResult();
            adminCheck.Name = "管理员权限";
            adminCheck.IsInfoOnly = true;
            adminCheck.Passed = isAdministrator;
            adminCheck.Detail = isAdministrator ? "当前以管理员身份运行" : "当前以普通用户运行";
            adminCheck.FixHint = "归档 / 恢复**不需要**管理员权限（而且官方建议以普通用户运行）。"
                + "只有改用户组、装组件这类系统级操作才需要管理员。";
            results.Add(adminCheck);

            // ── ⑦ 首次连接的授权弹窗（无法自动检测，属于须知）
            EnvironmentCheckResult firewallCheck = new EnvironmentCheckResult();
            firewallCheck.Name = "首次连接授权";
            firewallCheck.IsInfoOnly = true;
            firewallCheck.Passed = true;
            firewallCheck.Detail = "首次让 Openness 连接 TIA Portal 时，桌面会弹出授权窗口";
            firewallCheck.FixHint = "弹窗出现时选“允许”。连续拒绝 3 次会抛 EngineeringSecurityException，"
                + "需要重新运行程序再授权一次。";
            results.Add(firewallCheck);

            return results;
        }

        /// <summary>
        /// 读取实际安装的 .NET Framework 版本。
        ///
        /// 不用 Environment.Version —— 它在 .NET Framework 上永远返回 CLR 版本
        /// "4.0.30319.xxxxx"，看不出到底装的是 4.6.2 还是 4.8。
        /// 注册表里的 Release DWORD 才是准确的。
        /// </summary>
        /// <param name="atLeast48">是否达到 4.8。</param>
        /// <returns>可读的版本描述。</returns>
        private static string DescribeDotNetVersion(out bool atLeast48)
        {
            atLeast48 = false;

            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
                {
                    if (key != null)
                    {
                        object releaseValue = key.GetValue("Release");
                        if (releaseValue != null)
                        {
                            int release = Convert.ToInt32(releaseValue, CultureInfo.InvariantCulture);
                            atLeast48 = release >= 528040;

                            if (release >= 533320)
                            {
                                return "4.8.1 或更高（注册表 Release=" + release + "）";
                            }
                            if (release >= 528040)
                            {
                                return "4.8（注册表 Release=" + release + "）";
                            }
                            if (release >= 461808)
                            {
                                return "4.7.2（注册表 Release=" + release + "）—— 低于要求的 4.8";
                            }
                            if (release >= 460798)
                            {
                                return "4.7（注册表 Release=" + release + "）—— 低于要求的 4.8";
                            }

                            return "低于 4.7（注册表 Release=" + release + "）—— 不满足要求";
                        }
                    }
                }
            }
            catch (Exception)
            {
                // 读注册表失败时退化为 CLR 版本提示
            }

            return "无法从注册表读取（CLR 报告 " + Environment.Version + "），请确认已安装 .NET Framework 4.8";
        }

        /// <summary>
        /// 当前进程是否以管理员身份运行。
        /// </summary>
        /// <returns>是管理员返回 true。</returns>
        public static bool IsAdministrator()
        {
            try
            {
                WindowsIdentity identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 尝试直接把当前用户加入 Openness 组（这就是"帮你完成前置条件"）。
        ///
        /// 前提是当前进程有管理员权限 —— 改本地用户组必须提权。
        /// 返回失败时由调用方提示用户改用生成的 .cmd 脚本。
        /// </summary>
        /// <param name="message">结果说明（中文，可直接显示给用户）。</param>
        /// <returns>成功加入（或本来就在组里）返回 true。</returns>
        public static bool TryAddCurrentUserToGroup(out string message)
        {
            string userName = Environment.UserName;
            string groupName = TiaEnvironment.OpennessUserGroup;

            if (!IsAdministrator())
            {
                message = "当前程序不是以管理员身份运行的，无法修改用户组。"
                    + "请用生成的“修复前置条件.cmd”（右键 → 以管理员身份运行）来完成。";
                return false;
            }

            try
            {
                ProcessStartInfo startInfo = new ProcessStartInfo(
                    "net", "localgroup \"" + groupName + "\" \"" + userName + "\" /add");
                startInfo.RedirectStandardOutput = true;
                startInfo.RedirectStandardError = true;
                startInfo.UseShellExecute = false;
                startInfo.CreateNoWindow = true;
                startInfo.StandardOutputEncoding = Encoding.GetEncoding(936);
                startInfo.StandardErrorEncoding = Encoding.GetEncoding(936);

                using (Process process = Process.Start(startInfo))
                {
                    string output = process.StandardOutput.ReadToEnd();
                    string error = process.StandardError.ReadToEnd();
                    process.WaitForExit(10000);

                    if (process.ExitCode == 0)
                    {
                        message = "已把 " + userName + " 加入 " + groupName + " 组。"
                            + "★ 必须注销并重新登录（或重启），组身份才会对当前会话生效。";
                        return true;
                    }

                    // 常见的"已在组中"也算成功（说明目标状态已经达成）
                    string combined = output + error;
                    if (combined.IndexOf("已经", StringComparison.Ordinal) >= 0
                        || combined.IndexOf("already", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        message = userName + " 本来就在 " + groupName + " 组中。"
                            + "若仍报权限错误，请注销并重新登录让组身份生效。";
                        return true;
                    }

                    message = "加入失败（net localgroup 返回 " + process.ExitCode + "）："
                        + combined.Trim();
                    return false;
                }
            }
            catch (Exception ex)
            {
                message = "加入失败：" + ex.GetType().Name + "：" + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// 读取本地组成员列表。
        ///
        /// ★ 走 Win32 API（netapi32 的 NetLocalGroupGetMembers）而**不是**解析
        ///   `net localgroup` 的文本输出：后者要按"虚线之后到『命令成功完成』之前"去切，
        ///   而那句结束语是**随系统语言变化**的（中文是"命令成功完成"，英文是
        ///   "The command completed successfully"）。在别的语言版本的 Windows 上，
        ///   这个解析会失败，于是体检会误报"当前用户不在组里"，把一个好环境说成坏的。
        ///   API 返回的是结构化数据，与系统语言无关。
        /// </summary>
        /// <param name="groupName">组名。</param>
        /// <param name="members">成员名列表（形如 机器名\用户名）。</param>
        /// <returns>组存在且读取成功返回 true。</returns>
        private static bool TryReadGroupMembers(string groupName, out List<string> members)
        {
            members = new List<string>();
            IntPtr buffer = IntPtr.Zero;

            try
            {
                int entriesRead;
                int totalEntries;
                int resumeHandle = 0;

                // level=2 → LOCALGROUP_MEMBERS_INFO_2（带域\用户名，最省事的一档）
                // prefmaxlen=-1 → MAX_PREFERRED_LENGTH，一次把该组全部成员取回来
                int result = NetLocalGroupGetMembers(
                    null, groupName, 2, out buffer, -1, out entriesRead, out totalEntries, out resumeHandle);

                if (result != 0)
                {
                    // NERR_GroupNotFound / 权限不足等：交给调用方按"组不可用"处理
                    return false;
                }

                int structSize = Marshal.SizeOf(typeof(LocalGroupMembersInfo2));
                IntPtr current = buffer;

                for (int i = 0; i < entriesRead; i++)
                {
                    LocalGroupMembersInfo2 info = (LocalGroupMembersInfo2)Marshal.PtrToStructure(
                        current, typeof(LocalGroupMembersInfo2));

                    // 组里可能有"机器名\用户"这类带域名的写法，交给上层按 '\' 切开比较。
                    if (info != null && !string.IsNullOrEmpty(info.DomainAndName))
                    {
                        members.Add(info.DomainAndName.Trim());
                    }

                    current = IntPtr.Add(current, structSize);
                }

                return true;
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                if (buffer != IntPtr.Zero)
                {
                    NetApiBufferFree(buffer);
                }
            }
        }

        /// <summary>
        /// LOCALGROUP_MEMBERS_INFO_2：一个成员项。
        /// 字段顺序/类型必须与 netapi32.h 完全一致（PSID 用 IntPtr，避免 32/64 位尺寸差异）。
        /// </summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private sealed class LocalGroupMembersInfo2
        {
            /// <summary>成员的 SID 指针（我们不用，但要占位保证后续字段对齐）。</summary>
            public IntPtr Sid;

            /// <summary>SID_NAME_USE 枚举值（同样只用占位）。</summary>
            public int SidUsage;

            /// <summary>成员名，形如 "机器名\\用户名"。</summary>
            [MarshalAs(UnmanagedType.LPWStr)]
            public string DomainAndName;
        }

        /// <summary>枚举本地组成员（level=2 返回 LOCALGROUP_MEMBERS_INFO_2 数组）。</summary>
        [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
        private static extern int NetLocalGroupGetMembers(
            string serverName,
            string localGroupName,
            int level,
            out IntPtr buffer,
            int preferredMaxLength,
            out int entriesRead,
            out int totalEntries,
            out int resumeHandle);

        /// <summary>释放 Net* 系列 API 用 NetApiBufferAllocate 分配的缓冲区。</summary>
        [DllImport("netapi32.dll")]
        private static extern int NetApiBufferFree(IntPtr buffer);

        /// <summary>
        /// 判断成员列表里是否包含指定用户名（忽略大小写，兼容 "机器名\用户名" 写法）。
        /// </summary>
        /// <param name="members">成员列表。</param>
        /// <param name="userName">用户名。</param>
        /// <returns>包含则 true。</returns>
        private static bool ContainsUser(List<string> members, string userName)
        {
            if (members == null || string.IsNullOrEmpty(userName))
            {
                return false;
            }

            foreach (string member in members)
            {
                string name = member;
                int slash = name.LastIndexOf('\\');
                if (slash >= 0)
                {
                    name = name.Substring(slash + 1);
                }

                if (string.Equals(name.Trim(), userName.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 生成"修复前置条件.cmd"：一键把当前用户加入 Openness 组（自动申请管理员权限）。
        ///
        /// 之所以生成脚本而不是程序自己改组：修改本地用户组必须提权，
        /// 而让用户手动右键"以管理员身份运行"一个看得见内容的脚本，比程序偷偷提权更透明、更安全。
        /// </summary>
        /// <param name="targetDirectory">脚本输出目录。</param>
        /// <param name="scriptPath">生成的文件路径。</param>
        /// <returns>成功返回 true。</returns>
        public static bool TryGenerateFixScript(string targetDirectory, out string scriptPath)
        {
            scriptPath = Path.Combine(targetDirectory, "修复前置条件.cmd");

            string userName = Environment.UserName;
            string groupName = TiaEnvironment.OpennessUserGroup;

            StringBuilder script = new StringBuilder();
            script.AppendLine("@echo off");
            script.AppendLine("chcp 936 >nul");
            script.AppendLine("title 修复 TIA Portal Openness 前置条件");
            script.AppendLine("echo ============================================================");
            script.AppendLine("echo   修复 TIA Portal Openness 前置条件");
            script.AppendLine("echo ============================================================");
            script.AppendLine("echo.");
            script.AppendLine("net session >nul 2>&1");
            script.AppendLine("if errorlevel 1 (");
            script.AppendLine("    echo [错误] 需要管理员权限。");
            script.AppendLine("    echo        请关闭本窗口，右键本文件 -^> “以管理员身份运行”。");
            script.AppendLine("    echo.");
            script.AppendLine("    pause");
            script.AppendLine("    exit /b 1");
            script.AppendLine(")");
            script.AppendLine("echo 正在把用户 " + userName + " 加入组 " + groupName + " ...");
            script.AppendLine("net localgroup \"" + groupName + "\" \"" + userName + "\" /add >nul 2>&1");
            script.AppendLine("if errorlevel 1 (");
            script.AppendLine("    echo.");
            script.AppendLine("    echo [提示] 加入失败，可能是：用户已在组中、组名不存在、或权限不足。");
            script.AppendLine("    echo        正在显示该组当前成员供核对：");
            script.AppendLine("    echo.");
            script.AppendLine("    net localgroup \"" + groupName + "\"");
            script.AppendLine(") else (");
            script.AppendLine("    echo.");
            script.AppendLine("    echo [成功] 已把 " + userName + " 加入 " + groupName + "。");
            script.AppendLine(")");
            script.AppendLine("echo.");
            script.AppendLine("echo ★★★ 必须注销并重新登录（或重启电脑），组成员身份才会生效！ ★★★");
            script.AppendLine("echo     只加组不重新登录，Openness 仍然会报权限错误。");
            script.AppendLine("echo.");
            script.AppendLine("pause");

            try
            {
                if (!Directory.Exists(targetDirectory))
                {
                    Directory.CreateDirectory(targetDirectory);
                }

                // 批处理文件用系统 ANSI（936）写，否则 cmd 里中文会乱码
                File.WriteAllText(scriptPath, script.ToString(), Encoding.GetEncoding(936));
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
