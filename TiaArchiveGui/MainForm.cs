using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Windows.Forms;
using TiaOpennessKit;
using TiaOpennessKit.Cli;
using TiaOpennessKit.Tia;

namespace TiaArchiveGui
{
    /// <summary>
    /// 主窗口：三个页签（归档项目 / 恢复项目 / 环境探测）+ 运行日志 + 状态栏。
    ///
    /// 界面层只做三件事：
    ///   1) 收集用户输入并做基本校验；
    ///   2) 把任务丢到后台线程去执行（否则 TIA 启动的几十秒里窗口会卡死）；
    ///   3) 把日志显示出来。
    /// 真正的归档/检索/探测逻辑全部复用命令行版那套已经验证过的代码，不在这里重写。
    ///
    /// 布局说明（这块改过一版，值得记一笔）：
    ///   最早用 TableLayoutPanel + AutoSize 拼页面，结果卡片宽度没被撑开，
    ///   输入框被挤成几十像素、按钮文字全被截断 —— 光看代码完全看不出来。
    ///   现在改成：每个页签一个 TableLayoutPanel（列固定 100% + 显式行高），
    ///   每张卡片 Dock=Fill 撑满单元格，卡片内部再用"固定行高 40 的 3 列网格"。
    ///   所有尺寸都是算得出来的，不依赖 AutoSize 的推测。
    /// </summary>
    internal partial class MainForm : Form
    {
        private const string ToolTitle = "TIA Portal 归档助手";
        // 1.3.1：修高分屏（175% 缩放）下的文字被裁 —— 打开按 DPI 自动缩放，
        //        并修好"打包模式"提示条被压成一条的问题。
        private const string ToolVersion = "1.3.1";

        // 署名：日志开头、状态栏、彩蛋三处都用它，别在各处再写一遍字面量
        private const string Author = "by Decjlbj";

        // 卡片内部网格的行高与各列宽度（集中在这里，改一处全界面统一）
        private const int RowHeight = 40;
        private const int LabelWidth = 112;
        private const int ButtonWidth = 132;
        private const int CardPadding = 24;

        // 每张卡片所占的行高 = 行数 × RowHeight + 卡片内边距 + 卡片底部外间距
        // （卡片本身 Dock=Fill，所以行高必须把 Margin 一并算进去，否则最后一行会被裁掉）
        private const int CardThreeRows = 3 * RowHeight + CardPadding + 8;   // 152
        private const int CardTwoRows = 2 * RowHeight + CardPadding + 8;     // 112
        private const int CardFiveRows = 5 * RowHeight + CardPadding + 8;    // 232

        // ── 归档页控件
        private TextBox _txtProject;
        private TextBox _txtArchiveOut;
        private ComboBox _cmbMode;
        private CheckBox _chkSaveFirst;
        private CheckBox _chkKeepOpen;
        private CheckBox _chkUpgradeArchive;
        // "归档到"当前是不是我们自动建议的（用户没自己改过）。换项目时据此决定要不要跟着更新
        private bool _archiveOutUserChosen;
        // 程序内部改写输入框时用它抑制 TextChanged 里的"用户改过"判定
        private bool _suppressNamingEvents;
        private RadioButton _rbArchiveNoUi;
        private RadioButton _rbArchiveUi;
        private RadioButton _rbArchiveAttach;
        private Label _lblModeWarning;
        private Button _btnStartArchive;

        // ── 归档页：文件名规则（自定义后缀 + 日期时间戳）
        private CheckBox _chkAppendTimestamp;
        private ComboBox _cmbTimestampFormat;
        private CheckBox _chkCustomSuffix;
        private TextBox _txtSuffix;
        private Label _lblOutputPreview;
        private ToolTip _toolTip;

        // ── 恢复页控件
        private TextBox _txtArchiveFile;
        private TextBox _txtRetrieveTarget;
        private CheckBox _chkUpgrade;
        private CheckBox _chkSaveAfterRetrieve;
        private CheckBox _chkKeepOpenRetrieve;
        private RadioButton _rbRetrieveNoUi;
        private RadioButton _rbRetrieveUi;
        private RadioButton _rbRetrieveAttach;
        private Button _btnStartRetrieve;

        // ── 探测页控件
        private ComboBox _cmbApiVersion;
        private TextBox _txtApiDir;
        private TextBox _txtAssemblyFilter;
        private CheckBox _chkVerbose;
        private Button _btnStartProbe;
        private Button _btnCheckEnvironment;
        private RichTextBox _rtbCheckResult;

        // ── 可用 Openness 版本（由后台枚举填充）
        private List<TiaEnvironmentInfo> _availableVersions = new List<TiaEnvironmentInfo>();
        private bool _suppressVersionEvents;

        // ── 公共控件
        private TabControl _tabs;
        private RichTextBox _rtbLog;
        private CheckBox _chkAutoScroll;
        private Label _lblStatus;
        private Label _lblEnvironment;
        private ProgressBar _progress;
        private GuiSettings _settings;
        private bool _busy;

        /// <summary>
        /// 构造主窗口。
        /// </summary>
        public MainForm()
        {
            _settings = GuiSettings.Load();
            InitializeComponent();
            LoadSettingsIntoUi();
            LoadBatchSettingsIntoUi();
        }

        // ═══════════════════════════════════════════════════════════════════
        //  界面骨架
        // ═══════════════════════════════════════════════════════════════════

        private void InitializeComponent()
        {
            SuspendLayout();

            // ★ 高分屏的总开关（实测踩过，一定要在这里、建控件之前设好）
            //
            //   本文件里所有尺寸（行高 40、输入框 26、列宽 56、卡片 232……）都是按
            //   96 DPI（Windows 100% 缩放）的像素写的。而程序在 app.manifest 里声明了
            //   PerMonitorV2 DPI 感知（清单已编译进 exe，发布时不需要额外文件），
            //   于是 168 DPI（175% 缩放）的屏幕上文字会按
            //   1.75 倍像素绘制 —— 布局若还停在 1 倍，就成了"字大框小"，
            //   到处裁字：批量页表头"归档"被裁成"归"、时间戳格式只见"yyyyMMdd HHr"、
            //   模式提示条被压成一条。打开按 DPI 自动缩放后，控件尺寸跟着 DPI 一起放大，
            //   与文字的相对比例回到 100% 时的样子，也就不会再裁字。
            //
            //   基准写 96：这样在 100% 的机器上缩放比例正好是 1，界面与过去完全一致。
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;

            Text = ToolTitle + " v" + ToolVersion;
            StartPosition = FormStartPosition.CenterScreen;
            // 高度给到 820：归档页在"打包模式"下会多一条三行提示，760 高时会把
            // 内容挤到刚好看不全（多出滚动条），加 60px 刚好余出富余。
            ClientSize = new Size(1020, 820);
            MinimumSize = new Size(920, 780);
            BackColor = UiTheme.Background;
            Font = UiTheme.Body;
            AllowDrop = true;

            Panel header = BuildHeader();
            StatusStrip status = BuildStatusBar();
            SplitContainer split = BuildSplit();

            Controls.Add(split);
            Controls.Add(header);
            Controls.Add(status);

            // Dock 的堆叠顺序按 Controls 集合的相反顺序处理：集合里靠后的先贴边，
            // 索引 0 的最后布局。split 是第一个加进来的（索引 0），所以它最后 Dock，
            // 正好占满 header/status 之下的剩余区域 —— 顺序不能动。

            // 彩蛋（低调版）：启动日志最前面加一行作者署名，混在日志里不显眼
            AppendLog(LogLevel.Info, "[INFO]", "TiaArchiveGui v" + ToolVersion + " · " + Author);

            FormClosing += OnFormClosing;
            // 窗口显示后在后台枚举本机可用的 Openness 版本，填充"环境探测"页的下拉。
            // 顺便把批量列表的列宽按真实 DPI 换算一次（必须等句柄建好，见 ApplyGridDpiScale）。
            Shown += delegate
            {
                FitWindowToScreen();
                ApplyGridDpiScale();
                StartVersionEnumeration();
            };
            DragEnter += OnDragEnterAny;
            DragDrop += OnDragDropAny;

            // 悬停提示：把详细说明放这儿，界面文字就能保持简短
            _toolTip = new ToolTip();
            _toolTip.AutoPopDelay = 20000;
            _toolTip.SetToolTip(_chkSaveFirst,
                "归档前先执行 Project.Save()。项目存在未保存修改时归档可能失败，建议保持勾选。");
            _toolTip.SetToolTip(_chkKeepOpen,
                "不勾选则归档结束后关闭项目。附加到已运行实例时默认保持打开。");
            _toolTip.SetToolTip(_chkUpgradeArchive,
                "旧版本项目（.ap18 / .ap19 等）必须勾选才能打开；会把项目升级到本机 TIA 版本，不可回退。");
            _toolTip.SetToolTip(_chkAppendTimestamp,
                "在归档文件名里加入日期时间，每次归档生成新文件，不会覆盖历史备份。");
            _toolTip.SetToolTip(_cmbTimestampFormat,
                "时间戳格式。左边是示例，右边是 .NET 格式串。");
            _toolTip.SetToolTip(_chkCustomSuffix,
                "在文件名里加自定义后缀。例如填 backup，输出会变成 Demo_backup_20260919_111915.zap19"
                + "（扩展名随所用 TIA 内核版本，19 内核就是 .zap19）。");
            _toolTip.SetToolTip(_lblOutputPreview,
                "按当前规则加工后的最终文件名。归档时若该文件已存在，会问你是覆盖还是自动加序号。");
            _toolTip.SetToolTip(_lblBatchHint,
                "TIA 实例方式与文件名规则都沿用“归档项目”页的设置。"
                + "其中时间戳显示的是格式串，实际归档时按那一刻的时间生成。");
            _toolTip.SetToolTip(_btnBatchToggleSettings,
                "收起上面的“项目文件夹 / 归档设置”两张卡片，把高度让给下面的项目列表"
                + "（项目多的时候能多显示 6 行左右）。收起后这个按钮会变成“展开设置”，再点一下即可展开。");
            _toolTip.SetToolTip(_lblBatchSummary,
                "每个项目会生成一个与原项目同名的归档文件，扩展名随所用 TIA 内核版本（如 .zap19 / .zap21）。");

            ResumeLayout(false);
        }

        /// <summary>
        /// 把窗口居中摆好，并保证它完整落在屏幕工作区内。
        ///
        /// 为什么必须自己做（实测踩过）：`StartPosition = CenterScreen` 的位置是在**构造时**
        /// 按当时的尺寸算出来的，而"按 DPI 自动缩放"要到句柄创建之后才把窗口放大（本机 1.75 倍）——
        /// 位置没跟着重算，窗口就被顶出屏幕：实测 175% 缩放的机器上窗口跑到 top=416、
        /// 右下角超出工作区 200px，日志区和底部按钮直接被切到屏幕外面看不见。
        /// </summary>
        private void FitWindowToScreen()
        {
            Rectangle work = Screen.FromControl(this).WorkingArea;

            // ① 先确保放得下：比工作区还大就缩到工作区（留 40px 边距；但不小于最小尺寸）
            int maxWidth = Math.Max(MinimumSize.Width, work.Width - 40);
            int maxHeight = Math.Max(MinimumSize.Height, work.Height - 40);
            if (Width > maxWidth || Height > maxHeight)
            {
                Size = new Size(Math.Min(Width, maxWidth), Math.Min(Height, maxHeight));
            }

            // ② 再居中；居中后仍可能压边（例如窗口比工作区还大），最后夹一次位置
            int left = work.Left + (work.Width - Width) / 2;
            int top = work.Top + (work.Height - Height) / 2;
            left = Math.Max(work.Left, Math.Min(left, work.Right - Width));
            top = Math.Max(work.Top, Math.Min(top, work.Bottom - Height));

            Location = new Point(left, top);
        }

        private SplitContainer BuildSplit()
        {
            SplitContainer split = new SplitContainer();

            // ★ 顺序有讲究（实测踩过）：SplitContainer 要求
            //   Panel1MinSize ≤ SplitterDistance ≤ 高度 - Panel2MinSize 三者自洽，
            //   而刚 new 出来的 SplitContainer 只有 150×100 大小，
            //   此时若先设 Panel1MinSize = 220，setter 会直接抛
            //   InvalidOperationException，窗口连构造都完不成。
            //   所以：先摆正尺寸 → 再设分栏参数 → 最后 Dock 填满。
            split.Orientation = Orientation.Horizontal;
            split.Size = new Size(1020, 700);
            split.BackColor = UiTheme.Border;
            split.SplitterWidth = 6;
            split.Panel1MinSize = 220;
            // 日志面板最小 90：上面那半（页签区）要放得下归档页与批量页 ——
            // 归档页在"打包模式"下最高（多三行提示条），实测需要 540 左右；
            // 批量页的项目列表要靠这一半的剩余高度，所以给了 572（日志仍剩 132）。
            split.Panel2MinSize = 90;
            split.SplitterDistance = 572;

            split.Panel1.BackColor = UiTheme.Background;
            split.Panel2.BackColor = UiTheme.Background;
            split.Panel1.Padding = new Padding(10, 8, 10, 4);
            split.Panel2.Padding = new Padding(10, 4, 10, 8);

            _tabs = BuildTabs();
            split.Panel1.Controls.Add(_tabs);

            split.Panel2.Controls.Add(BuildLogPanel());

            split.Dock = DockStyle.Fill;
            return split;
        }

        private Panel BuildHeader()
        {
            Panel header = new Panel();
            header.Dock = DockStyle.Top;
            header.Height = 88;
            header.BackColor = UiTheme.HeaderBack;

            Label title = new Label();
            title.Text = ToolTitle;
            title.Font = UiTheme.Title;
            title.ForeColor = UiTheme.TextOnDark;
            title.AutoSize = true;
            title.Location = new Point(18, 12);
            header.Controls.Add(title);

            Label subtitle = new Label();
            subtitle.Text = "基于 Siemens TIA Portal Openness API · 项目归档 / 恢复 / 环境探测";
            subtitle.Font = UiTheme.Subtitle;
            subtitle.ForeColor = Color.FromArgb(0xB8, 0xCF, 0xD6);
            subtitle.AutoSize = true;
            subtitle.Location = new Point(20, 46);
            header.Controls.Add(subtitle);

            Label version = new Label();
            version.Text = "v" + ToolVersion + "   .NET Framework 4.8";
            version.Font = UiTheme.Small;
            version.ForeColor = Color.FromArgb(0x8F, 0xAB, 0xB4);
            version.AutoSize = true;
            header.Controls.Add(version);

            header.Resize += delegate
            {
                version.Location = new Point(
                    Math.Max(20, header.ClientSize.Width - version.Width - UiTheme.Scale(header, 18)),
                    UiTheme.Scale(header, 32));
            };

            return header;
        }

        private StatusStrip BuildStatusBar()
        {
            StatusStrip status = new StatusStrip();
            status.BackColor = Color.FromArgb(0xE9, 0xEE, 0xF2);
            status.SizingGrip = false;

            _lblStatus = new Label();
            _lblStatus.Text = "就绪";
            _lblStatus.AutoSize = true;
            _lblStatus.Font = UiTheme.Body;
            _lblStatus.ForeColor = UiTheme.TextPrimary;
            _lblStatus.Padding = new Padding(8, 4, 0, 0);

            _progress = new ProgressBar();
            _progress.Style = ProgressBarStyle.Marquee;
            _progress.MarqueeAnimationSpeed = 28;
            _progress.Width = 120;
            _progress.Height = 16;
            _progress.Visible = false;

            _lblEnvironment = new Label();
            _lblEnvironment.Text = string.Empty;
            _lblEnvironment.AutoSize = true;
            _lblEnvironment.Font = UiTheme.Small;
            _lblEnvironment.ForeColor = UiTheme.TextSecondary;
            _lblEnvironment.Padding = new Padding(0, 5, 8, 0);

            ToolStripStatusLabel spacer = new ToolStripStatusLabel();
            spacer.Spring = true;

            ToolStripControlHost hostStatus = new ToolStripControlHost(_lblStatus);
            ToolStripControlHost hostProgress = new ToolStripControlHost(_progress);
            ToolStripControlHost hostEnvironment = new ToolStripControlHost(_lblEnvironment);
            hostEnvironment.Alignment = ToolStripItemAlignment.Right;

            // ── 彩蛋：右下角一行几乎看不清的小字。连点三下会有小惊喜（见下面的 Click 处理）。 ──
            ToolStripStatusLabel author = new ToolStripStatusLabel(Author);
            author.Font = new Font(UiTheme.Small, FontStyle.Regular);
            author.ForeColor = Color.FromArgb(0xB0, 0xB8, 0xC0);   // 淡灰，不抢眼
            author.Margin = new Padding(6, 0, 10, 0);
            author.Alignment = ToolStripItemAlignment.Right;
            int authorClicks = 0;
            author.Click += delegate
            {
                authorClicks++;
                if (authorClicks >= 3)
                {
                    authorClicks = 0;
                    MessageBox.Show(
                        "🥚 彩蛋解锁：" + Author + "\r\n\r\n感谢你的使用与一路耐心的反馈。",
                        ToolTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            };

            status.Items.Add(hostStatus);
            status.Items.Add(hostProgress);
            status.Items.Add(spacer);
            status.Items.Add(author);
            status.Items.Add(hostEnvironment);

            return status;
        }

        private TabControl BuildTabs()
        {
            TabControl tabs = new TabControl();
            tabs.Dock = DockStyle.Fill;
            tabs.Font = UiTheme.Body;
            tabs.Padding = new Point(18, 6);

            TabPage pageArchive = new TabPage("  归档项目  ");
            pageArchive.BackColor = UiTheme.Background;
            pageArchive.Controls.Add(BuildArchivePage());

            TabPage pageRetrieve = new TabPage("  恢复项目  ");
            pageRetrieve.BackColor = UiTheme.Background;
            pageRetrieve.Controls.Add(BuildRetrievePage());

            TabPage pageProbe = new TabPage("  环境探测  ");
            pageProbe.BackColor = UiTheme.Background;
            pageProbe.Controls.Add(BuildProbePage());

            TabPage pageBatch = new TabPage("  批量归档  ");
            pageBatch.BackColor = UiTheme.Background;
            pageBatch.Controls.Add(BuildBatchPage());

            tabs.TabPages.Add(pageArchive);
            tabs.TabPages.Add(pageRetrieve);
            tabs.TabPages.Add(pageProbe);
            tabs.TabPages.Add(pageBatch);

            return tabs;
        }

        // ═══════════════════════════════════════════════════════════════════
        //  页签 1：归档
        // ═══════════════════════════════════════════════════════════════════

        private Control BuildArchivePage()
        {
            // 行高：文件卡片 / 模式提示（行高由 LayoutModeWarning() 按文字实测算出）/
            //       选项卡片 / 操作行 / 剩余空白
            TableLayoutPanel root = NewPageLayout(CardFiveRows, 0, 152, 48, -1);

            Panel cardFiles = UiTheme.CreateCard();
            TableLayoutPanel grid = NewGrid(5);
            grid.Controls.Add(NewGridLabel("项目文件"), 0, 0);
            _txtProject = NewTextBox();
            _txtProject.TextChanged += OnNamingRuleChanged;
            grid.Controls.Add(_txtProject, 1, 0);
            grid.Controls.Add(NewBrowseButton("浏览", OnBrowseProject), 2, 0);

            grid.Controls.Add(NewGridLabel("归档到"), 0, 1);
            _txtArchiveOut = NewTextBox();
            _txtArchiveOut.TextChanged += OnArchiveOutEdited;
            grid.Controls.Add(_txtArchiveOut, 1, 1);
            grid.Controls.Add(NewBrowseButton("另存为", OnBrowseArchiveOut), 2, 1);

            grid.Controls.Add(NewGridLabel("归档模式"), 0, 2);
            _cmbMode = new ComboBox();
            _cmbMode.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbMode.Font = UiTheme.Body;
            _cmbMode.Dock = DockStyle.Fill;
            _cmbMode.Margin = new Padding(0, 7, 8, 7);
            _cmbMode.Items.Add(new ModeItem("压缩（推荐）", "compressed", false));
            _cmbMode.Items.Add(new ModeItem("不压缩（类似“另存为”）", "none", false));
            _cmbMode.Items.Add(new ModeItem("丢弃可恢复数据（不可逆，慎用）", "discard-restorable", true));
            _cmbMode.Items.Add(new ModeItem("丢弃可恢复数据并压缩（不可逆，慎用）", "discard-restorable-compressed", true));
            _cmbMode.Items.Add(new ModeItem("打包项目文件夹（.zip，不需要 Openness）", "pack", false, true));
            _cmbMode.SelectedIndex = 0;
            _cmbMode.SelectedIndexChanged += OnModeChanged;
            grid.Controls.Add(_cmbMode, 1, 2);

            // 行 3：文件名规则（自定义后缀 + 日期时间戳）
            grid.Controls.Add(NewGridLabel("文件名规则"), 0, 3);
            FlowLayoutPanel namingRow = NewHorizontalFlow();
            namingRow.Margin = new Padding(0, 6, 0, 6);

            _chkAppendTimestamp = NewCheckBox("日期时间", true);
            _chkAppendTimestamp.CheckedChanged += OnNamingRuleChanged;
            namingRow.Controls.Add(_chkAppendTimestamp);

            _cmbTimestampFormat = new ComboBox();
            _cmbTimestampFormat.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbTimestampFormat.Font = UiTheme.Body;
            _cmbTimestampFormat.Width = 400;
            _cmbTimestampFormat.Margin = new Padding(0, 0, 16, 0);
            foreach (string preset in ArchiveNaming.TimestampPresets)
            {
                _cmbTimestampFormat.Items.Add(new TimestampItem(preset));
            }
            _cmbTimestampFormat.SelectedIndex = 0;
            _cmbTimestampFormat.SelectedIndexChanged += OnNamingRuleChanged;
            namingRow.Controls.Add(_cmbTimestampFormat);

            _chkCustomSuffix = NewCheckBox("自定义后缀", false);
            _chkCustomSuffix.CheckedChanged += OnNamingRuleChanged;
            namingRow.Controls.Add(_chkCustomSuffix);

            _txtSuffix = new TextBox();
            UiTheme.StyleTextBox(_txtSuffix);
            _txtSuffix.Width = 150;
            _txtSuffix.Enabled = false;
            _txtSuffix.Margin = new Padding(0, 2, 0, 2);
            _txtSuffix.TextChanged += OnNamingRuleChanged;
            namingRow.Controls.Add(_txtSuffix);

            grid.Controls.Add(namingRow, 1, 3);
            grid.SetColumnSpan(namingRow, 2);

            // 行 4：实时预览 —— 让用户直接看到最终会生成什么文件名，不用归档完再去目录里找
            grid.Controls.Add(NewGridLabel("实际输出"), 0, 4);
            _lblOutputPreview = new Label();
            _lblOutputPreview.Font = UiTheme.Small;
            _lblOutputPreview.ForeColor = UiTheme.Accent;
            _lblOutputPreview.AutoSize = true;
            _lblOutputPreview.Anchor = AnchorStyles.Left;
            _lblOutputPreview.Margin = new Padding(0, 0, 6, 0);
            _lblOutputPreview.Text = "（选择项目文件和输出路径后显示）";
            grid.Controls.Add(_lblOutputPreview, 1, 4);
            grid.SetColumnSpan(_lblOutputPreview, 2);

            cardFiles.Controls.Add(grid);
            root.Controls.Add(cardFiles, 0, 0);

            // 危险模式提示条：默认隐藏，隐藏时行高由 LayoutModeWarning() 压成 0
            _lblModeWarning = new Label();
            _lblModeWarning.Font = UiTheme.Body;
            _lblModeWarning.ForeColor = UiTheme.Danger;
            _lblModeWarning.BackColor = UiTheme.DangerBack;
            _lblModeWarning.Padding = new Padding(10, 8, 10, 8);
            _lblModeWarning.Dock = DockStyle.Fill;
            _lblModeWarning.Margin = new Padding(0, 0, 0, 8);
            _lblModeWarning.Visible = false;
            root.Controls.Add(_lblModeWarning, 0, 1);

            // 文字换行数随宽度变，窗口一改大小就得重新算这一行的高度
            root.SizeChanged += delegate { LayoutModeWarning(); };
            LayoutModeWarning();

            Panel cardOptions = UiTheme.CreateCard();
            FlowLayoutPanel options = NewVerticalFlow();

            // 三个选项横排 —— 原来竖排要占三行，横排省下的高度刚好给上面的文件名规则区
            // （详细说明放到悬停提示里，不占界面空间）
            FlowLayoutPanel optionRow = NewHorizontalFlow();
            optionRow.Margin = new Padding(0, 3, 0, 3);
            _chkSaveFirst = NewCheckBox("归档前先保存项目", true);
            _chkKeepOpen = NewCheckBox("完成后保持项目打开", false);
            _chkUpgradeArchive = NewCheckBox("旧版本项目升级打开", false);
            optionRow.Controls.Add(_chkSaveFirst);
            optionRow.Controls.Add(_chkKeepOpen);
            optionRow.Controls.Add(_chkUpgradeArchive);
            options.Controls.Add(optionRow);

            options.Controls.Add(NewFieldLabel("TIA 实例"));
            FlowLayoutPanel radios = NewHorizontalFlow();
            _rbArchiveNoUi = NewRadio("无界面（推荐）", true);
            _rbArchiveUi = NewRadio("显示界面", false);
            _rbArchiveAttach = NewRadio("附加到已运行的 TIA", false);
            radios.Controls.Add(_rbArchiveNoUi);
            radios.Controls.Add(_rbArchiveUi);
            radios.Controls.Add(_rbArchiveAttach);
            options.Controls.Add(radios);
            cardOptions.Controls.Add(options);
            root.Controls.Add(cardOptions, 0, 2);

            Panel actionRow = NewActionRow();
            _btnStartArchive = NewPrimaryButton("开始归档", OnStartArchive);
            actionRow.Controls.Add(NewHintLabel("提示：可以直接把 .apXX / .zapXX 文件拖进这个窗口自动填路径。"));
            actionRow.Controls.Add(_btnStartArchive);
            LayoutActionRow(actionRow, _btnStartArchive);
            root.Controls.Add(actionRow, 0, 3);

            return root;
        }

        // ═══════════════════════════════════════════════════════════════════
        //  页签 2：恢复
        // ═══════════════════════════════════════════════════════════════════

        private Control BuildRetrievePage()
        {
            TableLayoutPanel root = NewPageLayout(CardTwoRows, 232, 48, -1);

            Panel cardFiles = UiTheme.CreateCard();
            TableLayoutPanel grid = NewGrid(2);
            grid.Controls.Add(NewGridLabel("归档文件"), 0, 0);
            _txtArchiveFile = NewTextBox();
            grid.Controls.Add(_txtArchiveFile, 1, 0);
            grid.Controls.Add(NewBrowseButton("浏览", OnBrowseArchiveFile), 2, 0);

            grid.Controls.Add(NewGridLabel("解包到目录"), 0, 1);
            _txtRetrieveTarget = NewTextBox();
            grid.Controls.Add(_txtRetrieveTarget, 1, 1);
            grid.Controls.Add(NewBrowseButton("浏览", OnBrowseRetrieveTarget), 2, 1);

            cardFiles.Controls.Add(grid);
            root.Controls.Add(cardFiles, 0, 0);

            Panel cardOptions = UiTheme.CreateCard();
            FlowLayoutPanel options = NewVerticalFlow();
            _chkUpgrade = NewCheckBox("归档来自更早版本时，升级后打开（RetrieveWithUpgrade）", false);
            _chkSaveAfterRetrieve = NewCheckBox("解包后立即保存项目", false);
            _chkKeepOpenRetrieve = NewCheckBox("完成后保持项目打开（不勾选则关闭项目）", true);
            options.Controls.Add(_chkUpgrade);
            options.Controls.Add(_chkSaveAfterRetrieve);
            options.Controls.Add(_chkKeepOpenRetrieve);
            options.Controls.Add(NewFieldLabel("TIA 实例"));
            FlowLayoutPanel radios = NewHorizontalFlow();
            _rbRetrieveNoUi = NewRadio("无界面（推荐）", true);
            _rbRetrieveUi = NewRadio("显示界面", false);
            _rbRetrieveAttach = NewRadio("附加到已运行的 TIA", false);
            radios.Controls.Add(_rbRetrieveNoUi);
            radios.Controls.Add(_rbRetrieveUi);
            radios.Controls.Add(_rbRetrieveAttach);
            options.Controls.Add(radios);
            cardOptions.Controls.Add(options);
            root.Controls.Add(cardOptions, 0, 1);

            Panel actionRow = NewActionRow();
            _btnStartRetrieve = NewPrimaryButton("开始恢复", OnStartRetrieve);
            actionRow.Controls.Add(NewHintLabel("提示：解包目标目录建议选空目录，避免与其中已有项目重名。"));
            actionRow.Controls.Add(_btnStartRetrieve);
            LayoutActionRow(actionRow, _btnStartRetrieve);
            root.Controls.Add(actionRow, 0, 2);

            return root;
        }

        // ═══════════════════════════════════════════════════════════════════
        //  页签 3：环境探测
        // ═══════════════════════════════════════════════════════════════════

        private Control BuildProbePage()
        {
            TableLayoutPanel root = NewPageLayout(CardThreeRows, 190, 48, -1);

            Panel cardInput = UiTheme.CreateCard();
            TableLayoutPanel grid = NewGrid(3, 196);

            // 行 0：可用版本 —— 本机装了 Openness 组件的版本都会列在这里
            grid.Controls.Add(NewGridLabel("可用版本"), 0, 0);
            _cmbApiVersion = new ComboBox();
            _cmbApiVersion.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbApiVersion.Font = UiTheme.Body;
            _cmbApiVersion.Dock = DockStyle.Fill;
            _cmbApiVersion.Margin = new Padding(0, 6, 8, 6);
            _cmbApiVersion.SelectedIndexChanged += OnApiVersionSelected;
            grid.Controls.Add(_cmbApiVersion, 1, 0);
            grid.Controls.Add(NewBrowseButton("重新检测", OnEnumerateVersions), 2, 0);

            // 行 1：API 目录（选定版本后自动填，也可以手工指定）
            grid.Controls.Add(NewGridLabel("API 目录（可留空）"), 0, 1);
            _txtApiDir = NewTextBox();
            grid.Controls.Add(_txtApiDir, 1, 1);
            grid.Controls.Add(NewBrowseButton("浏览", OnBrowseApiDir), 2, 1);

            // 行 2：程序集过滤（仅 probe 用）+ 调试日志开关
            grid.Controls.Add(NewGridLabel("程序集过滤（可留空）"), 0, 2);
            _txtAssemblyFilter = NewTextBox();
            grid.Controls.Add(_txtAssemblyFilter, 1, 2);
            _chkVerbose = NewCheckBox("调试日志", false);
            _chkVerbose.Margin = new Padding(0, 8, 0, 8);
            grid.Controls.Add(_chkVerbose, 2, 2);

            cardInput.Controls.Add(grid);
            root.Controls.Add(cardInput, 0, 0);

            // ── 前置条件体检结果（多行、按通过/不通过着色）
            Panel cardCheck = UiTheme.CreateCard();
            _rtbCheckResult = new RichTextBox();
            _rtbCheckResult.Dock = DockStyle.Fill;
            _rtbCheckResult.ReadOnly = true;
            _rtbCheckResult.BorderStyle = BorderStyle.None;
            _rtbCheckResult.BackColor = UiTheme.Surface;
            _rtbCheckResult.ForeColor = UiTheme.TextSecondary;
            _rtbCheckResult.Font = UiTheme.Body;
            _rtbCheckResult.ScrollBars = RichTextBoxScrollBars.Vertical;
            _rtbCheckResult.WordWrap = false;
            _rtbCheckResult.DetectUrls = false;
            _rtbCheckResult.Text = "点“检查前置条件”逐项确认环境：Openness 组件是否安装、"
                + "当前用户是否已加入 Siemens TIA Openness 组、TIA 本体是否可用……\r\n"
                + "未通过的项目会给出可直接照做的修复步骤。";
            cardCheck.Controls.Add(_rtbCheckResult);
            root.Controls.Add(cardCheck, 0, 1);

            Panel actionRow = NewActionRow();
            _btnStartProbe = NewPrimaryButton("开始探测", OnStartProbe);

            _btnCheckEnvironment = new Button();
            _btnCheckEnvironment.Text = "检查前置条件";
            _btnCheckEnvironment.Width = 150;
            UiTheme.StyleSecondaryButton(_btnCheckEnvironment);
            _btnCheckEnvironment.Height = 36;
            _btnCheckEnvironment.Click += OnCheckEnvironment;

            Label probeHint = NewHintLabel("不确定环境是否就绪？先点右边的“检查前置条件”。");
            probeHint.Dock = DockStyle.Left;
            probeHint.Width = 560;
            probeHint.Height = 36;
            probeHint.AutoSize = false;
            probeHint.TextAlign = ContentAlignment.MiddleLeft;

            actionRow.Controls.Add(_btnStartProbe);
            actionRow.Controls.Add(_btnCheckEnvironment);
            actionRow.Controls.Add(probeHint);
            actionRow.Resize += delegate
            {
                int right = actionRow.ClientSize.Width;
                _btnStartProbe.Location = new Point(Math.Max(0, right - _btnStartProbe.Width), 0);
                _btnCheckEnvironment.Location = new Point(
                    Math.Max(0, right - _btnStartProbe.Width - _btnCheckEnvironment.Width
                        - UiTheme.Scale(actionRow, 8)), 0);
            };

            root.Controls.Add(actionRow, 0, 2);

            return root;
        }

        // ═══════════════════════════════════════════════════════════════════
        //  日志区
        // ═══════════════════════════════════════════════════════════════════

        private Control BuildLogPanel()
        {
            Panel container = new Panel();
            container.Dock = DockStyle.Fill;
            container.BackColor = UiTheme.Background;

            Panel toolbar = new Panel();
            toolbar.Dock = DockStyle.Top;
            toolbar.Height = 34;

            Label title = new Label();
            title.Text = "运行日志";
            title.Font = UiTheme.GroupTitle;
            title.ForeColor = UiTheme.TextPrimary;
            title.AutoSize = true;
            title.Location = new Point(0, 7);
            toolbar.Controls.Add(title);

            Button btnSaveLog = new Button();
            btnSaveLog.Text = "保存日志";
            btnSaveLog.Width = 140;
            UiTheme.StyleSecondaryButton(btnSaveLog);
            btnSaveLog.Click += OnSaveLog;
            toolbar.Controls.Add(btnSaveLog);

            Button btnCopyLog = new Button();
            btnCopyLog.Text = "复制全部";
            btnCopyLog.Width = 116;
            UiTheme.StyleSecondaryButton(btnCopyLog);
            btnCopyLog.Click += OnCopyLog;
            toolbar.Controls.Add(btnCopyLog);

            Button btnClearLog = new Button();
            btnClearLog.Text = "清空";
            btnClearLog.Width = 80;
            UiTheme.StyleSecondaryButton(btnClearLog);
            btnClearLog.Click += OnClearLog;
            toolbar.Controls.Add(btnClearLog);

            _chkAutoScroll = new CheckBox();
            _chkAutoScroll.Text = "自动滚动";
            _chkAutoScroll.Checked = true;
            _chkAutoScroll.Font = UiTheme.Body;
            _chkAutoScroll.AutoSize = true;
            _chkAutoScroll.ForeColor = UiTheme.TextSecondary;
            toolbar.Controls.Add(_chkAutoScroll);

            toolbar.Resize += delegate
            {
                int right = toolbar.ClientSize.Width;
                int gap = UiTheme.Scale(toolbar, 6);      // 按钮之间
                int top = UiTheme.Scale(toolbar, 3);
                btnSaveLog.Location = new Point(right - btnSaveLog.Width, top);
                btnCopyLog.Location = new Point(right - btnSaveLog.Width - btnCopyLog.Width - gap, top);
                btnClearLog.Location = new Point(
                    right - btnSaveLog.Width - btnCopyLog.Width - btnClearLog.Width - gap * 2, top);
                _chkAutoScroll.Location = new Point(
                    Math.Max(0, right - btnSaveLog.Width - btnCopyLog.Width - btnClearLog.Width
                        - _chkAutoScroll.Width - gap * 4), UiTheme.Scale(toolbar, 8));
            };

            _rtbLog = new RichTextBox();
            _rtbLog.Dock = DockStyle.Fill;
            _rtbLog.ReadOnly = true;
            _rtbLog.BackColor = Color.White;
            _rtbLog.ForeColor = UiTheme.TextPrimary;
            _rtbLog.Font = UiTheme.Log;
            _rtbLog.BorderStyle = BorderStyle.FixedSingle;
            _rtbLog.WordWrap = false;
            _rtbLog.ScrollBars = RichTextBoxScrollBars.Both;
            _rtbLog.HideSelection = false;
            _rtbLog.DetectUrls = false;

            container.Controls.Add(_rtbLog);
            container.Controls.Add(toolbar);
            return container;
        }

        // ═══════════════════════════════════════════════════════════════════
        //  布局工厂
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// 页面骨架：单列 TableLayoutPanel，行高按参数给定。
        /// </summary>
        /// <param name="rowHeights">
        /// 每行的高度：大于 0 表示固定像素；等于 0 表示按内容自适应；小于 0 表示占满剩余空间。
        /// </param>
        private static TableLayoutPanel NewPageLayout(params int[] rowHeights)
        {
            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.Padding = new Padding(12, 10, 12, 10);
            root.BackColor = UiTheme.Background;
            root.AutoScroll = true;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            foreach (int height in rowHeights)
            {
                if (height > 0)
                {
                    root.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
                }
                else if (height == 0)
                {
                    root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                }
                else
                {
                    root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                }
            }

            return root;
        }

        /// <summary>
        /// 卡片内部网格：3 列（标签 / 输入 / 按钮），行高固定。
        /// 必须是"固定行高 + 确定宽度"，否则输入框会被 AutoSize 挤没。
        /// </summary>
        private static TableLayoutPanel NewGrid(int rows)
        {
            return NewGrid(rows, LabelWidth);
        }

        private static TableLayoutPanel NewGrid(int rows, int labelWidth)
        {
            TableLayoutPanel grid = new TableLayoutPanel();
            grid.Dock = DockStyle.Fill;
            grid.AutoSize = false;
            grid.ColumnCount = 3;
            grid.RowCount = rows;
            grid.Margin = new Padding(0);
            // 标签列用 AutoSize：实测同一段文字在不同 DPI 缩放下宽度差很多
            // （"程序集过滤（可留空）" 实测要 223px），写死列宽必在某处被裁字。
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ButtonWidth));

            for (int i = 0; i < rows; i++)
            {
                grid.RowStyles.Add(new RowStyle(SizeType.Absolute, RowHeight));
            }

            return grid;
        }

        /// <summary>
        /// 网格单元格里的字段标签：Dock=Fill 填满自己那一列，宽度完全由列宽决定，
        /// 不再依赖 Label.Width 的固定值 —— 实测固定宽度在部分缩放下会被算窄，
        /// 结果就是“API 目录（可留空）”被裁成“API 目录（可留”，很难自查。
        /// </summary>
        private static Label NewGridLabel(string text)
        {
            Label label = UiTheme.CreateLabel(text, 100);
            // AutoSize=true 让标签宽度跟着文字走（列是 AutoSize 的），任何缩放都不裁字
            label.AutoSize = true;
            label.Dock = DockStyle.None;
            label.Margin = new Padding(0, 0, 12, 0);
            // 字段标签比说明文字更醒目一点，避免在浅色卡片上发灰看不清
            label.ForeColor = UiTheme.TextPrimary;
            return label;
        }

        private static TextBox NewTextBox()
        {
            TextBox textBox = new TextBox();
            UiTheme.StyleTextBox(textBox);
            textBox.Dock = DockStyle.Fill;
            textBox.Margin = new Padding(0, 5, 8, 5);
            return textBox;
        }

        private Button NewBrowseButton(string text, EventHandler handler)
        {
            Button button = new Button();
            button.Text = text;
            UiTheme.StyleSecondaryButton(button);
            button.Dock = DockStyle.Fill;
            button.Margin = new Padding(0, 4, 0, 4);
            button.Click += handler;
            return button;
        }

        private Button NewPrimaryButton(string text, EventHandler handler)
        {
            Button button = new Button();
            button.Text = text;
            button.Width = 170;
            UiTheme.StylePrimaryButton(button);
            button.Height = 36;
            button.Click += handler;
            return button;
        }

        private static Panel NewActionRow()
        {
            Panel row = new Panel();
            row.Dock = DockStyle.Fill;
            row.BackColor = UiTheme.Background;
            return row;
        }

        /// <summary>
        /// 操作行：提示文字靠左垂直居中，主按钮靠右。
        /// </summary>
        private static void LayoutActionRow(Panel row, Button primaryButton)
        {
            foreach (Control control in row.Controls)
            {
                Label hint = control as Label;
                if (hint != null)
                {
                    hint.Dock = DockStyle.Left;
                    hint.Width = 720;
                    hint.Height = 36;
                    hint.AutoSize = false;
                }
            }

            row.Resize += delegate
            {
                primaryButton.Location = new Point(
                    Math.Max(0, row.ClientSize.Width - primaryButton.Width), 0);
            };
            primaryButton.Location = new Point(
                Math.Max(0, row.ClientSize.Width - primaryButton.Width), 0);
        }

        private static Label NewHintLabel(string text)
        {
            Label label = UiTheme.CreateLabel(text, 560);
            label.TextAlign = ContentAlignment.MiddleLeft;
            return label;
        }

        private static CheckBox NewCheckBox(string text, bool isChecked)
        {
            CheckBox box = new CheckBox();
            box.Text = text;
            box.Checked = isChecked;
            box.Font = UiTheme.Body;
            box.ForeColor = UiTheme.TextPrimary;
            box.AutoSize = true;
            box.Margin = new Padding(0, 4, 0, 4);
            return box;
        }

        private static RadioButton NewRadio(string text, bool isChecked)
        {
            RadioButton radio = new RadioButton();
            radio.Text = text;
            radio.Checked = isChecked;
            radio.Font = UiTheme.Body;
            radio.ForeColor = UiTheme.TextPrimary;
            radio.AutoSize = true;
            radio.Margin = new Padding(0, 2, 20, 2);
            return radio;
        }

        private static Label NewFieldLabel(string text)
        {
            Label label = UiTheme.CreateLabel(text, 130);
            label.Height = 26;
            label.Margin = new Padding(0, 6, 0, 0);
            return label;
        }

        private static FlowLayoutPanel NewVerticalFlow()
        {
            FlowLayoutPanel panel = new FlowLayoutPanel();
            panel.FlowDirection = FlowDirection.TopDown;
            panel.WrapContents = false;
            panel.Dock = DockStyle.Fill;
            panel.AutoSize = false;
            panel.Margin = new Padding(0);
            return panel;
        }

        private static FlowLayoutPanel NewHorizontalFlow()
        {
            FlowLayoutPanel panel = new FlowLayoutPanel();
            panel.FlowDirection = FlowDirection.LeftToRight;
            panel.WrapContents = false;
            panel.AutoSize = true;
            panel.Margin = new Padding(14, 0, 0, 4);
            return panel;
        }

        // ═══════════════════════════════════════════════════════════════════
        //  输入辅助
        // ═══════════════════════════════════════════════════════════════════

        private void OnModeChanged(object sender, EventArgs e)
        {
            ModeItem item = _cmbMode.SelectedItem as ModeItem;
            if (item == null)
            {
                return;
            }

            if (item.IsFolderPack)
            {
                // 打包模式不是"危险"，但确实和普通归档差别很大，值得显著说明。
                // 文字刻意控制在三行以内：提示条占的高度直接从页签区扣，
                // 写太长会把归档页顶出滚动条（实测四行时正好溢出）。
                ShowModeWarning(
                    "打包模式：整个项目文件夹压成 .zip，不走 TIA 的 .zap 归档。\r\n"
                    + "不需要 Openness、不启动 TIA，也不受版本匹配限制（V21 也能打包 V18 项目）。\r\n"
                    + "解压后可直接用 TIA 打开，不触发项目升级；打包前请确认项目未在 TIA 中打开。",
                    UiTheme.Warning, UiTheme.WarningBack);

                if (_chkUpgradeArchive.Checked)
                {
                    SetStatus("提示：打包模式不会打开项目，“旧版本项目升级打开”对它不起作用；"
                        + "输出扩展名会自动改成 .zip。");
                }
                else
                {
                    SetStatus("已选择打包模式：不需要 Openness，直接压缩项目文件夹；输出扩展名会自动改成 .zip。");
                }
                return;
            }

            if (item.IsDangerous)
            {
                ShowModeWarning(
                    "⚠ 所选模式：" + item.Text + "\r\n"
                    + "该模式会不可逆地丢弃项目的可恢复数据（下载 / 在线比较所需的源码与符号信息）。"
                    + "归档一旦落盘就找不回来，之后该项目将无法再用于下载或在线比较。",
                    UiTheme.Danger, UiTheme.DangerBack);
                return;
            }

            HideModeWarning();
        }

        /// <summary>
        /// 显示模式提示条（黄条 / 红条）。
        /// </summary>
        private void ShowModeWarning(string text, Color foreColor, Color backColor)
        {
            _lblModeWarning.Text = text;
            _lblModeWarning.ForeColor = foreColor;
            _lblModeWarning.BackColor = backColor;
            _lblModeWarning.Visible = true;
            LayoutModeWarning();
        }

        /// <summary>
        /// 收起模式提示条（连它占的那一行也一起收掉）。
        /// </summary>
        private void HideModeWarning()
        {
            _lblModeWarning.Visible = false;
            LayoutModeWarning();
        }

        /// <summary>
        /// 按文字换行后的真实高度设定提示条所在的这一行。
        ///
        /// 为什么不能只把行样式设成 AutoSize：AutoSize 行量的是标签的"首选高度"，
        /// 而没开 AutoSize 的 Label 报的是单行高度（23px）—— 于是三行文字被塞进
        /// 23px 里，界面上只剩一条黄边、字全被裁掉（用户实际反馈的现象）。
        /// 这里自己用 TextRenderer 按可用宽度量一遍，把高度写回行样式，跟 DPI、
        /// 字体、窗口宽度三者的变化都对得上。
        /// </summary>
        private void LayoutModeWarning()
        {
            TableLayoutPanel root = _lblModeWarning == null
                ? null
                : _lblModeWarning.Parent as TableLayoutPanel;

            if (root == null || root.RowStyles.Count < 2)
            {
                return;
            }

            int target = 0;

            if (_lblModeWarning.Visible && !string.IsNullOrEmpty(_lblModeWarning.Text))
            {
                int available = root.ClientSize.Width
                    - root.Padding.Horizontal
                    - _lblModeWarning.Margin.Horizontal
                    - _lblModeWarning.Padding.Horizontal;

                if (available < UiTheme.Scale(root, 80))
                {
                    // 窗口还没显示出来（或窄到不合理）时先按一个下限估，
                    // 显示之后 SizeChanged 会再算一次，不会一直错着。
                    available = UiTheme.Scale(root, 400);
                }

                Size needed = TextRenderer.MeasureText(
                    _lblModeWarning.Text,
                    _lblModeWarning.Font,
                    new Size(available, int.MaxValue),
                    TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);

                target = needed.Height
                    + _lblModeWarning.Padding.Vertical
                    + _lblModeWarning.Margin.Vertical;
            }

            RowStyle style = root.RowStyles[1];

            // 只在真的变了才赋值：反复设 RowStyles 会不停触发重排
            if (style.SizeType != SizeType.Absolute || Math.Abs(style.Height - target) > 0.5f)
            {
                style.SizeType = SizeType.Absolute;
                style.Height = target;
            }
        }

        private TiaStartMode GetStartMode(RadioButton noUi, RadioButton ui, RadioButton attach)
        {
            if (attach.Checked)
            {
                return TiaStartMode.AttachExisting;
            }
            if (ui.Checked)
            {
                return TiaStartMode.WithUserInterface;
            }
            return TiaStartMode.WithoutUserInterface;
        }

        private void LoadSettingsIntoUi()
        {
            _txtProject.Text = _settings.GetString("LastProject", string.Empty);
            _txtArchiveOut.Text = _settings.GetString("LastArchiveOut", string.Empty);
            _txtArchiveFile.Text = _settings.GetString("LastArchiveFile", string.Empty);
            _txtRetrieveTarget.Text = _settings.GetString("LastRetrieveTarget", string.Empty);
            _txtApiDir.Text = _settings.GetString("LastApiDir", string.Empty);

            _chkSaveFirst.Checked = _settings.GetBool("SaveFirst", true);
            _chkKeepOpen.Checked = _settings.GetBool("KeepOpen", false);
            _chkUpgradeArchive.Checked = _settings.GetBool("UpgradeArchive", false);
            _chkUpgrade.Checked = _settings.GetBool("Upgrade", false);
            _chkSaveAfterRetrieve.Checked = _settings.GetBool("SaveAfterRetrieve", false);
            _chkKeepOpenRetrieve.Checked = _settings.GetBool("KeepOpenRetrieve", true);
            _chkVerbose.Checked = _settings.GetBool("Verbose", false);

            int modeIndex = _settings.GetInt("ModeIndex", 0);
            if (modeIndex >= 0 && modeIndex < _cmbMode.Items.Count)
            {
                _cmbMode.SelectedIndex = modeIndex;
            }

            int archiveStart = _settings.GetInt("ArchiveStartMode", 0);
            _rbArchiveNoUi.Checked = archiveStart == 0;
            _rbArchiveUi.Checked = archiveStart == 1;
            _rbArchiveAttach.Checked = archiveStart == 2;

            int retrieveStart = _settings.GetInt("RetrieveStartMode", 0);
            _rbRetrieveNoUi.Checked = retrieveStart == 0;
            _rbRetrieveUi.Checked = retrieveStart == 1;
            _rbRetrieveAttach.Checked = retrieveStart == 2;

            // 文件名规则
            _chkAppendTimestamp.Checked = _settings.GetBool("NamingTimestamp", true);
            _chkCustomSuffix.Checked = _settings.GetBool("NamingUseSuffix", false);
            _txtSuffix.Text = _settings.GetString("NamingSuffix", string.Empty);

            string savedFormat = _settings.GetString(
                "NamingTimestampFormat", ArchiveNaming.DefaultTimestampFormat);
            int formatIndex = 0;
            for (int i = 0; i < _cmbTimestampFormat.Items.Count; i++)
            {
                TimestampItem candidate = _cmbTimestampFormat.Items[i] as TimestampItem;
                if (candidate != null && string.Equals(candidate.Format, savedFormat, StringComparison.Ordinal))
                {
                    formatIndex = i;
                    break;
                }
            }
            _cmbTimestampFormat.SelectedIndex = formatIndex;

            // 规则装载完后统一刷新一次预览与批量页提示
            OnNamingRuleChanged(null, EventArgs.Empty);
        }

        private void SaveSettingsFromUi(bool archiveCompleted, bool retrieveCompleted)
        {
            _settings.SetString("LastProject", _txtProject.Text.Trim());
            _settings.SetString("LastArchiveOut", _txtArchiveOut.Text.Trim());
            _settings.SetString("LastArchiveFile", _txtArchiveFile.Text.Trim());
            _settings.SetString("LastRetrieveTarget", _txtRetrieveTarget.Text.Trim());
            _settings.SetString("LastApiDir", _txtApiDir.Text.Trim());
            _settings.SetBool("SaveFirst", _chkSaveFirst.Checked);
            _settings.SetBool("UpgradeArchive", _chkUpgradeArchive.Checked);

            // 文件名规则
            _settings.SetBool("NamingTimestamp", _chkAppendTimestamp.Checked);
            _settings.SetBool("NamingUseSuffix", _chkCustomSuffix.Checked);
            _settings.SetString("NamingSuffix", _txtSuffix.Text.Trim());

            TimestampItem selectedFormat = _cmbTimestampFormat.SelectedItem as TimestampItem;
            if (selectedFormat != null)
            {
                _settings.SetString("NamingTimestampFormat", selectedFormat.Format);
            }
            _settings.SetBool("Upgrade", _chkUpgrade.Checked);
            _settings.SetBool("SaveAfterRetrieve", _chkSaveAfterRetrieve.Checked);
            _settings.SetBool("Verbose", _chkVerbose.Checked);
            _settings.SetInt("ModeIndex", _cmbMode.SelectedIndex);
            _settings.SetInt("ArchiveStartMode",
                _rbArchiveAttach.Checked ? 2 : (_rbArchiveUi.Checked ? 1 : 0));
            _settings.SetInt("RetrieveStartMode",
                _rbRetrieveAttach.Checked ? 2 : (_rbRetrieveUi.Checked ? 1 : 0));

            // 归档/恢复成功跑过一次之后，才把"保持打开"作为新默认值记住 ——
            // 用户改了选项但还没跑就关窗口时，不该把没验证过的偏好留下。
            if (archiveCompleted)
            {
                _settings.SetBool("KeepOpen", _chkKeepOpen.Checked);
            }
            if (retrieveCompleted)
            {
                _settings.SetBool("KeepOpenRetrieve", _chkKeepOpenRetrieve.Checked);
            }

            _settings.Save();
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (_busy)
            {
                DialogResult result = MessageBox.Show(
                    "后台仍在执行 Openness 操作（TIA Portal 可能正在启动或归档）。\r\n"
                    + "现在关闭窗口不会中断正在进行的操作，且你可能会失去日志。\r\n\r\n确定要关闭吗？",
                    ToolTitle,
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);

                if (result != DialogResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
            }

            SaveSettingsFromUi(false, false);
        }

        // ── 拖放：把文件拖进窗口就填路径
        private void OnDragEnterAny(object sender, DragEventArgs e)
        {
            if (e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effect = DragDropEffects.Copy;
            }
        }

        private void OnDragDropAny(object sender, DragEventArgs e)
        {
            string[] files = e.Data == null ? null : e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files == null || files.Length == 0)
            {
                return;
            }

            string file = files[0];
            string extension = Path.GetExtension(file);

            if (extension.StartsWith(".ap", StringComparison.OrdinalIgnoreCase))
            {
                _tabs.SelectedIndex = 0;
                _txtProject.Text = file;
                ApplySuggestedArchiveOut();
                SetStatus("已从拖放填入项目文件；归档输出已自动建议路径。");
                return;
            }

            if (extension.StartsWith(".zap", StringComparison.OrdinalIgnoreCase))
            {
                _tabs.SelectedIndex = 1;
                _txtArchiveFile.Text = file;
                SetStatus("已从拖放填入归档文件。");
                return;
            }

            SetStatus("不认识的文件类型：" + extension + "（支持 .apXX 项目文件与 .zapXX 归档文件）");
        }

        /// <summary>
        /// 用户手工改了"归档到"（浏览选了别的位置、或直接敲了字）→ 之后换项目时就不再整个覆盖它。
        /// </summary>
        private void OnArchiveOutEdited(object sender, EventArgs e)
        {
            if (!_suppressNamingEvents)
            {
                _archiveOutUserChosen = true;
            }

            OnNamingRuleChanged(sender, e);
        }

        /// <summary>
        /// 项目换了以后，把"归档到"跟着更新。
        ///
        /// ★ 这条是防**覆盖历史归档**的：实测踩到过 —— 换了新项目、"归档到"还是上一个项目的名字，
        ///   第二次归档直接把第一次的产物覆盖掉了（虽有同名确认框，但很容易顺手点"覆盖"）。
        ///
        /// 规则：
        ///   · 用户没自己选过输出位置 → 整个跟着项目走（同目录 + 项目名 + 当前内核扩展名）
        ///   · 用户自己选过（例如固定备份到 D:/Bak）→ **保留他选的目录**，只把文件名换成新项目的名字
        /// </summary>
        private void ApplySuggestedArchiveOut()
        {
            if (_txtProject == null || _txtArchiveOut == null)
            {
                return;
            }

            string suggested = SuggestArchivePath(_txtProject.Text);
            if (string.IsNullOrWhiteSpace(suggested))
            {
                return;
            }

            string target = suggested;
            if (_archiveOutUserChosen)
            {
                // ★ “归档到”是用户可自由编辑的文本框，里面可能是手误/粘贴来的**非法路径**。
                //   Path.GetDirectoryName 对这类输入会抛异常，不接住就冒泡成“程序遇到未处理的错误”。
                //   实测过这一行崩溃：先在“归档到”里输入非法内容，再点“浏览”换项目即复现。
                //   取不到合法目录时，放弃“保留用户选过的目录”，直接采用上面算出的建议路径。
                //
                //   按实测（本机直接调 .NET 该 API，逐种输入取证）需要接两类：
                //     · 路径含非法字符（" | < 等）→ ArgumentException（“路径的形式不合法”）
                //     · 路径超长（>260 且未开启长路径）→ PathTooLongException
                //   两者互不继承，只接其一仍会漏掉另一种。不接更宽的 Exception：
                //   这里只该消化“这个路径我没法解析”，其它异常照旧往上抛。
                string chosenDirectory = null;
                try
                {
                    chosenDirectory = Path.GetDirectoryName(_txtArchiveOut.Text);
                }
                catch (ArgumentException)
                {
                    // 非法字符：chosenDirectory 保持 null（放弃保留旧目录）
                }
                catch (PathTooLongException)
                {
                    // 超长路径：同上，退回建议路径
                }

                if (!string.IsNullOrWhiteSpace(chosenDirectory))
                {
                    target = Path.Combine(chosenDirectory, Path.GetFileName(suggested));
                }
            }

            if (string.Equals(_txtArchiveOut.Text, target, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _suppressNamingEvents = true;
            try
            {
                _txtArchiveOut.Text = target;
            }
            catch (Exception)
            {
                // 忽略
            }
            finally
            {
                _suppressNamingEvents = false;
            }

            _archiveOutUserChosen = false;
        }

        /// <summary>
        /// 按项目文件路径建议一个归档输出路径（**项目目录的上一级** + 与项目同名的 .zapXX）。
        ///
        /// ★ 为什么是"上一级"而不是"项目目录里"：TIA 的 Project.Archive **拒绝把归档产物写进
        ///   项目自身所在目录** —— V19 实测报 "Unable to archive the project. / Archiving failed. /
        ///   项目目录已存在，无法保存。请选择一个不同的路径。"；同一个项目换到别的输出目录、
        ///   或走批量归档（输出目录另选）都是一次成功。默认建议值必须避开这个位置，
        ///   否则用户不点"浏览"就必然撞上。
        ///   （项目就在盘根、没有上一级可退时保持项目目录，由 CoreRunner.EnsureArchiveTargetUsable 拦下提示。）
        ///
        /// 扩展名取"当前要用的内核版本"（界面上"可用版本"下拉里选的那个），拿不到再退回项目自身版本。
        /// 这样在 V19 机器上给 .ap19 项目建议出来的就是 .zap19，而不是写死的 .zap21。
        /// </summary>
        private string SuggestArchivePath(string projectPath)
        {
            try
            {
                string projectDirectory = Path.GetDirectoryName(projectPath);
                string name = Path.GetFileNameWithoutExtension(projectPath);
                string fileName = name + ArchiveNaming.BuildArchiveExtension(CurrentKernelMajorVersion(), projectPath);
                if (string.IsNullOrEmpty(projectDirectory))
                {
                    return fileName;
                }

                // 上一级目录；到了盘根没有上一级时退回项目目录（此时由目标体检拦下并提示换盘）。
                string parent = Path.GetDirectoryName(projectDirectory.TrimEnd('\\', '/'));
                string directory = string.IsNullOrEmpty(parent) ? projectDirectory : parent;
                return Path.Combine(directory, fileName);
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// 当前打算用哪个 Openness 内核（就是"可用版本"下拉里选中的那个）的主版本号；
        /// 还没枚举出来时返回 0，调用方会退回"按项目自己的版本"。
        /// </summary>
        private int CurrentKernelMajorVersion()
        {
            VersionItem item = _cmbApiVersion == null ? null : _cmbApiVersion.SelectedItem as VersionItem;
            if (item != null && item.Info != null && item.Info.MajorVersion > 0)
            {
                return item.Info.MajorVersion;
            }

            foreach (TiaEnvironmentInfo info in _availableVersions)
            {
                if (info != null && info.MajorVersion > 0)
                {
                    return info.MajorVersion;
                }
            }

            return 0;
        }

        // ═══════════════════════════════════════════════════════════════════
        //  日志显示
        // ═══════════════════════════════════════════════════════════════════

        private void AppendLog(LogLevel level, string prefix, string message)
        {
            if (_rtbLog == null || _rtbLog.IsDisposed)
            {
                return;
            }

            try
            {
                _rtbLog.SelectionStart = _rtbLog.TextLength;
                _rtbLog.SelectionLength = 0;
                _rtbLog.SelectionColor = ColorForLevel(level);
                _rtbLog.AppendText(prefix + " " + message + Environment.NewLine);
                _rtbLog.SelectionColor = _rtbLog.ForeColor;
            }
            catch (Exception)
            {
                // 日志框异常不应影响主流程。
                return;
            }

            TrimLogIfTooLong();

            if (_chkAutoScroll.Checked)
            {
                _rtbLog.SelectionStart = _rtbLog.TextLength;
                _rtbLog.ScrollToCaret();
            }
        }

        private void TrimLogIfTooLong()
        {
            const int maxLines = 4000;
            if (_rtbLog.Lines.Length <= maxLines)
            {
                return;
            }

            int removeLines = _rtbLog.Lines.Length - maxLines + 500;
            int start = _rtbLog.GetFirstCharIndexFromLine(0);
            int end = _rtbLog.GetFirstCharIndexFromLine(removeLines);
            if (start < 0 || end <= start)
            {
                return;
            }

            _rtbLog.Select(start, end - start);
            _rtbLog.SelectedText = string.Empty;
        }

        private static Color ColorForLevel(LogLevel level)
        {
            switch (level)
            {
                case LogLevel.Debug: return UiTheme.LogDebug;
                case LogLevel.Warning: return UiTheme.LogWarning;
                case LogLevel.Error: return UiTheme.LogError;
                case LogLevel.Success: return UiTheme.LogSuccess;
                default: return UiTheme.LogInfo;
            }
        }

        private void SetStatus(string text)
        {
            if (_lblStatus == null || _lblStatus.IsDisposed)
            {
                return;
            }
            _lblStatus.Text = text;
        }

        // ═══════════════════════════════════════════════════════════════════
        //  日志区按钮
        // ═══════════════════════════════════════════════════════════════════

        private void OnClearLog(object sender, EventArgs e)
        {
            _rtbLog.Clear();
        }

        private void OnCopyLog(object sender, EventArgs e)
        {
            if (_rtbLog.TextLength == 0)
            {
                SetStatus("日志为空，没有可复制的内容。");
                return;
            }

            try
            {
                Clipboard.SetText(_rtbLog.Text);
                SetStatus("日志已复制到剪贴板。");
            }
            catch (Exception ex)
            {
                MessageBox.Show("复制失败：" + ex.Message, ToolTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void OnSaveLog(object sender, EventArgs e)
        {
            using (SaveFileDialog dialog = new SaveFileDialog())
            {
                dialog.Title = "保存运行日志";
                dialog.Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*";
                dialog.FileName = "TiaArchiveGui-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log";
                dialog.InitialDirectory = GuiSettings.DirectoryOf(_settings.GetString("LastProject", string.Empty));

                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }

                try
                {
                    File.WriteAllText(dialog.FileName, _rtbLog.Text, new UTF8Encoding(true));
                    SetStatus("日志已保存：" + dialog.FileName);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("保存日志失败：" + ex.Message, ToolTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        //  浏览按钮
        // ═══════════════════════════════════════════════════════════════════

        private void OnBrowseProject(object sender, EventArgs e)
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = "选择要归档的 TIA 项目文件";
                dialog.Filter = "TIA 项目文件 (*.ap*)|*.ap*|所有文件 (*.*)|*.*";
                dialog.CheckFileExists = true;
                dialog.InitialDirectory = GuiSettings.DirectoryOf(_txtProject.Text);

                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }

                _txtProject.Text = dialog.FileName;
                ApplySuggestedArchiveOut();
            }
        }

        private void OnBrowseArchiveOut(object sender, EventArgs e)
        {
            using (SaveFileDialog dialog = new SaveFileDialog())
            {
                dialog.Title = "选择归档输出文件";
                dialog.Filter = "TIA 归档文件 (*.zap*)|*.zap*|所有文件 (*.*)|*.*";

                string suggested = _txtArchiveOut.Text;
                if (string.IsNullOrWhiteSpace(suggested))
                {
                    suggested = SuggestArchivePath(_txtProject.Text);
                }

                if (!string.IsNullOrWhiteSpace(suggested))
                {
                    try
                    {
                        dialog.InitialDirectory = GuiSettings.DirectoryOf(suggested);
                        dialog.FileName = Path.GetFileName(suggested);
                    }
                    catch (Exception)
                    {
                        // 路径异常就退回默认
                    }
                }

                if (string.IsNullOrWhiteSpace(dialog.FileName))
                {
                    dialog.FileName = "Demo" + ArchiveNaming.BuildArchiveExtension(CurrentKernelMajorVersion(), null);
                }

                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    _txtArchiveOut.Text = dialog.FileName;
                }
            }
        }

        private void OnBrowseArchiveFile(object sender, EventArgs e)
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = "选择要恢复的归档文件";
                dialog.Filter = "TIA 归档文件 (*.zap*)|*.zap*|所有文件 (*.*)|*.*";
                dialog.CheckFileExists = true;
                dialog.InitialDirectory = GuiSettings.DirectoryOf(_txtArchiveFile.Text);

                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    _txtArchiveFile.Text = dialog.FileName;
                }
            }
        }

        private void OnBrowseRetrieveTarget(object sender, EventArgs e)
        {
            string currentTarget = GuiSettings.DirectoryOf(_txtRetrieveTarget.Text);
            string selectedTarget;

            if (ModernFolderDialog.TryShow(this, "选择解包目标目录", currentTarget, out selectedTarget))
            {
                if (!string.IsNullOrEmpty(selectedTarget))
                {
                    _txtRetrieveTarget.Text = selectedTarget;
                }
                return;
            }

            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = "选择解包目标目录";
                dialog.ShowNewFolderButton = true;

                string current = GuiSettings.DirectoryOf(_txtRetrieveTarget.Text);
                if (!string.IsNullOrEmpty(current))
                {
                    dialog.SelectedPath = current;
                }

                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    _txtRetrieveTarget.Text = dialog.SelectedPath;
                }
            }
        }

        private void OnBrowseApiDir(object sender, EventArgs e)
        {
            string currentApiDir = GuiSettings.DirectoryOf(_txtApiDir.Text);
            string selectedApiDir;

            if (ModernFolderDialog.TryShow(
                this, "选择 TIA Portal 的 PublicAPI 目录（例如 ...\\Portal V21\\PublicAPI\\V21\\net48\\）",
                currentApiDir, out selectedApiDir))
            {
                if (!string.IsNullOrEmpty(selectedApiDir))
                {
                    _txtApiDir.Text = selectedApiDir;
                }
                return;
            }

            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = "选择 TIA Portal 的 PublicAPI 目录（例如 ...\\Portal V21\\PublicAPI\\V21\\net48\\）";
                dialog.ShowNewFolderButton = false;

                string current = GuiSettings.DirectoryOf(_txtApiDir.Text);
                if (!string.IsNullOrEmpty(current))
                {
                    dialog.SelectedPath = current;
                }

                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    _txtApiDir.Text = dialog.SelectedPath;
                }
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        //  环境检查小工具
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// 检查当前 Windows 用户是否在本地组 "Siemens TIA Openness" 里。
        /// 这是 Openness 能不能连上 TIA 的第一道门槛，也是新手最常卡住的地方，
        /// 所以放在界面上主动提示，而不是等调用失败再让用户猜。
        /// </summary>
        /// <param name="message">中文说明，供界面显示。</param>
        /// <returns>true 表示已在该组内。</returns>
        internal static bool IsCurrentUserInOpennessGroup(out string message)
        {
            try
            {
                WindowsIdentity identity = WindowsIdentity.GetCurrent();
                WindowsPrincipal principal = new WindowsPrincipal(identity);
                if (principal.IsInRole(TiaEnvironment.OpennessUserGroup))
                {
                    message = "当前用户已在本地组 “" + TiaEnvironment.OpennessUserGroup
                        + "” 中，Openness 权限检查可以通过。";
                    return true;
                }

                message = "当前用户不在本地组 “" + TiaEnvironment.OpennessUserGroup + "” 中。"
                    + "运行 lusrmgr.msc → 组 → Siemens TIA Openness → 添加当前用户 → 注销并重新登录。";
                return false;
            }
            catch (Exception ex)
            {
                message = "无法检查用户组（" + ex.GetType().Name + "：" + ex.Message + "）。"
                    + "请自行确认当前用户在本地组 " + TiaEnvironment.OpennessUserGroup + " 中。";
                return false;
            }
        }

        /// <summary>
        /// 归档模式下拉项的载体。
        /// </summary>
        private sealed class ModeItem
        {
            /// <summary>显示文本。</summary>
            public string Text { get; private set; }

            /// <summary>传给核心逻辑的关键字。</summary>
            public string Keyword { get; private set; }

            /// <summary>是否为不可逆的危险模式。</summary>
            public bool IsDangerous { get; private set; }

            /// <summary>
            /// 是否为"打包项目文件夹"模式：不经过 Openness，直接把整个项目目录压成 .zip。
            /// 这个模式下不需要 Openness、不需要启动 TIA，也不受版本匹配限制。
            /// </summary>
            public bool IsFolderPack { get; private set; }

            /// <summary>构造。</summary>
            /// <param name="text">显示文本。</param>
            /// <param name="keyword">关键字。</param>
            /// <param name="isDangerous">是否危险。</param>
            /// <param name="isFolderPack">是否为"打包项目文件夹"模式。</param>
            public ModeItem(string text, string keyword, bool isDangerous, bool isFolderPack = false)
            {
                Text = text;
                Keyword = keyword;
                IsDangerous = isDangerous;
                IsFolderPack = isFolderPack;
            }

            /// <summary>下拉框显示的就是 Text。</summary>
            /// <returns>显示文本。</returns>
            public override string ToString()
            {
                return Text;
            }
        }

        /// <summary>
        /// 时间戳格式下拉项：显示"示例 + 格式串"，取值就是格式串本身。
        /// 示例用固定时间生成，这样下拉列表里的样子是稳定的，不会随着当前时间变来变去。
        /// </summary>
        private sealed class TimestampItem
        {
            /// <summary>.NET 日期格式字符串。</summary>
            public string Format { get; private set; }

            private readonly string _display;

            /// <summary>构造。</summary>
            /// <param name="format">格式字符串。</param>
            public TimestampItem(string format)
            {
                Format = format;
                _display = ArchiveNaming.DescribeFormat(format, new DateTime(2026, 9, 19, 11, 19, 15));
            }

            /// <summary>下拉框显示文本。</summary>
            /// <returns>形如 "20260919_111915    yyyyMMdd_HHmmss"。</returns>
            public override string ToString()
            {
                return _display;
            }
        }
    }
}
