// ============================================================================
//  GWMouseBattery - 托盘界面层
//
//  设计要点：
//   * 所有 HID 读写都在线程池线程上做，UI 线程永不阻塞（设备卡住也不会让界面假死）
//   * 每次查询都有硬超时上限（compx 约 1.2 秒），不会无限期挂着
//   * 插线充电时鼠标会换成另一个 PID 重新枚举，读失败会自动重扫设备
//
//  本文件必须保存为 UTF-8 with BOM。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace GWMouseBattery
{
    #region 调试日志（--log）

    internal static class DebugLog
    {
        public static string Path;

        /// <summary>测试钩子（--force-error）：拿到一次有效值之后就假装一直读失败，
        /// 用来验证"瞬时失败保留上次数值"这条路径，不影响正常使用。</summary>
        public static bool ForceError;

        private static readonly object Gate = new object();

        public static void Write(string text)
        {
            if (string.IsNullOrEmpty(Path)) return;
            try
            {
                lock (Gate)
                {
                    System.IO.File.AppendAllText(Path,
                        DateTime.Now.ToString("HH:mm:ss.fff") + " " + text + "\r\n",
                        new UTF8Encoding(false));
                }
            }
            catch { }
        }
    }

    #endregion

    #region 图标导出（--dump-icons，用来肉眼检查托盘图标画得对不对）

    internal static class IconDump
    {
        public static int Run(string dir)
        {
            int[] levels = new int[] { -1, 0, 8, 25, 60, 95, 100 };

            try { System.IO.Directory.CreateDirectory(dir); }
            catch (Exception ex)
            {
                Cli.Write("无法创建目录：" + ex.Message);
                return 2;
            }

            int count = 0;
            for (int style = 0; style <= 2; style++)
            {
                for (int c = 0; c <= 1; c++)
                {
                    bool charging = c == 1;
                    foreach (int level in levels)
                    {
                        Icon icon = BatteryIconRenderer.Get(level, charging, style);
                        using (Bitmap small = icon.ToBitmap())
                        using (Bitmap big = new Bitmap(small.Width * 12, small.Height * 12))
                        {
                            using (Graphics g = Graphics.FromImage(big))
                            {
                                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                                g.PixelOffsetMode = PixelOffsetMode.Half;
                                g.Clear(Color.FromArgb(245, 245, 245));
                                g.DrawImage(small, 0, 0, big.Width, big.Height);
                            }

                            string name = "style" + style + (charging ? "_charging" : "")
                                + "_" + (level < 0 ? "unknown" : level.ToString()) + ".png";
                            big.Save(System.IO.Path.Combine(dir, name),
                                System.Drawing.Imaging.ImageFormat.Png);
                            count++;
                        }
                    }
                }
            }

            Cli.Write("已导出 " + count + " 张图标到 " + dir);
            return 0;
        }
    }

    #endregion

    #region 输出（命令行 / 写文件）

    internal static class Cli
    {
        private static readonly List<string> Lines = new List<string>();
        private static string _outFile;

        public static void SetOutFile(string path)
        {
            _outFile = path;
        }

        public static void Write(string text)
        {
            Lines.Add(text);
            try { Console.WriteLine(text); }
            catch { }
        }

        public static void Blank()
        {
            Write("");
        }

        /// <summary>如果指定了 --out，就把收集到的内容落盘。</summary>
        public static void Flush()
        {
            if (string.IsNullOrEmpty(_outFile)) return;
            try
            {
                System.IO.File.WriteAllText(_outFile, string.Join("\r\n", Lines.ToArray()),
                    new UTF8Encoding(false));
                try { Console.WriteLine("（报告已写入 " + _outFile + "）"); }
                catch { }
            }
            catch { }
        }
    }

    #endregion

    #region 托盘图标绘制

    internal static class BatteryIconRenderer
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        private static readonly Dictionary<string, Icon> Cache = new Dictionary<string, Icon>();
        private static readonly object Gate = new object();

        private static readonly Color ColNormal = Color.FromArgb(46, 125, 50);    // 绿
        private static readonly Color ColLow = Color.FromArgb(239, 108, 0);       // 橙
        private static readonly Color ColCritical = Color.FromArgb(198, 40, 40);  // 红
        private static readonly Color ColCharging = Color.FromArgb(21, 101, 192); // 蓝
        private static readonly Color ColUnknown = Color.FromArgb(97, 97, 97);    // 灰

        public static Color StateColor(int percent, bool charging)
        {
            if (charging) return ColCharging;
            if (percent < 0) return ColUnknown;
            if (percent <= 10) return ColCritical;
            if (percent <= 25) return ColLow;
            return ColNormal;
        }

        public static Icon Get(int percent, bool charging, int style)
        {
            Size size = SystemInformation.SmallIconSize;
            string key = percent + "|" + charging + "|" + style + "|" + size.Width + "x" + size.Height;

            lock (Gate)
            {
                Icon cached;
                if (Cache.TryGetValue(key, out cached)) return cached;

                Icon icon = Render(percent, charging, style, size);
                if (Cache.Count < 128) Cache[key] = icon;
                return icon;
            }
        }

        private static Icon Render(int percent, bool charging, int style, Size size)
        {
            int w = size.Width > 0 ? size.Width : 16;
            int h = size.Height > 0 ? size.Height : 16;

            Bitmap bmp = new Bitmap(w, h);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                // 必须是灰度抗锯齿：ClearType 是次像素渲染，在有色底/透明底上
                // 会给笔画边缘染上青橙色毛边，缩到 16px 看就是一团脏。
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Transparent);

                Color color = StateColor(percent, charging);

                if (style == 1) DrawBattery(g, w, h, percent, charging, color);
                else if (style == 2) DrawTextOnly(g, w, h, percent, color);
                else DrawSolid(g, w, h, percent, charging, color);
            }

            IntPtr handle = bmp.GetHicon();
            try
            {
                using (Icon tmp = Icon.FromHandle(handle))
                {
                    Icon clone = (Icon)tmp.Clone();
                    bmp.Dispose();
                    return clone;
                }
            }
            finally
            {
                DestroyIcon(handle);
            }
        }

        /// <summary>样式 0：整块填色 + 白色数字。在 16px 下最清楚，也是默认样式。</summary>
        private static void DrawSolid(Graphics g, int w, int h, int percent, bool charging, Color color)
        {
            Rectangle box = new Rectangle(0, 0, w - 1, h - 1);
            using (GraphicsPath path = RoundedRect(box, Math.Max(2, h / 5)))
            using (SolidBrush brush = new SolidBrush(color))
                g.FillPath(brush, path);

            string text = percent < 0 ? "?" : percent.ToString();
            DrawFittedText(g, text, box, Color.White, 0);
        }

        /// <summary>样式 2：不画底，只画带描边的数字。适合喜欢清爽图标的场景。</summary>
        private static void DrawTextOnly(Graphics g, int w, int h, int percent, Color color)
        {
            Rectangle box = new Rectangle(0, 0, w, h);
            string text = percent < 0 ? "?" : percent.ToString();
            DrawFittedText(g, text, box, color, 1);
        }

        /// <summary>
        /// 样式 1：经典电池图形。
        /// 刻意不往里叠数字：16px 的电池内部只有 13x11，低电量时填充色和数字颜色
        /// 会撞在一起（橙字压橙底）根本看不清，各级别靠填充比例 + 颜色表达即可，
        /// 具体数值由悬停提示和详情窗口给出。
        /// 充电时改成"空电池 + 白色闪电"，避免蓝色画蓝线看不见。
        /// </summary>
        private static void DrawBattery(Graphics g, int w, int h, int percent, bool charging, Color color)
        {
            int nub = Math.Max(1, w / 12);
            Rectangle body = new Rectangle(0, Math.Max(1, h / 8), w - nub - 1, h - Math.Max(2, h / 4));

            using (SolidBrush nubBrush = new SolidBrush(color))
                g.FillRectangle(nubBrush, new Rectangle(body.Right + 1, body.Top + body.Height / 3,
                    nub, Math.Max(2, body.Height / 3)));

            using (GraphicsPath path = RoundedRect(body, Math.Max(1, h / 8)))
            {
                if (charging)
                {
                    using (Pen pen = new Pen(color, 1f))
                        g.DrawPath(pen, path);

                    using (GraphicsPath bolt = BoltPath(body))
                    {
                        using (Pen outline = new Pen(Color.FromArgb(200, 0, 0, 0), 1f))
                            g.DrawPath(outline, bolt);
                        using (SolidBrush white = new SolidBrush(Color.White))
                            g.FillPath(white, bolt);
                    }
                    return;
                }

                if (percent >= 0)
                {
                    int fillW = (int)Math.Round(body.Width * Math.Min(100, percent) / 100.0);
                    if (percent > 0 && fillW < 1) fillW = 1;

                    if (fillW > 0)
                    {
                        Region oldClip = g.Clip;
                        g.SetClip(path);
                        using (SolidBrush brush = new SolidBrush(color))
                            g.FillRectangle(brush, new Rectangle(body.X, body.Y, fillW, body.Height));
                        g.Clip = oldClip;
                        oldClip.Dispose();
                    }
                }

                using (Pen pen = new Pen(color, 1f))
                    g.DrawPath(pen, path);
            }
        }

        /// <summary>闪电轮廓，坐标按电池内部比例取，尽量占满高度保证 16px 下看得清。</summary>
        private static GraphicsPath BoltPath(Rectangle body)
        {
            float x = body.X;
            float y = body.Y;
            float w = body.Width;
            float h = body.Height;

            GraphicsPath p = new GraphicsPath();
            p.AddPolygon(new PointF[]
            {
                new PointF(x + w * 0.60f, y + h * 0.06f),
                new PointF(x + w * 0.22f, y + h * 0.58f),
                new PointF(x + w * 0.46f, y + h * 0.58f),
                new PointF(x + w * 0.38f, y + h * 0.94f),
                new PointF(x + w * 0.78f, y + h * 0.42f),
                new PointF(x + w * 0.53f, y + h * 0.42f)
            });
            p.CloseFigure();
            return p;
        }

        /// <summary>把文字缩到能塞进 box 里，居中绘制；outline > 0 时先描一圈黑边保证可读。</summary>
        private static void DrawFittedText(Graphics g, string text, Rectangle box, Color color, int outline)
        {
            if (string.IsNullOrEmpty(text)) return;

            float size = box.Height;
            Font font = null;

            for (int i = 0; i < 40; i++)
            {
                Font attempt = new Font("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Pixel);
                Size proposed = TextRenderer.MeasureText(g, text, attempt, new Size(int.MaxValue, int.MaxValue),
                    TextFormatFlags.NoPadding);

                if (font != null) font.Dispose();
                font = attempt;

                if (proposed.Width <= box.Width - 1 && proposed.Height <= box.Height)
                    break;

                size -= Math.Max(0.5f, size * 0.08f);
                if (size < 4f)
                {
                    size = 4f;
                    break;
                }
            }

            if (font == null) return;

            using (font)
            {
                StringFormat fmt = new StringFormat();
                fmt.Alignment = StringAlignment.Center;
                fmt.LineAlignment = StringAlignment.Center;
                fmt.FormatFlags = StringFormatFlags.NoWrap;

                float cx = box.X + box.Width / 2f;
                float cy = box.Y + box.Height / 2f;

                if (outline > 0)
                {
                    using (SolidBrush shadow = new SolidBrush(Color.FromArgb(190, 0, 0, 0)))
                    {
                        for (int dx = -outline; dx <= outline; dx += outline)
                            for (int dy = -outline; dy <= outline; dy += outline)
                            {
                                if (dx == 0 && dy == 0) continue;
                                g.DrawString(text, font, shadow, cx + dx, cy + dy, fmt);
                            }
                    }
                    using (SolidBrush fg = new SolidBrush(Color.White))
                        g.DrawString(text, font, fg, cx, cy, fmt);
                }
                else
                {
                    using (SolidBrush fg = new SolidBrush(color))
                        g.DrawString(text, font, fg, cx, cy, fmt);
                }

                fmt.Dispose();
            }
        }

        private static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            if (radius < 1) radius = 1;
            int d = radius * 2;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;

            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    #endregion

    #region 详情窗口

    internal sealed class DetailsForm : Form
    {
        private readonly Label _percent;
        private readonly Label _state;
        private readonly Label[] _values;
        private readonly BatteryBar _bar;
        private readonly Label _updated;
        private readonly System.Windows.Forms.Timer _auto;

        public event EventHandler RefreshRequested;

        public DetailsForm()
        {
            Text = "G-Wolves 鼠标电量";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(400, 288);
            Font = SystemFonts.MessageBoxFont;
            Icon = null;

            _percent = new Label();
            _percent.AutoSize = false;
            _percent.Bounds = new Rectangle(16, 12, 150, 62);
            _percent.Font = new Font("Segoe UI", 40f, FontStyle.Bold, GraphicsUnit.Pixel);
            _percent.TextAlign = ContentAlignment.MiddleLeft;
            _percent.Text = "--";
            Controls.Add(_percent);

            _state = new Label();
            _state.AutoSize = false;
            _state.Bounds = new Rectangle(20, 74, 360, 22);
            _state.Font = new Font(Font.FontFamily, 10f, FontStyle.Bold);
            _state.Text = "正在读取…";
            Controls.Add(_state);

            _bar = new BatteryBar();
            _bar.Bounds = new Rectangle(20, 102, 360, 18);
            Controls.Add(_bar);

            string[] captions = new string[] { "协议", "电压", "接口", "更新时间" };
            _values = new Label[captions.Length];

            int y = 134;
            for (int i = 0; i < captions.Length; i++)
            {
                Label cap = new Label();
                cap.AutoSize = false;
                cap.Bounds = new Rectangle(20, y, 62, 20);
                cap.Text = captions[i];
                cap.ForeColor = Color.FromArgb(110, 110, 110);
                Controls.Add(cap);

                Label val = new Label();
                val.AutoSize = false;
                val.Bounds = new Rectangle(88, y, 292, 20);
                val.Text = "-";
                Controls.Add(val);
                _values[i] = val;

                y += 24;
            }

            Button refresh = new Button();
            refresh.Text = "立即刷新";
            refresh.Bounds = new Rectangle(20, 244, 96, 28);
            refresh.Click += delegate { RaiseRefresh(); };
            Controls.Add(refresh);

            Button diag = new Button();
            diag.Text = "诊断信息…";
            diag.Bounds = new Rectangle(126, 244, 104, 28);
            diag.Click += delegate { DiagnosticsForm.ShowUp(); };
            Controls.Add(diag);

            Button close = new Button();
            close.Text = "关闭";
            close.Bounds = new Rectangle(300, 244, 80, 28);
            close.Click += delegate { Hide(); };
            Controls.Add(close);

            _updated = new Label();
            _updated.AutoSize = false;
            _updated.Bounds = new Rectangle(20, 220, 360, 18);
            _updated.ForeColor = Color.FromArgb(140, 140, 140);
            Controls.Add(_updated);

            // 窗口开着的时候自动加快刷新，做到"接近实时"
            _auto = new System.Windows.Forms.Timer();
            _auto.Interval = 1000;
            _auto.Tick += delegate { RaiseRefresh(); };

            VisibleChanged += delegate
            {
                if (Visible) _auto.Start();
                else _auto.Stop();
            };
        }

        private void RaiseRefresh()
        {
            if (RefreshRequested != null) RefreshRequested(this, EventArgs.Empty);
        }

        public void SetBusy()
        {
            _state.Text = "正在读取…";
        }

        /// <summary>
        /// 刷新窗口内容。percent/charging 是"该显示的值"（可能来自上一次的有效读数），
        /// note 非空时说明本次刷新并不成功。
        /// </summary>
        public void Apply(BatteryReading shown, int percent, bool charging, string note)
        {
            if (percent >= 0)
            {
                _percent.Text = percent + "%";
                _percent.ForeColor = BatteryIconRenderer.StateColor(percent, charging);
            }
            else
            {
                _percent.Text = "--";
                _percent.ForeColor = Color.FromArgb(97, 97, 97);
            }

            if (!string.IsNullOrEmpty(note))
            {
                _state.Text = note;
            }
            else
            {
                string s = Util.StateText(percent);
                if (charging) s = "充电中（" + s + "）";
                _state.Text = percent >= 0 ? "G-Wolves 鼠标：" + s : "还没有读到数据";
            }

            _state.ForeColor = percent >= 0
                ? BatteryIconRenderer.StateColor(percent, charging)
                : Color.FromArgb(198, 40, 40);

            _bar.Percent = percent;
            _bar.Charging = charging;

            _values[0].Text = shown != null && !string.IsNullOrEmpty(shown.Protocol)
                ? shown.Protocol : "-";
            _values[1].Text = shown != null ? Util.FormatMv(shown.VoltageMv) : "-";
            _values[2].Text = shown != null && !string.IsNullOrEmpty(shown.Path)
                ? ShortenPath(shown.Path) : "-";
            _values[3].Text = shown != null ? shown.Stamp.ToString("HH:mm:ss") : "-";

            _updated.Text = shown != null && shown.Raw != null && shown.Raw.Length > 0
                ? "原始报文：" + shown.RawHex
                : "";
        }

        private static string ShortenPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "-";
            int hash = path.IndexOf("&mi_", StringComparison.OrdinalIgnoreCase);
            string tail = hash >= 0 ? path.Substring(hash) : path;
            int brace = tail.IndexOf('#');
            if (brace >= 0) tail = tail.Substring(0, brace);
            return "\\.\\hid#" + tail;
        }
    }

    internal sealed class BatteryBar : Control
    {
        public int Percent = -1;
        public bool Charging;

        public BatteryBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Rectangle box = new Rectangle(0, 0, Width - 1, Height - 1);
            using (SolidBrush back = new SolidBrush(Color.FromArgb(238, 238, 238)))
                g.FillRectangle(back, box);
            using (Pen border = new Pen(Color.FromArgb(200, 200, 200)))
                g.DrawRectangle(border, box);

            if (Percent >= 0)
            {
                int inner = (int)Math.Round((Width - 4) * Math.Min(100, Percent) / 100.0);
                if (Percent > 0 && inner < 2) inner = 2;
                if (inner > 0)
                {
                    using (SolidBrush fill = new SolidBrush(BatteryIconRenderer.StateColor(Percent, Charging)))
                        g.FillRectangle(fill, new Rectangle(2, 2, inner, Height - 4));
                }
            }

            string text = Percent >= 0 ? Percent + "%" : "未知";
            using (Font f = new Font("Segoe UI", 11f, FontStyle.Bold, GraphicsUnit.Pixel))
            using (StringFormat fmt = new StringFormat())
            {
                fmt.Alignment = StringAlignment.Center;
                fmt.LineAlignment = StringAlignment.Center;
                using (SolidBrush fg = new SolidBrush(Color.FromArgb(60, 60, 60)))
                    g.DrawString(text, f, fg, new RectangleF(0, 0, Width, Height), fmt);
            }
        }
    }

    #endregion

    #region 诊断窗口

    internal sealed class DiagnosticsForm : Form
    {
        private static DiagnosticsForm _instance;
        private readonly TextBox _box;

        public DiagnosticsForm()
        {
            Text = "诊断信息";
            ClientSize = new Size(760, 520);
            StartPosition = FormStartPosition.CenterScreen;
            Font = SystemFonts.MessageBoxFont;

            _box = new TextBox();
            _box.Multiline = true;
            _box.ReadOnly = true;
            _box.ScrollBars = ScrollBars.Both;
            _box.WordWrap = false;
            _box.Font = new Font("Consolas", 9f);
            _box.Dock = DockStyle.Fill;
            Controls.Add(_box);

            Panel bottom = new Panel();
            bottom.Height = 42;
            bottom.Dock = DockStyle.Bottom;

            Button copy = new Button();
            copy.Text = "复制全部";
            copy.Bounds = new Rectangle(12, 7, 96, 28);
            copy.Click += delegate
            {
                try { Clipboard.SetText(_box.Text); }
                catch { }
            };
            bottom.Controls.Add(copy);

            Button save = new Button();
            save.Text = "另存为…";
            save.Bounds = new Rectangle(116, 7, 96, 28);
            save.Click += delegate { SaveToFile(); };
            bottom.Controls.Add(save);

            Button close = new Button();
            close.Text = "关闭";
            close.Bounds = new Rectangle(656, 7, 90, 28);
            close.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            close.Click += delegate { Hide(); };
            bottom.Controls.Add(close);

            Controls.Add(bottom);
            bottom.BringToFront();
        }

        private void SaveToFile()
        {
            SaveFileDialog dlg = new SaveFileDialog();
            dlg.FileName = "gwmouse-diagnostics.txt";
            dlg.Filter = "文本文件|*.txt|所有文件|*.*";
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                try { System.IO.File.WriteAllText(dlg.FileName, _box.Text, new UTF8Encoding(false)); }
                catch (Exception ex) { MessageBox.Show(this, "保存失败：" + ex.Message); }
            }
        }

        public static void ShowUp()
        {
            if (_instance == null || _instance.IsDisposed)
                _instance = new DiagnosticsForm();

            _instance._box.Text = BuildReport();
            _instance.Show();

            if (_instance.WindowState == FormWindowState.Minimized)
                _instance.WindowState = FormWindowState.Normal;
            _instance.Activate();
        }

        /// <summary>程序版本，用于诊断报告 / 问题反馈（来自 AssemblyInfo.cs）。</summary>
        public static string VersionText
        {
            get
            {
                try
                {
                    return "v" + System.Reflection.Assembly.GetExecutingAssembly()
                        .GetName().Version.ToString();
                }
                catch { return "v?"; }
            }
        }

        public static string BuildReport()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("G-Wolves 鼠标电量 · 诊断报告  " + VersionText);
            sb.AppendLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("系统: " + Environment.OSVersion.VersionString
                + " / " + (Environment.Is64BitProcess ? "64" : "32") + " 位进程");
            sb.AppendLine();
            sb.AppendLine("--- HID 接口 ---");

            string error;
            List<HidInterface> all = HidDiscovery.Enumerate(out error);

            if (!string.IsNullOrEmpty(error)) sb.AppendLine("枚举错误: " + error);
            sb.AppendLine("接口总数: " + all.Count);
            sb.AppendLine();

            foreach (HidInterface item in all)
            {
                sb.AppendLine("[" + (item.Kind == ProtocolKind.Unknown ? "其它" : item.KindName) + "]"
                    + (item.VendorId == ProductIds.GWolvesVendorId ? "  <== G-Wolves" : ""));
                sb.AppendLine("  VID/PID : " + item.VidPidHex);
                sb.AppendLine("  名称    : " + item.FriendlyName);
                sb.AppendLine("  用途    : UsagePage=0x" + item.UsagePage.ToString("X2")
                    + " Usage=0x" + item.Usage.ToString("X2"));
                sb.AppendLine("  报文长度: 输入=" + item.InputLength
                    + " 输出=" + item.OutputLength + " feature=" + item.FeatureLength);
                sb.AppendLine("  路径    : " + item.Path);
                sb.AppendLine();
            }

            sb.AppendLine("--- 实际读取 ---");
            HidInterface target = HidDiscovery.Find(out all, out error);
            if (target == null)
            {
                sb.AppendLine("没有找到可用的 G-Wolves 接收器接口。");
                sb.AppendLine("可能原因：");
                sb.AppendLine("  1) 接收器没插 / 鼠标没开机；");
                sb.AppendLine("  2) 鼠标正插着 USB 线充电，此时收发器会消失（拔线即恢复）；");
                sb.AppendLine("  3) 这不是 G-Wolves 的鼠标。");
            }
            else
            {
                sb.AppendLine("选中接口: " + target.VidPidHex + "  " + target.KindName);
                sb.AppendLine("路径    : " + target.Path);

                BatteryReading reading = BatteryClient.Read(target);
                if (reading.Ok)
                {
                    sb.AppendLine("读取成功: " + reading.Percent + "%"
                        + (reading.Charging ? "（充电中）" : "（未充电）")
                        + "  电压 " + Util.FormatMv(reading.VoltageMv));
                }
                else
                {
                    sb.AppendLine("读取失败: " + reading.Error);
                }
                sb.AppendLine("原始报文: " + reading.RawHex);
            }

            sb.AppendLine();
            sb.AppendLine("--- SPDT（左/右键的按键开关状态）---");
            if (target == null)
            {
                sb.AppendLine("没有可用设备，跳过。");
            }
            else if (target.Kind != ProtocolKind.Compx)
            {
                sb.AppendLine("当前设备是「" + target.KindName + "」。");
                sb.AppendLine("SPDT 走的是 compx 的 EEPROM 读写（命令 0x08 读 / 0x07 写），该族暂未适配。");
            }
            else
            {
                SpdtState spdt = Spdt.Read(target);
                sb.AppendLine(spdt.Describe());
                if (spdt.Raw != null)
                    sb.AppendLine("原始回复: " + Util.Hex(spdt.Raw, spdt.Raw.Length));
                sb.AppendLine("存放位置: EEPROM 地址 " + CompxProtocol.KeyOperationAddress
                    + "（网页驱动里的 Ge.KeyOperation），bit0 = 左键、bit1 = 右键。");
            }

            return sb.ToString();
        }
    }

    #endregion

    #region 单实例：把"又点了一次"变成"把窗口叫出来"

    /// <summary>
    /// 用窗口消息做实例间通信。注册同一个字符串，两个进程拿到的消息号相同。
    /// </summary>
    internal static class WindowMessages
    {
        /// <summary>隐藏宿主窗口的标题，FindWindow 靠它找到已有实例。</summary>
        public const string HostWindowTitle = "GWMouseBattery.HostWindow";

        private const string WakeName = "GWMouseBattery.ShowDetails.v1";
        private const string QuitName = "GWMouseBattery.Quit.v1";

        public static readonly uint Wake = Native.RegisterWindowMessageW(WakeName);
        public static readonly uint Quit = Native.RegisterWindowMessageW(QuitName);
    }

    internal sealed class HostForm : Form
    {
        public event EventHandler WakeRequested;
        public event EventHandler QuitRequested;

        protected override void WndProc(ref Message m)
        {
            if (WindowMessages.Wake != 0 && (uint)m.Msg == WindowMessages.Wake)
            {
                if (WakeRequested != null) WakeRequested(this, EventArgs.Empty);
                return;
            }

            if (WindowMessages.Quit != 0 && (uint)m.Msg == WindowMessages.Quit)
            {
                if (QuitRequested != null) QuitRequested(this, EventArgs.Empty);
                return;
            }

            base.WndProc(ref m);
        }
    }

    #endregion

    #region 托盘主体

    internal sealed class TrayContext : ApplicationContext
    {
        /// <summary>当前实例。供未处理异常时摘掉托盘图标用（避免留下僵尸图标）。</summary>
        private static TrayContext _current;

        /// <summary>
        /// 把当前实例的托盘图标摘掉。进程即将异常终止时调用 —— 带异常退出的进程
        /// 来不及走正常清理，Windows 会把图标留在通知区域，变成一个点不动、
        /// 也不会自己消失的僵尸图标。
        /// </summary>
        public static void HideCurrentIcon()
        {
            TrayContext c = _current;
            if (c == null) return;
            try { if (c._tray != null) c._tray.Visible = false; } catch { }
        }

        private readonly HostForm _host;
        private readonly NotifyIcon _tray;
        private readonly System.Windows.Forms.Timer _timer;
        private readonly AppSettings _settings;

        private volatile bool _busy;
        private BatteryReading _last;
        private HidInterface _device;
        private List<HidInterface> _all = new List<HidInterface>();
        private string _scanError = "";
        private DateTime _lastScan = DateTime.MinValue;
        private int _consecutiveFailures;
        private bool _lowNotified;
        private DetailsForm _details;
        private BatteryReading _lastGood;
        private bool _hintShown;
        private SpdtState _spdt;
        private DpiInfo _dpi;
        private RateInfo _rate;
        private DateTime _settingsStamp = DateTime.MinValue;
        private bool _settingsBusy;

        /// <summary>
        /// 单次读失败后，上一次成功值最多继续显示这么久。
        /// 无线鼠标休眠时收发器完全不转发查询（实测会连续十几秒一帧都收不到），
        /// 而电量本来就是个慢变量，所以窗口给足 10 分钟，别动不动就跳成"未知"。
        /// </summary>
        private const int StaleWindowSeconds = 600;

        public TrayContext(AppSettings settings)
        {
            _settings = settings;
            _current = this;

            // 隐藏宿主窗口：既给 BeginInvoke 提供句柄，也用来接收"用户又点了一次启动"
            HostForm host = new HostForm();
            host.Text = WindowMessages.HostWindowTitle;
            host.ShowInTaskbar = false;
            host.FormBorderStyle = FormBorderStyle.None;
            host.WindowState = FormWindowState.Minimized;
            host.Size = new Size(1, 1);
            host.StartPosition = FormStartPosition.Manual;
            host.Location = new Point(-32000, -32000);

            host.WakeRequested += delegate
            {
                DebugLog.Write("wake message received -> showing details window");
                ShowDetails();
                ShowBalloon("程序已经在运行了",
                    "详情窗口已经打开。\n若看不到右下角的电量图标，点任务栏上的 ^ 展开隐藏图标，"
                    + "再把图标拖到外面就常驻了。",
                    ToolTipIcon.Info);
            };
            host.QuitRequested += delegate { Shutdown(); };

            _host = host;
            IntPtr unused = _host.Handle;
            GC.KeepAlive(unused);

            _tray = new NotifyIcon();
            _tray.Visible = true;
            _tray.Icon = BatteryIconRenderer.Get(-1, false, _settings.IconStyle);
            _tray.Text = "G-Wolves 鼠标电量：正在读取…";
            _tray.MouseClick += OnTrayClick;
            _tray.ContextMenuStrip = BuildMenu();

            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = EffectiveInterval();
            _timer.Tick += delegate { Kick(); };
            _timer.Start();

            Kick();
        }

        private int EffectiveInterval()
        {
            int seconds = _settings.PollSeconds;
            bool detailsOpen = _details != null && !_details.IsDisposed && _details.Visible;
            if (detailsOpen && seconds > 1) seconds = 1;
            return seconds * 1000;
        }

        private ContextMenuStrip BuildMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Font = SystemFonts.MessageBoxFont;

            ToolStripMenuItem details = new ToolStripMenuItem("显示详情");
            details.Click += delegate { ShowDetails(); };
            menu.Items.Add(details);

            ToolStripMenuItem refresh = new ToolStripMenuItem("立即刷新");
            refresh.Click += delegate { Kick(); };
            menu.Items.Add(refresh);

            // ---- SPDT：左/右键各自的开关状态，存在鼠标 EEPROM 里 ----
            ToolStripMenuItem spdtMenu = new ToolStripMenuItem("SPDT（按键开关）");

            ToolStripMenuItem spdtLeft = new ToolStripMenuItem("左键 SPDT");
            spdtLeft.Tag = "spdt:left";
            spdtLeft.Click += delegate
            {
                ToggleSpdt(true, !(_spdt != null && _spdt.Ok && _spdt.Left));
            };
            spdtMenu.DropDownItems.Add(spdtLeft);

            ToolStripMenuItem spdtRight = new ToolStripMenuItem("右键 SPDT");
            spdtRight.Tag = "spdt:right";
            spdtRight.Click += delegate
            {
                ToggleSpdt(false, !(_spdt != null && _spdt.Ok && _spdt.Right));
            };
            spdtMenu.DropDownItems.Add(spdtRight);

            spdtMenu.DropDownItems.Add(new ToolStripSeparator());

            ToolStripMenuItem spdtReload = new ToolStripMenuItem("重新读取状态");
            spdtReload.Click += delegate { RefreshSettings(true); };
            spdtMenu.DropDownItems.Add(spdtReload);

            menu.Items.Add(spdtMenu);

            // ---- 灵敏度（DPI）----
            ToolStripMenuItem dpiMenu = new ToolStripMenuItem("灵敏度（DPI）");
            dpiMenu.Tag = "dpi";

            foreach (int preset in MouseSettings.DpiPresets)
            {
                int captured = preset;
                ToolStripMenuItem item = new ToolStripMenuItem(preset + " DPI");
                item.Tag = "dpi:" + preset;
                item.Click += delegate { ApplyDpi(captured); };
                dpiMenu.DropDownItems.Add(item);
            }

            dpiMenu.DropDownItems.Add(new ToolStripSeparator());

            ToolStripMenuItem dpiDown = new ToolStripMenuItem("减小 " + MouseSettings.DpiButtonStep + " DPI");
            dpiDown.Click += delegate { ApplyDpiStep(-MouseSettings.DpiButtonStep); };
            dpiMenu.DropDownItems.Add(dpiDown);

            ToolStripMenuItem dpiUp = new ToolStripMenuItem("增大 " + MouseSettings.DpiButtonStep + " DPI");
            dpiUp.Click += delegate { ApplyDpiStep(MouseSettings.DpiButtonStep); };
            dpiMenu.DropDownItems.Add(dpiUp);

            dpiMenu.DropDownItems.Add(new ToolStripSeparator());

            ToolStripMenuItem dpiReload = new ToolStripMenuItem("重新读取");
            dpiReload.Click += delegate { RefreshSettings(true); };
            dpiMenu.DropDownItems.Add(dpiReload);

            ToolStripMenuItem dpiApply = new ToolStripMenuItem("让改动生效（重新加载）");
            dpiApply.Click += delegate { ApplySettingsNow(); };
            dpiMenu.DropDownItems.Add(dpiApply);

            menu.Items.Add(dpiMenu);

            // ---- 回报率 ----
            ToolStripMenuItem rateMenu = new ToolStripMenuItem("回报率");
            rateMenu.Tag = "rate";

            foreach (int hz in MouseSettings.RateOptions)
            {
                int captured = hz;
                ToolStripMenuItem item = new ToolStripMenuItem(hz + " Hz");
                item.Tag = "rate:" + hz;
                item.Click += delegate { ApplyRate(captured); };
                rateMenu.DropDownItems.Add(item);
            }

            rateMenu.DropDownItems.Add(new ToolStripSeparator());

            ToolStripMenuItem rateReload = new ToolStripMenuItem("重新读取");
            rateReload.Click += delegate { RefreshSettings(true); };
            rateMenu.DropDownItems.Add(rateReload);

            ToolStripMenuItem rateApply = new ToolStripMenuItem("让改动生效（重新加载）");
            rateApply.Click += delegate { ApplySettingsNow(); };
            rateMenu.DropDownItems.Add(rateApply);

            menu.Items.Add(rateMenu);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem interval = new ToolStripMenuItem("刷新间隔");
            int[] choices = new int[] { 2, 5, 10, 30, 60, 120 };
            foreach (int seconds in choices)
            {
                int captured = seconds;
                ToolStripMenuItem item = new ToolStripMenuItem(seconds + " 秒");
                item.Checked = _settings.PollSeconds == seconds;
                item.Click += delegate
                {
                    _settings.PollSeconds = captured;
                    _settings.Save();
                    _timer.Interval = EffectiveInterval();
                };
                item.Tag = "interval:" + seconds;
                interval.DropDownItems.Add(item);
            }
            menu.Items.Add(interval);

            ToolStripMenuItem style = new ToolStripMenuItem("图标样式");
            string[] names = new string[] { "纯数字（实心底）", "电池图形（无数字）", "纯数字（无底色）" };
            for (int i = 0; i < names.Length; i++)
            {
                int captured = i;
                ToolStripMenuItem item = new ToolStripMenuItem(names[i]);
                item.Checked = _settings.IconStyle == i;
                item.Click += delegate
                {
                    _settings.IconStyle = captured;
                    _settings.Save();
                    Apply(_last);
                };
                item.Tag = "style:" + i;
                style.DropDownItems.Add(item);
            }
            menu.Items.Add(style);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem autostart = new ToolStripMenuItem("开机自动启动");
            autostart.Checked = AutoStart.IsEnabled();
            autostart.Tag = "autostart";
            autostart.Click += delegate
            {
                string error;
                bool ok = autostart.Checked
                    ? AutoStart.Disable(out error)
                    : AutoStart.Enable(out error);

                if (!ok)
                {
                    MessageBox.Show("设置失败：" + error, "G-Wolves 鼠标电量",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };
            menu.Items.Add(autostart);

            ToolStripMenuItem diag = new ToolStripMenuItem("诊断信息…");
            diag.Click += delegate { DiagnosticsForm.ShowUp(); };
            menu.Items.Add(diag);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem exit = new ToolStripMenuItem("退出");
            exit.Click += delegate { Shutdown(); };
            menu.Items.Add(exit);

            // 每次弹出菜单时把勾选状态对齐到当前设置，免得两处状态不一致
            menu.Opening += delegate { SyncChecks(menu.Items); };

            return menu;
        }

        private void SyncChecks(ToolStripItemCollection items)
        {
            foreach (ToolStripItem entry in items)
            {
                ToolStripMenuItem item = entry as ToolStripMenuItem;
                if (item == null) continue;

                string tag = item.Tag as string;
                if (!string.IsNullOrEmpty(tag))
                {
                    if (tag.StartsWith("interval:", StringComparison.Ordinal))
                    {
                        int seconds;
                        if (int.TryParse(tag.Substring(9), out seconds))
                            item.Checked = _settings.PollSeconds == seconds;
                    }
                    else if (tag.StartsWith("style:", StringComparison.Ordinal))
                    {
                        int style;
                        if (int.TryParse(tag.Substring(6), out style))
                            item.Checked = _settings.IconStyle == style;
                    }
                    else if (tag == "autostart")
                    {
                        item.Checked = AutoStart.IsEnabled();
                    }
                    else if (tag == "spdt:left" || tag == "spdt:right")
                    {
                        bool have = _spdt != null && _spdt.Ok;
                        bool on = have && (tag == "spdt:left" ? _spdt.Left : _spdt.Right);

                        item.Checked = on;
                        item.Text = (tag == "spdt:left" ? "左键 SPDT" : "右键 SPDT")
                            + (have ? "" : "（未读到）");
                        item.Enabled = have && !_settingsBusy;
                    }
                    else if (tag == "dpi")
                    {
                        bool have = _dpi != null && _dpi.Ok;
                        item.Text = have
                            ? "灵敏度（DPI）    " + _dpi.CurrentDpi + " DPI"
                            : "灵敏度（DPI）（未读到）";
                        item.Enabled = have && !_settingsBusy;
                    }
                    else if (tag == "rate")
                    {
                        bool have = _rate != null && _rate.Ok && _rate.Hz > 0;
                        item.Text = have
                            ? "回报率    " + _rate.Hz + " Hz"
                            : "回报率（未读到）";
                        item.Enabled = have && !_settingsBusy;
                    }
                    else if (tag.StartsWith("dpi:", StringComparison.Ordinal))
                    {
                        int value;
                        if (int.TryParse(tag.Substring(4), out value))
                        {
                            item.Checked = _dpi != null && _dpi.Ok && _dpi.CurrentDpi == value;
                            item.Enabled = _dpi != null && _dpi.Ok && !_settingsBusy;
                        }
                    }
                    else if (tag.StartsWith("rate:", StringComparison.Ordinal))
                    {
                        int value;
                        if (int.TryParse(tag.Substring(5), out value))
                        {
                            item.Checked = _rate != null && _rate.Ok && _rate.Hz == value;
                            item.Enabled = _rate != null && _rate.Ok && !_settingsBusy;
                        }
                    }
                }

                SyncChecks(item.DropDownItems);
            }
        }

        private void OnTrayClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) ShowDetails();
        }

        /// <summary>后台读一次设备设置（SPDT / 回报率 / DPI）。force=false 时 15 秒内不重复读。</summary>
        private void RefreshSettings(bool force)
        {
            if (_settingsBusy) return;
            if (_device == null) return;
            if (!force && (DateTime.Now - _settingsStamp).TotalSeconds < 15) return;

            _settingsBusy = true;
            HidInterface device = _device;

            ThreadPool.QueueUserWorkItem(delegate
            {
                SpdtState spdt;
                RateInfo rate;
                DpiInfo dpi;

                try { spdt = Spdt.Read(device); }
                catch (Exception ex)
                {
                    spdt = new SpdtState();
                    spdt.Error = ex.GetType().Name + ": " + ex.Message;
                }

                try { rate = MouseSettings.ReadRate(device); }
                catch (Exception ex)
                {
                    rate = new RateInfo();
                    rate.Error = ex.GetType().Name + ": " + ex.Message;
                }

                try { dpi = MouseSettings.ReadDpi(device); }
                catch (Exception ex)
                {
                    dpi = new DpiInfo();
                    dpi.Error = ex.GetType().Name + ": " + ex.Message;
                }

                try
                {
                    _host.BeginInvoke(new Action<SpdtState, RateInfo, DpiInfo>(OnSettingsRead),
                        spdt, rate, dpi);
                }
                catch { _settingsBusy = false; }
            });
        }

        private void OnSettingsRead(SpdtState spdt, RateInfo rate, DpiInfo dpi)
        {
            _settingsBusy = false;
            _settingsStamp = DateTime.Now;

            if (spdt != null && spdt.Ok) _spdt = spdt; else if (_spdt == null) _spdt = spdt;
            if (rate != null && rate.Ok) _rate = rate; else if (_rate == null) _rate = rate;
            if (dpi != null && dpi.Ok) _dpi = dpi; else if (_dpi == null) _dpi = dpi;

            DebugLog.Write("settings read -> " + (spdt == null ? "null" : spdt.Describe())
                + " | 回报率 " + (rate != null && rate.Hz > 0
                    ? rate.Hz + " Hz"
                    : "未知(0x" + (rate != null && rate.Code >= 0 ? rate.Code.ToString("X2") : "??") + ")")
                + " | " + (dpi != null && dpi.Ok
                    ? dpi.CurrentDpi + " DPI（档 " + (dpi.CurrentStage + 1) + "/" + dpi.StageCount + "）"
                    : "DPI 未读到"));
        }

        /// <summary>把当前档的灵敏度改成 dpi（先读-改-写，写完回读校验）。</summary>
        private void ApplyDpi(int dpi)
        {
            if (_settingsBusy) return;

            if (_device == null || _dpi == null || !_dpi.Ok)
            {
                ShowBalloon("暂时改不了灵敏度", "还没读到当前离线设置，动一下鼠标再试。", ToolTipIcon.Warning);
                RefreshSettings(true);
                return;
            }

            if (!MouseSettings.IsValidDpi(dpi))
            {
                ShowBalloon("DPI 不合法",
                    "必须是 " + MouseSettings.DpiStep + " 的倍数，且在 "
                    + MouseSettings.DpiStep + " ~ " + MouseSettings.DpiMax + " 之间。",
                    ToolTipIcon.Warning);
                return;
            }

            _settingsBusy = true;

            HidInterface device = _device;
            int stage = _dpi.CurrentStage;

            ThreadPool.QueueUserWorkItem(delegate
            {
                bool ok = false;
                string error = "";
                try { ok = MouseSettings.WriteDpi(device, stage, dpi, out error); }
                catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }

                try
                {
                    _host.BeginInvoke(new Action<bool, int, string, string>(OnSettingWritten),
                        ok, dpi, error, "灵敏度");
                }
                catch { _settingsBusy = false; }
            });
        }

        private void ApplyDpiStep(int delta)
        {
            if (_dpi == null || !_dpi.Ok)
            {
                ShowBalloon("暂时改不了灵敏度", "还没读到当前离线设置，动一下鼠标再试。", ToolTipIcon.Warning);
                RefreshSettings(true);
                return;
            }

            ApplyDpi(_dpi.CurrentDpi + delta);
        }

        private void ApplyRate(int hz)
        {
            if (_settingsBusy) return;

            if (_device == null || _rate == null || !_rate.Ok)
            {
                ShowBalloon("暂时改不了回报率", "还没读到当前离线设置，动一下鼠标再试。", ToolTipIcon.Warning);
                RefreshSettings(true);
                return;
            }

            _settingsBusy = true;
            HidInterface device = _device;

            ThreadPool.QueueUserWorkItem(delegate
            {
                bool ok = false;
                string error = "";
                try { ok = MouseSettings.WriteRate(device, hz, out error); }
                catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }

                try
                {
                    _host.BeginInvoke(new Action<bool, int, string, string>(OnSettingWritten),
                        ok, hz, error, "回报率");
                }
                catch { _settingsBusy = false; }
            });
        }

        private void ApplySettingsNow()
        {
            if (_settingsBusy || _device == null) return;

            _settingsBusy = true;
            HidInterface device = _device;

            ThreadPool.QueueUserWorkItem(delegate
            {
                bool ok = false;
                string error = "";
                try { ok = MouseSettings.ApplySettings(device, out error); }
                catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }

                try
                {
                    _host.BeginInvoke(new Action<bool, int, string, string>(OnSettingWritten),
                        ok, 0, error, "重新加载");
                }
                catch { _settingsBusy = false; }
            });
        }

        private void OnSettingWritten(bool ok, int value, string error, string what)
        {
            _settingsBusy = false;

            string unit = what == "回报率" ? " Hz" : " DPI";

            if (ok)
            {
                if (what == "重新加载")
                    ShowBalloon("配置已重新加载", "鼠标已重新读取设置，刚改的应该生效了。", ToolTipIcon.Info);
                else
                    ShowBalloon(what + "已设置", what + " 现在是 " + value + unit + "。", ToolTipIcon.Info);
            }
            else
            {
                if (what == "重新加载")
                    ShowBalloon("重新加载失败", error, ToolTipIcon.Warning);
                else
                    ShowBalloon(what + "设置失败",
                        what + " 改成 " + value + unit + " 没成功。\n" + error, ToolTipIcon.Warning);
            }

            DebugLog.Write("setting written: " + what + " value=" + value + " ok=" + ok
                + (string.IsNullOrEmpty(error) ? "" : " error=" + error));

            RefreshSettings(true);
        }

        /// <summary>
        /// 切换 SPDT。走的是"先读回当前值、只改这一位、写完再回读校验"，
        /// 所以即使设备此刻状态不明也不会盲写。
        /// </summary>
        private void ToggleSpdt(bool isLeft, bool target)
        {
            if (_settingsBusy) return;

            if (_device == null || _spdt == null || !_spdt.Ok)
            {
                ShowBalloon("SPDT 暂时不可用",
                    "还没读到当前状态。等设备应答后再试（动一下鼠标），或用「重新读取状态」。",
                    ToolTipIcon.Warning);
                RefreshSettings(true);
                return;
            }

            if (_device.Kind != ProtocolKind.Compx)
            {
                ShowBalloon("SPDT 不可用", "当前设备是「" + _device.KindName + "」，SPDT 只支持 compx 那一族。",
                    ToolTipIcon.Warning);
                return;
            }

            _settingsBusy = true;

            HidInterface device = _device;
            bool? wantLeft = null;
            bool? wantRight = null;
            if (isLeft) wantLeft = target; else wantRight = target;

            ThreadPool.QueueUserWorkItem(delegate
            {
                SpdtState after = null;
                string error = "";

                try { after = Spdt.Write(device, wantLeft, wantRight, out error); }
                catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }

                try { _host.BeginInvoke(new Action<SpdtState, string, bool, bool>(OnSpdtWritten),
                    after, error, isLeft, target); }
                catch { _settingsBusy = false; }
            });
        }

        private void OnSpdtWritten(SpdtState after, string error, bool isLeft, bool target)
        {
            _settingsBusy = false;
            _settingsStamp = DateTime.Now;

            if (after != null && after.Ok) _spdt = after;

            string what = (isLeft ? "左键" : "右键") + " SPDT";

            DebugLog.Write("spdt write " + what + " target=" + target
                + " -> " + (after == null ? "null" : after.Describe())
                + (string.IsNullOrEmpty(error) ? "" : " error=" + error));

            if (after != null && after.Ok && string.IsNullOrEmpty(error))
            {
                ShowBalloon("SPDT 设置成功",
                    what + " 已" + (target ? "打开" : "关闭") + "。\n现在：" + after.Describe(),
                    ToolTipIcon.Info);
            }
            else
            {
                ShowBalloon("SPDT 设置未确认",
                    what + " " + (target ? "打开" : "关闭") + " 失败。\n"
                    + (string.IsNullOrEmpty(error) ? "原因未知" : error)
                    + (after != null && after.Ok ? "\n设备当前：" + after.Describe() : ""),
                    ToolTipIcon.Warning);
            }
        }

        private void ShowDetails()
        {
            if (_details == null || _details.IsDisposed)
            {
                _details = new DetailsForm();
                _details.RefreshRequested += delegate { Kick(); };
            }

            _details.Apply(_last, -1, false, "正在读取…");
            _details.Show();
            _details.WindowState = FormWindowState.Normal;
            _details.Activate();
            _timer.Interval = EffectiveInterval();
            Kick();
        }

        private void Kick()
        {
            if (_busy) return;
            _busy = true;
            DebugLog.Write("kick (interval=" + _timer.Interval + "ms)");

            if (_details != null && !_details.IsDisposed && _details.Visible)
                _details.SetBusy();

            ThreadPool.QueueUserWorkItem(delegate
            {
                BatteryReading reading;
                try { reading = DoRead(); }
                catch (Exception ex)
                {
                    reading = new BatteryReading();
                    reading.Error = ex.GetType().Name + ": " + ex.Message;
                }

                try { _host.BeginInvoke(new Action<BatteryReading>(Apply), reading); }
                catch { _busy = false; }
            });
        }

        private BatteryReading DoRead()
        {
            // 测试钩子：已经拿到过有效值之后，一直返回失败
            if (DebugLog.ForceError && _lastGood != null)
            {
                BatteryReading fake = new BatteryReading();
                fake.Error = "（测试用）模拟读取失败";
                DebugLog.Write("forced-error");
                return fake;
            }

            bool needScan = _device == null
                || (DateTime.Now - _lastScan).TotalSeconds > 30;

            if (needScan) Rescan();

            if (_device == null)
            {
                BatteryReading none = new BatteryReading();
                none.Error = string.IsNullOrEmpty(_scanError)
                    ? "未找到 G-Wolves 接收器"
                    : _scanError;
                return none;
            }

            BatteryReading reading = BatteryClient.Read(_device);

            if (!reading.Ok)
            {
                // 收发器可能刚被拔掉/插线换了 PID：立刻重扫一次再试
                Rescan();
                if (_device != null && !string.Equals(_device.Path, reading.Path, StringComparison.OrdinalIgnoreCase))
                    reading = BatteryClient.Read(_device);
            }

            return reading;
        }

        private void Rescan()
        {
            string error;
            List<HidInterface> all;
            HidInterface found = HidDiscovery.Find(out all, out error);

            _all = all;
            _scanError = error;
            _lastScan = DateTime.Now;

            if (found == null || _device == null
                || !string.Equals(found.Path, _device.Path, StringComparison.OrdinalIgnoreCase))
                _device = found;

            DebugLog.Write("rescan: interfaces=" + all.Count
                + " selected=" + (_device == null ? "(none)" : _device.VidPidHex + " " + _device.KindName));
        }

        /// <summary>
        /// 算出"该显示什么"。读取成功就用新值；只是瞬时失败（设备省电/总线忙）
        /// 就沿用 30 秒内上一次的有效值；确实读不到才退回未知。
        /// </summary>
        private void Effective(BatteryReading reading, out int percent, out bool charging, out bool stale)
        {
            percent = -1;
            charging = false;
            stale = false;

            if (reading != null && reading.Ok)
            {
                percent = reading.Percent;
                charging = reading.Charging;
                return;
            }

            if (_lastGood != null
                && (DateTime.Now - _lastGood.Stamp).TotalSeconds <= StaleWindowSeconds)
            {
                stale = true;
                percent = _lastGood.Percent;
                charging = _lastGood.Charging;
            }
        }

        private void Apply(BatteryReading reading)
        {
            _busy = false;
            _last = reading;

            bool ok = reading != null && reading.Ok;
            if (ok) _lastGood = reading;

            int percent;
            bool charging;
            bool stale;
            Effective(reading, out percent, out charging, out stale);

            // 第一次成功读到值之后再提示"图标在哪" —— 这时托盘图标肯定已经注册好了。
            // Windows 11 默认把新图标塞进折叠区，不提示的话用户会以为程序没启动。
            if (!_hintShown)
            {
                _hintShown = true;
                if (_settings.ShowTrayHint)
                {
                    _settings.ShowTrayHint = false;
                    _settings.Save();
                    ShowBalloon("已经在托盘运行了",
                        "鼠标电量显示在右下角通知区域。\n若看不到图标，点任务栏上的 ^ 展开隐藏图标，"
                        + "再把图标拖到外面就常驻了。\n退出请走图标的右键菜单。",
                        ToolTipIcon.Info);
                }
            }

            if (ok)
            {
                _consecutiveFailures = 0;

                if (reading.Percent <= 15 && !reading.Charging && !_lowNotified)
                {
                    _lowNotified = true;
                    ShowBalloon("鼠标电量偏低", "当前 " + reading.Percent + "%，该充电了。",
                        ToolTipIcon.Warning);
                }
                else if (reading.Charging || reading.Percent > 25)
                {
                    _lowNotified = false;
                }
            }
            else
            {
                _consecutiveFailures++;
                if (_consecutiveFailures == 3)
                {
                    ShowBalloon("读不到电量",
                        (reading != null && !string.IsNullOrEmpty(reading.Error)
                            ? reading.Error : "未找到设备")
                        + "\n若是插着 USB 线充电，接收器会消失，拔线即恢复。",
                        ToolTipIcon.Info);
                }
            }

            _tray.Icon = BatteryIconRenderer.Get(percent, charging, _settings.IconStyle);
            _tray.Text = BuildTooltip(reading, percent, charging, stale);

            if (_details != null && !_details.IsDisposed && _details.Visible)
            {
                BatteryReading shown = ok || !stale ? reading : _lastGood;
                string note = "";
                if (!ok)
                {
                    note = stale
                        ? "暂时读不到（上次成功 "
                          + (_lastGood != null ? _lastGood.Stamp.ToString("HH:mm:ss") : "?")
                          + "）。无线鼠标休眠时收发器不会转发查询，动一下鼠标或按一下按键即可恢复。"
                        : "读取失败：" + (reading != null ? reading.Error : "未知原因");
                }
                _details.Apply(shown, percent, charging, note);
                _timer.Interval = EffectiveInterval();
            }

            // 每 60 秒后台刷一次 SPDT 状态，保证菜单里勾选的是最新的
            if ((DateTime.Now - _settingsStamp).TotalSeconds > 60) RefreshSettings(false);

            DebugLog.Write("apply: ok=" + ok
                + " stale=" + stale
                + " percent=" + percent
                + " charging=" + charging
                + " tip=\"" + _tray.Text + "\""
                + " raw=" + (reading != null ? reading.RawHex : "-")
                + (ok ? "" : " error=" + (reading != null ? reading.Error : "null")));
        }

        private string BuildTooltip(BatteryReading reading, int percent, bool charging, bool stale)
        {
            if (reading == null) return "G-Wolves 鼠标电量：读取中…";

            if (percent >= 0)
            {
                string line = "G-Wolves 鼠标 " + percent + "%";
                if (stale) line += "（" + AgeText() + "）";
                else line += charging ? "（充电中）" : "（未充电）";

                if (!stale && reading.VoltageMv > 0) line += "  " + Util.FormatMv(reading.VoltageMv);
                return Clip(line);
            }

            string text = "G-Wolves 鼠标：读不到电量";
            if (!string.IsNullOrEmpty(reading.Error)) text += "（" + reading.Error + "）";
            return Clip(text);
        }

        /// <summary>上一次成功读数距今多久，用于提示里的"3 分钟前的读数"。</summary>
        private string AgeText()
        {
            if (_lastGood == null) return "尚未刷新";

            int seconds = (int)(DateTime.Now - _lastGood.Stamp).TotalSeconds;
            if (seconds < 0) seconds = 0;
            if (seconds < 60) return seconds + " 秒前";
            return (seconds / 60) + " 分钟前";
        }

        private static string Clip(string text)
        {
            // NotifyIcon.Text 上限 63 个字符，超了会抛异常
            if (text.Length <= 63) return text;
            return text.Substring(0, 60) + "…";
        }

        private void ShowBalloon(string title, string text, ToolTipIcon icon)
        {
            try
            {
                _tray.BalloonTipTitle = title;
                _tray.BalloonTipText = text;
                _tray.BalloonTipIcon = icon;
                _tray.ShowBalloonTip(5000);
            }
            catch { }
        }

        private void Shutdown()
        {
            try { _timer.Stop(); } catch { }
            try { _tray.Visible = false; } catch { }
            try { _tray.Dispose(); } catch { }
            try { _host.Dispose(); } catch { }

            _current = null;
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { if (_tray != null) _tray.Dispose(); } catch { }
                try { if (_host != null) _host.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }
    }

    #endregion

    #region 开机自启（写启动文件夹，不需要管理员权限，也不动注册表）

    internal static class AutoStart
    {
        private const string FileName = "GWMouseBattery.cmd";

        private static string Folder
        {
            get
            {
                string startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
                return startup;
            }
        }

        private static string FilePath
        {
            get { return System.IO.Path.Combine(Folder, FileName); }
        }

        public static bool IsEnabled()
        {
            try { return System.IO.File.Exists(FilePath); }
            catch { return false; }
        }

        public static bool Enable(out string error)
        {
            error = "";
            try
            {
                string folder = Folder;
                if (string.IsNullOrEmpty(folder) || !System.IO.Directory.Exists(folder))
                {
                    error = "找不到启动文件夹（" + (folder ?? "空路径") + "）";
                    return false;
                }

                string exe = Application.ExecutablePath;
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("@echo off");
                sb.AppendLine("rem 由 GWMouseBattery 自动生成：开机静默启动托盘程序");
                sb.AppendLine("start \"\" \"" + exe + "\" --tray");
                System.IO.File.WriteAllText(FilePath, sb.ToString(), Encoding.ASCII);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static bool Disable(out string error)
        {
            error = "";
            try
            {
                if (System.IO.File.Exists(FilePath)) System.IO.File.Delete(FilePath);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }

    #endregion

    #region 快捷方式

    internal static class ShortcutInstaller
    {
        public static string Install()
        {
            string exe = Application.ExecutablePath;
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string target = System.IO.Path.Combine(desktop, "G-Wolves 鼠标电量.lnk");

            try
            {
                Type shell = Type.GetTypeFromProgID("WScript.Shell");
                if (shell == null) return "无法创建快捷方式：系统缺少 WScript.Shell。";

                object obj = Activator.CreateInstance(shell);
                object shortcut = shell.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod,
                    null, obj, new object[] { target });

                Type link = shortcut.GetType();
                link.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null,
                    shortcut, new object[] { exe });
                link.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null,
                    shortcut, new object[] { System.IO.Path.GetDirectoryName(exe) });
                link.InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null,
                    shortcut, new object[] { "在托盘显示 G-Wolves 鼠标电量" });
                link.InvokeMember("IconLocation", System.Reflection.BindingFlags.SetProperty, null,
                    shortcut, new object[] { exe + ",0" });
                link.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, shortcut, null);

                return "已创建桌面快捷方式：" + target;
            }
            catch (Exception ex)
            {
                return "创建快捷方式失败：" + ex.Message;
            }
        }
    }

    #endregion

    #region 入口

    internal static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [STAThread]
        private static int Main(string[] args)
        {
            try { SetProcessDPIAware(); }
            catch { }

            try { Console.OutputEncoding = Encoding.UTF8; }
            catch { }

            string outFile = ValueOf(args, "--out");
            if (!string.IsNullOrEmpty(outFile)) Cli.SetOutFile(outFile);

            DebugLog.Path = ValueOf(args, "--log");
            DebugLog.ForceError = HasFlag(args, "--force-error");

            try
            {
                if (HasFlag(args, "--help") || HasFlag(args, "-h") || HasFlag(args, "/?"))
                {
                    Cli.Write(HelpText());
                    Cli.Flush();
                    return 0;
                }

                if (HasFlag(args, "--version") || HasFlag(args, "-v"))
                {
                    Cli.Write("GWMouseBattery " + DiagnosticsForm.VersionText);
                    Cli.Write("编译于 " + System.IO.File.GetLastWriteTime(
                        System.Reflection.Assembly.GetExecutingAssembly().Location)
                        .ToString("yyyy-MM-dd HH:mm"));
                    Cli.Flush();
                    return 0;
                }

                // 自检：只测逻辑，绝不碰硬件（硬件卡住会把自检拖死，这是踩过的坑）
                if (HasFlag(args, "--selftest"))
                {
                    int code = SelfTest.Run();
                    Cli.Flush();
                    return code;
                }

                if (HasFlag(args, "--diag"))
                {
                    Cli.Write(DiagnosticsForm.BuildReport());
                    Cli.Flush();
                    return 0;
                }

                string iconDir = ValueOf(args, "--dump-icons");
                if (!string.IsNullOrEmpty(iconDir))
                {
                    int code = IconDump.Run(iconDir);
                    Cli.Flush();
                    return code;
                }

                if (HasFlag(args, "--probe") || HasFlag(args, "--json"))
                {
                    return RunProbe(HasFlag(args, "--json"));
                }

                if (HasFlag(args, "--apply")) return RunApply();

                if (HasFlag(args, "--dpi")) return RunDpi(args);
                if (HasFlag(args, "--rate")) return RunRate(args);

                if (HasFlag(args, "--eeprom"))
                {
                    return RunEeprom(args);
                }

                if (HasFlag(args, "--spdt"))
                {
                    return RunSpdt(args);
                }

                if (HasFlag(args, "--sniff"))
                {
                    int seconds = 60;
                    int parsed;
                    string value = ValueOf(args, "--sniff");
                    if (!string.IsNullOrEmpty(value) && int.TryParse(value, out parsed)) seconds = parsed;
                    if (seconds < 1) seconds = 60;
                    if (seconds > 600) seconds = 600;
                    return RunSniff(seconds);
                }

                if (HasFlag(args, "--listen"))
                {
                    int seconds = 15;
                    int parsed;
                    string value = ValueOf(args, "--listen");
                    if (!string.IsNullOrEmpty(value) && int.TryParse(value, out parsed)) seconds = parsed;
                    if (seconds < 1) seconds = 15;
                    if (seconds > 600) seconds = 600;
                    return RunListen(seconds);
                }

                if (HasFlag(args, "--shortcut"))
                {
                    Cli.Write(ShortcutInstaller.Install());
                    Cli.Flush();
                    return 0;
                }

                // 让正在运行的实例退出（重启前用，避免"两个图标"）
                if (HasFlag(args, "--quit")) return RunQuit();

                // 默认 / --tray：常驻托盘
                return RunTray();
            }
            catch (Exception ex)
            {
                Cli.Write("发生未处理的错误：" + ex);
                Cli.Flush();
                return 3;
            }
        }

        private static bool? ParseTriState(string[] args, string key)
        {
            string prefix = key + "=";
            foreach (string a in args)
            {
                if (!a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

                string v = a.Substring(prefix.Length).Trim().ToLowerInvariant();
                if (v == "1" || v == "on" || v == "true" || v == "开") return true;
                if (v == "0" || v == "off" || v == "false" || v == "关") return false;
                return null;
            }
            return null;
        }

        private static string TriText(bool? v)
        {
            if (!v.HasValue) return "不动";
            return v.Value ? "开" : "关";
        }

        /// <summary>
        /// --spdt              读当前 SPDT 状态
        /// --spdt left=1 right=0   设置（读-改-写，只动指定的位，写完回读校验）
        /// </summary>
        private static int RunSpdt(string[] args)
        {
            string error;
            List<HidInterface> all;
            HidInterface device = HidDiscovery.Find(out all, out error);

            if (device == null)
            {
                Cli.Write("没有找到可用的设备接口。");
                Cli.Flush();
                return 2;
            }

            if (device.Kind != ProtocolKind.Compx)
            {
                Cli.Write("该设备是「" + device.KindName + "」，SPDT 目前只实现了 compx（17 字节报文）那一族。");
                Cli.Flush();
                return 2;
            }

            bool? left = ParseTriState(args, "left");
            bool? right = ParseTriState(args, "right");

            Cli.Write("设备：" + device.VidPidHex + "  " + device.KindName);

            if (left == null && right == null)
            {
                SpdtState state = Spdt.Read(device);
                Cli.Write("当前：" + state.Describe());
                if (state.Raw != null) Cli.Write("原始回复：" + Util.Hex(state.Raw, state.Raw.Length));
                Cli.Flush();
                return state.Ok ? 0 : 1;
            }

            Cli.Write("请求：左键 " + TriText(left) + "，右键 " + TriText(right));

            string writeError;
            SpdtState after = Spdt.Write(device, left, right, out writeError);

            if (after == null)
            {
                Cli.Write("写入失败：" + writeError);
                Cli.Flush();
                return 1;
            }

            Cli.Write("写入后：" + after.Describe());
            if (!string.IsNullOrEmpty(writeError)) Cli.Write("警告：" + writeError);
            Cli.Flush();
            return string.IsNullOrEmpty(writeError) ? 0 : 1;
        }

        private static HidInterface RequireCompx(out int code)
        {
            code = 0;
            string error;
            List<HidInterface> all;
            HidInterface device = HidDiscovery.Find(out all, out error);

            if (device == null)
            {
                Cli.Write("没有找到可用的设备接口。");
                code = 2;
                return null;
            }
            if (device.Kind != ProtocolKind.Compx)
            {
                Cli.Write("该设备是「" + device.KindName + "」，这项设置只实现了 compx（17 字节报文）那一族。");
                code = 2;
                return null;
            }
            return device;
        }

        /// <summary>--apply  重新加载一遍配置，让刚写进去的设置生效。</summary>
        private static int RunApply()
        {
            int code;
            HidInterface device = RequireCompx(out code);
            if (device == null) { Cli.Flush(); return code; }

            int profile = MouseSettings.GetProfile(device);
            Cli.Write("设备：" + device.VidPidHex);
            Cli.Write("当前配置档：" + (profile > 0 ? profile.ToString() : "读不到"));

            if (profile < 1 || profile > 5)
            {
                Cli.Write("读不到有效配置档，放弃重新加载。");
                Cli.Flush();
                return 1;
            }

            string error;
            bool ok = MouseSettings.ApplySettings(device, out error);
            Cli.Write("重新加载：" + (ok ? "已发送（切回第 " + profile + " 档）" : "失败（" + error + "）"));

            DpiInfo dpi = MouseSettings.ReadDpi(device);
            RateInfo rate = MouseSettings.ReadRate(device);
            if (dpi.Ok) Cli.Write("当前灵敏度：" + dpi.CurrentDpi + " DPI（档 " + (dpi.CurrentStage + 1) + "/" + dpi.StageCount + "）");
            if (rate.Ok && rate.Hz > 0) Cli.Write("当前回报率：" + rate.Hz + " Hz");

            Cli.Flush();
            return ok ? 0 : 1;
        }

        /// <summary>--dpi             读当前灵敏度；--dpi &lt;值&gt;  设置（必须是 50 的倍数）。</summary>
        private static int RunDpi(string[] args)
        {
            int code;
            HidInterface device = RequireCompx(out code);
            if (device == null) { Cli.Flush(); return code; }

            Cli.Write("设备：" + device.VidPidHex + "  " + device.KindName);

            string value = ValueOf(args, "--dpi");
            int dpi = 0;
            bool parsed = !string.IsNullOrEmpty(value) && int.TryParse(value, out dpi);
            bool wantWrite = parsed;

            if (!wantWrite)
            {
                DpiInfo info = MouseSettings.ReadDpi(device);
                if (!info.Ok)
                {
                    Cli.Write("读取失败：" + info.Error);
                    Cli.Flush();
                    return 1;
                }

                Cli.Write("档位数 " + info.StageCount + "，当前第 " + (info.CurrentStage + 1) + " 档");
                Cli.Write("当前灵敏度：" + info.CurrentDpi + " DPI");
                for (int i = 0; i < MouseSettings.MaxStages; i++)
                    Cli.Write("   档 " + (i + 1) + (i == info.CurrentStage ? " *" : "  ") + " : "
                        + info.StageDpi[i] + " DPI" + (i < info.StageCount ? "" : "   （未启用）"));
                Cli.Write("原始（地址 " + MouseSettings.DpiAreaBase + "（0x"
                    + MouseSettings.DpiAreaBase.ToString("X") + "）起 "
                    + (MouseSettings.MaxStages * MouseSettings.DpiStageStride) + " 字节，每档 "
                    + MouseSettings.DpiStageStride + " 字节）：" + info.RawHex);
                Cli.Flush();
                return 0;
            }

            DpiInfo before = MouseSettings.ReadDpi(device);
            if (!before.Ok)
            {
                Cli.Write("写入前读取失败：" + before.Error);
                Cli.Flush();
                return 1;
            }

            string error;
            bool ok = MouseSettings.WriteDpi(device, before.CurrentStage, dpi, out error);

            DpiInfo after = MouseSettings.ReadDpi(device);
            Cli.Write("把第 " + (before.CurrentStage + 1) + " 档设为 " + dpi + " DPI："
                + (ok ? "成功" : "失败（" + error + "）"));
            if (after.Ok) Cli.Write("回读：当前灵敏度 " + after.CurrentDpi + " DPI");
            Cli.Flush();
            return ok ? 0 : 1;
        }

        /// <summary>--rate            读当前回报率；--rate &lt;Hz&gt;  设置。</summary>
        private static int RunRate(string[] args)
        {
            int code;
            HidInterface device = RequireCompx(out code);
            if (device == null) { Cli.Flush(); return code; }

            Cli.Write("设备：" + device.VidPidHex + "  " + device.KindName);

            string value = ValueOf(args, "--rate");
            int hz = 0;
            bool parsed = !string.IsNullOrEmpty(value) && int.TryParse(value, out hz);
            bool wantWrite = parsed;

            if (!wantWrite)
            {
                RateInfo info = MouseSettings.ReadRate(device);
                if (!info.Ok)
                {
                    Cli.Write("读取失败：" + info.Error);
                    Cli.Flush();
                    return 1;
                }

                Cli.Write("原始（地址 0 起 2 字节）：" + info.RawHex);
                Cli.Write("当前回报率：" + (info.Hz > 0 ? info.Hz + " Hz" : "未知（存储值 0x" + info.Code.ToString("X2") + "）"));
                Cli.Write("可选：" + string.Join(" / ", Array.ConvertAll(MouseSettings.RateOptions,
                    delegate (int v) { return v.ToString(); })) + " Hz");
                Cli.Flush();
                return 0;
            }

            string error;
            bool ok = MouseSettings.WriteRate(device, hz, out error);

            RateInfo after = MouseSettings.ReadRate(device);
            Cli.Write("把回报率设为 " + hz + " Hz：" + (ok ? "成功" : "失败（" + error + "）"));
            if (after.Ok && after.Hz > 0) Cli.Write("回读：当前回报率 " + after.Hz + " Hz");
            Cli.Flush();
            return ok ? 0 : 1;
        }

        /// <summary>--eeprom &lt;地址&gt; [长度]   只读，打印 EEPROM 内容。</summary>
        private static int RunEeprom(string[] args)
        {
            string error;
            List<HidInterface> all;
            HidInterface device = HidDiscovery.Find(out all, out error);

            if (device == null || device.Kind != ProtocolKind.Compx)
            {
                Cli.Write("需要一台 compx 设备（17 字节报文）。");
                Cli.Flush();
                return 2;
            }

            string value = ValueOf(args, "--eeprom");
            int address;
            if (string.IsNullOrEmpty(value) || !int.TryParse(value, out address))
            {
                Cli.Write("用法: --eeprom <起始地址> [长度]");
                Cli.Flush();
                return 2;
            }

            int length = 32;
            int index = Array.IndexOf(args, value);
            if (index + 1 < args.Length)
            {
                int parsed;
                if (int.TryParse(args[index + 1], out parsed) && parsed > 0) length = parsed;
            }
            if (length > 256) length = 256;

            byte[] data;
            if (!Eeprom.Read(device, address, length, out data, out error))
            {
                Cli.Write("读取失败：" + error);
                Cli.Flush();
                return 1;
            }

            Cli.Write("EEPROM 地址 " + address + " 起 " + length + " 字节：");
            for (int i = 0; i < length; i += 16)
            {
                StringBuilder line = new StringBuilder();
                line.Append("  ").Append((address + i).ToString("D5")).Append(" : ");
                for (int j = 0; j < 16 && i + j < length; j++)
                    line.Append(data[i + j].ToString("X2")).Append(' ');
                Cli.Write(line.ToString());
            }

            Cli.Flush();
            return 0;
        }

        /// <summary>
        /// --sniff [秒数]  只监听不发命令。用来观察"别的程序（官方驱动/网页）对设备做了什么"。
        /// </summary>
        private static int RunSniff(int seconds)
        {
            int code;
            HidInterface device = RequireCompx(out code);
            if (device == null) { Cli.Flush(); return code; }

            Cli.Write("设备：" + device.VidPidHex);
            Cli.Write("接口：" + device.Path);
            Cli.Write("只监听 " + seconds + " 秒，**不发任何命令**。");
            Cli.Write("请在监听期间去网页驱动里改一次设置（例如把 DPI 从 A 改成 B）。");
            Cli.Blank();

            BatteryClient.SniffCompx(device, seconds, Cli.Write);

            Cli.Flush();
            return 0;
        }

        private static int RunListen(int seconds)
        {
            string error;
            List<HidInterface> all;
            HidInterface device = HidDiscovery.Find(out all, out error);

            if (device == null)
            {
                Cli.Write("没有找到可用的设备接口。");
                Cli.Flush();
                return 2;
            }

            if (device.Kind != ProtocolKind.Compx)
            {
                Cli.Write("该设备不属于 compx 家族（" + device.KindName + "），本模式只支持 compx。");
                Cli.Flush();
                return 2;
            }

            Cli.Write("监听接口：" + device.Path);
            Cli.Write("时长 " + seconds + " 秒，每秒发一次电量命令；下面列出收到的每一帧。");
            Cli.Write("如果看到 echo 不是 04 的帧，说明有别的程序在同时跟这只设备说话。");
            Cli.Blank();

            BatteryClient.ListenCompx(device, seconds, Cli.Write);

            Cli.Blank();
            Cli.Write("监听结束。");
            Cli.Flush();
            return 0;
        }

        private static int RunProbe(bool json)
        {
            string error;
            List<HidInterface> all;
            HidInterface device = HidDiscovery.Find(out all, out error);

            if (device == null)
            {
                if (json)
                {
                    Cli.Write("{\"ok\":false,\"error\":"
                        + Util.JsonEscape(string.IsNullOrEmpty(error) ? "未找到 G-Wolves 接收器" : error)
                        + ",\"interfaces\":" + all.Count + "}");
                }
                else
                {
                    Cli.Write("没有找到可用的 G-Wolves 接收器（HID 接口 " + all.Count + " 个）。");
                    if (!string.IsNullOrEmpty(error)) Cli.Write("枚举错误：" + error);
                    Cli.Write("提示：插着 USB 线充电时收发器会消失，拔线即恢复。");
                }
                Cli.Flush();
                return 2;
            }

            BatteryReading reading = BatteryClient.Read(device);

            if (json)
            {
                StringBuilder sb = new StringBuilder();
                sb.Append("{");
                sb.Append("\"ok\":").Append(reading.Ok ? "true" : "false").Append(",");
                sb.Append("\"battery\":").Append(reading.Ok ? reading.Percent.ToString() : "null").Append(",");
                sb.Append("\"charging\":").Append(reading.Charging ? "true" : "false").Append(",");
                sb.Append("\"voltageMv\":").Append(reading.VoltageMv > 0 ? reading.VoltageMv.ToString() : "null").Append(",");
                sb.Append("\"state\":").Append(Util.JsonEscape(Util.StateText(reading.Percent))).Append(",");
                sb.Append("\"protocol\":").Append(Util.JsonEscape(reading.Protocol)).Append(",");
                sb.Append("\"vidPid\":").Append(Util.JsonEscape(device.VidPidHex)).Append(",");
                sb.Append("\"raw\":").Append(Util.JsonEscape(reading.RawHex)).Append(",");
                sb.Append("\"timestamp\":").Append(Util.JsonEscape(reading.Stamp.ToString("yyyy-MM-dd HH:mm:ss")));
                if (!reading.Ok) sb.Append(",\"error\":").Append(Util.JsonEscape(reading.Error));
                sb.Append("}");
                Cli.Write(sb.ToString());
            }
            else
            {
                if (reading.Ok)
                {
                    Cli.Write("G-Wolves 鼠标电量: " + reading.Percent + "%   "
                        + (reading.Charging ? "充电中" : "未充电")
                        + "   " + Util.StateText(reading.Percent)
                        + (reading.VoltageMv > 0 ? "   电压 " + Util.FormatMv(reading.VoltageMv) : ""));
                }
                else
                {
                    Cli.Write("读取失败: " + reading.Error);
                }
                Cli.Write("设备: " + device.VidPidHex + "  " + device.KindName);
                Cli.Write("接口: " + device.Path);
                Cli.Write("原始报文: " + reading.RawHex);
            }

            Cli.Flush();
            return reading.Ok ? 0 : 1;
        }

        private static IntPtr FindRunningHost()
        {
            try
            {
                IntPtr h = Native.FindWindowW(null, WindowMessages.HostWindowTitle);
                DebugLog.Write("FindWindow(" + WindowMessages.HostWindowTitle + ") -> " + h);
                return h;
            }
            catch { return IntPtr.Zero; }
        }

        /// <summary>请求已有实例把详情窗口打开。成功返回 true。</summary>
        private static bool WakeRunningInstance()
        {
            if (WindowMessages.Wake == 0) return false;

            IntPtr host = FindRunningHost();
            if (host == IntPtr.Zero) return false;

            try
            {
                Native.PostMessageW(host, WindowMessages.Wake, IntPtr.Zero, IntPtr.Zero);
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// 尝试唤醒已运行的实例：先立刻试一次（正常情况第一次就中），再每隔 500 毫秒
        /// 重试若干次 —— 对方可能正在读设备，或者还在启动、尚未建好宿主窗口。
        /// 成功返回 true，调用方此时应当安静退出。
        /// </summary>
        private static bool TryWakeWithRetries()
        {
            for (int i = 0; i < 9; i++)
            {
                if (i > 0) Thread.Sleep(500);
                if (WakeRunningInstance())
                {
                    DebugLog.Write("woke the running instance (attempt " + (i + 1) + ")");
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 礼貌地请其它同名实例自己退出：给它发 Quit 消息，然后等它退干净。
        /// 走正常退出流程的实例会先摘掉自己的托盘图标，所以不会留下僵尸图标。
        /// 返回真正退出的实例数；对方不理人时返回 0，由调用方决定是否强杀。
        /// </summary>
        private static int AskOtherInstancesToQuit()
        {
            if (WindowMessages.Quit == 0) return 0;

            IntPtr host = FindRunningHost();
            if (host == IntPtr.Zero) return 0;

            try { Native.PostMessageW(host, WindowMessages.Quit, IntPtr.Zero, IntPtr.Zero); }
            catch { return 0; }

            System.Diagnostics.Process self = System.Diagnostics.Process.GetCurrentProcess();

            // 最多等 2.5 秒，看它有没有自己走完退出流程
            for (int i = 0; i < 10; i++)
            {
                Thread.Sleep(250);

                int alive = 0;
                try
                {
                    foreach (System.Diagnostics.Process q in
                        System.Diagnostics.Process.GetProcessesByName(self.ProcessName))
                    {
                        if (q.Id != self.Id) alive++;
                    }
                }
                catch { return 0; }

                if (alive == 0)
                {
                    DebugLog.Write("the other instance exited gracefully");
                    return 1;
                }
            }

            DebugLog.Write("the other instance ignored the quit request");
            return 0;
        }

        /// <summary>
        /// 结束其它同名实例（不含自己）。只在"互斥体被占、又找不到它的窗口"时用，
        /// 目的是避免用户看到两个托盘图标，也避免新版旧版同时跑。
        /// 没有权限就返回 0，调用方照样继续启动。
        /// </summary>
        private static int KillOtherInstances()
        {
            int killed = 0;
            try
            {
                System.Diagnostics.Process self = System.Diagnostics.Process.GetCurrentProcess();
                System.Diagnostics.Process[] all =
                    System.Diagnostics.Process.GetProcessesByName(self.ProcessName);

                foreach (System.Diagnostics.Process p in all)
                {
                    if (p.Id == self.Id) continue;
                    try
                    {
                        p.Kill();
                        p.WaitForExit(3000);
                        killed++;
                        DebugLog.Write("killed stale instance pid=" + p.Id);
                    }
                    catch (Exception ex)
                    {
                        DebugLog.Write("could not kill pid=" + p.Id + ": " + ex.Message);
                    }
                    finally
                    {
                        try { p.Dispose(); } catch { }
                    }
                }
            }
            catch { }

            return killed;
        }

        private static int RunTray()
        {
            // 用户又双击了一次启动 —— 不要让新进程悄悄退出（那样看起来就是"点了没反应"），
            // 而是让已经在跑的实例把详情窗口弹出来。
            if (WakeRunningInstance())
            {
                Cli.Write("G-Wolves 鼠标电量已经在运行了。已让托盘程序打开详情窗口（看右下角通知区域）。");
                Cli.Write("如果看不到图标：点任务栏上的 ^ 展开隐藏图标，把图标拖出来即可常驻。");
                Cli.Flush();
                return 0;
            }

            bool created;
            Mutex mutex = null;
            try
            {
                mutex = new Mutex(true, "GWMouseBattery.SingleInstance", out created);
            }
            catch
            {
                created = true;
            }

            if (!created)
            {
                // 已经有实例在跑了。处理顺序很重要：
                //
                //   1) 先唤醒它（正常情况第一步就成功，本进程安静退出，不显示任何图标）
                //   2) 唤不动就**礼貌请它自己退出** —— 它会走正常退出流程，
                //      顺带把托盘图标摘掉
                //   3) 只有它彻底不理人（卡死）才强杀
                //
                // 为什么不能一上来就强杀：被强杀的进程来不及执行"移除托盘图标"，
                // Windows 也不会因为进程死了就自动清理那个图标，于是通知区域会留下
                // 一个点不动、也不会自己消失的"僵尸图标"。用户会看到两个图标，
                // 而且关掉真正在工作的那个之后就再也没有电量显示了。
                if (TryWakeWithRetries())
                {
                    Cli.Write("G-Wolves 鼠标电量已经在运行了。已让托盘程序打开详情窗口。");
                    Cli.Flush();
                    return 0;
                }

                if (AskOtherInstancesToQuit() == 0)
                {
                    int killed = KillOtherInstances();
                    DebugLog.Write("forced kill of unresponsive instance(s): " + killed);
                    if (killed > 0) Thread.Sleep(600);
                }
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // 托盘模式下，任何未处理异常都必须先把托盘图标摘掉再退出。
            // 否则进程一崩，Windows 会把图标留在通知区域，变成一个点不动、
            // 也不会自己消失的"僵尸图标" —— 看起来就像程序还在运行。
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e)
            {
                DebugLog.Write("UNHANDLED UI EXCEPTION: " + e.Exception);
                TrayContext.HideCurrentIcon();
                Environment.Exit(1);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
            {
                DebugLog.Write("UNHANDLED EXCEPTION: " + e.ExceptionObject);
                TrayContext.HideCurrentIcon();
            };

            Application.Run(new TrayContext(AppSettings.Load()));

            GC.KeepAlive(mutex);
            return 0;
        }

        private static int RunQuit()
        {
            if (WindowMessages.Quit != 0)
            {
                IntPtr host = FindRunningHost();
                if (host != IntPtr.Zero)
                {
                    try
                    {
                        Native.PostMessageW(host, WindowMessages.Quit, IntPtr.Zero, IntPtr.Zero);
                        Thread.Sleep(700);
                        Cli.Write("已请求正在运行的实例退出。");
                        Cli.Flush();
                        return 0;
                    }
                    catch { }
                }
            }

            // 找不到具名宿主窗口（例如旧版本）时，退化成按进程名结束
            int killed = KillOtherInstances();
            if (killed > 0)
            {
                Cli.Write("已结束 " + killed + " 个正在运行的实例。");
                Cli.Flush();
                return 0;
            }

            Cli.Write("没有找到正在运行的实例。");
            Cli.Flush();
            return 0;
        }

        private static bool HasFlag(string[] args, string flag)
        {
            foreach (string a in args)
                if (string.Equals(a, flag, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static string ValueOf(string[] args, string flag)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
                    return i + 1 < args.Length ? args[i + 1] : null;

                string prefix = flag + "=";
                if (args[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return args[i].Substring(prefix.Length);
            }
            return null;
        }

        private static string HelpText()
        {
            return string.Join("\r\n", new string[]
            {
                "G-Wolves 鼠标电量（托盘显示）",
                "",
                "  不带参数 / --tray   常驻托盘（双击 exe 就是这个）",
                "  --probe             读一次电量并打印",
                "  --json              读一次电量，以 JSON 输出",
                "  --diag              打印诊断报告（列出所有 HID 接口）",
                "  --listen [秒数]     监听接口，打印收到的每一帧（排查抢设备/休眠）",
                "  --sniff [秒数]      只监听不发命令，看别的程序对设备做了什么",
                "  --spdt              读取鼠标的 SPDT 开关状态",
                "  --spdt left=1 right=0   设置 SPDT（读-改-写，写完自动回读校验）",
                "  --dpi               读取灵敏度（DPI）",
                "  --dpi 1600          设置灵敏度（1 ~ 40000，任意值，无步进限制）",
                "  --rate              读取回报率",
                "  --rate 1000         设置回报率（125/250/500/1000/2000/4000/8000）",
                "  --eeprom <地址> [长度]  只读，打印鼠标 EEPROM 内容（排查用）",
                "  --selftest          运行核心逻辑自检（不访问硬件）",
                "  --shortcut          在桌面创建快捷方式",
                "  --quit              让正在运行的托盘程序退出",
                "  --out <文件>        把上面的文字输出同时写入文件",
                "  --version           显示程序版本",
                "  --help              显示本帮助",
                "",
                "  托盘图标：左键单击 = 详情窗口，右键 = 菜单",
                "  退出：右键菜单里的「退出」"
            });
        }
    }

    #endregion
}
