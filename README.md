# 星露谷纯壁纸版

在 Windows 桌面图标后方运行《星露谷物语》网页移植版。程序直接嵌入桌面，桌面和游戏共用 Windows 鼠标指针。

桌面版制作 **zhouyang**。

## 下载和使用

从 Releases 下载 Windows 宿主 ZIP，解压后把现有的 `game` 文件夹放在「启动星露谷纯壁纸版.exe」旁边。没有游戏资源时，可解压固定版本的上游 ZIP，将包含 `index.html` 和 `_framework` 的目录改名为 `game`。需要 64 位 Windows 和 Microsoft Edge WebView2 Runtime。[GitHub Releases](https://github.com/zhage5566/stardew-wasm-wallpaper/releases) · [对应游戏资源 ZIP](https://codeload.github.com/degloved-net/stardew-wasm/zip/a664842611f09771f9155b9718046649bcaded33)

- 桌面在前台时，移动鼠标到游戏空白区即可使用键盘和鼠标。移到图标或任务栏上，输入交回 Windows。
- 按 `Ctrl+Alt+F10` 暂停或恢复游戏控制。也可在系统托盘选择显示器或退出。
- 托盘的「设为常用壁纸」会记住所选显示器。「开机自启动」默认关闭，开启后随当前 Windows 用户登录启动。
- 画面跟随所选显示器的分辨率和缩放变化。显示器断开时回到主屏，重新连接后恢复。
- 存档位于 `%LOCALAPPDATA%\StardewWasmWallpaper\WebView2`。使用旧桌面版的玩家请先在游戏内保存，再退出旧版。

### Wallpaper Engine 本地项目

完整解压到短路径，退出独立版。在 Wallpaper Engine 的壁纸编辑器中导入同目录的 `StardewWallpaperEngine.exe`，或直接用本地项目配置 `project.json`。在 Wallpaper Engine 中选择显示器。应用类壁纸可以在本机使用，Steam 创意工坊已停止分发此类项目。[Wallpaper Engine 官方公告](https://store.steampowered.com/oldnews/?appgroupname=Wallpaper+Engine&appids=431960&feed=steam_community_announcements)

### macOS 预览版

从 Releases 下载 macOS 安装器，完整解压后双击 `install.command`。脚本会识别 Apple 芯片或 Intel 处理器，校验官方 Electron ZIP，并生成 `Stardew Wallpaper.app`。把 App 放在上面的 `game` 文件夹旁边，或首次运行时选择该文件夹。需要 macOS 13 或更新版本。按 `Command+Option+F10` 进入全屏试玩或返回壁纸。菜单栏可保存常用显示器和设置登录自动启动。

macOS 包装器未通过 Apple 公证。真实 Mac 上的桌面层、登录启动和键盘仍待实机验证。

## 构建桌面宿主

在 64 位 Windows 上，从 WebView2 SDK `1.0.3485.44` 或已解压的便携发行包取得 `Microsoft.Web.WebView2.Core.dll`、`Microsoft.Web.WebView2.WinForms.dll` 和 x64 `WebView2Loader.dll`，把它们放在同一目录。使用系统自带的 .NET Framework C# 编译器构建。

```powershell
.\build-direct.ps1 -SdkDirectory 'D:\path\to\webview2-dlls'
```

把与发行版配套的 `game` 文件夹放到生成的 `bin-direct` 目录内。宿主在加载时校验指定 WASM 组件的 SHA-256，然后应用单指针补丁。该补丁对应上游提交 `a664842611f09771f9155b9718046649bcaded33`。[网页移植项目](https://github.com/degloved-net/stardew-wasm)

macOS 构建脚本位于 `mac/build.py`。需要 Python 3.11 或更新版本和官方 Electron v45.5.0 的 macOS ZIP。[Electron v45.5.0](https://github.com/electron/electron/releases/tag/v45.5.0)

## 许可证

本仓库的 Windows 和 macOS 壁纸宿主源码、构建和安装脚本采用 MIT 许可证。[查看许可证](LICENSE)

《星露谷物语》原作及游戏素材归 ConcernedApe。网页移植资源和第三方运行时沿用各自的授权文件。网页移植的授权文件位于 `game/LICENSE`。
