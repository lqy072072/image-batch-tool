# 图片批处理工具

Windows 图片批处理与相册管理工具：LUT 滤镜、AI 调色、人像增强、美颜液化、景深虚化、超分、证件照、本地相册与去重。相册和图片编辑是两个可独立安装、独立运行的程序。

当前版本：**v2.49**

## 下载安装

安装包通过 GitHub Releases 发布：

**https://github.com/lqy072072/image-batch-tool/releases**

- Release 资产名：`image-batch-tool_v2.49_setup.exe`
- 本地构建名：`图片批处理工具_v2.49_安装包.exe`
- 大小：1,297,545,943 字节（约 1.21 GB）
- SHA256：`CA7565F7901165C9BDF80BCD345ADE7473DE1813ADEF761342BD32E90966840D`

安装包已内置两个模块的 Python 运行时、AI/OCR 模型和 LUT；生成式修图的大模型不预装，首次使用时在程序内下载。

运行安装包后按向导选择「相册」和/或「图片编辑」，默认路径为 `D:\Program Files\图片批量处理工具`，只勾选一个也能正常安装运行。

## 仓库内容

源码仓库只保留维护和构建所需的代码与界面资源：

```text
WebViewApp.cs           C# 主程序（相册 / 图片编辑共用）
AppModule.cs            模块标识、版本、图标与配置目录
AlbumMeta.cs            相册元数据
Extras.cs               专业调整算法
Uninstaller.cs          卸载程序
album.html              相册前端
edit.html               图片编辑前端
dedup.html              去重窗口前端
ui.html                 旧版单程序前端（兼容保留）
skins/                  九套皮肤 CSS / 背景图
assets/                 界面预览图和地图依赖
ai/                     Python 图像处理与 AI 脚本
native/                 原生图像处理 C++ 源码
installer/Installer.cs  安装向导源码
```

以下大体积或非源码文件不再进入仓库，统一放在 Release 安装包或本机构建目录：

- `luts/`：内置 LUT 文件
- `ai/models/`：ONNX 模型
- `webview2/`：WebView2 SDK
- `docs/`、`promo_*`：截图、宣传素材
- 历史测试报告、Release 文案、旧安装包哈希与快捷方式

## 编译

需要 .NET Framework 4.x 的 `csc.exe`，以及 WebView2 SDK 的 `Microsoft.Web.WebView2.Core.dll` / `Microsoft.Web.WebView2.WinForms.dll`。

```bat
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /target:winexe /platform:x64 /optimize+ /unsafe /nologo /d:ALBUM_BUILD /win32icon:相册.ico /out:"相册.exe" /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll /r:Microsoft.Web.WebView2.Core.dll /r:Microsoft.Web.WebView2.WinForms.dll WebViewApp.cs AppModule.cs AlbumMeta.cs Extras.cs GlassMouseBridge.cs

C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /target:winexe /platform:x64 /optimize+ /unsafe /nologo /d:EDIT_BUILD /win32icon:相册_编辑.ico /out:"图片编辑.exe" /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll /r:Microsoft.Web.WebView2.Core.dll /r:Microsoft.Web.WebView2.WinForms.dll WebViewApp.cs AppModule.cs AlbumMeta.cs Extras.cs GlassMouseBridge.cs
```

安装程序与卸载程序：

```bat
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /target:winexe /platform:x64 /optimize+ /nologo /win32icon:app.ico /out:"安装程序.exe" /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll installer\Installer.cs

C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /target:winexe /platform:x64 /optimize+ /nologo /win32icon:卸载.ico /out:"卸载图片批处理工具.exe" /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll Uninstaller.cs
```

> 源码仓库不包含 `luts/`、`ai/models/` 和 `webview2/`。需要从源码完整运行或重新打包时，请从 Release 安装包或本机构建目录补齐这些运行资源；仓库中的 `.gitignore` 已阻止它们再次被提交。

## 系统要求

- Windows 10 64 位或更高
- WebView2 Runtime
- 不需要单独安装 Python（安装包已嵌入运行时）

## License

MIT
