using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using TiaOpennessKit;
using TiaOpennessKit.Tia;

namespace TiaArchiveGui
{
    /// <summary>
    /// 主窗口的"批量归档"部分：选文件夹 → 扫描出里面的 TIA 项目 → 勾选要归档的 → 一次性归档。
    ///
    /// 与单文件归档的关系：两者共用同一套 Openness 调用（CoreRunner），只是批量版会
    /// **只启动一次 TIA Portal** 然后循环处理，否则每个项目都要等 TIA 冷启动几十秒。
    /// </summary>
    internal partial class MainForm
    {
        // ── 批量页控件
        private TextBox _txtBatchFolder;
        private CheckBox _chkBatchRecursive;
        private CheckBox _chkBatchIgnoreBackup;
        private Button _btnBatchScan;
        private DataGridView _gridBatch;
        private Label _lblBatchSummary;
        private TextBox _txtBatchOutputDir;
        private ComboBox _cmbBatchMode;
        private CheckBox _chkBatchSaveFirst;
        private CheckBox _chkBatchUpgrade;
        private CheckBox _chkBatchAutoKernel;
        private Button _btnBatchStart;
        private Button _btnBatchCancel;
        private Label _lblBatchHint;

        // ── 批量页：设置区可收起（腾出高度给项目列表）
        private TableLayoutPanel _batchRoot;
        private Panel _batchSourceCard;
        private Panel _batchSettingsCard;
        private Button _btnBatchToggleSettings;
        private bool _batchSettingsCollapsed;
        // 收起时记下的"真实行高"（已经按 DPI 放大过），展开时用它还原
        private float _batchSourceRowHeight;
        private float _batchSettingsRowHeight;

        // 表格行高 / 表头高（96 DPI 基准值；DataGridView 不归自动缩放管，实际值由
        // ApplyGridDpiScale() 按 DPI 换算）。收紧到 24/28 是为了同屏多显示一两行。
        private const int BatchRowHeight = 24;
        private const int BatchHeaderHeight = 28;

        // ── 批量页状态
        private List<TiaProjectEntry> _batchEntries = new List<TiaProjectEntry>();
        private volatile bool _batchCancelRequested;
        private volatile bool _batchRunning;
        private bool _suppressGridEvents;
        // 互斥处理时程序自己改另一个勾选框，会再次触发 CheckedChanged；
        // 用它挡住那次重入，否则后进来的分支会把"已自动取消 XX"的说明文字覆盖掉。
        private bool _suppressKernelOptionEvents;

        // ═══════════════════════════════════════════════════════════════════
        //  界面
        // ═══════════════════════════════════════════════════════════════════

        private Control BuildBatchPage()
        {
            // 行：来源卡片 / 归档设置卡片(三行) / 项目列表(占剩余) / 操作行
            TableLayoutPanel root = NewPageLayout(CardTwoRows, CardThreeRows, -1, 48);
            _batchRoot = root;

            // ── 卡片 1：来源
            Panel cardSource = UiTheme.CreateCard();
            TableLayoutPanel gridSource = NewGrid(2);
            gridSource.Controls.Add(NewGridLabel("项目文件夹"), 0, 0);
            _txtBatchFolder = NewTextBox();
            gridSource.Controls.Add(_txtBatchFolder, 1, 0);
            gridSource.Controls.Add(NewBrowseButton("浏览", OnBrowseBatchFolder), 2, 0);

            gridSource.Controls.Add(NewGridLabel("扫描范围"), 0, 1);
            FlowLayoutPanel scanScope = NewHorizontalFlow();
            scanScope.Margin = new Padding(0, 6, 0, 6);

            _chkBatchRecursive = NewCheckBox("包含子文件夹", true);
            _chkBatchIgnoreBackup = NewCheckBox("忽略 .backup 备份目录", true);
            scanScope.Controls.Add(_chkBatchRecursive);
            scanScope.Controls.Add(_chkBatchIgnoreBackup);
            gridSource.Controls.Add(scanScope, 1, 1);

            _btnBatchScan = NewPrimaryButton("扫描项目", OnBatchScan);
            _btnBatchScan.Dock = DockStyle.Fill;
            _btnBatchScan.Margin = new Padding(0, 4, 0, 4);
            gridSource.Controls.Add(_btnBatchScan, 2, 1);

            cardSource.Controls.Add(gridSource);
            _batchSourceCard = cardSource;
            root.Controls.Add(cardSource, 0, 0);

            // ── 卡片 2：归档设置
            Panel cardSettings = UiTheme.CreateCard();
            TableLayoutPanel gridSettings = NewGrid(3);
            gridSettings.Controls.Add(NewGridLabel("输出目录"), 0, 0);
            _txtBatchOutputDir = NewTextBox();
            gridSettings.Controls.Add(_txtBatchOutputDir, 1, 0);
            gridSettings.Controls.Add(NewBrowseButton("浏览", OnBrowseBatchOutput), 2, 0);

            gridSettings.Controls.Add(NewGridLabel("归档模式"), 0, 1);

            FlowLayoutPanel modeRow = new FlowLayoutPanel();
            modeRow.FlowDirection = FlowDirection.LeftToRight;
            modeRow.WrapContents = false;
            modeRow.AutoSize = true;
            modeRow.Margin = new Padding(0, 7, 0, 7);

            _cmbBatchMode = new ComboBox();
            _cmbBatchMode.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbBatchMode.Font = UiTheme.Body;
            // 宽 280：要放得下最长的那项"打包项目文件夹（.zip，不需要 Openness）"。
            // 原来给了 224，选到打包模式时下拉框里的字会被截掉半截。
            _cmbBatchMode.Width = 280;
            _cmbBatchMode.Margin = new Padding(0, 0, 18, 0);
            _cmbBatchMode.Items.Add(new ModeItem("压缩（推荐）", "compressed", false));
            _cmbBatchMode.Items.Add(new ModeItem("不压缩（类似“另存为”）", "none", false));
            _cmbBatchMode.Items.Add(new ModeItem("丢弃可恢复数据（不可逆，慎用）", "discard-restorable", true));
            _cmbBatchMode.Items.Add(new ModeItem("丢弃可恢复数据并压缩（不可逆，慎用）", "discard-restorable-compressed", true));
            _cmbBatchMode.Items.Add(new ModeItem("打包项目文件夹（.zip，不需要 Openness）", "pack", false, true));
            _cmbBatchMode.SelectedIndex = 0;
            modeRow.Controls.Add(_cmbBatchMode);

            // 按项目版本自动选内核：混版本时把"与当前内核不同版本"的项目交给子进程原生归档，
            // 好处是旧项目**不升级**、产物保持原版本（.ap16 → .zap16）。
            _chkBatchAutoKernel = NewCheckBox("按项目版本自动选内核（旧项目不升级）", true);
            _chkBatchAutoKernel.Margin = new Padding(12, 2, 0, 2);
            _chkBatchAutoKernel.CheckedChanged += OnBatchKernelOptionChanged;
            modeRow.Controls.Add(_chkBatchAutoKernel);

            gridSettings.Controls.Add(modeRow, 1, 1);
            gridSettings.SetColumnSpan(modeRow, 2);

            // 行 2：批量选项单独一行。
            // 原来这两个勾选框跟"归档模式"挤在同一行，高分屏上模式下拉一放大
            // 就把它们顶到卡片外面去了（看不见，只能靠拖窗口宽度猜）。
            gridSettings.Controls.Add(NewGridLabel("批量选项"), 0, 2);

            FlowLayoutPanel batchOptions = NewHorizontalFlow();
            batchOptions.Margin = new Padding(0, 6, 0, 6);
            _chkBatchSaveFirst = NewCheckBox("每个项目归档前先保存", true);
            _chkBatchUpgrade = NewCheckBox("旧版本项目升级打开", false);
            _chkBatchUpgrade.CheckedChanged += OnBatchKernelOptionChanged;
            batchOptions.Controls.Add(_chkBatchSaveFirst);
            batchOptions.Controls.Add(_chkBatchUpgrade);
            gridSettings.Controls.Add(batchOptions, 1, 2);
            gridSettings.SetColumnSpan(batchOptions, 2);

            cardSettings.Controls.Add(gridSettings);
            _batchSettingsCard = cardSettings;
            root.Controls.Add(cardSettings, 0, 1);

            // ── 卡片 3：项目列表
            Panel cardList = UiTheme.CreateCard();
            cardList.Padding = new Padding(14, 10, 14, 10);

            Panel listToolbar = new Panel();
            listToolbar.Dock = DockStyle.Top;
            listToolbar.Height = 34;

            _lblBatchSummary = new Label();
            _lblBatchSummary.Text = "尚未扫描。点击“扫描项目”列出文件夹里的 TIA 项目。";
            _lblBatchSummary.Font = UiTheme.Body;
            _lblBatchSummary.ForeColor = UiTheme.TextPrimary;
            _lblBatchSummary.AutoSize = true;
            _lblBatchSummary.Location = new Point(0, 8);
            listToolbar.Controls.Add(_lblBatchSummary);

            Button btnSelectAll = new Button();
            btnSelectAll.Text = "全选";
            btnSelectAll.Width = 76;
            UiTheme.StyleSecondaryButton(btnSelectAll);
            btnSelectAll.Click += OnBatchSelectAll;
            listToolbar.Controls.Add(btnSelectAll);

            Button btnSelectNone = new Button();
            btnSelectNone.Text = "全不选";
            btnSelectNone.Width = 88;
            UiTheme.StyleSecondaryButton(btnSelectNone);
            btnSelectNone.Click += OnBatchSelectNone;
            listToolbar.Controls.Add(btnSelectNone);

            Button btnInvert = new Button();
            btnInvert.Text = "反选";
            btnInvert.Width = 76;
            UiTheme.StyleSecondaryButton(btnInvert);
            btnInvert.Click += OnBatchInvert;
            listToolbar.Controls.Add(btnInvert);

            // 「设置收起」开关：把上面两张设置卡片收掉，把高度全让给项目列表。
            // 起因是用户反馈"项目多的时候只能看到 3 行" —— 列表区被固定高度的设置区挤压，
            // 而扫描完之后用户主要在列表里勾选，设置区其实用不上了。
            _btnBatchToggleSettings = new Button();
            _btnBatchToggleSettings.Text = "收起设置";
            _btnBatchToggleSettings.Width = 104;
            UiTheme.StyleSecondaryButton(_btnBatchToggleSettings);
            _btnBatchToggleSettings.Click += delegate { SetBatchSettingsCollapsed(!_batchSettingsCollapsed); };
            // 说明文字统一在 InitializeComponent 里挂（那时 _toolTip 才建好）
            listToolbar.Controls.Add(_btnBatchToggleSettings);

            // 位置一律用按钮自己的 Width 算，不写死 76/88/76。
            // 写死的话在 175% 缩放的高分屏上按钮实际是 133/154/133 宽，
            // 按 76 算出来的位置会让三个按钮叠在一起。
            listToolbar.Resize += delegate
            {
                int right = listToolbar.ClientSize.Width;
                int gap = UiTheme.Scale(listToolbar, 6);
                int top = UiTheme.Scale(listToolbar, 1);
                btnInvert.Location = new Point(Math.Max(0, right - btnInvert.Width), top);
                btnSelectNone.Location = new Point(
                    Math.Max(0, right - btnInvert.Width - btnSelectNone.Width - gap), top);
                btnSelectAll.Location = new Point(
                    Math.Max(0, right - btnInvert.Width - btnSelectNone.Width
                        - btnSelectAll.Width - gap * 2), top);
                _btnBatchToggleSettings.Location = new Point(
                    Math.Max(0, right - btnInvert.Width - btnSelectNone.Width - btnSelectAll.Width
                        - _btnBatchToggleSettings.Width - gap * 3), top);
            };

            _gridBatch = BuildBatchGrid();

            cardList.Controls.Add(_gridBatch);
            cardList.Controls.Add(listToolbar);
            root.Controls.Add(cardList, 0, 2);


            // ── 操作行
            Panel actionRow = NewActionRow();
            _btnBatchStart = NewPrimaryButton("开始批量归档", OnBatchStart);
            _btnBatchCancel = new Button();
            _btnBatchCancel.Text = "中止剩余";
            _btnBatchCancel.Width = 110;
            UiTheme.StyleSecondaryButton(_btnBatchCancel);
            _btnBatchCancel.Height = 36;
            _btnBatchCancel.Visible = false;
            _btnBatchCancel.Click += OnBatchCancel;

            actionRow.Controls.Add(_btnBatchStart);
            actionRow.Controls.Add(_btnBatchCancel);

            // 提示文字用 Dock=Left（与其它页一致，那里已验证可用）；
            // 直接摆坐标时 Label 不会自动适应操作行高度，容易看不见。
            // 宽度按"规则 = 后缀 _bak + 时间戳 yyyyMMdd_HHmmss"的最长情形给足，
            // 否则两个规则都勾上时会显示成 "规则 = 时" 这种半截话。
            _lblBatchHint = NewHintLabel("提示：实例方式与文件名规则都沿用“归档项目”页的设置");
            _lblBatchHint.Dock = DockStyle.Left;
            _lblBatchHint.Width = 660;
            _lblBatchHint.Height = 36;
            _lblBatchHint.AutoSize = false;
            _lblBatchHint.TextAlign = ContentAlignment.MiddleLeft;
            actionRow.Controls.Add(_lblBatchHint);

            actionRow.Resize += delegate
            {
                int right = actionRow.ClientSize.Width;
                _btnBatchStart.Location = new Point(Math.Max(0, right - _btnBatchStart.Width), 0);
                _btnBatchCancel.Location = new Point(
                    Math.Max(0, right - _btnBatchStart.Width - _btnBatchCancel.Width
                        - UiTheme.Scale(actionRow, 8)), 0);
            };

            root.Controls.Add(actionRow, 0, 3);

            return root;
        }

        private DataGridView BuildBatchGrid()
        {
            DataGridView grid = new DataGridView();
            grid.Dock = DockStyle.Fill;
            grid.BackgroundColor = Color.White;
            grid.BorderStyle = BorderStyle.FixedSingle;
            grid.Font = UiTheme.Body;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeRows = false;
            grid.RowHeadersVisible = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.MultiSelect = true;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
            grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            grid.ColumnHeadersHeight = BatchHeaderHeight;
            grid.RowTemplate.Height = BatchRowHeight;
            grid.EnableHeadersVisualStyles = false;
            grid.GridColor = UiTheme.Border;

            grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(0xED, 0xF1, 0xF4);
            grid.ColumnHeadersDefaultCellStyle.ForeColor = UiTheme.TextPrimary;
            grid.ColumnHeadersDefaultCellStyle.Font = UiTheme.BodyBold;
            grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Color.FromArgb(0xED, 0xF1, 0xF4);
            grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(0xD8, 0xE8, 0xEC);
            grid.DefaultCellStyle.SelectionForeColor = UiTheme.TextPrimary;

            // 列宽一律按 96 DPI 的基准值写，并把这个基准值记在列的 Tag 里 ——
            // DataGridView 的列**不是控件**，WinForms 那套按 DPI 自动缩放管不到它，
            // 必须在窗口句柄建好之后由 ApplyGridDpiScale() 自己换算一次。
            // 否则高分屏上表头文字按 1.75 倍画、列宽还是 1 倍，"归档"就被裁成"归"。
            DataGridViewCheckBoxColumn columnCheck = new DataGridViewCheckBoxColumn();
            columnCheck.HeaderText = "归档";
            columnCheck.Width = DesignColumnWidth(columnCheck, 64);
            columnCheck.SortMode = DataGridViewColumnSortMode.NotSortable;
            columnCheck.Resizable = DataGridViewTriState.False;

            DataGridViewTextBoxColumn columnName = new DataGridViewTextBoxColumn();
            columnName.HeaderText = "项目文件";
            columnName.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            columnName.MinimumWidth = 180;
            columnName.ReadOnly = true;

            // 版本列：显示项目自身的版本（V19 / V16 …），并按"与当前内核的关系"着色：
            // 同版本=绿（可直接归档）、较旧=橙（需勾升级打开）、较新=红（当前内核打不开）。
            DataGridViewTextBoxColumn columnVersion = new DataGridViewTextBoxColumn();
            columnVersion.HeaderText = "版本";
            columnVersion.Width = DesignColumnWidth(columnVersion, 64);
            columnVersion.ReadOnly = true;

            DataGridViewTextBoxColumn columnFolder = new DataGridViewTextBoxColumn();
            columnFolder.HeaderText = "所在位置";
            columnFolder.Width = DesignColumnWidth(columnFolder, 330);
            columnFolder.ReadOnly = true;

            DataGridViewTextBoxColumn columnSize = new DataGridViewTextBoxColumn();
            columnSize.HeaderText = "大小";
            columnSize.Width = DesignColumnWidth(columnSize, 88);
            columnSize.ReadOnly = true;

            DataGridViewTextBoxColumn columnTime = new DataGridViewTextBoxColumn();
            columnTime.HeaderText = "修改时间";
            columnTime.Width = DesignColumnWidth(columnTime, 132);
            columnTime.ReadOnly = true;

            grid.Columns.Add(columnCheck);
            grid.Columns.Add(columnName);
            grid.Columns.Add(columnVersion);
            grid.Columns.Add(columnFolder);
            grid.Columns.Add(columnSize);
            grid.Columns.Add(columnTime);

            // 复选框必须显式提交，否则值要等焦点离开才生效，读出来是旧的
            grid.CurrentCellDirtyStateChanged += delegate
            {
                if (grid.IsCurrentCellDirty)
                {
                    grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
                }
            };
            grid.CellValueChanged += OnBatchGridCellValueChanged;

            return grid;
        }

        /// <summary>
        /// 把一列的"设计列宽"（96 DPI 基准值）记进列的 Tag 并返回它。
        /// 记下来是为了让 ApplyGridDpiScale() 在窗口句柄建好之后再按真实 DPI 换算一次 ——
        /// 建控件的时候窗口还没有句柄，那一刻取到的 DPI 不一定准。
        /// </summary>
        private static int DesignColumnWidth(DataGridViewColumn column, int designPixels)
        {
            column.Tag = designPixels;
            return designPixels;
        }

        /// <summary>
        /// 按当前屏幕 DPI 重算列表的列宽与行高。
        ///
        /// DataGridView 的列宽、行高是普通属性，不在控件树里，WinForms 的
        /// AutoScaleMode 缩放扫不到它们，只能自己算。窗口显示后调用一次即可，
        /// 只影响显示宽度，不动任何数据。
        /// </summary>
        internal void ApplyGridDpiScale()
        {
            if (_gridBatch == null)
            {
                return;
            }

            foreach (DataGridViewColumn column in _gridBatch.Columns)
            {
                if (column.Tag is int)
                {
                    column.Width = UiTheme.Scale(_gridBatch, (int)column.Tag);
                }
            }

            _gridBatch.ColumnHeadersHeight = UiTheme.Scale(_gridBatch, BatchHeaderHeight);
            _gridBatch.RowTemplate.Height = UiTheme.Scale(_gridBatch, BatchRowHeight);

            foreach (DataGridViewRow row in _gridBatch.Rows)
            {
                row.Height = _gridBatch.RowTemplate.Height;
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        //  扫描
        // ═══════════════════════════════════════════════════════════════════

        private void OnBrowseBatchFolder(object sender, EventArgs e)
        {
            string current = GuiSettings.DirectoryOf(_txtBatchFolder.Text);
            string selected;

            // 优先用现代文件夹对话框（与归档页"浏览"的观感一致）；老系统上会返回 false，走下面的兜底
            if (ModernFolderDialog.TryShow(this, "选择包含 TIA 项目的文件夹", current, out selected))
            {
                if (!string.IsNullOrEmpty(selected))
                {
                    _txtBatchFolder.Text = selected;
                    _settings.SetString("LastBatchFolder", selected);
                    _settings.Save();
                }
                return;
            }

            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = "选择包含 TIA 项目的文件夹";
                dialog.ShowNewFolderButton = false;

                // current 已在方法开头算好（现代对话框与这个兜底共用）
                if (!string.IsNullOrEmpty(current))
                {
                    dialog.SelectedPath = current;
                }

                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    _txtBatchFolder.Text = dialog.SelectedPath;
                    _settings.SetString("LastBatchFolder", dialog.SelectedPath);
                    _settings.Save();
                }
            }
        }

        private void OnBrowseBatchOutput(object sender, EventArgs e)
        {
            string currentOutput = GuiSettings.DirectoryOf(_txtBatchOutputDir.Text);
            string selectedOutput;

            if (ModernFolderDialog.TryShow(
                this, "选择归档文件的输出目录（每个项目生成一个同名 .zapXX，扩展名随所用 TIA 版本）",
                currentOutput, out selectedOutput))
            {
                if (!string.IsNullOrEmpty(selectedOutput))
                {
                    _txtBatchOutputDir.Text = selectedOutput;
                    _settings.SetString("LastBatchOutputDir", selectedOutput);
                    _settings.Save();
                }
                return;
            }

            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = "选择归档文件的输出目录（每个项目生成一个同名 .zapXX，扩展名随所用 TIA 版本）";
                dialog.ShowNewFolderButton = true;

                string current = GuiSettings.DirectoryOf(_txtBatchOutputDir.Text);
                if (!string.IsNullOrEmpty(current))
                {
                    dialog.SelectedPath = current;
                }

                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    _txtBatchOutputDir.Text = dialog.SelectedPath;
                    _settings.SetString("LastBatchOutputDir", dialog.SelectedPath);
                    _settings.Save();
                }
            }
        }

        private void OnBatchScan(object sender, EventArgs e)
        {
            if (_busy)
            {
                return;
            }

            string folder = _txtBatchFolder.Text.Trim();
            if (folder.Length == 0)
            {
                Warn("请先选择要扫描的文件夹。");
                return;
            }

            if (!Directory.Exists(folder))
            {
                Warn("文件夹不存在：\r\n" + folder);
                return;
            }

            bool recursive = _chkBatchRecursive.Checked;
            bool ignoreBackup = _chkBatchIgnoreBackup.Checked;
            bool verbose = _chkVerbose.Checked;

            _batchEntries.Clear();
            RefreshBatchGrid();
            _lblBatchSummary.Text = "正在扫描…";

            _settings.SetString("LastBatchFolder", folder);
            _settings.SetBool("BatchRecursive", recursive);
            _settings.SetBool("BatchIgnoreBackup", ignoreBackup);
            _settings.Save();

            SetBusy(true, "正在扫描文件夹…");

            Thread worker = new Thread(delegate ()
            {
                Logger logger = new Logger(verbose, new UiLogSink(this, AppendLog));
                try
                {
                    logger.Section("扫描 TIA 项目");
                    List<TiaProjectEntry> found = BatchScanner.Scan(folder, recursive, ignoreBackup, logger);

                    if (found.Count == 0)
                    {
                        logger.Warning("该文件夹下没有找到 TIA 项目文件（*.apXX）。");
                        logger.Info("提示：TIA 项目文件形如 Demo.ap21，通常位于与项目同名的文件夹里。");
                    }
                    else
                    {
                        logger.Ok("可以在列表里勾选需要归档的项目，然后点“开始批量归档”。");
                    }

                    UiInvoke(delegate { LoadBatchEntries(found); });
                }
                catch (Exception ex)
                {
                    logger.Error("扫描失败：" + ex.Message);
                    UiInvoke(delegate { _lblBatchSummary.Text = "扫描失败，详见运行日志。"; });
                }
                finally
                {
                    SetBusy(false, "扫描完成。");
                }
            });

            worker.IsBackground = true;
            worker.Name = "BatchScanWorker";
            worker.Start();
        }

        /// <summary>
        /// 把扫描结果装进列表（默认全选，取消个别即可）。
        /// </summary>
        /// <param name="entries">扫描结果。</param>
        private void LoadBatchEntries(List<TiaProjectEntry> entries)
        {
            _batchEntries = entries ?? new List<TiaProjectEntry>();

            // 默认全选：批量备份的常见需求是"这个文件夹里的我都要"，
            // 只有个别不归档的才需要手动取消；且开始前还有一次汇总确认。
            foreach (TiaProjectEntry entry in _batchEntries)
            {
                entry.IsSelected = true;
            }

            RefreshBatchGrid();
        }

        /// <summary>
        /// 相对"当前内核版本"评价一个项目：同版本可直接归档 / 较旧需勾升级打开 / 较新打不开。
        /// 这句话会挂到版本列的悬停提示上。
        /// </summary>
        private string DescribeVersionStatus(TiaProjectEntry entry)
        {
            int kernel = CurrentKernelMajorVersion();

            if (entry == null || entry.MajorVersion <= 0)
            {
                return "认不出项目版本（文件名不是 .apXX 形式）。";
            }

            if (kernel <= 0)
            {
                return "V" + entry.MajorVersion + "：还没探测到本机 Openness 内核，先看“环境探测”页。";
            }

            if (entry.MajorVersion == kernel)
            {
                return "V" + entry.MajorVersion + "：与当前内核同版本，可以直接归档（产物 .zap"
                    + kernel + "）。";
            }

            if (entry.MajorVersion < kernel)
            {
                return "V" + entry.MajorVersion + "：比当前内核（V" + kernel + "）旧，直接打开会失败；"
                    + "需勾选“旧版本项目升级打开”——那会把项目升级到 V" + kernel
                    + "（同时勾了“先保存”就会覆盖磁盘上的原项目，不可逆）。"
                    + "想保留原版本，请把内核切到 V" + entry.MajorVersion + " 再归档。";
            }

            return "V" + entry.MajorVersion + "：比当前内核（V" + kernel + "）新，当前内核打不开；"
                + "请到“环境探测”页把“可用版本”切到 V" + entry.MajorVersion + "。";
        }

        /// <summary>版本列的着色（绿=可直接归档 / 橙=需升级 / 红=打不开）。</summary>
        private Color VersionStatusColor(TiaProjectEntry entry)
        {
            int kernel = CurrentKernelMajorVersion();
            if (entry == null || entry.MajorVersion <= 0 || kernel <= 0)
            {
                return UiTheme.TextSecondary;
            }

            if (entry.MajorVersion == kernel)
            {
                return Color.FromArgb(0x1A, 0x7F, 0x37);
            }

            return entry.MajorVersion < kernel
                ? Color.FromArgb(0xB2, 0x6A, 0x00)
                : UiTheme.Danger;
        }

        /// <summary>
        /// 刷新版本列的颜色与悬停提示（换内核版本时调用，不重建列表）。
        /// </summary>
        private void RefreshVersionHints()
        {
            if (_gridBatch != null)
            {
                foreach (DataGridViewRow row in _gridBatch.Rows)
                {
                    TiaProjectEntry entry = row.Tag as TiaProjectEntry;
                    if (entry == null || row.Cells.Count < 3)
                    {
                        continue;
                    }

                    row.Cells[2].Style.ForeColor = VersionStatusColor(entry);
                    row.Cells[2].ToolTipText = DescribeVersionStatus(entry);
                }
            }

            UpdateBatchSummary();
            ReportVersionMixToStatusBar();
        }

        /// <summary>
        /// 把"已勾选项目的版本与内核的关系"写进状态栏。
        ///
        /// 只在**需要提醒**（有较旧 / 较新的项目）时才写 —— 全都同版本时保持状态栏原样，
        /// 免得用户每点一次勾选都被刷一遍。详细说明在版本列的悬停提示里。
        /// </summary>
        private void ReportVersionMixToStatusBar()
        {
            int kernel = CurrentKernelMajorVersion();
            if (kernel <= 0 || _batchEntries.Count == 0)
            {
                return;
            }

            int selected = 0, older = 0, newer = 0, newerMax = 0;
            foreach (TiaProjectEntry entry in _batchEntries)
            {
                if (!entry.IsSelected)
                {
                    continue;
                }

                selected++;
                if (entry.MajorVersion <= 0)
                {
                    continue;
                }

                if (entry.MajorVersion < kernel)
                {
                    older++;
                }
                else if (entry.MajorVersion > kernel)
                {
                    newer++;
                    if (entry.MajorVersion > newerMax)
                    {
                        newerMax = entry.MajorVersion;
                    }
                }
            }

            if (selected == 0 || (older == 0 && newer == 0))
            {
                return;
            }

            List<string> parts = new List<string>();
            if (older > 0)
            {
                parts.Add(older + " 个比内核旧（需勾“升级打开”，或把内核切到对应版本以保留原版本）");
            }
            if (newer > 0)
            {
                parts.Add(newer + " 个比内核新（当前内核打不开，请切到 V" + newerMax + "）");
            }

            SetStatus("已勾选 " + selected + " 个：内核 V" + kernel + " 下，" + string.Join("；", parts.ToArray()));
        }

        private void RefreshBatchGrid()
        {
            if (_gridBatch == null)
            {
                return;
            }

            _suppressGridEvents = true;
            try
            {
                _gridBatch.Rows.Clear();
                foreach (TiaProjectEntry entry in _batchEntries)
                {
                    int index = _gridBatch.Rows.Add(
                        entry.IsSelected,
                        entry.FileName,
                        entry.VersionText,
                        entry.DirectoryName,
                        entry.SizeText,
                        entry.TimeText);
                    _gridBatch.Rows[index].Tag = entry;
                }
            }
            finally
            {
                _suppressGridEvents = false;
            }

            RefreshVersionHints();
        }

        private void UpdateBatchSummary()
        {
            if (_lblBatchSummary == null)
            {
                return;
            }

            int total = _batchEntries.Count;
            int selected = CountSelected();

            if (total == 0)
            {
                _lblBatchSummary.Text = "尚未扫描。点击“扫描项目”列出文件夹里的 TIA 项目。";
            }
            else
            {
                // 刻意写短：这行文字和右边四个按钮抢同一行的宽度，
                // "每个项目生成一个同名 .zapXX" 这类补充说明放 ToolTip 里（见 BuildBatchPage）。
                _lblBatchSummary.Text = "找到 " + total + " 个项目，已勾选 " + selected + " 个";
            }

            if (_btnBatchStart != null)
            {
                _btnBatchStart.Enabled = !_busy && selected > 0;
            }
        }

        /// <summary>
        /// 本机是否装了某个主版本的 Openness（按"环境探测"枚举到的版本判断）。
        /// </summary>
        private bool HasOpennessVersion(int majorVersion)
        {
            foreach (TiaEnvironmentInfo info in _availableVersions)
            {
                if (info != null && info.MajorVersion == majorVersion)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 算出一个项目会被怎么处理（用于确认框里逐项说明"会发生什么"）。
        /// </summary>
        private string DescribeKernelDisposition(TiaProjectEntry entry, int kernel, bool autoKernel)
        {
            if (entry == null)
            {
                return string.Empty;
            }

            if (entry.MajorVersion <= 0)
            {
                return "→ 版本未知，交给 TIA 自己判断";
            }

            if (kernel <= 0)
            {
                return "→ 内核未定（先看“环境探测”页）";
            }

            if (entry.MajorVersion == kernel)
            {
                return "→ 用本进程内核 V" + kernel + " 归档";
            }

            if (entry.MajorVersion > kernel)
            {
                return "→ ⚠ 比内核新，当前内核打不开（会被跳过）";
            }

            if (autoKernel && HasOpennessVersion(entry.MajorVersion))
            {
                return "→ 用 V" + entry.MajorVersion + " 内核原生归档（子进程，不升级）";
            }

            if (_chkBatchUpgrade.Checked)
            {
                return "→ 用 V" + kernel + " 升级打开（产物 .zap" + kernel + "）";
            }

            return "→ ⚠ 本机无 V" + entry.MajorVersion + " 内核且未勾升级（会被跳过）";
        }

        /// <summary>
        /// 算出"这个项目最终由哪个内核版本归档"，用于推导归档产物的扩展名。
        ///
        /// 判据与 <see cref="DescribeKernelDisposition"/> 必须保持**完全一致**
        /// （那边是给人看的文字，这里是给文件名用的版本号）—— 改一个就要改另一个，
        /// 否则确认框里说"用 V16 原生归档"、产出的名字却是 .zap21。
        ///   · 项目版本未知                       → 按内核（认不出就别猜项目版本）
        ///   · 内核未定                           → 按项目版本（与 ArchiveNaming 的兜底一致）
        ///   · 与内核同版本                       → 内核版本
        ///   · 比内核新                           → 按内核（其实会被提前拦下/跳过）
        ///   · 更旧 + 自动选内核且本机有该版本     → 项目版本（子进程原生归档，不升级）
        ///   · 更旧 + 升级打开（或本机无该版本）   → 内核版本（升级打开后就是内核格式）
        /// </summary>
        /// <param name="entry">项目。</param>
        /// <param name="kernel">当前选定的内核主版本号；0 表示未定。</param>
        /// <param name="autoKernel">是否勾了"按项目版本自动选内核"。</param>
        /// <param name="upgrade">是否勾了"旧版本项目升级打开"。</param>
        /// <returns>归档产物应按哪个主版本命名（0 表示推不出来，交给下层兜底）。</returns>
        private int ResolveProductKernelVersion(
            TiaProjectEntry entry, int kernel, bool autoKernel, bool upgrade)
        {
            if (entry == null || entry.MajorVersion <= 0)
            {
                return kernel;
            }

            if (kernel <= 0)
            {
                return entry.MajorVersion;
            }

            if (entry.MajorVersion >= kernel)
            {
                return kernel;
            }

            // 比内核旧：只有"自动选内核 + 本机装了该版本"才是用它自己的版本原生归档，
            // 其余情况（升级打开、本机没装该版本）都由当前内核处理。
            if (autoKernel && HasOpennessVersion(entry.MajorVersion))
            {
                return entry.MajorVersion;
            }

            return kernel;
        }

        /// <summary>
        /// 批量页两个"内核相关"选项的联动。
        ///
        /// ★ 它们其实是**两条互斥的路**，同时勾会让"升级打开"被静默忽略（用户反馈过这个困惑）：
        ///   · 按项目版本自动选内核 = 旧项目用它**自己的版本**原生归档，根本不升级；
        ///   · 升级打开           = 旧项目统统用**当前内核**升级打开。
        /// 所以这里做互斥：勾一个就自动取消另一个，并在状态栏说明为什么。
        ///
        /// ★ 必须看 sender 判断"用户刚勾上的是哪一个"：
        ///   以前两个分支的条件写成了一模一样（都是"两个都勾着"），于是只有第一个分支会命中，
        ///   结果用户去勾"升级打开"时反被程序把它取消掉 —— **想勾的那个永远勾不上**，
        ///   第二个分支则成了永不可达的死代码。
        /// </summary>
        private void OnBatchKernelOptionChanged(object sender, EventArgs e)
        {
            if (_chkBatchAutoKernel == null || _chkBatchUpgrade == null)
            {
                return;
            }

            // 程序自己改另一个勾选框时不再往下走：避免重入把说明文字覆盖成"内核策略：……"。
            if (_suppressKernelOptionEvents)
            {
                return;
            }

            int kernel = CurrentKernelMajorVersion();

            if (_chkBatchAutoKernel.Checked && _chkBatchUpgrade.Checked)
            {
                // 用户刚勾上的是"升级打开" → 取消"自动选内核"；否则取消"升级打开"。
                _suppressKernelOptionEvents = true;
                try
                {
                    if (ReferenceEquals(sender, _chkBatchUpgrade))
                    {
                        _chkBatchAutoKernel.Checked = false;
                        SetStatus("已自动取消“按项目版本自动选内核”：勾了“升级打开”后，"
                            + "所有旧项目都会用 V" + kernel + " 升级打开。"
                            + "想保留旧项目原版本，请改勾“自动选内核”。");
                    }
                    else
                    {
                        _chkBatchUpgrade.Checked = false;
                        SetStatus("已自动取消“旧版本项目升级打开”：勾了“按项目版本自动选内核”后，"
                            + "旧项目会用它自己的版本原生归档（不升级）。"
                            + "想统一升级到 V" + kernel + "，请取消“自动选内核”再勾“升级打开”。");
                    }
                }
                finally
                {
                    _suppressKernelOptionEvents = false;
                }
            }
            else if (_chkBatchAutoKernel.Checked)
            {
                SetStatus("内核策略：与当前内核（V" + kernel + "）不同的项目，"
                    + "用它自己的版本原生归档（不升级）；本机没装对应版本的会被跳过并提示。");
            }
            else if (_chkBatchUpgrade.Checked)
            {
                SetStatus("内核策略：全部项目用 V" + kernel + " 内核，旧项目升级打开"
                    + "（不勾“先保存”就不会改动磁盘上的原项目）。");
            }
            else
            {
                SetStatus("内核策略：全部用 V" + kernel + " 内核；旧版本项目若本机没有对应 Openness 会打开失败。");
            }

            if (_toolTip != null)
            {
                _toolTip.SetToolTip(_chkBatchAutoKernel,
                    "与当前内核版本不同的项目，按它**自己的版本**起子进程原生归档：不升级、"
                    + "产物也是那个版本（.ap16 → .zap16）。\r\n"
                    + "前提是本机装了那个版本的 TIA + Openness；没装的会被跳过并提示。\r\n"
                    + "与“旧版本项目升级打开”互斥（两者是两条不同的路）。");
                _toolTip.SetToolTip(_chkBatchUpgrade,
                    "所有旧版本项目都用当前内核（V" + kernel + "）升级打开，产物是 .zap" + kernel + "。\r\n"
                    + "只升级**内存**：不勾“每个项目归档前先保存”就不会改动磁盘上的原项目。\r\n"
                    + "与“按项目版本自动选内核”互斥。");
            }
        }

        /// <summary>
        /// 收起 / 展开批量页上面的两张设置卡片，把高度让给项目列表。
        ///
        /// 起因：用户反馈"项目多的时候列表只能看到 3 行"。列表区是"占剩余"的行，
        /// 上面两张固定高度的卡片 + 一行操作行把它挤扁了；而扫描完之后用户主要在列表里勾选，
        /// 设置区其实用不着一直占着。收起后列表能多显示 5~6 行。
        /// </summary>
        private void SetBatchSettingsCollapsed(bool collapsed)
        {
            _batchSettingsCollapsed = collapsed;

            if (_batchRoot != null && _batchRoot.RowStyles.Count >= 2)
            {
                // 行高直接置 0（而不是只藏控件）：TableLayoutPanel 的固定行不会因为
                // 控件隐藏就自动让位，必须把行本身收到 0，高度才会落到"占剩余"的列表行上。
                if (collapsed)
                {
                    // ★ 收起前把**当前真实行高**记下来。
                    //   不能拿 CardTwoRows / CardThreeRows 去还原：它们是 96 DPI 的设计值，
                    //   而实际行高已经被按屏幕 DPI 放大过（175% 下 112 → 196）。
                    //   用设计值还原会把卡片压扁 —— 这正是"展开后部分按钮看不见了"的原因。
                    _batchSourceRowHeight = _batchRoot.RowStyles[0].Height;
                    _batchSettingsRowHeight = _batchRoot.RowStyles[1].Height;
                }

                _batchRoot.RowStyles[0].SizeType = SizeType.Absolute;
                _batchRoot.RowStyles[1].SizeType = SizeType.Absolute;

                if (collapsed)
                {
                    _batchRoot.RowStyles[0].Height = 0;
                    _batchRoot.RowStyles[1].Height = 0;
                }
                else
                {
                    // 记录值为空（例如程序内部在布局完成前就调用过）时，退回"按当前 DPI 换算的设计值"
                    _batchRoot.RowStyles[0].Height = _batchSourceRowHeight > 0
                        ? _batchSourceRowHeight
                        : UiTheme.Scale(_batchRoot, CardTwoRows);
                    _batchRoot.RowStyles[1].Height = _batchSettingsRowHeight > 0
                        ? _batchSettingsRowHeight
                        : UiTheme.Scale(_batchRoot, CardThreeRows);

                    // 还原后把记录清掉，下次收起重记一遍（窗口被拖到别的 DPI 屏上也不会用旧值）
                    _batchSourceRowHeight = 0;
                    _batchSettingsRowHeight = 0;
                }
            }

            if (_batchSourceCard != null)
            {
                _batchSourceCard.Visible = !collapsed;
            }
            if (_batchSettingsCard != null)
            {
                _batchSettingsCard.Visible = !collapsed;
            }

            if (_btnBatchToggleSettings != null)
            {
                _btnBatchToggleSettings.Text = collapsed ? "展开设置" : "收起设置";
                // 收起时给个明显的底色提示，免得用户忘了设置区被藏起来了
                _btnBatchToggleSettings.BackColor = collapsed
                    ? UiTheme.WarningBack
                    : UiTheme.Surface;
                _btnBatchToggleSettings.ForeColor = collapsed
                    ? UiTheme.Warning
                    : UiTheme.TextPrimary;
            }

            SetStatus(collapsed
                ? "已收起“项目文件夹 / 归档设置”，项目列表已放大；再点“展开设置”可展开。"
                : "已展开“项目文件夹 / 归档设置”。");
        }

        private int CountSelected()
        {
            int count = 0;
            foreach (TiaProjectEntry entry in _batchEntries)
            {
                if (entry.IsSelected)
                {
                    count++;
                }
            }
            return count;
        }

        private void OnBatchGridCellValueChanged(object sender, DataGridViewCellEventArgs e)
        {
            if (_suppressGridEvents || e.RowIndex < 0 || e.ColumnIndex != 0)
            {
                return;
            }

            DataGridViewRow row = _gridBatch.Rows[e.RowIndex];
            TiaProjectEntry entry = row.Tag as TiaProjectEntry;
            if (entry == null)
            {
                return;
            }

            object value = row.Cells[0].Value;
            entry.IsSelected = value != null && Convert.ToBoolean(value);
            UpdateBatchSummary();
        }

        private void OnBatchSelectAll(object sender, EventArgs e)
        {
            SetAllBatchSelection(true);
        }

        private void OnBatchSelectNone(object sender, EventArgs e)
        {
            SetAllBatchSelection(false);
        }

        private void OnBatchInvert(object sender, EventArgs e)
        {
            foreach (TiaProjectEntry entry in _batchEntries)
            {
                entry.IsSelected = !entry.IsSelected;
            }
            RefreshBatchGrid();
        }

        private void SetAllBatchSelection(bool selected)
        {
            foreach (TiaProjectEntry entry in _batchEntries)
            {
                entry.IsSelected = selected;
            }
            RefreshBatchGrid();
        }

        /// <summary>
        /// 把归档页当前的文件名规则同步到批量页的提示上。
        /// 批量页不再单独放一套规则控件（列表区已经很挤），而是共用归档页的设置 ——
        /// 与"实例方式沿用归档页"的处理保持一致。
        /// </summary>
        private void UpdateBatchNamingHint()
        {
            if (_lblBatchHint == null || _lblBatchHint.IsDisposed)
            {
                return;
            }

            string suffix;
            string timestampFormat;
            ReadNamingRule(out suffix, out timestampFormat);

            _lblBatchHint.Text = BuildNamingRuleSummary(suffix, timestampFormat);
        }

        /// <summary>
        /// 生成批量页的"文件名规则 = ..."提示文本。
        ///
        /// 两项用 " + " 连接，顺序与实际文件命名保持一致：项目名_后缀_时间戳。
        /// 时间戳这里显示**格式串**而不是当前时刻 —— 它描述的是规则本身，
        /// 实际时间戳在归档那一刻生成，写成固定值反而会误导。
        ///
        /// 抽成独立方法是为了能被界面自检直接验证（包括用 MeasureString 量宽度，
        /// 防止文字过长被 Label 截成"规则 = 时"那种半截话）。
        /// </summary>
        /// <param name="suffix">自定义后缀，可为空。</param>
        /// <param name="timestampFormat">时间戳格式，可为空。</param>
        /// <returns>完整提示文本。</returns>
        internal static string BuildNamingRuleSummary(string suffix, string timestampFormat)
        {
            if (string.IsNullOrEmpty(suffix) && string.IsNullOrEmpty(timestampFormat))
            {
                return "文件名规则 = 无（直接用原文件名）";
            }

            List<string> parts = new List<string>();

            if (!string.IsNullOrEmpty(suffix))
            {
                parts.Add("后缀 " + ArchiveNaming.NormalizeSuffix(suffix));
            }

            if (!string.IsNullOrEmpty(timestampFormat))
            {
                parts.Add("时间戳 " + timestampFormat);
            }

            return "文件名规则 = " + string.Join(" + ", parts.ToArray());
        }

        /// <summary>
        /// 保证批量任务里每个输出路径互不重复；avoidDiskFiles=true 时还要避开磁盘上已有的文件。
        ///
        /// 为什么批内也得查重：不同目录下可能有两个同名项目（A\Demo.ap21 和 B\Demo.ap21），
        /// 算出来的输出名会一模一样，不处理的话后一个会冲掉前一个。
        /// </summary>
        /// <param name="items">批量项。</param>
        /// <param name="avoidDiskFiles">是否同时避开磁盘上已存在的文件。</param>
        private static void EnsureBatchUniqueOutputs(List<BatchArchiveItem> items, bool avoidDiskFiles)
        {
            HashSet<string> assigned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (BatchArchiveItem item in items)
            {
                string candidate = item.OutputPath;

                for (int attempt = 2; attempt < 1000; attempt++)
                {
                    bool diskConflict = avoidDiskFiles && File.Exists(candidate);
                    if (!diskConflict && !assigned.Contains(candidate))
                    {
                        break;
                    }

                    if (avoidDiskFiles)
                    {
                        candidate = ArchiveNaming.EnsureUniquePath(candidate);
                        if (!assigned.Contains(candidate))
                        {
                            break;
                        }
                    }

                    // 批内重复：推一个序号
                    string directory = Path.GetDirectoryName(candidate);
                    string name = Path.GetFileNameWithoutExtension(candidate);
                    string extension = Path.GetExtension(candidate);
                    candidate = Path.Combine(
                        directory == null ? string.Empty : directory,
                        name + "-" + attempt.ToString() + extension);
                }

                assigned.Add(candidate);
                item.OutputPath = candidate;
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        //  批量归档执行
        // ═══════════════════════════════════════════════════════════════════

        private void OnBatchCancel(object sender, EventArgs e)
        {
            if (!_batchRunning)
            {
                return;
            }

            _batchCancelRequested = true;
            AppendLog(LogLevel.Warning, "[WARN]",
                "已请求中止：当前正在归档的项目会跑完，之后不再处理剩余项目。");
            _btnBatchCancel.Enabled = false;
        }

        private void OnBatchStart(object sender, EventArgs e)
        {
            if (_busy)
            {
                return;
            }

            List<TiaProjectEntry> selected = new List<TiaProjectEntry>();
            foreach (TiaProjectEntry entry in _batchEntries)
            {
                if (entry.IsSelected)
                {
                    selected.Add(entry);
                }
            }

            if (selected.Count == 0)
            {
                Warn("还没有勾选任何项目。");
                return;
            }

            string outputDirectory = _txtBatchOutputDir.Text.Trim();
            if (outputDirectory.Length == 0)
            {
                Warn("请选择归档文件的输出目录。");
                return;
            }

            try
            {
                if (!Directory.Exists(outputDirectory))
                {
                    Directory.CreateDirectory(outputDirectory);
                }
            }
            catch (Exception ex)
            {
                Warn("输出目录不可用：" + ex.Message + "\r\n" + outputDirectory);
                return;
            }

            ModeItem mode = _cmbBatchMode.SelectedItem as ModeItem;
            if (mode == null)
            {
                Warn("请选择归档模式。");
                return;
            }

            bool packMode = mode.IsFolderPack;

            // ── 版本体检（在启动 TIA 之前，几十秒冷启动不该白等）：
            //    先把每个项目会被怎么处理算清楚，再处理"本机没有对应内核、又没勾升级"这种情况。
            //    注意"自动选内核"与"升级打开"是**互斥**的两条路（勾选时会互相取消），
            //    所以这里只有 autoKernel / upgrade 两种可能，不会出现"升级被静默忽略"的模糊地带。
            bool autoKernel = _chkBatchAutoKernel == null || _chkBatchAutoKernel.Checked;
            if (!packMode)
            {
                int kernel = CurrentKernelMajorVersion();
                int higherCount = 0;
                int highestProjectVersion = 0;
                int nativeCount = 0;
                int upgradeCount = 0;
                int skipCount = 0;

                foreach (TiaProjectEntry entry in selected)
                {
                    if (entry.MajorVersion <= 0)
                    {
                        continue;
                    }

                    if (entry.MajorVersion > highestProjectVersion)
                    {
                        highestProjectVersion = entry.MajorVersion;
                    }

                    if (kernel <= 0)
                    {
                        continue;
                    }

                    if (entry.MajorVersion > kernel)
                    {
                        higherCount++;
                        continue;
                    }

                    if (entry.MajorVersion == kernel)
                    {
                        continue;
                    }

                    // 比内核旧
                    if (autoKernel && HasOpennessVersion(entry.MajorVersion))
                    {
                        nativeCount++;
                    }
                    else if (_chkBatchUpgrade.Checked)
                    {
                        upgradeCount++;
                    }
                    else
                    {
                        skipCount++;
                    }
                }

                if (kernel > 0 && higherCount > 0)
                {
                    Warn("有 " + higherCount + " 个项目的版本比当前内核（V" + kernel + "）新，"
                        + "当前内核打不开这类项目。\r\n"
                        + "请先到“环境探测”页把“可用版本”切到 V" + highestProjectVersion
                        + "（或更高），再重新扫描归档。");
                    return;
                }

                if (skipCount > 0)
                {
                    // 按原计划这些项目会被"跳过"，等于白选了一批 —— 先问清楚再走
                    DialogResult answer = MessageBox.Show(
                        "有 " + skipCount + " 个项目比当前内核（V" + kernel + "）旧，"
                        + "而本机**没有装**它们那个版本的 Openness，也没有勾“旧版本项目升级打开”。\r\n\r\n"
                        + "按原计划这些项目会被**跳过**（不尝试归档，免得白等）。\r\n\r\n"
                        + "【是】 改用 V" + kernel + " 内核“升级打开”归档它们（会升级到 V" + kernel
                        + " 打开，产物 .zap" + kernel + "；不勾“每个项目归档前先保存”就不会改动磁盘上的原项目）\r\n"
                        + "【否】 照原计划：跳过它们，只归档能归档的\r\n"
                        + "【取消】 返回，我自己调整（例如给旧版本补装 Openness 组件）",
                        ToolTitle,
                        MessageBoxButtons.YesNoCancel,
                        MessageBoxIcon.Question,
                        MessageBoxDefaultButton.Button1);

                    if (answer == DialogResult.Cancel)
                    {
                        SetStatus("已取消。");
                        return;
                    }

                    if (answer == DialogResult.Yes)
                    {
                        // 切到"升级模式"：先关掉"自动选内核"（互斥，关它不会反向勾上什么），
                        // 再勾"升级打开"，这样不会触发互斥提示。
                        _chkBatchAutoKernel.Checked = false;
                        _chkBatchUpgrade.Checked = true;
                        autoKernel = false;
                        SetStatus("已改为用 V" + kernel + " 内核升级打开这些旧版本项目。");
                    }
                }
                else if (kernel > 0 && nativeCount > 0)
                {
                    SetStatus("内核策略：V" + kernel + " 的项目用本进程归档；另有 " + nativeCount
                        + " 个旧项目用**它自己的版本**原生归档（子进程，不升级）。");
                }
                else if (kernel > 0 && upgradeCount > 0)
                {
                    SetStatus("内核策略：全部用 V" + kernel + " 内核；其中 " + upgradeCount
                        + " 个旧项目会升级打开（产物 .zap" + kernel + "）。");
                }
            }

            if (mode.IsDangerous && !packMode)
            {
                DialogResult danger = MessageBox.Show(
                    "⚠ 批量归档所选模式：" + mode.Text + "\r\n\r\n"
                    + "该模式会**不可逆**地丢弃每个项目的可恢复数据，且会作用于全部 "
                    + selected.Count + " 个项目。\r\n\r\n确定要继续吗？",
                    ToolTitle,
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);

                if (danger != DialogResult.Yes)
                {
                    SetStatus("已取消（危险模式未确认）。");
                    return;
                }
            }

            if (!packMode && _chkBatchUpgrade.Checked)
            {
                // 与单文件归档同理："升级打开"+"归档前先保存"会覆盖原项目。
                // 批量场景下这个组合更危险 —— 一次会波及全部选中的项目。
                if (_chkBatchSaveFirst.Checked)
                {
                    DialogResult conflict = MessageBox.Show(
                        "检测到一个会破坏原项目的组合（将对全部 " + selected.Count + " 个项目生效）：\r\n\r\n"
                        + "  · 旧版本项目升级打开（OpenWithUpgrade）\r\n"
                        + "  · 每个项目归档前先保存\r\n\r\n"
                        + "两者一起用 = 每个旧版本项目都会被升级后**立即保存**，"
                        + "原项目文件被升级版本覆盖，此后旧版本 TIA 再也打不开。\r\n\r\n"
                        + "小知识：归档并不需要保存项目，不保存就不会改动磁盘上的原项目。\r\n\r\n"
                        + "【是】 两项都保留，接受原项目被升级覆盖\r\n"
                        + "【否】 自动取消“每个项目归档前先保存”，原项目保持旧版本（推荐）\r\n"
                        + "【取消】 返回，我自己调整",
                        ToolTitle,
                        MessageBoxButtons.YesNoCancel,
                        MessageBoxIcon.Warning,
                        MessageBoxDefaultButton.Button2);

                    if (conflict == DialogResult.Cancel)
                    {
                        SetStatus("已取消。");
                        return;
                    }

                    if (conflict == DialogResult.No)
                    {
                        _chkBatchSaveFirst.Checked = false;
                        SetStatus("已自动取消“每个项目归档前先保存”：原项目将保持旧版本。");
                    }
                }
                else
                {
                    DialogResult upgrade = MessageBox.Show(
                        "已勾选“旧版本项目升级打开”（OpenWithUpgrade）。\r\n\r\n"
                        + "全部 " + selected.Count + " 个项目中的旧版本项目都会被升级到本机 TIA 主版本，"
                        + "归档产物是 .zap<当前内核版本>（V19 内核即 .zap19）。\r\n"
                        + "因为你没有勾“每个项目归档前先保存”，磁盘上的原项目不会被改动。\r\n\r\n确定继续吗？",
                        ToolTitle,
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning,
                        MessageBoxDefaultButton.Button2);

                    if (upgrade != DialogResult.Yes)
                    {
                        SetStatus("已取消（未确认升级旧版本项目）。");
                        return;
                    }
                }
            }

            // 开始前的汇总确认：把"要动几个项目、动到哪、用什么模式"讲清楚
            StringBuilder preview = new StringBuilder();
            preview.AppendLine("即将批量归档 " + selected.Count + " 个项目：");
            preview.AppendLine();
            int shown = 0;
            foreach (TiaProjectEntry entry in selected)
            {
                if (shown >= 10)
                {
                    preview.AppendLine("  …… 还有 " + (selected.Count - shown) + " 个");
                    break;
                }
                preview.AppendLine("  · " + entry.FileName + "  "
                    + DescribeKernelDisposition(entry, CurrentKernelMajorVersion(),
                        _chkBatchAutoKernel == null || _chkBatchAutoKernel.Checked));
                shown++;
            }
            preview.AppendLine();
            preview.AppendLine("输出目录：" + outputDirectory);
            preview.AppendLine("归档模式：" + mode.Text);
            if (!packMode)
            {
                preview.AppendLine("内核策略：" + (_chkBatchAutoKernel == null || _chkBatchAutoKernel.Checked
                    ? "按项目版本自动选内核 —— 与当前内核(V" + CurrentKernelMajorVersion() + ")不同的项目"
                        + "用它自己的版本原生归档（不升级）；本机没装的会被跳过"
                    : "统一用 V" + CurrentKernelMajorVersion() + " 内核"
                        + (_chkBatchUpgrade.Checked ? "（旧项目升级打开）" : "（旧项目会打开失败）")));
            }
            if (packMode)
            {
                preview.AppendLine("· 打包模式：不启动 TIA、不受 Openness 版本限制，产物是 .zip");
                preview.AppendLine("· 请确认这些项目都没有在 TIA Portal 中打开");
            }
            else
            {
                preview.AppendLine("实例方式："
                    + CoreRunner.DescribeStartMode(GetStartMode(_rbArchiveNoUi, _rbArchiveUi, _rbArchiveAttach)));
            }
            preview.AppendLine();
            preview.AppendLine("确定开始吗？");

            DialogResult confirm = MessageBox.Show(
                preview.ToString(),
                ToolTitle,
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (confirm != DialogResult.Yes)
            {
                SetStatus("已取消。");
                return;
            }

            // ── 应用文件名规则（沿用归档页的设置）
            string ruleSuffix;
            string ruleTimestampFormat;
            ReadNamingRule(out ruleSuffix, out ruleTimestampFormat);

            // ★ 整批共用同一个时间戳：一眼就能看出这些文件属于同一批备份
            DateTime batchStamp = DateTime.Now;

            // ★ 扩展名必须按"这个项目**实际会被哪个内核**归档"来取 —— 这是
            //   BatchScanner.BuildArchivePath 的约定（归档格式由执行归档的内核决定）：
            //     · 与内核同版本                     → .zap<内核>
            //     · 更旧 + 自动选内核且本机有该版本   → 子进程用**它自己的版本**原生归档 → .zap<项目版本>
            //     · 更旧 + 升级打开（或本机无该版本） → 用内核升级打开 → .zap<内核>
            //   以前这里读的是 OpennessApi.Current（点按钮这一刻 TIA 还没启动，通常是 null），
            //   于是退化成"按项目版本"；而本进程只要跑过一次归档，静态引用就在了，又变成"按内核版本"——
            //   同一批项目在不同时机运行会得到不同扩展名，升级打开时还会名实不符。
            //   现在改成按界面上**确定的内核选择 + 内核策略**逐项推导，与执行时完全一致。
            int kernelForNaming = CurrentKernelMajorVersion();
            bool autoKernelForNaming = _chkBatchAutoKernel == null || _chkBatchAutoKernel.Checked;
            bool upgradeForNaming = _chkBatchUpgrade.Checked;

            BatchArchiveRequest request = new BatchArchiveRequest();
            request.Items = new List<BatchArchiveItem>();

            List<string> conflicts = new List<string>();
            HashSet<string> planned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (TiaProjectEntry entry in selected)
            {
                int productKernel = ResolveProductKernelVersion(
                    entry, kernelForNaming, autoKernelForNaming, upgradeForNaming);
                string raw = BatchScanner.BuildArchivePath(outputDirectory, entry.ProjectPath, productKernel);
                string target = ArchiveNaming.ApplyRule(raw, ruleSuffix, ruleTimestampFormat, batchStamp);

                // 打包模式产出 .zip，不是 .zapXX
                if (packMode)
                {
                    target = Path.ChangeExtension(target, ".zip");
                }

                BatchArchiveItem item = new BatchArchiveItem();
                item.ProjectPath = entry.ProjectPath;
                item.OutputPath = target;
                request.Items.Add(item);

                if (File.Exists(target) || planned.Contains(target))
                {
                    conflicts.Add(target);
                }

                planned.Add(target);
            }

            // ── 同名处理：批量场景一次问清，不逐个弹窗
            if (conflicts.Count > 0)
            {
                StringBuilder conflictPreview = new StringBuilder();
                for (int i = 0; i < conflicts.Count && i < 5; i++)
                {
                    conflictPreview.AppendLine("    · " + Path.GetFileName(conflicts[i]));
                }
                if (conflicts.Count > 5)
                {
                    conflictPreview.AppendLine("    …… 还有 " + (conflicts.Count - 5) + " 个");
                }

                DialogResult sameName = MessageBox.Show(
                    "有 " + conflicts.Count + " 个目标文件已存在：\r\n\r\n" + conflictPreview.ToString()
                    + "\r\n【是】全部覆盖\r\n"
                    + "【否】全部自动加序号（保留已有文件）\r\n"
                    + "【取消】返回，我先调整时间戳或后缀",
                    ToolTitle,
                    MessageBoxButtons.YesNoCancel,
                    MessageBoxIcon.Question);

                if (sameName == DialogResult.Cancel)
                {
                    SetStatus("已取消（目标文件已存在）。");
                    return;
                }

                // 选"覆盖"时仍需保证批内唯一（否则后一个项目会冲掉前一个的结果）
                EnsureBatchUniqueOutputs(request.Items, sameName == DialogResult.No);

                // ★ "覆盖"意图要传给内核：TIA 自己不会覆盖已存在的目标（V21 实测报 is already exist），
                //   由 ArchiveService 先把旧文件改名备份、归档成功后再删除备份、失败则恢复。
                request.OverwriteExisting = sameName == DialogResult.Yes;
            }

            request.ModeKeyword = mode.Keyword;
            request.IsFolderPack = packMode;
            request.SaveFirst = _chkBatchSaveFirst.Checked;
            request.Upgrade = _chkBatchUpgrade.Checked;
            request.StartMode = GetStartMode(_rbArchiveNoUi, _rbArchiveUi, _rbArchiveAttach);
            request.KeepProjectOpen = false;
            request.ApiDirectory = _txtApiDir.Text.Trim();
            request.AutoSelectKernel = _chkBatchAutoKernel == null || _chkBatchAutoKernel.Checked;
            request.Verbose = _chkVerbose.Checked;

            _settings.SetString("LastBatchOutputDir", outputDirectory);
            _settings.SetBool("BatchSaveFirst", request.SaveFirst);
            _settings.SetBool("BatchUpgrade", request.Upgrade);
            _settings.SetInt("BatchModeIndex", _cmbBatchMode.SelectedIndex);
            _settings.Save();

            _batchCancelRequested = false;
            _batchRunning = true;

            if (packMode)
            {
                SetBusy(true, "正在批量打包…（不需要启动 TIA，速度很快）");

                Thread packWorker = new Thread(ExecuteBatchPack);
                packWorker.IsBackground = true;
                packWorker.Name = "BatchPackWorker";
                packWorker.Start(request);
            }
            else
            {
                SetBusy(true, "正在批量归档…（TIA 只启动一次，之后逐个处理）");

                Thread worker = new Thread(ExecuteBatchArchive);
                worker.IsBackground = true;
                worker.Name = "BatchArchiveWorker";
                worker.Start(request);
            }
        }

        /// <summary>
        /// 后台线程：执行批量归档。整个方法体不引用 Siemens 类型。
        /// </summary>
        /// <param name="state">BatchArchiveRequest。</param>
        private void ExecuteBatchArchive(object state)
        {
            BatchArchiveRequest request = (BatchArchiveRequest)state;
            Logger logger = new Logger(request.Verbose, new UiLogSink(this, AppendLog));
            int exitCode = ExitCodes.Success;
            int succeeded = 0;
            int failed = 0;
            int cancelled = 0;

            try
            {
                logger.Section("批量归档任务开始");
                logger.Info("项目数量：" + request.Items.Count);
                ReportGroupMembership(logger);

                TiaEnvironmentInfo environment = CoreRunner.DetectEnvironment(logger, request.ApiDirectory);
                logger.Ok(environment.ToString());
                ReportEnvironmentSummary(environment);
                CoreRunner.InstallResolver(environment, logger);

                List<BatchArchiveResult> results = CoreRunner.ArchiveMany(
                    logger,
                    request,
                    delegate { return _batchCancelRequested; },
                    ReportBatchProgress);

                foreach (BatchArchiveResult result in results)
                {
                    if (result.Success)
                    {
                        succeeded++;
                    }
                    else
                    {
                        failed++;
                    }
                }

                if (results.Count < request.Items.Count)
                {
                    cancelled = request.Items.Count - results.Count;
                    logger.Warning("因中止而未处理：" + cancelled + " 个");
                }

                logger.Section("批量归档任务结束");
                if (failed == 0)
                {
                    logger.Ok("全部成功：成功 " + succeeded + " 个"
                        + (cancelled > 0 ? "，未处理 " + cancelled + " 个" : string.Empty));
                }
                else
                {
                    logger.Warning("批量归档完成：成功 " + succeeded + " 个，失败 " + failed + " 个"
                        + (cancelled > 0 ? "，未处理 " + cancelled + " 个" : string.Empty));
                    exitCode = ExitCodes.Api;
                }
            }
            catch (ToolException ex)
            {
                exitCode = ex.ExitCode;
                LogFailure(logger, ex);
            }
            catch (Exception ex)
            {
                exitCode = ExitCodes.Api;
                logger.Error("批量归档失败：" + CoreRunner.ExplainFailure(ex));
                logger.Error("异常详情：" + ex.GetType().FullName + "：" + ex.Message);
            }
            finally
            {
                _batchRunning = false;
                _batchCancelRequested = false;

                string summary = "批量归档完成：成功 " + succeeded + " 个，失败 " + failed + " 个"
                    + (cancelled > 0 ? "，未处理 " + cancelled + " 个" : string.Empty);

                SetBusy(false, summary);

                UiInvoke(delegate
                {
                    UpdateBatchSummary();
                    if (succeeded > 0 && failed == 0)
                    {
                        string directory = request.Items.Count > 0
                            ? Path.GetDirectoryName(request.Items[0].OutputPath)
                            : null;
                        if (!string.IsNullOrEmpty(directory))
                        {
                            OfferOpenFolder("批量归档", directory);
                        }
                    }
                });
            }
        }

        /// <summary>
        /// 后台线程：批量打包项目文件夹（"压缩包"模式）。
        ///
        /// 不探测 Openness 环境、不启动 TIA —— 每个项目就是"压一个文件夹"，
        /// 所以这里没有 TiaSession、没有版本匹配，纯文件操作顺序执行即可。
        /// </summary>
        /// <param name="state">BatchArchiveRequest。</param>
        private void ExecuteBatchPack(object state)
        {
            BatchArchiveRequest request = (BatchArchiveRequest)state;
            Logger logger = new Logger(request.Verbose, new UiLogSink(this, AppendLog));
            int succeeded = 0;
            int failed = 0;
            int cancelled = 0;

            try
            {
                logger.Section("批量打包项目文件夹（不需要 Openness）");
                logger.Info("待打包项目：" + request.Items.Count + " 个");
                logger.Info("压缩级别：快速");
                logger.Info("这个模式不启动 TIA Portal，也不受 Openness 版本限制。");
                logger.Warning("请确认这些项目都没有在 TIA Portal 中打开。");

                int index = 0;
                foreach (BatchArchiveItem item in request.Items)
                {
                    index++;

                    if (_batchCancelRequested)
                    {
                        cancelled = request.Items.Count - index + 1;
                        logger.Warning("收到中止请求：还有 " + cancelled + " 个未处理。");
                        break;
                    }

                    logger.Section("[" + index + "/" + request.Items.Count + "] "
                        + Path.GetFileName(item.ProjectPath));

                    try
                    {
                        FolderPackResult result = CoreRunner.PackProjectFolder(
                            logger, item.ProjectPath, item.OutputPath, ReportPackProgress);

                        succeeded++;

                        logger.Ok("打包完成：" + result.FileCount + " 个文件，"
                            + SizeFormat.Format(result.TotalSourceBytes) + " → "
                            + SizeFormat.Format(result.ZipBytes)
                            + "（" + result.CompressionRatioText + "）");

                        if (result.SkippedCount > 0)
                        {
                            logger.Warning("有 " + result.SkippedCount + " 个文件被跳过，包内可能不完整。");
                        }
                    }
                    catch (ToolException ex)
                    {
                        failed++;
                        logger.Error("打包失败：" + ex.Message);
                        logger.Warning("记录后继续处理下一个项目。");
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        logger.Error("打包失败：" + ex.GetType().Name + "：" + ex.Message);
                        logger.Warning("记录后继续处理下一个项目。");
                    }

                    ReportBatchProgress(index, request.Items.Count, item.ProjectPath);
                }

                logger.Section("批量打包结束");
                if (failed == 0)
                {
                    logger.Ok("全部成功：成功 " + succeeded + " 个"
                        + (cancelled > 0 ? "，未处理 " + cancelled + " 个" : string.Empty));
                }
                else
                {
                    logger.Warning("批量打包完成：成功 " + succeeded + " 个，失败 " + failed + " 个"
                        + (cancelled > 0 ? "，未处理 " + cancelled + " 个" : string.Empty));
                }
            }
            catch (Exception ex)
            {
                logger.Error("批量打包整体失败：" + ex.GetType().Name + "：" + ex.Message);
            }
            finally
            {
                _batchRunning = false;
                _batchCancelRequested = false;

                string summary = "批量打包完成：成功 " + succeeded + " 个，失败 " + failed + " 个"
                    + (cancelled > 0 ? "，未处理 " + cancelled + " 个" : string.Empty);

                SetBusy(false, summary);

                UiInvoke(delegate
                {
                    UpdateBatchSummary();
                    if (succeeded > 0 && failed == 0)
                    {
                        string directory = request.Items.Count > 0
                            ? Path.GetDirectoryName(request.Items[0].OutputPath)
                            : null;
                        if (!string.IsNullOrEmpty(directory))
                        {
                            OfferOpenFolder("批量打包", directory);
                        }
                    }
                });
            }
        }

        /// <summary>
        /// 进度回调（从后台线程调用）：更新状态栏文字。
        /// </summary>
        /// <param name="completed">已完成数。</param>
        /// <param name="total">总数。</param>
        /// <param name="currentProject">当前项目。</param>
        private void ReportBatchProgress(int completed, int total, string currentProject)
        {
            string name = Path.GetFileName(currentProject);
            UiInvoke(delegate
            {
                SetStatus("批量归档：" + completed + " / " + total + " —— 已完成 " + name);
            });
        }

        /// <summary>
        /// 批量控件可用性随"忙碌"状态联动。
        /// </summary>
        /// <param name="busy">是否正在执行任务。</param>
        private void UpdateBatchBusyState(bool busy)
        {
            if (_btnBatchScan != null)
            {
                _btnBatchScan.Enabled = !busy;
            }
            if (_cmbBatchMode != null)
            {
                _cmbBatchMode.Enabled = !busy;
            }
            if (_btnBatchCancel != null)
            {
                _btnBatchCancel.Visible = busy && _batchRunning;
                _btnBatchCancel.Enabled = busy && _batchRunning;
            }
        }

        /// <summary>
        /// 把批量页的设置装进界面。
        /// </summary>
        private void LoadBatchSettingsIntoUi()
        {
            _txtBatchFolder.Text = _settings.GetString("LastBatchFolder", string.Empty);
            _txtBatchOutputDir.Text = _settings.GetString("LastBatchOutputDir", string.Empty);
            _chkBatchRecursive.Checked = _settings.GetBool("BatchRecursive", true);
            _chkBatchIgnoreBackup.Checked = _settings.GetBool("BatchIgnoreBackup", true);
            _chkBatchSaveFirst.Checked = _settings.GetBool("BatchSaveFirst", true);
            _chkBatchUpgrade.Checked = _settings.GetBool("BatchUpgrade", false);

            int modeIndex = _settings.GetInt("BatchModeIndex", 0);
            if (modeIndex >= 0 && modeIndex < _cmbBatchMode.Items.Count)
            {
                _cmbBatchMode.SelectedIndex = modeIndex;
            }
        }
    }
}
