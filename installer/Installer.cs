using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32;

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
        CheckBox albumChk, editChk, desktopShortcut;
        int step = 0;
        string installPath = @"D:\Program Files\图片批量处理工具";
        internal bool silentMode = false;
        internal string silentInstallPath = null;
        internal bool silentAlbum = false;
        internal bool silentEdit = false;

        bool pendingAlbum = true, pendingEdit = true, pendingShortcut = true;
        internal bool installOk = true;

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
            Width = 620; Height = 500;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = BgColor;
            ForeColor = TextColor;
            Text = "图片批处理工具 v2.58";
            DoubleBuffered = true;

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

            AddCloseCircle(16, 12);

            var titleText = new Label();
            titleText.Text = "图片批处理工具 v2.58 安装向导";
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
            backBtn.Click += delegate { if (step > 0 && step < 2) { step--; ShowStep(); } };
            bottomPanel.Controls.Add(backBtn);

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
                welcome.Text = "  相册与图片编辑已经完全拆分为两个独立程序，可以分开安装。\n\n" +
                    "  相册：浏览、搜索、去重、人物分组、回收站\n" +
                    "  图片编辑：LUT、调色、美颜、证件照、批量导出\n\n" +
                    "  两个模块各自拥有独立目录、程序入口和配置，只装其中一个也能正常运行。\n\n" +
                    "  点击\"下一步\"继续，或\"取消\"退出安装。";
                welcome.Font = new Font("微软雅黑", 10.2f);
                welcome.ForeColor = TextColor;
                welcome.BackColor = BgColor;
                welcome.SetBounds(0, 18, bodyPanel.Width, 300);
                bodyPanel.Controls.Add(welcome);
                nextBtn.Text = "下一步 >";
                nextBtn.ForeColor = Color.White;
                backBtn.Enabled = false;
                backBtn.ForeColor = SubTextColor;
            }
            else if (step == 1)
            {
                subtitleLabel.Text = "选择安装位置和组件";
                var pathLabel = new Label();
                pathLabel.Text = "安装到：";
                pathLabel.Font = new Font("微软雅黑", 9.5f);
                pathLabel.ForeColor = TextColor;
                pathLabel.SetBounds(28, 16, 100, 22);
                bodyPanel.Controls.Add(pathLabel);

                pathBox = new TextBox();
                pathBox.Text = installPath;
                pathBox.Font = new Font("微软雅黑", 9.5f);
                pathBox.BackColor = PanelColor;
                pathBox.ForeColor = TextColor;
                pathBox.BorderStyle = BorderStyle.FixedSingle;
                pathBox.SetBounds(28, 42, 440, 26);
                bodyPanel.Controls.Add(pathBox);

                var browseBtn = new RoundedButton("浏览...", 6);
                browseBtn.Location = new Point(482, 41);
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

                var compLabel = new Label();
                compLabel.Text = "安装组件（至少选择一个，可只安装其中一个）：";
                compLabel.Font = new Font("微软雅黑", 9.2f);
                compLabel.ForeColor = TextColor;
                compLabel.SetBounds(28, 82, 520, 22);
                bodyPanel.Controls.Add(compLabel);

                albumChk = new CheckBox();
                albumChk.Text = "相册（浏览 / 搜索 / 去重 / 人物分组 / 回收站）";
                albumChk.Checked = true;
                albumChk.Font = new Font("微软雅黑", 9.2f);
                albumChk.ForeColor = TextColor;
                albumChk.BackColor = BgColor;
                albumChk.SetBounds(44, 108, 500, 24);
                bodyPanel.Controls.Add(albumChk);

                editChk = new CheckBox();
                editChk.Text = "图片编辑（LUT / 调色 / 美颜 / 证件照 / 批量导出）";
                editChk.Checked = true;
                editChk.Font = new Font("微软雅黑", 9.2f);
                editChk.ForeColor = TextColor;
                editChk.BackColor = BgColor;
                editChk.SetBounds(44, 136, 500, 24);
                bodyPanel.Controls.Add(editChk);

                desktopShortcut = new CheckBox();
                desktopShortcut.Text = "在桌面创建已选组件的快捷方式";
                desktopShortcut.Checked = true;
                desktopShortcut.Font = new Font("微软雅黑", 9.2f);
                desktopShortcut.ForeColor = TextColor;
                desktopShortcut.BackColor = BgColor;
                desktopShortcut.SetBounds(28, 176, 500, 24);
                bodyPanel.Controls.Add(desktopShortcut);

                var note = new Label();
                note.Text = "提示：两个组件分别安装到「相册」和「图片编辑」子文件夹，互不依赖。\n" +
                    "      默认安装到 D 盘，无需额外运行环境，开箱即用。";
                note.ForeColor = SubTextColor;
                note.Font = new Font("微软雅黑", 8.5f);
                note.SetBounds(28, 212, 520, 40);
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
                pendingAlbum = albumChk.Checked;
                pendingEdit = editChk.Checked;
                pendingShortcut = desktopShortcut.Checked;

                var bg = new System.ComponentModel.BackgroundWorker();
                bg.DoWork += delegate { DoInstall(); };
                bg.RunWorkerCompleted += delegate {
                    if (!installOk)
                    {
                        // 安装失败：回到组件选择页，不能显示“安装完成”。
                        step = 1;
                        nextBtn.Enabled = true;
                        nextBtn.BackColor = AccentColor;
                        nextBtn.ForeColor = Color.White;
                        backBtn.Enabled = true;
                        backBtn.ForeColor = TextColor;
                        cancelBtn.Enabled = true;
                        nextBtn.Text = "安装";
                        ShowStep();
                        return;
                    }
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
                string names = "";
                if (pendingAlbum) names += "相册";
                if (pendingEdit) names += (names.Length > 0 ? "、" : "") + "图片编辑";
                var done = new Label();
                done.Text = "  「" + names + "」已成功安装到：\n\n  " + installPath + "\n\n  各模块位于独立子文件夹，可分别启动、互不依赖；\n  未勾选的组件不会被安装。\n\n  点击\"完成\"退出安装向导。";
                done.Font = new Font("微软雅黑", 10.5f);
                done.ForeColor = TextColor;
                done.SetBounds(0, 26, bodyPanel.Width, 240);
                bodyPanel.Controls.Add(done);

                var launch = new CheckBox();
                launch.Text = "立即启动已安装的程序";
                launch.Checked = true;
                launch.Font = new Font("微软雅黑", 9.5f);
                launch.ForeColor = TextColor;
                launch.BackColor = BgColor;
                launch.SetBounds(28, 185, 300, 26);
                launch.Name = "launch";
                bodyPanel.Controls.Add(launch);

                nextBtn.Click -= OnNext;
                nextBtn.Click += delegate {
                    if (launch.Checked) {
                        try { if (pendingAlbum) Process.Start(Path.Combine(installPath, "相册", "相册.exe")); } catch {}
                        try { if (pendingEdit) Process.Start(Path.Combine(installPath, "图片编辑", "图片编辑.exe")); } catch {}
                    }
                    Close();
                };
            }
        }

        void OnNext(object sender, EventArgs e)
        {
            if (step == 1)
            {
                if (!albumChk.Checked && !editChk.Checked)
                {
                    MessageBox.Show("请至少勾选一个组件：相册 或 图片编辑。", "选择组件", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }
            if (step < 2) { step++; ShowStep(); }
        }

        internal void DoInstall()
        {
            try
            {
                string targetPath = (silentMode && !string.IsNullOrEmpty(silentInstallPath)) ? silentInstallPath : installPath;
                bool wantAlbum = silentMode ? silentAlbum : pendingAlbum;
                bool wantEdit = silentMode ? silentEdit : pendingEdit;
                bool wantShortcut = silentMode ? true : pendingShortcut;
                if (!wantAlbum && !wantEdit) throw new Exception("没有选择任何安装组件");

                UpdateStatus("创建目录...");
                if (!Directory.Exists(targetPath))
                    Directory.CreateDirectory(targetPath);

                UpdateStatus("关闭正在运行的程序...");
                CloseRunningInstances(targetPath);

                string myExe = Process.GetCurrentProcess().MainModule.FileName;
                long footerPos;
                long p7zExe, p7zDll, pCommon, pAlbum, pEdit;
                using (var fs = new FileStream(myExe, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    long len = fs.Length;
                    footerPos = len - (5 + 8*5);
                    if (footerPos <= 0) throw new Exception("安装包数据损坏");
                    byte[] footer = new byte[5 + 8*5];
                    fs.Seek(footerPos, SeekOrigin.Begin);
                    fs.Read(footer, 0, footer.Length);
                    string marker = Encoding.ASCII.GetString(footer, 0, 5);
                    if (marker != "IBPK2") throw new Exception("安装包数据损坏");
                    p7zExe = BitConverter.ToInt64(footer, 5);
                    p7zDll = BitConverter.ToInt64(footer, 13);
                    pCommon = BitConverter.ToInt64(footer, 21);
                    pAlbum = BitConverter.ToInt64(footer, 29);
                    pEdit = BitConverter.ToInt64(footer, 37);
                }

                string tempDir = Path.Combine(Path.GetTempPath(), "imgbatch_setup_v212");
                if (Directory.Exists(tempDir)) { try { Directory.Delete(tempDir, true); } catch {} }
                Directory.CreateDirectory(tempDir);
                string sevenZipExe = Path.Combine(tempDir, "7z.exe");
                string sevenZipDll = Path.Combine(tempDir, "7z.dll");

                UpdateStatus("释放解压工具...");
                SetProgress(5);
                CopyRange(myExe, p7zExe, p7zDll - p7zExe, sevenZipExe);
                CopyRange(myExe, p7zDll, pCommon - p7zDll, sevenZipDll);

                string commonArchive = Path.Combine(tempDir, "common.7z");
                CopyRange(myExe, pCommon, pAlbum - pCommon, commonArchive);

                UpdateStatus("解压公共文件...");
                SetProgress(10);
                string sevenZipOutput = "";
                int code = ExtractArchive(sevenZipExe, commonArchive, targetPath, tempDir, delegate {}, out sevenZipOutput);
                if (code != 0) throw new Exception("公共文件解压失败，代码 " + code + "：\n" + sevenZipOutput);

                if (wantAlbum)
                {
                    UpdateStatus("解压相册组件...");
                    SetProgress(20);
                    string albumArchive = Path.Combine(tempDir, "album.7z");
                    CopyRange(myExe, pAlbum, pEdit - pAlbum, albumArchive);
                    int pct = 20;
                    code = ExtractArchive(sevenZipExe, albumArchive, targetPath, tempDir, delegate {
                        if (pct < 55) { pct += 2; SetProgress(pct); }
                    }, out sevenZipOutput);
                    if (code != 0) throw new Exception("相册组件解压失败，代码 " + code + "：\n" + sevenZipOutput);
                    if (!File.Exists(Path.Combine(targetPath, "相册", "相册.exe")))
                        throw new Exception("相册组件解压后未找到「相册\\相册.exe」");
                }

                if (wantEdit)
                {
                    UpdateStatus("解压图片编辑组件...");
                    SetProgress(wantAlbum ? 58 : 20);
                    string editArchive = Path.Combine(tempDir, "edit.7z");
                    CopyRange(myExe, pEdit, footerPos - pEdit, editArchive);
                    int pct = wantAlbum ? 58 : 20;
                    code = ExtractArchive(sevenZipExe, editArchive, targetPath, tempDir, delegate {
                        if (pct < 88) { pct += 2; SetProgress(pct); }
                    }, out sevenZipOutput);
                    if (code != 0) throw new Exception("图片编辑组件解压失败，代码 " + code + "：\n" + sevenZipOutput);
                    if (!File.Exists(Path.Combine(targetPath, "图片编辑", "图片编辑.exe")))
                        throw new Exception("图片编辑组件解压后未找到「图片编辑\\图片编辑.exe」");
                }

                SetProgress(90);
                UpdateStatus("写入版本说明...");
                WriteRootReadme(targetPath, wantAlbum, wantEdit);

                UpdateStatus("创建快捷方式...");
                SetProgress(94);
                string desktop = GetDesktop();
                if (wantShortcut)
                {
                    if (wantAlbum)
                    {
                        string exe = Path.Combine(targetPath, "相册", "相册.exe");
                        string ico = Path.Combine(targetPath, "相册", "相册.ico");
                        if (!File.Exists(ico)) ico = exe;
                        string lnk = Path.Combine(desktop, "相册.lnk");
                        CreateShortcut(exe, lnk, "", ico, "相册");
                        if (!File.Exists(lnk))
                            File.WriteAllText(Path.Combine(desktop, "相册.bat"), "@echo off\r\nstart \"\" \"" + exe + "\"\r\n");
                    }
                    if (wantEdit)
                    {
                        string exe = Path.Combine(targetPath, "图片编辑", "图片编辑.exe");
                        string ico = Path.Combine(targetPath, "图片编辑", "相册_编辑.ico");
                        if (!File.Exists(ico)) ico = exe;
                        string lnk = Path.Combine(desktop, "图片编辑.lnk");
                        CreateShortcut(exe, lnk, "", ico, "图片编辑");
                        if (!File.Exists(lnk))
                            File.WriteAllText(Path.Combine(desktop, "图片编辑.bat"), "@echo off\r\nstart \"\" \"" + exe + "\"\r\n");
                    }
                }

                UpdateStatus("写入卸载信息...");
                WriteUninstallRegistry(targetPath, wantAlbum, wantEdit);

                UpdateStatus("完成");
                SetProgress(100);

                try { Directory.Delete(tempDir, true); } catch {}
            }
            catch (Exception ex)
            {
                installOk = false;
                if (silentMode)
                {
                    try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "imgbatch_setup_log.txt"), "Silent install error: " + ex + "\r\n"); } catch {}
                    Environment.Exit(1);
                }
                MessageBox.Show("安装失败: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        void CopyRange(string sourceFile, long start, long len, string destFile)
        {
            if (len <= 0) throw new Exception("安装包数据长度为 0");
            using (var src = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var dst = new FileStream(destFile, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                src.Seek(start, SeekOrigin.Begin);
                byte[] buffer = new byte[1024 * 1024];
                long left = len;
                while (left > 0)
                {
                    int want = (int)Math.Min(buffer.Length, left);
                    int read = src.Read(buffer, 0, want);
                    if (read <= 0) throw new Exception("安装包数据不完整");
                    dst.Write(buffer, 0, read);
                    left -= read;
                }
            }
        }

        void WriteRootReadme(string targetPath, bool album, bool edit)
        {
            var sb = new StringBuilder();
            sb.AppendLine("图片批处理工具 v2.58");
            sb.AppendLine();
            if (album) sb.AppendLine("相册：相册\\相册.exe");
            if (edit) sb.AppendLine("图片编辑：图片编辑\\图片编辑.exe");
            sb.AppendLine();
            sb.AppendLine("两个模块相互独立，可分别启动、互不依赖。");
            sb.AppendLine("同一安装目录内的已装组件会由卸载程序一并移除。");
            File.WriteAllText(Path.Combine(targetPath, "版本说明.txt"), sb.ToString(), new UTF8Encoding(true));
        }

        void WriteUninstallRegistry(string targetPath, bool album, bool edit)
        {
            try
            {
                string baseKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
                if (album)
                    WriteUninstallEntry(baseKey, "ImageBatchTool_Album", "相册",
                        Path.Combine(targetPath, "相册", "相册.ico"), targetPath);
                if (edit)
                    WriteUninstallEntry(baseKey, "ImageBatchTool_Edit", "图片编辑",
                        Path.Combine(targetPath, "图片编辑", "相册_编辑.ico"), targetPath);
            }
            catch { }
        }

        void WriteUninstallEntry(string baseKey, string keyName, string component, string iconPath, string targetPath)
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(baseKey + "\\" + keyName))
            {
                if (k == null) return;
                k.SetValue("DisplayName", "图片批处理工具 v2.58（" + component + "）");
                k.SetValue("DisplayVersion", "2.58");
                k.SetValue("Publisher", "純潔の工口君");
                k.SetValue("InstallLocation", targetPath);
                k.SetValue("DisplayIcon", iconPath);
                string un = Path.Combine(targetPath, "卸载图片批处理工具.exe");
                k.SetValue("UninstallString", "\"" + un + "\"");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
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

        void CloseRunningInstances(string targetPath)
        {
            string root = Path.GetFullPath(targetPath).TrimEnd('\\').ToLowerInvariant() + "\\";
            try
            {
                foreach (var pr in Process.GetProcesses())
                {
                    try
                    {
                        if (pr.Id == Process.GetCurrentProcess().Id) continue;
                        string fn = pr.MainModule.FileName;
                        if (fn == null) continue;
                        string full = Path.GetFullPath(fn).ToLowerInvariant();
                        if (full.StartsWith(root))
                        {
                            pr.CloseMainWindow();
                        }
                    }
                    catch { }
                }
                System.Threading.Thread.Sleep(1500);
                foreach (var pr in Process.GetProcesses())
                {
                    try
                    {
                        if (pr.Id == Process.GetCurrentProcess().Id) continue;
                        string fn = pr.MainModule.FileName;
                        if (fn == null) continue;
                        string full = Path.GetFullPath(fn).ToLowerInvariant();
                        if (full.StartsWith(root) && !pr.HasExited)
                        {
                            pr.Kill();
                        }
                    }
                    catch { }
                }
                System.Threading.Thread.Sleep(500);
            }
            catch { }
        }

        int ExtractArchive(string sevenZipExe, string archivePath, string targetPath, string tempDir, Action onTick, out string output)
        {
            var sb = new StringBuilder();
            var psi = new ProcessStartInfo();
            psi.FileName = sevenZipExe;
            psi.Arguments = "x \"" + archivePath + "\" -o\"" + targetPath + "\" -y -bso0 -bsp0";
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.WorkingDirectory = tempDir;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            var p = Process.Start(psi);
            p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) sb.AppendLine(e.Data); };
            p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) sb.AppendLine(e.Data); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            while (!p.WaitForExit(500))
            {
                if (onTick != null) onTick();
            }
            p.WaitForExit();
            output = sb.ToString().Trim();
            if (output.Length > 1600) output = output.Substring(output.Length - 1600);
            return p.ExitCode;
        }

        void CreateShortcut(string target, string lnkPath, string args, string iconPath, string desc)
        {
            try
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                object shell = Activator.CreateInstance(shellType);
                object shortcut = shellType.InvokeMember("CreateShortcut",
                    System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
                shortcut.GetType().InvokeMember("TargetPath",
                    System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { target });
                if (!string.IsNullOrEmpty(args))
                    shortcut.GetType().InvokeMember("Arguments",
                        System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { args });
                shortcut.GetType().InvokeMember("WorkingDirectory",
                    System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { Path.GetDirectoryName(target) });
                shortcut.GetType().InvokeMember("IconLocation",
                    System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { iconPath + ",0" });
                shortcut.GetType().InvokeMember("Description",
                    System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { desc });
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
                var cl = Environment.GetCommandLineArgs();
                bool silent = cl.Length > 1 && string.Equals(cl[1], "/S", StringComparison.OrdinalIgnoreCase);
                if (silent)
                {
                    var f = new InstallerForm();
                    f.silentMode = true;
                    for (int i = 2; i < cl.Length; i++)
                    {
                        if (cl[i].StartsWith("/D=", StringComparison.OrdinalIgnoreCase))
                            f.silentInstallPath = cl[i].Substring(3).Trim('"');
                        else if (string.Equals(cl[i], "/ALBUM", StringComparison.OrdinalIgnoreCase))
                            f.silentAlbum = true;
                        else if (string.Equals(cl[i], "/EDIT", StringComparison.OrdinalIgnoreCase))
                            f.silentEdit = true;
                    }
                    if (!f.silentAlbum && !f.silentEdit) { f.silentAlbum = true; f.silentEdit = true; }
                    f.DoInstall();
                    return;
                }
                Application.EnableVisualStyles();
                Application.Run(new InstallerForm());
            }
            catch (Exception ex)
            {
                MessageBox.Show("启动错误: " + ex.ToString(), "安装程序错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

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






