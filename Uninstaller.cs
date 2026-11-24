using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

static class Uninstaller
{
    const string AppExeName = "图片批处理工具.exe";
    const string AppTitle = "图片批处理工具";
    const string SelfName = "卸载图片批处理工具.exe";

    [STAThread]
    static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        string cleanupRoot = null;
        bool quiet = false;
        bool testMode = false;
        int waitPid = 0;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--cleanup" && i + 1 < args.Length) cleanupRoot = args[++i];
            else if (args[i] == "--quiet" || string.Equals(args[i], "/S", StringComparison.OrdinalIgnoreCase)) quiet = true;
            else if (args[i] == "--test") testMode = true;
            else if (args[i] == "--waitpid" && i + 1 < args.Length) int.TryParse(args[++i], out waitPid);
        }

        if (!string.IsNullOrEmpty(cleanupRoot))
        {
            RunCleanup(cleanupRoot, waitPid, quiet, testMode);
            return;
        }

        string installRoot = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
        if (!LooksLikeInstallDir(installRoot))
        {
            MessageBox.Show(
                "没有在当前目录找到「相册\\相册.exe」或「图片编辑\\图片编辑.exe」。\n\n请把本卸载程序放在软件的安装目录中运行。",
                "无法卸载", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        DialogResult answer = DialogResult.Yes;
        if (!quiet)
        {
            answer = MessageBox.Show(
                "确定卸载「" + AppTitle + "」？\n\n" +
                "将删除当前安装目录中的程序、缓存和缩略图，以及已安装组件的配置与快捷方式。\n\n" +
                "如果相册和图片编辑安装在同一目录，两者会一起卸载；\n" +
                "如果只安装了其中一个，不会碰另一个独立安装目录。\n\n" +
                "你的照片文件、视频文件以及相册回收站中尚未彻底删除的文件不会被删除。\n\n" +
                "卸载前请先保存正在进行的编辑。",
                "卸载 " + AppTitle,
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        }
        if (answer != DialogResult.Yes) return;

        try
        {
            // 卸载程序不能删除正在运行的自己：复制到临时目录后再执行清理。
            string tempDir = Path.Combine(Path.GetTempPath(), "ibt_uninstall_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string tempExe = Path.Combine(tempDir, SelfName);
            File.Copy(Application.ExecutablePath, tempExe, true);

            int pid = Process.GetCurrentProcess().Id;
            Process.Start(new ProcessStartInfo
            {
                FileName = tempExe,
                Arguments = "--cleanup \"" + installRoot + "\" --waitpid " + pid + (quiet ? " --quiet" : ""),
                UseShellExecute = true
            });
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            MessageBox.Show("无法启动卸载程序：\n" + ex.Message, "卸载失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    static bool LooksLikeInstallDir(string dir)
    {
        if (string.IsNullOrEmpty(dir)) return false;
        string full = Path.GetFullPath(dir).TrimEnd('\\');
        if (full.Length <= 3) return false; // 防止误删磁盘根目录
        string root = Path.GetPathRoot(full);
        if (root != null && string.Equals(root.TrimEnd('\\'), full, StringComparison.OrdinalIgnoreCase)) return false;
        bool album = File.Exists(Path.Combine(full, "相册", "相册.exe"));
        bool edit = File.Exists(Path.Combine(full, "图片编辑", "图片编辑.exe"));
        bool legacy = File.Exists(Path.Combine(full, AppExeName)) && File.Exists(Path.Combine(full, "ui.html"));
        return album || edit || legacy;
    }

    static bool HasAlbum(string dir)
    {
        return File.Exists(Path.Combine(dir, "相册", "相册.exe")) ||
               (File.Exists(Path.Combine(dir, AppExeName)) && File.Exists(Path.Combine(dir, "ui.html")));
    }

    static bool HasEdit(string dir)
    {
        return File.Exists(Path.Combine(dir, "图片编辑", "图片编辑.exe")) ||
               (File.Exists(Path.Combine(dir, AppExeName)) && File.Exists(Path.Combine(dir, "ui.html")));
    }

    static void RunCleanup(string installRoot, int waitPid, bool quiet, bool testMode)
    {
        installRoot = Path.GetFullPath(installRoot).TrimEnd('\\');
        if (!LooksLikeInstallDir(installRoot))
        {
            if (!quiet) MessageBox.Show("安装目录校验失败，为避免误删已停止。", "卸载失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        if (waitPid > 0)
        {
            try
            {
                Process parent = Process.GetProcessById(waitPid);
                parent.WaitForExit(5000);
            }
            catch { }
        }

        // 先结束后台主程序；卸载器自身是另一个进程名，不会被误杀。
        bool hadAlbum = HasAlbum(installRoot);
        bool hadEdit = HasEdit(installRoot);
        KillAppProcesses(installRoot, waitPid);
        Thread.Sleep(1200);

        List<string> failed = new List<string>();

        DeleteTree(installRoot, failed);
        if (Directory.Exists(installRoot))
        {
            // 如果目录仍被占用，再结束一次主程序并重试。
            KillAppProcesses(installRoot, waitPid);
            for (int i = 0; i < 18 && Directory.Exists(installRoot); i++)
            {
                Thread.Sleep(800);
                DeleteTree(installRoot, failed);
            }
        }

        if (!testMode)
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string startMenu = Path.Combine(appData, @"Microsoft\Windows\Start Menu\Programs");

            // 按已安装组件清理各自缓存，避免误删另一套独立安装的配置。
            if (hadAlbum)
            {
                string customCache = null;
                try
                {
                    string cfg = Path.Combine(appData, "ImageBatchTool", "cache_root.txt");
                    if (File.Exists(cfg)) customCache = File.ReadAllText(cfg).Trim();
                }
                catch { }
                DeleteCustomCache(customCache, installRoot, failed);
                DeleteTree(Path.Combine(appData, "ImageBatchTool"), failed);
                DeleteTree(Path.Combine(localAppData, "ImageBatchTool"), failed);
                DeleteTree(Path.Combine(localAppData, "ImageBatchTool_cache"), failed);
            }
            if (hadEdit)
            {
                string customCache = null;
                try
                {
                    string cfg = Path.Combine(appData, "ImageBatchTool_Edit", "cache_root.txt");
                    if (File.Exists(cfg)) customCache = File.ReadAllText(cfg).Trim();
                }
                catch { }
                DeleteCustomCache(customCache, installRoot, failed);
                DeleteTree(Path.Combine(appData, "ImageBatchTool_Edit"), failed);
            }
            DeleteTree(Path.Combine(appData, "图片批处理工具"), failed);

            string[] commonDesktopNames = { "图片批处理工具.lnk", "卸载图片批处理工具.lnk" };
            foreach (string name in commonDesktopNames) DeleteFile(Path.Combine(desktop, name), failed);
            if (hadAlbum)
            {
                DeleteFile(Path.Combine(desktop, "相册.lnk"), failed);
                DeleteFile(Path.Combine(desktop, "相册.bat"), failed);
            }
            if (hadEdit)
            {
                DeleteFile(Path.Combine(desktop, "图片编辑.lnk"), failed);
                DeleteFile(Path.Combine(desktop, "图片编辑.bat"), failed);
            }

            string[] commonStartNames = { "图片批处理工具.lnk", "卸载图片批处理工具.lnk" };
            foreach (string name in commonStartNames) DeleteFile(Path.Combine(startMenu, name), failed);
            if (hadAlbum) DeleteFile(Path.Combine(startMenu, "相册.lnk"), failed);
            if (hadEdit) DeleteFile(Path.Combine(startMenu, "图片编辑.lnk"), failed);
            DeleteTree(Path.Combine(startMenu, "图片批处理工具"), failed);

            string temp = Path.GetTempPath();
            DeleteTree(Path.Combine(temp, "ibt_thumbs"), failed);
            DeleteTree(Path.Combine(temp, "ibt_vthumbs"), failed);
            DeleteTree(Path.Combine(temp, "ImageBatchTool_cache"), failed);
            DeleteTree(Path.Combine(temp, "imgbatch_setup"), failed);
            DeleteFile(Path.Combine(temp, "imgbatch_setup_log.txt"), failed);
            DeleteFile(Path.Combine(temp, "crop_debug.log"), failed);
            DeleteFile(Path.Combine(temp, "ibt_vthumb_jobs.json"), failed);
            try
            {
                foreach (string f in Directory.GetFiles(temp, "ibt_faces_*.txt")) DeleteFile(f, failed);
            }
            catch { }

            DeleteRegistryEntriesForInstall(installRoot, failed);
        }

        if (!quiet)
        {
            string message = failed.Count == 0
                ? "「" + AppTitle + "」已卸载完成。\n\n照片文件没有被删除。"
                : "卸载已完成，但有部分文件未能删除：\n\n" + string.Join("\n", failed.ToArray()) +
                  "\n\n请关闭正在使用这些文件的程序后手动删除。";
            MessageBox.Show(message, testMode ? "卸载测试完成" : "卸载完成", MessageBoxButtons.OK,
                failed.Count == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }

        ScheduleSelfDelete();
    }

    static void KillAppProcesses(string installRoot, int waitPid)
    {
        string root = Path.GetFullPath(installRoot).TrimEnd('\\').ToLowerInvariant() + "\\";
        string[] names = { "相册", "图片编辑", "图片批处理工具" };
        foreach (string name in names)
        {
            try
            {
                foreach (Process p in Process.GetProcessesByName(name))
                {
                    try
                    {
                        if (p.Id == waitPid) continue;
                        string path = null;
                        try { path = p.MainModule.FileName; } catch { }
                        if (string.IsNullOrEmpty(path)) continue;
                        if (!Path.GetFullPath(path).ToLowerInvariant().StartsWith(root)) continue;
                        p.Kill();
                        p.WaitForExit(3000);
                    }
                    catch { }
                }
            }
            catch { }
        }
    }

    static void DeleteCustomCache(string root, string installRoot, List<string> failed)
    {
        if (string.IsNullOrEmpty(root)) return;
        try
        {
            string r = Path.GetFullPath(root).TrimEnd('\\');
            string ir = Path.GetFullPath(installRoot).TrimEnd('\\');
            if (r.Length <= 3) return; // 不直接清理磁盘根目录
            if (string.Equals(r, ir, StringComparison.OrdinalIgnoreCase)) return;
            if (r.StartsWith(ir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return; // 安装目录清理会处理
            if (!Directory.Exists(r)) return;
            // 只清理带本程序缓存根标识的目录，避免误删用户其他文件夹中的同名子目录。
            if (!File.Exists(Path.Combine(r, ".ibt_cache_root"))) return;

            // 只处理程序生成的缓存子目录；用户在缓存目录里放的其他文件保留。
            string[] known = { "index", "thumbs", "vthumbs", "media", "temp", "webview2", "logs", "config" };
            bool cacheLike = false;
            foreach (string n in known)
            {
                if (Directory.Exists(Path.Combine(r, n))) { cacheLike = true; break; }
            }
            if (!cacheLike)
            {
                // 只有标识、没有缓存子目录的目录也可以清掉。
                DeleteFile(Path.Combine(r, ".ibt_cache_root"), failed);
                try { if (Directory.Exists(r) && Directory.GetFileSystemEntries(r).Length == 0) Directory.Delete(r); }
                catch (Exception ex) { failed.Add(r + "（" + ex.Message + "）"); }
                return;
            }
            foreach (string n in known) DeleteTree(Path.Combine(r, n), failed);
            DeleteFile(Path.Combine(r, ".ibt_cache_root"), failed);

            // 缓存目录已经空了才删除目录本身，避免误删同目录中的用户文件。
            try
            {
                if (Directory.Exists(r) && Directory.GetFileSystemEntries(r).Length == 0)
                    Directory.Delete(r);
            }
            catch (Exception ex) { failed.Add(r + "（" + ex.Message + "）"); }
        }
        catch (Exception ex) { failed.Add(root + "（" + ex.Message + "）"); }
    }

    static void DeleteTree(string path, List<string> failed)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
        catch (Exception ex)
        {
            failed.Add(path + "（" + ex.Message + "）");
        }
    }

    static void DeleteFile(string path, List<string> failed)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            failed.Add(path + "（" + ex.Message + "）");
        }
    }

    static void DeleteRegistryEntriesForInstall(string installRoot, List<string> failed)
    {
        string root = Path.GetFullPath(installRoot).TrimEnd('\\');
        try
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall", true))
            {
                if (key == null) return;
                foreach (string name in key.GetSubKeyNames())
                {
                    // 只处理本产品写入的卸载项，避免碰其他软件。
                    if (!name.StartsWith("ImageBatchTool", StringComparison.OrdinalIgnoreCase) &&
                        !name.Equals("图片批处理工具", StringComparison.OrdinalIgnoreCase)) continue;
                    string loc = null;
                    try
                    {
                        using (RegistryKey sub = key.OpenSubKey(name))
                        {
                            if (sub != null) loc = Convert.ToString(sub.GetValue("InstallLocation"));
                        }
                    }
                    catch { }
                    bool same = string.IsNullOrEmpty(loc) ||
                        string.Equals(Path.GetFullPath(loc).TrimEnd('\\'), root, StringComparison.OrdinalIgnoreCase);
                    if (!same) continue;
                    try { key.DeleteSubKeyTree(name, false); }
                    catch (Exception ex) { failed.Add("注册表 " + name + "（" + ex.Message + "）"); }
                }
            }
        }
        catch (Exception ex) { failed.Add("注册表卸载项（" + ex.Message + "）"); }
    }

    static void DeleteRegistryKey(string subKey, List<string> failed)
    {
        try
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall", true))
            {
                if (key != null) key.DeleteSubKeyTree(subKey, false);
            }
        }
        catch (Exception ex)
        {
            failed.Add("注册表 " + subKey + "（" + ex.Message + "）");
        }
    }

    static void ScheduleSelfDelete()
    {
        try
        {
            string self = Application.ExecutablePath;
            string selfDir = Path.GetDirectoryName(self);
            string cmd = "/c ping 127.0.0.1 -n 3 >nul & del /f /q \"" + self + "\" & rmdir /s /q \"" + selfDir + "\"";
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = cmd,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }
        catch { }
    }
}
