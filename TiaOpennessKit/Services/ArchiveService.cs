using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using TiaOpennessKit.Tia;

namespace TiaOpennessKit.Services
{
    /// <summary>
    /// 归档 / 检索（解包）的封装层。
    ///
    /// ===== 各版本一致的真实签名（V16~V21 实测，来源：各版本 Siemens.Engineering.xml 官方文档）=====
    ///   void  Project.Archive(DirectoryInfo targetDirectory, string targetName, ProjectArchivationMode archivationMode)
    ///   Project ProjectComposition.Retrieve(FileInfo sourcePath, DirectoryInfo targetDirectory)
    ///   Project ProjectComposition.RetrieveWithUpgrade(FileInfo sourcePath, DirectoryInfo targetDirectory)
    ///
    /// 重要更正：**不存在** Siemens.Engineering.Archive.IArchiveService / ArchiveFolderMode。
    /// 那套（更早/其他产品线的）假设在 V16~V21 上都不成立，因此本工具直接面向
    /// Project / ProjectComposition 编程。
    ///
    /// ★ 本文件**不引用任何 Siemens 类型**：项目对象一律用 <c>object</c> 传递，
    ///   真正的调用交给 <see cref="OpennessApi"/> 在运行时按类型名反射。
    ///   这样一份 exe 才能同时吃下 V15~V21（各版本程序集**文件名**都不同，见 OpennessApi 注释）。
    ///
    /// 归档模式枚举的四个成员名各版本完全一致（已核实 V16~V21）：
    ///   None                            不压缩，类似"另存为"，TIA 自己决定扩展名
    ///   Compressed                      压缩，产出文件名为用户给定名（推荐）
    ///   DiscardRestorableData           丢弃可恢复数据
    ///   DiscardRestorableDataAndCompressed  丢弃可恢复数据并压缩
    ///
    /// 类比 SCL：ArchiveMode 就像 SCL 里自定义的枚举常量（MODE_NONE / MODE_COMPRESSED...），
    /// 传入不同值走不同处理分支；调用时再按名字翻译成目标版本的真实枚举。
    /// </summary>
    public sealed class ArchiveService
    {
        /// <summary>归档产物常见的扩展名前缀，用于扩展名检查提示（不强制）。</summary>
        private const string ArchiveExtensionPrefix = ".zap";

        private readonly Logger _logger;

        /// <summary>构造归档服务。</summary>
        /// <param name="logger">日志器。</param>
        public ArchiveService(Logger logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// 支持的归档模式关键字及其对应的枚举值（已按真实枚举逐个核实）。
        /// </summary>
        /// <returns>关键字 -> 枚举值 的有序映射。</returns>
        public static IDictionary<string, ArchiveMode> GetSupportedModes()
        {
            Dictionary<string, ArchiveMode> map = new Dictionary<string, ArchiveMode>(StringComparer.OrdinalIgnoreCase)
            {
                { "none", ArchiveMode.None },
                { "compressed", ArchiveMode.Compressed },
                { "compress", ArchiveMode.Compressed },
                { "zip", ArchiveMode.Compressed },
                { "discard-restorable", ArchiveMode.DiscardRestorableData },
                { "discardrestorabledata", ArchiveMode.DiscardRestorableData },
                { "discard-restorable-compressed", ArchiveMode.DiscardRestorableDataAndCompressed },
                { "discardrestorabledataandcompressed", ArchiveMode.DiscardRestorableDataAndCompressed }
            };
            return map;
        }

        /// <summary>
        /// 判断某个归档模式是否会**不可逆地丢弃**项目数据。
        /// 丢弃可恢复数据之后，项目就无法再用于下载 / 在线比较，
        /// 所以在执行前和执行时都要给出醒目警示。
        /// </summary>
        /// <param name="mode">归档模式。</param>
        /// <returns>是丢弃类模式返回 true。</returns>
        public static bool IsIrreversibleMode(ArchiveMode mode)
        {
            return mode == ArchiveMode.DiscardRestorableData
                || mode == ArchiveMode.DiscardRestorableDataAndCompressed;
        }

        /// <summary>
        /// 把命令行里写的模式关键字转成枚举。
        /// </summary>
        /// <param name="keyword">例如 compressed。</param>
        /// <returns>对应的枚举值。</returns>
        /// <exception cref="ToolException">关键字非法时抛出（退出码=用法错误）。</exception>
        public static ArchiveMode ParseArchivationMode(string keyword)
        {
            IDictionary<string, ArchiveMode> supported = GetSupportedModes();
            ArchiveMode mode;
            if (!string.IsNullOrWhiteSpace(keyword) && supported.TryGetValue(keyword.Trim(), out mode))
            {
                return mode;
            }

            List<string> valid = supported.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
            throw new ToolException(
                ExitCodes.Usage,
                "非法归档模式 '" + keyword + "'。合法取值：" + string.Join(" / ", valid.ToArray()));
        }

        /// <summary>
        /// 把当前打开的项目归档为 .zapXX 文件。
        ///
        /// 关键调用（一行，V21 真实 API）：
        ///   project.Archive(targetDirectory, targetName, mode);
        ///
        /// ★ "覆盖"必须由调用方自己做（实测，V21）：TIA 的 Project.Archive **不会覆盖**已存在的目标，
        ///   直接报 "Archive Operation is not possible as the target file/folder '…' is already exist."。
        ///   所以目标已存在时：
        ///     · <paramref name="overwriteExisting"/> 为 false → 当场给中文错误（不去做注定失败的调用）；
        ///     · 为 true（调用方已获用户确认）→ 先把旧文件改名成 ".old" 备份，归档成功再删掉备份；
        ///       归档失败则把备份改回原名 —— 不会出现"旧文件已删、新归档也没成功"的窗口。
        /// </summary>
        /// <param name="project">已打开的 TIA 项目。</param>
        /// <param name="archiveOutputPath">归档输出文件路径。</param>
        /// <param name="modeKeyword">归档模式关键字。</param>
        /// <param name="overwriteExisting">目标已存在时是否允许覆盖（需调用方已获用户确认）。</param>
        /// <returns>实际生成的归档文件（按显著差异前后 Enumeration 得到，避免猜扩展名）。</returns>
        public FileInfo ArchiveProject(
            object project, string archiveOutputPath, string modeKeyword, bool overwriteExisting)
        {
            if (project == null)
            {
                throw new ToolException(ExitCodes.Api, "ArchiveProject 收到空的 project 实例。");
            }

            ArchiveMode mode = ParseArchivationMode(modeKeyword);
            FileInfo targetFile = new FileInfo(Path.GetFullPath(archiveOutputPath));
            DirectoryInfo targetDirectory = targetFile.Directory;

            if (targetDirectory == null)
            {
                throw new ToolException(ExitCodes.Usage, "归档输出路径不合法：" + archiveOutputPath);
            }

            if (!targetDirectory.Exists)
            {
                _logger.Info("输出目录不存在，自动创建：" + targetDirectory.FullName);
                targetDirectory.Create();
            }

            string targetName = targetFile.Name;
            WarnOnUnexpectedExtension(targetName);

            _logger.Section("归档（Archive）");
            _logger.Info("目标目录：" + targetDirectory.FullName);
            _logger.Info("目标文件名：" + targetName);
            _logger.Info("归档模式：" + modeKeyword + " -> " + mode);

            if (IsIrreversibleMode(mode))
            {
                // P1-4：这是不可逆操作，在真正调用之前最后再确认一次。
                _logger.Warning("归档模式 " + mode + " 会丢弃可恢复数据，此操作不可逆。");
                _logger.Warning("归档后原项目的在线修改 / 下载信息将无法恢复，该项目也不能再用于下载或在线比较。");
            }

            // 目标已存在 → 按"是否已获覆盖授权"决定：改名备份，或者当场拦下。
            string overwriteBackupPath = MoveExistingTargetAside(targetFile, overwriteExisting);
            bool archiveConfirmed = false;

            try
            {
                // P2-2：先对目录做一次快照，归档完成后只在"新增或发生变化"的文件里找产物，
                // 避免在脏目录里把某个不相干的最新文件错报成归档结果。
                // （放在备份改名之后：快照里不该再把马上要被覆盖的旧文件算成"原有文件"。）
                Dictionary<string, DateTime> snapshotBefore = CaptureSnapshot(targetDirectory);

                System.Diagnostics.Stopwatch stopwatch = new System.Diagnostics.Stopwatch();
                stopwatch.Start();
                try
                {
                    // ★ 核心调用 1-of-2：运行时反射调用 project.Archive(目录, 文件名, 模式)
                    //   （以前这里是强类型调用，导致 exe 只能认一个 Openness 版本）
                    OpennessApi.Current.ArchiveProject(project, targetDirectory, targetName, mode);
                }
                catch (Exception ex)
                {
                    throw Tia.TiaSession.WrapSiemensFailure(ex, "调用 Project.Archive 失败。");
                }
                stopwatch.Stop();

                // 不同模式下 TIA 可能改写扩展名（例如自动变成 .zap21），
                // 因此不假设产出文件名，而是先按名字精确定位、再按最近创建时间回退查找。
                targetDirectory.Refresh();
                _logger.Debug("目录内文件数：" + CountFiles(targetDirectory));

                FileInfo result = LocateArchiveProduct(targetDirectory, targetName, snapshotBefore);
                if (result == null)
                {
                    throw new ToolException(
                        ExitCodes.Api,
                        "Project.Archive 已返回，但在目标目录没有检测到新增或被覆盖的产出文件："
                        + targetDirectory.FullName
                        + "。请用 -v 重新运行查看目录快照差异。");
                }

                archiveConfirmed = true;
                _logger.Ok("归档完成：" + result.FullName);
                _logger.Info("归档文件大小：" + SizeFormat.Format(result.Length));
                _logger.Info("归档耗时：" + FormatElapsed(stopwatch.Elapsed));

                return result;
            }
            finally
            {
                // 只有"确认拿到产物"才丢弃旧备份；其余情况（异常 / 没定位到产物）一律把旧文件恢复回去。
                if (overwriteBackupPath != null)
                {
                    if (archiveConfirmed)
                    {
                        DiscardOverwriteBackup(overwriteBackupPath);
                    }
                    else
                    {
                        RestoreOverwriteBackup(overwriteBackupPath, targetFile);
                    }
                }
            }
        }

        /// <summary>覆盖备份的扩展名后缀（旧归档先改名为 &lt;原名&gt;.old 再归档）。</summary>
        private const string OverwriteBackupSuffix = ".old";

        /// <summary>
        /// 目标文件已存在时的覆盖准备：按是否已获授权决定"改名备份"或"当场拦下"。
        /// </summary>
        /// <param name="targetFile">归档目标文件。</param>
        /// <param name="overwriteExisting">调用方是否已获用户"覆盖"授权。</param>
        /// <returns>备份文件路径；目标不存在时返回 null。</returns>
        /// <exception cref="ToolException">未获覆盖授权时抛出（消息里含中文办法）。</exception>
        private string MoveExistingTargetAside(FileInfo targetFile, bool overwriteExisting)
        {
            targetFile.Refresh();
            if (!targetFile.Exists)
            {
                return null;
            }

            if (!overwriteExisting)
            {
                throw new ToolException(
                    ExitCodes.InputOutput,
                    "归档目标已存在，且没有选择“覆盖”：\r\n  " + targetFile.FullName + "\r\n"
                    + "TIA 的归档不会覆盖已存在的文件（实测报 “Archive Operation is not possible as the target "
                    + "file/folder … is already exist.”），所以这里直接拦下，不去做注定失败的调用。\r\n"
                    + "办法：换个文件名（加时间戳 / 后缀 / 序号），或在界面上选“覆盖”"
                    + "（覆盖会先把旧文件改名为 .old 备份，归档成功后才删除它）。");
            }

            string backupPath = targetFile.FullName + OverwriteBackupSuffix;
            if (File.Exists(backupPath))
            {
                // 上一次异常留下的备份：已经没有任何用处，先清掉再改名。
                _logger.Warning("发现残留的覆盖备份，将先删除：" + Path.GetFileName(backupPath));
                File.Delete(backupPath);
            }

            _logger.Info("目标已存在，先改名为备份：" + Path.GetFileName(backupPath));
            File.Move(targetFile.FullName, backupPath);
            return backupPath;
        }

        /// <summary>归档确认成功后丢弃覆盖备份（删不掉只告警，不阻断主流程）。</summary>
        /// <param name="backupPath">备份文件路径。</param>
        private void DiscardOverwriteBackup(string backupPath)
        {
            try
            {
                File.Delete(backupPath);
                _logger.Debug("已删除覆盖备份：" + backupPath);
            }
            catch (Exception ex)
            {
                _logger.Warning("覆盖备份删除失败（不影响本次归档，可手动删除）：" + backupPath + "：" + ex.Message);
            }
        }

        /// <summary>归档失败时恢复旧文件：清掉可能残留的半成品，再把备份改回原名。</summary>
        /// <param name="backupPath">备份文件路径。</param>
        /// <param name="targetFile">原目标文件。</param>
        private void RestoreOverwriteBackup(string backupPath, FileInfo targetFile)
        {
            try
            {
                if (targetFile.Exists)
                {
                    File.Delete(targetFile.FullName);
                }

                File.Move(backupPath, targetFile.FullName);
                _logger.Warning("归档未成功，已把旧文件恢复回原名：" + targetFile.Name);
            }
            catch (Exception ex)
            {
                _logger.Error("旧文件恢复失败，请手动处理：备份文件在 " + backupPath + "；原因：" + ex.Message);
            }
        }

        /// <summary>
        /// 从归档文件中检索（解包）出项目，返回已打开的 Project。
        ///
        /// 关键调用（一行，V21 真实 API）：
        ///   Project project = tiaPortal.Projects.Retrieve(new FileInfo(archiveFile), new DirectoryInfo(target));
        /// </summary>
        /// <param name="portal">TIA Portal 实例。</param>
        /// <param name="archiveFilePath">归档文件路径。</param>
        /// <param name="targetDirectoryPath">解包目标目录。</param>
        /// <param name="upgrade">true 时改用 RetrieveWithUpgrade。</param>
        /// <returns>解包并打开后的 Project（会话负责关闭）。</returns>
        public object RetrieveProject(object portal, string archiveFilePath, string targetDirectoryPath, bool upgrade)
        {
            if (portal == null)
            {
                throw new ToolException(ExitCodes.Api, "RetrieveProject 收到空的 TiaPortal 实例。");
            }

            FileInfo sourceFile = new FileInfo(Path.GetFullPath(archiveFilePath));
            if (!sourceFile.Exists)
            {
                throw new ToolException(ExitCodes.InputOutput, "归档文件不存在：" + sourceFile.FullName);
            }

            DirectoryInfo targetDirectory = new DirectoryInfo(Path.GetFullPath(targetDirectoryPath));
            if (!targetDirectory.Exists)
            {
                _logger.Info("目标目录不存在，自动创建：" + targetDirectory.FullName);
                targetDirectory.Create();
            }
            else if (targetDirectory.GetFileSystemInfos().Length > 0)
            {
                _logger.Warning("目标目录非空，若其中已存在同名项目，Retrieve 可能失败：" + targetDirectory.FullName);
            }

            _logger.Section("检索/解包（Retrieve）");
            _logger.Info("归档文件：" + sourceFile.FullName);
            _logger.Info("文件大小：" + SizeFormat.Format(sourceFile.Length));
            _logger.Info("解包目录：" + targetDirectory.FullName);
            _logger.Info("是否升级到当前版本：" + (upgrade ? "是（RetrieveWithUpgrade）" : "否（Retrieve）"));

            object project;
            try
            {
                OpennessApi api = OpennessApi.Current;
                using (_logger.Measure(upgrade ? "Projects.RetrieveWithUpgrade" : "Projects.Retrieve"))
                {
                    // ★ 核心调用 2-of-2：运行时反射调用 Projects.Retrieve / RetrieveWithUpgrade
                    project = api.RetrieveProject(
                        api.GetProjects(portal), sourceFile, targetDirectory, upgrade);
                }
            }
            catch (Exception ex)
            {
                throw Tia.TiaSession.WrapSiemensFailure(
                    ex,
                    "调用 Projects." + (upgrade ? "RetrieveWithUpgrade" : "Retrieve") + " 失败。");
            }

            if (project == null)
            {
                throw new ToolException(ExitCodes.Api, "Retrieve 返回了 null，未能还原项目。");
            }

            targetDirectory.Refresh();
            long restoredBytes = CountFiles(targetDirectory);
            _logger.Ok("检索完成，项目已在该实例中打开。");
            _logger.Info("解包后目录内容字节数：" + SizeFormat.Format(restoredBytes));

            return project;
        }

        /// <summary>
        /// 若输出文件名看起来不像 TIA 归档扩展名（.zapXX），给出提示（不阻断）。
        /// </summary>
        /// <param name="targetName">目标文件名。</param>
        private void WarnOnUnexpectedExtension(string targetName)
        {
            string extension = Path.GetExtension(targetName);
            if (string.IsNullOrEmpty(extension))
            {
                _logger.Warning("输出文件名没有扩展名，TIA 会按当前版本自动补一个（例如 .zap21）。");
                return;
            }

            if (!extension.StartsWith(ArchiveExtensionPrefix, StringComparison.OrdinalIgnoreCase))
            {
                _logger.Warning(
                    "输出文件扩展名是 '" + extension + "'，通常归档用 '.zap<版本号>'（如 .zap21）。");
                _logger.Warning("若使用 mode=none / discard-restorable，TIA 也会自动决定扩展名，本提示可忽略。");
            }
        }

        /// <summary>
        /// 对目录顶层文件做一次快照：全路径 -> 最后写入时间（UTC）。
        /// 用于归档前后做集合差，避免把目录里既有的无关文件误判成归档产物（P2-2）。
        /// </summary>
        /// <param name="directory">目录。</param>
        /// <returns>快照字典；目录不可读时返回空字典。</returns>
        private Dictionary<string, DateTime> CaptureSnapshot(DirectoryInfo directory)
        {
            Dictionary<string, DateTime> snapshot = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (FileInfo file in directory.GetFiles())
                {
                    snapshot[file.FullName] = file.LastWriteTimeUtc;
                }
            }
            catch (Exception ex)
            {
                // 快照失败不阻断主流程：退化到"只按文件名精确匹配"，由上层保证不误报。
                _logger.Debug("目录快照失败：" + ex.Message);
            }
            return snapshot;
        }

        /// <summary>
        /// 判断某个文件相对于归档前的快照是否属于"新增或被覆盖"。
        /// </summary>
        /// <param name="file">候选文件。</param>
        /// <param name="snapshotBefore">归档前快照。</param>
        /// <returns>是新增或内容/时间戳有变化返回 true。</returns>
        private static bool IsNewOrChanged(FileInfo file, Dictionary<string, DateTime> snapshotBefore)
        {
            DateTime previous;
            if (!snapshotBefore.TryGetValue(file.FullName, out previous))
            {
                return true;
            }
            return file.LastWriteTimeUtc != previous;
        }

        /// <summary>
        /// 定位归档产物。
        ///
        /// 为什么不能直接用 targetName 拼路径了事？
        ///   在 mode=none / discard-restorable 下，TIA 会按当前主版本自行决定扩展名
        ///   （用户传 demo.zip 也可能落地成 demo.zap21），所以必须按实际落地结果查找。
        ///
        /// 查找策略（P2-2 修正 + QA R2.4-B 收严）：
        ///   1) 精确名 —— 且必须是归档后新增或被覆盖的；
        ///   2) 主干名一致、仅扩展名不同的差集文件（TIA 改写扩展名的场景，如 demo.zip -> demo.zap21），
        ///      其中扩展名以 .zap 开头的优先；
        ///   3) 都没有 -> 返回 null，明确报"没能定位产物"。
        ///
        /// 注意：第 3 步**不再**退化成"差集里挑最新的"。那样在脏目录 / 并发写入场景下
        /// 会把别人刚写的文件当成归档产物，报出错误的文件名和大小 —— 宁可报错也不猜。
        /// </summary>
        /// <param name="directory">目标目录。</param>
        /// <param name="targetName">用户指定的目标文件名。</param>
        /// <param name="snapshotBefore">归档前的目录快照。</param>
        /// <returns>定位到的文件；无法确定时返回 null。</returns>
        private FileInfo LocateArchiveProduct(
            DirectoryInfo directory,
            string targetName,
            Dictionary<string, DateTime> snapshotBefore)
        {
            FileInfo[] after;
            try
            {
                after = directory.GetFiles();
            }
            catch (Exception)
            {
                return null;
            }

            List<FileInfo> changed = new List<FileInfo>();
            foreach (FileInfo file in after)
            {
                if (IsNewOrChanged(file, snapshotBefore))
                {
                    changed.Add(file);
                }
            }

            _logger.Debug("目录快照差：新增/变化 " + changed.Count + " 个文件（原有 "
                + snapshotBefore.Count + " 个，现有 " + after.Length + " 个）。");

            if (changed.Count == 0)
            {
                return null;
            }

            // 1) 精确名优先：用户明确指定了输出名，只要它确实是本次新产生/被覆盖的就优先认它。
            foreach (FileInfo file in changed)
            {
                if (string.Equals(file.Name, targetName, StringComparison.OrdinalIgnoreCase))
                {
                    return file;
                }
            }

            // 按最后写入时间倒序排好，后面各轮筛选直接取"最近写入的那个"。
            changed.Sort(delegate (FileInfo left, FileInfo right)
            {
                return right.LastWriteTimeUtc.CompareTo(left.LastWriteTimeUtc);
            });

            // 2) 主干名命中：TIA 按当前主版本改写扩展名时（demo.zip -> demo.zap21），
            //    主干名仍与用户给的一致。只在差集中找，脏目录里的同名旧文件不参与。
            string targetStem = Path.GetFileNameWithoutExtension(targetName);
            if (!string.IsNullOrEmpty(targetStem))
            {
                FileInfo stemFallback = null;
                foreach (FileInfo file in changed)
                {
                    string stem = Path.GetFileNameWithoutExtension(file.Name);
                    if (!string.Equals(stem, targetStem, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (file.Extension.StartsWith(ArchiveExtensionPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.Debug("精确名未命中，改按主干名定位到归档产物：" + file.Name);
                        return file;
                    }

                    if (stemFallback == null)
                    {
                        // 主干名一致但扩展名不是 .zap*：先记着，若没有更合适的就用它。
                        stemFallback = file;
                    }
                }

                if (stemFallback != null)
                {
                    _logger.Debug("精确名未命中，改按主干名定位（扩展名非 .zap*）：" + stemFallback.Name);
                    return stemFallback;
                }
            }

            // 3) 都不命中就明确放弃，交给上层报"没能定位产物"。
            _logger.Warning("未能定位归档产物：目标名 '" + targetName + "'，差集里有 " + changed.Count
                + " 个文件（" + DescribeNames(changed) + "），但没有一个能对上名字或主干名。");
            return null;
        }

        /// <summary>
        /// 把文件列表的文件名拼成一行，供排错日志使用（最多列 10 个，避免刷屏）。
        /// </summary>
        /// <param name="files">文件列表。</param>
        /// <returns>形如 "a.zap21、b.zap21、…共 13 个" 的字符串。</returns>
        private static string DescribeNames(List<FileInfo> files)
        {
            List<string> names = new List<string>();
            for (int i = 0; i < files.Count && i < 10; i++)
            {
                names.Add(files[i].Name);
            }

            if (files.Count > names.Count)
            {
                names.Add("…共 " + files.Count.ToString(CultureInfo.InvariantCulture) + " 个");
            }

            return string.Join("、", names.ToArray());
        }

        /// <summary>
        /// 递归统计目录下的文件总字节数。
        /// </summary>
        /// <param name="directory">目录。</param>
        /// <returns>字节数。</returns>
        private static long CountFiles(DirectoryInfo directory)
        {
            long total = 0;
            try
            {
                foreach (FileInfo file in directory.GetFiles())
                {
                    total += file.Length;
                }
                foreach (DirectoryInfo sub in directory.GetDirectories())
                {
                    total += CountFiles(sub);
                }
            }
            catch (Exception)
            {
                // 统计失败不影响主流程。
            }
            return total;
        }

        /// <summary>
        /// 把 TimeSpan 格式化为中文秒数。
        /// </summary>
        /// <param name="elapsed">时间间隔。</param>
        /// <returns>例如 "3.21 秒"。</returns>
        private static string FormatElapsed(TimeSpan elapsed)
        {
            return elapsed.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) + " 秒";
        }
    }
}
