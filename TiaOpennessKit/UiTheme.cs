using System;
using System.Drawing;
using System.Windows.Forms;

namespace TiaOpennessKit
{
    /// <summary>
    /// 界面的配色与字体集中定义处（相当于 WinCC 里的"全局设计/样式"）。
    ///
    /// 把颜色和字体写在一处，好处是改一处全界面生效，不用在各处 Form 里翻硬编码色值。
    /// 类比 SCL：就像把常量集中定义在一个全局常量 DB 里，而不是散落在各段程序里写魔数。
    /// </summary>
    internal static class UiTheme
    {
        // ── 配色（浅色工程软件风格，强调色取偏青绿，贴近西门子的视觉习惯）
        public static readonly Color Background = Color.FromArgb(0xF4, 0xF6, 0xF8);
        public static readonly Color Surface = Color.White;
        public static readonly Color Border = Color.FromArgb(0xDD, 0xE2, 0xE8);
        public static readonly Color HeaderBack = Color.FromArgb(0x0F, 0x3C, 0x4A);
        public static readonly Color Accent = Color.FromArgb(0x11, 0x7C, 0x8C);
        public static readonly Color AccentHover = Color.FromArgb(0x0D, 0x63, 0x70);
        public static readonly Color AccentPressed = Color.FromArgb(0x0A, 0x4F, 0x5A);
        public static readonly Color Danger = Color.FromArgb(0xC0, 0x39, 0x2B);
        public static readonly Color DangerBack = Color.FromArgb(0xFD, 0xEE, 0xEC);
        public static readonly Color Warning = Color.FromArgb(0xB2, 0x6A, 0x00);
        public static readonly Color WarningBack = Color.FromArgb(0xFF, 0xF6, 0xE5);
        public static readonly Color TextPrimary = Color.FromArgb(0x22, 0x2A, 0x33);
        public static readonly Color TextSecondary = Color.FromArgb(0x66, 0x72, 0x80);
        public static readonly Color TextOnDark = Color.White;

        // 日志区按级别着色
        public static readonly Color LogDebug = Color.FromArgb(0x90, 0x99, 0xA4);
        public static readonly Color LogInfo = Color.FromArgb(0x2B, 0x33, 0x3C);
        public static readonly Color LogWarning = Color.FromArgb(0xB2, 0x6A, 0x00);
        public static readonly Color LogError = Color.FromArgb(0xC0, 0x39, 0x2B);
        public static readonly Color LogSuccess = Color.FromArgb(0x1A, 0x7F, 0x37);

        // ── 字体
        private const string Family = "Microsoft YaHei UI";

        public static readonly Font Body = new Font(Family, 9f, FontStyle.Regular, GraphicsUnit.Point);
        public static readonly Font BodyBold = new Font(Family, 9f, FontStyle.Bold, GraphicsUnit.Point);
        public static readonly Font Title = new Font(Family, 13f, FontStyle.Bold, GraphicsUnit.Point);
        public static readonly Font Subtitle = new Font(Family, 8.5f, FontStyle.Regular, GraphicsUnit.Point);
        public static readonly Font Small = new Font(Family, 8f, FontStyle.Regular, GraphicsUnit.Point);
        public static readonly Font GroupTitle = new Font(Family, 9.5f, FontStyle.Bold, GraphicsUnit.Point);
        // 日志字体刻意用中文字体而不是 Consolas：Consolas 不含汉字字形，
        // 一旦目标机器缺字体链，中文日志会显示成方框。可读性优先于等宽。
        public static readonly Font Log = new Font(Family, 9f, FontStyle.Regular, GraphicsUnit.Point);
        public static readonly Font Button = new Font(Family, 9.5f, FontStyle.Bold, GraphicsUnit.Point);

        /// <summary>
        /// 把"按 96 DPI 写的像素值"换算成当前屏幕 DPI 下的实际像素值。
        ///
        /// 为什么需要它：窗口开了按 DPI 自动缩放（AutoScaleMode.Dpi），控件自己的
        /// 尺寸和位置会跟着 DPI 放大；但**代码里运行时自己算出来的坐标**不在此列 ——
        /// 例如 Resize 事件里 `right - 按钮宽 - 12` 的间距 6/12/18 这种数，
        /// 在 175% 缩放下仍然是 6/12/18 个真实像素，按钮就会贴在一起或跑出边界。
        /// 这类"手算坐标"的常数都得用它过一道。
        /// </summary>
        /// <param name="control">用来取当前 DPI 的控件（一般传所在容器）。</param>
        /// <param name="pixels">按 96 DPI 设计的像素值。</param>
        public static int Scale(Control control, int pixels)
        {
            return (int)Math.Round(pixels * DeviceDpiOf(control) / 96.0);
        }

        /// <summary>
        /// 取"这个控件现在该按多少 DPI 算"。
        ///
        /// ★ 这里有坑，实测踩过：**不要用 `Control.DeviceDpi`**。
        ///   改用清单（app.manifest）声明 DPI 感知之后，175% 缩放的机器上
        ///   `Control.DeviceDpi` 一律返回 96（子控件、连窗体都是），而屏幕真实是 168、
        ///   布局也确实按 1.75 倍放大了 —— 拿 96 去换算等于"不放大"，
        ///   于是批量表格"归档"列没跟着放大，表头又被裁成"归"。
        ///
        ///   可靠的两个来源（按可靠性排序）：
        ///   ① 窗体的 `AutoScaleDimensions`：AutoScaleMode=Dpi 时，它就是**布局当前
        ///      真正采用的缩放基准**（实测 168×168），与控件尺寸的实际缩放完全一致；
        ///      在 100% 的机器上是 96×96，换算系数正好 1。
        ///   ② `CreateGraphics().DpiX`：来自屏幕 DC 的真实 DPI。
        /// </summary>
        private static int DeviceDpiOf(Control control)
        {
            Form form = control == null ? null : control.FindForm();
            if (form != null
                && form.AutoScaleMode == AutoScaleMode.Dpi
                && form.AutoScaleDimensions.Width > 0)
            {
                return (int)Math.Round(form.AutoScaleDimensions.Width);
            }

            if (control != null)
            {
                try
                {
                    using (Graphics graphics = control.CreateGraphics())
                    {
                        if (graphics.DpiX > 0)
                        {
                            return (int)Math.Round(graphics.DpiX);
                        }
                    }
                }
                catch (Exception)
                {
                    // 取不到就继续往下兜底
                }

                if (control.DeviceDpi > 0)
                {
                    return control.DeviceDpi;
                }
            }

            return 96;
        }

        /// <summary>
        /// 主操作按钮（青绿实心）：用于"开始归档""开始恢复""开始探测"。
        /// </summary>
        public static void StylePrimaryButton(Button button)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            button.BackColor = Accent;
            button.ForeColor = TextOnDark;
            button.Font = Button;
            button.Cursor = Cursors.Hand;
            button.Height = 36;
            button.FlatAppearance.MouseOverBackColor = AccentHover;
            button.FlatAppearance.MouseDownBackColor = AccentPressed;
        }

        /// <summary>
        /// 次要按钮（白底描边）：用于"浏览""另存为"。
        /// </summary>
        public static void StyleSecondaryButton(Button button)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.BorderColor = Border;
            button.BackColor = Surface;
            button.ForeColor = TextPrimary;
            button.Font = Body;
            button.Cursor = Cursors.Hand;
            // 高度给足 32：实测 28 时中文字符的下缘会被按钮边框切掉一截。
            button.Height = 32;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(0xEC, 0xF0, 0xF4);
        }

        /// <summary>
        /// 输入框统一样式。
        /// </summary>
        public static void StyleTextBox(TextBox textBox)
        {
            textBox.Font = Body;
            textBox.BorderStyle = BorderStyle.FixedSingle;
            textBox.BackColor = Surface;
            textBox.ForeColor = TextPrimary;
            textBox.Height = 26;
        }

        /// <summary>
        /// 一个浅色"卡片"面板（白底 + 细边框），用于把一组控件视觉上归拢起来。
        /// </summary>
        public static Panel CreateCard()
        {
            Panel card = new Panel();
            card.BackColor = Surface;
            card.Padding = new Padding(14, 12, 14, 12);
            card.Margin = new Padding(0, 0, 0, 8);

            // ★ 必须 Dock=Fill：卡片是放进 TableLayoutPanel 的单元格里的，
            //   不设 Dock 的话它会保持 Panel 的默认尺寸 200×100，
            //   结果就是卡片宽不起来、里面的输入框被挤成几十像素
            //   （这个坑真的踩过，截图上输入框全没了）。
            card.Dock = DockStyle.Fill;

            card.Paint += delegate (object sender, PaintEventArgs e)
            {
                Panel panel = (Panel)sender;
                using (Pen pen = new Pen(Border))
                {
                    e.Graphics.DrawRectangle(pen, 0, 0, panel.Width - 1, panel.Height - 1);
                }
            };
            return card;
        }

        /// <summary>
        /// 分组小标题。
        /// </summary>
        public static Label CreateGroupTitle(string text)
        {
            Label label = new Label();
            label.Text = text;
            label.Font = GroupTitle;
            label.ForeColor = TextPrimary;
            label.AutoSize = true;
            label.Margin = new Padding(0, 0, 0, 8);
            return label;
        }

        /// <summary>
        /// 普通说明文字。
        /// </summary>
        public static Label CreateLabel(string text, int width)
        {
            Label label = new Label();
            label.Text = text;
            label.Font = Body;
            label.ForeColor = TextSecondary;
            label.TextAlign = ContentAlignment.MiddleLeft;
            label.AutoSize = false;
            label.Width = width;
            label.Height = 24;
            label.Margin = new Padding(0, 4, 0, 4);
            // 放进网格单元格时垂直居中（只锚左边，高度不跟着拉伸）
            label.Anchor = AnchorStyles.Left;
            return label;
        }
    }
}
