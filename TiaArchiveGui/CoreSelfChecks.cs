using System;
using System.Collections.Generic;
using TiaOpennessKit;

namespace TiaArchiveGui
{
    /// <summary>
    /// 核心规则**纯逻辑**自检的结果。
    /// </summary>
    internal sealed class CoreSelfCheckResult
    {
        /// <summary>构造：初始化集合，调用方不必判空。</summary>
        public CoreSelfCheckResult()
        {
            Problems = new List<string>();
            HintBoth = string.Empty;
        }

        /// <summary>不通过的项（空表示全部通过）。</summary>
        public List<string> Problems { get; private set; }

        /// <summary>实际跑过的断言条数（报告里用来讲清"验了多少项"）。</summary>
        public int CheckCount { get; set; }

        /// <summary>两个规则都启用时的提示文本样本（报告里显示它，便于人工确认措辞）。</summary>
        public string HintBoth { get; set; }

        /// <summary>是否全部通过。</summary>
        public bool AllPassed
        {
            get { return Problems.Count == 0; }
        }
    }

    /// <summary>
    /// 与界面无关的**核心规则自检**：项目文件识别、归档输出路径推导、文件名规则、批量页提示文本。
    ///
    /// 为什么要从 <c>MainForm.UiSelfCheck</c> 里搬出来：
    ///   这些断言只用到内核里的纯函数（BatchScanner / ArchiveNaming），**不需要窗口、不需要控件**，
    ///   可原先它们写在 UI 类的自检方法里，于是想验证它们就必须先把窗体建起来 ——
    ///   而在"这台机器没装 Openness / 环境探测失败"时，界面自检根本走不到这些断言，
    ///   规则本身的问题就会被环境问题掩掉。搬到这里之后，无头模式（--selftest）也能跑同一份。
    ///
    /// 界面相关的断言（控件是否 new、控件树实测尺寸、文字会不会被裁）仍然留在 UiSelfCheck 里 ——
    /// 那些确实必须有窗口才能验。
    ///
    /// 每一条断言后面都留着"当初为什么加它"的记录（都对应一个真实踩过的坑），改动时别把注释删了。
    /// </summary>
    internal static class CoreSelfChecks
    {
        /// <summary>
        /// 跑一遍全部核心规则断言。
        /// </summary>
        /// <returns>结果（含不通过项、断言条数、提示文本样本）。</returns>
        public static CoreSelfCheckResult Run()
        {
            CoreSelfCheckResult result = new CoreSelfCheckResult();

            // ── ① 项目文件识别：什么算 TIA 项目、什么必须排除（.zapXX 是归档产物，不是项目）
            Check(result,
                BatchScanner.IsTiaProjectFile(@"C:\Proj\Demo\Demo.ap21")
                && BatchScanner.IsTiaProjectFile(@"C:\Proj\Old\Old.ap18")
                && !BatchScanner.IsTiaProjectFile(@"C:\Bak\Demo.zap21")
                && !BatchScanner.IsTiaProjectFile(@"C:\Proj\Demo\Demo.ap_bak")
                && !BatchScanner.IsTiaProjectFile(@"C:\Proj\readme.txt")
                && !BatchScanner.IsTiaProjectFile(@"C:\Proj\data.apx"),
                "项目文件识别规则异常（.ap21 应被识别，.zap21 / .ap_bak / .txt / .apx 应被排除）");

            // ── ② 归档输出路径：扩展名必须按**内核版本**推导（归档格式由内核决定），不能写死 .zap21。
            //   真实故障：在装 V19 的机器上归档 .ap19 项目，产物被命名成 .zap21（内容其实是 V19 格式）。
            string builtPath = BatchScanner.BuildArchivePath(@"D:\Bak", @"C:\Proj\Demo\Demo.ap21", 21);
            // 内核 19 归档 .ap19 → .zap19（用户实测抓到的那个 bug 的回归断言）
            string built19 = BatchScanner.BuildArchivePath(@"D:\Bak", @"C:\Proj\Demo\Demo.ap19", 19);
            // 内核 21 + .ap19 项目（升级打开）→ 产物是 V21 格式，所以叫 .zap21
            string builtUpgrade = BatchScanner.BuildArchivePath(@"D:\Bak", @"C:\Proj\Demo\Demo.ap19", 21);
            // 内核版本未知（还没探测环境）→ 退回项目自身版本，而不是瞎写 21
            string builtFallback = BatchScanner.BuildArchivePath(@"D:\Bak", @"C:\Proj\Demo\Demo.ap19", 0);

            Check(result,
                string.Equals(builtPath, @"D:\Bak\Demo.zap21", StringComparison.OrdinalIgnoreCase)
                && string.Equals(built19, @"D:\Bak\Demo.zap19", StringComparison.OrdinalIgnoreCase)
                && string.Equals(builtUpgrade, @"D:\Bak\Demo.zap21", StringComparison.OrdinalIgnoreCase)
                && string.Equals(builtFallback, @"D:\Bak\Demo.zap19", StringComparison.OrdinalIgnoreCase),
                "批量归档输出路径推导异常："
                + builtPath + " / " + built19 + " / " + builtUpgrade + " / " + builtFallback);

            // ── ②之一 归档目标体检：归档产物不能落在项目自身目录
            //   真实故障（用户实测，V19）：默认建议路径把 .zapXX 放到 .apXX 旁边，
            //   TIA 直接拒绝：“Unable to archive the project. / Archiving failed. /
            //   项目目录已存在，无法保存。请选择一个不同的路径。”
            //   （同一个项目换到别的输出目录、或走批量归档另选输出目录，都一次成功。）
            //   所以这条必须在启动 TIA 之前就拦住，而不是让用户白等几十秒再拿一句英文异常。
            bool targetInsideBlocked = false;
            try
            {
                CoreRunner.EnsureArchiveTargetUsable(null, @"C:\Proj\Demo\Demo.ap21", @"C:\Proj\Demo\Demo.zap21");
            }
            catch (ToolException)
            {
                targetInsideBlocked = true;
            }

            Check(result, targetInsideBlocked,
                "归档目标体检没有拦下“归档到项目自身目录”的组合（V19 实测必失败）");

            // 子目录同样失败（V21 实测复现“项目目录已存在”），所以也必须拦下 ——
            // 只拦“完全相同”是不够的。
            bool targetSubBlocked = false;
            try
            {
                CoreRunner.EnsureArchiveTargetUsable(
                    null, @"C:\Proj\Demo\Demo.ap21", @"C:\Proj\Demo\bak\Demo.zap21");
            }
            catch (ToolException)
            {
                targetSubBlocked = true;
            }

            Check(result, targetSubBlocked,
                "归档目标体检没有拦下“归档到项目目录的子目录”的组合（V21 实测同样失败）");

            string targetOutsideError = null;
            try
            {
                CoreRunner.EnsureArchiveTargetUsable(null, @"C:\Proj\Demo\Demo.ap21", @"C:\Proj\Demo.zap21");
            }
            catch (ToolException ex)
            {
                targetOutsideError = ex.Message;
            }

            Check(result, targetOutsideError == null,
                "归档目标体检误拦了项目目录之外的路径：" + (targetOutsideError ?? string.Empty));

            // ②之二 归档输出可用性体检：目标是**不存在**的路径时不得误拦
            //   （目标真实存在时必须拦下并给中文提示 —— 那一条靠真机实验守着，这里只守"正常路径不被误伤"。）
            string outputGuardError = null;
            try
            {
                CoreRunner.EnsureArchiveOutputAvailable(
                    null, @"C:\tia-archive-selfcheck-not-exist\Demo.zap21", false);
            }
            catch (ToolException ex)
            {
                outputGuardError = ex.Message;
            }

            Check(result, outputGuardError == null,
                "归档输出可用性体检误拦了不存在的目标：" + (outputGuardError ?? string.Empty));

            // ── ③ 文件名规则（自定义后缀 + 日期时间戳）
            DateTime sampleTime = new DateTime(2026, 9, 19, 11, 19, 15);

            string namingBoth = ArchiveNaming.ApplyRule(
                @"D:\Bak\Demo.zap21", "backup", "yyyyMMdd_HHmmss", sampleTime);
            Check(result,
                string.Equals(namingBoth, @"D:\Bak\Demo_backup_20260919_111915.zap21",
                    StringComparison.OrdinalIgnoreCase),
                "后缀+时间戳组合计算异常：" + namingBoth);

            string namingTimestampOnly = ArchiveNaming.ApplyRule(
                @"D:\Bak\Demo.zap21", null, "yyyyMMdd", sampleTime);
            Check(result,
                string.Equals(namingTimestampOnly, @"D:\Bak\Demo_20260919.zap21",
                    StringComparison.OrdinalIgnoreCase),
                "仅加时间戳计算异常：" + namingTimestampOnly);

            string suffixAutoUnderscore = ArchiveNaming.NormalizeSuffix("bak");
            Check(result,
                string.Equals(suffixAutoUnderscore, "_bak", StringComparison.Ordinal),
                "后缀自动补下划线异常：" + suffixAutoUnderscore);

            string suffixSanitized = ArchiveNaming.NormalizeSuffix("a:b*c?d");
            Check(result,
                suffixSanitized.IndexOf(':') < 0
                && suffixSanitized.IndexOf('*') < 0
                && suffixSanitized.IndexOf('?') < 0,
                "后缀非法字符过滤异常：" + suffixSanitized);

            // 不加规则时必须原样返回（否则会悄悄改动用户的文件名）
            string namingDisabled = ArchiveNaming.ApplyRule(@"D:\Bak\Demo.zap21", null, null, sampleTime);
            Check(result,
                string.Equals(namingDisabled, @"D:\Bak\Demo.zap21", StringComparison.OrdinalIgnoreCase),
                "未启用规则时文件名被改动了：" + namingDisabled);

            // ── ④ 批量页"文件名规则 = ..."提示的**内容完整性**
            //   用户反馈过"两个规则都勾上时提示只显示到『规则 = 时』" —— 那是宽度被截断，
            //   由界面自检量宽度守着；这里守的是内容本身拼得对不对。
            string hintBoth = MainForm.BuildNamingRuleSummary("bak", "yyyyMMdd_HHmmss");
            result.HintBoth = hintBoth;
            Check(result,
                hintBoth.IndexOf("后缀 _bak", StringComparison.Ordinal) >= 0
                && hintBoth.IndexOf("时间戳 yyyyMMdd_HHmmss", StringComparison.Ordinal) >= 0
                && hintBoth.IndexOf(" + ", StringComparison.Ordinal) >= 0,
                "批量页规则提示内容不完整：" + hintBoth);

            string hintNone = MainForm.BuildNamingRuleSummary(null, null);
            Check(result,
                hintNone.IndexOf("无", StringComparison.Ordinal) >= 0,
                "无规则时的提示异常：" + hintNone);

            return result;
        }

        /// <summary>
        /// 记一条断言：通过就只计数，不通过把原因收进结果。
        /// </summary>
        /// <param name="result">结果收集器。</param>
        /// <param name="ok">是否通过。</param>
        /// <param name="failureMessage">不通过时的说明（含实测值，便于排错）。</param>
        private static void Check(CoreSelfCheckResult result, bool ok, string failureMessage)
        {
            result.CheckCount++;
            if (!ok)
            {
                result.Problems.Add(failureMessage);
            }
        }
    }
}