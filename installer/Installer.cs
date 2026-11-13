using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;

namespace ImageBatchInstaller
{
    class InstallerForm : Form
    {
        Panel titleBar;
        Panel headerPanel;
        Label titleLabel;
        Label subtitleLabel;
        Panel bodyPanel;
        Panel bottomPanel;
        RoundedButton backBtn, nextBtn, cancelBtn;
        TextBox pathBox;
        ProgressBar progress;
        Label statusLabel;
        CheckBox desktopShortcut;
        int step = 0;
        string installPath = @"D:\Program Files\图片批量处理工具";

        static readonly Color BgColor = Color.FromArgb(32, 32, 36);
        static readonly Color PanelColor = Color.FromArgb(45, 45, 50);
        static readonly Color TitleBarColor = Color.FromArgb(38, 38, 42);
        static readonly Color AccentColor = Color.FromArgb(124, 58, 237);
        static readonly Color TextColor = Color.FromArgb(225, 225, 230);
        static readonly Color SubTextColor = Color.FromArgb(150, 150, 158);
        static readonly Color BtnBg = Color.FromArgb(60, 60, 68);
        static readonly Color BtnBorder = Color.FromArgb(85, 85, 95);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern bool SHGetFolderPath(IntPtr hwndOwner, int nFolder, IntPtr hToken, uint dwFlags, StringBuilder pszPath);

        public const int WM_NCLBUTTONDOWN = 0xA1;
        public const int HT_CAPTION = 0x2;
        [DllImport("user32.dll")]
        public static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);
        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();

        const int TitleBarH = 36;
        const int HeaderH = 80;
        const int BottomH = 60;
        const int WindowRadius = 12;

        public InstallerForm()
        {
            Width = 620; Height = 480;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = BgColor;
            ForeColor = TextColor;
            Text = "图片批处理工具 v1.04";
            DoubleBuffered = true;

            // 窗口圆角
            ApplyWindowRoundedRegion();

            titleBar = new Panel();
            titleBar.Height = TitleBarH;
            titleBar.Top = 0;
            titleBar.Left = 0;
            titleBar.Width = this.ClientSize.Width;
            titleBar.BackColor = TitleBarColor;
            Controls.Add(titleBar);

            headerPanel = new Panel();
            headerPanel.Height = HeaderH;
            headerPanel.Top = TitleBarH;
            headerPanel.Left = 0;
            headerPanel.Width = this.ClientSize.Width;
            headerPanel.BackColor = PanelColor;
            Controls.Add(headerPanel);

            bodyPanel = new Panel();
            bodyPanel.Top = TitleBarH + HeaderH;
            bodyPanel.Left = 0;
            bodyPanel.Width = this.ClientSize.Width;
            bodyPanel.Height = this.ClientSize.Height - TitleBarH - HeaderH - BottomH;
            bodyPanel.BackColor = BgColor;
            Controls.Add(bodyPanel);

            bottomPanel = new Panel();
            bottomPanel.Height = BottomH;
            bottomPanel.Top = this.ClientSize.Height - BottomH;
            bottomPanel.Left = 0;
            bottomPanel.Width = this.ClientSize.Width;
            bottomPanel.BackColor = PanelColor;
            Controls.Add(bottomPanel);

            // 只保留红色关闭按钮
            AddCloseCircle(16, 12);

            var titleText = new Label();
            titleText.Text = "图片批处理工具 v1.04 安装向导";
            titleText.ForeColor = SubTextColor;
            titleText.Font = new Font("微软雅黑", 9f);
            titleText.AutoSize = false;
            titleText.SetBounds(0, 9, this.ClientSize.Width, 20);
            titleText.TextAlign = ContentAlignment.MiddleCenter;
            titleBar.Controls.Add(titleText);

            titleBar.MouseDown += DragMouseDown;
            titleText.MouseDown += DragMouseDown;
            headerPanel.MouseDown += DragMouseDown;

            titleLabel = new Label();
            titleLabel.Text = "图片批处理工具";
            titleLabel.ForeColor = Color.White;
            titleLabel.Font = new Font("微软雅黑", 17f, FontStyle.Bold);
            titleLabel.AutoSize = false;
            titleLabel.SetBounds(28, 16, 560, 32);
            headerPanel.Controls.Add(titleLabel);

            subtitleLabel = new Label();
            subtitleLabel.ForeColor = SubTextColor;
            subtitleLabel.Font = new Font("微软雅黑", 9.5f);
            subtitleLabel.AutoSize = false;
            subtitleLabel.SetBounds(28, 50, 560, 22);
            headerPanel.Controls.Add(subtitleLabel);

            titleLabel.MouseDown += DragMouseDown;
            subtitleLabel.MouseDown += DragMouseDown;

            cancelBtn = new RoundedButton("取消", 8);
            cancelBtn.Location = new Point(495, 15);
            cancelBtn.Size = new Size(88, 32);
            cancelBtn.BackColor = BtnBg;
            cancelBtn.ForeColor = TextColor;
            cancelBtn.Click += delegate { Close(); };
            bottomPanel.Controls.Add(cancelBtn);

            nextBtn = new RoundedButton("下一步 >", 8);
            nextBtn.Location = new Point(395, 15);
            nextBtn.Size = new Size(88, 32);
            nextBtn.BackColor = AccentColor;
            nextBtn.ForeColor = Color.White;
            nextBtn.Font = new Font("微软雅黑", 9.5f, FontStyle.Bold);
            nextBtn.Click += OnNext;
            bottomPanel.Controls.Add(nextBtn);

            backBtn = new RoundedButton("< 上一步", 8);
            backBtn.Location = new Point(300, 15);
            backBtn.Size = new Size(88, 32);
            backBtn.BackColor = BtnBg;
            backBtn.ForeColor = SubTextColor;
            backBtn.Enabled = false;
            backBtn.Click += delegate { if (step > 0) { step--; ShowStep(); } };
            bottomPanel.Controls.Add(backBtn);

            var authorLabel = new Label();
            authorLabel.Text = "by Doubao";
            authorLabel.ForeColor = SubTextColor;
            authorLabel.Font = new Font("微软雅黑", 8.5f);
            authorLabel.AutoSize = false;
            authorLabel.SetBounds(22, 20, 120, 22);
            bottomPanel.Controls.Add(authorLabel);

            this.Resize += delegate {
                ApplyWindowRoundedRegion();
                titleBar.Width = this.ClientSize.Width;
                headerPanel.Width = this.ClientSize.Width;
                bodyPanel.Width = this.ClientSize.Width;
                bodyPanel.Top = TitleBarH + HeaderH;
                bodyPanel.Height = this.ClientSize.Height - TitleBarH - HeaderH - BottomH;
                bottomPanel.Top = this.ClientSize.Height - BottomH;
                bottomPanel.Width = this.ClientSize.Width;
                titleText.Width = this.ClientSize.Width;
            };

            ShowStep();
        }

        void DragMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                ReleaseCapture();
                SendMessage(this.Handle, WM_NCLBUTTONDOWN, HT_CAPTION, 0);
            }
        }

        void ApplyWindowRoundedRegion()
        {
            var path = new GraphicsPath();
            int r = WindowRadius;
            path.AddArc(0, 0, r*2, r*2, 180, 90);
            path.AddArc(this.ClientSize.Width - r*2, 0, r*2, r*2, 270, 90);
            path.AddArc(this.ClientSize.Width - r*2, this.ClientSize.Height - r*2, r*2, r*2, 0, 90);
            path.AddArc(0, this.ClientSize.Height - r*2, r*2, r*2, 90, 90);
            path.CloseFigure();
            this.Region = new Region(path);
        }

        void AddCloseCircle(int left, int top)
        {
            var btn = new RoundedButton("", 6);
            btn.Size = new Size(12, 12);
            btn.Left = left; btn.Top = top;
            btn.BackColor = Color.FromArgb(255, 69, 58);
            btn.Cursor = Cursors.Hand;
            btn.FlatAppearance.BorderSize = 0;
            btn.Click += delegate { Close(); };
            titleBar.Controls.Add(btn);
        }

        void ShowStep()
        {
            bodyPanel.Controls.Clear();
            if (step == 0)
            {
                subtitleLabel.Text = "欢迎使用图片批处理工具安装向导";
                var welcome = new Label();
                welcome.Text = "  本向导将引导您完成图片批处理工具的安装。\n\n" +
                    "  功能包括：\n" +
                    "    • 批量套用 LUT 滤镜（自动 / AI / 直出模式）\n" +
                    "    • 色彩调整（自动优化、AI 优化）\n" +
                    "    • AI 人像增强（GFPGAN）\n\n" +
                    "  点击\"下一步\"继续，或\"取消\"退出安装。";
                welcome.Font = new Font("微软雅黑", 10.5f);
                welcome.ForeColor = TextColor;
                welcome.BackColor = BgColor;
                welcome.SetBounds(0, 20, bodyPanel.Width, 280);
                bodyPanel.Controls.Add(welcome);
                nextBtn.Text = "下一步 >";
                nextBtn.ForeColor = Color.White;
                backBtn.Enabled = false;
                backBtn.ForeColor = SubTextColor;
            }
            else if (step == 1)
            {
                subtitleLabel.Text = "选择安装位置";
                var pathLabel = new Label();
                pathLabel.Text = "安装到：";
                pathLabel.Font = new Font("微软雅黑", 9.5f);
                pathLabel.ForeColor = TextColor;
                pathLabel.SetBounds(28, 25, 100, 22);
                bodyPanel.Controls.Add(pathLabel);

                pathBox = new TextBox();
                pathBox.Text = installPath;
                pathBox.Font = new Font("微软雅黑", 9.5f);
                pathBox.BackColor = PanelColor;
                pathBox.ForeColor = TextColor;
                pathBox.BorderStyle = BorderStyle.FixedSingle;
                pathBox.SetBounds(28, 52, 440, 26);
                bodyPanel.Controls.Add(pathBox);

                var browseBtn = new RoundedButton("浏览...", 6);
                browseBtn.Location = new Point(482, 51);
                browseBtn.Size = new Size(80, 27);
                browseBtn.BackColor = BtnBg;
                browseBtn.ForeColor = TextColor;
                browseBtn.Click += delegate {
                    var dlg = new FolderBrowserDialog();
                    dlg.Description = "选择安装文件夹";
                    dlg.SelectedPath = installPath;
                    if (dlg.ShowDialog() == DialogResult.OK) pathBox.Text = dlg.SelectedPath;
                };
                bodyPanel.Controls.Add(browseBtn);

                desktopShortcut = new CheckBox();
                desktopShortcut.Text = "在桌面创建快捷方式";
                desktopShortcut.Checked = true;
                desktopShortcut.Font = new Font("微软雅黑", 9.5f);
                desktopShortcut.ForeColor = TextColor;
                desktopShortcut.BackColor = BgColor;
                desktopShortcut.SetBounds(28, 95, 300, 26);
                bodyPanel.Controls.Add(desktopShortcut);

                var note = new Label();
                note.Text = "提示：默认安装到 D 盘，无需额外运行环境，开箱即用。";
                note.ForeColor = SubTextColor;
                note.Font = new Font("微软雅黑", 8.5f);
                note.SetBounds(28, 135, 500, 20);
                bodyPanel.Controls.Add(note);

                nextBtn.Text = "安装";
                backBtn.Enabled = true;
                backBtn.ForeColor = TextColor;
            }
            else if (step == 2)
            {
                subtitleLabel.Text = "正在安装";
                statusLabel = new Label();
                statusLabel.Text = "准备中...";
                statusLabel.Font = new Font("微软雅黑", 9.5f);
                statusLabel.ForeColor = TextColor;
                statusLabel.SetBounds(28, 30, 540, 25);
                bodyPanel.Controls.Add(statusLabel);

                progress = new ProgressBar();
                progress.Minimum = 0; progress.Maximum = 100;
                progress.Style = ProgressBarStyle.Continuous;
                progress.SetBounds(28, 65, 540, 24);
                bodyPanel.Controls.Add(progress);

                nextBtn.Enabled = false;
                backBtn.Enabled = false;
                cancelBtn.Enabled = false;

                installPath = pathBox.Text.Trim();
                var bg = new System.ComponentModel.BackgroundWorker();
                bg.DoWork += delegate { DoInstall(); };
                bg.RunWorkerCompleted += delegate {
                    step = 3;
                    nextBtn.Enabled = true;
                    nextBtn.BackColor = AccentColor;
                    nextBtn.ForeColor = Color.White;
                    backBtn.Enabled = false;
                    backBtn.ForeColor = SubTextColor;
                    cancelBtn.Enabled = false;
                    nextBtn.Text = "完成";
                    ShowStep();
                };
                bg.RunWorkerAsync();
            }
            else if (step == 3)
            {
                subtitleLabel.Text = "安装完成";
                var done = new Label();
                done.Text = "  图片批处理工具已成功安装到：\n\n  " + installPath + "\n\n  点击\"完成\"退出安装向导。";
                done.Font = new Font("微软雅黑", 10.5f);
                done.ForeColor = TextColor;
                done.SetBounds(0, 30, bodyPanel.Width, 220);
                bodyPanel.Controls.Add(done);

                var launch = new CheckBox();
                launch.Text = "立即启动图片批处理工具";
                launch.Checked = true;
                launch.Font = new Font("微软雅黑", 9.5f);
                launch.ForeColor = TextColor;
                launch.BackColor = BgColor;
                launch.SetBounds(28, 180, 300, 26);
                launch.Name = "launch";
                bodyPanel.Controls.Add(launch);

                nextBtn.Click -= OnNext;
                nextBtn.Click += delegate {
                    if (launch.Checked) {
                        try { Process.Start(Path.Combine(installPath, "图片批处理工具.exe")); } catch {}
                    }
                    Close();
                };
            }
        }

        void OnNext(object sender, EventArgs e)
        {
            if (step < 2) { step++; ShowStep(); }
        }

        void DoInstall()
        {
            try
            {
                UpdateStatus("创建目录...");
                if (!Directory.Exists(installPath))
                    Directory.CreateDirectory(installPath);

                string myExe = Process.GetCurrentProcess().MainModule.FileName;
                byte[] self = File.ReadAllBytes(myExe);

                long footerPos = self.Length - (6 + 8*3);
                string marker = Encoding.ASCII.GetString(self, (int)footerPos, 6);
                if (marker != "IBPACK") throw new Exception("安装包数据损坏");

                long p7zExe = BitConverter.ToInt64(self, (int)footerPos + 6);
                long p7zDll = BitConverter.ToInt64(self, (int)footerPos + 14);
                long p7zArch = BitConverter.ToInt64(self, (int)footerPos + 22);

                string tempDir = Path.Combine(Path.GetTempPath(), "imgbatch_setup");
                Directory.CreateDirectory(tempDir);
                string sevenZipExe = Path.Combine(tempDir, "7z.exe");
                string sevenZipDll = Path.Combine(tempDir, "7z.dll");
                string archivePath = Path.Combine(tempDir, "app.7z");

                UpdateStatus("释放解压工具...");
                SetProgress(10);
                File.WriteAllBytes(sevenZipExe, ReadRange(self, p7zExe, p7zDll - p7zExe));
                File.WriteAllBytes(sevenZipDll, ReadRange(self, p7zDll, p7zArch - p7zDll));
                File.WriteAllBytes(archivePath, ReadRange(self, p7zArch, footerPos - p7zArch));

                UpdateStatus("正在解压文件（约450MB，请稍候）...");
                SetProgress(20);
                var psi = new ProcessStartInfo();
                psi.FileName = sevenZipExe;
                psi.Arguments = "x \"" + archivePath + "\" -o\"" + installPath + "\" -y -bso0 -bsp0";
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WorkingDirectory = tempDir;
                var p = Process.Start(psi);

                // 解压期间模拟进度
                var timer = new System.Windows.Forms.Timer();
                int fakeProgress = 20;
                timer.Interval = 500;
                timer.Tick += delegate {
                    if (fakeProgress < 85) {
                        fakeProgress += 2;
                        SetProgress(fakeProgress);
                    }
                };
                timer.Start();
                p.WaitForExit(300000);
                timer.Stop();

                if (p.ExitCode != 0)
                {
                    throw new Exception("解压失败，代码 " + p.ExitCode);
                }

                SetProgress(90);

                // 验证主程序存在
                string mainExe = Path.Combine(installPath, "图片批处理工具.exe");
                if (!File.Exists(mainExe))
                {
                    // 尝试找exe
                    var exeFiles = Directory.GetFiles(installPath, "*.exe", SearchOption.TopDirectoryOnly);
                    if (exeFiles.Length > 0) mainExe = exeFiles[0];
                    else throw new Exception("解压后未找到主程序EXE");
                }

                UpdateStatus("创建快捷方式...");
                SetProgress(95);
                if (desktopShortcut.Checked)
                {
                    string lnkPath = Path.Combine(GetDesktop(), "图片批处理工具.lnk");
                    CreateShortcut(mainExe, lnkPath);
                    if (!File.Exists(lnkPath))
                    {
                        // 兜底：直接写bat
                        string batPath = Path.Combine(GetDesktop(), "图片批处理工具.bat");
                        File.WriteAllText(batPath, "@echo off\r\ncd /d \"" + installPath + "\"\r\nstart \"\" \"" + mainExe + "\"\r\n");
                    }
                }

                UpdateStatus("完成");
                SetProgress(100);
            }
            catch (Exception ex)
            {
                MessageBox.Show("安装失败: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        byte[] ReadRange(byte[] data, long start, long len)
        {
            byte[] r = new byte[len];
            Array.Copy(data, start, r, 0, (int)len);
            return r;
        }

        void UpdateStatus(string s)
        {
            if (statusLabel != null && statusLabel.IsHandleCreated)
                statusLabel.Invoke(new Action(delegate { statusLabel.Text = s; }));
        }

        void SetProgress(int val)
        {
            if (progress != null && progress.IsHandleCreated)
                progress.Invoke(new Action(delegate { progress.Value = val; }));
        }

        string GetDesktop()
        {
            StringBuilder sb = new StringBuilder(260);
            SHGetFolderPath(IntPtr.Zero, 0, IntPtr.Zero, 0, sb);
            return sb.ToString();
        }

        void CreateShortcut(string target, string lnkPath)
        {
            try
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                object shell = Activator.CreateInstance(shellType);
                object shortcut = shellType.InvokeMember("CreateShortcut",
                    System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
                shortcut.GetType().InvokeMember("TargetPath",
                    System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { target });
                shortcut.GetType().InvokeMember("WorkingDirectory",
                    System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { Path.GetDirectoryName(target) });
                shortcut.GetType().InvokeMember("IconLocation",
                    System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { target + ",0" });
                shortcut.GetType().InvokeMember("Description",
                    System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { "图片批处理工具" });
                shortcut.GetType().InvokeMember("Save",
                    System.Reflection.BindingFlags.InvokeMethod, null, shortcut, null);
            } catch (Exception ex) {
                System.IO.File.AppendAllText(Path.Combine(Path.GetTempPath(), "imgbatch_setup_log.txt"),
                    "Shortcut error: " + ex.Message + "\r\n");
            }
        }

        [STAThread]
        static void Main()
        {
            try
            {
                Application.EnableVisualStyles();
                Application.Run(new InstallerForm());
            }
            catch (Exception ex)
            {
                MessageBox.Show("启动错误: " + ex.ToString(), "安装程序错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    // 圆角按钮，抗锯齿
    class RoundedButton : Button
    {
        int radius;
        public RoundedButton(string text, int r)
        {
            Text = text;
            radius = r;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            BackColor = Color.FromArgb(60, 60, 68);
            ForeColor = Color.FromArgb(225, 225, 230);
            Font = new Font("微软雅黑", 9.5f);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.Clear(Parent != null ? Parent.BackColor : Color.FromArgb(32, 32, 36));

            var rect = new Rectangle(0, 0, this.Width - 1, this.Height - 1);
            using (var path = new GraphicsPath())
            {
                int r = radius;
                path.AddArc(rect.X, rect.Y, r*2, r*2, 180, 90);
                path.AddArc(rect.Right - r*2, rect.Y, r*2, r*2, 270, 90);
                path.AddArc(rect.Right - r*2, rect.Bottom - r*2, r*2, r*2, 0, 90);
                path.AddArc(rect.X, rect.Bottom - r*2, r*2, r*2, 90, 90);
                path.CloseFigure();

                using (var brush = new SolidBrush(this.Enabled ? this.BackColor : Color.FromArgb(50, 50, 56)))
                {
                    e.Graphics.FillPath(brush, path);
                }

                // 文字
                var sf = new StringFormat();
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                var textRect = new RectangleF(0, 0, this.Width, this.Height);
                var textColor = this.Enabled ? this.ForeColor : Color.FromArgb(120, 120, 128);
                using (var brush = new SolidBrush(textColor))
                {
                    e.Graphics.DrawString(this.Text, this.Font, brush, textRect, sf);
                }
            }
        }
    }
}
