using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Windows.Forms;

[assembly: AssemblyTitle("KeyParcel")]
[assembly: AssemblyProduct("KeyParcel")]
[assembly: AssemblyDescription("局域网文字接力")]
[assembly: AssemblyCompany("0x3fffff")]
[assembly: AssemblyCopyright("Copyright © 2026 0x3fffff")]
[assembly: AssemblyVersion("1.5.0.0")]

namespace KeyParcel
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KeyParcel");
            Application.Run(new ParcelWindow(folder, MaestroKeyboard.Create()));
        }
    }

    internal static class Theme
    {
        internal const string Author = "By:0x3fffff";
        internal const string AuthorUrl = "https://wenhao.li/";
        internal static readonly Color Canvas = Color.White;
        internal static readonly Color Paper = Color.White;
        internal static readonly Color Ink = Color.FromArgb(28, 25, 31);
        internal static readonly Color Muted = Color.FromArgb(100, 91, 104);
        internal static readonly Color Edge = Color.FromArgb(231, 222, 232);
        internal static readonly Color Accent = Color.FromArgb(125, 39, 161);
        internal static readonly Color Tint = Color.FromArgb(253, 245, 250);
        internal static readonly Color FieldColor = Color.FromArgb(250, 247, 251);
        internal static readonly Color Coral = Color.FromArgb(243, 116, 89);
        internal static readonly Color Berry = Color.FromArgb(198, 57, 139);
        internal static readonly Font Body = new Font("Microsoft YaHei UI", 9);
        internal static readonly Font Small = new Font("Microsoft YaHei UI", 8);
        internal static readonly Font Section = new Font("Microsoft YaHei UI", 11, FontStyle.Bold);
        internal static readonly Font Display = new Font("Segoe UI", 23, FontStyle.Bold);
        internal static readonly Font Chord = new Font("Consolas", 10);

        internal static void Window(Form form, string title, int width, int height)
        {
            form.SuspendLayout();
            form.AutoScaleDimensions = new SizeF(96, 96);
            form.AutoScaleMode = AutoScaleMode.Dpi;
            form.Text = title;
            form.Font = Body;
            form.ForeColor = Ink;
            form.BackColor = Canvas;
            form.AutoScroll = true;
            form.ClientSize = new Size(width, height);
            form.FormBorderStyle = FormBorderStyle.FixedSingle;
            form.MaximizeBox = false;
            form.StartPosition = FormStartPosition.CenterScreen;
            form.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        }

        internal static TableLayoutPanel Table(params int[] columns)
        {
            return Configure(new TableLayoutPanel(), columns);
        }

        internal static TableLayoutPanel Card(params int[] columns)
        {
            return Configure(new RoundedPanel(Tint, Edge, 15), columns);
        }

        private static TableLayoutPanel Configure(TableLayoutPanel table, int[] columns)
        {
            table.ColumnCount = columns.Length;
            table.AutoSize = true;
            table.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            table.Margin = Padding.Empty;
            table.Dock = DockStyle.Top;
            foreach (int column in columns)
                table.ColumnStyles.Add(column == 0 ? new ColumnStyle(SizeType.Percent, 100) :
                    column < 0 ? new ColumnStyle(SizeType.AutoSize) : new ColumnStyle(SizeType.Absolute, column));
            return table;
        }

        internal static void Put(TableLayoutPanel parent, Control control, int column, int row, Padding margin)
        {
            control.Margin = margin;
            while (parent.RowStyles.Count <= row) parent.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            parent.Controls.Add(control, column, row);
        }

        internal static void Stretch(TableLayoutPanel table, int row)
        {
            table.AutoSize = false;
            table.Dock = DockStyle.Fill;
            table.RowStyles[row].SizeType = SizeType.Percent;
            table.RowStyles[row].Height = 100;
        }

        internal static Label Label(string text, Font font, Color color)
        {
            return new Label { Text = text, Font = font, ForeColor = color, AutoSize = true,
                Anchor = AnchorStyles.Left, TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false };
        }

        internal static Button Button(string text, bool primary = false)
        {
            return new RoundedButton(primary) { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = new Size(68, 36), Padding = new Padding(13, 4, 13, 4),
                Anchor = AnchorStyles.Left | AnchorStyles.Right };
        }

        internal static Control Field(Control editor, int horizontal = 10, bool stretch = false)
        {
            var shell = (RoundedPanel)Configure(new RoundedPanel(FieldColor, Edge, 9), new[] { 0 });
            shell.Dock = DockStyle.None;
            shell.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            shell.Padding = new Padding(horizontal, 7, horizontal, 7);
            editor.BackColor = FieldColor;
            editor.ForeColor = Ink;
            editor.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            editor.GotFocus += delegate { shell.Invalidate(); };
            editor.LostFocus += delegate { shell.Invalidate(); };
            shell.Click += delegate { editor.Focus(); };
            Put(shell, editor, 0, 0, Padding.Empty);
            if (stretch)
            {
                editor.Dock = DockStyle.Fill;
                Stretch(shell, 0);
            }
            return shell;
        }

        internal static LinkLabel AuthorLink(Action<string> feedback)
        {
            var link = new LinkLabel { Text = Author, Font = Small, AutoSize = true, TabStop = true,
                Anchor = AnchorStyles.Left, LinkColor = Accent, ActiveLinkColor = Berry,
                VisitedLinkColor = Accent, LinkBehavior = LinkBehavior.HoverUnderline };
            link.Links.Add(0, Author.Length, AuthorUrl);
            link.LinkClicked += delegate(object sender, LinkLabelLinkClickedEventArgs args)
            {
                try { Process.Start(new ProcessStartInfo((string)args.Link.LinkData) { UseShellExecute = true }); }
                catch (System.ComponentModel.Win32Exception) { feedback("无法打开浏览器。 " + AuthorUrl); }
            };
            return link;
        }

        internal static Control Badge()
        {
            var badge = new Panel { Size = new Size(46, 46), Anchor = AnchorStyles.Left, AccessibleName = "KeyParcel" };
            badge.Paint += delegate(object sender, PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var shape = Shape(badge.ClientRectangle, badge.Height * 0.28f))
                using (var gradient = new LinearGradientBrush(badge.ClientRectangle, Coral, Accent, 45f))
                using (var pen = new Pen(Paper, badge.Height * 0.055f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                {
                    e.Graphics.FillPath(gradient, shape);
                    float s = badge.Height;
                    e.Graphics.DrawLines(pen, new[] { new PointF(s*.32f, s*.29f), new PointF(s*.32f, s*.61f),
                        new PointF(s*.68f, s*.61f), new PointF(s*.56f, s*.49f) });
                    e.Graphics.DrawLine(pen, s*.68f, s*.61f, s*.56f, s*.73f);
                }
            };
            return badge;
        }

        private static GraphicsPath Shape(Rectangle bounds, float radius)
        {
            var path = new GraphicsPath();
            float diameter = Math.Min(radius * 2, Math.Min(bounds.Width - 1, bounds.Height - 1));
            if (diameter < 1) return path;
            float right = bounds.Right - diameter - 1, bottom = bounds.Bottom - diameter - 1;
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(right, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(right, bottom, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bottom, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        private sealed class RoundedPanel : TableLayoutPanel
        {
            private readonly Color fill, border;
            private readonly int radius;
            internal Color SurfaceColor { get { return fill; } }

            internal RoundedPanel(Color fill, Color border, int radius)
            {
                this.fill = fill;
                this.border = border;
                this.radius = radius;
                SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer |
                    ControlStyles.ResizeRedraw, true);
                BackColor = Color.Transparent;
            }

            protected override void OnPaintBackground(PaintEventArgs e)
            {
                base.OnPaintBackground(e);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var shape = Shape(ClientRectangle, radius * e.Graphics.DpiX / 96f))
                using (var brush = new SolidBrush(fill))
                using (var pen = new Pen(ContainsFocus && Controls.Count == 1 ? Accent : border))
                {
                    e.Graphics.FillPath(brush, shape);
                    e.Graphics.DrawPath(pen, shape);
                }
            }
        }

        private sealed class RoundedButton : Button
        {
            private readonly bool primary;
            private bool hover, down;

            internal RoundedButton(bool primary)
            {
                this.primary = primary;
                FlatStyle = FlatStyle.Flat;
                FlatAppearance.BorderSize = 0;
                UseVisualStyleBackColor = false;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.Opaque |
                    ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            }

            protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
            protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }
            protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); down = e.Button == MouseButtons.Left; Invalidate(); }
            protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); down = false; Invalidate(); }
            protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); down = false; Invalidate(); }

            protected override void OnPaint(PaintEventArgs e)
            {
                // WinForms can hand us a fresh buffer on any repaint. Paint the corner pixels too.
                using (var background = new SolidBrush(SurfaceBehind()))
                    e.Graphics.FillRectangle(background, ClientRectangle);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                Color fill = !Enabled ? FieldColor : primary
                    ? (down ? Color.FromArgb(97, 29, 128) : hover ? Color.FromArgb(148, 45, 178) : Accent)
                    : (down ? Edge : hover ? Tint : Paper);
                using (var shape = Shape(ClientRectangle, 10 * e.Graphics.DpiX / 96f))
                using (var brush = new SolidBrush(fill))
                using (var pen = new Pen(Focused ? Berry : primary && Enabled ? fill : Edge))
                {
                    e.Graphics.FillPath(brush, shape);
                    e.Graphics.DrawPath(pen, shape);
                }
                TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle,
                    !Enabled ? Muted : primary ? Paper : Ink, TextFormatFlags.HorizontalCenter |
                    TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            }

            private Color SurfaceBehind()
            {
                for (Control parent = Parent; parent != null; parent = parent.Parent)
                {
                    var card = parent as RoundedPanel;
                    if (card != null) return card.SurfaceColor;
                    if (parent is Form) return parent.BackColor;
                }
                return Canvas;
            }
        }
    }

    internal sealed class ShortcutField : TextBox
    {
        internal event Action<Keys> Recorded;

        internal ShortcutField(Keys current, string accessibleName)
        {
            ReadOnly = true;
            ShortcutsEnabled = false;
            BorderStyle = BorderStyle.None;
            Font = Theme.Chord;
            AccessibleName = accessibleName;
            Text = Hotkeys.Display(current);
        }

        protected override bool ProcessCmdKey(ref Message message, Keys keyData)
        {
            // Capture before TextBoxBase handles commands such as Ctrl+Z and Ctrl+A.
            if (!Hotkeys.Valid(keyData)) return base.ProcessCmdKey(ref message, keyData);
            Text = Hotkeys.Display(keyData);
            if (Recorded != null) Recorded(keyData);
            return true;
        }
    }

    internal sealed class ParcelWindow : Form
    {
        private readonly string folder;
        private readonly MobileInbox inbox;
        private readonly QuickTextStore quickText;
        private readonly Hotkeys hotkeys;
        private readonly PacedInput input;
        private readonly InputModes inputModes;
        private readonly NumericUpDown delay;
        private readonly Label state, message, quickTextState, inputModeState;
        private readonly List<string> quickBindings = new List<string>();
        private readonly Timer refresh = new Timer { Interval = 400 };
        private readonly Timer inputNoticeTimer = new Timer { Interval = 5000 };
        private NotifyIcon inputNotice;
        private bool rimeSetupRunning;
        private readonly Button installChinese;
        private MobileServer receiver;
        private IPAddress lastAddress;

        internal ParcelWindow(string folder, IVirtualKeyboard virtualKeyboard = null)
        {
            this.folder = folder;
            inbox = new MobileInbox(folder);
            quickText = new QuickTextStore(folder);
            inputModes = new InputModes(virtualKeyboard, folder);
            Theme.Window(this, "KeyParcel", 516, 560);
            var root = Theme.Table(0);
            root.Padding = new Padding(24, 19, 24, 12);
            Controls.Add(root);
            var header = Theme.Table(-1, 0);
            Theme.Put(header, Theme.Badge(), 0, 0, new Padding(0, 0, 13, 0));
            var title = Theme.Table(0);
            Theme.Put(title, Theme.Label("KeyParcel", Theme.Display, Theme.Ink), 0, 0, Padding.Empty);
            Theme.Put(title, Theme.Label("手机文字，一键输入到电脑", Theme.Body, Theme.Muted),
                0, 1, new Padding(1, 1, 0, 0));
            Theme.Put(header, title, 1, 0, Padding.Empty);
            Theme.Put(root, header, 0, 0, new Padding(0, 0, 0, 19));

            var lan = Theme.Card(0, -1);
            lan.Padding = new Padding(14, 12, 14, 12);
            var summary = Theme.Table(0);
            Theme.Put(summary, Theme.Label("手机接力", Theme.Section, Theme.Ink), 0, 0, Padding.Empty);
            state = Theme.Label("", Theme.Small, Theme.Muted);
            Theme.Put(summary, state, 0, 1, new Padding(0, 4, 0, 0));
            Theme.Put(lan, summary, 0, 0, new Padding(0, 0, 14, 0));
            var connect = Theme.Button("连接手机", true);
            connect.Name = "ConnectPhone";
            Theme.Put(lan, connect, 1, 0, Padding.Empty);
            Theme.Put(root, lan, 0, 2, new Padding(0, 0, 0, 18));
            connect.Click += delegate
            {
                using (var dialog = new PairingWindow(inbox, () => receiver, StartReceiver, StopReceiver, lastAddress))
                    dialog.ShowDialog(this);
                RefreshState();
            };

            var quickRow = Theme.Table(0, -1);
            quickTextState = Theme.Label("", Theme.Body, Theme.Muted);
            Theme.Put(quickRow, quickTextState, 0, 0, new Padding(0, 0, 8, 0));
            var manage = Theme.Button("管理快捷文本");
            manage.Click += delegate { OpenQuickText(); };
            Theme.Put(quickRow, manage, 1, 0, Padding.Empty);
            Theme.Put(root, quickRow, 0, 3, new Padding(0, 0, 0, 15));

            var modeRow = virtualKeyboard == null ? Theme.Table(0, -1) : Theme.Table(0, -1, -1);
            inputModeState = Theme.Label("输入方式：模拟输入", Theme.Body, Theme.Muted);
            inputModeState.Name = "InputModeState";
            Theme.Put(modeRow, inputModeState, 0, 0, new Padding(0, 0, 8, 0));
            var switchMode = Theme.Button("切换输入方式");
            switchMode.Name = "SwitchInputMode";
            switchMode.Click += async delegate
            {
                switchMode.Enabled = false;
                try { await SwitchInputMode(); }
                finally { if (!switchMode.IsDisposed) switchMode.Enabled = true; }
            };
            Theme.Put(modeRow, switchMode, 1, 0, Padding.Empty);
            if (virtualKeyboard != null)
            {
                installChinese = Theme.Button("安装中文输入");
                installChinese.Name = "InstallChineseInput";
                installChinese.Visible = false;
                installChinese.Click += async delegate
                {
                    if (!switchMode.Enabled) { SetMessage("请先完成虚拟键盘授权。"); return; }
                    installChinese.Enabled = false;
                    try { await SetupChineseInput(); }
                    finally { if (!installChinese.IsDisposed) installChinese.Enabled = true; }
                };
                Theme.Put(modeRow, installChinese, 2, 0, new Padding(8, 0, 0, 0));
            }
            Theme.Put(root, modeRow, 0, 4, new Padding(0, 0, 0, 15));

            Theme.Put(root, Theme.Label("全局快捷键", Theme.Section, Theme.Ink), 0, 5, new Padding(0, 0, 0, 8));
            var shortcuts = Theme.Table(126, 0, -1);
            Theme.Put(root, shortcuts, 0, 6, new Padding(0, 0, 0, 12));
            message = Theme.Label("点击快捷键输入框可录制新组合键。", Theme.Small, Theme.Muted);
            hotkeys = new Hotkeys(Trigger);
            AddShortcut(shortcuts, 0, PasteShortcut.Phone, "手机文字优先", Keys.Control | Keys.Shift | Keys.V);
            AddShortcut(shortcuts, 1, PasteShortcut.Clipboard, "电脑剪贴板", Keys.Control | Keys.Alt | Keys.V);
            AddShortcut(shortcuts, 2, PasteShortcut.Resume, "继续粘贴", Keys.Control | Keys.Alt | Keys.Shift | Keys.V);

            var pacing = Theme.Table(-1, 0, 82, -1);
            Theme.Put(root, pacing, 0, 7, new Padding(0, 1, 0, 8));
            Theme.Put(pacing, Theme.Label("字符间隔", Theme.Section, Theme.Ink), 0, 0, new Padding(0, 0, 12, 0));
            Theme.Put(pacing, Theme.Label("0 表示最快", Theme.Small, Theme.Muted), 1, 0, Padding.Empty);
            delay = new NumericUpDown { Name = "CharacterInterval", AccessibleName = "字符间隔（毫秒）",
                BorderStyle = BorderStyle.None, Maximum = 250, Increment = 10,
                TextAlign = HorizontalAlignment.Right, Value = ReadNumber("delay.txt", 0, value => value >= 0 && value <= 250) };
            Theme.Put(pacing, Theme.Field(delay, 6), 2, 0, Padding.Empty);
            Theme.Put(pacing, Theme.Label("毫秒", Theme.Small, Theme.Muted), 3, 0, new Padding(8, 0, 0, 0));
            delay.ValueChanged += delegate { SaveDelay(); };

            Theme.Put(root, message, 0, 8, new Padding(0, 6, 0, 5));
            Theme.Put(root, Theme.AuthorLink(SetMessage), 0, 9, new Padding(0, 10, 0, 0));
            input = new PacedInput(value =>
            {
                if (value.StartsWith("输入失败。", StringComparison.Ordinal)) ReportInputProblem(value);
                else SetMessage(value);
            }, WindowsKeyboard.GetForegroundWindow, WindowsKeyboard.Send);
            inputNoticeTimer.Tick += delegate
            {
                inputNoticeTimer.Stop();
                if (inputNotice != null) inputNotice.Visible = false;
            };
            RefreshQuickBindings();
            refresh.Tick += delegate { RefreshState(); };
            RefreshState();
            refresh.Start();
            ResumeLayout(true);
        }

        private void AddShortcut(TableLayoutPanel panel, int slot, string shortcut, string caption, Keys fallback)
        {
            string file = "hotkey-" + slot + ".txt";
            var current = (Keys)ReadNumber(file, (int)fallback, value => Hotkeys.Valid((Keys)value));
            string error = hotkeys.Set(shortcut, current);
            if (error != null) SetMessage(caption + "的快捷键被占用。请重新录制。");
            Theme.Put(panel, Theme.Label(caption, Theme.Body, Theme.Ink), 0, slot, new Padding(0, 0, 8, 6));
            var box = new ShortcutField(current, caption + "快捷键");
            Theme.Put(panel, Theme.Field(box), 1, slot, new Padding(0, 0, 10, 6));
            Keys candidate = current;
            box.Recorded += chord => candidate = chord;
            var apply = Theme.Button("保存");
            Theme.Put(panel, apply, 2, slot, new Padding(0, 0, 0, 6));
            apply.Click += delegate
            {
                if (quickText.Entries.Any(entry => entry.Chord == candidate))
                {
                    SetMessage("这组快捷键已分配给一条快捷文本。");
                    return;
                }
                SetMessage(hotkeys.Set(shortcut, candidate, Path.Combine(folder, file)) ?? caption + "的快捷键已保存。");
            };
            box.Enter += delegate { SetMessage("按下新组合键，再点保存。"); };
        }

        private int ReadNumber(string file, int fallback, Func<int, bool> valid)
        {
            try
            {
                int value;
                if (Int32.TryParse(File.ReadAllText(Path.Combine(folder, file)), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out value) && valid(value)) return value;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return fallback;
        }

        private void SaveDelay()
        {
            try { MobileInbox.SaveSetting(Path.Combine(folder, "delay.txt"), delay.Value.ToString(CultureInfo.InvariantCulture)); }
            catch (IOException) { SetMessage("字符间隔已生效，写入设置失败。"); }
            catch (UnauthorizedAccessException) { SetMessage("字符间隔已生效，写入设置失败。"); }
        }

        private async System.Threading.Tasks.Task SwitchInputMode()
        {
            if (rimeSetupRunning) { SetMessage("请等中文输入安装完成后再切换。"); return; }
            string error = await inputModes.Toggle(() => MessageBox.Show(this,
                "免费虚拟键盘需要管理员授权。首次启用时，助手会安装 HIDMaestro 驱动并信任它的本机签名证书。继续吗？",
                "KeyParcel · 虚拟键盘",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes);
            if (IsDisposed || Disposing) return;
            if (error != null) { SetMessage(error); return; }
            string mode = inputModes.Virtual ? "虚拟键盘" : "模拟输入";
            inputModeState.Text = "输入方式：" + mode;
            RefreshChineseSetupButton();
            bool discarded = input.Discard();
            SetMessage(discarded ? "已切换，上一轮输入已结束。" : inputModes.Virtual
                ? "虚拟键盘已就绪。含中文的文字需部署小狼毫扩展。" : "已切换到模拟输入。");
        }

        private async System.Threading.Tasks.Task SetupChineseInput()
        {
            string package = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Rime", RimeSetup.InstallerName);
            rimeSetupRunning = true;
            input.Discard();
            try
            {
                string deployer = RimeSetup.FindDeployer();
                if (deployer == null)
                {
                    if (!File.Exists(package))
                    {
                        SetMessage("请将官方小狼毫安装包放到程序旁的 Rime 文件夹后重试。");
                        return;
                    }
                    SetMessage("正在打开小狼毫安装程序。请在安装界面选「简体中文」。");
                    int exit = await System.Threading.Tasks.Task.Run(() => RimeSetup.Install(package));
                    if (IsDisposed || Disposing) return;
                    deployer = RimeSetup.FindDeployer();
                    if (exit != 0 || deployer == null)
                    {
                        SetMessage("小狼毫安装尚未完成。完成安装后可以再点一次。");
                        return;
                    }
                }
                SetMessage("正在导入中文扩展并重新部署小狼毫…");
                int schemes = await System.Threading.Tasks.Task.Run(() => RimeSetup.Import(deployer));
                if (!IsDisposed && !Disposing) SetMessage("中文扩展已导入 " + schemes + " 个方案。启用虚拟键盘即可试用。");
            }
            catch (Exception error)
            {
                if (!IsDisposed && !Disposing)
                    MessageBox.Show(this, error.Message, "KeyParcel · 中文输入安装未完成", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                rimeSetupRunning = false;
                if (!IsDisposed && !Disposing) RefreshChineseSetupButton();
            }
        }

        private void RefreshChineseSetupButton()
        {
            if (installChinese != null) installChinese.Visible = inputModes.Virtual && !RimeSetup.IsReady();
        }

        private void Trigger(string shortcut)
        {
            if (input == null || rimeSetupRunning) return;
            if (shortcut == PasteShortcut.Resume)
            {
                input.Resume();
                return;
            }
            if (input.HandleRunningPasteShortcut(shortcut)) return;
            var window = WindowsKeyboard.GetForegroundWindow();
            if (window == IntPtr.Zero || window == Handle ||
                OwnedForms.Any(form => form.IsHandleCreated && form.Handle == window))
            {
                if (inputModes.Virtual) SetMessage("请先把光标放到目标应用的输入框，再按快捷键。");
                return;
            }
            try
            {
                string text;
                if (shortcut == PasteShortcut.Phone || shortcut == PasteShortcut.Clipboard)
                    text = (shortcut == PasteShortcut.Phone ? inbox.Latest : null) ?? Clipboard.GetText();
                else
                {
                    var entry = quickText.Entries.FirstOrDefault(item => item.ShortcutId == shortcut);
                    if (!quickText.Enabled || entry == null) return;
                    text = entry.Text;
                }
                if (String.IsNullOrEmpty(text))
                {
                    if (inputModes.Virtual) ReportInputProblem("没有可输入的文字。请先复制或发送一段文字。");
                    return;
                }
                Action<string> send;
                RimeInput rime;
                string error = inputModes.Prepare(text, window, out send, out rime);
                if (error != null) { ReportInputProblem(error); return; }
                input.Begin(shortcut, text, Decimal.ToInt32(delay.Value), window, send, rime);
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                SetMessage("剪贴板暂时被占用，请再按一次。");
            }
        }

        private void OpenQuickText()
        {
            ClearQuickBindings();
            hotkeys.Suspend();
            try
            {
                using (var dialog = new QuickTextWindow(quickText, chord => hotkeys.CanRegister(chord)))
                    dialog.ShowDialog(this);
            }
            finally
            {
                int conflicts = hotkeys.Restore();
                RefreshQuickBindings();
                if (conflicts > 0) SetMessage("部分全局快捷键已被占用，请重新保存或修改。");
            }
        }

        private void ClearQuickBindings()
        {
            foreach (string shortcut in quickBindings) hotkeys.Remove(shortcut);
            quickBindings.Clear();
        }

        private void RefreshQuickBindings()
        {
            ClearQuickBindings();
            if (!quickText.Enabled)
            {
                quickTextState.Text = "快捷文本已关闭";
                return;
            }
            int conflicts = 0;
            foreach (var entry in quickText.Entries)
            {
                if (entry.Chord == Keys.None || String.IsNullOrEmpty(entry.Text)) continue;
                if (hotkeys.Set(entry.ShortcutId, entry.Chord) == null) quickBindings.Add(entry.ShortcutId);
                else conflicts++;
            }
            quickTextState.Text = "快捷文本已启用 · " + quickBindings.Count + " 条可用" +
                (conflicts == 0 ? "" : " · " + conflicts + " 组按键被占用");
        }

        private void SetMessage(string value) { message.Text = value; }

        private void ReportInputProblem(string value)
        {
            SetMessage(value);
            if (!inputModes.Virtual || ContainsFocus) return;
            if (inputNotice == null) inputNotice = new NotifyIcon { Icon = Icon, Text = "KeyParcel" };
            inputNotice.Visible = true;
            inputNotice.ShowBalloonTip(5000, "KeyParcel · 虚拟键盘", value, ToolTipIcon.Warning);
            inputNoticeTimer.Stop();
            inputNoticeTimer.Start();
        }

        private void StartReceiver(IPAddress ip)
        {
            receiver = new MobileServer(ip, MobileServer.PreferredPort, inbox);
            lastAddress = ip;
            if (!inbox.Paired || receiver.ListeningPort != MobileServer.PreferredPort) inbox.NewCode();
        }

        private void StopReceiver()
        {
            if (receiver == null) return;
            receiver.Dispose();
            receiver = null;
        }

        private void RefreshState()
        {
            string received = inbox.Latest == null ? "" : " / 手机文字可用";
            state.Text = (receiver == null ? "接收已关闭" : inbox.Paired ? "接收中 / 手机已配对" : "接收中 / 等待配对") + received;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                refresh.Dispose();
                inputNoticeTimer.Dispose();
                if (inputNotice != null) inputNotice.Dispose();
                StopReceiver();
                if (hotkeys != null) hotkeys.Dispose();
                if (input != null) input.Dispose();
                if (inputModes != null) inputModes.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    internal sealed class PairingWindow : Form
    {
        private readonly MobileInbox inbox;
        private readonly Func<MobileServer> current;
        private readonly Action<IPAddress> start;
        private readonly Action stop;
        private readonly ComboBox adapters = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "局域网地址" };
        private readonly PictureBox image = new PictureBox { SizeMode = PictureBoxSizeMode.Zoom, BackColor = Theme.Paper };
        private readonly TextBox address = new TextBox { ReadOnly = true, Font = Theme.Chord, AccessibleName = "手机访问地址" };
        private readonly Timer refresh = new Timer { Interval = 400 };
        private readonly Button toggle, regenerate, forget, copy;
        private readonly Label state;
        private string lastUrl, error;

        internal PairingWindow(MobileInbox inbox, Func<MobileServer> current, Action<IPAddress> start, Action stop, IPAddress selected)
        {
            this.inbox = inbox;
            this.current = current;
            this.start = start;
            this.stop = stop;
            Theme.Window(this, "KeyParcel · 连接手机", 548, 414);
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            var root = Theme.Table(0);
            root.Padding = new Padding(24, 18, 24, 14);
            Controls.Add(root);
            Theme.Put(root, Theme.Label("连接手机", Theme.Section, Theme.Ink), 0, 0, Padding.Empty);
            Theme.Put(root, Theme.Label("手机和电脑需接入同一个可信网络。", Theme.Body, Theme.Muted),
                0, 1, new Padding(0, 6, 0, 16));
            var network = Theme.Table(0, -1);
            adapters.FlatStyle = FlatStyle.Flat;
            Theme.Put(network, Theme.Field(adapters, 6), 0, 0, new Padding(0, 0, 10, 0));
            toggle = Theme.Button("开启接收", true);
            Theme.Put(network, toggle, 1, 0, Padding.Empty);
            Theme.Put(root, network, 0, 2, new Padding(0, 0, 0, 14));
            toggle.Click += delegate { Toggle(); };

            var ticket = Theme.Card(-1, 0);
            ticket.Padding = new Padding(12);
            image.Size = new Size(176, 176);
            image.Anchor = AnchorStyles.None;
            image.Paint += delegate(object sender, PaintEventArgs args)
            {
                if (image.Image == null)
                    TextRenderer.DrawText(args.Graphics, "开启接收后扫码", Theme.Small, image.ClientRectangle, Theme.Muted,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            };
            Theme.Put(ticket, image, 0, 0, new Padding(0, 0, 14, 0));
            var details = Theme.Table(0);
            Theme.Put(ticket, details, 1, 0, new Padding(0, 6, 0, 0));
            state = Theme.Label("", Theme.Body, Theme.Ink);
            Theme.Put(details, state, 0, 0, new Padding(0, 0, 0, 10));
            var link = Theme.Table(0, -1);
            address.Font = new Font("Consolas", 8.5f);
            address.BorderStyle = BorderStyle.None;
            Theme.Put(link, Theme.Field(address), 0, 0, new Padding(0, 0, 6, 0));
            copy = Theme.Button("复制");
            Theme.Put(link, copy, 1, 0, Padding.Empty);
            Theme.Put(details, link, 0, 1, new Padding(0, 0, 0, 10));
            copy.Click += delegate
            {
                if (lastUrl == null) return;
                try { Clipboard.SetText(lastUrl); }
                catch (System.Runtime.InteropServices.ExternalException)
                {
                    error = "剪贴板暂时被占用，请重试。";
                    UpdateState();
                }
            };
            regenerate = Theme.Button("生成新配对码");
            Theme.Put(details, regenerate, 0, 2, new Padding(0, 0, 0, 7));
            regenerate.Click += delegate { error = null; inbox.NewCode(); UpdateState(); };
            forget = Theme.Button("解除配对");
            Theme.Put(details, forget, 0, 3, Padding.Empty);
            forget.Click += delegate
            {
                error = null;
                try { inbox.Forget(); }
                catch (IOException) { error = "无法移除已保存的配对。"; }
                catch (UnauthorizedAccessException) { error = "无法移除已保存的配对。"; }
                UpdateState();
            };
            Theme.Put(root, ticket, 0, 3, Padding.Empty);
            var footer = Theme.Table(0, -1);
            Theme.Put(footer, Theme.AuthorLink(value => { error = value; UpdateState(); }), 0, 0, Padding.Empty);
            var done = Theme.Button("完成");
            done.Anchor = AnchorStyles.Right;
            Theme.Put(footer, done, 1, 0, Padding.Empty);
            Theme.Put(root, footer, 0, 4, new Padding(0, 13, 0, 0));
            done.DialogResult = DialogResult.OK;
            CancelButton = done;
            LoadAddresses(selected);
            UpdateState();
            refresh.Tick += delegate { UpdateState(); };
            refresh.Start();
            ResumeLayout(true);
        }

        private void LoadAddresses(IPAddress selected)
        {
            try
            {
                var choices = from nic in NetworkInterface.GetAllNetworkInterfaces()
                              where nic.OperationalStatus == OperationalStatus.Up && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback
                              let properties = nic.GetIPProperties()
                              from ip in properties.UnicastAddresses
                              where ip.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip.Address)
                              orderby IsPrivateLan(ip.Address) descending,
                                  properties.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork &&
                                      !g.Address.Equals(IPAddress.Any)) descending
                              select new AddressChoice { Ip = ip.Address, Name = nic.Name };
                foreach (var choice in choices)
                {
                    int index = adapters.Items.Add(choice);
                    if (choice.Ip.Equals(selected)) adapters.SelectedIndex = index;
                }
                if (adapters.SelectedIndex < 0 && adapters.Items.Count != 0) adapters.SelectedIndex = 0;
            }
            catch (NetworkInformationException) { }
        }

        private static bool IsPrivateLan(IPAddress address)
        {
            var octets = address.GetAddressBytes();
            return octets[0] == 10 || (octets[0] == 172 && octets[1] >= 16 && octets[1] <= 31) ||
                (octets[0] == 192 && octets[1] == 168);
        }

        private void Toggle()
        {
            error = null;
            if (current() != null) stop();
            else
            {
                var choice = adapters.SelectedItem as AddressChoice;
                if (choice == null) return;
                try { start(choice.Ip); }
                catch (SocketException) { error = "无法开启接收。请检查所选网卡的地址是否可用。"; }
            }
            UpdateState();
        }

        private void UpdateState()
        {
            var server = current();
            var code = inbox.PairCode;
            toggle.Text = server == null ? "开启接收" : "停止接收";
            toggle.Enabled = server != null || adapters.Items.Count > 0;
            adapters.Enabled = server == null;
            regenerate.Enabled = server != null;
            copy.Enabled = server != null;
            forget.Enabled = inbox.Paired;
            state.Text = error ?? (server == null ? (adapters.Items.Count == 0 ? "没有可用的局域网地址。" : "接收已关闭。") :
                code != null ? "扫码配对。有效期 5 分钟。" :
                inbox.Paired ? "已配对。扫码打开发送页。" : "请生成新配对码。");
            string url = server == null ? null : server.Address + "/" + (code == null ? "" : "#" + code);
            if (url == lastUrl) return;
            lastUrl = url;
            address.Text = server == null ? "" : server.Address;
            var old = image.Image;
            image.Image = url == null ? null : MobileQr.Draw(url);
            if (old != null) old.Dispose();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                refresh.Dispose();
                if (image.Image != null) image.Image.Dispose();
            }
            base.Dispose(disposing);
        }

        private sealed class AddressChoice
        {
            internal IPAddress Ip;
            internal string Name;
            public override string ToString() { return Name + " / " + Ip; }
        }
    }
}
