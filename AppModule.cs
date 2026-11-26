namespace ImageBatchTool
{
    // 编译期模块标识：相册版和图片编辑版分别编译，不靠启动参数切换。
    static class AppModule
    {
#if ALBUM_BUILD
        public const string Id = "album";
        public const bool IsAlbum = true;
        public const string DisplayName = "相册";
        public const string WindowTitle = "相册";
        public const string ExeName = "相册.exe";
        public const string HtmlFile = "album.html";
        public const string IconFile = "相册.ico";
        public const string SplashSubtitle = "正在初始化相册引擎…";
        public const string AboutText = "相册\n版本 2.38\n\n制作者：純潔の工口君";
#if QA_BUILD
        public const string ConfigFolder = "ImageBatchTool_QA";
#else
        public const string ConfigFolder = "ImageBatchTool";
#endif
#elif EDIT_BUILD
        public const string Id = "edit";
        public const bool IsAlbum = false;
        public const string DisplayName = "图片编辑";
        public const string WindowTitle = "图片编辑";
        public const string ExeName = "图片编辑.exe";
        public const string HtmlFile = "edit.html";
        public const string IconFile = "相册_编辑.ico";
        public const string SplashSubtitle = "正在初始化图像处理引擎…";
        public const string AboutText = "图片编辑\n版本 2.38\n\n制作者：純潔の工口君";
#if QA_BUILD
        public const string ConfigFolder = "ImageBatchTool_Edit_QA";
#else
        public const string ConfigFolder = "ImageBatchTool_Edit";
#endif
#else
#error 必须指定 ALBUM_BUILD 或 EDIT_BUILD 编译符号
#endif

        public static string ConfigDir()
        {
            try
            {
                return System.IO.Path.Combine(
                    System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
                    ConfigFolder);
            }
            catch { return ConfigFolder; }
        }

        // 相册与图片编辑是两个独立进程、独立安装目录；皮肤选择放在公共配置目录，
        // 两个窗口及去重子窗口都读同一份，切换后能相互跟随。
        public static string SharedSkinFile()
        {
            try
            {
                string dir = System.IO.Path.Combine(
                    System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
                    "ImageBatchTool");
                if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
                return System.IO.Path.Combine(dir, "skin_shared.txt");
            }
            catch
            {
                return System.IO.Path.Combine(System.Windows.Forms.Application.StartupPath, "skin_shared.txt");
            }
        }
    }
}




